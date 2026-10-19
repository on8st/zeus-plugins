// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// Ask the running engine the questions this plugin's design depends on, instead
// of reasoning about them. Every one of these was got wrong at least once by
// reading the source and stopping there.
//
//   node tools/engine-probe/probe.mjs [--afgain]
//
// --afgain performs a WRITE: it drops RX AF gain to -50 dB for two seconds to
// see whether Zeus re-asserts its own slider value, then puts it back. Left out
// by default because it is audible.

import { execSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';

const PLUGIN_ID = 'be.on8st.zeus.plugins.ka9q.ubersdr.remoteears';
const doAfGain = process.argv.includes('--afgain');

const port = (() => {
  try {
    return execSync("ps aux | grep -o 'StationEngine --port [0-9]*' | head -1 | awk '{print $3}'")
      .toString().trim();
  } catch { return ''; }
})();
if (!port) { console.error('engine not running — start Zeus first'); process.exit(1); }
const api = (p, init) => fetch(`http://127.0.0.1:${port}/api${p}`, init);
console.log(`engine on port ${port}\n`);

// --- 1. which build is installed on disk -----------------------------------
try {
  const f = path.join(homedir(), 'Library/Application Support/Zeus/features',
                      PLUGIN_ID, 'ui/build.js');
  const stamp = /BUILD = '([^']+)'/.exec(readFileSync(f, 'utf8'))?.[1];
  console.log(`1. installed build   ${stamp}`);
  console.log('   the panel must show this same stamp. If it does not, the page is');
  console.log('   still running a cached module and nothing below is meaningful.\n');
} catch { console.log('1. installed build   NOT FOUND — plugin not installed\n'); }

// --- 2. is the plugin's backend reachable ----------------------------------
{
  const r = await api(`/plugins/${PLUGIN_ID}/ptt`).catch(() => null);
  console.log(`2. plugin backend    ${r?.ok ? 'ok — ' + (await r.text()) : 'UNREACHABLE'}`);
  console.log('   a 404 here means the engine has not loaded the renamed plugin.\n');
}

// --- 3. does the RX telemetry this panel now meters actually arrive? -------
{
  const seen = new Map();
  const ws = new WebSocket(`ws://127.0.0.1:${port}/ws`);
  ws.binaryType = 'arraybuffer';
  await new Promise((r) => { ws.onopen = r; });
  ws.onmessage = (e) => {
    if (typeof e.data === 'string') return;
    const t = new Uint8Array(e.data)[0];
    const k = `0x${t.toString(16).padStart(2, '0')}`;
    const s = seen.get(k) ?? { n: 0, bytes: e.data.byteLength };
    s.n++; seen.set(k, s);
    if (t === 0x19 && e.data.byteLength >= 33) {
      s.maxBin = new DataView(e.data).getFloat32(29, true).toFixed(1);
      s.avg = new DataView(e.data).getFloat32(5, true).toFixed(1);
    }
    if (t === 0x3f && e.data.byteLength >= 18) {
      s.snr = new DataView(e.data).getFloat32(2, true).toFixed(1);
      s.conf = new DataView(e.data).getFloat32(14, true).toFixed(2);
    }
  };
  // Audio needs asking for; the meter frames do not.
  const hdr = new Uint8Array(17);
  hdr[0] = 0x21; new DataView(hdr.buffer).setUint16(2, 1, true); hdr[16] = 1;
  ws.send(hdr);
  await new Promise((r) => setTimeout(r, 4000));
  ws.close();

  console.log('3. websocket frames in 4 s');
  const label = { '0x01': 'DisplayFrame', '0x02': 'AudioPcm', '0x14': 'RxMeter (legacy)',
                  '0x19': 'RxMetersV2', '0x3f': 'RxSignalQuality' };
  for (const [k, v] of [...seen].sort()) {
    const extra = v.maxBin ? `  maxBin=${v.maxBin} dBm  avg=${v.avg} dBm`
                : v.snr ? `  snr=${v.snr} dB  confidence=${v.conf}` : '';
    console.log(`   ${k} ${(label[k] ?? '').padEnd(18)} ${String(v.n).padStart(4)} frames  ${v.bytes} B${extra}`);
  }
  const v2 = seen.get('0x19');
  console.log(v2
    ? `   => the local RF bar has its source (${v2.bytes} B frames${v2.bytes >= 33 ? ', max bin present' : ', TOO SHORT — no max bin'})`
    : '   => NO RxMetersV2. The local bar will fall back to AF audio level.');
  console.log(seen.has('0x3f')
    ? '   => local S/N available.\n'
    : '   => NO RxSignalQuality: no local S/N, so no comparable axis.\n');
}

// --- 4. does Zeus fight an external AF gain write? -------------------------
if (doAfGain) {
  const before = (await (await api('/state')).json()).rxAfGainDb;
  console.log(`4. AF gain re-assertion   (was ${before} dB)`);
  await api('/rx/afGain', { method: 'POST', headers: { 'content-type': 'application/json' },
                            body: JSON.stringify({ db: -50 }) });
  for (const wait of [250, 1000, 2000]) {
    await new Promise((r) => setTimeout(r, wait === 250 ? 250 : 750));
    const now = (await (await api('/state')).json()).rxAfGainDb;
    console.log(`   after ${String(wait).padStart(4)} ms   rxAfGainDb = ${now}`);
  }
  const settled = (await (await api('/state')).json()).rxAfGainDb;
  await api('/rx/afGain', { method: 'POST', headers: { 'content-type': 'application/json' },
                            body: JSON.stringify({ db: before }) });
  console.log(`   restored to ${before} dB`);
  console.log(settled <= -49
    ? '   => the write STICKS. The plugin can silence Zeus itself.'
    : '   => Zeus RE-ASSERTS its slider. The operator must lower it by hand.\n');
} else {
  console.log('4. AF gain re-assertion   skipped — re-run with --afgain (it is audible)\n');
}
