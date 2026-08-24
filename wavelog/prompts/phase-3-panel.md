# Prompt — Wavelog plugin, phase 3: the config panel

Paste this into a session started in `~/Repos/on8st/zeus-plugins/wavelog`.

> **Done.** The blocking condition below was met: the registry publishes GPL
> sample plugins with source, which is a legitimate reading of the UI contract.
> The panel is built — `src/Zeus.Plugin.Wavelog/ui/wavelog.es.js`. Kept for the
> record of why it was deferred.

**This phase is blocked on external information.** Check the unblocking
condition below before starting.

---

Build **phase 3** of the Wavelog logger plugin: the configuration panel in the
Zeus Link UI.

## Why this is last

The panel adds **no capability**. Phases 1 and 2 are fully usable over the HTTP
endpoints; the panel is a form over them. It is last because it is the only part
that depends on something outside our control, and sequencing it here means the
only phase that can stall is the one that adds nothing.

## The blocker, and what unblocks it

`ui.modules` and panel slots are consumed by **Zeus Link**, which is proprietary
and not in the engine repository. Its shipped bundle carries an explicit *"may
not be … decompiled, disassembled, or reverse engineered"* clause.

**Do not attempt to reconstruct the contract by reading the product bundle.**
That is not a shortcut we take, regardless of how convenient it would be.

Legitimate unblocking, in order of preference:

1. **Upstream answers.** Issues are enabled on `Zeus-SDR/station-engine` and the
   one prior issue was handled.
2. **A registry plugin publishes its source** —
   `https://downloads.zeussdr.com/plugins/registry.json`.
3. **SDK documentation** accompanying `sdk.minVersion 1.4.0`.

One legitimate inference already exists, from file layout alone: the bundle
ships `wwwroot/zeus-sdk/react.js` and `react-jsx-runtime.js` as separate
importable modules — which is what you do when third-party modules are meant to
import your framework rather than bundle their own. Expect to import React from
the host, not to bundle it.

**If none of the three routes has produced the contract, stop and say so.** Do
not start.

## Scope, once unblocked

A form over the endpoints that already exist:

| Panel section | Endpoints |
|---|---|
| Connection | `GET`/`PUT …/config`, `POST …/test` |
| Station profiles | `GET …/profiles`, selection persisted via config |
| Sync status | `GET …/status` — pending, failed, last error, profiles synced |
| Repair | `POST …/resync` with a dry run first, then apply |
| Features | toggles for rig state and, if built, enrichment |

## Requirements

- **The API key is write-only in the UI.** `GET …/config` never returns it; the
  field shows whether one is set, never its value.
- **The dry run is the default path** for resync. Applying is a second,
  deliberate action showing what will change.
- **Status must name the station profiles being synced**, so "why isn't that
  contact here" is answerable at a glance rather than by investigation.
- Nothing in the panel does work the endpoints do not already do. If the panel
  needs a new behaviour, add the endpoint and its tests first.

## How to work

TDD still applies, at the level the UI layer allows. Whatever cannot be tested
should be thin enough that its failure modes are obvious.

## Definition of done

- the panel appears in its declared slot and configures the plugin end to end
- with the panel removed, phases 1 and 2 still work unchanged over HTTP
- the key cannot be read back through any route the panel uses
