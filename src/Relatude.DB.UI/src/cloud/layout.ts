/**
 * Laying words out as a cloud.
 *
 * The classic arrangement: the heaviest word goes in the middle, and every word after it spirals
 * outwards from there until it finds somewhere it does not touch anything already placed. What
 * makes it read as one shape rather than a scatter is that the search starts at the centre every
 * time, so each word settles into the nearest hole rather than the next free slot.
 *
 * Collisions are tested against a grid of occupied cells rather than against the words themselves:
 * a word covers a handful of cells and testing them is a few array reads, where testing rectangles
 * would be a pass over everything placed so far for every step of every spiral. The grid is coarse
 * (a few pixels), which is also what leaves the small gap between neighbours that keeps the cloud
 * legible instead of welded together.
 *
 * Nothing here draws or measures text itself: the caller passes a measurer, because measuring means
 * a canvas and the caller already has the font the words will really be drawn in.
 */

export interface CloudInput {
  text: string;
  /** What the word's size comes from; only the order and the ratios matter. */
  weight: number;
}

export interface PlacedWord {
  text: string;
  weight: number;
  /** The centre of the word, in the layout's own coordinates (the origin is the middle). */
  x: number;
  y: number;
  /** In pixels at layout scale. */
  fontSize: number;
  /** Quarter turn anticlockwise, so the word reads bottom-to-top. */
  rotated: boolean;
  width: number;
  height: number;
}

export interface CloudLayout {
  words: PlacedWord[];
  /** What the words cover, as a box around the origin - what a viewBox should show. */
  box: { x: number; y: number; width: number; height: number };
  /** How many were asked for but found nowhere to go. */
  dropped: number;
}

export interface CloudOptions {
  /** The box to fill, in the same pixels as the font sizes. */
  width: number;
  height: number;
  /** The smallest and largest font size to draw a word at. */
  minFontSize: number;
  maxFontSize: number;
  /** Whether every word sits level; false turns some of them a quarter turn. */
  upright: boolean;
  /** Measures a word at a size, as the caller will really draw it. */
  measure: (text: string, fontSize: number) => { width: number; height: number };
}

/** The gap left round every word, in pixels, so neighbours do not touch. */
const padding = 2;
/** How coarse the collision grid is. Smaller packs tighter and costs more. */
const cell = 3;

/**
 * A word's size from its weight. By square root rather than straight: a word ten times as common as
 * another is not worth ten times the height, and drawn that way it would leave no room for anything
 * else. The square root is what keeps the long tail readable while the head still dominates.
 */
function sizeOf(weight: number, min: number, max: number, minFont: number, maxFont: number): number {
  if (max <= min) return maxFont;
  const t = Math.sqrt((weight - min) / (max - min));
  return minFont + (maxFont - minFont) * t;
}

/** Deterministic per word, so the same cloud comes back the same way round every time. */
function hash(text: string): number {
  let h = 2166136261;
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i);
    h = Math.imul(h, 16777619);
  }
  return (h >>> 0) / 4294967296;
}

export function layoutCloud(input: CloudInput[], options: CloudOptions): CloudLayout {
  const words = input.filter((w) => w.text.length > 0 && w.weight > 0);
  if (words.length === 0 || options.width <= 0 || options.height <= 0) {
    return { words: [], box: { x: 0, y: 0, width: Math.max(1, options.width), height: Math.max(1, options.height) }, dropped: 0 };
  }
  // the cloud may spill past the box it was given and still be readable once it is scaled to fit,
  // so the grid is larger than the box - without the room, the tail would simply be dropped
  const gridWidth = Math.ceil((options.width * 1.6) / cell);
  const gridHeight = Math.ceil((options.height * 1.6) / cell);
  const taken = new Uint8Array(gridWidth * gridHeight);
  const originX = Math.floor(gridWidth / 2);
  const originY = Math.floor(gridHeight / 2);

  const weights = words.map((w) => w.weight);
  const min = Math.min(...weights);
  const max = Math.max(...weights);
  // the spiral is walked in steps of about one cell, and stretched across so a wide box fills wide
  const aspect = options.width / options.height;

  const placed: PlacedWord[] = [];
  let dropped = 0;
  let left = Infinity;
  let top = Infinity;
  let right = -Infinity;
  let bottom = -Infinity;

  for (const word of words) {
    const fontSize = sizeOf(word.weight, min, max, options.minFontSize, options.maxFontSize);
    const measured = options.measure(word.text, fontSize);
    // a word is turned only if it is long enough for the turn to buy anything, and then only some
    // of the time - a cloud where every second word stands on end is a chore to read
    const rotated = !options.upright && word.text.length > 3 && hash(word.text) < 0.28;
    const width = (rotated ? measured.height : measured.width) + padding * 2;
    const height = (rotated ? measured.width : measured.height) + padding * 2;
    const cols = Math.max(1, Math.ceil(width / cell));
    const rows = Math.max(1, Math.ceil(height / cell));
    if (cols >= gridWidth || rows >= gridHeight) {
      dropped++;
      continue;
    }

    const spot = findSpot(taken, gridWidth, gridHeight, originX, originY, cols, rows, aspect);
    if (spot === null) {
      dropped++;
      continue;
    }
    fill(taken, gridWidth, spot.col, spot.row, cols, rows);
    // back to pixels, and to an origin in the middle: the centre of the cells the word was given
    const x = (spot.col + cols / 2 - originX) * cell;
    const y = (spot.row + rows / 2 - originY) * cell;
    placed.push({ text: word.text, weight: word.weight, x, y, fontSize, rotated, width: measured.width, height: measured.height });
    left = Math.min(left, x - width / 2);
    right = Math.max(right, x + width / 2);
    top = Math.min(top, y - height / 2);
    bottom = Math.max(bottom, y + height / 2);
  }

  if (placed.length === 0) {
    return { words: [], box: { x: 0, y: 0, width: options.width, height: options.height }, dropped };
  }
  return { words: placed, box: { x: left, y: top, width: Math.max(1, right - left), height: Math.max(1, bottom - top) }, dropped };
}

/**
 * The first place going outwards from the centre where a block of cols x rows cells is free. The
 * spiral is Archimedean - the radius grows with the angle - which sweeps outwards evenly instead of
 * ringing round at one radius for a long time, and it is stretched by the box's aspect so the cloud
 * grows into the shape it has to fill.
 */
function findSpot(
  taken: Uint8Array,
  gridWidth: number,
  gridHeight: number,
  originX: number,
  originY: number,
  cols: number,
  rows: number,
  aspect: number,
): { col: number; row: number } | null {
  const maxRadius = Math.max(gridWidth, gridHeight);
  // a step under a cell would test the same block twice; much over one leaves holes
  const step = 0.8;
  for (let t = 0; ; t += step / Math.max(1, Math.sqrt(t + 1))) {
    const radius = t * 0.5;
    if (radius > maxRadius) return null;
    const col = Math.round(originX + Math.cos(t) * radius * aspect - cols / 2);
    const row = Math.round(originY + Math.sin(t) * radius - rows / 2);
    if (col < 0 || row < 0 || col + cols > gridWidth || row + rows > gridHeight) continue;
    if (free(taken, gridWidth, col, row, cols, rows)) return { col, row };
  }
}

function free(taken: Uint8Array, gridWidth: number, col: number, row: number, cols: number, rows: number): boolean {
  for (let r = 0; r < rows; r++) {
    const start = (row + r) * gridWidth + col;
    for (let c = 0; c < cols; c++) if (taken[start + c] !== 0) return false;
  }
  return true;
}

function fill(taken: Uint8Array, gridWidth: number, col: number, row: number, cols: number, rows: number): void {
  for (let r = 0; r < rows; r++) {
    const start = (row + r) * gridWidth + col;
    for (let c = 0; c < cols; c++) taken[start + c] = 1;
  }
}
