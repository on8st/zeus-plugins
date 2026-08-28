// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// The SDK 1.5.0 bridge: the real one. Compiled unless ZeusSdk14=true, in which
// case TxBridge.Legacy.cs takes its place.
//
// This is the ONLY file allowed to name the transmit contract surface —
// ITxTelemetry, TxFrame, SetDrivePercentAsync and the rest. Everything else in
// the assembly talks to TxBridge, so swapping this file for the legacy one is
// all it takes to target a host that has none of them.

using Zeus.Plugins.Contracts;

namespace Zeus.Plugin.Simpletx;

/// <summary>Radio access against a host that publishes the transmit path.</summary>
internal sealed class TxBridge
{
    private IRadioStateReader? _radio;
    private IRadioController? _control;
    private ITxTelemetry? _telemetry;
    private Action<TxFrame>? _onFrame;

    // A holder, because volatile needs a reference type and the snapshot is a
    // struct — a plain field could be read torn by a request handler while the
    // telemetry thread is writing it.
    private sealed class Held(TelemetrySnapshot value)
    {
        public TelemetrySnapshot Value { get; } = value;
    }

    private volatile Held? _last;

    public const string SdkFlavour = "1.5";

    public void Attach(IPluginContext context)
    {
        _radio = context.Radio;
        _control = context.RadioController;
        _telemetry = _radio?.Telemetry;

        if (_telemetry is null) return;

        _onFrame = frame => _last = new Held(new TelemetrySnapshot(
            frame.SignalDbm,
            double.IsNegativeInfinity(frame.MicPeakDbfs) ? null : frame.MicPeakDbfs,
            frame.WirePeak,
            frame.ForwardWatts,
            frame.ReflectedWatts,
            frame.PaTempC));

        _telemetry.Updated += _onFrame;
    }

    public void Detach()
    {
        if (_telemetry is not null && _onFrame is not null) _telemetry.Updated -= _onFrame;
        _onFrame = null;
        _telemetry = null;
        _control = null;
        _radio = null;
        _last = null;
    }

    public bool RadioAvailable => _radio is not null;
    public bool HasTelemetry => _telemetry is not null;
    public bool Keyed => _radio?.Mox ?? false;
    public long FrequencyHz => _radio?.FrequencyHz ?? 0;
    public string Mode => _radio?.Mode ?? "";
    public string Band => _radio?.Band ?? "";
    public int DrivePercent => _radio?.DrivePercent ?? 0;
    public double MicGainDb => _radio?.MicGainDb ?? 0;
    public TelemetrySnapshot? Latest => _last?.Value;

    public Task SetMoxAsync(bool on, CancellationToken ct) =>
        _control?.SetMoxAsync(on, ct) ?? Task.CompletedTask;

    public Task SetTuneAsync(bool on, CancellationToken ct) =>
        _control?.SetTuneAsync(on, ct) ?? Task.CompletedTask;

    public Task SetDriveAsync(int percent, CancellationToken ct) =>
        _control?.SetDrivePercentAsync(percent, ct) ?? Task.CompletedTask;

    public Task SetDriveMaxAsync(int percent, CancellationToken ct) =>
        _control?.SetDriveMaxPercentAsync(percent, ct) ?? Task.CompletedTask;

    public Task SetMicGainAsync(double db, CancellationToken ct) =>
        _control?.SetMicGainDbAsync(db, ct) ?? Task.CompletedTask;

    public Task SetLevelerAsync(double db, CancellationToken ct) =>
        _control?.SetLevelerMaxGainDbAsync(db, ct) ?? Task.CompletedTask;

    public Task SetSourceAsync(string source, CancellationToken ct) =>
        _control?.SetTxAudioSourceAsync(source, ct) ?? Task.CompletedTask;

    public Task SetFilterAsync(int lowHz, int highHz, CancellationToken ct) =>
        _control?.SetTxFilterAsync(lowHz, highHz, ct) ?? Task.CompletedTask;
}
