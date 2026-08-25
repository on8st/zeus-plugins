// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugin.Ubersdr.Domain;

namespace Zeus.Plugin.Ubersdr.Tests;

/// <summary>
/// Limits an operator puts on receiver choice, and the one preset the default
/// algorithm can never reach.
/// </summary>
public class ReceiverConstraintTests
{
    private static UberSdrInstance Rx(string id, double km, double bearing, int free = 5) =>
        new(id, "CALL", id, "loc", id + ".example", true, km, bearing,
            51.0, 4.6, "Belgium", free, 20, true, true);

    // ---- distance -----------------------------------------------------------

    [Fact]
    public void A_distance_window_excludes_both_ends()
    {
        var limits = new ReceiverConstraints { MinDistanceKm = 200, MaxDistanceKm = 1000 };

        Assert.False(limits.Allows(Rx("tooNear", 50, 0)));
        Assert.True(limits.Allows(Rx("justRight", 700, 0)));
        Assert.False(limits.Allows(Rx("tooFar", 4000, 0)));
    }

    // ---- bearing, including the case that is easy to get wrong -------------

    [Fact]
    public void A_bearing_arc_selects_a_direction()
    {
        // 240–300°: roughly North America from western Europe.
        var limits = new ReceiverConstraints { BearingFrom = 240, BearingTo = 300 };

        Assert.True(limits.Allows(Rx("west", 900, 265)));
        Assert.False(limits.Allows(Rx("east", 900, 90)));
    }

    [Fact]
    public void A_bearing_arc_wraps_through_north()
    {
        // 300 → 60 is the arc an operator means by "northwards", and a naive
        // from <= b && b <= to gets it exactly backwards.
        var limits = new ReceiverConstraints { BearingFrom = 300, BearingTo = 60 };

        Assert.True(limits.Allows(Rx("nw", 500, 320)));
        Assert.True(limits.Allows(Rx("due north", 500, 0)));
        Assert.True(limits.Allows(Rx("ne", 500, 45)));
        Assert.False(limits.Allows(Rx("south", 500, 180)));
    }

    // ---- capacity and exclusions -------------------------------------------

    [Fact]
    public void A_minimum_free_slot_count_is_respected()
    {
        var limits = new ReceiverConstraints { MinFreeSlots = 3 };

        Assert.False(limits.Allows(Rx("busy", 100, 0, free: 2)));
        Assert.True(limits.Allows(Rx("quiet", 100, 0, free: 9)));
    }

    [Fact]
    public void A_receiver_with_no_free_slot_is_never_allowed()
    {
        // Admission control would refuse it anyway; offering it wastes the
        // operator's click and the instance's attention.
        Assert.False(new ReceiverConstraints().Allows(Rx("full", 100, 0, free: 0)));
    }

    [Fact]
    public void Named_hosts_can_be_excluded()
    {
        var limits = new ReceiverConstraints { ExcludeHosts = ["noisy.example"] };

        Assert.False(limits.Allows(Rx("noisy", 100, 0)));
        Assert.True(limits.Allows(Rx("other", 100, 0)));
    }

    [Fact]
    public void Constraints_compose_with_the_candidate_filter()
    {
        var all = new[] { Rx("near", 50, 0), Rx("mid", 600, 90), Rx("far", 5000, 180) };
        var limits = new ReceiverConstraints { MinDistanceKm = 400, MaxDistanceKm = 2000 };

        Assert.Equal("mid", Assert.Single(ReceiverSelection.Candidates(all, limits)).Id);
    }

    // ---- the preset the default cannot reach --------------------------------

    [Fact]
    public void Furthest_prefers_distance_where_everything_else_prefers_proximity()
    {
        // Nearest-in-sector, then nearest to top up: without this the far end of
        // the list is unreachable, and for antenna work it is the interesting end.
        var all = new[] { Rx("a", 50, 0), Rx("b", 900, 90), Rx("c", 5000, 180), Rx("d", 3000, 270) };

        Assert.Equal(["c", "d"], ReceiverSelection.Furthest(all, 2).Select(i => i.Id));
    }

    [Fact]
    public void Furthest_ignores_receivers_with_no_known_distance()
    {
        var all = new[] { Rx("known", 900, 0), Rx("unknown", double.NaN, 0) };
        Assert.Equal("known", Assert.Single(ReceiverSelection.Furthest(all, 5)).Id);
    }
}
