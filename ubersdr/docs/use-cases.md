# UberSDR — ten use cases worth building

Ranked by how much they'd change an operating session. Each notes whether it
**needs several receivers at once**, which is the capability that makes UberSDR
different from a single web SDR.

What the plugin can work with: Zeus's frequency, mode and MOX (readable, with
events); N simultaneous WebSocket audio/spectrum streams in the panel; the
directory and per-instance REST from the backend. What it cannot do: put audio in
Zeus's DSP chain, retune Zeus, or create spots.

---

**1. Hear yourself as others hear you.** MOX goes true, and the panel is already
listening on your transmit frequency through two or three receivers at different
distances. Audio and spectrum, live, while you speak. Splatter, overdriven audio,
a bad ALC setting — all obvious from outside and invisible from inside. *Multiple
receivers: yes, and it is the point — one nearby for audio quality, one distant
for what DX actually gets.*

**2. Antenna A/B with an outside referee.** Your instance is literally named "on a
MicroHAM Antenna Switch". Transmit a carrier, switch antennas, and read the SNR
change at a receiver 500 km away instead of guessing from your own S-meter. *Yes —
several bearings at once turns a single number into a pattern.*

**3. Is it me or the band?** Your noise floor is S7 on 40m. Compare it against
receivers 20, 200 and 2000 km away on the same band, right now. Local QRM and a
noisy band look identical from one station and completely different from four.
*Yes.*

**4. Where is this band actually open?** Zeus tells the plugin your band; the
directory gives SNR, noise floor and bearing for 50-odd receivers. A map of who
is hearing what, on your band, this minute — measured rather than predicted.
*Yes — one receiver is an anecdote.*

**5. Diversity copy on weak DX.** Same frequency, three receivers, three
positions. Pick the best copy or listen to two at once; QSB rarely fades
everywhere simultaneously. *Yes — this is impossible with one.*

**6. Listen where the DX is.** Work a pileup by hearing it from the DX station's
side: a receiver near them tells you where they are actually listening and who
they are coming back to. *No — but a second one near you shows the contrast.*

**7. TDoA — find the interferer.** Several instances advertise `tdoa_enabled`.
Three or more receivers hearing the same signal locate it. For chasing down a
noise source or an unidentified carrier, that is the only real tool. *Yes,
fundamentally — three minimum.*

**8. Check before you call.** About to call CQ into a dead band? Sample decodes
and SNR from receivers in the direction you want, before spending twenty minutes.
*Yes — direction is the whole question.*

**9. What is being decoded near me.** Eighteen instances run a CW skimmer, fifty
do digital decodes. Show what they are hearing on your band, near your location.
It cannot become a Zeus spot — no spot API — but as a panel it is a live picture
of activity. *Optional, better with several.*

**10. Log what the conditions were.** On logging a QSO, capture remote SNR,
noise floor and MUF alongside it. Six months later the log says not just who you
worked but what the ionosphere was doing. Feeds naturally into the Wavelog
synchroniser as `APP_` fields. *No — one nearby receiver is enough.*

---

## Which to build first

**1, 2 and 3 share almost all their machinery**: pick receivers, stream them,
watch MOX, show SNR. Together they are a *self-monitoring* plugin, and they are
the ones that change what an operator can actually do rather than what they can
look at.

**4 and 8 share the other half**: the directory, filtered by the current band and
by bearing. A *conditions* plugin, useful without ever opening an audio stream —
and cheap, since it is backend-only until it isn't.

**7 is the most interesting and the least certain.** TDoA needs correlated
timing; whether the instances expose enough to do it from outside is unverified.
Worth an experiment, not a plan.

**10 is small and pairs with the plugin we already have.**

## Before any of it

Ask upstream whether the directory API is meant for third-party use. It is
unauthenticated and 638 KB; polling it from every Zeus install without asking
would be rude, and possibly the sort of thing that gets it locked down for
everyone.
