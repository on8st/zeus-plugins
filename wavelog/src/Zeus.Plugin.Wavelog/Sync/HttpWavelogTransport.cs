// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zeus.Plugin.Wavelog.Sync;

/// <summary>
/// The only place this plugin touches a network.
///
/// <para>It classifies rather than throws: every failure becomes a
/// <see cref="WavelogOutcome"/> so the retry decision stays a pure function
/// somewhere else. Note that a 200 carrying something other than the JSON
/// Wavelog promises is a <em>failure</em>, not a success — a proxy error page
/// would otherwise be read as "the QSO landed" and the contact would be
/// dropped.</para>
/// </summary>
public sealed class HttpWavelogTransport(HttpClient http) : IWavelogTransport
{
    public async Task<WavelogOutcome> PostQsoAsync(WavelogConfig config, string adif, CancellationToken ct)
    {
        var body = new
        {
            key = config.ApiKey,
            station_profile_id = config.StationProfileId.ToString(CultureInfo.InvariantCulture),
            type = "adif",
            @string = adif,
        };
        var (outcome, json) = await SendAsync(config.Endpoint("qso"), body, ct).ConfigureAwait(false);

        // A duplicate is success. Wavelog deduplicates on callsign + time to the
        // minute + band + mode + station, which is what makes at-least-once
        // delivery safe here — but it reports the skip as HTTP 400
        // status="abort", which the retry policy would dead-letter. The operator
        // would then see a permanent failure for a QSO that is sitting in their
        // log, and pressing retry would fail forever.
        //
        // Only found by pushing the same contact twice at a real instance; the
        // fake used to answer "created" with a zero count.
        if (IsOnlyDuplicates(json)) return WavelogOutcome.Success();

        if (!outcome.IsSuccess) return outcome;

        var status = json?["status"]?.GetValue<string>();
        return status is "created" or "successful"
            ? WavelogOutcome.Success()
            : WavelogOutcome.HttpStatus(400, json?["reason"]?.GetValue<string>() ?? status ?? "rejected");
    }

    public async Task<(WavelogOutcome, PulledQsos?)> GetContactsAsync(
        WavelogConfig config, int fetchFromId, int limit,
        IReadOnlyList<string>? qslFilter, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["key"] = config.ApiKey,
            // Never the raw list: it may be empty, and Wavelog refuses that.
            ["station_id"] = config.EffectivePullStationIds
                .Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray(),
            ["fetchfromid"] = fetchFromId,
            ["limit"] = limit,
            ["output_format"] = "adif",
        };
        if (qslFilter is { Count: > 0 }) body["qsl_filter"] = qslFilter;

        var (outcome, json) = await SendAsync(config.Endpoint("get_contacts_adif"), body, ct).ConfigureAwait(false);
        if (!outcome.IsSuccess) return (outcome, null);

        if (json?["status"]?.GetValue<string>() != "successful")
            return (WavelogOutcome.HttpStatus(400, json?["reason"]?.GetValue<string>() ?? "rejected"), null);

        // Wavelog is not consistent about JSON types here: a real instance
        // returns "lastfetchedid":"1" as a *string* while "exported_qsos" is a
        // number, and either can arrive in the other shape. GetValue<int>() on a
        // string throws, which killed the whole sync loop on every cycle — a
        // plugin that could never sync against any real server, while passing a
        // full suite against a fake that returned tidy integers.
        var last = ReadInt(json["lastfetchedid"]) ?? fetchFromId;
        var count = ReadInt(json["exported_qsos"]) ?? 0;
        var adif = json["adif"]?.GetValue<string?>();
        return (WavelogOutcome.Success(), new PulledQsos(last, count, adif));
    }

    public async Task<(WavelogOutcome, IReadOnlyList<StationProfile>?)> GetStationInfoAsync(
        WavelogConfig config, CancellationToken ct)
    {
        // station_info is the odd one out. Every other endpoint reads its JSON
        // body from php://input; this one is `function station_info($key = '')`,
        // so CodeIgniter fills the key from a URL segment and a POSTed body is
        // never looked at. Sending the body form gets a 401 that reads exactly
        // like a bad key — which is how this survived a green suite against our
        // own fake, and was only found by calling the real server.
        var (outcome, json) = await GetAsync(
            config.Endpoint("station_info") + "/" + Uri.EscapeDataString(config.ApiKey), ct)
            .ConfigureAwait(false);
        if (!outcome.IsSuccess) return (outcome, null);

        if (json is not JsonArray arr)
            return (WavelogOutcome.HttpStatus(400,
                json?["reason"]?.GetValue<string>() ?? "unexpected reply"), null);

        var profiles = new List<StationProfile>();
        foreach (var node in arr)
        {
            var idText = node?["station_id"]?.ToString();
            if (int.TryParse(idText, out var id))
                profiles.Add(new StationProfile(id, node?["station_profile_name"]?.GetValue<string>() ?? $"profile {id}"));
        }
        return (WavelogOutcome.Success(), profiles);
    }

    public async Task<WavelogOutcome> PostRadioAsync(WavelogConfig config, RadioState state, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["key"] = config.ApiKey,
            ["radio"] = state.Radio,
            ["timestamp"] = DateTime.UtcNow.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture),
        };
        // Wavelog wants hertz here, and skips any value that is not numeric.
        if (state.FrequencyHz is { } hz) body["frequency"] = ((long)hz).ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(state.Mode)) body["mode"] = state.Mode;
        if (state.PowerW is { } w) body["power"] = w.ToString("0.##", CultureInfo.InvariantCulture);

        var (outcome, json) = await SendAsync(config.Endpoint("radio"), body, ct).ConfigureAwait(false);
        if (!outcome.IsSuccess) return outcome;

        var status = json?["status"]?.GetValue<string>();
        if (status is "successful" or "created") return WavelogOutcome.Success();

        var reason = json?["reason"]?.GetValue<string>() ?? "rejected";
        // A read-only key can never work here, so it is permanent, not transient.
        return reason.Contains("write permission", StringComparison.OrdinalIgnoreCase)
            ? WavelogOutcome.HttpStatus(403, reason)
            : WavelogOutcome.HttpStatus(400, reason);
    }

    /// <summary>
    /// One place to decide what a Wavelog reply means, shared by both verbs.
    /// </summary>
    private static async Task<(WavelogOutcome, JsonNode?)> ReadAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Parse the body anyway. Wavelog says a great deal in the body of a
            // 400 — including that the QSO we are "failing" to send is already
            // safely in the log — and a caller that only sees the status code
            // cannot tell that apart from a real rejection.
            JsonNode? failure = null;
            try { failure = JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "null" : text); }
            catch (JsonException) { /* not JSON; the trimmed text is all we have */ }
            return (WavelogOutcome.HttpStatus((int)response.StatusCode, Trim(text)), failure);
        }

        try
        {
            var json = JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "null" : text);
            if (json is null) return (WavelogOutcome.MalformedReply(Trim(text)), null);

            // "failed" is Wavelog's own rejection shape, and it arrives with a 200.
            if (json is JsonObject o && o["status"]?.GetValue<string>() == "failed")
            {
                var reason = o["reason"]?.GetValue<string>() ?? "rejected";
                // Order matters: "API key does not have write permissions"
                // contains "api key" but is a different problem with a
                // different fix, so it must be classified first.
                var status =
                    reason.Contains("write permission", StringComparison.OrdinalIgnoreCase) ? 403 :
                    reason.Contains("api key", StringComparison.OrdinalIgnoreCase) ? 401 : 400;
                return (WavelogOutcome.HttpStatus(status, reason), null);
            }
            return (WavelogOutcome.Success(), json);
        }
        catch (JsonException)
        {
            return (WavelogOutcome.MalformedReply(Trim(text)), null);
        }
    }

    /// <summary>
    /// Read an integer that Wavelog may have sent as a number or as a string.
    /// Never throws: a field we cannot make sense of is absent, not fatal.
    /// </summary>
    internal static int? ReadInt(JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            if (node.GetValueKind() == JsonValueKind.Number) return node.GetValue<int>();
            var text = node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToString();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when Wavelog rejected the post and <em>every</em> reason was that it
    /// already had the contact.
    ///
    /// <para>Deliberately strict. One item is posted per outbox row, so in
    /// practice this is a single message — but a reply that mixes a duplicate
    /// with a genuine error must still be treated as an error, or a real problem
    /// hides behind a benign one.</para>
    /// </summary>
    internal static bool IsOnlyDuplicates(JsonNode? json)
    {
        if (json?["messages"] is not JsonArray messages) return false;

        var sawDuplicate = false;
        foreach (var message in messages)
        {
            var text = message?.GetValueKind() == JsonValueKind.String
                ? message.GetValue<string>()
                : message?.ToString();
            if (string.IsNullOrWhiteSpace(text)) continue;   // Wavelog pads with ""

            if (text.Contains("Duplicate", StringComparison.OrdinalIgnoreCase))
                sawDuplicate = true;
            else
                return false;                                 // something else is wrong too
        }
        return sawDuplicate;
    }

    private async Task<(WavelogOutcome, JsonNode?)> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            return await ReadAsync(response, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (WavelogOutcome.Timeout(), null);
        }
        catch (HttpRequestException ex)
        {
            return (WavelogOutcome.NetworkError(ex.Message), null);
        }
    }

    private async Task<(WavelogOutcome, JsonNode?)> SendAsync(string url, object body, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(url, body, ct).ConfigureAwait(false);
            return await ReadAsync(response, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (WavelogOutcome.Timeout(), null);
        }
        catch (TaskCanceledException)
        {
            return (WavelogOutcome.Timeout(), null);
        }
        catch (HttpRequestException ex)
        {
            return (WavelogOutcome.NetworkError(ex.Message), null);
        }
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
