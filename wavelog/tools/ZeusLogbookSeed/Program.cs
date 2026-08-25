// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// Writes a QSO into a Zeus logbook, standing in for Zeus itself.
//
// The harness needs a logbook that something *other than the synchroniser*
// created, or it proves nothing about attaching to somebody else's database.
// Zeus Link's logbook is proprietary and cannot be driven headlessly, so this
// reproduces what it writes: a LogbookEntrySnapshot in the `logs` collection of
// a zeus-logbook.db, opened shared, using LiteDB's default mapper.
//
// That shape is not guessed. It was read out of a real product logbook:
//   {"_id":"…","QsoDateTimeUtc":{"$date":…},"Callsign":"ON0TEST",
//    "FrequencyMhz":3.727,"Band":"80m","Mode":"SSB","RstSent":"59",
//    "RstRcvd":"59","CreatedUtc":{"$date":…}}
//
// Usage: ZeusLogbookSeed <db-path> [callsign] [band] [mode] [iso-utc]
using LiteDB;
using Zeus.Plugins.Contracts.Extensions;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: ZeusLogbookSeed <db-path> [callsign] [band] [mode] [iso-utc]");
    return 2;
}

var path = args[0];
var callsign = (args.Length > 1 ? args[1] : "ON0SEED").ToUpperInvariant();
var band = args.Length > 2 ? args[2] : "20m";
var mode = args.Length > 3 ? args[3] : "USB";
var when = args.Length > 4
    ? DateTime.Parse(args[4], null, System.Globalization.DateTimeStyles.AdjustToUniversal |
                                   System.Globalization.DateTimeStyles.AssumeUniversal)
    : new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

var dir = Path.GetDirectoryName(path);
if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

// Shared, and a mapper of our own — BsonMapper.Global is process-wide mutable
// state that is not safe to populate concurrently.
using var db = new LiteDatabase(
    new ConnectionString { Filename = path, Connection = ConnectionType.Shared },
    new BsonMapper());

var entry = new LogbookEntrySnapshot(
    Id: Guid.NewGuid().ToString(),
    QsoDateTimeUtc: when,
    Callsign: callsign,
    Name: null,
    FrequencyMhz: 14.074,
    Band: band,
    Mode: mode,
    RstSent: "59",
    RstRcvd: "57",
    Grid: null, Country: null, Dxcc: null, CqZone: null, ItuZone: null,
    State: null, Comment: null,
    CreatedUtc: DateTime.UtcNow);

db.GetCollection<LogbookEntrySnapshot>("logs").Insert(entry);

Console.WriteLine($"{callsign} -> {path}");
return 0;
