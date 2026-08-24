// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using Microsoft.Extensions.Logging;
using Zeus.Plugin.Wavelog.Adif;
using Zeus.Plugin.Wavelog.Storage;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Wavelog.Sync;

/// <summary>
/// Everything that moves QSOs between the local store and Wavelog: the push
/// side's enqueue, the incremental pull, the confirmation sweep and the full
/// resync.
///
/// <para>The rule that runs through all of it: <b>a QSO that arrived from
/// Wavelog is never enqueued for pushing back.</b> Import is a different write
/// path from Create for exactly that reason — without it a full resync would
/// enqueue the entire log, and because Wavelog deduplicates, thousands of
/// no-op inserts would achieve nothing while the operator watched a backlog
/// that never meant anything.</para>
/// </summary>
public sealed class WavelogSyncService(
    LiteDbLogStore store,
    IOutbox outbox,
    IWavelogTransport transport,
    Func<WavelogConfig> config,
    ICursorStore cursors,
    ILogger? log = null)
{
    public const int PullBatch = 200;

    /// <summary>Queue a locally-created QSO. Called after the store has it.</summary>
    public void EnqueueForPush(LogbookEntrySnapshot entry)
    {
        var settings = config();
        if (!settings.IsUsable || !settings.PushEnabled) return;
        outbox.Enqueue(entry.Id, AdifMapper.ToRecord(entry));
    }

    // ---- loop 1: new QSOs, by cursor ---------------------------------------

    public async Task<PullReport> PullNewAsync(CancellationToken ct)
    {
        var settings = config();
        if (!settings.IsUsable || !settings.PullEnabled) return PullReport.Skipped;

        var from = cursors.GetFetchFromId();
        var (outcome, result) = await transport
            .GetContactsAsync(settings, from, PullBatch, null, ct).ConfigureAwait(false);

        if (!outcome.IsSuccess) return PullReport.Failed(outcome);
        if (result is null || result.Count == 0 || string.IsNullOrWhiteSpace(result.Adif))
            return PullReport.Nothing;

        // Marked as inbound, so these rows never enter the outbox.
        var imported = await store.ImportFromWavelogAsync(result.Adif!, ct).ConfigureAwait(false);
        cursors.SetFetchFromId(result.LastFetchedId);

        log?.LogInformation("wavelog: pulled {Count}, imported {Imported}, cursor now {Cursor}",
            result.Count, imported.ImportedCount, result.LastFetchedId);

        return new PullReport(true, result.Count, imported.ImportedCount,
            imported.DuplicateCount, result.LastFetchedId, null);
    }

    // ---- loop 2: confirmations, which the cursor cannot see -----------------

    /// <summary>
    /// LoTW and eQSL confirmations arrive as <em>updates</em>, and an update
    /// does not change the primary key — so the incremental cursor is
    /// permanently blind to them. This sweep asks for confirmed QSOs of any age
    /// instead, and reconciles them against what is already held.
    /// </summary>
    public async Task<PullReport> SweepConfirmationsAsync(CancellationToken ct)
    {
        var settings = config();
        if (!settings.IsUsable || !settings.PullEnabled) return PullReport.Skipped;

        var (outcome, result) = await transport
            .GetContactsAsync(settings, 0, PullBatch, ["lotw", "qsl", "eqsl"], ct).ConfigureAwait(false);

        if (!outcome.IsSuccess) return PullReport.Failed(outcome);
        if (result is null || result.Count == 0 || string.IsNullOrWhiteSpace(result.Adif))
            return PullReport.Nothing;

        var updated = store.ApplyConfirmations(result.Adif!);
        log?.LogInformation("wavelog: confirmation sweep saw {Count}, updated {Updated}",
            result.Count, updated);

        return new PullReport(true, result.Count, 0, 0, cursors.GetFetchFromId(), updated);
    }

    // ---- the repair button --------------------------------------------------

    /// <summary>
    /// Reconcile in both directions. A gap can be on either side and the
    /// operator cannot know which, so one action covers both — and it only ever
    /// inserts. A QSO deleted in Wavelog but present locally stays: "full sync"
    /// must not be read as "make identical".
    /// </summary>
    public async Task<ResyncReport> ResyncAsync(bool dryRun, CancellationToken ct)
    {
        var settings = config();
        if (!settings.IsUsable) return ResyncReport.NotConfigured;

        // ---- what Wavelog has that we do not
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var missingHere = 0;
        var cursor = 0;
        while (!ct.IsCancellationRequested)
        {
            var (outcome, page) = await transport
                .GetContactsAsync(settings, cursor, PullBatch, null, ct).ConfigureAwait(false);
            if (!outcome.IsSuccess) return ResyncReport.Failed(outcome);
            if (page is null || page.Count == 0 || string.IsNullOrWhiteSpace(page.Adif)) break;

            foreach (var record in AdifParser.Parse(page.Adif!))
            {
                var key = store.DedupKeyOf(record);
                if (key is null) continue;
                seen.Add(key);
                if (!store.HasDedupKey(key)) missingHere++;
            }

            if (!dryRun)
                await store.ImportFromWavelogAsync(page.Adif!, ct).ConfigureAwait(false);

            cursor = page.LastFetchedId;
        }

        // ---- what we have that Wavelog does not
        var missingThere = store.LocalOnly(seen);

        if (!dryRun)
        {
            if (cursor > 0) cursors.SetFetchFromId(cursor);
            foreach (var entry in missingThere)
                outbox.Enqueue(entry.Id, AdifMapper.ToRecord(entry));
        }

        return new ResyncReport(true, dryRun, missingHere, missingThere.Count, null);
    }
}

public sealed record PullReport(
    bool Ran, int Fetched, int Imported, int Duplicates, int Cursor, int? Updated)
{
    public static PullReport Skipped => new(false, 0, 0, 0, 0, null);
    public static PullReport Nothing => new(true, 0, 0, 0, 0, null);
    public string? Error { get; init; }
    public static PullReport Failed(WavelogOutcome o) =>
        new(false, 0, 0, 0, 0, null) { Error = o.Detail ?? o.Kind.ToString() };
}

public sealed record ResyncReport(
    bool Ran, bool DryRun, int MissingHere, int MissingThere, string? Error)
{
    public static ResyncReport NotConfigured => new(false, true, 0, 0, "not configured");
    public static ResyncReport Failed(WavelogOutcome o) =>
        new(false, true, 0, 0, o.Detail ?? o.Kind.ToString());
}

/// <summary>Where the pull cursor is kept. Trivial, but it must be durable.</summary>
public interface ICursorStore
{
    int GetFetchFromId();
    void SetFetchFromId(int value);
}
