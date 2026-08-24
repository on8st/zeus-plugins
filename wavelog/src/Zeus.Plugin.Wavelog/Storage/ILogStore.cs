// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Wavelog.Storage;

/// <summary>
/// The port the plugin talks to. It mirrors <c>ILogbookPluginV2</c> because
/// that interface <em>is</em> a storage contract — the client keeps browsing,
/// sorting and editing, and calls through it.
/// </summary>
public interface ILogStore
{
    Task<LogbookEntrySnapshot> CreateAsync(LogbookNewEntry entry, CancellationToken ct = default);
    Task<LogbookPage> GetEntriesAsync(int skip, int take, CancellationToken ct = default);
    Task<IReadOnlyList<LogbookEntrySnapshot>> GetByIdsAsync(IEnumerable<string> ids, CancellationToken ct = default);
    Task<LogbookWorkedSummary?> GetWorkedSummaryAsync(string callsign, int recentTake, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetDigitalWorkedCallsignsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetAllTagsAsync(CancellationToken ct = default);
    Task<LogbookEntrySnapshot?> UpdateAsync(string id, LogbookEntryUpdate update, CancellationToken ct = default);
    Task<int> UpdateQslStatusAsync(IReadOnlyList<LogbookQslStatusUpdate> updates, CancellationToken ct = default);
    Task<bool> UpdateQrzUploadStatusAsync(string id, string qrzLogId, CancellationToken ct = default);
    Task<int> DeleteAsync(IEnumerable<string> ids, CancellationToken ct = default);
    Task<string> ExportAdifAsync(IEnumerable<string>? ids = null, CancellationToken ct = default);
    Task<LogbookExportFileResult> ExportAdifToFileAsync(string? directory = null, IEnumerable<string>? ids = null, CancellationToken ct = default);
    Task<LogbookImportResult> ImportAdifAsync(string adifText, CancellationToken ct = default);
}
