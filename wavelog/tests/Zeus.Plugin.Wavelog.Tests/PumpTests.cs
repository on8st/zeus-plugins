// SPDX-License-Identifier: GPL-2.0-or-later
using FakeWavelog;
using Zeus.Plugin.Wavelog.Sync;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>
/// The pump under a controlled clock and a controlled Wavelog: it must drain in
/// order, back off while the instance is unwell, recover on its own, and never
/// lose an item to a crash.
/// </summary>
public sealed class PumpTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wavelog-pump-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new(new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc));
    private readonly FakeWavelogServer _wavelog = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly LiteDbOutbox _outbox;

    public PumpTests()
    {
        Directory.CreateDirectory(_dir);
        _wavelog.Start();
        _outbox = new LiteDbOutbox(Path.Combine(_dir, "outbox.db"), _clock);
    }

    public void Dispose()
    {
        _outbox.Dispose(); _wavelog.Dispose(); _http.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>
    /// The key the <em>client</em> presents, held separately from the server's.
    /// Sharing one field would keep them in lockstep and make a wrong-key test
    /// silently pass.
    /// </summary>
    private string _clientKey = "test-key";

    private WavelogConfig Config => new()
    {
        BaseUrl = _wavelog.BaseUrl, ApiKey = _clientKey,
        StationProfileId = 1, PullStationIds = [1],
    };

    private OutboxPump NewPump() => new(
        _outbox, new HttpWavelogTransport(_http), () => Config, RetryPolicy.Default);

    private static string Adif(string call, string time = "090000") =>
        $"<CALL:{call.Length}>{call}<QSO_DATE:8>20260824<TIME_ON:6>{time}" +
        "<BAND:3>20m<MODE:3>SSB<EOR>";

    // ---- the happy path -----------------------------------------------------

    [Fact]
    public async Task Draining_delivers_and_empties_the_queue()
    {
        _outbox.Enqueue("q1", Adif("DL1ABC"));
        var delivered = new List<string>();
        var pump = NewPump();
        pump.Delivered += delivered.Add;

        await pump.DrainOnceAsync(10, default);

        Assert.Equal(0, _outbox.PendingCount);
        Assert.Single(_wavelog.Rows);
        Assert.Equal("q1", Assert.Single(delivered));
    }

    [Fact]
    public async Task Items_are_sent_oldest_first()
    {
        _outbox.Enqueue("q1", Adif("FIRST", "090000"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        _outbox.Enqueue("q2", Adif("SECOND", "091000"));

        await NewPump().DrainOnceAsync(10, default);

        Assert.Equal(["FIRST", "SECOND"], _wavelog.Rows.Select(r => r.Call).ToArray());
    }

    // ---- an unwell instance -------------------------------------------------

    [Fact]
    public async Task A_server_error_leaves_the_item_queued_and_stops_the_batch()
    {
        _wavelog.ForceStatus = 500;
        _outbox.Enqueue("q1", Adif("A"));
        _outbox.Enqueue("q2", Adif("B", "091000"));

        var tried = await NewPump().DrainOnceAsync(10, default);

        Assert.Equal(1, tried);                        // did not hammer a sick instance
        Assert.Equal(2, _outbox.PendingCount);
        Assert.Empty(_wavelog.Rows);
    }

    [Fact]
    public async Task It_recovers_by_itself_once_the_instance_returns()
    {
        _wavelog.ForceStatus = 500;
        _outbox.Enqueue("q1", Adif("DL1ABC"));
        await NewPump().DrainOnceAsync(10, default);
        Assert.Equal(1, _outbox.PendingCount);

        _wavelog.ForceStatus = 0;
        _clock.Advance(TimeSpan.FromMinutes(5));        // past the backoff

        await NewPump().DrainOnceAsync(10, default);

        Assert.Equal(0, _outbox.PendingCount);
        Assert.Single(_wavelog.Rows);
    }

    [Fact]
    public async Task Backoff_is_respected_rather_than_retried_immediately()
    {
        _wavelog.ForceStatus = 500;
        _outbox.Enqueue("q1", Adif("A"));
        await NewPump().DrainOnceAsync(10, default);

        _wavelog.ForceStatus = 0;
        var tried = await NewPump().DrainOnceAsync(10, default);   // no time has passed

        Assert.Equal(0, tried);
        Assert.Empty(_wavelog.Rows);
    }

    // ---- a wrong key --------------------------------------------------------

    [Fact]
    public async Task A_wrong_key_dead_letters_at_once_instead_of_queueing_forever()
    {
        _clientKey = "the-wrong-key";
        _outbox.Enqueue("q1", Adif("A"));
        var reasons = new List<string>();
        var pump = NewPump();
        pump.DeadLettered += (_, reason) => reasons.Add(reason);

        await pump.DrainOnceAsync(10, default);

        Assert.Equal(0, _outbox.PendingCount);
        Assert.Equal(1, _outbox.DeadLetterCount);
        Assert.Contains("key", Assert.Single(reasons), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fixing_the_key_and_requeueing_delivers_the_backlog()
    {
        _clientKey = "the-wrong-key";
        _outbox.Enqueue("q1", Adif("A"));
        await NewPump().DrainOnceAsync(10, default);

        _clientKey = _wavelog.ApiKey;                   // the operator fixed it
        Assert.Equal(1, _outbox.RequeueDeadLettered());

        await NewPump().DrainOnceAsync(10, default);

        Assert.Equal(0, _outbox.PendingCount);
        Assert.Single(_wavelog.Rows);
    }

    // ---- crash safety -------------------------------------------------------

    [Fact]
    public async Task An_item_leased_when_the_process_died_is_sent_later_not_lost()
    {
        _outbox.Enqueue("q1", Adif("DL1ABC"));
        _outbox.Lease(OutboxPump.LeaseFor);             // as if a send began and the process died

        _clock.Advance(OutboxPump.LeaseFor + TimeSpan.FromMinutes(1));
        await NewPump().DrainOnceAsync(10, default);

        Assert.Single(_wavelog.Rows);
        Assert.Equal(0, _outbox.PendingCount);
    }

    [Fact]
    public async Task A_redelivery_of_something_that_already_landed_does_not_duplicate_it()
    {
        // The ambiguous case: the POST succeeded but the ack never happened.
        // Wavelog's own duplicate check is what makes this safe.
        _outbox.Enqueue("q1", Adif("DL1ABC"));
        await NewPump().DrainOnceAsync(10, default);

        _outbox.Enqueue("q1-again", Adif("DL1ABC"));
        await NewPump().DrainOnceAsync(10, default);

        Assert.Single(_wavelog.Rows);
        Assert.Equal(2, _wavelog.QsoPostCount);
    }

    // ---- switches -----------------------------------------------------------

    [Fact]
    public async Task Nothing_is_sent_while_push_is_switched_off()
    {
        _outbox.Enqueue("q1", Adif("A"));
        var pump = new OutboxPump(_outbox, new HttpWavelogTransport(_http),
            () => Config with { PushEnabled = false }, RetryPolicy.Default);

        Assert.Equal(0, await pump.DrainOnceAsync(10, default));
        Assert.Equal(1, _outbox.PendingCount);
    }

    [Fact]
    public async Task Nothing_is_sent_before_the_plugin_is_configured()
    {
        _outbox.Enqueue("q1", Adif("A"));
        var pump = new OutboxPump(_outbox, new HttpWavelogTransport(_http),
            () => new WavelogConfig(), RetryPolicy.Default);

        Assert.Equal(0, await pump.DrainOnceAsync(10, default));
        Assert.Equal(1, _outbox.PendingCount);
    }
}
