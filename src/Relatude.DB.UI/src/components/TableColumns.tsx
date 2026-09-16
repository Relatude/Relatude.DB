import { useEffect, useRef, useState, type CSSProperties, type DragEvent as ReactDragEvent, type MouseEvent as ReactMouseEvent, type ReactNode } from "react";
import { IconChevronLeft, IconChevronRight, IconPlus, IconX } from "@tabler/icons-react";

/**
 * The column headings of the query tables: what each one is called, the grip that widens it, and the
 * cross that takes it away - plus one heading at the end that adds another.
 *
 * Everything about a column is on the column: drag its name to move it, the arrows beside it to move
 * it one place, the cross to drop it, the grip on its edge to size it. There used to be a row of
 * chips above the table doing the adding and the removing, which meant looking away from the table
 * to change what the table shows, and left the heading as the only part of a column you could not
 * act on. The two tables (the read-only one and the editable one) share this, so they stay the same
 * table with different cells.
 */

// A floor and no ceiling, the same rule the log tables follow: a column holding a long value is
// worth dragging as wide as it takes, and double-clicking the grip is always the way back.
const minColumnWidth = 60;
const widthsKey = (scope: string) => "query:columns:" + scope;

export interface ColumnSizing {
  /** Only the columns that have been dragged; the rest size themselves to their contents. */
  widths: Record<string, number>;
  dragging: string | null;
  hasAny: boolean;
  /** The inline style pinning one column - put it on the heading AND on that column's cells, or a
   * long value will push the column wider than the heading was dragged to. */
  styleOf(key: string): CSSProperties | undefined;
  start(key: string, e: ReactMouseEvent): void;
  reset(key: string): void;
  resetAll(): void;
}

/**
 * Remembered per scope (the node type), because a column is the same column in every query on it.
 * Reading and writing are both tolerant: a private window with no storage costs the widths, nothing else.
 */
export function useColumnSizing(scope: string): ColumnSizing {
  const [widths, setWidths] = useState<Record<string, number>>(() => read(scope));
  const [dragging, setDragging] = useState<string | null>(null);
  // the scope changes when the query changes type, and the widths of the type it changed to are
  // another set entirely
  const known = useRef(scope);
  useEffect(() => {
    if (known.current === scope) return;
    known.current = scope;
    setWidths(read(scope));
  }, [scope]);

  function start(key: string, e: ReactMouseEvent) {
    e.preventDefault();
    e.stopPropagation(); // the heading sorts, and the table behind it draws selection rectangles
    const cell = (e.currentTarget as HTMLElement).closest("th");
    const startWidth = widths[key] ?? Math.round(cell?.getBoundingClientRect().width ?? 120);
    const startX = e.clientX;
    setDragging(key);
    document.body.style.cursor = "col-resize";
    const move = (ev: MouseEvent) => {
      const next = Math.max(minColumnWidth, startWidth + ev.clientX - startX);
      setWidths((prev) => (prev[key] === next ? prev : { ...prev, [key]: next }));
    };
    const up = () => {
      window.removeEventListener("mousemove", move);
      window.removeEventListener("mouseup", up);
      document.body.style.cursor = "";
      setDragging(null);
      setWidths((current) => {
        write(scope, current); // read out of the setter: the closure above holds the old widths
        return current;
      });
    };
    window.addEventListener("mousemove", move);
    window.addEventListener("mouseup", up);
  }

  function reset(key: string) {
    setWidths((prev) => {
      if (!(key in prev)) return prev;
      const next = { ...prev };
      delete next[key];
      write(scope, next);
      return next;
    });
  }

  function resetAll() {
    setWidths({});
    write(scope, {});
  }

  return {
    widths,
    dragging,
    hasAny: Object.keys(widths).length > 0,
    styleOf: (key) => (widths[key] ? { width: widths[key], minWidth: widths[key], maxWidth: widths[key] } : undefined),
    start,
    reset,
    resetAll,
  };
}

function read(scope: string): Record<string, number> {
  try {
    const saved = localStorage.getItem(widthsKey(scope));
    if (!saved) return {};
    const parsed = JSON.parse(saved) as Record<string, unknown>;
    const widths: Record<string, number> = {};
    for (const [key, value] of Object.entries(parsed)) {
      if (typeof value === "number" && isFinite(value)) widths[key] = Math.max(minColumnWidth, value);
    }
    return widths;
  } catch {
    return {};
  }
}

function write(scope: string, widths: Record<string, number>) {
  try {
    if (Object.keys(widths).length === 0) localStorage.removeItem(widthsKey(scope));
    else localStorage.setItem(widthsKey(scope), JSON.stringify(widths));
  } catch {
    // nothing depends on it holding
  }
}

/**
 * Which column is being dragged and where it would land. It has to be one state for the whole row -
 * the heading being dragged and the heading it is over are two different headings - so it is held
 * here and handed down, the same way the widths are.
 */
export interface ColumnDrag {
  key: string | null;
  over: string | null;
  after: boolean;
  begin(key: string): void;
  enter(key: string, after: boolean): void;
  leave(key: string): void;
  end(): void;
  /** True for a moment after a drag, so the click it ends with is not read as a sort. */
  justMoved(): boolean;
}

export function useColumnDrag(): ColumnDrag {
  const [key, setKey] = useState<string | null>(null);
  const [over, setOver] = useState<{ key: string; after: boolean } | null>(null);
  const movedAt = useRef(0);
  return {
    key,
    over: over?.key ?? null,
    after: over?.after ?? false,
    begin: (k) => {
      setKey(k);
      setOver(null);
    },
    enter: (k, after) => setOver((prev) => (prev?.key === k && prev.after === after ? prev : { key: k, after })),
    leave: (k) => setOver((prev) => (prev?.key === k ? null : prev)),
    end: () => {
      movedAt.current = Date.now();
      setKey(null);
      setOver(null);
    },
    justMoved: () => Date.now() - movedAt.current < 200,
  };
}

/** What the headings need from the page they are in, handed to both tables as one thing. */
export interface TableColumnsUi {
  sizing: ColumnSizing;
  drag: ColumnDrag;
  /** The keys in the order they are shown, so a heading knows where it stands. */
  order: string[];
  /** Puts the column at that place in the order; the rest close up around it. */
  onMove(key: string, index: number): void;
  /** Takes the column out of the table. */
  onRemove(key: string): void;
  /** The columns not shown yet, or null while the type is still being asked. */
  addOptions: { key: string; name: string; hint: string }[] | null;
  onAdd(key: string): void;
  /** Whatever else belongs in the add menu: putting the type's own columns back, and so on. */
  extras: { label: string; onClick: () => void }[];
}

/**
 * One column heading: its name (which sorts, and which is what you drag to move the column), the
 * arrows that move it one place either way, the cross that drops it, and the grip that sizes it.
 * The controls sit to the right of the name and appear when the heading is hovered into - all four
 * of them on every heading of every table at all times would be a row of buttons, not headings.
 */
export function ColumnHead({
  ui,
  colKey,
  className,
  title,
  onClick,
  children,
}: {
  ui: TableColumnsUi;
  colKey: string;
  className?: string;
  title?: string;
  onClick?: () => void;
  children: ReactNode;
}) {
  const sized = ui.sizing.widths[colKey];
  const index = ui.order.indexOf(colKey);
  const dragging = ui.drag.key === colKey;
  const over = ui.drag.over === colKey && ui.drag.key !== null && ui.drag.key !== colKey;
  const stop = (e: ReactMouseEvent) => e.stopPropagation();

  // where in the row the dragged column would land if it were dropped on this one
  function landing(e: ReactDragEvent): number {
    const box = (e.currentTarget as HTMLElement).getBoundingClientRect();
    const after = e.clientX > box.left + box.width / 2;
    const from = ui.order.indexOf(ui.drag.key ?? "");
    let to = index + (after ? 1 : 0);
    if (from >= 0 && from < to) to -= 1; // it is leaving a place before this one, so everything shifts back
    return to;
  }

  return (
    <th
      className={
        (className ?? "") +
        " th-column" +
        (dragging ? " th-dragging" : "") +
        (over ? (ui.drag.after ? " th-drop-after" : " th-drop-before") : "")
      }
      title={title}
      style={ui.sizing.styleOf(colKey)}
      onDragOver={(e) => {
        if (ui.drag.key === null || ui.drag.key === colKey) return;
        e.preventDefault(); // without this the drop never happens
        const box = e.currentTarget.getBoundingClientRect();
        ui.drag.enter(colKey, e.clientX > box.left + box.width / 2);
      }}
      onDragLeave={() => ui.drag.leave(colKey)}
      onDrop={(e) => {
        e.preventDefault();
        const moved = ui.drag.key;
        const to = landing(e);
        ui.drag.end();
        if (moved && moved !== colKey) ui.onMove(moved, to);
      }}
    >
      {/* a flex row so the controls sit at the right edge of the heading rather than trailing the
          name: on a wide column they would otherwise be stranded in the middle of it */}
      <span className="th-inner">
      {/* The name is the handle as well as the sort: dragging a heading by its name is what a person
          tries first, and it leaves the buttons beside it draggable-free so they stay clickable. */}
      <span
        className={"th-name" + (onClick ? " sortable" : "")}
        draggable
        onDragStart={(e) => {
          e.dataTransfer.effectAllowed = "move";
          e.dataTransfer.setData("text/plain", colKey); // firefox starts no drag without data
          ui.drag.begin(colKey);
        }}
        onDragEnd={() => ui.drag.end()}
        onClick={() => {
          if (ui.drag.justMoved()) return; // the click a drag ends with is not a sort
          onClick?.();
        }}
      >
        {children}
      </span>
      <span className="th-tools">
        <button
          className="icon-button th-move"
          title="Move this column left"
          aria-label="Move this column left"
          disabled={index <= 0}
          onMouseDown={stop}
          onClick={(e) => {
            stop(e);
            ui.onMove(colKey, index - 1);
          }}
        >
          <IconChevronLeft size={12} stroke={2.4} />
        </button>
        <button
          className="icon-button th-move"
          title="Move this column right"
          aria-label="Move this column right"
          disabled={index < 0 || index >= ui.order.length - 1}
          onMouseDown={stop}
          onClick={(e) => {
            stop(e);
            ui.onMove(colKey, index + 1);
          }}
        >
          <IconChevronRight size={12} stroke={2.4} />
        </button>
        <button
          className="icon-button th-drop"
          title="Remove this column"
          aria-label="Remove this column"
          onMouseDown={stop}
          onClick={(e) => {
            stop(e);
            ui.onRemove(colKey);
          }}
        >
          <IconX size={12} stroke={2.2} />
        </button>
      </span>
      </span>
      <span
        className={"th-grip" + (ui.sizing.dragging === colKey ? " active" : "") + (sized ? " sized" : "")}
        title={sized ? "Drag to resize, double-click to fit it to its contents again" : "Drag to resize this column"}
        onMouseDown={(e) => ui.sizing.start(colKey, e)}
        onDoubleClick={() => ui.sizing.reset(colKey)}
      />
    </th>
  );
}

/**
 * The heading at the end of the row that adds a column. It is the only one that is not a column, so
 * it is also where the things that are about the set rather than about one of them live: putting the
 * type's own columns back, and giving every dragged width back to its contents.
 */
export function AddColumnHead({ ui }: { ui: TableColumnsUi }) {
  const [open, setOpen] = useState(false);
  const box = useRef<HTMLTableCellElement>(null);
  useEffect(() => {
    if (!open) return;
    const away = (e: MouseEvent) => {
      if (!box.current?.contains(e.target as Node)) setOpen(false);
    };
    const key = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    window.addEventListener("mousedown", away);
    window.addEventListener("keydown", key);
    return () => {
      window.removeEventListener("mousedown", away);
      window.removeEventListener("keydown", key);
    };
  }, [open]);

  const extras = ui.extras.concat(ui.sizing.hasAny ? [{ label: "Reset column widths", onClick: ui.sizing.resetAll }] : []);
  return (
    <th className="th-add" ref={box}>
      <button
        className={"icon-button" + (open ? " active" : "")}
        title="Add a column"
        aria-label="Add a column"
        aria-expanded={open}
        onMouseDown={(e) => e.stopPropagation()}
        onClick={() => setOpen((o) => !o)}
      >
        <IconPlus size={14} stroke={2.2} />
      </button>
      {open && (
        <div className="th-add-menu">
          {ui.addOptions === null ? (
            <div className="muted th-add-empty">Loading…</div>
          ) : ui.addOptions.length === 0 ? (
            <div className="muted th-add-empty">Every column this type has is already shown.</div>
          ) : (
            <div className="th-add-list">
              {ui.addOptions.map((option) => (
                <button
                  key={option.key}
                  className="th-add-option"
                  onClick={() => {
                    ui.onAdd(option.key);
                    setOpen(false);
                  }}
                >
                  <span>{option.name}</span>
                  <span className="muted">{option.hint}</span>
                </button>
              ))}
            </div>
          )}
          {extras.length > 0 && (
            <div className="th-add-extras">
              {extras.map((extra) => (
                <button
                  key={extra.label}
                  className="link-button"
                  onClick={() => {
                    extra.onClick();
                    setOpen(false);
                  }}
                >
                  {extra.label}
                </button>
              ))}
            </div>
          )}
        </div>
      )}
    </th>
  );
}
