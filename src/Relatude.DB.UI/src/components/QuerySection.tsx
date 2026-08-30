import { useEffect, useMemo, useRef, useState } from "react";
import {
  IconArrowNarrowDown,
  IconArrowNarrowUp,
  IconChevronLeft,
  IconChevronRight,
  IconCode,
  IconDownload,
  IconFilter,
  IconLayoutList,
  IconSearch,
  IconTable,
  IconX,
} from "@tabler/icons-react";
import { NodeEditor } from "./NodeEditor";
import { showError } from "../dialogs";
import {
  csvRowLimit,
  exportCsv,
  runSearch,
  fetchQueryModel,
  type Facet,
  type FacetSelection,
  type FacetValue,
  type QueryModel,
  type SearchRequest,
} from "../server/query";
import { useLiveResult } from "../server/hooks";
import type { DatabaseInfo } from "../server/serverInfo";
import { formatCount, formatTime } from "../format";

const pageSizes = [25, 50, 100, 200];

/** A bucket's identity, so a selection survives the counts changing under it. */
function keyOf(v: { value: string | null; value2: string | null }): string {
  return (v.value ?? " none") + " " + (v.value2 ?? "");
}

type Selections = Record<string, { value: string | null; value2: string | null }[]>;

/**
 * Search a database and edit what comes back.
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
export function QuerySection({ db }: { db: DatabaseInfo }) {
  const [model, setModel] = useState<QueryModel | null>(null);
  const [typeId, setTypeId] = useState<string | null>(null);
  const [text, setText] = useState("");
  const [semanticRatio, setSemanticRatio] = useState<number | null>(null);
  const [minSimilarity, setMinSimilarity] = useState<number | null>(null);
  const [selections, setSelections] = useState<Selections>({});
  const [expanded, setExpanded] = useState<string[]>([]);
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = useState(pageSizes[0]);
  const [table, setTable] = useState(false);
  const [sort, setSort] = useState<{ key: string; descending: boolean } | null>(null);
  const [showFacets, setShowFacets] = useState(false);
  const [exporting, setExporting] = useState(false);
  const [modelError, setModelError] = useState<string | null>(null);
  const [showQuery, setShowQuery] = useState(false);
  const [selected, setSelected] = useState<string | null>(null);
  const searchBox = useRef<HTMLInputElement>(null);
  // A native select fires change on every arrow key, so moving focus away on change alone would
  // make the type list unusable from the keyboard. Only a type picked with the pointer hands the
  // caret on; a keypress on the select means someone is still choosing.
  const pickedByPointer = useRef(false);

  useEffect(() => {
    let cancelled = false;
    setModel(null);
    setModelError(null);
    fetchQueryModel(db.id)
      .then((m) => {
        if (cancelled) return;
        setModel(m);
        setTypeId(m.baseTypeId);
      })
      .catch((e) => !cancelled && setModelError(e instanceof Error ? e.message : String(e)));
    return () => {
      cancelled = true;
    };
  }, [db.id]);

  const selectionList = useMemo<FacetSelection[]>(
    () => Object.entries(selections).filter(([, values]) => values.length > 0).map(([propertyId, values]) => ({ propertyId, values })),
    [selections],
  );

  // one object per distinct search: the runner treats a new object as a new request
  const query = useMemo<SearchRequest | null>(
    () =>
      model === null || typeId === null
        ? null
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
            facets: showFacets,
            sortBy: sort?.key ?? null,
            sortDescending: sort?.descending ?? false,
          },
    [model, db.id, typeId, text, semanticRatio, minSimilarity, selectionList, expanded, page, pageSize, table, showFacets, sort],
  );

  const { result, loading, error, refresh } = useLiveResult(query, runSearch);

  // Any change to what is being searched starts the result list over, and closes the node open
  // beside it: the form belongs to a hit in the list it came from, and once that list is a
  // different search it is no longer clear what is being edited or why it is still on screen.
  // Paging and the view switches do not go through here - they are the same search, still.
  function reset(apply: () => void) {
    setPage(0);
    setSelected(null);
    apply();
  }

  // A column header cycles through the three states a sort can be in: up, down, and the order the
  // store itself returns. Sorting is a different view of the same search, so the open node stays
  // open - only the page goes back to the first.
  function toggleSort(key: string) {
    setPage(0);
    setSort((prev) => (prev?.key !== key ? { key, descending: false } : prev.descending ? null : { key, descending: true }));
  }

  function toggleFacet(facet: Facet, value: FacetValue) {
    reset(() =>
      setSelections((prev) => {
        const current = prev[facet.propertyId] ?? [];
        const key = keyOf(value);
        const next = current.some((v) => keyOf(v) === key)
          ? current.filter((v) => keyOf(v) !== key)
          : [...current, { value: value.value, value2: value.value2 }];
        return { ...prev, [facet.propertyId]: next };
      }),
    );
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

  if (modelError) return <div className="placeholder">{modelError}</div>;
  if (!model) return null;

  const semanticAvailable = model.hasAi && model.hasSemanticIndex;
  const lastPage = result ? Math.max(0, Math.ceil(result.total / pageSize) - 1) : 0;
  return (
    <div className="query">
      <div className="query-toolbar">
        <select
          className="select"
          value={typeId ?? ""}
          onPointerDown={() => (pickedByPointer.current = true)}
          onKeyDown={() => (pickedByPointer.current = false)}
          onChange={(e) => {
            reset(() => {
              setTypeId(e.target.value);
              setSelections({}); // the facets of another type are different properties
              setExpanded([]);
              setSort(null); // and its columns are different properties too
            });
            if (pickedByPointer.current) searchBox.current?.focus();
            pickedByPointer.current = false;
          }}
        >
          {model.types.map((t) => (
            <option key={t.id} value={t.id}>
              {t.isBase ? "All node types" : t.name}
              {t.isInterface && !t.isBase ? " (interface)" : ""} — {formatCount(t.count)}
            </option>
          ))}
        </select>
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
            onChange={(e) => reset(() => setText(e.target.value))}
          />
          {text && (
            <button
              className="icon-button"
              title="Clear the search text"
              onClick={() => {
                reset(() => setText(""));
                searchBox.current?.focus(); // the button is about to disappear; the caret should not go with it
              }}
            >
              <IconX size={14} stroke={1.8} />
            </button>
          )}
        </div>
        <div className="query-spacer" />
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
          onChange={(v) => reset(() => setSemanticRatio(v))}
        />
        <Slider
          label="Minimum similarity"
          hint="how close a vector match has to be to count"
          value={minSimilarity}
          fallback={model.defaultMinimumSimilarity}
          disabled={!semanticAvailable}
          onChange={(v) => reset(() => setMinSimilarity(v))}
        />
        {!model.hasAi && <span className="query-note">No AI provider is configured for this database, so a search matches words only.</span>}
        {model.hasAi && !model.hasSemanticIndex && <span className="query-note">Nothing in this data model is semantically indexed, so both sliders are inert.</span>}
      </div>

      {showQuery && result && <div className="query-string">{result.query}</div>}
      {error && <div className="query-error">{error}</div>}

      <div className={"query-body" + (selected ? " with-editor" : "") + (showFacets ? "" : " no-facets")}>
        {showFacets && (
        <aside className="query-facets">
          <div className="query-facets-head">
            <span>Facets</span>
            {selectedCount > 0 && (
              <button className="link-button" onClick={() => reset(() => setSelections({}))}>
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

        <div className="query-results">
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
                <button className="link-button" onClick={() => reset(() => setSelections({}))}>
                  clear
                </button>
              </span>
            )}
            <div className="query-spacer" />
            <button
              className={"icon-button" + (showFacets ? " active" : "")}
              title={showFacets ? "Hide the facets" : "Show the facets — the search then counts their values"}
              onClick={() => setShowFacets(!showFacets)}
            >
              <IconFilter size={16} stroke={1.8} />
            </button>
            <div className="query-view">
              <button className={table ? "" : "active"} title="Show the hits as a list" onClick={() => setTable(false)}>
                <IconLayoutList size={15} stroke={1.8} />
              </button>
              <button className={table ? "active" : ""} title={`Show the hits as a table, one column per property`} onClick={() => setTable(true)}>
                <IconTable size={15} stroke={1.8} />
              </button>
            </div>
            <select
              className="select compact"
              value={pageSize}
              title="Rows per page"
              onChange={(e) => reset(() => setPageSize(Number(e.target.value)))}
            >
              {pageSizes.map((size) => (
                <option key={size} value={size}>
                  {size} / page
                </option>
              ))}
            </select>
            <button
              className="icon-button"
              disabled={exporting || !result || result.total === 0}
              title={`Download the whole result set as csv (up to ${formatCount(csvRowLimit)} rows)`}
              onClick={download}
            >
              <IconDownload size={16} stroke={1.8} />
            </button>
            {result && result.total > pageSize && (
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
          {table && result?.columns ? (
            <div className={"query-table-wrap" + (loading ? " loading" : "")}>
              <table className="query-table">
                <thead>
                  <tr>
                    {result.columns.map((column) => (
                      <th
                        key={column.key}
                        className={
                          (column.sortable ? "sortable" : "") +
                          (sort?.key === column.key ? (result.sortApplied ? " sorted" : " sorted-inactive") : "")
                        }
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
                        {sort?.key === column.key &&
                          (sort.descending ? <IconArrowNarrowDown size={13} stroke={2} /> : <IconArrowNarrowUp size={13} stroke={2} />)}
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
                    <span className="query-hit-name">{hit.displayName}</span>
                    <span className="query-hit-type">{hit.typeName}</span>
                    <span className="query-hit-time">{formatTime(hit.changedUtc)}</span>
                  </div>
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
          <aside className="query-editor">
            <NodeEditor key={selected} storeId={db.id} nodeId={selected} onSaved={refresh} onClose={() => setSelected(null)} />
          </aside>
        )}
      </div>
    </div>
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
