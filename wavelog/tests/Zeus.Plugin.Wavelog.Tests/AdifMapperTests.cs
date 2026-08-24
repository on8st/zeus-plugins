// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugin.Wavelog.Adif;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugin.Wavelog.Tests;

/// <summary>
/// The mapper is pure, so these are the cheapest tests in the plugin and cover
/// the most damage: Wavelog dedupes on CALL + TIME_ON to the minute + BAND +
/// MODE, so a formatting slip here does not fail loudly — it silently creates
/// duplicates.
/// </summary>
public class AdifMapperTests
{
    private static LogbookEntrySnapshot Entry(
        string call = "dl1abc",
        double? freqMhz = 14.074,
        string band = "20m",
        string mode = "USB",
        DateTime? when = null,
        string? comment = null,
        string? grid = null,
        Dictionary<string, string>? adif = null) =>
        new(
            Id: "1",
            QsoDateTimeUtc: when ?? new DateTime(2026, 8, 24, 9, 5, 3, DateTimeKind.Utc),
            Callsign: call,
            Name: null,
            FrequencyMhz: freqMhz,
            Band: band,
            Mode: mode,
            RstSent: "59",
            RstRcvd: "57",
            Grid: grid,
            Country: null,
            Dxcc: null,
            CqZone: null,
            ItuZone: null,
            State: null,
            Comment: comment,
            CreatedUtc: DateTime.UtcNow,
            AdifFields: adif);

    private static string Field(string record, string name)
    {
        var tag = $"<{name}:";
        var i = record.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        var colon = i + tag.Length;
        var close = record.IndexOf('>', colon);
        var lenText = record[colon..close];
        // a typed field carries "<NAME:len:TYPE>"
        var sep = lenText.IndexOf(':');
        if (sep >= 0) lenText = lenText[..sep];
        var len = int.Parse(lenText);
        return record.Substring(close + 1, len);
    }

    // ---- the fields Wavelog dedupes on --------------------------------------

    [Fact]
    public void Callsign_is_upper_cased()
        => Assert.Equal("DL1ABC", Field(AdifMapper.ToRecord(Entry(call: "dl1abc")), "CALL"));

    [Fact]
    public void Qso_date_is_yyyymmdd_in_utc()
        => Assert.Equal("20260824", Field(AdifMapper.ToRecord(Entry()), "QSO_DATE"));

    [Fact]
    public void Time_on_keeps_seconds()
        => Assert.Equal("090503", Field(AdifMapper.ToRecord(Entry()), "TIME_ON"));

    [Fact]
    public void Non_utc_timestamps_are_converted_not_relabelled()
    {
        var local = new DateTime(2026, 8, 24, 11, 5, 3, DateTimeKind.Local);
        var record = AdifMapper.ToRecord(Entry(when: local));
        var expected = local.ToUniversalTime();
        Assert.Equal(expected.ToString("yyyyMMdd"), Field(record, "QSO_DATE"));
        Assert.Equal(expected.ToString("HHmmss"), Field(record, "TIME_ON"));
    }

    [Fact]
    public void Band_is_lower_case_as_adif_expects()
        => Assert.Equal("20m", Field(AdifMapper.ToRecord(Entry(band: "20M")), "BAND"));

    // ---- frequency ----------------------------------------------------------

    [Fact]
    public void Frequency_is_megahertz_to_six_decimals()
        => Assert.Equal("14.074000", Field(AdifMapper.ToRecord(Entry(freqMhz: 14.074)), "FREQ"));

    [Fact]
    public void Frequency_uses_invariant_culture_not_the_machine_locale()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("nl-BE");
        try
        {
            Assert.Equal("14.074000", Field(AdifMapper.ToRecord(Entry(freqMhz: 14.074)), "FREQ"));
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    [Fact]
    public void Frequency_is_omitted_when_unknown()
        => Assert.DoesNotContain("<FREQ:", AdifMapper.ToRecord(Entry(freqMhz: null)));

    // ---- mode and submode ---------------------------------------------------

    [Theory]
    [InlineData("USB", "SSB", "USB")]
    [InlineData("LSB", "SSB", "LSB")]
    [InlineData("CWU", "CW", null)]
    [InlineData("CWL", "CW", null)]
    [InlineData("AM", "AM", null)]
    [InlineData("FM", "FM", null)]
    public void Mode_splits_into_mode_and_submode(string zeus, string mode, string? submode)
    {
        var record = AdifMapper.ToRecord(Entry(mode: zeus));
        Assert.Equal(mode, Field(record, "MODE"));
        if (submode is null) Assert.DoesNotContain("<SUBMODE:", record);
        else Assert.Equal(submode, Field(record, "SUBMODE"));
    }

    [Fact]
    public void An_explicit_adif_mode_wins_over_the_zeus_mode()
    {
        // WSJT-X and friends know the real mode; Zeus only knows the sideband.
        var record = AdifMapper.ToRecord(
            Entry(mode: "DIGU", adif: new() { ["MODE"] = "FT8" }));
        Assert.Equal("FT8", Field(record, "MODE"));
        Assert.DoesNotContain("<SUBMODE:", record);
    }

    [Fact]
    public void An_unmapped_mode_passes_through_rather_than_being_guessed()
    {
        // Guessing an ADIF equivalent would create duplicates in Wavelog, which
        // compares MODE exactly. Passing through is wrong loudly, not quietly.
        Assert.Equal("SAM", Field(AdifMapper.ToRecord(Entry(mode: "SAM")), "MODE"));
    }

    // ---- optional fields ----------------------------------------------------

    [Fact]
    public void Absent_optional_fields_are_omitted_not_emitted_empty()
    {
        var record = AdifMapper.ToRecord(Entry(comment: null, grid: null));
        Assert.DoesNotContain("<COMMENT:", record);
        Assert.DoesNotContain("<GRIDSQUARE:", record);
    }

    [Fact]
    public void Empty_strings_count_as_absent()
        => Assert.DoesNotContain("<COMMENT:", AdifMapper.ToRecord(Entry(comment: "   ")));

    [Fact]
    public void Present_optional_fields_are_emitted()
    {
        var record = AdifMapper.ToRecord(Entry(grid: "JO21", comment: "nice sig"));
        Assert.Equal("JO21", Field(record, "GRIDSQUARE"));
        Assert.Equal("nice sig", Field(record, "COMMENT"));
    }

    // ---- the length prefix does the escaping --------------------------------

    [Fact]
    public void Angle_brackets_in_a_comment_need_no_escaping()
    {
        // ADIF is length-prefixed, so the value is read by count, not by
        // scanning for a delimiter. This is why no escaping exists — and why a
        // wrong length is corrupting rather than merely ugly.
        const string awkward = "worked <him> on 20m & 40m";
        var record = AdifMapper.ToRecord(Entry(comment: awkward));
        Assert.Equal(awkward, Field(record, "COMMENT"));
    }

    [Fact]
    public void Length_prefix_counts_utf8_bytes_not_characters()
    {
        const string accented = "grüße";           // 5 chars, 7 UTF-8 bytes
        var record = AdifMapper.ToRecord(Entry(comment: accented));
        Assert.Contains("<COMMENT:7>", record);
    }

    // ---- record framing -----------------------------------------------------

    [Fact]
    public void Record_ends_with_eor()
        => Assert.EndsWith("<EOR>", AdifMapper.ToRecord(Entry()).TrimEnd());

    [Fact]
    public void Extra_adif_fields_are_carried_through()
    {
        var record = AdifMapper.ToRecord(Entry(adif: new() { ["MY_SIG"] = "POTA" }));
        Assert.Equal("POTA", Field(record, "MY_SIG"));
    }

    [Fact]
    public void Extra_adif_fields_never_overwrite_a_typed_field()
    {
        var record = AdifMapper.ToRecord(
            Entry(call: "dl1abc", adif: new() { ["CALL"] = "SOMEONE_ELSE" }));
        Assert.Equal("DL1ABC", Field(record, "CALL"));
    }
}
