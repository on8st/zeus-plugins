// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st

namespace Zeus.Plugin.Ubersdr.Domain;

/// <summary>
/// Which amateur band a frequency falls in.
///
/// <para>Used to answer "who is hearing the band I am on", so the edges only
/// need to be right to the nearest band, not to the nearest regulation. The
/// ranges are the ITU Region 1 allocations with a little slack, because an
/// operator sitting on the band edge still wants the wall to work.</para>
/// </summary>
public static class Band
{
    private static readonly (long LowHz, long HighHz, string Name)[] Bands =
    [
        (  135_700,     137_800, "2200m"),
        (  472_000,     479_000, "630m"),
        (1_800_000,   2_000_000, "160m"),
        (3_500_000,   4_000_000, "80m"),
        (5_250_000,   5_450_000, "60m"),
        (7_000_000,   7_300_000, "40m"),
        (10_100_000, 10_150_000, "30m"),
        (14_000_000, 14_350_000, "20m"),
        (18_068_000, 18_168_000, "17m"),
        (21_000_000, 21_450_000, "15m"),
        (24_890_000, 24_990_000, "12m"),
        (28_000_000, 29_700_000, "10m"),
        (50_000_000, 54_000_000, "6m"),
        (70_000_000, 70_500_000, "4m"),
        (144_000_000, 148_000_000, "2m"),
    ];

    /// <summary>
    /// The band containing <paramref name="hz"/>, or <c>null</c> outside every
    /// amateur allocation — a shortwave broadcast frequency is a legitimate
    /// thing to be tuned to and must not be forced into the nearest band.
    /// </summary>
    public static string? FromHz(long hz)
    {
        foreach (var (low, high, name) in Bands)
            if (hz >= low && hz <= high) return name;
        return null;
    }

    /// <summary>Every band name, in frequency order. For UI, not for logic.</summary>
    public static IReadOnlyList<string> All => Bands.Select(b => b.Name).ToList();
}
