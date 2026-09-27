// Direct hits on a node's id, for the two search boxes that find nodes: the top bar's and the query
// page's.
//
// A search text that is nothing but ids - the internal id the store numbers a node by ("#234" as the
// node form writes it), or the node's guid - is looked up as exactly those nodes, beside the text
// search it runs anyway: nobody pastes a guid hoping for the nodes whose text happens to contain it.
// The lookup is query-nodes, the command the node form reads itself through, which leaves out an id
// no node has rather than failing - so a number that is nobody's id costs one lookup and no error.

import { useMemo } from "react";
import { useLiveResult } from "./hooks";
import { fetchNodes, type Column, type FileValueView, type GeoValue, type Hit, type HitSummaryValue, type NodeView, type PropertyView } from "./query";

/** One id in a search text: an internal id, or a guid in the dashed lower-case form the server reads. */
export type IdTerm = { kind: "int"; id: number } | { kind: "guid"; id: string };

/** A node an id in the search text named, with the id that named it. */
export interface IdMatch {
  term: IdTerm;
  node: NodeView;
}

// A few pasted ids are a list worth looking up; a column of a thousand is not what a search box is
// for, and every one of them is a node read.
const maxIds = 20;
const maxInt32 = 2_147_483_647;
// "#234" is how the node form writes an internal id, so a copy of it is one too
const intPattern = /^#?(\d{1,10})$/;
// the spellings Guid.TryParse reads: dashes or none, braces or parentheses round it
const guidPattern = /^[{(]?([0-9a-f]{8})-?([0-9a-f]{4})-?([0-9a-f]{4})-?([0-9a-f]{4})-?([0-9a-f]{12})[)}]?$/i;

/**
 * The ids a search text names, when that is all it is: every word an internal id or a guid, parted
 * by spaces, commas or semicolons. Null the moment one word is anything else - "chair 2" is a search
 * for chairs, not for node 2.
 */
export function idTermsOf(text: string): IdTerm[] | null {
  const words = text.split(/[\s,;]+/).filter((w) => w.length > 0);
  if (words.length === 0) return null;
  const terms: IdTerm[] = [];
  const seen = new Set<string>();
  for (const word of words) {
    const term = idTermOf(word);
    if (term === null) return null;
    if (seen.has(String(term.id))) continue;
    seen.add(String(term.id));
    terms.push(term);
  }
  return terms.slice(0, maxIds);
}

function idTermOf(word: string): IdTerm | null {
  const int = intPattern.exec(word);
  if (int) {
    const id = Number(int[1]);
    return id <= maxInt32 ? { kind: "int", id } : null;
  }
  const guid = guidPattern.exec(word);
  return guid ? { kind: "guid", id: guid.slice(1, 6).join("-").toLowerCase() } : null;
}

/**
 * The nodes the ids name, in the order they were written. An id no node has is left out, and a node
 * named twice - by its number and by its guid - is there once. One request per id rather than one for
 * the lot: query-nodes fails as a whole when one node in it cannot be read, and one bad id in a pasted
 * list must not hide the others.
 */
export async function lookupIdTerms(storeId: string, terms: readonly IdTerm[]): Promise<IdMatch[]> {
  const nodes = await Promise.all(terms.map((term) => nodeOf(storeId, term)));
  const matches: IdMatch[] = [];
  const seen = new Set<string>();
  nodes.forEach((node, i) => {
    if (node === null || seen.has(node.id)) return;
    seen.add(node.id);
    matches.push({ term: terms[i], node });
  });
  return matches;
}

async function nodeOf(storeId: string, term: IdTerm): Promise<NodeView | null> {
  try {
    // fetchNodes takes internal ids or guids, and tells the two apart by their type
    const found = await fetchNodes(storeId, term.kind === "int" ? [term.id] : [term.id]);
    return found[0] ?? null;
  } catch {
    // a closed database, a node of a type the model no longer has: no direct hit, and no error in a
    // box that is being typed into
    return null;
  }
}

export interface IdMatches {
  /** the ids the text names, or null when it is a search for words */
  terms: IdTerm[] | null;
  /** the nodes they named: the answer to the text as it is now, never to the text before it */
  matches: IdMatch[];
  loading: boolean;
  /** looks the same ids up again, for when the nodes behind them have been written */
  refresh(): void;
}

const noMatches: IdMatch[] = [];

/**
 * The direct hits for what is in a search box, looked up as it is typed (one lookup in flight at a
 * time, see useLiveResult). While the lookup for a new text runs, the answer to the old one is held
 * back rather than shown stale: node 23 at the top of a search for 234 is a wrong answer, not an old
 * one. Nothing is asked while `enabled` is false.
 */
export function useIdMatches(storeId: string | null, text: string, enabled = true): IdMatches {
  const terms = useMemo(() => (enabled ? idTermsOf(text) : null), [enabled, text]);
  // the ids in one spelling, which idTermsOf reads back: "#234" and "234 " are the same lookup
  const key = terms === null ? null : terms.map((t) => String(t.id)).join(" ");
  const request = useMemo(() => (storeId !== null && key !== null ? { storeId, key } : null), [storeId, key]);
  const { result, loading, refresh } = useLiveResult(request, async (r) => ({ ...r, matches: await lookupIdTerms(r.storeId, idTermsOf(r.key) ?? []) }));
  const current = request !== null && result !== null && result.storeId === request.storeId && result.key === request.key;
  return { terms, matches: current ? result.matches : noMatches, loading: request !== null && loading, refresh };
}

// ---- a direct hit as a row of the query page ----

// the server's own numbers for a hit (UIQuery): the summary line, a table cell, references in one
const maxSummaryValues = 4;
const summaryValueLength = 160;
const maxCellLength = 300;
const maxReferencesInCell = 5;
// the property types a summary line reads (UIQuery.isSummaryType)
const summaryTypes = new Set(["String", "StringArray", "Integer", "Long", "Double", "Float", "Decimal", "Boolean", "DateTime", "DateTimeOffset", "TimeSpan"]);

/**
 * A direct hit as a row of the query page's list and table, in the shape the search answers with
 * (UIQuery.hitView) - built here from the node form's view of the node, since the search cannot be
 * asked for one node by its id. Where a search hit says which property held the searched words, this
 * one says it was the id, marked the way a matched word is. The cells and the summary follow the
 * server's formatting (UIQuery.cell and display), and the values are what the table's edit mode
 * types over.
 */
export function hitOfMatch(match: IdMatch, columns: Column[] | null, edit: boolean): Hit {
  const { node, term } = match;
  const snippet =
    term.kind === "int"
      ? { codeName: "Internal id", value: "#" + term.id, fragments: [{ text: "#", isMatch: false }, { text: String(term.id), isMatch: true }] }
      : { codeName: "Id", value: node.id, fragments: [{ text: node.id, isMatch: true }] };
  return {
    id: node.id,
    intId: node.intId,
    typeId: node.typeId,
    typeName: node.typeName,
    displayName: node.displayName,
    nameSample: null,
    snippet: { codeName: snippet.codeName, value: snippet.value, sample: { fragments: snippet.fragments, cutAtStart: false, cutAtEnd: false } },
    address: node.address,
    createdUtc: node.createdUtc,
    changedUtc: node.changedUtc,
    summary: columns === null ? summaryOf(node) : [],
    cells: columns === null ? null : columns.map((column) => cellOf(node, column)),
    values: !edit || columns === null ? null : valuesOf(node, columns),
    idMatch: true,
  };
}

/** A few telling values: the properties in name order, skipping the empty ones and the name itself. */
function summaryOf(node: NodeView): HitSummaryValue[] {
  const summary: HitSummaryValue[] = [];
  for (const property of node.properties) {
    if (summary.length >= maxSummaryValues) break;
    if (!summaryTypes.has(property.type) || property.notes.includes("display name")) continue;
    const text = textOf(property);
    if (text.trim() === "" || text === node.displayName) continue;
    summary.push({ codeName: property.name, value: truncate(text, summaryValueLength) });
  }
  return summary;
}

function cellOf(node: NodeView, column: Column): string {
  switch (column.key) {
    case "__type":
      return node.typeName;
    case "__name":
      return node.displayName;
    case "__id":
      return node.id;
    case "__address":
      return node.address ?? "";
    case "__created":
      return utcText(node.createdUtc);
    case "__changed":
      return utcText(node.changedUtc);
  }
  // a column of a property this node's type does not have - a direct hit need not be of the type
  // the table is showing - is empty, as it is in the form
  const property = node.properties.find((p) => p.id === column.key);
  return property ? truncate(textOf(property), maxCellLength) : "";
}

/** What the table's edit mode starts a cell from (UIQuery.editableValue): the form's value, a date as the table writes it. */
function valuesOf(node: NodeView, columns: Column[]): Record<string, unknown> {
  const values: Record<string, unknown> = {};
  for (const column of columns) {
    if (column.editor === null) continue;
    const property = node.properties.find((p) => p.id === column.key);
    if (!property) continue;
    values[column.key] = property.editor === "datetime" && typeof property.value === "string" ? utcText(property.value) : property.value;
  }
  return values;
}

/** A value as the table and the summary line write it (UIQuery.display), read off the form's view of it. */
function textOf(p: PropertyView): string {
  const v = p.value;
  switch (p.editor) {
    case "relation": // counted, not listed: a row is not the place to read a list
      return p.targets && p.targets.length > 0 ? String(p.targets.length) : "";
    case "reference":
    case "references":
      return (p.targets ?? []).slice(0, maxReferencesInCell).map((t) => t.name).join(", ");
    case "bool":
      return v === true ? "Yes" : "No";
    case "enum":
      return optionLabel(p, v);
    case "enumList":
      return Array.isArray(v) ? v.map((x) => optionLabel(p, x)).join(", ") : "";
    case "stringList":
    case "guidList":
      return Array.isArray(v) ? v.join(", ") : "";
    case "number": // a decimal arrives as text, so it survives the trip; shown the same way either way
      return v === null || v === undefined || v === "" ? "" : numberText(Number(v), 6);
    case "datetime":
      return typeof v === "string" ? dateText(v, true) : "";
    case "datetimeoffset":
      return typeof v === "string" ? dateText(v, false) : "";
    case "file":
      return (v as FileValueView | null)?.name ?? "";
    case "geo": {
      const geo = v as GeoValue | null;
      return geo ? numberText(geo.latitude, 9) + ", " + numberText(geo.longitude, 9) : "";
    }
    case "embedded": {
      // the form's info counts them ("3 inner nodes") where the value holds only the first few
      const count = Number.parseInt(p.info ?? "", 10);
      return count > 0 ? count + " items" : "";
    }
    case "binary":
    case "vector":
      return p.info && p.info !== "empty" ? p.info : "";
    case "unsupported":
      return "";
    default:
      return v === null || v === undefined ? "" : String(v);
  }
}

function optionLabel(p: PropertyView, value: unknown): string {
  return p.options?.find((o) => o.value === value)?.label ?? (value === null || value === undefined ? "" : String(value));
}

// "0.######": the invariant culture, no grouping, no trailing zeros
function numberText(n: number, decimals: number): string {
  return Number.isFinite(n) ? n.toLocaleString("en-US", { maximumFractionDigits: decimals, useGrouping: false }) : String(n);
}

// "yyyy-MM-dd HH:mm", or the date alone for a DateTime at midnight - read off the ISO text as written
// rather than moved into the browser's time zone, since the server formats the value as stored
function dateText(iso: string, dateAloneAtMidnight: boolean): string {
  if (iso.length < 16) return iso;
  if (dateAloneAtMidnight && /^T00:00:00(\.0*)?(Z|[+-]\d\d:\d\d)?$/.test(iso.slice(10))) return iso.slice(0, 10);
  return iso.slice(0, 10) + " " + iso.slice(11, 16);
}

// a node's own timestamps as the table writes them, "yyyy-MM-dd HH:mm:ss", and nothing for an unset one
function utcText(iso: string): string {
  return !iso || iso.startsWith("0001-01-01") ? "" : iso.slice(0, 10) + " " + iso.slice(11, 19);
}

function truncate(s: string, max: number): string {
  return s.length <= max ? s : s.slice(0, max) + "…";
}
