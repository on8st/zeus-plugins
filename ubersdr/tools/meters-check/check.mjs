// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// Round-trip the engine's RX telemetry frames against bytes built to the
// contract in Zeus.Contracts/RxMetersV2Frame.cs and RxSignalQualityFrame.cs.
//
// Byte offsets are the classic silent failure: read signalAv where
// signalMaxBin should be and the meter still moves, still looks plausible, and
// is simply the wrong number. Nothing about that shows up on screen.

import { pathToFileURL } from 'node:url';
import path from 'node:path';

const ui = path.resolve(process.argv[2]
  ?? 'src/Zeus.Plugin.Ubersdr/ui/engine-meters.js');
const m = await import(pathToFileURL(ui).href);

let failed = 0;
const check = (name, cond, extra = '') => {
  if (cond) console.log(`ok    ${name}${extra ? '  ' + extra : ''}`);
  else { console.error(`FAIL  ${name}${extra ? '  ' + extra : ''}`); failed++; }
};

/** Build a frame exactly as the engine serialises it. */
function rxMetersV2({ signalPk, signalAv, adcPk, adcAv, agcGain, agcEnvPk, agcEnvAv,
                      signalMaxBin }, { legacy = false } = {}) {
  const len = legacy ? 1 + 4 * 7 : 1 + 4 * 8;
  const b = new ArrayBuffer(len);
  const v = new DataView(b);
  v.setUint8(0, 0x19);
  [signalPk, signalAv, adcPk, adcAv, agcGain, agcEnvPk, agcEnvAv, signalMaxBin]
    .slice(0, legacy ? 7 : 8)
    .forEach((x, i) => v.setFloat32(1 + i * 4, x, true));
  return b;
}

function signalQuality({ rxId, snrDb, signalOnlyDbm, integratedNoiseDbm, confidence }) {
  const b = new ArrayBuffer(18);
  const v = new DataView(b);
  v.setUint8(0, 0x3f);
  v.setUint8(1, rxId);
  [snrDb, signalOnlyDbm, integratedNoiseDbm, confidence]
    .forEach((x, i) => v.setFloat32(2 + i * 4, x, true));
  return b;
}

// --- every field lands where the contract says --------------------------
{
  const src = { signalPk: -61.5, signalAv: -68.25, adcPk: -12.5, adcAv: -30.75,
                agcGain: 14.5, agcEnvPk: -59.0, agcEnvAv: -66.5, signalMaxBin: -84.5 };
  const got = m.parseRxMeters(rxMetersV2(src));
  const wrong = Object.keys(src).filter((k) => got[k] !== src[k]);
  check('RxMetersV2: every field reads back at its own offset', wrong.length === 0,
    wrong.length ? `wrong: ${wrong.join(', ')}` : '');
  // The specific confusion worth naming: max bin is not the average.
  check('signalMaxBin is not read from the signalAv slot',
    got.signalMaxBin === -84.5 && got.signalAv === -68.25);
}

// --- an older engine simply has no max bin -------------------------------
{
  const got = m.parseRxMeters(rxMetersV2(
    { signalPk: -60, signalAv: -70, adcPk: -10, adcAv: -30, agcGain: 0,
      agcEnvPk: -60, agcEnvAv: -70, signalMaxBin: -80 }, { legacy: true }));
  check('a legacy 29-byte frame yields null for max bin, not garbage',
    got !== null && got.signalMaxBin === null && got.signalAv === -70);
}

// --- wrong type byte is not our frame ------------------------------------
{
  const audio = new ArrayBuffer(33);
  new DataView(audio).setUint8(0, 0x02);
  check('an audio frame is not parsed as meters', m.parseRxMeters(audio) === null);
  check('a meters frame is not parsed as signal quality',
    m.parseSignalQuality(rxMetersV2({ signalPk: 0, signalAv: 0, adcPk: 0, adcAv: 0,
      agcGain: 0, agcEnvPk: 0, agcEnvAv: 0, signalMaxBin: 0 })) === null);
}

// --- unavailable means unavailable ---------------------------------------
{
  const good = m.parseSignalQuality(signalQuality(
    { rxId: 0, snrDb: 12.5, signalOnlyDbm: -84, integratedNoiseDbm: -96.5, confidence: 0.9 }));
  check('a settled signal-quality frame parses', good?.snrDb === 12.5 && good.rxId === 0);

  const zero = m.parseSignalQuality(signalQuality(
    { rxId: 0, snrDb: 12.5, signalOnlyDbm: -84, integratedNoiseDbm: -96.5, confidence: 0 }));
  check('zero confidence is refused rather than shown', zero === null);

  const nan = m.parseSignalQuality(signalQuality(
    { rxId: 0, snrDb: NaN, signalOnlyDbm: -84, integratedNoiseDbm: -96.5, confidence: 0.9 }));
  check('a NaN field is refused rather than shown', nan === null);

  // Below the noise floor is a real, valid reading — not an error.
  const neg = m.parseSignalQuality(signalQuality(
    { rxId: 0, snrDb: -4.5, signalOnlyDbm: -101, integratedNoiseDbm: -96.5, confidence: 0.5 }));
  check('a negative SNR is a valid below-noise reading, not a rejection',
    neg?.snrDb === -4.5);
}

// --- the sentinel a stopped stage reports --------------------------------
{
  check('the ≈−400 sentinel is treated as "no reading"', m.isMeterSentinel(-400));
  check('the -200 default is treated as "no reading"', m.isMeterSentinel(-200));
  check('a real weak signal is NOT treated as a sentinel', !m.isMeterSentinel(-127));
  check('a sentinel sits at the bottom of the bar', m.sMeterPct(-400) === 0);
}

// --- the S-meter scale ----------------------------------------------------
{
  check('S9 is −73 dBm', m.sUnits(-73) === 'S9+0' || m.sUnits(-73).startsWith('S9'));
  check('S9+20 reads as S9+20', m.sUnits(-53) === 'S9+20');
  check('a weak signal reads in S-units', m.sUnits(-115) === 'S2', m.sUnits(-115));
  check('the scale spans floor to top', m.sMeterPct(-127) === 0 && m.sMeterPct(-13) === 100);
  check('S9 sits high on the bar but not at the end',
    m.sMeterPct(-73) > 40 && m.sMeterPct(-73) < 60, `${m.sMeterPct(-73).toFixed(0)}%`);
}

if (failed) { console.error(`\n${failed} failed`); process.exit(1); }
console.log('\nengine RX telemetry holds');
