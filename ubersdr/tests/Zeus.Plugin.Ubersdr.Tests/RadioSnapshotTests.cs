// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugin.Ubersdr.Backend;
using Zeus.Plugin.Ubersdr.Domain;

namespace Zeus.Plugin.Ubersdr.Tests;

/// <summary>
/// Which frequency the wall points at.
///
/// <para>The single most damaging thing this plugin could get wrong: under split
/// the transmit frequency is not the VFO, so tuning receivers to the VFO
/// monitors an empty channel and reports that nobody hears the operator. Silent,
/// plausible, and completely wrong — the exact failure shape this repository has
/// already shipped once, in a different plugin.</para>
/// </summary>
public class RadioSnapshotTests
{
    [Fact]
    public void Simplex_transmits_on_the_vfo()
    {
        var r = new RadioSnapshot(7_200_000, "LSB", SplitEnabled: false, SplitTxHz: 0, MoxOn: false);
        Assert.Equal(7_200_000, r.TransmitHz);
    }

    [Fact]
    public void Split_transmits_on_the_split_frequency()
    {
        var r = new RadioSnapshot(14_195_000, "USB", SplitEnabled: true, SplitTxHz: 14_200_000, MoxOn: true);
        Assert.Equal(14_200_000, r.TransmitHz);
    }

    [Fact]
    public void Split_enabled_with_no_split_frequency_falls_back_to_the_vfo()
    {
        // The engine reports splitTxHz as 0 when split is off; if it ever
        // reports the flag without the frequency, monitoring the VFO is far
        // better than monitoring DC.
        var r = new RadioSnapshot(14_195_000, "USB", SplitEnabled: true, SplitTxHz: 0, MoxOn: false);
        Assert.Equal(14_195_000, r.TransmitHz);
    }

    [Fact]
    public void The_band_offered_follows_the_transmit_frequency_not_the_vfo()
    {
        // Contrived but not impossible: listening at the top of 40m and
        // transmitting outside it. The wall must follow where the signal
        // actually goes.
        var r = new RadioSnapshot(7_290_000, "LSB", SplitEnabled: true, SplitTxHz: 14_200_000, MoxOn: false);
        Assert.Equal("20m", Band.FromHz(r.TransmitHz));
    }
}
