// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;
using Zeus.Plugin.Wavelog.Sync;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>
/// Wavelog's JSON is not typed the way a reader would assume, and the places it
/// surprises you are load-bearing.
///
/// <para>Every one of these was found by calling a real instance, not by reading
/// the PHP and not by testing against our own fake. The fake returned tidy
/// integers, so a fatal parse bug passed a full green suite and would have shipped
/// as a plugin whose sync loop threw on its first cycle, forever, against every
/// real server.</para>
/// </summary>
public class WavelogJsonQuirksTests
{
    // ---- the one that actually broke --------------------------------------

    [Fact]
    public void Lastfetchedid_arrives_as_a_string_on_a_real_instance()
    {
        // Verbatim from a real instance: the cursor is quoted, the count is not.
        var reply = JsonNode.Parse(
            """{"status":"successful","lastfetchedid":"1","exported_qsos":1,"adif":"x"}""")!;

        Assert.Equal(1, HttpWavelogTransport.ReadInt(reply["lastfetchedid"]));
        Assert.Equal(1, HttpWavelogTransport.ReadInt(reply["exported_qsos"]));
    }

    [Fact]
    public void The_same_field_is_read_whichever_way_it_is_typed()
    {
        // Neither shape may be assumed: read both, always.
        Assert.Equal(42, HttpWavelogTransport.ReadInt(JsonNode.Parse("42")));
        Assert.Equal(42, HttpWavelogTransport.ReadInt(JsonNode.Parse("\"42\"")));
    }

    [Fact]
    public void A_field_that_makes_no_sense_is_absent_rather_than_fatal()
    {
        // The bug was not the wrong type — it was that the wrong type threw, and
        // the throw killed the loop rather than one poll.
        Assert.Null(HttpWavelogTransport.ReadInt(null));
        Assert.Null(HttpWavelogTransport.ReadInt(JsonNode.Parse("\"\"")));
        Assert.Null(HttpWavelogTransport.ReadInt(JsonNode.Parse("\"not a number\"")));
        Assert.Null(HttpWavelogTransport.ReadInt(JsonNode.Parse("null")));
        Assert.Null(HttpWavelogTransport.ReadInt(JsonNode.Parse("{}")));
        Assert.Null(HttpWavelogTransport.ReadInt(JsonNode.Parse("[1,2]")));
    }

    [Fact]
    public void Station_id_is_a_string_in_station_info()
    {
        // Which is why the profile list parses it with TryParse rather than
        // GetValue<int>. Same class of bug, caught earlier by luck.
        var reply = JsonNode.Parse(
            """[{"station_id":"1","station_profile_name":"Home"}]""")!;
        Assert.Equal(1, HttpWavelogTransport.ReadInt(reply[0]!["station_id"]));
    }
}
