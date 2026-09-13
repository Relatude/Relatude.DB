/**
 * The land as triangles: every country of the world, filled, from the same rings the coastlines are
 * drawn from.
 *
 * The countries used to be painted from a RASTER - a byte a cell at 0.088°, see countries.ts - and
 * a raster has cells: at a city's zoom each one was ten kilometres across, a block of colour with
 * the smooth coastline running over it several pixels to one side. What is built here instead is
 * the outline itself cut into triangles, once, at load, so the fill has exactly the edge the line
 * has at every zoom. The raster stays for what a raster is good at - which country a point is in, a
 * million times over.
 *
 * Three things have to be got right on the way from a ring of longitudes and latitudes to a list of
 * flat triangles:
 *
 *   the seam    A ring that crosses the antimeridian is stored as ...179.9, -180.0, ... (Russia's
 *               mainland runs on into Chukotka that way, and Fiji straddles it), which read
 *               literally is a polygon the width of the world. Each ring is FOLLOWED ROUND so every
 *               step is the small one it really is, and then cut at ±180 into pieces that each lie
 *               on the map. A ring that goes right round the world without closing (Antarctica's
 *               coast) is closed through the pole.
 *   the holes   A ring inside another ring of the same country is a hole in it - Lesotho, San
 *               Marino, the enclaves of the Fergana valley - and a ring inside THAT is land again.
 *               The source flattens every country to a bag of rings, so the nesting is worked out
 *               here by containment.
 *   the ball    A triangle is flat. One with a corner thirty degrees from the other two sinks a long
 *               way into the globe between them, and where it meets the edge of the ball the cut
 *               comes out wrong by that much. So every edge is broken into pieces of a few degrees
 *               and the triangle filled in between them - the boundary by the same rule the lines
 *               use (see addRun in mapField.ts), so the fill's edge and the line drawn along it pass
 *               through the same points; and always so that two triangles sharing an edge split it
 *               identically, since a point on one side that is not a vertex on the other is a crack
 *               in the picture.
 */

import earcut, { refine } from "earcut";
import { ringPoints, world } from "./worldMap";

export interface LandMesh {
  /**
   * Three floats a vertex - longitude, latitude, and the number of the country, which is its index
   * in `world().countries` plus one, as the raster counts them - and three vertices a triangle.
   */
  vertices: Float32Array<ArrayBuffer>;
  vertexCount: number;
}

/** A closed run of lon/lat pairs, first point not repeated at the end. */
type Piece = number[];

interface Polygon {
  outer: Piece;
  holes: Piece[];
}

/**
 * The whole world, triangulated. `boundaryStep` is the longest piece an edge of a coastline or
 * border is left in, in degrees; `interiorStep` the same for the edges inside a country, which
 * nothing is drawn along and so may be longer.
 */
export function buildLand(boundaryStep: number, interiorStep: number): LandMesh {
  // built once and kept, like the world it is cut from: a map view comes and goes with its tab,
  // and the sixty milliseconds this takes are not worth paying again
  const key = boundaryStep + "/" + interiorStep;
  const kept = built.get(key);
  if (kept !== undefined) return kept;
  const w = world();
  const out = new Writer();
  for (let c = 0; c < w.countries.length; c++) {
    const pieces: Piece[] = [];
    for (const ring of w.countries[c].rings) for (const piece of piecesOf(ringPoints(w, ring))) pieces.push(piece);
    for (const polygon of polygonsOf(pieces)) triangulate(polygon, c + 1, boundaryStep, interiorStep, out);
  }
  const mesh = { vertices: out.done(), vertexCount: out.length / 3 };
  built.set(key, mesh);
  return mesh;
}

const built = new Map<string, LandMesh>();

// ---- the seam ----

/**
 * One ring of the source as the pieces of it that lie on the map: followed round, shifted so that
 * it sits within a turn of the world, and cut wherever it still reaches past ±180.
 */
function piecesOf(points: Float32Array): Piece[] {
  const count = points.length / 2 - 1; // the last point is the first again
  if (count < 3) return [];
  const lon = new Float64Array(count);
  const lat = new Float64Array(count);
  lon[0] = points[0];
  lat[0] = points[1];
  for (let i = 1; i < count; i++) {
    lon[i] = lon[i - 1] + turn(points[i * 2] - points[(i - 1) * 2]);
    lat[i] = points[i * 2 + 1];
  }
  const ring: Piece = [];
  for (let i = 0; i < count; i++) ring.push(lon[i], lat[i]);
  // followed round, does the ring come back to where it started? One that ends a whole world away
  // from its start went right round it, and is closed through the pole on the side its land is
  const end = lon[count - 1] + turn(points[0] - points[(count - 1) * 2]);
  if (Math.abs(end - lon[0]) > 180) {
    let sum = 0;
    for (let i = 0; i < count; i++) sum += lat[i];
    const pole = sum < 0 ? -90 : 90;
    ring.push(end, pole, lon[0], pole);
  }
  // within a turn of the world, and then cut at the seams it still crosses
  let west = Infinity;
  let east = -Infinity;
  for (let i = 0; i < ring.length; i += 2) {
    if (ring[i] < west) west = ring[i];
    if (ring[i] > east) east = ring[i];
  }
  const shift = Math.round((west + east) / 2 / 360) * 360;
  if (shift !== 0) for (let i = 0; i < ring.length; i += 2) ring[i] -= shift;
  let pieces: Piece[] = [ring];
  if (east - shift > 180) pieces = pieces.flatMap((p) => [clipLongitude(p, 180, true), moved(clipLongitude(p, 180, false), -360)]);
  if (west - shift < -180) pieces = pieces.flatMap((p) => [clipLongitude(p, -180, false), moved(clipLongitude(p, -180, true), 360)]);
  return pieces.filter((p) => p.length >= 6 && Math.abs(area(p)) > 1e-9);
}

/** A step in longitude, taken the short way round. */
function turn(step: number): number {
  return step > 180 ? step - 360 : step < -180 ? step + 360 : step;
}

/**
 * Sutherland-Hodgman against a meridian: the part of the piece west of it (or east, with `west`
 * false). The crossing points are worked out from the same two ends in the same order whichever
 * side is asked for, so the two halves of a cut ring meet on the seam exactly.
 */
function clipLongitude(piece: Piece, at: number, west: boolean): Piece {
  const out: Piece = [];
  const count = piece.length / 2;
  const inside = (x: number) => (west ? x <= at : x >= at);
  for (let i = 0, j = count - 1; i < count; j = i++) {
    const xi = piece[i * 2];
    const yi = piece[i * 2 + 1];
    const xj = piece[j * 2];
    const yj = piece[j * 2 + 1];
    const keepI = inside(xi);
    const keepJ = inside(xj);
    if (keepI !== keepJ) out.push(at, yj + ((at - xj) / (xi - xj)) * (yi - yj));
    if (keepI) out.push(xi, yi);
  }
  return out;
}

function moved(piece: Piece, by: number): Piece {
  for (let i = 0; i < piece.length; i += 2) piece[i] += by;
  return piece;
}

/** Twice the signed area, shoelace; the sign says which way round the ring runs. */
function area(piece: Piece): number {
  let sum = 0;
  const count = piece.length / 2;
  for (let i = 0, j = count - 1; i < count; j = i++) sum += piece[j * 2] * piece[i * 2 + 1] - piece[i * 2] * piece[j * 2 + 1];
  return sum;
}

// ---- the holes ----

/**
 * The pieces of one country sorted into polygons: a piece inside an odd number of the others is a
 * hole in the innermost of them, and every other piece is land with its own holes.
 */
function polygonsOf(pieces: Piece[]): Polygon[] {
  const boxes = pieces.map(bounds);
  const within: number[][] = pieces.map(() => []);
  for (let i = 0; i < pieces.length; i++) {
    for (let j = 0; j < pieces.length; j++) {
      if (i === j || !boxes[j].holds(boxes[i]) || !contains(pieces[j], pieces[i])) continue;
      within[i].push(j);
    }
  }
  const polygons: Polygon[] = [];
  const outerOf = new Map<number, Polygon>();
  for (let i = 0; i < pieces.length; i++) if (within[i].length % 2 === 0) outerOf.set(i, { outer: pieces[i], holes: [] });
  for (let i = 0; i < pieces.length; i++) {
    if (within[i].length % 2 === 0) continue;
    // the innermost of the pieces round it is the one that sits inside the most of the others
    let parent = within[i][0];
    for (const j of within[i]) if (within[j].length > within[parent].length) parent = j;
    outerOf.get(parent)?.holes.push(pieces[i]);
  }
  for (const polygon of outerOf.values()) polygons.push(polygon);
  return polygons;
}

interface Box {
  west: number;
  south: number;
  east: number;
  north: number;
  holds(other: Box): boolean;
}

function bounds(piece: Piece): Box {
  let west = Infinity;
  let south = Infinity;
  let east = -Infinity;
  let north = -Infinity;
  for (let i = 0; i < piece.length; i += 2) {
    if (piece[i] < west) west = piece[i];
    if (piece[i] > east) east = piece[i];
    if (piece[i + 1] < south) south = piece[i + 1];
    if (piece[i + 1] > north) north = piece[i + 1];
  }
  return {
    west,
    south,
    east,
    north,
    holds: (o) => o.west >= west - 1e-6 && o.east <= east + 1e-6 && o.south >= south - 1e-6 && o.north <= north + 1e-6,
  };
}

/**
 * Whether the inner piece lies inside the outer one, by a vote of a handful of its vertices: an
 * enclave may touch the border it sits inside, and a vertex on the line could go either way.
 */
function contains(outer: Piece, inner: Piece): boolean {
  const count = inner.length / 2;
  const samples = Math.min(7, count);
  let inside = 0;
  for (let s = 0; s < samples; s++) {
    const i = Math.floor((s * count) / samples);
    if (pointInside(inner[i * 2], inner[i * 2 + 1], outer)) inside++;
  }
  return inside * 2 > samples;
}

/** Even-odd crossing test. */
function pointInside(x: number, y: number, piece: Piece): boolean {
  let inside = false;
  const count = piece.length / 2;
  for (let i = 0, j = count - 1; i < count; j = i++) {
    const xi = piece[i * 2];
    const yi = piece[i * 2 + 1];
    const xj = piece[j * 2];
    const yj = piece[j * 2 + 1];
    if (yi > y !== yj > y && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

// ---- the triangles ----

function triangulate(polygon: Polygon, country: number, boundaryStep: number, interiorStep: number, out: Writer) {
  // one flat list of coordinates, the outer ring first and each hole after it, as earcut takes them;
  // and which ring each vertex is on, so an edge between neighbours on a ring is known for a boundary
  const coords: number[] = [];
  const holeStarts: number[] = [];
  const ringOf: number[] = [];
  const ringStart: number[] = [];
  const ringEnd: number[] = [];
  const add = (piece: Piece, ring: number) => {
    ringStart.push(coords.length / 2);
    for (let i = 0; i < piece.length; i += 2) {
      coords.push(piece[i], piece[i + 1]);
      ringOf.push(ring);
    }
    ringEnd.push(coords.length / 2);
  };
  add(polygon.outer, 0);
  for (let h = 0; h < polygon.holes.length; h++) {
    holeStarts.push(coords.length / 2);
    add(polygon.holes[h], h + 1);
  }
  const triangles = earcut(coords, holeStarts.length > 0 ? holeStarts : null, 2);
  // towards Delaunay: ear clipping leaves long thin slivers, and a sliver's long edge is what the
  // splitting below has to pay for
  refine(triangles, coords, 2);

  const isBoundary = (a: number, b: number) => {
    if (ringOf[a] !== ringOf[b]) return false;
    const ring = ringOf[a];
    const first = ringStart[ring];
    const last = ringEnd[ring] - 1;
    return Math.abs(a - b) === 1 || (a === first && b === last) || (a === last && b === first);
  };
  for (let t = 0; t < triangles.length; t += 3) {
    const a = triangles[t];
    const b = triangles[t + 1];
    const c = triangles[t + 2];
    const ax = coords[a * 2];
    const ay = coords[a * 2 + 1];
    const bx = coords[b * 2];
    const by = coords[b * 2 + 1];
    const cx = coords[c * 2];
    const cy = coords[c * 2 + 1];
    const nAB = splitCount(ax, ay, bx, by, isBoundary(a, b) ? boundaryStep : interiorStep);
    const nBC = splitCount(bx, by, cx, cy, isBoundary(b, c) ? boundaryStep : interiorStep);
    const nCA = splitCount(cx, cy, ax, ay, isBoundary(c, a) ? boundaryStep : interiorStep);
    const n = Math.max(nAB, nBC, nCA);
    if (n === 1) out.triangle(ax, ay, bx, by, cx, cy, country);
    else subdivide(ax, ay, bx, by, cx, cy, nAB, nBC, nCA, n, country, out);
  }
}

/** How many pieces an edge is drawn in - the rule addRun in mapField.ts uses for the lines. */
function splitCount(ax: number, ay: number, bx: number, by: number, step: number): number {
  return Math.min(64, Math.max(1, Math.ceil(Math.max(Math.abs(bx - ax), Math.abs(by - ay)) / step)));
}

/**
 * The m-th of k points along an edge, the ends included, worked out from the two ends in a FIXED
 * order whichever way the edge was given - so the triangle on the other side of it, which walks it
 * the other way, gets the very same numbers.
 */
function edgePoint(px: number, py: number, qx: number, qy: number, m: number, k: number, out: Float64Array) {
  if (qx < px || (qx === px && qy < py)) {
    const tx = px;
    const ty = py;
    px = qx;
    py = qy;
    qx = tx;
    qy = ty;
    m = k - m;
  }
  if (m === 0) {
    out[0] = px;
    out[1] = py;
  } else if (m === k) {
    out[0] = qx;
    out[1] = qy;
  } else {
    const t = m / k;
    out[0] = px + (qx - px) * t;
    out[1] = py + (qy - py) * t;
  }
}

const scratch = new Float64Array(2);

/**
 * One triangle as n² smaller ones on a regular grid, with the points along each edge snapped to
 * that edge's own division. An edge divided more finely than the triangle's coarsest is left with
 * a few flat triangles along it, which draw nothing and cost nothing; what matters is that the
 * neighbour across the edge, dividing it by the same rule, lands on the same points.
 */
function subdivide(ax: number, ay: number, bx: number, by: number, cx: number, cy: number, nAB: number, nBC: number, nCA: number, n: number, country: number, out: Writer) {
  // the vertices (i, j): i steps towards B, j towards C, i + j <= n; row j starts at rowAt[j]
  const total = ((n + 1) * (n + 2)) / 2;
  const xs = new Float64Array(total);
  const ys = new Float64Array(total);
  const rowAt = new Int32Array(n + 2);
  for (let j = 0; j <= n; j++) rowAt[j + 1] = rowAt[j] + (n + 1 - j);
  for (let j = 0; j <= n; j++) {
    for (let i = 0; i + j <= n; i++) {
      const k = rowAt[j] + i;
      if (j === 0) edgePoint(ax, ay, bx, by, Math.round((i * nAB) / n), nAB, scratch);
      else if (i === 0) edgePoint(ax, ay, cx, cy, Math.round((j * nCA) / n), nCA, scratch);
      else if (i + j === n) edgePoint(bx, by, cx, cy, Math.round((j * nBC) / n), nBC, scratch);
      else {
        scratch[0] = ax + ((bx - ax) * i) / n + ((cx - ax) * j) / n;
        scratch[1] = ay + ((by - ay) * i) / n + ((cy - ay) * j) / n;
      }
      xs[k] = scratch[0];
      ys[k] = scratch[1];
    }
  }
  for (let j = 0; j < n; j++) {
    for (let i = 0; i + j < n; i++) {
      const p = rowAt[j] + i;
      const q = rowAt[j + 1] + i;
      out.triangleOf(xs, ys, p, p + 1, q, country);
      if (i + j + 1 < n) out.triangleOf(xs, ys, p + 1, q + 1, q, country);
    }
  }
}

/** A Float32Array that grows: the output, three floats a vertex. */
class Writer {
  private data = new Float32Array(1 << 20);
  length = 0;

  triangle(ax: number, ay: number, bx: number, by: number, cx: number, cy: number, country: number) {
    if ((ax === bx && ay === by) || (bx === cx && by === cy) || (ax === cx && ay === cy)) return;
    if (this.length + 9 > this.data.length) {
      const bigger = new Float32Array(this.data.length * 2);
      bigger.set(this.data);
      this.data = bigger;
    }
    const d = this.data;
    let at = this.length;
    d[at++] = ax;
    d[at++] = ay;
    d[at++] = country;
    d[at++] = bx;
    d[at++] = by;
    d[at++] = country;
    d[at++] = cx;
    d[at++] = cy;
    d[at++] = country;
    this.length = at;
  }

  triangleOf(xs: Float64Array, ys: Float64Array, a: number, b: number, c: number, country: number) {
    this.triangle(xs[a], ys[a], xs[b], ys[b], xs[c], ys[c], country);
  }

  done(): Float32Array<ArrayBuffer> {
    return this.data.subarray(0, this.length);
  }
}
