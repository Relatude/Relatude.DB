/**
 * The grid the lines are found on and the search over it. Its columns and rows are the edges of every
 * box pushed out by a margin, a few lanes through every gap between boxes, and the points where lines
 * meet the boxes; a segment inside a pushed-out box is blocked. A line is then the cheapest walk over
 * that grid, where a walk pays for its length, for every bend, for running on top of a line already
 * drawn (so parallel lines take lanes of their own) and for crossing one (so, where it can, a line
 * goes round another rather than through it).
 */

import { firstAbove, indexOf, lastBelow, routeMargin, type Dir, type Pt, type RouteRect } from "./geometry";

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
/** Bigger grids than this are not worth routing on: every line would take a good part of a second. */
export const maxGridNodes = 300_000;

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
export class Grid {
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
