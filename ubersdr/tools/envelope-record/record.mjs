// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// Record what the screener sees, so the screener can be worked on offline.
//
//   node tools/envelope-record/record.mjs [--seconds 60] [--out FILE]
//
// Captures, on one clock:
//   - the local audio envelope at 100 Hz, exactly as the panel builds it
//   - the engine's own RxMetersV2 (0x19) and RxSignalQuality (0x3F) tracks
//   - every candidate receiver's per-frame baseband and noise power
//
// Why this exists: every conclusion drawn from a single reading in this project
// has been wrong at least once — a filter left wide open looked like a broken
// correlator, a pause between overs looked like a deaf receiver. A minute of
// both sides on one timebase can be replayed against any change to the
// algorithm, as often as needed, without occupying the operator or anyone
// else's receiver a second time.
//
// Courtesy: admission goes through the plugin's own /connect, so instance
// client limits are respected exactly as in normal use, and every socket is
// closed on the way out.

import { execSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';

const PLUGIN_ID = 'be.on8st.zeus.plugins.ka9q.ubersdr.remoteears';
const arg = (name, dflt) => {
  const i = process.argv.indexOf(name);
  return i >= 0 && process.argv[i + 1] ? process.argv[i + 1] : dflt;
};
const SECONDS = Number(arg('--seconds', '60'));
const OUT = arg('--out', `envelope-${SECONDS}s.json`);
const WANT = Number(arg('--receivers', '3'));
const ENVELOPE_HZ = 100;

const port = execSync("ps aux | grep -o 'StationEngine --port [0-9]*' | head -1 | awk '{print $3}'")
  .toString().trim();
if (!port) { console.error('engine not running'); process.exit(1); }
const plug = (p, init) => fetch(`http://127.0.0.1:${port}/api/plugins/${PLUGIN_ID}/${p}`, init);
const post = (p, body) => plug(p, { method: 'POST', headers: { 'content-type': 'application/json' },
                                    body: JSON.stringify(body) });

const radio = await (await plug('radio')).json();
console.log(`radio: ${(radio.vfoHz / 1e6).toFixed(4)} MHz ${radio.mode}`);
if (!radio.vfoHz) { console.error('no frequency — is a radio connected?'); process.exit(1); }

// One clock for everything. Both sides are stamped on arrival, which is what
// the panel does too, so a lag measured here is the lag the panel would see.
const t0 = performance.now();
const now = () => (performance.now() - t0) / 1000;

// ---- local side -----------------------------------------------------------
const local = { env: [], envHz: ENVELOPE_HZ, meters: [], quality: [] };
let carry = [], carryN = 0, rate = 48000;

const eng = new WebSocket(`ws://127.0.0.1:${port}/ws`);
eng.binaryType = 'arraybuffer';
await new Promise((r, j) => { eng.onopen = r; eng.onerror = j; });
eng.onmessage = (e) => {
  if (typeof e.data === 'string') return;
  const v = new DataView(e.data);
  const type = v.getUint8(0);

  if (type === 0x19 && e.data.byteLength >= 33) {
    local.meters.push({ t: +now().toFixed(3), signalAv: v.getFloat32(5, true),
                        agcGain: v.getFloat32(17, true), maxBin: v.getFloat32(29, true) });
    return;
  }
  if (type === 0x3f && e.data.byteLength >= 18) {
    local.quality.push({ t: +now().toFixed(3), snr: v.getFloat32(2, true),
                         sig: v.getFloat32(6, true), noise: v.getFloat32(10, true),
                         conf: v.getFloat32(14, true) });
    return;
  }
  if (type !== 0x02) return;

  rate = v.getUint32(18, true);
  const ch = v.getUint8(17), n = v.getUint16(22, true);
  const per = Math.round(rate / ENVELOPE_HZ);
  // Frames do not divide evenly into envelope blocks, so the remainder is
  // carried into the next frame rather than dropped — dropping it would make
  // the envelope run slow and silently mis-time every lag measured from it.
  for (let i = 0; i < n; i++) {
    const x = v.getFloat32(24 + i * ch * 4, true);
    carry.push(x * x); carryN++;
    if (carryN === per) {
      let s = 0; for (const y of carry) s += y;
      local.env.push(+Math.sqrt(s / per).toFixed(6));
      carry = []; carryN = 0;
    }
  }
};
const req = new Uint8Array(17);
req[0] = 0x21; new DataView(req.buffer).setUint16(2, 1, true); req[16] = 1;
eng.send(req);

// ---- candidates -----------------------------------------------------------
let cfg = {};
try { cfg = await (await plug('config')).json(); } catch { /* first run */ }
const ex = (cfg?.excludeHosts ?? []).join(',');
const dir = await (await plug(`receivers?count=8${ex ? `&exclude=${encodeURIComponent(ex)}` : ''}`)).json();
const picks = (dir.suggested ?? []).slice(0, WANT);
console.log(`receivers: ${picks.map((r) => r.callsign || r.host).join(', ')}`);

const remotes = [];
for (const rx of picks) {
  let admit;
  try {
    const res = await post('connect', { host: rx.host });
    admit = await res.json();
    if (!res.ok) { console.log(`  ${rx.host}: refused (${admit.error ?? res.status})`); continue; }
  } catch (e) { console.log(`  ${rx.host}: ${e}`); continue; }

  const mode = (radio.mode ?? 'usb').toLowerCase();
  const url = `${admit.wsBase}/ws?frequency=${radio.vfoHz}&mode=${encodeURIComponent(mode)}`
    + `&user_session_id=${encodeURIComponent(admit.sessionId)}&format=opus&version=${admit.version}`;
  const rec = { host: rx.host, callsign: rx.callsign, location: rx.location,
                distanceKm: rx.distanceKm, frames: [] };
  const ws = new WebSocket(url);
  ws.binaryType = 'arraybuffer';
  ws.onmessage = (e) => {
    if (typeof e.data === 'string' || e.data.byteLength < 21) return;
    const v = new DataView(e.data);
    // 21-byte header: timestamp u64, sampleRate u32, channels u8, then the two
    // powers as f32. The Opus payload is not decoded — the headers alone carry
    // the envelope, which is the whole point of screening this way.
    rec.frames.push({ t: +now().toFixed(3),
                      baseband: v.getFloat32(13, true), noise: v.getFloat32(17, true) });
  };
  ws.onerror = () => {};
  remotes.push({ rec, ws });
}
if (!remotes.length) { console.error('no receivers connected'); eng.close(); process.exit(1); }

console.log(`recording ${SECONDS}s — run a QSO, or listen to one, so there is speech to capture`);
for (let s = 5; s <= SECONDS; s += 5) {
  await new Promise((r) => setTimeout(r, 5000));
  process.stdout.write(`  ${s}s  local ${local.env.length} blocks  `
    + remotes.map((r) => `${(r.rec.callsign || r.rec.host).slice(0, 8)}:${r.rec.frames.length}`).join(' ') + '\n');
}

for (const r of remotes) { try { r.ws.close(); } catch { /* already gone */ } }
eng.close();

const out = {
  recordedUtc: new Date().toISOString(),
  radio: { vfoHz: radio.vfoHz, mode: radio.mode },
  sampleRateHz: rate,
  seconds: SECONDS,
  local,
  remotes: remotes.map((r) => r.rec),
};
writeFileSync(OUT, JSON.stringify(out));
console.log(`\nwrote ${OUT}`);
console.log(`  local: ${local.env.length} envelope blocks (${(local.env.length / ENVELOPE_HZ).toFixed(1)}s), `
  + `${local.meters.length} meter frames, ${local.quality.length} quality frames`);
for (const r of remotes) console.log(`  ${r.rec.callsign || r.rec.host}: ${r.rec.frames.length} frames`);
