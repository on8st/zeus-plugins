# Wavelog Logger — Zeus station-engine plugin

Replaces the logbook's **storage** with a local store that mirrors every QSO to
a [Wavelog](https://github.com/wavelog/wavelog) instance. The client keeps
browsing, sorting, searching and editing — this plugin is what those call
through.

Design: [`docs/design/`](docs/design/) · Prompts: [`prompts/`](prompts/)

**Status: phase 1 complete, never run inside Zeus.** See *Before you trust it*.

## What it does

| | |
|---|---|
| **Logbook** | local LiteDB store, ADIF import and export, QSL and tags |
| **Push** | every QSO queued and delivered to Wavelog, with retry |
| **Pull** | contacts logged by *any* app — the web UI, WSJT-X, another logger |
| **Confirmations** | LoTW / eQSL / QSL status swept back onto local QSOs |
| **Repair** | full resync, both directions, dry run first |
| **Rig state** | live frequency and mode to Wavelog's `/api/radio` (off by default) |

The write path never touches the network. `CreateAsync` stores, queues and
returns — a contact logged while Wavelog is rebooting is safe the moment the
call returns.

## Configuring it

No UI panel yet (see the phase 3 prompt for why). Everything is over HTTP, on
the engine's own port:

```sh
BASE=http://127.0.0.1:6060/api/plugins/on8st.wavelog

curl $BASE/config                                     # key is never returned
curl -X PUT $BASE/config -H 'content-type: application/json' -d '{
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

Nothing in the test suite touches a real instance. `tools/FakeWavelog` is a
stand-in implementing the endpoints and semantics read out of Wavelog's own
source — the same duplicate key, the same primary-key cursor, the same response
shapes — so the plugin is driven end to end, including its real HTTP client.

```sh
dotnet test                                    # 134 tests, no network, no radio
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
`Zeus.Plugin.Wavelog.deps.json` and `manifest.json` — and **not**
`Zeus.Plugins.Contracts.dll`, which the host resolves from its own load context.

## Before you trust it

The engine repository cannot tell us what Zeus Link does with a logbook plugin —
whether it calls it at all, when, or what happens on uninstall. This plugin has
never run inside Zeus.

Use a **scratch profile first**, and confirm all of this before pointing it at a
real log:

- Zeus Link actually uses it as the logbook
- browsing, sorting, searching, editing and deleting all work
- performance is acceptable at your log size
- a QSO logged here reaches Wavelog, and one logged elsewhere reaches here
- a LoTW confirmation arrives after the sweep
- `resync --dryRun` reports zero drift after a week
- uninstall and reinstall leaves the log intact and exportable

The store deliberately lives in the host data directory rather than the plugin
root, so uninstalling the plugin does not delete your log.

Licence: GPL-2.0-or-later, matching the contracts it links against.
