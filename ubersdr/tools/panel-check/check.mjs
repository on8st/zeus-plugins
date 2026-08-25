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

// ---- the smallest React that can catch a render-time throw -----------------
const hooks = [];
let cursor = 0;

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
  hooks[i] ??= typeof init === 'function' ? init() : init;
  return [hooks[i], () => {}];
};
const useRef = (init) => { const i = cursor++; hooks[i] ??= { current: init }; return hooks[i]; };
const useCallback = (fn, deps) => { void deps; return fn; };   // deps are evaluated by the caller
const useEffect = (fn, deps) => { void fn; void deps; };       // not run: no timers in a check
const useMemo = (fn) => fn();

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
const api = {
  registerPanel: (p) => { registered = p; },
  callBackend: async () => ({ ok: true, json: async () => ({}) }),
};

mod.default(api);

if (!registered?.id || typeof registered.component !== 'function') {
  console.error('FAIL  register() did not register a panel with an id and a component');
  process.exit(1);
}

// First render. A TDZ error, a bad hook order, or a typo in JSX-free
// createElement all surface right here.
try {
  cursor = 0;
  registered.component();
  // Again with hook state retained, which is what a real re-render does.
  cursor = 0;
  registered.component();
} catch (e) {
  console.error(`FAIL  ${e?.name ?? 'Error'}: ${clean(e?.message ?? e)}`);
  if (e?.stack) console.error(clean(e.stack).split('\n').slice(1, 4).join('\n'));
  await unlink(tmp).catch(() => {});
  process.exit(1);
}
await unlink(tmp).catch(() => {});

console.log(`ok    panel '${registered.id}' rendered twice without throwing`);
