// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// Simple TX — the transmit settings that can silently stop you transmitting,
// on one face, with the one diagnosis that says whether a keyed radio can
// transmit at all.
//
// Two hops, for two different reasons. The panel cannot call the engine
// directly because it is served by the product on a different origin, so it
// calls these routes. These routes cannot use IPluginContext.Radio because no
// engine build provides one, so they call the engine's own HTTP API from
// inside its process — the same conclusion ubersdr reached.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Simpletx;

/// <summary>Entry point. Holds no DSP and owns no audio.</summary>
public sealed class SimpletxPlugin : IZeusPlugin, IBackendPlugin
{
    private HttpClient? _http;
    private TxBridge? _tx;
    private ILogger? _log;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _log = context.Logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var engine = new EngineClient(_http, _log);
        _tx = new TxBridge(engine);

        if (!engine.Reachable)
        {
            _log.LogWarning(
                "simpletx: no --port on the engine command line, so the engine "
                + "API cannot be reached and the panel will report no radio.");
        }

        // Worth stating once at load rather than leaving someone to wonder why
        // three meters are blank.
        _log.LogInformation(
            "simpletx: controls go through the engine API at {Base}. Metering is "
            + "not exposed there — the wire peak is only in the p1.tx.rate log "
            + "line and the meters reach the product over the binary /ws hub — "
            + "so the panel shows drive-based diagnosis without meters.",
            engine.BaseUrl);

        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _http?.Dispose();
        _http = null;
        _tx = null;
        _log = null;
        return Task.CompletedTask;
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Everything the panel needs to draw itself, in one round trip.
        endpoints.MapGet("state", async (CancellationToken ct) =>
        {
            if (_tx is null) return Results.Ok(new { available = false });

            var r = await _tx.ReadAsync(ct).ConfigureAwait(false);
            if (!r.Available) return Results.Ok(new { available = false });

            // No wire peak is reachable over HTTP, so the verdict is told so
            // explicitly rather than being handed a fabricated zero — which
            // would read as "transmitting nothing" on a healthy radio.
            var verdict = TxDiagnosis.Diagnose(r.Keyed, r.Tuning, r.DrivePercent, wirePeak: null);

            return Results.Ok(new
            {
                available = true,
                metering = TxBridge.MeteringAvailable,
                keyed = r.Keyed,
                tuning = r.Tuning,
                frequencyHz = r.FrequencyHz,
                mode = r.Mode,
                drivePercent = r.DrivePercent,
                driveMaxPercent = r.DriveMaxPercent,
                tunePercent = r.TunePercent,
                micGainDb = r.MicGainDb,
                levelerMaxGainDb = r.LevelerMaxGainDb,
                txAudioSource = r.TxAudioSource,
                txFilterLowHz = r.TxFilterLowHz,
                txFilterHighHz = r.TxFilterHighHz,
                timeoutSeconds = r.TimeoutSeconds,
                verdict = verdict.ToString(),
                message = TxDiagnosis.Explain(verdict, r.DrivePercent),
            });
        });

        endpoints.MapPost("mox", async (OnBody b, CancellationToken ct) =>
            Ok(await Bridge().SetMoxAsync(b.On, ct).ConfigureAwait(false), new { moxOn = b.On }));

        endpoints.MapPost("tune", async (OnBody b, CancellationToken ct) =>
            Ok(await Bridge().SetTuneAsync(b.On, ct).ConfigureAwait(false), new { tuning = b.On }));

        endpoints.MapPost("drive", async (PercentBody b, CancellationToken ct) =>
        {
            var pct = TxLimits.Percent(b.Percent);
            return Ok(await Bridge().SetDriveAsync(pct, ct).ConfigureAwait(false),
                new { drivePercent = pct });
        });

        endpoints.MapPost("drive-max", async (PercentBody b, CancellationToken ct) =>
        {
            var pct = TxLimits.MaxPercent(b.Percent);
            return Ok(await Bridge().SetDriveMaxAsync(pct, ct).ConfigureAwait(false),
                new { driveMaxPercent = pct });
        });

        endpoints.MapPost("tune-drive", async (PercentBody b, CancellationToken ct) =>
        {
            var pct = TxLimits.Percent(b.Percent);
            return Ok(await Bridge().SetTuneDriveAsync(pct, ct).ConfigureAwait(false),
                new { tunePercent = pct });
        });

        endpoints.MapPost("mic-gain", async (DbBody b, CancellationToken ct) =>
        {
            var db = TxLimits.MicGainDb(b.Db);
            return Ok(await Bridge().SetMicGainAsync(db, ct).ConfigureAwait(false),
                new { micGainDb = db });
        });

        endpoints.MapPost("leveler", async (DbBody b, CancellationToken ct) =>
        {
            var db = TxLimits.LevelerDb(b.Db);
            return Ok(await Bridge().SetLevelerAsync(db, ct).ConfigureAwait(false),
                new { levelerMaxGainDb = db });
        });

        endpoints.MapPost("filter", async (FilterBody b, CancellationToken ct) =>
        {
            var (lo, hi) = TxLimits.Filter(b.LowHz, b.HighHz);
            return Ok(await Bridge().SetFilterAsync(lo, hi, ct).ConfigureAwait(false),
                new { lowHz = lo, highHz = hi });
        });

        endpoints.MapPost("timeout", async (SecondsBody b, CancellationToken ct) =>
        {
            var seconds = TxLimits.TimeoutSeconds(b.Seconds);
            return Ok(await Bridge().SetTimeoutAsync(seconds, ct).ConfigureAwait(false),
                new { timeoutSeconds = seconds });
        });
    }

    private TxBridge Bridge() =>
        _tx ?? throw new InvalidOperationException("simpletx: not initialised");

    /// <summary>The engine refused or was unreachable — say so rather than
    /// returning 200 and letting the panel show a setting that never took.</summary>
    private static IResult Ok(bool accepted, object body) =>
        accepted ? Results.Ok(body) : Results.StatusCode(StatusCodes.Status502BadGateway);

    // Request bodies. Records rather than anonymous types so the minimal API
    // model binder has something to bind to.
    internal sealed record OnBody(bool On);
    internal sealed record PercentBody(int Percent);
    internal sealed record DbBody(double Db);
    internal sealed record SecondsBody(int Seconds);
    internal sealed record FilterBody(int LowHz, int HighHz);
}
