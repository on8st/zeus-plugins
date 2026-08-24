// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
namespace Zeus.Plugin.Wavelog.Sync;

/// <summary>Time as a dependency, so backoff and lease expiry are testable without waiting.</summary>
public interface IClock { DateTime UtcNow { get; } }

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();
    public DateTime UtcNow => DateTime.UtcNow;
}
