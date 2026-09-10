/**
 * Lines the reader has moved. A piece pushed sideways drags the pieces either side of it with it; the
 * pieces at the ends slide their point along the side of their box instead, and never off it. A line
 * placed by hand is drawn as it was placed for as long as it still fits the boxes it joins: dragging
 * one of them away from its end, opening it so it grows, or dragging another box over the line all
 * make it stale, and it is then routed afresh.
 */

import { segmentInside, sideOf, slotInset, type Pt, type RouteRect } from "./geometry";

/** A hand may not make the stub that leaves a box shorter than this. */
const minStub = 6;

/**
 * Whether a hand-placed line still fits: right-angled, starting on the border of its first box and
 * ending on the border of its second, and running through no box. A box dragged away from under a
 * line's end, opened so it grows, or dragged onto the line all make the line stale, and it is then
 * routed afresh.
 */
export function validManual(points: Pt[], a: RouteRect | undefined, b: RouteRect | undefined, rects: RouteRect[]): boolean {
  if (!a || !b || points.length < 2) return false;
  if (sideOf(points[0], a) < 0 || sideOf(points[points.length - 1], b) < 0) return false;
  for (let i = 0; i + 1 < points.length; i++) {
    const p = points[i];
    const q = points[i + 1];
    const horizontal = Math.abs(p.y - q.y) < 0.01;
    const vertical = Math.abs(p.x - q.x) < 0.01;
    if (!horizontal && !vertical) return false;
    for (const r of rects) if (segmentInside(p, q, r)) return false;
  }
  return true;
}

/** Whether any piece of a line runs through the box (used to see which lines a moved box disturbs). */
export function routeTouches(points: Pt[], r: RouteRect): boolean {
  for (let i = 0; i + 1 < points.length; i++) if (segmentInside(points[i], points[i + 1], r)) return true;
  return false;
}


/**
 * A line with one of its pieces pushed sideways by `delta`: a horizontal piece up or down, a
 * vertical one left or right, the pieces next to it stretching to follow. The first and last piece
 * slide their end along the side of the box instead, and never off it; the piece next to a box may
 * not pull the stub out of the box back into it.
 */
export function moveSegment(points: Pt[], seg: number, delta: number, a: RouteRect, b: RouteRect): Pt[] {
  const pts = points.map((p) => ({ x: p.x, y: p.y }));
  if (seg < 0 || seg + 1 >= pts.length) return pts;
  const last = pts.length - 2;
  const horizontal = Math.abs(pts[seg].y - pts[seg + 1].y) < 0.01;
  let v = (horizontal ? pts[seg].y : pts[seg].x) + delta;
  // the end pieces carry a port along a box side: within that side, clear of the corners
  if (seg === 0) v = clampAlong(v, a, horizontal);
  if (seg === last) v = clampAlong(v, b, horizontal);
  // the pieces next to the end pieces set how far out the stubs reach
  if (seg === 1) v = keepStub(v, pts[0], a, horizontal);
  if (seg === last - 1) v = keepStub(v, pts[pts.length - 1], b, horizontal);
  if (horizontal) pts[seg].y = pts[seg + 1].y = v;
  else pts[seg].x = pts[seg + 1].x = v;
  return pts;
}


function clampAlong(v: number, r: RouteRect, horizontalPiece: boolean): number {
  // a horizontal piece leaves a left or right side and slides up and down it; a vertical one, along the top or bottom
  return horizontalPiece ? Math.min(r.y + r.h - slotInset, Math.max(r.y + slotInset, v)) : Math.min(r.x + r.w - slotInset, Math.max(r.x + slotInset, v));
}


function keepStub(v: number, port: Pt, r: RouteRect, horizontalPiece: boolean): number {
  const side = sideOf(port, r);
  if (horizontalPiece) {
    // the stub next to this piece is vertical, out of the top or the bottom
    if (side === 1) return Math.max(v, port.y + minStub);
    if (side === 3) return Math.min(v, port.y - minStub);
  } else {
    if (side === 0) return Math.max(v, port.x + minStub);
    if (side === 2) return Math.min(v, port.x - minStub);
  }
  return v;
}
