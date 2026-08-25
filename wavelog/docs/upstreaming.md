# Getting this into the Zeus plugin registry

## What the route actually is

There is **no documented submission process**. Checked, rather than assumed:

- `Zeus-SDR/station-engine` is public, has issues enabled, and says nothing
  about plugins, the registry, or contributions in its README. No
  `CONTRIBUTING`, no issue templates.
- Every plugin repo the registry links as `homepage` returns **404**:
  `OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins`,
  `Zeus-SDR/openhpsdr-zeus-plugins`, `Kb2uka/openhpsdr-zeus-plugins`. There is
  no public repository to open a pull request against.
- Every `downloadUrl` in the registry points at `downloads.zeussdr.com`, and
  `PluginInstaller` refuses a non-HTTPS URL. Distribution is theirs.
- The catalogue carries a `verified` flag they curate — some entries are
  `false`, so unverified listing is evidently possible.

So this is **an ask, not a patch**. The only public channel is an issue on
`Zeus-SDR/station-engine`. Prior traffic there is thin: two issues, both closed,
the more recent one closed with no public reply — so a silent close is a
plausible outcome and shouldn't be read as rejection.

The realistic outcomes, in order of likelihood:

1. They point us at a private repo or a process that isn't published.
2. They add a registry entry pointing at a release we host, with `verified:
   false`.
3. They take the source into their own plugins repo and build it themselves.
4. Nothing happens, and the plugin stays a sideload — which already works
   through *install local feature*, and costs the operator one file picker.

Option 4 is not a failure. Nothing about this plugin needs the registry; the
registry buys discoverability and automatic updates, nothing more.

## What has to be true before asking

| | Status |
|---|---|
| Licence compatible — GPL-2.0-or-later, same as the contracts it links | done |
| Builds from a clean clone by someone else | done — `ZeusEngineRoot` |
| No secrets, no machine paths, anonymised commit identity | done — history scanned |
| ABI honest — `abi 1`, `sdk 1.4.0` | done |
| **Source public** | **decision needed** |
| **Tested on Windows and Linux** | **not done — macOS only** |
| **Plugin id follows their convention** | **decision needed** |

### Source has to be public

Not optional if they distribute it. The plugin links `Zeus.Plugins.Contracts`,
which is GPL-2.0-or-later, so binaries carry a source obligation. Publishing the
repo is the cheapest way to satisfy it. That is a decision about the `on8st`
identity, not a technical step — the history is already clean and anonymised.

### It has only ever run on macOS

The logbook path is derived as a sibling of the engine's data directory rather
than hard-coded, so the *shape* is portable — but that ZeusProduct keeps its
logbook in the same relative place on Windows and Linux is **an assumption we
have never checked**. If it is wrong there, the plugin reports "no Zeus logbook
found" on those platforms, which is exactly the bug we already shipped once.

Either verify on both, or declare `"platforms": ["osx"]` and say so plainly.
Declaring `any` on the strength of one platform would be the same guess that has
been wrong every time in this project.

### The id is unconventional

Ours is `on8st.wavelog`. Theirs are reverse-DNS: `org.openhpsdr.*`,
`com.openhpsdr.zeus.plugins.*`, `com.zeussdr.plugins.*`, `com.kb2uka.voyeur`.
`be.on8st.wavelog` would match, on8st.be being the domain.

Worth settling before listing, because the id is load-bearing: it names the
install directory and the settings collection (`plugin_on8st_wavelog`). The
engine does have `PluginIdMigrations`, so it is not irreversible — but a rename
after other people have installed it is churn for them, not for us.

## Draft registry entry

Their schema, filled in. `sha256` comes from `tools/package.sh`.

```jsonc
{
  "id": "on8st.wavelog",
  "name": "Wavelog Synchroniser",
  "description": "Keeps the Zeus logbook in step with a Wavelog instance, both directions. Pushes every contact you log, imports contacts logged by other apps, sweeps LoTW and QSL confirmations back, and can publish live rig state. Not a logbook: Zeus keeps owning your QSOs and uninstalling this leaves them untouched.",
  "author": "on8st",
  "license": "GPL-2.0-or-later",
  "homepage": "https://github.com/on8st/zeus-plugins",
  "categories": ["logging"],
  "verified": false,
  "versions": [
    {
      "version": "0.1.0",
      "sdkAbi": 1,
      "sdkMinVersion": "1.4.0",
      "platforms": ["osx"],
      "downloadUrl": "https://github.com/on8st/zeus-plugins/releases/download/wavelog-v0.1.0/on8st.wavelog-0.1.0.zip",
      "sha256": "<from tools/package.sh>"
    }
  ]
}
```

A GitHub release URL is HTTPS and satisfies the installer, so they need not host
the artefact themselves unless they prefer to.

## Draft issue

> **Title:** Plugin submission process — is there one?
>
> I've written a Wavelog synchroniser plugin against the v1.4.0 SDK
> (`abi 1`) and I'd like to know whether there's a way to get it into the
> registry, or whether sideloading is the intended route for third-party
> plugins.
>
> It keeps the Zeus logbook in step with a Wavelog instance in both directions
> — pushing logged contacts, importing ones logged elsewhere, sweeping QSL and
> LoTW status back. It deliberately doesn't implement `ILogbookPluginV2`: it
> attaches to `zeus-logbook.db` as a second shared-mode handle and keeps its own
> bookkeeping in a separate collection, so uninstalling it leaves the operator's
> log untouched.
>
> GPL-2.0-or-later, same as the contracts it links. Source, tests and an
> end-to-end harness are at `<repo>`. It's been running against a live Zeus Link
> and a live Wavelog.
>
> Two things I couldn't answer from the public repo:
>
> 1. The `homepage` links in `registry.json` (e.g.
>    `OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins`) 404 for me. Is there a
>    repository third-party plugins should be proposed to?
> 2. `downloadUrl` entries all point at `downloads.zeussdr.com`. Would you host
>    an artefact, or can a registry entry point at a GitHub release?
>
> Happy to keep it a sideload if that's the intent — *install local feature*
> works fine. Mainly want to avoid guessing at a process that already exists.

Deliberately short, asks a question rather than making a demand, and does not
assume they owe anyone a listing.
