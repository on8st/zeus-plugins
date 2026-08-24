// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using System.Text;

namespace Zeus.Plugin.Wavelog.Adif;

/// <summary>Raised when an ADIF stream cannot be trusted to parse correctly.</summary>
public sealed class AdifFormatException : Exception
{
    public AdifFormatException(string message) : base(message) { }
}

/// <summary>
/// Reads ADIF written by anyone — Wavelog, WSJT-X, another logger, a hand
/// edit. Forgiving about everything except the length prefix, which is the one
/// thing that cannot be recovered from if it is wrong: a bad length does not
/// corrupt one field, it desynchronises every field after it.
/// </summary>
public static class AdifParser
{
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Parse(string adif)
    {
        var records = new List<IReadOnlyDictionary<string, string>>();
        if (string.IsNullOrWhiteSpace(adif)) return records;

        // Work over bytes: ADIF lengths count UTF-8 bytes, so indexing the
        // string by character would slice multi-byte values in the wrong place.
        var bytes = Encoding.UTF8.GetBytes(adif);
        var i = SkipHeader(bytes);

        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sawField = false;

        while (i < bytes.Length)
        {
            if (bytes[i] != (byte)'<') { i++; continue; }

            var close = IndexOf(bytes, (byte)'>', i + 1);
            if (close < 0) break;                       // truncated tag — stop cleanly

            var tag = Encoding.UTF8.GetString(bytes, i + 1, close - i - 1);
            var parts = tag.Split(':');
            var name = parts[0].Trim().ToUpperInvariant();

            if (name is "EOR")
            {
                if (sawField) records.Add(current);
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sawField = false;
                i = close + 1;
                continue;
            }

            if (parts.Length < 2)
            {
                i = close + 1;                          // a lone marker such as <EOH>
                continue;
            }

            if (!int.TryParse(parts[1].Trim(), out var length) || length < 0)
                throw new AdifFormatException($"field '{name}' has a non-numeric length '{parts[1]}'");

            var start = close + 1;
            if (start + length > bytes.Length)
                throw new AdifFormatException(
                    $"field '{name}' declares {length} bytes but only {bytes.Length - start} remain");

            current[name] = Encoding.UTF8.GetString(bytes, start, length);
            sawField = true;
            i = start + length;
        }

        // Anything after the last <EOR> is a truncated record. Dropping it is
        // deliberate: a half-read QSO that looks real is worse than a missing one.
        return records;
    }

    private static int SkipHeader(byte[] bytes)
    {
        var eoh = IndexOfTag(bytes, "EOH");
        return eoh < 0 ? 0 : eoh;
    }

    private static int IndexOfTag(byte[] bytes, string tagName)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'<') continue;
            var close = IndexOf(bytes, (byte)'>', i + 1);
            if (close < 0) return -1;
            var tag = Encoding.UTF8.GetString(bytes, i + 1, close - i - 1).Trim();
            if (tag.Equals(tagName, StringComparison.OrdinalIgnoreCase)) return close + 1;
            i = close;
        }
        return -1;
    }

    private static int IndexOf(byte[] bytes, byte value, int from)
    {
        for (var i = from; i < bytes.Length; i++)
            if (bytes[i] == value) return i;
        return -1;
    }
}
