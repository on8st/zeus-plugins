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
//
// Nothing here names the transmit contract surface. That is all behind
// TxBridge, so this file compiles unchanged against SDK 1.5.0 and against a
// 1.4.0 host that has none of it.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Simpletx;

/// <summary>Entry point. Holds no DSP and owns no audio; it reads radio state
/// and forwards operator intent to the controller.</summary>
public sealed class SimpletxPlugin : IZeusPlugin, IBackendPlugin
{
    private const string TimeoutKey = "txTimeoutSeconds";
    private const int DefaultTimeoutSeconds = 120;

    private readonly TxBridge _tx = new();
    private IPluginContext? _ctx;

    // Tune is the plugin's own idea of state: the controller takes the request
    // but publishes nothing back that distinguishes it from MOX.
    private bool _tuning;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        _tx.Attach(context);

        if (!_tx.HasTelemetry)
        {
            context.Logger.LogWarning(
                "No transmit telemetry from this host (bridge {Flavour}): the "
                + "meters will read blank and the verdict will be Unknown while "
                + "keyed, rather than guessing.", TxBridge.SdkFlavour);
        }

        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _tx.Detach();
        _ctx = null;
        return Task.CompletedTask;
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Everything the panel needs to draw itself, in one round trip. The
        // panel polls this; there is no push channel to a plugin's UI.
        endpoints.MapGet("state", async (CancellationToken ct) =>
        {
            if (!_tx.RadioAvailable) return Results.Ok(new { available = false });

            var snap = _tx.Latest;
            var keyed = _tx.Keyed;
            var drive = _tx.DrivePercent;
            int? peak = snap?.WirePeak;

            var verdict = TxDiagnosis.Diagnose(keyed, _tuning, drive, peak);

            return Results.Ok(new
            {
                available = true,
                sdk = TxBridge.SdkFlavour,
                degraded = !_tx.HasTelemetry,
                keyed,
                tuning = _tuning,
                frequencyHz = _tx.FrequencyHz,
                mode = _tx.Mode,
                band = _tx.Band,
                drivePercent = drive,
                micGainDb = _tx.MicGainDb,
                timeoutSeconds = await ReadTimeoutAsync(ct).ConfigureAwait(false),
                telemetry = snap is { } s
                    ? new
                    {
                        signalDbm = s.SignalDbm,
                        micPeakDbfs = s.MicPeakDbfs,
                        wirePeak = s.WirePeak,
                        forwardWatts = s.ForwardWatts,
                        reflectedWatts = s.ReflectedWatts,
                        swr = TxMath.Swr(s.ForwardWatts, s.ReflectedWatts),
                        paTempC = s.PaTempC,
                    }
                    : null,
                verdict = verdict.ToString(),
                message = TxDiagnosis.Explain(verdict, drive),
            });
        });

        endpoints.MapPost("mox", async (OnBody body, CancellationToken ct) =>
        {
            if (body.On) _tuning = false;
            await _tx.SetMoxAsync(body.On, ct).ConfigureAwait(false);
            return Results.Ok(new { moxOn = body.On });
        });

        endpoints.MapPost("tune", async (OnBody body, CancellationToken ct) =>
        {
            _tuning = body.On;
            await _tx.SetTuneAsync(body.On, ct).ConfigureAwait(false);
            return Results.Ok(new { tuning = body.On });
        });

        endpoints.MapPost("drive", async (PercentBody body, CancellationToken ct) =>
        {
            var pct = TxLimits.Percent(body.Percent);
            await _tx.SetDriveAsync(pct, ct).ConfigureAwait(false);
            return Results.Ok(new { drivePercent = pct });
        });

        endpoints.MapPost("drive-max", async (PercentBody body, CancellationToken ct) =>
        {
            var pct = TxLimits.MaxPercent(body.Percent);
            await _tx.SetDriveMaxAsync(pct, ct).ConfigureAwait(false);
            return Results.Ok(new { driveMaxPercent = pct });
        });

        endpoints.MapPost("mic-gain", async (DbBody body, CancellationToken ct) =>
        {
            var db = TxLimits.MicGainDb(body.Db);
            await _tx.SetMicGainAsync(db, ct).ConfigureAwait(false);
            return Results.Ok(new { micGainDb = db });
        });

        endpoints.MapPost("leveler", async (DbBody body, CancellationToken ct) =>
        {
            var db = TxLimits.LevelerDb(body.Db);
            await _tx.SetLevelerAsync(db, ct).ConfigureAwait(false);
            return Results.Ok(new { levelerMaxGainDb = db });
        });

        endpoints.MapPost("source", async (SourceBody body, CancellationToken ct) =>
        {
            var source = string.IsNullOrWhiteSpace(body.Source) ? "Host" : body.Source.Trim();
            await _tx.SetSourceAsync(source, ct).ConfigureAwait(false);
            return Results.Ok(new { txAudioSource = source });
        });

        endpoints.MapPost("filter", async (FilterBody body, CancellationToken ct) =>
        {
            var (lo, hi) = TxLimits.Filter(body.LowHz, body.HighHz);
            await _tx.SetFilterAsync(lo, hi, ct).ConfigureAwait(false);
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
        if (_ctx is null) return DefaultTimeoutSeconds;
        var stored = await _ctx.Settings.GetAsync<int?>(TimeoutKey, ct).ConfigureAwait(false);
        return stored is { } v ? TxLimits.TimeoutSeconds(v) : DefaultTimeoutSeconds;
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
