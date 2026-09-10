/**
 * The lines as SVG. Bends are drawn a little rounded; where two lines cross, the horizontal one is
 * broken with a small gap so the crossing reads. Also where a line's name goes, and how one line is
 * blended into another while the boxes travel from one arrangement to the next.
 */

import { firstAbove, type Pt, type Route } from "./geometry";

/** A crossing on a horizontal piece of a line: which piece, and where along it. */
export interface Gap {
  seg: number;
  at: number;
}


/**
 * Every place a horizontal piece of one line crosses a vertical piece of another (or of itself),
 * as gaps for the horizontal one. Only right-angled lines take part; a straight fallback is left as
 * it is. A crossing closer than `clearance` to the end of a piece gets no gap: the gap would eat
 * into the bend there.
 */
export function crossingGaps(routes: Iterable<Route>, clearance: number): Map<string, Gap[]> {
  const verticals: { x: number; y1: number; y2: number }[] = [];
  const all: Route[] = [];
  for (const r of routes) {
    if (!r.orthogonal) continue;
    all.push(r);
    for (let i = 0; i + 1 < r.points.length; i++) {
      const a = r.points[i];
      const b = r.points[i + 1];
      if (Math.abs(a.x - b.x) < 0.01 && Math.abs(a.y - b.y) >= 0.01) verticals.push({ x: a.x, y1: Math.min(a.y, b.y), y2: Math.max(a.y, b.y) });
    }
  }
  verticals.sort((u, v) => u.x - v.x);
  const vx = verticals.map((v) => v.x);
  const gaps = new Map<string, Gap[]>();
  for (const r of all) {
    for (let i = 0; i + 1 < r.points.length; i++) {
      const a = r.points[i];
      const b = r.points[i + 1];
      if (Math.abs(a.y - b.y) >= 0.01) continue;
      const x1 = Math.min(a.x, b.x) + clearance;
      const x2 = Math.max(a.x, b.x) - clearance;
      if (x2 <= x1) continue;
      for (let k = firstAbove(vx, x1); k < verticals.length && vx[k] < x2; k++) {
        const v = verticals[k];
        if (v.y1 < a.y - 0.5 && v.y2 > a.y + 0.5) gaps.set(r.id, [...(gaps.get(r.id) ?? []), { seg: i, at: v.x }]);
      }
    }
  }
  return gaps;
}


/** How far before and after a bend the line starts to turn: the bends are a little rounded. */
export const cornerRadius = 6;

/**
 * The SVG path of a line: its bends rounded, and its horizontal pieces broken where other lines
 * cross them. A bend is drawn as a curve from a little before the corner to a little after it, with
 * the corner itself as the control point; a piece too short for that gets as much rounding as it has
 * room for.
 */
export function orthoPath(points: Pt[], gaps: Gap[] | undefined, gapRadius: number): string {
  const n = points.length;
  if (n === 0) return "";
  if (n === 1) return `M${fmt(points[0].x)} ${fmt(points[0].y)}`;
  const len = (i: number) => Math.abs(points[i + 1].x - points[i].x) + Math.abs(points[i + 1].y - points[i].y);
  // how far each bend reaches into the pieces either side of it; the ends of the line do not bend
  const reach: number[] = points.map((_, i) => (i === 0 || i === n - 1 ? 0 : Math.min(cornerRadius, len(i - 1) / 2, len(i) / 2)));
  let d = `M${fmt(points[0].x)} ${fmt(points[0].y)}`;
  // where the pen is: a piece that does not start there is a new subpath
  let pen = points[0];
  const lineTo = (from: Pt, to: Pt) => {
    if (Math.abs(from.x - pen.x) > 0.01 || Math.abs(from.y - pen.y) > 0.01) d += ` M${fmt(from.x)} ${fmt(from.y)}`;
    d += ` L${fmt(to.x)} ${fmt(to.y)}`;
    pen = to;
  };
  for (let i = 0; i + 1 < n; i++) {
    const a = points[i];
    const b = points[i + 1];
    const ux = Math.sign(b.x - a.x);
    const uy = Math.sign(b.y - a.y);
    // the straight part of the piece, between the bends at its ends
    const start = { x: a.x + ux * reach[i], y: a.y + uy * reach[i] };
    const end = { x: b.x - ux * reach[i + 1], y: b.y - uy * reach[i + 1] };
    const here = uy === 0 ? gaps?.filter((g) => g.seg === i) : undefined;
    if (!here || here.length === 0) {
      if (Math.abs(end.x - start.x) > 0.01 || Math.abs(end.y - start.y) > 0.01) lineTo(start, end);
    } else {
      // the pieces between the gaps, walked in the direction of the segment; gaps that run into each
      // other make one wider gap
      here.sort((u, v) => (u.at - v.at) * ux);
      let from = start.x;
      for (const g of here) {
        const before = g.at - gapRadius * ux;
        const after = g.at + gapRadius * ux;
        if ((before - from) * ux > 0) lineTo({ x: from, y: a.y }, { x: before, y: a.y });
        if ((after - from) * ux > 0) from = after;
      }
      if ((end.x - from) * ux > 0) lineTo({ x: from, y: a.y }, end);
    }
    // the bend into the next piece
    if (i + 1 < n - 1 && reach[i + 1] > 0) {
      const c = points[i + 2];
      const next = { x: b.x + Math.sign(c.x - b.x) * reach[i + 1], y: b.y + Math.sign(c.y - b.y) * reach[i + 1] };
      if (Math.abs(end.x - pen.x) > 0.01 || Math.abs(end.y - pen.y) > 0.01) d += ` M${fmt(end.x)} ${fmt(end.y)}`;
      d += ` Q${fmt(b.x)} ${fmt(b.y)} ${fmt(next.x)} ${fmt(next.y)}`;
      pen = next;
    }
  }
  return d;
}


/** The SVG path straight through the points, gaps or not. */
export function plainPath(points: Pt[]): string {
  return points.map((p, i) => `${i === 0 ? "M" : "L"}${fmt(p.x)} ${fmt(p.y)}`).join(" ");
}


const fmt = (v: number) => (Math.round(v * 10) / 10).toString();

/** Where a line's name goes: above the middle of its longest horizontal piece, or beside the
 *  longest vertical one when it has no horizontal piece worth the name. */
export function labelPlace(points: Pt[]): { x: number; y: number; anchor: "middle" | "start" } {
  let best: { score: number; x: number; y: number; horizontal: boolean } | null = null;
  for (let i = 0; i + 1 < points.length; i++) {
    const a = points[i];
    const b = points[i + 1];
    const horizontal = Math.abs(a.y - b.y) < 0.01;
    const len = horizontal ? Math.abs(b.x - a.x) : Math.abs(b.y - a.y);
    // a horizontal piece is preferred as long as it is not tiny next to a vertical one
    const score = horizontal ? len * 2 : len;
    if (!best || score > best.score) best = { score, x: (a.x + b.x) / 2, y: (a.y + b.y) / 2, horizontal };
  }
  if (!best) return { x: points[0].x, y: points[0].y - 4, anchor: "middle" };
  return best.horizontal ? { x: best.x, y: best.y - 4, anchor: "middle" } : { x: best.x + 5, y: best.y + 3.5, anchor: "start" };
}

/** The line as n points evenly spaced along its length, so two lines can be blended point by point. */
function resample(points: Pt[], n: number): Pt[] {
  if (points.length === 0) return [];
  if (points.length === 1) return Array.from({ length: n }, () => ({ ...points[0] }));
  const lengths: number[] = [];
  let total = 0;
  for (let i = 0; i + 1 < points.length; i++) {
    const l = Math.hypot(points[i + 1].x - points[i].x, points[i + 1].y - points[i].y);
    lengths.push(l);
    total += l;
  }
  const out: Pt[] = [];
  let seg = 0;
  let before = 0;
  for (let k = 0; k < n; k++) {
    const target = (total * k) / (n - 1);
    while (seg < lengths.length - 1 && before + lengths[seg] < target) before += lengths[seg++];
    const t = lengths[seg] === 0 ? 0 : Math.min(1, Math.max(0, (target - before) / lengths[seg]));
    out.push({ x: points[seg].x + (points[seg + 1].x - points[seg].x) * t, y: points[seg].y + (points[seg + 1].y - points[seg].y) * t });
  }
  return out;
}


/** Part of the way from one line to another: both resampled to the same points, then blended. */
export function blend(from: Pt[], to: Pt[], t: number): Pt[] {
  const n = 16;
  const a = resample(from, n);
  const b = resample(to, n);
  return a.map((p, i) => ({ x: p.x + (b[i].x - p.x) * t, y: p.y + (b[i].y - p.y) * t }));
}
