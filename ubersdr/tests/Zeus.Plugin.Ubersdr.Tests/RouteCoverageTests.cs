// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.RegularExpressions;

namespace Zeus.Plugin.Ubersdr.Tests;

/// <summary>
/// Every route a panel calls must exist in the backend.
///
/// <para>This exists because it did not. An edit that inserted two new routes
/// replaced a span of the file that happened to contain <c>MapPost("connect")</c>,
/// the route was silently deleted, and the panel then asked for it a hundred and
/// fifty times — every one a 404, none of them visible, and the feature simply
/// never worked. Nothing in C# or in JavaScript could see the mismatch, because
/// the contract between them is a string.</para>
///
/// <para>So the strings are compared. Crude, and it would have caught it.</para>
/// </summary>
public class RouteCoverageTests
{
    private static DirectoryInfo Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.Name != "ubersdr") dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!;
    }

    private static HashSet<string> RegisteredRoutes()
    {
        var src = File.ReadAllText(Path.Combine(
            Root().FullName, "src", "Zeus.Plugin.Ubersdr", "UbersdrPlugin.cs"));

        return Regex.Matches(src, @"endpoints\.Map(?:Get|Post|Put|Delete)\(""([a-zA-Z0-9/_-]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<(string File, string Path)> RoutesCalledByPanels()
    {
        var ui = new DirectoryInfo(Path.Combine(Root().FullName, "src", "Zeus.Plugin.Ubersdr", "ui"));
        foreach (var file in ui.GetFiles("*.js"))
        {
            var text = File.ReadAllText(file.FullName);
            foreach (Match m in Regex.Matches(text, @"callBackend\(\s*'[A-Z]+'\s*,\s*'/([a-zA-Z0-9/_-]+)"))
                yield return (file.Name, m.Groups[1].Value);
        }
    }

    [Fact]
    public void Every_route_a_panel_calls_is_registered()
    {
        var registered = RegisteredRoutes();
        Assert.NotEmpty(registered);

        var called = RoutesCalledByPanels().ToList();
        Assert.NotEmpty(called);

        var missing = called
            .Where(c => !registered.Contains(c.Path))
            .Select(c => $"{c.File} calls /{c.Path}")
            .Distinct()
            .ToList();

        Assert.True(missing.Count == 0,
            "panels call routes the backend does not register:\n  " + string.Join("\n  ", missing)
            + "\nregistered: " + string.Join(", ", registered.OrderBy(r => r)));
    }

    [Fact]
    public void The_routes_the_panels_depend_on_are_all_present()
    {
        // Named explicitly as well, so deleting a route AND its only caller
        // still fails rather than quietly agreeing with itself.
        var registered = RegisteredRoutes();
        foreach (var route in new[] { "radio", "ptt", "engine", "config", "receivers", "connect" })
            Assert.True(registered.Contains(route), $"route '{route}' is not registered");
    }
}
