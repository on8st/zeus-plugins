// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using LiteDB;

namespace Zeus.Plugin.Wavelog.Sync;

/// <summary>
/// Where the pull cursor lives. Small, but it must be durable: losing it means
/// re-reading the whole log, and inventing a value means silently skipping QSOs.
/// </summary>
public sealed class LiteDbCursorStore : ICursorStore, IDisposable
{
    private sealed class Row { public string Id { get; set; } = "cursor"; public int FetchFromId { get; set; } }

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<Row> _rows;

    public LiteDbCursorStore(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // A mapper of our own, not BsonMapper.Global — see ZeusLogbookDb.NewMapper.
        _db = new LiteDatabase(
            new ConnectionString { Filename = path, Connection = ConnectionType.Direct },
            new BsonMapper());
        _rows = _db.GetCollection<Row>("cursor");
    }

    public int GetFetchFromId() => _rows.FindById("cursor")?.FetchFromId ?? 0;

    public void SetFetchFromId(int value)
    {
        var row = _rows.FindById("cursor") ?? new Row();
        // Never move backwards: a stale reply must not cause a re-import storm.
        if (value <= row.FetchFromId && row.FetchFromId != 0) return;
        row.FetchFromId = value;
        _rows.Upsert(row);
    }

    public void Dispose() => _db.Dispose();
}

/// <summary>In-memory cursor, for tests and for a dry run.</summary>
public sealed class MemoryCursorStore : ICursorStore
{
    private int _value;
    public int GetFetchFromId() => _value;
    public void SetFetchFromId(int value) { if (value > _value) _value = value; }
}
