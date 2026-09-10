/**
 * Where every line leaves and enters its boxes, and the cheapest way between the two.
 *
 * The sides are decided before any routing: the side facing the other box, with the lines out of one
 * side spread along it in the order of where they are going, so they do not start by crossing each
 * other. Lines are then routed longest first, each seeing the ones already drawn, so the widest line
 * of a bundle takes the lane nearest the box and the narrower ones nest inside it. A line with no
 * right-angled way round comes back straight.
 */

import { center, dedupe, dx, dy, routeMargin, simplify, slotInset, straightRoute, type Dir, type PreferredAxis, type Pt, type Route, type RouteRect, type RouteRequest } from "./geometry";
import { Grid, maxGridNodes } from "./grid";

/** How far apart parallel lines run through a gap. */
const laneSpacing = 12;
const maxLanesPerGap = 6;
/** Lines out of one side of a box sit this far apart, or closer when the side is short. */
const slotSpacing = 18;
/** How far a line to the type itself reaches out past the box. */
const loopReach = 26;

interface Port {
  rect: RouteRect;
  side: Dir;
  /** The coordinate along the side: x for top and bottom, y for left and right. */
  along: number;
}


function portPoint(p: Port): Pt {
  const r = p.rect;
  switch (p.side) {
    case 0:
      return { x: r.x + r.w, y: p.along };
    case 1:
      return { x: p.along, y: r.y + r.h };
    case 2:
      return { x: r.x, y: p.along };
    default:
      return { x: p.along, y: r.y };
  }
}


function stubEnd(p: Port): Pt {
  const q = portPoint(p);
  return { x: q.x + dx[p.side] * routeMargin, y: q.y + dy[p.side] * routeMargin };
}


/**
 * Which sides two boxes face each other with. A box clearly below gets the line out of the bottom;
 * one clearly beside, out of the side; one both below and beside, whichever the layout reads in - or
 * the axis with the wider gap when it has no preference.
 */
function chooseSides(a: RouteRect, b: RouteRect, prefer: PreferredAxis): [Dir, Dir] {
  const vGap = Math.max(b.y - (a.y + a.h), a.y - (b.y + b.h));
  const hGap = Math.max(b.x - (a.x + a.w), a.x - (b.x + b.w));
  const ddx = b.x + b.w / 2 - (a.x + a.w / 2);
  const ddy = b.y + b.h / 2 - (a.y + a.h / 2);
  let vertical: boolean;
  if (vGap < 0 && hGap < 0) vertical = Math.abs(ddy) >= Math.abs(ddx); // the boxes overlap: nothing to go by but the centres
  else if (prefer === "v") vertical = vGap >= 0;
  else if (prefer === "h") vertical = hGap < 0;
  else vertical = vGap >= hGap;
  if (vertical) return ddy >= 0 ? [1, 3] : [3, 1];
  return ddx >= 0 ? [0, 2] : [2, 0];
}

/** A line from a type to itself: out of the right side, down, and back in. */
function loopRoute(r: RouteRect): Pt[] {
  const inset = r.h < 60 ? r.h / 3 : 20;
  const x = r.x + r.w;
  return [
    { x, y: r.y + inset },
    { x: x + loopReach, y: r.y + inset },
    { x: x + loopReach, y: r.y + r.h - inset },
    { x, y: r.y + r.h - inset },
  ];
}

/**
 * Routes every requested line. Lines given in `manual` are drawn as given; lines in `keep` are
 * taken over as they are; both are what the newly routed lines get round. Lines with no way
 * round come back straight (orthogonal: false).
 */
export function routeAll(rects: RouteRect[], requests: RouteRequest[], manual: Map<string, Pt[]>, keep: Map<string, Route>, prefer: PreferredAxis): Map<string, Route> {
  const byId = new Map(rects.map((r) => [r.id, r]));
  const result = new Map<string, Route>();
  const fixed: Pt[][] = [];
  const pending: { req: RouteRequest; a: RouteRect; b: RouteRect }[] = [];
  for (const req of requests) {
    const m = manual.get(req.id);
    if (m) {
      result.set(req.id, { id: req.id, points: m, orthogonal: true, manual: true });
      fixed.push(m);
      continue;
    }
    const k = keep.get(req.id);
    if (k) {
      result.set(req.id, k);
      if (k.orthogonal) fixed.push(k.points);
      continue;
    }
    const a = byId.get(req.from);
    const b = byId.get(req.to);
    if (!a || !b) continue;
    if (a === b) {
      const loop = loopRoute(a);
      result.set(req.id, { id: req.id, points: loop, orthogonal: true, manual: false });
      fixed.push(loop); // other lines keep off it like off any line already drawn
      continue;
    }
    pending.push({ req, a, b });
  }
  if (pending.length === 0) return result;

  // where each line leaves and enters: the sides first, then the lines out of one side spread along
  // it in the order of where they are going
  const sides = pending.map((p) => chooseSides(p.a, p.b, prefer));
  const groups = new Map<string, { i: number; end: 0 | 1; key: number; id: string }[]>();
  pending.forEach((p, i) => {
    const [sa, sb] = sides[i];
    const ga = p.a.id + "/" + sa;
    const gb = p.b.id + "/" + sb;
    groups.set(ga, [...(groups.get(ga) ?? []), { i, end: 0, key: center(p.b, sa), id: p.req.id }]);
    groups.set(gb, [...(groups.get(gb) ?? []), { i, end: 1, key: center(p.a, sb), id: p.req.id }]);
  });
  const ports: [Port, Port][] = pending.map((p, i) => [
    { rect: p.a, side: sides[i][0], along: center(p.a, sides[i][0]) },
    { rect: p.b, side: sides[i][1], along: center(p.b, sides[i][1]) },
  ]);
  for (const entries of groups.values()) {
    if (entries.length < 2) continue;
    entries.sort((u, v) => u.key - v.key || (u.id < v.id ? -1 : u.id > v.id ? 1 : 0));
    const first = ports[entries[0].i][entries[0].end];
    const r = first.rect;
    const length = first.side === 1 || first.side === 3 ? r.w : r.h;
    const spacing = Math.min(slotSpacing, Math.max(0, length - 2 * slotInset) / (entries.length - 1));
    const mid = center(r, first.side);
    entries.forEach((e, k) => {
      ports[e.i][e.end].along = mid + (k - (entries.length - 1) / 2) * spacing;
    });
  }

  // the grid: box outlines pushed out by the margin, lanes through the gaps between them, and
  // whatever else a line has to be able to reach - where lines leave the boxes, the box centres
  // (for a second try from there) and the lines already drawn
  const boundsX: number[] = [];
  const boundsY: number[] = [];
  for (const r of rects) {
    boundsX.push(r.x - routeMargin, r.x + r.w + routeMargin);
    boundsY.push(r.y - routeMargin, r.y + r.h + routeMargin);
  }
  const extraX: number[] = [];
  const extraY: number[] = [];
  for (const r of rects) {
    extraX.push(r.x + r.w / 2);
    extraY.push(r.y + r.h / 2);
  }
  for (const [pa, pb] of ports) {
    for (const p of [pa, pb]) {
      if (p.side === 1 || p.side === 3) extraX.push(p.along);
      else extraY.push(p.along);
    }
  }
  for (const pts of fixed) {
    for (const p of pts) {
      extraX.push(p.x);
      extraY.push(p.y);
    }
  }
  let grid: Grid | null = null;
  for (const lanes of [true, false]) {
    const xs = dedupe([...(lanes ? withLanes(dedupe([...boundsX])) : boundsX), ...extraX]);
    const ys = dedupe([...(lanes ? withLanes(dedupe([...boundsY])) : boundsY), ...extraY]);
    if (xs.length * ys.length > maxGridNodes) continue;
    grid = new Grid(xs, ys, rects);
    break;
  }
  if (!grid) {
    for (const p of pending) result.set(p.req.id, { id: p.req.id, points: straightRoute(p.a, p.b), orthogonal: false, manual: false });
    return result;
  }
  for (const pts of fixed) grid.use(pts);

  // longest first: the widest line of a bundle takes the lane nearest the box, and the narrower
  // ones nest inside it rather than cross it
  const order = pending.map((_, i) => i).sort((i, j) => manhattan(pending[j]) - manhattan(pending[i]) || (pending[i].req.id < pending[j].req.id ? -1 : 1));
  for (const i of order) {
    const { req, a, b } = pending[i];
    const [pa, pb] = ports[i];
    // the line's lanes gravitate to the end where more lines gather; the end it goes to when equal
    const bundleA = groups.get(a.id + "/" + pa.side)?.length ?? 1;
    const bundleB = groups.get(b.id + "/" + pb.side)?.length ?? 1;
    let points = route(grid, pa, pb, bundleA > bundleB ? pa : pb);
    if (!points) {
      // no way from the chosen sides: try the other axis, from the middle of those sides
      const [sa, sb] = chooseSides(a, b, sides[i][0] & 1 ? "h" : "v");
      const alt: [Port, Port] = [
        { rect: a, side: sa, along: center(a, sa) },
        { rect: b, side: sb, along: center(b, sb) },
      ];
      points = route(grid, alt[0], alt[1], alt[1]);
    }
    if (points) {
      grid.use(points);
      result.set(req.id, { id: req.id, points, orthogonal: true, manual: false });
    } else result.set(req.id, { id: req.id, points: straightRoute(a, b), orthogonal: false, manual: false });
  }
  return result;
}


function manhattan(p: { a: RouteRect; b: RouteRect }): number {
  return Math.abs(p.a.x + p.a.w / 2 - (p.b.x + p.b.w / 2)) + Math.abs(p.a.y + p.a.h / 2 - (p.b.y + p.b.h / 2));
}


/** Lanes through every gap wide enough for one: evenly spaced, at most a handful per gap. */
function withLanes(bounds: number[]): number[] {
  const out = [...bounds];
  for (let i = 0; i + 1 < bounds.length; i++) {
    const gap = bounds[i + 1] - bounds[i];
    const lanes = Math.min(maxLanesPerGap, Math.floor(gap / laneSpacing) - 1);
    if (lanes < 1) continue;
    const step = gap / (lanes + 1);
    for (let k = 1; k <= lanes; k++) out.push(bounds[i] + step * k);
  }
  return out;
}


/** One line over the grid, from the stub out of one port to the stub into the other; its lanes
 *  gravitate to the side of the box the `bundle` port is on. */
function route(grid: Grid, from: Port, to: Port, bundle: Port): Pt[] | null {
  const start = grid.node(stubEnd(from));
  const goal = grid.node(stubEnd(to));
  if (start < 0 || goal < 0) return null;
  const goalDir = ((to.side + 2) & 3) as Dir; // arriving means travelling into the box
  // out of a top or bottom, the lanes a line picks between are rows; out of a side, columns
  const nodes = start === goal ? [start] : grid.find(start, from.side, goal, goalDir, stubEnd(bundle), (bundle.side & 1) === 1);
  if (!nodes) return null;
  return simplify([portPoint(from), ...nodes.map((n) => grid.point(n)), portPoint(to)]);
}
