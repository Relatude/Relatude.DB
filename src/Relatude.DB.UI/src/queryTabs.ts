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

/** search: the hits; select: the hits as a table of chosen columns; groups and pivot: summaries of them. */
export type QueryMode = "search" | "select" | "groups" | "pivot";
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
  mode: QueryMode;
  hitsView: HitsView;
  sort: { key: string; descending: boolean } | null;
  pageSize: number;
  /** The columns of the select mode, by column key, in order; null until the mode has been opened. */
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
    mode: "search",
    hitsView: "list",
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
        const queries = parsed.queries.map((q) => ({ ...newQuery(), ...q }));
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

export function saveTabs(storeId: string, tabs: QueryTabs) {
  try {
    localStorage.setItem(storageKey(storeId), JSON.stringify(tabs));
  } catch {
    // storage full or unavailable: the page still works, it just will not remember
  }
}
