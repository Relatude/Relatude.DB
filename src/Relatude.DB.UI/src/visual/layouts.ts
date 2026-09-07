/**
 * Where the cards go. A layout is a position per card in world units - the pitch between cards is
 * one unit, so a card at (3, 2) sits in the fourth column of the third row - and the renderer takes
 * the cards there from wherever they are. Everything here is a typed-array pass over the cards, so a
 * layout of a million is milliseconds; nothing is ever made per card but two floats.
 *
 * y grows downward, the way a screen does: the grid reads top-left to bottom-right in result order,
 * and the bars stand on the line y = 0 and grow into negative y, so a bar is stacked upward.
 */

export interface Bounds {
  x0: number;
  y0: number;
  x1: number;
  y1: number;
}

/** One bar: the group it holds and where it stands. `top` is the y of its highest row (negative). */
export interface Bar {
  group: number;
  x0: number;
  x1: number;
  top: number;
  count: number;
}

export interface Layout {
  /** x, y per card */
  positions: Float32Array;
  bounds: Bounds;
  /** the bars, in the order they stand; null for the grid */
  bars: Bar[] | null;
}

/** A grid in result order, about as wide as the screen is: `aspect` is width over height. */
export function gridLayout(count: number, aspect: number): Layout {
  const columns = Math.max(1, Math.round(Math.sqrt(count * Math.max(0.2, aspect))));
  const positions = new Float32Array(count * 2);
  for (let i = 0; i < count; i++) {
    positions[i * 2] = i % columns;
    positions[i * 2 + 1] = Math.floor(i / columns);
  }
  const rows = Math.ceil(count / Math.max(1, columns));
  return { positions, bounds: { x0: 0, y0: 0, x1: Math.min(count, columns), y1: rows }, bars: null };
}

/**
 * One bar per group, side by side in group order, every bar the same number of cards wide - so a
 * bar's height is its count, and the bars together are a bar chart made of the very things it counts.
 * The width is chosen so the whole chart is about as wide as it is high times `aspect`.
 *
 * Within a bar the cards are laid in the order of `within` when given - the colour group - so that
 * bars by one property, coloured by another, come out as stacked bars: every bar is banded by the
 * second property, and the bands are in the same order in every bar. Cards in the same band keep
 * their result order.
 */
export function barLayout(count: number, groupOf: Uint16Array, groupCount: number, within: Uint16Array | null, aspect: number): Layout {
  const counts = new Int32Array(groupCount);
  for (let i = 0; i < count; i++) counts[groupOf[i]]++;
  // a stable counting sort by group: the cards of each bar, contiguous, in result order
  const starts = new Int32Array(groupCount + 1);
  for (let g = 0; g < groupCount; g++) starts[g + 1] = starts[g] + counts[g];
  const order = new Int32Array(count);
  const fill = starts.slice(0, groupCount);
  for (let i = 0; i < count; i++) order[fill[groupOf[i]]++] = i;
  if (within !== null && count > 0) sortWithin(order, starts, groupCount, within);

  let maxCount = 0;
  let bars = 0;
  for (let g = 0; g < groupCount; g++) {
    if (counts[g] === 0) continue;
    bars++;
    if (counts[g] > maxCount) maxCount = counts[g];
  }
  // (bars * width) / (maxCount / width) = aspect, solved for the width
  const width = Math.max(1, Math.ceil(Math.sqrt((Math.max(0.2, aspect) * maxCount) / Math.max(1, bars))));
  const gap = Math.max(1, Math.round(width * 0.35));
  const positions = new Float32Array(count * 2);
  const result: Bar[] = [];
  let x = 0;
  let top = 0;
  for (let g = 0; g < groupCount; g++) {
    const n = counts[g];
    if (n === 0) continue;
    const start = starts[g];
    for (let k = 0; k < n; k++) {
      const i = order[start + k];
      positions[i * 2] = x + (k % width);
      positions[i * 2 + 1] = -1 - Math.floor(k / width);
    }
    const rows = Math.ceil(n / width);
    if (-rows < top) top = -rows;
    result.push({ group: g, x0: x, x1: x + width, top: -rows, count: n });
    x += width + gap;
  }
  return { positions, bounds: { x0: 0, y0: top, x1: Math.max(1, x - gap), y1: 0 }, bars: result };
}

// each bar's segment of `order`, stably re-sorted by the second grouping
function sortWithin(order: Int32Array, starts: Int32Array, groupCount: number, within: Uint16Array) {
  let bands = 0;
  for (let i = 0; i < within.length; i++) if (within[i] >= bands) bands = within[i] + 1;
  const bandStarts = new Int32Array(bands + 1);
  const scratch = new Int32Array(order.length);
  for (let g = 0; g < groupCount; g++) {
    const s = starts[g];
    const e = starts[g + 1];
    if (e - s < 2) continue;
    bandStarts.fill(0);
    for (let k = s; k < e; k++) bandStarts[within[order[k]] + 1]++;
    for (let b = 0; b < bands; b++) bandStarts[b + 1] += bandStarts[b];
    for (let k = s; k < e; k++) scratch[s + bandStarts[within[order[k]]]++] = order[k];
    order.set(scratch.subarray(s, e), s);
  }
}
