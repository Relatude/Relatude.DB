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

/** search: the hits, as a list or a table of chosen columns; groups and pivot: summaries of them; visual: every hit as a card. */
export type QueryMode = "search" | "groups" | "pivot" | "visual";
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

/** The visual pivot: what colours the cards and what stacks them. A mode is auto | values | ranges. */
export interface VisualDefinition {
  /** the property whose values colour the cards; null is one colour for all of them */
  colorProperty: string | null;
  colorMode: string;
  /** the property whose values give the cards their shapes; null (and absent, in an older save) is the plain card */
  shapeProperty?: string | null;
  shapeMode?: string;
  /**
   * The property whose values give the cards their thickness. Choosing one is what turns the
   * picture into a picture of solids, seen through a camera that orbits; null (and absent, in an
   * older save) is the flat picture.
   */
  depthProperty?: string | null;
  depthMode?: string;
  /**
   * The property whose values lay the cards in rows one behind another along the depth axis - a
   * second axis for the bars, the way bars are the first. Choosing one also turns the picture into a
   * picture of solids; null (and absent) keeps it in a single plane.
   */
  depthGroupProperty?: string | null;
  depthGroupMode?: string;
  /**
   * How far the picture of solids is stretched along each axis, 1 being the shape the layout chooses
   * for itself: `xScale` how far it reaches across, `depthScale` how far it reaches back. Absent is 1.
   */
  xScale?: number;
  depthScale?: number;
  /** the property whose values the cards are stacked into bars by; null is the grid */
  barProperty: string | null;
  barMode: string;
  /** the property the cards are laid in the order of - along the grid, up each bar; null is the result's order */
  sortProperty?: string | null;
  sortDescending?: boolean;
  /** the legend beside the picture */
  legend: boolean;
  /** whether the two folded-away channels - thickness and shape - are on show; absent is folded */
  extras?: boolean;
  /**
   * Whether a card close enough to show one is given its picture - the photograph, or the placeholder
   * with the node's name on it - or stays a plain block of its colour. Kept twice, once for each kind
   * of picture, because the answer is not the same in both: flat, a screen of photographs is what the
   * view is for (absent is on); as solids, the pictures wrap every face of every block and what is
   * being read is usually the shape of the field rather than what is in it (absent is off).
   */
  pictures?: boolean;
  solidPictures?: boolean;
  /**
   * Whether the picture of solids turns on its own, slowly, whenever it is left alone. Absent is
   * still - a picture that will not hold still is no good for reading a chart, so it is asked for.
   */
  spin?: boolean;
  /**
   * Whether the picture stands on a bare ground - black under the dark theme, white under the light
   * one - rather than on the panel it sits in. Absent is the panel.
   */
  bare?: boolean;
  /** the colours the cards are painted with (visual/palette.ts); absent is the first palette */
  palette?: string;
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
  /**
   * The semantic ratio / minimum similarity panel: true or false is a choice someone made on this
   * query, null is no choice at all - the panel then follows the database, open wherever there is an
   * AI provider to search with. Same shape as the two knobs it holds, where null is the database's
   * own default rather than a value.
   */
  showSemantic?: boolean | null;
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
  visual: VisualDefinition | null;
}

export interface QueryTabs {
  active: string;
  queries: SavedQuery[];
  /**
   * What the saved set was written by. Bumped only when a field has to be reinterpreted rather than
   * merely added: a value an older page wrote means something else now, and migrate says what. An
   * absent version is anything written before this was kept.
   */
  version?: number;
}

/** see QueryTabs.version and migrateTabs */
const tabsVersion = 1;

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
    showSemantic: null, // no choice made: the panel follows the database
    mode: "search",
    hitsView: "list",
    editCells: false,
    sort: null,
    pageSize: 25,
    columns: null,
    pivot: null,
    groups: null,
    visual: null,
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
        const queries = parsed.queries.map((q) => migrate(migrateTabs({ ...newQuery(), ...q }, parsed.version ?? 0)));
        const active = queries.some((q) => q.id === parsed.active) ? parsed.active! : queries[0].id;
        return { active, queries, version: tabsVersion };
      }
    }
  } catch {
    // fall through to a fresh set
  }
  const first = newQuery();
  return { active: first.id, queries: [first], version: tabsVersion };
}

/**
 * A saved query whose fields have to be read the way the page that wrote them meant them.
 *
 * Version 1: the semantic panel used to start closed everywhere, so every query was saved with
 * showSemantic false whether or not anyone had touched it. It follows the database now, and a false
 * from before that cannot be told apart from a choice - so they are all dropped back to "no choice",
 * once. A false written since is a choice and is kept, which is what the version is for.
 */
function migrateTabs(q: SavedQuery, version: number): SavedQuery {
  if (version < 1 && q.showSemantic === false) return { ...q, showSemantic: null };
  return q;
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
    // stamped here rather than carried through the page's state, so a set rebuilt anywhere still
    // says which page wrote it
    localStorage.setItem(storageKey(storeId), JSON.stringify({ ...tabs, version: tabsVersion }));
  } catch {
    // storage full or unavailable: the page still works, it just will not remember
  }
}
