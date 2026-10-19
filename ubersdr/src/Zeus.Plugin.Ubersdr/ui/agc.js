// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// Making the local ear independent of Zeus's own volume control.
//
// Measured on a running engine: Zeus's volume slider attenuates the WebSocket
// audio lane itself, upstream of the broadcast — at its minimum the stream
// carries about 0.002 peak where it otherwise carries 0.7, a factor of ~350.
// It does not stop the stream, and it does not quantise it: the samples are
// float32, so 0.002 still has the full mantissa and scaling it back up is
// lossless in any sense that matters to a headphone.
//
// That is what makes the arrangement the operator asked for possible at all.
// Zeus's own player goes silent at the bottom of its slider; this panel takes
// the same lane, restores the level itself, and puts it in one ear. Neither
// the engine's mute (which stops the lane) nor its AF gain (which does not
// touch it) can do that.
//
// Two rules keep it safe, because this ends up in someone's headphones:
// gain comes down immediately and goes up slowly, and the output is limited
// whatever the gain says.

export const TARGET_PEAK = 0.5;
export const MAX_GAIN = 600;      // ~55 dB, a little past Zeus's slider range
export const ATTACK_TAU_S = 0.05; // getting quieter: fast, before it is heard
export const RELEASE_TAU_S = 3.0; // getting louder: slow, so it does not pump
export const LIMIT = 0.95;
const FLOOR = 1e-6;               // below this there is nothing to measure

/**
 * The gain to use for the next buffer.
 *
 * <p>Asymmetric on purpose. A signal that suddenly gets louder must be reined
 * in within a few milliseconds or the operator hears the overshoot; a signal
 * that gets quieter can be brought up over seconds, and should be, or every
 * pause between words turns into a swell of noise.
 *
 * <p>Silence holds the gain rather than chasing it. Between overs there is
 * nothing to normalise, and a gain that ran up to its maximum during the
 * silence would make the next syllable arrive as a bang.
 */
export function nextGain(prev, peak, dtSec) {
  if (!(peak > FLOOR)) return prev;                 // nothing to measure
  const want = Math.min(MAX_GAIN, Math.max(1, TARGET_PEAK / peak));
  const tau = want < prev ? ATTACK_TAU_S : RELEASE_TAU_S;
  const k = 1 - Math.exp(-Math.max(dtSec, 0) / tau);
  return prev + k * (want - prev);
}

/**
 * Apply it, and never let the result leave the headphone-safe range whatever
 * the gain happens to be. The soft knee above 70% of the limit keeps a loud
 * passage from turning into flat-topped distortion, and the hard clamp behind
 * it means no arithmetic error anywhere upstream can produce a bang.
 */
export function applyGain(samples, gain) {
  const out = new Float32Array(samples.length);
  const knee = LIMIT * 0.7;
  for (let i = 0; i < samples.length; i++) {
    let v = samples[i] * gain;
    const a = Math.abs(v);
    if (a > knee) {
      const over = (a - knee) / (LIMIT - knee);
      const shaped = knee + (LIMIT - knee) * Math.tanh(over);
      v = Math.sign(v) * shaped;
    }
    out[i] = Math.max(-LIMIT, Math.min(LIMIT, v));
  }
  return out;
}

/** Peak of a buffer. */
export function peakOf(samples) {
  let p = 0;
  for (let i = 0; i < samples.length; i++) { const a = Math.abs(samples[i]); if (a > p) p = a; }
  return p;
}
