/**
 * How a place on the globe becomes a place on a flat map, and back again.
 *
 * Every projection here works in the same world units: the whole world is 2 wide, from x = -1 at
 * 180° west to x = +1 at 180° east, and `height` tall, centred on 0 with y growing SOUTH - the
 * direction a screen's y grows, so nothing has to be flipped between projecting a point and drawing
 * it. What differs between them is only how much of that height each degree of latitude gets.
 *
 * Three of them, which is the useful spread rather than an atlas: the plain grid every geographic
 * dataset is implicitly drawn on, the one every web map uses, and one that looks like a world map.
 */

export interface Projection {
  id: string;
  name: string;
  hint: string;
  /** How tall the whole world is in world units, where it is 2 wide. */
  height: number;
  /** How far north and south the projection reaches; Mercator cannot draw the poles at all. */
  latitudeLimit: number;
  /** A place to a point of the map. */
  project(lon: number, lat: number): [number, number];
  /** And back: a point of the map to the place under it. Points outside the world come back null. */
  invert(x: number, y: number): [number, number] | null;
}

const rad = Math.PI / 180;
const deg = 180 / Math.PI;

/** The latitude Mercator is cut off at: the one that makes the map square, as every web map does. */
const mercatorLimit = 85.0511287798;

const equirectangular: Projection = {
  id: "equirectangular",
  name: "Equirectangular",
  hint: "Longitude and latitude as a plain grid — every degree the same size, which stretches the far north and south sideways",
  height: 1,
  latitudeLimit: 90,
  project: (lon, lat) => [lon / 180, -lat / 180],
  invert: (x, y) => (Math.abs(x) > 1 || Math.abs(y) > 0.5 ? null : [x * 180, -y * 180]),
};

const mercator: Projection = {
  id: "mercator",
  name: "Mercator",
  hint: "The projection web maps use: shapes stay true at every zoom, at the cost of a Greenland the size of Africa",
  height: 2,
  latitudeLimit: mercatorLimit,
  project: (lon, lat) => {
    const φ = Math.max(-mercatorLimit, Math.min(mercatorLimit, lat)) * rad;
    return [lon / 180, -Math.log(Math.tan(Math.PI / 4 + φ / 2)) / Math.PI];
  },
  invert: (x, y) => (Math.abs(x) > 1 || Math.abs(y) > 1 ? null : [x * 180, (2 * Math.atan(Math.exp(-y * Math.PI)) - Math.PI / 2) * deg]),
};

/**
 * Natural Earth (Tom Patterson's, as fitted by Bojan Šavrič): neither equal-area nor conformal, and
 * chosen for exactly that - it is the compromise that a world map is expected to look like, with
 * poles drawn as curves rather than as a line the width of the equator.
 *
 * Both axes are polynomials in the latitude. The forward one is the published fit; going back is a
 * few rounds of Newton on the y polynomial, which is monotone over the range and so has one answer.
 */
function naturalX(φ: number): number {
  const φ2 = φ * φ;
  const φ4 = φ2 * φ2;
  return 0.8707 - 0.131979 * φ2 + φ4 * (-0.013791 + φ4 * (0.003971 * φ2 - 0.001529 * φ4));
}
function naturalY(φ: number): number {
  const φ2 = φ * φ;
  const φ4 = φ2 * φ2;
  return φ * (1.007226 + φ2 * (0.015085 + φ4 * (-0.044475 + 0.028874 * φ2 - 0.005916 * φ4)));
}
function naturalYSlope(φ: number): number {
  const φ2 = φ * φ;
  const φ4 = φ2 * φ2;
  return 1.007226 + φ2 * (0.015085 * 3 + φ4 * (-0.044475 * 7 + 0.028874 * 9 * φ2 - 0.005916 * 11 * φ4));
}
// what the polynomials give at the edges, so the map can be scaled into the same 2 x height box
const naturalWidth = Math.PI * naturalX(0);
const naturalHeight = (2 * naturalY(Math.PI / 2)) / naturalWidth;

const natural: Projection = {
  id: "natural",
  name: "Natural Earth",
  hint: "A compromise projection: no property is exact, but the world looks the way a world map looks",
  height: naturalHeight,
  latitudeLimit: 90,
  project: (lon, lat) => {
    const φ = lat * rad;
    return [(lon * rad * naturalX(φ)) / naturalWidth, -naturalY(φ) / naturalWidth];
  },
  invert: (x, y) => {
    const target = -y * naturalWidth;
    if (Math.abs(target) > naturalY(Math.PI / 2)) return null;
    let φ = target; // the identity is a good first guess: the y polynomial is near enough φ itself
    for (let i = 0; i < 8; i++) {
      const step = (naturalY(φ) - target) / naturalYSlope(φ);
      φ -= step;
      if (Math.abs(step) < 1e-10) break;
    }
    const lon = (x * naturalWidth) / (naturalX(φ) * rad);
    return Math.abs(lon) > 180.0001 ? null : [Math.max(-180, Math.min(180, lon)), φ * deg];
  },
};

export const projections: Projection[] = [natural, equirectangular, mercator];

export function projectionOf(id: string | null | undefined): Projection {
  return projections.find((p) => p.id === id) ?? projections[0];
}

/**
 * What part of the map is on screen: the world point at the middle of the canvas, and how many
 * device-independent pixels one world unit is. A world unit is half the width of the world, so
 * `scale` is half the width the whole world is drawn at - `fit` sets it so the world fills the
 * canvas, and zooming multiplies it.
 */
export interface View {
  cx: number;
  cy: number;
  scale: number;
}

export function screenX(view: View, x: number, width: number): number {
  return width / 2 + (x - view.cx) * view.scale;
}
export function screenY(view: View, y: number, height: number): number {
  return height / 2 + (y - view.cy) * view.scale;
}
export function worldX(view: View, px: number, width: number): number {
  return view.cx + (px - width / 2) / view.scale;
}
export function worldY(view: View, py: number, height: number): number {
  return view.cy + (py - height / 2) / view.scale;
}

/** The view that fits the whole world in a canvas of this size, with a little air around it. */
export function fitView(projection: Projection, width: number, height: number, padding = 8): View {
  const scale = Math.min((width - padding * 2) / 2, (height - padding * 2) / projection.height);
  return { cx: 0, cy: 0, scale: Math.max(1, scale) };
}

/**
 * A view held inside the world: it may be zoomed in as far as anyone likes but never dragged so far
 * that the map leaves the canvas. Longitude wraps, so the horizontal limit is loose - the map is
 * allowed to sit anywhere as long as some of it is still on screen - while the vertical one is hard,
 * since there is nothing above the north pole to show.
 */
export function clampView(view: View, projection: Projection, width: number, height: number): View {
  const halfW = width / 2 / view.scale;
  const halfH = height / 2 / view.scale;
  const limitX = Math.max(0, 1 - halfW * 0.4);
  const limitY = Math.max(0, projection.height / 2 - halfH);
  return {
    scale: view.scale,
    cx: Math.max(-limitX, Math.min(limitX, view.cx)),
    cy: Math.max(-limitY, Math.min(limitY, view.cy)),
  };
}
