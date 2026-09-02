// The query section's half of the admin API (server side: NodeServer/UI/UIQuery.cs).
//
// Facet values travel as opaque strings: whatever the server sent as `value` is what a selection
// posts back, unchanged. They are round-trip tokens, not display text (dates are ticks, numbers are
// invariant), because the server matches a selection against the bucket it came from - so never
// build one in the browser, and never show one; that is what `display` is for.

import { send } from "./channel";
import { adminBase } from "./base";

export interface NodeTypeInfo {
  id: string;
  name: string;
  fullName: string;
  isInterface: boolean;
  hidden: boolean;
  isBase: boolean;
  count: number;
}

export interface QueryModel {
  storeId: string;
  types: NodeTypeInfo[];
  baseTypeId: string;
  hasAi: boolean;
  hasSemanticIndex: boolean;
  defaultSemanticRatio: number;
  defaultMinimumSimilarity: number;
}

export interface FacetValue {
  value: string | null; // null is the "no value" bucket
  value2: string | null; // set on a range bucket
  display: string;
  count: number;
  selected: boolean;
}

export interface Facet {
  propertyId: string;
  codeName: string;
  displayName: string;
  valueType: string;
  isRange: boolean;
  truncated: boolean;
  totalValues: number;
  values: FacetValue[];
}

export interface HitSummaryValue {
  codeName: string;
  value: string;
}

export interface Hit {
  id: string;
  intId: number;
  typeId: string;
  typeName: string;
  displayName: string;
  address: string | null;
  createdUtc: string;
  changedUtc: string;
  summary: HitSummaryValue[];
  /** One value per column of the table view, in column order. Null unless the table was asked for. */
  cells: string[] | null;
}

/** A column of the table view. `key` is a property id, or a "__"-prefixed name for a node's own fields. */
export interface Column {
  key: string;
  name: string;
  type: string;
  /** Whether the result can be ordered by it: a list, a document or an array has no single key to sort on. */
  sortable: boolean;
}

export interface SearchResult {
  typeId: string;
  typeName: string;
  total: number;
  sourceCount: number;
  page: number;
  pageSize: number;
  durationMs: number;
  query: string;
  facets: Facet[];
  columns: Column[] | null;
  hits: Hit[];
  /**
   * False when a sort was asked for and the result could not be given in that order. Filtering by a
   * facet is a set intersection and the set that comes out of one is in id order, so a selection and
   * a sort cannot both hold - the selection wins and the table says the order is not in force.
   */
  sortApplied: boolean;
}

/** A facet selection as it is posted back: the tokens of the buckets that are on. */
export interface FacetSelection {
  propertyId: string;
  values: { value: string | null; value2: string | null }[];
}

export interface SearchRequest {
  storeId: string;
  typeId: string | null;
  text: string;
  semanticRatio: number | null;
  minimumSimilarity: number | null;
  selections: FacetSelection[];
  expanded: string[];
  page: number;
  pageSize: number;
  /** Asks for a value per column on every hit. Costs a read per cell, so only the table view sets it. */
  table: boolean;
  /**
   * Asks for the facet buckets. Counting them is the expensive half of the query, so the rail says
   * whether anyone is going to read them. A selection still filters when this is false.
   */
  facets: boolean;
  /** The property id of the column the table is sorted by, or null for the store's own order. */
  sortBy: string | null;
  sortDescending: boolean;
}

/** How many rows the csv export covers before it stops; the server's own cap, repeated for the UI. */
export const csvRowLimit = 50_000;

export type EditorKind =
  | "text"
  | "code"
  | "integer"
  | "number"
  | "bool"
  | "enum"
  | "enumList"
  | "stringList"
  | "guid"
  | "guidList"
  | "datetime"
  | "datetimeoffset"
  | "timespan"
  | "geo"
  | "file"
  | "reference"
  | "references"
  | "relation"
  | "embedded"
  | "binary"
  | "vector"
  | "unsupported";

export interface NodeRef {
  id: string;
  name: string;
  typeName: string | null;
}

export interface TypeRef {
  id: string;
  name: string;
}

export type FileKind = "Unknown" | "Document" | "Image" | "Video" | "Audio" | "Meta";

export interface FileValueView {
  name: string;
  size: number;
  contentType: string;
  width: number;
  height: number;
  fileId: string;
  storageId: string;
  /** What the file is, which is what decides whether it can be shown at all. */
  fileType: FileKind;
  /** The detailed format ("Png", "Mp4", "Svg", ...), as the store reads it off the file name. */
  format: string;
  /** The property path the file sits at: the token the media route reads the bytes back from. */
  path: string;
  /** The file's own hash, short. Only in the url, so replacing a file cannot show the old one. */
  version: string;
}

export interface GeoValue {
  latitude: number;
  longitude: number;
}

export interface InnerNodeView {
  id: string;
  typeName: string;
  /** `file` is set on the values that are files, and is what a preview of one is built from. */
  values: { codeName: string; value: string; file: FileValueView | null }[];
}

export interface PropertyView {
  id: string;
  name: string;
  type: string; // PropertyType
  declaredBy: string | null;
  notes: string[];
  editor: EditorKind;
  readOnly: boolean;
  value: unknown;
  options: { value: number; label: string }[] | null;
  targets: NodeRef[] | null;
  targetTypes: TypeRef[] | null;
  isMany: boolean | null;
  multiline: boolean | null;
  language: string | null;
  maxLength: number | null;
  min: number | null;
  max: number | null;
  pattern: string | null;
  info: string | null;
}

export interface NodeView {
  id: string;
  intId: number;
  typeId: string;
  typeName: string;
  fullName: string;
  displayName: string;
  address: string | null;
  createdUtc: string;
  changedUtc: string;
  properties: PropertyView[];
}

// ---- the pivot view: the same search, summarized as groups x measures ----

/** A property of the queried type as the pivot builder sees it: what it can be used for. */
export interface PivotProperty {
  id: string;
  name: string;
  type: string; // PropertyType
  /** Can be a row or column grouping (indexed and facetable; relations that opted in). */
  groupable: boolean;
  /** Can carry a distinct count (an indexed scalar). */
  aggregatable: boolean;
  /** Can be summed and averaged. */
  numeric: boolean;
  /** A date: the grouping can be by calendar interval. */
  isDate: boolean;
  /** The type that declares it, when inherited. */
  declaredBy: string | null;
}

export interface PivotModel {
  typeId: string;
  typeName: string;
  properties: PivotProperty[];
}

export type PivotFunction = "Count" | "CountDistinct" | "Sum" | "Average" | "Min" | "Max";

/** One grouping level. `mode` is auto | values | ranges, or a calendar interval (year … hour) on a date. */
export interface PivotLevelSpec {
  propertyId: string;
  mode: string;
}

export interface PivotMeasureSpec {
  function: PivotFunction;
  /** Null for Count, which needs no property. */
  propertyId: string | null;
}

/** Options applied to every level of one axis. */
export interface PivotAxisOptions {
  /** Keep the first N groups (after sorting); 0 = all. */
  maxGroups: number;
  /** A measure name, or "Count"; null keeps the natural bucket order. */
  sortByMeasure: string | null;
  descending: boolean;
  /** Collect the trimmed groups into one "(other)" group. */
  otherGroup: boolean;
  /** A "(none)" group for the nodes without a value. */
  includeMissing: boolean;
}

export interface PivotRequest {
  storeId: string;
  typeId: string | null;
  text: string;
  semanticRatio: number | null;
  minimumSimilarity: number | null;
  selections: FacetSelection[];
  rows: PivotLevelSpec[];
  columns: PivotLevelSpec[];
  measures: PivotMeasureSpec[];
  rowOptions: PivotAxisOptions;
  columnOptions: PivotAxisOptions;
  subTotals: boolean;
  rowPage: number;
  rowPageSize: number;
}

export interface PivotLevel {
  propertyId: string;
  codeName: string;
  valueType: string;
  isRange: boolean;
  interval: string;
}

/**
 * A group on an axis: one label per level. `values`/`values2` are the facet selection tokens of the
 * buckets (see the note at the top of this file), so a cell can be turned into the search that
 * shows the nodes behind it. Null in `values` is the "(none)" bucket - or the "(other)" group, which
 * `isOther` marks and which no selection can express.
 */
export interface PivotGroup {
  labels: string[];
  values: (string | null)[];
  values2: (string | null)[];
  count: number;
  isOther: boolean;
  depth: number;
}

export interface PivotAxis {
  levels: PivotLevel[];
  groups: PivotGroup[];
  totalGroupCount: number;
  pageIndex: number;
  pageSize: number;
}

/** The aggregates of one cell; `values` is aligned with the result's measures, null = undefined. */
export interface PivotCell {
  row: number;
  column: number;
  count: number;
  values: (number | null)[];
}

export interface PivotMeasure {
  name: string;
  function: PivotFunction;
  propertyName: string | null;
}

export interface PivotSubTotal {
  group: PivotGroup;
  /** Aligned with the other axis' groups; null = empty. */
  cells: (PivotCell | null)[];
  total: PivotCell;
}

export interface PivotResult {
  typeId: string;
  typeName: string;
  query: string;
  durationMs: number;
  sourceCount: number;
  /** The cell limit was hit and the row axis cut short. */
  capped: boolean;
  measures: PivotMeasure[];
  rows: PivotAxis;
  columns: PivotAxis;
  /** Sparse: a row/column pair with no nodes has no cell. */
  cells: PivotCell[];
  rowTotals: PivotCell[];
  columnTotals: PivotCell[];
  grandTotal: PivotCell;
  rowSubTotals: PivotSubTotal[];
  columnSubTotals: PivotSubTotal[];
}

export function fetchPivotModel(storeId: string, typeId: string | null): Promise<PivotModel> {
  return send<PivotModel>("query-pivot-model", { storeId, typeId });
}

export function runPivot(request: PivotRequest): Promise<PivotResult> {
  return send<PivotResult>("query-pivot", request);
}

export function fetchQueryModel(storeId: string): Promise<QueryModel> {
  return send<QueryModel>("query-model", { storeId });
}

export function runSearch(request: SearchRequest): Promise<SearchResult> {
  return send<SearchResult>("query-search", request);
}

export function fetchNode(storeId: string, id: string): Promise<NodeView> {
  return send<NodeView>("query-node", { storeId, id });
}

/**
 * Saves the fields that changed. `values` is keyed by property id and holds the property's own
 * shape (a null clears the property back to its model default); `relations` is keyed by relation
 * property id and holds the complete list of related node ids - the server writes the difference.
 */
export function saveNode(
  storeId: string,
  id: string,
  values: Record<string, unknown>,
  relations: Record<string, string[]>,
): Promise<{ changed: number }> {
  return send<{ changed: number }>("query-save", { storeId, id, values, relations });
}

export function lookupNodes(storeId: string, typeIds: string[], text: string, take = 20): Promise<NodeRef[]> {
  return send<NodeRef[]>("query-lookup", { storeId, typeIds, text, take });
}

/**
 * Where the bytes of a file property are served (a url rather than a command, so an <img> or a
 * <video> can point straight at it; the login cookie authenticates it like everything else).
 *
 * Without a size the file is served as it was uploaded. With one it is an image adjusted to fit
 * inside it - and for a video, a frame taken out of it, which is how a clip gets a picture at all.
 * The conversion may not be done: the answer then carries the store's status image and the header
 * `X-Relatude-Ready: 0`, and asking again is what waits for it.
 */
export function mediaUrl(storeId: string, file: FileValueView, size?: { width?: number; height?: number }): string {
  const query = new URLSearchParams({ storeId, p: file.path, v: file.version });
  if (!size) query.set("original", "true");
  if (size?.width) query.set("w", String(Math.round(size.width)));
  if (size?.height) query.set("h", String(Math.round(size.height)));
  return `${adminBase}/ui/media?${query}`;
}

/**
 * Downloads the whole result set - not just the page on screen - as a csv file, one column per
 * property. A file download rather than a command: it has its own route so the rows can be streamed
 * and the browser can save them straight to disk.
 */
export async function exportCsv(request: SearchRequest): Promise<void> {
  const response = await fetch(`${adminBase}/ui/query-csv`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(request),
  });
  if (!response.ok) {
    let message = `The export failed (HTTP ${response.status}).`;
    try {
      const body = await response.json();
      if (typeof body?.error === "string") message = body.error;
    } catch {
      // not json, keep the default message
    }
    throw new Error(message);
  }
  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  try {
    const link = document.createElement("a");
    link.href = url;
    link.download = filenameOf(response.headers.get("content-disposition")) ?? "query.csv";
    link.click();
  } finally {
    URL.revokeObjectURL(url);
  }
}

function filenameOf(contentDisposition: string | null): string | null {
  const match = contentDisposition?.match(/filename="([^"]+)"/);
  return match ? match[1] : null;
}
