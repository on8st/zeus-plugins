// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
namespace Zeus.Plugin.Wavelog.Sync;

/// <summary>
/// What the operator configures. The key is held here in memory only — it is
/// persisted through the host's plugin settings and never returned by the
/// config endpoint.
/// </summary>
public sealed record WavelogConfig
{
    public string BaseUrl { get; init; } = "";
    public string ApiKey { get; init; } = "";

    /// <summary>The profile new QSOs are pushed to. One.</summary>
    public int StationProfileId { get; init; } = 1;

    /// <summary>
    /// The profiles pulled from. Many — a QSO imported under a profile that is
    /// not listed here is invisible to the sync permanently, not late.
    /// </summary>
    public IReadOnlyList<int> PullStationIds { get; init; } = [];

    public bool PushEnabled { get; init; } = true;
    public bool PullEnabled { get; init; } = true;
    public bool RadioEnabled { get; init; }

    public bool IsUsable => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// The locations actually pulled from.
    ///
    /// <para>An empty selection is the default, and it cannot be sent as-is:
    /// Wavelog refuses it outright with <c>"station_id" must not be empty</c>.
    /// So an empty list falls back to the location we push to, which is almost
    /// always what an operator who filled in one field and not the other
    /// meant.</para>
    ///
    /// <para>The fallback is fine; doing it <em>quietly</em> is not. Until this
    /// existed the plugin pulled from a location the status endpoint never
    /// mentioned — on a live install that meant importing the operator's real
    /// log while the panel displayed an empty list. Everything that reports
    /// configuration reports this, not the raw field.</para>
    /// </summary>
    public IReadOnlyList<int> EffectivePullStationIds =>
        PullStationIds.Count > 0 ? PullStationIds : [StationProfileId];

    /// <summary>True when the pull is running against a fallback rather than an explicit choice.</summary>
    public bool PullLocationsAreImplicit => PullEnabled && PullStationIds.Count == 0;

    public string Endpoint(string name) => $"{BaseUrl.TrimEnd('/')}/index.php/api/{name}";
}
