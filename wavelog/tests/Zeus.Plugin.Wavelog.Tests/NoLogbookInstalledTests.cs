// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugin.Wavelog.Storage;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>
/// A fresh Zeus has no plugins at all, so the thing this one synchronises may
/// simply not be installed.
///
/// <para>The Zeus logbook is <c>org.openhpsdr.logbook</c> from the plugin
/// registry, not part of the engine — verified against a live install whose
/// engine reports <c>{"plugins":[]}</c>, creates no <c>zeus-logbook.db</c> and
/// serves no logbook route. So "no logbook yet" is an ordinary state to start
/// in, and the operator has to be told which plugin to install.</para>
///
/// <para>The trap is that it is trivially easy to look fine instead: LiteDB will
/// create the file on demand, and an empty database we made ourselves is
/// indistinguishable from a logbook the operator has not written to. The plugin
/// would report a healthy, permanently idle sync and never mention that the
/// logbook is missing.</para>
/// </summary>
public sealed class NoLogbookInstalledTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wavelog-nolog-" + Guid.NewGuid().ToString("N"));

    public NoLogbookInstalledTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    [Fact]
    public void An_absent_logbook_is_detected_rather_than_created()
    {
        Assert.False(ZeusLogbookDb.ExistsIn(_dir));

        // And asking must not bring one into being.
        Assert.False(File.Exists(Path.Combine(_dir, ZeusLogbookDb.FileName)));
    }

    [Fact]
    public void The_message_names_the_plugin_to_install()
    {
        // "Install the logbook" is not actionable; the registry id is.
        Assert.Contains("org.openhpsdr.logbook", ZeusLogbookDb.NoLogbookMessage);
        Assert.Contains(ZeusLogbookDb.FileName, ZeusLogbookDb.NoLogbookMessage);
    }

    [Fact]
    public void Once_the_logbook_exists_it_is_detected()
    {
        // The operator installs the plugin while the engine is running, which is
        // exactly what the plugin manager does — no restart should be needed.
        using (var zeus = NativeLogbook.InDataDirectory(_dir)) zeus.Log();

        Assert.True(ZeusLogbookDb.ExistsIn(_dir));
        using var db = ZeusLogbookDb.ForDataDirectory(_dir);
        Assert.Equal(1, db.Count());
        Assert.Null(db.Verify());
    }
}
