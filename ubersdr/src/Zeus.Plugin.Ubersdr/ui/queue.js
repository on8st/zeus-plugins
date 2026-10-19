// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// Keeping the playback queue from growing without bound.
//
// Each ear is scheduled by advancing a pointer: every buffer starts where the
// last one ended. The original had exactly one correction — if the pointer fell
// behind the clock, restart at now + lead — which handles an *underrun* and
// nothing else. Nothing ever trimmed a queue that got too deep.
//
// That is not a theoretical concern. A sender whose clock runs a fraction of a
// percent faster than the local AudioContext adds that fraction of a second to
// the backlog every second, for as long as the stream runs, and the delay grows
// with it. Measured on the air: the two ears drifted about 0.8 s apart while
// the *arrival* times of the two streams were within 0.04 s of each other. The
// entire audible split was backlog.

/** How far past the intended lead the queue may run before it is cut back. */
export const QUEUE_SLACK_S = 0.30;

/**
 * Where the next buffer should start, and whether the backlog has to go.
 *
 * @param queuedUntil AudioContext time the queue currently reaches to.
 * @param now         AudioContext currentTime.
 * @param lead        Intended jitter buffer for this ear, plus any alignment.
 * @param slack       Tolerance above `lead` before trimming.
 * @returns {startAt, skip} — `skip` means: do not schedule this buffer at all.
 *
 * <p>Dropping the arriving buffer, rather than flushing what is already
 * scheduled, is the whole point. The first version stopped every pending source
 * and restarted at the lead, which corrected the backlog and left an audible
 * hole every time it fired — the operator heard the remote "cut out regularly
 * and come back" on a signal he described as otherwise perfect.
 *
 * <p>Dropping one arriving frame costs 20 ms of audio, below the threshold of
 * notice, and the queue drains by that much. A sender running fast simply loses
 * a frame now and then instead of the stream being cut.
 */
export function planStart({ queuedUntil, now, lead, slack = QUEUE_SLACK_S }) {
  // Underrun, or a fresh start: begin one lead ahead of the clock.
  if (queuedUntil < now + 0.01) return { startAt: now + lead, skip: false };

  // Backlog: let this one go and let the queue drain toward the lead.
  if (queuedUntil - now > lead + slack) return { startAt: queuedUntil, skip: true };

  return { startAt: queuedUntil, skip: false };
}
