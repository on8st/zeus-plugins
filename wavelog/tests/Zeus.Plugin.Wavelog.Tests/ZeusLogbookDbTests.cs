// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugin.Wavelog.Storage;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>
/// Attaching to somebody else's database.
///
/// <para>These are the tests that earn the reframe. The plugin is a
/// synchroniser, not a logbook: the QSOs are Zeus's, written by Zeus's own
/// plugin, and this class is a second handle on the same file. So every test
/// here starts by having <see cref="NativeLogbook"/> — a separate
/// <c>LiteDatabase</c>, opened the way the reference opens it — write the
/// contact, and then asks what the synchroniser can see and what it is allowed
/// to touch.</para>
///
/// <para>Real files in a temp directory throughout. A fake would satisfy every
/// assertion here for free and prove none of them, because the questions being
/// asked are about LiteDB's actual behaviour across two handles.</para>
/// </summary>
public sealed class ZeusLogbookDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wavelog-logbook-" + Guid.NewGuid().ToString("N"));

    public ZeusLogbookDbTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private NativeLogbook Zeus() => NativeLogbook.InDataDirectory(_dir);
    private ZeusLogbookDb Attach() => ZeusLogbookDb.ForDataDirectory(_dir);

    // ---- the file we attach to ---------------------------------------------

    [Fact]
    public void We_attach_to_the_file_and_collection_the_reference_uses()
    {
        // These two names are pinned by what the shipped v1.1.0 plugin actually
        // wrote when it was run in an isolated engine — not by reading its
        // string table, which is how the collection came out as "entries" (the
        // plugin's HTTP route) and stayed wrong through a green suite.
        //
        // A unit test cannot re-derive these; only tests/integration can, and
        // does. This one exists so a careless edit has to be deliberate.
        Assert.Equal("zeus-logbook.db", ZeusLogbookDb.FileName);
        Assert.Equal("logs", ZeusLogbookDb.EntriesCollection);

        using (var zeus = Zeus()) zeus.Log();
        Assert.True(File.Exists(Path.Combine(_dir, "zeus-logbook.db")));

        using var db = Attach();
        Assert.Equal(1, db.Count());
    }

    [Fact]
    public void A_qso_logged_in_zeus_is_visible_without_reopening()
    {
        // Shared mode, both sides. Under Direct the second handle opens happily
        // and then never sees this write — no error, two divergent views of the
        // operator's log. This test is the reason the connection string is not
        // negotiable.
        using var zeus = Zeus();
        using var db = Attach();

        Assert.Equal(0, db.Count());
        zeus.Log("G4XYZ");
        Assert.Equal(1, db.Count());
    }

    [Fact]
    public void Timestamps_come_back_as_utc_not_local()
    {
        // LiteDB stores dates as UTC and returns them in local time. Not
        // cosmetic: the dedup key is the timestamp to the minute, so an
        // unconverted value makes Wavelog treat the same contact as a new one.
        var when = new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc);
        string id;
        using (var zeus = Zeus()) id = zeus.Log(when: when).Id;

        using var db = Attach();
        var found = db.ById(id)!;
        Assert.Equal(DateTimeKind.Utc, found.QsoDateTimeUtc.Kind);
        Assert.Equal(when, found.QsoDateTimeUtc);
    }

    [Fact]
    public void A_renamed_collection_is_reported_rather_than_silently_empty()
    {
        // The failure this class is most exposed to, and it is silent by
        // construction: LiteDB hands back an empty collection for any name.
        // The plugin then works perfectly and syncs nothing, forever.
        using (var wrong = new LiteDB.LiteDatabase(new LiteDB.ConnectionString
        {
            Filename = Path.Combine(_dir, ZeusLogbookDb.FileName),
            Connection = LiteDB.ConnectionType.Shared,
        }, new LiteDB.BsonMapper()))
        {
            wrong.GetCollection("somethingelse").Insert(new LiteDB.BsonDocument { ["x"] = 1 });
        }

        using var db = Attach();
        var problem = db.Verify();
        Assert.NotNull(problem);
        Assert.Contains("somethingelse", problem);
    }

    [Fact]
    public void An_empty_logbook_is_not_reported_as_a_problem()
    {
        // The operator simply has not logged anything yet. Crying wolf here
        // would train them to ignore the message that matters.
        using var db = Attach();
        Assert.Null(db.Verify());
    }

    [Fact]
    public void A_logbook_with_qsos_verifies_clean()
    {
        using (var zeus = Zeus()) zeus.Log();
        using var db = Attach();
        Assert.Null(db.Verify());
    }

    // ---- noticing new work --------------------------------------------------

    [Fact]
    public void A_newly_logged_qso_is_unseen_until_it_is_tracked()
    {
        // The host offers no "QSO logged" event, so new work is found by
        // absence: an entry with no row of ours has not been dealt with.
        using var zeus = Zeus();
        using var db = Attach();

        var logged = zeus.Log();
        Assert.Equal(logged.Id, Assert.Single(db.Unseen()).Id);

        db.Track(db.ById(logged.Id)!, QsoSource.Zeus);
        Assert.Empty(db.Unseen());
    }

    [Fact]
    public void Unseen_comes_back_in_qso_order()
    {
        // Uploads then arrive at Wavelog in the order they were made, which is
        // what an operator reading the log there expects.
        using var zeus = Zeus();
        zeus.Log("LATER", new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc));
        zeus.Log("EARLIER", new DateTime(2026, 8, 24, 6, 0, 0, DateTimeKind.Utc));

        using var db = Attach();
        Assert.Equal(["EARLIER", "LATER"], db.Unseen().Select(e => e.Callsign));
    }

    [Fact]
    public void What_we_have_tracked_survives_a_restart()
    {
        using var zeus = Zeus();
        var logged = zeus.Log();

        using (var db = Attach()) db.Track(db.ById(logged.Id)!, QsoSource.Zeus);
        using (var reopened = Attach()) Assert.Empty(reopened.Unseen());
    }

    // ---- not our document ---------------------------------------------------

    [Fact]
    public void Our_bookkeeping_never_joins_the_operators_qso()
    {
        // The stored document has to stay exactly what the reference stores.
        // Fields of ours would leak into ADIF exports through AdifFields, and a
        // round-trip through the reference's own code could silently drop them.
        using var zeus = Zeus();
        var logged = zeus.Log();

        using (var db = Attach())
        {
            db.Track(db.ById(logged.Id)!, QsoSource.Zeus);
            db.MarkPushed(logged.Id);
        }

        var raw = zeus.RawById(logged.Id);
        Assert.DoesNotContain("WavelogUploadedUtc", raw.Keys);
        Assert.DoesNotContain("DedupKey", raw.Keys);
        Assert.DoesNotContain("Source", raw.Keys);
        Assert.Contains(ZeusLogbookDb.SyncCollection, zeus.CollectionNames());
    }

    [Fact]
    public void Uninstalling_us_would_leave_the_log_intact()
    {
        // Stated as a test because it is the promise the reframe is built on:
        // everything we add lives in a collection the native logbook never
        // reads, so removing this plugin costs the operator nothing.
        using var zeus = Zeus();
        var logged = zeus.Log("PW1ABC");

        using (var db = Attach())
        {
            db.Track(db.ById(logged.Id)!, QsoSource.Zeus);
            db.MarkPushFailed(logged.Id, "401");
        }

        var still = zeus.ById(logged.Id)!;
        Assert.Equal("PW1ABC", still.Callsign);
        Assert.Equal("20m", still.Band);
        Assert.Equal(1, zeus.Count());
    }

    // ---- push bookkeeping ---------------------------------------------------

    [Fact]
    public void A_pushed_qso_stops_counting_as_pending()
    {
        using var zeus = Zeus();
        var logged = zeus.Log();
        using var db = Attach();

        db.Track(db.ById(logged.Id)!, QsoSource.Zeus);
        Assert.Equal(1, db.PendingCount());

        db.MarkPushed(logged.Id);
        Assert.Equal(0, db.PendingCount());
        Assert.NotNull(db.StateOf(logged.Id)!.WavelogUploadedUtc);
    }

    [Fact]
    public void A_failure_is_recorded_and_then_cleared_by_a_later_success()
    {
        using var zeus = Zeus();
        var logged = zeus.Log();
        using var db = Attach();
        db.Track(db.ById(logged.Id)!, QsoSource.Zeus);

        db.MarkPushFailed(logged.Id, "connection refused");
        Assert.Equal("connection refused", db.StateOf(logged.Id)!.WavelogError);

        db.MarkPushed(logged.Id);
        Assert.Null(db.StateOf(logged.Id)!.WavelogError);
    }

    [Fact]
    public void A_qso_that_came_from_wavelog_is_never_pending()
    {
        // The loop-prevention rule, at its source.
        using var db = Attach();
        db.InsertFromWavelog(new LogbookNewEntry("G4XYZ", null, 14.074, "20m", "USB", "59", "59"));
        Assert.Equal(0, db.PendingCount());
    }

    // ---- writing what wavelog told us --------------------------------------

    [Fact]
    public void An_inbound_qso_lands_in_zeus_own_logbook()
    {
        using var zeus = Zeus();
        using var db = Attach();

        var inserted = db.InsertFromWavelog(new LogbookNewEntry(
            "g4xyz", null, 7.074, "40m", "FT8", "-12", "-09",
            QsoDateTimeUtc: new DateTime(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc)));

        // Visible to the native plugin, because it is in the native plugin's
        // collection — that is the whole point of a synchroniser.
        var asZeusSeesIt = zeus.ById(inserted.Id)!;
        Assert.Equal("G4XYZ", asZeusSeesIt.Callsign);
        Assert.Equal("40m", asZeusSeesIt.Band);
        Assert.Equal(QsoSource.Wavelog, db.StateOf(inserted.Id)!.Source);
    }

    [Fact]
    public void An_import_skips_what_the_logbook_already_holds()
    {
        using var db = Attach();
        const string adif =
            "<CALL:6>DL1ABC<QSO_DATE:8>20260824<TIME_ON:6>090000<BAND:3>20m<MODE:3>SSB<EOR>";

        Assert.Equal((1, 0, 0), db.ImportFromWavelog(adif));
        Assert.Equal((0, 1, 0), db.ImportFromWavelog(adif));
        Assert.Equal(1, db.Count());
    }

    [Fact]
    public void An_import_reports_a_bad_record_without_abandoning_the_good_one()
    {
        using var db = Attach();
        var (imported, _, failed) = db.ImportFromWavelog(
            "<CALL:6>DL1ABC<QSO_DATE:8>20260824<TIME_ON:6>090000<BAND:3>20m<MODE:3>SSB<EOR>" +
            "<QSO_DATE:8>20260824<EOR>");                        // no callsign

        Assert.Equal(1, imported);
        Assert.Equal(1, failed);
    }

    // ---- confirmations ------------------------------------------------------

    [Fact]
    public void A_confirmation_updates_the_qso_zeus_logged()
    {
        using var zeus = Zeus();
        var logged = zeus.Log("DL1ABC", new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc), "20m", "SSB");

        using var db = Attach();
        db.Track(db.ById(logged.Id)!, QsoSource.Zeus);

        var updated = db.ApplyConfirmations(
            "<CALL:6>DL1ABC<QSO_DATE:8>20260824<TIME_ON:6>090000<BAND:3>20m<MODE:3>SSB" +
            "<QSL_RCVD:1>Y<QSLRDATE:8>20260901<EOR>");

        Assert.Equal(1, updated);
        var after = zeus.ById(logged.Id)!;
        Assert.Equal("Y", after.QslRcvd);
        // Read back through the plugin, which is where the local-time
        // correction lives; the raw value is whatever LiteDB felt like.
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                     db.ById(logged.Id)!.QslRcvdDate);
        Assert.NotNull(after.QslRcvdDate);
    }

    [Fact]
    public void A_confirmation_changes_nothing_but_the_confirmation()
    {
        // This is the one place we edit a contact somebody else created, so it
        // is deliberately the narrowest edit in the plugin.
        using var zeus = Zeus();
        var logged = zeus.Log("DL1ABC", new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc), "20m", "SSB");

        using var db = Attach();
        db.Track(db.ById(logged.Id)!, QsoSource.Zeus);
        db.ApplyConfirmations(
            "<CALL:6>DL1ABC<QSO_DATE:8>20260824<TIME_ON:6>090000<BAND:3>20m<MODE:3>SSB" +
            "<RST_SENT:2>33<COMMENT:8>not ours<LOTW_QSL_RCVD:1>Y<EOR>");

        var after = zeus.ById(logged.Id)!;
        Assert.Equal("59", after.RstSent);          // theirs, untouched
        Assert.Null(after.Comment);                 // theirs, untouched
        Assert.NotNull(after.LotwQslRcvdUtc);       // ours to carry
    }

    [Fact]
    public void A_confirmation_for_a_qso_we_do_not_have_is_ignored()
    {
        using var db = Attach();
        Assert.Equal(0, db.ApplyConfirmations(
            "<CALL:5>K1AAA<QSO_DATE:8>20260824<TIME_ON:6>090000<BAND:3>20m<MODE:3>SSB<EOR>"));
        Assert.Equal(0, db.Count());
    }

    [Fact]
    public void Malformed_adif_is_refused_rather_than_half_applied()
    {
        using var db = Attach();
        Assert.Equal(0, db.ApplyConfirmations("<CALL:99>DL1ABC<EOR>"));
        Assert.Equal((0, 0, 1), db.ImportFromWavelog("<CALL:99>DL1ABC<EOR>"));
    }

    // ---- reconciliation -----------------------------------------------------

    [Fact]
    public void Local_only_lists_what_zeus_has_and_wavelog_did_not_report()
    {
        using var zeus = Zeus();
        var mine = zeus.Log("MINE", new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc), "20m", "SSB");
        var known = zeus.Log("KNOWN", new DateTime(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc), "20m", "SSB");

        using var db = Attach();
        db.Track(db.ById(mine.Id)!, QsoSource.Zeus);
        var knownState = db.Track(db.ById(known.Id)!, QsoSource.Zeus);

        var gap = db.LocalOnly(new HashSet<string>([knownState.DedupKey], StringComparer.Ordinal));
        Assert.Equal("MINE", Assert.Single(gap).Callsign);
    }

    [Fact]
    public void Local_only_never_includes_what_came_from_wavelog()
    {
        // Otherwise a repair run pushes the entire imported log straight back at
        // the instance it came from.
        using var db = Attach();
        db.InsertFromWavelog(new LogbookNewEntry("G4XYZ", null, 14.074, "20m", "USB", "59", "59"));
        Assert.Empty(db.LocalOnly(new HashSet<string>(StringComparer.Ordinal)));
    }

    [Fact]
    public void The_dedup_key_is_the_identity_wavelog_deduplicates_on()
    {
        // Call, time to the minute, band, mode — matched to Wavelog's own SQL.
        // Seconds and casing must not make two of one contact.
        var a = SyncState.MakeDedupKey("dl1abc", new DateTime(2026, 8, 24, 9, 0, 30, DateTimeKind.Utc), "20M", "ssb");
        var b = SyncState.MakeDedupKey("DL1ABC", new DateTime(2026, 8, 24, 9, 0, 59, DateTimeKind.Utc), "20m", "SSB");
        Assert.Equal(a, b);

        var other = SyncState.MakeDedupKey("DL1ABC", new DateTime(2026, 8, 24, 9, 1, 0, DateTimeKind.Utc), "20m", "SSB");
        Assert.NotEqual(a, other);
    }
}
