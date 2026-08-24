// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugin.Wavelog.Storage;
using Zeus.Plugin.Wavelog.Sync;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>
/// The property the reframe is worth having: the operator's log does not depend
/// on us.
///
/// <para>As a logbook replacement this had to be argued — the write path went
/// through the plugin, so "logging never waits on Wavelog" was a design rule
/// that had to be held. As a synchroniser it is structural: Zeus writes the
/// contact through its own plugin and we read it afterwards. There is no path
/// from the operator's keystroke into this code at all.</para>
///
/// <para>These tests hold that structurally, by giving the sync service a
/// transport that fails the test if anything reaches it, and then having the
/// native logbook do its work regardless.</para>
/// </summary>
public sealed class NeverInTheWayTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wavelog-inway-" + Guid.NewGuid().ToString("N"));
    private readonly NativeLogbook _zeus;
    private readonly ZeusLogbookDb _logbook;
    private readonly LiteDbOutbox _outbox;

    public NeverInTheWayTests()
    {
        Directory.CreateDirectory(_dir);
        _zeus = NativeLogbook.InDataDirectory(_dir);
        _logbook = ZeusLogbookDb.ForDataDirectory(_dir);
        _outbox = new LiteDbOutbox(Path.Combine(_dir, "outbox.db"));
    }

    public void Dispose()
    {
        _zeus.Dispose(); _logbook.Dispose(); _outbox.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>A transport that fails the test if anything reaches it.</summary>
    private sealed class ForbiddenTransport : IWavelogTransport
    {
        private static Exception No() =>
            new InvalidOperationException("noticing a new QSO must not touch the network");

        public Task<WavelogOutcome> PostQsoAsync(WavelogConfig c, string a, CancellationToken ct) => throw No();
        public Task<(WavelogOutcome, PulledQsos?)> GetContactsAsync(
            WavelogConfig c, int f, int l, IReadOnlyList<string>? q, CancellationToken ct) => throw No();
        public Task<(WavelogOutcome, IReadOnlyList<StationProfile>?)> GetStationInfoAsync(
            WavelogConfig c, CancellationToken ct) => throw No();
        public Task<WavelogOutcome> PostRadioAsync(WavelogConfig c, RadioState s, CancellationToken ct) => throw No();
    }

    /// <summary>A transport that is broken in the ordinary way: it throws.</summary>
    private sealed class BrokenTransport : IWavelogTransport
    {
        private static Exception Down() => new HttpRequestException("connection refused");

        public Task<WavelogOutcome> PostQsoAsync(WavelogConfig c, string a, CancellationToken ct) => throw Down();
        public Task<(WavelogOutcome, PulledQsos?)> GetContactsAsync(
            WavelogConfig c, int f, int l, IReadOnlyList<string>? q, CancellationToken ct) => throw Down();
        public Task<(WavelogOutcome, IReadOnlyList<StationProfile>?)> GetStationInfoAsync(
            WavelogConfig c, CancellationToken ct) => throw Down();
        public Task<WavelogOutcome> PostRadioAsync(WavelogConfig c, RadioState s, CancellationToken ct) => throw Down();
    }

    private static WavelogConfig Configured => new()
    {
        BaseUrl = "http://127.0.0.1:1", ApiKey = "k", StationProfileId = 1,
    };

    private WavelogSyncService Sync(IWavelogTransport transport, WavelogConfig config) =>
        new(_logbook, _outbox, transport, () => config, new MemoryCursorStore());

    // ---- the two that matter -----------------------------------------------

    [Fact]
    public void Noticing_a_new_qso_never_touches_the_network()
    {
        _zeus.Log();
        Assert.Equal(1, Sync(new ForbiddenTransport(), Configured).EnqueueNewLocalQsos());
        Assert.Equal(1, _outbox.PendingCount);
    }

    [Fact]
    public void A_qso_logged_while_wavelog_is_down_is_still_the_operators_qso()
    {
        var logged = _zeus.Log();
        Sync(new BrokenTransport(), Configured).EnqueueNewLocalQsos();

        // The contact is where the operator put it and is queued for later. The
        // outage is somebody else's problem, at some other time.
        Assert.Equal(1, _zeus.Count());
        Assert.Equal("DL1ABC", _zeus.ById(logged.Id)!.Callsign);
        Assert.Equal(1, _outbox.PendingCount);
    }

    // ---- and the corollaries -----------------------------------------------

    [Fact]
    public void An_unconfigured_plugin_queues_nothing_and_forgets_nothing()
    {
        _zeus.Log();
        Assert.Equal(0, Sync(new ForbiddenTransport(), new WavelogConfig()).EnqueueNewLocalQsos());
        Assert.Equal(0, _outbox.PendingCount);

        // Untracked, deliberately: the contact is still unseen, so the day the
        // operator pastes in a key the backlog goes up rather than being lost.
        Assert.Single(_logbook.Unseen());
        Assert.Equal(1, Sync(new ForbiddenTransport(), Configured).EnqueueNewLocalQsos());
    }

    [Fact]
    public void Push_switched_off_queues_nothing()
    {
        _zeus.Log();
        Sync(new ForbiddenTransport(), Configured with { PushEnabled = false }).EnqueueNewLocalQsos();
        Assert.Equal(0, _outbox.PendingCount);
    }

    [Fact]
    public void The_same_qso_noticed_twice_is_queued_once()
    {
        // The scan runs on a timer, so this is not a corner case, it is every
        // thirty seconds for the life of the session.
        _zeus.Log();
        var sync = Sync(new ForbiddenTransport(), Configured);

        Assert.Equal(1, sync.EnqueueNewLocalQsos());
        Assert.Equal(0, sync.EnqueueNewLocalQsos());
        Assert.Equal(1, _outbox.PendingCount);
    }

    [Fact]
    public void A_qso_that_arrived_from_wavelog_is_never_queued_back()
    {
        _logbook.InsertFromWavelog(new LogbookNewEntry("G4XYZ", null, 14.074, "20m", "USB", "59", "59"));
        Assert.Equal(0, Sync(new ForbiddenTransport(), Configured).EnqueueNewLocalQsos());
        Assert.Equal(0, _outbox.PendingCount);
    }

    [Fact]
    public void A_backlog_logged_before_the_plugin_existed_goes_up_on_first_scan()
    {
        // Installing the plugin into a station with years of contacts must not
        // quietly start from now.
        for (var i = 0; i < 25; i++)
            _zeus.Log($"CALL{i}", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i));

        Assert.Equal(25, Sync(new ForbiddenTransport(), Configured).EnqueueNewLocalQsos());
        Assert.Equal(25, _outbox.PendingCount);
    }
}
