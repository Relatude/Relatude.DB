/**
 * The world as SVG path data: the outlines, the shape of each country, and the grid of latitude and
 * longitude under them.
 *
 * Paths are written in a space of their own - world units multiplied by `pathScale` - and the view
 * is a transform on the group holding them, so panning and zooming the map never rebuilds a path.
 * The multiplier is there because a path in world units would be numbers like 0.0134 written to six
 * decimals; multiplied up they are written to one, which is finer than the outlines themselves are
 * (they are quantized to about 400 m) and a third of the text.
 */

import type { Projection } from "./projection";
import { ringPoints, world, type WorldCountry } from "./worldMap";

/** What a path coordinate is, in world units. The view's transform divides by it again. */
export const pathScale = 10000;

/**
 * How long a line may run before it is broken into pieces, in degrees. A straight line between two
 * places is straight on an equirectangular map and bowed on every other one, and the outlines hold
 * plenty of long straight borders - so on a projection that bends, the long ones are followed round
 * rather than cut across.
 */
const maxSegmentDegrees = 4;

/** Where the seam of the map is: a step wider than this in longitude is a line wrapping round the world, not a line. */
const seam = 180;

class PathBuilder {
  private out: string[] = [];
  private open = false;
  /** `straight`: a step between two places is a straight line on this projection, so it needs no following round. */
  constructor(
    private projection: Projection,
    private straight: boolean,
  ) {}
  private write(command: "M" | "L", lon: number, lat: number) {
    const [x, y] = this.projection.project(lon, lat);
    this.out.push(command + round(x * pathScale) + " " + round(y * pathScale));
  }
  /** Starts a new run at a place. */
  moveTo(lon: number, lat: number) {
    this.write("M", lon, lat);
    this.open = true;
  }
  /**
   * Continues the run to a place, following the projection's curve over a long step and breaking
   * the run entirely at the seam - where the two ends are on opposite edges of the map and the
   * line between them would be drawn straight across the middle of it.
   */
  lineTo(lon: number, lat: number, fromLon: number, fromLat: number) {
    if (Math.abs(lon - fromLon) > seam) {
      this.moveTo(lon, lat);
      return;
    }
    const steps = this.straight ? 1 : Math.min(64, Math.ceil(Math.max(Math.abs(lon - fromLon), Math.abs(lat - fromLat)) / maxSegmentDegrees));
    for (let s = 1; s < steps; s++) {
      const t = s / steps;
      this.write("L", fromLon + (lon - fromLon) * t, fromLat + (lat - fromLat) * t);
    }
    this.write("L", lon, lat);
  }
  close() {
    if (this.open) this.out.push("Z");
    this.open = false;
  }
  toString() {
    return this.out.join("");
  }
}

function round(v: number): string {
  return (Math.round(v * 10) / 10).toString();
}

/** One run of lon/lat degrees added to a builder, as an open line or a closed ring. */
function addRun(builder: PathBuilder, points: Float32Array, closed: boolean) {
  const count = points.length / 2;
  if (count < 2) return;
  builder.moveTo(points[0], points[1]);
  for (let i = 1; i < count; i++) builder.lineTo(points[i * 2], points[i * 2 + 1], points[(i - 1) * 2], points[(i - 1) * 2 + 1]);
  if (closed) builder.close();
}

/** A step between two places is a straight line on an equirectangular map and a curve on every other one. */
const isStraight = (projection: Projection) => projection.id === "equirectangular";

/**
 * Every coastline and border of the world as one path. One path rather than one per country because
 * the outlines share their arcs - a border belongs to both of its countries and is drawn once - and
 * because a single element of eight thousand points is a great deal cheaper for a browser to keep
 * on screen than six hundred small ones.
 */
export function outlinePath(projection: Projection): string {
  const builder = new PathBuilder(projection, isStraight(projection));
  for (const arc of world().arcs) addRun(builder, arc, false);
  return builder.toString();
}

/** The shape of one country, holes and islands and all, for a map that fills them. */
export function countryPath(projection: Projection, country: WorldCountry): string {
  const w = world();
  const builder = new PathBuilder(projection, isStraight(projection));
  for (const ring of country.rings) addRun(builder, ringPoints(w, ring), true);
  return builder.toString();
}

/** The lines of latitude and longitude, every `every` degrees, and the two edges of the map. */
export function graticulePath(projection: Projection, every = 30): string {
  const builder = new PathBuilder(projection, false);
  const limit = Math.min(projection.latitudeLimit, 90);
  const step = 2;
  for (let lon = -180; lon <= 180; lon += every) {
    builder.moveTo(lon, -limit);
    for (let lat = -limit + step; lat <= limit; lat += step) builder.lineTo(lon, Math.min(lat, limit), lon, lat - step);
  }
  for (let lat = -90 + every; lat < 90; lat += every) {
    if (Math.abs(lat) > limit) continue;
    builder.moveTo(-180, lat);
    for (let lon = -180 + step * 3; lon <= 180; lon += step * 3) builder.lineTo(Math.min(lon, 180), lat, lon - step * 3, lat);
  }
  return builder.toString();
}
