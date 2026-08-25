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
globalThis.fetch = async () => ({ ok: true, json: async () => ({}) });

// The module imports React by bare specifier and the vendored decoder for its
// side effect. Neither is resolvable here, so both are shimmed on disk-free
// module resolution via a loader-less trick: rewrite and evaluate.
const src = (await import('node:fs/promises')).readFile;
let code = await src(modulePath, 'utf8');
code = code
  .replace(/^import React[^\n]*\n/m, '')
  .replace(/^import '\.\/vendor\/[^\n]*\n/m, '');

const shim = `
const React = globalThis.__React;
const { useCallback, useEffect, useRef, useState, useMemo } = globalThis.__hooks;
`;
globalThis.__React = React;
globalThis.__hooks = { useCallback, useEffect, useRef, useState, useMemo };

const dataUrl = 'data:text/javascript;base64,' +
  Buffer.from(shim + code, 'utf8').toString('base64');

const mod = await import(dataUrl);

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
cursor = 0;
registered.component();

// Render again with hook state retained, which is what a real re-render does.
cursor = 0;
registered.component();

console.log(`ok    panel '${registered.id}' rendered twice without throwing`);
