// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugin.Wavelog.Sync;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>
/// The distinction that matters: a configuration error must not be retried
/// forever. A 401 every thirty seconds for a week hides a wrong API key behind
/// an infinite queue, and the operator sees a backlog rather than a cause.
/// </summary>
public class RetryPolicyTests
{
    private static readonly RetryPolicy Policy = RetryPolicy.Default;

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void Authorisation_failures_dead_letter_on_the_first_attempt(int status)
    {
        var d = Policy.Decide(WavelogOutcome.HttpStatus(status), attempt: 1);
        Assert.Equal(RetryAction.DeadLetter, d.Action);
        Assert.Contains("key", d.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_bad_request_dead_letters_and_keeps_the_body_for_the_operator()
    {
        var d = Policy.Decide(WavelogOutcome.HttpStatus(400, "station_profile_id missing"), 1);
        Assert.Equal(RetryAction.DeadLetter, d.Action);
        Assert.Contains("station_profile_id missing", d.Reason);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public void Server_failures_are_transient(int status)
        => Assert.Equal(RetryAction.Retry, Policy.Decide(WavelogOutcome.HttpStatus(status), 1).Action);

    [Fact]
    public void A_timeout_is_transient()
        => Assert.Equal(RetryAction.Retry, Policy.Decide(WavelogOutcome.Timeout(), 1).Action);

    [Fact]
    public void A_transport_failure_is_transient()
        => Assert.Equal(RetryAction.Retry, Policy.Decide(WavelogOutcome.NetworkError("no route"), 1).Action);

    [Fact]
    public void A_reply_that_is_not_json_is_transient_not_success()
    {
        // A proxy error page returns 200 with HTML. Treating that as success
        // would silently drop the QSO.
        var d = Policy.Decide(WavelogOutcome.MalformedReply("<html>502</html>"), 1);
        Assert.Equal(RetryAction.Retry, d.Action);
    }

    [Fact]
    public void Success_is_success()
        => Assert.Equal(RetryAction.Done, Policy.Decide(WavelogOutcome.Success(), 1).Action);

    [Fact]
    public void Backoff_grows_with_the_attempt_and_is_capped()
    {
        var first = Policy.Decide(WavelogOutcome.Timeout(), 1).RetryAfter;
        var later = Policy.Decide(WavelogOutcome.Timeout(), 5).RetryAfter;
        var far = Policy.Decide(WavelogOutcome.Timeout(), 50).RetryAfter;

        Assert.True(later > first, "backoff should grow");
        Assert.True(far <= Policy.MaxBackoff, "backoff should be capped");
    }

    [Fact]
    public void A_transient_failure_dead_letters_once_it_has_run_out_of_attempts()
    {
        var d = Policy.Decide(WavelogOutcome.Timeout(), Policy.MaxAttempts + 1);
        Assert.Equal(RetryAction.DeadLetter, d.Action);
    }
}
