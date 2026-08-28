// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st

using Zeus.Plugin.Simpletx;

namespace Zeus.Plugin.Simpletx.Tests;

/// <summary>
/// The verdict is the whole point of the plugin, so it is the thing most worth
/// pinning down. Every case below is one the radio actually produces.
/// </summary>
public class TxDiagnosisTests
{
    [Fact]
    public void Idle_is_receiving()
    {
        Assert.Equal(TxVerdict.Receiving, TxDiagnosis.Diagnose(false, false, 36, 0));
    }

    [Fact]
    public void Idle_stays_receiving_even_with_no_telemetry()
    {
        Assert.Equal(TxVerdict.Receiving, TxDiagnosis.Diagnose(false, false, 36, null));
    }

    /// <summary>
    /// The failure this plugin exists for. Keyed, drive at zero, wire silent:
    /// PA current, temperature, FIFO depth and packet rate all read healthy,
    /// and only the wire peak gives it away.
    /// </summary>
    [Fact]
    public void Keyed_with_zero_drive_is_NoDrive()
    {
        Assert.Equal(TxVerdict.NoDrive, TxDiagnosis.Diagnose(true, false, 0, 0));
    }

    /// <summary>The second failure, waiting behind the first: drive restored,
    /// still nothing modulated.</summary>
    [Fact]
    public void Keyed_with_drive_but_silent_wire_is_NoAudio()
    {
        Assert.Equal(TxVerdict.NoAudio, TxDiagnosis.Diagnose(true, false, 36, 0));
    }

    [Fact]
    public void Keyed_with_samples_on_the_wire_is_transmitting()
    {
        Assert.Equal(TxVerdict.Transmitting, TxDiagnosis.Diagnose(true, false, 36, 11_796));
    }

    [Fact]
    public void Tune_with_samples_on_the_wire_is_reported_as_tuning()
    {
        Assert.Equal(TxVerdict.Tuning, TxDiagnosis.Diagnose(true, true, 10, 3_276));
    }

    /// <summary>Tune bypasses the audio chain, so a silent wire while tuning
    /// is still an RF fault rather than an audio one — and with drive at zero
    /// it is the same NoDrive answer.</summary>
    [Fact]
    public void Tune_with_silent_wire_and_no_drive_is_NoDrive()
    {
        Assert.Equal(TxVerdict.NoDrive, TxDiagnosis.Diagnose(false, true, 0, 0));
    }

    /// <summary>A host on the old contracts publishes no telemetry. Saying
    /// "transmitting" there would be a guess, so the panel says it cannot
    /// tell.</summary>
    [Fact]
    public void Keyed_without_telemetry_is_unknown_not_a_guess()
    {
        Assert.Equal(TxVerdict.Unknown, TxDiagnosis.Diagnose(true, false, 36, null));
    }

    [Theory]
    [InlineData(TxVerdict.Receiving)]
    [InlineData(TxVerdict.Transmitting)]
    [InlineData(TxVerdict.Tuning)]
    [InlineData(TxVerdict.NoDrive)]
    [InlineData(TxVerdict.NoAudio)]
    [InlineData(TxVerdict.Unknown)]
    public void Every_verdict_has_something_to_say(TxVerdict verdict)
    {
        Assert.False(string.IsNullOrWhiteSpace(TxDiagnosis.Explain(verdict, 36)));
    }

    [Fact]
    public void NoDrive_message_names_the_setting_to_change()
    {
        var message = TxDiagnosis.Explain(TxVerdict.NoDrive, 0);
        Assert.Contains("drive", message, StringComparison.OrdinalIgnoreCase);
    }
}
