// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugin.Wavelog.Storage;
using Zeus.Plugin.Wavelog.Sync;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Wavelog;

/// <summary>
/// The plugin. It <em>is</em> the logbook: the client keeps browsing, sorting,
/// searching, editing and the QSL workflow, and calls through this interface.
///
/// <para>The write path is local-first and the network strictly downstream —
/// <c>CreateAsync</c> stores, enqueues and returns, and never waits on Wavelog.
/// A contact logged while the instance is rebooting is safe the moment this
/// method returns.</para>
/// </summary>
public sealed class WavelogLogbookPlugin : IZeusPlugin, ILogbookPluginV2, IBackendPlugin
{
    private const string ConfigKey = "wavelog.config";

    private LiteDbLogStore? _store;
    private LiteDbOutbox? _outbox;
    private LiteDbCursorStore? _cursors;
    private WavelogSyncService? _sync;
    private OutboxPump? _pump;
    private RadioStatePublisher? _radio;
    private HttpClient? _http;
    private IPluginContext? _ctx;
    private ILogger? _log;
    private CancellationTokenSource? _background;

    private volatile WavelogConfig _config = new();
    private DateTime _configReadUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _configGate = new(1, 1);

    /// <summary>
    /// How long a cached copy of the configuration is trusted.
    ///
    /// <para>Zeus owns the settings store — one LiteDB collection per plugin —
    /// and it can rewrite a plugin's whole collection without telling it: that
    /// is how the profile snapshot/restore system works. <c>PluginSettingsChanged</c>
    /// exists but sits on the host's own store and is not exposed on
    /// <see cref="IPluginContext"/>, so a plugin cannot subscribe to it.</para>
    ///
    /// <para>With no push available, holding our copy as authoritative would
    /// mean a profile restore silently leaves the plugin talking to the old
    /// instance with the old key until the next restart. So the store stays the
    /// single source of truth and this is only a short-lived cache.</para>
    /// </summary>
    public static readonly TimeSpan ConfigTtl = TimeSpan.FromSeconds(30);

    private LiteDbLogStore Store => _store
        ?? throw new InvalidOperationException("the plugin has not been initialised");

    // ---- lifecycle ----------------------------------------------------------

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        _log = context.Logger;

        // The host data directory, not the plugin root: the plugin root goes
        // away when the plugin is uninstalled, and the log must outlive that.
        var root = string.IsNullOrWhiteSpace(context.HostDataDirectory)
            ? context.PluginRootPath
            : context.HostDataDirectory;
        var dir = Path.Combine(root, "wavelog-plugin");

        _store = new LiteDbLogStore(Path.Combine(dir, "log.db"));
        _outbox = new LiteDbOutbox(Path.Combine(dir, "outbox.db"));
        _cursors = new LiteDbCursorStore(Path.Combine(dir, "cursor.db"));
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        _config = await LoadConfigAsync(ct).ConfigureAwait(false);
        _configReadUtc = DateTime.UtcNow;

        var transport = new HttpWavelogTransport(_http);
        _sync = new WavelogSyncService(_store, _outbox, transport, CurrentConfig, _cursors, _log);
        _pump = new OutboxPump(_outbox, transport, CurrentConfig, RetryPolicy.Default, _log);
        _pump.Delivered += id => _store?.MarkPushed(id);
        _pump.DeadLettered += (id, reason) => _store?.MarkPushFailed(id, reason);

        if (context.Radio is { } radio)
            _radio = new RadioStatePublisher(radio, transport, CurrentConfig, SystemClock.Instance, "Zeus", _log);

        _background = new CancellationTokenSource();
        StartBackground(_background.Token);

        _log.LogInformation("wavelog: ready — store {Dir}, configured={Configured}", dir, _config.IsUsable);
    }

    private void StartBackground(CancellationToken ct)
    {
        _ = Task.Run(() => _pump!.RunAsync(TimeSpan.FromSeconds(20), ct), ct);
        _ = Task.Run(() => PullLoopAsync(ct), ct);
        if (_radio is not null) { _radio.Start(); _ = Task.Run(() => _radio.RunAsync(ct), ct); }
    }

    /// <summary>
    /// Two cadences, because one cursor cannot do both jobs: new QSOs arrive
    /// above the primary key every few minutes, while confirmations are updates
    /// that never move it and need the filtered sweep.
    /// </summary>
    private async Task PullLoopAsync(CancellationToken ct)
    {
        var lastSweep = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _sync!.PullNewAsync(ct).ConfigureAwait(false);
                if (DateTime.UtcNow - lastSweep > TimeSpan.FromHours(12))
                {
                    await _sync.SweepConfirmationsAsync(ct).ConfigureAwait(false);
                    lastSweep = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log?.LogError(ex, "wavelog: pull loop failed"); }

            try { await Task.Delay(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _background?.Cancel();
        _radio?.Dispose();
        _cursors?.Dispose();
        _outbox?.Dispose();
        _store?.Dispose();
        _http?.Dispose();
        _store = null;
        return Task.CompletedTask;
    }

    // ---- configuration ------------------------------------------------------

    /// <summary>
    /// The configuration, re-read from Zeus's store when the cache has aged out.
    /// Synchronous because every caller is a hot-ish loop; the read is a single
    /// indexed LiteDB lookup and happens at most twice a minute.
    /// </summary>
    private WavelogConfig CurrentConfig()
    {
        if (DateTime.UtcNow - _configReadUtc <= ConfigTtl) return _config;
        if (!_configGate.Wait(0)) return _config;          // another read in flight
        try
        {
            _config = LoadConfigAsync(CancellationToken.None).GetAwaiter().GetResult();
            _configReadUtc = DateTime.UtcNow;
        }
        catch (Exception ex) { _log?.LogDebug(ex, "wavelog: settings re-read failed; keeping the last value"); }
        finally { _configGate.Release(); }
        return _config;
    }

    private async Task<WavelogConfig> LoadConfigAsync(CancellationToken ct)
    {
        try
        {
            var stored = await _ctx!.Settings.GetAsync<StoredConfig>(ConfigKey, ct).ConfigureAwait(false);
            return stored?.ToConfig() ?? new WavelogConfig();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "wavelog: could not read settings; starting unconfigured");
            return new WavelogConfig();
        }
    }

    /// <summary>The persisted shape. The key lives here and is never returned by the API.</summary>
    private sealed class StoredConfig
    {
        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public int StationProfileId { get; set; } = 1;
        public List<int> PullStationIds { get; set; } = [];
        public bool PushEnabled { get; set; } = true;
        public bool PullEnabled { get; set; } = true;
        public bool RadioEnabled { get; set; }

        public WavelogConfig ToConfig() => new()
        {
            BaseUrl = BaseUrl, ApiKey = ApiKey, StationProfileId = StationProfileId,
            PullStationIds = PullStationIds, PushEnabled = PushEnabled,
            PullEnabled = PullEnabled, RadioEnabled = RadioEnabled,
        };

        public static StoredConfig From(WavelogConfig c) => new()
        {
            BaseUrl = c.BaseUrl, ApiKey = c.ApiKey, StationProfileId = c.StationProfileId,
            PullStationIds = c.PullStationIds.ToList(), PushEnabled = c.PushEnabled,
            PullEnabled = c.PullEnabled, RadioEnabled = c.RadioEnabled,
        };
    }

    // ---- endpoints ----------------------------------------------------------

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("config", () => Results.Ok(new
        {
            baseUrl = _config.BaseUrl,
            stationProfileId = _config.StationProfileId,
            pullStationIds = _config.PullStationIds,
            pushEnabled = _config.PushEnabled,
            pullEnabled = _config.PullEnabled,
            radioEnabled = _config.RadioEnabled,
            // Never the key itself — only whether one is set.
            apiKeySet = !string.IsNullOrWhiteSpace(_config.ApiKey),
        }));

        // Both verbs: the GPL sample panels only ever use GET and POST through
        // api.callBackend, so PUT alone would leave the panel unable to save.
        endpoints.MapMethods("config", ["PUT", "POST"], async (JsonNode body, CancellationToken ct) =>
        {
            var url = body["baseUrl"]?.GetValue<string>()?.Trim() ?? _config.BaseUrl;
            if (!string.IsNullOrWhiteSpace(url) &&
                !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "baseUrl must start with http:// or https://" });

            // An absent key leaves the stored one alone, so a round-trip through
            // the config UI cannot wipe it.
            var key = body["apiKey"]?.GetValue<string>();

            _config = _config with
            {
                BaseUrl = url,
                ApiKey = string.IsNullOrWhiteSpace(key) ? _config.ApiKey : key!.Trim(),
                StationProfileId = body["stationProfileId"]?.GetValue<int>() ?? _config.StationProfileId,
                PullStationIds = body["pullStationIds"]?.AsArray()?.Select(n => n!.GetValue<int>()).ToList()
                                 ?? _config.PullStationIds,
                PushEnabled = body["pushEnabled"]?.GetValue<bool>() ?? _config.PushEnabled,
                PullEnabled = body["pullEnabled"]?.GetValue<bool>() ?? _config.PullEnabled,
                RadioEnabled = body["radioEnabled"]?.GetValue<bool>() ?? _config.RadioEnabled,
            };

            await _ctx!.Settings.SetAsync(ConfigKey, StoredConfig.From(_config), ct).ConfigureAwait(false);
            _configReadUtc = DateTime.UtcNow;      // our own write is already reflected
            return Results.Ok(new { ok = true });
        });

        endpoints.MapGet("profiles", async (CancellationToken ct) =>
        {
            var (outcome, profiles) = await new HttpWavelogTransport(_http!)
                .GetStationInfoAsync(CurrentConfig(), ct).ConfigureAwait(false);
            return outcome.IsSuccess
                ? Results.Ok(profiles!.Select(p => new { id = p.Id, name = p.Name }))
                : Results.BadRequest(new { error = outcome.Detail ?? outcome.Kind.ToString() });
        });

        endpoints.MapGet("status", () => Results.Ok(new
        {
            configured = CurrentConfig().IsUsable,
            pending = _outbox?.PendingCount ?? 0,
            failed = _outbox?.DeadLetterCount ?? 0,
            lastError = _outbox?.DeadLettered().LastOrDefault()?.LastError,
            cursor = _cursors?.GetFetchFromId() ?? 0,
            // Naming the profiles makes "why isn't that contact here" a glance
            // rather than an investigation.
            pullStationIds = _config.PullStationIds,
            pushStationProfileId = _config.StationProfileId,
        }));

        endpoints.MapPost("test", async (CancellationToken ct) =>
        {
            var (outcome, profiles) = await new HttpWavelogTransport(_http!)
                .GetStationInfoAsync(CurrentConfig(), ct).ConfigureAwait(false);
            return outcome.IsSuccess
                ? Results.Ok(new { ok = true, profiles = profiles!.Count })
                : Results.BadRequest(new { ok = false, error = outcome.Detail ?? outcome.Kind.ToString() });
        });

        endpoints.MapPost("retry", () => Results.Ok(new { requeued = _outbox?.RequeueDeadLettered() ?? 0 }));

        endpoints.MapPost("resync", async (JsonNode? body, CancellationToken ct) =>
        {
            var dryRun = body?["dryRun"]?.GetValue<bool>() ?? true;   // dry run is the default path
            var report = await _sync!.ResyncAsync(dryRun, ct).ConfigureAwait(false);
            return report.Ran
                ? Results.Ok(new { report.DryRun, report.MissingHere, report.MissingThere })
                : Results.BadRequest(new { error = report.Error });
        });
    }

    // ---- ILogbookPlugin -----------------------------------------------------

    /// <summary>
    /// Store, enqueue, return. The network is never on this path: a QSO logged
    /// while Wavelog is unreachable is safe the moment this returns.
    /// </summary>
    public async Task<LogbookEntrySnapshot> CreateAsync(LogbookNewEntry entry, CancellationToken ct = default)
    {
        var saved = await Store.CreateAsync(entry, ct).ConfigureAwait(false);
        try { _sync?.EnqueueForPush(saved); }
        catch (Exception ex) { _log?.LogError(ex, "wavelog: could not queue {Id} for upload", saved.Id); }
        return saved;
    }

    public Task<LogbookPage> GetEntriesAsync(int skip, int take, CancellationToken ct = default)
        => Store.GetEntriesAsync(skip, take, ct);

    public Task<IReadOnlyList<LogbookEntrySnapshot>> GetByIdsAsync(
        IEnumerable<string> ids, CancellationToken ct = default) => Store.GetByIdsAsync(ids, ct);

    public Task<LogbookWorkedSummary?> GetWorkedSummaryAsync(
        string callsign, int recentTake, CancellationToken ct = default)
        => Store.GetWorkedSummaryAsync(callsign, recentTake, ct);

    public Task<IReadOnlyList<string>> GetDigitalWorkedCallsignsAsync(CancellationToken ct = default)
        => Store.GetDigitalWorkedCallsignsAsync(ct);

    public Task<bool> UpdateQrzUploadStatusAsync(string id, string qrzLogId, CancellationToken ct = default)
        => Store.UpdateQrzUploadStatusAsync(id, qrzLogId, ct);

    public Task<int> DeleteAsync(IEnumerable<string> ids, CancellationToken ct = default)
        => Store.DeleteAsync(ids, ct);

    public Task<string> ExportAdifAsync(IEnumerable<string>? ids = null, CancellationToken ct = default)
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
