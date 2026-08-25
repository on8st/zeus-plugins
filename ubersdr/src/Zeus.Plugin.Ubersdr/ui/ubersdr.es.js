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
  input: { background: 'var(--bg-2, #16181d)', color: 'var(--fg, #e6e8ea)',
           border: '1px solid var(--border, #2a2e35)', borderRadius: 3,
           padding: '3px 6px', font: 'inherit', fontSize: 12 },
};

// 0 dB reads as empty, 60 dB as full. Chosen from the live probe, which saw
// 34–56 dB from a receiver hearing a strong signal well.
const snrPercent = (snr) => Math.max(0, Math.min(100, (snr / 60) * 100));
const snrColour = (snr) =>
  snr >= 30 ? 'var(--success, #4fbfa0)' : snr >= 15 ? 'var(--warning, #d8a657)' : 'var(--danger, #e5715f)';

function Tile({ rx, reading, live, onLive }) {
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
    h('div', { style: { display: 'flex', justifyContent: 'space-between', alignItems: 'center' } },
      h('span', { style: { fontFamily: 'var(--font-mono, ui-monospace, monospace)' } },
        snr == null ? '— dB' : `${snr.toFixed(1)} dB`),
      reading?.state === 'open'
        ? h('button', {
            style: {
              ...css.button, padding: '1px 7px', fontSize: 11,
              borderColor: live ? 'var(--danger, #e5715f)' : undefined,
              color: live ? 'var(--danger, #e5715f)' : undefined,
            },
            onClick: () => onLive(live ? null : rx.host),
            title: live ? 'stop listening live' : 'listen live — headphones',
          }, live ? '● live' : 'live')
        : h('span', { style: css.note },
            reading?.state === 'error' ? h('span', { style: css.bad }, 'failed')
              : reading?.state ?? 'idle')));
}

// Antenna comparison.
//
// Read this DOWN the columns, never across the rows. One receiver against
// itself minutes apart is a fair comparison — same site, same noise floor, so
// the difference is the operator's. One receiver against another is not: a
// quiet rural site reads better than a suburban one for an identical signal.
// The table is laid out to make the sound reading the easy one.
function Comparison({ takes, hosts }) {
  const labelled = takes.filter((t) => t.label.trim()).slice().reverse();   // oldest first
  if (labelled.length < 2) return null;

  const baseline = labelled[0];
  const peak = (take, host) => take.rows.find((r) => r.host === host)?.peakSnr ?? null;

  const cell = (key, v, delta) => h('td', {
    key,
    style: {
      padding: '3px 8px', textAlign: 'right', whiteSpace: 'nowrap',
      fontFamily: 'var(--font-mono, ui-monospace, monospace)',
      color: delta == null ? undefined
        : delta > 0 ? 'var(--success, #4fbfa0)'
        : delta < 0 ? 'var(--danger, #e5715f)' : undefined,
    },
  }, v);

  return h('div', { style: { display: 'flex', flexDirection: 'column', gap: 6 } },
    h('div', { style: { ...css.note, textTransform: 'uppercase', letterSpacing: '.1em' } },
      'comparison'),
    h('div', { style: { overflowX: 'auto' } },
      h('table', { style: { borderCollapse: 'collapse', fontSize: 12 } },
        h('thead', null,
          h('tr', null,
            h('th', { style: { textAlign: 'left', padding: '3px 8px' } }, ''),
            hosts.map((host) =>
              h('th', { key: host, style: { padding: '3px 8px', textAlign: 'right', fontWeight: 600 } },
                host.split('.')[0])))),
        h('tbody', null,
          labelled.map((take, i) =>
            h('tr', { key: take.at.getTime(), style: { borderTop: '1px solid var(--border, #2a2e35)' } },
              h('td', { style: { padding: '3px 8px', whiteSpace: 'nowrap' } }, take.label),
              hosts.map((host) => {
                const v = peak(take, host);
                const b = peak(baseline, host);
                const d = i === 0 || v == null || b == null ? null : v - b;
                return cell(host,
                  v == null ? '—'
                    : d == null ? `${v.toFixed(0)}`
                    : `${v.toFixed(0)}  ${d > 0 ? '+' : ''}${d.toFixed(0)}`,
                  d);
              })))))),
    h('div', { style: css.note },
      `dB peak, against “${baseline.label.trim()}”. Compare a column against itself — `
      + 'the difference between two receivers is mostly the difference between their '
      + 'noise floors, not your signal. Back-to-back overs are worth more than ones '
      + 'minutes apart: propagation drifts.'));
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
  const recording = useRef({ active: false, byHost: new Map(), startedAt: 0, source: 'keyed' });
  // Declared with the other refs, not beside the effect that drives it: a
  // binding used by a callback above its own declaration is the shape of the
  // temporal-dead-zone bug that already took this panel down once.
  const keyedRef = useRef(false);
  // The keying poll needs the current frequency without re-creating its interval
  // every time the radio ticks.
  const radioRef = useRef(null);
  const audio = useRef({ ctx: null, source: null, decoder: null });

  // ---- following the radio ------------------------------------------------
  //
  // The receivers track what the operator is doing, which is what turns this
  // from a transmit monitor into wide-area diversity: while receiving, every
  // connected receiver sits on the frequency you are listening to, so a station
  // you can barely copy may be perfectly readable 500 km away. While
  // transmitting they follow the transmit frequency instead.
  //
  // `tune` retunes an already open socket — that is what it is for, and it is
  // why this costs nothing: no reconnection, no fresh admission, no new slot.
  const tuned = useRef({ hz: 0, mode: '' });

  const retuneAll = useCallback((hz, mode) => {
    if (!hz) return;
    const t = tuned.current;
    const m = (mode || 'usb').toLowerCase();
    if (t.hz === hz && t.mode === m) return;        // nothing changed
    tuned.current = { hz, mode: m };

    for (const ws of sockets.current.values()) {
      if (ws.readyState !== WebSocket.OPEN) continue;
      try {
        ws.send(JSON.stringify({
          type: 'tune', frequency: hz, mode: m,
          bandwidthLow: -2800, bandwidthHigh: -100,
        }));
      } catch { /* a socket closing mid-retune is not an error */ }
    }
  }, []);

  // The radio is polled rather than subscribed to: the engine's own /ws carries
  // binary telemetry only, with no state or keying messages on it.
  useEffect(() => {
    let alive = true;
    const tick = async () => {
      try {
        const r = await api.callBackend('GET', '/radio');
        if (!r.ok || !alive) return;
        const next = await r.json();
        radioRef.current = next;
        setRadio(next);
      } catch { /* a restarting engine is not an error worth showing */ }
    };
    void tick();
    const t = window.setInterval(tick, 1000);
    return () => { alive = false; window.clearInterval(t); };
  }, [api]);

  // While receiving, the wall follows the VFO — that is the diversity case.
  // While transmitting it does not move: the keying handler has already pointed
  // it at the transmit frequency, and chasing the VFO mid-over would retune the
  // receivers away from the signal being measured.
  useEffect(() => {
    if (!radio?.available || keyedRef.current) return;
    if (sockets.current.size === 0) return;
    retuneAll(radio.vfoHz, radio.mode);
  }, [radio, retuneAll]);

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
    // Releasing the receivers must silence live audio too, or the panel keeps
    // a decoder alive feeding a stream that has stopped arriving.
    const l = liveRef.current;
    try { l.decoder?.free(); } catch { /* already freed */ }
    liveRef.current = { decoder: null, nextAt: 0, host: null };
    setLive(null);
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

          // Live monitoring, if this is the receiver being listened to. Runs
          // whether or not a recording is in progress — the point of it is to
          // hear the transmission as it happens.
          if (liveRef.current.host === rx.host) {
            const p = new Uint8Array(ev.data, HEADER_BYTES);
            if (p.length > 0) playLiveFrame(rx.host, p);
          }

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

  // ---- live monitoring, one receiver, opt-in ------------------------------
  //
  // Hearing yourself while you transmit. Two hazards, and neither is theoretical:
  //
  //   Feedback. Remote audio from the speakers with an open microphone is a
  //   howl with seconds of internet latency in it, put on the air. Headphones
  //   are not a suggestion here.
  //
  //   Delayed auditory feedback. Hearing your own voice a second or two late
  //   disrupts fluent speech badly — it is a known effect, not a matter of
  //   getting used to it. So this is genuinely useful for a tune-up carrier, CW,
  //   or watching an amplifier, and genuinely unpleasant for talking.
  //
  // Hence: one receiver at a time, off unless asked for, and the panel says
  // both of those things where the operator will read them.
  const [live, setLive] = useState(null);            // host being monitored live
  // Off by default. On, live audio continues through a transmission — which is
  // the tune-up-carrier and CW case, and is safe only on headphones.
  const [monitorWhileKeyed, setMonitorWhileKeyed] = useState(false);
  const liveRef = useRef({ decoder: null, nextAt: 0, host: null, paused: false });
  const monitorWhileKeyedRef = useRef(false);
  useEffect(() => { monitorWhileKeyedRef.current = monitorWhileKeyed; }, [monitorWhileKeyed]);

  const stopLive = useCallback(() => {
    const l = liveRef.current;
    try { l.decoder?.free(); } catch { /* already freed */ }
    l.decoder = null; l.host = null; l.nextAt = 0; l.paused = false;
    setLive(null);
  }, []);

  // Pausing keeps the decoder and the chosen receiver; it only stops scheduling
  // audio. Tearing the session down on every over and rebuilding it after would
  // lose the receiver selection and re-admit on someone else's instance for no
  // reason.
  const pauseLive = useCallback((paused) => {
    const l = liveRef.current;
    if (!l.decoder) return;
    l.paused = paused;
    // Restart the schedule after a pause rather than trying to catch up on
    // audio the operator did not hear.
    if (!paused) l.nextAt = 0;
  }, []);

  const startLive = useCallback(async (host) => {
    stopLive();
    const Decoder = OpusDecoder();
    if (!Decoder) { setMessage({ bad: true, text: 'the Opus decoder did not load' }); return; }
    try {
      const decoder = new Decoder();
      await decoder.ready;
      audio.current.ctx ??= new (window.AudioContext || window.webkitAudioContext)();
      if (audio.current.ctx.state === 'suspended') await audio.current.ctx.resume();
      liveRef.current = { decoder, nextAt: 0, host };
      setLive(host);
    } catch (e) {
      setMessage({ bad: true, text: 'could not start live monitoring: ' + e });
    }
  }, [stopLive]);

  // Called per frame from the socket. Decodes one frame and schedules it back
  // to back, so the stream paces itself rather than accumulating a lag.
  const playLiveFrame = useCallback((host, payload) => {
    const l = liveRef.current;
    if (l.host !== host || !l.decoder || l.paused) return;
    const ctx = audio.current.ctx;
    if (!ctx) return;
    try {
      const { channelData, samplesDecoded, sampleRate } = l.decoder.decodeFrame(payload);
      if (!samplesDecoded) return;

      const buf = ctx.createBuffer(channelData.length, samplesDecoded, sampleRate);
      channelData.forEach((ch, i) => buf.copyToChannel(ch, i));
      const src = ctx.createBufferSource();
      src.buffer = buf;
      src.connect(ctx.destination);

      // A small lead so scheduling jitter does not produce gaps; if we have
      // fallen behind, restart from now rather than trying to catch up.
      const lead = 0.12;
      const now = ctx.currentTime;
      if (l.nextAt < now + 0.01) l.nextAt = now + lead;
      src.start(l.nextAt);
      l.nextAt += buf.duration;
    } catch { /* one bad frame must not stop the stream */ }
  }, []);

  // ---- recording, bracketed by the key -----------------------------------

  const startRecording = useCallback((source = 'keyed') => {
    const rec = recording.current;
    rec.source = source;
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
    setTakes((prev) => [{
      at: new Date(),
      seconds,
      rows,
      source: rec.source,
      // Labelled after the fact: an operator switching antennas knows what they
      // just did, and typing before transmitting is one more thing to forget.
      label: '',
    }, ...prev].slice(0, 12));
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

  // A capture that does not need the key.
  //
  // Antenna comparison is the point of the table below, and it is measured from
  // transmissions — but the same machinery answers "what do these six receivers
  // hear right now", and it makes the whole feature usable and testable by
  // someone who cannot transmit at this moment. Same recording path, same peak
  // hold; only the trigger differs.
  const [capturing, setCapturing] = useState(0);
  const captureFor = useCallback(async (seconds) => {
    if (sockets.current.size === 0) {
      setMessage({ bad: true, text: 'connect some receivers first' });
      return;
    }
    if (keyedRef.current) {
      setMessage({ bad: true, text: 'already recording — you are keyed' });
      return;
    }
    stopPlayback();
    startRecording('manual');
    setCapturing(seconds);
    for (let left = seconds; left > 0; left--) {
      await new Promise((r) => window.setTimeout(r, 1000));
      setCapturing(left - 1);
    }
    stopRecording();
    setCapturing(0);
  }, [startRecording, stopRecording, stopPlayback]);

  // Keying is polled fast, and only while receivers are connected — there is no
  // state stream on the engine, and there is no reason to hammer it when the
  // panel is merely open. 10 Hz brackets a transmission to about a tenth of a
  // second, which is nothing beside a 200 ms tune and a multi-second over.
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
          // Anything audible while the microphone is open is a delayed howl put
          // on the air. Recorded playback always stops; live audio pauses unless
          // the operator has explicitly asked to monitor through a transmission
          // — the tune-up-carrier case, which is a headphone activity.
          stopPlayback();
          if (!monitorWhileKeyedRef.current) pauseLive(true);
          // Under split these differ, and monitoring the VFO while transmitting
          // elsewhere would report that nobody hears the operator.
          retuneAll(radioRef.current?.transmitHz, radioRef.current?.mode);
          startRecording();
        } else {
          stopRecording();
          pauseLive(false);
          // Back to what the operator is listening to.
          retuneAll(radioRef.current?.vfoHz, radioRef.current?.mode);
        }
      } catch { /* a restarting engine is not an error worth showing */ }
    };
    const t = window.setInterval(tick, 100);
    return () => { alive = false; window.clearInterval(t); };
  }, [api, startRecording, stopRecording, stopPlayback, retuneAll, pauseLive]);

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
      listening
        ? h('button', {
            style: css.button, disabled: busy || capturing > 0,
            onClick: () => captureFor(6),
          }, capturing > 0 ? `capturing… ${capturing}s` : 'capture 6 s without transmitting')
        : null,
      message ? h('span', { style: message.bad ? css.bad : null }, message.text) : null),

    h('div', { style: css.grid },
      wall.map((rx) => h(Tile, {
        key: rx.host, rx, reading: readings[rx.host],
        live: live === rx.host,
        onLive: (host) => (host ? startLive(host) : stopLive()),
      }))),

    live
      ? h('div', {
          style: {
            border: '1px solid var(--border, #2a2e35)', borderRadius: 3,
            padding: '8px 11px', ...css.note, lineHeight: 1.5,
            display: 'flex', flexDirection: 'column', gap: 6,
          },
        },
          h('div', null,
            `Listening live to ${live.split('.')[0]}. `,
            'Audio pauses while you transmit and resumes when you unkey.'),
          h('label', { style: { display: 'flex', gap: 6, alignItems: 'center', cursor: 'pointer' } },
            h('input', {
              type: 'checkbox',
              checked: monitorWhileKeyed,
              onChange: (e) => setMonitorWhileKeyed(e.target.checked),
            }),
            h('span', null, 'keep listening while I transmit')),
          monitorWhileKeyed
            ? h('div', { style: { color: 'var(--danger, #e5715f)' } },
                h('b', null, 'Headphones. '),
                'Remote audio from your speakers with the microphone open is a '
                + 'feedback howl, delayed by seconds, transmitted. Expect to hear '
                + 'yourself one to two seconds late as well, which badly disrupts '
                + 'speaking — this setting is for a tune-up carrier, CW or watching '
                + 'an amplifier, not for talking.')
            : null)
      : null,

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
              h('div', { style: { display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' } },
                h('span', { style: css.note },
                  `${take.at.toLocaleTimeString()} · ${take.seconds.toFixed(1)} s`
                  + (take.source === 'manual' ? ' · listened' : ' · transmitted')),
                // Labelled after the fact: the operator knows what they just
                // switched, and typing beforehand is one more thing to forget
                // mid-over.
                h('input', {
                  style: { ...css.input, maxWidth: 150 },
                  value: take.label,
                  placeholder: 'label (e.g. dipole)',
                  onChange: (e) => {
                    const v = e.target.value;
                    setTakes((prev) => prev.map((t) =>
                      t.at === take.at ? { ...t, label: v } : t));
                  },
                })),
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

    h(Comparison, {
      takes,
      hosts: wall.map((rx) => rx.host),
    }),

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
