// SPDX-License-Identifier: GPL-2.0-or-later
using FakeWavelog;
using Zeus.Plugin.Wavelog.Sync;
using Zeus.Plugins.Contracts;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>A rig under the test's control.</summary>
public sealed class FakeRadio : IRadioStateReader
{
    public long FrequencyHz { get; private set; } = 14_074_000;
    public string Mode { get; private set; } = "USB";
    public string Band { get; private set; } = "20m";
    public bool Mox { get; private set; }

    public event Action<long>? FrequencyChanged;
    public event Action<string>? ModeChanged;
    public event Action<bool>? MoxChanged;

    public void TuneTo(long hz) { FrequencyHz = hz; FrequencyChanged?.Invoke(hz); }
    public void SwitchTo(string mode) { Mode = mode; ModeChanged?.Invoke(mode); }
    public void Key(bool on) { Mox = on; MoxChanged?.Invoke(on); }
}

public sealed class RadioStateTests : IDisposable
{
    private readonly FakeWavelogServer _wavelog = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly FakeClock _clock = new(new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc));
    private readonly FakeRadio _radio = new();

    public RadioStateTests() => _wavelog.Start();
    public void Dispose() { _wavelog.Dispose(); _http.Dispose(); }

    private WavelogConfig Config => new()
    {
        BaseUrl = _wavelog.BaseUrl, ApiKey = _wavelog.ApiKey,
        StationProfileId = 1, RadioEnabled = true,
    };

    private RadioStatePublisher NewPublisher(WavelogConfig? config = null) => new(
        _radio, new HttpWavelogTransport(_http), () => config ?? Config, _clock);

    [Fact]
    public async Task It_is_off_unless_switched_on()
    {
        using var publisher = NewPublisher(Config with { RadioEnabled = false });
        publisher.Start();
        Assert.False(await publisher.TickAsync(default));
        Assert.Equal(0, _wavelog.RadioPostCount);
    }

    [Fact]
    public async Task The_first_tick_after_starting_sends_the_current_state()
    {
        using var publisher = NewPublisher();
        publisher.Start();

        Assert.True(await publisher.TickAsync(default));
        Assert.Equal("14074000", _wavelog.LastRadioPayload!["frequency"]!.GetValue<string>());
        Assert.Equal("USB", _wavelog.LastRadioPayload["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Nothing_is_sent_when_nothing_changed()
    {
        using var publisher = NewPublisher();
        publisher.Start();
        await publisher.TickAsync(default);

        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.False(await publisher.TickAsync(default));
        Assert.Equal(1, _wavelog.RadioPostCount);
    }

    [Fact]
    public async Task Spinning_the_vfo_produces_one_update_not_hundreds()
    {
        // A tuning knob fires a great many events; Wavelog only needs to know
        // roughly where the rig is.
        using var publisher = NewPublisher();
        publisher.Start();
        await publisher.TickAsync(default);
        _clock.Advance(TimeSpan.FromSeconds(10));

        for (var i = 0; i < 200; i++) _radio.TuneTo(14_074_000 + i * 10);

        Assert.True(await publisher.TickAsync(default));
        Assert.Equal(2, _wavelog.RadioPostCount);
    }

    [Fact]
    public async Task A_change_inside_the_interval_waits_rather_than_being_dropped()
    {
        using var publisher = NewPublisher();
        publisher.Start();
        await publisher.TickAsync(default);

        _radio.SwitchTo("CWU");
        Assert.False(await publisher.TickAsync(default));      // too soon

        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(await publisher.TickAsync(default));       // and now it goes
        Assert.Equal("CWU", _wavelog.LastRadioPayload!["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_read_only_key_switches_it_off_instead_of_complaining_forever()
    {
        _wavelog.KeyCanWrite = false;
        using var publisher = NewPublisher();
        publisher.Start();

        Assert.False(await publisher.TickAsync(default));
        var attempts = _wavelog.RadioPostCount;

        _radio.TuneTo(7_100_000);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await publisher.TickAsync(default);

        Assert.Equal(attempts, _wavelog.RadioPostCount);       // it stopped trying
    }

    [Fact]
    public async Task A_wavelog_that_is_down_does_not_stop_it_trying_again_later()
    {
        _wavelog.ForceStatus = 500;
        using var publisher = NewPublisher();
        publisher.Start();
        await publisher.TickAsync(default);

        _wavelog.ForceStatus = 0;
        _radio.TuneTo(7_100_000);
        _clock.Advance(TimeSpan.FromSeconds(10));

        Assert.True(await publisher.TickAsync(default));
    }

    [Fact]
    public void Disposing_unsubscribes_from_the_rig()
    {
        var publisher = NewPublisher();
        publisher.Start();
        publisher.Dispose();
        _radio.TuneTo(1_000_000);                              // must not throw or leak
    }
}
