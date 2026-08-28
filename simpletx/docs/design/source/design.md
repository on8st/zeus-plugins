# Simple TX — design

The SSOT is this file; any rendering at `docs/design/` is derived from it.

Companion: [`../../../../docs/simple-tx-proposal.md`](../../../../docs/simple-tx-proposal.md)
— the proposal, including the `Zeus.Plugins.Contracts` additions this depends on.
Working mockup of the panel:
<https://claude.ai/code/artifact/5fcf21fe-a747-43c5-9b82-b00cb5b9d6f3>.

## 1. What problem this solves

A transmit path can be entirely healthy and still put nothing on the air, and
Zeus currently has no screen that says so.

The case that prompted this: a Hermes-Lite 2 keyed for eight seconds and
radiated nothing. PA biased at 178 mA and heating at 0.45 °C/s, TX FIFO clean at
381 pkt/s with zero recovery events, network fine across a routed hop, gateware
current. Drive was 0% and the IQ buffer handed to the radio was all zeros:

```
mox=True  peak=0/32767  mean=0  firstI=0  firstQ=0  drv=0
```

Every reading a user could see looked healthy. Finding it took a packet capture
on UDP 1024, a PA bias read over port 1025, and a grep through four rotated log
files.

Two failures caused it, and both are silent:

1. **Drive at 0%** — the radio keys, the PA biases, nothing is modulated.
2. **Mic gain at 0 dB** — waiting behind the first, with the same symptom.

The panel exists to make both visible without instrumentation. Nine controls in
three groups, three meters, one verdict line.

### The split rule

> If a setting can stop the radio transmitting **entirely**, it belongs on the
> face. If it only changes how the transmission **sounds**, it stays where it is.

All nine chosen controls fail silently when wrong. PureSignal (17 state keys),
CFC, the VST chain, two-tone, AM profile, filter phase and window, the phase
rotator, the MOX delays and the post-TX mute delay do not — they change the
sound, not whether there is one. Mode is excluded for a different reason: it
belongs on the VFO and should not be duplicated here.

## 2. What is actually known

### Verified

Read directly from `Zeus.Plugins.Contracts/IPluginContext.cs` at station-engine
**v2.0.9** (`db764e1`):

- `IRadioController` has exactly three members — `SetFrequencyAsync(long)`,
  `SetModeAsync(string)`, `SetMoxAsync(bool)`. **One of the nine controls.**
- `IRadioStateReader` has `FrequencyHz`, `Mode`, `Band`, `Mox` and the three
  matching change events. **None of the five meter readings.**
- `PluginCapabilities` includes `ControlRadio`, `ReadRadioState`,
  `PersistSettings`, `NetworkAccess`, `AudioStream`.
- `IPluginContext.RadioController` is nullable and defaults to null, as does
  `Radio` — the established pattern for adding surface without an ABI bump.

From the plugin UI modules in this repo (`ubersdr/src/.../ui/*.js`):

- UI modules reach the host through `api.callBackend` (19 call sites) and
  `api.registerPanel` (3). There are **zero** `fetch` calls to the REST API.
  The C# surface is therefore the binding constraint, not a convenience.

From a running Zeus 2.0.14 engine and its logs:

- `p1.tx.rate` logs `peak=n/32767`, `mean`, `firstI`, `firstQ` and `drv` once
  per second, keyed or not.
- `wdsp.rx.meter` logs `sAv`, `adcAv`, `agcGain`, `agcAv`.
- `/api/state` carries `drivePct`, `driveMaxPct`, `tunePct`, `micGainDb`,
  `levelerMaxGainDb`, `txAudioSource`, `txFilterLowHz`, `txFilterHighHz`,
  `txTimeoutSec`.
- `POST /api/tx/drive` takes `{ percent }`; `POST /api/tx/mox` takes `{ on }`.
  `GET /api/tx/drive` returns 405 with `Allow: POST`.

From the radio itself (HL2 at gateware 74.2, four receivers, board id 5):

- Port 1025 answers while another client holds the radio, and polling it does
  not interrupt that client. Verified at 2 Hz against a concurrent stream of
  4.94 MB.
- It carries PA temperature, PA bias current, forward and reverse power counts,
  ADC clip count, TX FIFO recovery and depth — but **not** LNA gain or the
  attenuator, which are write-only.
- The HL2 mainboard has **no directional coupler**. Forward and reverse power
  come from the optional N2ADR filter/IO board (rev on AIN1, fwd on AIN2).

None of the telemetry the meters need is new DSP. Every number is already
computed and logged; it simply never reaches a plugin.

### Assumed — not verified

- **Panel slot `workspace.tools` and icon `Radio`.** Copied from Wavelog. A
  transmit-specific slot may exist and would be a better home. Not checked.
- **SDK `1.5.0`** as the version carrying the new contracts. No such release
  exists; the number is a placeholder for "the one after 1.4.0".
- **Endpoint names `/api/tx/timeout` and `/api/radio/mode`.** Inferred by
  pattern from neighbouring routes, never observed in a log or a probe.
- **That a UI module could reach `/api/tx/*` same-origin.** Never tested. It is
  rejected on design grounds regardless (§4), so it should stay untested.
- **`txAudioSource` value set.** Observed as `"Host"`. Whether the alternative
  is `"RadioMic"`, a device id, or an enumeration was not determined — the
  engine had shut down before it could be read.

## 3. Open questions

1. Is there a transmit-specific panel slot, or is `workspace.tools` the only
   home? Affects `plugin.json` before first release.
2. Does `txAudioSource` enumerate real CoreAudio devices, or only `Host` /
   radio mic? If it enumerates, the dropdown should show device names — "Host"
   does not tell you *which* host device, and picking the wrong one produces
   exactly the silent failure this panel exists to catch.
3. Should TX timeout live in plugin settings or engine state? It is in
   `/api/state` as `txTimeoutSec`, which argues for the engine, but no
   controller member exists for it.
4. Does the engine expose a mic-level tap outside transmit? The panel wants the
   mic meter live on receive so dead audio is found *before* keying;
   `/api/tx-audio-suite/preview` suggests a path exists.
5. Peak-hold behaviour on the power meter — worth having, or noise?

## 4. Rejected: UI calling the REST API directly

A plugin UI module runs inside the Zeus app origin, so this would work today
with no contract change at all:

```js
await fetch('/api/tx/drive', {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ percent: 20 }),
});
```

**Rejected.** It routes around the capability model entirely. A plugin could key
the transmitter without ever declaring `ControlRadio`, and the user would never
be asked to grant it. The manifest's whole promise is that the host knows what a
plugin can do before it runs; a plugin that can transmit while claiming no
capabilities makes that promise false.

Recorded here so it is not rediscovered later as a clever idea.

## 5. Meters

Three horizontal LED bars. The top one does double duty — signal strength on
receive, forward power on transmit — because the number you need on receive and
the number you need on transmit never matter at the same moment.

| Bar | State | Scale | Green / amber / red |
|---|---|---|---|
| Signal | RX | S1 – S9+60 | to S9 / S9+20 / S9+40 up |
| Power | TX | 0 – 10 W | to 70% / 70–90% / 90% up |
| Mic | always | −48 – 0 dBFS | to −12 / −12..−3 / −3 up |
| SWR | TX | 1.0 – 4.0+ | to 1.5 / 1.5..2.5 / 2.5 up |

The SWR bar dims on receive: SWR without forward power is meaningless, and a
bar showing *something* would be a lie. The mic bar does **not** dim — see open
question 4.

Red on SWR is set at 2.5 because that is where most PAs fold back, and the HL2
has no protection worth relying on.

## 6. Backend routes

Seven setters and one subscription, over `api.callBackend`:

| Route | Payload | Maps to |
|---|---|---|
| `tx.mox` | `{ on }` | `SetMoxAsync` |
| `tx.tune` | `{ on }` | `SetTuneAsync` |
| `tx.drive` | `{ percent }` | `SetDrivePercentAsync` |
| `tx.source` | `{ source }` | `SetTxAudioSourceAsync` |
| `tx.micgain` | `{ db }` | `SetMicGainDbAsync` |
| `tx.filter` | `{ low, high }` | `SetTxFilterAsync` |
| `tx.guard` | `{ maxPct, timeoutS, levelerDb }` | settings + controller |
| `tx.telemetry` | subscribe | `ITxTelemetry.Updated` |
