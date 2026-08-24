// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using System.Text;

namespace FakeWavelog;

/// <summary>
/// A deliberately separate ADIF reader.
///
/// <para>The fake must not share the plugin's parser: if both used the same
/// code, a bug in it would cancel itself out and the round-trip test would pass
/// while real Wavelog rejected everything. This is the independent reader that
/// makes the round trip mean something.</para>
/// </summary>
public static class MiniAdif
{
    public static List<Dictionary<string, string>> Parse(string adif)
    {
        var records = new List<Dictionary<string, string>>();
        if (string.IsNullOrWhiteSpace(adif)) return records;

        var bytes = Encoding.UTF8.GetBytes(adif);
        var i = 0;
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sawField = false;
        var pastHeader = adif.IndexOf("<EOH>", StringComparison.OrdinalIgnoreCase) < 0;

        while (i < bytes.Length)
        {
            if (bytes[i] != (byte)'<') { i++; continue; }
            var close = Array.IndexOf(bytes, (byte)'>', i + 1);
            if (close < 0) break;

            var tag = Encoding.UTF8.GetString(bytes, i + 1, close - i - 1);
            var parts = tag.Split(':');
            var name = parts[0].Trim().ToUpperInvariant();

            if (name == "EOH") { pastHeader = true; i = close + 1; current.Clear(); sawField = false; continue; }
            if (name == "EOR")
            {
                if (pastHeader && sawField) records.Add(new(current, StringComparer.OrdinalIgnoreCase));
                current.Clear(); sawField = false; i = close + 1; continue;
            }
            if (parts.Length < 2 || !int.TryParse(parts[1].Trim(), out var len) || len < 0)
            { i = close + 1; continue; }

            var start = close + 1;
            if (start + len > bytes.Length) break;
            current[name] = Encoding.UTF8.GetString(bytes, start, len);
            sawField = true;
            i = start + len;
        }
        return records;
    }
}
