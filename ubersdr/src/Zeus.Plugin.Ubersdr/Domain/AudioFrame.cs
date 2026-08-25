// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using System.Buffers.Binary;

namespace Zeus.Plugin.Ubersdr.Domain;

/// <summary>
/// The 21-byte header UberSDR puts in front of every Opus payload.
///
/// <para>Confirmed byte for byte against live captures from two instances — see
/// <c>docs/design/source/protocol.md</c>. The published client notes that a
/// follower can drive signal bars "from the frame header alone", and that is the
/// property the monitor is built on: while the operator is keyed we read 21
/// bytes per frame from every receiver and keep the payload undecoded.</para>
/// </summary>
public readonly record struct AudioFrameHeader(
    ulong TimestampNs,
    uint SampleRateHz,
    byte Channels,
    float BasebandPowerDb,
    float NoisePowerDb)
{
    public const int Size = 21;

    /// <summary>
    /// Signal-to-noise in dB, or <c>null</c> when the instance is not reporting
    /// a measurement.
    ///
    /// <para>The invalid sentinel on the wire is <b>negative infinity</b>
    /// (<c>0xff800000</c>), not the <c>-999.0</c> the published client guards
    /// against with <c>&gt; -900</c> — that check catches it only by luck. It
    /// arrives on the first frames before measurement settles, and on every
    /// frame from an instance whose antenna is disconnected.</para>
    /// </summary>
    public double? SnrDb =>
        float.IsFinite(BasebandPowerDb) && float.IsFinite(NoisePowerDb)
            ? BasebandPowerDb - NoisePowerDb
            : null;

    /// <summary>
    /// Parse the header. Returns <c>false</c> for anything shorter than the
    /// header itself rather than throwing — a truncated frame is a network
    /// event, not a programming error.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> frame, out AudioFrameHeader header)
    {
        if (frame.Length < Size) { header = default; return false; }

        header = new AudioFrameHeader(
            TimestampNs: BinaryPrimitives.ReadUInt64LittleEndian(frame[..8]),
            SampleRateHz: BinaryPrimitives.ReadUInt32LittleEndian(frame[8..12]),
            Channels: frame[12],
            BasebandPowerDb: BinaryPrimitives.ReadSingleLittleEndian(frame[13..17]),
            NoisePowerDb: BinaryPrimitives.ReadSingleLittleEndian(frame[17..21]));
        return true;
    }

    /// <summary>The Opus payload, which we keep and do not decode until asked.</summary>
    public static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> frame) =>
        frame.Length <= Size ? default : frame[Size..];
}
