# Prompt — Wavelog plugin, phase 1: log and radio

Paste this into a session started in `~/Repos/on8st/zeus-plugins/wavelog`.

---

Build **phase 1** of the Wavelog logger plugin for the Zeus SDR station engine.

## Read first, in this order

1. `../docs/design/source/design.md`
   — the design. It is the specification; follow it. Where you disagree with it,
   say so before writing code rather than quietly diverging.
2. `~/Repos/on8st/station-engine/docs/plugins-how-to.md` — how the plugin
   framework works. §10 and §11 in particular.
3. `~/Repos/on8st/station-engine/Zeus.Plugins.Contracts/` — the interfaces you
   implement. Read them; do not infer them from the design doc.

Wavelog's API was verified against `~/Repos/on8st/wavelog` at `af32561`. If you
need a detail the design doc does not state, read that source rather than
guessing — and record what you found.

## Scope

Phase 1 is **log sync and live rig state**, in five milestones. Each is a
separate commit and each leaves the plugin working.

| Milestone | Contains |
|---|---|
| **1a — store** | `ILogbookPluginV2` over a local store · ADIF mapper · export and import |
| **1b — push** | outbox · pump · retry policy · `POST /api/qso` |
| **1c — pull** | incremental `fetchfromid` loop · confirmation sweep · profile selection |
| **1d — repair** | full resync, dry run first |
| **1e — radio** | live rig state to `POST /api/radio` |

**1a must be shippable on its own** — a working logbook that syncs nothing.
Stop and check in after 1a before continuing.

## Out of scope — do not build these

- **No UI panel.** The contract belongs to Zeus Link and is not in the engine
  repository. Configuration is over HTTP endpoints only. Do not attempt to
  learn the panel contract by reading the shipped product bundle: it carries an
  explicit no-reverse-engineering clause.
- **No `logbook_check_callsign` enrichment.** That is phase 2, deliberately
  separated because it puts the network on a UI path.
- **No changes inside `~/Repos/on8st/station-engine`.** It is upstream's tree,
  kept clean. Reference it, never write to it.

## How to work

**TDD, strictly. Red first.** The order is chosen so the risk is covered before
the plumbing:

1. `AdifMapperTests` — pure, table-driven
2. `OutboxTests` — restart survival, exclusive lease, redelivery after a crash
   mid-flight, dead-letter after N
3. `RetryPolicyTests` — `401`/`403` dead-letter immediately, `5xx` and timeouts
   back off, `400` dead-letters with the body kept
4. `WavelogClientTests` — stub `HttpMessageHandler`
5. `PumpTests` — fake clock, fake transport
6. `LogbookFacadeTests` — `CreateAsync` returns without touching the transport;
   `CreateAsync` still succeeds when the transport throws
7. `SyncTests` — see the traps below
8. `ConfigEndpointTests` — `GET` never echoes the key

Integration tests against a real database or a real Wavelog go in a **separate,
opt-in project**. The default `dotnet test` must need no network, no database
and no radio.

## Traps — each of these has a test in the list above

- **Imported QSOs must bypass the outbox.** Import is a different write path
  from `CreateAsync`. Without this, a full resync enqueues the entire log for
  push-back: thousands of no-op inserts, an outbox that churns, and a status
  endpoint reporting a backlog that never means anything. This is the single
  most important test in the phase.
- **Send the `time_on` you stored.** Wavelog dedupes on callsign + time *to the
  minute* + band + mode + station. 12:00:59 re-sent as 12:01:00 is a new QSO.
- **Mode must match exactly** — uppercased, submode separate. A naming mismatch
  produces duplicates rather than collisions.
- **Pull from many station profiles, push to one.** `station_id` accepts an
  array; `station_info` lists what the key can reach. A QSO under an unselected
  profile is invisible permanently, not late.
- **`fetchfromid` sees inserts only.** Confirmations arrive as *updates* and
  keep their key, so they need the separate `qsl_filter` sweep.
- **Never ship `Zeus.Plugins.Contracts`** in the plugin output — the host
  resolves it from the default load context. Reference it with
  `<Private>false</Private>`.

## Constraints

- .NET 10 (`net10.0`), matching the engine. The SDK is at `~/.dotnet` and is
  **not on the default PATH** — `export PATH="$HOME/.dotnet:$PATH"`.
- Manifest: `schemaVersion: 1`, `sdk.abi: 1`, `entrypoint.assembly` a plain
  `.dll` filename. Capabilities `NetworkAccess` and — for 1e — `ReadRadioState`.
- The API key lives in `ctx.Settings`, never in the manifest, and is never
  returned by `GET …/config`.
- Keep dependencies minimal: everything except the contracts and the BCL ships
  in the plugin's own directory. Prefer `System.Net.Http`.
- Licence GPL-2.0-or-later, matching the contracts.
- `/api/radio` needs a **write-permission** API key; read-only keys are
  rejected.

## Definition of done for phase 1

- `dotnet test` green, with no network, database or radio
- every trap above has a failing-first test that now passes
- the plugin loads in the engine and appears in `GET /api/plugins`
- a QSO created through the plugin is durable locally **before** any network
  call is attempted, and survives the transport being down
- `POST …/resync` with `{"dryRun": true}` reports without writing anything
- a `README.md` in `wavelog/` explaining how to configure it over the endpoints

## After phase 1 — do not start phase 2

Phase 2 is gated on phase 1 being **proven inside Zeus**, not merely passing
tests. The criteria are in §9 of the design doc and include things only running
it can answer — whether Zeus Link uses the plugin as its logbook at all, and
how `GetEntriesAsync` performs at real log size.

Report what you built, what you had to decide that the design did not cover, and
anything in the design that turned out to be wrong.
