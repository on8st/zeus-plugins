// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// Simple TX — the transmit settings that can silently stop you transmitting,
// on one face, with the meters that say whether any of it reached the air.
//
// The panel is served by the product on a different origin from the engine, so
// it cannot fetch the engine's own /api/tx/* routes directly. Everything goes
// through api.callBackend into the endpoints mapped here, which is also what
// keeps the ControlRadio capability meaningful: the host granted it to this
// plugin, and only this plugin can use it.

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Simpletx;

/// <summary>Entry point. Holds no DSP and owns no audio; it reads radio state
/// and forwards operator intent to the controller.</summary>
public sealed class SimpletxPlugin : IZeusPlugin, IBackendPlugin
{
    private const string TimeoutKey = "txTimeoutSeconds";

    private IPluginContext? _ctx;
    private IRadioStateReader? _radio;
    private IRadioController? _control;
    private ITxTelemetry? _telemetry;
    private Action<TxFrame>? _onFrame;

    // Last frame the host pushed. Written on the telemetry thread and read by
    // request handlers, so it is swapped whole rather than mutated in place.
    // A holder, because volatile and Volatile.Write need a reference type and
    // TxFrame is a struct — a plain field could be read torn.
    private sealed class Frame(TxFrame value)
    {
        public TxFrame Value { get; } = value;
    }

    private volatile Frame? _last;

    // Tune is the plugin's own idea of state: the controller takes the
    // request but publishes nothing back that distinguishes it from MOX.
    private bool _tuning;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        _radio = context.Radio;
        _control = context.RadioController;

        _telemetry = _radio?.Telemetry;
        if (_telemetry is not null)
        {
            _onFrame = frame => _last = new Frame(frame);
            _telemetry.Updated += _onFrame;
        }
        else
        {
            context.Logger.LogWarning(
                "No transmit telemetry from this host: the meters and the "
                + "verdict line will report Unknown while keyed.");
        }

        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        if (_telemetry is not null && _onFrame is not null)
        {
            _telemetry.Updated -= _onFrame;
        }

        _onFrame = null;
        _telemetry = null;
        _control = null;
        _radio = null;
        _ctx = null;
        return Task.CompletedTask;
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Everything the panel needs to draw itself, in one round trip. The
        // panel polls this; there is no push channel to a plugin's UI.
        endpoints.MapGet("state", async (CancellationToken ct) =>
        {
            var radio = _radio;
            if (radio is null) return Results.Ok(new { available = false });

            var frame = _last?.Value;
            var keyed = radio.Mox;
            var drive = radio.DrivePercent;
            int? peak = frame?.WirePeak;

            var verdict = TxDiagnosis.Diagnose(keyed, _tuning, drive, peak);

            return Results.Ok(new
            {
                available = true,
                keyed,
                tuning = _tuning,
                frequencyHz = radio.FrequencyHz,
                mode = radio.Mode,
                band = radio.Band,
                drivePercent = drive,
                micGainDb = radio.MicGainDb,
                timeoutSeconds = await ReadTimeoutAsync(ct).ConfigureAwait(false),
                telemetry = frame is { } f
                    ? new
                    {
                        available = true,
                        signalDbm = f.SignalDbm,
                        micPeakDbfs = double.IsNegativeInfinity(f.MicPeakDbfs) ? (double?)null : f.MicPeakDbfs,
                        wirePeak = f.WirePeak,
                        forwardWatts = f.ForwardWatts,
                        reflectedWatts = f.ReflectedWatts,
                        swr = TxMath.Swr(f.ForwardWatts, f.ReflectedWatts),
                        paTempC = f.PaTempC,
                    }
                    : null,
                verdict = verdict.ToString(),
                message = TxDiagnosis.Explain(verdict, drive),
            });
        });

        endpoints.MapPost("mox", async (OnBody body, CancellationToken ct) =>
        {
            if (_control is null) return Results.Problem("No radio controller.");
            if (body.On) _tuning = false;
            await _control.SetMoxAsync(body.On, ct).ConfigureAwait(false);
            return Results.Ok(new { moxOn = body.On });
        });

        endpoints.MapPost("tune", async (OnBody body, CancellationToken ct) =>
        {
            if (_control is null) return Results.Problem("No radio controller.");
            _tuning = body.On;
            await _control.SetTuneAsync(body.On, ct).ConfigureAwait(false);
            return Results.Ok(new { tuning = body.On });
        });

        endpoints.MapPost("drive", async (PercentBody body, CancellationToken ct) =>
        {
            if (_control is null) return Results.Problem("No radio controller.");
            var pct = TxLimits.Percent(body.Percent);
            await _control.SetDrivePercentAsync(pct, ct).ConfigureAwait(false);
            return Results.Ok(new { drivePercent = pct });
        });

        endpoints.MapPost("drive-max", async (PercentBody body, CancellationToken ct) =>
        {
            if (_control is null) return Results.Problem("No radio controller.");
            var pct = TxLimits.MaxPercent(body.Percent);
            await _control.SetDriveMaxPercentAsync(pct, ct).ConfigureAwait(false);
            return Results.Ok(new { driveMaxPercent = pct });
        });

        endpoints.MapPost("mic-gain", async (DbBody body, CancellationToken ct) =>
        {
            if (_control is null) return Results.Problem("No radio controller.");
            var db = TxLimits.MicGainDb(body.Db);
            await _control.SetMicGainDbAsync(db, ct).ConfigureAwait(false);
            return Results.Ok(new { micGainDb = db });
        });

        endpoints.MapPost("leveler", async (DbBody body, CancellationToken ct) =>
        {
            if (_control is null) return Results.Problem("No radio controller.");
            var db = TxLimits.LevelerDb(body.Db);
            await _control.SetLevelerMaxGainDbAsync(db, ct).ConfigureAwait(false);
            return Results.Ok(new { levelerMaxGainDb = db });
        });

        endpoints.MapPost("source", async (SourceBody body, CancellationToken ct) =>
        {
            if (_control is null) return Results.Problem("No radio controller.");
            var source = string.IsNullOrWhiteSpace(body.Source) ? "Host" : body.Source.Trim();
            await _control.SetTxAudioSourceAsync(source, ct).ConfigureAwait(false);
            return Results.Ok(new { txAudioSource = source });
        });

        endpoints.MapPost("filter", async (FilterBody body, CancellationToken ct) =>
        {
            if (_control is null) return Results.Problem("No radio controller.");
            var (lo, hi) = TxLimits.Filter(body.LowHz, body.HighHz);
            await _control.SetTxFilterAsync(lo, hi, ct).ConfigureAwait(false);
            return Results.Ok(new { lowHz = lo, highHz = hi });
        });

        // Timeout is the plugin's own setting: the engine publishes
        // txTimeoutSec but the controller exposes no way to set it.
        endpoints.MapPost("timeout", async (SecondsBody body, CancellationToken ct) =>
        {
            var seconds = TxLimits.TimeoutSeconds(body.Seconds);
            if (_ctx is not null)
            {
                await _ctx.Settings.SetAsync(TimeoutKey, seconds, ct).ConfigureAwait(false);
            }
            return Results.Ok(new { timeoutSeconds = seconds });
        });
    }

    private async Task<int> ReadTimeoutAsync(CancellationToken ct)
    {
        if (_ctx is null) return 120;
        var stored = await _ctx.Settings.GetAsync<int?>(TimeoutKey, ct).ConfigureAwait(false);
        return stored is { } v ? TxLimits.TimeoutSeconds(v) : 120;
    }

    // Request bodies. Records rather than anonymous types so the minimal API
    // model binder has something to bind to.
    internal sealed record OnBody(bool On);
    internal sealed record PercentBody(int Percent);
    internal sealed record DbBody(double Db);
    internal sealed record SecondsBody(int Seconds);
    internal sealed record SourceBody(string Source);
    internal sealed record FilterBody(int LowHz, int HighHz);
}
