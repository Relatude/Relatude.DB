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
 * of the world. A scanline fill has no such thing: a cell is inside a shape or it is not. It costs
 * one pass over each country's own rows, which for the whole world is a few million trivial tests.
 */

import { ringPoints, world, type WorldCountry } from "./worldMap";

/**
 * The raster's size. A cell is 0.176° of longitude - about 20 km at the equator - which is finer
 * than the outlines it is drawn from (110m Natural Earth) and so as exact as the source allows.
 */
const rasterWidth = 2048;
const rasterHeight = 1024;

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
    // every edge of every ring of the country, as flat coordinate runs; holes and islands alike,
    // since an even-odd fill wants no more than the edges
    const rings = country.rings.map((ring) => ringPoints(w, ring));
    const [, south, , north] = country.bounds;
    const firstRow = Math.max(0, Math.floor(rowOf(north)));
    const lastRow = Math.min(rasterHeight - 1, Math.ceil(rowOf(south)));
    for (let row = firstRow; row <= lastRow; row++) {
      const lat = latOf(row);
      crossings.length = 0;
      for (const points of rings) {
        const count = points.length / 2;
        let x0 = points[(count - 1) * 2];
        let y0 = points[(count - 1) * 2 + 1];
        for (let i = 0; i < count; i++) {
          const x1 = points[i * 2];
          const y1 = points[i * 2 + 1];
          // the half-open rule: an edge counts at its lower end and not at its upper one, so a
          // vertex exactly on the scanline is crossed once rather than twice or not at all
          if (y0 <= lat !== y1 <= lat) crossings.push(x0 + ((lat - y0) / (y1 - y0)) * (x1 - x0));
          x0 = x1;
          y0 = y1;
        }
      }
      if (crossings.length < 2) continue;
      crossings.sort((a, b) => a - b);
      for (let i = 0; i + 1 < crossings.length; i += 2) {
        const from = Math.max(0, Math.ceil(colOf(crossings[i]) - 0.5));
        const to = Math.min(rasterWidth - 1, Math.floor(colOf(crossings[i + 1]) - 0.5));
        const at = row * rasterWidth;
        for (let col = from; col <= to; col++) cells[at + col] = c + 1;
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
 * as fine as the outlines it was drawn from and no finer, so a place within one cell of a coast is
 * not really at sea - it is on a coast this map cannot draw closely enough - and every harbour
 * town in the world sits there. Anything further out than that is water and says so.
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
 * and - last - how many were at sea, which on a 110m coastline means "in the water or within twenty
 * kilometres of a shore".
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
