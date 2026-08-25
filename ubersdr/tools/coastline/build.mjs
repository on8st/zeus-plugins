// SPDX-License-Identifier: GPL-2.0-or-later
//
// Regenerate ui/vendor/coastline.js from Natural Earth 1:50m land.
//
// Run when a finer or coarser map is wanted. Everything happens here so the
// panel ships plain arrays and needs no TopoJSON decoder:
//
//   node tools/coastline/build.mjs [tolerance-degrees]
//
// 0.55° is the shipped default — 5129 points become 1472, and the difference is
// invisible at panel size.
import { writeFileSync } from 'node:fs';

const TOL = Number(process.argv[2] ?? 0.30);
const SRC = 'https://cdn.jsdelivr.net/npm/world-atlas@2/land-50m.json';

const topo = await (await fetch(SRC)).json();
const { scale: [sx, sy], translate: [dx, dy] } = topo.transform;

const decode = (arc) => {
  let x = 0, y = 0;
  return arc.map(([ax, ay]) => { x += ax; y += ay; return [x * sx + dx, y * sy + dy]; });
};

// Douglas-Peucker.
const simplify = (pts, tol) => {
  if (pts.length < 3) return pts;
  const keep = new Set([0, pts.length - 1]);
  const stack = [[0, pts.length - 1]];
  while (stack.length) {
    const [i, j] = stack.pop();
    const [ax, ay] = pts[i], [bx, by] = pts[j];
    const ex = bx - ax, ey = by - ay, den = ex * ex + ey * ey;
    let best = tol, bi = null;
    for (let k = i + 1; k < j; k++) {
      const [px, py] = pts[k];
      let d;
      if (den === 0) d = Math.hypot(px - ax, py - ay);
      else {
        const t = Math.max(0, Math.min(1, ((px - ax) * ex + (py - ay) * ey) / den));
        d = Math.hypot(px - (ax + t * ex), py - (ay + t * ey));
      }
      if (d > best) { best = d; bi = k; }
    }
    if (bi !== null) { keep.add(bi); stack.push([i, bi], [bi, j]); }
  }
  return [...keep].sort((a, b) => a - b).map((k) => pts[k]);
};

const arcs = topo.arcs.map(decode)
  .map((a) => simplify(a, TOL))
  .filter((a) => a.length >= 4)
  .map((a) => a.map(([x, y]) => [Math.round(x * 100) / 100, Math.round(y * 100) / 100]));

const header = `// SPDX-License-Identifier: GPL-2.0-or-later
//
// World coastline, [[[lon,lat],...],...] in degrees. GENERATED — do not edit.
// Natural Earth 1:50m land (public domain) via world-atlas (ISC), decoded from
// TopoJSON and simplified at ${TOL}° by tools/coastline/build.mjs.
export const COASTLINE = `;

writeFileSync(
  new URL('../../src/Zeus.Plugin.Ubersdr/ui/vendor/coastline.js', import.meta.url),
  header + JSON.stringify(arcs) + ';\n');

console.log(`coastline.js: ${arcs.length} arcs, ${arcs.reduce((n, a) => n + a.length, 0)} points`);
