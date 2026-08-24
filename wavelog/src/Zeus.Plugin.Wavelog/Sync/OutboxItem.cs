// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
namespace Zeus.Plugin.Wavelog.Sync;

/// <summary>One QSO waiting to reach Wavelog.</summary>
public sealed class OutboxItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The QSO in the local store this belongs to.</summary>
    public string QsoId { get; set; } = "";

    /// <summary>The ADIF record, rendered once at enqueue time.</summary>
    public string Adif { get; set; } = "";

    public DateTime EnqueuedUtc { get; set; }

    /// <summary>Not before this instant. Backoff is expressed by moving it forward.</summary>
    public DateTime VisibleFromUtc { get; set; }

    /// <summary>
    /// Set while an attempt is in flight. It is an expiry rather than a delete,
    /// which is what makes a crash mid-attempt recoverable: the lease simply
    /// runs out and the item becomes visible again.
    /// </summary>
    public DateTime? LeaseExpiresUtc { get; set; }

    public int Attempt { get; set; }
    public string? LastError { get; set; }
    public bool Dead { get; set; }
}
