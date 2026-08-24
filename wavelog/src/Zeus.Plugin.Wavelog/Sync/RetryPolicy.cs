// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
namespace Zeus.Plugin.Wavelog.Sync;

public enum RetryAction { Done, Retry, DeadLetter }

public readonly record struct RetryDecision(RetryAction Action, TimeSpan RetryAfter, string Reason);

/// <summary>
/// Decides what to do after one attempt. Pure — no clock, no queue, no network.
///
/// <para>The important line here is the one that refuses to retry an
/// authorisation failure. Retrying a wrong API key every thirty seconds for a
/// week does not fix it; it hides a configuration error behind a queue that
/// only ever grows, and the operator is shown a backlog instead of a cause.</para>
/// </summary>
public sealed class RetryPolicy
{
    public static RetryPolicy Default { get; } = new();

    public int MaxAttempts { get; init; } = 8;
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(30);

    public RetryDecision Decide(WavelogOutcome outcome, int attempt)
    {
        if (outcome.IsSuccess)
            return new RetryDecision(RetryAction.Done, TimeSpan.Zero, "accepted");

        // Permanent: nothing about waiting changes the answer.
        if (outcome.Kind == OutcomeKind.HttpStatus && outcome.Status is 401 or 403)
            return new RetryDecision(RetryAction.DeadLetter, TimeSpan.Zero,
                $"rejected ({outcome.Status}) — the API key or its permissions are wrong; " +
                "retrying would only hide that");

        if (outcome.Kind == OutcomeKind.HttpStatus && outcome.Status is >= 400 and < 500)
            return new RetryDecision(RetryAction.DeadLetter, TimeSpan.Zero,
                $"rejected ({outcome.Status}): {outcome.Detail ?? "no detail given"}");

        if (attempt > MaxAttempts)
            return new RetryDecision(RetryAction.DeadLetter, TimeSpan.Zero,
                $"gave up after {MaxAttempts} attempts: {Describe(outcome)}");

        return new RetryDecision(RetryAction.Retry, Backoff(attempt), Describe(outcome));
    }

    /// <summary>Exponential, capped. No jitter: one operator, one queue.</summary>
    private TimeSpan Backoff(int attempt)
    {
        var seconds = BaseBackoff.TotalSeconds * Math.Pow(2, Math.Max(0, attempt - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    private static string Describe(WavelogOutcome o) => o.Kind switch
    {
        OutcomeKind.Timeout => "timed out",
        OutcomeKind.NetworkError => $"network error: {o.Detail}",
        OutcomeKind.MalformedReply => "reply was not the JSON Wavelog promises — treating as a failure "
                                    + "rather than a silent success",
        _ => $"HTTP {o.Status}",
    };
}
