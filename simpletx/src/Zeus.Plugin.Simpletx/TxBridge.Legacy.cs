// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// The SDK 1.4.0 bridge. Compiled instead of TxBridge.Full.cs when the build
// sets ZeusSdk14=true, for a host whose contracts predate the transmit path.
//
// It names nothing the 1.4.0 contracts do not have. MOX is the one control
// that exists there; everything else accepts the call and does nothing, and
// there is no telemetry at all.
//
// This exists to prove the panel, the routes and the manifest wire up on a
// released engine. It cannot make the radio transmit and does not pretend to:
// with no telemetry the verdict is Unknown, so the panel says it cannot tell
// rather than inventing an answer.

using Zeus.Plugins.Contracts;

namespace Zeus.Plugin.Simpletx;

/// <summary>Radio access against a host that predates the transmit contracts.</summary>
internal sealed class TxBridge
{
    private IRadioStateReader? _radio;
    private IRadioController? _control;

    public const string SdkFlavour = "1.4-degraded";

    public void Attach(IPluginContext context)
    {
        _radio = context.Radio;
        _control = context.RadioController;
    }

    public void Detach()
    {
        _control = null;
        _radio = null;
    }

    public bool RadioAvailable => _radio is not null;

    /// <summary>Always false here: 1.4.0 publishes no transmit telemetry, which
    /// is what drives the panel's Unknown verdict.</summary>
    public bool HasTelemetry => false;

    public bool Keyed => _radio?.Mox ?? false;
    public long FrequencyHz => _radio?.FrequencyHz ?? 0;
    public string Mode => _radio?.Mode ?? "";
    public string Band => _radio?.Band ?? "";

    /// <summary>Not readable on 1.4.0. Zero here means "unknown", and the panel
    /// shows it as such rather than as a real setting.</summary>
    public int DrivePercent => 0;

    /// <summary>Not readable on 1.4.0.</summary>
    public double MicGainDb => 0;

    /// <summary>Never anything: no telemetry exists to snapshot.</summary>
    public TelemetrySnapshot? Latest => null;

    // The one control 1.4.0 has.
    public Task SetMoxAsync(bool on, CancellationToken ct) =>
        _control?.SetMoxAsync(on, ct) ?? Task.CompletedTask;

    // Everything below is accepted and dropped. Returning success would be a
    // lie, but failing the request would make the panel look broken when the
    // truth is that this host cannot do it — the panel reports the degraded
    // flavour instead, once, where the operator can see it.
    public Task SetTuneAsync(bool on, CancellationToken ct) => Task.CompletedTask;
    public Task SetDriveAsync(int percent, CancellationToken ct) => Task.CompletedTask;
    public Task SetDriveMaxAsync(int percent, CancellationToken ct) => Task.CompletedTask;
    public Task SetMicGainAsync(double db, CancellationToken ct) => Task.CompletedTask;
    public Task SetLevelerAsync(double db, CancellationToken ct) => Task.CompletedTask;
    public Task SetSourceAsync(string source, CancellationToken ct) => Task.CompletedTask;
    public Task SetFilterAsync(int lowHz, int highHz, CancellationToken ct) => Task.CompletedTask;
}
