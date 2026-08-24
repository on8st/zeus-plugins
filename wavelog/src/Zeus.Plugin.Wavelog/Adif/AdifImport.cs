// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using System.Globalization;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Wavelog.Adif;

/// <summary>Turns one parsed ADIF record into a logbook entry.</summary>
public static class AdifImport
{
    /// <summary>Fields the typed model owns; the rest ride along in AdifFields.</summary>
    private static readonly HashSet<string> Typed = new(StringComparer.OrdinalIgnoreCase)
    {
        "CALL", "QSO_DATE", "TIME_ON", "BAND", "MODE", "SUBMODE", "FREQ",
        "RST_SENT", "RST_RCVD", "NAME", "GRIDSQUARE", "COUNTRY", "DXCC",
        "CQZ", "ITUZ", "STATE", "COMMENT",
    };

    public static LogbookNewEntry ToNewEntry(IReadOnlyDictionary<string, string> r)
    {
        var call = Get(r, "CALL");
        if (string.IsNullOrWhiteSpace(call))
            throw new AdifFormatException("record has no CALL");

        // MODE and SUBMODE are kept verbatim in the extras so a re-export says
        // exactly what was imported — guessing a Zeus mode back from ADIF would
        // change what Wavelog deduplicates on.
        var extras = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in r)
            if (!Typed.Contains(k) && !string.IsNullOrWhiteSpace(v))
                extras[k.ToUpperInvariant()] = v;

        var mode = Get(r, "MODE");
        if (!string.IsNullOrWhiteSpace(mode)) extras["MODE"] = mode!;
        var submode = Get(r, "SUBMODE");
        if (!string.IsNullOrWhiteSpace(submode)) extras["SUBMODE"] = submode!;

        return new LogbookNewEntry(
            Callsign: call!,
            Name: Get(r, "NAME"),
            FrequencyMhz: Double(r, "FREQ") ?? 0,
            Band: Get(r, "BAND") ?? "",
            Mode: string.IsNullOrWhiteSpace(submode) ? mode ?? "" : submode!,
            RstSent: Get(r, "RST_SENT") ?? "",
            RstRcvd: Get(r, "RST_RCVD") ?? "",
            Grid: Get(r, "GRIDSQUARE"),
            Country: Get(r, "COUNTRY"),
            Dxcc: Int(r, "DXCC"),
            CqZone: Int(r, "CQZ"),
            ItuZone: Int(r, "ITUZ"),
            State: Get(r, "STATE"),
            Comment: Get(r, "COMMENT"),
            QsoDateTimeUtc: When(r),
            AdifFields: extras.Count == 0 ? null : extras);
    }

    private static string? Get(IReadOnlyDictionary<string, string> r, string k)
        => r.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static double? Double(IReadOnlyDictionary<string, string> r, string k)
        => Get(r, k) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static int? Int(IReadOnlyDictionary<string, string> r, string k)
        => Get(r, k) is { } s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static DateTime? When(IReadOnlyDictionary<string, string> r)
    {
        var date = Get(r, "QSO_DATE");
        if (date is null) return null;
        var time = Get(r, "TIME_ON") ?? "000000";
        if (time.Length == 4) time += "00";
        return DateTime.TryParseExact(date + time, "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed) ? parsed : null;
    }
}
