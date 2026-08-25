# UberSDR audio protocol — verified against live instances

Phase-0 gate for the remote monitor. Everything here was **measured**, not read
off the client: two live instances, one with an antenna and one without.

## Connecting

Three steps, and the first two are not optional.

**1. Generate a session id.** A plain client-side UUID v4. There is no handshake
and nothing signs it.

**2. Admission control.** `POST https://<host>/connection`, body
`{"user_session_id": "<uuid>"}`:

```json
{"client_ip":"…","allowed":true,"session_timeout":0,"max_session_time":0,
 "bypassed":true,"allowed_iq_modes":["iq48","iq96","iq192","iq384"]}
```

Skip it and the socket is refused with *"Invalid session"*. **This is where an
instance enforces its client limit**, so it is also where a polite client learns
it is not welcome — honour a refusal instead of retrying.

**3. Connect, with the parameters in the query string:**

```
wss://<host>/ws?frequency=<Hz>&mode=<mode>&user_session_id=<uuid>&format=opus&version=2
                                                                        [&muted=1]
```

`tune` **retunes an already open socket** — it cannot open one. Connecting to a
bare `/ws` and sending `tune` fails with *"Invalid or missing user_session_id"*.

## Version 2 only

`DEFAULT_PROTOCOL_VERSION` in the published client is 2, and it can request 3.
Against server 0.1.58, **version 3 connects, returns a `status` message, and then
sends no audio at all** — 0 frames in 8 seconds. Version 2 streams normally.

So: request 2, and treat a version that yields a status but no frames as
unsupported rather than as a dead receiver.

## Frame layout — confirmed byte for byte

```
byte  0–7    timestamp       uint64 LE
byte  8–11   sampleRate      uint32 LE     12000 or 24000 observed, mode-dependent
byte  12     channels        uint8         1
byte  13–16  basebandPower   float32 LE
byte  17–20  noisePower      float32 LE
byte  21+    Opus payload
```

A live capture, tuned to 9.5 MHz AM:

```
c0 5d 00 00   → 24000 Hz        01 → 1 channel
00 00 80 ff   → -Infinity       00 00 80 ff → -Infinity
```

`snr = basebandPower - noisePower`, in dB.

## The invalid sentinel is -Infinity, not -999

The client guards with `> -900`, which happens to catch it, but the value on the
wire is `0xff800000` — **negative infinity**. Test for "not finite", not for
`== -999.0`.

It appears in two situations, both of which a monitor must render as *no
reading* rather than as a bad signal:

- **the first frame or two**, before measurement settles;
- **for the whole session on an instance with no antenna.**

## Measured

| | quiet instance, no antenna | live instance, antenna, 10 MHz AM |
|---|---|---|
| frames/s | 10 | 50 |
| Opus payload | 8 bytes | ~108 bytes |
| bandwidth | 0.3 kB/s | ~5.4 kB/s |
| **30-second over** | **8 kB** | **~196 kB** |
| tune → first frame | 199–201 ms | **7 ms** |
| SNR | `-Infinity` throughout | 34.7 – 55.7 dB, varying |

**Buffering a wall of receivers is cheap.** Twenty receivers recording a
30-second over is roughly 4 MB of Opus held in memory, undecoded. That is the
number that makes the operator's design work.

**Tune latency is not a problem.** Under a fifth of a second in the worst case
measured, 7 ms in the best. Receivers can be tuned on key-down; pre-tuning on VFO
change is an optimisation, not a requirement.

## A note for this station

`ubersdr.on8st.be` reports `antenna_connected: false` and `snr_0_30_mhz: -1`, and
every frame it sends carries `-Infinity` for both power fields. Audio flows and
the protocol is fine — but **it can report nothing about signal strength until an
antenna is connected**, so it cannot take part in its own operator's monitor wall
in that state.

That is worth surfacing in the panel: an instance with no antenna should be shown
as unavailable for metering rather than as a receiver that hears nothing.
