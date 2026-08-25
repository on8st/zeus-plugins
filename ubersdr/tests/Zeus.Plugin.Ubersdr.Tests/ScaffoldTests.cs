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
}
