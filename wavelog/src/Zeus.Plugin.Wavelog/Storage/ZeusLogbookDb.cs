// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using LiteDB;
using Zeus.Plugin.Wavelog.Adif;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Wavelog.Storage;

/// <summary>
/// A view onto <b>Zeus's own logbook</b>, not a logbook of our own.
///
/// <para>This plugin does not implement <c>ILogbookPluginV2</c> and does not
/// own the operator's QSOs. The native logbook — <c>org.openhpsdr.logbook</c> —
/// keeps doing that, along with browsing, editing, ADIF and QSL, all of which
/// are already correct. We attach to the same database and keep it in step with
/// Wavelog.</para>
///
/// <para><b>Everything here was verified before it was written:</b></para>
/// <list type="bullet">
/// <item>the file is <c>zeus-logbook.db</c> and the collection is
/// <c>entries</c> — both read from the reference plugin's GPL assembly;</item>
/// <item>the document is <see cref="LogbookEntrySnapshot"/>, the published
/// contract record, stored with LiteDB's default mapper. The reference defines
/// no storage type of its own, and a round-trip through the default mapper
/// reproduces the same document keys;</item>
/// <item>the reference opens with <c>Connection=shared</c>, which is the only
/// mode under which two handles on one file see each other's writes. Two
/// <c>Direct</c> handles open without error and then silently diverge, so this
/// class must never use anything else.</item>
/// </list>
///
/// <para>Our own bookkeeping lives in a separate collection the reference never
/// reads; see <see cref="SyncState"/> for why it must not join the document.</para>
/// </summary>
public sealed class ZeusLogbookDb : IDisposable
{
    /// <summary>The reference's file name, read from its GPL assembly.</summary>
    public const string FileName = "zeus-logbook.db";

    /// <summary>
    /// The reference's collection name — <c>logs</c>, confirmed by running the
    /// shipped v1.1.0 plugin in an isolated engine and reading the file it
    /// wrote.
    ///
    /// <para>This was <c>entries</c> for a while, taken from the assembly's
    /// string table. <c>entries</c> is the plugin's HTTP <em>route</em>. The
    /// mistake survived a full test suite because both sides of every test used
    /// the same wrong name, and it would have shipped as a plugin that attached
    /// to an empty collection and reported a healthy, permanently idle sync.
    /// Hence <see cref="Verify"/>.</para>
    /// </summary>
    public const string EntriesCollection = "logs";

    /// <summary>Ours. The native logbook never looks at it.</summary>
    public const string SyncCollection = "wavelog_sync";

    private readonly LiteDatabase _db;
    private readonly IReadOnlyList<string> _collectionNames;
    private readonly ILiteCollection<LogbookEntrySnapshot> _entries;
    private readonly ILiteCollection<SyncState> _sync;

    /// <summary>Attach to the logbook in a Zeus data directory.</summary>
    public static ZeusLogbookDb ForDataDirectory(string dataDirectory)
        => new(Path.Combine(dataDirectory, FileName));

    /// <summary>
    /// Is there a logbook here at all?
    ///
    /// <para>The Zeus logbook is a <em>plugin</em> — <c>org.openhpsdr.logbook</c>
    /// — not something the engine provides. With it uninstalled the engine
    /// creates no <c>zeus-logbook.db</c> and serves no logbook route; verified
    /// against a bare engine and against a live install.</para>
    ///
    /// <para>So the file's absence means the operator has no logbook backend,
    /// which is a different thing from an empty log and needs a different
    /// message. We must also not <em>create</em> it: LiteDB would happily make
    /// one, and then this looks like a logbook that simply has no QSOs in it —
    /// a contented, permanently idle sync with nothing to say for itself.</para>
    /// </summary>
    public static bool ExistsIn(string dataDirectory)
        => File.Exists(Path.Combine(dataDirectory, FileName));

    /// <summary>
    /// What to tell the operator when there is no logbook to attach to. Names the
    /// plugin, because "install the logbook" is not actionable and this is.
    /// </summary>
    public const string NoLogbookMessage =
        "no Zeus logbook found (" + FileName + " does not exist). The logbook is a " +
        "plugin, not part of the engine: install \"Zeus Logbook\" (org.openhpsdr.logbook) " +
        "from the plugin registry. Nothing will sync until it is there.";

    public ZeusLogbookDb(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Shared, always. Under Direct a second handle opens happily and then
        // never sees the other's writes — no error, just two divergent views of
        // the operator's log.
        _db = new LiteDatabase(new ConnectionString
        {
            Filename = path,
            Connection = ConnectionType.Shared,
        }, NewMapper());

        _entries = _db.GetCollection<LogbookEntrySnapshot>(EntriesCollection);
        _collectionNames = _db.GetCollectionNames().ToList();
        _sync = _db.GetCollection<SyncState>(SyncCollection);
        _sync.EnsureIndex(s => s.DedupKey);
        _sync.EnsureIndex(s => s.Source);
    }

    /// <summary>
    /// Check that we attached to something real, and say so loudly if not.
    ///
    /// <para>Getting the collection name wrong is the failure this class is most
    /// exposed to, and it is silent by construction: LiteDB happily hands back
    /// an empty collection for a name nothing ever wrote. The plugin then works
    /// perfectly and syncs nothing, forever.</para>
    ///
    /// <para>So on startup, ask the file what it actually contains. If our
    /// collection is missing while another one holds documents, that is a
    /// rename in the reference and the operator needs to be told — not left
    /// reading a contented log line.</para>
    /// </summary>
    public string? Verify()
    {
        if (_collectionNames.Contains(EntriesCollection, StringComparer.Ordinal))
            return null;

        var others = _collectionNames
            .Where(n => !string.Equals(n, SyncCollection, StringComparison.Ordinal))
            .ToList();

        return others.Count == 0
            ? null      // an empty logbook is not an error; the operator has not logged yet
            : $"the logbook has no '{EntriesCollection}' collection — it has [{string.Join(", ", others)}]. " +
              "The reference logbook plugin has probably renamed it; this sync will do nothing until that is corrected.";
    }

    // ---- reading the native log --------------------------------------------

    public int Count() => _entries.Count();

    public IReadOnlyList<LogbookEntrySnapshot> All()
        => _entries.FindAll().Select(Normalise).ToList();

    public LogbookEntrySnapshot? ById(string id)
    {
        var e = _entries.FindById(id);
        return e is null ? null : Normalise(e);
    }

    /// <summary>
    /// QSOs the native logbook holds that we have not yet accounted for.
    ///
    /// <para>There is no notification when the logbook inserts a contact — the
    /// host offers plugins no such event — so new work is found by absence: an
    /// entry with no row in our own collection has not been seen before. Polling
    /// is not a compromise here, it is the only mechanism available.</para>
    /// </summary>
    public IReadOnlyList<LogbookEntrySnapshot> Unseen()
    {
        var known = _sync.FindAll().Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        return _entries.FindAll()
            .Where(e => !known.Contains(e.Id))
            .Select(Normalise)
            .OrderBy(e => e.QsoDateTimeUtc)
            .ToList();
    }

    // ---- our own bookkeeping ------------------------------------------------

    public SyncState? StateOf(string id) => _sync.FindById(id);

    /// <summary>Record that we have seen a QSO, and where it came from.</summary>
    public SyncState Track(LogbookEntrySnapshot e, string source)
    {
        var state = new SyncState
        {
            Id = e.Id,
            Source = source,
            DedupKey = SyncState.MakeDedupKey(e.Callsign, Utc(e.QsoDateTimeUtc), e.Band, e.Mode),
        };
        _sync.Upsert(state);
        return state;
    }

    public void MarkPushed(string id)
    {
        var s = _sync.FindById(id) ?? new SyncState { Id = id };
        s.WavelogUploadedUtc = DateTime.UtcNow;
        s.WavelogError = null;
        _sync.Upsert(s);
    }

    public void MarkPushFailed(string id, string error)
    {
        var s = _sync.FindById(id) ?? new SyncState { Id = id };
        s.WavelogError = error;
        _sync.Upsert(s);
    }

    public bool HasDedupKey(string key) => _sync.Exists(s => s.DedupKey == key);

    public int PendingCount() => _sync.Count(s => s.WavelogUploadedUtc == null && s.Source != QsoSource.Wavelog);

    // ---- writing what Wavelog told us --------------------------------------

    /// <summary>
    /// Insert a QSO that came from Wavelog into the native logbook.
    ///
    /// <para>Marked as inbound so it is never pushed back — the loop-prevention
    /// rule. Writing into a collection another plugin owns is deliberate and
    /// safe only because both sides use shared mode and the document is the
    /// contract record.</para>
    /// </summary>
    public LogbookEntrySnapshot InsertFromWavelog(LogbookNewEntry e)
    {
        var when = Utc(e.QsoDateTimeUtc ?? DateTime.UtcNow);
        var entry = new LogbookEntrySnapshot(
            Id: Guid.NewGuid().ToString("N"),
            QsoDateTimeUtc: when,
            Callsign: e.Callsign.Trim().ToUpperInvariant(),
            Name: e.Name,
            FrequencyMhz: e.FrequencyMhz,
            Band: e.Band,
            Mode: e.Mode,
            RstSent: e.RstSent,
            RstRcvd: e.RstRcvd,
            Grid: e.Grid,
            Country: e.Country,
            Dxcc: e.Dxcc,
            CqZone: e.CqZone,
            ItuZone: e.ItuZone,
            State: e.State,
            Comment: e.Comment,
            CreatedUtc: DateTime.UtcNow,
            AdifFields: e.AdifFields is null ? null : new Dictionary<string, string>(e.AdifFields));

        _entries.Insert(entry);
        Track(entry, QsoSource.Wavelog);
        return entry;
    }

    /// <summary>
    /// Import ADIF that came from Wavelog, skipping anything already held.
    /// Returns how many were inserted and how many were already there.
    /// </summary>
    public (int Imported, int Duplicates, int Failed) ImportFromWavelog(string adifText)
    {
        int imported = 0, duplicates = 0, failed = 0;
        IReadOnlyList<IReadOnlyDictionary<string, string>> records;
        try { records = AdifParser.Parse(adifText); } catch (AdifFormatException) { return (0, 0, 1); }

        foreach (var record in records)
        {
            try
            {
                var entry = AdifImport.ToNewEntry(record);
                var key = SyncState.MakeDedupKey(
                    entry.Callsign, Utc(entry.QsoDateTimeUtc ?? DateTime.UtcNow), entry.Band, entry.Mode);
                if (HasDedupKey(key)) { duplicates++; continue; }
                InsertFromWavelog(entry);
                imported++;
            }
            catch (Exception) { failed++; }
        }
        return (imported, duplicates, failed);
    }

    /// <summary>
    /// Apply QSL and LoTW status from Wavelog onto QSOs the logbook already
    /// holds, matched on the dedup key.
    ///
    /// <para>Confirmation fields are Wavelog's to own — it is where they arrive
    /// — so nothing else about the contact is touched. This is the one place we
    /// modify a QSO the operator or the native logbook created, and it is
    /// deliberately the narrowest possible edit.</para>
    /// </summary>
    public int ApplyConfirmations(string adifText)
    {
        var updated = 0;
        IReadOnlyList<IReadOnlyDictionary<string, string>> records;
        try { records = AdifParser.Parse(adifText); } catch (AdifFormatException) { return 0; }

        foreach (var record in records)
        {
            var key = DedupKeyOf(record);
            if (key is null) continue;
            var state = _sync.FindOne(s => s.DedupKey == key);
            if (state is null) continue;
            var e = _entries.FindById(state.Id);
            if (e is null) continue;

            var next = e with
            {
                QslRcvd = Str(record, "QSL_RCVD") ?? e.QslRcvd,
                QslSent = Str(record, "QSL_SENT") ?? e.QslSent,
                QslRcvdDate = Date(record, "QSLRDATE") ?? e.QslRcvdDate,
                QslSentDate = Date(record, "QSLSDATE") ?? e.QslSentDate,
                LotwQslSentUtc = Date(record, "LOTW_QSLSDATE") ?? e.LotwQslSentUtc,
                LotwQslRcvdUtc = Date(record, "LOTW_QSLRDATE")
                                 ?? (Yes(record, "LOTW_QSL_RCVD")
                                     ? e.LotwQslRcvdUtc ?? DateTime.UtcNow
                                     : e.LotwQslRcvdUtc),
            };

            if (next == e) continue;
            _entries.Update(next);
            updated++;
        }
        return updated;
    }

    // ---- reconciliation -----------------------------------------------------

    public string? DedupKeyOf(IReadOnlyDictionary<string, string> record)
    {
        try
        {
            var e = AdifImport.ToNewEntry(record);
            return SyncState.MakeDedupKey(
                e.Callsign, Utc(e.QsoDateTimeUtc ?? DateTime.UtcNow), e.Band, e.Mode);
        }
        catch (AdifFormatException) { return null; }
    }

    /// <summary>
    /// QSOs the logbook holds that Wavelog did not report. Entries that came
    /// <em>from</em> Wavelog are excluded: pushing those back is the loop this
    /// design exists to prevent.
    ///
    /// <para>Two details that are easy to get wrong. It starts from the
    /// <em>entries</em>, not from our own rows, so a contact we have never
    /// tracked still counts as local-only — otherwise a dry run over a log the
    /// plugin has not scanned yet would report no gap at all and the operator
    /// would be told everything was fine. And the key is recomputed from the
    /// entry rather than read from our row, so a QSO the operator corrected
    /// after we first saw it is compared as it stands now.</para>
    /// </summary>
    public IReadOnlyList<LogbookEntrySnapshot> LocalOnly(IReadOnlySet<string> wavelogKeys)
    {
        var inbound = _sync.Find(s => s.Source == QsoSource.Wavelog)
            .Select(s => s.Id)
            .ToHashSet(StringComparer.Ordinal);

        return _entries.FindAll()
            .Where(e => !inbound.Contains(e.Id))
            .Select(Normalise)
            .Where(e => !wavelogKeys.Contains(
                SyncState.MakeDedupKey(e.Callsign, e.QsoDateTimeUtc, e.Band, e.Mode)))
            .ToList();
    }

    // ---- helpers ------------------------------------------------------------

    private static string? Str(IReadOnlyDictionary<string, string> r, string k)
        => r.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static bool Yes(IReadOnlyDictionary<string, string> r, string k)
        => string.Equals(Str(r, k), "Y", StringComparison.OrdinalIgnoreCase);

    private static DateTime? Date(IReadOnlyDictionary<string, string> r, string k)
        => Str(r, k) is { } s && DateTime.TryParseExact(s, "yyyyMMdd",
               System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.AssumeUniversal |
               System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    /// <summary>
    /// LiteDB stores dates as UTC and hands them back in local time. For a
    /// logbook that is not cosmetic: the dedup key is the timestamp to the
    /// minute, so an unconverted value would make Wavelog treat the same contact
    /// as a new one.
    /// </summary>
    internal static DateTime Utc(DateTime v) => v.Kind switch
    {
        DateTimeKind.Utc => v,
        DateTimeKind.Local => v.ToUniversalTime(),
        _ => DateTime.SpecifyKind(v, DateTimeKind.Utc),
    };

    private static DateTime? Utc(DateTime? v) => v is null ? null : Utc(v.Value);

    private static LogbookEntrySnapshot Normalise(LogbookEntrySnapshot e) => e with
    {
        QsoDateTimeUtc = Utc(e.QsoDateTimeUtc),
        CreatedUtc = Utc(e.CreatedUtc),
        QrzUploadedUtc = Utc(e.QrzUploadedUtc),
        QslSentDate = Utc(e.QslSentDate),
        QslRcvdDate = Utc(e.QslRcvdDate),
        LotwQslSentUtc = Utc(e.LotwQslSentUtc),
        LotwQslRcvdUtc = Utc(e.LotwQslRcvdUtc),
        QrzQslRcvdUtc = Utc(e.QrzQslRcvdUtc),
    };

    /// <summary>
    /// A mapper of our own rather than <c>BsonMapper.Global</c>.
    ///
    /// <para>The global one is process-wide mutable state with a cache that is
    /// not safe to populate from several threads at once — two databases opened
    /// concurrently can hand back a half-built entity mapping, which surfaces
    /// much later as "member not found" on a field that plainly exists. A fresh
    /// mapper has identical defaults, so the stored document is byte-for-byte
    /// what the reference writes; it just cannot be raced or reconfigured by
    /// anything else sharing the load context.</para>
    /// </summary>
    private static BsonMapper NewMapper() => new();

    public void Dispose() => _db.Dispose();
}
