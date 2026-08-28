// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st

namespace Zeus.Plugin.Simpletx;

/// <summary>
/// The plugin's own copy of one telemetry tick.
/// <para>
/// Deliberately not <c>Zeus.Plugins.Contracts.TxFrame</c>: that type does not
/// exist on SDK 1.4.0, and naming it anywhere outside
/// <c>TxBridge.Full.cs</c> would make this assembly fail to load there. Every
/// other file works in these terms so it compiles against either SDK.
/// </para>
/// </summary>
/// <param name="SignalDbm">Receive signal strength.</param>
/// <param name="MicPeakDbfs">Microphone peak; null when silent or unmeasured.</param>
/// <param name="WirePeak">Peak sample handed to the radio, 0..32767.</param>
/// <param name="ForwardWatts">Forward power.</param>
/// <param name="ReflectedWatts">Reflected power.</param>
/// <param name="PaTempC">PA temperature in degrees Celsius.</param>
public readonly record struct TelemetrySnapshot(
    double SignalDbm,
    double? MicPeakDbfs,
    int WirePeak,
    double ForwardWatts,
    double ReflectedWatts,
    double PaTempC);
