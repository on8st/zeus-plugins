// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FakeWavelog;

/// <summary>
/// A stand-in for a Wavelog instance, on loopback, entirely under the test's
/// control.
///
/// <para><b>Why this exists.</b> TDD must never point at a live logbook. This
/// server implements the endpoints and semantics read out of the real Wavelog
/// source at <c>af32561</c> — the same duplicate key, the same primary-key
/// cursor, the same response shapes — so the plugin can be driven end to end
/// including its real HTTP client, with no risk to anyone's log.</para>
///
/// <para><b>What it is not.</b> It encodes our reading of Wavelog. It cannot
/// prove that reading correct — only a run against a real instance does that,
/// which is a phase-1 gate item, not a unit test.</para>
/// </summary>
public sealed class FakeWavelogServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<Row> _rows = [];
    private readonly object _gate = new();
    private int _nextId = 8400;
    private CancellationTokenSource? _cts;

    public FakeWavelogServer(int port = 0)
    {
        Port = port == 0 ? FreePort() : port;
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    // ---- knobs the tests turn ----------------------------------------------

    /// <summary>Accepted key. Anything else is rejected the way Wavelog rejects it.</summary>
    public string ApiKey { get; set; } = "test-key";

    /// <summary>Read-only keys are refused by /api/radio, as in the real thing.</summary>
    public bool KeyCanWrite { get; set; } = true;

    /// <summary>Force every reply to this status. 0 means behave normally.</summary>
    public int ForceStatus { get; set; }

    /// <summary>Reply with this body instead of JSON — a proxy error page, say.</summary>
    public string? ForceBody { get; set; }

    /// <summary>Delay each reply, so a client timeout can be exercised.</summary>
    public TimeSpan Delay { get; set; }

    public int QsoPostCount { get; private set; }
    public int RadioPostCount { get; private set; }
    public JsonNode? LastRadioPayload { get; private set; }

    public IReadOnlyList<Row> Rows { get { lock (_gate) return _rows.ToList(); } }

    public sealed record Row(int Id, string Call, string TimeKey, string Band, string Mode,
                             int StationId, string Adif)
    {
        public bool LotwConfirmed { get; set; }
        public DateTime? LotwConfirmedOn { get; set; }

        /// <summary>
        /// What the export actually contains for this row.
        ///
        /// <para>A confirmation in Wavelog is a column on the QSO, and it shows
        /// up in the export as extra ADIF fields on the same record — it is not
        /// a separate object. Modelling it as a flag the export ignored would
        /// let the sweep look like it worked while carrying nothing back.</para>
        /// </summary>
        public string Export()
        {
            if (!LotwConfirmed) return Adif;
            var on = (LotwConfirmedOn ?? DateTime.UtcNow).ToString("yyyyMMdd");
            return Adif[..Adif.LastIndexOf("<EOR>", StringComparison.Ordinal)] +
                   $"<LOTW_QSL_RCVD:1>Y<LOTW_QSLRDATE:8>{on}<QSL_RCVD:1>Y<QSLRDATE:8>{on}<EOR>";
        }
    }

    // ---- lifecycle ----------------------------------------------------------

    public void Start()
    {
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); _listener.Stop(); _listener.Close(); } catch { }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { return; }

            try { await HandleAsync(ctx).ConfigureAwait(false); }
            catch { try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { } }
        }
    }

    // ---- routing ------------------------------------------------------------

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay).ConfigureAwait(false);

        var path = ctx.Request.Url?.AbsolutePath ?? "";
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);

        if (ForceStatus != 0) { Reply(ctx, ForceStatus, ForceBody ?? "{\"status\":\"failed\"}"); return; }
        if (ForceBody is not null) { Reply(ctx, 200, ForceBody); return; }

        // station_info is `function station_info($key = '')` in Wavelog's own
        // controller, so CodeIgniter fills the key from a URL segment and never
        // looks at a body. Every other endpoint reads php://input. The fake got
        // this wrong for a while and validated the wrong reading of the API
        // perfectly, so the shape is reproduced here deliberately.
        const string StationInfoPath = "/index.php/api/station_info";
        if (path.StartsWith(StationInfoPath, StringComparison.Ordinal))
        {
            var segment = path[StationInfoPath.Length..].TrimStart('/');
            if (segment != ApiKey) { Reply(ctx, 401, Fail("missing or invalid api key")); return; }
            StationInfo(ctx);
            return;
        }

        JsonNode? json = null;
        try { json = JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body); } catch { }
        var key = json?["key"]?.GetValue<string>();

        if (key != ApiKey) { Reply(ctx, 200, Fail("missing api key")); return; }

        switch (path)
        {
            case "/index.php/api/qso": PostQso(ctx, json!); return;
            case "/index.php/api/get_contacts_adif": GetContacts(ctx, json!); return;
            case "/index.php/api/radio": PostRadio(ctx, json!); return;
            default: Reply(ctx, 404, Fail("no such endpoint")); return;
        }
    }

    // ---- /api/qso -----------------------------------------------------------

    private void PostQso(HttpListenerContext ctx, JsonNode json)
    {
        QsoPostCount++;
        var adif = json["string"]?.GetValue<string>() ?? "";
        var stationId = ReadInt(json["station_profile_id"]) ?? 1;

        var records = MiniAdif.Parse(adif);
        var added = 0;
        lock (_gate)
        {
            foreach (var r in records)
            {
                var call = Get(r, "CALL");
                var timeKey = TimeKey(Get(r, "QSO_DATE"), Get(r, "TIME_ON"));
                var band = Get(r, "BAND").ToLowerInvariant();
                var mode = Get(r, "MODE").ToUpperInvariant();

                // The duplicate key read out of Logbook_model::import:
                // CALL + TIME_ON to the minute + BAND + MODE + station_id.
                var duplicate = _rows.Any(x =>
                    x.Call == call && x.TimeKey == timeKey &&
                    x.Band == band && x.Mode == mode && x.StationId == stationId);
                if (duplicate) continue;

                _rows.Add(new Row(_nextId++, call, timeKey, band, mode, stationId, RecordOf(r)));
                added++;
            }
        }

        Reply(ctx, 200, $"{{\"status\":\"created\",\"adif_count\":{added}}}");
    }

    // ---- /api/get_contacts_adif --------------------------------------------

    private void GetContacts(HttpListenerContext ctx, JsonNode json)
    {
        var from = ReadInt(json["fetchfromid"]) ?? 0;
        var limit = ReadInt(json["limit"]) ?? 500;
        var stationIds = ReadStationIds(json["station_id"]);
        var qslFilter = json["qsl_filter"]?.AsArray()?.Select(n => n!.GetValue<string>()).ToList();

        List<Row> selected;
        lock (_gate)
        {
            selected = _rows
                .Where(r => stationIds.Count == 0 || stationIds.Contains(r.StationId))
                .Where(r => r.Id > from)
                .Where(r => qslFilter is null || qslFilter.Count == 0 || r.LotwConfirmed)
                .OrderBy(r => r.Id)
                .Take(limit)
                .ToList();
        }

        if (selected.Count == 0)
        {
            Reply(ctx, 200,
                $"{{\"status\":\"successful\",\"message\":\"No new QSOs available.\"," +
                $"\"lastfetchedid\":\"{from}\",\"exported_qsos\":0,\"adif\":null}}");
            return;
        }

        var last = selected[^1].Id;
        var adif = new StringBuilder("<ADIF_VER:5>3.1.4<EOH>\n");
        foreach (var r in selected) adif.Append(r.Export()).Append('\n');

        // lastfetchedid is a STRING on a real instance while exported_qsos is a
        // number. Reproduced exactly: the tidy all-integers version this used to
        // send is what let a fatal parse bug pass a full suite.
        Reply(ctx, 200, JsonSerializer.Serialize(new
        {
            status = "successful",
            message = "Export successful",
            lastfetchedid = last.ToString(System.Globalization.CultureInfo.InvariantCulture),
            exported_qsos = selected.Count,
            adif = adif.ToString(),
        }));
    }

    private void StationInfo(HttpListenerContext ctx) => Reply(ctx, 200, JsonSerializer.Serialize(new[]
    {
        new { station_id = "1", station_profile_name = "Home" },
        new { station_id = "2", station_profile_name = "Portable" },
    }));

    // ---- /api/radio ---------------------------------------------------------

    private void PostRadio(HttpListenerContext ctx, JsonNode json)
    {
        if (!KeyCanWrite) { Reply(ctx, 200, Fail("API key does not have write permissions")); return; }
        RadioPostCount++;
        LastRadioPayload = json;
        Reply(ctx, 200, "{\"status\":\"successful\"}");
    }

    // ---- test helpers -------------------------------------------------------

    /// <summary>Add a QSO as though another app had logged it directly.</summary>
    public int AddQsoFromAnotherApp(string call, DateTime whenUtc, string band, string mode,
                                    int stationId = 1)
    {
        lock (_gate)
        {
            var id = _nextId++;
            var adif =
                $"<CALL:{call.Length}>{call}" +
                $"<QSO_DATE:8>{whenUtc:yyyyMMdd}<TIME_ON:6>{whenUtc:HHmmss}" +
                $"<BAND:{band.Length}>{band}<MODE:{mode.Length}>{mode}<EOR>";
            _rows.Add(new Row(id, call.ToUpperInvariant(),
                TimeKey(whenUtc.ToString("yyyyMMdd"), whenUtc.ToString("HHmmss")),
                band.ToLowerInvariant(), mode.ToUpperInvariant(), stationId, adif));
            return id;
        }
    }

    /// <summary>
    /// Confirm a QSO on LoTW — an UPDATE, which does not change the primary key.
    /// That is the whole reason the plugin needs a second, filtered sweep.
    /// </summary>
    public void ConfirmOnLotw(string call, DateTime? onUtc = null)
    {
        lock (_gate)
            foreach (var r in _rows.Where(r => r.Call == call.ToUpperInvariant()))
            {
                r.LotwConfirmed = true;
                r.LotwConfirmedOn = onUtc ?? new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            }
    }

    // ---- plumbing -----------------------------------------------------------

    private static string Fail(string reason) => $"{{\"status\":\"failed\",\"reason\":\"{reason}\"}}";

    private static void Reply(HttpListenerContext ctx, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    private static int? ReadInt(JsonNode? n)
    {
        if (n is null) return null;
        try { return n.GetValue<int>(); } catch { }
        try { return int.Parse(n.GetValue<string>()); } catch { return null; }
    }

    private static List<int> ReadStationIds(JsonNode? n)
    {
        var ids = new List<int>();
        if (n is null) return ids;
        if (n is JsonArray arr)
        {
            foreach (var item in arr) { var v = ReadInt(item); if (v is not null) ids.Add(v.Value); }
            return ids;
        }
        var single = ReadInt(n);
        if (single is not null) ids.Add(single.Value);
        return ids;
    }

    private static string TimeKey(string date, string time)
    {
        if (time.Length >= 4) time = time[..4];
        return date + time;                         // to the minute, as Wavelog compares
    }

    private static string Get(IReadOnlyDictionary<string, string> r, string k)
        => r.TryGetValue(k, out var v) ? v.Trim().ToUpperInvariant() : "";

    private static string RecordOf(IReadOnlyDictionary<string, string> r)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in r)
            sb.Append('<').Append(k).Append(':').Append(Encoding.UTF8.GetByteCount(v)).Append('>').Append(v);
        return sb.Append("<EOR>").ToString();
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
