import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { IconCheck, IconChevronDown, IconCube, IconDatabase, IconSearch, IconX } from "@tabler/icons-react";
import { KindIcon, SourceDot } from "./DatamodelIcons";
import { codeSourceGuid, sourceColors, type ModelKind, type SourceType } from "../server/datamodel";
import { formatCount } from "../format";
import "../datamodel.css";

export interface PickableType {
  id: string;
  name: string;
  fullName: string;
  isBase: boolean;
  isInterface: boolean;
  hidden: boolean;
  kind: ModelKind;
  sourceId: string;
  /** whether a node of it can be made here; false is shown, and not choosable, with the reason */
  canCreate: boolean;
  count: number;
}

interface Props {
  types: PickableType[];
  sources: { id: string; name: string; type: SourceType; color?: string | null }[];
  value: string | null;
  onChange: (id: string) => void;
  /** Called when the picker closes after a pointer pick, so the page can move focus on. */
  onPicked?: () => void;
}

const showEmptyKey = "queryShowEmptyTypes";

/**
 * Chooses the node type a query runs against.
 *
 * A plain select was fine while a database had a handful of types and unusable once it had fifty:
 * the list is searched here instead, and the types holding nothing are left out of it - a type with
 * no nodes has no rows to find, and on a real model those are most of the list. The switch to show
 * them anyway is inside the list, because an empty type is exactly what someone checking whether
 * anything was imported wants to see, and the choice is remembered.
 *
 * A type is marked the way the data model editor marks it - the icon says class, interface, record
 * or struct, the colour says which model source defines it - so a type is recognisable across pages
 * rather than being a name in one place and a name in another.
 */
export function TypePicker({ types, sources, value, onChange, onPicked }: Props) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [showEmpty, setShowEmpty] = useState(() => localStorage.getItem(showEmptyKey) === "true");
  const [active, setActive] = useState(0);
  const searchRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);

  const colors = useMemo(() => sourceColors(sources, codeSourceGuid), [sources]);
  const selected = types.find((t) => t.id === value) ?? null;

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    return types.filter((t) => {
      // the base type is the way back to everything, and the type in the box has to stay in the list
      // it was picked from, however it is filtered
      if (t.isBase || t.id === value) return q.length === 0 || match(t, q);
      if (!showEmpty && t.count === 0) return false;
      return q.length === 0 || match(t, q);
    });
  }, [types, query, showEmpty, value]);

  const emptyHidden = useMemo(() => (showEmpty ? 0 : types.filter((t) => !t.isBase && t.id !== value && t.count === 0).length), [types, showEmpty, value]);

  useEffect(() => localStorage.setItem(showEmptyKey, String(showEmpty)), [showEmpty]);

  // the search starts where the eye is, and the highlight starts on what is selected
  useEffect(() => {
    if (!open) return;
    setQuery("");
    setActive(Math.max(0, shown.findIndex((t) => t.id === value)));
    searchRef.current?.focus();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  // a filtered list can be shorter than where the highlight was
  useEffect(() => setActive((a) => Math.min(a, Math.max(0, shown.length - 1))), [shown.length]);

  useLayoutEffect(() => {
    if (!open) return;
    listRef.current?.querySelector<HTMLElement>(".type-option.active")?.scrollIntoView({ block: "nearest" });
  }, [active, open]);

  function pick(t: PickableType) {
    onChange(t.id);
    setOpen(false);
    onPicked?.();
  }

  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      setActive((a) => Math.min(shown.length - 1, Math.max(0, a + (e.key === "ArrowDown" ? 1 : -1))));
    } else if (e.key === "Enter") {
      e.preventDefault();
      if (shown[active]) pick(shown[active]);
    } else if (e.key === "Escape") {
      e.preventDefault();
      setOpen(false);
    }
  }

  return (
    <div className="type-picker">
      <button className="type-picker-button" onClick={() => setOpen(!open)} title={selected ? selected.fullName : "Pick a node type"}>
        <TypeMark type={selected} colors={colors} />
        <span className="type-picker-name">{selected ? (selected.isBase ? "All node types" : selected.name) : "Pick a type"}</span>
        {selected && <span className="type-picker-count">{formatCount(selected.count)}</span>}
        <IconChevronDown size={15} stroke={1.9} />
      </button>
      {open && (
        <>
          <div className="db-menu-backdrop" onClick={() => setOpen(false)} />
          <div className="type-picker-menu" onKeyDown={onKeyDown}>
            <div className="type-picker-search">
              <IconSearch size={14} stroke={2} />
              <input ref={searchRef} value={query} placeholder="Search types…" onChange={(e) => setQuery(e.target.value)} />
              {query && (
                <button className="icon-button" onClick={() => setQuery("")} title="Clear">
                  <IconX size={13} stroke={2} />
                </button>
              )}
            </div>
            <div className="type-picker-list" ref={listRef}>
              {shown.map((t, i) => (
                <button
                  key={t.id}
                  className={"type-option" + (i === active ? " active" : "") + (t.id === value ? " selected" : "") + (t.count === 0 ? " empty" : "")}
                  onMouseEnter={() => setActive(i)}
                  onMouseDown={(e) => e.preventDefault()}
                  onClick={() => pick(t)}
                  title={t.fullName}
                >
                  <TypeMark type={t} colors={colors} />
                  <span className="type-option-name">
                    {t.isBase ? "All node types" : t.name}
                    {t.hidden && <span className="badge">hidden</span>}
                  </span>
                  <span className="type-option-count">{formatCount(t.count)}</span>
                  {t.id === value && <IconCheck size={14} stroke={2} />}
                </button>
              ))}
              {shown.length === 0 && <div className="muted type-picker-empty">No type matches “{query}”.</div>}
            </div>
            <label className="type-picker-foot">
              <input type="checkbox" checked={showEmpty} onChange={(e) => setShowEmpty(e.target.checked)} />
              <span>Show types with no nodes{emptyHidden > 0 ? ` (${emptyHidden} hidden)` : ""}</span>
            </label>
          </div>
        </>
      )}
    </div>
  );
}

/**
 * Chooses the type of a node about to be made. The same list as the picker above, as a dialog and
 * without the empty-types switch: a type with no nodes is exactly what someone making the first one
 * is looking for. Types the server cannot instantiate - the base type, and a type in the model whose
 * assembly is not loaded - are shown but not choosable, with the reason on them.
 */
export function NewNodeDialog({
  types,
  sources,
  onPick,
  onClose,
}: {
  types: PickableType[];
  sources: { id: string; name: string; type: SourceType; color?: string | null }[];
  onPick: (type: PickableType) => void;
  onClose: () => void;
}) {
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const listRef = useRef<HTMLDivElement>(null);
  const colors = useMemo(() => sourceColors(sources, codeSourceGuid), [sources]);

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    return types.filter((t) => !t.isBase && (q.length === 0 || match(t, q)));
  }, [types, query]);

  useEffect(() => setActive((a) => Math.min(a, Math.max(0, shown.length - 1))), [shown.length]);
  useLayoutEffect(() => {
    listRef.current?.querySelector<HTMLElement>(".type-option.active")?.scrollIntoView({ block: "nearest" });
  }, [active]);

  function pick(t: PickableType) {
    if (!t.canCreate) return;
    onPick(t);
  }
  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      setActive((a) => Math.min(shown.length - 1, Math.max(0, a + (e.key === "ArrowDown" ? 1 : -1))));
    } else if (e.key === "Enter") {
      e.preventDefault();
      if (shown[active]) pick(shown[active]);
    } else if (e.key === "Escape") {
      e.preventDefault();
      onClose();
    }
  }

  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="dialog new-node-dialog" onKeyDown={onKeyDown}>
        <h3>
          <IconCube size={16} stroke={2} /> New node
        </h3>
        <div className="dialog-body">The node is written straight away, with the defaults of its type, and opens in the form beside the list.</div>
        <div className="type-picker-search">
          <IconSearch size={14} stroke={2} />
          <input autoFocus value={query} placeholder="Search types…" onChange={(e) => setQuery(e.target.value)} />
          {query && (
            <button className="icon-button" onClick={() => setQuery("")} title="Clear">
              <IconX size={13} stroke={2} />
            </button>
          )}
        </div>
        <div className="type-picker-list new-node-list" ref={listRef}>
          {shown.map((t, i) => (
            <button
              key={t.id}
              className={"type-option" + (i === active ? " active" : "") + (t.canCreate ? "" : " disabled")}
              disabled={!t.canCreate}
              onMouseEnter={() => setActive(i)}
              onClick={() => pick(t)}
              title={t.canCreate ? t.fullName : t.fullName + " — the model has this type, but no assembly loaded here implements it"}
            >
              <TypeMark type={t} colors={colors} />
              <span className="type-option-name">
                {t.name}
                {t.hidden && <span className="badge">hidden</span>}
                {!t.canCreate && <span className="badge">not loaded</span>}
              </span>
              <span className="type-option-count">{formatCount(t.count)}</span>
            </button>
          ))}
          {shown.length === 0 && <div className="muted type-picker-empty">No type matches “{query}”.</div>}
        </div>
        <div className="dialog-actions">
          <button className="action-button" onClick={onClose}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}

function match(t: PickableType, q: string): boolean {
  return t.name.toLowerCase().includes(q) || t.fullName.toLowerCase().includes(q) || (t.isBase && "all node types".includes(q));
}

/** The icon and the source colour of one type; the base type stands for the database itself. */
function TypeMark({ type, colors }: { type: PickableType | null; colors: Map<string, string> }) {
  if (!type) return <IconDatabase size={16} stroke={1.9} />;
  if (type.isBase) return <IconDatabase size={16} stroke={1.9} />;
  return (
    <span className="type-mark">
      <KindIcon kind={type.kind} size={15} />
      <SourceDot color={colors.get(type.sourceId) ?? "#8a8781"} />
    </span>
  );
}
