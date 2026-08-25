// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// QSO Assist — dual-mono diversity, with no knobs.
//
// Left ear: the radio in front of you. Right ear: whichever remote receiver is
// currently copying this frequency best. It picks that receiver itself and keeps
// re-checking, because during a QSO the operator has better things to do than
// audition receivers.
//
// Why cross-receiver SNR is the right measure *here*, when it is the wrong one
// on the map: the question is "who copies this station best", and a quiet site
// genuinely does copy better. On the map the question is "how is my antenna
// doing", and there a quiet site merely flatters itself.
//
// Courtesy is the hard constraint. Every connection is a session on hardware
// somebody else pays for, so this holds a small, fixed number and rotates
// slowly rather than sampling everything it can reach.

import React, { useCallback, useEffect, useRef, useState } from 'react';
import './vendor/opus-decoder.min.js';
import { parseAudioFrame, requestAudio } from './engine-audio.js';

const h = React.createElement;
const OpusDecoder = () => globalThis['opus-decoder']?.OpusDecoder;
const HEADER_BYTES = 21;

/** How many remote receivers are held open at once. Deliberately small. */
export const CONNECTION_BUDGET = 3;
/** How long a receiver keeps its place before the worst one is rotated out. */
export const ROTATE_AFTER_MS = 90_000;

const css = {
  panel: { display: 'flex', flexDirection: 'column', gap: 10, padding: 12,
           fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)', fontSize: 13 },
  note: { color: 'var(--fg-3, #5a5e66)', fontSize: 11, lineHeight: 1.45 },
  ears: { display: 'flex', gap: 8, alignItems: 'stretch' },
  ear: { flex: 1, border: '1px solid var(--border, #2a2e35)', borderRadius: 3,
         padding: '8px 10px', display: 'flex', flexDirection: 'column', gap: 3 },
  earLabel: { fontSize: 10, letterSpacing: '.1em', textTransform: 'uppercase',
              color: 'var(--fg-3, #5a5e66)' },
  who: { fontWeight: 600 },
  snr: { fontFamily: 'var(--font-mono, ui-monospace, monospace)' },
  button: { background: 'var(--bg-3, #22262d)', color: 'var(--fg, #e6e8ea)',
            border: '1px solid var(--border, #2a2e35)', borderRadius: 3,
            padding: '5px 10px', font: 'inherit', cursor: 'pointer' },
  bad: { color: 'var(--danger, #e5715f)' },
};

function readSnr(buf) {
  if (buf.byteLength < HEADER_BYTES) return null;
  const v = new DataView(buf);
  const p = v.getFloat32(13, true);
  const n = v.getFloat32(17, true);
  return Number.isFinite(p) && Number.isFinite(n) ? p - n : null;
}

export function QsoAssistPanel({ api }) {
  const [running, setRunning] = useState(false);
  const [radio, setRadio] = useState(null);
  const [best, setBest] = useState(null);       // { host, snr }
  const [pool, setPool] = useState([]);         // [{ host, snr, since }]
  const [message, setMessage] = useState(null);
  const [localLevel, setLocalLevel] = useState(0);

  const audio = useRef({ ctx: null, left: null, right: null, nextLeft: 0, nextRight: 0 });
  const remote = useRef(new Map());             // host -> { ws, decoder, snr, since }
  const engineWs = useRef(null);
  const bestRef = useRef(null);
  const radioRef = useRef(null);

  // ---- audio graph --------------------------------------------------------
  //
  // Two mono sources hard-panned. Built once and kept: rebuilding a graph
  // mid-QSO is audible.
  const ensureAudio = useCallback(async () => {
    const a = audio.current;
    if (a.ctx) return a;
    const ctx = new (window.AudioContext || window.webkitAudioContext)();
    if (ctx.state === 'suspended') await ctx.resume();

    const merge = ctx.createChannelMerger(2);
    const left = ctx.createGain();
    const right = ctx.createGain();
    left.connect(merge, 0, 0);
    right.connect(merge, 0, 1);
    merge.connect(ctx.destination);

    Object.assign(a, { ctx, left, right, nextLeft: 0, nextRight: 0 });
    return a;
  }, []);

  const schedule = useCallback((side, samples, sampleRate) => {
    const a = audio.current;
    if (!a.ctx) return;
    const buf = a.ctx.createBuffer(1, samples.length, sampleRate);
    buf.copyToChannel(samples, 0);
    const src = a.ctx.createBufferSource();
    src.buffer = buf;
    src.connect(side === 'left' ? a.left : a.right);

    const key = side === 'left' ? 'nextLeft' : 'nextRight';
    const now = a.ctx.currentTime;
    // A short lead absorbs jitter; falling behind restarts from now rather than
    // accumulating a delay that never recovers.
    if (a[key] < now + 0.01) a[key] = now + (side === 'left' ? 0.06 : 0.14);
    src.start(a[key]);
    a[key] += buf.duration;
  }, []);

  // ---- the local ear ------------------------------------------------------
  const startEngineAudio = useCallback(async () => {
    const info = await (await api.callBackend('GET', '/engine')).json();
    if (!info?.wsUrl) { setMessage({ bad: true, text: 'could not find the engine websocket' }); return; }

    const ws = new WebSocket(info.wsUrl);
    ws.binaryType = 'arraybuffer';
    ws.onopen = () => requestAudio(ws, true);
    ws.onmessage = (ev) => {
      if (typeof ev.data === 'string') return;
      const f = parseAudioFrame(ev.data);
      if (!f) return;                                  // display frames share this socket
      const mono = f.channels === 1
        ? f.samples
        : f.samples.filter((_, i) => i % f.channels === 0);
      schedule('left', mono, f.sampleRate);

      let peak = 0;
      for (let i = 0; i < mono.length; i++) peak = Math.max(peak, Math.abs(mono[i]));
      setLocalLevel(peak);
    };
    ws.onclose = () => { engineWs.current = null; };
    engineWs.current = ws;
  }, [api, schedule]);

  // ---- the remote ear -----------------------------------------------------
  const dropRemote = useCallback((host) => {
    const r = remote.current.get(host);
    if (!r) return;
    try { r.ws.close(); } catch { /* already gone */ }
    try { r.decoder?.free(); } catch { /* already freed */ }
    remote.current.delete(host);
  }, []);

  const addRemote = useCallback(async (rx) => {
    if (remote.current.has(rx.host)) return;
    if (remote.current.size >= CONNECTION_BUDGET) return;

    const Decoder = OpusDecoder();
    if (!Decoder) { setMessage({ bad: true, text: 'the Opus decoder did not load' }); return; }

    let admit;
    try {
      const res = await api.callBackend('POST', '/connect', { host: rx.host });
      admit = await res.json();
      // A refusal is the instance saying it is full or unwilling. It is not
      // retried, and the host is not queued again this session.
      if (!res.ok) return;
    } catch { return; }

    const hz = radioRef.current?.vfoHz ?? 0;
    const mode = (radioRef.current?.mode ?? 'usb').toLowerCase();
    const url = `${admit.wsBase}/ws?frequency=${hz}&mode=${encodeURIComponent(mode)}`
      + `&user_session_id=${encodeURIComponent(admit.sessionId)}&format=opus&version=${admit.version}`;

    const decoder = new Decoder();
    await decoder.ready;

    const ws = new WebSocket(url);
    ws.binaryType = 'arraybuffer';
    const entry = { ws, decoder, snr: null, since: Date.now() };
    remote.current.set(rx.host, entry);

    ws.onmessage = (ev) => {
      if (typeof ev.data === 'string') return;
      const snr = readSnr(ev.data);
      if (snr != null) entry.snr = entry.snr == null ? snr : entry.snr * 0.8 + snr * 0.2;

      // Only the chosen receiver is decoded. The others are measured from their
      // 21-byte headers alone, which is what makes holding three affordable.
      if (bestRef.current !== rx.host || ev.data.byteLength <= HEADER_BYTES) return;
      try {
        const payload = new Uint8Array(ev.data, HEADER_BYTES);
        const { channelData, samplesDecoded, sampleRate } = entry.decoder.decodeFrame(payload);
        if (samplesDecoded) schedule('right', channelData[0], sampleRate);
      } catch { /* one bad frame must not stop the ear */ }
    };
    ws.onclose = () => remote.current.delete(rx.host);
  }, [api, schedule]);

  const stopAll = useCallback(() => {
    for (const host of [...remote.current.keys()]) dropRemote(host);
    const ws = engineWs.current;
    if (ws) { try { requestAudio(ws, false); ws.close(); } catch { /* gone */ } }
    engineWs.current = null;
    setBest(null); bestRef.current = null; setPool([]);
    setRunning(false);
  }, [dropRemote]);

  useEffect(() => () => stopAll(), [stopAll]);

  // ---- what the radio is doing -------------------------------------------
  useEffect(() => {
    let alive = true;
    const tick = async () => {
      try {
        const r = await api.callBackend('GET', '/radio');
        if (!r.ok || !alive) return;
        const next = await r.json();
        radioRef.current = next;
        setRadio(next);
      } catch { /* a restarting engine is not worth an error */ }
    };
    void tick();
    const t = window.setInterval(tick, 2000);
    return () => { alive = false; window.clearInterval(t); };
  }, [api]);

  // ---- keep the pool honest ----------------------------------------------
  //
  // Every second: retune anything that has drifted from the operator's
  // frequency, re-rank by SNR, and promote the best to the right ear. Every
  // ROTATE_AFTER_MS: drop the worst and try someone new. Slowly, because each
  // of these is a session on hardware somebody else pays for.
  useEffect(() => {
    if (!running) return;
    let alive = true;
    let lastRotate = Date.now();
    let tunedHz = 0;

    const tick = async () => {
      if (!alive) return;
      const hz = radioRef.current?.vfoHz ?? 0;
      const mode = (radioRef.current?.mode ?? 'usb').toLowerCase();

      if (hz && hz !== tunedHz) {
        tunedHz = hz;
        for (const { ws } of remote.current.values()) {
          if (ws.readyState !== WebSocket.OPEN) continue;
          try {
            ws.send(JSON.stringify({ type: 'tune', frequency: hz, mode,
                                     bandwidthLow: -2800, bandwidthHigh: -100 }));
          } catch { /* closing mid-retune is not an error */ }
        }
        // A new frequency invalidates every measurement taken on the old one.
        for (const e of remote.current.values()) e.snr = null;
      }

      const ranked = [...remote.current.entries()]
        .map(([host, e]) => ({ host, snr: e.snr, since: e.since }))
        .sort((a, b) => (b.snr ?? -999) - (a.snr ?? -999));
      setPool(ranked);

      const top = ranked.find((r) => r.snr != null);
      if (top && top.host !== bestRef.current) {
        bestRef.current = top.host;
        setBest(top);
      } else if (top) {
        setBest(top);
      }

      // Top up to the budget, then rotate the worst out on a timer.
      if (remote.current.size < CONNECTION_BUDGET) {
        try {
          const res = await api.callBackend('GET', '/receivers?count=8');
          const d = await res.json();
          const next = (d.suggested ?? []).find((rx) => !remote.current.has(rx.host));
          if (next) await addRemote(next);
        } catch { /* the directory is cached; a miss costs nothing */ }
      } else if (Date.now() - lastRotate > ROTATE_AFTER_MS && ranked.length > 1) {
        lastRotate = Date.now();
        const worst = ranked[ranked.length - 1];
        if (worst.host !== bestRef.current) dropRemote(worst.host);
      }
    };

    void tick();
    const t = window.setInterval(tick, 1000);
    return () => { alive = false; window.clearInterval(t); };
  }, [running, api, addRemote, dropRemote]);

  const start = useCallback(async () => {
    setMessage(null);
    try {
      await ensureAudio();
      await startEngineAudio();
      setRunning(true);
    } catch (e) {
      setMessage({ bad: true, text: 'could not start: ' + e });
    }
  }, [ensureAudio, startEngineAudio]);

  // ---- render -------------------------------------------------------------

  const bar = (v) => h('div', {
    style: { height: 4, borderRadius: 2, background: 'var(--bg-2, #16181d)', overflow: 'hidden' },
  }, h('div', { style: {
    height: '100%', width: `${Math.max(0, Math.min(100, v))}%`,
    background: 'var(--success, #4fbfa0)', transition: 'width .2s linear',
  } }));

  return h('div', { style: css.panel },

    h('div', { style: css.ears },
      h('div', { style: css.ear },
        h('span', { style: css.earLabel }, 'left · your radio'),
        h('span', { style: css.who },
          radio?.available ? `${(radio.vfoHz / 1e6).toFixed(3)} ${radio.mode}` : '—'),
        bar(localLevel * 140)),
      h('div', { style: css.ear },
        h('span', { style: css.earLabel }, 'right · best remote'),
        h('span', { style: css.who }, best?.host?.split('.')[0] ?? '—'),
        h('span', { style: css.snr }, best?.snr == null ? '— dB' : `${best.snr.toFixed(1)} dB`))),

    h('div', { style: { display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' } },
      h('button', { style: css.button, onClick: running ? stopAll : start },
        running ? 'stop' : 'start listening'),
      message ? h('span', { style: message.bad ? css.bad : null }, message.text) : null),

    running && pool.length
      ? h('div', { style: css.note },
          'watching ' + pool.map((p) =>
            `${p.host.split('.')[0]} ${p.snr == null ? '—' : p.snr.toFixed(0)}`).join(' · '))
      : null,

    h('div', { style: css.note },
      'Left ear is your receiver, right ear is whichever remote station copies '
      + 'this frequency best. It swaps on its own as conditions change.'),

    h('div', { style: css.note },
      'Mute Zeus\u2019s own audio output while this runs, or you will hear your '
      + 'receiver twice \u2014 once from Zeus in both ears and once here on the left.'),

    h('div', { style: css.note },
      `Holds ${CONNECTION_BUDGET} remote receivers at a time and rotates the weakest `
      + 'every minute or so. Each one is a session on somebody else\u2019s hardware.'));
}

export default function register(api) {
  api.registerPanel({
    id: 'ubersdr.qso',
    component: () => h(QsoAssistPanel, { api }),
  });
}
