// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugin.Wavelog.Storage;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>
/// An untouched station has no logbook file yet, so the thing this plugin
/// synchronises may simply not exist.
///
/// <para>Zeus creates <c>zeus-logbook.db</c> when the operator logs their first
/// contact. So "no logbook yet" is an ordinary state to start in, not a fault,
/// and it has to be distinguishable from a logbook that is merely empty.</para>
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
    public void The_message_says_where_it_looked_and_what_to_do()
    {
        // Vague is useless here: the whole failure mode is an operator who
        // cannot tell "nothing to sync" from "syncing the wrong thing".
        Assert.Contains(ZeusLogbookDb.FileName, ZeusLogbookDb.NoLogbookMessage);
        Assert.Contains("ZeusProduct", ZeusLogbookDb.NoLogbookMessage);
        Assert.Contains("Log a QSO", ZeusLogbookDb.NoLogbookMessage);
    }

    [Fact]
    public void Once_the_logbook_exists_it_is_detected()
    {
        // The operator logs their first contact while the engine is running.
        // No restart should be needed for that to start syncing.
        using (var zeus = NativeLogbook.InDataDirectory(_dir)) zeus.Log();

        Assert.True(ZeusLogbookDb.ExistsIn(_dir));
        using var db = ZeusLogbookDb.ForDataDirectory(_dir);
        Assert.Equal(1, db.Count());
        Assert.Null(db.Verify());
    }
}
