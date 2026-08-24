// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using System.Globalization;
using System.Text;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Wavelog.Adif;

/// <summary>
/// Turns a logbook entry into one ADIF record.
///
/// <para>Pure and allocation-tolerant: it is off the realtime path entirely.
/// Correctness here matters more than it looks, because Wavelog deduplicates on
/// <c>CALL</c> + <c>TIME_ON</c> to the minute + <c>BAND</c> + <c>MODE</c> +
/// station. A formatting slip does not fail loudly — it silently creates a
/// second copy of the QSO.</para>
/// </summary>
public static class AdifMapper
{
    /// <summary>
    /// Zeus names a sideband; ADIF names a mode with an optional submode. Only
    /// mappings we are sure of live here — see <see cref="ResolveMode"/> for
    /// why anything else is passed through rather than guessed.
    /// </summary>
    private static readonly Dictionary<string, (string Mode, string? Submode)> ModeMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["USB"] = ("SSB", "USB"),
            ["LSB"] = ("SSB", "LSB"),
            ["CWU"] = ("CW", null),
            ["CWL"] = ("CW", null),
            ["CW"]  = ("CW", null),
            ["AM"]  = ("AM", null),
            ["FM"]  = ("FM", null),
        };

    /// <summary>Typed fields own these names; an extra field may not shadow one.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CALL", "QSO_DATE", "TIME_ON", "BAND", "MODE", "SUBMODE", "FREQ",
        "RST_SENT", "RST_RCVD", "NAME", "GRIDSQUARE", "COUNTRY", "DXCC",
        "CQZ", "ITUZ", "STATE", "COMMENT", "TX_PWR", "RIG", "MY_ANTENNA",
        "QSL_SENT", "QSL_RCVD", "LOTW_QSL_SENT", "LOTW_QSL_RCVD", "EOR",
    };

    public static string ToRecord(LogbookEntrySnapshot e)
    {
        var sb = new StringBuilder();
        var utc = ToUtc(e.QsoDateTimeUtc);
        var (mode, submode) = ResolveMode(e);

        Write(sb, "CALL", e.Callsign.Trim().ToUpperInvariant());
        Write(sb, "QSO_DATE", utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        Write(sb, "TIME_ON", utc.ToString("HHmmss", CultureInfo.InvariantCulture));
        Write(sb, "BAND", e.Band?.Trim().ToLowerInvariant());
        Write(sb, "MODE", mode);
        Write(sb, "SUBMODE", submode);

        if (e.FrequencyMhz is { } mhz)
            Write(sb, "FREQ", mhz.ToString("F6", CultureInfo.InvariantCulture));

        Write(sb, "RST_SENT", e.RstSent);
        Write(sb, "RST_RCVD", e.RstRcvd);
        Write(sb, "NAME", e.Name);
        Write(sb, "GRIDSQUARE", e.Grid);
        Write(sb, "COUNTRY", e.Country);
        Write(sb, "DXCC", e.Dxcc?.ToString(CultureInfo.InvariantCulture));
        Write(sb, "CQZ", e.CqZone?.ToString(CultureInfo.InvariantCulture));
        Write(sb, "ITUZ", e.ItuZone?.ToString(CultureInfo.InvariantCulture));
        Write(sb, "STATE", e.State);
        Write(sb, "COMMENT", e.Comment);
        Write(sb, "TX_PWR", e.TxPowerW?.ToString("0.###", CultureInfo.InvariantCulture));
        Write(sb, "RIG", e.Rig);
        Write(sb, "MY_ANTENNA", e.Antenna);
        Write(sb, "QSL_SENT", e.QslSent);
        Write(sb, "QSL_RCVD", e.QslRcvd);

        // Anything the typed model does not carry rides along untouched, which
        // is what keeps a round-trip through this plugin lossless.
        if (e.AdifFields is { Count: > 0 })
        {
            foreach (var (key, value) in e.AdifFields.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var name = key.Trim().ToUpperInvariant();
                if (Reserved.Contains(name)) continue;
                Write(sb, name, value);
            }
        }

        sb.Append("<EOR>");
        return sb.ToString();
    }

    /// <summary>
    /// An explicit <c>MODE</c> in the extra fields wins: WSJT-X and friends know
    /// the real mode, whereas Zeus only knows which sideband it was on.
    /// A mode we have no mapping for is passed through unchanged rather than
    /// guessed at — guessing would be wrong <em>quietly</em>, and Wavelog
    /// compares <c>MODE</c> exactly, so a wrong guess duplicates the QSO.
    /// </summary>
    private static (string Mode, string? Submode) ResolveMode(LogbookEntrySnapshot e)
    {
        if (e.AdifFields is not null &&
            e.AdifFields.TryGetValue("MODE", out var explicitMode) &&
            !string.IsNullOrWhiteSpace(explicitMode))
        {
            var name = explicitMode.Trim().ToUpperInvariant();
            e.AdifFields.TryGetValue("SUBMODE", out var explicitSub);
            return (name, string.IsNullOrWhiteSpace(explicitSub) ? null : explicitSub!.Trim().ToUpperInvariant());
        }

        var zeus = (e.Mode ?? string.Empty).Trim();
        return ModeMap.TryGetValue(zeus, out var mapped)
            ? mapped
            : (zeus.ToUpperInvariant(), null);
    }

    /// <summary>
    /// A local or unspecified timestamp is <em>converted</em>, never relabelled.
    /// Unspecified is treated as UTC because that is what the contract's field
    /// name promises.
    /// </summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>
    /// ADIF is length-prefixed, so a value is read by counting bytes rather
    /// than by scanning for a delimiter. That is why the format needs no
    /// escaping — and why the length must be the <b>UTF-8 byte</b> count, not
    /// the character count: get it wrong and every field after it is corrupt.
    /// </summary>
    private static void Write(StringBuilder sb, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.Append('<').Append(name).Append(':')
          .Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
          .Append('>').Append(value);
    }
}
