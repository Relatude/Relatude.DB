import { useEffect, useMemo, useRef, useState } from "react";
import {
  IconArrowNarrowDown,
  IconArrowNarrowUp,
  IconChartBar,
  IconChevronLeft,
  IconChevronRight,
  IconCode,
  IconColumns3,
  IconDownload,
  IconFilter,
  IconLayoutList,
  IconPlus,
  IconRefresh,
  IconSearch,
  IconSum,
  IconTable,
  IconX,
} from "@tabler/icons-react";
import { NodeEditor } from "./NodeEditor";
import { PivotView, type PivotBase } from "./PivotView";
import { GroupByView } from "./GroupByView";
import { TypePicker } from "./TypePicker";
import { showError } from "../dialogs";
import {
  csvRowLimit,
  exportCsv,
  fetchColumns,
  runSearch,
  fetchQueryModel,
  type Facet,
  type FacetSelection,
  type FacetValue,
  type QueryModel,
  type SearchRequest,
  type SelectColumn,
  type TextSample,
} from "../server/query";
import { useLiveResult } from "../server/hooks";
import { subscribeResync } from "../server/channel";
import type { DatabaseInfo } from "../server/serverInfo";
import { formatCount, formatTime } from "../format";
import { loadTabs, newQuery, saveTabs, type HitsView, type QueryMode, type QueryTabs, type SavedQuery } from "../queryTabs";

const pageSizes = [25, 50, 100, 200];

const editorWidthKey = "queryEditorWidth";
const minEditorWidth = 320; // narrower and the form's own labels start wrapping
const minResultsWidth = 320; // wider and the list the form was opened from stops being readable
const maxEditorShare = 0.6; // and a share of the page as well, so a narrower window keeps a list

/** The columns the select mode opens with: the node's own fields that name it, then its first two properties. */
const defaultSelectKeys = ["__name", "__type", "__changed"];

/**
 * A text the search was sampled from, with the words it matched marked. The server sends the
 * fragments the engine's own TextSample produced (see UIQuery.sampleView), so what is marked here is
 * what the index matched on rather than a second guess at it made from the search box. Without a
 * sample - no search, or a semantic hit with no literal match - the plain text is rendered as it was.
 */
function Sampled({ sample, plain }: { sample: TextSample | null; plain: string }) {
  if (!sample) return <>{plain}</>;
  return (
    <>
      {sample.cutAtStart && "…"}
      {sample.fragments.map((f, i) => (f.isMatch ? <mark key={i}>{f.text}</mark> : <span key={i}>{f.text}</span>))}
      {sample.cutAtEnd && "…"}
    </>
  );
}

/** A bucket's identity, so a selection survives the counts changing under it. */
function keyOf(v: { value: string | null; value2: string | null }): string {
  return (v.value ?? " none") + " " + (v.value2 ?? "");
}

const modes: { id: QueryMode; label: string; icon: typeof IconSearch; hint: string }[] = [
  { id: "search", label: "Search", icon: IconSearch, hint: "The nodes that match, as a list or a table" },
  { id: "select", label: "Select", icon: IconColumns3, hint: "The nodes that match, as a table of the columns you choose (SQL's SELECT)" },
  { id: "groups", label: "Group by", icon: IconSum, hint: "One row per value of a property, with a count and aggregates (SQL's GROUP BY)" },
  { id: "pivot", label: "Pivot", icon: IconChartBar, hint: "Groups by property on two axes, a count or sum per cell" },
];

/**
 * Search a database and edit what comes back - on as many queries at once as there are tabs.
 *
 * Every tab is a saved query (see queryTabs.ts): everything that decides what is asked of the server
 * lives in it and is written to localStorage as it changes, so the page comes back the way it was
 * left. What is not the query's - the page number, the open node, the widths - is the tab's own
 * while it is on screen, and starts over when it is switched to again. The tabs are kept here and
 * the query itself is a component keyed by the tab, so switching is a remount and nothing of one
 * query leaks into another.
 */
export function QuerySection({ db }: { db: DatabaseInfo }) {
  const [model, setModel] = useState<QueryModel | null>(null);
  const [modelError, setModelError] = useState<string | null>(null);
  const [tabs, setTabs] = useState<QueryTabs>(() => loadTabs(db.id));
  const [renaming, setRenaming] = useState<string | null>(null);

  useEffect(() => saveTabs(db.id, tabs), [db.id, tabs]);

  useEffect(() => {
    let cancelled = false;
    setModel(null);
    setModelError(null);
    fetchQueryModel(db.id)
      .then((m) => !cancelled && setModel(m))
      .catch((e) => !cancelled && setModelError(e instanceof Error ? e.message : String(e)));
    return () => {
      cancelled = true;
    };
  }, [db.id]);

  const active = tabs.queries.find((q) => q.id === tabs.active) ?? tabs.queries[0];

  function patch(id: string, changes: Partial<SavedQuery>) {
    setTabs((t) => ({ ...t, queries: t.queries.map((q) => (q.id === id ? { ...q, ...changes } : q)) }));
  }
  function add() {
    const q = newQuery();
    // a new tab starts where the current one is: same type, nothing else - the type is the one
    // choice that takes a moment to make again, and a fresh search on it is the common next move
    q.typeId = active.typeId;
    setTabs((t) => ({ active: q.id, queries: [...t.queries, q] }));
  }
  function close(id: string) {
    setTabs((t) => {
      const remaining = t.queries.filter((q) => q.id !== id);
      if (remaining.length === 0) {
        const q = newQuery();
        return { active: q.id, queries: [q] };
      }
      // closing the active tab lands on its neighbour to the left, the way browsers do
      const index = t.queries.findIndex((q) => q.id === id);
      const next = t.active === id ? remaining[Math.max(0, index - 1)].id : t.active;
      return { active: next, queries: remaining };
    });
  }
  function rename(id: string, name: string) {
    patch(id, { name: name.trim() === "" ? null : name.trim() });
    setRenaming(null);
  }

  // A tab without a typed name is named after what it asks: the type, the text, the kind of query.
  function autoName(q: SavedQuery): string {
    const type = model?.types.find((t) => t.id === (q.typeId ?? model?.baseTypeId));
    const parts = [type ? (type.isBase ? "All nodes" : type.name) : "Query"];
    const text = q.text.trim();
    if (text) parts.push("“" + (text.length > 24 ? text.slice(0, 24) + "…" : text) + "”");
    if (q.mode !== "search") parts.push(modes.find((m) => m.id === q.mode)?.label.toLowerCase() ?? q.mode);
    return parts.join(" · ");
  }

  if (modelError) return <div className="placeholder">{modelError}</div>;

  return (
    <div className="query">
      <div className="tabs query-tabs" role="tablist">
        {tabs.queries.map((q) => {
          const isActive = q.id === active.id;
          return (
            <div
              key={q.id}
              role="tab"
              aria-selected={isActive}
              className={"tab" + (isActive ? " active" : "") + (q.name === null ? " auto-named" : "")}
              title={q.name === null ? "Double-click to name this query" : autoName(q)}
              onClick={() => setTabs((t) => ({ ...t, active: q.id }))}
              onDoubleClick={() => setRenaming(q.id)}
              onAuxClick={(e) => e.button === 1 && close(q.id)}
            >
              {renaming === q.id ? (
                <input
                  className="tab-rename"
                  autoFocus
                  defaultValue={q.name ?? ""}
                  placeholder={autoName(q)}
                  onFocus={(e) => e.target.select()}
                  onBlur={(e) => rename(q.id, e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") rename(q.id, e.currentTarget.value);
                    if (e.key === "Escape") setRenaming(null);
                  }}
                  onClick={(e) => e.stopPropagation()}
                />
              ) : (
                <span className="query-tab-name">{q.name ?? autoName(q)}</span>
              )}
              <button
                className="icon-button"
                title="Close this query"
                tabIndex={isActive ? 0 : -1}
                onClick={(e) => {
                  e.stopPropagation();
                  close(q.id);
                }}
              >
                <IconX size={12} stroke={2} />
              </button>
            </div>
          );
        })}
        <button className="icon-button query-tabs-add" title="New query" onClick={add}>
          <IconPlus size={16} stroke={1.8} />
        </button>
      </div>
      {model && <QueryTab key={active.id} db={db} model={model} query={active} onChange={(changes) => patch(active.id, changes)} />}
    </div>
  );
}

/**
 * One query and its result.
 *
 * The facets are whatever the engine finds facetable in the current result set - nothing here
 * names a property - so this page works against a data model it has never seen. Selections are
 * posted back as the exact tokens the server sent, never as anything built in the browser. The rail
 * starts closed and the query then asks for no buckets at all: counting them is the expensive half
 * of a search, and most visits here are a search rather than a drill-down.
 *
 * The two sliders are the search itself, not a filter on it: the semantic ratio decides how much
 * of the ranking comes from the vector index rather than the word index, and the similarity floor
 * decides how close a vector match has to be to count at all. Both are left at the database's own
 * defaults until someone moves them, which is why they can be reset rather than only set.
 *
 * Every change runs at once - see useLiveResult for why nothing is debounced.
 *
 * The editor opens beside the result list rather than over it, so working through a set of nodes is
 * a click per node and the list keeps its scroll position between them.
 */
function QueryTab({ db, model, query: q, onChange }: { db: DatabaseInfo; model: QueryModel; query: SavedQuery; onChange: (changes: Partial<SavedQuery>) => void }) {
  // a type the model no longer has - or never named - falls back to the base type
  const typeId = q.typeId !== null && model.types.some((t) => t.id === q.typeId) ? q.typeId : model.baseTypeId;
  const { text, semanticRatio, minimumSimilarity: minSimilarity, selections, showFacets, mode, hitsView, sort, pageSize } = q;
  const [expanded, setExpanded] = useState<string[]>([]);
  const [page, setPage] = useState(0);
  // search shows the hits as a list or a table; select as a table of chosen columns; the groups and
  // the pivot summarize them, as views of the same search
  const select = mode === "select";
  const pivot = mode === "pivot";
  const groups = mode === "groups";
  const table = select || (mode === "search" && hitsView === "table");
  const summary = pivot || groups; // no hits on screen: no paging, no csv of hits, no query string of the search
  const [exporting, setExporting] = useState(false);
  const [showQuery, setShowQuery] = useState(false);
  const [selected, setSelected] = useState<string | null>(null);
  // The editor column's width, dragged on the bar between the list and the form. null is the
  // stylesheet's own share of the page, which is where most people leave it; a width someone has
  // dragged is theirs for good, so it outlives the page and the session.
  const [editorWidth, setEditorWidth] = useState<number | null>(() => {
    const saved = Number(localStorage.getItem(editorWidthKey));
    return Number.isFinite(saved) && saved >= minEditorWidth ? saved : null;
  });
  const [resizing, setResizing] = useState(false);
  const body = useRef<HTMLDivElement>(null);
  const results = useRef<HTMLDivElement>(null);
  const editor = useRef<HTMLElement>(null);
  const searchBox = useRef<HTMLInputElement>(null);

  // the columns the select mode can pick from, per type; the mode opens with a handful chosen
  const [available, setAvailable] = useState<SelectColumn[] | null>(null);
  useEffect(() => {
    if (!select) return;
    let cancelled = false;
    setAvailable(null);
    fetchColumns(db.id, typeId)
      .then((r) => {
        if (cancelled) return;
        setAvailable(r.columns);
        if (q.columns === null) {
          const properties = r.columns.filter((c) => !c.key.startsWith("__")).slice(0, 2);
          onChange({ columns: [...defaultSelectKeys, ...properties.map((c) => c.key)] });
        }
      })
      .catch(() => !cancelled && setAvailable([]));
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the default is decided once per type
  }, [db.id, typeId, select]);

  const selectionList = useMemo<FacetSelection[]>(() => selections.filter((s) => s.values.length > 0), [selections]);

  // one object per distinct search: the runner treats a new object as a new request
  const query = useMemo<SearchRequest | null>(
    () =>
      select && q.columns === null
        ? null // the columns are still being chosen; a table with none would be a row of nothing
        : {
            storeId: db.id,
            typeId,
            text,
            semanticRatio,
            minimumSimilarity: minSimilarity,
            selections: selectionList,
            expanded,
            page,
            pageSize,
            table,
            columns: select ? q.columns : null,
            facets: showFacets,
            sortBy: sort?.key ?? null,
            sortDescending: sort?.descending ?? false,
          },
    [db.id, typeId, text, semanticRatio, minSimilarity, selectionList, expanded, page, pageSize, table, select, q.columns, showFacets, sort],
  );

  const { result, loading, error, refresh } = useLiveResult(query, runSearch);

  // the database changed under the page as a whole (a rollback, a reconnect), or someone asked for
  // the result again: the list is searched again and the open node read again - it may now be a
  // different node, or none. The summaries take the same token and run again with it.
  const [epoch, setEpoch] = useState(0);
  function refreshAll() {
    refresh();
    setEpoch((e) => e + 1);
  }
  useEffect(
    () =>
      subscribeResync(() => {
        refresh();
        setEpoch((e) => e + 1);
      }),
    [refresh],
  );

  // the summaries' source: the search as this page has it, without the paging and the view switches
  const pivotBase = useMemo<PivotBase>(
    () => ({ storeId: db.id, typeId, text, semanticRatio, minimumSimilarity: minSimilarity, selections: selectionList }),
    [db.id, typeId, text, semanticRatio, minSimilarity, selectionList],
  );

  // a pivot cell clicked: its groups become the facet selection, and the list shows the nodes behind
  // the number. A selection on a property the rail already filters by is replaced, not added to.
  function drill(from: FacetSelection[]) {
    const next = selections.filter((s) => !from.some((f) => f.propertyId === s.propertyId));
    reset({ selections: [...next, ...from], mode: "search" });
  }

  // Any change to what is being searched starts the result list over, and closes the node open
  // beside it: the form belongs to a hit in the list it came from, and once that list is a
  // different search it is no longer clear what is being edited or why it is still on screen.
  // Paging and the view switches do not go through here - they are the same search, still.
  function reset(changes: Partial<SavedQuery>) {
    setPage(0);
    setSelected(null);
    onChange(changes);
  }

  // A column header cycles through the three states a sort can be in: up, down, and the order the
  // store itself returns. Sorting is a different view of the same search, so the open node stays
  // open - only the page goes back to the first.
  function toggleSort(key: string) {
    setPage(0);
    onChange({ sort: sort?.key !== key ? { key, descending: false } : sort.descending ? null : { key, descending: true } });
  }

  function toggleFacet(facet: Facet, value: FacetValue) {
    const current = selections.find((s) => s.propertyId === facet.propertyId)?.values ?? [];
    const key = keyOf(value);
    const values = current.some((v) => keyOf(v) === key) ? current.filter((v) => keyOf(v) !== key) : [...current, { value: value.value, value2: value.value2 }];
    reset({ selections: [...selections.filter((s) => s.propertyId !== facet.propertyId), { propertyId: facet.propertyId, values }] });
  }

  // How wide the editor may be pulled right now: never so wide that the list it belongs to is
  // gone, and never so narrow that the form cannot show a field. What the two columns hold between
  // them is measured rather than worked out, so the facet rail and the gaps around it need no
  // arithmetic here - and while a drag is in progress that total does not move, only the split of
  // it does. The share the stylesheet caps at is a limit here too, so the bar stops where the
  // pointer stops mattering instead of running on past an edge that has stopped moving.
  function widthLimits() {
    const available = body.current?.getBoundingClientRect().width ?? 0;
    const shared = (editor.current?.getBoundingClientRect().width ?? 0) + (results.current?.getBoundingClientRect().width ?? 0);
    const room = Math.min(shared - minResultsWidth, available * maxEditorShare);
    return { min: minEditorWidth, max: Math.max(minEditorWidth, room) };
  }

  /** The width as it can be honoured now; the caller decides whether to keep it. */
  function applyWidth(width: number) {
    const { min, max } = widthLimits();
    return Math.round(Math.min(Math.max(width, min), max));
  }

  function setWidth(width: number) {
    const clamped = applyWidth(width);
    setEditorWidth(clamped);
    return clamped;
  }

  // The bar is dragged rather than stepped, so the pointer is captured for the duration: the
  // pointer leaves the 17 pixels of the bar on the very first move, and without the capture the
  // drag would end there. The width is written back once, on release, not on every frame.
  function startResize(e: React.PointerEvent<HTMLDivElement>) {
    if (e.button !== 0 || !editor.current) return;
    e.preventDefault();
    const startX = e.clientX;
    const startWidth = editor.current.getBoundingClientRect().width;
    let width = startWidth;
    e.currentTarget.setPointerCapture(e.pointerId);
    setResizing(true);
    const move = (ev: PointerEvent) => (width = setWidth(startWidth - (ev.clientX - startX)));
    const end = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", end);
      window.removeEventListener("pointercancel", end);
      setResizing(false);
      localStorage.setItem(editorWidthKey, String(Math.round(width)));
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", end);
    window.addEventListener("pointercancel", end);
  }

  // The rail opening, or the window narrowing, can leave a dragged width too wide for what is now
  // beside it. Pull it back in rather than squeezing the list the form was opened from - without
  // writing it back, so the width someone actually dragged is still theirs when the room returns.
  useEffect(() => {
    if (!selected || editorWidth === null) return;
    const fit = () => setEditorWidth((w) => (w === null ? w : applyWidth(w)));
    fit();
    window.addEventListener("resize", fit);
    return () => window.removeEventListener("resize", fit);
  }, [selected, showFacets, editorWidth]);

  // the same bar from the keyboard, and a double click to hand the width back to the stylesheet
  function resizeByKey(e: React.KeyboardEvent) {
    if (e.key !== "ArrowLeft" && e.key !== "ArrowRight") return;
    if (!editor.current) return;
    e.preventDefault();
    const from = editor.current.getBoundingClientRect().width;
    localStorage.setItem(editorWidthKey, String(setWidth(from + (e.key === "ArrowLeft" ? 24 : -24))));
  }

  function resetWidth() {
    setEditorWidth(null);
    localStorage.removeItem(editorWidthKey);
  }

  async function download() {
    if (!query) return;
    setExporting(true);
    try {
      await exportCsv(query);
    } catch (e) {
      await showError("Could not export", e instanceof Error ? e.message : String(e));
    } finally {
      setExporting(false);
    }
  }

  const selectedCount = selectionList.reduce((n, s) => n + s.values.length, 0);
  const chosen = q.columns ?? [];
  const columnName = (key: string) => available?.find((c) => c.key === key);

  const semanticAvailable = model.hasAi && model.hasSemanticIndex;
  const lastPage = result ? Math.max(0, Math.ceil(result.total / pageSize) - 1) : 0;
  return (
    <>
      <div className="query-toolbar">
        <TypePicker
          types={model.types}
          sources={model.sources ?? []}
          value={typeId}
          onChange={(id) => {
            reset({
              typeId: id,
              selections: [], // the facets of another type are different properties
              sort: null, // and its columns are different properties too
              columns: null, // as are the ones the select mode chose
              pivot: null, // and what the summaries group by
              groups: null,
            });
            setExpanded([]);
          }}
          // the type is chosen, the search box is where the next thing happens
          onPicked={() => searchBox.current?.focus()}
        />
        <div className="query-search">
          <IconSearch size={15} stroke={1.8} />
          <input
            className="text-input"
            ref={searchBox}
            // the page exists to be searched, and it only mounts when someone asks for it, so the
            // caret starts here rather than one click away
            autoFocus
            value={text}
            placeholder="Free text search — leave empty to browse everything"
            spellCheck={false}
            onChange={(e) => reset({ text: e.target.value })}
          />
          {text && (
            <button
              className="icon-button"
              title="Clear the search text"
              onClick={() => {
                reset({ text: "" });
                searchBox.current?.focus(); // the button is about to disappear; the caret should not go with it
              }}
            >
              <IconX size={14} stroke={1.8} />
            </button>
          )}
        </div>
        {/* what kind of query this is: the hits themselves, a table of chosen columns, or a summary */}
        <div className="query-view" role="tablist">
          {modes.map((m) => (
            <button key={m.id} role="tab" aria-selected={mode === m.id} className={mode === m.id ? "active" : ""} title={m.hint} onClick={() => onChange({ mode: m.id })}>
              <m.icon size={14} stroke={1.8} />
              {m.label}
            </button>
          ))}
        </div>
        <button className="icon-button" title="Run the query again" onClick={refreshAll}>
          <IconRefresh size={16} stroke={1.8} className={loading ? "spinning" : ""} />
        </button>
        <button className={"icon-button" + (showQuery ? " armed" : "")} title="Show the query this page sends" onClick={() => setShowQuery(!showQuery)}>
          <IconCode size={16} stroke={1.8} />
        </button>
      </div>

      {/* Always here, whatever the database can do: a control that comes and goes is one nobody
          trusts, and where the two knobs stand is worth reading even when they are not in play.
          They are live as soon as this database can search semantically - setting the ratio before
          typing is a perfectly good order to work in - and only greyed when it cannot, with a note
          saying which half is missing. */}
      <div className="query-sliders">
        <Slider
          label="Semantic ratio"
          hint="0 is words only, 1 is vectors only"
          value={semanticRatio}
          fallback={model.defaultSemanticRatio}
          disabled={!semanticAvailable}
          onChange={(v) => reset({ semanticRatio: v })}
        />
        <Slider
          label="Minimum similarity"
          hint="how close a vector match has to be to count"
          value={minSimilarity}
          fallback={model.defaultMinimumSimilarity}
          disabled={!semanticAvailable}
          onChange={(v) => reset({ minimumSimilarity: v })}
        />
        {!model.hasAi && <span className="query-note">No AI provider is configured for this database, so a search matches words only.</span>}
        {model.hasAi && !model.hasSemanticIndex && <span className="query-note">Nothing in this data model is semantically indexed, so both sliders are inert.</span>}
      </div>

      {select && (
        // the columns of the select mode, in the order they were chosen; a column is added from what
        // the type has left to offer and taken away on its own chip
        <div className="query-columns">
          <span className="pivot-builder-label">Columns</span>
          {chosen.map((key) => {
            const column = columnName(key);
            return (
              <span className="query-column" key={key} title={column ? column.type + (column.declaredBy ? " · from " + column.declaredBy : "") : key}>
                {column?.name ?? key}
                <button className="icon-button" title="Remove this column" onClick={() => onChange({ columns: chosen.filter((k) => k !== key) })}>
                  <IconX size={12} stroke={2} />
                </button>
              </span>
            );
          })}
          <select
            className="select compact"
            value=""
            disabled={available === null}
            title="Add a column"
            onChange={(e) => e.target.value && onChange({ columns: [...chosen, e.target.value] })}
          >
            <option value="">{available === null ? "loading…" : "+ add column…"}</option>
            {available
              ?.filter((c) => !chosen.includes(c.key))
              .map((c) => (
                <option key={c.key} value={c.key}>
                  {c.name}
                  {c.declaredBy ? " (" + c.declaredBy + ")" : ""} · {c.type}
                </option>
              ))}
          </select>
          {chosen.length > 0 && (
            <button className="link-button" onClick={() => onChange({ columns: [] })}>
              clear
            </button>
          )}
        </div>
      )}

      {showQuery && result && !summary && <div className="query-string">{result.query}</div>}
      {error && !summary && <div className="query-error">{error}</div>}

      <div
        ref={body}
        className={"query-body" + (selected ? " with-editor" : "") + (showFacets ? "" : " no-facets") + (resizing ? " resizing" : "")}
        // capped as a share of the page as well as in pixels: a width dragged on a wide window
        // would otherwise leave nothing of the list on a narrow one
        style={editorWidth === null ? undefined : ({ "--editor-width": `min(${editorWidth}px, ${maxEditorShare * 100}%)` } as React.CSSProperties)}
      >
        {showFacets && (
          <aside className="query-facets">
            <div className="query-facets-head">
              <span>Facets</span>
              {selectedCount > 0 && (
                <button className="link-button" onClick={() => reset({ selections: [] })}>
                  clear {selectedCount}
                </button>
              )}
            </div>
            {result?.facets.length === 0 && <div className="query-empty">Nothing in this result set is facetable.</div>}
            {result?.facets.map((facet) => (
              <section className="query-facet" key={facet.propertyId}>
                <h4 title={facet.codeName + " · " + facet.valueType}>{facet.displayName}</h4>
                {facet.values.map((v) => (
                  <button className={"query-facet-value" + (v.selected ? " selected" : "")} key={keyOf(v)} onClick={() => toggleFacet(facet, v)}>
                    <span className="query-facet-check" aria-hidden />
                    <span className="query-facet-label" title={v.display}>
                      {v.display}
                    </span>
                    <span className="query-facet-count">{formatCount(v.count)}</span>
                  </button>
                ))}
                {facet.truncated && (
                  <button className="link-button" onClick={() => setExpanded([...expanded, facet.propertyId])}>
                    show all {formatCount(facet.totalValues)}
                  </button>
                )}
              </section>
            ))}
          </aside>
        )}

        <div className="query-results" ref={results}>
          <div className="query-results-head">
            {result ? (
              <>
                <strong>{formatCount(result.total)}</strong>
                <span className="muted">
                  {result.total === 1 ? "node" : "nodes"}
                  {result.total !== result.sourceCount ? ` of ${formatCount(result.sourceCount)}` : ""} · {result.durationMs.toFixed(1)} ms
                </span>
              </>
            ) : (
              <span className="muted">Searching…</span>
            )}
            {!showFacets && selectedCount > 0 && (
              // the rail is where a selection is normally seen and undone; with it closed, saying so
              // here is the difference between a filtered result and one that looks wrong
              <span className="query-filters">
                {formatCount(selectedCount)} {selectedCount === 1 ? "filter" : "filters"}
                <button className="link-button" onClick={() => reset({ selections: [] })}>
                  clear
                </button>
              </span>
            )}
            <div className="query-spacer" />
            <button
              className={"icon-button" + (showFacets ? " active" : "")}
              title={showFacets ? "Hide the facets" : "Show the facets — the search then counts their values"}
              onClick={() => onChange({ showFacets: !showFacets })}
            >
              <IconFilter size={16} stroke={1.8} />
            </button>
            {mode === "search" && (
              // how the hits are shown; the other modes are each one view
              <div className="query-view" role="tablist">
                {(
                  [
                    { id: "list", label: "List", icon: IconLayoutList, hint: "Show the hits as a list" },
                    { id: "table", label: "Table", icon: IconTable, hint: "Show the hits as a table, one column per property" },
                  ] as { id: HitsView; label: string; icon: typeof IconTable; hint: string }[]
                ).map((v) => (
                  <button key={v.id} role="tab" aria-selected={hitsView === v.id} className={hitsView === v.id ? "active" : ""} title={v.hint} onClick={() => onChange({ hitsView: v.id })}>
                    <v.icon size={14} stroke={1.8} />
                    {v.label}
                  </button>
                ))}
              </div>
            )}
            {!summary && (
              <select className="select compact" value={pageSize} title="Rows per page" onChange={(e) => reset({ pageSize: Number(e.target.value) })}>
                {pageSizes.map((size) => (
                  <option key={size} value={size}>
                    {size} / page
                  </option>
                ))}
              </select>
            )}
            {!summary && (
              <button
                className="icon-button"
                disabled={exporting || !result || result.total === 0}
                title={`Download the whole result set as csv (up to ${formatCount(csvRowLimit)} rows)`}
                onClick={download}
              >
                <IconDownload size={16} stroke={1.8} />
              </button>
            )}
            {!summary && result && result.total > pageSize && (
              <div className="query-paging">
                <button className="icon-button" disabled={page === 0} title="Previous page" onClick={() => setPage(page - 1)}>
                  <IconChevronLeft size={15} stroke={1.8} />
                </button>
                <span className="muted">
                  {page * pageSize + 1}–{Math.min((page + 1) * pageSize, result.total)}
                </span>
                <button className="icon-button" disabled={page >= lastPage} title="Next page" onClick={() => setPage(page + 1)}>
                  <IconChevronRight size={15} stroke={1.8} />
                </button>
              </div>
            )}
          </div>
          {pivot ? (
            // keyed by type: another type has other properties, so the definition starts over with it
            <PivotView key={typeId} base={pivotBase} definition={q.pivot} onChange={(pivot) => onChange({ pivot })} refreshToken={epoch} showQuery={showQuery} onDrill={drill} />
          ) : groups ? (
            <GroupByView key={typeId} base={pivotBase} definition={q.groups} onChange={(groups) => onChange({ groups })} refreshToken={epoch} showQuery={showQuery} onDrill={drill} />
          ) : select && chosen.length === 0 ? (
            <div className="query-empty">Choose at least one column.</div>
          ) : table && result?.columns ? (
            <div className={"query-table-wrap" + (loading ? " loading" : "")}>
              <table className="query-table">
                <thead>
                  <tr>
                    {result.columns.map((column) => (
                      <th
                        key={column.key}
                        className={(column.sortable ? "sortable" : "") + (sort?.key === column.key ? (result.sortApplied ? " sorted" : " sorted-inactive") : "")}
                        title={
                          sort?.key === column.key && !result.sortApplied
                            ? "Sorted by this column, but a facet selection is filtering and the rows come back in the database's own order"
                            : column.sortable
                              ? `${column.type} — click to sort`
                              : `${column.type} — cannot be sorted on`
                        }
                        onClick={column.sortable ? () => toggleSort(column.key) : undefined}
                      >
                        {column.name}
                        {sort?.key === column.key && (sort.descending ? <IconArrowNarrowDown size={13} stroke={2} /> : <IconArrowNarrowUp size={13} stroke={2} />)}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {result.hits.map((hit) => (
                    <tr key={hit.id} className={selected === hit.id ? "selected" : ""} onClick={() => setSelected(hit.id)}>
                      {(hit.cells ?? []).map((value, i) => (
                        <td key={result.columns![i]?.key ?? i} title={value}>
                          {value}
                        </td>
                      ))}
                    </tr>
                  ))}
                </tbody>
              </table>
              {result.hits.length === 0 && <div className="query-empty">Nothing matched.</div>}
            </div>
          ) : (
            <div className={"query-hits" + (loading ? " loading" : "")}>
              {result?.hits.length === 0 && <div className="query-empty">Nothing matched.</div>}
              {result?.hits.map((hit) => (
                <button className={"query-hit" + (selected === hit.id ? " selected" : "")} key={hit.id} onClick={() => setSelected(hit.id)}>
                  <div className="query-hit-head">
                    <span className="query-hit-name" title={hit.displayName}>
                      <Sampled sample={hit.nameSample} plain={hit.displayName} />
                    </span>
                    <span className="query-hit-type">{hit.typeName}</span>
                    <span className="query-hit-time">{formatTime(hit.changedUtc)}</span>
                  </div>
                  {hit.snippet && (
                    <div className="query-hit-snippet" title={hit.snippet.value}>
                      <em>{hit.snippet.codeName}</em> <Sampled sample={hit.snippet.sample} plain={hit.snippet.value} />
                    </div>
                  )}
                  {hit.summary.length > 0 && (
                    <div className="query-hit-summary">
                      {hit.summary.map((s) => (
                        <span key={s.codeName}>
                          <em>{s.codeName}</em> {s.value}
                        </span>
                      ))}
                    </div>
                  )}
                </button>
              ))}
            </div>
          )}
        </div>

        {selected && (
          <aside className="query-editor" ref={editor}>
            <div
              className="query-splitter"
              role="separator"
              aria-orientation="vertical"
              aria-label="Width of the editor"
              title="Drag to resize the editor · double-click to reset"
              tabIndex={0}
              onPointerDown={startResize}
              onDoubleClick={resetWidth}
              onKeyDown={resizeByKey}
            />
            <NodeEditor key={selected + ":" + epoch} storeId={db.id} nodeId={selected} onSaved={refresh} onClose={() => setSelected(null)} />
          </aside>
        )}
      </div>
    </>
  );
}

/**
 * A search knob that has a database default. Until it is moved it shows that default and sends
 * nothing, so the query keeps whatever the engine would have used on its own; the reset puts it
 * back into that state rather than to a number that happens to look the same today.
 */
function Slider({
  label,
  hint,
  value,
  fallback,
  disabled,
  onChange,
}: {
  label: string;
  hint: string;
  value: number | null;
  fallback: number;
  disabled: boolean;
  onChange: (value: number | null) => void;
}) {
  const shown = value ?? fallback;
  // deliberately not a <label>: a click on the reset button inside one would be forwarded to the
  // range input and move the slider it was meant to put back
  return (
    <div className={"query-slider" + (disabled ? " disabled" : "")} title={hint}>
      <span className="query-slider-label">{label}</span>
      <input type="range" min={0} max={1} step={0.01} value={shown} disabled={disabled} onChange={(e) => onChange(Number(e.target.value))} />
      <span className="query-slider-value">{shown.toFixed(2)}</span>
      {value === null ? (
        <span className="setting-badge faint">default</span>
      ) : (
        <button className="link-button" onClick={() => onChange(null)}>
          reset
        </button>
      )}
    </div>
  );
}
