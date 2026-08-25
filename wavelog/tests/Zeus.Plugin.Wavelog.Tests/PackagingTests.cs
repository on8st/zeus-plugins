// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using System.Text.RegularExpressions;
using Zeus.Plugins.Contracts;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>
/// What actually ships, and whether the host will look at it.
///
/// <para>These exist because of a real miss: the manifest was called
/// <c>manifest.json</c>, inferred from prose, when the GPL sample plugins the
/// registry distributes all ship <c>plugin.json</c>. The plugin would very
/// likely never have been discovered, and nothing in the C# would have told
/// us.</para>
/// </summary>
public class PackagingTests
{
    /// <summary>The build output — what would be copied into the plugin root.</summary>
    private static string Output
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            // tests/.../bin/Debug/net10.0 -> src/Zeus.Plugin.Wavelog/bin/Debug/net10.0
            var root = new DirectoryInfo(dir);
            while (root is not null && root.Name != "wavelog") root = root.Parent;
            Assert.NotNull(root);
            return Path.Combine(root!.FullName, "src", "Zeus.Plugin.Wavelog",
                                "bin", "Debug", "net10.0");
        }
    }

    private static JsonDocument Manifest()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "plugin.json")));

    // ---- the file the host looks for ----------------------------------------

    [Fact]
    public void The_manifest_is_called_plugin_json()
    {
        Assert.True(File.Exists(Path.Combine(Output, "plugin.json")),
            "the host reads plugin.json — the GPL samples all ship that name");
        Assert.False(File.Exists(Path.Combine(Output, "manifest.json")),
            "shipping both invites the wrong one being edited");
    }

    // ---- what the host's validator refuses ---------------------------------

    [Fact]
    public void Schema_version_is_the_one_the_host_accepts()
        => Assert.Equal(1, Manifest().RootElement.GetProperty("schemaVersion").GetInt32());

    [Fact]
    public void The_id_matches_the_pattern_the_validator_enforces()
    {
        var id = Manifest().RootElement.GetProperty("id").GetString()!;
        Assert.Matches(new Regex("^[a-z][a-z0-9.]*[a-z0-9]$"), id);
    }

    [Fact]
    public void The_version_is_semver()
        => Assert.Matches(new Regex(@"^[0-9]+\.[0-9]+\.[0-9]+([+-][0-9A-Za-z.-]+)?$"),
                          Manifest().RootElement.GetProperty("version").GetString()!);

    [Fact]
    public void The_declared_abi_is_the_one_this_build_targets()
        => Assert.Equal(AbiVersion.Current,
                        Manifest().RootElement.GetProperty("sdk").GetProperty("abi").GetInt32());

    [Fact]
    public void The_sdk_minimum_is_three_part()
        => Assert.Matches(new Regex(@"^[0-9]+\.[0-9]+\.[0-9]+$"),
                          Manifest().RootElement.GetProperty("sdk").GetProperty("minVersion").GetString()!);

    [Fact]
    public void The_entrypoint_is_a_plain_dll_filename_not_a_path()
    {
        var assembly = Manifest().RootElement
            .GetProperty("entrypoint").GetProperty("assembly").GetString()!;
        Assert.EndsWith(".dll", assembly);
        Assert.Equal(assembly, Path.GetFileName(assembly));
    }

    [Fact]
    public void The_entrypoint_type_exists_in_the_shipped_assembly()
    {
        // The GPL samples name the type explicitly rather than leaving it null,
        // so a typo here must fail the build rather than the install.
        var type = Manifest().RootElement
            .GetProperty("entrypoint").GetProperty("type").GetString()!;
        Assert.Equal(typeof(WavelogSyncPlugin).FullName, type);
    }

    [Fact]
    public void Every_declared_ui_module_is_actually_shipped()
    {
        foreach (var module in Manifest().RootElement
                     .GetProperty("ui").GetProperty("modules").EnumerateArray())
        {
            var relative = module.GetString()!;
            Assert.True(File.Exists(Path.Combine(Output, relative)),
                $"the manifest declares {relative} but it is not in the output");
        }
    }

    [Fact]
    public void Every_panel_has_the_two_fields_the_validator_requires()
    {
        foreach (var panel in Manifest().RootElement
                     .GetProperty("ui").GetProperty("panels").EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(panel.GetProperty("id").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(panel.GetProperty("slot").GetString()));
        }
    }

    [Fact]
    public void The_ui_module_registers_the_panel_the_manifest_declares()
    {
        // The module's registerPanel id has to match, or the panel silently
        // never appears.
        var panelId = Manifest().RootElement.GetProperty("ui")
            .GetProperty("panels").EnumerateArray().First()
            .GetProperty("id").GetString()!;

        var module = File.ReadAllText(Path.Combine(Output, "ui", "wavelog.es.js"));
        Assert.Contains($"id: '{panelId}'", module);
        Assert.Contains("export default function register", module);
        Assert.Contains("api.registerPanel", module);
    }

    // ---- the dependency we cannot declare ----------------------------------

    [Fact]
    public void The_description_says_which_feature_is_required()
    {
        // A plugin manifest has no dependency mechanism — no dependsOn, no
        // requires, in neither the manifest schema nor the registry catalogue.
        // So the description is the only place an operator learns this before
        // installing, and the only thing standing between them and a feature
        // that installs cleanly and then does nothing at all.
        var description = Manifest().RootElement.GetProperty("description").GetString()!;
        Assert.Contains("org.openhpsdr.logbook", description);
        Assert.Contains("Zeus Logbook", description);
    }

    [Fact]
    public void The_panel_warns_when_the_logbook_is_missing()
    {
        // The other half: after installing, the panel is where they look. It
        // reads /status, so it must actually consult the flag.
        var module = File.ReadAllText(Path.Combine(Output, "ui", "wavelog.es.js"));
        Assert.Contains("logbookInstalled", module);
        Assert.Contains("org.openhpsdr.logbook", module);
    }

    // ---- what must and must not be in the output ---------------------------

    [Fact]
    public void The_dependencies_the_loader_needs_are_present()
    {
        // AssemblyDependencyResolver reads the deps.json and looks beside it, so
        // a class library that has not copied its packages simply fails to load.
        Assert.True(File.Exists(Path.Combine(Output, "Zeus.Plugin.Wavelog.dll")));
        Assert.True(File.Exists(Path.Combine(Output, "Zeus.Plugin.Wavelog.deps.json")));
        Assert.True(File.Exists(Path.Combine(Output, "LiteDB.dll")));
    }

    [Fact]
    public void The_contracts_assembly_is_not_shipped()
    {
        // The host forces Zeus.Plugins.Contracts to resolve from the default
        // load context so the interface types have one identity. Shipping a
        // copy invites version confusion for no benefit.
        Assert.False(File.Exists(Path.Combine(Output, "Zeus.Plugins.Contracts.dll")),
            "the host provides the contracts; do not ship them");
    }
}
