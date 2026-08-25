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
}
