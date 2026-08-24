// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Wavelog.Storage;

/// <summary>
/// The persisted shape of one QSO.
///
/// <para>It mirrors <see cref="LogbookEntrySnapshot"/> and adds the fields this
/// plugin owns: where the contact came from, and how far it has got towards
/// Wavelog. Those live here rather than in <c>AdifFields</c> because they are
/// this plugin's bookkeeping, not part of the QSO — they must never reach an
/// ADIF export or a Wavelog push.</para>
/// </summary>
public sealed class StoredQso
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTime QsoDateTimeUtc { get; set; }
    public string Callsign { get; set; } = "";
    /// <summary>Upper-cased copy, so a lookup never depends on how it was typed.</summary>
    public string CallsignKey { get; set; } = "";
    public string? Name { get; set; }
    public double? FrequencyMhz { get; set; }
    public string Band { get; set; } = "";
    public string Mode { get; set; } = "";
    public string RstSent { get; set; } = "";
    public string RstRcvd { get; set; } = "";
    public string? Grid { get; set; }
    public string? Country { get; set; }
    public int? Dxcc { get; set; }
    public int? CqZone { get; set; }
    public int? ItuZone { get; set; }
    public string? State { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public string? QrzLogId { get; set; }
    public DateTime? QrzUploadedUtc { get; set; }
    public Dictionary<string, string>? AdifFields { get; set; }

    public List<string>? Tags { get; set; }
    public string? QslSent { get; set; }
    public string? QslRcvd { get; set; }
    public DateTime? QslSentDate { get; set; }
    public DateTime? QslRcvdDate { get; set; }
    public DateTime? LotwQslSentUtc { get; set; }
    public DateTime? LotwQslRcvdUtc { get; set; }
    public DateTime? QrzQslRcvdUtc { get; set; }
    public string? Rig { get; set; }
    public string? Antenna { get; set; }
    public double? TxPowerW { get; set; }

    // ---- this plugin's own bookkeeping --------------------------------------

    /// <summary>
    /// Where the QSO entered this store. <c>wavelog</c> means it arrived on the
    /// pull, and must therefore never be pushed back — the loop-prevention rule.
    /// </summary>
    public string Source { get; set; } = QsoSource.Zeus;

    public DateTime? WavelogUploadedUtc { get; set; }
    public string? WavelogError { get; set; }

    /// <summary>
    /// The same identity Wavelog deduplicates on: callsign, time to the minute,
    /// band and mode. Held as a stored field so a lookup is an index hit rather
    /// than a scan, and so both sides agree on what "the same QSO" means.
    /// </summary>
    public string DedupKey { get; set; } = "";

    public static string MakeDedupKey(string callsign, DateTime whenUtc, string? band, string? mode) =>
        string.Join('|',
            callsign.Trim().ToUpperInvariant(),
            whenUtc.ToUniversalTime().ToString("yyyyMMddHHmm"),
            (band ?? "").Trim().ToLowerInvariant(),
            (mode ?? "").Trim().ToUpperInvariant());

    /// <summary>
    /// LiteDB stores dates as UTC but hands them back in <em>local</em> time.
    /// For a logbook that is not cosmetic: it would shift every QSO time the
    /// operator sees, and — because the dedup key is built from the timestamp
    /// to the minute — it would make Wavelog treat the same contact as a new
    /// one. Every date is therefore normalised on the way out as well as in.
    /// </summary>
    private static DateTime U(DateTime v) => v.Kind switch
    {
        DateTimeKind.Utc => v,
        DateTimeKind.Local => v.ToUniversalTime(),
        _ => DateTime.SpecifyKind(v, DateTimeKind.Utc),
    };

    private static DateTime? U(DateTime? v) => v is null ? null : U(v.Value);

    public LogbookEntrySnapshot ToSnapshot() => new(
        Id: Id,
        QsoDateTimeUtc: U(QsoDateTimeUtc),
        Callsign: Callsign,
        Name: Name,
        FrequencyMhz: FrequencyMhz,
        Band: Band,
        Mode: Mode,
        RstSent: RstSent,
        RstRcvd: RstRcvd,
        Grid: Grid,
        Country: Country,
        Dxcc: Dxcc,
        CqZone: CqZone,
        ItuZone: ItuZone,
        State: State,
        Comment: Comment,
        CreatedUtc: U(CreatedUtc),
        QrzLogId: QrzLogId,
        QrzUploadedUtc: U(QrzUploadedUtc),
        AdifFields: AdifFields)
    {
        Tags = Tags,
        QslSent = QslSent,
        QslRcvd = QslRcvd,
        QslSentDate = U(QslSentDate),
        QslRcvdDate = U(QslRcvdDate),
        LotwQslSentUtc = U(LotwQslSentUtc),
        LotwQslRcvdUtc = U(LotwQslRcvdUtc),
        QrzQslRcvdUtc = U(QrzQslRcvdUtc),
        Rig = Rig,
        Antenna = Antenna,
        TxPowerW = TxPowerW,
    };
}

public static class QsoSource
{
    public const string Zeus = "zeus";
    public const string Wavelog = "wavelog";
}
