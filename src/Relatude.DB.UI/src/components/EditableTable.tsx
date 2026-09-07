import { useEffect, useRef, useState } from "react";
import { IconArrowNarrowDown, IconArrowNarrowUp } from "@tabler/icons-react";
import { saveNode, type Column, type Hit } from "../server/query";
import { useRowWindow } from "../rowWindow";

interface Props {
  storeId: string;
  columns: Column[];
  hits: Hit[];
  /** the node the form beside the table has open, if any: its row is marked */
  selected: string | null;
  sort: { key: string; descending: boolean } | null;
  sortApplied: boolean;
  onSort: (key: string) => void;
  /** a cell was written: the row is stale and the page may want to read the result again */
  onSaved: () => void;
  loading: boolean;
}

interface Cursor {
  row: number;
  col: number;
}

/**
 * The table view with the cells open for typing.
 *
 * It works the way a spreadsheet does, and for the same reason: fixing twenty values across twenty
 * rows through a form is twenty times open-edit-save, and the whole point of a table is that the
 * next thing to change is one key away. One cell has the cursor; the arrows, tab and enter move it;
 * typing starts editing where the cursor is; escape puts the old value back; enter and tab commit
 * and move on.
 *
 * A commit writes that one property of that one node, on its own, the moment it is made - there is
 * no sheet-wide save to forget, and two people editing different cells of the same row do not
 * overwrite each other. What comes back replaces the cell, so a value the store rounded, trimmed or
 * refused is visible immediately rather than at the next search.
 *
 * Only single scalars are cells. A list, a coordinate, a file, a relation, a reference and an
 * embedded document each need a control of their own, and the form beside the table is where they
 * are edited; their columns are marked read only here rather than pretending otherwise.
 */
export function EditableTable({ storeId, columns, hits, selected, sort, sortApplied, onSort, onSaved, loading }: Props) {
  const [cursor, setCursor] = useState<Cursor>({ row: 0, col: 0 });
  const [editing, setEditing] = useState<{ row: number; col: number; value: string } | null>(null);
  // what was written here since the last search, keyed "<node id>/<column key>": the table shows
  // these over what the result carries, so a saved cell does not flick back to its old text
  const [written, setWritten] = useState<Record<string, { text: string; value: unknown }>>({});
  const [failed, setFailed] = useState<{ key: string; message: string } | null>(null);
  const [saving, setSaving] = useState<string | null>(null);
  const bodyRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement | HTMLSelectElement | null>(null);
  // an open cell commits once. The keyboard commits it and moves on, and moving on takes the focus
  // off the input, which would otherwise commit it a second time from its own blur - with whatever
  // the detached input happens to hold by then.
  const cellOpen = useRef(false);
  // a large page is built a chunk at a time; the cursor may not walk out of what is built
  const { count: builtRows, onScroll, ensure } = useRowWindow(hits);

  useEffect(() => setWritten({}), [hits]);
  useEffect(() => {
    if (editing) inputRef.current?.focus();
  }, [editing]);

  // the cursor has to stay inside a result that just got shorter
  useEffect(() => {
    setCursor((c) => ({ row: Math.min(c.row, Math.max(0, hits.length - 1)), col: Math.min(c.col, Math.max(0, columns.length - 1)) }));
  }, [hits.length, columns.length]);

  const key = (hit: Hit, column: Column) => hit.id + "/" + column.key;
  const textOf = (hit: Hit, column: Column, i: number) => written[key(hit, column)]?.text ?? hit.cells?.[i] ?? "";
  const valueOf = (hit: Hit, column: Column) => (key(hit, column) in written ? written[key(hit, column)].value : hit.values?.[column.key]);

  function move(dRow: number, dCol: number) {
    setCursor((c) => {
      const row = Math.min(hits.length - 1, Math.max(0, c.row + dRow));
      const col = Math.min(columns.length - 1, Math.max(0, c.col + dCol));
      return { row, col };
    });
  }

  /** Opens the cell under the cursor, unless its column is one a table cannot hold. */
  function beginEdit(seed?: string) {
    const column = columns[cursor.col];
    const hit = hits[cursor.row];
    if (!column || !hit || !column.editor) return;
    if (column.editor === "bool") {
      // a checkbox has nothing to type into: the key that would open it flips it instead
      void commit(cursor.row, cursor.col, !(valueOf(hit, column) === true));
      return;
    }
    const current = valueOf(hit, column);
    cellOpen.current = true;
    setEditing({ row: cursor.row, col: cursor.col, value: seed ?? (current === null || current === undefined ? "" : String(current)) });
  }

  async function commit(row: number, col: number, value: unknown) {
    const column = columns[col];
    const hit = hits[row];
    if (!column || !hit || !column.editor) return;
    const cellKey = key(hit, column);
    cellOpen.current = false;
    setEditing(null);
    setFailed(null);
    setSaving(cellKey);
    try {
      await saveNode(storeId, hit.id, { [column.key]: value === "" && column.editor !== "text" ? null : value });
      // what the store made of it, rather than what was typed: a number it rounded or a date it
      // normalised has to show as it is now stored
      setWritten((prev) => ({ ...prev, [cellKey]: { text: display(value, column), value } }));
      onSaved();
    } catch (e) {
      setFailed({ key: cellKey, message: e instanceof Error ? e.message : String(e) });
    } finally {
      setSaving(null);
    }
  }

  function onKeyDown(e: React.KeyboardEvent) {
    if (editing) {
      if (e.key === "Escape") {
        e.preventDefault();
        cellOpen.current = false;
        setEditing(null);
        bodyRef.current?.focus();
      } else if (e.key === "Enter" || e.key === "Tab") {
        e.preventDefault();
        void commit(editing.row, editing.col, typed(editing.value, columns[editing.col]));
        if (e.key === "Tab") move(0, e.shiftKey ? -1 : 1);
        else move(1, 0);
        // after React has taken the input away, so its blur cannot commit a second time
        queueMicrotask(() => bodyRef.current?.focus());
      }
      return;
    }
    switch (e.key) {
      case "ArrowDown":
        e.preventDefault();
        move(1, 0);
        break;
      case "ArrowUp":
        e.preventDefault();
        move(-1, 0);
        break;
      case "ArrowLeft":
        e.preventDefault();
        move(0, -1);
        break;
      case "ArrowRight":
        e.preventDefault();
        move(0, 1);
        break;
      case "Tab":
        e.preventDefault();
        move(0, e.shiftKey ? -1 : 1);
        break;
      case "Home":
        e.preventDefault();
        setCursor((c) => ({ ...c, col: 0 }));
        break;
      case "End":
        e.preventDefault();
        setCursor((c) => ({ ...c, col: columns.length - 1 }));
        break;
      case "PageDown":
        e.preventDefault();
        move(10, 0);
        break;
      case "PageUp":
        e.preventDefault();
        move(-10, 0);
        break;
      case "Enter":
      case "F2":
        e.preventDefault();
        beginEdit();
        break;
      case "Delete":
      case "Backspace":
        e.preventDefault();
        if (columns[cursor.col]?.editor) void commit(cursor.row, cursor.col, columns[cursor.col].editor === "text" ? "" : null);
        break;
      default:
        // a printable key starts editing with it, as it does in a sheet
        if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) {
          e.preventDefault();
          beginEdit(e.key);
        } else if (e.key === "c" && (e.ctrlKey || e.metaKey)) {
          const hit = hits[cursor.row];
          const column = columns[cursor.col];
          if (hit && column) void navigator.clipboard?.writeText(textOf(hit, column, cursor.col));
        }
    }
  }

  // The cursor has to stay in view when the keyboard moves it past the edge of the scroller. On a
  // large page the row may not be built yet, so moving there builds it and the scroll waits for the
  // render that has it - but only a move asks to be scrolled to. Rows built because someone scrolled
  // must not pull the view back to wherever the cursor was left.
  const wantScroll = useRef(false);
  useEffect(() => {
    wantScroll.current = true;
    ensure(cursor.row + 1);
  }, [cursor, ensure]);
  useEffect(() => {
    if (!wantScroll.current) return;
    const cell = bodyRef.current?.querySelector<HTMLElement>("td.cursor");
    if (!cell) return; // still to be built; the render that builds it scrolls to it
    wantScroll.current = false;
    cell.scrollIntoView({ block: "nearest", inline: "nearest" });
  }, [cursor, builtRows]);

  return (
    <div className={"query-table-wrap grid" + (loading ? " loading" : "")} ref={bodyRef} tabIndex={0} onKeyDown={onKeyDown} onScroll={onScroll}>
      <table className="query-table editable">
        <thead>
          <tr>
            {columns.map((column) => (
              <th
                key={column.key}
                className={(column.sortable ? "sortable" : "") + (sort?.key === column.key ? (sortApplied ? " sorted" : " sorted-inactive") : "") + (column.editor ? "" : " locked")}
                title={column.editor ? `${column.type} — editable` : `${column.type} — edited in the form, not in a cell`}
                onClick={column.sortable ? () => onSort(column.key) : undefined}
              >
                {column.name}
                {sort?.key === column.key && (sort.descending ? <IconArrowNarrowDown size={13} stroke={2} /> : <IconArrowNarrowUp size={13} stroke={2} />)}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {hits.slice(0, builtRows).map((hit, row) => (
            <tr key={hit.id} className={selected === hit.id ? "selected" : ""}>
              {columns.map((column, col) => {
                const cellKey = key(hit, column);
                const here = cursor.row === row && cursor.col === col;
                const open = editing?.row === row && editing?.col === col;
                return (
                  <td
                    key={column.key}
                    className={(here ? "cursor " : "") + (column.editor ? "editable" : "locked") + (written[cellKey] ? " written" : "") + (failed?.key === cellKey ? " failed" : "")}
                    title={failed?.key === cellKey ? failed.message : textOf(hit, column, col)}
                    onMouseDown={(e) => {
                      setCursor({ row, col });
                      // a click on the control in a cell - the checkbox, or the input of the cell being
                      // edited - is about that value and nothing else: it must not take the keyboard
                      // away from the control it landed on
                      if (isControl(e.target)) return;
                      // and a click anywhere else only moves the cursor. In a sheet a click picks the
                      // cell to type in; opening the node form here would take half the width away and
                      // reflow the row under the pointer, which is not what the click asked for
                      bodyRef.current?.focus();
                    }}
                    onDoubleClick={() => {
                      setCursor({ row, col });
                      beginEdit();
                    }}
                  >
                    {open ? (
                      <CellInput
                        column={column}
                        value={editing.value}
                        inputRef={inputRef}
                        onChange={(v) => setEditing({ row, col, value: v })}
                        onCommit={(v) => {
                          if (!cellOpen.current) return; // the keyboard already committed this cell
                          void commit(row, col, typed(v, column));
                        }}
                      />
                    ) : column.editor === "bool" ? (
                      <input
                        type="checkbox"
                        checked={valueOf(hit, column) === true}
                        onChange={(e) => {
                          void commit(row, col, e.target.checked);
                          bodyRef.current?.focus(); // flipped; the keyboard goes back to the sheet
                        }}
                        onClick={(e) => e.stopPropagation()}
                      />
                    ) : (
                      <span>{textOf(hit, column, col)}</span>
                    )}
                    {saving === cellKey && <span className="grid-saving" />}
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
      {hits.length === 0 && <div className="query-empty">Nothing matched.</div>}
      {failed && <div className="query-error grid-error">{failed.message}</div>}
    </div>
  );
}

/** The control a cell is edited with. A select for an enum, otherwise a plain typed input. */
function CellInput({
  column,
  value,
  inputRef,
  onChange,
  onCommit,
}: {
  column: Column;
  value: string;
  inputRef: React.MutableRefObject<HTMLInputElement | HTMLSelectElement | null>;
  onChange: (value: string) => void;
  onCommit: (value: string) => void;
}) {
  if (column.editor === "enum" && column.options) {
    return (
      <select
        className="grid-input"
        ref={(el) => {
          inputRef.current = el;
        }}
        value={value}
        onChange={(e) => {
          onChange(e.target.value);
          onCommit(e.target.value);
        }}
      >
        {column.options.some((o) => String(o.value) === value) ? null : <option value={value}>{value}</option>}
        {column.options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
    );
  }
  return (
    <input
      className="grid-input"
      ref={(el) => {
        inputRef.current = el;
      }}
      type={column.editor === "integer" || column.editor === "number" ? "number" : "text"}
      step={column.editor === "integer" ? 1 : "any"}
      value={value}
      spellCheck={false}
      onChange={(e) => onChange(e.target.value)}
      onBlur={(e) => onCommit(e.target.value)}
    />
  );
}

/** Whether a click landed on a control inside a cell rather than on the cell itself. */
function isControl(target: EventTarget | null): boolean {
  return target instanceof Element && target.closest("input, select, textarea, button") !== null;
}

/** What a typed string means for the column: the server parses the rest, and says so when it cannot. */
function typed(text: string, column: Column | undefined): unknown {
  if (!column) return text;
  switch (column.editor) {
    case "integer":
    case "enum":
      return text === "" ? null : Number.parseInt(text, 10);
    case "number":
      return text === "" ? null : Number(text);
    case "bool":
      return text === "true";
    default:
      return text;
  }
}

/** The text a just-written value shows as, until the next search brings the store's own. */
function display(value: unknown, column: Column): string {
  if (value === null || value === undefined) return "";
  if (column.editor === "bool") return value === true ? "True" : "False";
  if (column.editor === "enum" && column.options) return column.options.find((o) => o.value === value)?.label ?? String(value);
  return String(value);
}
