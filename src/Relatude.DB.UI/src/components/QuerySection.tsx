import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  IconArrowNarrowDown,
  IconArrowNarrowUp,
  IconChartBar,
  IconChartHistogram,
  IconCloud,
  IconChevronLeft,
  IconChevronRight,
  IconCode,
  IconDownload,
  IconFilter,
  IconLayoutList,
  IconMap2,
  IconMarquee2,
  IconSelectAll,
  IconPlus,
  IconRefresh,
  IconSearch,
  IconSparkles,
  IconSum,
  IconPencil,
  IconTable,
  IconX,
} from "@tabler/icons-react";
import { NodeEditor } from "./NodeEditor";
import { PivotView, emptyPivot, type PivotBase } from "./PivotView";
import { GroupByView, emptyGroupBy } from "./GroupByView";
import { VisualPivotView, emptyVisual } from "./VisualPivotView";
import { MapView, emptyMap } from "./MapView";
import { WordCloudView } from "./WordCloudView";
import { EditableTable } from "./EditableTable";
import { AddColumnHead, ColumnHead, useColumnDrag, useColumnSizing, type TableColumnsUi } from "./TableColumns";
import { CopyButton } from "./CopyButton";
import { NewNodeDialog, TypePicker } from "./TypePicker";
import { showChoice, showError } from "../dialogs";
import { peekSearchTarget, takeQueryTarget, takeSearchTarget, useNavigationRequest } from "../navigate";
import {
  createNode,
  csvRowLimit,
  exportCsv,
  maxPageRows,
  fetchColumns,
  runSearch,
  fetchQueryModel,
  type Facet,
  type FacetSelection,
  type FacetValue,
  type QueryModel,
  type SearchRequest,
  type PivotLevelSpec,
  type SelectColumn,
  type TextSample,
} from "../server/query";
import { useLiveResult } from "../server/hooks";
import { subscribeResync } from "../server/channel";
import type { DatabaseInfo } from "../server/serverInfo";
import { formatCount, formatQuery, formatTime } from "../format";
import { loadTabs, newQuery, saveTabs, type HitsView, type QueryMode, type QueryTabs, type SavedQuery } from "../queryTabs";
import { useRowWindow } from "../rowWindow";
import { applyMarquee, applySelect, noSelection, selectedInts, selectionCount, selectModeOf, type MarqueeMode, type PageSelection, type SelectMode } from "../selection";
import { marqueeModeClass, useListMarquee, useMarqueeMode } from "../marquee";

// How many hits one page holds. The large ones are for reading a whole set in one go - a table
// someone is going to scroll, or export - and are asked for deliberately; "all" (0 here) is one
// page as large as the server will build, which is maxPageRows.
const pageSizes = [25, 50, 100, 200, 1000, 10_000, 100_000];

// The round numbers the csv export offers, beside the page on screen and the whole result set.
const csvRowChoices = [1000, 10_000, 100_000];

const rowCount = (n: number) => formatCount(n) + (n === 1 ? " row" : " rows");

const editorWidthKey = "queryEditorWidth";
const minEditorWidth = 320; // narrower and the form's own labels start wrapping
const minResultsWidth = 320; // wider and the list the form was opened from stops being readable
const maxEditorShare = 0.6; // and a share of the page as well, so a narrower window keeps a list

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
  { id: "search", label: "Search", icon: IconSearch, hint: "The nodes that match, as a list or a table of the columns you choose" },
  { id: "groups", label: "Group by", icon: IconSum, hint: "One row per value of a property, with a count and aggregates (SQL's GROUP BY)" },
  { id: "pivot", label: "Pivot", icon: IconChartBar, hint: "Groups by property on two axes, a count or sum per cell" },
  { id: "visual", label: "Visual pivot", icon: IconChartHistogram, hint: "Every node as a card: coloured by one property, stacked into bars by another" },
  { id: "map", label: "Map", icon: IconMap2, hint: "Every node where it is: on a world map or a globe, as pins, dots, heat, clusters or shaded countries" },
  { id: "cloud", label: "Word cloud", icon: IconCloud, hint: "The words the result is written with, sized by how much of it they account for; click one to search for it" },
];

/**
 * The two groupings every summary is built on, in the order they read: what splits the picture one
 * way, and what splits it the other. They are one pair of choices wearing three sets of names - the
 * group by's first and second key, the pivot's rows and columns, the visual pivot's bars and colours
 * - so moving between the views carries them along instead of asking for the same thing again in
 * each. The hits have no groupings and give nothing.
 */
function groupingOf(q: SavedQuery, mode: QueryMode): [PivotLevelSpec | null, PivotLevelSpec | null] {
  if (mode === "groups") return [q.groups?.keys[0] ?? null, q.groups?.keys[1] ?? null];
  if (mode === "pivot") return [q.pivot?.rows[0] ?? null, q.pivot?.columns[0] ?? null];
  if (mode === "visual" && q.visual !== null) {
    const v = q.visual;
    return [
      v.barProperty === null ? null : { propertyId: v.barProperty, mode: v.barMode },
      v.colorProperty === null ? null : { propertyId: v.colorProperty, mode: v.colorMode },
    ];
  }
  // the map has one grouping and it is a colouring, so it fills the second slot and leaves the
  // first alone: what a map is split BY is the world, which no other view has a place for
  if (mode === "map" && q.map !== null) return [null, q.map.colorProperty === null ? null : { propertyId: q.map.colorProperty, mode: q.map.colorMode }];
  return [null, null];
}

/**
 * How a level is bucketed, said in the words the view being entered knows. The three vocabularies
 * overlap without being the same: the group by has no "auto" - a key there is always bucketed some
 * definite way - and the visual pivot knows nothing of the calendar intervals. What does not
 * translate falls back to the nearest thing that means the same, so a level always arrives with a
 * mode its new view can offer.
 */
function modeFor(view: QueryMode, mode: string): string {
  if (view === "groups") return mode === "auto" ? "values" : mode;
  if (view === "visual" || view === "map") return mode === "values" || mode === "ranges" ? mode : "auto";
  return mode;
}

/**
 * The summary definitions of `q` with the groupings of the view being left carried into the one
 * being entered. Only a slot the old view actually filled is carried: a group by on one key does not
 * empty the pivot's columns on the way past, and leaving the hits - which group by nothing - leaves
 * every summary exactly as it was. What the new view has beyond those two slots (deeper levels, the
 * aggregates, its own options) is untouched.
 */
function carryGrouping(q: SavedQuery, from: QueryMode, to: QueryMode): Partial<SavedQuery> {
  if (from === to) return {};
  const [first, second] = groupingOf(q, from);
  if (first === null && second === null) return {};
  const as = (view: QueryMode, l: PivotLevelSpec): PivotLevelSpec => ({ propertyId: l.propertyId, mode: modeFor(view, l.mode) });
  if (to === "groups") {
    const base = q.groups ?? emptyGroupBy;
    // the two slots are the first two keys of one list here, so they are rebuilt as a pair: a
    // grouping that arrives without a first one becomes the first key rather than leaving a hole
    const k0 = first !== null ? as("groups", first) : (base.keys[0] ?? null);
    const k1 = second !== null ? as("groups", second) : (base.keys[1] ?? null);
    const keys = [k0, k1, ...base.keys.slice(2)].filter((k): k is PivotLevelSpec => k !== null);
    return { groups: { ...base, keys } };
  }
  if (to === "pivot") {
    const base = q.pivot ?? emptyPivot;
    return {
      pivot: {
        ...base,
        rows: first !== null ? [as("pivot", first), ...base.rows.slice(1)] : base.rows,
        columns: second !== null ? [as("pivot", second), ...base.columns.slice(1)] : base.columns,
      },
    };
  }
  if (to === "visual") {
    const base = q.visual ?? emptyVisual;
    return {
      visual: {
        ...base,
        ...(first !== null ? { barProperty: first.propertyId, barMode: modeFor("visual", first.mode) } : {}),
        ...(second !== null ? { colorProperty: second.propertyId, colorMode: modeFor("visual", second.mode) } : {}),
      },
    };
  }
  if (to === "map") {
    // whichever of the two the view being left had: the map has one colouring, and arriving at it
    // from a group by on a single key should colour by that key rather than by nothing
    const carried = second ?? first;
    if (carried === null) return {};
    return { map: { ...(q.map ?? emptyMap), colorProperty: carried.propertyId, colorMode: modeFor("map", carried.mode) } };
  }
  return {};
}

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

  /**
   * The model again, for the count each type carries in the picker: nodes have been written or
   * deleted, so those numbers are as stale as the result was. Read in place rather than through the
   * effect above, which clears the model first and would take the whole page down to a spinner for
   * the four milliseconds this takes; a failure leaves the counts as they were, which is a number
   * slightly out of date rather than a page that has stopped working.
   */
  const refreshCounts = useCallback(() => {
    fetchQueryModel(db.id)
      .then(setModel)
      .catch(() => {});
  }, [db.id]);

  const active = tabs.queries.find((q) => q.id === tabs.active) ?? tabs.queries[0];

  // Another page asking for a query on a type - the dashboard's treemap, the global search - gets a
  // fresh tab on it, so whatever was open here stays as it was. A request naming a node wants that
  // node in the form as well; which node is open is the tab's own state and not part of the saved
  // query, so it is handed to the tab rather than written into it.
  // it belongs to the tab that was made for it, so a tab someone opens later does not inherit it
  const [openNode, setOpenNode] = useState<{ tabId: string; nodeId: string } | null>(null);
  const navigation = useNavigationRequest();
  useEffect(() => {
    const target = takeQueryTarget();
    if (target) {
      const q = newQuery();
      q.typeId = target.typeId;
      if (target.text) q.text = target.text;
      setOpenNode(target.nodeId ? { tabId: q.id, nodeId: target.nodeId } : null);
      setTabs((t) => ({ active: q.id, queries: [...t.queries, q] }));
      return;
    }
    // words handed over from the global search box: a fresh tab searching every type for them,
    // which is the same search the box ran, with the paging and the facets it had no room for
    if (peekSearchTarget()?.section !== "query") return;
    const q = newQuery();
    q.text = takeSearchTarget()!.text;
    setOpenNode(null);
    setTabs((t) => ({ active: q.id, queries: [...t.queries, q] }));
  }, [navigation]);

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
      {model && (
        <QueryTab
          key={active.id}
          db={db}
          model={model}
          query={active}
          openNode={openNode?.tabId === active.id ? openNode.nodeId : null}
          onChange={(changes) => patch(active.id, changes)}
          onNodesChanged={refreshCounts}
        />
      )}
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
 * defaults until someone moves them, which is why they can be reset rather than only set, and they
 * are on screen from the start wherever the database has an AI provider - a search there is already
 * half vectors whether or not anyone opened the panel.
 *
 * Every change runs at once - see useLiveResult for why nothing is debounced.
 *
 * The editor opens beside the result list rather than over it, so working through a set of nodes is
 * a click per node and the list keeps its scroll position between them. Several nodes can be open
 * at once - ctrl-click adds one, shift-click a run of them, ctrl+A the page (see selection.ts), or a
 * rectangle dragged round them with the Select switch on (see marquee.tsx) - and the form then edits
 * them together.
 */
function QueryTab({
  db,
  model,
  query: q,
  openNode,
  onChange,
  onNodesChanged,
}: {
  db: DatabaseInfo;
  model: QueryModel;
  query: SavedQuery;
  /** a node someone asked to have open here, from another page or the global search */
  openNode: string | null;
  onChange: (changes: Partial<SavedQuery>) => void;
  /** nodes were written or deleted from this tab: the counts the type picker shows are stale */
  onNodesChanged: () => void;
}) {
  // a type the model no longer has - or never named - falls back to the base type
  const typeId = q.typeId !== null && model.types.some((t) => t.id === q.typeId) ? q.typeId : model.baseTypeId;
  const { text, semanticRatio, minimumSimilarity: minSimilarity, selections, showFacets, mode, hitsView, sort, pageSize } = q;
  // drag to select (marquee.tsx): a drag draws a rectangle round nodes instead of dragging the picture
  const dragSelect = q.dragSelect === true;
  // The panel is open wherever there is an AI provider to search with, until someone on this query
  // says otherwise: a search against such a database is already part vectors - the engine resolves
  // the unset knobs to the database's own ratio - so how it is ranked should be on screen rather
  // than a click away. Without a provider it stays folded: nothing there would ever move.
  const showSemantic = q.showSemantic ?? model.hasAi;
  const [expanded, setExpanded] = useState<string[]>([]);
  const [page, setPage] = useState(0);
  // what one page actually holds: "all" (0) asks for the largest page the server will build, and a
  // result bigger than that is still paged - in pages of that size - rather than quietly cut off
  const pageRows = pageSize > 0 ? pageSize : maxPageRows;
  // search shows the hits, as a list or as a table of the columns it is told to show; the groups and
  // the pivot summarize them, as views of the same search
  const pivot = mode === "pivot";
  const groups = mode === "groups";
  const visual = mode === "visual";
  const map = mode === "map";
  const cloud = mode === "cloud";
  const table = mode === "search" && hitsView === "table";
  const summary = pivot || groups || visual || map || cloud; // no hits on screen: no paging, no csv of hits, no query string of the search
  const [exporting, setExporting] = useState(false);
  const [newNode, setNewNode] = useState(false);
  // typing straight into the table; kept per tab, like the view it belongs to
  const editCells = q.editCells === true;
  const [showQuery, setShowQuery] = useState(false);
  // The nodes the form beside the result has open, in the order they were chosen: one from a plain
  // click, several from ctrl-clicks and shift-clicks. The anchor is the row a shift-click takes its
  // run from - the last row clicked without shift.
  /**
   * What is selected, addressed by internal id (see PageSelection): a rectangle over a million cards
   * is then four bytes each and needs nothing resolved, which is what it used to spend twelve seconds
   * and several hundred megabytes doing. A node handed over by another page arrives as a guid and is
   * the one shape that is not an internal id.
   */
  const [selection, setSelection] = useState<PageSelection>(openNode ? { kind: "guids", ids: [openNode] } : noSelection);
  /** how many NODES are selected (selectedCount, further down, is how many facet values are) */
  const nodesSelected = selectionCount(selection);
  /** and whether that is the whole result set rather than nodes named one by one */
  const allSelected = selection.kind === "query";
  const marked = useMemo(() => new Set(selectedInts(selection)), [selection]);
  /** whether a row of the list or the table is in the selection: all of them while the whole result is */
  const isSelected = (hit: { intId: number }) => selection.kind === "query" || marked.has(hit.intId);
  const anchor = useRef<number | null>(null);
  // The editor column's width, dragged on the bar between the list and the form. null is the
  // stylesheet's own share of the page, which is where most people leave it; a width someone has
  // dragged is theirs for good, so it outlives the page and the session.
  const [editorWidth, setEditorWidth] = useState<number | null>(() => {
    const saved = Number(localStorage.getItem(editorWidthKey));
    return Number.isFinite(saved) && saved >= minEditorWidth ? saved : null;
  });
  const [resizing, setResizing] = useState(false);
  const [fullscreen, setFullscreen] = useState(false);
  const body = useRef<HTMLDivElement>(null);
  const results = useRef<HTMLDivElement>(null);
  const editor = useRef<HTMLElement>(null);
  const searchBox = useRef<HTMLInputElement>(null);

  // every column this type could show, for the picker in the table's last heading; the columns it IS
  // showing come back with the result, so the table needs none of this to be drawn
  const [available, setAvailable] = useState<SelectColumn[] | null>(null);
  // how wide the columns have been dragged. Kept per type: a column is the same column in every
  // query on it, and a width is about reading it, not about the query
  const columnSizing = useColumnSizing(typeId || "all");
  const columnDrag = useColumnDrag();
  useEffect(() => {
    if (!table) return;
    let cancelled = false;
    setAvailable(null);
    fetchColumns(db.id, typeId)
      .then((r) => !cancelled && setAvailable(r.columns))
      .catch(() => !cancelled && setAvailable([]));
    return () => {
      cancelled = true;
    };
  }, [db.id, typeId, table]);

  const selectionList = useMemo<FacetSelection[]>(() => selections.filter((s) => s.values.length > 0), [selections]);

  // one object per distinct search: the runner treats a new object as a new request
  const query = useMemo<SearchRequest | null>(
    () => ({
            storeId: db.id,
            typeId,
            text,
            semanticRatio,
            minimumSimilarity: minSimilarity,
            selections: selectionList,
            expanded,
            page,
            pageSize: pageRows,
            table,
            edit: table && editCells,
            // null asks for the type's own columns, which is what the table opens with
            columns: table ? q.columns : null,
            facets: showFacets,
            sortBy: sort?.key ?? null,
            sortDescending: sort?.descending ?? false,
            // the summary views draw their own picture of the result; this search is here for the
            // total and the facets, and the hits of it would be read and thrown away
            summary,
    }),
    [db.id, typeId, text, semanticRatio, minSimilarity, selectionList, expanded, page, pageRows, table, editCells, q.columns, showFacets, sort, summary],
  );

  const { result, loading, error, refresh } = useLiveResult(query, runSearch);

  // The rows of the page, and how many of them are built (see rowWindow): a page can be asked to
  // hold a hundred thousand, which is a query the store answers in a moment and a table the dom
  // cannot be handed in one piece. One array per result, so the window starts over with the search
  // and not with every render of it.
  const hits = useMemo(() => result?.hits ?? [], [result]);
  const rowWindow = useRowWindow(hits);

  const [epoch, setEpoch] = useState(0);
  const [formEpoch, setFormEpoch] = useState(0);
  /**
   * The nodes on this page are not what they were, so everything drawn from them runs again: the
   * list, the facets, and every summary view, which take `epoch` as their refreshToken.
   *
   * What it does NOT do is touch the form. A write made in the form is the commonest reason to be
   * here, and the form has just finished reading itself back (NodeEditor.reload) - taking it apart
   * and building it again would throw away the combined editor the write was made in and leave the
   * selection sitting on its summary again.
   */
  function refreshData() {
    refresh();
    setEpoch((e) => e + 1);
    onNodesChanged(); // the type picker counts nodes too, and is as stale as everything else
  }
  // the database changed under the page as a whole (a rollback, a reconnect), or someone asked for
  // the result again: everything above, and the form started over with it - what it has open may
  // now be a different node, or none
  function refreshAll() {
    refreshData();
    setFormEpoch((e) => e + 1);
  }
  useEffect(
    () =>
      subscribeResync(() => {
        refresh();
        setEpoch((e) => e + 1);
        setFormEpoch((e) => e + 1);
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
  /**
   * A word of the cloud was clicked. Not a drill the way a group is - a word is not a value of a
   * property but something the text holds - so it goes into the search box, where it narrows the
   * result to the nodes holding it. The words come out of the same index the search reads, so this
   * always finds something.
   */
  function searchWord(word: string) {
    const already = q.text.split(/\s+/).some((w) => w.toLowerCase() === word.toLowerCase());
    reset({ text: already ? q.text : (q.text.trim() + " " + word).trim(), mode: "search" });
  }

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
    setSelection(noSelection); // a different search is a different result set, and a different selection
    onChange(changes);
  }

  /** What is selected now, as ids to work from: a query or a guid selection has none to add to. */
  function currentInts(): number[] {
    return selection.kind === "ints" ? selection.ids : [];
  }

  /** A row of the list or the table clicked, with whatever keys were held (see selection.ts). */
  function selectHit(e: React.MouseEvent, hit: { intId: number }) {
    const mode = selectModeOf(e);
    const order = hits.map((h) => h.intId);
    setSelection({ kind: "ints", ids: applySelect(currentInts(), hit.intId, mode, { order, anchor: anchor.current, keep: e.ctrlKey || e.metaKey }) });
    if (mode !== "extend") anchor.current = hit.intId;
  }

  /** A card or a point clicked in one of the pictures, which have no run of rows for shift to take. */
  function selectNode(id: number, mode: SelectMode) {
    setSelection({ kind: "ints", ids: applySelect(currentInts(), id, mode) });
    anchor.current = id;
  }

  /**
   * A rectangle was drawn round some nodes, in whichever view (see marquee.tsx): on its own it is
   * the selection, with shift held it is added to what was selected, and with alt held it is taken
   * out of it. The last of them is where a shift-click's run starts from next.
   */
  function selectMany(ids: number[], marqueeMode: MarqueeMode) {
    setSelection({ kind: "ints", ids: applyMarquee(currentInts(), ids, marqueeMode) });
    if (ids.length > 0) anchor.current = ids[ids.length - 1];
  }

  /**
   * Every node this query matches, selected at once - the whole result and not the page on screen.
   *
   * Nothing is asked of the server and nothing is read: the selection is the query (see allSelected),
   * which is why this is instant on a result of any size.
   */
  function selectAll() {
    if (!result) return;
    setSelection({ kind: "query", count: result.total });
    anchor.current = null;
  }

  /** The selection let go of, and the drag-to-select mode with it: the way out of a selection. */
  function clearSelection() {
    setSelection(noSelection);
    anchor.current = null;
    if (dragSelect) onChange({ dragSelect: false });
  }

  // the rectangle over the list and the table; the pictures draw their own (see VisualPivotView, MapView)
  const listMarquee = useListMarquee({
    enabled: dragSelect && mode === "search",
    host: results,
    // the rows carry the internal id (data-node-id), which is what the dataset hands back as text
    onSelect: (ids, marqueeMode) => selectMany(ids.map(Number), marqueeMode),
  });
  // and what the keys held would make of the next rectangle, so the pointer over the rows can show it
  const listMarqueeMode = useMarqueeMode(dragSelect && mode === "search");

  // ctrl+A over the hits: every row of the page into the form at once
  function onHitsKeyDown(e: React.KeyboardEvent) {
    if ((e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() === "a" && hits.length > 0) {
      e.preventDefault();
      setSelection({ kind: "ints", ids: hits.map((h) => h.intId) });
    }
  }
  const hitsHint = dragSelect
    ? "Drag a rectangle round the rows to open them together; hold shift to add them to the selection, alt to take them out of it. A click still opens one"
    : "Click a row to open the node; ctrl-click adds one, shift-click a run of them, ctrl+A the whole page";

  // A column header cycles through the three states a sort can be in: up, down, and the order the
  // store itself returns. Sorting is a different view of the same search, so the open node stays
  // open - only the page goes back to the first.
  function toggleSort(key: string) {
    setPage(0);
    onChange({ sort: sort?.key !== key ? { key, descending: false } : sort.descending ? null : { key, descending: true } });
  }

  /** Takes a column out of the table - and the sort with it, if that is what it was sorted by. */
  function removeColumn(key: string) {
    onChange({ columns: shownColumns.filter((k) => k !== key), ...(sort?.key === key ? { sort: null } : {}) });
  }

  /**
   * Puts a column at another place in the row. Moving one is a choice about the columns, so it
   * writes the list even when the query had not named one before: from here on this query says what
   * it shows and in what order, rather than following whatever the type happens to offer.
   */
  function moveColumn(key: string, index: number) {
    const from = shownColumns.indexOf(key);
    const to = Math.max(0, Math.min(shownColumns.length - 1, index));
    if (from < 0 || from === to) return;
    const columns = shownColumns.filter((k) => k !== key);
    columns.splice(to, 0, key);
    onChange({ columns });
  }

  function toggleFacet(facet: Facet, value: FacetValue) {
    const current = selections.find((s) => s.propertyId === facet.propertyId)?.values ?? [];
    const key = keyOf(value);
    const values = current.some((v) => keyOf(v) === key) ? current.filter((v) => keyOf(v) !== key) : [...current, { value: value.value, value2: value.value2 }];
    reset({ selections: [...selections.filter((s) => s.propertyId !== facet.propertyId), { propertyId: facet.propertyId, values }] });
  }

  // ---- the whole screen ----

  // The browser owns the fullscreen state - Escape and F11 change it without asking - so the button
  // follows the document rather than the other way round. Both sides are null before the first
  // paint, which is not the same as being fullscreen.
  useEffect(() => {
    const sync = () => setFullscreen(document.fullscreenElement !== null && document.fullscreenElement === body.current);
    document.addEventListener("fullscreenchange", sync);
    sync();
    return () => document.removeEventListener("fullscreenchange", sync);
  }, []);

  /**
   * Fills the screen with the row rather than with the picture alone: the facet rail goes with it,
   * and so does the form a card opens in, so a picture that fills the screen is still one a set can
   * be narrowed down in without leaving it. The rail is opened on the way in if it was closed -
   * there is room for it now - and the Filter button in the head still closes it again.
   */
  function toggleFullscreen() {
    const el = body.current;
    if (!el) return;
    if (document.fullscreenElement === el) {
      void document.exitFullscreen().catch(() => {});
      return;
    }
    // refused (a permissions policy, or no gesture behind this call): the row stays where it is
    void el
      .requestFullscreen?.()
      .then(() => !showFacets && onChange({ showFacets: true }))
      .catch(() => {});
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
    if (nodesSelected === 0 || editorWidth === null) return;
    const fit = () => setEditorWidth((w) => (w === null ? w : applyWidth(w)));
    fit();
    window.addEventListener("resize", fit);
    return () => window.removeEventListener("resize", fit);
  }, [nodesSelected, showFacets, editorWidth]);

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

  /**
   * The csv export, asked for by size first.
   *
   * An export is a query of its own, not the page on screen: the file people want is usually more
   * than the rows they are looking at, and sometimes it is the whole set - which on a large store is
   * a scan worth choosing on purpose rather than discovering as a wait. So the size is picked before
   * anything runs, and each choice says how many rows it is in the count the search already knows.
   *
   * Row counts are a take, and the page on screen is that take from where the page starts - the same
   * two numbers the search itself ran with, so the file holds exactly the rows that were on screen,
   * in the order they were in.
   */
  async function download() {
    if (!query || !result) return;
    const total = result.total;
    const onPage = Math.max(0, Math.min(pageRows, total - page * pageRows));
    const choices: { label: string; hint?: string; page: number; rows: number }[] = [];
    if (onPage < total) choices.push({ label: `This page (${rowCount(onPage)})`, hint: "the rows on screen, in the order they are shown", page, rows: pageRows });
    for (const n of csvRowChoices) if (n < total) choices.push({ label: `First ${rowCount(n)}`, page: 0, rows: n });
    choices.push({
      label: `All (${rowCount(total)})`,
      hint: total > csvRowLimit ? `the export stops at ${formatCount(csvRowLimit)} rows` : undefined,
      page: 0,
      rows: 0, // as many as there are, up to the server's own cap
    });
    const picked = await showChoice("Export as csv", "How much of the result should the file hold?", choices);
    if (picked === null) return;
    const choice = choices[picked];
    setExporting(true);
    try {
      await exportCsv({ ...query, page: choice.page, csvRows: choice.rows, summary: false }); // the file is the rows, whatever view asked for it
    } catch (e) {
      await showError("Could not export", e instanceof Error ? e.message : String(e));
    } finally {
      setExporting(false);
    }
  }

  const selectedCount = selectionList.reduce((n, s) => n + s.values.length, 0);
  // The columns the table is showing: the ones this query names, or - until it names any - the ones
  // the type answered with. Reading them off the result rather than seeding the query with them
  // keeps "no choice made" a state of its own, so the type's own set can change under a query that
  // never asked for anything else, and touching a chip is what writes a list of its own.
  const shownColumns = q.columns ?? result?.columns?.map((c) => c.key) ?? [];

  // Everything a heading can do, in one place: drop the column, size it, or add another. Built here
  // because only this component knows what the type has left to offer and what "no choice" means.
  const columnsUi: TableColumnsUi = {
    sizing: columnSizing,
    drag: columnDrag,
    order: shownColumns,
    onMove: moveColumn,
    onRemove: removeColumn,
    addOptions:
      available === null
        ? null
        : available
            .filter((c) => !shownColumns.includes(c.key))
            .map((c) => ({ key: c.key, name: c.name, hint: (c.declaredBy ? c.declaredBy + " · " : "") + c.type })),
    onAdd: (key) => onChange({ columns: [...shownColumns, key] }),
    extras:
      q.columns !== null
        ? [
            // back to no choice at all, which is not the same as choosing what the type happens to
            // show today: a query that never asked follows the type as the model changes
            { label: "Show the type's own columns", onClick: () => onChange({ columns: null }) },
          ]
        : [],
  };

  // "all" says how many that is, so choosing it is not a guess. A set larger than one page can hold
  // says so too: it is still all of them, read a page at a time.
  const allRowsLabel = !result
    ? "All"
    : result.total > maxPageRows
      ? `All (${formatCount(maxPageRows)} of ${formatCount(result.total)})`
      : `All (${formatCount(result.total)})`;

  // Drag to select (marquee.tsx), wherever there are nodes on screen to draw a rectangle round: the
  // hits as a list or a table, the cards, the points of the map. The groups, the pivot and the cloud
  // have no nodes on them, so the switch is not offered there.
  const selectable = mode === "search" || visual || map;
  const selectToggle = selectable && (
    <button
      className={"icon-button labelled" + (dragSelect ? " active" : "")}
      aria-pressed={dragSelect}
      title={
        dragSelect
          ? "Drag to select is on: a drag draws a rectangle, and every node it touches opens in the form. Hold shift to add what it catches to the selection, alt to take it out. The right button moves a picture meanwhile. Click to turn it off"
          : "Drag to select: draw a rectangle round the nodes to open them together (shift adds to the selection, alt takes out of it)"
      }
      onClick={() => onChange({ dragSelect: !dragSelect })}
    >
      <IconMarquee2 size={16} stroke={1.8} />
      Select
    </button>
  );

  // Every node the query matches, however many pages that is. Beside the drag switch, since the two
  // are the same job asked at two scales: a rectangle round some of them, or the lot.
  const selectAllButton = selectable && result !== null && result.total > 1 && (
    <button
      className={"icon-button labelled" + (allSelected ? " active" : "")}
      aria-pressed={allSelected}
      title={`Select all ${formatCount(result.total)} nodes this query matches — every page, not just this one`}
      onClick={selectAll}
    >
      <IconSelectAll size={16} stroke={1.8} />
      Select all
    </button>
  );

  // Filling the screen with the picture: the result's own head is a line spent on a count and the
  // switch for the facet rail, and in the visual pivot both of those fit on the line of controls the
  // picture already has. So they go down there, and the head goes away - one more line of canvas.
  const headInToolbar = (visual || map) && fullscreen;
  const resultHead = (
    <>
      <span className="query-head-count">
        {result ? (
          <>
            {formatCount(result.total)} {result.total === 1 ? "node" : "nodes"}
            {result.total !== result.sourceCount ? ` of ${formatCount(result.sourceCount)}` : ""} · {result.durationMs.toFixed(1)} ms
          </>
        ) : (
          "Searching…"
        )}
      </span>
      {!showFacets && selectedCount > 0 && (
        // as in the head it came from: with the rail closed, this is the difference between a
        // filtered result and one that looks wrong
        <span className="query-filters">
          {formatCount(selectedCount)} {selectedCount === 1 ? "filter" : "filters"}
          <button className="link-button" onClick={() => reset({ selections: [] })}>
            clear
          </button>
        </span>
      )}
      <button
        className={"icon-button labelled" + (showFacets ? " active" : "")}
        title={showFacets ? "Hide the facets" : "Show the facets — the search then counts their values"}
        onClick={() => onChange({ showFacets: !showFacets })}
      >
        <IconFilter size={16} stroke={1.8} />
        Filter
      </button>
      {selectToggle}
      {selectAllButton}
    </>
  );

  const semanticAvailable = model.hasAi && model.hasSemanticIndex;
  // a knob this query has moved off the database's own default, and so a search whose text is not the
  // only thing deciding the answer
  const semanticSet = semanticRatio !== null || minSimilarity !== null;
  const lastPage = result ? Math.max(0, Math.ceil(result.total / pageRows) - 1) : 0;
  return (
    <>
      <div className="query-toolbar">
        {/* the type and the refresh belong together: both are about which nodes are on the page,
            and the refresh is framed like the picker so the two read as one control */}
        <div className="query-type">
          <TypePicker
            types={model.types}
            sources={model.sources ?? []}
            value={typeId}
            onChange={(id) => {
              reset({
                typeId: id,
                selections: [], // the facets of another type are different properties
                sort: null, // and its columns are different properties too
                columns: null, // and the columns of the table are that type's, not this one's
                pivot: null, // and what the summaries group by
                groups: null,
                visual: null,
                map: null,
              });
              setExpanded([]);
            }}
            // the type is chosen, the search box is where the next thing happens
            onPicked={() => searchBox.current?.focus()}
          />
          <button className="icon-button query-refresh" title="Run the query again" onClick={refreshAll}>
            <IconRefresh size={15} stroke={1.8} className={loading ? "spinning" : ""} />
          </button>
        </div>
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
        <button className="action-button" onClick={() => setNewNode(true)} title="Make a node and open it here">
          <IconPlus size={15} stroke={1.9} /> New node
        </button>
        {/* what kind of query this is: the hits themselves, a table of chosen columns, or a summary */}
        <div className="query-view" role="tablist">
          {modes.map((m) => (
            <button
              key={m.id}
              role="tab"
              aria-selected={mode === m.id}
              className={mode === m.id ? "active" : ""}
              title={m.hint}
              onClick={() => onChange({ mode: m.id, ...carryGrouping(q, mode, m.id) })}
            >
              <m.icon size={14} stroke={1.8} />
              {m.label}
            </button>
          ))}
        </div>
        {/* The semantic knobs, folded away and back. A value set while they were open keeps the
            button lit with them closed: a search running at a ratio nobody can see is the one thing
            this must not allow. */}
        <button
          // lit while the panel is open, and lit with it closed when a knob is set: the panel below
          // says which, and red (armed) is for something dangerous, which this is not
          className={"icon-button" + (showSemantic || semanticSet ? " active" : "")}
          title={
            showSemantic
              ? "Hide the semantic search sliders"
              : semanticSet
                ? `Semantic search: ratio ${semanticRatio ?? model.defaultSemanticRatio}, minimum similarity ${minSimilarity ?? model.defaultMinimumSimilarity} — click to show`
                : "Show the semantic search sliders — how much of the search is vectors, and how close a match has to be"
          }
          onClick={() => onChange({ showSemantic: !showSemantic })}
        >
          <IconSparkles size={16} stroke={1.8} />
        </button>
        <button className={"icon-button" + (showQuery ? " armed" : "")} title="Show the query this page sends" onClick={() => setShowQuery(!showQuery)}>
          <IconCode size={16} stroke={1.8} />
        </button>
      </div>

      {/* Open, they are live as soon as this database can search semantically - setting the ratio
          before typing is a perfectly good order to work in - and only greyed when it cannot, with a
          note saying which half is missing. */}
      {showSemantic && (
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
      )}


      {showQuery && result && !summary && <div className="query-string">{formatQuery(result.query)}</div>}
      {error && !summary && <div className="query-error">{error}</div>}

      <div
        ref={body}
        className={"query-body" + (nodesSelected > 0 ? " with-editor" : "") + (showFacets ? "" : " no-facets") + (resizing ? " resizing" : "")}
        // capped as a share of the page as well as in pixels: a width dragged on a wide window
        // would otherwise leave nothing of the list on a narrow one
        style={editorWidth === null ? undefined : ({ "--editor-width": `min(${editorWidth}px, ${maxEditorShare * 100}%)` } as React.CSSProperties)}
      >
        {showFacets && (
          <aside className="query-facets panel">
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

        <div
          className={"query-results panel" + (dragSelect && mode === "search" ? " marquee-mode" + marqueeModeClass(listMarqueeMode) : "")}
          ref={results}
          onPointerDown={listMarquee.onPointerDown}
          onClickCapture={listMarquee.onClickCapture}
        >
          {/* Filling the screen with the picture gives every pixel of it to the picture: this head
              would be a whole line of it spent on a count and one switch, so in that one case both
              go down to the picture's own line of controls instead (see head, below). */}
          {!headInToolbar && (
            <div className={"query-results-head" + (summary ? " on-panel" : "")}>
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
                className={"icon-button labelled" + (showFacets ? " active" : "")}
                title={showFacets ? "Hide the facets" : "Show the facets — the search then counts their values"}
                onClick={() => onChange({ showFacets: !showFacets })}
              >
                <IconFilter size={16} stroke={1.8} />
                Filter
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
              {table && (
                // typing into the cells; off by default, because a table people read should not change
                // under a stray keystroke, and on it costs a value per cell on the wire
                <button
                  className={"icon-button labelled" + (editCells ? " active" : "")}
                  title={editCells ? "Stop editing in the table" : "Edit in the table — arrows move, typing edits, enter saves"}
                  onClick={() => onChange({ editCells: !editCells })}
                >
                  <IconPencil size={16} stroke={1.8} />
                  Edit
                </button>
              )}
              {selectToggle}
              {selectAllButton}
              {!summary && (
                <select className="select compact" value={pageSize} title="Rows per page" onChange={(e) => reset({ pageSize: Number(e.target.value) })}>
                  {pageSizes.map((size) => (
                    <option key={size} value={size}>
                      {formatCount(size)} / page
                    </option>
                  ))}
                  <option value={0}>{allRowsLabel}</option>
                </select>
              )}
              {!summary && (
                <button
                  className="icon-button"
                  disabled={exporting || !result || result.total === 0}
                  title="Download the result as csv — choose how many rows"
                  onClick={download}
                >
                  <IconDownload size={16} stroke={1.8} />
                </button>
              )}
              {table && (
                // this page of the table, as it is shown: what a spreadsheet or a message wants pasted
                <CopyButton
                  title="Copy this page of the table to the clipboard"
                  disabled={!result?.columns || result.hits.length === 0}
                  table={() => ({ header: result?.columns?.map((c) => c.name) ?? [], rows: result?.hits.map((h) => h.cells ?? []) ?? [] })}
                />
              )}
              {!summary && result && result.total > pageRows && (
                <div className="query-paging">
                  <button className="icon-button" disabled={page === 0} title="Previous page" onClick={() => setPage(page - 1)}>
                    <IconChevronLeft size={15} stroke={1.8} />
                  </button>
                  <span className="muted">
                    {page * pageRows + 1}–{Math.min((page + 1) * pageRows, result.total)}
                  </span>
                  <button className="icon-button" disabled={page >= lastPage} title="Next page" onClick={() => setPage(page + 1)}>
                    <IconChevronRight size={15} stroke={1.8} />
                  </button>
                </div>
              )}
            </div>
          )}
          {pivot ? (
            // keyed by type: another type has other properties, so the definition starts over with it
            <PivotView key={typeId} base={pivotBase} definition={q.pivot} onChange={(pivot) => onChange({ pivot })} refreshToken={epoch} showQuery={showQuery} onDrill={drill} />
          ) : visual ? (
            <VisualPivotView
              key={typeId}
              base={pivotBase}
              definition={q.visual}
              onChange={(visual) => onChange({ visual })}
              refreshToken={epoch}
              showQuery={showQuery}
              onOpen={selectNode}
              onSelectMany={selectMany}
              marquee={dragSelect}
              selected={selectedInts(selection)}
              allSelected={allSelected}
              fullscreen={fullscreen}
              onToggleFullscreen={toggleFullscreen}
              head={headInToolbar ? resultHead : undefined}
            />
          ) : map ? (
            <MapView
              key={typeId}
              base={pivotBase}
              definition={q.map}
              onChange={(m) => onChange({ map: m })}
              refreshToken={epoch}
              showQuery={showQuery}
              onOpen={selectNode}
              onSelectMany={selectMany}
              marquee={dragSelect}
              selected={selectedInts(selection)}
              allSelected={allSelected}
              fullscreen={fullscreen}
              onToggleFullscreen={toggleFullscreen}
              head={headInToolbar ? resultHead : undefined}
            />
          ) : cloud ? (
            <WordCloudView
              key={typeId}
              base={pivotBase}
              definition={q.cloud}
              onChange={(c) => onChange({ cloud: c })}
              refreshToken={epoch}
              showQuery={showQuery}
              onWord={searchWord}
              head={headInToolbar ? resultHead : undefined}
            />
          ) : groups ? (
            <GroupByView key={typeId} base={pivotBase} definition={q.groups} onChange={(groups) => onChange({ groups })} refreshToken={epoch} showQuery={showQuery} onDrill={drill} />
          ) : table && q.columns?.length === 0 ? (
            <div className="query-empty">No columns: add one to see the rows.</div>
          ) : table && result?.columns && editCells ? (
            <EditableTable
              // keyed by type only: a cell save re-runs the search, and rebuilding the grid on every
              // one of them would put the cursor back at the first cell after each edit
              key={typeId}
              storeId={db.id}
              columns={result.columns}
              columnsUi={columnsUi}
              hits={hits}
              selected={marked}
              allSelected={allSelected}
              sort={sort}
              sortApplied={result.sortApplied}
              onSort={toggleSort}
              onSaved={refreshData}
              loading={loading}
            />
          ) : table && result?.columns ? (
            <div className={"query-table-wrap" + (loading ? " loading" : "")} tabIndex={-1} title={hitsHint} onScroll={rowWindow.onScroll} onKeyDown={onHitsKeyDown}>
              <table className="query-table">
                <thead>
                  <tr>
                    {result.columns.map((column) => (
                      <ColumnHead
                        key={column.key}
                        ui={columnsUi}
                        colKey={column.key}
                        className={sort?.key === column.key ? (result.sortApplied ? "sorted" : "sorted-inactive") : ""}
                        title={
                          sort?.key === column.key && !result.sortApplied
                            ? "Sorted by this column, but a facet selection is filtering and the rows come back in the database's own order"
                            : column.sortable
                              ? `${column.type} — click the name to sort`
                              : `${column.type} — cannot be sorted on`
                        }
                        onClick={column.sortable ? () => toggleSort(column.key) : undefined}
                      >
                        {column.name}
                        {sort?.key === column.key && (sort.descending ? <IconArrowNarrowDown size={13} stroke={2} /> : <IconArrowNarrowUp size={13} stroke={2} />)}
                      </ColumnHead>
                    ))}
                    <AddColumnHead ui={columnsUi} />
                  </tr>
                </thead>
                <tbody>
                  {hits.slice(0, rowWindow.count).map((hit) => (
                    <tr
                      key={hit.id}
                      className={isSelected(hit) ? "selected" : ""}
                      data-node-id={hit.intId}
                      // a shift-click takes a run of rows, not a run of text
                      onMouseDown={(e) => e.shiftKey && e.preventDefault()}
                      onClick={(e) => selectHit(e, hit)}
                    >
                      {(hit.cells ?? []).map((value, i) => (
                        <td key={result.columns![i]?.key ?? i} title={value} style={columnSizing.styleOf(result.columns![i]?.key ?? "")}>
                          {value}
                        </td>
                      ))}
                      {/* under the heading that adds a column; it takes what is left of the row */}
                      <td className="th-add-cell" />
                    </tr>
                  ))}
                </tbody>
              </table>
              {hits.length === 0 && <div className="query-empty">Nothing matched.</div>}
            </div>
          ) : (
            <div className={"query-hits" + (loading ? " loading" : "")} tabIndex={-1} title={hitsHint} onScroll={rowWindow.onScroll} onKeyDown={onHitsKeyDown}>
              {result && hits.length === 0 && <div className="query-empty">Nothing matched.</div>}
              {hits.slice(0, rowWindow.count).map((hit) => (
                <button className={"query-hit" + (isSelected(hit) ? " selected" : "")} key={hit.id} data-node-id={hit.intId} onClick={(e) => selectHit(e, hit)}>
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
          {listMarquee.box}
        </div>

        {nodesSelected > 0 && (
          <>
            {/* a grid item of its own in the editor's column rather than a child of the panel: the
                panel clips its content to keep its rounded corners, and would clip the bar with it */}
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
            <aside className="query-editor panel" ref={editor}>
              <NodeEditor
                // keyed by the form's own epoch alone (see refreshData): a change of selection is
                // the form's business, and so is a write made in it - both keep the edits made so
                // far. Only the page being started over takes the form with it.
                key={formEpoch}
                storeId={db.id}
                selection={selection}
                // what a selection of the whole result is resolved through, when something is done with it
                request={query}
                // Saved: the list, the facets and every picture are drawn from values that have
                // just changed, so they are asked again - and drag-to-select is switched off, the
                // way a delete already leaves it, so the left button goes back to turning the
                // picture. The selection itself stays: the form is still open on it.
                onSaved={() => {
                  refreshData();
                  if (dragSelect) onChange({ dragSelect: false });
                }}
                onClose={() => setSelection(noSelection)}
                onClearSelection={clearSelection}
                onDeselect={(intId) =>
                  setSelection((prev) => (prev.kind === "ints" ? { kind: "ints", ids: prev.ids.filter((x) => x !== intId) } : noSelection))
                }
                onDeleted={() => {
                  clearSelection();
                  refreshData(); // the rows and the cards they were are there until the query runs again
                }}
              />
            </aside>
          </>
        )}
      </div>

      {newNode && (
        <NewNodeDialog
          types={model.types}
          sources={model.sources ?? []}
          onClose={() => setNewNode(false)}
          onPick={async (t) => {
            setNewNode(false);
            try {
              const ref = await createNode(db.id, t.id);
              // the list is a search result and the new node may not match it; the form opens on it
              // either way, and the refresh puts it in the list whenever the query does match it
              setSelection({ kind: "guids", ids: [ref.id] });
              anchor.current = null;
              refresh();
            } catch (e) {
              await showError("Could not create the node", e instanceof Error ? e.message : String(e));
            }
          }}
        />
      )}
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
