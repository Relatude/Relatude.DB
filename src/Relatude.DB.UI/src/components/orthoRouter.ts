/**
 * Right-angled line routing for the datamodel diagram: every line is made of horizontal and vertical
 * pieces only, bends as often as it has to, and never runs through a box.
 *
 * The lines are found on a sparse grid. Its columns and rows are the edges of every box pushed out by
 * a margin, a few lanes through every gap between boxes, and the points where lines meet the boxes;
 * a grid segment inside a pushed-out box is blocked. Each line is then the cheapest walk over that
 * grid from where it leaves one box to where it enters the other, where a walk pays for its length,
 * for every bend, for running on top of a line already drawn (so parallel lines take lanes of their
 * own) and for crossing one (so, where it can, a line goes round another rather than through it).
 * Lines are routed shortest first, each seeing the ones before it.
 *
 * Where a line leaves and enters a box is decided before any routing: on the side facing the other
 * box, with the lines out of one side spread along it in the order of where they are going, so they
 * do not start by crossing each other. Where two lines do cross in the end, the horizontal one is
 * drawn with a small gap around the crossing, and a line can be dragged sideways segment by segment
 * and then stays where it was put (see moveSegment).
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
/** How far apart parallel lines run through a gap. */
const laneSpacing = 12;
const maxLanesPerGap = 6;
/** A bend costs as much as this many pixels of line. */
const bendCost = 35;
/** Running on top of another line costs this much extra per pixel, and this much for every grid
 *  segment shared: two lines drawn as one cannot be read apart, so a lane of its own is worth a
 *  detour and worth a crossing. */
const shareCost = 2.5;
const shareFixed = 20;
/** Crossing another line costs this much, so a line goes round when the way round is short. */
const crossCost = 25;
/** Passing over the corner of another line, or its end, costs more than crossing it cleanly: a
 *  crossing gets a gap, a corner touched looks like a junction. */
const vertexCost = 40;
/** A whisper of cost per pixel of line for every pixel it runs away from the side of the box its
 *  bundle gathers at: among equally short ways it picks the lane nearest that box, so the lines of
 *  a bundle nest - the widest nearest the box - rather than cross. */
const laneBias = 0.0005;
/** Lines out of one side of a box sit this far apart, or closer when the side is short. */
const slotSpacing = 18;
/** No line leaves a box closer than this to a corner (the corners are rounded). */
export const slotInset = 12;
/** A hand may not make the stub that leaves a box shorter than this. */
const minStub = 6;
/** How far a line to the type itself reaches out past the box. */
const loopReach = 26;
/** Bigger grids than this are not worth routing on: every line would take a good part of a second. */
const maxGridNodes = 300_000;

// directions, doubling as the sides they point out of: 0 right, 1 down (bottom), 2 left, 3 up (top)
type Dir = 0 | 1 | 2 | 3;
const dx = [1, 0, -1, 0];
const dy = [0, 1, 0, -1];

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

function center(r: RouteRect, side: Dir): number {
  return side === 1 || side === 3 ? r.x + r.w / 2 : r.y + r.h / 2;
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
function dedupe(values: number[]): number[] {
  values.sort((a, b) => a - b);
  const out: number[] = [];
  for (const v of values) if (out.length === 0 || v - out[out.length - 1] > 0.5) out.push(v);
  return out;
}

/** The index of the grid coordinate at v, or -1 when v is not on the grid. */
function indexOf(sorted: number[], v: number): number {
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

/** A small binary heap of (key, value) pairs, the smallest key first. */
class Heap {
  private keys: number[] = [];
  private vals: number[] = [];
  get size() {
    return this.keys.length;
  }
  clear() {
    this.keys.length = 0;
    this.vals.length = 0;
  }
  push(key: number, val: number) {
    const k = this.keys;
    const v = this.vals;
    let i = k.length;
    k.push(key);
    v.push(val);
    while (i > 0) {
      const parent = (i - 1) >> 1;
      if (k[parent] <= key) break;
      k[i] = k[parent];
      v[i] = v[parent];
      i = parent;
    }
    k[i] = key;
    v[i] = val;
  }
  /** Removes and returns the value with the smallest key. */
  pop(): number {
    const k = this.keys;
    const v = this.vals;
    const top = v[0];
    const lastKey = k.pop()!;
    const lastVal = v.pop()!;
    const n = k.length;
    if (n > 0) {
      let i = 0;
      for (;;) {
        const l = 2 * i + 1;
        if (l >= n) break;
        const r = l + 1;
        const c = r < n && k[r] < k[l] ? r : l;
        if (k[c] >= lastKey) break;
        k[i] = k[c];
        v[i] = v[c];
        i = c;
      }
      k[i] = lastKey;
      v[i] = lastVal;
    }
    return top;
  }
}

/**
 * The grid the lines are found on, and the lines already on it. Built once per routing and used for
 * every line in turn; each line found is added to what the next one has to get round.
 */
class Grid {
  readonly xs: number[];
  readonly ys: number[];
  readonly nx: number;
  readonly ny: number;
  /** blockedH[n]: the segment from node n to the node right of it runs through a box; blockedV[n]: down. */
  private readonly blockedH: Uint8Array;
  private readonly blockedV: Uint8Array;
  /** How many lines already run along each segment. */
  private readonly shareH: Uint16Array;
  private readonly shareV: Uint16Array;
  /** usedH[n]: a horizontal line already passes straight through node n; usedV[n]: a vertical one. */
  private readonly usedH: Uint8Array;
  private readonly usedV: Uint8Array;
  /** vertex[n]: a line already bends or ends at node n. */
  private readonly vertex: Uint8Array;
  // the search's working memory, kept between lines and told apart by generation rather than cleared
  private readonly g: Float32Array;
  private readonly parent: Int32Array;
  private readonly opened: Int32Array;
  private readonly closed: Int32Array;
  private generation = 0;
  private readonly heap = new Heap();

  constructor(xs: number[], ys: number[], rects: RouteRect[]) {
    this.xs = xs;
    this.ys = ys;
    this.nx = xs.length;
    this.ny = ys.length;
    const n = this.nx * this.ny;
    this.blockedH = new Uint8Array(n);
    this.blockedV = new Uint8Array(n);
    this.shareH = new Uint16Array(n);
    this.shareV = new Uint16Array(n);
    this.usedH = new Uint8Array(n);
    this.usedV = new Uint8Array(n);
    this.vertex = new Uint8Array(n);
    this.g = new Float32Array(n * 4);
    this.parent = new Int32Array(n * 4);
    this.opened = new Int32Array(n * 4);
    this.closed = new Int32Array(n * 4);
    for (const r of rects) this.block(r);
  }

  /** Marks every segment inside the pushed-out box; its outline itself stays open. */
  private block(r: RouteRect) {
    const eps = 0.75;
    const i0 = firstAbove(this.xs, r.x - routeMargin + eps);
    const i1 = lastBelow(this.xs, r.x + r.w + routeMargin - eps);
    const j0 = firstAbove(this.ys, r.y - routeMargin + eps);
    const j1 = lastBelow(this.ys, r.y + r.h + routeMargin - eps);
    // rows strictly inside: every horizontal segment from the left outline to the right one
    for (let yi = j0; yi <= j1; yi++) {
      const row = yi * this.nx;
      for (let xi = Math.max(0, i0 - 1); xi <= Math.min(this.nx - 2, i1); xi++) this.blockedH[row + xi] = 1;
    }
    // columns strictly inside: every vertical segment from the top outline to the bottom one
    for (let xi = i0; xi <= i1; xi++) {
      for (let yi = Math.max(0, j0 - 1); yi <= Math.min(this.ny - 2, j1); yi++) this.blockedV[yi * this.nx + xi] = 1;
    }
  }

  node(p: Pt): number {
    const xi = indexOf(this.xs, p.x);
    const yi = indexOf(this.ys, p.y);
    return xi < 0 || yi < 0 ? -1 : yi * this.nx + xi;
  }

  /** Adds a finished line to what later lines pay for sharing, crossing or touching. */
  use(points: Pt[]) {
    for (const p of points) {
      const n = this.node(p);
      if (n >= 0) this.vertex[n] = 1;
    }
    for (let i = 0; i + 1 < points.length; i++) {
      const a = points[i];
      const b = points[i + 1];
      if (Math.abs(a.y - b.y) < 0.01) {
        const yi = indexOf(this.ys, a.y);
        const xa = indexOf(this.xs, a.x);
        const xb = indexOf(this.xs, b.x);
        if (yi < 0 || xa < 0 || xb < 0) continue;
        const lo = Math.min(xa, xb);
        const hi = Math.max(xa, xb);
        const row = yi * this.nx;
        for (let xi = lo; xi < hi; xi++) this.shareH[row + xi]++;
        for (let xi = lo + 1; xi < hi; xi++) this.usedH[row + xi] = 1;
      } else if (Math.abs(a.x - b.x) < 0.01) {
        const xi = indexOf(this.xs, a.x);
        const ya = indexOf(this.ys, a.y);
        const yb = indexOf(this.ys, b.y);
        if (xi < 0 || ya < 0 || yb < 0) continue;
        const lo = Math.min(ya, yb);
        const hi = Math.max(ya, yb);
        for (let yi = lo; yi < hi; yi++) this.shareV[yi * this.nx + xi]++;
        for (let yi = lo + 1; yi < hi; yi++) this.usedV[yi * this.nx + xi] = 1;
      }
    }
  }

  /**
   * The cheapest walk from one node to another, setting out in one direction and preferring to
   * arrive in another; the nodes it passes through, or null when there is no way. A* over
   * (node, direction) states: the direction is part of the state because a bend costs, and what a
   * step costs depends on whether it bends. `ideal` is the point the line's lanes gravitate to:
   * its horizontal runs when `rows` is set (the bundle leaves a top or bottom), else its vertical ones.
   */
  find(start: number, startDir: Dir, goal: number, goalDir: Dir, ideal: Pt, rows: boolean): number[] | null {
    const { nx, ny, xs, ys, g, parent, opened, closed, heap } = this;
    const gen = ++this.generation;
    heap.clear();
    const gx = goal % nx;
    const gy = (goal - gx) / nx;
    // a touch over the true distance: among equally cheap ways, the one heading for the goal is
    // looked at first, which keeps the search from wandering over the whole grid
    const h = (n: number) => {
      const xi = n % nx;
      const yi = (n - xi) / nx;
      return (Math.abs(xs[xi] - xs[gx]) + Math.abs(ys[yi] - ys[gy])) * 1.0005;
    };
    const s0 = start * 4 + startDir;
    g[s0] = (startDir & 1 ? this.usedH[start] : this.usedV[start]) ? crossCost : this.vertex[start] ? vertexCost : 0;
    parent[s0] = -1;
    opened[s0] = gen;
    heap.push(g[s0] + h(start), s0);
    while (heap.size > 0) {
      const s = heap.pop();
      if (closed[s] === gen) continue;
      closed[s] = gen;
      const n = s >> 2;
      const dir = s & 3;
      if (n === goal) {
        const nodes: number[] = [];
        for (let cur = s; cur >= 0; cur = parent[cur]) nodes.push(cur >> 2);
        return nodes.reverse();
      }
      const xi = n % nx;
      const yi = (n - xi) / nx;
      for (let nd = 0; nd < 4; nd++) {
        if (nd === ((dir + 2) & 3)) continue; // never straight back
        let n2: number;
        let len: number;
        let share: number;
        if (nd === 0) {
          if (xi + 1 >= nx || this.blockedH[n]) continue;
          n2 = n + 1;
          len = xs[xi + 1] - xs[xi];
          share = this.shareH[n];
        } else if (nd === 1) {
          if (yi + 1 >= ny || this.blockedV[n]) continue;
          n2 = n + nx;
          len = ys[yi + 1] - ys[yi];
          share = this.shareV[n];
        } else if (nd === 2) {
          if (xi === 0 || this.blockedH[n - 1]) continue;
          n2 = n - 1;
          len = xs[xi] - xs[xi - 1];
          share = this.shareH[n - 1];
        } else {
          if (yi === 0 || this.blockedV[n - nx]) continue;
          n2 = n - nx;
          len = ys[yi] - ys[yi - 1];
          share = this.shareV[n - nx];
        }
        // crossing: a line of the other orientation already passes straight through the node entered;
        // touching: a line bends or ends there
        const crosses = nd & 1 ? this.usedH[n2] : this.usedV[n2];
        let cost = len + share * (len * shareCost + shareFixed) + (crosses ? crossCost : this.vertex[n2] ? vertexCost : 0);
        // the lane bias: the runs that pick a lane pay for its distance from the ideal one
        if (rows ? !(nd & 1) : nd & 1) cost += len * laneBias * (rows ? Math.abs(ys[yi] - ideal.y) : Math.abs(xs[xi] - ideal.x));
        if (nd !== dir) cost += bendCost;
        if (n2 === goal && nd !== goalDir) cost += bendCost; // the stub into the box is one more piece
        const s2 = n2 * 4 + nd;
        const ng = g[s] + cost;
        if (opened[s2] === gen && ng >= g[s2]) continue;
        opened[s2] = gen;
        g[s2] = ng;
        parent[s2] = s;
        heap.push(ng + h(n2), s2);
      }
    }
    return null;
  }

  point(n: number): Pt {
    const xi = n % this.nx;
    return { x: this.xs[xi], y: this.ys[(n - xi) / this.nx] };
  }
}

/** The index of the first value above v, or the length when there is none. */
function firstAbove(sorted: number[], v: number): number {
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
function lastBelow(sorted: number[], v: number): number {
  let lo = 0;
  let hi = sorted.length;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (sorted[mid] >= v) hi = mid;
    else lo = mid + 1;
  }
  return lo - 1;
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

// ---- what is done with the routes ----

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

/** Whether an axis-aligned segment has any of its length strictly inside a box. */
function segmentInside(p: Pt, q: Pt, r: RouteRect): boolean {
  const eps = 0.5;
  const x1 = Math.min(p.x, q.x);
  const x2 = Math.max(p.x, q.x);
  const y1 = Math.min(p.y, q.y);
  const y2 = Math.max(p.y, q.y);
  return x2 > r.x + eps && x1 < r.x + r.w - eps && y2 > r.y + eps && y1 < r.y + r.h - eps;
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

/** The line as n points evenly spaced along its length, so two lines can be blended point by point. */
export function resample(points: Pt[], n: number): Pt[] {
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
