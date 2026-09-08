/**
 * Where the cards go. A layout is a position per card in world units - the pitch between cards is
 * one unit, so a card at (3, 2) sits in the fourth column of the third row - and the renderer takes
 * the cards there from wherever they are. Everything here is a typed-array pass over the cards, so a
 * layout of a million is milliseconds; nothing is ever made per card but two floats.
 *
 * y grows downward, the way a screen does: the grid reads top-left to bottom-right in card order,
 * and the bars stand on the line y = 0 and grow into negative y, so a bar is stacked upward.
 *
 * A third grouping lays the picture in rows one behind another along z (`DepthGrouping`), which only
 * the three-dimensional renderer draws: bars by one property across, rows by another into the
 * distance, and the same stack of colours up each of them. z runs from 0 at the row nearest the
 * viewer into negative numbers behind it, so the first group of the property is the front row and
 * the picture reads front to back in group order.
 *
 * The card order is the result's own unless an `order` is given: a permutation of the card indexes,
 * the cards as sorted by some property, which the grid follows and every bar follows from its foot.
 */

export interface Bounds {
  x0: number;
  y0: number;
  x1: number;
  y1: number;
  /** how far the rows reach along z, when the picture is laid in rows; absent when it lies in one plane */
  z0?: number;
  z1?: number;
}

/** One bar: the group it holds and where it stands. `top` is the y of its highest row (negative). */
export interface Bar {
  group: number;
  x0: number;
  x1: number;
  top: number;
  count: number;
}

/** One row of the depth grouping: the group it holds, the z its cards stand on, and how many they are. */
export interface Row {
  group: number;
  z: number;
  count: number;
}

/**
 * How much room the rows are given beyond the cards themselves, as a multiple of how deep a row is.
 * A chart of solids reads as a chart of solids only if the groups stand well clear of one another:
 * blocks pushed together are one block, and the lines on the floor have nowhere to show.
 */
const rowGapShare = 5;

/**
 * A grouping laid into the distance: which row every card belongs to, and how deep the thickest card
 * of the picture is. How far apart the rows stand is worked out here rather than handed in, because
 * it depends on how large the layout turns out to be - rows a couple of cells apart are invisible in
 * a chart whose bars are a hundred cells wide - but a row must always be at least `clearance` clear
 * of the one in front of it, or the cards standing on it would grow through their neighbours.
 */
export interface DepthGrouping {
  groupOf: Uint16Array;
  groupCount: number;
  clearance: number;
}

export interface Layout {
  /** x, y per card */
  positions: Float32Array;
  /** the z of every card, or null when the picture lies in one plane */
  rows: Float32Array | null;
  bounds: Bounds;
  /** the bars, in the order they stand; null for the grid */
  bars: Bar[] | null;
  /** the rows, nearest the viewer first; null when the picture lies in one plane */
  rowSlots: Row[] | null;
  /** how many cells deep one row is: what its middle is measured from, for the lines on the floor */
  rowDepth: number;
}

/**
 * Where each row stands: the front of the first row is z = 0 and the rest are `pitch` behind one
 * another, so the picture reads front to back in group order. Groups with nothing in them take no
 * room at all - a row of empty air in the middle of a chart would read as a value that had none.
 */
function rowPlaces(depth: DepthGrouping, totals: Int32Array, pitch: number): { zOf: Float32Array; slots: Row[]; used: number } {
  const zOf = new Float32Array(depth.groupCount);
  const slots: Row[] = [];
  let placed = 0;
  for (let g = 0; g < depth.groupCount; g++) {
    if (totals[g] === 0) continue;
    zOf[g] = -placed * pitch;
    slots.push({ group: g, z: zOf[g], count: totals[g] });
    placed++;
  }
  return { zOf, slots, used: placed };
}

/** How many rows a grouping actually fills: what the spacing has to be worked out against. */
function rowsUsed(depth: DepthGrouping, totals: Int32Array): number {
  let used = 0;
  for (let g = 0; g < depth.groupCount; g++) if (totals[g] > 0) used++;
  return used;
}

/**
 * A grid in card order, about as wide as the screen is: `aspect` is width over height. With a depth
 * grouping it becomes one grid per row, laid one behind another - a stack of contact sheets - all
 * with the same number of columns so that they line up.
 */
export function gridLayout(count: number, aspect: number, order: Int32Array | null = null, depth: DepthGrouping | null = null): Layout {
  const positions = new Float32Array(count * 2);
  if (depth === null) {
    const columns = Math.max(1, Math.round(Math.sqrt(count * Math.max(0.2, aspect))));
    for (let k = 0; k < count; k++) {
      const i = order === null ? k : order[k];
      positions[i * 2] = k % columns;
      positions[i * 2 + 1] = Math.floor(k / columns);
    }
    const high = Math.ceil(count / Math.max(1, columns));
    return { positions, rows: null, bounds: { x0: 0, y0: 0, x1: Math.min(count, columns), y1: high }, bars: null, rowSlots: null, rowDepth: 1 };
  }
  const totals = new Int32Array(depth.groupCount);
  for (let i = 0; i < count; i++) totals[depth.groupOf[i]]++;
  let biggest = 0;
  for (let g = 0; g < depth.groupCount; g++) if (totals[g] > biggest) biggest = totals[g];
  // the widest row sets the columns, so every row is as wide as the screen at most and none of them
  // is laid to a different shape than its neighbours
  const columns = Math.max(1, Math.round(Math.sqrt(biggest * Math.max(0.2, aspect))));
  // The rows are a share of the grid's own width apart - a fixed few cells would be nothing at all
  // between two sheets seven hundred cells wide - and the whole stack is held to a few times the
  // width of one sheet, so a property with fifty values does not become an endless tunnel.
  const used = rowsUsed(depth, totals);
  const pitch = Math.max(Math.ceil(depth.clearance) + 2, Math.min(Math.round(columns * 0.48), Math.round((columns * 3.6) / Math.max(1, used - 1))));
  const { zOf, slots } = rowPlaces(depth, totals, pitch);
  const rows = new Float32Array(count);
  const filled = new Int32Array(depth.groupCount);
  for (let k = 0; k < count; k++) {
    const i = order === null ? k : order[k];
    const g = depth.groupOf[i];
    const n = filled[g]++;
    positions[i * 2] = n % columns;
    positions[i * 2 + 1] = Math.floor(n / columns);
    rows[i] = zOf[g];
  }
  const high = Math.ceil(biggest / columns);
  const back = slots.length > 0 ? slots[slots.length - 1].z : 0;
  return { positions, rows, bounds: { x0: 0, y0: 0, x1: Math.min(biggest, columns), y1: high, z0: back, z1: 0 }, bars: null, rowSlots: slots, rowDepth: 1 };
}

/**
 * One bar per group, side by side in group order, every bar the same number of cards wide - so a
 * bar's height is its count, and the bars together are a bar chart made of the very things it counts.
 * The width is chosen so the whole chart is about as wide as it is high times `aspect`.
 *
 * Within a bar the cards are laid in the order of `within` when given - the colour group - so that
 * bars by one property, coloured by another, come out as stacked bars: every bar is banded by the
 * second property, and the bands are in the same order in every bar. Cards in the same band keep
 * the card order.
 *
 * With a depth grouping the chart gains a second axis: every bar becomes one wall per row, standing
 * one card deep at that row's z, so the picture is a bar chart per row laid one behind another with
 * the bars of each lined up. The walls are all the same width - it is the width of the largest of
 * them that sets it - which is what makes two walls comparable by eye across the picture as well as
 * along it.
 */
export function barLayout(
  count: number,
  groupOf: Uint16Array,
  groupCount: number,
  within: Uint16Array | null,
  aspect: number,
  cardOrder: Int32Array | null = null,
  depth: DepthGrouping | null = null,
): Layout {
  // a card belongs to one cell of the (bar, row) grid; with no depth grouping there is one row and
  // a cell is a bar, which is the two-dimensional chart unchanged
  const rowCount = depth === null ? 1 : Math.max(1, depth.groupCount);
  const cells = groupCount * rowCount;
  const cellOf = (i: number) => groupOf[i] * rowCount + (depth === null ? 0 : depth.groupOf[i]);
  const counts = new Int32Array(cells);
  for (let i = 0; i < count; i++) counts[cellOf(i)]++;
  // a stable counting sort by cell: the cards of each wall, contiguous, in card order
  const starts = new Int32Array(cells + 1);
  for (let c = 0; c < cells; c++) starts[c + 1] = starts[c] + counts[c];
  const order = new Int32Array(count);
  const fill = starts.slice(0, cells);
  for (let k = 0; k < count; k++) {
    const i = cardOrder === null ? k : cardOrder[k];
    order[fill[cellOf(i)]++] = i;
  }
  if (within !== null && count > 0) sortWithin(order, starts, cells, within);

  const barTotals = new Int32Array(groupCount);
  const rowTotals = new Int32Array(rowCount);
  let biggest = 0;
  for (let g = 0; g < groupCount; g++) {
    for (let r = 0; r < rowCount; r++) {
      const n = counts[g * rowCount + r];
      barTotals[g] += n;
      rowTotals[r] += n;
      if (n > biggest) biggest = n;
    }
  }
  let bars = 0;
  for (let g = 0; g < groupCount; g++) if (barTotals[g] > 0) bars++;
  const used = depth === null ? 1 : rowsUsed(depth, rowTotals);

  /**
   * How large a footprint one wall of the chart stands on. Flat, a wall is a line of cards `width`
   * across and the height is what is left: (bars * width) / (biggest / width) = aspect, solved for
   * the width. With rows it is a block `side` by `side` instead, chosen so that the tallest of them
   * is about as high as the chart is wide or deep - which is what makes a bar read as a bar rather
   * than as a tower a hundred cards high on a footprint of one, or as a slab an inch tall.
   */
  const width = depth === null ? Math.max(1, Math.ceil(Math.sqrt((Math.max(0.2, aspect) * biggest) / Math.max(1, bars)))) : Math.max(1, Math.round(Math.cbrt(biggest / (1.08 * Math.max(1, bars, used)))));
  const deep = depth === null ? 1 : width;
  const gap = Math.max(1, Math.round(width * 0.35));
  // behind each block, several block-depths of empty floor - which is what makes a row a row rather
  // than part of the block in front of it - and never less than the thickest card standing at the
  // front of the next one, which grows toward the viewer
  const pitch = depth === null ? 1 : deep + Math.max(Math.ceil(depth.clearance) + 1, Math.round(deep * rowGapShare));
  const places = depth === null ? null : rowPlaces(depth, rowTotals, pitch);
  const positions = new Float32Array(count * 2);
  const rows = depth === null ? null : new Float32Array(count);
  const result: Bar[] = [];
  const layer = width * deep;
  let x = 0;
  let top = 0;
  for (let g = 0; g < groupCount; g++) {
    if (barTotals[g] === 0) continue;
    let barTop = 0;
    for (let r = 0; r < rowCount; r++) {
      const n = counts[g * rowCount + r];
      if (n === 0) continue;
      const start = starts[g * rowCount + r];
      const front = places === null ? 0 : places.zOf[r];
      // a block is filled a layer at a time, across and then back, and the layers stack upward: the
      // bands of the colour grouping come out as slabs one on top of another, the way a stacked bar
      // chart has them, whichever side of the block is being looked at
      for (let k = 0; k < n; k++) {
        const i = order[start + k];
        const within = k % layer;
        positions[i * 2] = x + (within % width);
        positions[i * 2 + 1] = -1 - Math.floor(k / layer);
        if (rows !== null) rows[i] = front - Math.floor(within / width);
      }
      const high = Math.ceil(n / layer);
      if (-high < barTop) barTop = -high;
    }
    if (barTop < top) top = barTop;
    // the bar's own count is the whole of its group; the label under it names the group, not one row
    result.push({ group: g, x0: x, x1: x + width, top: barTop, count: barTotals[g] });
    x += width + gap;
  }
  const bounds: Bounds = { x0: 0, y0: top, x1: Math.max(1, x - gap), y1: 0 };
  if (places !== null) {
    // the rows run back from z = 0, and the last of them is `deep` cells thick
    bounds.z0 = (places.slots.length > 0 ? places.slots[places.slots.length - 1].z : 0) - (deep - 1);
    bounds.z1 = 0;
  }
  return { positions, rows, bounds, bars: result, rowSlots: places === null ? null : places.slots, rowDepth: deep };
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
