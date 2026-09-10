/**
 * Which country a place is in, and how many of the map's points are in each.
 *
 * The answer comes out of a raster of the world - one byte a cell, the country that covers it -
 * because the alternative is a point-in-polygon test per point against a set of shapes holding
 * eight thousand vertices, and a map can hold a million points. The raster is drawn once, lazily,
 * the first time anything asks; from then on a country is an array lookup, so counting a million
 * points is a single pass over them.
 *
 * It is drawn here rather than on a canvas because a canvas antialiases, and a blended edge pixel
 * between country 20 and country 60 reads back as country 40 - some third country on the other side
 * of the world. A scanline fill has no such thing: a cell is inside a shape or it is not.
 *
 * Every edge is first sorted into the rows it crosses, and each row then looks only at the edges
 * that reach it. Without that, filling Russia would test its thirty thousand edges against each of
 * its three hundred rows, and the world would take a second to draw; with it, the whole thing is
 * one pass over the edges and one over the rows.
 *
 * The raster is also what the map SHOWS when the countries are shaded: it goes to the graphics card
 * as one byte a cell - a country's number, not its colour - and the colours are a palette of 256
 * the shader looks the number up in. So the picture is as fine as the raster is, recolouring it
 * costs a kilobyte rather than a repaint, and no two countries are ever blended into a third by a
 * filter.
 */

import { ringPoints, world, type WorldCountry } from "./worldMap";

/**
 * The raster's size. A cell is 0.088° of longitude - about 10 km at the equator, and less than that
 * anywhere else - which is what it takes for a shaded country to keep up with the 50m outline drawn
 * over it. One byte a cell, so the whole world is eight megabytes.
 */
const rasterWidth = 4096;
const rasterHeight = 2048;

/** 0 is sea; anything else is the index of a country in `world().countries`, plus one. */
let raster: Uint8Array | null = null;

export function countryRasterSize(): [number, number] {
  return [rasterWidth, rasterHeight];
}

/** The raster, drawn on the first call and kept. */
export function countryRaster(): Uint8Array {
  if (raster !== null) return raster;
  const w = world();
  const cells = new Uint8Array(rasterWidth * rasterHeight);
  const crossings: number[] = [];
  for (let c = 0; c < w.countries.length; c++) {
    const country = w.countries[c];
    const [, south, , north] = country.bounds;
    const firstRow = Math.max(0, Math.floor(rowOf(north)));
    const lastRow = Math.min(rasterHeight - 1, Math.ceil(rowOf(south)));
    if (lastRow < firstRow) continue;
    // Every edge of every ring - holes and islands alike, since an even-odd fill wants no more
    // than the edges - sorted into the rows it reaches. Four numbers an edge, and an index per
    // row saying where that row's edges start in it.
    const rows = lastRow - firstRow + 1;
    const perRow = new Int32Array(rows + 1);
    const edges: number[] = [];
    const addEdge = (x0: number, y0: number, x1: number, y1: number) => {
      if (y0 === y1) return; // a horizontal edge crosses no scanline
      edges.push(x0, y0, x1, y1);
      const from = Math.max(firstRow, Math.ceil(rowOf(Math.max(y0, y1)) - 0.5));
      const to = Math.min(lastRow, Math.floor(rowOf(Math.min(y0, y1)) - 0.5));
      for (let row = from; row <= to; row++) perRow[row - firstRow + 1]++;
    };
    for (const ring of country.rings) {
      const points = ringPoints(w, ring);
      const count = points.length / 2;
      if (count < 2) continue;
      /**
       * The ring's longitudes FOLLOWED ROUND rather than taken as written. A ring that spans the
       * antimeridian is stored as ...179.9, -180.0..., and read literally that is a step right
       * across the world - an edge lying over the whole Arctic, which is what used to shade the
       * ocean between Russia and Canada. Followed round, each step is the small one it really is
       * and the ring simply runs on past 180; the fill below wraps its columns back into the
       * raster, which is the same thing longitude does.
       *
       * The last point of a ring is its first, so the run below is every edge it has.
       */
      const lons = new Float64Array(count);
      lons[0] = points[0];
      for (let i = 1; i < count; i++) {
        let step = points[i * 2] - points[(i - 1) * 2];
        if (step > 180) step -= 360;
        else if (step < -180) step += 360;
        lons[i] = lons[i - 1] + step;
      }
      for (let i = 1; i < count; i++) addEdge(lons[i - 1], points[(i - 1) * 2 + 1], lons[i], points[i * 2 + 1]);
    }
    for (let i = 0; i < rows; i++) perRow[i + 1] += perRow[i];
    const byRow = new Int32Array(perRow[rows]);
    const at = perRow.slice(0, rows);
    for (let e = 0; e < edges.length; e += 4) {
      const from = Math.max(firstRow, Math.ceil(rowOf(Math.max(edges[e + 1], edges[e + 3])) - 0.5));
      const to = Math.min(lastRow, Math.floor(rowOf(Math.min(edges[e + 1], edges[e + 3])) - 0.5));
      for (let row = from; row <= to; row++) byRow[at[row - firstRow]++] = e;
    }
    for (let row = firstRow; row <= lastRow; row++) {
      const lat = latOf(row);
      crossings.length = 0;
      for (let i = perRow[row - firstRow]; i < perRow[row - firstRow + 1]; i++) {
        const e = byRow[i];
        const y0 = edges[e + 1];
        const y1 = edges[e + 3];
        // the half-open rule: an edge counts at its lower end and not at its upper one, so a
        // vertex exactly on the scanline is crossed once rather than twice or not at all
        if (y0 <= lat !== y1 <= lat) crossings.push(edges[e] + ((lat - y0) / (y1 - y0)) * (edges[e + 2] - edges[e]));
      }
      if (crossings.length < 2) continue;
      crossings.sort((a, b) => a - b);
      const line = row * rasterWidth;
      for (let i = 0; i + 1 < crossings.length; i += 2) {
        const from = Math.ceil(colOf(crossings[i]) - 0.5);
        // a span may run off either edge of the raster, or right round it, since the longitudes it
        // came from were followed round; it wraps, and never covers the world more than once
        const to = Math.min(Math.floor(colOf(crossings[i + 1]) - 0.5), from + rasterWidth - 1);
        for (let col = from; col <= to; col++) cells[line + (((col % rasterWidth) + rasterWidth) % rasterWidth)] = c + 1;
      }
    }
  }
  raster = cells;
  return cells;
}

/** The middle of a raster row, in degrees, and back. */
const latOf = (row: number) => 90 - ((row + 0.5) * 180) / rasterHeight;
const rowOf = (lat: number) => ((90 - lat) * rasterHeight) / 180;
const colOf = (lon: number) => ((lon + 180) * rasterWidth) / 360;

/**
 * The country a place is in, as an index into `world().countries`; -1 at sea.
 *
 * A cell that is sea takes the answer of whichever of its eight neighbours has one. The raster is
 * no finer than ten kilometres, so a place within one cell of a coast is not really at sea - it is
 * on a coast drawn to the nearest cell - and a good many harbour towns sit there. Anything further
 * out than that is water and says so.
 */
export function countryAt(lon: number, lat: number): number {
  const cells = countryRaster();
  const col = Math.floor(colOf(lon));
  const row = Math.floor(rowOf(lat));
  if (col < 0 || col >= rasterWidth || row < 0 || row >= rasterHeight) return -1;
  const country = cells[row * rasterWidth + col];
  return country !== 0 ? country - 1 : ashore(cells, col, row) - 1;
}

/** The country of a sea cell's nearest neighbour, or 0 for one that has none. */
function ashore(cells: Uint8Array, col: number, row: number): number {
  for (let dy = -1; dy <= 1; dy++) {
    const y = row + dy;
    if (y < 0 || y >= rasterHeight) continue;
    for (let dx = -1; dx <= 1; dx++) {
      const x = ((col + dx) % rasterWidth + rasterWidth) % rasterWidth; // longitude wraps
      const country = cells[y * rasterWidth + x];
      if (country !== 0) return country;
    }
  }
  return 0;
}

/**
 * How many of the points are in each country: one entry per country of the world, in its order,
 * and - last - how many were at sea, which here means "in the water, or within ten kilometres of a
 * shore" (see countryAt).
 */
export interface CountryCounts {
  counts: Int32Array;
  atSea: number;
  max: number;
  total: number;
}

export function countCountries(lat: Float32Array, lon: Float32Array, count: number): CountryCounts {
  const cells = countryRaster();
  const countries = world().countries.length;
  const counts = new Int32Array(countries);
  let atSea = 0;
  for (let i = 0; i < count; i++) {
    const col = Math.floor(colOf(lon[i]));
    const row = Math.floor(rowOf(lat[i]));
    if (col < 0 || col >= rasterWidth || row < 0 || row >= rasterHeight) continue;
    let country = cells[row * rasterWidth + col];
    if (country === 0) country = ashore(cells, col, row); // see countryAt
    if (country === 0) atSea++;
    else counts[country - 1]++;
  }
  let max = 0;
  let total = 0;
  for (const n of counts) {
    if (n > max) max = n;
    total += n;
  }
  return { counts, atSea, max, total };
}

/** The countries that hold anything, largest first: what a legend lists. */
export function rankedCountries(counts: CountryCounts): { country: WorldCountry; index: number; count: number }[] {
  const list = world().countries;
  const ranked: { country: WorldCountry; index: number; count: number }[] = [];
  for (let i = 0; i < list.length; i++) if (counts.counts[i] > 0) ranked.push({ country: list[i], index: i, count: counts.counts[i] });
  ranked.sort((a, b) => b.count - a.count);
  return ranked;
}
