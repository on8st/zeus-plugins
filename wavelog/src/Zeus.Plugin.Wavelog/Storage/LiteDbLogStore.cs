// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using System.Globalization;
using System.Text;
using LiteDB;
using Zeus.Plugin.Wavelog.Adif;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Wavelog.Storage;

/// <summary>
/// The logbook itself.
///
/// <para>The contract has no search, sort or filter parameter — the only
/// listing method is <c>GetEntriesAsync(skip, take)</c> — so the client filters
/// client-side over what it has pulled. That means this store needs to serve
/// bulk reads quickly and needs no query language; it does need its indexes,
/// because browsing is on the operator's path.</para>
/// </summary>
public sealed class LiteDbLogStore : ILogStore, IDisposable
{
    private const string Collection = "qsos";
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<StoredQso> _qsos;

    public LiteDbLogStore(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _db = new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Direct });
        _qsos = _db.GetCollection<StoredQso>(Collection);
        _qsos.EnsureIndex(q => q.QsoDateTimeUtc);
        _qsos.EnsureIndex(q => q.CallsignKey);
        _qsos.EnsureIndex(q => q.DedupKey, unique: false);
        _qsos.EnsureIndex(q => q.WavelogUploadedUtc);
    }

    // ---- create / read ------------------------------------------------------

    public Task<LogbookEntrySnapshot> CreateAsync(LogbookNewEntry entry, CancellationToken ct = default)
        => Task.FromResult(Insert(entry, QsoSource.Zeus).ToSnapshot());

    /// <summary>
    /// Used by the pull: identical to <see cref="CreateAsync"/> except that the
    /// row is marked as having come from Wavelog, which is what keeps it out of
    /// the outbox and stops a resync pushing the whole log back.
    /// </summary>
    public Task<LogbookEntrySnapshot> CreateFromWavelogAsync(LogbookNewEntry entry, CancellationToken ct = default)
        => Task.FromResult(Insert(entry, QsoSource.Wavelog).ToSnapshot());

    private StoredQso Insert(LogbookNewEntry e, string source)
    {
        var when = Normalise(e.QsoDateTimeUtc ?? DateTime.UtcNow);
        var row = new StoredQso
        {
            QsoDateTimeUtc = when,
            Callsign = e.Callsign.Trim(),
            CallsignKey = e.Callsign.Trim().ToUpperInvariant(),
            Name = e.Name,
            FrequencyMhz = e.FrequencyMhz,
            Band = e.Band,
            Mode = e.Mode,
            RstSent = e.RstSent,
            RstRcvd = e.RstRcvd,
            Grid = e.Grid,
            Country = e.Country,
            Dxcc = e.Dxcc,
            CqZone = e.CqZone,
            ItuZone = e.ItuZone,
            State = e.State,
            Comment = e.Comment,
            AdifFields = e.AdifFields is null ? null : new Dictionary<string, string>(e.AdifFields),
            Source = source,
            DedupKey = StoredQso.MakeDedupKey(e.Callsign, when, e.Band, e.Mode),
        };
        _qsos.Insert(row);
        return row;
    }

    public Task<LogbookPage> GetEntriesAsync(int skip, int take, CancellationToken ct = default)
    {
        var total = _qsos.Count();
        var page = _qsos.Query()
            .OrderByDescending(q => q.QsoDateTimeUtc)
            .Skip(Math.Max(0, skip))
            .Limit(Math.Max(0, take))
            .ToList()
            .Select(q => q.ToSnapshot())
            .ToList();
        return Task.FromResult(new LogbookPage(page, total));
    }

    public Task<IReadOnlyList<LogbookEntrySnapshot>> GetByIdsAsync(
        IEnumerable<string> ids, CancellationToken ct = default)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<LogbookEntrySnapshot> found = _qsos.Find(q => wanted.Contains(q.Id))
            .Select(q => q.ToSnapshot()).ToList();
        return Task.FromResult(found);
    }

    public Task<LogbookWorkedSummary?> GetWorkedSummaryAsync(
        string callsign, int recentTake, CancellationToken ct = default)
    {
        var key = callsign.Trim().ToUpperInvariant();
        var all = _qsos.Find(q => q.CallsignKey == key)
            .OrderByDescending(q => q.QsoDateTimeUtc)
            .ToList();

        if (all.Count == 0)
            return Task.FromResult<LogbookWorkedSummary?>(new LogbookWorkedSummary(
                callsign, false, 0, null, null, null, null, null, null, null, null,
                null, null, null, [], [], []));

        var last = all[0];
        var summary = new LogbookWorkedSummary(
            Callsign: last.Callsign,
            WorkedBefore: true,
            TotalCount: all.Count,
            LastWorkedUtc: last.QsoDateTimeUtc,
            LastBand: last.Band,
            LastMode: last.Mode,
            LastFrequencyMhz: last.FrequencyMhz,
            LastRstSent: last.RstSent,
            LastRstRcvd: last.RstRcvd,
            LastName: last.Name,
            LastGrid: last.Grid,
            LastCountry: last.Country,
            LastState: last.State,
            LastComment: last.Comment,
            Bands: all.Select(q => q.Band).Where(b => !string.IsNullOrWhiteSpace(b))
                      .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Modes: all.Select(q => q.Mode).Where(m => !string.IsNullOrWhiteSpace(m))
                      .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RecentQsos: all.Take(Math.Max(0, recentTake)).Select(q => new LogbookWorkedRecentQso(
                q.QsoDateTimeUtc, q.Band, q.Mode, q.FrequencyMhz, q.RstSent, q.RstRcvd,
                q.Name, q.Grid, q.Country, q.State, q.Comment, q.QrzLogId)).ToList());

        return Task.FromResult<LogbookWorkedSummary?>(summary);
    }

    private static readonly string[] DigitalModes =
        ["FT8", "FT4", "JT65", "JT9", "RTTY", "PSK31", "PSK", "MFSK", "OLIVIA", "JS8", "DIGU", "DIGL", "DATA"];

    public Task<IReadOnlyList<string>> GetDigitalWorkedCallsignsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<string> calls = _qsos.FindAll()
            .Where(q => DigitalModes.Contains(q.Mode?.Trim().ToUpperInvariant()))
            .Select(q => q.CallsignKey)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(calls);
    }

    public Task<IReadOnlyList<string>> GetAllTagsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<string> tags = _qsos.FindAll()
            .Where(q => q.Tags is { Count: > 0 })
            .SelectMany(q => q.Tags!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(tags);
    }

    // ---- update / delete ----------------------------------------------------

    public Task<LogbookEntrySnapshot?> UpdateAsync(
        string id, LogbookEntryUpdate u, CancellationToken ct = default)
    {
        var row = _qsos.FindById(id);
        if (row is null) return Task.FromResult<LogbookEntrySnapshot?>(null);

        // Only what is given changes. The Clear* flags exist because null means
        // "leave alone" everywhere else, so there has to be a way to say "empty".
        if (u.Name is not null) row.Name = u.Name;
        if (u.Grid is not null) row.Grid = u.Grid;
        if (u.Country is not null) row.Country = u.Country;
        if (u.State is not null) row.State = u.State;
        if (u.Comment is not null) row.Comment = u.Comment;
        if (u.Tags is not null) row.Tags = u.Tags.ToList();
        if (u.QslSent is not null) row.QslSent = u.QslSent;
        if (u.QslRcvd is not null) row.QslRcvd = u.QslRcvd;
        if (u.QslSentDate is not null) row.QslSentDate = u.QslSentDate;
        if (u.QslRcvdDate is not null) row.QslRcvdDate = u.QslRcvdDate;
        if (u.Rig is not null) row.Rig = u.Rig;
        if (u.Antenna is not null) row.Antenna = u.Antenna;
        if (u.TxPowerW is not null) row.TxPowerW = u.TxPowerW;
        if (u.RstSent is not null) row.RstSent = u.RstSent;
        if (u.RstRcvd is not null) row.RstRcvd = u.RstRcvd;
        if (u.Mode is not null) row.Mode = u.Mode;
        if (u.Band is not null) row.Band = u.Band;
        if (u.FrequencyMhz is not null) row.FrequencyMhz = u.FrequencyMhz;
        if (u.QsoDateTimeUtc is not null) row.QsoDateTimeUtc = Normalise(u.QsoDateTimeUtc.Value);

        if (u.ClearQslSentDate) row.QslSentDate = null;
        if (u.ClearQslRcvdDate) row.QslRcvdDate = null;
        if (u.ClearTxPowerW) row.TxPowerW = null;
        if (u.ClearFrequencyMhz) row.FrequencyMhz = null;

        row.DedupKey = StoredQso.MakeDedupKey(row.Callsign, row.QsoDateTimeUtc, row.Band, row.Mode);
        _qsos.Update(row);
        return Task.FromResult<LogbookEntrySnapshot?>(row.ToSnapshot());
    }

    public Task<int> UpdateQslStatusAsync(
        IReadOnlyList<LogbookQslStatusUpdate> updates, CancellationToken ct = default)
    {
        var n = 0;
        foreach (var u in updates)
        {
            var row = _qsos.FindById(u.Id);
            if (row is null) continue;
            if (u.LotwQslRcvdUtc is not null) row.LotwQslRcvdUtc = u.LotwQslRcvdUtc;
            if (u.LotwQslSentUtc is not null) row.LotwQslSentUtc = u.LotwQslSentUtc;
            if (u.QrzQslRcvdUtc is not null) row.QrzQslRcvdUtc = u.QrzQslRcvdUtc;
            if (u.QslRcvd is not null) row.QslRcvd = u.QslRcvd;
            if (u.QslRcvdDate is not null) row.QslRcvdDate = u.QslRcvdDate;
            _qsos.Update(row);
            n++;
        }
        return Task.FromResult(n);
    }

    public Task<bool> UpdateQrzUploadStatusAsync(
        string id, string qrzLogId, CancellationToken ct = default)
    {
        var row = _qsos.FindById(id);
        if (row is null) return Task.FromResult(false);
        row.QrzLogId = qrzLogId;
        row.QrzUploadedUtc = DateTime.UtcNow;
        _qsos.Update(row);
        return Task.FromResult(true);
    }

    public Task<int> DeleteAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var n = ids.Count(id => _qsos.Delete(id));
        return Task.FromResult(n);
    }

    // ---- adif ---------------------------------------------------------------

    public Task<string> ExportAdifAsync(IEnumerable<string>? ids = null, CancellationToken ct = default)
    {
        List<StoredQso> rows;
        if (ids is null)
        {
            rows = _qsos.Query().OrderBy(q => q.QsoDateTimeUtc).ToList();
        }
        else
        {
            // Materialise before querying: LiteDB translates the predicate into
            // its own expression language and cannot call ToHashSet in there.
            var wanted = ids.ToHashSet(StringComparer.Ordinal);
            rows = _qsos.FindAll().Where(q => wanted.Contains(q.Id))
                        .OrderBy(q => q.QsoDateTimeUtc).ToList();
        }

        var sb = new StringBuilder();
        sb.Append("Exported by Zeus Wavelog plugin\n")
          .Append("<PROGRAMID:20>zeus-wavelog-plugin")
          .Append("<ADIF_VER:5>3.1.4")
          .Append("<EOH>\n");
        foreach (var row in rows) sb.Append(AdifMapper.ToRecord(row.ToSnapshot())).Append('\n');
        return Task.FromResult(sb.ToString());
    }

    public async Task<LogbookExportFileResult> ExportAdifToFileAsync(
        string? directory = null, IEnumerable<string>? ids = null, CancellationToken ct = default)
    {
        var dir = directory ?? Path.GetTempPath();
        Directory.CreateDirectory(dir);
        var idList = ids?.ToList();
        var adif = await ExportAdifAsync(idList, ct).ConfigureAwait(false);
        var path = Path.Combine(dir,
            $"zeus-log-{DateTime.UtcNow:yyyyMMdd-HHmmss}.adi");
        await File.WriteAllTextAsync(path, adif, ct).ConfigureAwait(false);
        var count = idList?.Count ?? _qsos.Count();
        return new LogbookExportFileResult(path, count, new FileInfo(path).Length);
    }

    public Task<LogbookImportResult> ImportAdifAsync(string adifText, CancellationToken ct = default)
        => Task.FromResult(Import(adifText, QsoSource.Zeus));

    /// <summary>
    /// Import marked as coming from Wavelog. Rows written this way are excluded
    /// from the outbox — see <see cref="StoredQso.Source"/>.
    /// </summary>
    public Task<LogbookImportResult> ImportFromWavelogAsync(string adifText, CancellationToken ct = default)
        => Task.FromResult(Import(adifText, QsoSource.Wavelog));

    private LogbookImportResult Import(string adifText, string source)
    {
        var errors = new List<LogbookImportError>();
        int imported = 0, duplicates = 0, skipped = 0, total = 0;

        IReadOnlyList<IReadOnlyDictionary<string, string>> records;
        try { records = AdifParser.Parse(adifText); }
        catch (AdifFormatException ex)
        {
            return new LogbookImportResult(0, 0, 0, 0, [new LogbookImportError(0, ex.Message)]);
        }

        foreach (var record in records)
        {
            total++;
            try
            {
                var entry = AdifImport.ToNewEntry(record);
                var when = Normalise(entry.QsoDateTimeUtc ?? DateTime.UtcNow);
                var key = StoredQso.MakeDedupKey(entry.Callsign, when, entry.Band, entry.Mode);

                if (_qsos.Exists(q => q.DedupKey == key)) { duplicates++; continue; }

                Insert(entry, source);
                imported++;
            }
            catch (AdifFormatException ex) { errors.Add(new LogbookImportError(total, ex.Message)); skipped++; }
            catch (Exception ex) { errors.Add(new LogbookImportError(total, ex.Message)); skipped++; }
        }

        return new LogbookImportResult(total, imported, duplicates, skipped, errors);
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>Everything is stored as UTC; a local value is converted, not relabelled.</summary>
    private static DateTime Normalise(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    public void Dispose() => _db.Dispose();
}
