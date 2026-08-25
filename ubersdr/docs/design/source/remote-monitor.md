# Remote monitor — design

**Hear your own signal as a distant station hears it.** A list of public UberSDR
receivers, each auto-tuned to the frequency and mode Zeus is on, streaming back
while you transmit — audio you can listen to, and an SNR figure per receiver
saying how strongly you are actually getting out.

Fuses use cases 1 and 2. It is the first thing worth building here because it
changes what the operator can *do*, not what they can look at, and because
everything it needs turned out to exist.

## What the framework actually provides

Two corrections to earlier notes in this repository, both found by reading
`IPluginContext` properly rather than skimming it:

- **A plugin can control the radio.** `IPluginContext.RadioController`
  (`IRadioController`) offers `SetFrequencyAsync`, `SetModeAsync`,
  `SetMoxAsync`, gated by the `ControlRadio` capability. An earlier note here
  said a plugin cannot retune; that was wrong.
- **A plugin can play audio into Zeus.** `IPluginContext.Playback`
  (`IAudioPlaybackSink`) mixes mono float32 into the operator's local monitor —
  the RX audio bus — inside a `BeginLocalMonitor()` session, paced by the host's
  RX clock. It also exposes `IsMoxOn`, and `PlayOnAir` for injecting into the TX
  chain (which never keys by itself).

So the monitor does **not** have to live in the browser. It can be a backend
plugin that streams server-side and plays through Zeus's own audio device.

What is still true: a plugin cannot be a *receiver* — remote IQ cannot enter the
DSP chain to be filtered, notched or noise-reduced. Playback is a monitor bus,
not a demodulator. That distinction is the whole design.

## The UberSDR side

Read from the published client, not assumed:

```
wss://<host>/ws                       audio + status
wss://<host>/ws/user-spectrum         spectrum

→ { "type": "tune", "frequency": <Hz>, "mode": "<mode>",
    "bandwidthLow": <Hz>, "bandwidthHigh": <Hz> }
→ { "type": "set_mute", "muted": <bool> }
→ { "type": "ping" }
← { "type": "audio", ... }            Opus
← { "type": "status", ... }
```

Signal quality is a **true SNR in dB**, computed as `basebandPower - noisePower`.
That is the number the operator wants: not an S-meter reading of unknown
calibration, but a comparable figure across receivers.

Receiver selection comes from `https://instances.ubersdr.org/api/instances` —
callsign, location, `maidenhead`, `distance`, `bearing_degrees`,
`available_clients`, `is_online`.

## Shape

```
Zeus radio state ──► plugin backend ──► N × wss://…/ws  (one per receiver)
  FrequencyHz                              tune to Zeus's frequency + mode
  Mode                                     ▼
  MoxChanged ─────────────────────────► record / play
                                             ▼
                        IAudioPlaybackSink.PlayLocal → operator hears it
                        SNR per receiver     → panel shows how well you're heard
```

The panel picks receivers and displays SNR; the backend owns the sockets, the
Opus decoding and the playback. Server-side also sidesteps CORS entirely, which
the panel would hit on any per-instance REST call.

## The thing that will bite: feedback

Playing a remote receiver through the shack speakers **while the microphone is
open** is a feedback loop with one to three seconds of internet latency in it —
a delayed howl, transmitted. This is not a theoretical risk; it is the default
outcome of the obvious implementation.

Two defences, and the default should be the second:

1. **Headphones**, stated plainly in the panel. Necessary but not sufficient —
   an operator will forget.
2. **Record while keyed, play back on unkey.** No open mic when audio is
   playing, so no loop is possible. It is also *better*: nobody can critically
   judge their own audio while talking. `MoxChanged` gives the exact window, and
   `LocalMonitorBacklog` lets the plugin wait out the tail before reporting done.

Live monitoring stays available for headphone users, behind an explicit toggle
that says why.

## Open questions

1. **Is `PlayLocal` audible while MOX is on?** The sink documents a
   "local-monitor (preview) path" and exposes `IsMoxOn`, which suggests the host
   has opinions during transmit. Live monitoring depends on the answer;
   record-and-replay does not. **Test before designing around it.**
2. **Split operation.** `IRadioStateReader` exposes a single `FrequencyHz`. If
   that is the RX VFO, split transmit would monitor the wrong frequency —
   silently. Establish what it reports before trusting the reading.
3. **Opus in .NET.** Needs a decoder in the plugin; Concentus is pure managed and
   the obvious candidate. Check the licence.
4. **Courtesy.** Each monitored receiver occupies a client slot on someone
   else's hardware. Connect around transmit rather than camping, honour
   `available_clients`, and never auto-connect more than the operator chose.
5. **Latency alignment.** The SNR figure arrives seconds after the speech that
   caused it. For "how strong am I", peak-hold across the transmission is more
   honest than an instantaneous reading.

## Why this one first

Everything it needs now exists and has been checked: radio state and events, a
playback path into Zeus, a documented tune command, a true SNR, and a public
directory to choose receivers from. The remaining unknowns are two tests and a
licence check, not a research programme.
