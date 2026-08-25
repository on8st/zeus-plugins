// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;
using FakeWavelog;
using Zeus.Plugin.Wavelog.Sync;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>
/// Wavelog reports "I already have this QSO" as a failure, and it is not one.
///
/// <para>Deduplication on callsign + time to the minute + band + mode + station
/// is what makes at-least-once delivery safe here: a POST that timed out but
/// actually landed can be retried without creating a second contact. The design
/// leaned on that and called the retry question closed.</para>
///
/// <para>What a real instance actually returns for that retry is <b>HTTP 400,
/// <c>status: "abort"</c></b>, with <c>"Duplicate for ON0XYZ"</c> among the
/// messages — and the retry policy dead-letters a 400. So the operator would see
/// a permanent failure for a QSO sitting in their log, and pressing retry would
/// fail forever. The queue would never drain and the number would never mean
/// anything again.</para>
///
/// <para>Found by pushing the same contact twice at a real instance. The fake
/// used to answer "created" with a zero count, so nothing here could fail.</para>
/// </summary>
public sealed class DuplicateIsSuccessTests : IDisposable
{
    private readonly FakeWavelogServer _wavelog = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public DuplicateIsSuccessTests() => _wavelog.Start();
    public void Dispose() { _wavelog.Dispose(); _http.Dispose(); }

    private WavelogConfig Config => new()
    {
        BaseUrl = _wavelog.BaseUrl, ApiKey = _wavelog.ApiKey, StationProfileId = 1,
    };

    private const string Qso =
        "<CALL:6>DL1ABC<QSO_DATE:8>20260824<TIME_ON:6>090000<BAND:3>20m<MODE:3>SSB<EOR>";

    // ---- the behaviour that matters ----------------------------------------

    [Fact]
    public async Task Sending_the_same_qso_twice_succeeds_both_times()
    {
        var transport = new HttpWavelogTransport(_http);

        var first = await transport.PostQsoAsync(Config, Qso, default);
        Assert.True(first.IsSuccess);

        // The retry. The QSO is already there, which is the outcome we wanted.
        var second = await transport.PostQsoAsync(Config, Qso, default);
        Assert.True(second.IsSuccess, "a duplicate must not dead-letter a QSO that is safely logged");
    }

    [Fact]
    public async Task A_duplicate_does_not_create_a_second_contact()
    {
        var transport = new HttpWavelogTransport(_http);
        await transport.PostQsoAsync(Config, Qso, default);
        await transport.PostQsoAsync(Config, Qso, default);

        Assert.Single(_wavelog.Rows);
    }

    // ---- the exact shape a real instance sends ------------------------------

    [Fact]
    public void The_real_duplicate_reply_is_read_as_a_duplicate()
    {
        // Verbatim from a real instance, including the empty first message.
        var reply = JsonNode.Parse("""
            {"status":"abort","type":"adif","string":"","adif_count":1,"adif_errors":1,
             "messages":["","Date/Time: 2026-01-01 12:00:00 Callsign: ON0HARNESS Band: 20m Duplicate for ON0XYZ<br>"]}
            """);

        Assert.True(HttpWavelogTransport.IsOnlyDuplicates(reply));
    }

    [Fact]
    public void A_real_error_alongside_a_duplicate_is_still_an_error()
    {
        // Strictness on purpose: a genuine problem must not hide behind a benign
        // one just because they arrived in the same reply.
        var reply = JsonNode.Parse("""
            {"status":"abort","messages":["","Duplicate for ON0XYZ<br>","Invalid band: 21m<br>"]}
            """);

        Assert.False(HttpWavelogTransport.IsOnlyDuplicates(reply));
    }

    [Fact]
    public void An_ordinary_rejection_is_not_mistaken_for_a_duplicate()
    {
        Assert.False(HttpWavelogTransport.IsOnlyDuplicates(
            JsonNode.Parse("""{"status":"failed","reason":"missing or invalid api key"}""")));
        Assert.False(HttpWavelogTransport.IsOnlyDuplicates(
            JsonNode.Parse("""{"status":"abort","messages":["","Invalid band<br>"]}""")));
        Assert.False(HttpWavelogTransport.IsOnlyDuplicates(JsonNode.Parse("""{"messages":[]}""")));
        Assert.False(HttpWavelogTransport.IsOnlyDuplicates(null));
    }
}
