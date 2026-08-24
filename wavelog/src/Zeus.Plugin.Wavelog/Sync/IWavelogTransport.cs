// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
namespace Zeus.Plugin.Wavelog.Sync;

public sealed record PulledQsos(int LastFetchedId, int Count, string? Adif);

public sealed record StationProfile(int Id, string Name);

public interface IWavelogTransport
{
    Task<WavelogOutcome> PostQsoAsync(WavelogConfig config, string adif, CancellationToken ct);
    Task<(WavelogOutcome Outcome, PulledQsos? Result)> GetContactsAsync(
        WavelogConfig config, int fetchFromId, int limit, IReadOnlyList<string>? qslFilter, CancellationToken ct);
    Task<(WavelogOutcome Outcome, IReadOnlyList<StationProfile>? Profiles)> GetStationInfoAsync(
        WavelogConfig config, CancellationToken ct);
    Task<WavelogOutcome> PostRadioAsync(WavelogConfig config, RadioState state, CancellationToken ct);
}

/// <summary>What /api/radio carries: the rig, where it is, and what it is doing.</summary>
public readonly record struct RadioState(
    string Radio, double? FrequencyHz, string? Mode, double? PowerW);
