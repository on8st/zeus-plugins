// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st

namespace Zeus.Plugin.Ubersdr.Domain;

/// <summary>
/// Choosing which receivers to put on the wall.
///
/// <para>Pure, so it is testable without a network, and deliberately
/// conservative: every rule here exists to avoid wasting somebody else's client
/// slot or showing the operator a number that means nothing.</para>
/// </summary>
public static class ReceiverSelection
{
    /// <summary>
    /// Receivers worth offering, best first.
    ///
    /// <para>Excluded: anything offline, anything with no antenna (it cannot
    /// meter — see <see cref="UberSdrInstance.CanMeter"/>), and anything with no
    /// free slot, because connecting would be refused by admission control
    /// anyway.</para>
    ///
    /// <para>Ordered by distance, nearest first. Not by SNR: a quiet rural
    /// receiver reports a better SNR than a suburban one for an identical
    /// signal, so ranking by it would rank noise floors rather than the
    /// operator's signal.</para>
    /// </summary>
    public static IReadOnlyList<UberSdrInstance> Candidates(
        IEnumerable<UberSdrInstance> all) =>
        all.Where(i => i.CanMeter && i.HasCapacity)
           .OrderBy(i => double.IsNaN(i.DistanceKm) ? double.MaxValue : i.DistanceKm)
           .ToList();

    /// <summary>
    /// A default wall: <paramref name="count"/> receivers spread around the
    /// compass rather than the <paramref name="count"/> nearest.
    ///
    /// <para>The nearest few are often the same direction, and a wall that only
    /// looks one way answers "how am I doing towards the north-east" while
    /// appearing to answer "how am I doing". Taking the closest receiver in each
    /// of several bearing sectors gives a picture instead of a sample.</para>
    /// </summary>
    public static IReadOnlyList<UberSdrInstance> SpreadByBearing(
        IEnumerable<UberSdrInstance> candidates, int count)
    {
        if (count <= 0) return [];

        var usable = candidates.ToList();
        var withBearing = usable.Where(i => !double.IsNaN(i.BearingDegrees)).ToList();
        if (withBearing.Count == 0) return usable.Take(count).ToList();

        var sectors = Math.Min(count, 8);
        var picked = new List<UberSdrInstance>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var s = 0; s < sectors; s++)
        {
            var lo = 360.0 / sectors * s;
            var hi = 360.0 / sectors * (s + 1);
            var best = withBearing
                .Where(i => !seen.Contains(i.Id) && i.BearingDegrees >= lo && i.BearingDegrees < hi)
                .OrderBy(i => double.IsNaN(i.DistanceKm) ? double.MaxValue : i.DistanceKm)
                .FirstOrDefault();
            if (best is null) continue;
            picked.Add(best);
            seen.Add(best.Id);
        }

        // Sectors are often empty — the world is not evenly covered — so top up
        // with the nearest remaining rather than returning a short wall.
        foreach (var i in usable)
        {
            if (picked.Count >= count) break;
            if (seen.Add(i.Id)) picked.Add(i);
        }

        return picked.Take(count).ToList();
    }
}
