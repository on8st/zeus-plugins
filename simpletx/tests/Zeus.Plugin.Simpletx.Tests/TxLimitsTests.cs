// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st

using Zeus.Plugin.Simpletx;

namespace Zeus.Plugin.Simpletx.Tests;

/// <summary>Limits are applied in the backend rather than the panel, so a
/// value that never went through the UI still cannot reach the radio.</summary>
public class TxLimitsTests
{
    [Theory]
    [InlineData(-40, 0)]
    [InlineData(0, 0)]
    [InlineData(36, 36)]
    [InlineData(100, 100)]
    [InlineData(4000, 100)]
    public void Drive_is_held_between_zero_and_full(int input, int expected)
    {
        Assert.Equal(expected, TxLimits.Percent(input));
    }

    /// <summary>A ceiling of zero would make the radio unable to transmit
    /// while every control still looked set — the exact class of silent
    /// failure this plugin exists to surface, so it is not reachable.</summary>
    [Fact]
    public void Drive_ceiling_can_never_be_zero()
    {
        Assert.Equal(1, TxLimits.MaxPercent(0));
        Assert.Equal(1, TxLimits.MaxPercent(-10));
    }

    [Fact]
    public void Mic_gain_is_bounded_both_ways()
    {
        Assert.Equal(-12.0, TxLimits.MicGainDb(-99));
        Assert.Equal(40.0, TxLimits.MicGainDb(1000));
        Assert.Equal(0.0, TxLimits.MicGainDb(0));
    }

    [Fact]
    public void Timeout_stays_within_something_a_PA_survives()
    {
        Assert.Equal(10, TxLimits.TimeoutSeconds(0));
        Assert.Equal(600, TxLimits.TimeoutSeconds(9999));
        Assert.Equal(120, TxLimits.TimeoutSeconds(120));
    }

    [Fact]
    public void Filter_edges_come_back_in_order_however_they_go_in()
    {
        var (lo, hi) = TxLimits.Filter(2850, 0);
        Assert.Equal(0, lo);
        Assert.Equal(2850, hi);
    }

    [Fact]
    public void Filter_never_collapses_to_zero_width()
    {
        var (lo, hi) = TxLimits.Filter(1500, 1500);
        Assert.True(hi - lo >= 100, $"expected at least 100 Hz, got {hi - lo}");
    }

    [Fact]
    public void Filter_passes_a_normal_ssb_setting_through_untouched()
    {
        var (lo, hi) = TxLimits.Filter(0, 2850);
        Assert.Equal((0, 2850), (lo, hi));
    }
}

/// <summary>
/// SWR is the reading most likely to be believed without question, so the
/// cases where it must refuse to answer matter more than the arithmetic.
/// </summary>
public class SwrTests
{
    /// <summary>An idle HL2 reads a couple of ADC counts on both forward and
    /// reflected. Dividing one by the other yields a confident-looking number
    /// that means nothing.</summary>
    [Fact]
    public void No_forward_power_means_no_SWR_rather_than_a_made_up_one()
    {
        Assert.Null(TxMath.Swr(0.0, 0.0));
        Assert.Null(TxMath.Swr(0.02, 0.01));
    }

    [Fact]
    public void A_matched_load_reads_one_to_one()
    {
        var swr = TxMath.Swr(10.0, 0.0);
        Assert.NotNull(swr);
        Assert.Equal(1.0, swr!.Value, 3);
    }

    /// <summary>rho = 1/3 gives 2:1, the textbook case.</summary>
    [Fact]
    public void Known_mismatch_gives_the_textbook_ratio()
    {
        var swr = TxMath.Swr(9.0, 1.0);
        Assert.NotNull(swr);
        Assert.Equal(2.0, swr!.Value, 3);
    }

    [Fact]
    public void Total_reflection_does_not_divide_by_zero()
    {
        Assert.Null(TxMath.Swr(5.0, 5.0));
        Assert.Null(TxMath.Swr(5.0, 6.0));
    }

    [Fact]
    public void Nonsense_input_is_refused_rather_than_propagated()
    {
        Assert.Null(TxMath.Swr(double.NaN, 1.0));
        Assert.Null(TxMath.Swr(10.0, double.NaN));
        Assert.Null(TxMath.Swr(10.0, -1.0));
    }
}
