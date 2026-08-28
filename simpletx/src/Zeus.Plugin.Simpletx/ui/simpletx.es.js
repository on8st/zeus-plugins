// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// Simple TX — nine controls and three meters.
//
// The panel polls one endpoint. Everything it needs to draw itself comes back
// in a single /state round trip, including the verdict, which is worked out in
// the backend so the panel cannot disagree with it.
//
// React arrives from the host as a bare specifier and there is no build step,
// so this is React.createElement rather than JSX.

import React, { useCallback, useEffect, useRef, useState } from 'react';

const h = React.createElement;

const POLL_MS = 400;

// ---------------------------------------------------------------- meters

const SEG_MAIN = 28;
const SEG_MIC = 20;
const SEG_SWR = 20;

// S1 is -121 dBm and every S-unit is 6 dB, so S9 lands on -73 dBm. The bar
// runs S1 to S9+60 because that is what the scale under it claims.
const S1_DBM = -121;
const S_TOP_DBM = -13;

const litFromRange = (value, lo, hi, segments) => {
  if (value === null || value === undefined || Number.isNaN(value)) return 0;
  const t = (value - lo) / (hi - lo);
  return Math.max(0, Math.min(segments, Math.round(t * segments)));
};

const COLOURS = {
  green: '#2f9463',
  amber: '#d99518',
  red: '#c4382c',
  off: 'var(--zeus-led-off, #2c343d)',
};

function LedBar({ lit, segments, amberAt, redAt, dim }) {
  const cells = [];
  for (let i = 0; i < segments; i += 1) {
    let bg = COLOURS.off;
    if (i < lit) bg = i >= redAt ? COLOURS.red : i >= amberAt ? COLOURS.amber : COLOURS.green;
    cells.push(h('i', {
      key: i,
      style: {
        flex: 1,
        height: 14,
        background: bg,
        opacity: dim ? 0.35 : 1,
        transition: 'background 90ms linear',
      },
    }));
  }
  return h('div', {
    style: {
      display: 'flex', gap: 2, padding: 4,
      background: 'var(--zeus-led-well, #14181d)',
      border: '1px solid var(--zeus-border, #333c47)',
    },
  }, cells);
}

function Meter({ name, hot, lit, segments, amberAt, redAt, reading, scale, dim }) {
  return h('div', { style: { marginBottom: 10 } },
    h('div', {
      style: {
        display: 'grid', gridTemplateColumns: '54px 1fr 78px',
        alignItems: 'center', gap: 12,
      },
    },
      h('span', {
        style: {
          textTransform: 'uppercase', letterSpacing: '.11em',
          fontSize: 12, fontWeight: 700,
          color: hot ? '#b0322a' : 'var(--zeus-muted, #98a3b0)',
        },
      }, name),
      h(LedBar, { lit, segments, amberAt, redAt, dim }),
      h('span', {
        style: {
          fontFamily: 'ui-monospace, monospace', fontSize: 17, fontWeight: 600,
          textAlign: 'right', fontVariantNumeric: 'tabular-nums',
          opacity: dim ? 0.5 : 1,
        },
      }, reading)),
    h('div', {
      style: {
        display: 'flex', justifyContent: 'space-between',
        fontFamily: 'ui-monospace, monospace', fontSize: 10,
        color: 'var(--zeus-faint, #6d7885)',
        marginLeft: 66, marginRight: 78, marginTop: 3,
      },
    }, scale.map((t, i) => h('span', { key: i }, t))));
}

// ---------------------------------------------------------------- controls

const labelStyle = {
  display: 'block', textTransform: 'uppercase', letterSpacing: '.1em',
  fontSize: 11, fontWeight: 600, color: 'var(--zeus-muted, #98a3b0)',
  marginBottom: 4,
};

const valueStyle = {
  fontFamily: 'ui-monospace, monospace', fontSize: 18, fontWeight: 500,
  fontVariantNumeric: 'tabular-nums',
};

function Group({ title, children }) {
  return h('div', { style: { padding: '16px 16px 18px', minWidth: 210, flex: 1 } },
    h('span', {
      style: {
        display: 'block', textTransform: 'uppercase', letterSpacing: '.12em',
        fontSize: 12, fontWeight: 700, color: '#b06c0d', marginBottom: 14,
      },
    }, title),
    children);
}

function KeyButton({ label, on, onClick, disabled }) {
  return h('button', {
    onClick,
    disabled,
    'aria-pressed': on ? 'true' : 'false',
    style: {
      flex: 1, padding: '13px 8px', cursor: disabled ? 'not-allowed' : 'pointer',
      textTransform: 'uppercase', letterSpacing: '.11em',
      fontWeight: 700, fontSize: 16,
      border: `1px solid ${on ? '#b0322a' : 'var(--zeus-border, #333c47)'}`,
      background: on ? '#b0322a' : 'var(--zeus-surface-2, #222831)',
      color: on ? '#fff' : 'inherit',
      opacity: disabled ? 0.5 : 1,
    },
  }, label);
}

// ---------------------------------------------------------------- panel

function SimpleTxPanel({ api }) {
  const [state, setState] = useState(null);
  const [error, setError] = useState(null);

  // Slider positions are local while dragging so a poll landing mid-drag does
  // not yank the handle back to the last value the radio reported.
  const [drive, setDrive] = useState(null);
  const [micGain, setMicGain] = useState(null);
  const dragging = useRef(false);

  const post = useCallback(async (path, body) => {
    try {
      await api.callBackend('POST', path, body);
    } catch {
      /* a restarting engine is not an error worth showing */
    }
  }, [api]);

  useEffect(() => {
    let alive = true;
    const tick = async () => {
      try {
        const r = await api.callBackend('GET', '/state');
        if (!r.ok || !alive) return;
        const next = await r.json();
        setState(next);
        setError(null);
        if (!dragging.current) {
          setDrive((d) => (d === null ? next.drivePercent : d));
          setMicGain((g) => (g === null ? next.micGainDb : g));
        }
      } catch (e) {
        if (alive) setError(String(e && e.message ? e.message : e));
      }
    };
    tick();
    const id = setInterval(tick, POLL_MS);
    return () => { alive = false; clearInterval(id); };
  }, [api]);

  if (error) {
    return h('div', { style: { padding: 20 } }, `Simple TX: ${error}`);
  }
  if (!state) {
    return h('div', { style: { padding: 20 } }, 'Simple TX: connecting…');
  }
  if (!state.available) {
    return h('div', { style: { padding: 20 } },
      'Simple TX: no radio. Connect a radio and this panel comes up with it.');
  }

  const t = state.telemetry;
  const keyed = state.keyed || state.tuning;
  const drivePct = drive === null ? state.drivePercent : drive;
  const micDb = micGain === null ? state.micGainDb : micGain;

  // Top meter does double duty: signal on receive, forward power on transmit.
  const mainLit = keyed
    ? litFromRange(t ? t.forwardWatts : 0, 0, 10, SEG_MAIN)
    : litFromRange(t ? t.signalDbm : null, S1_DBM, S_TOP_DBM, SEG_MAIN);
  const mainRead = keyed
    ? `${(t ? t.forwardWatts : 0).toFixed(1)} W`
    : t ? sLabel(t.signalDbm) : '—';

  const micLit = t && t.micPeakDbfs !== null && t.micPeakDbfs !== undefined
    ? litFromRange(t.micPeakDbfs, -48, 0, SEG_MIC) : 0;
  const micRead = t && t.micPeakDbfs !== null && t.micPeakDbfs !== undefined
    ? `${t.micPeakDbfs.toFixed(0)} dB` : '—';

  const swr = t && t.swr !== null && t.swr !== undefined ? t.swr : null;
  const swrLit = swr === null ? 0 : Math.max(1, litFromRange(swr, 1, 4, SEG_SWR));

  const bad = state.verdict === 'NoDrive' || state.verdict === 'NoAudio';

  return h('div', { style: { fontSize: 14 } },

    // meters
    h('div', { style: { padding: '16px 16px 6px' } },
      h(Meter, {
        name: keyed ? 'Power' : 'Signal',
        hot: keyed,
        lit: mainLit,
        segments: SEG_MAIN,
        amberAt: keyed ? Math.round(SEG_MAIN * 0.7) : 18,
        redAt: keyed ? Math.round(SEG_MAIN * 0.9) : 23,
        reading: mainRead,
        scale: keyed ? ['0', '2', '4', '6', '8', '10 W']
                     : ['S1', '3', '5', '7', '9', '+20', '+40', '+60'],
      }),
      h(Meter, {
        name: 'Mic',
        lit: micLit,
        segments: SEG_MIC,
        amberAt: 15,
        redAt: 19,
        reading: micRead,
        scale: ['-40', '-30', '-20', '-12', '-6', '0 dB'],
      }),
      h(Meter, {
        name: 'SWR',
        lit: swrLit,
        segments: SEG_SWR,
        amberAt: Math.round(SEG_SWR * (1.5 - 1) / 3),
        redAt: Math.round(SEG_SWR * (2.5 - 1) / 3),
        reading: swr === null ? '—' : swr.toFixed(1),
        scale: ['1.0', '1.5', '2.0', '3.0', '4+'],
        dim: swr === null,
      })),

    // controls
    h('div', {
      style: {
        display: 'flex', flexWrap: 'wrap',
        borderTop: '1px solid var(--zeus-border, #333c47)',
      },
    },

      h(Group, { title: 'Key' },
        h('div', { style: { display: 'flex', gap: 8, marginBottom: 14 } },
          h(KeyButton, {
            label: 'PTT',
            on: state.keyed && !state.tuning,
            onClick: () => post('/mox', { on: !state.keyed }),
          }),
          h(KeyButton, {
            label: 'Tune',
            on: state.tuning,
            onClick: () => post('/tune', { on: !state.tuning }),
          })),
        h('div', null,
          h('span', { style: labelStyle }, 'Drive'),
          h('span', { style: valueStyle }, `${drivePct}%`),
          h('input', {
            type: 'range', min: 0, max: 100, value: drivePct,
            'aria-label': 'Drive percent',
            style: { width: '100%', marginTop: 6 },
            onMouseDown: () => { dragging.current = true; },
            onMouseUp: () => { dragging.current = false; },
            onChange: (e) => {
              const v = Number(e.target.value);
              setDrive(v);
              post('/drive', { percent: v });
            },
          }))),

      h(Group, { title: 'Source' },
        h('div', { style: { marginBottom: 14 } },
          h('span', { style: labelStyle }, 'TX audio from'),
          h('select', {
            value: state.txAudioSource || 'Host',
            'aria-label': 'TX audio source',
            style: { width: '100%', padding: '5px 6px' },
            onChange: (e) => post('/source', { source: e.target.value }),
          },
            h('option', { key: 'host', value: 'Host' }, 'Host'),
            h('option', { key: 'mic', value: 'RadioMic' }, 'Radio mic'))),
        h('div', { style: { marginBottom: 14 } },
          h('span', { style: labelStyle }, 'Mic gain'),
          h('span', { style: valueStyle }, `${Number(micDb).toFixed(0)} dB`),
          h('input', {
            type: 'range', min: -12, max: 40, value: micDb,
            'aria-label': 'Mic gain dB',
            style: { width: '100%', marginTop: 6 },
            onMouseDown: () => { dragging.current = true; },
            onMouseUp: () => { dragging.current = false; },
            onChange: (e) => {
              const v = Number(e.target.value);
              setMicGain(v);
              post('/mic-gain', { db: v });
            },
          })),
        h('div', null,
          h('span', { style: labelStyle }, 'TX filter'),
          h('span', { style: valueStyle }, '0 – 2850 Hz'))),

      h(Group, { title: 'Guard' },
        h('div', { style: { marginBottom: 14 } },
          h('span', { style: labelStyle }, 'Max drive'),
          h('span', { style: valueStyle }, '100%')),
        h('div', { style: { marginBottom: 14 } },
          h('span', { style: labelStyle }, 'TX timeout'),
          h('span', { style: valueStyle }, `${state.timeoutSeconds} s`)),
        h('div', null,
          h('span', { style: labelStyle }, 'PA'),
          h('span', { style: valueStyle },
            t ? `${t.paTempC.toFixed(1)} °C` : '—')))),

    // verdict
    h('div', {
      style: {
        borderTop: '1px solid var(--zeus-border, #333c47)',
        padding: '12px 16px',
        borderLeft: `3px solid ${bad ? '#b0322a' : '#2c7052'}`,
        background: bad ? 'rgba(176,50,42,.12)' : 'transparent',
      },
    },
      h('span', {
        style: {
          display: 'block', textTransform: 'uppercase', letterSpacing: '.1em',
          fontSize: 11, color: 'var(--zeus-muted, #98a3b0)', marginBottom: 2,
        },
      }, 'Status'),
      h('span', null, state.message),
      t ? h('span', {
        style: {
          fontFamily: 'ui-monospace, monospace', fontSize: 12,
          color: 'var(--zeus-faint, #6d7885)', marginLeft: 8,
        },
      }, `wire ${t.wirePeak}/32767`) : null));
}

function sLabel(dbm) {
  if (dbm === null || dbm === undefined) return '—';
  const overS1 = dbm - S1_DBM;
  if (dbm <= -73) return `S${Math.max(1, Math.min(9, Math.round(overS1 / 6) + 1))}`;
  return `S9+${Math.round((dbm + 73) / 10) * 10}`;
}

export default function register(api) {
  api.registerPanel({
    id: 'simpletx.main',
    component: () => h(SimpleTxPanel, { api }),
  });
}
