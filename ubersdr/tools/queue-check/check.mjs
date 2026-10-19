// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// The queue may not grow without bound. Measured on the air: the ears drifted
// 0.8 s apart while the two streams arrived within 0.04 s of each other, so the
// entire audible split was backlog nobody was trimming.
import { pathToFileURL } from 'node:url';
import path from 'node:path';
const ui = path.resolve(process.argv[2] ?? 'src/Zeus.Plugin.Ubersdr/ui/queue.js');
const { planStart, QUEUE_SLACK_S } = await import(pathToFileURL(ui).href);

let failed = 0;
const check = (n, c, x = '') => { if (c) console.log(`ok    ${n}${x ? '  ' + x : ''}`);
  else { console.error(`FAIL  ${n}${x ? '  ' + x : ''}`); failed++; } };

const LEAD = 0.14;

check('a fresh stream starts one lead ahead of the clock',
  planStart({ queuedUntil: 0, now: 10, lead: LEAD }).startAt === 10 + LEAD);
check('an underrun restarts rather than scheduling in the past',
  planStart({ queuedUntil: 9.5, now: 10, lead: LEAD }).startAt === 10 + LEAD);
check('a healthy queue simply continues where it ended',
  planStart({ queuedUntil: 10.1, now: 10, lead: LEAD }).startAt === 10.1);
check('a queue just inside the slack is left alone',
  !planStart({ queuedUntil: 10 + LEAD + QUEUE_SLACK_S - 0.01, now: 10, lead: LEAD }).skip);
check('a queue past the slack drops the arriving frame',
  planStart({ queuedUntil: 10 + LEAD + QUEUE_SLACK_S + 0.01, now: 10, lead: LEAD }).skip);
check('a deep backlog is never flushed \u2014 nothing already scheduled is stopped',
  planStart({ queuedUntil: 10.8, now: 10, lead: LEAD }).skip);
{
  // A sender 1% fast for a minute. Untrimmed the delay grows forever.
  let queued = 0, now = 0, worst = 0, dropped = 0;
  for (let i = 0; i < 3000; i++) {
    const p = planStart({ queuedUntil: queued, now, lead: LEAD });
    if (p.skip) dropped++; else queued = p.startAt + 0.0202;  // 20.2 ms of audio...
    now += 0.02;                                              // ...per 20 ms real
    worst = Math.max(worst, queued - now);
  }
  check('a 1% fast sender cannot grow the delay without bound',
    worst <= LEAD + QUEUE_SLACK_S + 0.03, `worst ${worst.toFixed(3)}s`);
  check('and pays for it in dropped frames, not in gaps',
    dropped > 0 && dropped < 3000 * 0.05,
    `${dropped} of 3000 frames dropped (${(100 * dropped / 3000).toFixed(1)}%)`);
}

if (failed) { console.error(`\n${failed} failed`); process.exit(1); }
console.log('\nqueue depth holds');
