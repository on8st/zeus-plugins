// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Plugin.Ubersdr.Tests;

/// <summary>
/// The two-panel arrangement, and the promises QSO Assist makes.
/// </summary>
public class DualMonoTests
{
    private static string Ui(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.Name != "ubersdr") dir = dir.Parent;
        return File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "Zeus.Plugin.Ubersdr", "ui", name));
    }

    [Fact]
    public void Both_panels_are_declared_and_both_modules_ship()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.Name != "ubersdr") dir = dir.Parent;
        var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "Zeus.Plugin.Ubersdr", "plugin.json")));

        var ui = manifest.RootElement.GetProperty("ui");
        var ids = ui.GetProperty("panels").EnumerateArray()
            .Select(p => p.GetProperty("id").GetString()).ToList();
        var modules = ui.GetProperty("modules").EnumerateArray()
            .Select(m => m.GetString()!).ToList();

        Assert.Contains("ubersdr.qso", ids);
        Assert.Contains("ubersdr.monitor", ids);
        // One plugin, two panels — deliberately, so they share a directory cache
        // and one connection budget rather than competing for slots on other
        // people's receivers.
        Assert.Equal(2, modules.Count);
        foreach (var m in modules)
        {
            var path = Path.Combine(dir.FullName, "src", "Zeus.Plugin.Ubersdr",
                m.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"declared module {m} is missing");
        }
    }

    [Fact]
    public void The_connection_budget_is_small_and_stated()
    {
        // Every held connection is a session on hardware somebody else pays for.
        // The number is a constant so it cannot drift upward unnoticed.
        var qso = Ui("qso-assist.js");
        Assert.Contains("CONNECTION_BUDGET = 3", qso);
        Assert.Contains("ROTATE_AFTER_MS", qso);
        Assert.Contains("somebody else", qso);
    }

    [Fact]
    public void Only_the_chosen_receiver_is_decoded()
    {
        // Holding three receivers is affordable because two of them are measured
        // from their 21-byte headers and never decoded. Decoding all three would
        // put the cost back.
        var qso = Ui("qso-assist.js");
        Assert.Contains("bestRef.current !== rx.host", qso);
    }

    [Fact]
    public void The_double_audio_caveat_is_stated_in_the_panel()
    {
        // The panel owns both ears, so Zeus's own output has to be muted or the
        // operator hears their receiver twice. That is a real setup step and it
        // belongs on screen, not in a commit message.
        Assert.Contains("Mute Zeus", Ui("qso-assist.js"));
    }
}
