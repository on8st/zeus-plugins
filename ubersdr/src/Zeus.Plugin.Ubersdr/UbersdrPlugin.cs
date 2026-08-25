// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugin.Ubersdr.Backend;
using Zeus.Plugin.Ubersdr.Domain;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Ubersdr;

/// <summary>
/// A wall of public UberSDR receivers listening to the operator's transmission.
///
/// <para>Phase 1: choose receivers and show a live signal-to-noise figure from
/// each. Audio recording and playback come next; the picking and the metering
/// are the half that everything else is built on.</para>
///
/// <para><b>The plugin does not stream anything.</b> Audio sockets are opened by
/// the panel, because <c>IPluginContext.Playback</c> is null on every host
/// tested and there is nowhere for a backend to put the samples. The backend's
/// job is the directory, the radio state and the settings — the parts a browser
/// cannot do politely or at all.</para>
/// </summary>
public sealed class UbersdrPlugin : IZeusPlugin, IBackendPlugin
{
    private const string ConfigKey = "ubersdr.config";

    private HttpClient? _http;
    private InstanceDirectory? _directory;
    private EngineRadio? _radio;
    private IPluginContext? _ctx;
    private ILogger? _log;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        _log = context.Logger;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("zeus-ubersdr-plugin/0.1");
        _directory = new InstanceDirectory(_http, _log);
        _radio = new EngineRadio(_http, _log);

        var port = EngineRadio.DiscoverPort();
        if (port == 0)
            _log?.LogWarning(
                "ubersdr: could not find --port on the engine command line; radio state will be unavailable");
        else
            _log?.LogInformation("ubersdr: reading radio state from {Base}", _radio.BaseUrl);

        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _http?.Dispose();
        return Task.CompletedTask;
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // What the radio is doing. Proxied rather than fetched by the panel:
        // the panel's page is served by the product on a different origin from
        // the engine, so a relative fetch would 404 and an absolute one needs
        // the engine's port — which the plugin already knows, being inside it.
        endpoints.MapGet("radio", async (CancellationToken ct) =>
        {
            var r = await _radio!.ReadAsync(ct).ConfigureAwait(false);
            if (r is not { } s) return Results.Ok(new { available = false });

            return Results.Ok(new
            {
                available = true,
                vfoHz = s.VfoHz,
                s.Mode,
                splitEnabled = s.SplitEnabled,
                splitTxHz = s.SplitTxHz,
                moxOn = s.MoxOn,
                // What the wall should point at, worked out here so the panel
                // cannot get the split rule wrong.
                transmitHz = s.TransmitHz,
                band = Band.FromHz(s.TransmitHz),
            });
        });

        // Keying only, polled at 10 Hz by the panel. One upstream call, so a
        // transmission is bracketed to about a tenth of a second without making
        // twenty engine requests a second for a boolean.
        endpoints.MapGet("ptt", async (CancellationToken ct) =>
        {
            var keyed = await _radio!.ReadMoxAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { available = keyed is not null, keyed = keyed ?? false });
        });

        // Receivers worth offering for the band the operator is on.
        endpoints.MapGet("receivers", async (int? count, CancellationToken ct) =>
        {
            var all = await _directory!.GetAsync(ct).ConfigureAwait(false);
            var candidates = ReceiverSelection.Candidates(all);
            var wall = ReceiverSelection.SpreadByBearing(candidates, count ?? 6);

            return Results.Ok(new
            {
                fetchedUtc = _directory.FetchedUtc,
                total = all.Count,
                // Named so the panel can explain an absence rather than just
                // showing a shorter list.
                excludedNoAntenna = all.Count(i => i.IsOnline && !i.AntennaConnected),
                excludedFull = all.Count(i => i.CanMeter && !i.HasCapacity),
                offline = all.Count(i => !i.IsOnline),
                suggested = wall.Select(Dto),
                candidates = candidates.Take(60).Select(Dto),
            });
        });

        // Admission control, done here rather than in the panel.
        //
        // Not because the panel could not: the instances reflect the requesting
        // origin in Access-Control-Allow-Origin and would allow it. It lives
        // here so that the courtesy rules are in one place — one directory fetch
        // shared by the whole panel, and a refusal that is honoured rather than
        // retried in a render loop. The session id is a client-generated UUID
        // and the instance ties it to the requesting IP, which is the same
        // machine either way.
        endpoints.MapPost("connect", async (ConnectRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Host))
                return Results.BadRequest(new { error = "host is required" });

            var all = await _directory!.GetAsync(ct).ConfigureAwait(false);
            var instance = all.FirstOrDefault(i =>
                string.Equals(i.Host, req.Host, StringComparison.OrdinalIgnoreCase));
            if (instance is null)
                return Results.BadRequest(new { error = $"unknown instance '{req.Host}'" });
            if (!instance.CanMeter)
                return Results.BadRequest(new
                {
                    error = instance.IsOnline
                        ? "that receiver has no antenna connected, so it cannot report a signal level"
                        : "that receiver is offline",
                });

            var session = Guid.NewGuid().ToString();
            try
            {
                using var reply = await _http!.PostAsJsonAsync(
                    $"{instance.BaseUrl}/connection", new { user_session_id = session }, ct)
                    .ConfigureAwait(false);
                var body = await reply.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!reply.IsSuccessStatusCode)
                {
                    // A refusal is the instance saying it is full or unwilling.
                    // Honour it; do not retry.
                    _log?.LogInformation("ubersdr: {Host} refused a connection ({Status})",
                        instance.Host, (int)reply.StatusCode);
                    return Results.Json(new { error = "the receiver refused the connection", detail = Trim(body) },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                return Results.Ok(new
                {
                    sessionId = session,
                    wsBase = instance.WebSocketBase,
                    // Version 2 is the only one that streams audio: version 3
                    // connects, sends a status message, and then nothing.
                    version = 2,
                    admission = Trim(body),
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.Json(new { error = "could not reach the receiver", detail = ex.Message },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
        });

        endpoints.MapGet("status", async (CancellationToken ct) =>
        {
            var r = await _radio!.ReadAsync(ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                enginePort = EngineRadio.DiscoverPort(),
                radioAvailable = r is not null,
                directoryCount = _directory!.Count,
                directoryFetchedUtc = _directory.FetchedUtc,
            });
        });
    }

    /// <summary>Body of a connect request from the panel.</summary>
    public sealed record ConnectRequest(string Host);

    private static string Trim(string s) =>
        s.Length <= 300 ? s : s[..300] + "…";

    private static object Dto(UberSdrInstance i) => new
    {
        i.Id,
        i.Callsign,
        i.Name,
        i.Location,
        i.Host,
        wsBase = i.WebSocketBase,
        baseUrl = i.BaseUrl,
        distanceKm = double.IsNaN(i.DistanceKm) ? (double?)null : Math.Round(i.DistanceKm, 1),
        bearingDegrees = double.IsNaN(i.BearingDegrees) ? (double?)null : Math.Round(i.BearingDegrees),
        i.AvailableClients,
        i.MaxClients,
    };
}
