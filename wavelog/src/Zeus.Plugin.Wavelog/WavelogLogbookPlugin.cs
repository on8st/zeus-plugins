// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using Microsoft.Extensions.Logging;
using Zeus.Plugin.Wavelog.Storage;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Wavelog;

/// <summary>
/// The plugin. It <em>is</em> the logbook: the client keeps browsing, sorting,
/// searching, editing and the QSL workflow, and calls through this interface.
///
/// <para>Milestone 1a is deliberately a logbook that syncs nothing — shippable
/// on its own, and provable on its own. The outbox, the push and the pull are
/// added behind it in 1b–1d without changing anything the operator sees.</para>
/// </summary>
public sealed class WavelogLogbookPlugin : IZeusPlugin, ILogbookPluginV2
{
    private LiteDbLogStore? _store;
    private ILogger? _log;

    private LiteDbLogStore Store => _store
        ?? throw new InvalidOperationException("the plugin has not been initialised");

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _log = context.Logger;

        // Prefer the host's data directory: the log must outlive a reinstall of
        // the plugin, and the plugin root is deleted when the plugin is removed.
        var root = string.IsNullOrWhiteSpace(context.HostDataDirectory)
            ? context.PluginRootPath
            : context.HostDataDirectory;
        var path = Path.Combine(root, "wavelog-plugin", "log.db");

        _store = new LiteDbLogStore(path);
        _log.LogInformation("wavelog: logbook store opened at {Path}", path);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _store?.Dispose();
        _store = null;
        return Task.CompletedTask;
    }

    // ---- ILogbookPlugin -----------------------------------------------------

    public Task<LogbookEntrySnapshot> CreateAsync(LogbookNewEntry entry, CancellationToken ct = default)
        => Store.CreateAsync(entry, ct);

    public Task<LogbookPage> GetEntriesAsync(int skip, int take, CancellationToken ct = default)
        => Store.GetEntriesAsync(skip, take, ct);

    public Task<IReadOnlyList<LogbookEntrySnapshot>> GetByIdsAsync(
        IEnumerable<string> ids, CancellationToken ct = default)
        => Store.GetByIdsAsync(ids, ct);

    public Task<LogbookWorkedSummary?> GetWorkedSummaryAsync(
        string callsign, int recentTake, CancellationToken ct = default)
        => Store.GetWorkedSummaryAsync(callsign, recentTake, ct);

    public Task<IReadOnlyList<string>> GetDigitalWorkedCallsignsAsync(CancellationToken ct = default)
        => Store.GetDigitalWorkedCallsignsAsync(ct);

    public Task<bool> UpdateQrzUploadStatusAsync(
        string id, string qrzLogId, CancellationToken ct = default)
        => Store.UpdateQrzUploadStatusAsync(id, qrzLogId, ct);

    public Task<int> DeleteAsync(IEnumerable<string> ids, CancellationToken ct = default)
        => Store.DeleteAsync(ids, ct);

    public Task<string> ExportAdifAsync(
        IEnumerable<string>? ids = null, CancellationToken ct = default)
        => Store.ExportAdifAsync(ids, ct);

    public Task<LogbookExportFileResult> ExportAdifToFileAsync(
        string? directory = null, IEnumerable<string>? ids = null, CancellationToken ct = default)
        => Store.ExportAdifToFileAsync(directory, ids, ct);

    public Task<LogbookImportResult> ImportAdifAsync(string adifText, CancellationToken ct = default)
        => Store.ImportAdifAsync(adifText, ct);

    // ---- ILogbookPluginV2 ---------------------------------------------------

    public Task<LogbookEntrySnapshot?> UpdateAsync(
        string id, LogbookEntryUpdate update, CancellationToken ct = default)
        => Store.UpdateAsync(id, update, ct);

    public Task<int> UpdateQslStatusAsync(
        IReadOnlyList<LogbookQslStatusUpdate> updates, CancellationToken ct = default)
        => Store.UpdateQslStatusAsync(updates, ct);

    public Task<IReadOnlyList<string>> GetAllTagsAsync(CancellationToken ct = default)
        => Store.GetAllTagsAsync(ct);
}
