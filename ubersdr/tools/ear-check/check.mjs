// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// Drive the right-ear selection rule through conditions that would take an
// afternoon on the air, and check it does not flap.
//
// The rule is about time — a margin that must be beaten, a dwell that must be
// served, a floor under how often the ear may change hands. A test that greps
// for `SWITCH_MARGIN_DB = 3` proves the constant is spelled correctly and
// nothing else. This runs it.

import { pathToFileURL } from 'node:url';
import { readFile, writeFile, unlink } from 'node:fs/promises';
import path from 'node:path';

const ui = path.resolve(process.argv[2] ?? 'src/Zeus.Plugin.Ubersdr/ui/qso-assist.js');
const dir = path.dirname(ui);

// Same trick as panel-check: React and the vendored UMD bundles are not
// resolvable here, so a shimmed copy is written beside the original, where its
// relative imports still resolve the way they will in the browser.
let code = await readFile(ui, 'utf8');
code = code.replace(/^import React[^\n]*\n/m, '');
code = code.replace(/^import '\.\/vendor\/[^\n]*\n/gm, '');
const shim = `const React = { createElement: () => null };
const useState = () => [], useEffect = () => {}, useRef = () => ({ current: null }),
      useCallback = (f) => f;
`;
const tmp = path.join(dir, `.ear-check-${process.pid}.mjs`);
await writeFile(tmp, shim + code, 'utf8');
let mod;
try { mod = await import(pathToFileURL(tmp).href); } finally { await unlink(tmp).catch(() => {}); }

const { chooseEar, SWITCH_MARGIN_DB, SWITCH_DWELL_MS, MIN_HOLD_MS } = mod;

let failed = 0;
const check = (name, cond) => {
  if (cond) console.log(`ok    ${name}`);
  else { console.error(`FAIL  ${name}`); failed++; }
};

const rx = (host, snr) => ({ host, snr, rx: { callsign: host.toUpperCase() } });

/** Run a scenario minute, returning every host that ever held the ear. */
function run(steps, { start = null } = {}) {
  let st = { held: start, heldSince: 0, challenger: null };
  const seen = [];
  let now = 0;
  for (const ranked of steps) {
    now += 1000;                                   // the tick is one second
    const next = chooseEar({ ranked, ...st, now });
    st = { held: next.held, heldSince: next.heldSince, challenger: next.challenger };
    if (seen[seen.length - 1] !== st.held) seen.push(st.held);
  }
  return { held: st.held, switches: seen.length - 1, seen };
}

// --- the first choice is immediate -----------------------------------------
{
  const r = run([[rx('a.example', 10), rx('b.example', 4)]]);
  check('an empty ear is filled at once, without serving any dwell',
    r.held === 'a.example');
}

// --- the flap this exists to prevent ---------------------------------------
{
  // Two receivers a dB apart, swapping the lead every second — exactly what
  // SSB speech does to a power measurement.
  const steps = [];
  for (let i = 0; i < 60; i++) {
    steps.push(i % 2
      ? [rx('a.example', 11), rx('b.example', 10)]
      : [rx('b.example', 11), rx('a.example', 10)]);
  }
  const r = run(steps);
  check('a 1 dB lead trading hands every second never moves the ear after the first',
    r.switches === 0);
}

// --- a real improvement does win, but only after serving its dwell ----------
{
  const steps = [[rx('a.example', 10)]];                       // a takes the ear
  for (let i = 0; i < 40; i++) steps.push([rx('b.example', 20), rx('a.example', 10)]);
  const r = run(steps);
  check('a decisively better receiver does take the ear', r.held === 'b.example');

  // Find the tick it happened on. It cannot be before the minimum hold, and it
  // cannot be before the dwell has been served on top of that.
  let st = { held: null, heldSince: 0, challenger: null }, at = 0, now = 0;
  for (const ranked of steps) {
    now += 1000;
    const n = chooseEar({ ranked, ...st, now });
    if (n.held !== st.held && st.held) at = now;
    st = { held: n.held, heldSince: n.heldSince, challenger: n.challenger };
    if (at) break;
  }
  check(`the switch waits out the hold and the dwell (at ${at}ms, floor ${MIN_HOLD_MS + SWITCH_DWELL_MS}ms)`,
    at >= MIN_HOLD_MS + SWITCH_DWELL_MS);
}

// --- a lead that does not hold wins nothing --------------------------------
{
  const steps = [[rx('a.example', 10)]];
  for (let i = 0; i < 20; i++) steps.push([rx('a.example', 10)]);   // clear the hold
  for (let i = 0; i < 3; i++) steps.push([rx('b.example', 30), rx('a.example', 10)]);
  for (let i = 0; i < 20; i++) steps.push([rx('a.example', 10), rx('b.example', 5)]);
  const r = run(steps);
  check('a burst of signal that fades before the dwell is up keeps the ear where it was',
    r.held === 'a.example' && r.switches === 0);
}

// --- exactly at the margin --------------------------------------------------
{
  const steps = [[rx('a.example', 10)]];
  for (let i = 0; i < 60; i++) {
    steps.push([rx('b.example', 10 + SWITCH_MARGIN_DB - 0.1), rx('a.example', 10)]);
  }
  check('a challenger a hair under the margin never switches, however long it holds',
    run(steps).held === 'a.example');
}

// --- a dead source is replaced without ceremony -----------------------------
{
  const steps = [[rx('a.example', 10), rx('b.example', 2)]];
  // a goes silent: its measurement is null, which is what a retune or a dropped
  // socket leaves behind.
  for (let i = 0; i < 3; i++) steps.push([rx('b.example', 2), { host: 'a.example', snr: null }]);
  const r = run(steps);
  check('when the held receiver falls silent the ear moves at once, dwell or no dwell',
    r.held === 'b.example');
}

// --- the operator is told before it happens ---------------------------------
{
  const ranked = [rx('b.example', 30), rx('a.example', 10)];
  const n = chooseEar({ ranked, held: 'a.example', heldSince: 0,
                        challenger: { host: 'b.example', since: 20_000 }, now: 23_000 });
  check('a pending switch is announced with who, how much better, and how long left',
    n.chasing?.host === 'b.example' && n.chasing.gain === 20
    && n.chasing.left === Math.ceil((SWITCH_DWELL_MS - 3000) / 1000));
}

if (failed) { console.error(`\n${failed} failed`); process.exit(1); }
console.log('\near selection holds');
