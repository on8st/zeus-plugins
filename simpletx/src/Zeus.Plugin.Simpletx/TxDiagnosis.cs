// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st

namespace Zeus.Plugin.Simpletx;

/// <summary>What the transmit path is actually doing, as opposed to what it
/// was asked to do.</summary>
public enum TxVerdict
{
    /// <summary>Not keyed.</summary>
    Receiving,

    /// <summary>Keyed, samples on the wire.</summary>
    Transmitting,

    /// <summary>Tune carrier, samples on the wire.</summary>
    Tuning,

    /// <summary>Keyed with drive at zero. The PA is biased and heating for
    /// no output.</summary>
    NoDrive,

    /// <summary>Keyed with drive up, but nothing is being modulated — no
    /// audio is reaching the modulator.</summary>
    NoAudio,

    /// <summary>Keyed, but the host publishes no telemetry, so whether
    /// anything is going out cannot be known.</summary>
    Unknown,
}

/// <summary>
/// Turns the transmit numbers into one answer.
/// <para>
/// The distinction that matters is <em>keyed</em> versus <em>transmitting</em>.
/// A radio can key correctly, bias its PA, hold a clean TX FIFO and a steady
/// packet rate while the buffer handed to it is all zeros — every other
/// reading looks healthy. Only the peak sample on the wire separates the two,
/// so it is the authority here and the requested settings are used solely to
/// explain <em>why</em> it is zero.
/// </para>
/// </summary>
public static class TxDiagnosis
{
    /// <param name="keyed">MOX or PTT asserted.</param>
    /// <param name="tuning">Tune carrier requested.</param>
    /// <param name="drivePercent">Requested drive, 0..100.</param>
    /// <param name="wirePeak">Peak sample handed to the radio, or null when
    /// the host publishes no telemetry.</param>
    public static TxVerdict Diagnose(bool keyed, bool tuning, int drivePercent, int? wirePeak)
    {
        if (!keyed && !tuning) return TxVerdict.Receiving;
        if (wirePeak is not { } peak) return TxVerdict.Unknown;

        if (peak > 0) return tuning ? TxVerdict.Tuning : TxVerdict.Transmitting;

        // Keyed and silent. Drive at zero explains it on its own; drive up
        // means the samples are arriving empty from further back.
        return drivePercent <= 0 ? TxVerdict.NoDrive : TxVerdict.NoAudio;
    }

    /// <summary>One line the operator can act on. Deliberately says what to do
    /// next, not just what is wrong.</summary>
    public static string Explain(TxVerdict verdict, int drivePercent) => verdict switch
    {
        TxVerdict.Receiving =>
            "Receiving. Key up to check the transmit path.",
        TxVerdict.Transmitting =>
            $"Transmitting at {drivePercent}% — samples on the wire.",
        TxVerdict.Tuning =>
            "Tuning — carrier on the wire, audio chain bypassed.",
        TxVerdict.NoDrive =>
            "Keyed, but drive is 0% — nothing is being transmitted. "
            + "The PA is biased and heating for no output.",
        TxVerdict.NoAudio =>
            "Keyed with drive up, but the wire is silent — no audio is "
            + "reaching the modulator. Try Tune to confirm the RF path.",
        TxVerdict.Unknown =>
            "Keyed. This host publishes no transmit telemetry, so whether "
            + "anything is going out cannot be shown here.",
        _ => "",
    };
}

/// <summary>Range limits applied before anything reaches the radio, so a bad
/// value from the panel cannot become a bad value on the air.</summary>
public static class TxLimits
{
    /// <summary>Drive and tune power, 0..100.</summary>
    public static int Percent(int value) => Math.Clamp(value, 0, 100);

    /// <summary>Drive ceiling, 1..100. Zero is excluded: a maximum of zero
    /// would silently make the radio unable to transmit at all, which is the
    /// exact failure this plugin exists to surface.</summary>
    public static int MaxPercent(int value) => Math.Clamp(value, 1, 100);

    /// <summary>Microphone gain, −12..+40 dB.</summary>
    public static double MicGainDb(double value) => Math.Clamp(value, -12.0, 40.0);

    /// <summary>Leveler maximum gain, 0..20 dB.</summary>
    public static double LevelerDb(double value) => Math.Clamp(value, 0.0, 20.0);

    /// <summary>Transmit timeout in seconds, 10..600.</summary>
    public static int TimeoutSeconds(int value) => Math.Clamp(value, 10, 600);

    /// <summary>Filter edges in Hz, ordered and held at least 100 Hz apart.</summary>
    public static (int LowHz, int HighHz) Filter(int lowHz, int highHz)
    {
        var lo = Math.Clamp(Math.Min(lowHz, highHz), 0, 20_000);
        var hi = Math.Clamp(Math.Max(lowHz, highHz), 0, 20_000);
        if (hi - lo < 100) hi = Math.Min(20_000, lo + 100);
        return (lo, hi);
    }
}

/// <summary>Small transmit computations kept out of the plugin class so they
/// can be tested without a host.</summary>
public static class TxMath
{
    /// <summary>
    /// SWR from forward and reflected power.
    /// <para>
    /// Null below a floor where the ratio is arithmetic on noise rather than a
    /// measurement — an idle HL2 reads a couple of counts on both, and turning
    /// that into "SWR 2.3" would be a fabrication. Null also when reflected
    /// meets or exceeds forward, where the formula diverges.
    /// </para>
    /// </summary>
    public static double? Swr(double forwardWatts, double reflectedWatts)
    {
        if (double.IsNaN(forwardWatts) || double.IsNaN(reflectedWatts)) return null;
        if (forwardWatts <= 0.05 || reflectedWatts < 0) return null;

        var rho = Math.Sqrt(reflectedWatts / forwardWatts);
        if (rho >= 0.999) return null;

        return (1 + rho) / (1 - rho);
    }
}
