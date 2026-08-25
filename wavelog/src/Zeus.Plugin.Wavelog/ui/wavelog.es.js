// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// Wavelog configuration panel.
//
// The contract this implements was read from the GPL-2.0-or-later sample
// plugins the Zeus registry distributes (verified by sha256): an ES module whose
// default export is `register(api)`, which calls `api.registerPanel({ id,
// component })`. `api.callBackend(method, path, body)` returns a fetch Response
// and is prefixed with this plugin's route, so "/config" reaches
// /api/plugins/be.on8st.zeus.plugins.wavelog/config.
//
// Written with React.createElement rather than JSX so the plugin needs no build
// step: no npm, no bundler, no lockfile. React comes from the host as a bare
// specifier, the way the sample modules import it.

import React, { useCallback, useEffect, useState } from 'react';

const h = React.createElement;

// ---- small presentational helpers ------------------------------------------

const css = {
  panel: { display: 'flex', flexDirection: 'column', gap: 14, padding: 14,
           fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)', fontSize: 13 },
  row: { display: 'flex', alignItems: 'center', gap: 8 },
  label: { flex: '0 0 150px', color: 'var(--fg-2, #9aa0a6)' },
  input: { flex: 1, background: 'var(--bg-2, #16181d)', color: 'var(--fg, #e6e8ea)',
           border: '1px solid var(--border, #2a2e35)', borderRadius: 3, padding: '4px 6px',
           font: 'inherit' },
  button: { background: 'var(--bg-3, #22262d)', color: 'var(--fg, #e6e8ea)',
            border: '1px solid var(--border, #2a2e35)', borderRadius: 3,
            padding: '5px 10px', font: 'inherit', cursor: 'pointer' },
  section: { fontSize: 11, letterSpacing: '.1em', textTransform: 'uppercase',
             color: 'var(--fg-3, #5a5e66)', marginTop: 6 },
  note: { color: 'var(--fg-3, #5a5e66)', fontSize: 11, lineHeight: 1.45 },
  bad: { color: 'var(--danger, #e5715f)' },
  good: { color: 'var(--success, #4fbfa0)' },
};

function Field({ label, children, hint }) {
  return h('div', { style: { display: 'flex', flexDirection: 'column', gap: 3 } },
    h('div', { style: css.row },
      h('span', { style: css.label }, label),
      children),
    hint ? h('div', { style: { ...css.note, paddingLeft: 158 } }, hint) : null);
}

function Toggle({ label, checked, onChange, hint }) {
  return Field({
    label,
    hint,
    children: h('label', { style: { ...css.row, gap: 6, flex: 1 } },
      h('input', { type: 'checkbox', checked: !!checked,
                   onChange: (e) => onChange(e.target.checked) }),
      h('span', { style: css.note }, checked ? 'on' : 'off')),
  });
}

// ---- the panel --------------------------------------------------------------

function WavelogPanel({ api }) {
  const [config, setConfig] = useState(null);
  const [status, setStatus] = useState(null);
  const [profiles, setProfiles] = useState(null);
  const [apiKey, setApiKey] = useState('');
  const [message, setMessage] = useState(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      const [c, s] = await Promise.all([
        api.callBackend('GET', '/config'),
        api.callBackend('GET', '/status'),
      ]);
      if (c.ok) setConfig(await c.json());
      if (s.ok) setStatus(await s.json());
    } catch (e) {
      setMessage({ bad: true, text: 'could not read the plugin: ' + e });
    }
  }, [api]);

  useEffect(() => { void load(); }, [load]);

  // Status is the operator's window on the queue, so keep it fresh — but slowly.
  useEffect(() => {
    const tick = async () => {
      try {
        const s = await api.callBackend('GET', '/status');
        if (s.ok) setStatus(await s.json());
      } catch { /* a closed panel or a restarting engine is not an error */ }
    };
    const t = window.setInterval(tick, 5000);
    return () => window.clearInterval(t);
  }, [api]);

  const save = useCallback(async (patch) => {
    setBusy(true);
    setMessage(null);
    try {
      // An absent apiKey leaves the stored one alone, so saving other fields
      // can never wipe the key.
      const body = { ...patch };
      if (apiKey.trim()) body.apiKey = apiKey.trim();

      const res = await api.callBackend('POST', '/config', body);
      if (res.ok) {
        setApiKey('');
        setMessage({ text: 'saved' });
        await load();
      } else {
        const j = await res.json().catch(() => ({}));
        setMessage({ bad: true, text: j.error || 'the plugin refused that' });
      }
    } catch (e) {
      setMessage({ bad: true, text: String(e) });
    } finally {
      setBusy(false);
    }
  }, [api, apiKey, load]);

  const act = useCallback(async (path, body, describe) => {
    setBusy(true);
    setMessage(null);
    try {
      const res = await api.callBackend('POST', path, body);
      const j = await res.json().catch(() => ({}));
      setMessage(res.ok
        ? { text: describe(j) }
        : { bad: true, text: j.error || 'that did not work' });
      await load();
    } catch (e) {
      setMessage({ bad: true, text: String(e) });
    } finally {
      setBusy(false);
    }
  }, [api, load]);

  const fetchProfiles = useCallback(async () => {
    setBusy(true);
    try {
      const res = await api.callBackend('GET', '/profiles');
      if (res.ok) setProfiles(await res.json());
      else setMessage({ bad: true, text: (await res.json().catch(() => ({}))).error || 'could not list locations' });
    } finally { setBusy(false); }
  }, [api]);

  if (!config) return h('div', { style: css.panel }, h('div', { style: css.note }, 'loading…'));

  const pullIds = (config.pullStationIds || []).join(', ');

  return h('div', { style: css.panel },

    // The dependency, where the operator actually looks. A plugin manifest has
    // no way to declare one, so without this the only symptom of a missing
    // logbook is that nothing ever happens. The settings stay visible and
    // editable underneath: configuring before installing the logbook is
    // legitimate, and the values apply the moment it appears.
    status && status.logbookInstalled === false
      ? h('div', {
          style: {
            border: '1px solid var(--danger, #e5715f)', borderRadius: 3,
            padding: '12px 14px', display: 'flex', flexDirection: 'column', gap: 8,
          },
        },
          h('div', { style: { fontWeight: 600, color: 'var(--danger, #e5715f)' } },
            'No Zeus logbook found yet'),
          h('div', { style: { ...css.note, lineHeight: 1.5 } },
            'This is an extension of the Zeus logbook. It keeps your log in step '
            + 'with Wavelog, but it is not a logbook itself and has nothing to '
            + 'synchronise until one exists.'),
          h('div', { style: { ...css.note, lineHeight: 1.5 } },
            'Log a contact in Zeus and it will appear \u2014 no restart needed, this '
            + 'picks it up within half a minute. Settings below are saved meanwhile.'))
      : null,

    // Say plainly what this is. An operator who thinks the plugin *is* their
    // logbook will read every button here as riskier than it is.
    h('div', { style: css.note },
      'Keeps Zeus\u2019s own logbook in step with Wavelog, both directions. '
      + 'It does not hold your log \u2014 Zeus does, as always.'),

    // ---- connection
    h('div', { style: css.section }, 'connection'),

    Field({
      label: 'Wavelog URL',
      children: h('input', {
        style: css.input, value: config.baseUrl || '', placeholder: 'https://wavelog.example',
        onChange: (e) => setConfig({ ...config, baseUrl: e.target.value }),
      }),
    }),

    Field({
      label: 'API key',
      hint: config.apiKeySet
        ? 'a key is stored — leave blank to keep it, or type a new one to replace it'
        : 'no key stored yet; the plugin cannot reach Wavelog without one',
      children: h('input', {
        style: css.input, type: 'password', value: apiKey,
        placeholder: config.apiKeySet ? '•••••••• stored' : 'paste the key',
        onChange: (e) => setApiKey(e.target.value),
      }),
    }),

    // ---- station profiles
    h('div', { style: css.section }, 'station locations'),

    Field({
      label: 'push to location',
      hint: 'new QSOs are written to this one station location. Wavelog calls '
          + 'these Station Locations; a Logbook is a grouping of them, and is not '
          + 'something the API can be pointed at.',
      children: h('input', {
        style: { ...css.input, maxWidth: 90 }, type: 'number', min: 1,
        value: config.stationProfileId ?? 1,
        onChange: (e) => setConfig({ ...config, stationProfileId: Number(e.target.value) }),
      }),
    }),

    Field({
      label: 'pull from locations',
      hint: 'comma separated. A QSO logged under a station location that is not '
          + 'listed here is invisible to the sync — permanently, not late. List '
          + 'them all unless you mean to exclude one.',
      children: h('input', {
        style: css.input, value: pullIds, placeholder: '1, 2',
        onChange: (e) => setConfig({
          ...config,
          pullStationIds: e.target.value.split(',')
            .map((s) => parseInt(s.trim(), 10)).filter((n) => !Number.isNaN(n)),
        }),
      }),
    }),

    status && status.pullLocationsAreImplicit
      ? h('div', { style: { ...css.note, color: 'var(--warning, #d8a657)', lineHeight: 1.5 } },
          'No pull location chosen, so contacts are being imported from location '
          + (status.pullStationIds || []).join(', ')
          + ' \u2014 the one being pushed to. Wavelog will not accept an empty '
          + 'selection, so this is a fallback, not "everything". Set it explicitly '
          + 'if you meant somewhere else.')
      : null,

    h('div', { style: css.row },
      h('button', { style: css.button, disabled: busy, onClick: fetchProfiles },
        'list locations this key can reach'),
      profiles
        ? h('span', { style: css.note },
            profiles.map((p) => `${p.id} = ${p.name}`).join(' · '))
        : null),

    // ---- features
    h('div', { style: css.section }, 'features'),

    Toggle({
      label: 'push QSOs', checked: config.pushEnabled,
      onChange: (v) => setConfig({ ...config, pushEnabled: v }),
      hint: 'queue every logged contact and deliver it to Wavelog',
    }),
    Toggle({
      label: 'pull QSOs', checked: config.pullEnabled,
      onChange: (v) => setConfig({ ...config, pullEnabled: v }),
      hint: 'import contacts logged by other apps, and sweep back QSL status',
    }),
    Toggle({
      label: 'publish rig state', checked: config.radioEnabled,
      onChange: (v) => setConfig({ ...config, radioEnabled: v }),
      hint: 'send live frequency and mode so Wavelog can auto-fill its entry '
          + 'form. Needs a key with write permission.',
    }),

    h('div', { style: css.row },
      h('button', {
        style: css.button, disabled: busy,
        onClick: () => save({
          baseUrl: config.baseUrl,
          stationProfileId: config.stationProfileId,
          pullStationIds: config.pullStationIds,
          pushEnabled: config.pushEnabled,
          pullEnabled: config.pullEnabled,
          radioEnabled: config.radioEnabled,
        }),
      }, 'save'),
      h('button', {
        style: css.button, disabled: busy,
        onClick: () => act('/test', null, (j) => `reached Wavelog · ${j.profiles} location(s)`),
      }, 'test connection'),
      message
        ? h('span', { style: message.bad ? css.bad : css.good }, message.text)
        : null),

    // ---- status
    h('div', { style: css.section }, 'status'),

    status
      ? h('div', { style: { ...css.note, display: 'flex', flexDirection: 'column', gap: 3 } },
          h('div', null, status.configured ? 'configured' : 'not configured yet'),
          // Worth showing: if this reads zero against a log full of contacts,
          // the plugin has attached to the wrong file and is quietly idle.
          h('div', null, `${status.qsosInLogbook} QSO(s) in Zeus\u2019s logbook`),
          h('div', null, `${status.pending} waiting to upload · ${status.failed} failed`),
          h('div', null, `pull cursor at ${status.cursor}`),
          // Naming the profiles turns "why isn't that contact here" into a
          // glance rather than an investigation.
          h('div', null, `pulling from location(s) ${(status.pullStationIds || []).join(', ') || '—'}`
                       + ` · pushing to ${status.pushStationProfileId}`),
          status.lastError
            ? h('div', { style: css.bad }, `last error: ${status.lastError}`)
            : null)
      : null,

    status && status.failed > 0
      ? h('div', { style: css.row },
          h('button', {
            style: css.button, disabled: busy,
            onClick: () => act('/retry', null, (j) => `requeued ${j.requeued}`),
          }, `retry ${status.failed} failed`),
          h('span', { style: css.note },
            'fix the cause first — a wrong key or location will just fail again'))
      : null,

    // ---- repair
    h('div', { style: css.section }, 'repair'),

    h('div', { style: css.note },
      'Reconciles both directions by inserting what is missing. It never deletes: '
      + 'a QSO removed in Wavelog but present in Zeus stays. Dry run first.'),

    h('div', { style: css.row },
      h('button', {
        style: css.button, disabled: busy,
        onClick: () => act('/resync', { dryRun: true },
          (j) => `${j.missingHere} here that Wavelog has · ${j.missingThere} there that it does not`),
      }, 'check for gaps (dry run)'),
      h('button', {
        style: css.button, disabled: busy,
        onClick: () => act('/resync', { dryRun: false },
          (j) => `imported ${j.missingHere}, queued ${j.missingThere}`),
      }, 'apply')));
}

// ---- registration -----------------------------------------------------------

export default function register(api) {
  api.registerPanel({
    id: 'wavelog.config',
    component: () => h(WavelogPanel, { api }),
  });
}
