// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugin.Wavelog.Storage;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>
/// There are two Zeus logbooks, and picking the wrong one looks exactly like
/// having no logbook at all.
///
/// <para>Zeus Link keeps a built-in logbook at
/// <c>&lt;Application Support&gt;/ZeusProduct/logbook/zeus-logbook.db</c>. The
/// <c>org.openhpsdr.logbook</c> plugin writes to the engine's host data
/// directory instead. Same file name, same <c>logs</c> collection, same
/// documents — different directory.</para>
///
/// <para>This is written from a live install where the plugin reported "no Zeus
/// logbook found" while the operator was looking at a QSO they had just logged.
/// Checking one path and treating the answer as definitive is the mistake; these
/// tests exist so it stays fixed.</para>
/// </summary>
public sealed class LogbookDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "wavelog-discover-" + Guid.NewGuid().ToString("N"));

    private string HostData => Path.Combine(_root, "Zeus");
    private string ProductLogbookDir => Path.Combine(_root, "ZeusProduct", "logbook");

    public LogbookDiscoveryTests() => Directory.CreateDirectory(HostData);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private void CreateLogbook(string dir)
    {
        Directory.CreateDirectory(dir);
        using var zeus = new NativeLogbook(Path.Combine(dir, ZeusLogbookDb.FileName));
        zeus.Log();
    }

    // ---- both places are looked in -----------------------------------------

    [Fact]
    public void Both_known_locations_are_candidates()
    {
        var candidates = ZeusLogbookDb.CandidatePaths(HostData);

        Assert.Contains(candidates, c => c == Path.Combine(HostData, ZeusLogbookDb.FileName));
        Assert.Contains(candidates, c => c == Path.Combine(ProductLogbookDir, ZeusLogbookDb.FileName));
    }

    [Fact]
    public void The_product_location_is_derived_not_hard_coded()
    {
        // It is a sibling of the host data directory, so a relocated profile
        // still resolves rather than silently falling back to a fixed path.
        var moved = Path.Combine(_root, "elsewhere", "Zeus");
        var candidates = ZeusLogbookDb.CandidatePaths(moved);
        Assert.Contains(candidates, c => c.Contains(Path.Combine("elsewhere", "ZeusProduct", "logbook")));
    }

    // ---- what gets picked ---------------------------------------------------

    [Fact]
    public void The_product_logbook_is_found_when_the_plugin_one_does_not_exist()
    {
        // Exactly the live situation: no logbook plugin installed, Zeus Link
        // logging into its own store, and the synchroniser previously blind.
        CreateLogbook(ProductLogbookDir);

        var found = Assert.Single(ZeusLogbookDb.FindExisting(HostData));
        Assert.Equal(Path.Combine(ProductLogbookDir, ZeusLogbookDb.FileName), found);
        Assert.True(ZeusLogbookDb.ExistsIn(HostData));

        using var db = new ZeusLogbookDb(found);
        Assert.Equal(1, db.Count());
        Assert.Null(db.Verify());
    }

    [Fact]
    public void The_plugin_logbook_is_found_when_that_is_the_one_present()
    {
        CreateLogbook(HostData);
        var found = Assert.Single(ZeusLogbookDb.FindExisting(HostData));
        Assert.Equal(Path.Combine(HostData, ZeusLogbookDb.FileName), found);
    }

    [Fact]
    public void Neither_present_is_reported_as_none()
    {
        Assert.Empty(ZeusLogbookDb.FindExisting(HostData));
        Assert.False(ZeusLogbookDb.ExistsIn(HostData));
    }

    // ---- the case we refuse to guess ---------------------------------------

    [Fact]
    public void Two_logbooks_are_both_reported_rather_than_one_being_chosen()
    {
        // Installing the logbook plugin on a machine that already has the
        // built-in store gives two files with the same name. Silently syncing
        // one of them would mean the operator's visible log and the synced log
        // are different logs, which is worse than doing nothing.
        CreateLogbook(HostData);
        CreateLogbook(ProductLogbookDir);

        var found = ZeusLogbookDb.FindExisting(HostData);
        Assert.Equal(2, found.Count);

        var message = ZeusLogbookDb.AmbiguousLogbookMessage(found);
        Assert.Contains("will not guess", message);
        foreach (var path in found) Assert.Contains(path, message);
    }

    [Fact]
    public void The_no_logbook_message_names_both_places_it_looked()
    {
        Assert.Contains("ZeusProduct", ZeusLogbookDb.NoLogbookMessage);
        Assert.Contains(ZeusLogbookDb.FileName, ZeusLogbookDb.NoLogbookMessage);
    }
}
