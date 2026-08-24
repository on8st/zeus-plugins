// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using LiteDB;

namespace Zeus.Plugin.Wavelog.Sync;

/// <summary>
/// A durable at-least-once queue.
///
/// <para>At-least-once is safe here because Wavelog deduplicates on insert —
/// callsign, time to the minute, band, mode, station — so a redelivery of an
/// attempt that actually landed is silently skipped on its side. That is what
/// lets this queue prefer "send twice" over "might lose one".</para>
/// </summary>
public sealed class LiteDbOutbox : IOutbox, IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<OutboxItem> _items;
    private readonly IClock _clock;
    private readonly object _gate = new();

    public LiteDbOutbox(string path, IClock? clock = null)
    {
        _clock = clock ?? SystemClock.Instance;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _db = new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Direct });
        _items = _db.GetCollection<OutboxItem>("outbox");
        _items.EnsureIndex(i => i.QsoId);
        _items.EnsureIndex(i => i.VisibleFromUtc);
        _items.EnsureIndex(i => i.Dead);
    }

    public void Enqueue(string qsoId, string adif)
    {
        lock (_gate)
        {
            // One row per QSO: a resync or a retry must not multiply the queue.
            if (_items.Exists(i => i.QsoId == qsoId && !i.Dead)) return;

            var now = _clock.UtcNow;
            _items.Insert(new OutboxItem
            {
                QsoId = qsoId,
                Adif = adif,
                EnqueuedUtc = now,
                VisibleFromUtc = now,
            });
        }
    }

    public OutboxItem? Lease(TimeSpan leaseFor)
    {
        lock (_gate)
        {
            var now = _clock.UtcNow;
            var next = _items.Find(i => !i.Dead)
                .Where(i => Utc(i.VisibleFromUtc) <= now)
                .Where(i => i.LeaseExpiresUtc is null || Utc(i.LeaseExpiresUtc.Value) <= now)
                .OrderBy(i => Utc(i.EnqueuedUtc))
                .FirstOrDefault();

            if (next is null) return null;

            next.LeaseExpiresUtc = now + leaseFor;
            next.Attempt++;
            _items.Update(next);
            return next;
        }
    }

    public void Ack(string itemId)
    {
        lock (_gate) _items.Delete(itemId);
    }

    public void Fail(string itemId, TimeSpan retryAfter, string error)
    {
        lock (_gate)
        {
            var item = _items.FindById(itemId);
            if (item is null) return;
            item.LeaseExpiresUtc = null;
            item.VisibleFromUtc = _clock.UtcNow + retryAfter;
            item.LastError = error;
            _items.Update(item);
        }
    }

    public void DeadLetter(string itemId, string error)
    {
        lock (_gate)
        {
            var item = _items.FindById(itemId);
            if (item is null) return;
            item.Dead = true;
            item.LeaseExpiresUtc = null;
            item.LastError = error;
            _items.Update(item);
        }
    }

    /// <summary>
    /// Put the dead letters back, attempts reset. Used after the operator fixes
    /// the cause — almost always a wrong key or station profile.
    /// </summary>
    public int RequeueDeadLettered()
    {
        lock (_gate)
        {
            var dead = _items.Find(i => i.Dead).ToList();
            foreach (var item in dead)
            {
                item.Dead = false;
                item.Attempt = 0;
                item.LeaseExpiresUtc = null;
                item.VisibleFromUtc = _clock.UtcNow;
                _items.Update(item);
            }
            return dead.Count;
        }
    }

    public int PendingCount { get { lock (_gate) return _items.Count(i => !i.Dead); } }
    public int DeadLetterCount { get { lock (_gate) return _items.Count(i => i.Dead); } }

    public IReadOnlyList<OutboxItem> DeadLettered()
    {
        lock (_gate) return _items.Find(i => i.Dead).ToList();
    }

    /// <summary>LiteDB returns dates in local time; see StoredQso for why that matters.</summary>
    private static DateTime Utc(DateTime v) => v.Kind switch
    {
        DateTimeKind.Utc => v,
        DateTimeKind.Local => v.ToUniversalTime(),
        _ => DateTime.SpecifyKind(v, DateTimeKind.Utc),
    };

    public void Dispose() => _db.Dispose();
}
