/**
 * The shapes the routing works in, and the plain geometry over them: where a line meets a box, and
 * the sorted-array searching the grid is built on.
 */

export interface RouteRect {
  id: string;
  x: number;
  y: number;
  w: number;
  h: number;
}


export interface Pt {
  x: number;
  y: number;
}


export interface RouteRequest {
  id: string;
  from: string;
  to: string;
}


export interface Route {
  id: string;
  /** From a point on the border of the first box to a point on the border of the second, with no
   *  two consecutive pieces in line with each other. */
  points: Pt[];
  /** False when no right-angled way was found and the line is drawn straight instead. */
  orthogonal: boolean;
  /** True when the points are where a hand put them rather than where the router did. */
  manual: boolean;
}


/** Which way lines prefer to leave a box when the other box is both beside and above or below it. */
export type PreferredAxis = "v" | "h" | "auto";

/** How far from a box a line keeps, and so also how long the stub is that leaves a box. */
export const routeMargin = 12;
/** No line leaves a box closer than this to a corner (the corners are rounded). */
export const slotInset = 12;
// directions, doubling as the sides they point out of: 0 right, 1 down (bottom), 2 left, 3 up (top)
export type Dir = 0 | 1 | 2 | 3;
export const dx = [1, 0, -1, 0];
export const dy = [0, 1, 0, -1];

export function center(r: RouteRect, side: Dir): number {
  return side === 1 || side === 3 ? r.x + r.w / 2 : r.y + r.h / 2;
}

/** The straight line between two boxes, border to border, for when no right-angled one can be had. */
export function straightRoute(a: RouteRect, b: RouteRect): Pt[] {
  const ac = { x: a.x + a.w / 2, y: a.y + a.h / 2 };
  const bc = { x: b.x + b.w / 2, y: b.y + b.h / 2 };
  return [borderPoint(a, bc), borderPoint(b, ac)];
}


/** The point on the border of a box where a line from its centre to (tx, ty) leaves it. */
export function borderPoint(b: RouteRect, t: Pt): Pt {
  const cx = b.x + b.w / 2;
  const cy = b.y + b.h / 2;
  const ddx = t.x - cx;
  const ddy = t.y - cy;
  if (ddx === 0 && ddy === 0) return { x: cx, y: cy };
  const sx = ddx === 0 ? Infinity : b.w / 2 / Math.abs(ddx);
  const sy = ddy === 0 ? Infinity : b.h / 2 / Math.abs(ddy);
  const s = Math.min(sx, sy);
  return { x: cx + ddx * s, y: cy + ddy * s };
}

/** Sorted, with values closer than half a pixel folded into one. */
export function dedupe(values: number[]): number[] {
  values.sort((a, b) => a - b);
  const out: number[] = [];
  for (const v of values) if (out.length === 0 || v - out[out.length - 1] > 0.5) out.push(v);
  return out;
}


/** The index of the grid coordinate at v, or -1 when v is not on the grid. */
export function indexOf(sorted: number[], v: number): number {
  let lo = 0;
  let hi = sorted.length - 1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    const c = sorted[mid];
    if (Math.abs(c - v) <= 0.75) return mid;
    if (c < v) lo = mid + 1;
    else hi = mid - 1;
  }
  return -1;
}

/** Drops repeated points and any point lying on the straight line between its neighbours. */
export function simplify(points: Pt[]): Pt[] {
  const out: Pt[] = [];
  for (const p of points) {
    const last = out[out.length - 1];
    if (last && Math.abs(last.x - p.x) < 0.01 && Math.abs(last.y - p.y) < 0.01) continue;
    out.push({ x: p.x, y: p.y });
  }
  for (let i = 1; i < out.length - 1; ) {
    const a = out[i - 1];
    const b = out[i];
    const c = out[i + 1];
    const sameX = Math.abs(a.x - b.x) < 0.01 && Math.abs(b.x - c.x) < 0.01;
    const sameY = Math.abs(a.y - b.y) < 0.01 && Math.abs(b.y - c.y) < 0.01;
    if (sameX || sameY) out.splice(i, 1);
    else i++;
  }
  return out;
}

/** The index of the first value above v, or the length when there is none. */
export function firstAbove(sorted: number[], v: number): number {
  let lo = 0;
  let hi = sorted.length;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (sorted[mid] > v) hi = mid;
    else lo = mid + 1;
  }
  return lo;
}

/** The index of the last value below v, or -1 when there is none. */
export function lastBelow(sorted: number[], v: number): number {
  let lo = 0;
  let hi = sorted.length;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (sorted[mid] >= v) hi = mid;
    else lo = mid + 1;
  }
  return lo - 1;
}

/** Which side of a box a point on its border is on, or -1 when it is not on the border. */
export function sideOf(p: Pt, r: RouteRect): Dir | -1 {
  const eps = 0.5;
  const withinX = p.x >= r.x - eps && p.x <= r.x + r.w + eps;
  const withinY = p.y >= r.y - eps && p.y <= r.y + r.h + eps;
  if (withinX && Math.abs(p.y - r.y) <= eps) return 3;
  if (withinX && Math.abs(p.y - (r.y + r.h)) <= eps) return 1;
  if (withinY && Math.abs(p.x - r.x) <= eps) return 2;
  if (withinY && Math.abs(p.x - (r.x + r.w)) <= eps) return 0;
  return -1;
}

/** Whether an axis-aligned segment has any of its length strictly inside a box. */
export function segmentInside(p: Pt, q: Pt, r: RouteRect): boolean {
  const eps = 0.5;
  const x1 = Math.min(p.x, q.x);
  const x2 = Math.max(p.x, q.x);
  const y1 = Math.min(p.y, q.y);
  const y2 = Math.max(p.y, q.y);
  return x2 > r.x + eps && x1 < r.x + r.w - eps && y2 > r.y + eps && y1 < r.y + r.h - eps;
}
