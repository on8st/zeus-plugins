// SPDX-License-Identifier: GPL-2.0-or-later
//
// Actually render the panel, with a stub React and a stub host API.
//
// `node --check` parses; it does not execute, so it cannot see a hook whose
// dependency array names a const declared further down the component. That is
// a temporal-dead-zone ReferenceError on first render, and it shipped: Zeus
// caught it and told the operator "one of the panels failed to render".
//
// This calls the component function, so anything that throws on first render
// fails the build instead of the workspace.
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const modulePath = process.argv[2];
if (!modulePath) { console.error('usage: check.mjs <ui/panel.es.js>'); process.exit(2); }

// ---- a React small enough to read and real enough to catch things ---------
//
// The first version of this rendered once with empty state, which caught a
// temporal-dead-zone error and then missed everything that only happens once
// data has loaded. So: state that actually updates, effects that actually run,
// and a backend stub that returns realistic shapes. Render until it settles.
const hooks = [];
let cursor = 0;
let dirty = false;
const effects = [];

const React = {
  createElement(type, props, ...children) {
    // Render function components eagerly: a child that throws must fail here.
    if (typeof type === 'function') {
      const before = cursor;
      try { return type({ ...(props ?? {}), children }); }
      finally { cursor = before; }
    }
    return { type, props, children };
  },
};

const useState = (init) => {
  const i = cursor++;
  if (!(i in hooks)) hooks[i] = typeof init === 'function' ? init() : init;
  const set = (v) => {
    const next = typeof v === 'function' ? v(hooks[i]) : v;
    if (!Object.is(next, hooks[i])) { hooks[i] = next; dirty = true; }
  };
  return [hooks[i], set];
};
const useRef = (init) => { const i = cursor++; if (!(i in hooks)) hooks[i] = { current: init }; return hooks[i]; };
const useCallback = (fn) => fn;
const useMemo = (fn) => fn();
const useEffect = (fn, deps) => {
  const i = cursor++;
  const prev = hooks[i];
  const changed = !prev || !deps || !prev.deps || deps.some((d, k) => !Object.is(d, prev.deps[k]));
  hooks[i] = { deps };
  if (changed) effects.push(fn);
};

globalThis.window = {
  setInterval: () => 0, clearInterval: () => {}, AudioContext: function () {},
};
globalThis.WebSocket = function () {};
// What the vendored UMD bundle registers when a browser loads it.
globalThis['opus-decoder'] = { OpusDecoder: function () {} };
globalThis.fetch = async () => ({ ok: true, json: async () => ({}) });

// The module imports React by a bare specifier, and its own siblings by
// relative path. A data: URL has no base to resolve "./map.js" against, so the
// shimmed copy is written NEXT TO the original and imported from there —
// relative imports then resolve exactly as they will in the browser, and stack
// traces name a real file.
const { readFile, writeFile, unlink } = await import('node:fs/promises');
let code = await readFile(modulePath, 'utf8');
code = code.replace(/^import React[^\n]*\n/m, '');
// Vendored bundles are imported for their side effect and are UMD. Node
// evaluates them as CommonJS and pulls dependencies a browser ES module never
// touches, so they are stubbed here — their real loading is a browser concern,
// and PackagingTests already asserts they ship.
code = code.replace(/^import '\.\/vendor\/[^\n]*\n/gm, '');

const shim = `
const React = globalThis.__React;
const { useCallback, useEffect, useRef, useState, useMemo } = globalThis.__hooks;
`;
globalThis.__React = React;
globalThis.__hooks = { useCallback, useEffect, useRef, useState, useMemo };

const dir = path.dirname(path.resolve(modulePath));
const tmp = path.join(dir, `.panel-check-${process.pid}.mjs`);
await writeFile(tmp, shim + code, 'utf8');

const clean = (s) => String(s ?? '').split(tmp).join('<panel>');

let mod;
try {
  mod = await import(pathToFileURL(tmp).href);
} catch (e) {
  console.error(`FAIL  ${e?.name ?? 'Error'}: ${clean(e?.message ?? e)}`);
  await unlink(tmp).catch(() => {});
  process.exit(1);
} finally {
  // Leave it only long enough to import; a stray file in ui/ would be packaged.
  setTimeout(() => unlink(tmp).catch(() => {}), 0);
}

if (typeof mod.default !== 'function') {
  console.error('FAIL  the module has no default export register(api)');
  process.exit(1);
}

// ---- exercise it -----------------------------------------------------------
let registered = null;
const cleanups = [];
// Shapes taken from real responses, so the loaded state the panel reaches is
// the one it will actually reach.
const RX = (host, lat, lon, km, brg) => ({
  id: host, callsign: host.slice(0, 6).toUpperCase(), name: host, location: 'Somewhere',
  host, wsBase: 'wss://' + host, baseUrl: 'https://' + host,
  distanceKm: km, bearingDegrees: brg, lat, lon, country: 'Belgium',
  availableClients: 9, maxClients: 20,
});
const FIXTURES = {
  '/radio': { available: true, vfoHz: 7125000, mode: 'LSB', splitEnabled: false,
              splitTxHz: 0, moxOn: false, transmitHz: 7125000, band: '40m' },
  '/ptt': { available: true, keyed: false },
  '/config': { homeGrid: 'JO21ha', selectedHosts: [], preset: 'spread', count: 6 },
  '/receivers': {
    fetchedUtc: new Date().toISOString(), total: 54,
    excludedNoAntenna: 2, excludedFull: 0, offline: 1, excludedByLimits: 0,
    suggested: [RX('a.example', 52.9, 5.7, 215, 26), RX('b.example', 51.1, 5.2, 58, 100),
                RX('c.example', 41.7, -72.7, 5800, 292)],
    candidates: [RX('a.example', 52.9, 5.7, 215, 26)],
  },
};

const api = {
  registerPanel: (p) => { registered = p; },
  callBackend: async (method, path2) => {
    const key = Object.keys(FIXTURES).find((k) => String(path2).startsWith(k));
    return { ok: true, json: async () => (key ? FIXTURES[key] : {}) };
  },
};

mod.default(api);

if (!registered?.id || typeof registered.component !== 'function') {
  console.error('FAIL  register() did not register a panel with an id and a component');
  process.exit(1);
}

// First render. A TDZ error, a bad hook order, or a typo in JSX-free
// createElement all surface right here.
try {
  // Render, run effects, and keep rendering while state is still settling —
  // which is how the panel reaches its loaded state rather than its empty one.
  for (let pass = 0; pass < 12; pass++) {
    dirty = false;
    cursor = 0;
    registered.component();

    const queued = effects.splice(0);
    for (const fn of queued) {
      const cleanup = fn();
      if (typeof cleanup === 'function') cleanups.push(cleanup);
    }
    // Let any promises the effects started resolve before the next pass.
    await new Promise((r) => setImmediate(r));
    if (!dirty && queued.length === 0 && pass > 1) break;
  }

  // And unmount, which must not throw either.
  for (const c of cleanups.splice(0)) c();
} catch (e) {
  console.error(`FAIL  ${e?.name ?? 'Error'}: ${clean(e?.message ?? e)}`);
  if (e?.stack) console.error(clean(e.stack).split('\n').slice(1, 4).join('\n'));
  await unlink(tmp).catch(() => {});
  process.exit(1);
}
await unlink(tmp).catch(() => {});

// Report what state it actually reached, so a check that quietly renders the
// empty view forever cannot look like a pass.
const loaded = hooks.some((v) => Array.isArray(v) && v.length > 0);
console.log(`ok    panel '${registered.id}' rendered, effects ran, unmounted`
  + (loaded ? ' — reached a loaded state' : ' — WARNING: never left the empty state'));
