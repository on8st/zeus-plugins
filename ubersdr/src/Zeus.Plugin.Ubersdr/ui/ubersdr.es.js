// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// UberSDR Monitor — a wall of remote receivers listening to your transmission.
//
// Phase 1: pick receivers and show a live signal-to-noise figure from each.
// No audio is decoded here. While the operator is keyed we read the 21-byte
// header of every frame and drop the Opus payload; phase 2 keeps the payload
// so it can be played back after unkey.
//
// React arrives from the host as a bare specifier and there is no build step,
// so this is React.createElement rather than JSX.

import React, { useCallback, useEffect, useRef, useState } from 'react';

// UMD, imported for its side effect: in an ES module it takes the globalThis
// branch and registers itself. Vendored — see vendor/README.md.
import './vendor/opus-decoder.min.js';

const h = React.createElement;

const OpusDecoder = () => globalThis['opus-decoder']?.OpusDecoder;

// The audio frame header, confirmed byte for byte against live captures.
// See docs/design/source/protocol.md.
const HEADER_BYTES = 21;

function readHeader(buf) {
  if (buf.byteLength < HEADER_BYTES) return null;
  const v = new DataView(buf);
  const power = v.getFloat32(13, true);
  const noise = v.getFloat32(17, true);
  // The invalid sentinel is -Infinity, not -999: an instance with no antenna
  // sends it on every frame, and the first frames of any session carry it
  // before measurement settles. Withhold a reading rather than showing 0 dB.
  const snr = Number.isFinite(power) && Number.isFinite(noise) ? power - noise : null;
  return { sampleRate: v.getUint32(8, true), snr };
}

const css = {
  panel: { display: 'flex', flexDirection: 'column', gap: 12, padding: 14,
           fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)', fontSize: 13 },
  note: { color: 'var(--fg-3, #5a5e66)', fontSize: 11, lineHeight: 1.45 },
  head: { display: 'flex', alignItems: 'baseline', gap: 12, flexWrap: 'wrap' },
  freq: { fontFamily: 'var(--font-mono, ui-monospace, monospace)', fontSize: 20, fontWeight: 600 },
  keyed: { padding: '2px 8px', borderRadius: 3, fontWeight: 600, fontSize: 11, letterSpacing: '.08em' },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(230px, 1fr))', gap: 8 },
  tile: { border: '1px solid var(--border, #2a2e35)', borderRadius: 3, padding: '9px 11px',
          display: 'flex', flexDirection: 'column', gap: 5 },
  call: { fontWeight: 600 },
  bar: { height: 6, borderRadius: 3, background: 'var(--bg-2, #16181d)', overflow: 'hidden' },
  button: { background: 'var(--bg-3, #22262d)', color: 'var(--fg, #e6e8ea)',
            border: '1px solid var(--border, #2a2e35)', borderRadius: 3,
            padding: '5px 10px', font: 'inherit', cursor: 'pointer' },
  bad: { color: 'var(--danger, #e5715f)' },
};

// 0 dB reads as empty, 60 dB as full. Chosen from the live probe, which saw
// 34–56 dB from a receiver hearing a strong signal well.
const snrPercent = (snr) => Math.max(0, Math.min(100, (snr / 60) * 100));
const snrColour = (snr) =>
  snr >= 30 ? 'var(--success, #4fbfa0)' : snr >= 15 ? 'var(--warning, #d8a657)' : 'var(--danger, #e5715f)';

function Tile({ rx, reading }) {
  const snr = reading?.snr ?? null;
  return h('div', { style: css.tile },
    h('div', { style: { display: 'flex', justifyContent: 'space-between', gap: 8 } },
      h('span', { style: css.call }, rx.callsign || rx.host),
      h('span', { style: css.note },
        rx.distanceKm != null ? `${Math.round(rx.distanceKm)} km` : '',
        rx.bearingDegrees != null ? ` · ${rx.bearingDegrees}°` : '')),
    h('div', { style: { ...css.note, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' } },
      rx.location || rx.name),
    h('div', { style: css.bar },
      h('div', {
        style: {
          height: '100%',
          width: snr == null ? '0%' : `${snrPercent(snr)}%`,
          background: snr == null ? 'transparent' : snrColour(snr),
          transition: 'width .25s linear',
        },
      })),
    h('div', { style: { display: 'flex', justifyContent: 'space-between' } },
      h('span', { style: { fontFamily: 'var(--font-mono, ui-monospace, monospace)' } },
        snr == null ? '— dB' : `${snr.toFixed(1)} dB`),
      h('span', { style: css.note },
        reading?.state === 'open' ? 'listening'
          : reading?.state === 'error' ? h('span', { style: css.bad }, 'failed')
          : reading?.state ?? 'idle')));
}

function UbersdrPanel({ api }) {
  const [radio, setRadio] = useState(null);
  const [wall, setWall] = useState([]);
  const [info, setInfo] = useState(null);
  const [readings, setReadings] = useState({});
  const [message, setMessage] = useState(null);
  const [busy, setBusy] = useState(false);
  const [takes, setTakes] = useState([]);        // finished recordings, newest first
  const [playing, setPlaying] = useState(null);  // host currently being played back
  const sockets = useRef(new Map());
  const recording = useRef({ active: false, byHost: new Map(), startedAt: 0 });
  const audio = useRef({ ctx: null, source: null, decoder: null });

  // The radio is polled rather than subscribed to: the engine's own /ws carries
  // binary telemetry only, with no state or keying messages on it.
  useEffect(() => {
    let alive = true;
    const tick = async () => {
      try {
        const r = await api.callBackend('GET', '/radio');
        if (r.ok && alive) setRadio(await r.json());
      } catch { /* a restarting engine is not an error worth showing */ }
    };
    void tick();
    const t = window.setInterval(tick, 1000);
    return () => { alive = false; window.clearInterval(t); };
  }, [api]);

  // Keying is polled fast, and only while receivers are connected — there is no
  // state stream on the engine, and there is no reason to hammer it when the
  // panel is merely open. 10 Hz brackets a transmission to about a tenth of a
  // second, which is nothing beside a 200 ms tune and a multi-second over.
  const keyedRef = useRef(false);
  useEffect(() => {
    let alive = true;
    const tick = async () => {
      if (sockets.current.size === 0) return;
      try {
        const r = await api.callBackend('GET', '/ptt');
        if (!r.ok || !alive) return;
        const { keyed } = await r.json();
        if (keyed === keyedRef.current) return;
        keyedRef.current = keyed;

        if (keyed) {
          // Playing audio while the microphone is open is a delayed howl put on
          // the air. Nothing is audible while keyed, ever.
          stopPlayback();
          startRecording();
        } else {
          stopRecording();
        }
      } catch { /* a restarting engine is not an error worth showing */ }
    };
    const t = window.setInterval(tick, 100);
    return () => { alive = false; window.clearInterval(t); };
  }, [api, startRecording, stopRecording, stopPlayback]);

  const loadReceivers = useCallback(async () => {
    setBusy(true);
    try {
      const r = await api.callBackend('GET', '/receivers?count=6');
      if (!r.ok) { setMessage({ bad: true, text: 'could not read the receiver directory' }); return; }
      const d = await r.json();
      setInfo(d);
      setWall(d.suggested ?? []);
    } finally { setBusy(false); }
  }, [api]);

  useEffect(() => { void loadReceivers(); }, [loadReceivers]);

  const setReading = useCallback((host, patch) =>
    setReadings((prev) => ({ ...prev, [host]: { ...prev[host], ...patch } })), []);

  const disconnectAll = useCallback(() => {
    for (const ws of sockets.current.values()) { try { ws.close(); } catch { /* already gone */ } }
    sockets.current.clear();
    setReadings({});
  }, []);

  // Connect the whole wall. Admission goes through the backend so a refusal is
  // handled once, politely, rather than retried per tile.
  const connectAll = useCallback(async () => {
    if (!radio?.available) { setMessage({ bad: true, text: 'radio state unavailable' }); return; }
    disconnectAll();
    setBusy(true);
    setMessage(null);
    try {
      for (const rx of wall) {
        setReading(rx.host, { state: 'connecting', snr: null });
        let admit;
        try {
          const res = await api.callBackend('POST', '/connect', { host: rx.host });
          admit = await res.json();
          if (!res.ok) { setReading(rx.host, { state: 'error', note: admit.error }); continue; }
        } catch (e) { setReading(rx.host, { state: 'error', note: String(e) }); continue; }

        // Parameters go in the query string; `tune` only retunes an open socket.
        const url = `${admit.wsBase}/ws?frequency=${radio.transmitHz}`
          + `&mode=${encodeURIComponent((radio.mode || 'usb').toLowerCase())}`
          + `&user_session_id=${encodeURIComponent(admit.sessionId)}`
          + `&format=opus&version=${admit.version}`;

        const ws = new WebSocket(url);
        ws.binaryType = 'arraybuffer';
        ws.onopen = () => setReading(rx.host, { state: 'open' });
        ws.onmessage = (ev) => {
          if (typeof ev.data === 'string') return;      // status/error JSON
          const hdr = readHeader(ev.data);
          if (!hdr) return;

          setReading(rx.host, { snr: hdr.snr });

          // While keyed: keep the Opus payload, undecoded, and hold the peak.
          // Undecoded is the whole reason a long list is affordable — the
          // metering above needs 21 bytes, not a decoder per receiver.
          const rec = recording.current;
          if (!rec.active) return;
          const slot = rec.byHost.get(rx.host);
          if (!slot) return;

          const payload = new Uint8Array(ev.data, HEADER_BYTES);
          if (payload.length > 0) slot.frames.push(payload.slice());
          slot.sampleRate = hdr.sampleRate || slot.sampleRate;
          if (hdr.snr != null) {
            slot.peakSnr = slot.peakSnr == null ? hdr.snr : Math.max(slot.peakSnr, hdr.snr);
            slot.snrCount++;
          }
        };
        ws.onerror = () => setReading(rx.host, { state: 'error' });
        ws.onclose = () => setReading(rx.host, { state: 'closed' });
        sockets.current.set(rx.host, ws);
      }
    } finally { setBusy(false); }
  }, [api, radio, wall, disconnectAll, setReading]);

  // ---- recording, bracketed by the key -----------------------------------

  const startRecording = useCallback(() => {
    const rec = recording.current;
    rec.byHost = new Map();
    for (const host of sockets.current.keys())
      rec.byHost.set(host, { frames: [], sampleRate: 48000, peakSnr: null, snrCount: 0 });
    rec.active = true;
    rec.startedAt = Date.now();
  }, []);

  const stopRecording = useCallback(() => {
    const rec = recording.current;
    if (!rec.active) return;
    rec.active = false;

    const seconds = (Date.now() - rec.startedAt) / 1000;
    const rows = [...rec.byHost.entries()]
      .map(([host, slot]) => ({
        host,
        frames: slot.frames,
        sampleRate: slot.sampleRate,
        peakSnr: slot.peakSnr,
        bytes: slot.frames.reduce((n, f) => n + f.length, 0),
      }))
      .filter((r) => r.frames.length > 0);

    if (rows.length === 0) return;
    setTakes((prev) => [{ at: new Date(), seconds, rows }, ...prev].slice(0, 8));
  }, []);

  // ---- playback ------------------------------------------------------------

  const stopPlayback = useCallback(() => {
    const a = audio.current;
    try { a.source?.stop(); } catch { /* already stopped */ }
    a.source = null;
    setPlaying(null);
  }, []);

  const play = useCallback(async (take, row) => {
    stopPlayback();
    const Decoder = OpusDecoder();
    if (!Decoder) { setMessage({ bad: true, text: 'the Opus decoder did not load' }); return; }

    setPlaying(row.host);
    try {
      // Decode on demand, one receiver at a time. This is the only place a
      // decoder runs — never while the operator is keyed, and never once per
      // receiver at the same time.
      const decoder = new Decoder();
      await decoder.ready;
      const { channelData, samplesDecoded, sampleRate } = await decoder.decodeFrames(row.frames);
      decoder.free();

      if (!samplesDecoded) { setMessage({ bad: true, text: 'nothing decodable in that recording' }); setPlaying(null); return; }

      audio.current.ctx ??= new (window.AudioContext || window.webkitAudioContext)();
      const ctx = audio.current.ctx;
      if (ctx.state === 'suspended') await ctx.resume();

      const buf = ctx.createBuffer(channelData.length, samplesDecoded, sampleRate);
      channelData.forEach((ch, i) => buf.copyToChannel(ch, i));

      const src = ctx.createBufferSource();
      src.buffer = buf;
      src.connect(ctx.destination);
      src.onended = () => setPlaying((cur) => (cur === row.host ? null : cur));
      src.start();
      audio.current.source = src;
    } catch (e) {
      setMessage({ bad: true, text: 'playback failed: ' + e });
      setPlaying(null);
    }
  }, [stopPlayback]);

  // Never leave sockets open on someone else's receiver.
  useEffect(() => () => disconnectAll(), [disconnectAll]);

  const listening = sockets.current.size > 0;

  return h('div', { style: css.panel },

    h('div', { style: css.note },
      'Remote receivers listening to your transmit frequency. Each records while '
      + 'you are keyed and can be played back after you unkey — never during, so '
      + 'an open microphone cannot feed back. The figure is signal-to-noise in dB '
      + 'at that receiver: compare a receiver against itself over time, not '
      + 'receivers against each other, since a quiet site reads better than a '
      + 'noisy one for the same signal.'),

    h('div', { style: css.head },
      h('span', { style: css.freq },
        radio?.available ? `${(radio.transmitHz / 1e6).toFixed(3)} MHz` : '—'),
      h('span', { style: css.note }, radio?.mode || ''),
      radio?.band ? h('span', { style: css.note }, radio.band) : null,
      radio?.splitEnabled
        ? h('span', { style: { ...css.note, color: 'var(--warning, #d8a657)' } }, 'SPLIT — monitoring the TX frequency')
        : null,
      h('span', {
        style: {
          ...css.keyed,
          background: radio?.moxOn ? 'var(--danger, #e5715f)' : 'var(--bg-2, #16181d)',
          color: radio?.moxOn ? '#fff' : 'var(--fg-3, #5a5e66)',
        },
      }, radio?.moxOn ? 'ON AIR' : 'RECEIVE')),

    h('div', { style: { display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' } },
      h('button', { style: css.button, disabled: busy || !wall.length, onClick: connectAll },
        listening ? 'reconnect' : `listen on ${wall.length} receivers`),
      listening
        ? h('button', { style: css.button, disabled: busy, onClick: disconnectAll }, 'release them')
        : null,
      h('button', { style: css.button, disabled: busy, onClick: loadReceivers }, 'refresh list'),
      message ? h('span', { style: message.bad ? css.bad : null }, message.text) : null),

    h('div', { style: css.grid },
      wall.map((rx) => h(Tile, { key: rx.host, rx, reading: readings[rx.host] }))),

    takes.length
      ? h('div', { style: { display: 'flex', flexDirection: 'column', gap: 6 } },
          h('div', { style: { ...css.note, textTransform: 'uppercase', letterSpacing: '.1em' } },
            'recordings'),
          takes.map((take, ti) =>
            h('div', {
              key: take.at.getTime(),
              style: { border: '1px solid var(--border, #2a2e35)', borderRadius: 3, padding: '8px 10px',
                       display: 'flex', flexDirection: 'column', gap: 5 },
            },
              h('div', { style: css.note },
                `${take.at.toLocaleTimeString()} · ${take.seconds.toFixed(1)} s`
                + (ti === 0 ? ' · latest' : '')),
              h('div', { style: { display: 'flex', flexWrap: 'wrap', gap: 6 } },
                take.rows.map((row) =>
                  h('button', {
                    key: row.host,
                    style: {
                      ...css.button,
                      borderColor: playing === row.host ? 'var(--success, #4fbfa0)' : undefined,
                    },
                    onClick: () => (playing === row.host ? stopPlayback() : play(take, row)),
                  },
                    `${row.host.split('.')[0]} · `,
                    // The peak across the whole over, not an instantaneous
                    // reading arriving seconds after the speech that caused it.
                    row.peakSnr == null ? '— dB' : `${row.peakSnr.toFixed(0)} dB`,
                    playing === row.host ? ' ■' : ' ▶'))))))
      : null,

    info
      ? h('div', { style: css.note },
          `${info.total} receivers in the directory · ${info.excludedNoAntenna} excluded `
          + `(no antenna, so they cannot report a level) · ${info.offline} offline`)
      : null,

    h('div', { style: css.note },
      'Each receiver you listen on occupies a slot on somebody else’s hardware. '
      + 'Release them when you are done.'));
}

export default function register(api) {
  api.registerPanel({
    id: 'ubersdr.monitor',
    component: () => h(UbersdrPanel, { api }),
  });
}
