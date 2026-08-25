# Remote monitor — design

**A wall of stations all listening to your transmission.** Every one shows a live
signal level while you are keyed; when you unkey, each has a recording of your
over that you can play back.

Specified by the operator, and the specification is better than the design it
replaced: it drops live audio during transmit, which removes the feedback problem
outright, and it turns out to map onto the protocol almost exactly.

## Why this scales to many receivers

UberSDR's audio frames are binary, with a fixed header before the Opus payload:

```
byte  0–7    timestamp       uint64 LE
byte  8–11   sampleRate      uint32 LE
byte  12     channels        uint8
byte  13–16  basebandPower   float32 LE      ← signal
byte  17–20  noisePower      float32 LE      ← noise floor
byte  21+    Opus payload
```

`signalSNR = basebandPower - noisePower`, in dB. `-999.0` in either field means
*invalid* and must be shown as no reading rather than as a very poor one.

The published client says so itself: *"Signal metrics are included so followers
can update signal bars and SNR charts **from the frame header alone**."*

That is the whole reason a long list is practical:

| While keyed | Read 21 bytes per frame, append the Opus payload to a buffer. **No decoding.** |
| After unkey | Decode one receiver's buffer — the one the operator picked. **One decoder, on demand.** |

Twenty receivers metering live costs twenty header reads per frame interval and
some memcpy. Opus at typical rates is a few kB/s, so a 30-second over is well
under 100 kB per receiver — the whole wall fits in memory without thought.

Had the design needed live audio from every receiver, it would have needed N
concurrent Opus decoders and would not have scaled past a handful. The
operator's version is cheaper *and* safer.

## Shape

```
engine /api/radio/ptt-status ──► key down
engine /api/state            ──► splitTxHz when splitEnabled, else vfoHz
                                        │
        ┌───────────────────────────────┴───────────────────────────┐
        ▼                    ▼                    ▼                 ▼
   wss://rx1/ws         wss://rx2/ws         wss://rx3/ws  …   wss://rxN/ws
   tune → TX freq       tune → TX freq       tune → TX freq
        │                    │                    │
   header → S bar       header → S bar       header → S bar     ← live, while keyed
   opus  → buffer       opus  → buffer       opus  → buffer
        └───────────────────────────────┬───────────────────────────┘
                                key up  ▼
                        play back any one of them
```

## What the numbers mean, and do not

The figure available is **SNR in dB**, not an absolute signal level. That matters
for how it is labelled:

- **Comparing one receiver against itself over time is sound.** Antenna A versus
  antenna B, before and after a tuning change, more power versus less — same
  receiver, same noise floor, so the difference is yours.
- **Comparing receivers against each other is not.** A quiet rural receiver shows
  a better SNR than a suburban one for the identical signal. Ranking the wall
  by SNR would say more about their noise floors than about your antenna.

So the wall should show each receiver's **own** reading and its change across
transmissions, and must not present a leaderboard implying "this station hears me
best". Calling it "S level" invites exactly that reading; the panel should say
SNR, in dB, and say what it is relative to.

Absolute S-units are not available: they would need each receiver's gain
calibration, which the directory does not carry.

## Feedback: no longer a problem

The earlier design wanted live remote audio during transmit, which with an open
microphone is a delayed howl put on the air. This specification does not: nothing
is audible while keyed, and playback happens after unkey with the microphone
closed. The hazard is designed out rather than warned about.

The one remaining care: **playback must stop if the operator keys again.** Watch
`ptt-status` during playback and pause on key-down.

## Radio state

From the engine's own HTTP API, since the plugin contract provides none of it:

- `GET /api/state` — `vfoHz`, `mode`, `splitEnabled`, `splitTxHz`
- `GET /api/radio/ptt-status` — `moxOn`, `tunOn`, `hangTimeMs`

**Tune to the transmit frequency, not the VFO**: `splitTxHz` when `splitEnabled`
is true, `vfoHz` otherwise. Getting this wrong under split monitors an empty
frequency and reports that nobody hears you — a silent, plausible, wrong answer.

## Open questions

1. **Protocol version.** The header layout above is version 2/3; the client
   negotiates and normalises `noisePower` accordingly. Establish what a plain
   connection negotiates and refuse to meter a version we do not understand
   rather than misreading a float.
2. **Tune latency.** How long between `tune` and the first frame on the new
   frequency? If it is seconds, receivers must be tuned *before* key-down —
   which means watching the VFO, not just the key.
3. **Slot cost.** N receivers is N client slots on other people's hardware.
   Connect on key-down and release after playback, cap the default list, and
   honour `available_clients`.
4. **Panel or backend?** Backend keeps CORS and vendoring out of it, but must
   then stream audio to the panel for playback. Panel is simpler and WebSocket
   ignores CORS. Decide once phase 0 answers whether the panel can reach the
   engine API.
