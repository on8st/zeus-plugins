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

    public string Endpoint(string name) => $"{BaseUrl.TrimEnd('/')}/index.php/api/{name}";
}
