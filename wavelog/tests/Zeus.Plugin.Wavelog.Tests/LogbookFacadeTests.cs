// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugin.Wavelog.Storage;
using Zeus.Plugin.Wavelog.Sync;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>
/// These encode the design decision the whole plugin is arranged around: the
/// operator's call never waits on Wavelog, and never fails because of it.
///
/// <para>They exercise the sync service against a transport that refuses to be
/// used, which is the sharpest way to state the property — if the write path
/// touched the network at all, these would fail.</para>
/// </summary>
public sealed class LogbookFacadeTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wavelog-facade-" + Guid.NewGuid().ToString("N"));
    private readonly LiteDbLogStore _store;
    private readonly LiteDbOutbox _outbox;

    public LogbookFacadeTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new LiteDbLogStore(Path.Combine(_dir, "log.db"));
        _outbox = new LiteDbOutbox(Path.Combine(_dir, "outbox.db"));
    }

    public void Dispose()
    {
        _store.Dispose(); _outbox.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>A transport that fails the test if anything reaches it.</summary>
    private sealed class ForbiddenTransport : IWavelogTransport
    {
        public Task<WavelogOutcome> PostQsoAsync(WavelogConfig c, string a, CancellationToken ct)
            => throw new InvalidOperationException("the write path must not touch the network");
        public Task<(WavelogOutcome, PulledQsos?)> GetContactsAsync(
            WavelogConfig c, int f, int l, IReadOnlyList<string>? q, CancellationToken ct)
            => throw new InvalidOperationException("the write path must not touch the network");
        public Task<(WavelogOutcome, IReadOnlyList<StationProfile>?)> GetStationInfoAsync(
            WavelogConfig c, CancellationToken ct)
            => throw new InvalidOperationException("the write path must not touch the network");
        public Task<WavelogOutcome> PostRadioAsync(WavelogConfig c, RadioState s, CancellationToken ct)
            => throw new InvalidOperationException("the write path must not touch the network");
    }

    /// <summary>A transport that is broken in the ordinary way: it throws.</summary>
    private sealed class BrokenTransport : IWavelogTransport
    {
        public Task<WavelogOutcome> PostQsoAsync(WavelogConfig c, string a, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
        public Task<(WavelogOutcome, PulledQsos?)> GetContactsAsync(
            WavelogConfig c, int f, int l, IReadOnlyList<string>? q, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
        public Task<(WavelogOutcome, IReadOnlyList<StationProfile>?)> GetStationInfoAsync(
            WavelogConfig c, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
        public Task<WavelogOutcome> PostRadioAsync(WavelogConfig c, RadioState s, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }

    private static WavelogConfig Configured => new()
    {
        BaseUrl = "http://127.0.0.1:1", ApiKey = "k", StationProfileId = 1,
    };

    private static LogbookNewEntry Qso(string call = "DL1ABC") =>
        new(call, null, 14.074, "20m", "USB", "59", "59",
            QsoDateTimeUtc: new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc));

    // ---- the two that matter -----------------------------------------------

    [Fact]
    public async Task Logging_a_qso_never_touches_the_network()
    {
        var sync = new WavelogSyncService(
            _store, _outbox, new ForbiddenTransport(), () => Configured, new MemoryCursorStore());

        var saved = await _store.CreateAsync(Qso());
        sync.EnqueueForPush(saved);                 // would throw if it sent anything

        Assert.Equal(1, _outbox.PendingCount);
    }

    [Fact]
    public async Task Logging_a_qso_still_succeeds_when_wavelog_is_unreachable()
    {
        var sync = new WavelogSyncService(
            _store, _outbox, new BrokenTransport(), () => Configured, new MemoryCursorStore());

        var saved = await _store.CreateAsync(Qso());
        sync.EnqueueForPush(saved);

        // The contact is durable and queued; the outage is somebody else's
        // problem, later.
        Assert.Equal(1, (await _store.GetEntriesAsync(0, 10)).TotalCount);
        Assert.Equal(1, _outbox.PendingCount);
    }

    // ---- and the corollaries -----------------------------------------------

    [Fact]
    public async Task An_unconfigured_plugin_still_logs_it_just_queues_nothing()
    {
        var sync = new WavelogSyncService(
            _store, _outbox, new ForbiddenTransport(), () => new WavelogConfig(), new MemoryCursorStore());

        var saved = await _store.CreateAsync(Qso());
        sync.EnqueueForPush(saved);

        Assert.Equal(1, (await _store.GetEntriesAsync(0, 10)).TotalCount);
        Assert.Equal(0, _outbox.PendingCount);
    }

    [Fact]
    public async Task Push_switched_off_logs_without_queueing()
    {
        var sync = new WavelogSyncService(
            _store, _outbox, new ForbiddenTransport(),
            () => Configured with { PushEnabled = false }, new MemoryCursorStore());

        sync.EnqueueForPush(await _store.CreateAsync(Qso()));

        Assert.Equal(0, _outbox.PendingCount);
    }

    [Fact]
    public async Task The_same_qso_queued_twice_is_queued_once()
    {
        var sync = new WavelogSyncService(
            _store, _outbox, new ForbiddenTransport(), () => Configured, new MemoryCursorStore());

        var saved = await _store.CreateAsync(Qso());
        sync.EnqueueForPush(saved);
        sync.EnqueueForPush(saved);

        Assert.Equal(1, _outbox.PendingCount);
    }
}
