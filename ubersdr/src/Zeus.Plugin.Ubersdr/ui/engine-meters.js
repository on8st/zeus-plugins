// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// The local receiver's own RF telemetry.
//
// The panel used to meter the local ear from the decoded audio, which is the
// wrong quantity twice over: it sits downstream of the AGC, and downstream of
// this panel's own level restoration, so it says more about Zeus's volume
// slider than about the band. The engine already publishes the real thing on
// the same WebSocket, unrequested, at 5 Hz.
//
// Two frames matter here. Both are "bare payload": no 16-byte WireFormat
// header, the type byte is the first byte of the message.

export const MSG_RX_METERS_V2 = 0x19;
export const MSG_RX_SIGNAL_QUALITY = 0x3f;

/**
 * RxMetersV2 (0x19), 33 bytes. The full set of WDSP RXA stage readings.
 *
 * <p><code>signalMaxBin</code> is the interesting one: the strongest single FFT
 * bin inside the *active* passband, with a 0.5 s decaying peak hold, already
 * carrying the per-board calibration (+0.98 dB on a Hermes-Lite 2). It follows
 * mode, filter width and CTUN, because the engine reconfigures the detector on
 * every one of those changes. This is Thetis's `%signal_max_bin%`.
 *
 * <p>Units: Signal* and AgcEnv* are calibrated dBm; Adc* are dBFS (board
 * independent, so no calibration is applied); AgcGain is signed dB, positive
 * when the AGC is boosting.
 */
export function parseRxMeters(buf) {
  if (buf.byteLength < 29) return null;
  const v = new DataView(buf);
  if (v.getUint8(0) !== MSG_RX_METERS_V2) return null;
  const f = (o) => v.getFloat32(o, true);
  return {
    signalPk: f(1), signalAv: f(5),
    adcPk: f(9), adcAv: f(13),
    agcGain: f(17),
    agcEnvPk: f(21), agcEnvAv: f(25),
    // A short frame is a pre-0x19-extension engine; the field simply is not there.
    signalMaxBin: buf.byteLength >= 33 ? f(29) : null,
  };
}

/**
 * RxSignalQuality (0x3F), 18 bytes.
 *
 * <p>This is the one quantity that can honestly be compared with a remote
 * receiver. Absolute levels cannot be: the engine reports calibrated dBm from
 * your own front end, ka9q-ubersdr reports its own baseband power, and no
 * calibration ties the two together. Signal-above-noise has no such problem —
 * each side measures its own noise floor and subtracts it.
 *
 * <p>Zeus integrates signal-plus-noise inside the RX filter, estimates and
 * integrates the adjacent noise across the same bandwidth, and subtracts in
 * linear power. A valid below-noise signal therefore reads negative.
 */
export function parseSignalQuality(buf) {
  if (buf.byteLength < 18) return null;
  const v = new DataView(buf);
  if (v.getUint8(0) !== MSG_RX_SIGNAL_QUALITY) return null;
  const f = (o) => v.getFloat32(o, true);
  const q = {
    rxId: v.getUint8(1),
    snrDb: f(2), signalOnlyDbm: f(6), integratedNoiseDbm: f(10), confidence: f(14),
  };
  // The engine says so itself: NaN fields or zero confidence mean unavailable.
  // Reporting an unsettled measurement as a number would be worse than nothing.
  const ok = [q.snrDb, q.signalOnlyDbm, q.integratedNoiseDbm, q.confidence].every(Number.isFinite);
  return ok && q.confidence > 0 ? q : null;
}

/**
 * A stage whose WDSP path has not started yet returns about −400. The engine
 * passes that sentinel through deliberately so a frontend can show "bypassed"
 * rather than a plausible-looking −400 dBm.
 */
export const isMeterSentinel = (db) => !Number.isFinite(db) || db <= -200;

// S0 = −127 dBm, S9 = −73 dBm, 6 dB per S-unit, then dB over S9 — the standard
// amateur scale the engine's own manual specifies.
export const S_METER_FLOOR_DBM = -127;
export const S_METER_TOP_DBM = -13;      // S9+60

/** Where a dBm reading sits along an S-meter bar, 0..100. */
export function sMeterPct(dbm) {
  if (isMeterSentinel(dbm)) return 0;
  const span = S_METER_TOP_DBM - S_METER_FLOOR_DBM;
  return Math.max(0, Math.min(100, ((dbm - S_METER_FLOOR_DBM) / span) * 100));
}

/** "S7" / "S9+20", the way an operator reads a meter. */
export function sUnits(dbm) {
  if (isMeterSentinel(dbm)) return '—';
  if (dbm >= -73) return `S9+${Math.round((dbm + 73) / 10) * 10}`;
  const s = Math.max(0, Math.min(9, Math.round((dbm - S_METER_FLOOR_DBM) / 6)));
  return `S${s}`;
}
