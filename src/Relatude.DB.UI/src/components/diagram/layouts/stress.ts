/**
 * Constraint-based optimisation: stress majorization for the picture, PRISM for the boxes.
 *
 * Every pair of types is given a distance it would rather be at - how many steps apart they are in
 * the model, times a step length - and the arrangement is the one that comes closest to all of those
 * at once. Majorization is what solves it: each round moves every box to the weighted mean of where
 * every other box would put it, which lowers the total stress and never raises it, so it walks
 * steadily downhill rather than swinging about like a force simulation. Pairs further apart are
 * weighted less (1 over the distance squared), which is what keeps the near structure sharp.
 *
 * The result is a picture of points, and points do not overlap. PRISM takes over from there: a
 * proximity graph over the boxes as they lie, each of its pairs asked to grow by however much the two
 * boxes overlap, and the same majorization solves that. Because only near pairs are constrained, the
 * shape the stress model found survives being made room in. Repeated until nothing overlaps.
 *
 * The proximity graph here is each box's nearest handful rather than a Delaunay triangulation: it is
 * the sparseness that matters, and the boxes are few enough to find neighbours by measuring.
 */

import { averageSize, gaps, graphDistances, normalize, pairs, separate, spiral, adjacency, type LayoutBox, type LayoutEdge } from "./graph";

const stressRounds = 300;
const prismPasses = 24;
const prismRounds = 10;
/** How much of the way to its target a PRISM pair may be pulled in one pass. */
const maxGrowth = 1.5;
/** How many neighbours each box is held apart from. */
const nearest = 8;

export function stressLayout(boxes: LayoutBox[], edges: LayoutEdge[], spacing: number) {
  const n = boxes.length;
  if (n < 2) return;
  const size = averageSize(boxes);
  const gap = gaps(spacing);
  const step = Math.max(size.w, size.h) + gap.box;
  const d = graphDistances(adjacency(n, pairs(boxes, edges)));
  spiral(boxes, Math.sqrt(n) * step * 0.7);
  const cx = Float64Array.from(boxes, (b) => b.x);
  const cy = Float64Array.from(boxes, (b) => b.y);
  const all: Target[] = [];
  for (let i = 0; i < n; i++) for (let j = i + 1; j < n; j++) all.push({ i, j, d: d[i * n + j] * step });
  // every pair is a constraint, so a big model is a lot of them: it gets fewer rounds
  majorize(cx, cy, all, n > 200 ? stressRounds / 2 : stressRounds, step / 400);
  prism(boxes, cx, cy, step, gap.pad);
  boxes.forEach((b, i) => {
    b.x = cx[i] - b.w / 2;
    b.y = cy[i] - b.h / 2;
  });
  separate(boxes, gap.pad);
  normalize(boxes);
}

interface Target {
  i: number;
  j: number;
  d: number;
}

/**
 * Stress majorization, one box at a time (Gauss-Seidel): a box goes where the pairs it is in would
 * have it, each pair weighted by 1 over its target distance squared. Stops once a round moves nothing
 * worth moving.
 */
function majorize(cx: Float64Array, cy: Float64Array, targets: Target[], rounds: number, settled: number) {
  const n = cx.length;
  const held: number[][] = Array.from({ length: n }, () => []);
  targets.forEach((t, k) => {
    held[t.i].push(k);
    held[t.j].push(k);
  });
  for (let round = 0; round < rounds; round++) {
    let moved = 0;
    for (let i = 0; i < n; i++) {
      let sumX = 0;
      let sumY = 0;
      let weight = 0;
      for (const k of held[i]) {
        const t = targets[k];
        if (t.d <= 0) continue;
        const j = t.i === i ? t.j : t.i;
        let ddx = cx[i] - cx[j];
        let ddy = cy[i] - cy[j];
        let dist = Math.hypot(ddx, ddy);
        if (dist < 1e-6) {
          // exactly on top of each other: the same nudge every time this pair meets
          ddx = ((i * 7 + j * 13) % 11) / 11 - 0.5;
          ddy = ((i * 11 + j * 3) % 11) / 11 - 0.5;
          dist = Math.hypot(ddx, ddy) || 1;
        }
        const w = 1 / (t.d * t.d);
        sumX += w * (cx[j] + (t.d * ddx) / dist);
        sumY += w * (cy[j] + (t.d * ddy) / dist);
        weight += w;
      }
      if (weight === 0) continue;
      const nx = sumX / weight;
      const ny = sumY / weight;
      moved = Math.max(moved, Math.abs(nx - cx[i]) + Math.abs(ny - cy[i]));
      cx[i] = nx;
      cy[i] = ny;
    }
    if (moved < settled) return;
  }
}

/** PRISM: near pairs asked to grow by however much their boxes overlap, until none do. */
function prism(boxes: LayoutBox[], cx: Float64Array, cy: Float64Array, step: number, pad: number) {
  for (let pass = 0; pass < prismPasses; pass++) {
    const targets: Target[] = [];
    let worst = 1;
    for (const [i, j] of proximity(cx, cy)) {
      const ddx = Math.abs(cx[i] - cx[j]);
      const ddy = Math.abs(cy[i] - cy[j]);
      const dist = Math.hypot(cx[i] - cx[j], cy[i] - cy[j]);
      // how much further apart they would have to be, whichever axis is cheaper to fix
      const grow = Math.max(1, Math.min(((boxes[i].w + boxes[j].w) / 2 + pad) / Math.max(ddx, 1e-6), ((boxes[i].h + boxes[j].h) / 2 + pad) / Math.max(ddy, 1e-6)));
      worst = Math.max(worst, grow);
      targets.push({ i, j, d: Math.max(dist, 1e-6) * Math.min(grow, maxGrowth) });
    }
    if (worst <= 1.0001) return;
    majorize(cx, cy, targets, prismRounds, step / 2000);
  }
}

/** Each box paired with its nearest few, both ways round, once each. */
function proximity(cx: Float64Array, cy: Float64Array): [number, number][] {
  const n = cx.length;
  const out: [number, number][] = [];
  const seen = new Set<number>();
  const by: { j: number; d: number }[] = [];
  for (let i = 0; i < n; i++) {
    by.length = 0;
    for (let j = 0; j < n; j++) if (j !== i) by.push({ j, d: Math.hypot(cx[i] - cx[j], cy[i] - cy[j]) });
    by.sort((a, b) => a.d - b.d);
    for (const near of by.slice(0, nearest)) {
      const key = Math.min(i, near.j) * n + Math.max(i, near.j);
      if (seen.has(key)) continue;
      seen.add(key);
      out.push([i, near.j]);
    }
  }
  return out;
}
