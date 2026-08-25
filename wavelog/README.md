# Wavelog Synchroniser — Zeus station-engine plugin

Keeps Zeus's **own** logbook and a [Wavelog](https://github.com/wavelog/wavelog)
instance in step, in both directions.

This is not a logbook. It does not implement `ILogbookPluginV2` and never owns
the operator's QSOs — the native logbook keeps doing that, along with browsing,
sorting, searching, editing, ADIF and QSL, all of which already work. This
attaches to the same database file and moves contacts across.

Design: [`docs/design/`](docs/design/) · Prompts: [`prompts/`](prompts/)

**Status: running in production.** Installed in Zeus Link, attached to the
built-in logbook, syncing both directions with a live Wavelog: a contact logged
in Zeus reached the server, nine logged elsewhere reached Zeus, the panel
configures it, and a resync dry run reports zero drift. The confirmation sweep is
the one path still unexercised against real data — nothing in the log is
LoTW/QSL-confirmed yet. See *Before you trust it*.

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

## Where the logbook lives

Zeus keeps its logbook at
`<Application Support>/ZeusProduct/logbook/zeus-logbook.db`. The engine's own
data directory is checked too — `PrefsDbPath.LogbookPath()` names it, and a
future layout may use it. Same file name, same `logs` collection, same
`LogbookEntrySnapshot` documents; different directory.

This cost real time to find. The plugin reported *"no Zeus logbook found"* while
the operator was looking at a QSO they had just logged — because it checked one
path and treated the answer as definitive.

So it checks both, reports **which file** it attached to in `/status`, and when
**both** exist it refuses to choose: syncing one while the operator reads the
other is worse than doing nothing. Set `logbookPath` explicitly to settle it.

This is an **extension of the Zeus logbook**, not a replacement and not a logbook
of its own. It needs one to exist — Zeus creates it when you log your first
contact — and uninstalling this leaves it untouched.

## Configuring it

Through the **Wavelog Sync** panel in the workspace tools. The same surface is
also plain HTTP on the engine's own port, which is what the panel calls:

```sh
BASE=http://127.0.0.1:$PORT/api/plugins/be.on8st.zeus.plugins.wavelog   # the launcher picks the port

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

The engine's port is assigned by the Zeus Link launcher at start (it was 51032
here), not fixed — read it from the running `StationEngine --port` argument.

**Pull from many locations, push to one.** A QSO logged under a station location
that is not in `pullStationIds` is invisible to the sync — permanently, not
late. `GET /profiles` lists everything the key can see; put them all in unless
you mean to exclude one.

**Locations, not logbooks.** Wavelog has two things and they are easy to swap.
A **Station Location** has the `station_id` the API writes to; a **Station
Logbook** is a *grouping* of locations. A QSO goes to a location and then
appears in whichever logbook that location is linked to. Neither the v1 nor the
v2 API exposes any way to list logbooks, so if a QSO is not where you expect,
the question to ask is always which *location* it was written to. The panel uses
Wavelog's own wording for this reason.

## Validating against a real Zeus

`tools/zeus-harness/run.sh` stands the whole thing up from nothing: it builds the
engine and this plugin, installs the plugin into a throwaway sandbox, seeds a
logbook **at the layout Zeus really uses** with `tools/ZeusLogbookSeed`, starts a
real station engine, and drives the lot over HTTP.

The seeder matters: a logbook this plugin created would prove nothing about
attaching to somebody else's database. It writes the document shape read out of a
real product logbook, into the product location, so the harness exercises the
configuration that actually ships.

Credentials for `--live` come from the environment, never from the repo. On this
machine they live in `~/.config/on8st/wavelog.env` (mode 600, outside any git
tree); the production copy is separate and belongs to Zeus, which persists what
you type into the panel.

```sh
set -a; . ~/.config/on8st/wavelog.env; set +a

./tools/zeus-harness/run.sh                     # offline, against the fake Wavelog
./tools/zeus-harness/run.sh --live \           # read-only against a real instance
  --station-profile 1                           # needs WAVELOG_URL + WAVELOG_KEY
./tools/zeus-harness/run.sh --live --allow-write --station-profile N
```

It touches nothing installed. `ZEUS_PREFS_PATH` and `ZEUS_PLUGINS_PATH` move the
data directory and plugin root into a sandbox — the engine's own source names
dev, CI and tests as why those exist. **`--live` is read-only unless you also
pass `--allow-write`**, and write goes to the profile you name, so it can never
land in a real log by default.

This harness is not a nicety. Three bugs that a green unit suite could not see
were found the first time it ran:

| | |
|---|---|
| **the collection was `logs`, not `entries`** | `entries` is an HTTP *route*; the collection name was read out of a DLL string table and guessed wrong. Both sides of every unit test used the same wrong name, so the suite stayed green. Shipped, it would have attached to an empty collection and reported a healthy, permanently idle sync |
| **`station_info` takes its key in the URL** | it is `function station_info($key = '')`, so CodeIgniter fills it from a path segment and never reads a POST body. The body form returns a 401 that reads exactly like a bad key |
| **`lastfetchedid` is a JSON string** | a real instance returns `"1"` where the fake returned `1`. `GetValue<int>()` threw, killing the sync loop on every cycle — against *every* real Wavelog, forever |
| **a duplicate is reported as HTTP 400 `abort`** | dedup is what makes at-least-once delivery safe, and it works — but the retry's reply is a *failure*, which the retry policy dead-lettered. One timed-out-but-delivered POST would have left a permanent "1 failed" that pressing retry could never clear |
| **the logbook was in a different directory** | Zeus keeps it under `ZeusProduct/`, not the engine data directory. The plugin declared "no logbook found" to an operator looking at their own QSO |

The first is why `ZeusLogbookDb.Verify()` exists: it now says so loudly on
startup rather than syncing nothing in silence.

## Testing without a live Wavelog

Nothing in the unit suite touches a real instance, and nothing touches a real
Zeus. It is necessary and not sufficient — see above for what it cannot see. `tools/FakeWavelog` is a stand-in implementing the endpoints and semantics
read out of Wavelog's own source — the same duplicate key, the same primary-key
cursor, the same response shapes — so the plugin is driven end to end including
its real HTTP client. `NativeLogbook` in the test project plays Zeus's own
logbook plugin: a separate LiteDB handle writing contract records into
`zeus-logbook.db`, so every test starts from a log this plugin did not create.

```sh
dotnet test                                    # 180 tests, no network, no radio
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
      ~/Library/Application\ Support/Zeus/features/be.on8st.zeus.plugins.wavelog/
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
