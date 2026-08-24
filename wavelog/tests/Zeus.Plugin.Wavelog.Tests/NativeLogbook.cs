// SPDX-License-Identifier: GPL-2.0-or-later
using LiteDB;
using Zeus.Plugin.Wavelog.Storage;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>
/// A stand-in for Zeus's own logbook plugin.
///
/// <para>The synchroniser does not own the log, so a test that reaches for the
/// synchroniser to create a QSO would be testing a path that does not exist in
/// the product. This writes contacts the way the native plugin does — its own
/// <c>LiteDatabase</c> handle, opened shared, on <c>zeus-logbook.db</c>, storing
/// the contract record in the <c>entries</c> collection with the default mapper
/// — so every test below starts from a log the plugin genuinely did not
/// create.</para>
///
/// <para>Keeping it a separate handle is deliberate. It is the only way the
/// suite can hold the claim the whole design rests on: two shared handles on
/// one file see each other's writes.</para>
/// </summary>
public sealed class NativeLogbook : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<LogbookEntrySnapshot> _entries;

    public NativeLogbook(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _db = new LiteDatabase(new ConnectionString
        {
            Filename = path,
            Connection = ConnectionType.Shared,
        }, new BsonMapper());
        _entries = _db.GetCollection<LogbookEntrySnapshot>(ZeusLogbookDb.EntriesCollection);
    }

    public static NativeLogbook InDataDirectory(string dataDirectory)
        => new(Path.Combine(dataDirectory, ZeusLogbookDb.FileName));

    /// <summary>The operator logs a contact in Zeus.</summary>
    public LogbookEntrySnapshot Log(
        string callsign = "DL1ABC",
        DateTime? when = null,
        string band = "20m",
        string mode = "USB",
        double? frequencyMhz = 14.074)
    {
        var entry = new LogbookEntrySnapshot(
            Id: Guid.NewGuid().ToString("N"),
            QsoDateTimeUtc: when ?? new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc),
            Callsign: callsign.ToUpperInvariant(),
            Name: null,
            FrequencyMhz: frequencyMhz,
            Band: band,
            Mode: mode,
            RstSent: "59",
            RstRcvd: "57",
            Grid: null, Country: null, Dxcc: null, CqZone: null, ItuZone: null,
            State: null, Comment: null,
            CreatedUtc: DateTime.UtcNow);
        _entries.Insert(entry);
        return entry;
    }

    public int Count() => _entries.Count();

    public LogbookEntrySnapshot? ById(string id) => _entries.FindById(id);

    /// <summary>The raw stored document, for checking what we did and did not add to it.</summary>
    public BsonDocument RawById(string id)
        => _db.GetCollection(ZeusLogbookDb.EntriesCollection).FindById(id);

    public IReadOnlyList<string> CollectionNames() => _db.GetCollectionNames().ToList();

    public void Dispose() => _db.Dispose();
}
