// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;

namespace Zeus.Plugin.Wavelog.Sync;

/// <summary>
/// Pushes live rig state to Wavelog's <c>/api/radio</c>, so its QSO entry form
/// auto-fills the current frequency and mode and the station shows as live —
/// from any browser, including a phone.
///
/// <para>Independent of the logging path by design: it holds no queue and never
/// retries. A dropped update is replaced by the next one a moment later, so
/// durability here would cost more than it is worth. If Wavelog is down, rig
/// state simply stops updating and logging is unaffected.</para>
/// </summary>
public sealed class RadioStatePublisher(
    IRadioStateReader radio,
    IWavelogTransport transport,
    Func<WavelogConfig> config,
    IClock clock,
    string radioName = "Zeus",
    ILogger? log = null) : IDisposable
{
    /// <summary>
    /// A VFO knob produces a great many events. Wavelog only needs to know
    /// roughly where the rig is, so updates are coalesced rather than sent per
    /// event — this is a status display, not telemetry.
    /// </summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromSeconds(5);

    private DateTime _lastSent = DateTime.MinValue;
    private bool _dirty;
    private bool _permanentlyOff;
    private bool _subscribed;

    public void Start()
    {
        if (_subscribed) return;
        radio.FrequencyChanged += OnFrequency;
        radio.ModeChanged += OnMode;
        radio.MoxChanged += OnMox;
        _subscribed = true;
        _dirty = true;
    }

    private void OnFrequency(long _) => _dirty = true;
    private void OnMode(string _) => _dirty = true;
    private void OnMox(bool _) => _dirty = true;

    /// <summary>
    /// Send if anything changed and the interval has elapsed. Driven by the
    /// caller's loop rather than a timer of its own, so a test can step it.
    /// </summary>
    public async Task<bool> TickAsync(CancellationToken ct)
    {
        var settings = config();
        if (_permanentlyOff || !settings.IsUsable || !settings.RadioEnabled) return false;
        if (!_dirty || clock.UtcNow - _lastSent < MinInterval) return false;

        _dirty = false;
        _lastSent = clock.UtcNow;

        var state = new RadioState(radioName, radio.FrequencyHz, radio.Mode, null);
        var outcome = await transport.PostRadioAsync(settings, state, ct).ConfigureAwait(false);

        if (outcome.IsSuccess) return true;

        // A read-only key can never work here, so keep quiet rather than
        // logging the same rejection every few seconds forever.
        if (outcome.Status == 403)
        {
            _permanentlyOff = true;
            log?.LogWarning("wavelog: rig state disabled — {Reason}", outcome.Detail);
        }
        else
        {
            log?.LogDebug("wavelog: rig state not sent — {Reason}", outcome.Detail ?? outcome.Kind.ToString());
        }
        return false;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log?.LogError(ex, "wavelog: rig state tick failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose()
    {
        if (!_subscribed) return;
        radio.FrequencyChanged -= OnFrequency;
        radio.ModeChanged -= OnMode;
        radio.MoxChanged -= OnMox;
        _subscribed = false;
    }
}
