// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// One bridge, over the engine's HTTP API. This replaces the pair of
// contract-based bridges: IPluginContext.Radio and RadioController are never
// provided by any engine build, so a contract path is not a path.
//
// What that costs. The engine's API carries every control but no metering:
// the wire peak lives only in Protocol1Client's 1 Hz p1.tx.rate log line, and
// forward power, SWR, mic level and the S-meter reach the product over the
// binary /ws StreamingHub rather than any route. So the meters read blank and
// the panel says so, rather than drawing something plausible.
//
// The one diagnosis that survives is the one that mattered: drive at zero
// while keyed cannot transmit, and /api/state reports drivePct.

using System.Text.Json;

namespace Zeus.Plugin.Simpletx;

/// <summary>Everything the panel needs, read and written through the engine.</summary>
public sealed class TxBridge(EngineClient engine)
{
    /// <summary>What one poll of the engine yielded.</summary>
    public sealed record Reading(
        bool Available,
        bool Keyed,
        bool Tuning,
        long FrequencyHz,
        string Mode,
        int DrivePercent,
        int DriveMaxPercent,
        int TunePercent,
        double MicGainDb,
        double LevelerMaxGainDb,
        string TxAudioSource,
        int TxFilterLowHz,
        int TxFilterHighHz,
        int TimeoutSeconds);

    public static readonly Reading Unavailable =
        new(false, false, false, 0, "", 0, 100, 0, 0, 0, "", 0, 0, 0);

    public bool EngineReachable => engine.Reachable;

    /// <summary>
    /// Metering is not reachable over the engine's HTTP API. Kept as a named
    /// constant rather than a silent null so the reason travels with the code.
    /// </summary>
    public const bool MeteringAvailable = false;

    public async Task<Reading> ReadAsync(CancellationToken ct)
    {
        var state = await engine.GetStateAsync(ct).ConfigureAwait(false);
        if (state is not { } s) return Unavailable;

        var connected = Str(s, "status").Equals("Connected", StringComparison.OrdinalIgnoreCase);
        if (!connected) return Unavailable;

        return new Reading(
            Available: true,
            Keyed: Bool(s, "moxOn") || Bool(s, "mox"),
            Tuning: Bool(s, "tunOn") || Bool(s, "tuning"),
            FrequencyHz: Long(s, "radioLoHz"),
            Mode: Str(s, "mode"),
            DrivePercent: Int(s, "drivePct"),
            DriveMaxPercent: Int(s, "driveMaxPct", 100),
            TunePercent: Int(s, "tunePct"),
            MicGainDb: Dbl(s, "micGainDb"),
            LevelerMaxGainDb: Dbl(s, "levelerMaxGainDb"),
            TxAudioSource: Str(s, "txAudioSource"),
            TxFilterLowHz: Int(s, "txFilterLowHz"),
            TxFilterHighHz: Int(s, "txFilterHighHz"),
            TimeoutSeconds: Int(s, "txTimeoutSec", 120));
    }

    public Task<bool> SetMoxAsync(bool on, CancellationToken ct) => engine.SetMoxAsync(on, ct);
    public Task<bool> SetTuneAsync(bool on, CancellationToken ct) => engine.SetTuneAsync(on, ct);
    public Task<bool> SetDriveAsync(int pct, CancellationToken ct) => engine.SetDriveAsync(pct, ct);
    public Task<bool> SetDriveMaxAsync(int pct, CancellationToken ct) => engine.SetDriveMaxAsync(pct, ct);
    public Task<bool> SetTuneDriveAsync(int pct, CancellationToken ct) => engine.SetTuneDriveAsync(pct, ct);
    public Task<bool> SetMicGainAsync(double db, CancellationToken ct) => engine.SetMicGainAsync(db, ct);
    public Task<bool> SetLevelerAsync(double db, CancellationToken ct) => engine.SetLevelerAsync(db, ct);
    public Task<bool> SetFilterAsync(int lo, int hi, CancellationToken ct) => engine.SetFilterAsync(lo, hi, ct);
    public Task<bool> SetTimeoutAsync(int s, CancellationToken ct) => engine.SetTimeoutAsync(s, ct);

    // The engine's state document is large and its shape is not ours to
    // depend on, so every read is by name with a fallback.
    private static bool Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement o, string name, int fallback = 0) =>
        o.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : fallback;

    private static long Long(JsonElement o, string name, long fallback = 0) =>
        o.TryGetProperty(name, out var v) && v.TryGetInt64(out var l) ? l : fallback;

    private static double Dbl(JsonElement o, string name, double fallback = 0) =>
        o.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : fallback;

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
