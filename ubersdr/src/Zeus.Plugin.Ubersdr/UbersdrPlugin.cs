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

    /// <summary>
    /// What the operator has chosen. Persisted by the host, per plugin.
    ///
    /// <para><c>HomeGrid</c> exists because the directory's <c>distance</c> and
    /// <c>bearing</c> are relative to <em>whoever called the API</em>, geolocated
    /// by IP — measured 28 km out on this station, and a VPN would put it in
    /// another country. Everything else here is the operator's selection, which
    /// is theirs to keep rather than to re-make every session.</para>
    /// </summary>
    public sealed class StoredConfig
    {
        public string HomeGrid { get; set; } = "";
        public List<string> SelectedHosts { get; set; } = [];
        public string Preset { get; set; } = "spread";
        public int Count { get; set; } = 6;
        public double? MinDistanceKm { get; set; }
        public double? MaxDistanceKm { get; set; }
        public double? BearingFrom { get; set; }
        public double? BearingTo { get; set; }
        public int MinFreeSlots { get; set; } = 1;
        public List<string> ExcludeHosts { get; set; } = [];
    }

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

        // Receivers worth offering, with the operator's constraints applied.
        endpoints.MapGet("receivers", async (
            int? count, string? preset,
            double? minKm, double? maxKm, double? bearingFrom, double? bearingTo,
            int? minFree, string? exclude,
            CancellationToken ct) =>
        {
            var limits = new ReceiverConstraints
            {
                MinDistanceKm = minKm,
                MaxDistanceKm = maxKm,
                BearingFrom = bearingFrom,
                BearingTo = bearingTo,
                MinFreeSlots = minFree ?? 1,
                ExcludeHosts = string.IsNullOrWhiteSpace(exclude)
                    ? []
                    : exclude.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            };

            var all = await _directory!.GetAsync(ct).ConfigureAwait(false);
            var candidates = ReceiverSelection.Candidates(all, limits);
            var n = count ?? 6;

            var suggested = (preset ?? "spread").ToLowerInvariant() switch
            {
                "nearest" => candidates.Take(n).ToList(),
                "furthest" => ReceiverSelection.Furthest(candidates, n).ToList(),
                _ => ReceiverSelection.SpreadByBearing(candidates, n).ToList(),
            };

            return Results.Ok(new
            {
                fetchedUtc = _directory.FetchedUtc,
                total = all.Count,
                // Named so the panel can explain an absence rather than just
                // showing a shorter list.
                excludedNoAntenna = all.Count(i => i.IsOnline && !i.AntennaConnected),
                excludedFull = all.Count(i => i.CanMeter && !i.HasCapacity),
                offline = all.Count(i => !i.IsOnline),
                excludedByLimits = all.Count(i => i.CanMeter && i.HasCapacity && !limits.Allows(i)),
                suggested = suggested.Select(Dto),
                // Everything that passes the filters, so the panel can offer a
                // manual pick rather than only the preset's choice.
                candidates = candidates.Select(Dto),
            });
        });

        // Named receivers, for a manual selection the operator has saved.
        endpoints.MapGet("receivers/by-host", async (string hosts, CancellationToken ct) =>
        {
            var wanted = hosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var all = await _directory!.GetAsync(ct).ConfigureAwait(false);

            // Preserve the operator's order, and report what has gone away
            // rather than silently returning a shorter list.
            var found = wanted
                .Select(hostName => all.FirstOrDefault(i =>
                    string.Equals(i.Host, hostName, StringComparison.OrdinalIgnoreCase)))
                .Where(i => i is not null).Select(i => i!).ToList();

            return Results.Ok(new
            {
                receivers = found.Select(Dto),
                missing = wanted.Where(hostName =>
                    !found.Any(i => string.Equals(i.Host, hostName, StringComparison.OrdinalIgnoreCase))),
                unusable = found.Where(i => !i.CanMeter || !i.HasCapacity).Select(i => i.Host),
            });
        });

        endpoints.MapGet("config", async (CancellationToken ct) =>
            Results.Ok(await LoadConfigAsync(ct).ConfigureAwait(false)));

        endpoints.MapPost("config", async (StoredConfig body, CancellationToken ct) =>
        {
            await _ctx!.Settings.SetAsync(ConfigKey, body, ct).ConfigureAwait(false);
            return Results.Ok(new { ok = true });
        });

        // Where the engine's own websocket is. The panel cannot work this out:
        // its page is served by the product on another origin and the engine's
        // port is assigned at launch. The plugin runs inside the engine, so it
        // simply knows.
        endpoints.MapGet("engine", () =>
        {
            var port = EngineRadio.DiscoverPort();
            return port == 0
                ? Results.Ok(new { available = false })
                : Results.Ok(new { available = true, port, wsUrl = $"ws://127.0.0.1:{port}/ws" });
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

    private async Task<StoredConfig> LoadConfigAsync(CancellationToken ct)
    {
        try
        {
            return await _ctx!.Settings.GetAsync<StoredConfig>(ConfigKey, ct).ConfigureAwait(false)
                   ?? new StoredConfig();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "ubersdr: could not read settings; starting with defaults");
            return new StoredConfig();
        }
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
        lat = i.HasPosition ? Math.Round(i.Latitude, 4) : (double?)null,
        lon = i.HasPosition ? Math.Round(i.Longitude, 4) : (double?)null,
        i.Country,
        i.AvailableClients,
        i.MaxClients,
    };
}
