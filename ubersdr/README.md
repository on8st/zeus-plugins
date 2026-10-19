# Remote Ears

*powered by ka9q-ubersdr*

A Zeus station-engine plugin that listens to the public ka9q-ubersdr receiver
network alongside your own radio.

**QSO Assist** puts your own receiver in the left ear and whichever remote
station copies your frequency best in the right — picking it, re-picking it as
conditions change, and delaying the two into step so you are not hearing the
same speech twice a second apart. **The map** shows where you are being heard
while you transmit, with a recording from each receiver.

What it is **not**: it is receive-only, it never transmits anywhere, and remote
audio never enters the Zeus DSP chain — it is decoded and played in the panel,
so nothing it does can reach your transmitted signal. It holds at most three
remote connections at a time on hardware other people pay for, never retries a
receiver that refused, and caches the directory rather than polling it.

## Credits

Receivers come from the **UberSDR** network — <https://ubersdr.org>, directory
at `instances.ubersdr.org`. UberSDR is in turn built on **ka9q-radio** by Phil
Karn, KA9Q — <https://github.com/ka9q/ka9q-radio> — and the name here follows
the upstream project's own (`ka9q_ubersdr`).

This plugin is not affiliated with, endorsed by, or connected to either project.
It is a third-party client that speaks their public protocol.

## What you need

- Zeus station-engine with plugin ABI 1 (SDK 1.4.0 or later), and .NET 10 to build
- An internet connection — the receivers are other people's, on the public network
- Nothing else: no account, no key, no configuration

## Install

```sh
./tools/package.sh            # prints the .zip and its sha256
```

In Zeus: **Features → install local feature**, choose the zip.

## Status

Working on the air: the monitor wall, the map, receiver selection, station
naming, per-ear mute, and two-ear time alignment.

Built but not yet confirmed against a real transmission: the right ear going
silent while you transmit, and the switching hysteresis.

Known rough edge: envelope correlation degrades at low signal levels, because a
weak signal's envelope is mostly noise and noise is uncorrelated between two
receivers by construction. It refuses to align rather than aligning wrongly.

See `HANDOFF.md` for the full state, the measurements behind it, and what to do
next.
