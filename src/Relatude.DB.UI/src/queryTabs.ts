import type { FacetSelection, PivotAxisOptions, PivotLevelSpec, PivotMeasureSpec } from "./server/query";

/**
 * The queries open on the query page, one tab each, kept per database in localStorage.
 *
 * A query is everything that decides what the page asks the server for - the type, the text, the
 * knobs, the facet selection, the kind of query and how its result is shown - and nothing about
 * where someone is in the answer: the page number, the open node and the widths are the session's,
 * not the query's. What is stored is the query as the page has it, so coming back finds every tab
 * exactly as it was left, still running against whatever the database holds now.
 */

/** search: the hits, as a list or a table of chosen columns; groups and pivot: summaries of them. */
export type QueryMode = "search" | "groups" | "pivot";
export type HitsView = "list" | "table";
/** How a summary is shown: the numbers, or bars drawn from them. */
export type SummaryView = "table" | "chart";

export interface PivotDefinition {
  rows: PivotLevelSpec[];
  columns: PivotLevelSpec[];
  measures: PivotMeasureSpec[];
  rowOptions: PivotAxisOptions;
  columnOptions: PivotAxisOptions;
  subTotals: boolean;
  view: SummaryView;
  /** The measure the chart draws, by name; null is the first one. */
  chartMeasure: string | null;
}

export interface GroupByDefinition {
  keys: PivotLevelSpec[];
  measures: PivotMeasureSpec[];
  includeMissing: boolean;
  sort: { by: string; descending: boolean } | null;
  view: SummaryView;
  /** The measure the chart draws, by name; null is the count. */
  chartMeasure: string | null;
}

export interface SavedQuery {
  id: string;
  /** A name someone typed; null is a name made from the query itself, which follows it as it changes. */
  name: string | null;
  /** null until the model is known: the base type, whatever it is called in this database. */
  typeId: string | null;
  text: string;
  semanticRatio: number | null;
  minimumSimilarity: number | null;
  selections: FacetSelection[];
  showFacets: boolean;
  /** the semantic ratio / minimum similarity panel; off unless someone asked for it */
  showSemantic?: boolean;
  mode: QueryMode;
  hitsView: HitsView;
  /** the table view with its cells open for typing; off unless someone asked for it */
  editCells?: boolean;
  sort: { key: string; descending: boolean } | null;
  pageSize: number;
  /** The columns of the table view, by column key, in order; null is the type's own set of them. */
  columns: string[] | null;
  /** The summaries' definitions, null until their view has been opened; dropped when the type changes. */
  pivot: PivotDefinition | null;
  groups: GroupByDefinition | null;
}

export interface QueryTabs {
  active: string;
  queries: SavedQuery[];
}

const storageKey = (storeId: string) => "queryTabs:" + storeId;

export function newQuery(): SavedQuery {
  return {
    id: crypto.randomUUID(),
    name: null,
    typeId: null,
    text: "",
    semanticRatio: null,
    minimumSimilarity: null,
    selections: [],
    showFacets: false,
    showSemantic: false,
    mode: "search",
    hitsView: "list",
    editCells: false,
    sort: null,
    pageSize: 25,
    columns: null,
    pivot: null,
    groups: null,
  };
}

/** The tabs as they were left, or a single fresh one. Anything unreadable is a fresh one too. */
export function loadTabs(storeId: string): QueryTabs {
  try {
    const raw = localStorage.getItem(storageKey(storeId));
    if (raw) {
      const parsed = JSON.parse(raw) as Partial<QueryTabs>;
      if (Array.isArray(parsed.queries) && parsed.queries.length > 0) {
        // a field added later is missing from an older save; the fresh query supplies it
        const queries = parsed.queries.map((q) => migrate({ ...newQuery(), ...q }));
        const active = queries.some((q) => q.id === parsed.active) ? parsed.active! : queries[0].id;
        return { active, queries };
      }
    }
  } catch {
    // fall through to a fresh set
  }
  const first = newQuery();
  return { active: first.id, queries: [first] };
}

/**
 * A saved query written by an older page, brought up to date.
 *
 * "select" used to be a mode of its own: the hits as a table of chosen columns, beside a search that
 * could only show the type's own. The table view chooses its columns now, so a saved select query is
 * that table - it keeps the columns it had picked.
 */
function migrate(q: SavedQuery): SavedQuery {
  if ((q.mode as string) === "select") return { ...q, mode: "search", hitsView: "table" };
  return q;
}

export function saveTabs(storeId: string, tabs: QueryTabs) {
  try {
    localStorage.setItem(storageKey(storeId), JSON.stringify(tabs));
  } catch {
    // storage full or unavailable: the page still works, it just will not remember
  }
}
