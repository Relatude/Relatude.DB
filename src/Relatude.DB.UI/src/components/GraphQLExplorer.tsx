import { useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent, type ReactNode } from "react";
import {
  IconAlertTriangle,
  IconArrowsMaximize,
  IconArrowsMinimize,
  IconBook2,
  IconCheck,
  IconCode,
  IconHistory,
  IconLayoutSidebarLeftCollapse,
  IconLayoutSidebarLeftExpand,
  IconListTree,
  IconPlayerPlay,
  IconPlus,
  IconRefresh,
  IconSchool,
  IconSquareX,
  IconTable,
  IconBraces,
  IconWand,
  IconX,
} from "@tabler/icons-react";
import "../explorer.css";
import {
  builderOperation,
  fragmentType,
  isFragmentSegment,
  newArgument,
  newField,
  setArgument,
  setVariable,
  toggleField,
  toLiteral,
  toVariable,
  type BuildPath,
  type FieldDefaults,
} from "../graphql/build";
import type { CodeLanguage, CodePart, CodeScope } from "../graphql/codegen";
import { complete } from "../graphql/complete";
import {
  lineAndColumn,
  operationAt,
  operations,
  parse,
  print,
  tryParse,
  type DocumentNode,
  type OperationNode,
  type OperationType,
  type SelectionNode,
  type SelectionSetNode,
  type ValueNode,
} from "../graphql/language";
import { isList, isRequired, namedType, Schema, stripNonNull, type ArgInfo, type FieldInfo } from "../graphql/schema";
import { analyze, printTypeRef, toLintIssues, variableProblems } from "../graphql/validate";
import { lint } from "../code/lint";
import { showConfirm } from "../dialogs";
import type { ExplorerData, GuideExample } from "../server/graphql";
import { CodeEditor, type CodeEditorApi } from "./CodeEditor";
import { CopyText } from "./CopyText";
import { ExplorerBuilder, type BuilderActions } from "./ExplorerBuilder";
import { ExplorerDocs, type DocsPlace } from "./ExplorerDocs";
import { ExplorerGuide, type GuideAction } from "./ExplorerGuide";
import { CodeView, errorsOf, HistoryList, isError, ResultView, serverMs, sizeText, type HistoryEntry, type RunResult, type RunState } from "./ExplorerResult";
import { Loading } from "./Loading";

// The explorer of a GraphQL endpoint: a place to find out what the endpoint can answer and to try it.
// On the left, a builder to tick a query together, the schema's docs, a guide with runnable examples and
// the history of runs; in the middle the query and its variables, checked and completed against the
// schema as they are typed; on the right the answer, as JSON or tables, or the query as code in a
// client language. Where the schema comes from and where queries go is the source's business: in the admin
// UI they run against the definition as it is in the editor, saved or not; on the endpoint's own page
// (src/explorer) they go to the endpoint's url. Tabs, history and the layout are kept in the browser.

/** Where the explorer reads the endpoint from and sends its queries to. */
export interface ExplorerSource {
  /** tabs and history are kept in the browser under this key */
  storageKey: string;
  /** changes whenever what load answers may have: the definition being edited, in the admin UI */
  version: string;
  load(signal: AbortSignal): Promise<ExplorerData>;
  /** runs a request and gives the GraphQL answer: { data, errors, extensions } */
  execute(request: { query: string; variables?: unknown; operationName?: string }): Promise<unknown>;
  /** the absolute url the generated code calls */
  endpoint: string;
  apiKey: boolean;
  introspection: boolean;
  /** "admin" runs the definition being edited; "public" is the endpoint's own page */
  audience: "admin" | "public";
}

interface ExplorerTab {
  id: string;
  query: string;
  variables: string;
  /** the guide topic the tab was opened from, so running the topic again reuses it */
  guideId?: string;
  title?: string;
}

type SidePane = "build" | "docs" | "guide" | "history";

const settleMs = 300;
const historyLimit = 60;

function load<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    return raw === null ? fallback : (JSON.parse(raw) as T);
  } catch {
    return fallback;
  }
}

function store(key: string, value: unknown) {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // a full or blocked storage only costs the memory of the layout
  }
}

let tabCounter = 0;
function newTab(query = "", variables = "", extra: Partial<ExplorerTab> = {}): ExplorerTab {
  return { id: Date.now().toString(36) + "-" + tabCounter++, query, variables, ...extra };
}

function titleOf(query: string, fallback = "New tab"): string {
  const named = /^\s*(?:query|mutation)\s+([A-Za-z_]\w*)/m.exec(query);
  if (named) return named[1];
  const field = /\{\s*(?:[A-Za-z_]\w*\s*:\s*)?([A-Za-z_]\w*)/.exec(query);
  return field ? field[1] : fallback;
}

export function GraphQLExplorer({ source, fullWindow, tools }: { source: ExplorerSource; fullWindow?: boolean; tools?: ReactNode }) {
  const key = (what: string) => `gqlExplorer.${what}.${source.storageKey}`;
  // the latest source, for the calls; the data is fetched again only when its version changes
  const sourceRef = useRef(source);
  sourceRef.current = source;

  // ---- what the server tells about the endpoint ----
  const [data, setData] = useState<ExplorerData | null>(null);
  const [dataError, setDataError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    const timer = window.setTimeout(() => {
      sourceRef.current.load(controller.signal).then(
        (d) => {
          setData(d);
          setDataError(null);
          setLoading(false);
        },
        (e) => {
          if (controller.signal.aborted) return;
          setDataError(e instanceof Error ? e.message : String(e));
          setLoading(false);
        },
      );
    }, settleMs);
    return () => {
      controller.abort();
      window.clearTimeout(timer);
    };
  }, [source.version]);
  const schema = useMemo(() => (data ? new Schema(data.schema, source.introspection) : null), [data, source.introspection]);

  // ---- tabs ----
  const [tabs, setTabs] = useState<ExplorerTab[]>(() => {
    const saved = load<ExplorerTab[]>(key("tabs"), []);
    return Array.isArray(saved) && saved.length > 0 ? saved : [newTab()];
  });
  const [activeId, setActiveId] = useState<string>(() => load<string>(key("active"), ""));
  const tab = tabs.find((t) => t.id === activeId) ?? tabs[0];
  useEffect(() => store(key("tabs"), tabs), [tabs]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => store(key("active"), tab.id), [tab.id]); // eslint-disable-line react-hooks/exhaustive-deps
  const updateTab = (id: string, patch: Partial<ExplorerTab>) => setTabs((ts) => ts.map((t) => (t.id === id ? { ...t, ...patch } : t)));
  const addTab = (t: ExplorerTab) => {
    setTabs((ts) => [...ts, t]);
    setActiveId(t.id);
    setCursor(0);
  };
  const closeTab = (id: string) => {
    setTabs((ts) => {
      const rest = ts.filter((t) => t.id !== id);
      if (rest.length === 0) {
        const fresh = newTab();
        setActiveId(fresh.id);
        return [fresh];
      }
      if (id === tab.id) setActiveId(rest[Math.max(0, ts.findIndex((t) => t.id === id) - 1)].id);
      return rest;
    });
  };

  // a first visit starts with something to run
  const seeded = useRef(false);
  useEffect(() => {
    if (!data || seeded.current) return;
    seeded.current = true;
    if (tabs.length === 1 && !tabs[0].query.trim()) {
      const first = data.guide.examples.find((e) => e.id === "first");
      if (first) updateTab(tabs[0].id, { query: first.query, variables: first.variables ?? "", guideId: "first" });
    }
  }, [data]); // eslint-disable-line react-hooks/exhaustive-deps

  // ---- layout and preferences ----
  const [side, setSide] = useState<SidePane>(() => load("gqlExplorer.side", "build"));
  const [sideOpen, setSideOpen] = useState(() => load("gqlExplorer.sideOpen", true));
  const [sideWidth, setSideWidth] = useState(() => load("gqlExplorer.sideWidth", 330));
  const [right, setRight] = useState<"result" | "code">(() => load("gqlExplorer.right", "result"));
  const [resultView, setResultView] = useState<"json" | "table">(() => load("gqlExplorer.resultView", "json"));
  const [codeLanguage, setCodeLanguage] = useState<CodeLanguage>(() => load("gqlExplorer.codeLanguage", "typescript"));
  const [codeScope, setCodeScope] = useState<CodeScope>(() => load("gqlExplorer.codeScope", "query"));
  const [codePart, setCodePart] = useState<CodePart>(() => load("gqlExplorer.codePart", "all"));
  useEffect(() => store("gqlExplorer.side", side), [side]);
  useEffect(() => store("gqlExplorer.sideOpen", sideOpen), [sideOpen]);
  useEffect(() => store("gqlExplorer.sideWidth", sideWidth), [sideWidth]);
  useEffect(() => store("gqlExplorer.right", right), [right]);
  useEffect(() => store("gqlExplorer.resultView", resultView), [resultView]);
  useEffect(() => store("gqlExplorer.codeLanguage", codeLanguage), [codeLanguage]);
  useEffect(() => store("gqlExplorer.codeScope", codeScope), [codeScope]);
  useEffect(() => store("gqlExplorer.codePart", codePart), [codePart]);
  const [docsPlace, setDocsPlace] = useState<DocsPlace[]>([]);
  const [maximized, setMaximized] = useState(false);
  useEffect(() => {
    if (!maximized) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape" && !e.defaultPrevented) setMaximized(false);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [maximized]);

  // ---- runs and history ----
  const [results, setResults] = useState<Record<string, RunState>>({});
  const [running, setRunning] = useState<string | null>(null);
  const [history, setHistory] = useState<HistoryEntry[]>(() => load<HistoryEntry[]>(key("history"), []));
  useEffect(() => store(key("history"), history), [history]); // eslint-disable-line react-hooks/exhaustive-deps

  // ---- the query being edited ----
  const [cursor, setCursor] = useState(0);
  const editor = useRef<CodeEditorApi | null>(null);
  const analysis = useMemo(() => analyze(tab.query, schema), [tab.query, schema]);
  const doc = analysis.doc;
  const parseError = !doc && analysis.issues.length > 0 ? analysis.issues[0].message : null;
  const ops = doc ? operations(doc) : [];
  const activeOp = doc ? operationAt(doc, cursor) : null;
  const activeIndex = activeOp ? ops.indexOf(activeOp) : 0;
  const issues = useMemo(() => toLintIssues(tab.query, analysis.issues), [tab.query, analysis]);
  const varLint = useMemo(() => lint(tab.variables, "json"), [tab.variables]);
  const varProblems = doc && activeOp ? variableProblems(doc, activeOp, tab.variables) : null;
  const variablesJson = useMemo(() => {
    try {
      const v = JSON.parse(tab.variables || "{}");
      return v && typeof v === "object" && !Array.isArray(v) ? (v as Record<string, unknown>) : {};
    } catch {
      return {};
    }
  }, [tab.variables]);
  const samples = data?.samples ?? {};

  const setQuery = (q: string) => updateTab(tab.id, { query: q });
  const setVariables = (v: string) => updateTab(tab.id, { variables: v });
  const applyDoc = (next: DocumentNode, variables?: string) => updateTab(tab.id, { query: print(next), ...(variables !== undefined ? { variables } : {}) });

  function prettify() {
    const parsed = tryParse(tab.query);
    if (parsed.doc) setQuery(print(parsed.doc));
    let vars = tab.variables;
    try {
      if (vars.trim()) vars = JSON.stringify(JSON.parse(vars), null, 2);
      setVariables(vars);
    } catch {
      // variables that do not parse are left as they are
    }
  }

  // ---- the builder's defaults ----
  function fieldAtPath(kind: OperationType, path: BuildPath): { field: FieldInfo | null; type: string | null } {
    if (!schema) return { field: null, type: null };
    let type: string | null = kind === "mutation" ? schema.mutationType : schema.queryType;
    let field: FieldInfo | null = null;
    for (const seg of path) {
      if (!type) return { field: null, type: null };
      if (isFragmentSegment(seg)) {
        type = fragmentType(seg);
        field = null;
        continue;
      }
      field = schema.field(type, seg) ?? null;
      type = field ? namedType(field.type) : null;
    }
    return { field, type };
  }

  function defaultSelection(typeName: string | null, depth = 0): SelectionNode[] | undefined {
    if (!schema || !typeName || !schema.isComposite(typeName)) return undefined;
    const fields = schema.fields(typeName);
    const has = (n: string) => fields.some((f) => f.name === n);
    if (has("items") && has("totalCount") && depth < 2) {
      const items = fields.find((f) => f.name === "items")!;
      return [newField("totalCount"), newField("items", [], defaultSelection(namedType(items.type), depth + 1))];
    }
    if (has("id") && has("displayName")) return [newField("id"), newField("displayName")];
    const leaves = fields.filter((f) => !schema.isComposite(namedType(f.type)) && !f.args.some(isRequired)).slice(0, 3);
    return leaves.length > 0 ? leaves.map((f) => newField(f.name)) : [newField("__typename")];
  }

  function requiredValue(arg: ArgInfo, field: FieldInfo): ValueNode {
    const loc = { start: -1, end: -1 };
    const named = namedType(arg.type);
    const type = schema?.type(named);
    if (isList(stripNonNull(arg.type))) return { kind: "List", values: [], ...loc };
    if (type?.kind === "INPUT_OBJECT") return { kind: "Object", fields: [], ...loc };
    if (type?.kind === "ENUM") return { kind: "Enum", value: type.enumValues?.[0] ?? "", ...loc };
    if (named === "ID") {
      // a real id only where nothing is changed by it
      const sample = field.kind === "single" ? samples[namedType(field.type)] : undefined;
      return { kind: "String", value: sample?.id ?? "", ...loc };
    }
    if (named === "Int" || named === "Long" || named === "Float" || named === "Decimal") return { kind: "Int", value: "0", ...loc };
    if (named === "Boolean") return { kind: "Boolean", value: true, ...loc };
    return { kind: "String", value: "", ...loc };
  }

  const defaults = (kind: OperationType): FieldDefaults => ({
    args(path) {
      const { field } = fieldAtPath(kind, path);
      return field ? field.args.filter(isRequired).map((a) => newArgument(a.name, requiredValue(a, field))) : [];
    },
    selection(path) {
      const last = path[path.length - 1];
      if (isFragmentSegment(last) && schema) {
        // a subtype starts with what only it has
        const parent = fieldAtPath(kind, path.slice(0, -1)).type;
        const own = schema.fields(fragmentType(last)).filter((f) => !(parent && schema.field(parent, f.name)) && !schema.isComposite(namedType(f.type)));
        return own.length > 0 ? own.slice(0, 2).map((f) => newField(f.name)) : [newField("__typename")];
      }
      return defaultSelection(fieldAtPath(kind, path).type);
    },
  });

  const builderActions: BuilderActions = {
    toggle(kind, path, on) {
      if (parseError) return;
      applyDoc(toggleField(doc, kind, activeIndex, path, on, defaults(kind)));
    },
    setArgument(kind, path, name, value) {
      if (doc) applyDoc(setArgument(doc, kind, activeIndex, path, name, value));
    },
    toVariable(kind, path, argName, vp, typeRef) {
      if (!doc) return;
      const r = toVariable(doc, kind, activeIndex, path, argName, vp, typeRef, tab.variables);
      applyDoc(r.doc, r.variables);
    },
    toLiteral(kind, path, argName, vp) {
      if (!doc) return;
      const r = toLiteral(doc, kind, activeIndex, path, argName, vp, tab.variables);
      applyDoc(r.doc, r.variables);
    },
    setVariable(name, value) {
      setVariables(setVariable(tab.variables, name, value));
    },
    openDocs(typeName, field) {
      setSideOpen(true);
      setSide("docs");
      setDocsPlace([{ type: typeName, field }]);
    },
  };

  // ---- running ----
  async function run(target?: { tabId: string; query: string; variables: string; operationName?: string | null }) {
    const t = target ?? { tabId: tab.id, query: tab.query, variables: tab.variables };
    let operationName = t.operationName;
    if (operationName === undefined) {
      const parsed = tryParse(t.query);
      const all = parsed.doc ? operations(parsed.doc) : [];
      const chosen = parsed.doc && t.tabId === tab.id ? operationAt(parsed.doc, cursor) : all[0];
      operationName = all.length > 1 ? (chosen?.name ?? null) : null;
    }
    let variables: unknown = undefined;
    if (t.variables.trim()) {
      try {
        variables = JSON.parse(t.variables);
      } catch (e) {
        setResults((r) => ({ ...r, [t.tabId]: { error: "The variables are not valid JSON: " + (e instanceof Error ? e.message : String(e)), at: Date.now() } }));
        setRight("result");
        return;
      }
    }
    setRunning(t.tabId);
    setRight("result");
    const started = performance.now();
    try {
      const result = await sourceRef.current.execute({ query: t.query, variables, operationName: operationName ?? undefined });
      const ms = performance.now() - started;
      const state: RunResult = { result, ms, bytes: new Blob([JSON.stringify(result)]).size, at: Date.now(), operationName: operationName ?? null, query: t.query };
      setResults((rs) => ({ ...rs, [t.tabId]: state }));
      const entry: HistoryEntry = {
        query: t.query,
        variables: t.variables,
        operationName: operationName ?? null,
        at: state.at,
        ms,
        ok: errorsOf(state).length === 0,
        title: operationName ?? titleOf(t.query),
      };
      setHistory((h) => [entry, ...h.filter((x) => !(x.query === entry.query && x.variables === entry.variables))].slice(0, historyLimit));
    } catch (e) {
      setResults((rs) => ({ ...rs, [t.tabId]: { error: e instanceof Error ? e.message : String(e), at: Date.now() } }));
    } finally {
      setRunning(null);
    }
  }

  /** Opens a node in a tab of its own, with all its fields and the nodes it relates to. */
  function openNode(typeName: string, id: string, name: string | null) {
    if (!schema) return;
    const query = nodeQuery(schema, typeName);
    if (!query) return;
    const t = newTab(query, JSON.stringify({ id }, null, 2), { title: `${typeName} ${name || id.slice(0, 8)}` });
    addTab(t);
    void run({ tabId: t.id, query: t.query, variables: t.variables, operationName: null });
  }

  async function openExample(topic: { id: string; title: string }, example: GuideExample, runIt: boolean) {
    if (runIt && example.isMutation) {
      const { ok } = await showConfirm("Run a mutation", `"${topic.title}" changes data in the database. Run it?`, { confirmLabel: "Run", danger: true });
      if (!ok) return;
    }
    const existing = tabs.find((t) => t.guideId === topic.id);
    let id: string;
    if (existing) {
      updateTab(existing.id, { query: example.query, variables: example.variables ?? "" });
      setActiveId(existing.id);
      setCursor(0);
      id = existing.id;
    } else {
      const t = newTab(example.query, example.variables ?? "", { guideId: topic.id, title: topic.title });
      addTab(t);
      id = t.id;
    }
    if (runIt) void run({ tabId: id, query: example.query, variables: example.variables ?? "", operationName: firstOperationName(example.query) });
  }

  function guideAction(action: GuideAction) {
    const first = data?.guide.examples.find((e) => e.id === "first");
    switch (action) {
      case "builder":
        if (tab.query.trim()) addTab(newTab("", "", { title: "Built" }));
        setSideOpen(true);
        setSide("build");
        break;
      case "complete": {
        const t = newTab("{\n  \n}\n", "", { title: "Completion" });
        addTab(t);
        window.setTimeout(() => {
          editor.current?.setCursor(4);
          editor.current?.openCompletion();
        }, 80);
        break;
      }
      case "docs":
        setSideOpen(true);
        setSide("docs");
        setDocsPlace([]);
        break;
      case "mistake": {
        if (!first) break;
        const field = /\{\s*([A-Za-z_]\w*)/.exec(first.query)?.[1];
        if (!field) break;
        const wrong = field.slice(0, 2) + field.slice(1);
        addTab(newTab(first.query.replace(field, wrong), first.variables ?? "", { title: "A mistake" }));
        break;
      }
      case "table":
        setResultView("table");
        if (first) void openExample({ id: "first", title: "Your first query" }, first, true);
        break;
      case "code":
        setRight("code");
        break;
      case "history":
        setSideOpen(true);
        setSide("history");
        break;
    }
  }

  // ---- the left pane's width ----
  const drag = useRef<{ x: number; width: number } | null>(null);
  function startDrag(e: ReactPointerEvent<HTMLDivElement>) {
    drag.current = { x: e.clientX, width: sideWidth };
    e.currentTarget.setPointerCapture(e.pointerId);
  }
  function moveDrag(e: ReactPointerEvent<HTMLDivElement>) {
    if (!drag.current) return;
    setSideWidth(Math.max(240, Math.min(720, drag.current.width + e.clientX - drag.current.x)));
  }

  if (!data || !schema) {
    if (dataError) return <div className="logs-note">The explorer could not read the schema: {dataError}</div>;
    return <Loading label="Reading the schema…" />;
  }

  const state = results[tab.id];
  const ok = state && !isError(state) ? state : null;
  const errorCount = ok ? errorsOf(ok).length : 0;
  const endpoint = source.endpoint;
  const firstIssue = analysis.issues[0];
  const queryOp = builderOperation(doc, "query", activeIndex);
  const mutationOp = builderOperation(doc, "mutation", activeIndex);

  return (
    <div className={"gx" + (maximized ? " max" : "") + (fullWindow ? " full" : "")}>
      <div className="gx-bar">
        <button className="icon-button small" title={sideOpen ? "Hide the side panel" : "Show the side panel"} onClick={() => setSideOpen(!sideOpen)}>
          {sideOpen ? <IconLayoutSidebarLeftCollapse size={16} stroke={1.8} /> : <IconLayoutSidebarLeftExpand size={16} stroke={1.8} />}
        </button>
        <div className="gx-tabs" role="tablist">
          {tabs.map((t) => (
            <div
              key={t.id}
              className={"gx-tab" + (t.id === tab.id ? " active" : "")}
              role="tab"
              aria-selected={t.id === tab.id}
              onAuxClick={(e) => {
                // the middle button closes a tab, as in a browser
                if (e.button === 1) closeTab(t.id);
              }}
            >
              <button
                className="gx-tab-name"
                title={t.title ?? titleOf(t.query)}
                onClick={() => {
                  setActiveId(t.id);
                  setCursor(0);
                }}
              >
                {running === t.id && <IconRefresh size={12} stroke={2} className="spinning" />}
                {t.title ?? titleOf(t.query)}
              </button>
              <button className="gx-tab-close" title="Close the tab (middle-click does too)" onClick={() => closeTab(t.id)}>
                <IconX size={12} stroke={2} />
              </button>
            </div>
          ))}
          <button className="icon-button small" title="New tab" onClick={() => addTab(newTab())}>
            <IconPlus size={15} stroke={1.8} />
          </button>
          {tabs.length > 2 && (
            <button className="icon-button small" title="Close the other tabs" onClick={() => setTabs((ts) => ts.filter((t) => t.id === tab.id))}>
              <IconSquareX size={15} stroke={1.8} />
            </button>
          )}
        </div>
        {loading && <IconRefresh size={13} stroke={1.8} className="spinning muted" aria-label="Reading the schema" />}
        {tools}
        {!fullWindow && (
          <button className="icon-button small" title={maximized ? "Back to the page (Esc)" : "Use the whole window"} onClick={() => setMaximized(!maximized)}>
            {maximized ? <IconArrowsMinimize size={16} stroke={1.8} /> : <IconArrowsMaximize size={16} stroke={1.8} />}
          </button>
        )}
        {ops.length > 1 && (
          <select
            className="select compact gx-op-pick"
            value={activeIndex}
            title="The operation that runs: the one the caret is in"
            onChange={(e) => {
              const op = ops[Number(e.target.value)];
              if (op) {
                setCursor(op.selectionSet.start + 1);
                editor.current?.setCursor(op.selectionSet.start + 1);
              }
            }}
          >
            {ops.map((o, i) => (
              <option key={i} value={i}>
                {o.name ?? `(${o.operation} ${i + 1})`}
              </option>
            ))}
          </select>
        )}
        <button className="action-button primary small gx-run" onClick={() => run()} disabled={running !== null || !tab.query.trim()} title="Run (Ctrl+Enter)">
          <IconPlayerPlay size={14} stroke={1.8} /> {running === tab.id ? "Running…" : "Run"}
        </button>
      </div>

      <div className={"gx-main" + (sideOpen ? "" : " no-side")} style={{ ["--gx-side" as string]: sideWidth + "px" }}>
        {sideOpen && (
          <div className="gx-side">
            <div className="module-switch compact gx-side-switch">
              <button className={side === "build" ? "active" : ""} onClick={() => setSide("build")}>
                <IconListTree size={13} stroke={1.8} /> Build
              </button>
              <button className={side === "docs" ? "active" : ""} onClick={() => setSide("docs")}>
                <IconBook2 size={13} stroke={1.8} /> Docs
              </button>
              <button className={side === "guide" ? "active" : ""} onClick={() => setSide("guide")}>
                <IconSchool size={13} stroke={1.8} /> Guide
              </button>
              <button className={side === "history" ? "active" : ""} onClick={() => setSide("history")}>
                <IconHistory size={13} stroke={1.8} /> History
              </button>
            </div>
            <div className="gx-side-body">
              {side === "build" && (
                <ExplorerBuilder
                  schema={schema}
                  queryOp={queryOp}
                  mutationOp={mutationOp}
                  samples={samples}
                  variables={variablesJson}
                  error={parseError}
                  actions={builderActions}
                  onReset={() => updateTab(tab.id, { query: "", variables: "" })}
                />
              )}
              {side === "docs" && <ExplorerDocs schema={schema} place={docsPlace} onPlace={setDocsPlace} onAddRoot={(kind, field) => builderActions.toggle(kind, [field], true)} />}
              {side === "guide" && (
                <ExplorerGuide audience={source.audience} examples={data.guide.examples} unavailable={data.guide.unavailable} onOpen={openExample} onAction={guideAction} />
              )}
              {side === "history" && (
                <HistoryList
                  entries={history}
                  onOpen={(e, inNewTab) => {
                    if (inNewTab) addTab(newTab(e.query, e.variables));
                    else updateTab(tab.id, { query: e.query, variables: e.variables });
                  }}
                  onClear={async () => {
                    const { ok } = await showConfirm(
                      "Clear the history",
                      `The ${history.length} runs kept for this endpoint in this browser are forgotten. The tabs stay as they are.`,
                      { confirmLabel: "Clear", danger: true },
                    );
                    if (ok) setHistory([]);
                  }}
                />
              )}
            </div>
          </div>
        )}
        {sideOpen && (
          <div
            className="gx-split"
            onPointerDown={startDrag}
            onPointerMove={moveDrag}
            onPointerUp={() => (drag.current = null)}
            onPointerCancel={() => (drag.current = null)}
            title="Drag to resize"
          />
        )}

        <div className="gx-center">
          <div className="gx-pane-head">
            <span className="gx-pane-title">Query</span>
            {firstIssue ? (
              <button
                className="gx-status bad"
                title={analysis.issues.map((i) => i.message).join("\n")}
                onClick={() => editor.current?.goToLine(lineAndColumn(tab.query, firstIssue.offset).line)}
              >
                <IconAlertTriangle size={13} stroke={1.8} />
                <span>
                  {firstIssue.message}
                  {analysis.issues.length > 1 ? ` (+${analysis.issues.length - 1})` : ""}
                </span>
              </button>
            ) : doc ? (
              <span className="gx-status ok">
                <IconCheck size={13} stroke={2} /> matches the schema
              </span>
            ) : (
              <span className="gx-status muted">Tick fields under Build, or type: Ctrl+Space completes</span>
            )}
            <span className="gx-spacer" />
            <CopyText text={tab.query} title="Copy the query" small />
            <button className="icon-button small" title="Lay out the query and the variables (Shift+Alt+F)" onClick={prettify} disabled={!doc}>
              <IconWand size={15} stroke={1.8} />
            </button>
          </div>
          <div className="api-code gx-fill">
            <CodeEditor
              value={tab.query}
              onChange={setQuery}
              language="graphql"
              issues={issues}
              onSave={() => run()}
              onRun={() => run()}
              onFormat={prettify}
              onCursor={setCursor}
              complete={(text, offset) => complete(text, offset, schema)}
              apiRef={editor}
            />
          </div>
          <div className="gx-pane-head">
            <span className="gx-pane-title">Variables</span>
            {varProblems?.invalidJson && <span className="gx-status bad">not valid JSON</span>}
            {varProblems && varProblems.missing.length > 0 && (
              <>
                <span className="gx-status bad">needs {varProblems.missing.map((m) => "$" + m).join(", ")}</span>
                <button
                  className="link-button"
                  onClick={() => {
                    let text = tab.variables;
                    for (const name of varProblems.missing) {
                      const v = activeOp!.variables.find((x) => x.name === name)!;
                      text = setVariable(text, name, exampleValue(schema, printTypeRef(v.type), sampleIdFor(schema, activeOp!, name, samples)));
                    }
                    setVariables(text);
                  }}
                >
                  Add them
                </button>
              </>
            )}
            {varProblems && varProblems.unknown.length > 0 && (
              <>
                <span className="gx-status muted">not used: {varProblems.unknown.join(", ")}</span>
                <button
                  className="link-button"
                  onClick={() => {
                    const values = { ...variablesJson };
                    for (const k of varProblems.unknown) delete values[k];
                    setVariables(Object.keys(values).length > 0 ? JSON.stringify(values, null, 2) : "");
                  }}
                >
                  Remove
                </button>
              </>
            )}
          </div>
          <div className="api-code small gx-vars">
            <CodeEditor value={tab.variables} onChange={setVariables} language="json" issues={varLint} onSave={() => run()} onRun={() => run()} onFormat={prettify} />
          </div>
        </div>

        <div className="gx-right">
          <div className="gx-pane-head">
            <div className="module-switch compact">
              <button className={right === "result" ? "active" : ""} onClick={() => setRight("result")}>
                <IconBraces size={13} stroke={1.8} /> Result
              </button>
              <button className={right === "code" ? "active" : ""} onClick={() => setRight("code")}>
                <IconCode size={13} stroke={1.8} /> Code
              </button>
            </div>
            {right === "result" && ok && (
              <>
                <div className="module-switch compact">
                  <button className={resultView === "json" ? "active" : ""} onClick={() => setResultView("json")}>
                    JSON
                  </button>
                  <button className={resultView === "table" ? "active" : ""} onClick={() => setResultView("table")}>
                    <IconTable size={13} stroke={1.8} /> Table
                  </button>
                </div>
                <span className={"gx-meta" + (errorCount > 0 ? " bad" : "")}>
                  {/* the time the server took to answer, and in brackets the whole round trip as the browser saw it */}
                  {errorCount > 0 ? `${errorCount} error${errorCount > 1 ? "s" : ""}` : "OK"} ·{" "}
                  {serverMs(ok) !== null ? `${serverMs(ok)!.toFixed(1)} ms (total ${Math.round(ok.ms)} ms)` : `${Math.round(ok.ms)} ms`} · {sizeText(ok.bytes)}
                </span>
                <span className="gx-spacer" />
                <CopyText text={() => JSON.stringify(ok.result, null, 2)} title="Copy the result" small />
              </>
            )}
          </div>
          {right === "code" ? (
            <CodeView
              schema={schema}
              doc={doc}
              op={activeOp}
              variables={tab.variables}
              endpoint={endpoint}
              apiKey={source.apiKey}
              audience={source.audience}
              language={codeLanguage}
              scope={codeScope}
              part={codePart}
              onLanguage={setCodeLanguage}
              onScope={setCodeScope}
              onPart={setCodePart}
              problem={
                !tab.query.trim() ? "Write or build a query first; its code shows here." : !doc || !activeOp ? "The query has a mistake; the code shows once it parses." : null
              }
            />
          ) : !state ? (
            <div className="gx-empty muted">
              {running === tab.id ? "Running…" : "Run the query (Ctrl+Enter) and the answer shows here."}
              {data.warnings.length > 0 && (
                <div className="gx-warnings">
                  {data.warnings.slice(0, 4).map((w, i) => (
                    <div key={i}>
                      <IconAlertTriangle size={12} stroke={1.8} /> {w}
                    </div>
                  ))}
                </div>
              )}
            </div>
          ) : isError(state) ? (
            <div className="gx-errors">
              <div className="gx-error">
                <IconAlertTriangle size={13} stroke={1.8} /> {state.error}
              </div>
            </div>
          ) : (
            <ResultView
              state={state}
              view={resultView}
              schema={schema}
              doc={tryParse(state.query).doc}
              op={(() => {
                const d = tryParse(state.query).doc;
                return d ? (operations(d).find((o) => o.name === state.operationName) ?? operations(d)[0] ?? null) : null;
              })()}
              onOpenNode={openNode}
              onGoToLine={(line) => editor.current?.goToLine(line)}
            />
          )}
        </div>
      </div>
    </div>
  );
}

function firstOperationName(query: string): string | null {
  const parsed = tryParse(query);
  if (!parsed.doc) return null;
  const all = operations(parsed.doc);
  return all.length > 1 ? (all[0].name ?? null) : null;
}

/**
 * A stored node's id for a variable that is used as the id of a node to fetch, or in the ids of a list - the
 * node's type is told by where the variable is used. Never for an update or a delete.
 */
function sampleIdFor(schema: Schema, op: OperationNode, name: string, samples: Record<string, { id: string; name: string | null }>): string | null {
  let found: string | null = null;
  const visit = (set: SelectionSetNode, type: string | null) => {
    for (const s of set.selections) {
      if (found) return;
      if (s.kind === "Field") {
        const field = type ? schema.field(type, s.name) : undefined;
        for (const a of s.arguments) {
          if (
            a.value.kind === "Variable" &&
            a.value.name === name &&
            field &&
            ((field.kind === "single" && a.name === "id") || ((field.kind === "list" || field.kind === "view") && a.name === "ids"))
          ) {
            const items = field.kind === "single" ? namedType(field.type) : namedType(schema.field(namedType(field.type), "items")?.type ?? "");
            found = samples[items]?.id ?? null;
          }
        }
        if (s.selectionSet) visit(s.selectionSet, field ? namedType(field.type) : null);
      } else if (s.kind === "InlineFragment") visit(s.selectionSet, s.typeCondition ?? type);
    }
  };
  visit(op.selectionSet, op.operation === "mutation" ? schema.mutationType : schema.queryType);
  return found;
}

/** A value of the given type for the variables: an id from where the variable is used, the first value of an enum… */
function exampleValue(schema: Schema, typeRef: string, sampleId: string | null): unknown {
  const inner = stripNonNull(typeRef);
  if (isList(inner)) return sampleId && namedType(typeRef) === "ID" ? [sampleId] : [];
  const named = namedType(typeRef);
  const type = schema.type(named);
  if (type?.kind === "ENUM") return type.enumValues?.[0] ?? "";
  if (type?.kind === "INPUT_OBJECT") return {};
  switch (named) {
    case "Int":
    case "Long":
    case "Float":
    case "Decimal":
      return 0;
    case "Boolean":
      return true;
    case "ID":
      return sampleId ?? "";
    default:
      return "";
  }
}

/** A query for one node with all its fields, and its relations as id and name, so it can be walked on from. */
function nodeQuery(schema: Schema, typeName: string): string | null {
  const single = schema.singleFieldFor(typeName);
  if (!single) return null;
  const target = namedType(single.type);
  const fieldText = (f: FieldInfo): string | null => {
    if (f.name.startsWith("__") || f.args.some(isRequired)) return null;
    const named = namedType(f.type);
    if (!schema.isComposite(named)) return f.name;
    const sub = schema.fields(named);
    const top = f.args.some((a) => a.name === "top") ? "(top: 10)" : "";
    if (sub.some((x) => x.name === "id") && sub.some((x) => x.name === "displayName")) return `${f.name}${top} { __typename id displayName }`;
    const leaves = sub.filter((x) => !schema.isComposite(namedType(x.type)) && !x.args.some(isRequired)).map((x) => x.name);
    return leaves.length > 0 ? `${f.name}${top} { ${leaves.join(" ")} }` : null;
  };
  const own = schema
    .fields(target)
    .map(fieldText)
    .filter((x): x is string => !!x);
  const extra =
    typeName !== target
      ? schema
          .fields(typeName)
          .filter((f) => !schema.field(target, f.name))
          .map(fieldText)
          .filter((x): x is string => !!x)
      : [];
  const body = ["__typename", ...own, ...(extra.length > 0 ? [`... on ${typeName} { ${extra.join(" ")} }`] : [])].join(" ");
  return print(parse(`query Open${typeName}($id: ID!) { ${single.name}(id: $id) { ${body} } }`));
}
