# Prompt — Wavelog plugin, phase 2: enrichment

Paste this into a session started in `~/Repos/on8st/zeus-plugins/wavelog`.

**Do not start this phase until the phase-1 gate has been met.** See the bottom
of this file.

---

Build **phase 2** of the Wavelog logger plugin: worked-before enrichment and
new-grid alerts.

## Read first

1. `../docs/design/source/design.md`
   §8 (features) and §9 (phases)
2. The phase-1 code you are extending
3. `~/Repos/on8st/wavelog` @ `af32561` — `application/controllers/Api.php`,
   functions `logbook_check_callsign`, `logbook_check_grid`,
   `logbook_get_worked_grids`

## Scope

Two opt-in features, both **off by default**:

- **Worked-before enrichment** — `GetWorkedSummaryAsync` consults Wavelog, so
  the answer includes contacts logged by *every* app rather than only the local
  store.
- **New-grid alerts** — `logbook_check_grid` and `logbook_get_worked_grids`.

## The one thing that makes this its own phase

This is the **only part of the plugin that puts the network in front of the
operator.** Everything in phase 1 was deliberately arranged so the network sits
downstream of the operator's call; this deliberately breaks that, so it carries
its own discipline:

- **Local answer first, always.** The local store's answer is returned. Wavelog
  is an *enrichment* layered on top — never the blocking source.
- **Short timeout**, and the timeout value is configurable.
- **Cache**, so a repeated lookup of the same callsign does not repeat the call.
- **The local fallback path is exercised by tests**, not assumed. A test must
  prove that a dead Wavelog produces a correct local answer at full speed.
- **Off by default.** The operator turns it on knowing what it costs.

If any of those is inconvenient to build, that is the signal that the feature is
not ready — not a reason to skip it.

## Note on addressing

`logbook_check_callsign` keys on **`logbook_public_slug`**, not `station_id` —
a different addressing scheme from the sync endpoints. It needs its own config
field, and `GET …/config` must surface it distinctly so nobody assumes the
station profile selection covers it.

## How to work

TDD, red first:

1. `WorkedBeforeCacheTests` — a repeated lookup does not repeat the call; entries
   expire
2. `EnrichmentTests` — local answer returned when Wavelog is **down**, at full
   speed; local answer returned when Wavelog is **slow**, respecting the
   timeout; Wavelog's extra contacts merged when it answers in time
3. `GridCheckTests`
4. `ConfigEndpointTests` — the new fields, key still never echoed

## Definition of done

- `dotnet test` green with no network
- both features off by default, each independently toggleable
- a dead Wavelog is indistinguishable from the feature being off, from the
  operator's point of view
- `GET …/status` reports enrichment health separately from sync health, so one
  failing does not disguise the other

## The gate you must not skip

Phase 2 does not start until phase 1 is proven **inside Zeus**, not merely
passing tests. From §9 of the design:

- Zeus Link actually uses the plugin as its logbook
- browsing, sorting, searching, editing and deleting all work through it
- performance is acceptable at real log size
- a QSO logged in Zeus appears in Wavelog; one logged elsewhere appears in Zeus
- a LoTW confirmation reaches Zeus after the daily sweep
- full resync dry-run reports zero drift after a week of normal use
- uninstall and reinstall leaves the log intact and exportable

If you are asked to start phase 2 and cannot confirm these, say so and ask
before writing code.
