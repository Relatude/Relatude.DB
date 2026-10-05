import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { ReactNode } from "react";
import { IconArrowsMaximize, IconArrowsMinimize } from "@tabler/icons-react";
import { PanelMaximizedContext } from "../panelMaximized";

/**
 * Panels in rows the reader can resize.
 *
 * The grid has exactly two columns and one split between them, shared by every row that has two
 * panels. That is deliberate: with one split the vertical dividers of all such rows line up into a
 * single line down the page, so where that line crosses a horizontal divider there is a real corner
 * to grab - which is the only way a corner can resize in both directions at once. Rows with a single
 * panel span the width and simply break the line where they sit.
 *
 * Widths are shares of the row, never pixels, so the panels fill the window at any size and keep
 * filling it as the window changes: dragging a vertical divider moves the boundary, it never leaves
 * a gap or overflows. Heights are pixels, and a horizontal divider always sizes the row above it -
 * the page scrolls, so there is no total height to conserve, and a row nobody has touched keeps
 * sizing itself to its content. There is a divider under the last row too, or the bottom panel of a
 * page would be the one thing on it with no height of its own.
 *
 * Below `narrowAt` the columns are gone and so is the whole idea: the panels stack in order and
 * nothing is resizable, because there is no second column to take room from.
 *
 * Any panel can be taken to the whole page for a while - a graph to read closely, a terminal to
 * follow. It covers the scrolling area the grid lives in rather than the window, so the rail and the
 * header stay in reach; the other panels are hidden where they are and shown again on the way back,
 * which is a matter of a click on the same button or Escape.
 *
 * When a page swaps its rows for others - the dashboard does when its database opens or closes - the
 * panels that are going fade out first, together, drifting up a little; then the new rows take their
 * place and their panels fade in from a little below, each a moment after the one before it. The
 * panels a page opened with are simply there, and a panel that is in both sets stays and carries on.
 */

export interface PanelRow {
  /** Stable: the saved heights are keyed by it, so renaming a row forgets its height. */
  id: string;
  /** One panel spans the width; two share it at the grid's split. */
  cells: ReactNode[];
  /** The height in pixels before anyone drags it. Unset lets the content decide. */
  height?: number;
}

interface Layout {
  split: number;
  heights: Record<string, number>;
}

/** The gap between panels, and the track each divider lives in. */
const barSize = 14;
/** No column may be dragged below this share of the row. */
const minShare = 0.18;
const minHeight = 90;
const maxHeight = 2400;
/** Under this the grid is one column and nothing is resizable. */
const narrowAt = 760;
const keyStep = 0.02;
const keyStepPx = 16;
/** how much later each newly arrived panel starts its fade than the one before it */
const arriveStaggerMs = 120;
/** how long the panels that are going take to fade out before the new ones take their place (the
    css animation, pg-leave, is a little shorter, so they are gone when the swap happens) */
const leaveMs = 560;

/** the key of each cell, in order: a row's first panel and, when it has one, its second */
function cellKeys(rows: PanelRow[]): string[] {
  return rows.flatMap((row) => row.cells.map((_, i) => row.id + (i === 0 ? ":a" : ":b")));
}

function reducedMotion(): boolean {
  return typeof window !== "undefined" && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
}

type DragMode = "col" | "row" | "both";

export function PanelGrid({ id, rows: incoming, defaultSplit = 0.62 }: { id: string; rows: PanelRow[]; defaultSplit?: number }) {
  const storageKey = "panelGrid:" + id;

  // The rows on screen are the ones handed in, except while panels that are going fade out: then the
  // grid keeps showing the rows they were in for `leaveMs` - as they last were, since their page has
  // already moved on - and only then takes the new ones. A cell that is in both sets is never
  // frozen: it shows what it is handed throughout. Handed yet other rows meanwhile, the grid simply
  // takes the latest when the fade is over.
  const incomingSignature = cellKeys(incoming).join("|");
  const [signature, setSignature] = useState(incomingSignature);
  const [outgoing, setOutgoing] = useState<PanelRow[] | null>(null);
  const lastShown = useRef<PanelRow[]>(incoming);
  if (incomingSignature !== signature) {
    // adjusting state to a changed prop during the render, before anything is drawn
    setSignature(incomingSignature);
    if (outgoing === null && !reducedMotion()) {
      const staying = new Set(cellKeys(incoming));
      if (cellKeys(lastShown.current).some((key) => !staying.has(key))) setOutgoing(lastShown.current);
    }
  }
  useEffect(() => {
    if (outgoing === null) lastShown.current = incoming;
  });
  useEffect(() => {
    if (outgoing === null) return;
    const timer = setTimeout(() => setOutgoing(null), leaveMs);
    return () => clearTimeout(timer);
  }, [outgoing]);
  const incomingKeys = new Set(cellKeys(incoming));
  const incomingCell = (rowId: string, i: number) => incoming.find((row) => row.id === rowId)?.cells[i];
  const rows: PanelRow[] = outgoing
    ? outgoing.map((row) => ({ ...row, cells: row.cells.map((content, i) => (incomingKeys.has(row.id + (i === 0 ? ":a" : ":b")) ? (incomingCell(row.id, i) ?? content) : content)) }))
    : incoming;

  const [layout, setLayout] = useState<Layout>(() => read(storageKey, defaultSplit));
  const [dragging, setDragging] = useState<DragMode | null>(null);
  // the cell taken to the whole page, by its key, and the area it covers
  const [maximized, setMaximized] = useState<string | null>(null);
  const [maxRect, setMaxRect] = useState<{ top: number; left: number; width: number; height: number } | null>(null);
  const [narrow, setNarrow] = useState(false);
  const box = useRef<HTMLDivElement>(null);
  const rowEls = useRef<(HTMLDivElement | null)[]>([]);
  const drag = useRef<{ pointerId: number; mode: DragMode; row: number; x: number; y: number; split: number; height: number; free: number } | null>(null);
  // every cell on the grid, with where it came in the batch it arrived in; -1 for the ones the grid
  // started with, which do not fade in. A cell that leaves is forgotten, so coming back is arriving.
  const arrived = useRef<Map<string, number> | null>(null);

  // measured rather than asked of the window: the grid is what has to fit, and the rail beside it
  // collapses on its own
  useLayoutEffect(() => {
    const element = box.current;
    if (!element) return;
    const measure = () => setNarrow(element.clientWidth < narrowAt);
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    measure();
    return () => observer.disconnect();
  }, []);

  // written after the drag has settled rather than on every move: a pointer produces a hundred of
  // these a second and none of them is the one worth keeping
  useEffect(() => {
    const timer = window.setTimeout(() => {
      try {
        localStorage.setItem(storageKey, JSON.stringify(layout));
      } catch {
        // storage unavailable: the layout just won't outlive the tab
      }
    }, 200);
    return () => clearTimeout(timer);
  }, [storageKey, layout]);

  // the cursor has to belong to the whole page while a divider is being dragged: the pointer leaves
  // the bar the moment it moves, and a text selection dragged along with it makes a mess of the page
  useEffect(() => {
    if (!dragging) return;
    const previous = document.body.style.cursor;
    document.body.style.cursor = dragging === "col" ? "col-resize" : dragging === "row" ? "row-resize" : "nwse-resize";
    document.body.classList.add("pg-resizing");
    return () => {
      document.body.style.cursor = previous;
      document.body.classList.remove("pg-resizing");
    };
  }, [dragging]);

  // the area the maximized panel covers is the nearest scrolling ancestor - the page's content, not
  // the window. Measured again as the window or the rail beside it changes, so it keeps fitting.
  useLayoutEffect(() => {
    if (!maximized) return;
    const scroller = scrollParentOf(box.current);
    const measure = () => {
      const r = scroller?.getBoundingClientRect();
      setMaxRect(r ? { top: r.top, left: r.left, width: r.width, height: r.height } : { top: 0, left: 0, width: window.innerWidth, height: window.innerHeight });
    };
    measure();
    const observer = new ResizeObserver(measure);
    if (scroller) observer.observe(scroller);
    window.addEventListener("resize", measure);
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setMaximized(null);
    };
    window.addEventListener("keydown", onKey);
    return () => {
      observer.disconnect();
      window.removeEventListener("resize", measure);
      window.removeEventListener("keydown", onKey);
    };
  }, [maximized]);

  function onPointerDown(e: React.PointerEvent<HTMLDivElement>, mode: DragMode, row: number) {
    if (e.button !== 0) return;
    const width = box.current?.clientWidth ?? 0;
    if (width <= 0) return;
    e.preventDefault();
    drag.current = {
      pointerId: e.pointerId,
      mode,
      row,
      x: e.clientX,
      y: e.clientY,
      split: layout.split,
      // whatever the row is now, however it got that height: dragging continues from what is on
      // screen rather than jumping to a remembered number
      height: rowEls.current[row]?.getBoundingClientRect().height ?? minHeight,
      free: Math.max(1, width - barSize),
    };
    e.currentTarget.setPointerCapture(e.pointerId);
    setDragging(mode);
  }

  function onPointerMove(e: React.PointerEvent<HTMLDivElement>) {
    const d = drag.current;
    if (!d || e.pointerId !== d.pointerId) return;
    setLayout((previous) => {
      const next: Layout = { split: previous.split, heights: { ...previous.heights } };
      if (d.mode !== "row") next.split = clamp(d.split + (e.clientX - d.x) / d.free, minShare, 1 - minShare);
      if (d.mode !== "col") next.heights[rows[d.row].id] = clamp(d.height + (e.clientY - d.y), minHeight, maxHeight);
      return next;
    });
  }

  function onPointerUp(e: React.PointerEvent<HTMLDivElement>) {
    if (drag.current?.pointerId !== e.pointerId) return;
    e.currentTarget.releasePointerCapture(e.pointerId);
    drag.current = null;
    setDragging(null);
  }

  /** Back to the way it was laid out: the split, the row's own height, or both. */
  function reset(mode: DragMode, row: number) {
    setLayout((previous) => {
      const next: Layout = { split: previous.split, heights: { ...previous.heights } };
      if (mode !== "row") next.split = defaultSplit;
      if (mode !== "col") delete next.heights[rows[row].id];
      return next;
    });
  }

  function onKeyDown(e: React.KeyboardEvent<HTMLDivElement>, mode: DragMode, row: number) {
    const horizontal = e.key === "ArrowLeft" ? -1 : e.key === "ArrowRight" ? 1 : 0;
    const vertical = e.key === "ArrowUp" ? -1 : e.key === "ArrowDown" ? 1 : 0;
    if (e.key === "Enter" || e.key === "Home") {
      e.preventDefault();
      reset(mode, row);
      return;
    }
    if ((horizontal === 0 || mode === "row") && (vertical === 0 || mode === "col")) return;
    e.preventDefault();
    const current = rowEls.current[row]?.getBoundingClientRect().height ?? minHeight;
    setLayout((previous) => {
      const next: Layout = { split: previous.split, heights: { ...previous.heights } };
      if (horizontal !== 0 && mode !== "row") next.split = clamp(previous.split + horizontal * keyStep, minShare, 1 - minShare);
      if (vertical !== 0 && mode !== "col") next.heights[rows[row].id] = clamp(current + vertical * keyStepPx, minHeight, maxHeight);
      return next;
    });
  }

  // a row is "sized" once it has a height of its own, dragged or given: a panel there has room to
  // hand to a body that wants to fill it, which a row sizing itself to its content does not. A
  // maximized panel has the whole page, so it is sized whatever its row is.
  const heightOf = (row: PanelRow) => (narrow ? undefined : layout.heights[row.id] ?? row.height);
  const cellClass = (row: PanelRow, isMax: boolean) => "panel-cell" + (isMax || heightOf(row) != null ? " sized" : "") + (isMax ? " maximized" : "");

  // Which cells are new since the last render, worked out while rendering so the class is there on
  // the element's first frame - an animation added a frame later would show the panel and then
  // start it from nothing. Rendering twice over (strict mode) finds the same answer the second time.
  const keysNow = cellKeys(rows);
  const starting = arrived.current === null;
  const known = arrived.current ?? new Map<string, number>();
  let batch = 0;
  for (const key of keysNow) if (!known.has(key)) known.set(key, starting ? -1 : batch++);
  for (const key of [...known.keys()]) if (!keysNow.includes(key)) known.delete(key);
  arrived.current = known;

  // a maximized panel that has gone (its page swapped it out) takes the maximizing with it: kept,
  // it would leave every other panel hidden behind a panel that is no longer there
  const maxKey = maximized !== null && keysNow.includes(maximized) ? maximized : null;
  useEffect(() => {
    if (maximized !== null && maxKey === null) setMaximized(null);
  }, [maximized, maxKey]);

  // a cell of the grid: the panel it was handed, and the button that takes it to the whole page
  const cell = (key: string, row: PanelRow, content: ReactNode, style?: React.CSSProperties, ref?: (el: HTMLDivElement | null) => void) => {
    const isMax = maxKey === key;
    const leaving = outgoing !== null && !incomingKeys.has(key);
    const order = leaving ? -1 : (known.get(key) ?? -1);
    const placed = isMax && maxRect ? { position: "fixed" as const, ...maxRect } : style;
    return (
      <div
        key={key}
        className={cellClass(row, isMax) + (leaving ? " pg-leave" : order >= 0 ? " pg-enter" : "")}
        aria-hidden={leaving || undefined}
        ref={ref}
        style={order > 0 ? { ...placed, animationDelay: order * arriveStaggerMs + "ms" } : placed}
        // arrived: from now on it is one of the panels that were there, so a cell the grid builds
        // again (the stacked layout and the columns are different elements) does not fade in twice
        onAnimationEnd={(e) => {
          if (e.target === e.currentTarget && e.animationName === "pg-enter") arrived.current?.set(key, -1);
        }}
      >
        <button
          className="icon-button pg-max"
          title={isMax ? "Back to the layout (Esc)" : "Maximize this panel"}
          aria-pressed={isMax}
          onClick={() => setMaximized(isMax ? null : key)}
        >
          {isMax ? <IconArrowsMinimize size={14} stroke={1.8} /> : <IconArrowsMaximize size={14} stroke={1.8} />}
        </button>
        <PanelMaximizedContext.Provider value={isMax}>{content}</PanelMaximizedContext.Provider>
      </div>
    );
  };

  if (narrow) {
    // one column: the cells in the order they were given, nothing to drag
    return (
      <div className={"panel-grid-wrap narrow" + (maxKey ? " has-max" : "")} ref={box}>
        {rows.flatMap((row) => row.cells.map((content, i) => cell(row.id + (i === 0 ? ":a" : ":b"), row, content)))}
      </div>
    );
  }

  const children: ReactNode[] = [];
  rows.forEach((row, i) => {
    const gridRow = 2 * i + 1;
    const split = row.cells.length > 1;
    children.push(cell(row.id + ":a", row, row.cells[0], { gridRow, gridColumn: split ? 1 : "1 / -1" }, (el) => void (rowEls.current[i] = el)));
    if (split) {
      children.push(
        <Divider
          key={row.id + ":v"}
          className="pg-vbar"
          label="Resize the columns"
          active={dragging === "col"}
          style={{ gridRow, gridColumn: 2 }}
          onPointerDown={(e) => onPointerDown(e, "col", i)}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onDoubleClick={() => reset("col", i)}
          onKeyDown={(e) => onKeyDown(e, "col", i)}
        />,
      );
      children.push(cell(row.id + ":b", row, row.cells[1], { gridRow, gridColumn: 3 }));
    }
    // every row has a divider under it, the last one included: the rule is that a horizontal
    // divider sizes the row above it, and without this one the bottom panel of a page could never
    // be given a height at all
    const barRow = gridRow + 1;
    children.push(
      <Divider
        key={row.id + ":h"}
        className="pg-hbar"
        label="Resize the row above"
        active={dragging === "row"}
        style={{ gridRow: barRow, gridColumn: "1 / -1" }}
        onPointerDown={(e) => onPointerDown(e, "row", i)}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onDoubleClick={() => reset("row", i)}
        onKeyDown={(e) => onKeyDown(e, "row", i)}
      />,
    );
    // a corner only exists where the column line runs through the rows on both sides of the
    // divider; over a full-width panel, or under the last row, there is no boundary to move sideways
    if (split && rows[i + 1]?.cells.length > 1) {
      children.push(
        <Divider
          key={row.id + ":c"}
          className="pg-corner"
          label="Resize in both directions"
          active={dragging === "both"}
          style={{ gridRow: barRow, gridColumn: 2 }}
          onPointerDown={(e) => onPointerDown(e, "both", i)}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onDoubleClick={() => reset("both", i)}
          onKeyDown={(e) => onKeyDown(e, "both", i)}
        />,
      );
    }
  });

  const templateRows = rows
    .map((row) => {
      const height = heightOf(row);
      return height == null ? "auto" : height + "px";
    })
    .flatMap((height) => [height, barSize + "px"])
    .join(" ");

  return (
    <div className={"panel-grid-wrap" + (dragging ? " dragging" : "") + (maxKey ? " has-max" : "")} ref={box}>
      <div
        className="panel-grid"
        style={{ gridTemplateColumns: `minmax(0, ${layout.split}fr) ${barSize}px minmax(0, ${1 - layout.split}fr)`, gridTemplateRows: templateRows }}
      >
        {children}
      </div>
    </div>
  );
}

function Divider({
  className,
  label,
  active,
  ...rest
}: {
  className: string;
  label: string;
  active: boolean;
} & React.HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={"pg-bar " + className + (active ? " active" : "")}
      role="separator"
      tabIndex={0}
      aria-label={label}
      title={label + " — drag, or double-click to reset"}
      {...rest}
    />
  );
}

function clamp(value: number, low: number, high: number): number {
  return Math.min(high, Math.max(low, value));
}

function read(storageKey: string, defaultSplit: number): Layout {
  try {
    const saved = localStorage.getItem(storageKey);
    if (saved) {
      const parsed = JSON.parse(saved) as Partial<Layout>;
      const split = typeof parsed.split === "number" && isFinite(parsed.split) ? clamp(parsed.split, minShare, 1 - minShare) : defaultSplit;
      const heights: Record<string, number> = {};
      // a saved height from a version that laid the page out differently is still a number, but a
      // nonsense one; the clamp is what keeps a row from being restored as a sliver
      for (const [key, value] of Object.entries(parsed.heights ?? {})) {
        if (typeof value === "number" && isFinite(value)) heights[key] = clamp(value, minHeight, maxHeight);
      }
      return { split, heights };
    }
  } catch {
    // unreadable or unparsable: the default layout is a fine answer
  }
  return { split: defaultSplit, heights: {} };
}

/** The nearest ancestor that scrolls: the area a maximized panel has to cover. */
function scrollParentOf(el: HTMLElement | null): HTMLElement | null {
  for (let node = el?.parentElement ?? null; node; node = node.parentElement) {
    const overflow = getComputedStyle(node).overflowY;
    if (overflow === "auto" || overflow === "scroll") return node;
  }
  return null;
}
