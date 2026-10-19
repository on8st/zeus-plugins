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
import { EnvelopeTracker, estimateLag, decideAlignment, ENVELOPE_HZ, SYNC_MIN_QUALITY,
         MIN_DEPTH } from './align.js';
import { nextGain, applyGain, peakOf } from './agc.js';
import { planStart } from './queue.js';
import { parseRxMeters, parseSignalQuality, sMeterPct, sUnits, isMeterSentinel }
  from './engine-meters.js';
// Written by the packaging script. Absent in a working tree, which is itself
// the honest answer to "which build is this".
let BUILD = 'dev';
try { ({ BUILD } = await import('./build.js')); } catch { /* not packaged */ }

const h = React.createElement;
const OpusDecoder = () => globalThis['opus-decoder']?.OpusDecoder;
const HEADER_BYTES = 21;

/** How many remote receivers are held open at once. Deliberately small. */
// Default only; the operator sets this in the panel. Every one of these is a
// client slot on somebody else's receiver, so the choice is deliberately
// visible rather than buried.
export const CONNECTION_BUDGET = 3;
const BUDGET_CHOICES = [2, 3, 4, 6, 8];

// Simple, mutually exclusive ways to pick who to listen to. The first three are
// shapes of the whole world; the rest are places.
const SELECTIONS = [
  { id: 'spread',       label: 'all directions', preset: 'spread' },
  { id: 'nearest',      label: 'close by',       preset: 'nearest' },
  { id: 'furthest',     label: 'far away',       preset: 'furthest' },
  { id: 'europe',       label: 'Europe',         preset: 'spread', region: 'europe' },
  { id: 'northamerica', label: 'North America',  preset: 'spread', region: 'northamerica' },
  { id: 'southamerica', label: 'South America',  preset: 'spread', region: 'southamerica' },
  { id: 'asia',         label: 'Asia',           preset: 'spread', region: 'asia' },
  { id: 'africa',       label: 'Africa',         preset: 'spread', region: 'africa' },
  { id: 'oceania',      label: 'Oceania',        preset: 'spread', region: 'oceania' },
];
// Zeus's own playback is held here while the panel owns the audio.
const SILENCE_AF_DB = -50;
/** How long a receiver keeps its place before the worst one is rotated out. */
// Longer than the screening window plus a margin: dropping a receiver before it
// has been judged wastes the connection and guarantees it is never confirmed.
export const ROTATE_AFTER_MS = 300_000;

// Switching ears is not free — it costs a gap in the audio and, worse, the
// operator's place in the conversation. So a challenger has to earn it three
// times over: be clearly better, stay better, and not arrive on the heels of
// the last switch. Without all three the ranking flaps at syllable rate,
// because SSB speech swings the measured SNR by more than the difference
// between two decent receivers ever will.
const RANK_TAU_MS = 3000;             // memory of the SNR average
export const SWITCH_MARGIN_DB = 3;    // how much better a challenger must be
export const SWITCH_DWELL_MS = 6000;  // how long it must hold that lead
export const MIN_HOLD_MS = 10_000;    // floor on how long an ear keeps a source

// Base jitter buffers. The remote gets more: it crossed the internet.
const LEAD = { left: 0.06, right: 0.14 };
const ALIGN_EVERY_MS = 2000;
// The same window the screening was calibrated to. At 8 s a true match and pure
// chance score the same, and alignment acted on the result anyway — and because
// its output *is* the playback offset, a wrong measurement makes itself true:
// delay the left ear by two seconds and the next measurement dutifully reports
// two seconds. That ratchet was audible as a local signal drifting further and
// further behind.
const ALIGN_WINDOW_S = 45;
// Measured arrival skew between the two streams over a full minute: 0.02-0.04 s.
// A correction beyond a second is not a measurement, it is the loop above.
const ALIGN_MAX_S = 1.0;
// Hard ceiling on the jitter buffer, whatever alignment asks for. The queue
// bound is expressed relative to the lead, so an inflated lead would otherwise
// license an inflated queue.
const MAX_LEAD_S = 1.2;
// How much history a candidate is judged on. Measured, and the measurement was
// unambiguous: at 10 s the true and the impossible pairings are statistically
// identical (median quality 0.27 vs 0.25), so the screening could not tell them
// apart at all. The null distribution collapses as the window grows — 0.17 at
// 20 s, 0.16 at 30 s, 0.11 at 45 s — while true matches stay at 0.27 throughout.
const SYNC_WINDOW_S = 45;
// Below this the operator's own receiver has nothing above its noise floor, so
// no candidate can be matched against it however well that candidate is doing.
const LOCAL_MIN_SNR_DB = 3;
// A remote ear is only worth having if it hears the QSO better than you do.
// Once both sides are on the same scale this is checkable, and it turns out to
// matter: on the first honest measurement all three candidates were confirmed
// as hearing the operator's QSO while sitting 12-16 dB *below* his own
// receiver. Correct, and useless. A pinned receiver bypasses this — listening
// to a worse ear on purpose is a legitimate thing to want.
const BEAT_LOCAL_DB = 3;
// ...but a pause between overs looks exactly the same, and on SSB those last
// tens of seconds. Saying "your receiver is deaf" at every gap in a QSO would
// be noise itself. Only a sustained drought means anything.
const LOCAL_DEAF_AFTER_MS = 45_000;

/**
 * Who gets the right ear, given the current ranking and what happened before.
 *
 * <p>This is pulled out of the tick as a pure function on purpose. It is a
 * timing rule — margin, dwell, minimum hold — and a timing rule that can only
 * be checked by grepping for its constants is not checked at all. Here it can
 * be driven through a whole minute of made-up conditions in a millisecond, and
 * the flapping it exists to prevent can be demonstrated rather than asserted.
 *
 * <p>Returns the next state; the caller applies it. `chasing` is non-null only
 * while a challenger is serving its dwell, so the panel can announce a switch
 * while it is being earned instead of springing it.
 */
export function chooseEar({ ranked, held: heldHost, heldSince, challenger, now }) {
  const keep = (chasing) => ({ held: heldHost, heldSince, challenger, chasing });

  const top = ranked.find((r) => r.snr != null);
  const held = ranked.find((r) => r.host === heldHost && r.snr != null);

  // Nothing in the ear, or it fell silent. Hysteresis defends a working
  // choice; it must never delay the first one.
  if (!held) {
    return top
      ? { held: top.host, heldSince: now, challenger: null, chasing: null, switched: true }
      : keep(null);
  }

  const better = top && top.host !== held.host && top.snr >= held.snr + SWITCH_MARGIN_DB;
  if (!better || now - heldSince < MIN_HOLD_MS) return keep(null);

  const c = challenger?.host === top.host ? challenger : { host: top.host, since: now };
  const heldFor = now - c.since;
  if (heldFor >= SWITCH_DWELL_MS) {
    return { held: top.host, heldSince: now, challenger: null, chasing: null, switched: true };
  }
  return {
    held: heldHost,
    heldSince,
    challenger: c,
    chasing: { rx: top.rx, host: top.host, gain: top.snr - held.snr,
               left: Math.ceil((SWITCH_DWELL_MS - heldFor) / 1000) },
  };
}

const css = {
  panel: { display: 'flex', flexDirection: 'column', gap: 10, padding: 12,
           fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)', fontSize: 13 },
  note: { color: 'var(--fg-3, #5a5e66)', fontSize: 11, lineHeight: 1.45 },
  // The power control lives in its own narrow column spanning both meters, so
  // the on/off state costs no vertical space at all — it is simply the thing
  // the bars are attached to.
  ears: { display: 'grid', gridTemplateColumns: '30px 1fr', gap: 8, alignItems: 'center' },
  power: { gridColumn: 1, width: 26, height: 26, padding: 0,
           borderRadius: 13, display: 'grid', placeItems: 'center', fontSize: 13,
           background: 'var(--bg-3, #22262d)', border: '1px solid var(--border, #2a2e35)',
           cursor: 'pointer', font: 'inherit', lineHeight: 1 },
  // The remote ear needs a control that does not depend on a station being
  // heard: it starts muted, and the only way out used to be clicking the row
  // of the station currently playing — which cannot exist while nothing has
  // been confirmed. That was a deadlock.
  earBtn: { gridColumn: 1, gridRow: '2 / -1', width: 26, alignSelf: 'stretch',
            minHeight: 26, padding: 0, borderRadius: 4, display: 'grid',
            placeItems: 'center', fontSize: 12, background: 'var(--bg-3, #22262d)',
            border: '1px solid var(--border, #2a2e35)', cursor: 'pointer',
            font: 'inherit', lineHeight: 1 },
  // A meter row: name on the left, the bar taking the room, place on the right.
  // Stacked rather than side by side so the two bars share a baseline and can
  // be compared at a glance — which is the whole point of putting them together.
  select: { background: 'var(--bg-3, #22262d)', color: 'var(--fg, #e6e8ea)',
            border: '1px solid var(--border, #2a2e35)', borderRadius: 3,
            padding: '4px 6px', font: 'inherit', fontSize: 12, cursor: 'pointer' },
  cog: { background: 'none', border: 'none', color: 'var(--fg-3, #5a5e66)',
         cursor: 'pointer', font: 'inherit', fontSize: 14, padding: '0 4px', flex: 'none' },
  scrim: { position: 'fixed', inset: 0, background: 'rgba(0,0,0,.5)',
           display: 'grid', placeItems: 'center', zIndex: 60 },
  modal: { width: 'min(420px, calc(100vw - 32px))', maxHeight: '80vh', overflowY: 'auto',
           background: 'var(--bg-1, #14171c)', color: 'var(--fg, #e6e8ea)',
           border: '1px solid var(--border, #2a2e35)', borderRadius: 8, padding: 16,
           display: 'flex', flexDirection: 'column', gap: 14,
           boxShadow: '0 20px 60px rgba(0,0,0,.5)' },
  modalHead: { display: 'flex', alignItems: 'baseline', gap: 10 },
  field: { display: 'flex', flexDirection: 'column', gap: 4 },
  fieldLabel: { fontSize: 11, letterSpacing: '.08em', textTransform: 'uppercase',
                color: 'var(--fg-3, #5a5e66)' },
  strip: { display: 'flex', alignItems: 'center', gap: 8, padding: '2px 0',
           cursor: 'pointer', userSelect: 'none' },
  dot: { fontSize: 12, width: 12, flex: 'none', textAlign: 'center' },
  stripName: { fontWeight: 600, fontSize: 12, whiteSpace: 'nowrap' },
  stripDelta: { marginLeft: 'auto', fontFamily: 'var(--font-mono, ui-monospace, monospace)',
                fontSize: 12, fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' },
  chevron: { background: 'none', border: 'none', color: 'var(--fg-3, #5a5e66)',
             cursor: 'pointer', font: 'inherit', fontSize: 11, padding: '0 2px', flex: 'none' },
  tabs: { display: 'flex', gap: 2, borderBottom: '1px solid var(--border, #2a2e35)' },
  tab: { background: 'none', border: 'none', borderBottom: '2px solid transparent',
         color: 'var(--fg-3, #5a5e66)', font: 'inherit', fontSize: 12, cursor: 'pointer',
         padding: '5px 10px', marginBottom: -1 },
  tabOn: { color: 'var(--fg, #e6e8ea)', borderBottomColor: 'var(--success, #4fbfa0)' },
  tiles: { display: 'flex', gap: 6, flexWrap: 'wrap' },
  tile: { position: 'relative', flex: '1 1 92px', minWidth: 92, maxWidth: 150,
          border: '1px solid var(--border, #2a2e35)', borderRadius: 4,
          padding: '6px 8px 5px', display: 'flex', flexDirection: 'column', gap: 2,
          cursor: 'pointer', userSelect: 'none', background: 'var(--bg-2, #1a1d22)' },
  tileHead: { fontWeight: 600, fontSize: 12, whiteSpace: 'nowrap',
              overflow: 'hidden', textOverflow: 'ellipsis' },
  // Ten seconds of history, not just the present. A receiver that has gone
  // quiet and one whose socket died look identical in a single reading.
  spark: { fontFamily: 'var(--font-mono, ui-monospace, monospace)', fontSize: 13,
           lineHeight: 1.1, letterSpacing: '-0.5px', whiteSpace: 'pre',
           overflow: 'hidden' },
  tileDb: { fontFamily: 'var(--font-mono, ui-monospace, monospace)', fontSize: 12,
            fontVariantNumeric: 'tabular-nums' },
  tileFoot: { fontSize: 10, color: 'var(--fg-3, #5a5e66)', whiteSpace: 'nowrap',
              fontVariantNumeric: 'tabular-nums' },
  tileX: { position: 'absolute', top: 2, right: 3, background: 'none', border: 'none',
           color: 'var(--fg-3, #5a5e66)', cursor: 'pointer', font: 'inherit',
           fontSize: 12, lineHeight: 1, padding: '0 2px' },
  exclude: { background: 'none', border: 'none', color: 'var(--fg-3, #5a5e66)',
             cursor: 'pointer', font: 'inherit', fontSize: 13, lineHeight: 1,
             padding: '0 2px' },
  row: { gridColumn: 2, display: 'grid', gridTemplateColumns: '84px 1fr 50px auto auto',
         alignItems: 'center', gap: 8, userSelect: 'none' },
  rowName: { fontWeight: 600, fontSize: 12, whiteSpace: 'nowrap',
             overflow: 'hidden', textOverflow: 'ellipsis' },
  rowRight: { fontSize: 10, color: 'var(--fg-3, #5a5e66)', whiteSpace: 'nowrap',
              fontFamily: 'var(--font-mono, ui-monospace, monospace)' },
  rowDb: { fontSize: 11, textAlign: 'right', whiteSpace: 'nowrap',
           fontFamily: 'var(--font-mono, ui-monospace, monospace)',
           fontVariantNumeric: 'tabular-nums' },
  tick: { position: 'absolute', top: 0, bottom: 0, width: 1,
          background: 'var(--border, #2a2e35)' },
  // A hairline under the local bar. The rows above and below it are measuring
  // different things, and a stack of identical bars invites reading a
  // comparison that is not there.
  localRow: { paddingBottom: 5, borderBottom: '1px solid var(--border, #2a2e35)' },
  meter: { position: 'relative', height: 12, borderRadius: 2,
           background: 'var(--bg-2, #1a1d22)',
           border: '1px solid var(--border, #2a2e35)', overflow: 'hidden' },
  fill: { position: 'absolute', inset: '0 auto 0 0', borderRadius: 1,
          transition: 'width 60ms linear' },
  peak: { position: 'absolute', top: 0, bottom: 0, width: 2,
          background: 'var(--fg, #e6e8ea)', opacity: 0.8 },
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

const SPARK = '\u2581\u2582\u2583\u2584\u2585\u2586\u2587\u2588';
const SPARK_LEN = 14;          // samples kept per receiver for the tile sparkline
const TRACK_LEN = 64;          // ~45 s of S/N history for the overlay graph
const SPARK_EVERY = 12;        // meter ticks between samples: ~0.7 s, ~10 s total

// Meters are read in dB, not in amplitude. A linear bar spends most of its
// length on the top 6 dB and crushes everything an operator actually wants to
// see — the difference between a weak signal and a dead stream lives down at
// −40 and is invisible unless the scale is logarithmic.
const METER_FLOOR_DB = -60;
const TICKS_DB = [-40, -20, -6];

// Remote baseband power lands far lower than the audio scale: measured on the
// air, a strong receiver reported -67 dB and the rest -90 and below. On the
// -60 dB audio floor every remote bar sat at zero and every dB column read an
// em-dash — the bars looked broken because the scale was wrong, not the data.
const RF_FLOOR_DB = -120;
const RF_TOP_DB = -40;
const RF_TICKS_DB = [-100, -80, -60];
const rfPct = (amp) => {
  const db = amplitudeToDb(amp);
  return Math.max(0, Math.min(100, ((db - RF_FLOOR_DB) / (RF_TOP_DB - RF_FLOOR_DB)) * 100));
};

const amplitudeToDb = (amp) => 20 * Math.log10(Math.max(amp, 1e-7));
/** Where a level sits along the bar, 0..100. */
const meterPct = (amp) => {
  const db = amplitudeToDb(amp);
  return Math.max(0, Math.min(100, ((db - METER_FLOOR_DB) / -METER_FLOOR_DB) * 100));
};

/** Baseband power from the frame header, as an amplitude. */
function readLevel(buf) {
  if (buf.byteLength < HEADER_BYTES) return null;
  const db = new DataView(buf).getFloat32(13, true);
  return Number.isFinite(db) ? Math.pow(10, db / 20) : null;
}

// The receive bandwidth this panel asks every receiver for (-2800..-100 Hz).
const REMOTE_BANDWIDTH_HZ = 2700;
// ka9q reports basebandPower as total power in the passband but noisePower as a
// spectral *density*, in dBm/Hz. Subtracting one from the other therefore
// leaves 10*log10(bandwidth) behind — about 34 dB — and that is not signal.
//
// Confirmed against a recording rather than assumed: a receiver hearing nothing
// but noise, whose true SNR must be ~0, showed exactly 34.4 dB. That is
// 10*log10(2754) — the filter, measured back out of the operator's own data.
//
// Without this the remote figures ran 18-22 dB above the engine's own SNR for
// the same QSO, and were being offered beside it as if comparable.
const BANDWIDTH_DB = 10 * Math.log10(REMOTE_BANDWIDTH_HZ);

function readSnr(buf) {
  if (buf.byteLength < HEADER_BYTES) return null;
  const v = new DataView(buf);
  const p = v.getFloat32(13, true);
  const n = v.getFloat32(17, true);
  return Number.isFinite(p) && Number.isFinite(n) ? p - n - BANDWIDTH_DB : null;
}


export function QsoAssistPanel({ api }) {
  const [running, setRunning] = useState(false);
  const [radio, setRadio] = useState(null);
  const [best, setBest] = useState(null);       // { host, snr }
  const [chasing, setChasing] = useState(null); // a challenger serving its dwell
  const [alignment, setAlignment] = useState(null);   // { side, offset, quality }
  // True only while there is genuinely something in the right ear. Read inside
  // the audio callbacks, so it lives on the audio record rather than in state.
  const [split, setSplit] = useState(false);
  const [pool, setPool] = useState([]);         // [{ host, snr, since }]
  const [message, setMessage] = useState(null);
  const [localLevel, setLocalLevel] = useState(0);
  // The local receiver's own RF telemetry, straight off the engine. Arrives
  // unrequested at 5 Hz alongside the audio.
  const [localRf, setLocalRf] = useState(null);
  const [screening, setScreening] = useState(null);
  const [view, setView] = useState('console');
  const [settingsOpen, setSettingsOpen] = useState(false);
  // The full layout lives in a modal rather than a second panel: one panel means
  // one session, and the shared-ownership machinery that a second one needed can
  // simply not exist.
  const [expanded, setExpanded] = useState(false);
  // The panel takes Zeus's volume away while it runs, so it has to hand one
  // back. The level restoration aims at a fixed target, which is the right
  // thing for *comparing* two receivers and the wrong thing for deciding how
  // loud they should be in your head.
  const [volume, setVolume] = useState(1);
  const volumeRef = useRef(1);
  useEffect(() => { volumeRef.current = volume; }, [volume]);


  // S/N against time, for every feed including the operator's own. Absolute
  // levels cannot share an axis — his receiver reports calibrated dBm and ka9q
  // reports its own baseband power, with no calibration between them — but
  // signal-above-noise can, because each side subtracts its own noise floor.
  const [tracks, setTracks] = useState([]);
  // Read inside the screening interval, which is not re-created per frame.
  const localRfRef = useRef(null);
  // Smoothed on the same 3 s timescale as the remote figures, so the comparison
  // is between two numbers that move at the same speed.
  const localSnrAvg = useRef(null);
  const deafSince = useRef(0);
  const beatenBy = useRef({ localSnr: null, confirmed: 0, better: 0 });
  // Which receivers currently clear the beat-the-operator bar, so the bar can
  // be lower for one that already holds it.
  const beating = useRef(new Set());
  const [remoteRows, setRemoteRows] = useState([]);
  // The station the operator has chosen by hand, if any. Held until it loses
  // sync or is clicked again — a manual pick that the ranking could override a
  // second later would not be a choice.
  const [pinned, setPinned] = useState(null);
  const pinnedRef = useRef(null);
  // Receivers the operator has struck off for good. Persisted in the plugin's
  // own settings, so a station that cannot help — his own SDR, which is
  // switched away from the antenna whenever the HL2 is live — does not keep
  // taking one of the three connections.
  const excluded = useRef([]);
  const stopRef = useRef(null);
  const [budget, setBudget] = useState(CONNECTION_BUDGET);
  const [selection, setSelection] = useState('spread');
  const budgetRef = useRef(CONNECTION_BUDGET);
  const selectionRef = useRef('spread');
  useEffect(() => { budgetRef.current = budget; }, [budget]);
  useEffect(() => { selectionRef.current = selection; }, [selection]);
  // What Zeus's volume was before this panel silenced it.
  const restoreAf = useRef(null);
  useEffect(() => { pinnedRef.current = pinned; }, [pinned]);
  useEffect(() => { localRfRef.current = localRf; }, [localRf]);
  useEffect(() => {
    let alive = true;
    api.callBackend('GET', '/config')
      .then((r) => r.json())
      .then((c) => {
        if (!alive || !c) return;
        if (Array.isArray(c.excludeHosts)) excluded.current = c.excludeHosts;
        if (c.count) { setBudget(c.count); budgetRef.current = c.count; }
        if (c.region || c.preset) {
          const found = SELECTIONS.find((x) => (x.region ?? '') === (c.region ?? '')
                                            && x.preset === (c.preset || 'spread'));
          if (found) { setSelection(found.id); selectionRef.current = found.id; }
        }
        if (c.restoreAfGainDb != null) restoreAf.current = c.restoreAfGainDb;
        if (c.volume != null) { setVolume(c.volume); volumeRef.current = c.volume; }
      })
      .catch(() => { /* no config yet is the normal first run */ });
    return () => { alive = false; };
  }, [api]);
  // Written at audio rate, read at display rate. Calling setState fifty times a
  // second per ear would re-render the panel into the ground for no visible
  // gain — the eye cannot follow it and a VU meter is not supposed to try.
  const levels = useRef({ left: 0, right: 0, peakL: 0, peakR: 0 });
  // One mute per ear. The right starts muted because it replaces one side of
  // the operator's headphones, which is not something to start doing
  // unannounced; the left starts open because it is their own receiver.
  const [mutedLeft, setMutedLeft] = useState(false);
  const [mutedRight, setMutedRight] = useState(true);
  const [keyed, setKeyed] = useState(false);
  // Off by default, and deliberately so. Hearing your own voice come back from
  // a remote receiver about a second late, in one ear, while you are speaking
  // is the delayed-auditory-feedback effect — it disrupts fluent speech in most
  // people. It is also exactly what you want when the question is "how do I
  // actually sound on the air", so it is offered, not withheld.
  const [txMonitor, setTxMonitor] = useState(false);
  // Read inside the socket handlers, which are not re-created when state changes.
  const gateRef = useRef({ mutedLeft: false, mutedRight: true, keyed: false, txMonitor: false });
  useEffect(() => {
    gateRef.current = { mutedLeft, mutedRight, keyed, txMonitor };
  }, [mutedLeft, mutedRight, keyed, txMonitor]);

  const audio = useRef({ ctx: null, nextLeft: 0, nextRight: 0,
    env: { left: new EnvelopeTracker(12), right: new EnvelopeTracker(12) },
    pending: { left: [], right: [] },
    align: null, gainLeft: 1, makeup: null, dropped: 0, split: false,
    envArrival: new EnvelopeTracker(SYNC_WINDOW_S + 6),
  });
  const remote = useRef(new Map());             // host -> { ws, decoder, snr, since }
  // Hosts that refused, or that the plugin could not reach. Never retried in
  // this session: a receiver that said no meant it, and a route that 404s will
  // keep doing so.
  const refused = useRef(new Set());
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
    Object.assign(a, { ctx, nextLeft: 0, nextRight: 0 });
    return a;
  }, []);

  /**
   * Put a mono block in one ear.
   *
   * Written into a *stereo* buffer with the other channel left silent, and
   * connected straight to the destination. The first version routed two mono
   * sources through gain nodes into a ChannelMergerNode, which is the textbook
   * way and came out of both ears — channel-count and interpretation rules make
   * that arrangement easy to get subtly wrong, and impossible to check by
   * reading. A two-channel buffer has no such rules: sample i of channel 0 is
   * the left speaker, and that is the whole of it.
   */
  const schedule = useCallback((side, samples, sampleRate) => {
    const a = audio.current;
    if (!a.ctx) return;

    // The local ear is restored to a usable level regardless of where Zeus's
    // own volume sits, which is what lets the operator turn Zeus's player down
    // to silence without losing their own receiver. See agc.js.
    let audible = samples;
    if (side === 'left') {
      // Undo exactly what was done, and nothing else.
      //
      // The panel lowers Zeus's AF gain by a known number of dB, so the amount
      // to put back is that number — not whatever an automatic gain control
      // decides the signal ought to sound like. The adaptive version dated from
      // before the panel set the gain itself, and it flattened the one thing an
      // operator reads by ear: a weak station is supposed to sound weak.
      //
      // The AGC stays only as a fallback for a host where the AF gain could not
      // be read or written, where guessing beats silence.
      if (a.makeup != null) {
        audible = applyGain(samples, a.makeup);
      } else {
        a.gainLeft = nextGain(a.gainLeft ?? 1, peakOf(samples), samples.length / sampleRate);
        audible = applyGain(samples, a.gainLeft);
      }
    }
    // Applied last, after the limiter, and never above unity — so the operator's
    // volume can only ever make it quieter than the safe ceiling.
    const vol = Math.max(0, Math.min(1, volumeRef.current ?? 1));
    if (vol !== 1) {
      const scaled = new Float32Array(audible.length);
      for (let i = 0; i < audible.length; i++) scaled[i] = audible[i] * vol;
      audible = scaled;
    }

    const buf = a.ctx.createBuffer(2, audible.length, sampleRate);
    if (side === 'right') {
      buf.copyToChannel(audible, 1);
    } else {
      buf.copyToChannel(audible, 0);
      // With nothing confirmed to put in the right ear, the operator's own
      // receiver belongs in both. Splitting the moment the panel is switched on
      // left him listening to his own radio through one headphone shell, which
      // is uncomfortable and buys nothing until there is a second signal.
      if (!a.split) buf.copyToChannel(audible, 1);
    }

    const src = a.ctx.createBufferSource();
    src.buffer = buf;
    src.connect(a.ctx.destination);

    const key = side === 'left' ? 'nextLeft' : 'nextRight';
    const now = a.ctx.currentTime;
    // The alignment delay rides on top of the jitter buffer, so it survives
    // every restart below without being reapplied anywhere.
    const lead = Math.min(MAX_LEAD_S,
                          LEAD[side] + (a.align?.side === side ? a.align.offset : 0));

    const plan = planStart({ queuedUntil: a[key], now, lead });
    if (plan.skip) {
      // The queue is deeper than intended. Let this frame go rather than
      // flushing what is already scheduled: 20 ms lost is inaudible, a flush
      // is a hole.
      a.dropped = (a.dropped ?? 0) + 1;
      return;
    }
    const startAt = plan.startAt;
    src.start(startAt);
    a[key] = startAt + buf.duration;

    // Stamped with when it will be *heard*, not when it arrived — see align.js.
    a.env[side].push(audible, sampleRate, startAt);

    // Held so a realignment can cut what has been promised but not yet played.
    a.pending[side].push(src);
    src.onended = () => {
      const i = a.pending[side].indexOf(src);
      if (i >= 0) a.pending[side].splice(i, 1);
    };
  }, []);

  /**
   * Re-cut one ear. Anything already scheduled was scheduled against the old
   * offset, so it has to go: letting it play would put a seam of doubled or
   * dropped audio exactly where the operator is listening hardest.
   */
  const recut = useCallback((side) => {
    const a = audio.current;
    for (const src of a.pending[side].splice(0)) { try { src.stop(); } catch { /* already ended */ } }
    a[side === 'left' ? 'nextLeft' : 'nextRight'] = 0;
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

      // RF telemetry shares this socket with the audio. Checked first because
      // it is cheap and because a meters frame must never fall through to the
      // audio path and be scheduled as sound.
      const met = parseRxMeters(ev.data);
      if (met) {
        setLocalRf((prev) => ({ ...prev, dbm: met.signalMaxBin, agcGain: met.agcGain }));
        return;
      }
      const q = parseSignalQuality(ev.data);
      if (q) {
        const prevAvg = localSnrAvg.current;
        localSnrAvg.current = prevAvg == null ? q.snrDb : prevAvg + 0.2 * (q.snrDb - prevAvg);
        setLocalRf((prev) => ({ ...prev, snrDb: q.snrDb, noiseDbm: q.integratedNoiseDbm }));
        return;
      }

      const f = parseAudioFrame(ev.data);
      if (!f) return;                                  // display frames share this socket
      const mono = f.channels === 1
        ? f.samples
        : f.samples.filter((_, i) => i % f.channels === 0);
      // Muting silences this panel's copy, not the meter: the operator still
      // wants to see what their own receiver is hearing while they listen to
      // Zeus's own output instead of ours.
      let peak = 0;
      for (let i = 0; i < mono.length; i++) peak = Math.max(peak, Math.abs(mono[i]));

      // The reference every candidate is screened against. Stamped on arrival
      // and recorded even when the left ear is muted — screening must keep
      // working when the operator is listening to Zeus's own output instead.
      const a = audio.current;
      if (a.ctx) a.envArrival.pushLevel(peak, a.ctx.currentTime, mono.length / f.sampleRate);

      if (!gateRef.current.mutedLeft) schedule('left', mono, f.sampleRate);

      // What the operator hears, not what arrived: with Zeus's volume down the
      // raw stream is near zero, and a meter reading zero on audible speech is
      // just wrong.
      levels.current.left = Math.min(1, peak * (a.makeup ?? a.gainLeft ?? 1));
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
    if (remote.current.has(rx.host) || refused.current.has(rx.host)) return;
    if (remote.current.size >= budgetRef.current) return;

    const Decoder = OpusDecoder();
    if (!Decoder) { setMessage({ bad: true, text: 'the Opus decoder did not load' }); return; }

    let admit;
    try {
      const res = await api.callBackend('POST', '/connect', { host: rx.host });
      admit = await res.json().catch(() => ({}));
      if (!res.ok) {
        // A failure here used to be swallowed and retried every second — a
        // hundred and fifty times against a route that had been deleted, with
        // nothing on screen. Now it is said once and the host is not tried
        // again this session, whether the instance refused or the plugin is
        // broken. Silence was the actual bug; the 404 was easy.
        refused.current.add(rx.host);
        setMessage({
          bad: true,
          text: `${rx.host.split('.')[0]}: ${admit.error ?? `connect failed (${res.status})`}`,
        });
        return;
      }
    } catch (e) {
      refused.current.add(rx.host);
      setMessage({ bad: true, text: `${rx.host.split('.')[0]}: ${e}` });
      return;
    }

    const hz = radioRef.current?.vfoHz ?? 0;
    const mode = (radioRef.current?.mode ?? 'usb').toLowerCase();
    const url = `${admit.wsBase}/ws?frequency=${hz}&mode=${encodeURIComponent(mode)}`
      + `&user_session_id=${encodeURIComponent(admit.sessionId)}&format=opus&version=${admit.version}`;

    const decoder = new Decoder();
    await decoder.ready;

    const ws = new WebSocket(url);
    ws.binaryType = 'arraybuffer';
    // The whole directory record, not just the host: the operator wants to know
    // *who* is copying them best, and a hostname is not an answer.
    // Screened continuously, whether or not it is the one being heard: a
    // candidate has to be proved to be carrying *your* QSO before it can be
    // promoted, and that cannot be established in the moment it is promoted.
    const entry = { ws, decoder, snr: null, since: Date.now(), rx,
                    env: new EnvelopeTracker(SYNC_WINDOW_S + 6), sync: null,
                    level: 0, peak: 0, hist: [], snrHist: [] };
    remote.current.set(rx.host, entry);

    ws.onmessage = (ev) => {
      if (typeof ev.data === 'string') return;
      // Stamped on arrival, and taken from the header rather than the decoded
      // audio — a candidate that is not being heard is not being decoded, and
      // the header states its baseband power outright.
      const lvl = readLevel(ev.data);
      if (lvl != null && audio.current.ctx) {
        entry.env.pushLevel(lvl, audio.current.ctx.currentTime, 0.02);
        // Meter ballistics on the received level itself. This is what the
        // receiver is actually hearing, not what survived its AGC and ours —
        // so the bars are comparable between stations, which is the only reason
        // to put them one above the other.
        entry.level = lvl > entry.level ? lvl : entry.level * 0.82 + lvl * 0.18;
      }

      const snr = readSnr(ev.data);
      if (snr != null) {
        // A time-based average, so it means the same thing whatever rate the
        // frames arrive at — a per-frame coefficient would silently become a
        // different filter on a receiver using a different Opus frame size.
        const at = Date.now();
        const dt = entry.snrAt ? at - entry.snrAt : 0;
        entry.snrAt = at;
        const k = 1 - Math.exp(-dt / RANK_TAU_MS);
        entry.snr = entry.snr == null ? snr : entry.snr + k * (snr - entry.snr);
      }

      // Only the chosen receiver is decoded. The others are measured from their
      // 21-byte headers alone, which is what makes holding three affordable.
      // Receive only, and only when asked for. Remote audio during transmit
      // would be your own signal arriving a second late in one ear while you
      // speak, which is the delayed-feedback effect that wrecks fluent speech —
      // and on speakers it is a howl put on the air.
      const gate = gateRef.current;
      if (gate.mutedRight || (gate.keyed && !gate.txMonitor)) return;
      if (bestRef.current !== rx.host || ev.data.byteLength <= HEADER_BYTES) return;
      try {
        const payload = new Uint8Array(ev.data, HEADER_BYTES);
        const { channelData, samplesDecoded, sampleRate } = entry.decoder.decodeFrame(payload);
        if (samplesDecoded) {
          let pk = 0;
          for (let i = 0; i < channelData[0].length; i++) {
            const v = Math.abs(channelData[0][i]); if (v > pk) pk = v;
          }
          levels.current.right = Math.min(1, pk);
          schedule('right', channelData[0], sampleRate);
        }
      } catch { /* one bad frame must not stop the ear */ }
    };
    ws.onclose = () => remote.current.delete(rx.host);
  }, [api, schedule]);

  const saveConfig = useCallback(async (patch) => {
    try {
      const cur = await (await api.callBackend('GET', '/config')).json().catch(() => ({}));
      await api.callBackend('POST', '/config', { ...(cur ?? {}), ...patch });
    } catch { /* the setting is still live for this session */ }
  }, [api]);

  /**
   * Take Zeus's own playback down, remembering where it was.
   *
   * <p>Zeus plays the same stream this panel reads, into both ears, so leaving
   * it up means hearing everything twice. Its AF gain is what its volume slider
   * drives and it does not touch the WebSocket lane, so lowering it silences
   * Zeus while this panel keeps its audio — verified on a running engine, along
   * with the fact that Zeus does not re-assert the value afterwards.
   */
  const silenceZeus = useCallback(async () => {
    try {
      const cur = (await (await api.callBackend('GET', '/af-gain')).json())?.db;
      // Only capture a level worth going back to. If it is already down at the
      // silence value — because the operator lowered it by hand, or a previous
      // run left it there — overwriting the stored original would strand him
      // with a radio that stays silent when he switches the panel off.
      if (cur != null && cur > SILENCE_AF_DB + 5) {
        restoreAf.current = cur;
        void saveConfig({ restoreAfGainDb: cur });
      }
      const res = await api.callBackend('POST', '/af-gain', { db: SILENCE_AF_DB });

      // Exactly the attenuation just applied, as a linear factor — computed
      // from the level read a moment ago, NOT from the stored restore value.
      //
      // Those two are not the same thing and the difference is loud. The stored
      // value is whatever the slider was on some earlier run; the attenuation
      // actually applied is measured from where it stood just now. Using the
      // stored one over-amplified by the difference between them — 20 dB of
      // staleness is a factor of ten too much gain, which arrives as a wall of
      // noise in both ears.
      //
      // And if the write did not take, nothing was attenuated, so nothing may
      // be put back: fall through to the adaptive gain instead of multiplying
      // an unattenuated stream by three hundred.
      audio.current.makeup = res?.ok && Number.isFinite(cur)
        ? Math.pow(10, (cur - SILENCE_AF_DB) / 20)
        : null;
    } catch { /* the panel still works, the operator just hears Zeus too */ }
  }, [api, saveConfig]);

  /** Put it back, so switching the feature off returns a working radio. */
  const restoreZeus = useCallback(async () => {
    const db = restoreAf.current ?? 0;
    audio.current.makeup = null;
    try { await api.callBackend('POST', '/af-gain', { db }); } catch { /* nothing to do */ }
  }, [api]);

  const stopAll = useCallback(() => {
    for (const host of [...remote.current.keys()]) dropRemote(host);
    const ws = engineWs.current;
    if (ws) { try { requestAudio(ws, false); ws.close(); } catch { /* gone */ } }
    engineWs.current = null;
    setBest(null); bestRef.current = null; setPool([]);
    setRunning(false);
    // Give the operator his radio back at the level he had it.
    void restoreZeus();
  }, [dropRemote, restoreZeus]);

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

  // ---- the meters ---------------------------------------------------------
  //
  // Fall time is slower than rise, the way a VU meter behaves: a bar that
  // tracked the waveform exactly would be a blur, and what the operator wants
  // from it is the shape of the speech, not the samples. The peak marker holds
  // longer still, so a brief burst leaves something to see.
  useEffect(() => {
    if (!running) return;
    const t = window.setInterval(() => {
      const L = levels.current;
      L.peakL = Math.max(L.peakL * 0.94, L.left);
      setLocalLevel(L.left);
      L.left *= 0.7;

      L.tick = (L.tick ?? 0) + 1;
      const sample = L.tick % SPARK_EVERY === 0;
      const rows = [];
      for (const [host, e] of remote.current) {
        e.peak = Math.max(e.peak * 0.94, e.level);
        if (sample) {
          e.hist = [...(e.hist ?? []), e.peak].slice(-SPARK_LEN);
          e.snrHist = [...(e.snrHist ?? []), e.snr].slice(-TRACK_LEN);
          e.peak = e.level;            // start a fresh window for the next sample
        }
        rows.push({ host, rx: e.rx, level: e.level, peak: e.peak, snr: e.snr,
                    sync: e.sync, hist: e.hist ?? [], heard: host === bestRef.current });
      }
      setRemoteRows(rows);

      if (sample) {
        const a2 = audio.current;
        a2.snrLocal = [...(a2.snrLocal ?? []), localSnrAvg.current].slice(-TRACK_LEN);
        setTracks([
          { key: 'local', label: 'your radio', local: true, data: a2.snrLocal },
          ...[...remote.current.entries()].map(([host, e]) => ({
            key: host,
            label: e.rx?.callsign || host.split('.')[0],
            heard: host === bestRef.current,
            ok: !!e.sync?.ok,
            data: e.snrHist ?? [],
          })),
        ]);
      }
    }, 60);
    return () => window.clearInterval(t);
  }, [running]);

  // ---- is this candidate even hearing my QSO? ----------------------------
  //
  // Every connected candidate is correlated against the local receiver
  // continuously, not just the one being heard. A receiver cannot be checked in
  // the instant it is promoted — by then it is already in the operator's ear —
  // so the evidence has to have been gathered while it was still a candidate.
  useEffect(() => {
    if (!running) return;
    const t = window.setInterval(() => {
      const a = audio.current;
      if (!a.ctx) return;
      const len = Math.round(SYNC_WINDOW_S * ENVELOPE_HZ);
      // How much history each side actually has. Reported rather than kept
      // private: "?" on a row is indistinguishable from a stalled measurement,
      // and this session has already lost hours to guessing at hidden state.
      // Screening compares the operator's own audio against each candidate's.
      // Below the noise floor there is nothing on his side to compare, and
      // every receiver will fail for a reason that has nothing to do with it.
      // Saying so beats three rows of low numbers that invite the wrong fix.
      const localSnr = localRfRef.current?.snrDb;
      const at = Date.now();
      if (localSnr == null || localSnr >= LOCAL_MIN_SNR_DB) deafSince.current = 0;
      else if (!deafSince.current) deafSince.current = at;
      setScreening({
        localS: a.envArrival.end / ENVELOPE_HZ,
        needS: SYNC_WINDOW_S,
        localSnr,
        deaf: !!deafSince.current && at - deafSince.current >= LOCAL_DEAF_AFTER_MS,
        confirmed: beatenBy.current.confirmed,
        better: beatenBy.current.better,
        deafForS: deafSince.current ? (at - deafSince.current) / 1000 : 0,
        remotes: [...remote.current.values()].map((e) => e.env.end / ENVELOPE_HZ),
      });
      for (const e of remote.current.values()) {
        const end = Math.min(a.envArrival.end, e.env.end);
        if (end < len) {
          // Not a verdict — a measurement that has not happened yet.
          e.sync = { pending: true, haveS: end / ENVELOPE_HZ };
          continue;
        }
        const est = estimateLag(a.envArrival.series(end, len), e.env.series(end, len),
                                Math.round(ALIGN_MAX_S * ENVELOPE_HZ));
        e.sync = {
          quality: est.quality,
          depth: est.depth,
          lag: est.lag / ENVELOPE_HZ,
          // Depth first: it answers "is anyone speaking", which has to be true
          // before "is it the same speech" can mean anything.
          ok: est.depth >= MIN_DEPTH
              && est.quality >= SYNC_MIN_QUALITY
              && est.margin >= 0.05,
        };
      }
    }, 2000);
    return () => window.clearInterval(t);
  }, [running]);

  // ---- lining the two ears up --------------------------------------------
  //
  // The remote ear is a second or so behind the local one, and hearing the same
  // speech twice that far apart is genuinely disruptive rather than merely
  // untidy. The measurement is envelope cross-correlation — see align.js for
  // why the obvious waveform correlation cannot work between two receivers.
  useEffect(() => {
    if (!running) return;
    let alive = true;
    let prevOffset = null;
    const t = window.setInterval(() => {
      const a = audio.current;
      if (!alive || !a.ctx || mutedLeft || mutedRight || keyed) return;

      const end = Math.min(a.env.left.end, a.env.right.end);
      const len = Math.round(ALIGN_WINDOW_S * ENVELOPE_HZ);
      if (end < len) return;                       // not enough history yet

      // Search only as far as a credible correction. A wider search is a wider
      // opportunity for a spurious peak, and there is nothing out there to find.
      const est = estimateLag(a.env.left.series(end, len), a.env.right.series(end, len),
                              Math.round(ALIGN_MAX_S * ENVELOPE_HZ));
      const d = decideAlignment(est, { current: a.align, maxSec: ALIGN_MAX_S,
                                       minChangeSec: 0.15 });
      if (!d.change) {
        // Only report a failure once there is nothing to fall back on; a
        // settled alignment does not need re-announcing every two seconds.
        if (!a.align) setAlignment({ pending: true, reason: d.reason });
        return;
      }
      a.align = { side: d.side, offset: d.offset };
      setAlignment({ side: d.side, offset: d.offset, quality: d.quality });
      // Only cut the stream when the correction is large enough to be worth a
      // hole in the audio. A small change takes effect on its own as soon as
      // the queue next runs dry, because the lead is read fresh every buffer.
      if (Math.abs((prevOffset ?? 0) - d.offset) > 0.25) recut(d.side);
      prevOffset = d.offset;
    }, ALIGN_EVERY_MS);
    return () => { alive = false; window.clearInterval(t); };
  }, [running, mutedLeft, mutedRight, keyed, recut]);

  // Alignment belongs to a particular remote receiver: a different one is a
  // different path with a different delay, so the old figure is not a starting
  // point, it is a wrong answer.
  useEffect(() => {
    if (!best?.host) return;
    audio.current.align = null;
    setAlignment(null);
    recut('left');
  }, [best?.host, recut]);

  // Switch to two-ear operation exactly when a confirmed receiver is playing.
  useEffect(() => {
    const want = !mutedRight && !!best;
    if (want === audio.current.split) return;
    audio.current.split = want;
    setSplit(want);
    // Buffers already queued carry the old channel layout; letting them play
    // would put the local signal in both ears on top of the remote.
    recut('left');
  }, [mutedRight, best, recut]);

  // A closed panel must never leave Zeus silenced. Without this, closing the
  // tab looks exactly like a dead receiver.
  useEffect(() => () => { void restoreZeus(); }, [restoreZeus]);

  // ---- keying, so the right ear is receive-only ---------------------------
  useEffect(() => {
    if (!running) return;
    let alive = true;
    const tick = async () => {
      try {
        const r = await api.callBackend('GET', '/ptt');
        if (!r.ok || !alive) return;
        const { keyed: k } = await r.json();
        setKeyed(!!k);
      } catch { /* a restarting engine is not worth an error */ }
    };
    void tick();
    const t = window.setInterval(tick, 150);
    return () => { alive = false; window.clearInterval(t); };
  }, [running, api]);

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
    let heldSince = 0;
    let challenger = null;

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
        .map(([host, e]) => ({ host, snr: e.snr, since: e.since, rx: e.rx, sync: e.sync }))
        .sort((a, b) => (b.snr ?? -999) - (a.snr ?? -999));
      setPool(ranked);

      // Only receivers proved to be carrying this QSO are eligible. An
      // unverified one is not a weaker choice, it is an unknown signal, and
      // silence is better than a stranger in one ear.
      // Confirmed as carrying this QSO...
      const confirmed = ranked.filter((r) => r.sync?.ok);
      // ...and actually better at it than the operator's own receiver.
      // The best confirmed remote gets the ear, whether or not it beats the
      // operator's own receiver. Requiring it to be better sounded principled
      // and was wrong in practice: a second opinion on a signal is worth having
      // even when it is the weaker one, and the operator can see both figures
      // and judge for himself.
      const localSnr = localSnrAvg.current;
      const verified = confirmed;
      beatenBy.current = { localSnr, confirmed: confirmed.length, better: verified.length };

      // ---- who gets the ear, with hysteresis ----
      const at = Date.now();
      // Swapping receivers mid-transmission would make the monitor useless: the
      // point is to hear one station consistently for the length of an over,
      // not to hear the strongest one moment by moment.
      // A receiver that has lost sync stops being the incumbent, so it cannot be
      // defended by the hysteresis that exists to protect a *good* choice.
      const heldHost = verified.some((r) => r.host === bestRef.current) ? bestRef.current : null;
      const pin = pinnedRef.current;
      // A pin bypasses the S/N requirement — listening to a worse ear on
      // purpose is a legitimate thing to want, and it was the documented
      // behaviour while the code checked the wrong list and quietly dropped it
      // whenever the operator's own receiver was doing better.
      const pinIsUsable = pin && confirmed.some((r) => r.host === pin);
      const next = pinIsUsable
        ? { held: pin, heldSince: at, challenger: null, chasing: null }
        : gateRef.current.keyed
          ? { held: heldHost, heldSince, challenger, chasing: null }
          : chooseEar({ ranked: verified, held: heldHost, heldSince, challenger, now: at });
      if (next.held !== bestRef.current) {
        bestRef.current = next.held;
        // A promoted receiver already has a measured offset from screening, so
        // it starts roughly in step instead of drifting in over ten seconds.
        const lag = verified.find((r) => r.host === next.held)?.sync?.lag;
        audio.current.align = lag > 0 ? { side: 'left', offset: Math.min(lag, ALIGN_MAX_S) } : null;
        // Start the new source's queue from scratch: the old one's lead time
        // belongs to audio that is no longer coming.
        if (audio.current.ctx) audio.current.nextRight = 0;
      }
      heldSince = next.heldSince;
      challenger = next.challenger;
      setChasing(next.chasing);

      setBest(verified.find((r) => r.host === bestRef.current) ?? null);

      // Over budget, because the operator just lowered it. Let the weakest go,
      // one per tick, and never the one being heard. The top-up below could
      // only ever add — so the setting appeared to do nothing until the panel
      // was restarted, which is worse than not offering it.
      if (remote.current.size > budgetRef.current) {
        const spare = [...ranked].reverse().find((r) => r.host !== bestRef.current);
        if (spare) dropRemote(spare.host);
      }

      // Top up to the budget, then rotate the worst out on a timer.
      if (remote.current.size < budgetRef.current && hz > 0) {
        // Not before the frequency is known: connecting at 0 Hz would tune every
        // receiver to nothing and measure it.
        try {
          const ex = excluded.current.length
            ? `&exclude=${encodeURIComponent(excluded.current.join(','))}` : '';
          const sel = SELECTIONS.find((x) => x.id === selectionRef.current) ?? SELECTIONS[0];
          const rg = sel.region ? `&region=${sel.region}` : '';
          const res = await api.callBackend('GET',
            `/receivers?count=${budgetRef.current * 3}&preset=${sel.preset}${rg}${ex}`);
          const d = await res.json();
          const next = (d.suggested ?? []).find(
            (rx) => !remote.current.has(rx.host) && !refused.current.has(rx.host));
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

  /** Strike a receiver off, drop it, and remember it. */
  const excludeHost = useCallback(async (host) => {
    if (excluded.current.includes(host)) return;
    excluded.current = [...excluded.current, host];
    dropRemote(host);
    if (bestRef.current === host) bestRef.current = null;
    try {
      const cur = await (await api.callBackend('GET', '/config')).json().catch(() => ({}));
      await api.callBackend('POST', '/config',
        { ...(cur ?? {}), excludeHosts: excluded.current });
    } catch { /* excluded for this session regardless */ }
  }, [api, dropRemote]);

  useEffect(() => { stopRef.current = stopAll; }, [stopAll]);

  const start = useCallback(async () => {
    setMessage(null);
    try {
      await ensureAudio();
      await silenceZeus();
      await startEngineAudio();
      setRunning(true);
    } catch (e) {
      setMessage({ bad: true, text: 'could not start: ' + e });
    }
  }, [ensureAudio, silenceZeus, startEngineAudio]);

  // ---- render -------------------------------------------------------------

  const bar = (v) => h('div', {
    style: { height: 4, borderRadius: 2, background: 'var(--bg-2, #16181d)', overflow: 'hidden' },
  }, h('div', { style: {
    height: '100%', width: `${Math.max(0, Math.min(100, v))}%`,
    background: 'var(--success, #4fbfa0)', transition: 'width .2s linear',
  } }));

  // The collapsed row's two-trace graph: the operator heavy, the receiver he is
  // hearing light, ~30 s across about ninety pixels. A present-tense number
  // cannot show a remote climbing towards you; this can.
  useEffect(() => {
    if (typeof document === 'undefined') return;      // panel-check has no DOM
    const cv = document.getElementById('ubersdr-strip');
    if (!cv || !cv.getContext) return;
    const w = cv.clientWidth || 90, hgt = 20;
    const k = Math.min(window.devicePixelRatio || 1, 2);
    cv.width = w * k; cv.height = hgt * k;
    const g = cv.getContext('2d');
    g.scale(k, k);
    g.clearRect(0, 0, w, hgt);

    const LO = -10, HI = 40;
    const y = (db) => hgt - 2 - ((Math.max(LO, Math.min(HI, db)) - LO) / (HI - LO)) * (hgt - 4);
    const line = (data, heavy, colour) => {
      const d = (data ?? []).filter((v) => v != null).slice(-40);
      if (d.length < 2) return;
      g.beginPath();
      d.forEach((v, i) => {
        const px = (i / (d.length - 1)) * w;
        return i ? g.lineTo(px, y(v)) : g.moveTo(px, y(v));
      });
      g.lineWidth = heavy ? 2 : 1;
      g.strokeStyle = colour;
      g.stroke();
    };
    const me = tracks.find((t) => t.local);
    const them = tracks.find((t) => t.heard);
    line(them?.data, false, mutedRight ? 'rgba(128,136,150,.6)' : '#4fbfa0');
    line(me?.data, true, '#6ea8fe');
  }, [tracks, mutedRight]);

  // The S/N overlay. Drawn rather than laid out: sixty points per feed, five
  // feeds, re-rendered every second — that is a picture, not a DOM tree.
  useEffect(() => {
    if (view !== 'levels' || typeof document === 'undefined') return;
    const cv = document.getElementById('ubersdr-tracks');
    if (!cv || !cv.getContext) return;
    const w = cv.clientWidth || 520, h = 190;
    const k = Math.min(window.devicePixelRatio || 1, 2);
    cv.width = w * k; cv.height = h * k;
    const g = cv.getContext('2d');
    g.scale(k, k);
    g.clearRect(0, 0, w, h);

    const LO = -10, HI = 40;                       // dB S/N
    const y = (db) => h - 18 - ((Math.max(LO, Math.min(HI, db)) - LO) / (HI - LO)) * (h - 30);
    const x = (i, n) => 34 + (i / Math.max(1, n - 1)) * (w - 44);

    // grid
    g.strokeStyle = 'rgba(128,136,150,.18)';
    g.fillStyle = 'rgba(128,136,150,.75)';
    g.font = '10px ui-monospace, monospace';
    for (const db of [0, 10, 20, 30, 40]) {
      g.beginPath(); g.moveTo(34, y(db)); g.lineTo(w - 10, y(db)); g.stroke();
      g.fillText(String(db), 8, y(db) + 3);
    }
    g.fillText('dB S/N', 8, 12);
    g.fillText('now', w - 26, h - 4);
    g.fillText('-45 s', 34, h - 4);

    for (const t of tracks) {
      const d = (t.data ?? []).filter((v) => v != null);
      if (d.length < 2) continue;
      g.beginPath();
      d.forEach((v, i) => (i ? g.lineTo(x(i, d.length), y(v)) : g.moveTo(x(i, d.length), y(v))));
      // The operator's own receiver is the reference every other line is read
      // against, so it is drawn heavier and in the accent colour.
      g.lineWidth = t.local ? 2.6 : 1.4;
      g.strokeStyle = t.local ? 'var-accent'
        : t.heard ? '#4fbfa0' : t.ok ? '#58bccd' : 'rgba(128,136,150,.5)';
      if (t.local) g.strokeStyle = '#d9a441';
      g.setLineDash(t.ok || t.local ? [] : [3, 3]);
      g.stroke();
      g.setLineDash([]);
      // label at the right-hand end, where the eye finishes the line
      const last = d[d.length - 1];
      g.fillStyle = g.strokeStyle;
      g.font = (t.local ? '600 ' : '') + '10px ui-sans-serif, system-ui, sans-serif';
      g.fillText(t.label, Math.min(w - 60, x(d.length - 1, d.length) + 4), y(last) + 3);
    }
  }, [view, tracks]);

  // Whether the engine's RF telemetry is actually flowing. A stopped WDSP
  // stage reports about −400, which must read as "no measurement" and not as
  // an extremely weak signal.
  const rfOk = localRf?.dbm != null && !isMeterSentinel(localRf.dbm);

  /**
   * Receivers at their true bearing and range.
   *
   * <p>Radius is logarithmic, so a receiver 60 km away and one 3000 km away can
   * share a screen without the near one collapsing onto the centre.
   */
  const bearingView = () => {
    const R = 150, cx = 175, cy = 165;
    const ring = (km) => (Math.log10(Math.max(30, km)) - 1.4) / (3.6 - 1.4) * R;
    const at = (r) => {
      const b = ((r.rx?.bearingDegrees ?? 0) - 90) * Math.PI / 180;
      const rad = ring(r.rx?.distanceKm ?? 100);
      return [cx + rad * Math.cos(b), cy + rad * Math.sin(b)];
    };
    return h('svg', {
      viewBox: '0 0 350 330', style: { width: '100%', maxWidth: 420, height: 'auto' },
      role: 'img',
      'aria-label': 'Receivers by bearing and range from your station',
    },
      ...[0.34, 0.62, 1].map((f) => h('circle', {
        key: f, cx, cy, r: R * f, fill: 'none', stroke: 'var(--border, #2a2e35)',
      })),
      ...[[cx, cy - R - 8, cx, cy + R + 8], [cx - R - 8, cy, cx + R + 8, cy]].map((l, i) =>
        h('line', { key: i, x1: l[0], y1: l[1], x2: l[2], y2: l[3],
                    stroke: 'var(--border, #2a2e35)', strokeOpacity: 0.6 })),
      ...[['N', cx, cy - R - 12], ['E', cx + R + 14, cy + 4], ['S', cx, cy + R + 20],
          ['W', cx - R - 14, cy + 4]].map(([t, tx, ty]) =>
        h('text', { key: t, x: tx, y: ty, textAnchor: 'middle', fontSize: 10,
                    fill: 'var(--fg-3, #5a5e66)' }, t)),
      ...[100, 500, 2500].map((km, i) => h('text', {
        key: km, x: cx + 4, y: cy - ring(km) + 3, fontSize: 9,
        fill: 'var(--fg-3, #5a5e66)', opacity: 0.7,
      }, i === 2 ? `${km} km` : String(km))),

      ...remoteRows.flatMap((r) => {
        if (r.rx?.distanceKm == null || r.rx?.bearingDegrees == null) return [];
        const [px, py] = at(r);
        const playing = r.heard && !mutedRight;
        const col = playing ? 'var(--success, #4fbfa0)'
          : r.sync?.ok ? 'var(--accent-2, #58bccd)' : 'var(--fg-3, #5a5e66)';
        return [
          h('line', { key: `l${r.host}`, x1: cx, y1: cy, x2: px, y2: py, stroke: col,
                      strokeWidth: playing ? 2 : 1,
                      strokeDasharray: r.sync?.ok ? undefined : '3 4', strokeOpacity: 0.75 }),
          h('circle', {
            key: `c${r.host}`, cx: px, cy: py, r: playing ? 8 : 5,
            fill: r.sync?.ok ? col : 'none', stroke: col, strokeWidth: 1.5,
            style: { cursor: r.sync?.ok ? 'pointer' : 'default' },
            onClick: () => {
              if (!r.sync?.ok) return;
              setPinned(r.host); setMutedRight(false);
            },
          }, h('title', {}, `${r.rx?.callsign || r.host} · ${Math.round(r.rx.distanceKm)} km · `
              + `${Math.round(r.rx.bearingDegrees)}°`)),
          h('text', { key: `t${r.host}`, x: px + 11, y: py + 3, fontSize: 11,
                      fill: playing ? col : 'var(--fg-2, #a3aab8)' },
            r.rx?.callsign || r.host.split('.')[0]),
        ];
      }),
      h('path', { d: `M${cx} ${cy - 6} l6 11 h-12 z`, fill: 'var(--warn, #d8a657)' }),
      h('text', { x: cx, y: cy + 24, textAnchor: 'middle', fontSize: 9,
                  fill: 'var(--warn, #d8a657)' }, 'you'));
  };

  /**
   * Settings, in a modal off the cogwheel.
   *
   * <p>These belong out of the way: they are set once and then left alone,
   * while everything else on the panel is read continuously. Keeping them in
   * the controls row cost a line of chrome on every panel, in both sizes, for
   * a choice made twice a month.
   */
  const settingsModal = () => h('div', {
    style: css.scrim,
    onClick: () => setSettingsOpen(false),
  },
    h('div', {
      style: css.modal,
      onClick: (e) => e.stopPropagation(),
      role: 'dialog',
      'aria-label': 'Remote Ears settings',
    },
      h('div', { style: css.modalHead },
        h('strong', {}, 'Remote Ears'),
        h('span', { style: { ...css.note, marginLeft: 'auto' } }, `build ${BUILD}`)),

      h('div', { style: css.field },
        h('span', { style: css.fieldLabel }, 'output level'),
        h('input', {
          type: 'range', min: '0', max: '100', step: '1',
          value: String(Math.round(volume * 100)),
          style: { width: '100%' },
          onChange: (e) => {
            const v = Number(e.target.value) / 100;
            setVolume(v); volumeRef.current = v;
            void saveConfig({ volume: v });
          },
        }),
        h('span', { style: css.note },
          'This panel owns the audio while it runs, so Zeus\u2019s own slider does '
          + 'nothing \u2014 set the listening level here.')),

      h('div', { style: css.field },
        h('span', { style: css.fieldLabel }, 'receivers monitored'),
        h('select', {
          style: css.select,
          value: String(budget),
          onChange: (e) => {
            const n = Number(e.target.value);
            setBudget(n); budgetRef.current = n;
            void saveConfig({ count: n });
          },
        }, ...BUDGET_CHOICES.map((n) => h('option', { key: n, value: String(n) },
            `${n} receivers`))),
        h('span', { style: css.note },
          'Each one is a client slot on somebody else\u2019s hardware.')),

      h('div', { style: css.field },
        h('span', { style: css.fieldLabel }, 'which receivers'),
        h('select', {
          style: css.select,
          value: selection,
          onChange: (e) => {
            const id = e.target.value;
            setSelection(id); selectionRef.current = id;
            const sel = SELECTIONS.find((x) => x.id === id) ?? SELECTIONS[0];
            void saveConfig({ preset: sel.preset, region: sel.region ?? '' });
            for (const host of [...remote.current.keys()]) dropRemote(host);
          },
        }, ...SELECTIONS.map((x) => h('option', { key: x.id, value: x.id }, x.label)))),

      excluded.current.length
        ? h('div', { style: css.field },
            h('span', { style: css.fieldLabel }, 'blocked'),
            ...excluded.current.map((host) => h('div', {
              key: host,
              style: { display: 'flex', alignItems: 'center', gap: 8, fontSize: 12 },
            },
              h('span', {}, host),
              h('button', {
                style: { ...css.button, marginLeft: 'auto', fontSize: 11, padding: '2px 8px' },
                onClick: () => {
                  excluded.current = excluded.current.filter((x) => x !== host);
                  void saveConfig({ excludeHosts: excluded.current });
                  setSettingsOpen(false); setSettingsOpen(true);
                },
              }, 'unblock'))))
        : null,

      h('div', { style: css.note },
        'While this panel runs, Zeus\u2019s own volume is held down and restored when '
        + 'you stop \u2014 so its slider does nothing audible in the meantime.'),

      h('div', { style: { display: 'flex' } },
        h('button', { style: { ...css.button, marginLeft: 'auto' },
                      onClick: () => setSettingsOpen(false) }, 'close'))));

  /** One horizontal VU meter: who, the bar, and where. */
  const meterRow = ({ key, name, level, peak, muted, colour, right, dim, onClick, title,
                      rowStyle, pct, dbText, clickable = true, scale, ticks,
                      onExclude }) => h('div', {
    key: key ?? name,
    // A row that does nothing when clicked must not look like a row that does.
    // Half of these are inert until the station has been confirmed, and they
    // were indistinguishable from the ones that respond.
    style: { ...css.row, ...(rowStyle ?? {}), opacity: muted ? 0.45 : 1,
             cursor: clickable ? 'pointer' : 'default' },
    onClick: clickable ? onClick : undefined,
    title: title ?? (muted ? `unmute ${name}` : `mute ${name}`),
  },
    h('span', { style: { ...css.rowName, color: dim ? 'var(--fg-3, #5a5e66)' : undefined } },
      muted ? `\u2298${name}` : name),
    h('div', { style: css.meter },
      // Scale marks, so a bar length can be read as a number rather than only
      // compared with the bar above it.
      ...(ticks ?? TICKS_DB).map((db) => h('div', {
        key: db,
        style: { ...css.tick, left: `${(scale ?? meterPct)(Math.pow(10, db / 20))}%` },
      })),
      h('div', { style: { ...css.fill, width: `${pct ?? (scale ?? meterPct)(level)}%`,
                          background: colour } }),
      // The peak marker is the part that survives a syllable; without it a
      // quiet passage looks identical to a dead stream.
      peak > 0.002
        ? h('div', { style: { ...css.peak, left: `${Math.min(99.5, (scale ?? meterPct)(peak))}%` } })
        : null),
    h('span', { style: { ...css.rowDb, color: dim ? 'var(--fg-3, #5a5e66)' : undefined } },
      dbText ?? (level > 1e-6 ? `${amplitudeToDb(level).toFixed(0)} dB` : '\u2014')),
    h('span', { style: css.rowRight }, right),
    // Struck off for good, not just skipped. A receiver that cannot hear your
    // antenna will never earn a tick, and it would otherwise hold one of the
    // three connections for the whole session.
    onExclude
      ? h('button', {
          style: css.exclude,
          onClick: (e) => { e.stopPropagation(); onExclude(); },
          title: 'never use this receiver',
          'aria-label': 'exclude this receiver',
        }, '\u00d7')
      : null);

  /** Everything, in a modal off the expand control. */
  const fullView = () => h('div', { style: css.scrim },
    h('div', {
      style: { ...css.modal, width: 'min(880px, calc(100vw - 32px))' },
      onClick: (e) => e.stopPropagation(),
      role: 'dialog',
      'aria-label': 'Remote Ears',
    },
      h('div', { style: css.modalHead },
        h('strong', {}, 'Remote Ears'),
        h('button', { style: { ...css.cog, marginLeft: 'auto' },
                      onClick: () => setSettingsOpen(true),
                      title: 'settings', 'aria-label': 'settings' }, '\u2699'),
        h('button', { style: css.cog,
                      onClick: () => setExpanded(false),
                      title: 'close', 'aria-label': 'close' }, '\u00d7')),


    // Two stacked meters. Clicking one mutes that ear: the meter is the
    // control, so there is nothing to hunt for and nothing that can disagree
    // with what you are hearing.
    h('div', { style: css.ears },
      h('button', {
        style: {
          ...css.power,
          borderColor: running ? 'var(--success, #4fbfa0)' : undefined,
          color: running ? 'var(--success, #4fbfa0)' : 'var(--fg-3, #5a5e66)',
        },
        onClick: () => (running ? stopAll() : start()),
        title: running ? 'stop listening' : 'start listening',
        'aria-label': running ? 'stop listening' : 'start listening',
      }, '\u23fb'),

      meterRow({
        name: split ? 'local L' : 'local L+R',
        rowStyle: css.localRow,
        level: localLevel,
        peak: levels.current.peakL,
        muted: mutedLeft,
        colour: 'var(--accent, #6ea8fe)',
        // The engine's own calibrated peak-bin reading when it is flowing;
        // the decoded audio only as a fallback, marked AF so the two are never
        // mistaken for each other.
        pct: rfOk ? sMeterPct(localRf.dbm) : undefined,
        dbText: rfOk ? `${localRf.dbm.toFixed(0)} dBm` : undefined,
        right: [radio?.available ? `${(radio.vfoHz / 1e6).toFixed(3)} ${radio.mode}` : '—',
                rfOk ? sUnits(localRf.dbm) : 'AF',
                // The one figure that IS comparable with the receivers below.
                localRf?.snrDb != null ? `S/N ${localRf.snrDb.toFixed(0)}` : null,
               ].filter(Boolean).join(' · '),
        onClick: () => setMutedLeft(!mutedLeft),
      })),

    // Three ways of reading the same feeds. The console is the resting state
    // because operating is the common case; the other two answer questions the
    // console cannot — where a signal comes from, and how the feeds have
    // compared over the last minute.
    running
      ? h('div', { style: css.tabs },
          ...[['console', 'console'], ['bearing', 'bearing'], ['levels', 'S/N over time']]
            .map(([id, label]) => h('button', {
              key: id,
              style: { ...css.tab, ...(view === id ? css.tabOn : {}) },
              onClick: () => setView(id),
            }, label)))
      : null,

    // One tile per monitored station. The tile that is being heard carries a
    // green edge and a caret; everything else stays quiet. Place, distance,
    // bearing and correlation live in the tooltip — they are worth having but
    // not worth four lines of text each, every second, forever.
    running && view === 'console'
      ? h('div', { style: css.tiles }, ...remoteRows.map((r) => {
          const playing = r.heard && !mutedRight;
          const name = r.rx?.callsign || r.host.split('.')[0];
          const usable = r.heard || !!r.sync?.ok;
          const mark = r.sync?.ok ? '\u2713'
            : r.sync?.pending ? `${Math.round(r.sync.haveS)}/${SYNC_WINDOW_S}s`
            : r.sync ? (r.sync.depth < MIN_DEPTH ? '\u2717' : `\u2717 ${r.sync.quality.toFixed(2)}`)
            : '?';
          const delta = r.snr != null && localRf?.snrDb != null
            ? `${r.snr - localRf.snrDb >= 0 ? '+' : ''}${(r.snr - localRf.snrDb).toFixed(0)}`
            : '\u2014';
          return h('div', {
            key: r.host,
            style: {
              ...css.tile,
              borderColor: playing
                ? (keyed ? 'var(--warn, #d8a657)' : 'var(--success, #4fbfa0)')
                : undefined,
              opacity: r.sync?.ok || r.heard ? 1 : 0.6,
            },
            onClick: () => {
              if (r.heard) { setMutedRight(!mutedRight); return; }
              if (!r.sync?.ok) return;
              setPinned(r.host); setMutedRight(false);
            },
            title: [
              name,
              [r.rx?.location, r.rx?.distanceKm != null ? `${Math.round(r.rx.distanceKm)} km` : null,
               r.rx?.bearingDegrees != null ? `${Math.round(r.rx.bearingDegrees)}\u00b0` : null]
                .filter(Boolean).join(' \u00b7 '),
              r.snr != null ? `S/N ${r.snr.toFixed(0)} dB (${delta} vs you)` : null,
              r.sync?.quality != null ? `correlation ${r.sync.quality.toFixed(2)}` : null,
              usable ? (r.heard ? 'click to mute' : 'click to listen') : 'not confirmed yet',
            ].filter(Boolean).join('\n'),
          },
            h('span', { style: { ...css.tileHead,
                                 color: playing ? 'var(--success, #4fbfa0)' : undefined } },
              (playing ? '\u25b6 ' : '') + name),
            h('span', { style: css.spark },
              (r.hist.length
                ? r.hist.map((v) => SPARK[Math.min(7, Math.floor(rfPct(v) / 12.5))]).join('')
                : '').padStart(SPARK_LEN, '\u2581')),
            h('span', { style: css.tileDb },
              r.level > 1e-6 ? `${amplitudeToDb(r.level).toFixed(0)}` : '\u2014'),
            h('span', { style: css.tileFoot }, `${delta}  ${mark}`),
            h('button', {
              style: css.tileX,
              onClick: (e) => { e.stopPropagation(); excludeHost(r.host); },
              title: 'never use this receiver',
              'aria-label': 'exclude',
            }, '\u00d7'));
        }))
      : null,

    running && view === 'bearing' ? bearingView() : null,

    running && view === 'levels'
      ? h('div', {},
          h('canvas', { id: 'ubersdr-tracks', style: { width: '100%', height: 190 } }),
          h('div', { style: css.note },
            'Signal above noise, which is the only quantity the two sides can share \u2014 '
            + 'each measures its own noise floor and subtracts it. Absolute levels cannot go '
            + 'on one axis: your receiver reports calibrated dBm, theirs report their own '
            + 'baseband power, and nothing ties the two scales together. Your radio is the '
            + 'heavy line.'))
      : null,

    h('div', { style: { display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' } },
      pinned
        ? h('button', { style: css.button, onClick: () => setPinned(null),
                        title: 'go back to picking the best automatically' },
            'unpin \u2014 auto')
        : null,
      h('button', {
        style: {
          ...css.button,
          borderColor: mutedRight ? undefined : 'var(--success, #4fbfa0)',
          color: mutedRight ? undefined : 'var(--success, #4fbfa0)',
        },
        disabled: !running,
        onClick: () => setMutedRight(!mutedRight),
        title: mutedRight
          ? 'the right ear stays silent until a receiver has been confirmed'
          : 'silence the right ear',
      }, mutedRight ? '\u2298 remote ear off' : '\u25b6 remote ear on'),

      h('label', {
        style: { ...css.button, display: 'inline-flex', alignItems: 'center', gap: 6,
                 cursor: 'pointer',
                 borderColor: txMonitor ? 'var(--warn, #d8a657)' : undefined,
                 color: txMonitor ? 'var(--warn, #d8a657)' : undefined },
        title: 'hear yourself come back from the remote receiver while you transmit',
      },
        h('input', { type: 'checkbox', checked: txMonitor,
                     onChange: (e) => setTxMonitor(e.target.checked) }),
        'remote ears during TX (remote monitor)'),
      message ? h('span', { style: message.bad ? css.bad : null }, message.text) : null),

    running
      ? h('div', { style: css.note },
          pool.length
            // A tick means "proved to be hearing what you are hearing", which
            // is a different and more useful fact than a signal level.
            ? 'watching ' + pool.map((p) =>
                `${p.rx?.callsign || p.host.split('.')[0]} `
                + `${p.snr == null ? '…' : p.snr.toFixed(0) + ' dB'}`
                + `${p.sync?.ok ? ' \u2713' : p.sync ? ' \u2717' : ' ?'}`).join(' · ')
            : radio?.vfoHz
              ? 'looking for receivers…'
              : 'waiting for the radio')
      : null,

    h('div', { style: css.note },
      'Top bar is your own receiver in calibrated dBm (strongest bin in the '
      + 'passband, the engine\u2019s own measurement). The rest is each remote\u2019s '
      + 'baseband power. Absolute levels are not comparable across them \u2014 no '
      + 'shared calibration exists \u2014 but the S/N figures are.'),

    // What the screener has to work with. Without this, a row stuck on "?" is
    // silent about whether it is waiting for the local side, the remote side,
    // or nothing at all.
    running && screening?.deaf
      ? h('div', { style: { ...css.note, ...css.bad } },
          `your own receiver has been below its noise floor for `
          + `${Math.round(screening.deafForS)}s (${screening.localSnr.toFixed(0)} dB S/N). `
          + 'That is longer than a gap between overs \u2014 worth checking the antenna '
          + 'path, while the remote receivers below are hearing the band.')
      : running && mutedRight
        ? h('div', { style: css.note },
            `remote ear off \u2014 ${remoteRows.length} receiver`
            + `${remoteRows.length === 1 ? '' : 's'} still connected and being `
            + 'measured, which is why the bars move. Nothing is playing on the right.')
      : running && !mutedRight && !best
        ? h('div', { style: css.note },
            'remote ear on, nothing confirmed yet \u2014 your radio is in both ears '
            + 'until a receiver has been shown to be carrying this QSO.')
        : running && screening
          ? h('div', { style: css.note },
              `screening: local ${screening.localS.toFixed(0)}s, remotes `
              + screening.remotes.map((x) => `${x.toFixed(0)}s`).join('/')
              + ` (${screening.needS}s needed)`)
          : null,

    // One line, and it says the thing that is not visible in the bars.
    h('div', { style: css.note },
      alignment?.side
        ? `${alignment.side === 'left' ? 'remote' : 'local'} ${alignment.offset.toFixed(2)}s behind, `
          + `held back to match \u00b7 correlation ${alignment.quality.toFixed(2)}`
        : mutedLeft || mutedRight
          ? `both ears must be playing to line them up \u2014 `
            + `${mutedLeft && mutedRight ? 'both are' : (mutedLeft ? 'local' : 'remote') + ' is'} muted`
          : keyed
            ? 'alignment paused while you transmit'
            : alignment?.reason === 'no correlation'
              ? 'not aligned yet \u2014 no signal both are hearing'
              : 'lining the ears up\u2026'),

    settingsOpen ? settingsModal() : null,

    h('div', { style: css.note },
      'Turn Zeus\u2019s own volume slider down \u2014 not its mute, which would stop '
      + 'the stream this panel reads. The local level is restored here.'),

    h('div', { style: css.note },
      `Swaps ears only for a receiver ${SWITCH_MARGIN_DB} dB better that holds the lead `
      + `${SWITCH_DWELL_MS / 1000}s. Holds ${CONNECTION_BUDGET} remote receivers at a time and rotates the weakest `
      + 'every minute or so. Each one is a session on somebody else\u2019s hardware.')));

  // ---- the panel is one row; everything else is a modal off it ------------
  {
    const live = running;
    const heard = remoteRows.find((r) => r.heard);
    const muted = mutedRight;
    const key = keyed;
    const mine = localSnrAvg.current;

    const dot = !live ? '\u25cb' : muted ? '\u2298' : key ? '\u25d0' : heard ? '\u25b6' : '\u25cb';
    const name = !live ? 'off'
      : heard ? (heard.rx?.callsign || heard.host.split('.')[0])
      : `${remoteRows.length} measured`;
    const delta = heard?.snr != null && mine != null ? heard.snr - mine : null;

    return h('div', { style: { ...css.panel, padding: '6px 10px', gap: 4 } },
      settingsOpen ? settingsModal() : null,
      expanded ? fullView() : null,
      h('div', {
        style: css.strip,
        onClick: () => { if (live) setMutedRight(!mutedRight); },
        title: heard
          ? [heard.rx?.callsign || heard.host,
             [heard.rx?.location,
              heard.rx?.distanceKm != null ? `${Math.round(heard.rx.distanceKm)} km` : null,
              heard.rx?.bearingDegrees != null ? `${Math.round(heard.rx.bearingDegrees)}\u00b0` : null]
               .filter(Boolean).join(' \u00b7 '),
             heard.snr != null ? `S/N ${heard.snr.toFixed(0)} dB` : null,
             mine != null ? `you ${mine.toFixed(0)} dB` : null,
             heard.sync?.quality != null ? `correlation ${heard.sync.quality.toFixed(2)}` : null,
             muted ? 'click to unmute' : 'click to mute',
            ].filter(Boolean).join('\n')
          : 'Remote Ears',
      },
        h('button', {
          style: {
            ...css.cog, fontSize: 13,
            color: live ? 'var(--success, #4fbfa0)' : 'var(--fg-3, #5a5e66)',
          },
          onClick: (e) => { e.stopPropagation(); if (live) stopAll(); else void start(); },
          title: live ? 'stop' : 'start listening',
          'aria-label': live ? 'stop' : 'start listening',
        }, '\u23fb'),

        h('span', { style: { ...css.dot,
                             color: heard && !muted ? 'var(--success, #4fbfa0)'
                                   : key ? 'var(--warn, #d8a657)' : 'var(--fg-3, #5a5e66)' } }, dot),
        h('span', { style: css.stripName }, name),

        // Both traces: the operator heavy, the receiver he is hearing light.
        // A number cannot show a remote climbing towards you; this can.
        h('canvas', { id: 'ubersdr-strip',
                      style: { width: 96, height: 20, flex: 'none', marginLeft: 4 } }),

        h('span', {
          style: { ...css.stripDelta,
                   color: delta == null ? 'var(--fg-3, #5a5e66)'
                         : delta >= 0 ? 'var(--success, #4fbfa0)' : 'var(--fg-2, #a3aab8)' },
        }, delta == null ? '\u2014' : `${delta >= 0 ? '+' : ''}${delta.toFixed(0)} dB`),

        h('button', { style: css.cog,
                      onClick: (e) => { e.stopPropagation(); setExpanded(true); },
                      title: 'open the full view', 'aria-label': 'expand' }, '\u2922'),
        h('button', { style: css.cog, onClick: (e) => { e.stopPropagation(); setSettingsOpen(true); },
                      title: 'settings', 'aria-label': 'settings' }, '\u2699')),

      message ? h('div', { style: { ...css.note, ...(message.bad ? css.bad : {}) } }, message.text) : null);
  }


}

export default function register(api) {
  api.registerPanel({
    id: 'ubersdr.qso',
    component: () => h(QsoAssistPanel, { api }),
  });
  // 'ubersdr.min' existed briefly as a separate panel and may sit in a saved
  // workspace layout. It is no longer offered in plugin.json, but a layout that
  // still references it must render something rather than taking the whole
  // workspace down with "a panel failed to render" — removing a panel id is a
  // breaking change for anyone who had already placed it.
  api.registerPanel({
    id: 'ubersdr.min',
    component: () => h(QsoAssistPanel, { api }),
  });
}
