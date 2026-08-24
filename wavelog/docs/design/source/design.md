# Wavelog logger plugin — design and TDD plan

Push every logged QSO to a Wavelog instance. Design notes, not yet built.

- Framework reference: [`plugins-how-to.md`](plugins-how-to.md)
- Target: `wavelog.on8st.be` — Wavelog (Cloudlog fork) in Docker on the
  multihost, loopback `127.0.0.1:8086` behind Caddy
- Plugin id: `on8st.wavelog`

---

## 1. The constraint that determines the design

The framework has **no "QSO logged" event** (how-to §11). The only seam that
sees every contact is `ILogbookPluginV2`.

**That seam is the storage port, not the experience.** Its shape is
`CreateAsync` / `UpdateAsync` / `DeleteAsync` / `GetEntriesAsync(skip, take)` /
`GetByIdsAsync` / `GetWorkedSummaryAsync` / ADIF import and export — a
data-access contract. Browsing, sorting, searching, the edit dialogs and the
QSL workflow all stay in Zeus Link and call *through* these methods. You
implement roughly fourteen methods over a store; you rebuild nothing the
operator sees.

Three details make the job smaller than it first appears:

- **There is no search, sort or filter parameter anywhere.** The only listing
  method is `GetEntriesAsync(skip, take)`, so the client filters and sorts
  client-side over what it has pulled. Your store must serve bulk reads
  quickly, but needs no query language and no indexes beyond callsign.
- **`AdifFields` is a `Dictionary<string,string>`** on both the new entry and
  the snapshot, so arbitrary ADIF fields round-trip. Nothing is lost that the
  typed model does not cover.
- **ADIF export and import are part of the contract regardless.** The mapper is
  not Wavelog-specific cost — `ExportAdifAsync` owes it anyway, and the Wavelog
  push reuses it.

What you cannot lean on is the built-in *store*: there is no handle to it on
`IPluginContext`, and it lives in the closed client. Do not open
`zeus-logbook.db` directly either — undocumented schema, and the client may
hold it open.

Since storage is what moves:

> The plugin's first duty is **being a good logbook**. Pushing to Wavelog is a
> side effect that must never be able to fail a QSO.

That kills the obvious shape — `CreateAsync` → POST → return. The Wavelog box
can be rebooting mid-contest; logging must still work. So the write path is
local-first, with the network strictly downstream:

```
CreateAsync ──► store locally ──► enqueue outbox ──► return snapshot
                                        │
                    background pump ────┴──► Wavelog   (retry, backoff)
```

**The outbox is the heart of this plugin**, not the HTTP call. It is also where
the interesting bugs live, which is why it is tested first.

### One alternative, explicitly rejected

Knowing the seam is storage, Wavelog could *be* the store — `GetEntriesAsync`
querying it on every browse. Don't. Cloudlog-family APIs are write-oriented,
`GetEntriesAsync` sits on the browsing path, and it would put the network
between the operator and their own log — the exact failure this section exists
to prevent. Local store as source of truth, Wavelog as mirror.

## 2. Shape

```
Domain — pure, no I/O                    ← most tests live here
  AdifMapper       LogbookEntrySnapshot → ADIF record
  WavelogRequest   config + adif        → url, body
  RetryPolicy      attempt + failure    → delay | dead-letter

Ports
  IWavelogTransport   PostAsync(request, ct) → WavelogResult
  IOutbox             Enqueue / Lease / Ack / Fail
  ILogStore           the logbook itself
  IClock

Adapters
  HttpWavelogTransport   System.Net.Http, stub-handler tested
  LiteDbOutbox           temp-file tested
  LiteDbLogStore
  SystemClock

Plugin
  WavelogLogbookPlugin : IZeusPlugin, ILogbookPluginV2, IBackendPlugin, IUiPlugin
```

Dependencies stay minimal on purpose: everything except `Zeus.Plugins.Contracts`
and the BCL is loaded from the plugin's own directory (how-to §10), so
`System.Net.Http` is preferred over any third-party client.

## 3. Manifest

```jsonc
{
  "schemaVersion": 1,
  "id": "on8st.wavelog",
  "name": "Wavelog Logger",
  "version": "0.1.0",
  "license": "GPL-2.0-or-later",
  "sdk":        { "abi": 1, "minVersion": "1.4.0" },
  "entrypoint": { "assembly": "Zeus.Plugin.Wavelog.dll" },
  "capabilities": ["NetworkAccess"],
  "permissions":  { "network": true },
  "ui": { "panels": [
    { "id": "wavelog", "title": "Wavelog", "slot": "settings",
      "icon": "Upload", "category": "plugins" } ] }
}
```

`NetworkAccess` is declared honestly even though the how-to's §7 shows it is not
enforced. The manifest is what the operator reads.

**The API key never goes in the manifest.** It lives in `ctx.Settings`, which is
plugin-scoped and host-persisted, and the config `GET` endpoint never returns it.

## 4. Endpoints

`IBackendPlugin` receives a route builder already scoped to the plugin, so:

| Route | Purpose |
|---|---|
| `GET /api/plugins/on8st.wavelog/config` | URL, profiles pulled, profile pushed to, **key redacted** |
| `GET /api/plugins/on8st.wavelog/profiles` | `station_info` passthrough — what this key can reach |
| `PUT /api/plugins/on8st.wavelog/config` | set them |
| `POST /api/plugins/on8st.wavelog/test` | one round-trip against the instance |
| `GET /api/plugins/on8st.wavelog/status` | pending / failed counts, last error |
| `POST /api/plugins/on8st.wavelog/retry` | re-queue the dead-letter items |
| `POST /api/plugins/on8st.wavelog/resync` | full reconcile; `{"dryRun": true}` reports without writing |

This is the whole product. The UI panel is a form over these, and is
deliberately last (§7).

## 5. TDD order

Start where the risk is, not where the interfaces are. Each numbered group is
red-first.

**1 · `AdifMapperTests` — pure, table-driven.**
Frequency to MHz at six decimals · band derivation · mode and submode split ·
UTC handling · RST defaults · **optional fields omitted rather than emitted
empty** · `<eor>` · awkward characters in a comment · callsign casing.

**2 · `OutboxTests` — the ones that catch real failures.**
Enqueue survives a restart · a lease is exclusive · ack removes · nack
reschedules with backoff · **an item in flight when the process dies is
redelivered, not lost** · poison items dead-letter after N attempts.

**3 · `RetryPolicyTests` — the distinction that matters.**
`401`/`403` dead-letters *immediately*: the key or station profile is wrong and
retrying forever only hides it. `5xx` and timeouts back off. `400` dead-letters
with the response body kept for the status endpoint.

**4 · `WavelogClientTests` — stub `HttpMessageHandler`.**
Correct URL and body · a non-JSON response is a failure, not a success · status
surfaced.

**5 · `PumpTests` — fake clock, fake transport.**
Drains in order · backs off while the instance is down · recovers and catches
up · never loses an item.

**6 · `LogbookFacadeTests` — these two encode the design decision.**
`CreateAsync` returns **without touching the transport**. `CreateAsync` still
succeeds when the transport throws.

**7 · `SyncTests` — the pull half, and the trap.**
An imported QSO **does not enter the outbox** · the cursor advances to the
returned `lastfetchedid` and never regresses · a confirmation sweep updates QSL
fields on an existing entry without duplicating it · full resync is idempotent,
so running it twice inserts nothing the second time · full resync never deletes ·
two resyncs cannot run at once · a QSO in a profile that is not selected is not
imported, and the status endpoint says which profiles are being synced.

**8 · `ConfigEndpointTests`.**
`GET` never echoes the key · `PUT` validates the URL.

Integration tests — real LiteDB, real `wavelog.on8st.be` — come last and live in
a separate opt-in project, so the default test run needs no network and no
radio.

## 6. Wavelog's API — verified against source

Read from `wavelog/wavelog` at `af32561` (2026-08-09), cloned to
`~/Repos/on8st/wavelog`. Not from memory.

### Write

```
POST /index.php/api/qso
{ "key": …, "station_profile_id": …, "type": "adif", "string": "<record>" }
```

Parses the ADIF and calls `import_bulk(..., $skipDuplicate = true, ...)`.

### The duplicate key — this closes the retry question

```sql
COL_CALL = ?
AND DATE_FORMAT(COL_TIME_ON,'%Y-%m-%d %H:%i') = DATE_FORMAT(?,'%Y-%m-%d %H:%i')
AND COL_BAND = ? AND COL_MODE = ? AND station_id = ?
```

Callsign + time **to the minute** + band + mode + station. Consequences:

- **At-least-once is safe.** A timed-out POST that did land is silently skipped
  on retry. No client-side idempotency key is needed.
- **Send the same `time_on` you stored.** 12:00:59 re-sent as 12:01:00 is a new
  QSO, not a duplicate.
- **Mode must match exactly** — uppercased, compared, submode separate. A
  mode-naming mismatch produces duplicates rather than collisions.

### Read

```
POST /index.php/api/get_contacts_adif
{ "key": …, "station_id": …, "fetchfromid": 0,
  "limit": 500, "output_format": "adif" | "json", "fields": [...] }
```

Returns `{status, lastfetchedid, exported_qsos, adif}` — the cursor for the next
call comes back in the response.

The query behind it (`Adif_data::export_past_id_chunked`) is **source-blind**:

```sql
WHERE station_id IN (?) AND COL_PRIMARY_KEY > ?
ORDER BY COL_PRIMARY_KEY ASC LIMIT ? OFFSET ?
```

No filter on origin. A QSO entered in Wavelog's web UI, uploaded by WSJT-X,
bulk-imported from another logger or posted by any other client gets an
auto-increment key above the cursor and comes back on the next poll. **Wavelog
is the meeting point; Zeus is one writer among several.**

### Station profiles — the real gap

`COL_PRIMARY_KEY` is `int(11) NOT NULL AUTO_INCREMENT`, so the cursor follows
**insertion order, not QSO date**. A bulk ADIF import of 2015 contacts made
today lands above the cursor and is picked up on the next poll. A timestamp
cursor keyed on QSO date would have missed that import silently — this is the
reason to prefer the primary key.

What is *not* picked up is a profile you are not asking for. The query filters
`station_id IN (?)`, so **QSOs imported under a different station profile are
invisible to the sync — permanently, not late.** Importing an old log under an
"Old QTH" or "/P" profile while Zeus syncs "Home" produces no error and no
contacts.

The API already solves it:

- `station_id` **accepts an array**, so one call covers several profiles.
- `station_info` returns every `station_id` and `station_profile_name` the key
  can reach.
- An id the key cannot reach fails loudly: *"Station ID not accessible for this
  API key"*.

So configuration must not take a single profile id. Call `station_info`, show
the operator every profile, and let them tick which to sync — defaulting to all.
Status must name the profiles being synced, so "why isn't that contact here" is
answerable at a glance. Note the asymmetry this makes explicit: **you pull from
many profiles and push to one** (`station_profile_id` on the write call).

### Also available

`logbook_check_callsign` and `logbook_check_grid` — server-side worked-before
checks, so `GetWorkedSummaryAsync` could see contacts logged by other apps
without importing them. Plus `station_info`, `statistics`, `version`, `lookup`,
and an `Api_v2` with a router and rate limiting worth reading before committing
to v1 endpoints.

## 7. Synchronisation

### The limitation that shapes it

`fetchfromid` is the primary key, and **an UPDATE does not change it**. A row
modified after the cursor has passed is invisible to the incremental pull
forever.

That hits precisely the reverse flow that matters: **LoTW and eQSL confirmations
arrive as updates to existing rows.** A QSO pushed on Monday and confirmed on
Friday keeps its original id, so `LotwQslRcvdUtc` would stay empty forever on an
insert-only pull.

Wavelog offers the way out in the same call — `qsl_filter`, mapping to
`COL_LOTW_QSL_RCVD` / `COL_QSL_RCVD` / `COL_EQSL_QSL_RCVD` /
`COL_CLUBLOG_QSO_DOWNLOAD_STATUS` = `'Y'`.

### Two loops, not one

| Loop | Cursor | Catches | Cadence |
|---|---|---|---|
| new QSOs | `fetchfromid` advancing | anything anyone inserted | minutes |
| confirmations | `fetchfromid: 0` + `qsl_filter` | QSL status on any row, any age | daily |

Keep the confirmation sweep cheap with `output_format: "json"` and a narrow
`fields` list.

**Neither catches a plain content edit** made in Wavelog — a callsign corrected
there stays corrected only there. Accepted as a known limitation rather than
solved with a full diff.

### Full resync — the repair button

Incremental is the normal path. The config UI also offers **sync full database**,
for when a gap has appeared: a crash mid-lease, a period of misconfiguration, a
restored backup.

It is cheap because both sides already dedupe — it is the incremental loop with
the cursor set to zero.

- **Both directions, one action.** A gap can be on either side and the operator
  cannot know which. Pull what is missing locally; enqueue what Wavelog lacks.
- **Insert only, never delete.** A QSO deleted in Wavelog but present locally
  stays. "Full sync" must not be read as "make identical".
- **Dry run first.** *"12 in Wavelog not here, 3 here not in Wavelog — apply?"*
- **Resumable and single-flight.** The chunked cursor makes resuming natural;
  two concurrent resyncs must be impossible.
- **It repairs absence, not divergence.** It cannot detect a record present on
  both sides with different content.

### The trap

**Imported QSOs must bypass the outbox.** Import is a different write path from
`CreateAsync`. Without that, a full resync enqueues the entire log for push-back
— thousands of no-op inserts hammering Wavelog to achieve nothing, while the
outbox churns and the status endpoint reports a backlog that never means
anything. This is the single most important test in §5.

## 8. Features — one plugin, opt-in

Wavelog is more than a log backend. One plugin, one API key, one config panel
with toggles — rather than three plugins each holding the same credential. Each
feature independently testable and independently disableable, which matters
because one of them touches a UI path.

| Feature | Endpoint | Default |
|---|---|---|
| Log sync — push, pull, confirmations, resync | `qso` · `get_contacts_adif` | **on** |
| Live rig state | `radio` | off |
| Worked-before enrichment | `logbook_check_callsign` | off |
| New-grid alerts | `logbook_check_grid` · `logbook_get_worked_grids` | off |

### Live rig state

`POST /api/radio` takes `radio`, `frequency`, `mode`, `power`, `timestamp`, plus
`uplink_freq` / `uplink_mode` for satellite work and an optional `cat_url`.

Zeus already has all of it and already exposes it: `ctx.Radio` with
`FrequencyChanged`, `ModeChanged`, `MoxChanged`. A few lines of event handler
make Wavelog's QSO entry form auto-fill the current QRG and mode, and show the
station as live — from any browser, including a phone.

Needs `ReadRadioState` in the manifest, and a **write-permission** API key:
read-only keys are rejected with *"API key does not have write permissions"*.

Completely independent of the logging path, which is why it is cheap.

### Worked-before, and why it stays off by default

`logbook_check_callsign` keys on **`logbook_public_slug`**, not `station_id` — a
different addressing scheme from the sync endpoints, so it needs its own config
field.

The appeal is real: `GetWorkedSummaryAsync` answered by Wavelog sees contacts
logged by *every* app, not just the local store. The catch is that it puts the
network on a UI path — the exact opposite of §1.

So: **local answer first, Wavelog as enrichment**, with a short timeout and a
cache, never as the blocking source. Off unless asked for.

### What Wavelog already does, so Zeus need not

The stronger argument for this whole approach is not any single integration —
it is the surface you inherit:

- **confirmation pipelines** — LoTW, eQSL, Clublog, QRZ, HRDLog, WebADIF, DCL
- **awards and analytics** — awards, gridmap, activated gridmap, distance
  records, zone checker, statistics, timeline
- **QSL management** — cards, printing, labels, postcards, OQRS
- **contest** — contest logging, Cabrillo export, FLE
- **satellite** — satellite, Hamsat, sat timers, AMSAT status

### Deliberately not integrated

`Dxcluster` and `Bandmap` overlap what Zeus already has — its own
`SpotManager`, the TCI spot feed and a `SpotList` frame type. Two spot sources
competing for one screen is worse than one.

## 9. Phases

### Phase 1 — log and radio

The product. Everything an operator needs, configured over HTTP.

| Milestone | Contains |
|---|---|
| 1a — store | `ILogbookPluginV2` over a local store · ADIF mapper · export and import |
| 1b — push | outbox · pump · retry policy · `POST /api/qso` |
| 1c — pull | incremental `fetchfromid` loop · confirmation sweep · profile selection |
| 1d — repair | full resync, dry run first |
| 1e — radio | live rig state to `POST /api/radio` |

1a is shippable on its own: a working logbook that syncs nothing. Each
milestone after it adds one loop, and each is independently revertible by a
config toggle.

### The gate — phase 1 must be proven before phase 2 starts

Phase 1 rests on assumptions the engine repository **cannot verify** (§12): that
Zeus Link calls `ILogbookPluginV2` at all, when it calls it, whether a fallback
to the built-in exists, and what happens on uninstall. None of that is knowable
from source. It has to be established by running it.

Phase 2 adds network to a UI path. Doing that on top of an unproven store
compounds two risks that are much easier to diagnose apart.

So phase 2 does not start until all of these hold:

- **Zeus Link actually uses the plugin as its logbook** — the single biggest
  unknown, and unanswerable any other way
- **Browsing, sorting, searching, editing and deleting all work** through it,
  with the client's UI unchanged
- **Performance is acceptable at real log size** — `GetEntriesAsync` is on the
  browsing path and the client appears to filter client-side, so it may pull
  more than a page
- a QSO logged in Zeus **appears in Wavelog** within the poll interval
- a QSO logged **elsewhere** — web UI, WSJT-X, another logger — appears in Zeus
- a **LoTW confirmation** reaches Zeus after the daily sweep
- **full resync dry-run reports zero drift** after a week of normal use
- **uninstall and reinstall leaves the log intact** and exportable

Run it on a **scratch profile first**, then on the real log, and let it soak
under ordinary operating before calling it proven. The dry-run resync is the
cheap weekly check: if it keeps reporting nothing to do, the loops are working.

If any criterion fails, the gap is worth closing before adding anything on top —
the same discipline as any other migration: the fallback existing is not the
same as the design working.

### Phase 2 — enrichment

`logbook_check_callsign` and the grid checks. Separated from phase 1 because it
has a **different risk profile**: it is the only part that puts the network in
front of the operator, so it needs a cache, a timeout, and a local fallback that
is exercised by tests rather than assumed.

### Phase 3 — the panel

Blocked on external information, not on us — the UI contract belongs to Zeus
Link and is not in the engine repository (§11). Phase 3 starts when upstream
answers or a registry plugin publishes its source. Until then phases 1 and 2 are
fully usable over the endpoints in §4.

Sequencing them this way means the only phase that can stall is the one that
adds no capability.

## 10. Open questions to settle before the first test

**Per-entry upload state.** The contract already has
`UpdateQrzUploadStatusAsync`, so there is precedent for tracking per-QSO upload
status. Mirror it: store `wavelogUploadedAt` and a failure reason, so the status
endpoint can report honestly and `retry` has something to work from.

## 11. The UI panel comes last, and why

`ui.modules` and panel slots are consumed by Zeus Link, which is proprietary and
not in this repository. Its shipped bundle carries an explicit *"may not be …
decompiled, disassembled, or reverse engineered"* clause, so it is not a
legitimate source for reconstructing the contract.

The layout does suggest one thing legitimately: the bundle ships
`wwwroot/zeus-sdk/react.js` and `react-jsx-runtime.js` as separate importable
modules, which is what you do when third-party modules are meant to import your
framework rather than bundle their own.

To learn the contract properly: ask upstream — issues are enabled on
`Zeus-SDR/station-engine` and the one prior issue was handled — or find a plugin
in the registry (`https://downloads.zeussdr.com/plugins/registry.json`) that
publishes its source.

Until then the plugin is **fully usable without a panel**, configured over the
endpoints in §4. That is also why the panel was the least test-covered part of
the plan: deferring it costs nothing.

## 12. Risks worth stating up front

**You are replacing the store, not the logbook UI.** The operator's experience
is unaffected; what moves is the data. Your QSOs live in this plugin's storage.
Before running it against a real log: confirm ADIF export works, and that
uninstalling the plugin leaves the data recoverable. Write the export test
before the import path, not after.

**The engine cannot verify the consumer.** `ILogbookPlugin` is never called
anywhere in the engine repository — Zeus Link calls it. When it is called,
whether a fallback to the built-in exists, what happens if the plugin is
uninstalled with entries in it: none of that is knowable from the GPL source.
Establish it empirically on a scratch profile before trusting it with a real log.
