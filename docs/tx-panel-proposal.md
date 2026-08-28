# Proposal: a TX Panel plugin

`be.on8st.zeus.plugins.txpanel` — slug `txpanel`, GPL-2.0-or-later.

Checked against station-engine **v2.0.9** (`db764e1`) and zeus-plugins `373fa03`
on `main`. Where this disagrees with the code, the code is right.

Companion: panel design and working mockup —
<https://claude.ai/code/artifact/5fcf21fe-a747-43c5-9b82-b00cb5b9d6f3>.
This document —
<https://claude.ai/code/artifact/3eedff50-faef-426f-b65c-6b924f4c1ff8>.

---

## 1. What it is

A transmit panel that puts the settings which can *silently stop you
transmitting* on one face, with the meters that say whether any of it reached
the air. Nine controls in three groups, three LED bar meters, one verdict line.

It is not a replacement for the TX audio suite and does not try to be. The
split it proposes:

> If a setting can stop the radio transmitting **entirely**, it belongs on the
> face. If it only changes how the transmission **sounds**, it stays where it
> is.

All nine fail silently when wrong. PureSignal, CFC, the VST chain, two-tone,
filter phase and window, the MOX delays — none of them do.

### Why it is worth building

A live Hermes-Lite 2 keyed for eight seconds and radiated nothing. PA biased at
178 mA and heating at 0.45 °C/s, TX FIFO clean at 381 pkt/s with zero recovery
events, network fine across a routed hop. Drive was 0% and the IQ buffer handed
to the radio was all zeros:

```
mox=True  peak=0/32767  mean=0  firstI=0  firstQ=0  drv=0
```

Every screen looked healthy. Finding it took a packet capture, a PA bias read
over port 1025, and a grep through four rotated log files. A panel that shows
what is actually leaving the radio turns that into a glance.

---

## 2. Read this before scaffolding

**`IRadioController` exposes three members.** From
`Zeus.Plugins.Contracts/IPluginContext.cs`:

```csharp
Task SetFrequencyAsync(long hz, CancellationToken ct = default);
Task SetModeAsync(string mode, CancellationToken ct = default);
Task SetMoxAsync(bool keyed, CancellationToken ct = default);
```

Of the nine controls this panel needs, **one** is reachable. Of the five meter
readings, **none** are. `IRadioStateReader` carries `FrequencyHz`, `Mode`,
`Band`, `Mox` and their change events — no drive, no levels, no metering.

The plugin is **not buildable against ABI 1 / SDK 1.4.0 as the contracts
stand.** `Zeus.Plugins.Contracts` has to grow first.

This is not a case where the UI can route around the backend. Plugin UI modules
reach the host through `api.callBackend` into their own plugin — `ubersdr` and
`wavelog` both do, and neither `fetch`es the REST API. The C# surface is the
binding constraint.

### The gap

| Panel element | Today | Needs |
|---|---|---|
| PTT / MOX | available | `SetMoxAsync(bool)` |
| Tune | missing | `SetTuneAsync(bool)` |
| Drive | missing | `SetDrivePercentAsync(int)` |
| TX audio from | missing | `SetTxAudioSourceAsync(string)` |
| Mic gain | missing | `SetMicGainDbAsync(double)` |
| TX filter | missing | `SetTxFilterAsync(int,int)` |
| Max drive | missing | `SetDriveMaxPercentAsync(int)` |
| TX timeout | missing | plugin settings, not radio state |
| Leveler max gain | missing | `SetLevelerMaxGainDbAsync(double)` |
| Signal / power meter | missing | `ITxTelemetry.Updated` |
| Mic level meter | missing | `ITxTelemetry.Updated` |
| SWR meter | missing | `ITxTelemetry.Updated` |
| On the wire | missing | `ITxTelemetry.Updated` |

**None of this is new DSP.** The engine already computes every one of these and
writes them to the log each second — `p1.tx.rate` carries `peak` and `drv`,
`wdsp.rx.meter` carries `sAv`, and the HL2's port 1025 carries PA temperature
and forward/reflected power. This is plumbing an existing signal out to
plugins, not measuring anything new.

---

## 3. Proposed contract additions

Default interface members throughout, matching the convention already used for
`HostDataDirectory` and `RadioController` — existing hosts and test doubles keep
compiling untouched, so **no ABI bump**. Ship as SDK `1.5.0`.

```csharp
// Zeus.Plugins.Contracts/IPluginContext.cs

public interface IRadioController
{
    Task SetFrequencyAsync(long hz, CancellationToken ct = default);
    Task SetModeAsync(string mode, CancellationToken ct = default);
    Task SetMoxAsync(bool keyed, CancellationToken ct = default);

    // Transmit path. Defaults are no-ops so a host that does not implement
    // them stays source-compatible on ABI 1.
    Task SetTuneAsync(bool on, CancellationToken ct = default)
        => Task.CompletedTask;
    Task SetDrivePercentAsync(int percent, CancellationToken ct = default)
        => Task.CompletedTask;
    Task SetDriveMaxPercentAsync(int percent, CancellationToken ct = default)
        => Task.CompletedTask;
    Task SetMicGainDbAsync(double db, CancellationToken ct = default)
        => Task.CompletedTask;
    Task SetLevelerMaxGainDbAsync(double db, CancellationToken ct = default)
        => Task.CompletedTask;
    Task SetTxAudioSourceAsync(string source, CancellationToken ct = default)
        => Task.CompletedTask;
    Task SetTxFilterAsync(int lowHz, int highHz, CancellationToken ct = default)
        => Task.CompletedTask;
}

public interface IRadioStateReader
{
    long FrequencyHz { get; }
    string Mode { get; }
    string Band { get; }
    bool Mox { get; }

    int DrivePercent => 0;
    double MicGainDb => 0;

    /// <summary>Null when the host publishes no transmit telemetry.</summary>
    ITxTelemetry? Telemetry => null;

    event Action<long> FrequencyChanged;
    event Action<string> ModeChanged;
    event Action<bool> MoxChanged;
}

public interface ITxTelemetry
{
    event Action<TxFrame> Updated;
}

/// <summary>One tick of transmit telemetry. WirePeak is the one that matters
/// most: it is what is actually being handed to the radio.</summary>
public readonly record struct TxFrame(
    double SignalDbm,      // receive S-meter
    double MicPeakDbfs,    // input level, -inf when silent
    int    WirePeak,       // 0..32767 -- zero means nothing is going out
    double ForwardWatts,
    double ReflectedWatts,
    double PaTempC);
```

### Why `WirePeak` earns its place

It is the single field that separates *the radio is transmitting* from *the
radio is keyed*. Everything else — PA current, temperature, FIFO depth, packet
rate — reads healthy in both cases. No plugin can see it today.

---

## 4. Plugin shape, once contracts land

### Scaffold

```sh
cd ~/Repos/on8st/zeus-plugins
./tools/new-plugin.sh txpanel "TX Panel"
```

The slug must match `^[a-z][a-z0-9]*$` — `txpanel`, not `tx-panel`, or the
generated id is rejected.

### plugin.json

```json
{
  "schemaVersion": 1,
  "id": "be.on8st.zeus.plugins.txpanel",
  "name": "TX Panel",
  "version": "0.1.0",
  "author": "on8st",
  "description": "A basic transmit panel: PTT, tune and drive, the audio source and gain feeding them, and meters that show whether any of it reached the air.",
  "homepage": "https://github.com/on8st/zeus-plugins",
  "license": "GPL-2.0-or-later",
  "sdk": { "abi": 1, "minVersion": "1.5.0" },
  "entrypoint": {
    "assembly": "Zeus.Plugin.Txpanel.dll",
    "type": "Zeus.Plugin.Txpanel.TxPanelPlugin"
  },
  "capabilities": [ "ControlRadio", "ReadRadioState", "PersistSettings" ],
  "permissions": {
    "network": false, "fileSystemRead": false, "fileSystemWrite": false
  },
  "ui": {
    "modules": [ "ui/txpanel.es.js" ],
    "panels": [{
      "id": "txpanel.main",
      "title": "TX Panel",
      "icon": "Radio",
      "slot": "workspace.tools",
      "category": "tools"
    }]
  }
}
```

Slot and icon are copied from Wavelog. A transmit-specific slot may exist and
would be a better home — worth checking before this ships.

### Backend routes

The UI module registers its panel and calls back over `api.callBackend`, as
`ubersdr` does. Seven setters and one subscription:

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

### Meters

Three horizontal LED bars. The top one does double duty — signal strength on
receive, forward power on transmit — because the number you need on receive and
the number you need on transmit never matter at the same moment.

| Bar | State | Scale | Green / amber / red |
|---|---|---|---|
| Signal | RX | S1 – S9+60 | to S9 / S9+20 / S9+40 |
| Power | TX | 0 – 10 W | to 70% / 90% / above |
| Mic | always | −48 – 0 dBFS | to −12 / −12..−3 / above −3 |
| SWR | TX | 1.0 – 4.0+ | to 1.5 / 1.5..2.5 / above 2.5 |

The SWR bar dims on receive, because SWR without forward power is a meaningless
number and a bar showing *something* would be a lie. The mic bar does **not**
dim — dead audio is worth finding before you key, and the engine already has a
preview path (`/api/tx-audio-suite/preview`).

SWR needs the N2ADR filter board configured; the HL2 mainboard has no
directional coupler at all.

---

## 5. Order of work

| Step | Where | What |
|---|---|---|
| 1 | station-engine | Add the controller members, reader properties and `ITxTelemetry` as default interface members. No ABI bump; ship as SDK 1.5.0. |
| 2 | station-engine | Implement `TxFrame` from values already logged in `p1.tx.rate` and `wdsp.rx.meter`, plus HL2 port-1025 telemetry. |
| 3 | zeus-plugins | `new-plugin.sh txpanel "TX Panel"`, then the backend routes. |
| 4 | zeus-plugins | UI module: three LED bar meters, nine controls, the verdict line. |
| 5 | zeus-plugins | Add the row to the repo README table, next to Wavelog. |

---

## 6. The shortcut, and why not

A plugin UI module runs inside the Zeus app origin, so this works today with no
contract change at all:

```js
await fetch('/api/tx/drive', {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ percent: 20 }),
});
```

**Reject it.** It routes around the capability model entirely. A plugin could
key the transmitter without ever declaring `ControlRadio`, and the user would
never be asked to grant it. The whole point of the manifest is that the host
knows what a plugin can do before it runs — a plugin that can transmit while
claiming no capabilities makes that promise false.

Worth naming explicitly here so nobody rediscovers it later as a clever idea.
