// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
//   node tools/envelope-record/calibrate.mjs FILE.json [more.json ...]
//
// Where should the screening thresholds actually sit?
//
// Positives come from pairing each receiver with the operator's own audio at the
// same instant. Negatives come from pairing them at instants far enough apart
// that a correct match is impossible — same receivers, same band, same noise,
// same everything except simultaneity. That yields a null distribution from real
// signals rather than from a synthetic envelope built on the same assumptions as
// the algorithm it is meant to test.

import { pathToFileURL } from 'node:url';
import { readFileSync } from 'node:fs';
import path from 'node:path';

const winArg = process.argv.indexOf('--window');
const WIN_OVERRIDE = winArg >= 0 ? Number(process.argv[winArg + 1]) : null;
const files = process.argv.slice(2).filter((a, i, all) =>
  a !== '--window' && all[i - 1] !== '--window');
if (!files.length) { console.error('usage: calibrate.mjs FILE.json [...]'); process.exit(1); }
const A = await import(pathToFileURL(path.resolve(
  path.dirname(new URL(import.meta.url).pathname),
  '../../src/Zeus.Plugin.Ubersdr/ui/align.js')).href);

const HZ = A.ENVELOPE_HZ;
const WIN_S = WIN_OVERRIDE ?? 10;
const STEP_S = 2, MAXLAG_S = 3;

// Negatives by time-reversing the candidate's envelope. Speech reversed has the
// same level, the same fading, the same syllable statistics — and cannot match.
// Shifting forward in time also works but costs recording length, which caps
// how long a window can be tested; reversal costs nothing, so the window can be
// swept as far as the recording allows.
const reverse = (v) => { const o = new Float32Array(v.length);
  for (let i = 0; i < v.length; i++) o[i] = v[v.length - 1 - i]; return o; };

const pos = [], neg = [];
for (const f of files) {
  const rec = JSON.parse(readFileSync(f, 'utf8'));
  const localAt = (t0) => {
    const tr = new A.EnvelopeTracker(WIN_S + 4);
    const i0 = Math.round(t0 * rec.local.envHz);
    for (let i = 0; i < WIN_S * rec.local.envHz; i++) {
      const v = rec.local.env[i0 + i];
      if (v == null) return null;
      tr.pushLevel(v, i / rec.local.envHz, 1 / rec.local.envHz);
    }
    return tr;
  };
  const remoteAt = (rx, t0) => {
    const tr = new A.EnvelopeTracker(WIN_S + 4);
    let n = 0;
    for (const fr of rx.frames) {
      if (fr.t < t0 || fr.t >= t0 + WIN_S || !Number.isFinite(fr.baseband)) continue;
      tr.pushLevel(Math.pow(10, fr.baseband / 20), fr.t - t0, 0.02); n++;
    }
    return n > WIN_S * 20 ? tr : null;
  };

  for (let t = 0; t + WIN_S < rec.seconds; t += STEP_S) {
    const L = localAt(t);
    if (!L) continue;
    for (const rx of rec.remotes) {
      const same = remoteAt(rx, t);
      const win = WIN_S * HZ;
      const grab = (tr) => tr && tr.series(tr.end, Math.min(win, tr.end));
      const a = grab(L);
      if (same) {
        const b = grab(same);
        if (a && b && a.length === b.length) pos.push(A.estimateLag(a, b, MAXLAG_S * HZ));
      }
      if (same) {
        const b = grab(same);
        if (a && b && a.length === b.length) neg.push(A.estimateLag(a, reverse(b), MAXLAG_S * HZ));
      }
    }
  }
}

console.log(`window ${WIN_S}s: ${pos.length} simultaneous, ${neg.length} reversed (impossible) pairs\n`);

const pct = (xs, p) => { const s = [...xs].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.floor(s.length * p))]; };
const show = (name, get) => {
  const P = pos.map(get), N = neg.map(get);
  console.log(`${name.padEnd(9)} same-time  p10 ${pct(P, .1).toFixed(2)}  median ${pct(P, .5).toFixed(2)}  p90 ${pct(P, .9).toFixed(2)}`);
  console.log(`${''.padEnd(9)} reversed   p10 ${pct(N, .1).toFixed(2)}  median ${pct(N, .5).toFixed(2)}  p90 ${pct(N, .9).toFixed(2)}`);
};
show('quality', (e) => e.quality);
show('margin', (e) => e.margin);
show('depth', (e) => e.depth);

// How well does a given rule separate the two piles?
const rule = (q, m, d) => (e) => e.quality >= q && e.margin >= m && e.depth >= d;
const score = (r) => {
  const tp = pos.filter(r).length / (pos.length || 1);
  const fp = neg.filter(r).length / (neg.length || 1);
  return { tp, fp };
};
console.log('\nthresholds            confirmed   false alarms');
const cur = [A.SYNC_MIN_QUALITY, 0.06, A.MIN_DEPTH];
for (const [q, m, d] of [
  cur,
  [0.16, 0.02, 0.15], [0.18, 0.02, 0.15], [0.20, 0.03, 0.15],
  [0.22, 0.03, 0.15], [0.24, 0.04, 0.15], [0.26, 0.04, 0.15],
  [0.28, 0.05, 0.15], [0.30, 0.05, 0.15],
  [0.22, 0.05, 0.15], [0.20, 0.05, 0.15], [0.18, 0.06, 0.15], [0.22, 0.06, 0.15],
]) {
  const { tp, fp } = score(rule(q, m, d));
  const tag = (q === cur[0] && m === cur[1] && d === cur[2]) ? '  <- in use' : '';
  console.log(`  q>=${q.toFixed(2)} m>=${m.toFixed(2)} d>=${d.toFixed(2)}    `
    + `${(100 * tp).toFixed(0).padStart(4)}%      ${(100 * fp).toFixed(0).padStart(4)}%${tag}`);
}
console.log('\na false alarm is a receiver put in the operator\'s ear that is not');
console.log('carrying his QSO — the failure this screening exists to prevent.');
