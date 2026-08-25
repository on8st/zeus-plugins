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
/// Keeps Zeus's native logbook synchronised with a Wavelog instance.
///
/// <para>This plugin is <b>not</b> the logbook. It does not implement
/// <c>ILogbookPluginV2</c> and never owns the operator's QSOs — the native
/// logbook keeps doing that, along with browsing, editing, ADIF and QSL, all of
/// which already work. This attaches to the same database and moves contacts
/// in both directions.</para>
///
/// <para>Framing it this way removes the one assumption the engine repository
/// could not settle: whether Zeus Link calls a logbook plugin at all, and what
/// happens on uninstall. Uninstall this and the operator's log is untouched,
/// because it was never ours.</para>
/// </summary>
public sealed class WavelogSyncPlugin : IZeusPlugin, IBackendPlugin
{
    private const string ConfigKey = "wavelog.config";

    private ZeusLogbookDb? _logbook;
    private string _dataDirectory = "";
    private IWavelogTransport? _transport;
    private bool _saidNoLogbook;
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
    /// <para>Zeus owns the settings store and can rewrite a plugin's whole
    /// collection without telling it — that is how profile snapshot and restore
    /// work. <c>PluginSettingsChanged</c> exists but sits on the host's own
    /// store and is not exposed on <see cref="IPluginContext"/>, so a plugin
    /// cannot subscribe. With no push available, holding our copy as
    /// authoritative would leave the plugin talking to the old instance with the
    /// old key until the next restart.</para>
    /// </summary>
    public static readonly TimeSpan ConfigTtl = TimeSpan.FromSeconds(30);

    // ---- lifecycle ----------------------------------------------------------

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        _log = context.Logger;

        var data = string.IsNullOrWhiteSpace(context.HostDataDirectory)
            ? context.PluginRootPath
            : context.HostDataDirectory;

        _dataDirectory = data;

        // Ours alone, kept beside it rather than inside it.
        var mine = Path.Combine(data, "wavelog-plugin");
        _outbox = new LiteDbOutbox(Path.Combine(mine, "outbox.db"));
        _cursors = new LiteDbCursorStore(Path.Combine(mine, "cursor.db"));
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        _config = await LoadConfigAsync(ct).ConfigureAwait(false);
        _configReadUtc = DateTime.UtcNow;

        var transport = new HttpWavelogTransport(_http);
        _transport = transport;
        _pump = new OutboxPump(_outbox, transport, CurrentConfig, RetryPolicy.Default, _log);
        if (context.Radio is { } radio)
            _radio = new RadioStatePublisher(radio, transport, CurrentConfig, SystemClock.Instance, "Zeus", _log);

        _background = new CancellationTokenSource();
        StartBackground(_background.Token);

        TryAttachLogbook();
    }

    /// <summary>
    /// Attach to the operator's logbook, if there is one yet.
    ///
    /// <para>The Zeus logbook is a plugin — <c>org.openhpsdr.logbook</c> — and a
    /// fresh Zeus has no plugins at all: verified against a live install, whose
    /// engine reports <c>{"plugins":[]}</c> and serves no logbook route. So this
    /// plugin can perfectly well start before the thing it synchronises exists,
    /// and must handle that as a state rather than an error.</para>
    ///
    /// <para>Two rules. We never <em>create</em> <c>zeus-logbook.db</c> — LiteDB
    /// would happily make one, and an empty file we made ourselves is
    /// indistinguishable from a logbook the operator has not written to yet, so
    /// the plugin would sit there looking healthy and syncing nothing. And we
    /// keep trying, because the operator may install the logbook at any moment
    /// and should not have to restart the engine afterwards.</para>
    /// </summary>
    private readonly object _attachGate = new();

    private bool TryAttachLogbook()
    {
        if (_logbook is not null) return true;      // fast path, no lock
        lock (_attachGate)
        {
        if (_logbook is not null) return true;

        if (!ZeusLogbookDb.ExistsIn(_dataDirectory))
        {
            if (!_saidNoLogbook)
            {
                _log?.LogError("wavelog: {Message}", ZeusLogbookDb.NoLogbookMessage);
                _saidNoLogbook = true;      // once, not every thirty seconds
            }
            return false;
        }

        _logbook = ZeusLogbookDb.ForDataDirectory(_dataDirectory);
        _sync = new WavelogSyncService(_logbook, _outbox!, _transport!, CurrentConfig, _cursors!, _log);
        _pump!.Delivered += id => _logbook?.MarkPushed(id);
        _pump.DeadLettered += (id, reason) => _logbook?.MarkPushFailed(id, reason);

        if (_logbook.Verify() is { } problem)
            _log?.LogError("wavelog: {Problem}", problem);

        _saidNoLogbook = false;
        _log?.LogInformation(
            "wavelog: attached to the native logbook ({Count} QSOs), configured={Configured}",
            _logbook.Count(), _config.IsUsable);
        return true;
        }
    }

    private void StartBackground(CancellationToken ct)
    {
        _ = Task.Run(() => _pump!.RunAsync(TimeSpan.FromSeconds(20), ct), ct);
        _ = Task.Run(() => SyncLoopAsync(ct), ct);
        if (_radio is not null) { _radio.Start(); _ = Task.Run(() => _radio.RunAsync(ct), ct); }
    }

    /// <summary>
    /// Three cadences. Newly logged contacts are noticed by polling, because the
    /// host offers no event for them. New contacts from elsewhere arrive above
    /// the primary key. Confirmations are updates that never move that key, so
    /// they need their own filtered sweep and can afford to be slow.
    /// </summary>
    private async Task SyncLoopAsync(CancellationToken ct)
    {
        var lastSweep = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!TryAttachLogbook()) { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); continue; }
                _sync!.EnqueueNewLocalQsos();
                await _sync.PullNewAsync(ct).ConfigureAwait(false);
                if (DateTime.UtcNow - lastSweep > TimeSpan.FromHours(12))
                {
                    await _sync.SweepConfirmationsAsync(ct).ConfigureAwait(false);
                    lastSweep = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log?.LogError(ex, "wavelog: sync loop failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _background?.Cancel();
        _radio?.Dispose();
        _cursors?.Dispose();
        _outbox?.Dispose();
        _logbook?.Dispose();
        _http?.Dispose();
        return Task.CompletedTask;
    }

    // ---- configuration ------------------------------------------------------

    private WavelogConfig CurrentConfig()
    {
        if (DateTime.UtcNow - _configReadUtc <= ConfigTtl) return _config;
        if (!_configGate.Wait(0)) return _config;
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
        endpoints.MapGet("config", () =>
        {
            var c = CurrentConfig();
            return Results.Ok(new
            {
                baseUrl = c.BaseUrl,
                stationProfileId = c.StationProfileId,
                pullStationIds = c.PullStationIds,
                pushEnabled = c.PushEnabled,
                pullEnabled = c.PullEnabled,
                radioEnabled = c.RadioEnabled,
                apiKeySet = !string.IsNullOrWhiteSpace(c.ApiKey),   // never the key itself
            });
        });

        // Both verbs: the GPL sample panels only ever use GET and POST through
        // api.callBackend, so PUT alone would leave the panel unable to save.
        endpoints.MapMethods("config", ["PUT", "POST"], async (JsonNode body, CancellationToken ct) =>
        {
            var url = body["baseUrl"]?.GetValue<string>()?.Trim() ?? _config.BaseUrl;
            if (!string.IsNullOrWhiteSpace(url) &&
                !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "baseUrl must start with http:// or https://" });

            // An absent key leaves the stored one alone, so saving other fields
            // can never wipe it.
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
            _configReadUtc = DateTime.UtcNow;
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

        endpoints.MapGet("status", () =>
        {
            // Attach on demand as well as on the timer. The operator installs
            // the logbook and opens this panel seconds later; waiting out the
            // scan interval would show them a stale "not installed" and invite
            // them to conclude it had not worked.
            TryAttachLogbook();
            var c = CurrentConfig();
            return Results.Ok(new
            {
                configured = c.IsUsable,
                logbookInstalled = _logbook is not null,
                logbookProblem = _logbook is null ? ZeusLogbookDb.NoLogbookMessage : _logbook.Verify(),
                qsosInLogbook = _logbook?.Count() ?? 0,
                pending = _outbox?.PendingCount ?? 0,
                failed = _outbox?.DeadLetterCount ?? 0,
                lastError = _outbox?.DeadLettered().LastOrDefault()?.LastError,
                cursor = _cursors?.GetFetchFromId() ?? 0,
                // Naming the profiles turns "why isn't that contact here" into a
                // glance rather than an investigation.
                pullStationIds = c.PullStationIds,
                pushStationProfileId = c.StationProfileId,
            });
        });

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
            if (!TryAttachLogbook())
                return Results.BadRequest(new { error = ZeusLogbookDb.NoLogbookMessage });
            var report = await _sync!.ResyncAsync(dryRun, ct).ConfigureAwait(false);
            return report.Ran
                ? Results.Ok(new { report.DryRun, report.MissingHere, report.MissingThere })
                : Results.BadRequest(new { error = report.Error });
        });
    }
}
