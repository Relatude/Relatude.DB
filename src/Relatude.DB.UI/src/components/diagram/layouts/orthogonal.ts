/**
 * An orthogonal drawing, by topology, shape and metrics.
 *
 * Topology: the boxes are taken in one walk of the model, the most connected first and then whichever
 * has most of its neighbours already down, so every box arrives with something to attach itself to.
 *
 * Shape: an arriving box takes a cell of a square grid straight above, below, left of or right of a
 * neighbour already placed. That choice is the shape of the drawing - it fixes which side of a box
 * each of its lines leaves by - so it is what the cost is paid for: a bend, a run over a box in the
 * way, a run down a lane other lines already use, a side of a box already carrying lines, and the
 * distance itself. A few sweeps of moving one box at a time to a better free cell follow.
 *
 * Metrics: the grid is then compacted. Every column is as wide as its widest box, every row as tall
 * as its tallest, and a column or row nothing sits in becomes a lane for the lines to pass down.
 *
 * Bend minimisation proper is left to the router: it has the whole picture, including where each line
 * meets its boxes, which is more than a grid of cells knows. What this arrangement gives it is a
 * picture where most lines can be a single straight run.
 */

import { adjacency, averageSize, gaps, normalize, pairs, type LayoutBox, type LayoutEdge } from "./graph";

interface Cell {
  cx: number;
  cy: number;
}

/** How wide the grid may grow, and how much taller than that, since a row is shorter than a column
 *  is wide: past this a box pays heavily, which is what makes the drawing wrap into a block. */
interface Frame {
  budget: number;
  rowWeight: number;
}

/** How far past a neighbour a box may look for a free cell. */
const reach = 3;
/** Costs, in grid steps. */
const bendCost = 5;
const blockCost = 9;
const laneCost = 2.5;
const sideCost = 2.5;
const spreadCost = 0.3;
const outsideCost = 14;
const sweeps = 4;
/** How much of a box's width an empty column is worth as a lane for the lines. */
const laneWidth = 30;

const key = (c: Cell) => c.cx + "," + c.cy;

export function orthogonalLayout(boxes: LayoutBox[], edges: LayoutEdge[], spacing: number) {
  const n = boxes.length;
  if (n === 0) return;
  const adj = adjacency(n, pairs(boxes, edges));
  const size = averageSize(boxes);
  const gap = gaps(spacing);
  const frame: Frame = { budget: Math.max(2, Math.ceil(Math.sqrt(n) * 0.7)), rowWeight: (size.h + gap.box) / (size.w + gap.box) };
  const cells = embed(n, adj, frame);
  compact(boxes, cells, spacing);
  normalize(boxes);
}

/** How far out of the middle a cell is, rows counting for less because they are shorter. */
function outward(c: Cell, frame: Frame): number {
  const ex = Math.abs(c.cx);
  const ey = Math.abs(c.cy) * frame.rowWeight;
  return spreadCost * (ex + ey) + outsideCost * (Math.max(0, ex - frame.budget) + Math.max(0, ey - frame.budget));
}

/** The most connected box first, then whichever has most of its neighbours placed already. */
function walkOrder(n: number, adj: number[][]): number[] {
  const placed = new Uint8Array(n);
  const out: number[] = [];
  for (let step = 0; step < n; step++) {
    let best = -1;
    let bestScore = -1;
    for (let v = 0; v < n; v++) {
      if (placed[v]) continue;
      const known = adj[v].filter((w) => placed[w]).length;
      const score = known * 1000 + adj[v].length;
      if (score > bestScore) {
        bestScore = score;
        best = v;
      }
    }
    placed[best] = 1;
    out.push(best);
  }
  return out;
}

function embed(n: number, adj: number[][], frame: Frame): Cell[] {
  const cells = new Array<Cell>(n);
  const taken = new Map<string, number>();
  const lanes = new Map<string, number>();
  const sides = Array.from({ length: n }, () => [0, 0, 0, 0]);
  const placed = new Uint8Array(n);
  for (const v of walkOrder(n, adj)) {
    const known = adj[v].filter((w) => placed[w]);
    const cell = known.length === 0 ? freeStart(taken, frame) : cheapest(v, known, cells, taken, lanes, sides, frame);
    cells[v] = cell;
    taken.set(key(cell), v);
    placed[v] = 1;
    for (const w of known) claim(cell, cells[w], v, w, lanes, sides);
  }
  for (let sweep = 0; sweep < sweeps; sweep++) {
    let moved = false;
    for (let v = 0; v < n; v++) {
      const known = adj[v];
      if (known.length === 0) continue;
      const before = cells[v];
      let bestCost = cost(v, before, known, cells, taken, lanes, sides, frame) - 0.5;
      taken.delete(key(before));
      let best = before;
      for (const c of candidates(known, cells, taken)) {
        const value = cost(v, c, known, cells, taken, lanes, sides, frame);
        if (value < bestCost) {
          bestCost = value;
          best = c;
        }
      }
      if (best.cx !== before.cx || best.cy !== before.cy) {
        cells[v] = best;
        moved = true;
      }
      taken.set(key(cells[v]), v);
    }
    if (!moved) break;
    rebuild(cells, adj, lanes, sides);
  }
  return cells;
}

/** The free cell nearest the middle: where a box joined to nothing placed yet goes, so the parts of a
 *  model that have nothing to do with each other still pack into a block. */
function freeStart(taken: Map<string, number>, frame: Frame): Cell {
  for (let ring = 0; ; ring++) {
    let best: Cell | null = null;
    let bestCost = Infinity;
    for (let dx = -ring; dx <= ring; dx++) {
      const dy = ring - Math.abs(dx);
      for (const c of dy === 0 ? [{ cx: dx, cy: 0 }] : [{ cx: dx, cy: dy }, { cx: dx, cy: -dy }]) {
        if (taken.has(key(c))) continue;
        const value = outward(c, frame);
        if (value < bestCost) {
          bestCost = value;
          best = c;
        }
      }
    }
    if (best) return best;
  }
}

/** Every free cell within reach of a placed neighbour, in the four directions. */
function candidates(known: number[], cells: Cell[], taken: Map<string, number>): Cell[] {
  const out: Cell[] = [];
  const seen = new Set<string>();
  for (const w of known) {
    for (const [dx, dy] of [
      [1, 0],
      [0, 1],
      [-1, 0],
      [0, -1],
    ]) {
      for (let step = 1; step <= reach; step++) {
        const c = { cx: cells[w].cx + dx * step, cy: cells[w].cy + dy * step };
        const k = key(c);
        if (taken.has(k) || seen.has(k)) continue;
        seen.add(k);
        out.push(c);
      }
    }
  }
  return out;
}

function cheapest(v: number, known: number[], cells: Cell[], taken: Map<string, number>, lanes: Map<string, number>, sides: number[][], frame: Frame): Cell {
  let best: Cell | null = null;
  let bestCost = Infinity;
  for (const c of candidates(known, cells, taken)) {
    const value = cost(v, c, known, cells, taken, lanes, sides, frame);
    if (value < bestCost) {
      bestCost = value;
      best = c;
    }
  }
  return best ?? freeStart(taken, frame);
}

function cost(v: number, c: Cell, known: number[], cells: Cell[], taken: Map<string, number>, lanes: Map<string, number>, sides: number[][], frame: Frame): number {
  let total = outward(c, frame);
  const own = [0, 0, 0, 0]; // lines this box would send out each of its own sides
  for (const w of known) {
    const u = cells[w];
    total += Math.abs(c.cx - u.cx) + Math.abs(c.cy - u.cy);
    if (c.cx !== u.cx && c.cy !== u.cy) total += bendCost;
    total += sideCost * Math.min(3, sides[w][direction(u, c)]);
    total += sideCost * own[direction(c, u)]++;
    for (const step of path(c, u)) {
      const k = key(step);
      const other = taken.get(k);
      if (other !== undefined && other !== v) total += blockCost;
      total += laneCost * Math.min(3, lanes.get(k) ?? 0);
    }
  }
  return total;
}

/** 0 right, 1 down, 2 left, 3 up: the side of `from` that `to` lies off. */
function direction(from: Cell, to: Cell): number {
  const dx = to.cx - from.cx;
  const dy = to.cy - from.cy;
  if (Math.abs(dx) >= Math.abs(dy)) return dx >= 0 ? 0 : 2;
  return dy >= 0 ? 1 : 3;
}

/** The cells an L-shaped run between two cells passes through, ends left out. */
function path(a: Cell, b: Cell): Cell[] {
  const out: Cell[] = [];
  const stepX = Math.sign(b.cx - a.cx);
  const stepY = Math.sign(b.cy - a.cy);
  for (let x = a.cx + stepX; stepX !== 0 && x !== b.cx + stepX; x += stepX) out.push({ cx: x, cy: a.cy });
  for (let y = a.cy + stepY; stepY !== 0 && y !== b.cy; y += stepY) out.push({ cx: b.cx, cy: y });
  return out.filter((c) => !(c.cx === a.cx && c.cy === a.cy) && !(c.cx === b.cx && c.cy === b.cy));
}

function claim(from: Cell, to: Cell, v: number, w: number, lanes: Map<string, number>, sides: number[][]) {
  sides[v][direction(from, to)]++;
  sides[w][direction(to, from)]++;
  for (const step of path(from, to)) lanes.set(key(step), (lanes.get(key(step)) ?? 0) + 1);
}

function rebuild(cells: Cell[], adj: number[][], lanes: Map<string, number>, sides: number[][]) {
  lanes.clear();
  for (const s of sides) s.fill(0);
  for (let v = 0; v < cells.length; v++) for (const w of adj[v]) if (w > v) claim(cells[v], cells[w], v, w, lanes, sides);
}

/** Columns as wide as their widest box, rows as tall as their tallest, empty ones as lanes. */
function compact(boxes: LayoutBox[], cells: Cell[], spacing: number) {
  const gap = gaps(spacing);
  const span = (of: (c: Cell) => number, size: (i: number) => number) => {
    const keys = [...new Set(cells.map(of))].sort((a, b) => a - b);
    const extent = new Map(keys.map((k) => [k, Math.max(...cells.map((c, i) => (of(c) === k ? size(i) : 0)))]));
    const at = new Map<number, number>();
    let along = 0;
    keys.forEach((k, i) => {
      if (i > 0) along += gap.box + Math.min(2, k - keys[i - 1] - 1) * laneWidth * spacing;
      at.set(k, along);
      along += extent.get(k)!;
    });
    return { at, extent };
  };
  const columns = span((c) => c.cx, (i) => boxes[i].w);
  const rows = span((c) => c.cy, (i) => boxes[i].h);
  boxes.forEach((b, i) => {
    // centred in its column and its row, so a small box does not sit against a tall neighbour's lines
    b.x = columns.at.get(cells[i].cx)! + (columns.extent.get(cells[i].cx)! - b.w) / 2;
    b.y = rows.at.get(cells[i].cy)! + (rows.extent.get(cells[i].cy)! - b.h) / 2;
  });
}
