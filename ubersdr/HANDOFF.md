# Remote Ears — handoff, last updated 2026-08-28

State of the Remote Ears panel: your own receiver in one ear, whichever remote
station is confirmed to be carrying the same QSO in the other.

Installed build: **`2026-08-28T15:11`**. The panel prints its build stamp at the
bottom — check it before trusting any test (see *The trap* below).

## What the panel does now

Left ear carries your own receiver, right ear carries whichever UberSDR station
copies your frequency best, delayed into step with the left.

| Piece | State |
|---|---|
| Station named by callsign, town, distance | **works** |
| Mute per ear, right muted by default | **works** |
| Right ear silent while transmitting | built, **not yet confirmed on the air** |
| **The whole two-ear path, end to end** | **working on the air, 2026-08-26** |
| Receiver switching with hysteresis | built, tested synthetically |
| Two-ear time alignment | **works at good signal levels** |
| Candidate screening before promotion | built, tested synthetically |
| Remote monitor during transmit (opt-in) | built, untested on the air |
| Local level restored independent of Zeus's volume | built, **outcome ambiguous** |
| Stacked dB VU meters, one bar per station | built, **not yet seen running** |
| Click a station to listen to it (pinning) | built, not yet exercised |
| Inline power toggle | built |

### Receive-only

A 150 ms `/ptt` poll drives a `keyed` flag; the socket handler drops frames when
either `mutedRight` or `keyed` is set, checked *before* the Opus decoder so
nothing is queued that would have to be cancelled. Remote audio during transmit
is your own voice arriving a second late in one ear — the delayed-auditory-
feedback effect that disrupts fluent speech, and a howl on the air if it reaches
a microphone. Confirmed the `/ptt` route answers on the running engine; **never
seen go true under an actual transmission.**

### Switching hysteresis

Was a bare argmax on instantaneous SNR re-run every second, which flapped
continuously — SSB speech swings measured power by more than the difference
between two decent receivers ever will. Now a challenger must clear **3 dB**,
hold that lead for **6 s**, and the ear must have been held **10 s**. Two
exemptions: an empty ear fills instantly, and a source that falls silent is
replaced instantly. A pending switch is announced while it is being earned.

The SNR average is time-based (τ = 3 s) rather than per-frame — a per-frame
coefficient silently becomes a different filter on a receiver using a different
Opus frame size.

### Alignment

The remote ear runs 0.3–2 s behind. Waveform cross-correlation cannot work here:
two SSB receivers differ by a few Hz of reference offset, which is inaudible but
shifts the whole audio spectrum. What survives is the **amplitude envelope** —
syllable rhythm is the same on every receiver hearing the same transmission.
Decimated to 100 Hz, log-compressed, correlated over ±3 s.

Envelopes are stamped with **playback** time, not arrival time. That is the
reliability of the whole thing: arrival skew is not what you hear, and stamping
with the scheduled AudioContext start folds in both jitter buffers, every gap
and every restart without modelling any of them.

### Screening: nothing reaches the ear unverified

SNR says a receiver is hearing something loud on your frequency. On a busy band
that is routinely a *different station*, and promoting on level alone puts a
stranger in one ear with nothing to signal it. So every connected candidate is
correlated against the local receiver **continuously**, not only when promoted —
a receiver cannot be checked in the instant it goes into the ear, because by
then it is already there.

Candidates are screened from their **frame headers**, which state baseband power
outright, so no Opus decoder runs for a candidate that is not being heard. A
promoted receiver arrives with its offset already measured, so it starts in step
instead of drifting in over ten seconds. Only receivers clearing
`SYNC_MIN_QUALITY` are eligible; if none qualify the right ear stays silent, and
the panel says *"no receiver confirmed on your signal yet"* rather than showing
nothing.

A receiver that loses sync stops being the incumbent, so the switching
hysteresis cannot defend it — that hysteresis exists to protect a *good* choice.

### A real bug this surfaced

`estimateLag`'s guard band — the window inside which two peaks count as the same
peak — was 0.25 s, which is about one syllable period. That let the adjacent
syllable peak be absorbed into the main peak instead of counted against it, so a
cycle slip of exactly one syllable looked confident. Measured: r = 0.75 on a lag
that was 240 ms wrong, and a different station passing the screen outright. The
guard is now 0.08 s.

### The panel, as it now looks

```
 (power)  local L+R  ##############     -94 dBm   7.142 LSB . S4 . S/N 16

 +--------+ +--------+ +--------+
 |> PD2RPS| | HEPPEN | | F4VTQ  |
 |_-=#=-_-| |_-=-_-__| |__-_____|   <- 14 s of level history
 |  -77   | |  -89   | |  -115  |
 | -12 v  | | -15 v  | | -16 x  |   <- dB vs the operator, sync state
 +--------+ +--------+ +--------+
```

The local receiver keeps a full-width bar: it is compared with nothing, so it
does not need a tile. Each remote gets one tile carrying a **sparkline of the
last 14 seconds** rather than a present-tense meter — a receiver that has just
gone quiet and one whose socket has died look identical in a single reading, and
different in a sparkline.

The playing tile carries a green edge and a caret, amber while transmitting with
the monitor on; an unconfirmed tile is dimmed. Click to listen, click again to
mute, `x` to block for good.

**Everything wordy lives in the tooltip** — location, distance, bearing, S/N
with its delta against the operator, correlation, and what a click will do. The
tile carries only callsign, history, level, delta and state.

Three tabs sit under the local bar, working on the air as of 2026-08-26:

- **console** — the tiles above.
- **bearing** — receivers at true bearing and range, rings at 100/500/2500 km
  on a log radius so 59 km and 2500 km share a screen. Filled = confirmed,
  hollow = not, heavy spoke = playing. Click a confirmed dot to listen. Note the
  directory's `bearingDegrees` is computed from the *caller's* IP geolocation,
  which was 28 km off for this station — negligible at 200 km, visible at 60.
- **S/N over time** — every feed overlaid on one axis, the operator's own radio
  as the heavy amber line, refused receivers dashed, ~45 s of history.

**The overlay plots S/N, not absolute level, and that is not cosmetic.** The
engine reports calibrated dBm and ka9q reports its own baseband power with no
calibration between them; putting those on one axis is the exact mistake that
produced a night of wrong conclusions and an unnecessary antenna hunt. S/N is
comparable because each side subtracts its own noise floor. The absolute figure
stays on the tiles and in the tooltip. The note under the graph says so, so that
nobody "improves" it back into dBm later.

Two dropdowns sit in the controls row rather than behind a settings pane,
because each receiver added is a client slot on somebody else's hardware: how
many to monitor (2-8), and which to consider (all directions / close by / far
away / Europe / North America / South America / Asia / Africa / Oceania). The
regions are plain lat-lon boxes — no country table to maintain and no opinion
about where a border runs.

### The local bar now meters RF, from the engine's own telemetry

**Correction to what this file said earlier.** It claimed no local RF figure was
readily available and that the only route was integrating the `DisplayFrame`
spectrum. That was wrong — it came from searching `/api/state` and the `Status`
frames and stopping there. The engine publishes RX telemetry on the same
WebSocket, unrequested, at 5 Hz:

- **`RxMetersV2` (MsgType 0x19)**, 33 bytes, bare payload. Carries
  `signalMaxBin` at offset 29 — the strongest single FFT bin inside the
  *active* passband, 0.5 s decaying peak hold, already carrying the per-board
  calibration (+0.98 dB on a Hermes-Lite 2). The engine reconfigures the
  detector on every mode, filter and CTUN change, so the window follows the
  filter. This is Thetis's `%signal_max_bin%`.
- **`RxSignalQuality` (MsgType 0x3F)**, 18 bytes. `snrDb`, `signalOnlyDbm`,
  `integratedNoiseDbm`, `confidence`. NaN fields or zero confidence mean
  unavailable and are refused rather than displayed; a *negative* SNR is a
  valid below-noise reading, not an error.

The local bar is now driven by `signalMaxBin` on a standard S-unit scale
(S0 = −127 dBm, S9 = −73 dBm, top S9+60), showing calibrated dBm and S-units.
The decoded-audio level remains only as a fallback, marked `AF`, for an engine
too old to send 0x19.

`tools/meters-check` round-trips both frames against bytes built to the
contract, and `package.sh` gates on it. Byte offsets are the classic silent
failure here: read `signalAv` where `signalMaxBin` belongs and the meter still
moves, still looks plausible, and is simply the wrong number.

**Zeus has no engine-side meter-source setting** — it computes and broadcasts
peak, average and max-bin in every frame, and the choice is purely a frontend
one. In Zeus Link the operator cycles it by clicking the dBm readout on the bar
S-meter, or the PK / AVG / BIN badge on the analog meter. (The `wwwroot` SPA the
engine serves has no reference to `maxBin` at all — that badge is Zeus Link's
own frontend.)

### Local and remote bars are still not comparable in absolute terms

The remote bars are comparable *with each other* because they are all the same
quantity measured by the same software. The local bar is audio level after this
panel's own level restoration — a different chain, a different AGC, no shared
calibration with any ka9q receiver. Putting them on one axis would produce a
comparison that reads fine and means nothing.

Both sides are now RF, but the references still differ: the engine reports
calibrated dBm from your own front end, ka9q-ubersdr reports its own baseband
power, and nothing ties the two scales together. So the bars remain separated by
a hairline and the panel says plainly that absolute levels are not comparable.

**The S/N figures are.** Each side measures its own noise floor and subtracts
it, so signal-above-noise needs no shared calibration. The local row now carries
`S/N` from `RxSignalQuality` beside the remote rows' `S/N` from ka9q's
`basebandPower − noisePower`. That is the honest common axis, and the next
useful step is to rank and screen on it rather than on raw level.

### The recorder, and what it overturned

`tools/envelope-record/record.mjs` captures a minute of both sides on one clock:
the local audio envelope at 100 Hz, the engine's `RxMetersV2` and
`RxSignalQuality` tracks, and every candidate's per-frame baseband and noise
power. `analyse.mjs` replays it through the real screening code; `calibrate.mjs`
sweeps thresholds against a null distribution built from the same recording.

It was built because every conclusion drawn from a single on-screen reading in
this project has been wrong at least once. It immediately overturned three more.

**1. The remote S/N figures were not S/N.** ka9q reports `basebandPower` as
total power in the passband but `noisePower` as a spectral *density* in dBm/Hz.
Subtracting them leaves `10*log10(bandwidth)` behind. Proved from the data
rather than assumed: a receiver hearing only noise, whose true SNR must be ~0,
showed exactly 34.4 dB — that is 10*log10(2754), the SSB filter measured back
out. `readSnr()` now subtracts it.

The correction reverses the picture completely. Uncorrected, three remotes read
33-43 dB against the operator's 16 and he was told to check his antenna.
Corrected: **local 16 dB, remotes 4, 1 and 0.** His own receiver was the best of
the four the whole time.

**2. The screening window was far too short.** Pairing each receiver with the
operator's audio at the same instant (true) and with its own time-reversed
envelope (impossible, identical statistics) gives a null distribution from real
signals. At the 10 s window then in use, the two are indistinguishable — median
quality 0.27 against 0.25. The null collapses as the window grows: 0.17 at 20 s,
0.16 at 30 s, 0.11 at 45 s, while true matches hold at 0.27 throughout.

Old settings scored **16% of true matches confirmed at 24% false alarms**. The
calibrated operating point — 45 s, quality >= 0.20, margin >= 0.05, depth >=
0.15 — scores **86% at 0%**. `ROTATE_AFTER_MS` went 90 s -> 300 s, because
dropping a receiver before it can be judged guarantees it never is.

The margin turned out to discriminate better than the quality: true pairs median
0.18 against 0.01 for impossible ones.

**3. The 0.84 s split between the ears was self-inflicted.** Arrival skew
between the two streams, measured over a full minute, is 0.02-0.04 s. Everything
audible beyond that came from this panel's own scheduling.

### Two bugs the operator heard, both mine

**An alignment ratchet.** Alignment delays the earlier ear to match the later
one, and measured on an 8 s window — the length just proved to have no
discrimination at all. Its output *is* the playback offset, so a wrong
measurement makes itself true: delay the left ear two seconds and the next
measurement dutifully reports two seconds. It never recovered, it accumulated.
Fixed by measuring on the calibrated 45 s window, capping the search at 1.0 s
(arrival skew is 0.04 s; anything beyond a second is the loop), and putting a
hard ceiling on the jitter buffer.

**Queue trimming as flushing.** The first bound on queue depth stopped every
pending source and restarted — which corrected the backlog and left an audible
hole each time. It shipped with a test that reported "1 trim per minute" and
nobody asked what a trim sounds like. It now drops one arriving 20 ms frame
instead, and the queue drains on its own: a 1% fast sender loses 0.5% of its
frames and nothing is ever cut.

### A third thing the operator heard: the station going grey

Reported as the remote "dropping out regularly and coming back", with the bar
turning grey — which is the selection giving the ear up, not the audio failing.
Two causes, both in the ranking:

**A pin did not pin.** The documented behaviour is that manually choosing a
receiver bypasses the "must beat your own receiver by 3 dB" rule — that is the
entire point of the exception. The code checked the pinned host against the list
that had *already applied* the rule, so whenever the operator's own receiver was
doing better, the pin was silently ignored. The implementation did the opposite
of its own comment.

**And the threshold chattered.** One hard bar between two fluctuating
measurements: in a pause the operator's S/N falls, a remote clears the bar and
is adopted; his S/N recovers and it is dropped. Every few seconds. Now 3 dB
better to earn the ear and 1 dB worse to lose it — the same hysteresis that
already governed switching *between* receivers, which nobody had applied to the
boundary of eligibility itself.

Diagnostic worth keeping: **grey means a selection decision, a bar falling to
zero means the socket.** They look similar and have nothing to do with each
other.

### It works

Confirmed by the operator on 2026-08-26 with build `21:25`: remote ear on, bars
grey when nothing is playing and green when it is, local receiver in both ears
until a remote is confirmed and then split left/right, selection automatic.

The last three faults were all display rather than DSP, and all mine:

- A **pin that did not pin** — checked against the list that had already applied
  the "must beat your own receiver" rule, so it was silently dropped exactly
  when it was wanted.
- **A threshold with no hysteresis** on that same rule, so a receiver was
  adopted and dropped every few seconds as the two fluctuating S/N figures
  crossed. Seen as a station "going grey and coming back".
- **A muted receiver coloured green.** Green means "this is what you are
  hearing"; with the right ear muted nothing was. The bars kept moving because
  candidates stay connected to be measured — which is necessary, since
  disconnecting on mute restarts the 45 s clock and nothing is ever confirmed —
  but the colour said the control had failed when it had worked.

Also settled: **Zeus does not re-assert an external AF gain write.** The first
test of this was worthless — the gain was already at -50 and it was set to -50,
which proves nothing. Re-run from -50 to -20, held for three seconds, restored.
The panel now silences Zeus on start and restores the operator's own level on
stop or unmount, capturing that level only when it is above the silence value so
a hand-lowered slider cannot be recorded as the level to return to.

Note for anyone testing: **while the panel runs, Zeus's volume slider does
nothing audible**, because the level restoration compensates for it. That makes
it a misleading thing to test with. Switch the panel off first.

## Open — and the reason this note exists

### 1. Correlation is poor at low signal levels

Reported on the air at the end of the session. The alignment locks on strong
signals and degrades as the signal weakens. Expected, and not yet addressed.

The cause is that a weak signal's envelope is mostly **noise**, and noise is
uncorrelated between two receivers by definition. Three things make it worse
than it needs to be:

- `prepare()` applies `log1p(v * 1000)`, which **expands** low-level variation.
  Exactly the wrong compression when the low level is noise. There is now direct
  evidence: the same signal measured two ways (audio RMS vs frame header) agrees
  on lag exactly but correlates only 0.49, because the two disagree loudly about
  the pauses where there is nothing to agree about. Fixing the compression
  should lift that towards 1 and is the single highest-value change here.
- Every block in the window counts equally, including the ones carrying nothing
  but noise from both receivers.
- Each receiver's AGC rides its own noise floor differently, so the noise
  envelopes are not merely uncorrelated, they are actively different shapes.

Ranked by expected value for the effort:

1. **Gate on speech presence.** Only correlate blocks where *both* channels are
   above their own noise floor. The UberSDR frame header already carries
   `basebandPower` and `noisePower`, so the right ear's SNR is known per frame
   and need not be estimated. Cheapest real win.
2. **Band-pass the envelope to 2–10 Hz** before correlating. Syllable rate is
   where the timing information lives; everything above it is jitter that
   carries none. A one-pole pair either side would do.
3. **Accumulate correlation functions across windows** instead of taking the
   peak of a single one. Uncorrelated noise averages down, a real peak does not.
   Costs nothing in responsiveness if the accumulator is leaky.
4. **Lengthen the window** from 8 s. Simplest, and the least good: it trades
   responsiveness for averaging, and it does not fix the noise-block problem,
   it only dilutes it.

Do not raise `MIN_QUALITY` to hide this — refusing to align a weak signal is
the current behaviour and it is the *safe* failure, not the fix.

### 2. Is Zeus's own player actually silent?

**Unresolved, and it is the one thing blocking "left ear only local, right ear
only remote".**

Measured on the running engine, in this order:

- `/api/audio/native` → `supported: false`. The engine's native sink is off.
- The engine's RX master mute (`/api/audio/mute`) **stops the WebSocket audio
  lane**: 45 frames in 1.5 s unmuted, 0 muted. So Zeus's mute button takes the
  panel's left ear with it. Unusable.
- `/api/rx/afGain` at −50 dB → appeared to have **no effect** on the lane.
  **This reading was wrong** — see below.
- Zeus's own **volume slider** → the lane fell 0.70 → 0.002 and back, cleanly,
  frames flowing at 30/s throughout. So the slider is a **server-side gain
  applied upstream of the broadcast**, a factor of ~350 (−51 dB).

That last one is what the level restoration exploits: the slider attenuates but
does not stop or quantise the lane, and the samples are float32, so scaling back
up is lossless. Hence: turn Zeus's slider down, and the panel restores its own
copy for the left ear.

**What was observed:** with the slider down, local audio did *not* go silent.
That is equally consistent with the design working (the panel restoring the
level) and with Zeus playing through some other path entirely.

**The one-click test that was never run:** with Zeus's volume down, press
`mute left` in the panel.

- Local audio disappears → working as designed, nothing more to do.
- Local audio persists → Zeus has an audio path that is not the WebSocket lane,
  and the model above is incomplete. Chase it from there.

Also unrecorded: whether the local audio is **centred or hard left** with both
ears unmuted. Centred means Zeus is still contributing.

### 2b. Zeus's volume slider IS `/api/rx/afGain`

Found in Zeus Link's own log (`~/Library/Application Support/Zeus/logs/zeus-app*.log`),
which records every route the app calls. A slider drag appears verbatim:

```
api.rx.afGain db=17.0   db=9.0   db=5.0   db=20.0   db=15.0
api.rx.afGain db=-5.0   db=-11.0   db=-1.0   db=-50.0
```

So the earlier conclusion that "AF gain does not touch the lane" is false: it is
the very control whose effect was measured dropping the lane 0.70 → 0.002. The
direct test almost certainly failed because **Zeus re-asserts its own slider
value** within milliseconds of an external POST, restoring the level before the
two-second sample completed.

**This is the route to suppressing Zeus's own audio from inside the plugin** —
one POST of `{"db": -50}` on start, restore on stop, with the panel's makeup
gain bringing the left ear back. Before building it, settle with Zeus running:

1. POST `{"db": -50}` and immediately poll `/api/state` for `rxAfGainDb`. If it
   returns to the slider's value on its own, Zeus is re-asserting and the plugin
   cannot simply hold it down.
2. Watch `zeus-app.log` while doing it — if Zeus posts afGain unprompted, the
   log says so.

If Zeus does re-assert, the fallback is unchanged: ask the operator to lower the
slider themselves.

**Restoring is mandatory.** Leaving AF gain at −50 dB with the plugin stopped
looks exactly like a dead radio.

### 3. RX AF gain was changed and not restored

While testing whether AF gain was upstream of the lane, `/api/rx/afGain` was
posted with a field named `gain`; the endpoint expects `Db`, so both calls
silently defaulted to **0 dB** — including the one meant to restore it. It is
now 0 dB and the original value is unknown. Worth setting back if 0 dB is not
where it belongs.

## The trap that cost most of the evening

Zeus serves panel modules with `Cache-Control: no-store, no-cache` — and it does
not matter. Panels are **ES modules on a stable URL**, so once the page has
imported one, the browser keeps that module record for the life of the page.
**Reopening the panel re-uses the cached module. Only restarting Zeus reloads
it.**

Four rounds of fixes were installed and tested against a page still running the
first build. It presented as three unrelated bugs failing simultaneously. That
pattern — several unrelated things broken at once, right after a reinstall — is
the signature; read it as a stale module before debugging any of them.

`tools/package.sh` now writes `ui/build.js` and the panel renders `build <stamp>`
at the bottom. Ask what it says.

## Tests and gates

`tools/package.sh` refuses to package unless all of these pass:

- `tools/panel-check` — renders every panel with stub React, effects and all
- `tools/ear-check` — drives the switching rule through a simulated minute;
  proves a 1 dB lead trading hands every second never moves the ear
- `tools/meters-check` — round-trips the engine's RxMetersV2 (0x19) and
  RxSignalQuality (0x3F) frames against bytes built to the contract
- `tools/queue-check` — a 1% fast sender must not grow the delay without bound,
  and must pay for it in dropped frames rather than gaps
- `tools/align-check` — two synthetic receivers on carriers 3 Hz apart recover
  0.35 / 0.8 / 1.5 / 2.4 s delays to within 30 ms; **also asserts the naive
  waveform correlation fails** (r = −0.11 at the true, known lag), so a future
  change that swaps envelopes for waveforms cannot pass
- level restoration: gain 241 on a full-scale signal peaks at 0.950, not 168;
  gain falls 241 → 5 within 200 ms; silence holds the gain

**And the C# suite is now in the gate too** — it was not, for the first fifty
builds, which is how a package was twice installed with a failing test while the
JS checks reported green.

Not gates, but the tools to reach for when something is in doubt:

- `tools/engine-probe` — asks a *running* engine the questions the design
  depends on; `--afgain` additionally tests whether Zeus re-asserts a write
- `tools/envelope-record` + `analyse.mjs` + `calibrate.mjs` — capture both sides
  of the correlation on one clock and replay them against the real screening
  code, as often as needed, without occupying anyone's receiver again
- `tools/zeus-harness` (in `wavelog/`) — a real engine from nothing

Plus 63 C# tests including `RouteCoverageTests`, which greps the routes the
panels call and asserts the backend registers them — added after an edit
silently deleted `MapPost("connect")` and 150 POSTs 404'd in silence.

## 2026-08-28 — the panel became one row, and three things broke on the way

The panel is now a **single row** with the full layout behind an expand control,
and the settings behind a cogwheel in the modal's title bar. That was the
operator's idea and it paid for itself immediately: with only one panel there is
only one session, so the entire shared-ownership machinery — owner election,
snapshots, listeners, mirroring state, routing controls — could be deleted
rather than maintained.

```
(power)  > PD2RPS  /\/-\/\-/\/     -12 dB   (expand) (settings)
```

Callsign, a two-trace graph of the last ~30 s (the operator heavy, the receiver
he is hearing light), and the delta. Everything else is in the tooltip.

### Verified on the air

1. The single row, both traces moving. **passed**
2. Expand modal, all three tabs. **passed**
3. Settings: region filters validated against the live directory — *Europe* gave
   PH5HP / HEPPEN / F4VTQ, *far away* gave PT2 and K5. **passed**
   (Open: whether lowering 6 -> 3 sheds receivers. Fixed but never confirmed.)
4. AF gain start/stop/close. **passed**, but see below.

### Not yet verified

5. Whether an old `ubersdr.min` layout entry still renders (the id is kept
   registered for exactly this, though it is no longer offered).
6. The right ear going silent on transmit.
7. The opt-in transmit monitor.

Plus a **re-test of 4** against build `15:11`, which is where the last fix
landed.

### Three regressions, all introduced while improving working code

**A dangling reference crashed the panel on switch-on.** Removing the session
machinery left `following ? src.rows?.length : ...` in the one branch that runs
while listening but before anything is confirmed. `panel-check` rendered the
idle state and never pressed anything, so it never evaluated that line.
*It now clicks every `onClick` it renders and re-renders afterwards.* Verified by
reintroducing the bug: `FAIL ReferenceError: following is not defined`.

**An AGC on the local ear flattened signal strength.** The level restoration was
adaptive — it aimed at a fixed target peak, so a weak station came out as loud
as a strong one. That destroys the thing an operator judges by ear. It dated
from before the panel set the AF gain itself and therefore had to guess how much
had been taken away; once the panel applied the attenuation it knew the number
exactly. It now puts back precisely `10^((cur - (-50))/20)` and nothing else.
The adaptive path survives only as a fallback for a host where the AF gain
cannot be read or written.

**The makeup gain was computed from the wrong number and produced a wall of
noise.** It used the *stored* restore value rather than the level read a moment
before the write. Those differ by however stale the stored one is: +20 stored
against -6 actual is twenty times too much gain. It now measures from the fresh
reading, and applies nothing at all if the write did not succeed — because then
nothing was attenuated and there is nothing to put back.

### Two gaps in the harness, both now closed

- `panel-check` rendered but never interacted. It presses now.
- `package.sh` gated on the JS checks and **not** on the C# suite, so a package
  was twice installed with a failing test while everything looked green. The
  suite is in the gate; verified by breaking a test on purpose and watching it
  refuse.

### And the operator lost his volume control

The panel silences Zeus while it runs, so Zeus's slider does nothing audible —
which means the operator had no way to set the listening level at all. There is
now an **output level** slider in the settings, applied after the limiter and
never above unity, so it can only ever make things quieter than the safe
ceiling. 100% means "exactly as loud as Zeus was".

## Next session, in order

1. **Re-test the AF gain flow against build `15:11`.** Panel off, AF gain at your
   normal level, switch on — the moment of pressing should be *inaudible* in
   loudness. Then tune between a strong and a weak station and check the
   difference is still audible rather than regulated away.
2. **Confirm 6 -> 3 sheds receivers.** One dropdown change, fixed but unverified.
3. **The transmit tests (6 and 7).** Still the largest untested piece, and the
   only one that needs the operator to key up: does the right ear go silent, and
   does the opt-in monitor work.
4. **A second recording for the calibration.** The 45 s window and the
   quality >= 0.20 / margin >= 0.05 / depth >= 0.15 thresholds rest on one
   minute of one QSO on 40 m. `record.mjs` and `calibrate.mjs` are ready;
   weaker signals or another band would show whether they hold.
5. **Antenna A/B** once the MX-S3 cable arrives.

## A note on how this went

Three regressions on 2026-08-28 all came from tidying or improving code that
already worked, and all three reached the operator rather than a test. The
pattern across the whole project is narrower than that, though, and worth
keeping in front of whoever picks this up:

**Almost every real defect here came from believing something read rather than
something run.** The S/N figures that were not S/N. The screening window too
short to separate truth from chance. The alignment that confirmed its own error.
The queue bound that corrected the backlog by cutting the audio. Each was found
by measuring, none by reasoning — and in several cases a confident explanation
had already been given to the operator and was wrong.

The tools exist because of that: `zeus-harness`, `panel-check`, `ear-check`,
`align-check`, `meters-check`, `queue-check`, `engine-probe`, `envelope-record`
and `calibrate`. When something here is in doubt, the answer is to run one of
them, or to write the one that is missing.
