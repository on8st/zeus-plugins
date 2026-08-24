# Writing a Zeus station-engine plugin

Derived by reading the source at tag **v2.0.9** (`db764e1`). Local notes —
untracked, not upstream's documentation. Where this disagrees with the code,
the code is right.

Companion: [`station-engine-architecture.html`](station-engine-architecture.html)
(rendering: <https://claude.ai/code/artifact/73a7f884-ee5c-4c36-b300-71e6634b12f0>).

Kept here rather than in the engine clone: this is reference material for
**plugin authors**, it applies across every plugin in this repo, and in that
clone it was untracked inside upstream's tree — one `git clone` from being lost.

---

## 1. What a plugin is

A .NET assembly, plus a `manifest.json`, in a directory under the plugin root.
The host discovers it at startup, validates the manifest, loads the assembly
into its own collectible `AssemblyLoadContext`, and calls two methods.

You compile against **`Zeus.Plugins.Contracts`** — 1,160 lines that reference
nothing else. That is deliberate: a plugin author never pulls in the engine.

```csharp
public interface IZeusPlugin
{
    Task InitializeAsync(IPluginContext context, CancellationToken ct);
    Task ShutdownAsync(CancellationToken ct);
}
```

That is the whole mandatory surface. Everything else is opt-in.

## 2. Pick your extension points

Implement any combination. Each is discovered by interface, not declared.

| Interface | What it makes you |
|---|---|
| `IBackendPlugin` | server-side logic that contributes HTTP routes |
| `IUiPlugin` | panels contributed into named UI slots |
| `IAudioPlugin` | a stage in the serial audio chain |
| `IRxAudioTapPlugin` | read-only tap on receive audio (realtime) |
| `ITxAudioTapPlugin` | read-only tap on transmit audio, pre-processing |
| `IAudioModemPlugin` | transforms audio — the seam FreeDV runs through |
| `ILogbookPlugin`, `ILogbookPluginV2` | replaces the QSO logbook entirely — see §11 |

`IBackendPlugin` gets a route builder already scoped to your prefix — mapping
`"status"` exposes `/api/plugins/{id}/status`.

## 3. The manifest

`manifest.json` beside your assembly. Required fields are marked; the rest have
defaults.

```jsonc
{
  "schemaVersion": 1,                 // required — the host accepts only 1
  "id": "on8st.tone",                 // required — ^[a-z][a-z0-9.]*[a-z0-9]$
  "name": "Tone Generator",           // required
  "version": "1.0.0",                 // required — semver, +/- suffix allowed
  "author": "",
  "description": "",
  "homepage": null,
  "license": "",

  "sdk": {                            // required
    "abi": 1,                         // must equal AbiVersion.Current
    "minVersion": "1.4.0"             // x.y.z exactly
  },

  "entrypoint": {                     // required
    "assembly": "Tone.dll",           // plain filename, relative to plugin root
    "type": null                      // optional explicit type name
  },

  "capabilities": ["ReadRadioState", "AudioStream"],

  "permissions": {                    // documentation; see §7
    "network": false,
    "fileSystemRead": false,
    "fileSystemWrite": false
  },

  "ui": {
    "modules": [],
    "panels": [
      { "id": "tone", "title": "Tone", "slot": "right", 
        "icon": "Box", "category": "plugins" }
    ]
  },

  "audio": {
    "format": "vst3",                 // or an Audio Unit
    "vst3Path": "vendor/Tone.vst3",   // MUST be relative to the plugin root
    "vst3Uid": null,
    "auComponentId": null,
    "slot": "tx.post-leveler"
  }
}
```

### What the validator refuses, before any code loads

- `schemaVersion` other than 1
- an `id` failing `^[a-z][a-z0-9.]*[a-z0-9]$`
- a missing `name`, or a `version` that is not semver
- a missing `sdk` block, an `abi` that is not the host's, a malformed
  `minVersion`
- an `entrypoint.assembly` that is missing, does not end in `.dll`, or is
  anything other than a plain filename relative to the plugin root
- a `ui.panels[]` entry without `id` or `slot`
- an `audio.vst3Path` that is not relative to the plugin root

Unknown **capability names are ignored**, not rejected — that is deliberate
forward-compatibility, so a plugin declaring a capability from a later host
still loads here.

## 4. Capabilities and what they actually do

Seven flags. They decide **what the host hands you**, at wiring time:

| Capability | Effect if absent |
|---|---|
| `ReadRadioState` | `ctx.Radio` is `null` |
| `ControlRadio` | `ctx.RadioController` is `null` |
| `NetworkAccess` | `ctx.Qrz` is `null` |
| `AudioStream` | you are not placed in the audio chain |
| `FileSystemRead` / `FileSystemWrite` | *nothing* — see §7 |
| `PersistSettings` | granted implicitly to everyone |

So the pattern to write against is: **ask for what you need, and null-check
what you were given.**

```csharp
public Task InitializeAsync(IPluginContext ctx, CancellationToken ct)
{
    if (ctx.Radio is null)
        throw new InvalidOperationException(
            "declare ReadRadioState in the manifest");

    ctx.Radio.FrequencyChanged += hz => ctx.Logger.LogInformation("VFO {Hz}", hz);
    return Task.CompletedTask;
}
```

`IPluginContext` also gives you `PluginId`, `Manifest`, `Logger`,
`PluginRootPath`, `HostDataDirectory`, `Settings` (`SetAsync` / `DeleteAsync`,
scoped to your plugin), and optionally `Playback` and `OperatorIdentity`.

`Playback` is **not** capability-gated, with a reason recorded in the code:
on-air audio only reaches the antenna under operator MOX. Note the host prefers
a per-plugin factory for it — the sink's over-air resampler is stateful, so two
plugins sharing one instance would leak residual samples into each other's
first block.

## 5. Writing an audio stage

```csharp
void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx);
```

`AudioBlockContext` carries `SampleRate`, `Channels`, `Frames`, `SampleTime`,
`Mox` and `Receiver`. Spans are non-overlapping, planar, of length
`Frames * Channels`.

Rules that come straight from the contract:

- **Frame count varies by route.** Allocate for `IAudioHost.CurrentBlockSize`
  during initialisation, then iterate the span length you are actually given.
- In-place is fine: `input.CopyTo(output)`, then mutate `output`.
- **A bypassed slot should still copy** rather than skipping the call. The host
  handles chain-disabled short-circuiting itself.
- `InitializeAudioAsync` runs on the realtime thread and *may* allocate; it has
  a **1-second** timeout. `Process` must not allocate, block, or take locks.

The chain around you is written to the same standard: `AudioChain.Process`
allocates nothing, takes no locks, and collapses to a single `memcpy` when
master bypass is engaged — a bit-identical pass-through requirement.

`IAudioPlugin.Requirements` declares the sample rate, channel count and block
size you need. **The host refuses to load you if the current TX/RX path cannot
satisfy them** — better than discovering it mid-transmission.

## 6. Lifecycle, and the timeouts that will bite you

| Phase | Budget |
|---|---|
| `InitializeAsync` | **10 s** |
| `InitializeAudioAsync` | **1 s** |
| `ShutdownAsync` | **5 s** |
| plugin-id migration | 60 s |

Each plugin loads into its own **collectible** `AssemblyLoadContext`, so it can
be unloaded without restarting the engine. Honour the `CancellationToken`.

The shutdown contract is worth quoting, because it tells you exactly whose
problem an overrun is:

> The host applies a 5-second timeout; if it expires the plugin is
> force-unloaded and any leaked threads remain a debugging problem for the
> plugin author.

Activation is wrapped per-plugin: one plugin that throws or hangs fails alone.

## 7. Security — read this before trusting the manifest

**Capabilities are declared and gated at wiring time. They are not enforced at
runtime.**

```csharp
// PluginManager.ComputeGrantedCapabilities
// v1 grants every declared capability; user-prompt UI is iter 5.
return m.ParseCapabilities();
```

You get everything you ask for. There is no prompt, and no refusal.

`PluginPermissionException` exists in the contracts — and is **never thrown
anywhere in the codebase**. `FileSystemRead`, `FileSystemWrite` and
`NetworkAccess` have no enforcement mechanism at all: nothing stops loaded .NET
code opening a socket or a file, capability or not.

So treat those three as **declarations of intent for the operator to read**,
not as a sandbox. A plugin is code you are choosing to run in your engine's
process, with your radio attached.

The one boundary that *is* enforced sits in `Program.cs`: plugin installation
and removal are refused with **403 from any non-loopback address**, regardless
of access token. You can administer plugins only from the station computer.

## 8. Installing

Plugin root: `~/Library/Application Support/Zeus/features` on macOS (overridable
by environment variable; the engine falls back to `<Zeus data dir>/features`).

| Route | Method |
|---|---|
| `GET /api/plugins` | list installed and active |
| `POST /api/plugins/install` | install from the registry |
| `POST /api/plugins/install/zip` | install from a zip |
| `GET /api/plugins/registry` | browse the registry |
| `DELETE /api/plugins/{id}` | remove |

The registry defaults to `https://downloads.zeussdr.com/plugins/registry.json`
and the client refuses any source URL that is not `https://` — the sole
exceptions being `http://localhost` and `http://127.0.0.1`, for developing
against a local registry.

Registry installs verify the package **SHA-256** against the registry's declared
hash and fail the install on mismatch. Zip extraction resolves every entry
against a canonical destination prefix, so an archive cannot write outside the
staging directory.

During development, dropping the directory into the plugin root and restarting
is the shortest loop — the host catalogues installed plugins without loading
their assemblies, so a broken manifest shows up as a listed-but-inactive plugin
rather than a failed start.

## 9. Hosting a VST or Audio Unit instead

If your "plugin" is really an existing VST3 or AU, you do not write .NET at
all — you declare it in the `audio` block and let the host bridge it.

Third-party VSTs run **out of process**. The host launches and supervises
`VSTHostEngine.exe --zeus-bridge`, with a control plane of newline-delimited
JSON over the child's stdin/stdout and a separate audio plane. The engine is
the externally-installed upstream binary (KlayaR/VSTHost) — **Zeus never bundles
it**.

The practical consequence: a VST that crashes takes down a child process, not
your radio. That is the reason for the extra machinery.

## 10. Loading and dependencies

`PluginLoadContext` forces four assembly families to resolve from the **default**
load context rather than yours:

```csharp
Zeus.Plugins.Contracts*    Microsoft.*    System.*    netstandard
```

with the reason stated in the code: so that plugin-defined `IZeusPlugin` and
host-side `IZeusPlugin` are the same `Type` identity, and
`(IZeusPlugin)Activator.CreateInstance(...)` does not throw. This is the classic
way plugin loading breaks, and the host has already handled it — you do not need
to fight it.

The consequence is the part to plan around: **everything else you ship is loaded
from your own plugin directory**, via `AssemblyDependencyResolver`. A
third-party HTTP or database library gets its own copy per plugin. Keep
dependencies few, and prefer what already comes from the default context —
`System.Net.Http` over any third-party client.

Unmanaged native libraries resolve the same way, from your directory.

## 11. What the framework does not give you

Worth knowing before designing around an assumption.

**There is no "QSO logged" event.** Nothing in `IPluginContext` or the WebSocket
frame set signals that a contact was logged. The context's only events are
`FrequencyChanged`, `ModeChanged` and `MoxChanged`.

**`ILogbookPlugin` is a replacement, not an observer.** It is full CRUD — create,
page, fetch by id, worked summaries, QSL status, delete, ADIF import and export.
There is no handle to the built-in logbook on `IPluginContext`, so a logbook
plugin **cannot delegate**. Implementing it means owning storage.

**And the engine does not consume it.** `ILogbookPlugin` appears nowhere in
`Station.Engine.Hosting` or `Zeus.Plugins.Host` — the engine knows only a path,
`zeus-logbook.db`. The consumer is Zeus Link, the proprietary client. So the
contract is readable but its behaviour — when it is called, whether a fallback
exists — is not verifiable from this repository.

**The UI panel contract is likewise not in this repository.** `ui.modules` and
panel slots are consumed by the client. The product bundle ships
`wwwroot/zeus-sdk/react.js` and `react-jsx-runtime.js` as separate importable
modules, which strongly suggests panel modules are expected to import React from
the host rather than bundle their own — but that is an inference from the file
layout. The bundle itself carries an explicit *"may not be … decompiled,
disassembled, or reverse engineered"* clause, so it is not a legitimate source
for reconstructing the contract. Ask upstream, or find a plugin that publishes
its source.

The practical consequence for both: **build backend-first.** A plugin is fully
usable with `IBackendPlugin` endpoints and no panel at all.

## 12. Checklist

- [ ] compile against `Zeus.Plugins.Contracts`, nothing else
- [ ] `manifest.json` with `schemaVersion: 1` and `sdk.abi` matching the host
- [ ] `entrypoint.assembly` a plain `.dll` filename, no path
- [ ] declare every capability you need, and null-check what you get
- [ ] honour the cancellation tokens; respect 10 s / 5 s / 1 s
- [ ] `Process` allocates nothing and takes no locks
- [ ] bypassed slots still copy input to output
- [ ] any `vst3Path` is relative to the plugin root
- [ ] remember: the manifest is documentation, not a sandbox
