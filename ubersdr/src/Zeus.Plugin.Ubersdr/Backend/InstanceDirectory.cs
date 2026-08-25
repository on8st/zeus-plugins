// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Zeus.Plugin.Ubersdr.Domain;

namespace Zeus.Plugin.Ubersdr.Backend;

/// <summary>
/// The public receiver directory, cached.
///
/// <para><c>GET https://instances.ubersdr.org/api/instances</c> is
/// unauthenticated and around 640 kB. It is somebody else's endpoint, reachable
/// rather than offered, so this errs heavily towards politeness: one fetch on
/// demand, a long cache, and the previous answer served rather than a retry when
/// it fails.</para>
/// </summary>
public sealed class InstanceDirectory(HttpClient http, ILogger? log = null)
{
    public const string DefaultUrl = "https://instances.ubersdr.org/api/instances";

    /// <summary>
    /// Deliberately long. The wall does not need minute-fresh capacity figures —
    /// admission control refuses a full receiver anyway — and the alternative is
    /// every Zeus install in the world polling a 640 kB endpoint.
    /// </summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<UberSdrInstance> _cached = [];
    private DateTime _fetchedUtc = DateTime.MinValue;

    public string Url { get; init; } = DefaultUrl;
    public DateTime FetchedUtc => _fetchedUtc;
    public int Count => _cached.Count;

    public async Task<IReadOnlyList<UberSdrInstance>> GetAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _fetchedUtc < CacheFor && _cached.Count > 0) return _cached;

        // One fetch at a time: a panel opening with several components must not
        // become several downloads.
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false))
            return _cached;
        try
        {
            if (DateTime.UtcNow - _fetchedUtc < CacheFor && _cached.Count > 0) return _cached;

            var text = await http.GetStringAsync(Url, ct).ConfigureAwait(false);
            var parsed = Parse(text);
            if (parsed.Count == 0 && _cached.Count > 0)
            {
                // An empty parse is far more likely to be a changed shape than a
                // world with no receivers in it. Keep what we had and say so.
                log?.LogWarning("ubersdr: directory returned nothing usable; keeping {Count} cached", _cached.Count);
                return _cached;
            }

            _cached = parsed;
            _fetchedUtc = DateTime.UtcNow;
            log?.LogInformation("ubersdr: directory {Count} instances, {Metering} able to meter",
                parsed.Count, parsed.Count(i => i.CanMeter));
            return _cached;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Serve stale rather than nothing: a wall that keeps working through
            // a network blip is worth more than a fresh capacity number.
            log?.LogWarning(ex, "ubersdr: directory fetch failed; serving {Count} cached", _cached.Count);
            return _cached;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Parse the directory body. Public so a test can feed it a capture.</summary>
    public static IReadOnlyList<UberSdrInstance> Parse(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return []; }

        // The endpoint returns a bare array today. Accept an envelope too rather
        // than breaking the day it grows one.
        var array = root as JsonArray ?? root?["instances"] as JsonArray;
        if (array is null) return [];

        return array.Select(UberSdrInstance.FromJson)
                    .Where(i => i is not null)
                    .Select(i => i!)
                    .ToList();
    }
}
