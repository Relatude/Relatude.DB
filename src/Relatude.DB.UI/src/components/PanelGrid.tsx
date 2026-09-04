import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { ReactNode } from "react";

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

type DragMode = "col" | "row" | "both";

export function PanelGrid({ id, rows, defaultSplit = 0.62 }: { id: string; rows: PanelRow[]; defaultSplit?: number }) {
  const storageKey = "panelGrid:" + id;
  const [layout, setLayout] = useState<Layout>(() => read(storageKey, defaultSplit));
  const [dragging, setDragging] = useState<DragMode | null>(null);
  const [narrow, setNarrow] = useState(false);
  const box = useRef<HTMLDivElement>(null);
  const rowEls = useRef<(HTMLDivElement | null)[]>([]);
  const drag = useRef<{ pointerId: number; mode: DragMode; row: number; x: number; y: number; split: number; height: number; free: number } | null>(null);

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

  const customized = layout.split !== defaultSplit || Object.keys(layout.heights).length > 0;

  if (narrow) {
    // one column: the cells in the order they were given, nothing to drag
    return (
      <div className="panel-grid-wrap narrow" ref={box}>
        {rows.flatMap((row) => row.cells.map((cell, i) => <div key={row.id + i} className="panel-cell">{cell}</div>))}
      </div>
    );
  }

  // a row is "sized" once it has a height of its own, dragged or given: a panel there has room to
  // hand to a body that wants to fill it, which a row sizing itself to its content does not
  const heightOf = (row: PanelRow) => layout.heights[row.id] ?? row.height;
  const cellClass = (row: PanelRow) => "panel-cell" + (heightOf(row) == null ? "" : " sized");

  const children: ReactNode[] = [];
  rows.forEach((row, i) => {
    const gridRow = 2 * i + 1;
    const split = row.cells.length > 1;
    children.push(
      <div key={row.id + ":a"} className={cellClass(row)} ref={(el) => void (rowEls.current[i] = el)} style={{ gridRow, gridColumn: split ? 1 : "1 / -1" }}>
        {row.cells[0]}
      </div>,
    );
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
      children.push(
        <div key={row.id + ":b"} className={cellClass(row)} style={{ gridRow, gridColumn: 3 }}>
          {row.cells[1]}
        </div>,
      );
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
    <div className={"panel-grid-wrap" + (dragging ? " dragging" : "")} ref={box}>
      {customized && (
        <div className="panel-grid-tools">
          <button className="link-button" onClick={() => setLayout({ split: defaultSplit, heights: {} })}>
            Reset layout
          </button>
        </div>
      )}
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
