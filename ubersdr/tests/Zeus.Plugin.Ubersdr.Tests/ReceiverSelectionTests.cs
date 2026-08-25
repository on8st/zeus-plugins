// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;
using Zeus.Plugin.Ubersdr.Domain;

namespace Zeus.Plugin.Ubersdr.Tests;

/// <summary>
/// Which receivers go on the wall. Every rule here exists either to avoid
/// wasting a stranger's client slot or to avoid showing a number that means
/// nothing.
/// </summary>
public class ReceiverSelectionTests
{
    // ---- parsing the directory ---------------------------------------------

    [Fact]
    public void A_directory_record_is_parsed_from_the_real_shape()
    {
        // Trimmed from an actual /api/instances response.
        var json = JsonNode.Parse("""
            {"id":"80538f4c-1cea-4e89-871c-247c2c13b9a2","callsign":"ON0XYZ",
             "name":"Example SDR","location":"Somewhere, Belgium",
             "host":"example.tunnel.ubersdr.org","port":443,"tls":true,
             "distance":28.741092272540282,"bearing_degrees":147.77,
             "available_clients":20,"max_clients":20,
             "is_online":true,"antenna_connected":false}
            """);

        var i = UberSdrInstance.FromJson(json)!;

        Assert.Equal("ON0XYZ", i.Callsign);
        Assert.Equal("example.tunnel.ubersdr.org", i.Host);
        Assert.Equal("wss://example.tunnel.ubersdr.org", i.WebSocketBase);
        Assert.Equal(28.74, i.DistanceKm, 2);
        Assert.True(i.IsOnline);
        Assert.False(i.AntennaConnected);
    }

    [Fact]
    public void A_record_with_no_host_is_not_an_instance()
        => Assert.Null(UberSdrInstance.FromJson(JsonNode.Parse("""{"callsign":"ON0XYZ"}""")));

    [Fact]
    public void Missing_and_oddly_typed_fields_do_not_throw()
    {
        // A directory that grows a field, or types one differently, must not
        // break a released plugin — the lesson from lastfetchedid arriving as a
        // string in the Wavelog work.
        var i = UberSdrInstance.FromJson(JsonNode.Parse("""
            {"host":"x.example","distance":"123.5","available_clients":"3"}
            """))!;

        Assert.Equal(123.5, i.DistanceKm, 3);
        Assert.Equal(3, i.AvailableClients);
        Assert.False(i.IsOnline);            // absent means not online, not a crash
    }

    // ---- who is offered -----------------------------------------------------

    [Fact]
    public void An_instance_with_no_antenna_is_never_offered()
    {
        // It streams audio happily and reports -Infinity power on every frame.
        // Offered as a monitor it looks like a receiver that cannot hear you,
        // which reads as "my signal is not getting out" — the most misleading
        // answer available.
        var all = new[] { Rx("near-no-antenna", 10, 0, antenna: false), Rx("far-ok", 900, 90) };

        var picked = ReceiverSelection.Candidates(all);

        Assert.Equal("far-ok", Assert.Single(picked).Id);
    }

    [Fact]
    public void An_instance_that_is_offline_or_full_is_never_offered()
    {
        var all = new[]
        {
            Rx("offline", 10, 0, online: false),
            Rx("full", 20, 10, free: 0),
            Rx("fine", 30, 20),
        };

        Assert.Equal("fine", Assert.Single(ReceiverSelection.Candidates(all)).Id);
    }

    [Fact]
    public void Candidates_come_back_nearest_first()
    {
        // Explicitly not by SNR: a quiet rural receiver reports a better SNR
        // than a suburban one for an identical signal, so ranking by it would
        // rank noise floors rather than the operator's signal.
        var all = new[] { Rx("far", 900, 10), Rx("near", 12, 20), Rx("mid", 300, 30) };

        Assert.Equal(["near", "mid", "far"],
            ReceiverSelection.Candidates(all).Select(i => i.Id));
    }

    // ---- the default wall ---------------------------------------------------

    [Fact]
    public void The_default_wall_looks_in_several_directions()
    {
        // The four nearest receivers are often the same direction; a wall that
        // only looks one way answers "how am I doing towards the north-east"
        // while appearing to answer "how am I doing".
        var all = new[]
        {
            Rx("ne1", 10, 45), Rx("ne2", 12, 50), Rx("ne3", 14, 55),
            Rx("s",  400, 180), Rx("w", 500, 270),
        };

        var wall = ReceiverSelection.SpreadByBearing(ReceiverSelection.Candidates(all), 3);

        Assert.Contains(wall, i => i.Id == "s");
        Assert.Contains(wall, i => i.Id == "w");
        Assert.Single(wall.Where(i => i.Id.StartsWith("ne")));
    }

    [Fact]
    public void An_empty_sector_does_not_shorten_the_wall()
    {
        // The world is not evenly covered; asking for four must give four when
        // four exist, even if they cluster.
        var all = new[] { Rx("a", 10, 10), Rx("b", 20, 12), Rx("c", 30, 14), Rx("d", 40, 16) };

        Assert.Equal(4, ReceiverSelection.SpreadByBearing(ReceiverSelection.Candidates(all), 4).Count);
    }

    [Fact]
    public void Asking_for_none_gives_none()
        => Assert.Empty(ReceiverSelection.SpreadByBearing([Rx("a", 1, 1)], 0));

    private static UberSdrInstance Rx(
        string id, double km, double bearing,
        bool online = true, bool antenna = true, int free = 5,
        double lat = 51.0, double lon = 4.6) =>
        new(id, "CALL", id, "loc", id + ".example", true, km, bearing,
            lat, lon, "Belgium", free, 20, online, antenna);
}
