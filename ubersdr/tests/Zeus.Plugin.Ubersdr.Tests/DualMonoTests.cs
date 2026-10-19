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
    public void Zeus_is_silenced_by_the_panel_and_always_given_back()
    {
        // The panel owns both ears, so Zeus's own output has to go — it plays the
        // same stream into both. This used to be a written instruction to the
        // operator; the plugin now does it, which makes *restoring* it the thing
        // that matters. A panel that silences the radio and does not put it back
        // is indistinguishable from a broken receiver.
        var qso = Ui("qso-assist.js");

        Assert.Contains("silenceZeus", qso);
        Assert.Contains("restoreZeus", qso);

        // Restored on stop, and on unmount — closing the panel is not a stop.
        Assert.Contains("void restoreZeus();", qso);
        Assert.Contains("useEffect(() => () => { void restoreZeus(); }", qso);

        // And the level to go back to is only captured when it is worth going
        // back to, or a hand-lowered slider gets recorded as the operator's own.
        Assert.Contains("cur > SILENCE_AF_DB + 5", qso);

        // The local ear is restored by exactly the attenuation applied, not by
        // an automatic gain control. An AGC makes every signal sound the same
        // strength, which destroys the one thing an operator judges by ear.
        // Measured from the level read a moment before the write, never from the
        // stored restore value: those differ by however stale the stored one is,
        // and the difference arrives as gain.
        Assert.Contains("Math.pow(10, (cur - SILENCE_AF_DB) / 20)", qso);
        Assert.Contains("res?.ok && Number.isFinite(cur)", qso);
        Assert.Contains("if (a.makeup != null)", qso);
    }

    [Fact]
    public void The_right_ear_is_receive_only_and_muted_by_default()
    {
        // It replaces one side of the operator's headphones, so it does not
        // start unasked. And remote audio during transmit is your own voice
        // arriving a second late in one ear, which wrecks fluent speech — or a
        // howl on the air if it reaches a microphone.
        var qso = Ui("qso-assist.js");

        Assert.Contains("useState(true)", qso);                    // the right ear, muted
        Assert.Contains("gate.mutedRight || (gate.keyed && !gate.txMonitor)", qso);
        // Remote monitoring during transmit is opt-in and starts off.
        Assert.Contains("const [txMonitor, setTxMonitor] = useState(false)", qso);
    }

    [Fact]
    public void A_refused_receiver_is_not_retried()
    {
        // The panel once asked a deleted route for a connection 150 times, in
        // silence. A failure is now said once and the host is dropped for the
        // session.
        var qso = Ui("qso-assist.js");
        Assert.Contains("refused.current.add", qso);
        Assert.Contains("refused.current.has", qso);
    }
}
