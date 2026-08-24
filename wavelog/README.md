# Wavelog Synchroniser — Zeus station-engine plugin

Keeps Zeus's **own** logbook and a [Wavelog](https://github.com/wavelog/wavelog)
instance in step, in both directions.

This is not a logbook. It does not implement `ILogbookPluginV2` and never owns
the operator's QSOs — the native logbook keeps doing that, along with browsing,
sorting, searching, editing, ADIF and QSL, all of which already work. This
attaches to the same database file and moves contacts across.

Design: [`docs/design/`](docs/design/) · Prompts: [`prompts/`](prompts/)

**Status: built and tested against a local stand-in; never run inside Zeus.**
See *Before you trust it*.

## What it does

| | |
|---|---|
| **Push** | every QSO Zeus logs is queued and delivered to Wavelog, with retry |
| **Pull** | contacts logged by *any* app — the web UI, WSJT-X, another logger — land in Zeus's logbook |
| **Confirmations** | LoTW / eQSL / QSL status swept back onto the QSOs Zeus already holds |
| **Repair** | full resync, both directions, dry run first |
| **Rig state** | live frequency and mode to Wavelog's `/api/radio` (off by default) |
| **Panel** | configuration and status inside the Zeus workspace |

Nothing here sits on the operator's write path. Zeus logs the contact through
its own plugin; this notices it afterwards and queues it. A contact made while
Wavelog is rebooting is safe the moment Zeus stores it, because Wavelog was
never involved.

## Why a synchroniser rather than a logbook

An earlier cut of this replaced the logbook's storage. Two things pushed it
here.

**It removed the one assumption the engine repository could not settle.** As a
logbook, everything depended on Zeus Link actually calling a logbook plugin —
which the engine source does not say — and on uninstall not stranding the log.
As a synchroniser both questions disappear: uninstall this and the operator's
log is untouched, because it was never ours.

**The data was already the right shape.** The native plugin stores the published
contract record, `LogbookEntrySnapshot`, in `entries` inside `zeus-logbook.db`,
with LiteDB's default mapper — so there is nothing to migrate and no second copy
to keep honest. Two handles on one file see each other's writes as long as both
open `Connection=shared`, which the reference does and this does. (Two `Direct`
handles open without error and then silently diverge, which is why that is
checked by a test rather than trusted.)

Our own bookkeeping — where a QSO came from, whether it has been uploaded — is a
**separate collection** the native logbook never reads. It is deliberately not
part of the stored QSO: fields of ours would leak into ADIF exports through
`AdifFields`, and a round-trip through the reference's own code could drop them.

## How new QSOs are noticed

By polling, because the host offers plugins no "QSO logged" event. Every thirty
seconds the plugin looks for entries with no row of its own. That is not a
compromise chosen over something better — it is the only mechanism available,
and it has one real advantage: a backlog logged before the plugin was installed
goes up on the first scan rather than being silently skipped.

## Configuring it

Through the **Wavelog Sync** panel in the workspace tools. The same surface is
also plain HTTP on the engine's own port, which is what the panel calls:

```sh
BASE=http://127.0.0.1:6060/api/plugins/on8st.wavelog

curl $BASE/config                                     # key is never returned
curl -X POST $BASE/config -H 'content-type: application/json' -d '{
  "baseUrl": "https://wavelog.example",
  "apiKey": "wl-…",
  "stationProfileId": 1,
  "pullStationIds": [1, 2],
  "radioEnabled": false
}'
curl $BASE/profiles                                   # what the key can reach
curl -X POST $BASE/test                               # one round trip
curl $BASE/status                                     # pending, failed, cursor
curl -X POST $BASE/resync -d '{"dryRun":true}'        # report without writing
curl -X POST $BASE/retry                              # requeue dead letters
```

**Pull from many profiles, push to one.** A QSO logged under a station profile
that is not in `pullStationIds` is invisible to the sync — permanently, not
late. `GET /profiles` lists everything the key can see; put them all in unless
you mean to exclude one.

## Testing without a live Wavelog

Nothing in the test suite touches a real instance, and nothing touches a real
Zeus. `tools/FakeWavelog` is a stand-in implementing the endpoints and semantics
read out of Wavelog's own source — the same duplicate key, the same primary-key
cursor, the same response shapes — so the plugin is driven end to end including
its real HTTP client. `NativeLogbook` in the test project plays Zeus's own
logbook plugin: a separate LiteDB handle writing contract records into
`zeus-logbook.db`, so every test starts from a log this plugin did not create.

```sh
dotnet test                                    # 151 tests, no network, no radio
dotnet run --project tools/FakeWavelog -- 8099 # drive it by hand instead
```

The fake encodes *our reading* of Wavelog. It cannot prove that reading right —
only a run against a real instance does that, which is a gate item, not a unit
test.

## Building and installing

```sh
export PATH="$HOME/.dotnet:$PATH"
dotnet build
cp -r src/Zeus.Plugin.Wavelog/bin/Debug/net10.0/ \
      ~/Library/Application\ Support/Zeus/features/on8st.wavelog/
```

The output must contain `Zeus.Plugin.Wavelog.dll`, `LiteDB.dll`,
`Zeus.Plugin.Wavelog.deps.json`, `plugin.json` and `ui/wavelog.es.js` — and
**not** `Zeus.Plugins.Contracts.dll`, which the host resolves from its own load
context. `PackagingTests` checks all of that, including that the manifest is
called `plugin.json` and not `manifest.json`.

## Before you trust it

This has never run inside Zeus. Use a **scratch profile first**, and confirm all
of this before pointing it at a real log:

- the panel appears in the workspace and saves
- a QSO logged in Zeus reaches Wavelog within a minute
- one logged elsewhere appears in Zeus's own logbook view — browsable,
  editable and exportable like any other
- a LoTW confirmation arrives after the sweep and changes nothing else
- `resync` dry run reports zero drift after a week
- uninstalling the plugin leaves the log complete and exportable

Two risks worth naming, because neither is visible from the C#:

**The native logbook could change its document.** We store into a collection it
owns. If a future Zeus renames a field or moves the file, this attaches to the
wrong thing rather than failing loudly. The names are asserted in tests, so a
change is caught the moment the reference is re-read — but it has to be
re-read.

**Both sides must open shared.** If a future Zeus switches to `Direct`, the two
handles stop seeing each other with no error at all. Check `Count()` in
`/status` against what the logbook view shows before believing a quiet sync.

Licence: GPL-2.0-or-later, matching the contracts it links against.
