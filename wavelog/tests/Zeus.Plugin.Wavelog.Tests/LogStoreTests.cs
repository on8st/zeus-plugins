// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugin.Wavelog.Storage;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>
/// The store is the plugin's first duty — it <em>is</em> the logbook, and the
/// client's browsing, sorting, searching and editing all run through it. These
/// tests use a real LiteDB file in a temp directory rather than a fake, because
/// the things worth checking (ordering, paging, persistence across a reopen)
/// are exactly the things a fake would get right for free.
/// </summary>
public sealed class LogStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wavelog-store-" + Guid.NewGuid().ToString("N"));

    private LiteDbLogStore NewStore() => new(Path.Combine(_dir, "log.db"));

    public LogStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private static LogbookNewEntry Qso(
        string call = "DL1ABC", string band = "20m", string mode = "USB",
        DateTime? when = null) =>
        new(Callsign: call, Name: null, FrequencyMhz: 14.074, Band: band, Mode: mode,
            RstSent: "59", RstRcvd: "57",
            QsoDateTimeUtc: when ?? new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc));

    // ---- create -------------------------------------------------------------

    [Fact]
    public async Task Create_returns_a_snapshot_with_an_id()
    {
        using var store = NewStore();
        var saved = await store.CreateAsync(Qso());
        Assert.False(string.IsNullOrWhiteSpace(saved.Id));
        Assert.Equal("DL1ABC", saved.Callsign);
    }

    [Fact]
    public async Task Create_survives_a_reopen()
    {
        string id;
        using (var store = NewStore()) id = (await store.CreateAsync(Qso())).Id;
        using (var reopened = NewStore())
        {
            var found = await reopened.GetByIdsAsync([id]);
            Assert.Equal("DL1ABC", Assert.Single(found).Callsign);
        }
    }

    [Fact]
    public async Task Create_defaults_the_timestamp_to_now_when_omitted()
    {
        using var store = NewStore();
        var saved = await store.CreateAsync(new LogbookNewEntry(
            "G4XYZ", null, 14.074, "20m", "USB", "59", "59"));
        Assert.True((DateTime.UtcNow - saved.QsoDateTimeUtc).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Timestamps_round_trip_as_utc_not_local()
    {
        // LiteDB stores dates as UTC but hands them back in local time. For a
        // logbook that is not cosmetic: it shifts every QSO time the operator
        // sees, and because the dedup key is built from the timestamp to the
        // minute, it would make Wavelog treat the same contact as a new one.
        var when = new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc);
        string id;
        using (var store = NewStore()) id = (await store.CreateAsync(Qso(when: when))).Id;
        using (var reopened = NewStore())
        {
            var found = Assert.Single(await reopened.GetByIdsAsync([id]));
            Assert.Equal(DateTimeKind.Utc, found.QsoDateTimeUtc.Kind);
            Assert.Equal(when, found.QsoDateTimeUtc);
        }
    }

    // ---- browsing: the client's path ---------------------------------------

    [Fact]
    public async Task Entries_come_back_newest_first()
    {
        using var store = NewStore();
        await store.CreateAsync(Qso("OLD", when: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        await store.CreateAsync(Qso("NEW", when: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        var page = await store.GetEntriesAsync(0, 10);
        Assert.Equal("NEW", page.Entries[0].Callsign);
        Assert.Equal("OLD", page.Entries[1].Callsign);
    }

    [Fact]
    public async Task Paging_reports_the_total_not_the_page_size()
    {
        using var store = NewStore();
        for (var i = 0; i < 5; i++)
            await store.CreateAsync(Qso($"CALL{i}", when: new DateTime(2026, 1, 1 + i, 0, 0, 0, DateTimeKind.Utc)));

        var page = await store.GetEntriesAsync(skip: 1, take: 2);
        Assert.Equal(2, page.Entries.Count);
        Assert.Equal(5, page.TotalCount);
    }

    [Fact]
    public async Task Paging_past_the_end_returns_empty_rather_than_throwing()
    {
        using var store = NewStore();
        await store.CreateAsync(Qso());
        var page = await store.GetEntriesAsync(skip: 100, take: 10);
        Assert.Empty(page.Entries);
        Assert.Equal(1, page.TotalCount);
    }

    // ---- worked before ------------------------------------------------------

    [Fact]
    public async Task Worked_summary_is_empty_for_an_unknown_callsign()
    {
        using var store = NewStore();
        var summary = await store.GetWorkedSummaryAsync("NEVER", 5);
        Assert.NotNull(summary);
        Assert.False(summary!.WorkedBefore);
        Assert.Equal(0, summary.TotalCount);
    }

    [Fact]
    public async Task Worked_summary_collects_bands_modes_and_the_most_recent()
    {
        using var store = NewStore();
        await store.CreateAsync(Qso("DL1ABC", "40m", "CWU", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        await store.CreateAsync(Qso("DL1ABC", "20m", "USB", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));

        var s = (await store.GetWorkedSummaryAsync("dl1abc", 5))!;
        Assert.True(s.WorkedBefore);
        Assert.Equal(2, s.TotalCount);
        Assert.Equal("20m", s.LastBand);
        Assert.Contains("40m", s.Bands);
        Assert.Contains("CWU", s.Modes);
        Assert.Equal(2, s.RecentQsos.Count);
    }

    [Fact]
    public async Task Worked_summary_matches_regardless_of_callsign_casing()
    {
        using var store = NewStore();
        await store.CreateAsync(Qso("dl1abc"));
        Assert.True((await store.GetWorkedSummaryAsync("DL1ABC", 5))!.WorkedBefore);
    }

    // ---- update and delete --------------------------------------------------

    [Fact]
    public async Task Update_changes_only_what_is_given()
    {
        using var store = NewStore();
        var saved = await store.CreateAsync(Qso());
        var updated = await store.UpdateAsync(saved.Id, new LogbookEntryUpdate(Comment: "nice"));
        Assert.Equal("nice", updated!.Comment);
        Assert.Equal("DL1ABC", updated.Callsign);
        Assert.Equal(saved.Band, updated.Band);
    }

    [Fact]
    public async Task Update_can_clear_a_value_explicitly()
    {
        using var store = NewStore();
        var saved = await store.CreateAsync(Qso());
        var updated = await store.UpdateAsync(saved.Id, new LogbookEntryUpdate(ClearFrequencyMhz: true));
        Assert.Null(updated!.FrequencyMhz);
    }

    [Fact]
    public async Task Update_of_an_unknown_id_returns_null()
    {
        using var store = NewStore();
        Assert.Null(await store.UpdateAsync("nope", new LogbookEntryUpdate(Comment: "x")));
    }

    [Fact]
    public async Task Delete_removes_and_reports_how_many()
    {
        using var store = NewStore();
        var a = await store.CreateAsync(Qso("A"));
        var b = await store.CreateAsync(Qso("B"));
        Assert.Equal(2, await store.DeleteAsync([a.Id, b.Id, "not-there"]));
        Assert.Equal(0, (await store.GetEntriesAsync(0, 10)).TotalCount);
    }

    // ---- qsl and tags -------------------------------------------------------

    [Fact]
    public async Task Qsl_status_updates_apply_to_the_named_entries()
    {
        using var store = NewStore();
        var saved = await store.CreateAsync(Qso());
        var when = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        var n = await store.UpdateQslStatusAsync(
            [new LogbookQslStatusUpdate(saved.Id, when, null, null, "Y", when)]);

        Assert.Equal(1, n);
        var after = Assert.Single(await store.GetByIdsAsync([saved.Id]));
        Assert.Equal(when, after.LotwQslRcvdUtc);
        Assert.Equal("Y", after.QslRcvd);
    }

    [Fact]
    public async Task Tags_are_returned_deduplicated_and_sorted()
    {
        using var store = NewStore();
        var a = await store.CreateAsync(Qso("A"));
        var b = await store.CreateAsync(Qso("B"));
        await store.UpdateAsync(a.Id, new LogbookEntryUpdate(Tags: ["pota", "sota"]));
        await store.UpdateAsync(b.Id, new LogbookEntryUpdate(Tags: ["pota"]));

        Assert.Equal(new[] { "pota", "sota" }, await store.GetAllTagsAsync());
    }

    // ---- adif export and import --------------------------------------------

    [Fact]
    public async Task Export_produces_a_header_and_one_record_per_qso()
    {
        using var store = NewStore();
        await store.CreateAsync(Qso("A"));
        await store.CreateAsync(Qso("B"));

        var adif = await store.ExportAdifAsync();
        Assert.Contains("<EOH>", adif);
        Assert.Equal(2, AdifCountRecords(adif));
    }

    [Fact]
    public async Task Export_of_named_ids_exports_only_those()
    {
        using var store = NewStore();
        var a = await store.CreateAsync(Qso("A"));
        await store.CreateAsync(Qso("B"));
        Assert.Equal(1, AdifCountRecords(await store.ExportAdifAsync([a.Id])));
    }

    [Fact]
    public async Task Export_to_file_writes_it_and_reports_the_size()
    {
        using var store = NewStore();
        await store.CreateAsync(Qso());
        var result = await store.ExportAdifToFileAsync(_dir);
        Assert.True(File.Exists(result.Path));
        Assert.Equal(1, result.Count);
        Assert.Equal(new FileInfo(result.Path).Length, result.Bytes);
    }

    [Fact]
    public async Task Import_round_trips_an_export()
    {
        string adif;
        using (var source = NewStore())
        {
            await source.CreateAsync(Qso("A"));
            await source.CreateAsync(Qso("B", "40m", "CWU"));
            adif = await source.ExportAdifAsync();
        }

        var otherDir = Path.Combine(_dir, "other");
        Directory.CreateDirectory(otherDir);
        using var target = new LiteDbLogStore(Path.Combine(otherDir, "log.db"));

        var result = await target.ImportAdifAsync(adif);
        Assert.Equal(2, result.TotalRecords);
        Assert.Equal(2, result.ImportedCount);
        Assert.Empty(result.Errors);
        Assert.Equal(2, (await target.GetEntriesAsync(0, 10)).TotalCount);
    }

    [Fact]
    public async Task Import_skips_a_qso_it_already_has()
    {
        // Same key Wavelog uses: call + time to the minute + band + mode.
        using var store = NewStore();
        await store.CreateAsync(Qso());
        var adif = await store.ExportAdifAsync();

        var result = await store.ImportAdifAsync(adif);
        Assert.Equal(1, result.DuplicateCount);
        Assert.Equal(0, result.ImportedCount);
        Assert.Equal(1, (await store.GetEntriesAsync(0, 10)).TotalCount);
    }

    [Fact]
    public async Task Import_reports_a_bad_record_without_abandoning_the_good_ones()
    {
        using var store = NewStore();
        var result = await store.ImportAdifAsync(
            "<CALL:6>DL1ABC<QSO_DATE:8>20260824<TIME_ON:6>090000<BAND:3>20m<MODE:3>SSB<EOR>" +
            "<QSO_DATE:8>20260824<EOR>");                       // no callsign

        Assert.Equal(2, result.TotalRecords);
        Assert.Equal(1, result.ImportedCount);
        Assert.Single(result.Errors);
    }

    private static int AdifCountRecords(string adif) =>
        AdifParserCount(adif);

    private static int AdifParserCount(string adif) =>
        Zeus.Plugin.Wavelog.Adif.AdifParser.Parse(adif).Count;
}
