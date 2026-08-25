# Remote monitor — design

**Hear your own signal as a distant station hears it.** A list of public UberSDR
receivers, each auto-tuned to the frequency and mode Zeus is on, streaming back
while you transmit — audio you can listen to, and an SNR figure per receiver
saying how strongly you are actually getting out.

Fuses use cases 1 and 2. It is the first thing worth building here because it
changes what the operator can *do*, not what they can look at, and because
everything it needs turned out to exist.

## What the plugin framework actually provides: nothing useful here

Both "corrections" I made to this document were themselves wrong, and only a
runtime probe found it. The contracts declare `IRadioStateReader`,
`IRadioController` and `IAudioPlaybackSink` — but every one is a
`GetService<T>()` lookup in `PluginManager`, and **nothing implements or
registers any of them**, not in the published source and not in the shipped
build.

A probe plugin declaring `ReadRadioState` and `ControlRadio`, run in both:

```
                     source engine v2.0.9    shipped Zeus Link 2.0.12
Radio                NULL                    NULL
RadioController      NULL                    NULL
Playback             NULL                    NULL
Qrz                  NULL                    NULL
OperatorIdentity     NULL                    NULL
```

So through `IPluginContext` a plugin **cannot** read the frequency, know when
the operator keys, retune anything, or play a single sample into Zeus. What is
left is settings, logging, HTTP routes and a UI panel.

This also means the Wavelog synchroniser's rig-state publishing can never have
worked: it is guarded by `if (context.Radio is { } radio)`, and that is always
false. Dead code, not a bug — but it should stop being advertised.

## The route that does work: the engine's own HTTP API

The plugin API is not the only surface. The engine serves `GET /api/state` on
its own port, and it carries more than the plugin contract ever offered:

```
vfoHz            7200000        mode   LSB
splitEnabled     false          splitTxHz  0        txVfo  A
txMonitorEnabled false          rx2AudioMode Both   txReceiverIndex 0
```

`splitEnabled` and `splitTxHz` answer the split question outright — the transmit
frequency is exposed separately and does not have to be inferred.

`GET /api/radio/ptt-status` carries the keying state:

```
{ "moxOn": false, "tunOn": false, "twoToneOn": false, "cwKeyDown": null,
  "ownedMox": false, "hangTimeMs": 250, "moxOwner": null }
```

So the monitor gets its radio state from the engine over HTTP rather than from
`IPluginContext`. A backend plugin can call it on loopback; the panel can too,
being served from the same origin.

## Audio: the panel, and only the panel

With `IAudioPlaybackSink` null, remote audio cannot enter Zeus at all. The one
remaining path is the panel: `wss://<host>/ws` straight from the webview, Opus
into an `AudioContext`. WebSocket is not subject to CORS, and several sockets can
be open at once.

That forces the feedback question rather than merely raising it. Audio played by
the panel goes to the browser's output device with **no relationship to Zeus's
audio routing and no knowledge of MOX**. Headphones stop being a recommendation
and become a requirement, and *record-while-keyed, replay-on-unkey* stops being
the safer default and becomes the only responsible one — the panel can poll
`ptt-status`, or watch it over the state stream, to know the window.

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

Both original questions are now answered, and not as hoped:

1. ~~Is `PlayLocal` audible while MOX is on?~~ **Moot — `Playback` is null.**
   Even had it existed, the drain is gated behind `ShouldPublishNormalRxAudio`,
   which is false while transmit suppresses RX audio, so it would not have been
   audible while keyed anyway.
2. ~~What does `FrequencyHz` report under split?~~ **Moot — `Radio` is null.**
   `GET /api/state` exposes `splitEnabled` and `splitTxHz` separately, which is
   a better answer than the contract could have given.

What remains open:

3. **Is `/api/state` stable?** It is the engine's own API, not a plugin contract,
   so nothing promises it. Depending on it means tracking engine releases —
   acceptable, but it should be an explicit decision, and `schemaVersion` in the
   ptt response suggests upstream thinks about compatibility here.
4. **How does the panel reach the engine?** Same origin is likely but unverified.
   If not, a backend route proxying `/api/state` is a two-line fallback.
5. **Live updates or polling?** `StreamingHub` exists; whether state and PTT are
   on it is unchecked. Polling `ptt-status` at a few Hz would work but is crude.
6. **Opus in the browser.** UberSDR's own client uses `OpusDecoder`; the panel
   would need the same, and it must be vendored rather than fetched from a CDN.
7. **Courtesy.** Unchanged: connect around transmit, honour `available_clients`,
   and ask upstream before polling the directory from every install.

## Why this one first

Everything it needs exists — just not where this document first assumed. Radio
state and keying come from the engine's HTTP API, audio and SNR from UberSDR's
WebSocket in the panel, receiver choice from the public directory. The plugin
contract contributes settings, a route and a panel, and nothing else.

It is still the right first build: it is the only one of the ten use cases whose
every dependency has now been verified at runtime rather than read hopefully off
an interface.
