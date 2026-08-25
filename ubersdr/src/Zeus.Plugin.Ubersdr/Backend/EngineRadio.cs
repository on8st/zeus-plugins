// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Zeus.Plugin.Ubersdr.Backend;

/// <summary>What the operator's radio is doing, as far as the monitor cares.</summary>
public readonly record struct RadioSnapshot(
    long VfoHz, string Mode, bool SplitEnabled, long SplitTxHz, bool MoxOn)
{
    /// <summary>
    /// The frequency to point receivers at.
    ///
    /// <para>Under split the transmit frequency is <em>not</em> the VFO. Getting
    /// this wrong monitors an empty frequency and reports that nobody hears the
    /// operator — silent, plausible, and completely wrong.</para>
    /// </summary>
    public long TransmitHz => SplitEnabled && SplitTxHz > 0 ? SplitTxHz : VfoHz;
}

/// <summary>
/// Reads radio state from the engine's own HTTP API.
///
/// <para>Not from <c>IPluginContext</c>: a runtime probe against both the
/// source-built engine and shipped Zeus Link found <c>Radio</c>,
/// <c>RadioController</c> and <c>Playback</c> all null — the contracts declare
/// them and nothing provides them. The engine's own API carries more anyway,
/// including <c>splitEnabled</c> and <c>splitTxHz</c> as separate fields.</para>
///
/// <para>The plugin runs inside the engine process, so the port is on its own
/// command line. That is a little grubby, but it is exact, and it is better than
/// asking the operator to configure a port they never chose.</para>
/// </summary>
public sealed class EngineRadio(HttpClient http, ILogger? log = null)
{
    private readonly string _base = $"http://127.0.0.1:{DiscoverPort()}";

    public string BaseUrl => _base;

    public static int DiscoverPort()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--port" && int.TryParse(args[i + 1], out var p)) return p;
        return 0;
    }

    /// <summary>
    /// Keying state only, in one upstream call.
    ///
    /// <para>The monitor polls this at 10 Hz to catch the start and end of a
    /// transmission — the engine has no state stream, its <c>/ws</c> being
    /// binary telemetry only. Reading the full snapshot at that rate would make
    /// two engine calls twenty times a second for one boolean, so keying gets
    /// its own route and frequency is read slowly alongside it.</para>
    /// </summary>
    public async Task<bool?> ReadMoxAsync(CancellationToken ct)
    {
        try
        {
            var ptt = JsonNode.Parse(
                await http.GetStringAsync($"{_base}/api/radio/ptt-status", ct).ConfigureAwait(false));
            // tun and two-tone key the transmitter as surely as MOX does, and a
            // monitor that ignored them would miss exactly the carrier an
            // operator sends to compare antennas.
            return (Bool(ptt, "moxOn") ?? false)
                || (Bool(ptt, "tunOn") ?? false)
                || (Bool(ptt, "twoToneOn") ?? false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or System.Text.Json.JsonException)
        {
            log?.LogDebug(ex, "ubersdr: could not read keying state");
            return null;
        }
    }

    public async Task<RadioSnapshot?> ReadAsync(CancellationToken ct)
    {
        try
        {
            var state = JsonNode.Parse(
                await http.GetStringAsync($"{_base}/api/state", ct).ConfigureAwait(false));
            var ptt = JsonNode.Parse(
                await http.GetStringAsync($"{_base}/api/radio/ptt-status", ct).ConfigureAwait(false));

            return new RadioSnapshot(
                VfoHz: (long)(Num(state, "vfoHz") ?? 0),
                Mode: state?["mode"]?.GetValue<string>() ?? "",
                SplitEnabled: Bool(state, "splitEnabled") ?? false,
                SplitTxHz: (long)(Num(state, "splitTxHz") ?? 0),
                MoxOn: Bool(ptt, "moxOn") ?? false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or System.Text.Json.JsonException)
        {
            log?.LogDebug(ex, "ubersdr: could not read engine radio state");
            return null;
        }
    }

    private static double? Num(JsonNode? n, string k) =>
        n?[k]?.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? n[k]!.GetValue<double>() : null;

    private static bool? Bool(JsonNode? n, string k) => n?[k]?.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        _ => null,
    };
}
