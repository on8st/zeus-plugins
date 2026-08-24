// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugin.Wavelog.Sync;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>Time under the test's control, so backoff costs nothing to verify.</summary>
public sealed class FakeClock(DateTime start) : IClock
{
    public DateTime UtcNow { get; private set; } = start;
    public void Advance(TimeSpan by) => UtcNow += by;
}
