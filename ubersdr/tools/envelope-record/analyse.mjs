// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
//   node tools/envelope-record/analyse.mjs FILE.json
//
// Replay a recording through the real screening code. Change align.js, run this
// again, and see what it did to actual signals rather than to a synthetic
// envelope built from the same assumptions as the algorithm.

import { pathToFileURL } from 'node:url';
import { readFileSync } from 'node:fs';
import path from 'node:path';

const file = process.argv[2];
if (!file) { console.error('usage: analyse.mjs FILE.json'); process.exit(1); }
const rec = JSON.parse(readFileSync(file, 'utf8'));

const ui = path.resolve(path.dirname(new URL(import.meta.url).pathname),
                        '../../src/Zeus.Plugin.Ubersdr/ui/align.js');
const A = await import(pathToFileURL(ui).href);

console.log(`${rec.recordedUtc}  ${(rec.radio.vfoHz / 1e6).toFixed(4)} MHz ${rec.radio.mode}  ${rec.seconds}s\n`);

// --- do the two S/N columns measure the same thing? ------------------------
// This was asserted as "the honest common axis" before anyone checked whether
// the two sides even respond on the same timescale.
const stat = (xs) => {
  if (!xs.length) return null;
  const s = [...xs].sort((a, b) => a - b);
  return { min: s[0], p50: s[s.length >> 1], max: s[s.length - 1],
           swing: s[s.length - 1] - s[0] };
};
const lq = stat(rec.local.quality.filter((q) => q.conf > 0).map((q) => q.snr));
console.log('S/N over the recording:');
if (lq) console.log(`  local          ${lq.min.toFixed(0)} .. ${lq.max.toFixed(0)} dB  (median ${lq.p50.toFixed(0)}, swing ${lq.swing.toFixed(0)})`);
for (const r of rec.remotes) {
  // ka9q's noise is a density in dBm/Hz; the bandwidth term has to come off
  // before this figure can sit beside the engine's integrated SNR.
  const BW = 10 * Math.log10(2700);
  const s = stat(r.frames.filter((f) => Number.isFinite(f.baseband) && Number.isFinite(f.noise))
                         .map((f) => f.baseband - f.noise - BW));
  if (s) console.log(`  ${(r.callsign || r.host).padEnd(14)} ${s.min.toFixed(0)} .. ${s.max.toFixed(0)} dB  (median ${s.p50.toFixed(0)}, swing ${s.swing.toFixed(0)})`);
}
console.log('  remote figures carry the bandwidth correction (-34.3 dB); without it');
console.log('  they read as an SNR they do not have.\n');

// --- rebuild both envelopes on one grid and screen them --------------------
const HZ = A.ENVELOPE_HZ;
const localTr = new A.EnvelopeTracker(rec.seconds + 10);
rec.local.env.forEach((v, i) => localTr.pushLevel(v, i / rec.local.envHz, 1 / rec.local.envHz));

console.log('screening, over the whole recording:');
for (const r of rec.remotes) {
  const tr = new A.EnvelopeTracker(rec.seconds + 10);
  for (const f of r.frames) {
    if (!Number.isFinite(f.baseband)) continue;
    tr.pushLevel(Math.pow(10, f.baseband / 20), f.t, 0.02);
  }
  const end = Math.min(localTr.end, tr.end);
  const win = Math.min(end, Math.round(Math.min(20, rec.seconds - 1) * HZ));
  const est = A.estimateLag(localTr.series(end, win), tr.series(end, win), Math.round(3 * HZ));
  const ok = est.depth >= A.MIN_DEPTH && est.quality >= A.SYNC_MIN_QUALITY && est.margin >= 0.06;
  console.log(`  ${(r.callsign || r.host).padEnd(14)} r=${est.quality.toFixed(2)}  `
    + `margin=${est.margin.toFixed(2)}  depth=${est.depth.toFixed(2)}  `
    + `lag=${(est.lag / HZ).toFixed(2)}s  ${ok ? 'CONFIRMED' : 'refused'}`);
}

// --- where is the local envelope's energy? ---------------------------------
const band = (sig, lo, hi) => {
  const N = 1 << Math.floor(Math.log2(sig.length));
  const x = sig.slice(-N);
  const m = x.reduce((a, b) => a + b, 0) / N;
  let p = 0;
  for (let k = Math.max(1, Math.ceil(lo * N / HZ)); k <= Math.floor(hi * N / HZ); k++) {
    let re = 0, im = 0;
    for (let i = 0; i < N; i++) {
      const w = 2 * Math.PI * k * i / N;
      re += (x[i] - m) * Math.cos(w); im -= (x[i] - m) * Math.sin(w);
    }
    p += re * re + im * im;
  }
  return p;
};
const env = rec.local.env;
const slow = band(env, 0.2, 1.5), syl = band(env, 2, 10), fast = band(env, 15, 40);
const tot = slow + syl + fast;
console.log('\nlocal envelope modulation:');
console.log(`  0.2-1.5 Hz  ${(100 * slow / tot).toFixed(1)}%   fading and AGC — path-specific, cannot correlate`);
console.log(`  2-10 Hz     ${(100 * syl / tot).toFixed(1)}%   syllables — the only part two receivers can share`);
console.log(`  15-40 Hz    ${(100 * fast / tot).toFixed(1)}%   measurement noise`);
