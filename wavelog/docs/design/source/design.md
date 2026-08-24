# Wavelog synchroniser — design and TDD plan

Keep Zeus's own logbook and a Wavelog instance in step, both directions.

- Framework reference: [`docs/plugin-framework-how-to.md`](../../../../docs/plugin-framework-how-to.md)
- Target: `wavelog.on8st.be` — Wavelog (Cloudlog fork) in Docker on the
  multihost, loopback `127.0.0.1:8086` behind Caddy
- Plugin id: `on8st.wavelog`

---

## 1. The constraint that determines the design

The framework has **no "QSO logged" event** (how-to §11). Nothing tells a plugin
that a contact was made.

The obvious way round that is `ILogbookPluginV2` — the storage seam, and the
only interface in the contracts that sees every QSO. This design took that route
first, and then left it.

### Why the logbook seam was the wrong seam

Implementing it means *becoming* the logbook: fourteen methods over a store, and
from then on the operator's contacts live in this plugin's database. Everything
downstream inherits three problems that no amount of care inside the plugin
fixes.

- **The consumer cannot be verified.** `ILogbookPlugin` is never called anywhere
  in the engine repository — Zeus Link calls it, and Zeus Link is proprietary.
  Whether it calls a logbook plugin at all, when, whether a fallback to the
  built-in exists, what happens on uninstall with entries in it: none of that is
  knowable from source. The whole design rested on it.
- **Uninstall becomes the operator's problem.** A plugin that holds the log is a
  plugin that cannot be casually removed.
- **It rebuilds what already works.** Browsing, sorting, editing, ADIF, QSL and
  the export path all exist and are correct. Reimplementing their storage buys
  nothing an operator can see.

### What replaces it

The native logbook plugin — `org.openhpsdr.logbook` — stores the published
contract record, `LogbookEntrySnapshot`, in a collection called `entries` inside
`zeus-logbook.db`, using LiteDB's default mapper. All three facts were read out
of its GPL assembly, not guessed.

So there is nothing to migrate and no second copy to keep honest. **Attach to
that file as a second handle and synchronise it.**

```
Zeus Link ──► native logbook plugin ──► zeus-logbook.db  ── entries
                                              ▲   │
                                              │   │ scan every 30s
                              insert / confirm│   ▼
                                        ┌─────┴──────────┐
                                        │  synchroniser  │── wavelog_sync
                                        └───────┬────────┘
                                                │ outbox, retry
                                                ▼
                                             Wavelog
```

Two conditions make this safe, and both are tested rather than assumed:

- **Both handles must open `Connection=shared`.** The reference does. Two
  `Direct` handles open without error and then silently diverge — no exception,
  two different views of the operator's log.
- **Our bookkeeping must not join their document.** Where a QSO came from and
  whether it has been uploaded live in a *separate collection*, `wavelog_sync`.
  Adding fields to the stored QSO would leak them into ADIF exports through
  `AdifFields`, and a round-trip through the reference's own code could drop
  them.

Uninstall this and the log is untouched, because it was never ours. That is the
whole argument for the reframe.

### Polling, and why it is not a compromise

With no event, new work is found by absence: an entry with no row in
`wavelog_sync` has not been dealt with. That is a scan every thirty seconds.

It is the only mechanism available, and it happens to be the better one anyway —
a station with years of contacts logged before the plugin existed has its whole
backlog picked up on the first scan, rather than the plugin quietly starting
from now.

### The rule that survives from the first design

> Wavelog must never be able to fail, delay, or alter a QSO.

Under the logbook design that was a rule to be held: the write path went through
the plugin, so `CreateAsync` → POST → return had to be refused deliberately.
Here it is structural. Zeus writes the contact through its own plugin; this code
is not on that path at all and could not block it if it tried.

The network stays strictly downstream:

```
Zeus logs ──► entries ──► [scan] ──► outbox ──► pump ──► Wavelog  (retry, backoff)
```

**The outbox is still the heart of this plugin**, not the HTTP call. It is also
where the interesting bugs live, which is why it is tested first.

### One alternative, explicitly rejected

Wavelog could *be* the store, queried on every browse. Don't. Cloudlog-family
APIs are write-oriented, browsing is a UI path, and it would put the network
between the operator and their own log. Zeus's database stays the source of
truth; Wavelog is the meeting point with everything else.

## 2. Shape

```
Domain — pure, no I/O                    ← most tests live here
  AdifMapper       LogbookEntrySnapshot → ADIF record
  AdifParser       ADIF text            → records
  RetryPolicy      attempt + failure    → delay | dead-letter
  SyncState        the dedup key Wavelog compares on

Ports
  IWavelogTransport   post / get contacts / station info / radio
  IOutbox             Enqueue / Lease / Ack / Fail
  ICursorStore        where the pull cursor lives
  IClock

Adapters
  HttpWavelogTransport   System.Net.Http, stub-handler tested
  LiteDbOutbox           temp-file tested
  LiteDbCursorStore
  ZeusLogbookDb          the second handle on Zeus's own logbook
  SystemClock

Plugin
  WavelogSyncPlugin : IZeusPlugin, IBackendPlugin
```

Note what is *not* in that last line: no `ILogbookPluginV2`. The plugin declares
a backend and a panel, and nothing else.

Dependencies stay minimal on purpose: everything except `Zeus.Plugins.Contracts`
and the BCL is loaded from the plugin's own directory (how-to §10), so
`System.Net.Http` is preferred over any third-party client. LiteDB is the one
exception, and is not a choice — it is the format the file is already in.

## 3. Manifest

The file is **`plugin.json`**. Prose in the how-to suggested `manifest.json`;
every GPL sample plugin the registry distributes ships `plugin.json`, and the
host reads that name. A plugin with the wrong filename is simply never
discovered, and nothing in the C# would say so — which is why a packaging test
asserts it.

```jsonc
{
  "schemaVersion": 1,
  "id": "on8st.wavelog",
  "name": "Wavelog Synchroniser",
  "version": "0.1.0",
  "license": "GPL-2.0-or-later",
  "sdk":        { "abi": 1, "minVersion": "1.4.0" },
  "entrypoint": { "assembly": "Zeus.Plugin.Wavelog.dll",
                  "type": "Zeus.Plugin.Wavelog.WavelogSyncPlugin" },
  "capabilities": ["NetworkAccess"],
  "permissions":  { "network": true },
  "ui": { "modules": ["ui/wavelog.es.js"],
          "panels": [
            { "id": "wavelog.config", "title": "Wavelog Sync",
              "slot": "workspace.tools", "icon": "Upload",
              "category": "tools" } ] }
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
| `POST /api/plugins/on8st.wavelog/config` | set them (`PUT` also accepted) |
| `POST /api/plugins/on8st.wavelog/test` | one round-trip against the instance |
| `GET /api/plugins/on8st.wavelog/status` | logbook size, pending / failed counts, cursor, last error |
| `POST /api/plugins/on8st.wavelog/retry` | re-queue the dead-letter items |
| `POST /api/plugins/on8st.wavelog/resync` | full reconcile; `{"dryRun": true}` reports without writing |

`POST` as well as `PUT` on `config` is not tidiness: the sample panels only ever
call `GET` and `POST` through `api.callBackend`, so `PUT` alone would leave the
panel unable to save.

The panel (§11) is a form over exactly these, which is why it could be written
without touching the C#.

**Configuration is re-read, not cached authoritatively.** Zeus owns the settings
store and can rewrite a plugin's whole collection without telling it — that is
how profile snapshot and restore work. `PluginSettingsChanged` exists but sits on
the host's own store and is not on `IPluginContext`, so a plugin cannot
subscribe. With no push available, a cached copy would leave the plugin talking
to the old instance with the old key until restart. A 30-second TTL closes it.

## 5. TDD order

Start where the risk is, not where the interfaces are. Each numbered group is
red-first.

**1 · `AdifMapperTests` — pure, table-driven.**
Frequency to MHz at six decimals · band derivation · mode and submode split ·
UTC handling · RST defaults · **optional fields omitted rather than emitted
empty** · `<eor>` · awkward characters in a comment · callsign casing ·
**lengths in UTF-8 bytes, not characters**.

**2 · `AdifParserTests` — the reading half.**
Length-prefixed fields · a wrong length is a format error, not a silent
truncation · trailing material without `<EOR>` is dropped.

**3 · `OutboxTests` — the ones that catch real failures.**
Enqueue survives a restart · a lease is exclusive · ack removes · nack
reschedules with backoff · **an item in flight when the process dies is
redelivered, not lost** · poison items dead-letter after N attempts.

**4 · `RetryPolicyTests` — the distinction that matters.**
`401`/`403` dead-letters *immediately*: the key or station profile is wrong and
retrying forever only hides it. `5xx` and timeouts back off. `400` dead-letters
with the response body kept for the status endpoint. A `200` carrying HTML is a
proxy error page, not a success.

**5 · `TransportTests` — stub `HttpMessageHandler`.**
Correct URL and body · a non-JSON response is a failure, not a success · status
surfaced.

**6 · `PumpTests` — fake clock, fake transport.**
Drains in order · backs off while the instance is down · recovers and catches
up · never loses an item.

**7 · `ZeusLogbookDbTests` — attaching to somebody else's database.**
A QSO written by a *separate handle* is visible without reopening · timestamps
come back UTC, not local · an entry with no sync row is unseen, and stops being
unseen once tracked · **our fields never appear in their document** · a
confirmation changes the confirmation and nothing else · uninstalling would
leave the log intact.

These use `NativeLogbook`, a stand-in for Zeus's own plugin: its own
`LiteDatabase`, opened the way the reference opens it, writing contract records
into `zeus-logbook.db`. Every test starts from a log this plugin did not create,
which is the only way the shared-mode claim can be held at all.

**8 · `NeverInTheWayTests` — the property the reframe is worth having.**
The scan never touches the network · a QSO logged while Wavelog is down is still
the operator's QSO · an unconfigured plugin queues nothing **and forgets
nothing**, so the backlog goes up the day a key is pasted in · the same QSO
noticed twice is queued once · a backlog logged before the plugin existed goes
up on the first scan.

**9 · `SyncTests` — the pull half, and the trap.**
An imported QSO **does not enter the outbox**, and is not queued by the next
scan either · a pulled QSO shows up in *Zeus's own* logbook · the cursor advances
to the returned `lastfetchedid` and never regresses · a confirmation sweep
updates QSL fields on an existing entry without duplicating it · a dry run sees
a gap in a logbook it has never scanned · full resync is idempotent and never
deletes · a QSO in a profile that is not selected is not imported.

**10 · `PackagingTests` — what actually ships.**
The manifest is `plugin.json` · the entrypoint type exists · every declared UI
module is in the output · `LiteDB.dll` and the deps file are present ·
`Zeus.Plugins.Contracts.dll` is **not**.

All of it runs against `tools/FakeWavelog`, a local stand-in implementing the
endpoints and semantics read out of Wavelog's own source. Integration tests
against a real instance come last and live in a separate opt-in project, so the
default run needs no network, no radio, and no live server.

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

**Imported QSOs must never be pushed back.** They land in the same collection
everything else lands in, so nothing about their shape distinguishes them —
only the sync row this plugin writes beside them, marked `wavelog`. Without
that mark, the next scan sees a contact it has not queued, queues it, and a full
resync enqueues the entire imported log for push-back: thousands of no-op
inserts hammering Wavelog to achieve nothing, while the outbox churns and the
status endpoint reports a backlog that never means anything.

It has to hold at two moments, not one — immediately, and again on the next
scan thirty seconds later. Both are tested.

This is the single most important test in §5.

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

### Phase 1 — synchronise

The product. Everything an operator needs.

| Milestone | Contains |
|---|---|
| 1a — attach | second handle on `zeus-logbook.db` · sync collection · scan for unseen |
| 1b — push | outbox · pump · retry policy · `POST /api/qso` |
| 1c — pull | incremental `fetchfromid` loop · confirmation sweep · profile selection |
| 1d — repair | full resync, dry run first |
| 1e — radio | live rig state to `POST /api/radio` |

Each milestone adds one loop, and each is independently revertible by a config
toggle.

### The gate — phase 1 must be proven before phase 2 starts

The reframe removed the largest unknown: nothing now depends on what Zeus Link
does with a logbook plugin. What remains has to be established by running it.

Phase 2 adds network to a UI path. Doing that on top of an unproven attachment
compounds two risks that are much easier to diagnose apart.

So phase 2 does not start until all of these hold:

- **the plugin actually attaches** — `/status` reports the same number of QSOs
  the logbook view shows. If it reports zero against a full log, it has opened
  the wrong file, or one side is not in shared mode
- a QSO logged in Zeus **appears in Wavelog** within the poll interval
- a QSO logged **elsewhere** — web UI, WSJT-X, another logger — appears in
  Zeus's own logbook view, browsable and editable like any other
- a **LoTW confirmation** reaches Zeus after the sweep, and changes nothing else
  about the contact
- **full resync dry-run reports zero drift** after a week of normal use
- **uninstalling the plugin leaves the log complete** and exportable

Run it on a **scratch profile first**, then on the real log, and let it soak
under ordinary operating before calling it proven. The dry-run resync is the
cheap weekly check: if it keeps reporting nothing to do, the loops are working.

### Phase 2 — enrichment

`logbook_check_callsign` and the grid checks. Separated from phase 1 because it
has a **different risk profile**: it is the only part that puts the network in
front of the operator, so it needs a cache, a timeout, and a local fallback that
is exercised by tests rather than assumed.

### Phase 3 — the panel

Done, and no longer last. It was deferred while the UI contract looked
unknowable; it turned out to be readable from the GPL sample plugins the
registry distributes (§11).

## 10. Settled during phase 1

**Per-entry upload state.** Open at the start; settled by the reframe. It cannot
live on the QSO, because the QSO is not ours — so `wavelog_sync` carries the
source, the dedup key, the upload time and the last error, keyed by entry id.
The status endpoint reports from it and `retry` works from it.

**Where the plugin's own files live.** The outbox and cursor go in a
`wavelog-plugin` directory *beside* the logbook in the host data directory, not
inside the plugin root — so an uninstall or an upgrade does not take the queue
with it.

**A mapper per database, not `BsonMapper.Global`.** The global one is
process-wide mutable state with a cache that is not safe to populate from
several threads at once; two databases opened concurrently can hand back a
half-built entity mapping, which surfaces much later as *"member not found"* on
a field that plainly exists. It showed up as a flaky test, which is the only
reason it was found. A fresh mapper has identical defaults, so the stored
document is byte-for-byte what the reference writes — it just cannot be raced,
or reconfigured by anything else sharing the load context.

**Dates.** LiteDB stores UTC and hands back local. For a logbook that is not
cosmetic: the dedup key is the timestamp to the minute, so an unconverted value
makes Wavelog treat the same contact as a new one. Normalised on every read and
write, with a regression test.

## 11. The panel, and how the contract was learned

`ui.modules` and panel slots are consumed by Zeus Link, which is proprietary.
Its shipped bundle carries an explicit *"may not be … decompiled, disassembled,
or reverse engineered"* clause, so it is not a legitimate source.

The registry is. `https://downloads.zeussdr.com/plugins/registry.json` publishes
sample plugins under **GPL-2.0-or-later**, with source — plugins that exist to
be read. That is the intended documentation, not a workaround, and reading it
answered everything:

- an ES module whose **default export is `register(api)`**
- `api.registerPanel({ id, component })` — and the id must match the manifest's
  panel id, or the panel silently never appears
- `api.callBackend(method, path, body)` returns a `fetch` Response and is
  already prefixed with the plugin's route
- React arrives from the host as a **bare specifier**, which is why the bundle
  ships `zeus-sdk/react.js` as a separate importable module
- real tool panels use `slot: "workspace.tools"` with `category: "tools"`.
  `"settings"` was invented earlier in this design and appears nowhere

The panel is written with `React.createElement` rather than JSX, so the plugin
needs no build step: no npm, no bundler, no lockfile. A packaging test asserts
the registered id matches the manifest, because that failure mode is silent.

## 12. Risks worth stating up front

**You are writing into a collection another plugin owns.** That is the deal the
reframe makes, and it is a real exposure: if a future Zeus renames a field,
changes the mapper or moves the file, this attaches to the wrong thing rather
than failing loudly. The names and the document shape are asserted in tests, so
a change is caught the moment the reference is re-read — but it has to be
re-read. Treat a Zeus upgrade as a reason to run the test suite, not just the
plugin.

**Both sides must open shared.** If a future Zeus switches to `Direct`, the two
handles stop seeing each other with no error at all — the plugin reports a
healthy empty queue while nothing syncs. `/status` reporting the logbook count is
the cheap tell.

**The fake encodes our reading of Wavelog, not Wavelog.** 151 passing tests prove
the plugin does what this document says. They cannot prove this document read the
API right. Only a run against a real instance does that, and it is a gate item,
not a unit test.

**Confirmations are the one place we edit somebody else's record.** Deliberately
the narrowest edit in the plugin — QSL and LoTW fields only, matched on the dedup
key. Worth re-reading if it ever grows.
