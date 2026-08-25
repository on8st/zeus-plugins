# Wavelog Synchroniser

A Zeus SDR station-engine plugin that keeps the Zeus logbook in step with a
[Wavelog](https://github.com/wavelog/wavelog) instance, **both directions**.

It is **not a logbook**. It does not implement `ILogbookPluginV2` and never owns
your QSOs — Zeus keeps doing that, along with browsing, editing, ADIF and QSL,
all of which already work. This attaches to the same database and moves contacts
across. Uninstall it and your log is untouched, because it was never ours.

| | |
|---|---|
| **Push** | every contact you log in Zeus is queued and delivered to Wavelog, with retry |
| **Pull** | contacts logged by *any* app — the Wavelog web UI, WSJT-X, another logger — land in the Zeus logbook |
| **Confirmations** | LoTW / eQSL / QSL status swept back onto contacts Zeus already holds |
| **Repair** | full resync, both directions, dry run first |
| **Rig state** | live frequency and mode to Wavelog's `/api/radio` (off by default) |
| **Panel** | configuration and status inside the Zeus workspace |

Nothing here sits on your write path. Zeus stores the contact through its own
logbook; this notices it afterwards and queues it. A QSO made while Wavelog is
rebooting is safe the moment Zeus stores it, because Wavelog was never involved.

## What you need

- **Zeus with a logbook.** Any contact logged in Zeus creates it; there is
  nothing to install. This is an extension of that logbook, not a replacement.
- **A Wavelog instance and an API key.** Read-only is enough for pull; pushing
  contacts and publishing rig state need a key with **write** permission.
- **A station location** in Wavelog to push to — see *Locations, not logbooks*.

## Install

```sh
./tools/package.sh            # prints the .zip and its sha256
```

In Zeus: **Features → install local feature**, choose the zip. It activates
without a restart.

## Configure

Through the **Wavelog Sync** panel in the workspace tools. The same surface is
plain HTTP on the engine's own port, which is what the panel calls — useful for
a headless station:

```sh
# the launcher assigns the port at start; read it from the running process
PORT=$(ps aux | grep -o 'StationEngine --port [0-9]*' | head -1 | awk '{print $3}')
BASE=http://127.0.0.1:$PORT/api/plugins/be.on8st.zeus.plugins.wavelog

curl $BASE/config                                     # the key is never returned
curl -X POST $BASE/config -H 'content-type: application/json' -d '{
  "baseUrl": "https://wavelog.example",
  "apiKey": "…",
  "stationProfileId": 1,
  "pullStationIds": [1],
  "radioEnabled": false
}'
curl $BASE/profiles                                   # what the key can reach
curl -X POST $BASE/test                               # one round trip
curl $BASE/status                                     # attachment, queue, cursor
curl -X POST $BASE/resync -d '{"dryRun":true}'        # report drift, write nothing
curl -X POST $BASE/retry                              # requeue dead letters
```

The API key is stored by Zeus in its own plugin settings and is never returned
by `GET /config` — only `apiKeySet: true`.

## How it behaves

**New contacts are found by polling**, every thirty seconds. The framework has no
"QSO logged" event, so a contact is noticed by *absence*: an entry with no sync
row of ours has not been dealt with. That is the only mechanism available, and it
has one real advantage — a backlog logged before you installed this goes up on
the first scan instead of being silently skipped.

**A contact that came from Wavelog is never pushed back.** Imports are marked
inbound, so a repair run cannot enqueue your whole imported log to be sent back
at the instance it came from.

**Delivery is at-least-once, and that is safe.** Wavelog deduplicates on
callsign + time to the minute + band + mode + station, so a POST that timed out
after arriving is skipped rather than duplicated. It reports that skip as an
error; this treats it as the success it is.

**Confirmations need their own sweep.** Wavelog's incremental cursor is a primary
key, and a confirmation is an *update* — the key never moves, so the incremental
pull is permanently blind to it. A separate filtered sweep picks them up at any
age. Neither loop catches a plain content edit made in Wavelog; that is a known
limitation, not an oversight.

**Repair only ever inserts.** A contact deleted in Wavelog but present in Zeus
stays. "Full sync" must not be read as "make identical".

## Locations, not logbooks

Wavelog has two things and they are easy to confuse:

- a **Station Location** carries the `station_id` the API writes to;
- a **Station Logbook** is a *grouping* of locations.

A QSO is written to a location and then appears in whichever logbook that
location is linked to. **A logbook is never a write target**, and no Wavelog API
— v1 or v2 — can even list logbooks. So when a contact is not where you expect,
the question is always which *location* it went to.

A QSO under a location that is not in `pullStationIds` is invisible to the sync —
**permanently, not late**. `GET /profiles` lists everything the key can reach;
list them all unless you mean to exclude one. Leave it empty and it falls back to
the location you push to, and says so rather than pretending otherwise.

## Where the logbook lives

Zeus keeps its logbook at
`<Application Support>/ZeusProduct/logbook/zeus-logbook.db`. The engine's own
data directory is checked too, since `PrefsDbPath.LogbookPath()` names it. Same
file name, same `logs` collection, same documents — different directory.

`GET /status` reports **which file** it attached to and every candidate it found.
If both exist it attaches to neither and says so: syncing one logbook while you
read the other is worse than doing nothing. Set `logbookPath` to settle it.

## When something looks wrong

`GET /status` is the whole diagnostic surface:

| Field | Means |
|---|---|
| `logbookInstalled: false` | no logbook found — log a contact in Zeus, or set `logbookPath` |
| `logbookPath` | the file actually being synced. If this is not the log you are reading, that is the bug |
| `qsosInLogbook: 0` with contacts on screen | attached to the wrong file, or the two handles are not both in shared mode |
| `pullLocationsAreImplicit: true` | no pull location chosen; falling back to the push location |
| `failed > 0` | dead-lettered. Fix the cause — a wrong key or location will just fail again — then `POST /retry` |
| `missingHere` / `missingThere` from `resync` | drift. Both zero after a week of normal use is the cheap weekly health check |

## Testing

```sh
dotnet test                      # 180 tests — no network, no live server, no radio
./tools/zeus-harness/run.sh      # a real engine, end to end, offline
```

`tools/FakeWavelog` implements the endpoints and semantics read out of Wavelog's
own source. `tools/zeus-harness` goes further: it builds a **real station
engine**, seeds a logbook at the layout Zeus really uses, installs the plugin and
drives the lot over HTTP.

That harness exists because the unit suite cannot see what a real deployment
does, and repeatedly it was wrong:

| Found only by running it | Would have shipped as |
|---|---|
| the collection is `logs`, not `entries` | a plugin attached to an empty collection, reporting a healthy, permanently idle sync |
| `station_info` takes its key in the URL, not the body | a 401 indistinguishable from a bad key |
| `lastfetchedid` is a JSON string, not an int | the sync loop throwing on every cycle, against every real Wavelog |
| a duplicate is HTTP 400 `abort` | a permanent "1 failed" for a contact sitting safely in the log |
| the logbook is under `ZeusProduct/`, not the engine data dir | "no logbook found" told to an operator looking at their own QSO |

Every one passed a green unit suite, because the tests and the bug shared one
wrong belief. A fake cannot falsify the belief it was built from — which is why
the harness is part of the product rather than something done once.

Against a real instance, `--live` is read-only unless `--allow-write` is also
given, and write goes to the location you name:

```sh
export WAVELOG_URL=https://wavelog.example WAVELOG_KEY=…
./tools/zeus-harness/run.sh --live --allow-write --station-profile 2
```

## What is proven, and what is not

Running in production: attached to the Zeus logbook, syncing both directions with
a live Wavelog. A contact logged in Zeus reaches the server, contacts logged
elsewhere reach Zeus, retries deduplicate, and `resync` reports zero drift.

Not yet exercised against real data:

- **the confirmation sweep** — it needs a QSO that LoTW or eQSL has actually
  confirmed. The code is tested against the fake; the *reading* of the API is
  not yet confirmed, and this project's record on unconfirmed readings is poor.
- **Windows and Linux.** The logbook path is derived rather than hard-coded, so
  the shape carries — but that Zeus uses the same relative layout there has never
  been checked. If it does not, `/status` will say so and `logbookPath` fixes it.

## Design

[`docs/design/`](docs/design/) — why a synchroniser rather than a logbook
replacement, the Wavelog API as verified against its source, the synchronisation
model, and what running it actually proved.

Licence: **GPL-2.0-or-later**, matching the contracts it links against.
