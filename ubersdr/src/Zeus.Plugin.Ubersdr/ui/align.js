// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// Lining the two ears up in time.
//
// The remote ear is always later than the local one — a remote receiver's DSP,
// the Opus framing, and the trip across the internet add up to somewhere
// between a third of a second and two seconds. Hearing the same speech twice
// that far apart is not a subtle artefact; it is the effect language labs use
// deliberately to disrupt speech.
//
// The obvious fix — cross-correlate the two audio streams and delay the early
// one — does not work, and it is worth saying why, because the failure is
// silent. Two SSB receivers tuned to the same frequency do not produce the same
// audio. Their reference oscillators differ by a few Hz, which is inaudible but
// shifts the whole audio spectrum; their AGC, filters and noise are all
// different. A sample-level correlation between them is noise.
//
// What does survive all of that is the *envelope*: the syllable-rate rise and
// fall of speech, which is the same on every receiver hearing the same
// transmission regardless of how each one demodulates it. So the envelope is
// what gets correlated here — decimated to 100 Hz, which is far above the
// 2-10 Hz of syllables and gives 10 ms resolution, well under what anyone can
// hear as a split.

/** Envelope sample rate. 10 ms resolution, far finer than the ear needs. */
export const ENVELOPE_HZ = 100;

/**
 * A ring of short-term energy, indexed by *playback* time rather than arrival
 * time.
 *
 * <p>That distinction is the whole reliability of this. Indexing by arrival
 * would measure the network skew, which is not the quantity anyone hears —
 * what reaches the ears is separated by the difference in *scheduled* start
 * times, which also carries each channel's jitter buffer, every gap, and every
 * restart. Stamping each block with the AudioContext time it will actually be
 * played at folds all of that in for free, and needs no model of it.
 */
export class EnvelopeTracker {
  constructor(seconds = 10) {
    this.n = Math.round(seconds * ENVELOPE_HZ);
    this.sum = new Float64Array(this.n);
    this.cnt = new Int32Array(this.n);
    this.end = 0;                    // absolute block index, one past the newest
  }

  /** @param startTime AudioContext time this buffer begins playing. */
  push(samples, sampleRate, startTime) {
    const n = this.n;
    const base = startTime * ENVELOPE_HZ;
    const step = ENVELOPE_HZ / sampleRate;
    for (let i = 0; i < samples.length; i++) {
      const idx = Math.floor(base + i * step);
      if (idx >= this.end) {
        // Blocks the stream skipped over are silence, not stale energy from a
        // lap ago. Clearing them is what makes a gap read as a gap.
        for (let k = Math.max(this.end, idx - n + 1); k <= idx; k++) {
          const kk = ((k % n) + n) % n;
          this.sum[kk] = 0; this.cnt[kk] = 0;
        }
        this.end = idx + 1;
      } else if (idx < this.end - n) {
        continue;                    // older than the ring holds
      }
      const j = ((idx % n) + n) % n;
      this.sum[j] += samples[i] * samples[i];
      this.cnt[j] += 1;
    }
  }

  /**
   * Push a single level held for a span, rather than a buffer of samples.
   *
   * <p>This is how a candidate receiver is tracked without decoding it. Every
   * UberSDR audio frame carries its own baseband power in the header, which is
   * an envelope already — sampled at the frame rate, and free. Running an Opus
   * decoder on every candidate just to measure something the header states
   * outright would be wasteful.
   */
  pushLevel(value, startTime, durationSec) {
    const n = this.n;
    const first = Math.floor(startTime * ENVELOPE_HZ);
    const last = Math.max(first, Math.floor((startTime + durationSec) * ENVELOPE_HZ) - 1);
    for (let idx = first; idx <= last; idx++) {
      if (idx >= this.end) {
        for (let k = Math.max(this.end, idx - n + 1); k <= idx; k++) {
          const kk = ((k % n) + n) % n;
          this.sum[kk] = 0; this.cnt[kk] = 0;
        }
        this.end = idx + 1;
      } else if (idx < this.end - n) continue;
      const j = ((idx % n) + n) % n;
      this.sum[j] += value * value;
      this.cnt[j] += 1;
    }
  }

  /**
   * `len` blocks of RMS ending just before absolute block `endIndex`, in
   * chronological order. Both channels are read with the same `endIndex`, so
   * index i means the same instant in both — which is what makes a lag
   * measured between them meaningful.
   */
  series(endIndex, len) {
    const out = new Float32Array(len);
    for (let i = 0; i < len; i++) {
      const idx = endIndex - len + i;
      if (idx < 0 || idx < this.end - this.n || idx >= this.end) continue;
      const j = ((idx % this.n) + this.n) % this.n;
      out[i] = this.cnt[j] ? Math.sqrt(this.sum[j] / this.cnt[j]) : 0;
    }
    return out;
  }
}

/**
 * Normalise, log-compress, mean-remove — in that order.
 *
 * <p>The order is the whole point. Two receivers hearing the same transmission
 * can differ by 40 dB in absolute level, and <code>log1p</code> is very nearly
 * linear for small arguments and logarithmic for large ones. Compressing before
 * normalising therefore sends each side through a *different part of the
 * curve*: the loud one gets genuinely compressed, the quiet one barely at all.
 * The same speech comes out with two different shapes, and the correlation then
 * measures that difference instead of the timing.
 *
 * <p>Measured on the air: a receiver 29 km away on the operator's own frequency
 * — which by construction hears what he hears — scored 0.11. Scaling each
 * series to its own level first puts both through the same compression.
 *
 * <p>The reference is a high percentile rather than the maximum, so one click
 * or spike cannot set the scale for the whole window.
 */
function prepare(v) {
  const n = v.length;
  const sorted = Float32Array.from(v).sort();
  const ref = sorted[Math.min(n - 1, Math.floor(n * 0.95))] || sorted[n - 1] || 0;
  const k = ref > 1e-12 ? 1 / ref : 0;

  const c = new Float32Array(n);
  for (let i = 0; i < n; i++) c[i] = Math.log1p(v[i] * k * 1000);
  const x = bandpassSyllables(c);

  // How much of this envelope is actually syllables. Two streams of noise
  // correlate at r = 0.99 once they are normalised, because normalising a
  // signal that is almost nothing amplifies almost nothing into something —
  // and a correlation coefficient cannot tell you there was nothing there.
  // This can.
  let sx = 0, sc = 0, mc = 0;
  for (let i = 0; i < n; i++) mc += c[i];
  mc /= n || 1;
  for (let i = 0; i < n; i++) { sx += x[i] * x[i]; sc += (c[i] - mc) * (c[i] - mc); }
  const depth = sc > 1e-12 ? Math.sqrt(sx / sc) : 0;
  return { x, depth };
}

// Speech syllables live between about 2 and 10 Hz. Everything slower than that
// is fading and AGC drift; everything faster is measurement noise.
const SLOW_WIN = Math.round(0.5 * ENVELOPE_HZ);   // removes below ~2 Hz
const FAST_WIN = Math.round(0.1 * ENVELOPE_HZ);   // removes above ~10 Hz

/**
 * Centred boxcar moving average via prefix sums, with the window truncated at
 * the edges and divided by however many samples it actually covered.
 *
 * <p>The first attempt kept a running accumulator and divided by
 * <code>min(w, i + 1)</code>, which is wrong everywhere the window is clipped.
 * It left both series with the same deterministic ramp at each end, and two
 * streams of unrelated noise then correlated at r = 1.00 — a confident
 * alignment of nothing at all.
 */
function smooth(v, w) {
  const n = v.length, out = new Float32Array(n);
  if (w <= 1) { out.set(v); return out; }
  const pre = new Float64Array(n + 1);
  for (let i = 0; i < n; i++) pre[i + 1] = pre[i] + v[i];
  const half = w >> 1;
  for (let i = 0; i < n; i++) {
    const a = Math.max(0, i - half);
    const b = Math.min(n, i + half + 1);
    out[i] = (pre[b] - pre[a]) / (b - a);
  }
  return out;
}

/**
 * Keep the syllable band and throw the rest away.
 *
 * <p>Measured on the operator's own receiver: 64% of the envelope's modulation
 * energy sat below 1.5 Hz and only 31% in the syllable band. That slow majority
 * is fading and AGC drift — and fading is path-specific, so it is precisely the
 * component that two receivers 200 km apart *cannot* agree on. Correlating the
 * raw envelope therefore let the one part that carries no shared information
 * dominate the answer.
 *
 * <p>Mean removal falls out of the high-pass, so it is not done separately.
 */
function bandpassSyllables(v) {
  const slow = smooth(v, SLOW_WIN);
  const fast = smooth(v, FAST_WIN);
  const out = new Float32Array(v.length);
  for (let i = 0; i < v.length; i++) out[i] = fast[i] - slow[i];
  return out;
}

/**
 * How far `b` lags `a`, in envelope blocks. Positive means b is later.
 *
 * <p>Returns a `quality` — the normalised correlation at the peak — and a
 * `margin` over the best rival outside the peak's neighbourhood. Both matter:
 * a high correlation with no margin means a periodic signal that lines up at
 * many lags equally well, and acting on it would be a coin toss.
 */
export function estimateLag(a, b, maxLag) {
  const pa = prepare(a), pb = prepare(b);
  const A = pa.x, B = pb.x;
  // The weaker of the two governs: a strong syllable envelope on one side
  // cannot make up for silence on the other.
  const depth = Math.min(pa.depth, pb.depth);
  const n = Math.min(A.length, B.length);
  const scores = new Map();
  let best = { lag: 0, r: -2 };

  for (let k = -maxLag; k <= maxLag; k++) {
    // Overlap of a[i] against b[i+k].
    const i0 = Math.max(0, -k), i1 = Math.min(n, n - k);
    const len = i1 - i0;
    if (len < n / 2) continue;                    // too little overlap to trust
    let sab = 0, saa = 0, sbb = 0;
    for (let i = i0; i < i1; i++) {
      const x = A[i], y = B[i + k];
      sab += x * y; saa += x * x; sbb += y * y;
    }
    const denom = Math.sqrt(saa * sbb);
    const r = denom > 1e-9 ? sab / denom : 0;
    scores.set(k, r);
    if (r > best.r) best = { lag: k, r };
  }

  // The best correlation that is not part of the same peak.
  //
  // This has to be well under one syllable period (~0.25 s) or it swallows the
  // adjacent-cycle peak, and a cycle slip of exactly one syllable then looks
  // like a confident answer. That is not hypothetical: at 0.25 s this returned
  // r=0.75 on a lag that was 240 ms wrong, and passed a different station
  // outright.
  const guard = Math.round(0.08 * ENVELOPE_HZ);
  let rival = -2;
  for (const [k, r] of scores) {
    if (Math.abs(k - best.lag) > guard && r > rival) rival = r;
  }

  return { lag: best.lag, quality: best.r, margin: best.r - Math.max(rival, 0), depth };
}

/**
 * How well a candidate must correlate with your own receiver before it is
 * allowed anywhere near your ear.
 *
 * <p>This is a different question from "is it strong". SNR says a receiver is
 * hearing something loud on your frequency; on a crowded band that is routinely
 * a different station altogether, and promoting it would put a stranger in one
 * ear with nothing to signal that it happened. Correlation against your own
 * audio is the only evidence that it is *your* QSO. Set below MIN_QUALITY
 * because screening runs on the frame headers rather than decoded audio, which
 * is coarser — but it still separates "the same speech" from "some other
 * speech" decisively.
 */
export const SYNC_MIN_QUALITY = 0.20;

/**
 * Minimum syllable content before a correlation means anything at all.
 * Band-passed RMS as a fraction of the whole envelope's RMS — around 0.5 on
 * real speech, near zero on noise or a steady carrier.
 */
export const MIN_DEPTH = 0.15;

/** Below these, the measurement is not worth acting on. */
// Calibrated alongside SYNC_MIN_QUALITY on the same recording. The old 0.45 was
// a guess sitting well above what a true match actually scores (median 0.27),
// so alignment either refused honest measurements or fired on chance.
export const MIN_QUALITY = 0.20;
// Band-passing sharpens the peak but also raises the syllable-rate sidelobes
// beside it, so the honest margin on a correct match is smaller than it was.
// The depth gate now carries the load that an inflated margin used to.
export const MIN_MARGIN = 0.05;

/**
 * Turn a measurement into an instruction, or into nothing.
 *
 * <p>Deliberately conservative. A wrong alignment is worse than none — it adds
 * delay *and* leaves the ears split — and the situations that produce a
 * confident-looking wrong answer are ordinary: both ears on noise between
 * overs, or the remote receiver tuned to a different signal entirely.
 */
export function decideAlignment(est, { current, maxSec, minChangeSec = 0.04 }) {
  if (est.depth != null && est.depth < MIN_DEPTH) {
    return { change: false, reason: 'no speech to lock onto' };
  }
  if (est.quality < MIN_QUALITY || est.margin < MIN_MARGIN) {
    return { change: false, reason: 'no correlation' };
  }
  const sec = est.lag / ENVELOPE_HZ;
  if (Math.abs(sec) > maxSec) return { change: false, reason: 'out of range' };

  // Positive lag means the right ear is later, so the left is the one to hold
  // back. It is almost always this way round; the other case is cheap to keep.
  const side = sec >= 0 ? 'left' : 'right';
  const offset = Math.abs(sec);

  if (current && current.side === side && Math.abs(current.offset - offset) < minChangeSec) {
    return { change: false, reason: 'settled', side, offset: current.offset };
  }
  return { change: true, side, offset, quality: est.quality };
}
