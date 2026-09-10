/**
 * The map's answer, turned into the typed arrays everything downstream works on: where each node
 * is, which colour group it belongs to, and - once anything has to be picked with a pointer - a
 * grid over the world for finding the point nearest a place without touching the other million.
 */

import { bytesOf, type MapResult, type VisualGroup } from "../server/query";
import { paletteSlot } from "../visual/palette";

/** What a group stands for: a value of the property, the nodes without one, or the ones outside the groups kept. */
export type GroupKind = "value" | "none" | "other";

export interface MapGroup extends VisualGroup {
  kind: GroupKind;
  /** the group's place in the palette; -1 for the two greys (see paletteSlot) */
  ordinal: number;
}

export interface MapProperty {
  propertyId: string;
  name: string;
  groups: MapGroup[];
  /** the group of every point, an index into `groups` */
  assignment: Uint16Array;
}

/** The result decoded, done once per result. */
export interface MapPoints {
  /** how many points are on the map */
  count: number;
  /** how many nodes the search found, and how many of them the map looked at */
  total: number;
  read: number;
  ids: Int32Array;
  /** degrees */
  lat: Float32Array;
  lon: Float32Array;
  byProperty: Map<string, MapProperty>;
}

/** What the server sends a coordinate as: ten million to the degree, which is the grid the store snaps to. */
const coordinateScale = 1e7;
const paletteSize = 512; // above the server's cap of groups per property

export function decodeMap(result: MapResult): MapPoints {
  const idBytes = bytesOf(result.ids);
  const ids = new Int32Array(idBytes.buffer, idBytes.byteOffset, result.count);
  const fixed = bytesOf(result.coordinates);
  const packed = new Int32Array(fixed.buffer, fixed.byteOffset, result.count * 2);
  const lat = new Float32Array(result.count);
  const lon = new Float32Array(result.count);
  for (let i = 0; i < result.count; i++) {
    lat[i] = packed[i * 2] / coordinateScale;
    lon[i] = packed[i * 2 + 1] / coordinateScale;
  }
  const byProperty = new Map<string, MapProperty>();
  for (const p of result.properties) {
    const bytes = bytesOf(p.assignment);
    const assignment = new Uint16Array(bytes.buffer, bytes.byteOffset, result.count);
    const groups: MapGroup[] = p.groups.map((g) => ({ ...g, kind: g.value === null && g.value2 === null ? "none" : "value", ordinal: -1 }));
    const taken = new Set<number>();
    for (const g of groups) if (g.kind === "value") g.ordinal = paletteSlot((g.value ?? "") + "|" + (g.value2 ?? ""), taken, paletteSize);
    if (p.unassigned > 0) {
      // the points outside the groups kept become one group of their own, so every point has a place
      const other = groups.length;
      groups.push({ label: "(other)", value: null, value2: null, count: p.unassigned, kind: "other", ordinal: -1 });
      for (let i = 0; i < assignment.length; i++) if (assignment[i] === 0xffff) assignment[i] = other;
    }
    byProperty.set(p.propertyId, { propertyId: p.propertyId, name: p.name, groups, assignment });
  }
  return { count: result.count, total: result.total, read: result.read, ids, lat, lon, byProperty };
}

/**
 * A grid over the world holding every point, for finding what is under a pointer.
 *
 * One degree a cell, as a chained list rather than a list per cell: a head index per cell and a
 * next index per point, which is two typed arrays and one pass over the points to build - no
 * objects, no allocation per point, and the same cost whether the map holds a thousand points or a
 * million. What a search does is walk the cells around a place and look at the few points in them.
 */
export class PointIndex {
  private static readonly cols = 360;
  private static readonly rows = 180;
  private head: Int32Array;
  private next: Int32Array;
  constructor(
    private lat: Float32Array,
    private lon: Float32Array,
    private count: number,
  ) {
    this.head = new Int32Array(PointIndex.cols * PointIndex.rows).fill(-1);
    this.next = new Int32Array(count);
    for (let i = 0; i < count; i++) {
      const cell = this.cellOf(lon[i], lat[i]);
      this.next[i] = this.head[cell];
      this.head[cell] = i;
    }
  }
  private cellOf(lon: number, lat: number): number {
    const col = Math.min(PointIndex.cols - 1, Math.max(0, Math.floor(lon + 180)));
    const row = Math.min(PointIndex.rows - 1, Math.max(0, Math.floor(90 - lat)));
    return row * PointIndex.cols + col;
  }
  /**
   * The point nearest a place, within `withinDegrees` of it, or -1. Distance is measured on the
   * lon/lat grid with the longitude squeezed by the latitude, so "nearest" means nearest on the
   * ground rather than nearest on a chart that stretches the poles.
   */
  nearest(lon: number, lat: number, withinDegrees: number): number {
    const reach = Math.max(1, Math.ceil(withinDegrees));
    const squeeze = Math.max(0.05, Math.cos((lat * Math.PI) / 180));
    let best = -1;
    let bestDistance = withinDegrees * withinDegrees;
    const centreCol = Math.floor(lon + 180);
    const centreRow = Math.floor(90 - lat);
    for (let row = centreRow - reach; row <= centreRow + reach; row++) {
      if (row < 0 || row >= PointIndex.rows) continue;
      for (let c = centreCol - reach; c <= centreCol + reach; c++) {
        const col = ((c % PointIndex.cols) + PointIndex.cols) % PointIndex.cols; // longitude wraps
        for (let i = this.head[row * PointIndex.cols + col]; i >= 0; i = this.next[i]) {
          let dLon = this.lon[i] - lon;
          if (dLon > 180) dLon -= 360;
          else if (dLon < -180) dLon += 360;
          const dLat = this.lat[i] - lat;
          const distance = dLon * squeeze * (dLon * squeeze) + dLat * dLat;
          if (distance < bestDistance) {
            bestDistance = distance;
            best = i;
          }
        }
      }
    }
    return best;
  }
  get size() {
    return this.count;
  }
}
