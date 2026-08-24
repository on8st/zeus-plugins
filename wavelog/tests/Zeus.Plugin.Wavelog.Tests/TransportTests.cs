// SPDX-License-Identifier: GPL-2.0-or-later
using FakeWavelog;
using Zeus.Plugin.Wavelog.Sync;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>
/// Driven against a fake Wavelog on loopback — the real <see cref="HttpClient"/>
/// path, a real socket, and no live logbook anywhere near it. The fake
/// implements the semantics read out of Wavelog's own source, including the
/// duplicate key and the primary-key cursor.
/// </summary>
public sealed class TransportTests : IDisposable
{
    private readonly FakeWavelogServer _wavelog = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public TransportTests() => _wavelog.Start();
    public void Dispose() { _wavelog.Dispose(); _http.Dispose(); }

    private WavelogConfig Config => new()
    {
        BaseUrl = _wavelog.BaseUrl,
        ApiKey = _wavelog.ApiKey,
        StationProfileId = 1,
        PullStationIds = [1],
    };

    private HttpWavelogTransport Transport => new(_http);

    private const string OneQso =
        "<CALL:6>DL1ABC<QSO_DATE:8>20260824<TIME_ON:6>090000<BAND:3>20m<MODE:3>SSB<EOR>";

    // ---- push ---------------------------------------------------------------

    [Fact]
    public async Task A_qso_is_accepted()
    {
        var outcome = await Transport.PostQsoAsync(Config, OneQso, default);
        Assert.True(outcome.IsSuccess);
        Assert.Single(_wavelog.Rows);
    }

    [Fact]
    public async Task Sending_the_same_qso_twice_leaves_one_copy()
    {
        // This is what makes at-least-once delivery safe: a retry after an
        // ambiguous timeout cannot duplicate the contact.
        await Transport.PostQsoAsync(Config, OneQso, default);
        var second = await Transport.PostQsoAsync(Config, OneQso, default);

        Assert.True(second.IsSuccess);
        Assert.Single(_wavelog.Rows);
        Assert.Equal(2, _wavelog.QsoPostCount);
    }

    [Fact]
    public async Task A_wrong_key_is_reported_as_an_authorisation_failure()
    {
        var outcome = await Transport.PostQsoAsync(Config with { ApiKey = "nope" }, OneQso, default);
        Assert.Equal(401, outcome.Status);
        Assert.Equal(RetryAction.DeadLetter, RetryPolicy.Default.Decide(outcome, 1).Action);
    }

    [Fact]
    public async Task A_server_error_is_transient()
    {
        _wavelog.ForceStatus = 500;
        var outcome = await Transport.PostQsoAsync(Config, OneQso, default);
        Assert.Equal(RetryAction.Retry, RetryPolicy.Default.Decide(outcome, 1).Action);
    }

    [Fact]
    public async Task A_200_that_is_not_json_is_a_failure_not_a_success()
    {
        // A proxy error page in front of Wavelog. Reading this as success would
        // silently drop the QSO.
        _wavelog.ForceBody = "<html><body>502 Bad Gateway</body></html>";
        var outcome = await Transport.PostQsoAsync(Config, OneQso, default);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(OutcomeKind.MalformedReply, outcome.Kind);
    }

    [Fact]
    public async Task A_slow_instance_times_out_rather_than_hanging()
    {
        _wavelog.Delay = TimeSpan.FromSeconds(3);
        using var impatient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(300) };

        var outcome = await new HttpWavelogTransport(impatient).PostQsoAsync(Config, OneQso, default);

        Assert.Equal(OutcomeKind.Timeout, outcome.Kind);
        Assert.Equal(RetryAction.Retry, RetryPolicy.Default.Decide(outcome, 1).Action);
    }

    [Fact]
    public async Task An_unreachable_instance_is_a_network_error()
    {
        var outcome = await Transport.PostQsoAsync(
            Config with { BaseUrl = "http://127.0.0.1:9" }, OneQso, default);
        Assert.Equal(OutcomeKind.NetworkError, outcome.Kind);
    }

    // ---- pull ---------------------------------------------------------------

    [Fact]
    public async Task Nothing_new_returns_the_cursor_unchanged()
    {
        var (outcome, result) = await Transport.GetContactsAsync(Config, 0, 100, null, default);
        Assert.True(outcome.IsSuccess);
        Assert.Equal(0, result!.Count);
        Assert.Equal(0, result.LastFetchedId);
    }

    [Fact]
    public async Task The_pull_sees_a_qso_logged_by_another_app()
    {
        // The query behind get_contacts_adif filters on station and primary key
        // only — never on origin.
        _wavelog.AddQsoFromAnotherApp("G4XYZ", new DateTime(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc), "40m", "CW");

        var (outcome, result) = await Transport.GetContactsAsync(Config, 0, 100, null, default);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(1, result!.Count);
        Assert.Contains("G4XYZ", result.Adif);
    }

    [Fact]
    public async Task The_cursor_advances_and_does_not_repeat_itself()
    {
        _wavelog.AddQsoFromAnotherApp("A", DateTime.UtcNow, "20m", "SSB");
        var (_, first) = await Transport.GetContactsAsync(Config, 0, 100, null, default);

        var (_, second) = await Transport.GetContactsAsync(Config, first!.LastFetchedId, 100, null, default);

        Assert.Equal(0, second!.Count);
        Assert.Equal(first.LastFetchedId, second.LastFetchedId);
    }

    [Fact]
    public async Task A_qso_in_an_unselected_profile_is_invisible()
    {
        // Not late — invisible. This is the silent gap the config must prevent
        // by letting the operator pick every profile.
        _wavelog.AddQsoFromAnotherApp("PORTABLE", DateTime.UtcNow, "20m", "SSB", stationId: 2);

        var (_, onlyHome) = await Transport.GetContactsAsync(Config, 0, 100, null, default);
        Assert.Equal(0, onlyHome!.Count);

        var (_, both) = await Transport.GetContactsAsync(
            Config with { PullStationIds = [1, 2] }, 0, 100, null, default);
        Assert.Equal(1, both!.Count);
    }

    [Fact]
    public async Task A_confirmation_is_invisible_to_the_cursor_but_found_by_the_filtered_sweep()
    {
        // The reason there are two loops: confirming a QSO is an UPDATE, and an
        // update does not change the primary key.
        _wavelog.AddQsoFromAnotherApp("DL1ABC", DateTime.UtcNow, "20m", "SSB");
        var (_, first) = await Transport.GetContactsAsync(Config, 0, 100, null, default);

        _wavelog.ConfirmOnLotw("DL1ABC");

        var (_, incremental) = await Transport.GetContactsAsync(Config, first!.LastFetchedId, 100, null, default);
        Assert.Equal(0, incremental!.Count);

        var (_, sweep) = await Transport.GetContactsAsync(Config, 0, 100, ["lotw"], default);
        Assert.Equal(1, sweep!.Count);
    }

    // ---- profiles and radio -------------------------------------------------

    [Fact]
    public async Task Station_profiles_can_be_listed()
    {
        var (outcome, profiles) = await Transport.GetStationInfoAsync(Config, default);
        Assert.True(outcome.IsSuccess);
        Assert.Equal(2, profiles!.Count);
        Assert.Contains(profiles, p => p.Name == "Portable");
    }

    [Fact]
    public async Task Radio_state_is_posted_with_frequency_in_hertz()
    {
        var outcome = await Transport.PostRadioAsync(
            Config, new RadioState("Zeus", 14_074_000, "USB", 25), default);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(1, _wavelog.RadioPostCount);
        Assert.Equal("14074000", _wavelog.LastRadioPayload!["frequency"]!.GetValue<string>());
        Assert.Equal("USB", _wavelog.LastRadioPayload["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_read_only_key_is_refused_permanently_for_radio()
    {
        _wavelog.KeyCanWrite = false;
        var outcome = await Transport.PostRadioAsync(Config, new RadioState("Zeus", 14_074_000, "USB", null), default);

        Assert.Equal(403, outcome.Status);
        Assert.Equal(RetryAction.DeadLetter, RetryPolicy.Default.Decide(outcome, 1).Action);
    }
}
