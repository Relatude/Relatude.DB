import { useCallback, useEffect, useRef, useState } from "react";

/**
 * Spread onto the two fillers that stand in for the rows outside the window. Both stay in the dom
 * with a height of zero when there is nothing to stand in for: the front one is also how the window
 * finds where the rows begin, below whatever fixed heading or padding the scroller has of its own.
 */
export const windowPad = { "data-window-pad": "" } as const;

const padSelector = "[data-window-pad]";
const firstBatch = 40; // rows built before anything has been measured: enough to measure one of them

/** The slice of a long list that is actually built, and the height of what stands in for the rest. */
export interface VirtualWindow {
  /** the first item to build */
  first: number;
  /** one past the last item to build */
  last: number;
  /** how many items sit side by side: 1 down a list, the column count across a grid */
  perRow: number;
  /** the height of the filler in front of the built items, in pixels */
  above: number;
  /** the height of the filler after them */
  below: number;
  /** put on the scrolling element, alongside whatever ref the caller keeps of it */
  ref: (element: HTMLElement | null) => void;
  /** bring the item into view the short way, as `scrollIntoView({ block: "nearest" })` would */
  reveal: (index: number) => void;
}

export interface VirtualWindowOptions {
  /** how many items the list holds */
  count: number;
  /** a css selector matching one built item, so a row can be measured */
  item: string;
  /** anything that changes when the shape of a row does - the view mode, say - which invalidates what was measured */
  layout?: unknown;
  /** rows built beyond the screen either way, so a scroll of a line or two has nothing to wait for */
  overscan?: number;
}

interface Metrics {
  perRow: number; // items side by side
  stride: number; // from the top of one row to the top of the next, the gap included
  origin: number; // where the rows begin within the scroller's content
}

const unmeasured: Metrics = { perRow: 1, stride: 0, origin: 0 };

/**
 * The window of a long list: the rows on screen, a few either side, and a filler standing in for
 * everything else.
 *
 * A listing that reaches into the subfolders can be tens of thousands of files, and a row of one is
 * a handful of elements - a checkbox, an icon, four cells, two buttons - while a thumbnail tile is
 * a picture as well. Building them all is what makes such a folder slow: not the pictures, which
 * only the tiles on screen ever ask for, but the dom and the react tree behind it, which every
 * selection, sort and filter then has to walk again.
 *
 * So only the rows that can be seen are built. Their height is measured off a real one rather than
 * assumed, the rows above and below are two empty divs of the height they would have taken, and the
 * scrollbar therefore says what it would have said and the scroll position means what it meant. The
 * window follows the scroller, so what is under the eye is always built; `reveal` moves the scroller
 * instead, for a cursor walking onto a row that is not.
 *
 * Nothing here knows what a row looks like: `item` is how it finds one to measure, `windowPad` marks
 * the fillers the caller renders, and a grid is spotted by its computed column list, which is what
 * `perRow` reports for a keyboard stepping down a grid rather than along a list.
 */
export function useVirtualWindow({ count, item, layout = null, overscan = 4 }: VirtualWindowOptions): VirtualWindow {
  const [node, setNode] = useState<HTMLElement | null>(null);
  const [top, setTop] = useState(0);
  const [size, setSize] = useState({ width: 0, height: 0 });
  const [metrics, setMetrics] = useState<Metrics>(unmeasured);
  const measured = useRef<Metrics>(unmeasured); // the same, for the handlers: reveal is called between renders

  const ref = useCallback((element: HTMLElement | null) => setNode(element), []);

  // where the scroller is and how big it is: between them, which rows are on screen
  useEffect(() => {
    if (!node) return;
    const readTop = () => setTop(node.scrollTop);
    const readSize = () =>
      setSize((prev) => (prev.width === node.clientWidth && prev.height === node.clientHeight ? prev : { width: node.clientWidth, height: node.clientHeight }));
    readTop();
    readSize();
    node.addEventListener("scroll", readTop, { passive: true });
    // a narrower list is fewer tiles across and a shorter one is fewer rows on screen: both are
    // measured again, which is why the width is watched and not only the height
    const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(readSize);
    observer?.observe(node);
    return () => {
      node.removeEventListener("scroll", readTop);
      observer?.disconnect();
    };
  }, [node]);

  // What a row costs in height, and where the rows start. Measured after the rows are in the dom,
  // and again whenever something that could have changed either did: the width, the view, the list.
  // A reading that comes to nothing - an empty list, a hidden panel - leaves the last one standing,
  // since a stride of zero would build the whole list and lose the scroll position with it.
  useEffect(() => {
    if (!node) return;
    const next = readMetrics(node, item);
    if (next.stride <= 0) return;
    const was = measured.current;
    if (was.perRow === next.perRow && was.stride === next.stride && was.origin === next.origin) return;
    measured.current = next;
    setMetrics(next);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- layout and count are not read here; they are what says the measurement may be stale
  }, [node, item, layout, count, size.width, size.height]);

  const reveal = useCallback(
    (index: number) => {
      const { perRow, stride, origin } = measured.current;
      if (!node || stride <= 0 || index < 0) return;
      const rowTop = origin + Math.floor(index / perRow) * stride;
      // going up, the row stops where the rows begin rather than at the very top of the scroller:
      // that is where a fixed heading ends, and it would otherwise be standing over the row
      if (rowTop < node.scrollTop + origin) node.scrollTop = Math.max(0, rowTop - origin);
      else if (rowTop + stride > node.scrollTop + node.clientHeight) node.scrollTop = rowTop + stride - node.clientHeight;
    },
    [node],
  );

  const { perRow, stride, origin } = metrics;
  const rows = Math.ceil(count / perRow);
  let firstRow = 0;
  let builtRows = Math.min(rows, firstBatch);
  if (stride > 0 && size.height > 0) {
    builtRows = Math.min(rows, Math.ceil(size.height / stride) + 1 + 2 * overscan);
    firstRow = Math.floor((top - origin) / stride) - overscan;
    firstRow = Math.max(0, Math.min(firstRow, rows - builtRows));
  }
  return {
    first: firstRow * perRow,
    last: Math.min(count, (firstRow + builtRows) * perRow),
    perRow,
    above: firstRow * stride,
    below: Math.max(0, rows - firstRow - builtRows) * stride,
    ref,
    reveal,
  };
}

/**
 * Reads the row geometry off the scroller as it stands. The height comes from a built row rather
 * than a constant, so a change of font, of density or of tile size needs nothing here; the gap comes
 * from the computed style, and is `normal` - so nothing - outside a grid.
 */
function readMetrics(node: HTMLElement, item: string): Metrics {
  const row = node.querySelector<HTMLElement>(item);
  const pad = node.querySelector<HTMLElement>(padSelector);
  if (!row || !pad) return unmeasured;
  const height = row.getBoundingClientRect().height;
  if (height <= 0) return unmeasured; // nothing laid out: a hidden panel, or a row still to come
  const style = getComputedStyle(node);
  // the track list holds one entry per column, whatever the container turned out to be wide
  const columns = style.display.endsWith("grid") ? style.gridTemplateColumns.split(" ").filter((track) => track !== "").length : 1;
  return {
    perRow: Math.max(1, columns),
    stride: height + (parseFloat(style.rowGap) || 0),
    origin: pad.getBoundingClientRect().top - node.getBoundingClientRect().top + node.scrollTop,
  };
}
