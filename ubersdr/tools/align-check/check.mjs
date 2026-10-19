// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// Measure a known delay between two receivers that do not agree on anything
// else, which is the situation on the air.
//
// The synthetic signal here is built to break the naive approach on purpose:
// the two channels carry the same speech but on carriers 3 Hz apart, at
// different gains, with different noise. If a future change swaps envelope
// correlation for waveform correlation it will pass no test here.

import { pathToFileURL } from 'node:url';
import path from 'node:path';

const ui = path.resolve(process.argv[2] ?? 'src/Zeus.Plugin.Ubersdr/ui/align.js');
const { EnvelopeTracker, estimateLag, decideAlignment, ENVELOPE_HZ } =
  await import(pathToFileURL(ui).href);

let failed = 0;
const check = (name, cond, extra = '') => {
  if (cond) console.log(`ok    ${name}${extra ? '  ' + extra : ''}`);
  else { console.error(`FAIL  ${name}${extra ? '  ' + extra : ''}`); failed++; }
};

const RATE = 48000;
// A deterministic noise source: Math.random would make a failure unreproducible.
let seed = 12345;
const rnd = () => ((seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff) * 2 - 1;

/**
 * A speech-like envelope. Deliberately aperiodic: incommensurate syllable
 * components, so it does not correlate with a shifted copy of itself. An
 * envelope built from a single syllable rate is a much easier target than real
 * speech, and a correlator that only works on it is not proved at all.
 */
function speechEnvelope(t) {
  // Pauses come and go irregularly. A fixed 12-second over/pause cycle is a
  // degenerate signal: its own repetition gives the correlator several equally
  // good answers, which is a property of the test rig and not of speech.
  if (Math.sin(t / 2.09) + 0.6 * Math.sin(t / 1.16 + 1.0) < -1.05) return 0.02;
  // Incommensurate on purpose: 3.3 / 5.1 / 2.3 Hz are all multiples of 0.1 and
  // repeat exactly every 10 s — the length of the correlation window — which
  // makes the match circular and every lag equally good. That degeneracy is a
  // property of the rig, not of speech, and it produced a "failure" that was
  // really the correlator honestly refusing an ambiguous signal.
  const s = Math.sin(2 * Math.PI * 3.371 * t)
          + 0.7 * Math.sin(2 * Math.PI * 5.137 * t + 1.1)
          + 0.5 * Math.sin(2 * Math.PI * 2.293 * t + 2.2);
  const v = Math.max(0, s);
  return 0.05 + 0.7 * v * v / 3;
}

/**
 * One receiver's audio for the same transmission: its own carrier offset, its
 * own gain, its own noise, and its own delay.
 */
function receiver({ seconds, carrierHz, gain, noise, delaySec, envelope = speechEnvelope }) {
  const out = new Float32Array(Math.round(seconds * RATE));
  for (let i = 0; i < out.length; i++) {
    const t = i / RATE;
    const env = envelope(t - delaySec);
    const voice = Math.sin(2 * Math.PI * carrierHz * t)
                + 0.4 * Math.sin(2 * Math.PI * carrierHz * 1.9 * t);
    out[i] = gain * (env * voice * 0.3 + noise * rnd());
  }
  return out;
}

/** Push audio through a tracker the way the panel does — in frames, timestamped. */
function track(samples, startAt, frame = 960) {
  const tr = new EnvelopeTracker(30);
  for (let i = 0; i < samples.length; i += frame) {
    tr.push(samples.subarray(i, i + frame), RATE, startAt + i / RATE);
  }
  return tr;
}

function measure({ delaySec, noiseA = 0.02, noiseB = 0.08, seconds = 20 }) {
  const a = receiver({ seconds, carrierHz: 700, gain: 1.0, noise: noiseA, delaySec: 0 });
  const b = receiver({ seconds, carrierHz: 703, gain: 0.35, noise: noiseB, delaySec });
  const ta = track(a, 0), tb = track(b, 0);
  const end = Math.min(ta.end, tb.end);
  const len = Math.round(8 * ENVELOPE_HZ);
  const est = estimateLag(ta.series(end, len), tb.series(end, len), Math.round(3 * ENVELOPE_HZ));
  return { ...est, sec: est.lag / ENVELOPE_HZ };
}

// --- the case this exists for ----------------------------------------------
for (const delay of [0.35, 0.8, 1.5, 2.4]) {
  const m = measure({ delaySec: delay });
  const err = Math.abs(m.sec - delay);
  check(`a ${delay}s split is measured to within 30 ms across a 3 Hz tuning offset`,
    err <= 0.03, `got ${m.sec.toFixed(2)}s, r=${m.quality.toFixed(2)}`);
}

// --- the naive approach, demonstrated failing -------------------------------
{
  // Correlate the waveforms instead of the envelopes: 3 Hz of tuning offset is
  // enough to destroy it. This is why the module decimates to an envelope.
  const seconds = 12;
  const a = receiver({ seconds, carrierHz: 700, gain: 1, noise: 0.01, delaySec: 0 });
  const b = receiver({ seconds, carrierHz: 703, gain: 1, noise: 0.01, delaySec: 0.8 });
  const off = Math.round(0.8 * RATE);
  let sab = 0, saa = 0, sbb = 0;
  // Integrate over many beat cycles. Over a short window a 3 Hz beat has too
  // few cycles to average away, and the speech envelope weighting leaves a
  // residue — which is itself worth knowing: waveform correlation is not
  // merely weak here, it is unstable, giving a large coefficient of arbitrary
  // sign depending on the window.
  for (let i = 0; i < RATE * 8; i++) {
    sab += a[i] * b[i + off]; saa += a[i] * a[i]; sbb += b[i + off] * b[i + off];
  }
  const r = sab / Math.sqrt(saa * sbb);
  check('waveform correlation is useless at the true lag, even with the lag known',
    Math.abs(r) < 0.2, `r=${r.toFixed(3)}`);
}

// --- refusing to answer -----------------------------------------------------
{
  // Both ears on noise between overs: there is nothing to line up, and a
  // confident-looking answer here would add delay for no reason.
  const n = () => { const v = new Float32Array(RATE * 12); for (let i = 0; i < v.length; i++) v[i] = 0.05 * rnd(); return v; };
  const ta = track(n(), 0), tb = track(n(), 0);
  const end = Math.min(ta.end, tb.end), len = Math.round(8 * ENVELOPE_HZ);
  const est = estimateLag(ta.series(end, len), tb.series(end, len), Math.round(3 * ENVELOPE_HZ));
  const d = decideAlignment(est, { current: null, maxSec: 3 });
  check('two ears on unrelated noise produce no alignment at all',
    !d.change, `r=${est.quality.toFixed(2)} margin=${est.margin.toFixed(2)} depth=${est.depth.toFixed(2)} (${d.reason})`);
}

// --- not chasing the last 10 ms ---------------------------------------------
{
  const est = { lag: Math.round(0.82 * ENVELOPE_HZ), quality: 0.8, margin: 0.4 };
  const d = decideAlignment(est, { current: { side: 'left', offset: 0.80 }, maxSec: 3 });
  check('a 20 ms wobble around a settled alignment does not re-cut the audio', !d.change);
  const d2 = decideAlignment({ lag: Math.round(1.4 * ENVELOPE_HZ), quality: 0.8, margin: 0.4 },
    { current: { side: 'left', offset: 0.80 }, maxSec: 3 });
  check('a real move does get applied', d2.change && Math.abs(d2.offset - 1.4) < 0.02);
}

// --- which ear to hold back -------------------------------------------------
{
  const late = decideAlignment({ lag: 80, quality: 0.9, margin: 0.5 }, { current: null, maxSec: 3 });
  const early = decideAlignment({ lag: -80, quality: 0.9, margin: 0.5 }, { current: null, maxSec: 3 });
  check('the earlier ear is the one delayed, whichever it is',
    late.side === 'left' && early.side === 'right');
}

// --- beyond what we will buffer ---------------------------------------------
{
  const d = decideAlignment({ lag: Math.round(5 * ENVELOPE_HZ), quality: 0.9, margin: 0.5 },
    { current: null, maxSec: 3 });
  check('a lag past the buffer limit is declined rather than clamped', !d.change);
}

if (failed) { console.error(`\n${failed} failed`); process.exit(1); }
console.log('\nchannel alignment holds');

// --- restoring the local ear after Zeus's own volume ------------------------
{
  const agc = await import(pathToFileURL(path.join(path.dirname(ui), 'agc.js')).href);

  // Zeus's slider at minimum, measured on a running engine: 0.002 where the
  // stream otherwise carries 0.7.
  let g = 1;
  for (let i = 0; i < 500; i++) g = agc.nextGain(g, 0.002, 0.02);
  const restored = 0.002 * g;
  check('ten seconds at Zeus\'s minimum brings the local ear back to a usable level',
    restored > 0.3 && restored <= agc.TARGET_PEAK + 0.01, `peak ${restored.toFixed(3)}`);

  // The dangerous moment: the slider goes back up while the gain is still high.
  const loud = new Float32Array(256).fill(0.7);
  const worst = agc.peakOf(agc.applyGain(loud, g));
  check('the slider returning before the gain does cannot exceed the limit',
    worst <= agc.LIMIT + 1e-6, `worst ${worst.toFixed(3)}`);

  // And it must come down fast, not over the release time.
  let down = g;
  for (let i = 0; i < 10; i++) down = agc.nextGain(down, 0.7, 0.02);
  check('gain falls within 200 ms when the signal jumps', down < g / 10,
    `${g.toFixed(0)} -> ${down.toFixed(0)}`);

  // Silence must not run the gain up, or the next syllable arrives as a bang.
  let quiet = 50;
  for (let i = 0; i < 500; i++) quiet = agc.nextGain(quiet, 0, 0.02);
  check('silence holds the gain instead of chasing it', quiet === 50);

  // Never below unity: this restores level, it does not become a volume control.
  check('a full-scale stream is left alone', agc.nextGain(1, 0.9, 0.02) === 1);
}

if (failed) { console.error(`\n${failed} failed`); process.exit(1); }
console.log('level restoration holds');

// --- screening a candidate before it is allowed into the ear ----------------
{
  // The question is not "is this receiver strong" but "is this receiver hearing
  // *my* QSO". On a busy band two receivers on one frequency routinely carry
  // different stations, and promoting on signal level alone would put a
  // stranger in one ear with nothing to show it had happened.
  //
  // A candidate is screened from its frame headers: every UberSDR frame states
  // its own baseband power, which is the power of the received signal — the
  // same quantity the RMS envelope of its audio carries, and free to read.
  const { SYNC_MIN_QUALITY } = await import(pathToFileURL(ui).href);

  // The window the panel actually screens on. Testing at 10 s proved nothing:
  // the null distribution there is indistinguishable from a true match, so a
  // synthetic impostor scoring 0.25 was neither a pass nor a failure — it was
  // the operating point being wrong.
  const SCREEN_WIN_S = 45;
  const win = Math.round(SCREEN_WIN_S * ENVELOPE_HZ);
  const score = (a, b) => {
    const end = Math.min(a.end, b.end);
    return estimateLag(a.series(end, win), b.series(end, win), Math.round(3 * ENVELOPE_HZ));
  };
  // The live rule is quality AND margin AND depth. Depth is deliberately left
  // out here: it gates on *signal presence*, and this rig's envelope is built
  // from three rectified sines whose difference frequencies land at 1.1 and
  // 1.8 Hz — below the syllable band by construction — so it scores about 0.09
  // where real speech measures 0.19-0.22. Bending the generator until it hit
  // 0.15 would be fitting the test rig to the answer. Depth is covered by the
  // noise case above, on the property it actually describes.
  const passes = (r) => r.quality >= SYNC_MIN_QUALITY && r.margin >= 0.05;

  const seconds = 60;
  const mine = track(receiver({ seconds, carrierHz: 700, gain: 1, noise: 0.02, delaySec: 0 }), 0);

  // Same QSO, a second late, on a receiver that agrees with mine about nothing
  // else: different carrier, a third of the gain, four times the noise.
  const sameQso = track(
    receiver({ seconds, carrierHz: 703, gain: 0.3, noise: 0.08, delaySec: 1.0 }), 0);
  const rSame = score(mine, sameQso);
  check('a candidate carrying my QSO is confirmed, and its delay measured',
    passes(rSame) && Math.abs(rSame.lag / ENVELOPE_HZ - 1.0) < 0.05,
    `r=${rSame.quality.toFixed(2)} margin=${rSame.margin.toFixed(2)} depth=${rSame.depth.toFixed(2)} lag=${(rSame.lag / ENVELOPE_HZ).toFixed(2)}s`);

  // A different, unrelated station on the same frequency — strong, clean, and
  // completely wrong. This is the one that must never be promoted.
  const otherQso = track(receiver({
    seconds, carrierHz: 701, gain: 1.2, noise: 0.02, delaySec: 0,
    envelope: (t) => {
      const u = Math.sin(2 * Math.PI * 4.717 * t + 0.4)
              + 0.7 * Math.sin(2 * Math.PI * 2.939 * t + 2.6)
              + 0.5 * Math.sin(2 * Math.PI * 6.311 * t + 1.3);
      if (Math.sin(t / 1.71 + 0.7) + 0.6 * Math.sin(t / 2.63 + 2.1) < -1.05) return 0.02;
      const v = Math.max(0, u);
      return 0.05 + 0.7 * v * v / 3;
    },
  }), 0);
  const rOther = score(mine, otherQso);
  check('the rig is less speech-like than the air, and says so rather than pretending',
    rSame.depth < 0.15,
    `rig depth ${rSame.depth.toFixed(2)} vs 0.19-0.22 measured on a real QSO`);

  check('a different station on the same frequency is refused, however strong',
    !passes(rOther),
    `r=${rOther.quality.toFixed(2)} margin=${rOther.margin.toFixed(2)} depth=${rOther.depth.toFixed(2)}`);

  // And the header path must agree with the audio path, or screening measures
  // something other than what alignment then acts on.
  const viaHeaders = new EnvelopeTracker(40);
  const audio = receiver({ seconds: 12, carrierHz: 700, gain: 1, noise: 0.02, delaySec: 0 });
  const blk = Math.round(RATE * 0.02);
  for (let i = 0; i + blk <= audio.length; i += blk) {
    let sum = 0;
    for (let j = i; j < i + blk; j++) sum += audio[j] * audio[j];
    viaHeaders.pushLevel(Math.sqrt(sum / blk), i / RATE, 0.02);   // what a header reports
  }
  const viaAudio = track(audio, 0);
  const agree = score(viaAudio, viaHeaders);
  // Lag is the quantity screening hands to alignment, and the two paths agree
  // on it exactly. The correlation is only ~0.5 rather than ~1, and that is a
  // real finding rather than a rounding artefact: prepare() applies
  // log1p(v * 1000), which *expands* near-silence, so the two paths disagree
  // loudly about the pauses where there is nothing to agree about. It is the
  // same weakness that makes weak signals correlate badly — see HANDOFF.md.
  check('a header-derived envelope agrees with the audio-derived one on lag',
    Math.abs(agree.lag) <= 2,
    `lag=${agree.lag} blocks, r=${agree.quality.toFixed(2)} (log expansion of silence)`);
}

if (failed) { console.error(`\n${failed} failed`); process.exit(1); }
console.log('candidate screening holds');
