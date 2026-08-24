// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using Microsoft.Extensions.Logging;

namespace Zeus.Plugin.Wavelog.Sync;

/// <summary>
/// Drains the outbox towards Wavelog.
///
/// <para>Deliberately dull: lease, send, classify, ack or reschedule. All the
/// judgement lives in <see cref="RetryPolicy"/>, which is pure, so this class
/// has nothing worth arguing about and can be driven a step at a time by a
/// test with a fake clock.</para>
/// </summary>
public sealed class OutboxPump(
    IOutbox outbox,
    IWavelogTransport transport,
    Func<WavelogConfig> config,
    RetryPolicy policy,
    ILogger? log = null)
{
    public static readonly TimeSpan LeaseFor = TimeSpan.FromMinutes(2);

    /// <summary>Raised when an item reaches Wavelog, so the QSO can be stamped.</summary>
    public event Action<string>? Delivered;

    /// <summary>Raised when an item is given up on, with the reason.</summary>
    public event Action<string, string>? DeadLettered;

    /// <summary>
    /// Sends at most <paramref name="max"/> items. Returns how many were tried,
    /// so a caller can loop until it drains without this class owning a timer.
    /// </summary>
    public async Task<int> DrainOnceAsync(int max, CancellationToken ct)
    {
        var settings = config();
        if (!settings.IsUsable || !settings.PushEnabled) return 0;

        var tried = 0;
        for (var i = 0; i < max && !ct.IsCancellationRequested; i++)
        {
            var item = outbox.Lease(LeaseFor);
            if (item is null) break;
            tried++;

            var outcome = await transport.PostQsoAsync(settings, item.Adif, ct).ConfigureAwait(false);
            var decision = policy.Decide(outcome, item.Attempt);

            switch (decision.Action)
            {
                case RetryAction.Done:
                    outbox.Ack(item.Id);
                    Delivered?.Invoke(item.QsoId);
                    break;

                case RetryAction.DeadLetter:
                    outbox.DeadLetter(item.Id, decision.Reason);
                    DeadLettered?.Invoke(item.QsoId, decision.Reason);
                    log?.LogWarning("wavelog: giving up on QSO {Qso}: {Reason}", item.QsoId, decision.Reason);
                    break;

                default:
                    outbox.Fail(item.Id, decision.RetryAfter, decision.Reason);
                    log?.LogDebug("wavelog: retrying QSO {Qso} in {Delay}: {Reason}",
                        item.QsoId, decision.RetryAfter, decision.Reason);
                    // Stop the batch: if Wavelog is unwell, the next item will
                    // fail the same way, and hammering it helps nobody.
                    return tried;
            }
        }
        return tried;
    }

    /// <summary>The background loop. Not used by the tests, which step it themselves.</summary>
    public async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await DrainOnceAsync(50, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log?.LogError(ex, "wavelog: pump iteration failed"); }

            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
