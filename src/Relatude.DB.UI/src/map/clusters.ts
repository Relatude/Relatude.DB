/**
 * The nodes gathered into patches, and the bubbles that stand for them.
 *
 * Everything else on the map is drawn by the graphics card (mapField.ts). This is not, and that is
 * deliberate: there are a few hundred bubbles at most however many nodes there are, and each one
 * carries a count written in words - which a canvas does in one call and a shader does not do at
 * all without an alphabet baked into a texture. So the bubbles go on a small 2d canvas over the
 * top, placed by the same projection the shader uses.
 */

/**
 * Where the i-th node is on the canvas, in css pixels, written into `out`. False when it is not
 * drawn at all: behind the globe, or off the map.
 */
export type Place = (i: number, out: Float32Array) => boolean;

/**
 * The nodes gathered into patches of the WORLD - so many degrees of longitude and latitude a side -
 * rather than of the canvas: one rod per patch, standing where its nodes are and as tall as there
 * are many of them. Gathered on the world because a rod is a thing standing on the ground: it
 * belongs to a place and has to stay the same height when the globe is turned or closed in on,
 * which a patch of the canvas would not.
 *
 * What comes back is what the graphics card wants - longitude, latitude and a height from 0 to 1
 * per rod - with the heights by SQUARE ROOT of the count, so a village is not invisible beside a
 * capital. `counts` carries the same rods' real counts, for the tooltip.
 */
export function rodsOf(lat: Float32Array, lon: Float32Array, count: number, cellDegrees: number): { rods: Float32Array; counts: Int32Array; total: number; max: number } {
  const cell = Math.max(0.25, cellDegrees);
  const cols = Math.ceil(360 / cell) + 1;
  const patches = new Map<number, { lat: number; lon: number; n: number }>();
  for (let i = 0; i < count; i++) {
    const key = Math.floor((90 - lat[i]) / cell) * cols + Math.floor((lon[i] + 180) / cell);
    let patch = patches.get(key);
    if (patch === undefined) {
      patch = { lat: 0, lon: 0, n: 0 };
      patches.set(key, patch);
    }
    patch.lat += lat[i];
    patch.lon += lon[i];
    patch.n++;
  }
  const rods = new Float32Array(patches.size * 3);
  const counts = new Int32Array(patches.size);
  let max = 1;
  for (const patch of patches.values()) if (patch.n > max) max = patch.n;
  let at = 0;
  for (const patch of patches.values()) {
    rods[at * 3] = patch.lon / patch.n;
    rods[at * 3 + 1] = patch.lat / patch.n;
    rods[at * 3 + 2] = Math.sqrt(patch.n / max);
    counts[at] = patch.n;
    at++;
  }
  return { rods, counts, total: patches.size, max };
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

/**
 * How solid a bubble is: quiet for a patch holding almost nothing, solid for a crowd. Its own scale
 * rather than the ramp's, which fades to nothing at the low end because a heat field must - a
 * bubble that has faded to nothing is a bubble nobody can count.
 */
const bubbleAlpha = (share: number) => 0.3 + 0.6 * share;

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
    const share = Math.sqrt(cluster.count / max);
    const at = Math.min(steps, Math.round(share * steps)) * 4;
    ctx.fillStyle = `rgba(${ramp[at]}, ${ramp[at + 1]}, ${ramp[at + 2]}, ${bubbleAlpha(share).toFixed(3)})`;
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
