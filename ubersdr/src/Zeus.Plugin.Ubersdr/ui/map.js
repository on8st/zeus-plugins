// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// The world map: coastline, receivers where they actually are, and a great
// circle from the operator to each one.
//
// Equirectangular, because it is the projection people read a world map in and
// because the panel is a wide strip. Great circles are therefore curves rather
// than straight lines — which is correct, and is what makes a path to North
// America visibly arc over the top rather than crossing the Atlantic flat.

import { COASTLINE } from './vendor/coastline.js';

export const MAP_W = 720;
export const MAP_H = 360;

// Equirectangular. Latitude is clipped: the 110m coastline runs to Antarctica,
// and a map that spends a fifth of its height on ice nobody is listening from
// wastes the space the receivers need.
export const LAT_MAX = 78;
export const LAT_MIN = -60;

export function project(lon, lat) {
  const x = ((lon + 180) / 360) * MAP_W;
  const y = ((LAT_MAX - lat) / (LAT_MAX - LAT_MIN)) * MAP_H;
  return [x, y];
}

/** Coastline as SVG path data, computed once. */
export const COAST_PATH = COASTLINE.map((arc) => {
  let d = '';
  let prevX = null;
  for (const [lon, lat] of arc) {
    const [x, y] = project(lon, lat);
    // An arc that wraps the antimeridian would otherwise draw a line straight
    // across the map.
    if (prevX !== null && Math.abs(x - prevX) > MAP_W / 2) d += ` M${x.toFixed(1)} ${y.toFixed(1)}`;
    else d += `${d ? ' L' : 'M'}${x.toFixed(1)} ${y.toFixed(1)}`;
    prevX = x;
  }
  return d;
}).join(' ');

/**
 * A Maidenhead locator to a position, at the centre of the square.
 *
 * The operator's own grid beats a derived one: the directory's distances are
 * relative to whoever called the API, geolocated by IP — 28 km out on the
 * station this was written for, and anywhere at all behind a VPN.
 */
export function gridToLatLon(grid) {
  const g = (grid ?? '').trim().toUpperCase();
  if (!/^[A-R]{2}[0-9]{2}([A-X]{2})?$/.test(g)) return null;

  let lon = (g.charCodeAt(0) - 65) * 20 - 180;
  let lat = (g.charCodeAt(1) - 65) * 10 - 90;
  lon += Number(g[2]) * 2;
  lat += Number(g[3]);

  if (g.length === 6) {
    lon += (g.charCodeAt(4) - 65) * (2 / 24) + (1 / 24);
    lat += (g.charCodeAt(5) - 65) * (1 / 24) + (0.5 / 24);
  } else {
    lon += 1; lat += 0.5;                 // centre of the four-character square
  }
  return { lat, lon };
}

const toRad = (d) => (d * Math.PI) / 180;
const toDeg = (r) => (r * 180) / Math.PI;

/**
 * Where the operator is.
 *
 * The directory gives each receiver's position plus its distance and bearing
 * *from the caller*, but never the caller's own position. So it is derived:
 * walk backwards from a receiver along the reverse bearing by its distance.
 * The nearest receiver gives the best fix, because the back-bearing
 * approximation costs least over a short path.
 */
export function deriveHome(receivers) {
  const usable = receivers
    .filter((r) => r.lat != null && r.lon != null && r.distanceKm != null && r.bearingDegrees != null)
    .sort((a, b) => a.distanceKm - b.distanceKm)
    .slice(0, 5);
  if (usable.length === 0) return null;

  // Estimate from each of the nearest few and take the median. The single
  // nearest is the most accurate individually, but a receiver whose directory
  // entry is a rounded town centre would then move the whole map; a median over
  // several is unbothered by one bad row.
  const fixes = usable.map(solve).filter(Boolean);
  if (fixes.length === 0) return null;

  const mid = (xs) => {
    const s = [...xs].sort((a, b) => a - b);
    return s.length % 2 ? s[(s.length - 1) / 2] : (s[s.length / 2 - 1] + s[s.length / 2]) / 2;
  };
  return { lat: mid(fixes.map((f) => f.lat)), lon: mid(fixes.map((f) => f.lon)) };
}

/**
 * One estimate of the operator's position from one receiver.
 *
 * The direct formula walks from a point along a bearing. Here the bearing is
 * known at the *other* end, so the naive reverse — bearing + 180 — is wrong by
 * the convergence of the meridians, tens of kilometres on a European path and
 * far worse across an ocean. So: guess, measure the forward bearing the guess
 * would produce, and correct. Two or three passes settle it to metres.
 */
function solve(r) {
  const want = ((r.bearingDegrees % 360) + 360) % 360;
  let back = (want + 180) % 360;
  let home = destination(r, back, r.distanceKm);

  for (let i = 0; i < 4; i++) {
    const got = initialBearing(home, r);
    let err = ((want - got + 540) % 360) - 180;      // signed, shortest way round
    if (Math.abs(err) < 1e-6) break;
    back = (back + err + 360) % 360;
    home = destination(r, back, r.distanceKm);
  }
  return home;
}

function destination(from, bearingDeg, distanceKm) {
  const R = 6371;
  const d = distanceKm / R;
  const brg = toRad(bearingDeg);
  const lat1 = toRad(from.lat);
  const lon1 = toRad(from.lon);

  const lat2 = Math.asin(Math.sin(lat1) * Math.cos(d) + Math.cos(lat1) * Math.sin(d) * Math.cos(brg));
  const lon2 = lon1 + Math.atan2(
    Math.sin(brg) * Math.sin(d) * Math.cos(lat1),
    Math.cos(d) - Math.sin(lat1) * Math.sin(lat2));

  return { lat: toDeg(lat2), lon: ((toDeg(lon2) + 540) % 360) - 180 };
}

function initialBearing(from, to) {
  const lat1 = toRad(from.lat), lat2 = toRad(to.lat);
  const dLon = toRad(to.lon - from.lon);
  const y = Math.sin(dLon) * Math.cos(lat2);
  const x = Math.cos(lat1) * Math.sin(lat2) - Math.sin(lat1) * Math.cos(lat2) * Math.cos(dLon);
  return (toDeg(Math.atan2(y, x)) + 360) % 360;
}

/**
 * A great circle between two points, as SVG path data.
 *
 * Sampled and split where it crosses the antimeridian, so a path to Japan
 * leaves one edge and arrives at the other instead of drawing a line back
 * across the whole map.
 */
export function greatCirclePath(from, to, steps = 64) {
  const lat1 = toRad(from.lat), lon1 = toRad(from.lon);
  const lat2 = toRad(to.lat), lon2 = toRad(to.lon);

  const dLon = lon2 - lon1;
  const dLat = lat2 - lat1;
  const a = Math.sin(dLat / 2) ** 2 + Math.cos(lat1) * Math.cos(lat2) * Math.sin(dLon / 2) ** 2;
  const delta = 2 * Math.asin(Math.min(1, Math.sqrt(a)));
  if (delta === 0) return '';

  let d = '';
  let prevX = null;
  for (let i = 0; i <= steps; i++) {
    const f = i / steps;
    const A = Math.sin((1 - f) * delta) / Math.sin(delta);
    const B = Math.sin(f * delta) / Math.sin(delta);
    const x = A * Math.cos(lat1) * Math.cos(lon1) + B * Math.cos(lat2) * Math.cos(lon2);
    const y = A * Math.cos(lat1) * Math.sin(lon1) + B * Math.cos(lat2) * Math.sin(lon2);
    const z = A * Math.sin(lat1) + B * Math.sin(lat2);

    const lat = toDeg(Math.atan2(z, Math.sqrt(x * x + y * y)));
    const lon = toDeg(Math.atan2(y, x));
    const [px, py] = project(lon, lat);

    if (prevX !== null && Math.abs(px - prevX) > MAP_W / 2) d += ` M${px.toFixed(1)} ${py.toFixed(1)}`;
    else d += `${d ? ' L' : 'M'}${px.toFixed(1)} ${py.toFixed(1)}`;
    prevX = px;
  }
  return d;
}
