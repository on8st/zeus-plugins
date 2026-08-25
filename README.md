# zeus-plugins

Plugins for the [Zeus SDR](https://zeussdr.com) station engine, written against
`Zeus.Plugins.Contracts`. One directory per plugin, each a self-contained .NET
solution with its own tests.

| Plugin | Id | What it does |
|---|---|---|
| [**Wavelog Synchroniser**](wavelog/) | `be.on8st.zeus.plugins.wavelog` | Keeps the Zeus logbook in step with a [Wavelog](https://github.com/wavelog/wavelog) instance, both directions |

Tested on macOS; expected to work on Linux and Windows — pure .NET, no native
code. See each plugin's README for what has and has not been verified.

## Installing

These are not in the Zeus plugin registry, so they install as local features.
Build the package and pick it in Zeus:

```sh
cd <plugin> && ./tools/package.sh
```

Then in Zeus: **Features → install local feature**, and choose the `.zip`. The
engine validates the manifest, checks the SDK ABI, and activates it without a
restart.

The package format is a zip with `plugin.json` **at the top level** — nested in a
folder and the installer rejects it. `tools/package.sh` asserts that before it
prints a path, so a bad package cannot leave the build.

## Building

Plugins reference the station-engine contracts by project. `ZeusEngineRoot`
defaults to a **sibling checkout**:

```
some-parent/
├── station-engine/     git clone https://github.com/Zeus-SDR/station-engine
└── zeus-plugins/       this repo
```

Anything else, point at the engine explicitly:

```sh
dotnet build -p:ZeusEngineRoot=/path/to/station-engine
dotnet test
```

Needs the **.NET 10 SDK**. Built against **SDK ABI 1, minimum 1.4.0** — the
engine refuses a plugin whose ABI does not match.

## Layout

```
zeus-plugins/
├── docs/                 cross-plugin reference — the engine, the framework
└── <plugin>/
    ├── docs/design/      the design — rendering at the root, SSOT in source/
    ├── prompts/          implementation prompts, one per phase
    ├── src/  tests/      the code
    ├── tools/            packaging, test harnesses, fakes
    └── README.md         what it does and how to run it
```

Everything about a plugin lives inside its own folder, so a second plugin never
interleaves with the first.

## Reference

- [`docs/plugin-framework-how-to.md`](docs/plugin-framework-how-to.md) — writing
  a plugin against `Zeus.Plugins.Contracts`: the interfaces, the load context,
  the manifest, what the capability declarations do and do not enforce.
- [`docs/station-engine-architecture.html`](docs/station-engine-architecture.html)
  — transports, DSP pipeline, station protocol and signal paths, derived from
  the engine source.

Both were written by reading the GPL engine source. Where they disagree with a
running Zeus, believe the running Zeus — that mistake has been made here more
than once, and each plugin's design notes record how.

## House rules

Anything added here is expected to:

- **link the contracts, never vendor them.** The host resolves
  `Zeus.Plugins.Contracts` from its own load context; shipping a copy gives the
  interface types two identities and the plugin fails to bind.
- **carry its own tests**, and a way to exercise it end to end without touching
  live services or the operator's real data.
- **declare capabilities honestly**, even though the host does not enforce them.
  The manifest is what the operator reads.
- **fail loudly rather than quietly.** A plugin that installs cleanly and then
  silently does nothing is the worst outcome available, and the easiest to ship.

## Licence

**GPL-2.0-or-later**, matching `Zeus.Plugins.Contracts`, which everything here
links against. Full text in [`LICENSE`](LICENSE); every source file carries an
SPDX header.
