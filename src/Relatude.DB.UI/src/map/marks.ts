/**
 * Drawing the nodes on a map: a dot each, a pin each, a picture of how thickly they lie, or one
 * bubble per patch of it with a count in it.
 *
 * Nothing here knows whether it is drawing on the flat map or on the globe. It is handed a `Place`
 * - a function that says where the i-th node is on the canvas, and whether it is on it at all -
 * and everything else follows from that, so the two renderers cannot end up with two heat maps
 * that mean different things.
 *
 * On the flat map this all goes on a canvas laid over the svg the world is drawn in: the world is
 * six hundred lines that never change and belongs in the dom, and the nodes can be a million and
 * very much do not. Dots and heat are written straight into an ImageData rather than through the 2d
 * context - at a million points a fillRect each is a tenth of a second of the browser's own
 * bookkeeping, where blending pixels by hand is a pass over an array - while pins and bubbles go
 * through the context, since there are never many and they have a shape worth having.
 */

/**
 * Where the i-th node is on the canvas, in css pixels, written into `out`. False when it is not
 * drawn at all: behind the globe, or off the map.
 */
export type Place = (i: number, out: Float32Array) => boolean;

/** What colours the points: rgb bytes per group, and the group of each point (null = all one colour). */
export interface PointColors {
  palette: Uint8Array;
  assignment: Uint16Array | null;
}

/** A patch of the map with nodes in it. */
export interface Cluster {
  /** where the bubble goes, in css pixels: the middle of the points in it, not the middle of the cell */
  x: number;
  y: number;
  count: number;
  /** and the same place as a place, for the tooltip and for zooming to it */
  lat: number;
  lon: number;
}

/**
 * A canvas cleared and handed back as pixels to write into. The buffer is kept between frames -
 * allocating twenty megabytes sixty times a second is what makes a map stutter - and only replaced
 * when the canvas changes size.
 */
export class MarkSurface {
  private image: ImageData | null = null;
  private pixels: Uint32Array | null = null;
  width = 0;
  height = 0;
  /** Empties the surface and sizes it to the canvas, in device pixels. */
  begin(width: number, height: number): void {
    const w = Math.max(1, Math.round(width));
    const h = Math.max(1, Math.round(height));
    if (this.image === null || this.width !== w || this.height !== h) {
      this.width = w;
      this.height = h;
      this.image = new ImageData(w, h);
      this.pixels = new Uint32Array(this.image.data.buffer);
    } else {
      this.pixels!.fill(0);
    }
  }
  get words(): Uint32Array {
    if (this.pixels === null) throw new Error("the surface has not been sized yet");
    return this.pixels;
  }
  /** Puts the pixels on the canvas. ImageData ignores the context's transform, so this is always 1:1 with the device. */
  put(ctx: CanvasRenderingContext2D): void {
    if (this.image !== null) ctx.putImageData(this.image, 0, 0);
  }
}

/** The pixels of a disc of the given diameter, as offsets from its middle. */
function disc(diameter: number): Int32Array {
  const r = diameter / 2;
  const reach = Math.ceil(r);
  const offsets: number[] = [];
  for (let dy = -reach; dy <= reach; dy++) {
    for (let dx = -reach; dx <= reach; dx++) {
      if (dx * dx + dy * dy <= r * r) offsets.push(dx, dy);
    }
  }
  return new Int32Array(offsets.length > 0 ? offsets : [0, 0]);
}

const discCache = new Map<number, Int32Array>();
function cachedDisc(diameter: number): Int32Array {
  const key = Math.round(diameter * 2) / 2;
  let d = discCache.get(key);
  if (d === undefined) {
    d = disc(key);
    discCache.set(key, d);
  }
  return d;
}

/**
 * A dot per node. `step` draws every n-th point instead of all of them, which is what a map does
 * while a hand is still moving it; `alpha` is what makes a crowd read as a crowd - a hundred dots
 * on one another are darker than one. Returns how many were actually drawn.
 */
export function drawDots(surface: MarkSurface, count: number, step: number, place: Place, colors: PointColors, size: number, alpha: number, dpr: number): number {
  const words = surface.words;
  const width = surface.width;
  const height = surface.height;
  const offsets = cachedDisc(Math.max(1, size * dpr));
  const a = Math.max(1, Math.min(255, Math.round(alpha * 255)));
  const { palette, assignment } = colors;
  const out = new Float32Array(2);
  let drawn = 0;
  for (let i = 0; i < count; i += step) {
    if (!place(i, out)) continue;
    const cx = (out[0] * dpr) | 0;
    const cy = (out[1] * dpr) | 0;
    if (cx < -8 || cy < -8 || cx > width + 8 || cy > height + 8) continue;
    const at = (assignment === null ? 0 : assignment[i]) * 3;
    const r = palette[at];
    const g = palette[at + 1];
    const b = palette[at + 2];
    for (let o = 0; o < offsets.length; o += 2) {
      const x = cx + offsets[o];
      const y = cy + offsets[o + 1];
      if (x < 0 || y < 0 || x >= width || y >= height) continue;
      blend(words, y * width + x, r, g, b, a);
    }
    drawn++;
  }
  return drawn;
}

/**
 * Source-over, by hand, on a little-endian rgba word. What is already there has its alpha grown
 * towards opaque rather than replaced, so a hundred half-transparent dots on the same pixel end up
 * solid - which is the whole point of drawing a crowd with alpha.
 */
function blend(words: Uint32Array, at: number, r: number, g: number, b: number, a: number): void {
  const under = words[at];
  const ua = (under >>> 24) & 0xff;
  if (ua === 0) {
    words[at] = (a << 24) | (b << 16) | (g << 8) | r;
    return;
  }
  const ur = under & 0xff;
  const ug = (under >>> 8) & 0xff;
  const ub = (under >>> 16) & 0xff;
  const rest = 255 - a;
  const na = a + (((ua * rest) / 255) | 0);
  const nr = ((r * a + ur * rest) / 255) | 0;
  const ng = ((g * a + ug * rest) / 255) | 0;
  const nb = ((b * a + ub * rest) / 255) | 0;
  words[at] = (na << 24) | (nb << 16) | (ng << 8) | nr;
}

/**
 * A pin per node: the marker a map is expected to have, drawn as a teardrop standing on the place
 * it marks. Through the 2d context, since a pin has a shape and an outline and there are never
 * enough of them for that to cost anything - past `budget` the map draws the ones it has room for
 * and says how many that was.
 */
export function drawPins(
  ctx: CanvasRenderingContext2D,
  count: number,
  place: Place,
  width: number,
  height: number,
  colors: PointColors,
  size: number,
  outline: string,
  budget: number,
): number {
  const { palette, assignment } = colors;
  const head = size / 2;
  const out = new Float32Array(2);
  ctx.save();
  ctx.lineWidth = 1;
  ctx.strokeStyle = outline;
  let drawn = 0;
  for (let i = 0; i < count && drawn < budget; i++) {
    if (!place(i, out)) continue;
    const px = out[0];
    const py = out[1];
    if (px < -size || py < -size * 2 || px > width + size || py > height + size) continue;
    const at = (assignment === null ? 0 : assignment[i]) * 3;
    ctx.fillStyle = `rgb(${palette[at]} ${palette[at + 1]} ${palette[at + 2]})`;
    ctx.beginPath();
    // a round head, and two sides narrowing to the tip - which is where the node actually is
    ctx.arc(px, py - size, head, Math.PI * 0.82, Math.PI * 0.18);
    ctx.lineTo(px, py);
    ctx.closePath();
    ctx.fill();
    ctx.stroke();
    drawn++;
  }
  ctx.restore();
  return drawn;
}

/**
 * How thickly the nodes lie, as a field of colour.
 *
 * They are counted into a coarse grid - one cell every few pixels - which is then blurred three
 * times with a box, since three boxes are a gaussian to the eye and each is two additions a cell
 * whatever the radius. What comes out is scaled by its own largest value through a square root, so
 * a map with one enormous city on it still shows the villages, and read through a colour ramp whose
 * low end is transparent, since the map underneath has to stay readable.
 */
export function drawHeat(surface: MarkSurface, count: number, step: number, place: Place, radius: number, ramp: Uint8Array, dpr: number): number {
  const width = surface.width;
  const height = surface.height;
  const cell = Math.max(2, Math.round((radius * dpr) / 4));
  const cols = Math.ceil(width / cell) + 2;
  const rows = Math.ceil(height / cell) + 2;
  const density = new Float32Array(cols * rows);
  const out = new Float32Array(2);
  let drawn = 0;
  for (let i = 0; i < count; i += step) {
    if (!place(i, out)) continue;
    const col = Math.floor((out[0] * dpr) / cell) + 1;
    const row = Math.floor((out[1] * dpr) / cell) + 1;
    if (col < 0 || row < 0 || col >= cols || row >= rows) continue;
    density[row * cols + col] += step; // a sampled point stands for the ones it was drawn instead of
    drawn++;
  }
  const spread = Math.max(1, Math.round(radius * dpr) / cell / 2);
  const scratch = new Float32Array(density.length);
  const r = Math.max(1, Math.round(spread));
  for (let pass = 0; pass < 3; pass++) {
    blurRows(density, scratch, cols, rows, r);
    blurCols(scratch, density, cols, rows, r);
  }
  let max = 0;
  for (const d of density) if (d > max) max = d;
  if (max <= 0) return drawn;
  const steps = ramp.length / 4 - 1;
  const words = surface.words;
  for (let y = 0; y < height; y++) {
    // bilinear between the cells, so a grid a few pixels across does not read as squares
    const gy = y / cell + 0.5;
    const row0 = Math.min(rows - 1, Math.max(0, Math.floor(gy)));
    const row1 = Math.min(rows - 1, row0 + 1);
    const fy = gy - row0;
    for (let x = 0; x < width; x++) {
      const gx = x / cell + 0.5;
      const col0 = Math.min(cols - 1, Math.max(0, Math.floor(gx)));
      const col1 = Math.min(cols - 1, col0 + 1);
      const fx = gx - col0;
      const top = density[row0 * cols + col0] * (1 - fx) + density[row0 * cols + col1] * fx;
      const bottom = density[row1 * cols + col0] * (1 - fx) + density[row1 * cols + col1] * fx;
      const value = top * (1 - fy) + bottom * fy;
      if (value <= 0) continue;
      const at = Math.min(steps, Math.max(0, Math.round(Math.sqrt(value / max) * steps))) * 4;
      const a = ramp[at + 3];
      if (a === 0) continue;
      blend(words, y * width + x, ramp[at], ramp[at + 1], ramp[at + 2], a);
    }
  }
  return drawn;
}

/** One box pass across the rows, and one down the columns: a running sum, so the radius is free. */
function blurRows(src: Float32Array, dst: Float32Array, cols: number, rows: number, r: number) {
  const span = r * 2 + 1;
  for (let y = 0; y < rows; y++) {
    const at = y * cols;
    let sum = 0;
    for (let x = 0; x <= r && x < cols; x++) sum += src[at + x];
    for (let x = 0; x < cols; x++) {
      dst[at + x] = sum / span;
      const add = x + r + 1;
      const drop = x - r;
      if (add < cols) sum += src[at + add];
      if (drop >= 0) sum -= src[at + drop];
    }
  }
}

function blurCols(src: Float32Array, dst: Float32Array, cols: number, rows: number, r: number) {
  const span = r * 2 + 1;
  for (let x = 0; x < cols; x++) {
    let sum = 0;
    for (let y = 0; y <= r && y < rows; y++) sum += src[y * cols + x];
    for (let y = 0; y < rows; y++) {
      dst[y * cols + x] = sum / span;
      const add = y + r + 1;
      const drop = y - r;
      if (add < rows) sum += src[add * cols + x];
      if (drop >= 0) sum -= src[drop * cols + x];
    }
  }
}

/**
 * The nodes gathered into patches: a square grid laid over the CANVAS, one bubble per square that
 * has anything in it, standing at the middle of what it holds rather than at the middle of the
 * square - so the bubble of a coastal city sits on the city and not in the sea beside it.
 *
 * Gathering on the canvas rather than on the world is what keeps this bounded and what makes a
 * cluster mean the same thing on the flat map and on the globe: however far in the map is zoomed,
 * there are only ever as many squares as fit on a screen.
 */
export function clusterPoints(count: number, step: number, place: Place, lat: Float32Array, lon: Float32Array, width: number, height: number, cellPixels: number): Cluster[] {
  const cell = Math.max(8, cellPixels);
  const byCell = new Map<number, { x: number; y: number; lat: number; lon: number; count: number }>();
  const out = new Float32Array(2);
  const cols = Math.ceil(width / cell) + 3;
  for (let i = 0; i < count; i += step) {
    if (!place(i, out)) continue;
    const x = out[0];
    const y = out[1];
    if (x < -cell || y < -cell || x > width + cell || y > height + cell) continue;
    const key = (Math.floor(y / cell) + 1) * cols + Math.floor(x / cell) + 1;
    let bucket = byCell.get(key);
    if (bucket === undefined) {
      bucket = { x: 0, y: 0, lat: 0, lon: 0, count: 0 };
      byCell.set(key, bucket);
    }
    bucket.x += x;
    bucket.y += y;
    bucket.lat += lat[i];
    bucket.lon += lon[i];
    bucket.count++;
  }
  const clusters: Cluster[] = [];
  for (const b of byCell.values()) {
    clusters.push({ x: b.x / b.count, y: b.y / b.count, lat: b.lat / b.count, lon: b.lon / b.count, count: b.count * step });
  }
  // largest last, so the big ones are drawn over the small ones they overlap
  clusters.sort((a, b) => a.count - b.count);
  return clusters;
}

/** How large a bubble holding `count` is: by area, so two bubbles hold the number they look like. */
export function clusterRadius(count: number, max: number, cellPixels: number): number {
  const smallest = Math.max(5, cellPixels * 0.17);
  const largest = Math.max(smallest + 2, cellPixels * 0.62);
  return smallest + (largest - smallest) * (max <= 1 ? 1 : Math.sqrt(count / max));
}

/** The bubbles, with the count written in the ones large enough to hold it. */
export function drawClusters(ctx: CanvasRenderingContext2D, clusters: Cluster[], cellPixels: number, ramp: Uint8Array, ink: string, label: (count: number) => string): void {
  let max = 1;
  for (const c of clusters) if (c.count > max) max = c.count;
  const steps = ramp.length / 4 - 1;
  ctx.save();
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  for (const cluster of clusters) {
    const r = clusterRadius(cluster.count, max, cellPixels);
    // the colour says the same thing the size does, which is what makes a small dense patch stand
    // out from a small sparse one at a glance
    const at = Math.min(steps, Math.round(Math.sqrt(cluster.count / max) * steps)) * 4;
    ctx.fillStyle = `rgba(${ramp[at]}, ${ramp[at + 1]}, ${ramp[at + 2]}, ${(ramp[at + 3] / 255).toFixed(3)})`;
    ctx.beginPath();
    ctx.arc(cluster.x, cluster.y, r, 0, Math.PI * 2);
    ctx.fill();
    if (r >= 11) {
      const text = label(cluster.count);
      ctx.font = `${Math.min(13, Math.max(9, Math.round(r * 0.7)))}px system-ui, sans-serif`;
      if (ctx.measureText(text).width < r * 1.8) {
        ctx.fillStyle = ink;
        ctx.fillText(text, cluster.x, cluster.y);
      }
    }
  }
  ctx.restore();
}
