import { useMemo, useState } from "react";
import { IconAlertTriangle, IconArrowUpRight, IconCheck, IconClock, IconDownload, IconTrash } from "@tabler/icons-react";
import { codeLanguages, generateFile, type CodeLanguage, type CodePart, type CodeScope } from "../graphql/codegen";
import { fragments, type DocumentNode, type OperationNode, type SelectionSetNode } from "../graphql/language";
import { namedType, type Schema } from "../graphql/schema";
import { formatBytes } from "../format";
import { CodeEditor } from "./CodeEditor";
import { CopyText } from "./CopyText";

// The right-hand side of the explorer: the answer to the last run (as JSON or as tables of the lists in
// it), the query written as code in a client language, and the list of earlier runs.

export interface RunResult {
  /** the server's answer: { data, errors, extensions } */
  result: unknown;
  ms: number;
  bytes: number;
  at: number;
  operationName: string | null;
  /** what was run, so the answer is read against the query that produced it */
  query: string;
}

export interface RunError {
  error: string;
  at: number;
}

export type RunState = RunResult | RunError;

export interface GqlError {
  message: string;
  locations?: { line: number; column: number }[];
  path?: (string | number)[];
}

export function isError(state: RunState): state is RunError {
  return "error" in state;
}

export function errorsOf(state: RunResult): GqlError[] {
  const r = state.result as { errors?: GqlError[] } | null;
  return Array.isArray(r?.errors) ? r!.errors! : [];
}

export function serverMs(state: RunResult): number | null {
  const r = state.result as { extensions?: { durationMs?: number } } | null;
  return typeof r?.extensions?.durationMs === "number" ? r.extensions.durationMs : null;
}

// ---- tables ----

interface Table {
  path: (string | number)[];
  label: string;
  rows: Record<string, unknown>[];
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return !!v && typeof v === "object" && !Array.isArray(v);
}

/**
 * The lists of objects in an answer, each a table, and the objects with values of their own (a node fetched by
 * id, the counts of a page) as tables of one row. Lists come first: they are what a table is for.
 */
function findTables(data: unknown): Table[] {
  const lists: Table[] = [];
  const singles: Table[] = [];
  const walk = (v: unknown, path: (string | number)[]) => {
    if (Array.isArray(v)) {
      if (v.length > 0 && v.every((x) => isRecord(x) || x === null)) lists.push({ path, label: label(path), rows: v.filter(isRecord) });
      return;
    }
    if (!isRecord(v)) return;
    const own = Object.values(v).some((x) => x === null || typeof x !== "object");
    if (own && path.length > 0) {
      singles.push({ path, label: label(path), rows: [v] });
      // the lists inside it, such as a node's relations or a page's items, are tables of their own
      for (const [k, x] of Object.entries(v)) if (Array.isArray(x)) walk(x, [...path, k]);
      return;
    }
    for (const [k, x] of Object.entries(v)) walk(x, [...path, k]);
  };
  walk(data, []);
  return [...lists, ...singles];
}

function label(path: (string | number)[]): string {
  return path.filter((p) => typeof p === "string").join(".") || "data";
}

/** The schema type of what sits at a path in the answer, read from the query that produced it. */
export function typeAt(schema: Schema, doc: DocumentNode, op: OperationNode, path: (string | number)[]): string | null {
  let type: string | null = op.operation === "mutation" ? schema.mutationType : schema.queryType;
  let sets: { set: SelectionSetNode; on: string }[] = type ? [{ set: op.selectionSet, on: type }] : [];
  const frags = fragments(doc);
  for (const step of path) {
    if (typeof step === "number") continue;
    if (!type) return null;
    const found: { field: string; set?: SelectionSetNode; on: string }[] = [];
    const visit = (set: SelectionSetNode, on: string, depth: number) => {
      if (depth > 20) return;
      for (const s of set.selections) {
        if (s.kind === "Field" && (s.alias ?? s.name) === step) found.push({ field: s.name, set: s.selectionSet, on });
        else if (s.kind === "InlineFragment") visit(s.selectionSet, s.typeCondition ?? on, depth + 1);
        else if (s.kind === "FragmentSpread") {
          const f = frags.get(s.name);
          if (f) visit(f.selectionSet, f.typeCondition, depth + 1);
        }
      }
    };
    for (const x of sets) visit(x.set, x.on, 0);
    if (found.length === 0) return null;
    const def = schema.field(found[0].on, found[0].field) ?? schema.field(type, found[0].field);
    if (!def) return null;
    type = namedType(def.type);
    sets = found.filter((f) => f.set).map((f) => ({ set: f.set!, on: type! }));
  }
  return type;
}

export function ResultView({
  state,
  view,
  schema,
  doc,
  op,
  onOpenNode,
  onGoToLine,
}: {
  state: RunResult;
  view: "json" | "table";
  schema: Schema | null;
  doc: DocumentNode | null;
  op: OperationNode | null;
  onOpenNode: (typeName: string, id: string, name: string | null) => void;
  onGoToLine: (line: number) => void;
}) {
  const errors = errorsOf(state);
  const json = useMemo(() => JSON.stringify(state.result, null, 2), [state.result]);
  return (
    <div className="gx-result">
      {errors.length > 0 && (
        <div className="gx-errors">
          {errors.map((e, i) => (
            <div key={i} className="gx-error">
              <IconAlertTriangle size={13} stroke={1.8} />
              <span>
                {e.message}
                {e.path && <span className="muted"> at {e.path.join(".")}</span>}
              </span>
              {e.locations?.[0] && (
                <button className="link-button" onClick={() => onGoToLine(e.locations![0].line)}>
                  line {e.locations[0].line}:{e.locations[0].column}
                </button>
              )}
            </div>
          ))}
        </div>
      )}
      {view === "json" ? (
        <div className="api-code gx-fill">
          <CodeEditor value={json} onChange={() => {}} language="json" issues={[]} readOnly />
        </div>
      ) : (
        <TableView state={state} schema={schema} doc={doc} op={op} onOpenNode={onOpenNode} />
      )}
    </div>
  );
}

function TableView({
  state,
  schema,
  doc,
  op,
  onOpenNode,
}: {
  state: RunResult;
  schema: Schema | null;
  doc: DocumentNode | null;
  op: OperationNode | null;
  onOpenNode: (typeName: string, id: string, name: string | null) => void;
}) {
  const data = (state.result as { data?: unknown } | null)?.data;
  const tables = useMemo(() => findTables(data), [data]);
  const [chosen, setChosen] = useState<string | null>(null);
  const table = tables.find((t) => t.label === chosen) ?? tables[0];
  if (!table) return <div className="gx-empty muted">No lists or objects in the answer to show as a table.</div>;
  const columns: string[] = [];
  for (const r of table.rows) for (const k of Object.keys(r)) if (!columns.includes(k)) columns.push(k);
  const rowType = schema && doc && op ? typeAt(schema, doc, op, table.path) : null;
  const openable = (row: Record<string, unknown>, fallback: string | null): string | null => {
    if (!schema || typeof row.id !== "string") return null;
    const t = typeof row.__typename === "string" ? row.__typename : fallback;
    return t && schema.singleFieldFor(t) ? t : null;
  };
  const cellType = (column: string): string | null => (schema && doc && op ? typeAt(schema, doc, op, [...table.path, column]) : null);
  return (
    <div className="gx-table-wrap">
      {tables.length > 1 && (
        <div className="gx-table-pick">
          {tables.map((t) => (
            <button key={t.label} className={"gx-chip" + (t === table ? " active" : "")} onClick={() => setChosen(t.label)}>
              {t.label} <span className="muted">{t.rows.length}</span>
            </button>
          ))}
        </div>
      )}
      {table.rows.length === 1 ? (
        // one object: its fields down the page, the way a record is read
        <div className="gx-table-scroll">
          <table className="gx-table gx-record">
            <tbody>
              {(() => {
                const row = table.rows[0];
                const type = openable(row, rowType);
                return (
                  <>
                    {type && (
                      <tr>
                        <th>open</th>
                        <td>
                          <button
                            className="gx-node-link"
                            onClick={() => onOpenNode(type, row.id as string, typeof row.displayName === "string" && row.displayName ? row.displayName : null)}
                          >
                            {type} with all its fields <IconArrowUpRight size={12} stroke={1.8} />
                          </button>
                        </td>
                      </tr>
                    )}
                    {columns.map((c) => (
                      <tr key={c}>
                        <th>{c}</th>
                        <td>
                          <Cell value={row[c]} schema={schema} type={cellType(c)} onOpenNode={onOpenNode} />
                        </td>
                      </tr>
                    ))}
                  </>
                );
              })()}
            </tbody>
          </table>
        </div>
      ) : (
        <div className="gx-table-scroll">
          <table className="gx-table">
            <thead>
              <tr>
                <th />
                {columns.map((c) => (
                  <th key={c}>{c}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {table.rows.map((row, i) => {
                const type = openable(row, rowType);
                return (
                  <tr key={i}>
                    <td className="gx-row-tools">
                      {type && (
                        <button
                          className="icon-button small"
                          title={`Open this ${type} with all its fields`}
                          onClick={() => onOpenNode(type, row.id as string, typeof row.displayName === "string" && row.displayName ? row.displayName : null)}
                        >
                          <IconArrowUpRight size={13} stroke={1.8} />
                        </button>
                      )}
                    </td>
                    {columns.map((c) => (
                      <td key={c}>
                        <Cell value={row[c]} schema={schema} type={cellType(c)} onOpenNode={onOpenNode} />
                      </td>
                    ))}
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function Cell({
  value,
  schema,
  type,
  onOpenNode,
}: {
  value: unknown;
  schema: Schema | null;
  type: string | null;
  onOpenNode: (typeName: string, id: string, name: string | null) => void;
}) {
  if (value === null || value === undefined) return <span className="gx-null">null</span>;
  if (typeof value === "number") return <span className="gx-num">{value}</span>;
  if (typeof value === "boolean") return <span className="gx-bool">{String(value)}</span>;
  if (typeof value === "string")
    return (
      <span className="gx-str" title={value.length > 80 ? value : undefined}>
        {value.length > 80 ? value.slice(0, 80) + "…" : value}
      </span>
    );
  const node = (v: unknown) => {
    if (!isRecord(v)) return <span className="gx-json">{JSON.stringify(v)}</span>;
    const t = typeof v.__typename === "string" ? v.__typename : type;
    const name = typeof v.displayName === "string" && v.displayName ? v.displayName : typeof v.name === "string" && v.name ? v.name : null;
    const text = name ?? (typeof v.id === "string" ? v.id : JSON.stringify(v));
    if (schema && t && typeof v.id === "string" && schema.singleFieldFor(t)) {
      return (
        <button className="gx-node-link" title={`Open ${t} ${v.id}`} onClick={() => onOpenNode(t, v.id as string, name)}>
          {text.length > 40 ? text.slice(0, 40) + "…" : text}
        </button>
      );
    }
    return (
      <span className="gx-json" title={JSON.stringify(v, null, 2)}>
        {text.length > 60 ? text.slice(0, 60) + "…" : text}
      </span>
    );
  };
  if (Array.isArray(value)) {
    if (value.length === 0) return <span className="gx-null">[]</span>;
    if (value.every((v) => !isRecord(v))) return <span className="gx-json">{value.map((v) => (typeof v === "string" ? v : JSON.stringify(v))).join(", ")}</span>;
    return (
      <span className="gx-node-list">
        {value.slice(0, 5).map((v, i) => (
          <span key={i}>{node(v)}</span>
        ))}
        {value.length > 5 && <span className="muted">+{value.length - 5}</span>}
      </span>
    );
  }
  return node(value);
}

// ---- code ----

export function CodeView({
  schema,
  doc,
  op,
  variables,
  endpoint,
  apiKey,
  language,
  scope,
  part,
  onLanguage,
  onScope,
  onPart,
  problem,
  audience,
}: {
  schema: Schema | null;
  doc: DocumentNode | null;
  op: OperationNode | null;
  variables: string;
  endpoint: string;
  apiKey: boolean;
  language: CodeLanguage;
  scope: CodeScope;
  part: CodePart;
  onLanguage: (l: CodeLanguage) => void;
  onScope: (s: CodeScope) => void;
  onPart: (p: CodePart) => void;
  problem: string | null;
  audience: "admin" | "public";
}) {
  const info = codeLanguages.find((l) => l.id === language) ?? codeLanguages[0];
  const text = useMemo(() => {
    if (!schema || !doc || !op) return "";
    try {
      return generateFile(language, scope, info.models ? part : "all", { schema, doc, op, variablesText: variables, endpoint, apiKey });
    } catch (e) {
      return "// The code could not be written: " + (e instanceof Error ? e.message : String(e));
    }
  }, [schema, doc, op, variables, endpoint, apiKey, language, scope, part, info.models]);
  const fileName = (op?.name ?? "query").replace(/^./, (c) => c.toLowerCase()) + info.extension;
  return (
    <div className="gx-code">
      <div className="gx-code-bar">
        <select className="select compact" value={language} onChange={(e) => onLanguage(e.target.value as CodeLanguage)} aria-label="Language">
          {codeLanguages.map((l) => (
            <option key={l.id} value={l.id}>
              {l.label}
            </option>
          ))}
        </select>
        {info.models && (
          <>
            <div className="module-switch compact">
              <button className={scope === "query" ? "active" : ""} onClick={() => onScope("query")} title="Types for exactly what this query selects">
                This query
              </button>
              <button className={scope === "schema" ? "active" : ""} onClick={() => onScope("schema")} title="Types for every node type, page, enum and input">
                Whole schema
              </button>
            </div>
            <div className="module-switch compact">
              {(["all", "model", "connect"] as CodePart[]).map((p) => (
                <button key={p} className={part === p ? "active" : ""} onClick={() => onPart(p)}>
                  {p === "all" ? "Both" : p === "model" ? "Model" : "Connect"}
                </button>
              ))}
            </div>
          </>
        )}
        <span className="gx-code-tools">
          <CopyText text={text} title="Copy the code" small />
          <button className="icon-button small" title={"Download " + fileName} disabled={!text} onClick={() => download(fileName, text)}>
            <IconDownload size={15} stroke={1.8} />
          </button>
        </span>
      </div>
      {problem ? (
        <div className="gx-empty muted">{problem}</div>
      ) : (
        <div className="api-code gx-fill">
          <CodeEditor value={text} onChange={() => {}} language={info.editor} issues={[]} readOnly />
        </div>
      )}
      <p className="gx-code-note muted">
        {audience === "admin"
          ? `The code calls the endpoint as it is saved, at ${endpoint}; the explorer runs the definition as it is here.`
          : `The code calls this endpoint, at ${endpoint}.`}
        {apiKey ? " The api key is read from the GRAPHQL_API_KEY environment variable." : ""}
      </p>
    </div>
  );
}

export function download(name: string, text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: "text/plain" }));
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  a.click();
  URL.revokeObjectURL(url);
}

// ---- history ----

export interface HistoryEntry {
  query: string;
  variables: string;
  operationName: string | null;
  at: number;
  ms: number;
  ok: boolean;
  title: string;
}

export function HistoryList({ entries, onOpen, onClear }: { entries: HistoryEntry[]; onOpen: (e: HistoryEntry, newTab: boolean) => void; onClear: () => void }) {
  if (entries.length === 0) return <div className="gx-empty muted">Nothing has been run yet. Every run is kept here, newest first.</div>;
  return (
    <div className="gx-history">
      {entries.map((e, i) => (
        <div key={e.at + ":" + i} className="gx-history-entry" onClick={() => onOpen(e, false)} title="Open in this tab">
          <span className={"gx-dot" + (e.ok ? " ok" : " bad")}>{e.ok ? <IconCheck size={11} stroke={2.4} /> : <IconAlertTriangle size={11} stroke={2} />}</span>
          <span className="gx-history-title">{e.title}</span>
          <span className="gx-history-meta muted">
            <IconClock size={11} stroke={1.8} /> {ago(e.at)} · {Math.round(e.ms)} ms
          </span>
          <button
            className="icon-button small"
            title="Open in a new tab"
            onClick={(ev) => {
              ev.stopPropagation();
              onOpen(e, true);
            }}
          >
            <IconArrowUpRight size={13} stroke={1.8} />
          </button>
        </div>
      ))}
      <button className="action-button small gx-history-clear" onClick={onClear}>
        <IconTrash size={13} stroke={1.8} /> Clear the history
      </button>
    </div>
  );
}

function ago(at: number): string {
  const s = Math.max(0, Math.round((Date.now() - at) / 1000));
  if (s < 60) return s + " s ago";
  const m = Math.round(s / 60);
  if (m < 60) return m + " min ago";
  const h = Math.round(m / 60);
  if (h < 24) return h + " h ago";
  return new Date(at).toLocaleDateString();
}

export function sizeText(bytes: number): string {
  return formatBytes(bytes);
}
