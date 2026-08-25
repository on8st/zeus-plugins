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

const h = React.createElement;

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
  const sockets = useRef(new Map());

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
          if (hdr) setReading(rx.host, { snr: hdr.snr });
        };
        ws.onerror = () => setReading(rx.host, { state: 'error' });
        ws.onclose = () => setReading(rx.host, { state: 'closed' });
        sockets.current.set(rx.host, ws);
      }
    } finally { setBusy(false); }
  }, [api, radio, wall, disconnectAll, setReading]);

  // Never leave sockets open on someone else's receiver.
  useEffect(() => () => disconnectAll(), [disconnectAll]);

  const listening = sockets.current.size > 0;

  return h('div', { style: css.panel },

    h('div', { style: css.note },
      'Remote receivers listening to your transmit frequency. The figure is '
      + 'signal-to-noise in dB at each receiver — compare a receiver against '
      + 'itself over time, not receivers against each other, since a quiet site '
      + 'reads better than a noisy one for the same signal.'),

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
        listening ? 'reconnect the wall' : `listen on ${wall.length} receivers`),
      listening
        ? h('button', { style: css.button, disabled: busy, onClick: disconnectAll }, 'release them')
        : null,
      h('button', { style: css.button, disabled: busy, onClick: loadReceivers }, 'refresh list'),
      message ? h('span', { style: message.bad ? css.bad : null }, message.text) : null),

    h('div', { style: css.grid },
      wall.map((rx) => h(Tile, { key: rx.host, rx, reading: readings[rx.host] }))),

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
