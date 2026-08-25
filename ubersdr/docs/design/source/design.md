# UberSDR — design notes

Nothing is decided. This records what has been **verified** and what is merely
assumed, because in this repository the assumptions have been the bugs.

## 1. What UberSDR is

A web-based SDR platform built on `ka9q-radio`, running on RX-888 MkII plus
generic PC hardware, with a network of public receivers serving 20–200
simultaneous listeners each. Source at `madpsy/ka9q_ubersdr`.

**Verified from the project's own site**, not from memory. It exposes an unusual
number of integration surfaces:

| Surface | Notes |
|---|---|
| REST + WebSocket | "Frontend is entirely API driven"; spectrum and decoded data available via API |
| **HPSDR protocol** | via a bridge application (SparkSDR, Thetis compatible) |
| **KiwiSDR emulation** | port 8073, any KiwiSDR client |
| RTL-TCP emulation | port 1234 |
| TCI | network CAT + audio |
| rigctl / flrig / OmniRig | CAT control |
| SoapySDR driver | authenticated, wide IQ |
| MQTT + Prometheus | metrics and decoder data |

## 2. The awkward fact: Zeus may already connect

Zeus is **itself an OpenHPSDR Protocol 1/2 client** — that is what the engine is.
And it already ships:

- **TCI** — 33 source files, with persisted runtime config
- **KiwiSDR** — `KiwiSdrService`, a hosted service, with config taking
  `host:8073`

So three of UberSDR's integration surfaces are ones Zeus speaks natively,
without any plugin. Before writing code, the first job is to find out whether
**Zeus can already receive from an UberSDR instance today** by pointing its
KiwiSDR client at port 8073, or its TCI client at the CAT/audio port, or by
running the HPSDR bridge.

If it can, a plugin that transports audio or IQ is redundant. That question is
answerable in an afternoon with a public UberSDR instance and no code at all.

## 3. What the plugin surface can and cannot do

Read from `Zeus.Plugins.Contracts`, not assumed:

`IPluginContext` offers `PluginId`, `Logger`, `PluginRootPath`,
`HostDataDirectory`, `Settings`, and `Radio` — frequency, mode, MOX, each
readable with a change event. Plus `IBackendPlugin` (HTTP routes), `IUiPlugin`
(panels), and the audio interfaces.

**There is no spot ingestion API.** `Spot` appears nowhere in the contracts, so a
plugin cannot feed Zeus's `SpotManager` — which rules out the otherwise obvious
idea of piping UberSDR's WSJT-X skimmer decodes in as spots. Worth confirming
with upstream before designing around it.

**A plugin cannot retune the radio.** `Radio` exposes frequency and mode as
*readable* state with events; nothing in the contracts sets them.

Those two limits shape everything: a plugin here can *observe* Zeus, *talk to the
network*, *store settings* and *draw a panel*. It cannot drive the radio or feed
the spot pipeline.

## 4. Candidate shapes, none chosen

- **Receiver browser.** A tools panel listing the public UberSDR network, with
  band/mode/quality, and one-click configuration of whichever transport Zeus
  ends up using. Plays to what the plugin surface is actually good at.
- **Decoded-data viewer.** UberSDR's skimmer output over REST/MQTT, shown in a
  panel. Additive precisely *because* it cannot become spots.
- **Bridge supervisor.** If the HPSDR bridge is the route, a plugin that manages
  and monitors it rather than reimplementing it.
- **Nothing.** If Zeus already connects natively and the browsing is comfortable
  on ubersdr.org, the honest answer may be that no plugin is warranted.

## 5. Open questions

1. Which transport does Stan actually want — HPSDR bridge, KiwiSDR emulation,
   TCI, or REST/WebSocket?
2. Does Zeus's existing KiwiSDR client already work against UberSDR's port 8073?
3. Is this for *his own* UberSDR instance, or for browsing the public network?
4. Receive only, or does transmit/CAT matter?
5. Is there a spot-ingestion route for plugins that the contracts do not show?

Nothing gets built until 1 and 2 are answered — 2 is a test, not a discussion.
