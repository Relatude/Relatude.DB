/**
 * What every arrangement works on. The diagram's own boxes and edges already have these shapes, so
 * nothing is copied in and out: an arrangement is handed the boxes and writes x and y onto them.
 */

export interface LayoutBox {
  id: string;
  x: number;
  y: number;
  w: number;
  h: number;
}

export type EdgeKind = "inherits" | "relation" | "reference" | "embeds";

export interface LayoutEdge {
  from: string;
  to: string;
  kind: EdgeKind;
}

/** Side to side between two boxes: room for a line or two to pass between them. */
const boxGap = 52;
/** Between one layer of a hierarchy and the next. */
const layerGap = 92;

/** The room an arrangement leaves around its boxes. Every distance a method works in comes from
 *  here, so one number tightens the whole picture or opens it up without changing what it says. */
export interface Gaps {
  box: number;
  layer: number;
  /** What no two boxes may come closer than once a method that works on points has placed them. */
  pad: number;
}

export function gaps(spacing: number): Gaps {
  return { box: boxGap * spacing, layer: layerGap * spacing, pad: boxGap * 0.7 * spacing };
}

/** Every pair of boxes with a line between them, as indexes, once each and never to itself. */
export function pairs(boxes: LayoutBox[], edges: LayoutEdge[]): [number, number][] {
  const index = new Map(boxes.map((b, i) => [b.id, i]));
  const seen = new Set<number>();
  const out: [number, number][] = [];
  for (const e of edges) {
    const i = index.get(e.from);
    const j = index.get(e.to);
    if (i === undefined || j === undefined || i === j) continue;
    const key = Math.min(i, j) * boxes.length + Math.max(i, j);
    if (seen.has(key)) continue;
    seen.add(key);
    out.push([i, j]);
  }
  return out;
}

/**
 * The same pairs, pointing the way the model reads: from a parent to its children, from a relation's
 * source to its target, from the type that holds a reference or an embedded node to the one it names.
 */
export function directedPairs(boxes: LayoutBox[], edges: LayoutEdge[]): [number, number][] {
  const index = new Map(boxes.map((b, i) => [b.id, i]));
  const seen = new Set<number>();
  const out: [number, number][] = [];
  for (const e of edges) {
    const a = index.get(e.kind === "inherits" ? e.to : e.from);
    const b = index.get(e.kind === "inherits" ? e.from : e.to);
    if (a === undefined || b === undefined || a === b) continue;
    const key = a * boxes.length + b;
    if (seen.has(key)) continue;
    seen.add(key);
    out.push([a, b]);
  }
  return out;
}

export function adjacency(n: number, ps: [number, number][]): number[][] {
  const adj: number[][] = Array.from({ length: n }, () => []);
  for (const [i, j] of ps) {
    adj[i].push(j);
    adj[j].push(i);
  }
  return adj;
}

/**
 * How many steps apart every pair of boxes is, breadth first, as a flat n by n table. Two boxes with
 * no path between them are put one step further than the model's own width, so the parts of a model
 * that have nothing to do with each other sit apart without flying off.
 */
export function graphDistances(adj: number[][]): Float64Array {
  const n = adj.length;
  const d = new Float64Array(n * n);
  const queue = new Int32Array(n);
  let widest = 1;
  for (let s = 0; s < n; s++) {
    const row = s * n;
    d.fill(-1, row, row + n);
    d[row + s] = 0;
    queue[0] = s;
    for (let head = 0, tail = 1; head < tail; head++) {
      const v = queue[head];
      for (const w of adj[v]) {
        if (d[row + w] >= 0) continue;
        d[row + w] = d[row + v] + 1;
        if (d[row + w] > widest) widest = d[row + w];
        queue[tail++] = w;
      }
    }
  }
  for (let i = 0; i < d.length; i++) if (d[i] < 0) d[i] = widest + 1;
  return d;
}

/** A deterministic even spread to start from, so a model always relaxes into the same picture. */
export function spiral(boxes: LayoutBox[], spread: number) {
  const n = boxes.length;
  boxes.forEach((b, i) => {
    const a = i * 2.399963229728653; // the golden angle, or the start comes out in spokes
    const r = spread * Math.sqrt((i + 0.5) / n);
    b.x = Math.cos(a) * r;
    b.y = Math.sin(a) * r;
  });
}

/** Shifts everything so the drawing starts at the origin, where the fit and the export read it. */
export function normalize(boxes: LayoutBox[]) {
  if (boxes.length === 0) return;
  const minX = Math.min(...boxes.map((b) => b.x));
  const minY = Math.min(...boxes.map((b) => b.y));
  for (const b of boxes) {
    b.x -= minX;
    b.y -= minY;
  }
}

/**
 * Pushes overlapping boxes apart the short way - the way apart that moves them least - and, when a
 * crowd is too tight for that to settle, spreads the whole picture out a little and tries again. The
 * arrangements that work on points rather than boxes all end here.
 */
export function separate(boxes: LayoutBox[], pad: number) {
  for (let attempt = 0; attempt < 5; attempt++) {
    if (push(boxes, pad)) return;
    // room has to come from somewhere: everything moves out from the middle by a tenth
    const cx = boxes.reduce((s, b) => s + b.x + b.w / 2, 0) / boxes.length;
    const cy = boxes.reduce((s, b) => s + b.y + b.h / 2, 0) / boxes.length;
    for (const b of boxes) {
      b.x = cx + (b.x + b.w / 2 - cx) * 1.1 - b.w / 2;
      b.y = cy + (b.y + b.h / 2 - cy) * 1.1 - b.h / 2;
    }
  }
  push(boxes, pad);
}

function push(boxes: LayoutBox[], pad: number, rounds = 120): boolean {
  const n = boxes.length;
  for (let round = 0; round < rounds; round++) {
    let hit = false;
    for (let i = 0; i < n; i++) {
      for (let j = i + 1; j < n; j++) {
        const a = boxes[i];
        const b = boxes[j];
        const ox = (a.w + b.w) / 2 + pad - Math.abs(a.x + a.w / 2 - (b.x + b.w / 2));
        const oy = (a.h + b.h) / 2 + pad - Math.abs(a.y + a.h / 2 - (b.y + b.h / 2));
        if (ox <= 0 || oy <= 0) continue;
        hit = true;
        if (ox < oy) {
          const s = (a.x <= b.x ? -1 : 1) * ox * 0.5;
          a.x += s;
          b.x -= s;
        } else {
          const s = (a.y <= b.y ? -1 : 1) * oy * 0.5;
          a.y += s;
          b.y -= s;
        }
      }
    }
    if (!hit) return true;
  }
  return false;
}

/** The size of a typical box, which is what every arrangement measures its distances in. */
export function averageSize(boxes: LayoutBox[]): { w: number; h: number } {
  const w = boxes.reduce((s, b) => s + b.w, 0) / boxes.length;
  const h = boxes.reduce((s, b) => s + b.h, 0) / boxes.length;
  return { w, h };
}
