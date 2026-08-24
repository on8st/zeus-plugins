// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
namespace Zeus.Plugin.Wavelog.Sync;

public interface IOutbox
{
    void Enqueue(string qsoId, string adif);
    OutboxItem? Lease(TimeSpan leaseFor);
    void Ack(string itemId);
    void Fail(string itemId, TimeSpan retryAfter, string error);
    void DeadLetter(string itemId, string error);
    int RequeueDeadLettered();
    int PendingCount { get; }
    int DeadLetterCount { get; }
    IReadOnlyList<OutboxItem> DeadLettered();
}
