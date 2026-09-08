/**
 * The shapes a card can take, and the silhouettes the card field cuts them out with.
 *
 * A card is a square by default. When the picture is grouped by a shape property as well as by a
 * colour, each value of that property is given a shape of its own, and the card is cut out to it -
 * picture, colour and all. There are thirty of them, and past thirty values the same shapes come
 * round again turned and made smaller (see `shapeVariants`), which is a weaker signal than a shape
 * of its own but still tells two values apart.
 *
 * A shape is authored ONCE, here, as a drawing in a unit box: everything that needs one paints it
 * on a canvas. That is the whole reason the shapes are drawings rather than distance functions in
 * the shader - a crown, a cloud, an anchor are a few curves and a stroke as a drawing, and a page
 * of arithmetic as a signed distance function, and the legend would need the drawing anyway. What
 * the shader gets is measured from the drawing: the shape is rasterized larger than it is needed,
 * the distance from every pixel to the outline is measured (`distanceField`), and that field goes
 * into one layer of an array texture the card shader reads. A distance field is what lets a card
 * keep a clean edge at any size - the shape is not a picture being stretched, it is a boundary the
 * shader finds to the pixel - and it costs the same one texture read whatever the shape, which
 * matters in a renderer that draws a million cards.
 *
 * The fields are built the first time a shape is actually used and kept for the life of the page:
 * about three milliseconds a shape, and only for the shapes on screen.
 */

/** The shapes, in the order they are handed out (see shapeSlotFor). Index 0 is the plain card. */
export const shapeNames = [
  "Square",
  "Triangle up",
  "Triangle down",
  "Circle",
  "Diamond",
  "Pentagon",
  "Hexagon",
  "Five-pointed star",
  "Four-pointed star",
  "Six-pointed star",
  "Plus",
  "Cross",
  "Teardrop",
  "Arrowhead up",
  "Arrowhead right",
  "Crescent moon",
  "Shield",
  "Heart",
  "Spade",
  "Club",
  "Keyhole",
  "Bell",
  "Leaf",
  "Cloud",
  "Lightning bolt",
  "Gear",
  "House",
  "Flag",
  "Crown",
  "Anchor",
] as const;

export const shapeCount = shapeNames.length;

/**
 * What is done to a shape when the shapes have run out and it has to stand for a second value: it
 * is turned and made smaller. Turning alone would leave a circle unchanged and a square looking
 * like a diamond, so every variation changes the size as well.
 */
export const shapeVariants: { angle: number; scale: number; suffix: string }[] = [
  { angle: 0, scale: 1, suffix: "" },
  { angle: (32 * Math.PI) / 180, scale: 0.84, suffix: ", turned" },
  { angle: 0, scale: 0.6, suffix: ", small" },
  { angle: (-32 * Math.PI) / 180, scale: 0.44, suffix: ", small and turned" },
];

/** How many distinct silhouettes there are: every shape in every variation. */
export const shapeSlots = shapeCount * shapeVariants.length;

/**
 * A slot - what a card carries - is the shape in its low byte and the variation in its high one.
 * Two bytes either way, but the shader takes a card apart with a mask and a shift rather than a
 * division, and it does that for every card of the picture on every frame.
 */
export const slotOf = (shape: number, variant: number) => ((variant & 0xff) << 8) | (shape & 0xff);
export const baseShapeOf = (slot: number) => slot & 0xff;
export const variantOf = (slot: number) => (slot >> 8) % shapeVariants.length;
export const shapeLabel = (slot: number) => shapeNames[baseShapeOf(slot)] + shapeVariants[variantOf(slot)].suffix;

/**
 * Which silhouette a value gets. The slot is hashed from the value rather than counted off the
 * list, for the same reason a colour is (see paletteSlots): a value keeps its shape when the
 * picture is narrowed to fewer of them. Two values that want the same shape are moved apart by
 * probing, and the probing walks the whole of one variation before it takes anything from the
 * next, so a property with a handful of values gets a handful of plain shapes and nothing is
 * turned or shrunk until there is a reason for it. Slot 0 - the plain card - is left out of the
 * hashing and kept for the cards with no value at all.
 */
export function shapeSlotFor(hash: number, taken: Set<number>): number {
  for (let variant = 0; variant < shapeVariants.length; variant++) {
    const first = variant === 0 ? 1 : 0; // the plain square is not handed to a value
    const n = shapeCount - first;
    const start = hash % n;
    for (let k = 0; k < n; k++) {
      const slot = slotOf(first + ((start + k) % n), variant);
      if (!taken.has(slot)) return slot;
    }
  }
  // more values than silhouettes: from here on they share
  return slotOf(hash % shapeCount, (hash >>> 8) % shapeVariants.length);
}

// ---- the drawings ----

/**
 * Paints one shape, white, into a canvas `size` across. Everything is authored in a unit box with a
 * margin of a few percent, so a shape never touches the edge of the card: the field outside the box
 * is not measured, and a shape running into it would smear along the edge when it is shrunk.
 */
export function paintShape(ctx: CanvasRenderingContext2D, shape: number, size: number) {
  ctx.save();
  ctx.scale(size, size);
  ctx.fillStyle = "#fff";
  ctx.strokeStyle = "#fff";
  ctx.lineJoin = "round";
  ctx.lineCap = "round";
  draw(ctx, shape);
  ctx.restore();
}

function draw(ctx: CanvasRenderingContext2D, shape: number) {
  switch (shape) {
    case 0: // square: the plain card, rounded the way the shader rounds it
      roundedRect(ctx, 0.035, 0.035, 0.965, 0.965, 0.085);
      return;
    case 1: // triangle up
      polygon(ctx, [0.5, 0.05, 0.965, 0.86, 0.035, 0.86]);
      return;
    case 2: // triangle down
      polygon(ctx, [0.035, 0.14, 0.965, 0.14, 0.5, 0.95]);
      return;
    case 3: // circle
      disc(ctx, 0.5, 0.5, 0.465);
      return;
    case 4: // diamond
      polygon(ctx, [0.5, 0.035, 0.965, 0.5, 0.5, 0.965, 0.035, 0.5]);
      return;
    case 5: // pentagon
      regular(ctx, 0.5, 0.53, 0.485, 5);
      return;
    case 6: // hexagon, point up
      regular(ctx, 0.5, 0.5, 0.475, 6);
      return;
    case 7: // five-pointed star
      star(ctx, 0.5, 0.53, 0.485, 0.2, 5);
      return;
    case 8: // four-pointed star
      star(ctx, 0.5, 0.5, 0.48, 0.145, 4);
      return;
    case 9: // six-pointed star
      star(ctx, 0.5, 0.5, 0.475, 0.245, 6);
      return;
    case 10: // plus
      polygon(ctx, plusPoints());
      return;
    case 11: // cross: the plus turned a quarter of a right angle
      turned(ctx, Math.PI / 4, 1, () => polygon(ctx, plusPoints()));
      return;
    case 12: // teardrop
      ctx.beginPath();
      ctx.moveTo(0.5, 0.04);
      ctx.quadraticCurveTo(0.88, 0.42, 0.86, 0.63);
      ctx.arc(0.5, 0.63, 0.36, 0, Math.PI);
      ctx.quadraticCurveTo(0.12, 0.42, 0.5, 0.04);
      ctx.fill();
      return;
    case 13: // arrowhead up
      polygon(ctx, [0.5, 0.05, 0.95, 0.79, 0.5, 0.57, 0.05, 0.79]);
      return;
    case 14: // arrowhead right
      polygon(ctx, [0.95, 0.5, 0.21, 0.95, 0.43, 0.5, 0.21, 0.05]);
      return;
    case 15: // crescent moon: a disc with a second one taken out of it
      disc(ctx, 0.46, 0.5, 0.465);
      ctx.save();
      ctx.globalCompositeOperation = "destination-out";
      disc(ctx, 0.73, 0.43, 0.44);
      ctx.restore();
      return;
    case 16: // shield
      ctx.beginPath();
      ctx.moveTo(0.09, 0.09);
      ctx.lineTo(0.91, 0.09);
      ctx.lineTo(0.91, 0.48);
      ctx.bezierCurveTo(0.91, 0.78, 0.72, 0.9, 0.5, 0.96);
      ctx.bezierCurveTo(0.28, 0.9, 0.09, 0.78, 0.09, 0.48);
      ctx.closePath();
      ctx.fill();
      return;
    case 17: // heart
      heart(ctx);
      return;
    case 18: // spade: the heart upside down, on a stem
      ctx.save();
      ctx.translate(0.5, 0.47);
      ctx.rotate(Math.PI);
      ctx.scale(0.94, 0.86);
      ctx.translate(-0.5, -0.5);
      heart(ctx);
      ctx.restore();
      polygon(ctx, [0.5, 0.6, 0.66, 0.95, 0.34, 0.95]);
      return;
    case 19: // club: three lobes with a notch between them, or it is a cloud
      disc(ctx, 0.5, 0.25, 0.225);
      disc(ctx, 0.25, 0.63, 0.225);
      disc(ctx, 0.75, 0.63, 0.225);
      polygon(ctx, [0.5, 0.5, 0.71, 0.96, 0.29, 0.96]);
      return;
    case 20: // keyhole
      disc(ctx, 0.5, 0.35, 0.26);
      polygon(ctx, [0.4, 0.48, 0.6, 0.48, 0.71, 0.95, 0.29, 0.95]);
      return;
    case 21: // bell
      ctx.beginPath();
      ctx.moveTo(0.15, 0.79);
      ctx.bezierCurveTo(0.2, 0.62, 0.22, 0.42, 0.28, 0.28);
      ctx.bezierCurveTo(0.33, 0.16, 0.42, 0.09, 0.5, 0.09);
      ctx.bezierCurveTo(0.58, 0.09, 0.67, 0.16, 0.72, 0.28);
      ctx.bezierCurveTo(0.78, 0.42, 0.8, 0.62, 0.85, 0.79);
      ctx.closePath();
      ctx.fill();
      roundedRect(ctx, 0.11, 0.79, 0.89, 0.88, 0.035);
      disc(ctx, 0.5, 0.94, 0.075);
      return;
    case 22: // leaf
      ctx.beginPath();
      ctx.moveTo(0.09, 0.91);
      ctx.bezierCurveTo(0.07, 0.42, 0.42, 0.07, 0.91, 0.09);
      ctx.bezierCurveTo(0.93, 0.58, 0.58, 0.93, 0.09, 0.91);
      ctx.closePath();
      ctx.fill();
      return;
    case 23: // cloud
      disc(ctx, 0.29, 0.55, 0.21);
      disc(ctx, 0.51, 0.37, 0.27);
      disc(ctx, 0.75, 0.56, 0.2);
      roundedRect(ctx, 0.08, 0.55, 0.92, 0.83, 0.13);
      return;
    case 24: // lightning bolt
      polygon(ctx, [0.66, 0.04, 0.24, 0.56, 0.47, 0.56, 0.34, 0.96, 0.78, 0.42, 0.53, 0.42]);
      return;
    case 25: // gear
      gear(ctx);
      return;
    case 26: // house
      polygon(ctx, [0.5, 0.06, 0.97, 0.45, 0.85, 0.45, 0.85, 0.94, 0.15, 0.94, 0.15, 0.45, 0.03, 0.45]);
      return;
    case 27: // flag
      ctx.beginPath();
      ctx.moveTo(0.21, 0.09);
      ctx.bezierCurveTo(0.45, 0.01, 0.68, 0.25, 0.93, 0.15);
      ctx.lineTo(0.93, 0.53);
      ctx.bezierCurveTo(0.68, 0.63, 0.45, 0.39, 0.21, 0.47);
      ctx.closePath();
      ctx.fill();
      roundedRect(ctx, 0.13, 0.05, 0.24, 0.96, 0.055);
      return;
    case 28: // crown
      polygon(ctx, [0.05, 0.83, 0.05, 0.24, 0.28, 0.5, 0.5, 0.15, 0.72, 0.5, 0.95, 0.24, 0.95, 0.83]);
      return;
    default: // anchor
      anchor(ctx);
      return;
  }
}

function polygon(ctx: CanvasRenderingContext2D, points: number[]) {
  ctx.beginPath();
  ctx.moveTo(points[0], points[1]);
  for (let i = 2; i < points.length; i += 2) ctx.lineTo(points[i], points[i + 1]);
  ctx.closePath();
  ctx.fill();
}

function disc(ctx: CanvasRenderingContext2D, cx: number, cy: number, r: number) {
  ctx.beginPath();
  ctx.arc(cx, cy, r, 0, Math.PI * 2);
  ctx.fill();
}

function roundedRect(ctx: CanvasRenderingContext2D, x0: number, y0: number, x1: number, y1: number, r: number) {
  r = Math.min(r, (x1 - x0) / 2, (y1 - y0) / 2);
  ctx.beginPath();
  ctx.moveTo(x0 + r, y0);
  ctx.arcTo(x1, y0, x1, y1, r);
  ctx.arcTo(x1, y1, x0, y1, r);
  ctx.arcTo(x0, y1, x0, y0, r);
  ctx.arcTo(x0, y0, x1, y0, r);
  ctx.closePath();
  ctx.fill();
}

/** A regular polygon standing on a flat side, its first point straight up. */
function regular(ctx: CanvasRenderingContext2D, cx: number, cy: number, r: number, n: number) {
  const points: number[] = [];
  for (let i = 0; i < n; i++) {
    const a = -Math.PI / 2 + (i * 2 * Math.PI) / n;
    points.push(cx + r * Math.cos(a), cy + r * Math.sin(a));
  }
  polygon(ctx, points);
}

function star(ctx: CanvasRenderingContext2D, cx: number, cy: number, r: number, inner: number, n: number) {
  const points: number[] = [];
  for (let i = 0; i < n * 2; i++) {
    const a = -Math.PI / 2 + (i * Math.PI) / n;
    const rr = i % 2 === 0 ? r : inner;
    points.push(cx + rr * Math.cos(a), cy + rr * Math.sin(a));
  }
  polygon(ctx, points);
}

/** the twelve corners of a thick plus, arms a third of the box across */
function plusPoints(): number[] {
  const a = 0.335;
  const b = 0.665;
  const lo = 0.04;
  const hi = 0.96;
  return [a, lo, b, lo, b, a, hi, a, hi, b, b, b, b, hi, a, hi, a, b, lo, b, lo, a, a, a];
}

function turned(ctx: CanvasRenderingContext2D, angle: number, scale: number, body: () => void) {
  ctx.save();
  ctx.translate(0.5, 0.5);
  ctx.rotate(angle);
  ctx.scale(scale, scale);
  ctx.translate(-0.5, -0.5);
  body();
  ctx.restore();
}

function heart(ctx: CanvasRenderingContext2D) {
  ctx.beginPath();
  ctx.moveTo(0.5, 0.95);
  ctx.bezierCurveTo(0.02, 0.62, 0.04, 0.2, 0.27, 0.11);
  ctx.bezierCurveTo(0.4, 0.06, 0.48, 0.16, 0.5, 0.27);
  ctx.bezierCurveTo(0.52, 0.16, 0.6, 0.06, 0.73, 0.11);
  ctx.bezierCurveTo(0.96, 0.2, 0.98, 0.62, 0.5, 0.95);
  ctx.closePath();
  ctx.fill();
}

function gear(ctx: CanvasRenderingContext2D) {
  const teeth = 8;
  for (let i = 0; i < teeth; i++) {
    ctx.save();
    ctx.translate(0.5, 0.5);
    ctx.rotate((i * Math.PI * 2) / teeth);
    ctx.beginPath();
    ctx.rect(-0.105, -0.475, 0.21, 0.28);
    ctx.fill();
    ctx.restore();
  }
  disc(ctx, 0.5, 0.5, 0.335);
  ctx.save();
  ctx.globalCompositeOperation = "destination-out";
  disc(ctx, 0.5, 0.5, 0.135);
  ctx.restore();
}

function anchor(ctx: CanvasRenderingContext2D) {
  ctx.lineWidth = 0.115;
  ctx.beginPath();
  ctx.moveTo(0.5, 0.22);
  ctx.lineTo(0.5, 0.88);
  ctx.moveTo(0.26, 0.37);
  ctx.lineTo(0.74, 0.37);
  ctx.stroke();
  ctx.beginPath();
  ctx.arc(0.5, 0.62, 0.32, (25 * Math.PI) / 180, (155 * Math.PI) / 180);
  ctx.stroke();
  ctx.lineWidth = 0.075;
  ctx.beginPath();
  ctx.arc(0.5, 0.15, 0.105, 0, Math.PI * 2);
  ctx.stroke();
}

// ---- the field the shader reads ----

/** how many texels across one shape is measured; the whole card, so a texel is a card in this many parts */
export const shapeFieldSize = 256;
/** how much finer than that the shape is rasterized before it is measured */
const superSample = 2;
/** how far from the outline the field is kept, as a fraction of the card: past this a card is simply outside the shape */
const fieldRange = 0.5;

const fields: (Float32Array | null)[] = shapeNames.map(() => null);
let scratch: { canvas: HTMLCanvasElement; ctx: CanvasRenderingContext2D } | null = null;

/**
 * The distance from every point of the card to the outline of a shape, positive outside it, in
 * fractions of the card's width - which is what the shader needs to draw an edge one pixel wide
 * whatever the card's size. Built once per shape and kept.
 */
export function shapeField(shape: number): Float32Array {
  const cached = fields[shape];
  if (cached !== null) return cached;
  const n = shapeFieldSize * superSample;
  if (scratch === null || scratch.canvas.width !== n) {
    const canvas = document.createElement("canvas");
    canvas.width = n;
    canvas.height = n;
    const ctx = canvas.getContext("2d", { willReadFrequently: true });
    if (!ctx) throw new Error("no 2d canvas to measure the shapes with");
    scratch = { canvas, ctx };
  }
  const { ctx } = scratch;
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.clearRect(0, 0, n, n);
  paintShape(ctx, shape, n);
  const pixels = ctx.getImageData(0, 0, n, n).data;
  const inside = new Uint8Array(n * n);
  for (let i = 0; i < inside.length; i++) inside[i] = pixels[i * 4 + 3] >= 128 ? 1 : 0;

  // the distance to the nearest pixel of the other side, both ways round: their difference is the
  // signed distance, and its zero crossing sits on the boundary between the two pixels
  const out = squaredDistance(inside, n, 1);
  const within = squaredDistance(inside, n, 0);
  const field = new Float32Array(shapeFieldSize * shapeFieldSize);
  const limit = fieldRange * n;
  for (let y = 0; y < shapeFieldSize; y++) {
    for (let x = 0; x < shapeFieldSize; x++) {
      // the finer measurement is averaged down: a distance field takes an average without harm
      let sum = 0;
      for (let sy = 0; sy < superSample; sy++) {
        for (let sx = 0; sx < superSample; sx++) {
          const at = (y * superSample + sy) * n + x * superSample + sx;
          const d = Math.sqrt(out[at]) - Math.sqrt(within[at]);
          sum += Math.max(-limit, Math.min(limit, d));
        }
      }
      field[y * shapeFieldSize + x] = sum / (superSample * superSample * n);
    }
  }
  fields[shape] = field;
  return field;
}

/**
 * The squared distance from every cell to the nearest cell whose mask is `seed`, by Felzenszwalb
 * and Huttenlocher's transform: the lower envelope of one parabola per cell, found in one pass
 * along each row and then each column, so the whole grid costs a constant few operations a cell
 * however far the distances reach.
 */
function squaredDistance(mask: Uint8Array, n: number, seed: number): Float64Array {
  const INF = 1e20;
  const grid = new Float64Array(n * n);
  for (let i = 0; i < grid.length; i++) grid[i] = mask[i] === seed ? 0 : INF;
  const f = new Float64Array(n);
  const d = new Float64Array(n);
  const v = new Int32Array(n);
  const z = new Float64Array(n + 1);
  for (let y = 0; y < n; y++) {
    for (let x = 0; x < n; x++) f[x] = grid[y * n + x];
    envelope(f, d, v, z, n);
    for (let x = 0; x < n; x++) grid[y * n + x] = d[x];
  }
  for (let x = 0; x < n; x++) {
    for (let y = 0; y < n; y++) f[y] = grid[y * n + x];
    envelope(f, d, v, z, n);
    for (let y = 0; y < n; y++) grid[y * n + x] = d[y];
  }
  return grid;
}

function envelope(f: Float64Array, d: Float64Array, v: Int32Array, z: Float64Array, n: number) {
  const INF = 1e20;
  let k = 0;
  v[0] = 0;
  z[0] = -INF;
  z[1] = INF;
  for (let q = 1; q < n; q++) {
    let s = (f[q] + q * q - (f[v[k]] + v[k] * v[k])) / (2 * q - 2 * v[k]);
    while (s <= z[k]) {
      k--;
      s = (f[q] + q * q - (f[v[k]] + v[k] * v[k])) / (2 * q - 2 * v[k]);
    }
    k++;
    v[k] = q;
    z[k] = s;
    z[k + 1] = INF;
  }
  k = 0;
  for (let q = 0; q < n; q++) {
    while (z[k + 1] < q) k++;
    d[q] = (q - v[k]) * (q - v[k]) + f[v[k]];
  }
}

// ---- the legend's swatch ----

const swatches = new Map<number, string>();

/**
 * A picture of one silhouette, white on nothing, for the legend to wear as a mask - so the swatch
 * beside a value is the shape its cards are cut out to, in whatever colour the legend paints it.
 */
export function shapeMaskUrl(slot: number): string {
  const cached = swatches.get(slot);
  if (cached !== undefined) return cached;
  const size = 48;
  const canvas = document.createElement("canvas");
  canvas.width = size;
  canvas.height = size;
  const ctx = canvas.getContext("2d");
  if (!ctx) return "";
  const variant = shapeVariants[variantOf(slot)];
  ctx.translate(size / 2, size / 2);
  ctx.rotate(variant.angle);
  ctx.scale(variant.scale, variant.scale);
  ctx.translate(-size / 2, -size / 2);
  paintShape(ctx, baseShapeOf(slot), size);
  const url = canvas.toDataURL();
  swatches.set(slot, url);
  return url;
}
