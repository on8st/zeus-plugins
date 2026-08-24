// SPDX-License-Identifier: GPL-2.0-or-later
using FakeWavelog;
using Zeus.Plugin.Wavelog.Storage;
using Zeus.Plugin.Wavelog.Sync;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>
/// The pull half, and the trap. The first test here is the one the design calls
/// the most important in the phase: an imported QSO must not enter the outbox,
/// or a resync enqueues the whole log to be pushed back at a Wavelog that will
/// deduplicate every one of them.
/// </summary>
public sealed class SyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wavelog-sync-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new(new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc));
    private readonly FakeWavelogServer _wavelog = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly LiteDbLogStore _store;
    private readonly LiteDbOutbox _outbox;
    private readonly MemoryCursorStore _cursor = new();

    public SyncTests()
    {
        Directory.CreateDirectory(_dir);
        _wavelog.Start();
        _store = new LiteDbLogStore(Path.Combine(_dir, "log.db"));
        _outbox = new LiteDbOutbox(Path.Combine(_dir, "outbox.db"), _clock);
    }

    public void Dispose()
    {
        _store.Dispose(); _outbox.Dispose(); _wavelog.Dispose(); _http.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private WavelogConfig Config => new()
    {
        BaseUrl = _wavelog.BaseUrl, ApiKey = _wavelog.ApiKey,
        StationProfileId = 1, PullStationIds = [1],
    };

    private WavelogSyncService NewSync() => new(
        _store, _outbox, new HttpWavelogTransport(_http), () => Config, _cursor);

    private static LogbookNewEntry Qso(string call, DateTime? when = null, string band = "20m") =>
        new(call, null, 14.074, band, "USB", "59", "59",
            QsoDateTimeUtc: when ?? new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc));

    // ---- THE trap -----------------------------------------------------------

    [Fact]
    public async Task An_imported_qso_does_not_enter_the_outbox()
    {
        _wavelog.AddQsoFromAnotherApp("G4XYZ", new DateTime(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc), "40m", "CW");

        var report = await NewSync().PullNewAsync(default);

        Assert.Equal(1, report.Imported);
        Assert.Equal(0, _outbox.PendingCount);      // <- the whole point
    }

    [Fact]
    public async Task A_full_resync_does_not_enqueue_what_it_just_imported()
    {
        for (var i = 0; i < 5; i++)
            _wavelog.AddQsoFromAnotherApp($"CALL{i}",
                new DateTime(2026, 8, 24, 10, i, 0, DateTimeKind.Utc), "20m", "SSB");

        await NewSync().ResyncAsync(dryRun: false, default);

        Assert.Equal(5, (await _store.GetEntriesAsync(0, 50)).TotalCount);
        Assert.Equal(0, _outbox.PendingCount);
    }

    // ---- the cursor ---------------------------------------------------------

    [Fact]
    public async Task The_cursor_advances_and_the_next_pull_sees_nothing()
    {
        _wavelog.AddQsoFromAnotherApp("A", DateTime.UtcNow, "20m", "SSB");
        var first = await NewSync().PullNewAsync(default);
        Assert.Equal(1, first.Imported);

        var second = await NewSync().PullNewAsync(default);
        Assert.Equal(0, second.Fetched);
    }

    [Fact]
    public void The_cursor_never_moves_backwards()
    {
        // A stale or reordered reply must not cause the whole log to be re-read.
        _cursor.SetFetchFromId(500);
        _cursor.SetFetchFromId(100);
        Assert.Equal(500, _cursor.GetFetchFromId());
    }

    [Fact]
    public async Task A_bulk_import_of_old_qsos_is_picked_up_because_the_cursor_is_the_primary_key()
    {
        // The reason to prefer the primary key over a timestamp: these contacts
        // are from 2015, and a date-based watermark would never see them.
        _wavelog.AddQsoFromAnotherApp("OLD1", new DateTime(2015, 3, 1, 12, 0, 0, DateTimeKind.Utc), "20m", "SSB");
        _wavelog.AddQsoFromAnotherApp("OLD2", new DateTime(2015, 3, 2, 12, 0, 0, DateTimeKind.Utc), "20m", "SSB");

        var report = await NewSync().PullNewAsync(default);

        Assert.Equal(2, report.Imported);
    }

    // ---- confirmations ------------------------------------------------------

    [Fact]
    public async Task A_confirmation_reaches_the_local_qso_through_the_sweep()
    {
        var saved = await _store.CreateAsync(Qso("DL1ABC"));
        _wavelog.AddQsoFromAnotherApp("DL1ABC", saved.QsoDateTimeUtc, "20m", "SSB");
        await NewSync().PullNewAsync(default);          // cursor moves past it

        _wavelog.ConfirmOnLotw("DL1ABC");

        var incremental = await NewSync().PullNewAsync(default);
        Assert.Equal(0, incremental.Fetched);           // invisible to the cursor

        var sweep = await NewSync().SweepConfirmationsAsync(default);
        Assert.Equal(1, sweep.Fetched);
    }

    [Fact]
    public async Task The_sweep_does_not_duplicate_the_qso_it_confirms()
    {
        var saved = await _store.CreateAsync(Qso("DL1ABC"));
        _wavelog.AddQsoFromAnotherApp("DL1ABC", saved.QsoDateTimeUtc, "20m", "SSB");
        _wavelog.ConfirmOnLotw("DL1ABC");

        await NewSync().SweepConfirmationsAsync(default);

        Assert.Equal(1, (await _store.GetEntriesAsync(0, 50)).TotalCount);
    }

    // ---- profiles -----------------------------------------------------------

    [Fact]
    public async Task A_qso_under_an_unselected_profile_is_not_imported()
    {
        _wavelog.AddQsoFromAnotherApp("PORTABLE", DateTime.UtcNow, "20m", "SSB", stationId: 2);
        Assert.Equal(0, (await NewSync().PullNewAsync(default)).Imported);
    }

    // ---- resync -------------------------------------------------------------

    [Fact]
    public async Task A_dry_run_reports_both_directions_and_writes_nothing()
    {
        await _store.CreateAsync(Qso("LOCALONLY"));
        _wavelog.AddQsoFromAnotherApp("THEIRS", DateTime.UtcNow, "20m", "SSB");

        var report = await NewSync().ResyncAsync(dryRun: true, default);

        Assert.True(report.DryRun);
        Assert.Equal(1, report.MissingHere);
        Assert.Equal(1, report.MissingThere);
        Assert.Equal(1, (await _store.GetEntriesAsync(0, 50)).TotalCount);   // nothing imported
        Assert.Equal(0, _outbox.PendingCount);                                // nothing queued
    }

    [Fact]
    public async Task Applying_a_resync_fills_both_gaps()
    {
        await _store.CreateAsync(Qso("LOCALONLY"));
        _wavelog.AddQsoFromAnotherApp("THEIRS", DateTime.UtcNow, "20m", "SSB");

        await NewSync().ResyncAsync(dryRun: false, default);

        Assert.Equal(2, (await _store.GetEntriesAsync(0, 50)).TotalCount);
        Assert.Equal(1, _outbox.PendingCount);          // only the local-only one
    }

    [Fact]
    public async Task Running_a_resync_twice_changes_nothing_the_second_time()
    {
        await _store.CreateAsync(Qso("LOCALONLY"));
        _wavelog.AddQsoFromAnotherApp("THEIRS", DateTime.UtcNow, "20m", "SSB");

        await NewSync().ResyncAsync(dryRun: false, default);
        var second = await NewSync().ResyncAsync(dryRun: false, default);

        Assert.Equal(0, second.MissingHere);
        Assert.Equal(2, (await _store.GetEntriesAsync(0, 50)).TotalCount);
    }

    [Fact]
    public async Task A_resync_never_deletes_a_qso_wavelog_has_forgotten()
    {
        // "Full sync" must not be read as "make identical".
        await _store.CreateAsync(Qso("ONLY_HERE"));
        await NewSync().ResyncAsync(dryRun: false, default);
        Assert.Equal(1, (await _store.GetEntriesAsync(0, 50)).TotalCount);
    }

    // ---- switched off -------------------------------------------------------

    [Fact]
    public async Task Nothing_is_pulled_before_the_plugin_is_configured()
    {
        var sync = new WavelogSyncService(
            _store, _outbox, new HttpWavelogTransport(_http), () => new WavelogConfig(), _cursor);
        Assert.False((await sync.PullNewAsync(default)).Ran);
    }
}
