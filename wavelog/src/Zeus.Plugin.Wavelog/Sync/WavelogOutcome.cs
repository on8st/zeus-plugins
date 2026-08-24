// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
namespace Zeus.Plugin.Wavelog.Sync;

public enum OutcomeKind { Success, HttpStatus, Timeout, NetworkError, MalformedReply }

/// <summary>
/// What one attempt against Wavelog produced. Deliberately a value: the retry
/// decision is a pure function of this plus the attempt number, so it can be
/// tested without a network, a clock or a queue.
/// </summary>
public readonly record struct WavelogOutcome(OutcomeKind Kind, int Status, string? Detail)
{
    public static WavelogOutcome Success() => new(OutcomeKind.Success, 200, null);
    public static WavelogOutcome HttpStatus(int status, string? body = null) => new(OutcomeKind.HttpStatus, status, body);
    public static WavelogOutcome Timeout() => new(OutcomeKind.Timeout, 0, "timed out");
    public static WavelogOutcome NetworkError(string detail) => new(OutcomeKind.NetworkError, 0, detail);
    public static WavelogOutcome MalformedReply(string body) => new(OutcomeKind.MalformedReply, 200, body);

    public bool IsSuccess => Kind == OutcomeKind.Success;
}
