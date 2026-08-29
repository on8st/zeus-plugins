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

## 4. How the plugin reaches the radio, and what it costs

### The contracts are a dead end

`IPluginContext.Radio` and `RadioController` are declared and never provided.
`PluginManager` resolves them from DI —
`_services.GetService<IRadioStateReader>()` — and nothing in the engine source
registers either. A runtime probe settled it: with the engine reporting
`status: Connected`, `connectedProtocol: P1`, `endpoint: 192.168.8.2:1024`,
this plugin's context still saw both as null. ubersdr's `EngineRadio` carries
the same finding in a comment, reached independently.

So §3 of the proposal, and the contract additions on the `feat/tx-contracts`
branch, are necessary but **not sufficient**. Adding members to an interface
nobody hands out changes nothing. Registering the services is the larger and
more important ask.

### What it does instead

Calls the engine's own HTTP API from inside the engine process, taking the
port from the engine's command line, exactly as ubersdr does. Every route and
payload was read from `TxControlEndpoints.cs`, `TxTimingAndTestEndpoints.cs`
and `FilterEndpoints.cs`:

| Control | Route | Payload |
|---|---|---|
| PTT | `POST /api/tx/mox` | `MoxSetRequest(bool On)` |
| Tune | `POST /api/tx/tun` | `TunSetRequest(bool On)` |
| Tune drive | `POST /api/tx/tune-drive` | `TuneDriveSetRequest(int Percent)` |
| Drive | `POST /api/tx/drive` | `DriveSetRequest(int Percent)` |
| Max drive | `POST /api/tx/drive-max` | `DriveMaxSetRequest(int Percent)` |
| Mic gain | `POST /api/mic-gain` | `MicGainSetRequest(int Db)` |
| Leveler | `POST /api/tx/leveler-max-gain` | `LevelerMaxGainSetRequest(double Gain)` |
| TX filter | `POST /api/tx-filter` | `TxFilterSetRequest(int LowHz, int HighHz)` |
| Timeout | `POST /api/tx/timeout` | `TxTimeoutSetRequest(int Seconds)` |
| Everything read | `GET /api/state` | — |

`/api/tx/timeout` exists, which closes open question 3: the timeout belongs to
the engine, not to plugin settings. `txAudioSource` is readable from
`/api/state` and has **no setter route**, so the panel shows it read-only.

### What this costs, stated plainly

An earlier version of this document rejected reaching the REST API, on the
grounds that it routes around the capability model. That objection was aimed
at the *panel* fetching cross-origin, and it still holds there — the panel is
served from a different origin and cannot reach the engine anyway. But the
objection applies to the backend doing it too, and it is not answered by the
fact that ubersdr does it first.

The cost is real: a plugin that calls the engine's API can key the
transmitter whether or not it declared `ControlRadio`, and the operator is
never asked to grant anything. The manifest here declares only
`NetworkAccess`, because declaring `ControlRadio` would claim a grant that
does nothing.

This is a weakness in the platform, not a trick worth being pleased about.
The fix is upstream: register the radio services so the capability grant has
something to gate.

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

**None of these read anything today.** No engine route carries a meter: the
wire peak exists only inside `Protocol1Client`'s 1 Hz `p1.tx.rate` log line,
and forward power, SWR, mic level and the S-meter reach the product over the
binary `/ws` StreamingHub. The bars are therefore drawn dark and read `—`,
with a caption saying why — a zero-length green bar would claim "measured, and
it is nothing", which is false on a healthy radio.

Reading the `/ws` hub against its `WireContract` is the way to bring them
back, and is not done.

The scales above are the intended mapping, kept here because the code that
implemented them was removed rather than left dead.

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
