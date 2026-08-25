// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// Zeus's own RX audio, read from the engine's WebSocket.
//
// The engine withholds the audio stream in desktop mode until a client asks for
// it, and its own contract says why: "so a browser-side consumer (e.g. a CW
// decoder panel) can be fed without duplicating the audio stream". A panel is an
// intended consumer, and this is how the left ear gets filled.
//
// Wire format read from Zeus.Contracts/WireFormat.cs and AudioFrame.cs:
//
//   header, 16 bytes
//     0      msgType      u8      0x02 = AudioPcm
//     1      flags        u8
//     2–3    payloadLen   u16 LE
//     4–7    seq          u32 LE
//     8–15   tsUnixMs     f64 LE
//   body
//     0      rxId         u8
//     1      channels     u8
//     2–5    sampleRateHz u32 LE
//     6–7    sampleCount  u16 LE
//     8+     samples      f32 LE × sampleCount × channels
//
// Client → server: [0x21, enable] starts or stops the stream, refcounted.

export const MSG_AUDIO_PCM = 0x02;
export const MSG_AUDIO_STREAM_REQUEST = 0x21;
const HEADER = 16;
const BODY_HEADER = 8;

/** Ask the engine to start or stop sending RX audio to this socket. */
export function requestAudio(ws, enable) {
  if (ws?.readyState !== WebSocket.OPEN) return false;
  ws.send(new Uint8Array([MSG_AUDIO_STREAM_REQUEST, enable ? 1 : 0]));
  return true;
}

/**
 * Parse one frame. Returns null for anything that is not audio — the socket
 * carries display frames and control messages on the same wire, and a panel
 * that threw on those would die on the first spectrum update.
 */
export function parseAudioFrame(buf) {
  if (!buf || buf.byteLength < HEADER + BODY_HEADER) return null;
  const v = new DataView(buf);
  if (v.getUint8(0) !== MSG_AUDIO_PCM) return null;

  const payloadLen = v.getUint16(2, true);
  if (buf.byteLength < HEADER + payloadLen) return null;

  const channels = v.getUint8(HEADER + 1);
  const sampleRate = v.getUint32(HEADER + 2, true);
  const sampleCount = v.getUint16(HEADER + 6, true);
  if (channels === 0 || sampleCount === 0) return null;

  const floats = sampleCount * channels;
  const need = HEADER + BODY_HEADER + floats * 4;
  if (buf.byteLength < need) return null;

  // The samples are f32le and may be unaligned in the frame, so copy rather
  // than viewing in place — a misaligned Float32Array constructor throws.
  const samples = new Float32Array(floats);
  const src = new DataView(buf, HEADER + BODY_HEADER, floats * 4);
  for (let i = 0; i < floats; i++) samples[i] = src.getFloat32(i * 4, true);

  return {
    rxId: v.getUint8(HEADER),
    channels,
    sampleRate,
    sampleCount,
    samples,
    seq: v.getUint32(4, true),
  };
}
