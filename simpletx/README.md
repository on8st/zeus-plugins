# Simple TX

A Zeus SDR station-engine plugin that puts the transmit settings which can
*silently stop you transmitting* on one face, with the meters that say whether
any of it reached the air.

It is **not** a replacement for the TX audio suite and owns no DSP. PureSignal,
CFC, the VST chain, two-tone, filter phase and window all stay exactly where
they are. The split is one rule:

> If a setting can stop the radio transmitting **entirely**, it belongs on the
> face. If it only changes how the transmission **sounds**, it stays where it is.

| | |
|---|---|
| **Key** | PTT / MOX, Tune, Drive |
| **Source** | TX audio source, mic gain, TX filter |
| **Guard** | max drive, TX timeout, leveler max gain |
| **Meters** | signal / power, mic input level, SWR — horizontal LED bars |
| **Verdict** | one line saying whether anything is actually going out |

Design and rationale: [`docs/design/source/design.md`](docs/design/source/design.md).
Proposal, including the contract additions this needs:
[`../docs/simple-tx-proposal.md`](../docs/simple-tx-proposal.md).

## What you need

- Zeus station-engine **2.x** — any released build. It talks to the engine's
  own HTTP API, so no SDK change is needed.
- A radio connected in Zeus. The panel reports "no radio" otherwise.

## Install

```sh
./tools/package.sh            # prints the .zip and its sha256
```

In Zeus: **Features → install local feature**, choose the zip.

## Status

**Controls work. Meters do not, and the panel says so.**

| | |
|---|---|
| Nine controls | live, through the engine's own API |
| Diagnosis | drive-at-zero is caught; the rest needs metering |
| Meters | blank, with a caption explaining why |
| Tests | 33 passing |
| Package | `panel-check` renders, runs effects, clicks, unmounts |

### Why it does not use the plugin contracts

`IPluginContext.Radio` and `RadioController` are declared by
`Zeus.Plugins.Contracts` and **never provided**. `PluginManager` resolves them
with `_services.GetService<IRadioStateReader>()` and nothing registers one; a
runtime probe against a live engine with a radio connected returned null for
both. ubersdr found the same and reached the same answer, so this follows it:
the plugin calls the engine's HTTP API from inside the engine process, taking
the port off the engine's own command line.

Every route and payload was read from the engine source — `TxControlEndpoints`,
`TxTimingAndTestEndpoints`, `FilterEndpoints` — rather than guessed.

**This does weaken the capability model, and that is worth saying plainly.**
A plugin reaching the engine's API can key the transmitter whether or not it
declared `ControlRadio`, and the operator is never asked. The manifest here
declares only `NetworkAccess`, because declaring `ControlRadio` would imply a
grant that does nothing. The honest fix is upstream: register the radio
services, so the contracts mean what they say.

### Why the meters are blank

No engine route carries them. The wire peak exists only inside
`Protocol1Client`'s 1 Hz `p1.tx.rate` log line, and forward power, SWR, mic
level and the S-meter reach the product over the binary `/ws` StreamingHub.
Reading that hub is a real piece of work and has not been done.

The bars are drawn dark and read `—` rather than showing a zero-length green
bar, which would claim "measured, and it is nothing" — a different and false
statement on a healthy radio.

One diagnosis survives without any metering, and it is the one that prompted
the plugin: **drive at zero while keyed cannot transmit**, and `/api/state`
reports `drivePct`.

### Not verified

Never run against a radio through this path. The engine was closed before the
HTTP bridge could be exercised end to end.
