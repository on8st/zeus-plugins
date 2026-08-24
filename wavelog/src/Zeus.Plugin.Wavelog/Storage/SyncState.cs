// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st

namespace Zeus.Plugin.Wavelog.Storage;

/// <summary>
/// This plugin's own bookkeeping about one QSO, kept in a <em>separate
/// collection</em> in the same database file.
///
/// <para>It is deliberately not part of the stored QSO. The document in
/// <c>entries</c> is <c>LogbookEntrySnapshot</c> — the published contract
/// record, exactly as the reference logbook plugin stores it — so that
/// uninstalling this plugin and installing that one leaves the operator's log
/// working untouched. Adding fields to that document would break the promise:
/// they would leak into ADIF exports through <c>AdifFields</c>, and a
/// round-trip through the reference's own code could silently drop them.</para>
///
/// <para>So the QSO stays theirs, and the sync state stays ours.</para>
/// </summary>
public sealed class SyncState
{
    /// <summary>The QSO's id in the <c>entries</c> collection.</summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// Where the QSO entered the log. <c>wavelog</c> means it arrived on the
    /// pull and must never be pushed back — the loop-prevention rule.
    /// </summary>
    public string Source { get; set; } = QsoSource.Zeus;

    /// <summary>
    /// The identity Wavelog deduplicates on: callsign, time to the minute, band
    /// and mode. Stored so a lookup is an index hit, and so both sides agree on
    /// what "the same QSO" means.
    /// </summary>
    public string DedupKey { get; set; } = "";

    public DateTime? WavelogUploadedUtc { get; set; }
    public string? WavelogError { get; set; }

    public static string MakeDedupKey(string callsign, DateTime whenUtc, string? band, string? mode) =>
        string.Join('|',
            callsign.Trim().ToUpperInvariant(),
            whenUtc.ToUniversalTime().ToString("yyyyMMddHHmm"),
            (band ?? "").Trim().ToLowerInvariant(),
            (mode ?? "").Trim().ToUpperInvariant());
}

public static class QsoSource
{
    public const string Zeus = "zeus";
    public const string Wavelog = "wavelog";
}
