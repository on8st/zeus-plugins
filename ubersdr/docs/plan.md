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

## Phase 0 — done. All four settled.

**1. Can the panel reach the engine's API? Yes, cross-origin, explicitly allowed.**

The UI page is served by the **product** on `:53984`; the engine 404s `/`. The
plugin's UI module is served by the **engine** and 404s on the product. So the
panel runs on one origin and its module comes from another — and the engine
returns:

```
Access-Control-Allow-Origin: http://127.0.0.1:53984
Access-Control-Allow-Credentials: true
```

It permits the product origin by name. A panel fetch of `/api/state` therefore
works, provided it uses an absolute URL to the engine — a bare `/api/state`
resolves against the *page* origin and 404s on the product.

**A backend route is still the recommendation.** It sidesteps origin and port
discovery entirely, and the plugin runs inside the engine process, so it can read
`--port` from `Environment.GetCommandLineArgs()`. Direct fetch is the
optimisation; the proxy is the thing that keeps working when the layout changes.

**2. Poll or stream? Poll — there is no state stream.**

The engine's `/ws` accepts a connection and streams **binary telemetry only**:
217 frames in 8 seconds, not one JSON message, nothing resembling radio state or
keying. It is the audio/spectrum transport, not a control plane.

Polling `GET /api/radio/ptt-status` measured at 10 Hz over loopback:

| median | p95 | max | worst-case detection lag |
|---|---|---|---|
| 3.2 ms | 4.6 ms | 16.2 ms | **≈ 116 ms** |

Against a 7 ms tune latency and transmissions measured in seconds, 100 ms
resolution is comfortably enough. Poll at 10 Hz while idle-but-armed, and there
is no reason to go faster.

**3. Does the tune protocol behave as read? Yes — with four corrections.**

Full findings in [`design/source/protocol.md`](design/source/protocol.md).
Connection is a three-step affair (client-generated UUID → `POST /connection`
admission → socket with parameters in the query string), version 2 is the only
one that streams audio, the invalid sentinel is `-Infinity` rather than `-999.0`,
and a 30-second over costs about 196 kB per receiver. **The gate passed.**

**4. Opus decoder.** `opus-decoder` (`eshaz/wasm-audio-decoders`) is **MIT**,
compatible with GPL-2.0-or-later, and vendorable. Current version 0.7.11.

### What phase 0 changed

- Pre-tuning receivers on VFO change is **not** required: 7 ms tune latency means
  key-down is soon enough.
- The monitor must **exclude antenna-less instances from metering** — this
  station's own instance reports `antenna_connected: false` and sends
  `-Infinity` power on every frame, which must read as *no meter available*
  rather than *hears nothing*.
- Admission control (`POST /connection`) is where an instance enforces its client
  limit, so honouring a refusal is not optional politeness — it is the protocol.

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
