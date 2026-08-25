// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugin.Ubersdr.Domain;

namespace Zeus.Plugin.Ubersdr.Tests;

/// <summary>
/// Mapping a frequency to a band, which is how the wall answers "who is hearing
/// what I am on".
/// </summary>
public class BandTests
{
    [Theory]
    [InlineData(1_840_000, "160m")]
    [InlineData(3_573_000, "80m")]
    [InlineData(7_200_000, "40m")]     // the operator's own frequency during this work
    [InlineData(10_136_000, "30m")]
    [InlineData(14_074_000, "20m")]
    [InlineData(18_100_000, "17m")]
    [InlineData(21_074_000, "15m")]
    [InlineData(24_915_000, "12m")]
    [InlineData(28_074_000, "10m")]
    [InlineData(50_313_000, "6m")]
    [InlineData(144_174_000, "2m")]
    public void A_frequency_maps_to_its_band(long hz, string expected)
        => Assert.Equal(expected, Band.FromHz(hz));

    [Fact]
    public void Band_edges_are_inclusive()
    {
        // An operator sitting exactly on the edge still wants the wall to work.
        Assert.Equal("40m", Band.FromHz(7_000_000));
        Assert.Equal("40m", Band.FromHz(7_300_000));
    }

    [Fact]
    public void A_frequency_outside_every_amateur_band_has_no_band()
    {
        // Shortwave broadcast is a legitimate thing to be tuned to, and forcing
        // it into the nearest band would silently offer receivers for a band
        // the operator is not on.
        Assert.Null(Band.FromHz(9_500_000));      // 31m broadcast
        Assert.Null(Band.FromHz(10_000_000));     // WWV
        Assert.Null(Band.FromHz(0));
    }
}
