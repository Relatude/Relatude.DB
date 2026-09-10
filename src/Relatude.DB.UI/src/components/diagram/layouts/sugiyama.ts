/**
 * The Sugiyama method: the model drawn down its own direction, a parent above its children and a
 * relation's source above its target.
 *
 * Its four steps, in order. Back edges are turned round, so what is left has a direction to read.
 * Every box is then given a layer - the longest path to it, and afterwards pulled down to just above
 * its nearest successor, so no line is longer than it has to be - and a line spanning more than one
 * layer is given a thin stand-in in each layer it passes, which earns it a lane of its own and makes
 * the boxes leave room for it. Boxes within a layer are ordered by the median of where their
 * neighbours sit, swept down and up, then improved by swapping neighbours for as long as that removes
 * crossings. Each is finally given an x by the priority method: a box moves to the middle of its
 * neighbours in the layer before it, and may push the boxes beside it aside as far as they in turn
 * have room - stand-ins pushing hardest, since a long line kept straight is worth more than a tidy
 * row.
 */

import { directedPairs, gaps, normalize, type Gaps, type LayoutBox, type LayoutEdge } from "./graph";

type Link = [number, number];

interface Vertex {
  /** The box, or null for a stand-in on a line passing through the layer. */
  box: LayoutBox | null;
  rank: number;
  w: number;
  h: number;
  cx: number;
  /** Vertexes in the layer above and below, by index. */
  up: number[];
  down: number[];
}

const standInWidth = 16;
const sweeps = 24;
const transposePasses = 4;
const xPasses = 8;

export function sugiyama(boxes: LayoutBox[], edges: LayoutEdge[], spacing: number) {
  if (boxes.length === 0) return;
  const gap = gaps(spacing);
  const links = acyclic(boxes.length, directedPairs(boxes, edges));
  const { vertices, layers } = expand(boxes, links, compactRanks(ranks(boxes.length, links)));
  order(vertices, layers);
  wrap(vertices, layers, boxes.length, gap);
  place(vertices, layers, gap);
  normalize(boxes);
}

/** A depth-first walk; any edge back to a box on the current path is turned round. */
function acyclic(n: number, links: Link[]): Link[] {
  const out: number[][] = Array.from({ length: n }, () => []);
  for (const [a, b] of links) out[a].push(b);
  const state = new Uint8Array(n); // 0 unseen, 1 on the path, 2 done
  const back = new Set<number>();
  for (let s = 0; s < n; s++) {
    if (state[s] !== 0) continue;
    state[s] = 1;
    const path = [s];
    const at = [0];
    while (path.length > 0) {
      const v = path[path.length - 1];
      if (at[at.length - 1] < out[v].length) {
        const w = out[v][at[at.length - 1]++];
        if (state[w] === 1) back.add(v * n + w);
        else if (state[w] === 0) {
          state[w] = 1;
          path.push(w);
          at.push(0);
        }
      } else {
        state[v] = 2;
        path.pop();
        at.pop();
      }
    }
  }
  const seen = new Set<number>();
  const kept: Link[] = [];
  for (const [a, b] of links) {
    const [p, q] = back.has(a * n + b) ? [b, a] : [a, b];
    if (p === q || seen.has(p * n + q)) continue;
    seen.add(p * n + q);
    kept.push([p, q]);
  }
  return kept;
}

function ranks(n: number, links: Link[]): number[] {
  const succ: number[][] = Array.from({ length: n }, () => []);
  const left = new Int32Array(n);
  for (const [a, b] of links) {
    succ[a].push(b);
    left[b]++;
  }
  const walk: number[] = [];
  for (let v = 0; v < n; v++) if (left[v] === 0) walk.push(v);
  for (let head = 0; head < walk.length; head++) for (const w of succ[walk[head]]) if (--left[w] === 0) walk.push(w);
  const rank = new Array<number>(n).fill(0);
  for (const v of walk) for (const w of succ[v]) rank[w] = Math.max(rank[w], rank[v] + 1);
  for (let i = walk.length - 1; i >= 0; i--) {
    const v = walk[i];
    if (succ[v].length > 0) rank[v] = Math.min(...succ[v].map((w) => rank[w])) - 1;
  }
  return rank;
}

/** Layers nothing is left in are dropped: pulling boxes down can empty the one they came from. */
function compactRanks(rank: number[]): number[] {
  const used = [...new Set(rank)].sort((a, b) => a - b);
  const map = new Map(used.map((r, i) => [r, i]));
  return rank.map((r) => map.get(r)!);
}

function expand(boxes: LayoutBox[], links: Link[], rank: number[]) {
  const vertices: Vertex[] = boxes.map((b, i) => ({ box: b, rank: rank[i], w: b.w, h: b.h, cx: 0, up: [], down: [] }));
  const join = (a: number, b: number) => {
    vertices[a].down.push(b);
    vertices[b].up.push(a);
  };
  for (const [a, b] of links) {
    if (rank[b] - rank[a] <= 1) {
      join(a, b);
      continue;
    }
    let prev = a;
    for (let r = rank[a] + 1; r < rank[b]; r++) {
      vertices.push({ box: null, rank: r, w: standInWidth, h: 0, cx: 0, up: [], down: [] });
      join(prev, vertices.length - 1);
      prev = vertices.length - 1;
    }
    join(prev, b);
  }
  const layers: number[][] = Array.from({ length: Math.max(...vertices.map((v) => v.rank)) + 1 }, () => []);
  vertices.forEach((v, i) => layers[v.rank].push(i));
  return { vertices, layers };
}

// ---- crossing reduction ----

function order(vertices: Vertex[], layers: number[][]) {
  let best = layers.map((l) => [...l]);
  let fewest = crossings(vertices, layers);
  for (let sweep = 0; sweep < sweeps && fewest > 0; sweep++) {
    medianSweep(vertices, layers, sweep % 2 === 0);
    transpose(vertices, layers);
    const c = crossings(vertices, layers);
    if (c < fewest) {
      fewest = c;
      best = layers.map((l) => [...l]);
    }
  }
  layers.forEach((l, i) => l.splice(0, l.length, ...best[i]));
}

function positions(vertices: Vertex[], layers: number[][]): Int32Array {
  const pos = new Int32Array(vertices.length);
  for (const l of layers) l.forEach((v, i) => (pos[v] = i));
  return pos;
}

/** The weighted median of where a box's neighbours in the fixed layer sit. */
function median(ns: number[], pos: Int32Array, fallback: number): number {
  if (ns.length === 0) return fallback;
  const ps = ns.map((w) => pos[w]).sort((a, b) => a - b);
  const m = ps.length >> 1;
  if (ps.length % 2 === 1) return ps[m];
  if (ps.length === 2) return (ps[0] + ps[1]) / 2;
  const before = ps[m - 1] - ps[0];
  const after = ps[ps.length - 1] - ps[m];
  return before + after === 0 ? (ps[m - 1] + ps[m]) / 2 : (ps[m - 1] * after + ps[m] * before) / (before + after);
}

function medianSweep(vertices: Vertex[], layers: number[][], down: boolean) {
  const pos = positions(vertices, layers);
  const first = down ? 1 : layers.length - 2;
  const past = down ? layers.length : -1;
  const step = down ? 1 : -1;
  for (let r = first; r !== past; r += step) {
    const layer = layers[r];
    const key = new Map(layer.map((v, i) => [v, median(down ? vertices[v].up : vertices[v].down, pos, i)]));
    layer.sort((a, b) => key.get(a)! - key.get(b)! || pos[a] - pos[b]);
    layer.forEach((v, i) => (pos[v] = i));
  }
}

/** Swaps neighbours while that removes crossings; only the pair's own edges have to be counted. */
function transpose(vertices: Vertex[], layers: number[][]) {
  const pos = positions(vertices, layers);
  for (let pass = 0; pass < transposePasses; pass++) {
    let swapped = false;
    for (const layer of layers) {
      for (let i = 0; i + 1 < layer.length; i++) {
        const a = vertices[layer[i]];
        const b = vertices[layer[i + 1]];
        let now = 0;
        let flipped = 0;
        for (const side of ["up", "down"] as const) {
          for (const p of a[side]) {
            for (const q of b[side]) {
              if (pos[p] > pos[q]) now++;
              else if (pos[q] > pos[p]) flipped++;
            }
          }
        }
        if (flipped >= now) continue;
        const moved = layer[i];
        layer[i] = layer[i + 1];
        layer[i + 1] = moved;
        pos[layer[i]] = i;
        pos[layer[i + 1]] = i + 1;
        swapped = true;
      }
    }
    if (!swapped) return;
  }
}

function crossings(vertices: Vertex[], layers: number[][]): number {
  const pos = positions(vertices, layers);
  let total = 0;
  for (let r = 0; r + 1 < layers.length; r++) {
    const ends: number[][] = [];
    for (const v of layers[r]) for (const w of vertices[v].down) ends.push([pos[v], pos[w]]);
    ends.sort((a, b) => a[0] - b[0] || a[1] - b[1]);
    for (let i = 0; i < ends.length; i++) for (let j = i + 1; j < ends.length; j++) if (ends[j][1] < ends[i][1]) total++;
  }
  return total;
}

/**
 * A layer too wide to read is broken into rows of its own, in the order the crossing reduction left
 * it. A model where little inherits anything is nearly all one layer, and a layer of eighty types is
 * a line off the edge of the screen rather than a drawing; this is what the layered arrangements that
 * have a wrapping strategy do with it.
 */
function wrap(vertices: Vertex[], layers: number[][], boxCount: number, gap: Gaps) {
  const boxWidth = Math.max(...vertices.map((v) => (v.box ? v.w : 0)));
  const budget = Math.max(4, Math.ceil(Math.sqrt(boxCount) * 1.4)) * (boxWidth + gap.box);
  const rows: number[][] = [];
  for (const layer of layers) {
    const width = layer.reduce((s, v) => s + vertices[v].w + gap.box, -gap.box);
    if (width <= budget || layer.length < 3) {
      rows.push(layer);
      continue;
    }
    const per = Math.ceil(layer.length / Math.ceil(width / budget));
    for (let i = 0; i < layer.length; i += per) rows.push(layer.slice(i, i + per));
  }
  layers.splice(0, layers.length, ...rows);
  rows.forEach((l, r) => l.forEach((v) => (vertices[v].rank = r)));
}

// ---- coordinates ----

function separation(a: Vertex, b: Vertex, gap: Gaps): number {
  // a stand-in is a line rather than a box, and needs no more room beside it than a lane
  const between = a.box && b.box ? gap.box : gap.box * (a.box || b.box ? 0.5 : 0.35);
  return (a.w + b.w) / 2 + between;
}

function place(vertices: Vertex[], layers: number[][], gap: Gaps) {
  for (const layer of layers) {
    let cx = 0;
    layer.forEach((v, i) => {
      cx = i === 0 ? vertices[v].w / 2 : cx + separation(vertices[layer[i - 1]], vertices[v], gap);
      vertices[v].cx = cx;
    });
  }
  const priority = vertices.map((v) => (v.box ? v.up.length + v.down.length : 1e6));
  for (let pass = 0; pass < xPasses; pass++) {
    const down = pass % 2 === 0;
    const first = down ? 1 : layers.length - 2;
    const past = down ? layers.length : -1;
    const step = down ? 1 : -1;
    for (let r = first; r !== past; r += step) {
      const layer = layers[r];
      for (const v of [...layer].sort((a, b) => priority[b] - priority[a])) {
        const ns = down ? vertices[v].up : vertices[v].down;
        if (ns.length === 0) continue;
        const delta = ns.reduce((s, w) => s + vertices[w].cx, 0) / ns.length - vertices[v].cx;
        if (Math.abs(delta) < 0.5) continue;
        const k = layer.indexOf(v);
        const dir = delta > 0 ? 1 : -1;
        shift(vertices, layer, k, dir * Math.min(Math.abs(delta), room(vertices, layer, k, dir, priority, priority[v], gap)), gap);
      }
    }
  }
  let y = 0;
  for (const layer of layers) {
    const h = Math.max(0, ...layer.map((v) => vertices[v].h));
    for (const v of layer) {
      const u = vertices[v];
      if (u.box) {
        u.box.x = u.cx - u.w / 2;
        u.box.y = y + (h - u.h) / 2;
      }
    }
    y += h + gap.layer;
  }
}

/** How far a box may move before one that matters at least as much is in the way. */
function room(vertices: Vertex[], layer: number[], k: number, dir: number, priority: number[], moving: number, gap: Gaps): number {
  const next = k + dir;
  if (next < 0 || next >= layer.length) return Infinity;
  const a = vertices[layer[k]];
  const b = vertices[layer[next]];
  const slack = Math.max(0, (dir > 0 ? b.cx - a.cx : a.cx - b.cx) - separation(a, b, gap));
  if (priority[layer[next]] >= moving) return slack;
  return slack + room(vertices, layer, next, dir, priority, moving, gap);
}

function shift(vertices: Vertex[], layer: number[], k: number, delta: number, gap: Gaps) {
  if (Math.abs(delta) < 0.5) return;
  vertices[layer[k]].cx += delta;
  if (delta > 0) {
    for (let i = k + 1; i < layer.length; i++) {
      const need = vertices[layer[i - 1]].cx + separation(vertices[layer[i - 1]], vertices[layer[i]], gap);
      if (vertices[layer[i]].cx >= need) return;
      vertices[layer[i]].cx = need;
    }
  } else {
    for (let i = k - 1; i >= 0; i--) {
      const need = vertices[layer[i + 1]].cx - separation(vertices[layer[i]], vertices[layer[i + 1]], gap);
      if (vertices[layer[i]].cx <= need) return;
      vertices[layer[i]].cx = need;
    }
  }
}
