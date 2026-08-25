// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugin.Ubersdr.Domain;

namespace Zeus.Plugin.Ubersdr.Tests;

/// <summary>
/// The 21-byte header, against bytes captured from live instances.
///
/// <para>These are not hand-made fixtures. Each is a real frame, hexdumped from
/// a socket during the phase-0 protocol probe — one from an instance with an
/// antenna and one from an instance without. The Wavelog work in this repository
/// established why that matters: a fake built from one's own reading of an API
/// validates one's own misreading, and did so three times.</para>
/// </summary>
public class AudioFrameHeaderTests
{
    // Captured from a receiver with no antenna, tuned to 7.200 MHz LSB.
    // 12000 Hz, 1 channel, both power fields -Infinity, 8 bytes of Opus.
    private static readonly byte[] NoAntennaFrame = Hex(
        "d0 4d fc c9 5a 22 cf 18 e0 2e 00 00 01 00 00 80 ff 00 00 80 ff 28 0b e4 b9 9e 78 48 54");

    // Captured from a receiver with an antenna, tuned to 9.5 MHz AM: 24000 Hz.
    private static readonly byte[] LiveFrame = Hex(
        "ed d1 87 c2 61 22 cf 18 c0 5d 00 00 01 00 00 80 ff 00 00 80 ff 68 0b e4 c1 22 23 61 f9");

    [Fact]
    public void The_header_is_parsed_as_the_protocol_documents_it()
    {
        Assert.True(AudioFrameHeader.TryParse(NoAntennaFrame, out var h));

        Assert.Equal(12000u, h.SampleRateHz);
        Assert.Equal(1, h.Channels);
        Assert.Equal(AudioFrameHeader.Size + 8, NoAntennaFrame.Length);
    }

    [Fact]
    public void A_higher_rate_mode_parses_the_same_way()
    {
        Assert.True(AudioFrameHeader.TryParse(LiveFrame, out var h));
        Assert.Equal(24000u, h.SampleRateHz);      // AM at 24 kHz, SSB at 12 kHz
    }

    // ---- the sentinel that is not what the published client checks for ------

    [Fact]
    public void Negative_infinity_means_no_reading_rather_than_a_terrible_one()
    {
        // 0xff800000 in both power fields. The published client guards with
        // "> -900", which catches this only by accident; anything testing for
        // == -999.0 would report an SNR of zero from a receiver measuring
        // nothing, which is the most misleading answer available.
        Assert.True(AudioFrameHeader.TryParse(NoAntennaFrame, out var h));

        Assert.Equal(float.NegativeInfinity, h.BasebandPowerDb);
        Assert.Equal(float.NegativeInfinity, h.NoisePowerDb);
        Assert.Null(h.SnrDb);
    }

    [Fact]
    public void A_real_measurement_becomes_a_signal_to_noise_figure()
    {
        // Synthesised from the confirmed layout, with values in the range the
        // live probe actually observed (34.7 – 55.7 dB).
        var frame = FrameWith(basebandPower: -32.5f, noisePower: -75.0f);

        Assert.True(AudioFrameHeader.TryParse(frame, out var h));
        Assert.Equal(42.5, h.SnrDb!.Value, 3);
    }

    [Fact]
    public void One_bad_field_is_enough_to_withhold_a_reading()
    {
        Assert.Null(Parse(FrameWith(-32.5f, float.NegativeInfinity)).SnrDb);
        Assert.Null(Parse(FrameWith(float.NegativeInfinity, -75.0f)).SnrDb);
        Assert.Null(Parse(FrameWith(float.NaN, -75.0f)).SnrDb);
    }

    // ---- payload handling ---------------------------------------------------

    [Fact]
    public void The_payload_is_everything_after_the_header()
    {
        var payload = AudioFrameHeader.Payload(NoAntennaFrame);
        Assert.Equal(8, payload.Length);
        Assert.Equal(0x28, payload[0]);      // first Opus byte in the capture
    }

    [Fact]
    public void A_frame_with_no_payload_yields_an_empty_span_not_an_exception()
    {
        var headerOnly = new byte[AudioFrameHeader.Size];
        Assert.True(AudioFrameHeader.Payload(headerOnly).IsEmpty);
    }

    [Fact]
    public void A_truncated_frame_is_refused_rather_than_throwing()
    {
        // A short read is a network event, not a bug, and must not take down
        // the socket loop for every other receiver on the wall.
        Assert.False(AudioFrameHeader.TryParse(NoAntennaFrame.AsSpan(0, 20), out _));
        Assert.False(AudioFrameHeader.TryParse([], out _));
    }

    // ---- helpers ------------------------------------------------------------

    private static AudioFrameHeader Parse(byte[] f)
    {
        Assert.True(AudioFrameHeader.TryParse(f, out var h));
        return h;
    }

    private static byte[] FrameWith(float basebandPower, float noisePower)
    {
        var f = (byte[])NoAntennaFrame.Clone();
        BitConverter.GetBytes(basebandPower).CopyTo(f, 13);
        BitConverter.GetBytes(noisePower).CopyTo(f, 17);
        return f;
    }

    private static byte[] Hex(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
         .Select(b => Convert.ToByte(b, 16)).ToArray();
}
