// SPDX-License-Identifier: GPL-2.0-or-later
using FakeWavelog;
using Zeus.Plugin.Wavelog.Storage;
using Zeus.Plugin.Wavelog.Sync;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>
/// Pulling with no station location selected.
///
/// <para>Wavelog refuses an empty selection outright — <c>"station_id" must not
/// be empty</c> — so this is not a broad query, it is a request that can only
/// fail. And an empty list is the <em>default</em>, so it is the state a freshly
/// configured plugin arrives in: found on a live install where the operator had
/// saved a URL and key and nothing else.</para>
///
/// <para>Left alone it presents as silence with an error every thirty seconds in
/// a log nobody reads, while the actual cause — a field nobody filled in — stays
/// invisible in a panel that accepted it without comment.</para>
/// </summary>
public sealed class EmptyStationSelectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wavelog-empty-" + Guid.NewGuid().ToString("N"));
    private readonly FakeWavelogServer _wavelog = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly NativeLogbook _zeus;
    private readonly ZeusLogbookDb _logbook;
    private readonly LiteDbOutbox _outbox;

    public EmptyStationSelectionTests()
    {
        Directory.CreateDirectory(_dir);
        _wavelog.Start();
        _zeus = NativeLogbook.InDataDirectory(_dir);
        _logbook = ZeusLogbookDb.ForDataDirectory(_dir);
        _outbox = new LiteDbOutbox(Path.Combine(_dir, "outbox.db"));
    }

    public void Dispose()
    {
        _zeus.Dispose(); _logbook.Dispose(); _outbox.Dispose();
        _wavelog.Dispose(); _http.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private WavelogConfig Config => new()
    {
        BaseUrl = _wavelog.BaseUrl, ApiKey = _wavelog.ApiKey,
        StationProfileId = 1, PullStationIds = [],      // the default
    };

    private WavelogSyncService Sync(WavelogConfig c) =>
        new(_logbook, _outbox, new HttpWavelogTransport(_http), () => c, new MemoryCursorStore());

    // ---- what an empty selection resolves to -------------------------------

    [Fact]
    public void An_empty_selection_falls_back_to_the_location_we_push_to()
    {
        // Not "everything": Wavelog refuses an empty station_id outright, so
        // there is no broad-query option to fall back to even if we wanted one.
        Assert.Equal([1], Config.EffectivePullStationIds);
        Assert.True(Config.PullLocationsAreImplicit);
    }

    [Fact]
    public void An_explicit_selection_is_used_as_given()
    {
        var c = Config with { PullStationIds = [2, 3] };
        Assert.Equal([2, 3], c.EffectivePullStationIds);
        Assert.False(c.PullLocationsAreImplicit);
    }

    [Fact]
    public async Task The_fallback_actually_pulls_rather_than_erroring()
    {
        // The bug this replaced: an empty list was reported as configured-and-
        // idle, when in fact a whole logbook was being imported from the push
        // location. Both halves matter — that it works, and that it is named.
        _wavelog.AddQsoFromAnotherApp("G4XYZ", new DateTime(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc), "40m", "CW");

        var report = await Sync(Config).PullNewAsync(default);

        Assert.True(report.Ran);
        Assert.Equal(1, report.Imported);
    }

    [Fact]
    public async Task An_explicit_elsewhere_does_not_pull_the_push_location()
    {
        // The operator pushes to 1 and pulls from 2 only. A QSO under 1 must
        // stay invisible, or "pull from these locations" means nothing.
        _wavelog.AddQsoFromAnotherApp("HOME", DateTime.UtcNow, "20m", "SSB", stationId: 1);

        var report = await Sync(Config with { PullStationIds = [2] }).PullNewAsync(default);

        Assert.Equal(0, report.Imported);
    }

    // ---- and the server really does refuse an empty one ---------------------

    [Fact]
    public async Task The_server_refuses_a_genuinely_empty_selection()
    {
        // Verbatim from wavelog.on8st.be. The transport must never send this
        // shape; the fake reproduces the refusal so that if it ever does, a test
        // fails rather than a live sync.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var reply = await http.PostAsync(
            _wavelog.BaseUrl + "/index.php/api/get_contacts_adif",
            new StringContent(
                $$"""{"key":"{{_wavelog.ApiKey}}","station_id":[],"fetchfromid":0,"limit":5}""",
                System.Text.Encoding.UTF8, "application/json"));

        Assert.Contains("must not be empty", await reply.Content.ReadAsStringAsync());
    }
}
