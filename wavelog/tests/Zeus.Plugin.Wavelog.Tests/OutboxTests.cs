// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugin.Wavelog.Sync;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>
/// The outbox is where the interesting failures live. The HTTP call is a POST
/// and a status code; the queue in front of it is what has to survive a process
/// dying mid-flight, a wrong key, and a Wavelog that is down for a day.
/// </summary>
public sealed class OutboxTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wavelog-outbox-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new(new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc));

    private LiteDbOutbox NewOutbox() => new(Path.Combine(_dir, "outbox.db"), _clock);

    public OutboxTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void An_enqueued_item_is_pending()
    {
        using var outbox = NewOutbox();
        outbox.Enqueue("qso-1", "<CALL:6>DL1ABC<EOR>");
        Assert.Equal(1, outbox.PendingCount);
    }

    [Fact]
    public void Enqueue_survives_a_restart()
    {
        using (var outbox = NewOutbox()) outbox.Enqueue("qso-1", "adif");
        using (var reopened = NewOutbox()) Assert.Equal(1, reopened.PendingCount);
    }

    [Fact]
    public void The_same_qso_is_not_queued_twice()
    {
        // A resync or a retry loop must not multiply the queue.
        using var outbox = NewOutbox();
        outbox.Enqueue("qso-1", "adif");
        outbox.Enqueue("qso-1", "adif");
        Assert.Equal(1, outbox.PendingCount);
    }

    [Fact]
    public void Lease_hands_out_the_oldest_first()
    {
        using var outbox = NewOutbox();
        outbox.Enqueue("first", "a");
        _clock.Advance(TimeSpan.FromSeconds(1));
        outbox.Enqueue("second", "b");

        Assert.Equal("first", outbox.Lease(TimeSpan.FromMinutes(5))!.QsoId);
    }

    [Fact]
    public void A_leased_item_is_not_handed_out_again()
    {
        using var outbox = NewOutbox();
        outbox.Enqueue("qso-1", "adif");
        Assert.NotNull(outbox.Lease(TimeSpan.FromMinutes(5)));
        Assert.Null(outbox.Lease(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void An_item_in_flight_when_the_process_dies_is_redelivered_not_lost()
    {
        // The lease expires rather than the item being deleted on hand-out, so
        // a crash between lease and ack loses nothing.
        using var outbox = NewOutbox();
        outbox.Enqueue("qso-1", "adif");
        Assert.NotNull(outbox.Lease(TimeSpan.FromMinutes(5)));

        _clock.Advance(TimeSpan.FromMinutes(6));                 // the process was gone

        Assert.Equal("qso-1", outbox.Lease(TimeSpan.FromMinutes(5))!.QsoId);
    }

    [Fact]
    public void Ack_removes_the_item()
    {
        using var outbox = NewOutbox();
        outbox.Enqueue("qso-1", "adif");
        var leased = outbox.Lease(TimeSpan.FromMinutes(5))!;
        outbox.Ack(leased.Id);
        Assert.Equal(0, outbox.PendingCount);
    }

    [Fact]
    public void Fail_puts_it_back_with_a_delay_and_counts_the_attempt()
    {
        using var outbox = NewOutbox();
        outbox.Enqueue("qso-1", "adif");
        var leased = outbox.Lease(TimeSpan.FromMinutes(5))!;
        outbox.Fail(leased.Id, TimeSpan.FromMinutes(10), "timed out");

        Assert.Null(outbox.Lease(TimeSpan.FromMinutes(5)));       // still waiting
        _clock.Advance(TimeSpan.FromMinutes(11));

        var again = outbox.Lease(TimeSpan.FromMinutes(5))!;
        Assert.Equal(2, again.Attempt);
        Assert.Equal("timed out", again.LastError);
    }

    [Fact]
    public void Dead_lettered_items_leave_the_queue_but_stay_visible()
    {
        using var outbox = NewOutbox();
        outbox.Enqueue("qso-1", "adif");
        var leased = outbox.Lease(TimeSpan.FromMinutes(5))!;
        outbox.DeadLetter(leased.Id, "the API key is wrong");

        Assert.Equal(0, outbox.PendingCount);
        Assert.Equal(1, outbox.DeadLetterCount);
        var dead = Assert.Single(outbox.DeadLettered());
        Assert.Equal("the API key is wrong", dead.LastError);
    }

    [Fact]
    public void Dead_letters_can_be_requeued_when_the_cause_is_fixed()
    {
        using var outbox = NewOutbox();
        outbox.Enqueue("qso-1", "adif");
        outbox.DeadLetter(outbox.Lease(TimeSpan.FromMinutes(5))!.Id, "bad key");

        Assert.Equal(1, outbox.RequeueDeadLettered());

        Assert.Equal(1, outbox.PendingCount);
        Assert.Equal(0, outbox.DeadLetterCount);
        Assert.Equal(1, outbox.Lease(TimeSpan.FromMinutes(5))!.Attempt);   // attempts reset
    }

    [Fact]
    public void An_empty_queue_leases_nothing()
    {
        using var outbox = NewOutbox();
        Assert.Null(outbox.Lease(TimeSpan.FromMinutes(5)));
    }
}
