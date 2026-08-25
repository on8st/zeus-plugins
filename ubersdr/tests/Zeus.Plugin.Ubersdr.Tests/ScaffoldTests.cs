// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using Zeus.Plugins.Contracts;

namespace Zeus.Plugin.Ubersdr.Tests;

/// <summary>
/// The manifest facts that are silently fatal if wrong: a mistyped entrypoint
/// fails at install, and a wrong ABI fails at load. Both are cheap to assert and
/// expensive to discover.
/// </summary>
public class ScaffoldTests
{
    private static JsonDocument Manifest()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.Name != "ubersdr") dir = dir.Parent;
        Assert.NotNull(dir);
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "Zeus.Plugin.Ubersdr", "plugin.json")));
    }

    [Fact]
    public void The_entrypoint_type_exists()
        => Assert.Equal(typeof(UbersdrPlugin).FullName,
            Manifest().RootElement.GetProperty("entrypoint").GetProperty("type").GetString());

    [Fact]
    public void The_declared_abi_is_the_one_this_build_targets()
        => Assert.Equal(AbiVersion.Current,
            Manifest().RootElement.GetProperty("sdk").GetProperty("abi").GetInt32());

    [Fact]
    public void The_id_matches_the_pattern_the_validator_enforces()
    {
        // ^[a-z][a-z0-9.]*[a-z0-9]$ — no hyphens, rejected at install time.
        var id = Manifest().RootElement.GetProperty("id").GetString()!;
        Assert.Matches("^[a-z][a-z0-9.]*[a-z0-9]$", id);
    }

    [Fact]
    public void The_panel_id_matches_what_the_module_registers()
    {
        // A mismatch here means the panel silently never appears — the failure
        // mode that a packaging test caught once already in this repository.
        var panelId = Manifest().RootElement.GetProperty("ui")
            .GetProperty("panels").EnumerateArray().First().GetProperty("id").GetString()!;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.Name != "ubersdr") dir = dir.Parent;
        var module = File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "Zeus.Plugin.Ubersdr", "ui", "ubersdr.es.js"));

        Assert.Contains($"id: '{panelId}'", module);
        Assert.Contains("export default function register", module);
    }

    [Fact]
    public void The_panel_reads_the_header_layout_the_protocol_uses()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.Name != "ubersdr") dir = dir.Parent;
        var module = File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "Zeus.Plugin.Ubersdr", "ui", "ubersdr.es.js"));

        // The two offsets and the finite check are the whole metering contract;
        // a typo in any of them shows a plausible wrong number.
        Assert.Contains("getFloat32(13, true)", module);
        Assert.Contains("getFloat32(17, true)", module);
        Assert.Contains("Number.isFinite", module);
    }

    [Fact]
    public void The_vendored_opus_decoder_ships_with_its_licence()
    {
        // The panel imports it for its side effect; without the file the module
        // fails to load and the panel silently never appears. And a vendored
        // MIT file without its licence text is a licence problem, not an
        // oversight.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.Name != "ubersdr") dir = dir.Parent;
        var ui = Path.Combine(dir!.FullName, "src", "Zeus.Plugin.Ubersdr", "ui");

        Assert.True(File.Exists(Path.Combine(ui, "vendor", "opus-decoder.min.js")));
        Assert.True(File.Exists(Path.Combine(ui, "vendor", "opus-decoder.LICENSE")));

        var module = File.ReadAllText(Path.Combine(ui, "ubersdr.es.js"));
        Assert.Contains("./vendor/opus-decoder.min.js", module);
    }

    [Fact]
    public void Nothing_is_played_while_the_operator_is_keyed()
    {
        // The one safety property of the whole design: audio out of the speakers
        // with an open microphone is a delayed howl put on the air. The keying
        // handler must stop playback before it starts recording.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.Name != "ubersdr") dir = dir.Parent;
        var module = File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "Zeus.Plugin.Ubersdr", "ui", "ubersdr.es.js"));

        var keyedBranch = module[module.IndexOf("if (keyed) {", StringComparison.Ordinal)..];
        var stop = keyedBranch.IndexOf("stopPlayback()", StringComparison.Ordinal);
        var start = keyedBranch.IndexOf("startRecording()", StringComparison.Ordinal);

        Assert.True(stop >= 0, "keying must stop any playback");
        Assert.True(start >= 0, "keying must start recording");
        Assert.True(stop < start, "playback must be stopped before recording begins");
    }

    [Fact]
    public void Live_audio_pauses_on_key_unless_explicitly_opted_out()
    {
        // The default has to be safe. Receive diversity means listening on
        // speakers and then answering the station, and audio still running at
        // that moment is a feedback howl put on the air. Monitoring through a
        // transmission stays available, but only when asked for.
        var module = Panel();

        Assert.Contains("useState(false)", module);                 // opt-in defaults off
        Assert.Contains("if (!monitorWhileKeyedRef.current) pauseLive(true)", module);
        Assert.Contains("pauseLive(false)", module);                // and resumes on unkey
    }

    [Fact]
    public void Live_monitoring_warns_about_both_hazards()
    {
        // Feedback is the obvious one. Delayed auditory feedback is the one an
        // operator will not anticipate: hearing your own voice a second or two
        // late disrupts fluent speech badly, which is why this is for a carrier
        // or CW rather than for talking. Both belong in front of the operator,
        // not in a design document.
        var module = Panel();

        Assert.Contains("headphones", module, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("feedback howl", module, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("late", module, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Only_one_receiver_can_be_monitored_live()
    {
        // A decoder per receiver is what phase 2 exists to avoid, and six
        // simultaneous live streams would be unintelligible anyway. The live
        // state is a single host, not a set.
        var module = Panel();

        Assert.Contains("const [live, setLive] = useState(null)", module);
        Assert.Contains("stopLive();", module);   // starting one stops any other
    }

    [Fact]
    public void The_tile_layer_carries_its_attribution()
    {
        // OpenStreetMap's tiles are volunteer-funded and their usage policy asks
        // for attribution and light use. Both are conditions of using them at
        // all, so neither may be quietly dropped in a refactor.
        var map = MapModule();
        var panel = Panel();

        Assert.Contains("OpenStreetMap contributors", map);
        Assert.Contains("OSM_ATTRIBUTION", panel);
        // Zoom 2 is twelve tiles for the inhabited world. A deeper zoom would
        // multiply that by four for every level.
        Assert.Contains("TILE_Z = 2", map);
    }

    [Fact]
    public void The_vector_map_survives_as_an_offline_fallback()
    {
        // A panel that shows nothing when tiles fail is worse than one that
        // never had them.
        var panel = Panel();
        Assert.Contains("onTileFail", panel);
        Assert.Contains("COAST_PATH", panel);
    }

    private static string MapModule()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.Name != "ubersdr") dir = dir.Parent;
        return File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "Zeus.Plugin.Ubersdr", "ui", "map.js"));
    }

    private static string Panel()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.Name != "ubersdr") dir = dir.Parent;
        return File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "Zeus.Plugin.Ubersdr", "ui", "ubersdr.es.js"));
    }
}
