// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// The engine's own HTTP API, called from inside the engine process.
//
// Not IPluginContext.Radio / RadioController. Those are declared by the
// contracts and never provided: PluginManager resolves them with
// _services.GetService<IRadioStateReader>(), nothing registers one, and a
// runtime probe on a connected radio confirmed both come back null. ubersdr
// reached the same conclusion and does the same thing — see its EngineRadio.
//
// Every route and payload below was read from the engine source rather than
// guessed: TxControlEndpoints.cs, TxTimingAndTestEndpoints.cs, FilterEndpoints.cs.

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Zeus.Plugin.Simpletx;

/// <summary>Typed access to the engine's transmit routes.</summary>
public sealed class EngineClient(HttpClient http, ILogger? log = null)
{
    private readonly string _base = $"http://127.0.0.1:{DiscoverPort()}";

    public string BaseUrl => _base;
    public bool Reachable => DiscoverPort() != 0;

    /// <summary>
    /// The engine's port, taken from its own command line.
    /// <para>Grubby but exact: the plugin runs inside the engine process, the
    /// port is assigned at launch, and asking the operator to configure a port
    /// they never chose would be worse.</para>
    /// </summary>
    public static int DiscoverPort()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--port" && int.TryParse(args[i + 1], out var port)) return port;
        }

        return 0;
    }

    /// <summary>The engine's whole state document, or null if it cannot be read.</summary>
    public async Task<JsonElement?> GetStateAsync(CancellationToken ct)
    {
        try
        {
            var doc = await http.GetFromJsonAsync<JsonElement>($"{_base}/api/state", ct)
                .ConfigureAwait(false);
            return doc;
        }
        catch (Exception ex)
        {
            log?.LogDebug(ex, "simpletx: /api/state unavailable");
            return null;
        }
    }

    private async Task<bool> PostAsync(string route, object body, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"{_base}{route}", body, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                log?.LogWarning("simpletx: {Route} returned {Status}", route, (int)response.StatusCode);
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "simpletx: {Route} failed", route);
            return false;
        }
    }

    // MoxSetRequest(bool On)
    public Task<bool> SetMoxAsync(bool on, CancellationToken ct) =>
        PostAsync("/api/tx/mox", new { on }, ct);

    // TunSetRequest(bool On) — the tune carrier, gated by a coordinator that
    // can refuse, which is why this reports success rather than returning void.
    public Task<bool> SetTuneAsync(bool on, CancellationToken ct) =>
        PostAsync("/api/tx/tun", new { on }, ct);

    // DriveSetRequest(int Percent)
    public Task<bool> SetDriveAsync(int percent, CancellationToken ct) =>
        PostAsync("/api/tx/drive", new { percent }, ct);

    // DriveMaxSetRequest(int Percent)
    public Task<bool> SetDriveMaxAsync(int percent, CancellationToken ct) =>
        PostAsync("/api/tx/drive-max", new { percent }, ct);

    // TuneDriveSetRequest(int Percent)
    public Task<bool> SetTuneDriveAsync(int percent, CancellationToken ct) =>
        PostAsync("/api/tx/tune-drive", new { percent }, ct);

    // MicGainSetRequest(int Db) — an int on the wire, so the panel's dB is
    // rounded here rather than silently truncated by the binder.
    public Task<bool> SetMicGainAsync(double db, CancellationToken ct) =>
        PostAsync("/api/mic-gain", new { db = (int)Math.Round(db) }, ct);

    // LevelerMaxGainSetRequest(double Gain)
    public Task<bool> SetLevelerAsync(double gain, CancellationToken ct) =>
        PostAsync("/api/tx/leveler-max-gain", new { gain }, ct);

    // TxFilterSetRequest(int LowHz, int HighHz)
    public Task<bool> SetFilterAsync(int lowHz, int highHz, CancellationToken ct) =>
        PostAsync("/api/tx-filter", new { lowHz, highHz }, ct);

    // TxTimeoutSetRequest(int Seconds)
    public Task<bool> SetTimeoutAsync(int seconds, CancellationToken ct) =>
        PostAsync("/api/tx/timeout", new { seconds }, ct);
}
