# UberSDR plugin — implementation plan

Target: the **remote monitor** — hear and measure your own signal through public
receivers while you transmit. Everything below is scoped by what a runtime probe
established, not by what the contracts advertise.

## What we are building on

| Need | Source | Verified |
|---|---|---|
| Operator's frequency, mode, split | engine `GET /api/state` — `vfoHz`, `mode`, `splitEnabled`, `splitTxHz` | yes, against the live engine |
| Keying state | engine `GET /api/radio/ptt-status` — `moxOn`, `tunOn`, `hangTimeMs` | yes |
| Receiver list | `https://instances.ubersdr.org/api/instances` | yes — 54 instances, unauthenticated |
| Remote audio + tune | `wss://<host>/ws`, `{type:"tune",frequency,mode,bandwidthLow,bandwidthHigh}`, Opus | read from the published client, **not yet exercised** |
| Signal quality | `signalSNR = basebandPower - noisePower`, true dB | read from the client, not yet exercised |
| Plugin contract gives | settings, logging, backend routes, panel | yes |
| Plugin contract does **not** give | radio state, radio control, audio playback, QRZ, identity | probed NULL on both engines |

## Phase 0 — settle four unknowns before writing product code

None of these is a discussion; each is an afternoon at most.

1. **Can the panel reach the engine's own API?** The panel is served from the
   engine (`/api/plugins/<id>/ui/...`), so same-origin is likely — but unverified.
   *If not:* a backend route proxying `/api/state` and `/api/radio/ptt-status` is
   the two-line fallback. Settle it first; it decides where the polling lives.
2. **Poll or stream?** `StreamingHub` exists. If radio state and PTT are on it,
   MOX transitions arrive promptly; if not, polling `ptt-status` at a few Hz is
   the floor. Latency here bounds how tightly recording can bracket a transmission.
3. **Does the tune protocol behave as read?** Connect to one instance — Stan's
   own, so nobody else's slot is consumed — send `tune`, confirm audio frames and
   a usable SNR arrive. **This is the one that can invalidate the plan**, so it
   comes before anything is built on top.
4. **Opus in the panel.** Vendor a decoder; a CSP-constrained webview cannot pull
   one from a CDN. Check the licence is GPL-compatible.

**Gate:** if (3) fails, stop and redesign. Everything downstream assumes it.

## Phase 1 — receiver picker and live SNR, no audio

The smallest useful thing, and half the machinery for everything after.

- Backend: fetch and cache the directory; filter to online instances with free
  slots; rank by distance and bearing spread. Settings for the operator's grid
  (or take it from the directory's own `distance` field).
- Panel: list candidate receivers for **the band Zeus is on**, updating when the
  VFO moves bands. Connect to the chosen ones, show live SNR per receiver.
- Ships use cases **3** (is it me or the band) and **4** (where is this band
  open) on its own, without a single audio sample.

**Testable without a network:** directory parsing, filtering and ranking are pure
functions. Band-from-frequency is a pure function. Both get unit tests.

## Phase 2 — the monitor proper

- Watch keying. On key-down, connect (or unmute) the chosen receivers tuned to
  the **transmit** frequency — `splitTxHz` when `splitEnabled`, `vfoHz`
  otherwise. This is the detail that makes it correct for split operation and it
  is free, because the engine exposes both.
- **Record while keyed. Play back on unkey.** Not the safe option — the only
  responsible one. Panel audio goes to the browser's output with no knowledge of
  Zeus's routing and no MOX awareness; live playback through shack speakers with
  an open mic is a delayed howl, transmitted.
- Peak-hold the SNR across the transmission and show it per receiver. An
  instantaneous reading arriving seconds late says nothing; the peak over the
  over is the honest number.

Ships use case **1** — hear yourself as others hear you.

## Phase 3 — live monitoring, opt-in

A toggle for headphone users, with the reason stated in the panel rather than
buried. Off by default, and it should stay off by default forever.

## Phase 4 — antenna A/B

Peak-hold SNR per transmission, retained across several, labelled by whatever the
operator was switching. Turns use case **2** into a measurement instead of an
impression. Cheap once phase 2 exists: it is the same numbers, kept.

## How it gets tested

The pattern that worked for Wavelog, applied again:

- **`tools/FakeUberSdr`** — a local WebSocket server speaking the real protocol:
  accepts `tune`, emits `audio` frames and power/noise figures. Lets the whole
  monitor run offline, deterministically, with no public receiver involved.
  Every fixture in it must be traceable to the published client or a capture —
  the Wavelog fake encoded *our reading* of the API and validated three bugs
  into existence.
- **Unit tests** for the pure parts: directory filtering, band mapping,
  frequency selection under split, peak-hold arithmetic.
- **`tools/ubersdr-harness`** — the zeus-harness pattern: real engine, plugin
  installed, fake UberSDR, driven over HTTP and asserted.
- **A live check against Stan's own instance**, never someone else's, and only
  when a phase gate needs it.

## Courtesy, and a thing to ask first

Every monitored receiver occupies a client slot on hardware someone else pays
for. So: connect around transmissions rather than camping, honour
`available_clients`, never auto-connect more than the operator chose, and default
to nearby instances rather than the rarest DX ones.

**Before any of this polls the directory from more than one machine, ask.**
`instances.ubersdr.org/api/instances` is unauthenticated and 638 KB. It is
reachable, which is not the same as being offered. The project has a groups.io
community and a GitHub repo (`madpsy/ka9q_ubersdr`); asking costs one message and
protects an open endpoint from being closed for everyone.

## What this plan does not include

Transmitting through UberSDR, feeding Zeus's DSP chain, or creating Zeus spots —
all established as impossible for a plugin. And TDoA (use case 7), which needs
correlated timing across instances and remains unverified as an outside
capability; worth an experiment later, not a phase.
