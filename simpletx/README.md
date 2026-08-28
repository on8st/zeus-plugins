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

- Zeus station-engine with **SDK 1.5.0 or later**. This plugin does not load on
  1.4.0 — see Status.
- A radio whose transmit path the engine can drive. Developed against a
  Hermes-Lite 2 (gateware 74.2, four receivers).
- For the SWR meter and forward power: an **N2ADR filter/IO board**. The HL2
  mainboard has no directional coupler, so without it those two readings have
  no source and the panel dims them.

## Install

```sh
./tools/package.sh            # prints the .zip and its sha256
```

In Zeus: **Features → install local feature**, choose the zip.

## Status

**Builds, tests and packages. Never run against a radio.**

| | |
|---|---|
| Backend | `SimpletxPlugin`, nine routes, clean with warnings as errors |
| Panel | `ui/simpletx.es.js` — three meters, nine controls, verdict line |
| Tests | 31 passing: the verdict table, the limits, and SWR |
| Package | `panel-check` renders, runs effects, clicks, unmounts |

**It needs SDK 1.5.0 and no released engine has it.** The contract additions
this depends on — seven `IRadioController` members, `DrivePercent`,
`MicGainDb`, `ITxTelemetry` and `TxFrame` — live on a `feat/tx-contracts`
branch of a station-engine clone, not in any release. Against 1.4.0 the host
refuses to load this, which is deliberate: the manifest records the dependency
instead of half-working.

Nothing in the engine implements those members yet either, so even on a build
that loads it the controller calls are no-ops and no telemetry arrives. The
panel is honest about that — with no telemetry the verdict is `Unknown` rather
than a guess, and the meters read `—`.

So: everything above the contract boundary is exercised; everything below it
is untested against real hardware.
