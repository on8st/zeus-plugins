# zeus-plugins

Plugins for the Zeus SDR station engine, written against
`Zeus.Plugins.Contracts`. One directory per plugin; each is a self-contained
.NET solution with its own tests.

## Layout

```
zeus-plugins/
├── docs/                 cross-plugin reference — the engine, the framework
└── <plugin>/
    ├── prompts/          implementation prompts, one per phase
    ├── docs/design/      the design — rendering at the root, SSOT in source/
    ├── src/  tests/      the code
    └── README.md         how to configure it
```

Everything about a plugin lives inside its own folder, so a second plugin never
interleaves with the first.

## Why this is not a fork of station-engine

`Zeus-SDR/station-engine` is a release mirror: every commit on `main` is a
squashed export of one release, so anything added there is fought by the next
version bump. Forks exist to contribute back, which that repository does not
take. And a plugin is an independent solution that only *references* the
contracts assembly — it has no reason to live inside the engine's tree.

The engine clone at `~/Repos/on8st/station-engine` stays read-only. Its
`docs/` folder holds the design documents these prompts implement, untracked
and excluded from upstream.

## Reference material

| What | Where |
|---|---|
| Framework how-to | `docs/plugin-framework-how-to.md` |
| Wavelog plugin design (SSOT) | `wavelog/docs/design/source/design.md` |
| Same, rendered | https://claude.ai/code/artifact/5718e906-4e86-4f53-bf7b-f15da052d487 |
| Engine architecture | `docs/station-engine-architecture.html` |
| Contracts to compile against | `station-engine/Zeus.Plugins.Contracts` |
| Wavelog source (verified against) | `~/Repos/on8st/wavelog` @ `af32561` |

## House rules for every plugin here

- **TDD, always.** Red first. The order is in each phase's prompt.
- **GPL-2.0-or-later**, matching the contracts it links against.
- **Never ship `Zeus.Plugins.Contracts`** in the plugin output — the host
  resolves it from the default load context.
- **Never write into `~/Repos/on8st/station-engine`** — it is upstream's tree.
