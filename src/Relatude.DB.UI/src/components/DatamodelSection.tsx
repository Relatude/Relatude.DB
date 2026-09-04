import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  IconAlertCircle,
  IconAlertTriangle,
  IconArrowsExchange,
  IconBraces,
  IconBrandCSharp,
  IconCheck,
  IconChevronDown,
  IconCircleCheck,
  IconCube,
  IconDatabaseImport,
  IconDeviceFloppy,
  IconDots,
  IconHistory,
  IconInfoCircle,
  IconLayoutGrid,
  IconList,
  IconPlus,
  IconRefreshAlert,
  IconRocket,
  IconSearch,
  IconSitemap,
  IconStack2,
  IconTable,
  IconTrash,
  IconX,
} from "@tabler/icons-react";
import { runWithProgress, showConfirm, showError, showInfo } from "../dialogs";
import { subscribeResync } from "../server/channel";
import type { DatabaseInfo } from "../server/serverInfo";
import {
  activateDraft,
  cloneModel,
  deleteHistory,
  diffModels,
  discardDraft,
  exportModel,
  fetchDatamodel,
  fetchSchema,
  loadHistory,
  newGuid,
  saveDraft,
  searchModel,
  sourceColors,
  validateDraft,
  type DatamodelPage,
  type FieldDef,
  type HistoryEntry,
  type Issue,
  type ModelJson,
  type NodeTypeJson,
  type RelationJson,
  type Schema,
  type SourceInfo,
  type SourceJson,
  type Validation,
} from "../server/datamodel";
import { formatTime } from "../format";
import { KindIcon, PropertyIcon, RelationIcon, SourceDot, SourceIcon } from "./DatamodelIcons";
import { PropertyEditor, RelationEditor, SourceEditor, TypeEditor, type EditorContext, type Selection } from "./DatamodelEditors";
import { HistoryView, ListView, MatrixView, SourcesView, TreeView } from "./DatamodelViews";
import { DatamodelDiagram } from "./DatamodelDiagram";
import "../datamodel.css";

type ViewId = "list" | "tree" | "diagram" | "matrix" | "sources" | "history";

const views: { id: ViewId; label: string; icon: typeof IconList }[] = [
  { id: "list", label: "List", icon: IconList },
  { id: "tree", label: "Inheritance", icon: IconSitemap },
  { id: "diagram", label: "Diagram", icon: IconLayoutGrid },
  { id: "matrix", label: "Matrix", icon: IconTable },
  { id: "sources", label: "Sources", icon: IconStack2 },
  { id: "history", label: "History", icon: IconHistory },
];

function readSet(key: string): Set<string> {
  try {
    const raw = localStorage.getItem(key);
    return new Set(raw ? (JSON.parse(raw) as string[]) : []);
  } catch {
    return new Set();
  }
}
function writeSet(key: string, set: Set<string>) {
  try {
    localStorage.setItem(key, JSON.stringify([...set]));
  } catch {
    // not remembered, then
  }
}

/** The values a new object starts with: every default the schema knows that is not null. */
function defaultsOf(fields: FieldDef[]): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const f of fields) if (f.default !== null && f.default !== undefined) out[f.path] = f.default;
  return out;
}

function download(fileName: string, content: string, contentType: string) {
  const url = URL.createObjectURL(new Blob([content], { type: contentType }));
  const a = document.createElement("a");
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/**
 * The data model of one database, and the editor for changing it.
 *
 * Nothing edited here touches the database directly. The page works on a draft - a copy of the
 * active model, or the draft saved earlier - and only "Activate" writes it back into the model
 * sources it came from (JSON files, C# files, or a compiled project's source folder), after the
 * server has validated it, compiled it, and shown what it is about to write. A model that has to be
 * compiled into the application is written but not active until the application is rebuilt; the
 * page says so until then. Every model that has been active is kept in a history that can be
 * loaded back as a draft.
 *
 * Color is the source a thing came from, shape is what kind of thing it is (see DatamodelIcons).
 * Sources can be switched off in the toolbar; a type of a switched off source that something
 * visible inherits from, relates to or refers to stays visible, grayed out.
 */
export function DatamodelSection({ db }: { db: DatabaseInfo }) {
  const [page, setPage] = useState<DatamodelPage | null>(null);
  const [schema, setSchema] = useState<Schema | null>(null);
  const [model, setModel] = useState<ModelJson | null>(null);
  const [baseline, setBaseline] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [view, setView] = useState<ViewId>(() => (localStorage.getItem("dmView") as ViewId | null) ?? "list");
  const [query, setQuery] = useState("");
  const [hitsOpen, setHitsOpen] = useState(false);
  const [hiddenSources, setHiddenSources] = useState<Set<string>>(() => readSet("dmHiddenSources:" + db.id));
  const [selection, setSelection] = useState<Selection | null>(null);
  const [validation, setValidation] = useState<Validation | null>(null);
  const [issuesOpen, setIssuesOpen] = useState(false);
  const [planOpen, setPlanOpen] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [menuOpen, setMenuOpen] = useState(false);
  const searchRef = useRef<HTMLInputElement>(null);

  const load = useCallback(async () => {
    try {
      const [p, s] = await Promise.all([fetchDatamodel(db.id), fetchSchema()]);
      setPage(p);
      setSchema(s);
      const source = p.draft?.model ?? p.active?.model ?? null;
      const working = source ? cloneModel(source) : null;
      setModel(working);
      setBaseline(working ? JSON.stringify(working) : "");
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, [db.id]);

  useEffect(() => {
    load();
    return subscribeResync(load);
  }, [load]);
  useEffect(() => localStorage.setItem("dmView", view), [view]);
  useEffect(() => writeSet("dmHiddenSources:" + db.id, hiddenSources), [hiddenSources, db.id]);

  const dirty = model !== null && JSON.stringify(model) !== baseline;
  const hasDraft = dirty || page?.draft != null;
  const baseTypeId = page?.baseTypeId ?? "";
  const codeSourceId = page?.codeSourceId ?? "";

  // sources: what the server knows, plus what the draft added and the server has not seen yet
  const sourceInfos: SourceInfo[] = useMemo(() => {
    if (!model) return page?.sources ?? [];
    const known = page?.sources ?? [];
    return model.Sources.map((s) => {
      const info = known.find((k) => k.id === s.Id);
      const compiled = s.Type === "AssemblyNameReference" || s.Type === "TypeNameReference";
      if (info) {
        // the draft may have changed what decides writability (the kind, or a compiled source's code
        // folder); the server's verdict only holds while the definition it judged is the one shown
        const sameDefinition = info.type === s.Type && (info.sourceCodePath ?? "") === (s.SourceCodePath ?? "");
        if (sameDefinition) return { ...info, name: s.Name || info.name, enabled: s.Enabled };
        const writable = s.Type !== "Code" && (!compiled || !!s.SourceCodePath);
        return {
          ...info,
          name: s.Name || info.name,
          enabled: s.Enabled,
          type: s.Type,
          sourceCodePath: s.SourceCodePath ?? null,
          writable,
          requiresRebuild: compiled,
          readOnlyReason: writable ? null : compiled ? "Set a source code folder on the source to write into it." : info.readOnlyReason,
          resolvedPath: null, // the server resolves it at the next validation
          pathExists: null,
        };
      }
      return {
        id: s.Id,
        name: s.Name || "New source",
        type: s.Type,
        enabled: s.Enabled,
        namespace: s.Namespace ?? null,
        filepath: s.Filepath ?? null,
        reference: s.Reference ?? null,
        fileIO: s.FileIO ?? null,
        sourceCodePath: s.SourceCodePath ?? null,
        autoDeduceRelations: s.AutoDeduceRelations,
        isCode: s.Type === "Code",
        inSettings: false,
        writable: s.Type !== "Code" && (!compiled || !!s.SourceCodePath),
        readOnlyReason: s.Type === "Code" ? "Types registered from application code." : compiled && !s.SourceCodePath ? "Set a source code folder on the source to write into it." : null,
        requiresRebuild: compiled,
        resolvedPath: null,
        pathExists: null,
        typeCount: 0,
        relationCount: 0,
        files: [],
      };
    });
  }, [model, page]);

  const colors = useMemo(() => sourceColors(model?.Sources ?? [], codeSourceId), [model, codeSourceId]);

  // what is shown: types of switched on sources, plus what they point at in switched off ones
  const { visibleTypes, ghostTypes } = useMemo(() => {
    const visible = new Set<string>();
    const ghost = new Set<string>();
    if (!model) return { visibleTypes: visible, ghostTypes: ghost };
    for (const t of Object.values(model.NodeTypes)) if (!hiddenSources.has(t.DatamodelSourceId)) visible.add(t.Id);
    const pull = (id: string | undefined | null) => {
      if (id && model.NodeTypes[id] && !visible.has(id)) ghost.add(id);
    };
    for (const id of visible) {
      const t = model.NodeTypes[id];
      for (const p of t.Parents ?? []) pull(p);
      for (const p of Object.values(t.Properties)) {
        for (const x of p.NodeTypes ?? []) pull(x);
        for (const x of p.InnerNodeTypes ?? []) pull(x);
        pull(p.NodeTypeOfRelated);
      }
    }
    for (const r of Object.values(model.Relations)) {
      const touches = [...r.SourceTypes, ...r.TargetTypes].some((id) => visible.has(id));
      if (touches) for (const id of [...r.SourceTypes, ...r.TargetTypes]) pull(id);
    }
    return { visibleTypes: visible, ghostTypes: ghost };
  }, [model, hiddenSources]);

  const diff = useMemo(() => (model && page?.active ? diffModels(page.active.model, model, baseTypeId) : null), [model, page, baseTypeId]);

  const update = useCallback((mutate: (m: ModelJson) => void) => {
    setModel((prev) => {
      if (!prev) return prev;
      const next = cloneModel(prev);
      mutate(next);
      return next;
    });
  }, []);

  const writableSource = useCallback(
    (sourceId: string) => {
      const info = sourceInfos.find((s) => s.id === sourceId);
      return !!info && info.writable && info.enabled;
    },
    [sourceInfos],
  );
  const readOnlyReason = useCallback(
    (sourceId: string) => {
      const info = sourceInfos.find((s) => s.id === sourceId);
      if (!info) return "The source is not in the model.";
      if (!info.enabled) return "The source is turned off.";
      return info.readOnlyReason;
    },
    [sourceInfos],
  );

  const ctx: EditorContext | null = useMemo(
    () =>
      model && schema
        ? { model, schema, baseTypeId, codeSourceId, sources: sourceInfos, colors, typeCounts: page?.typeCounts ?? {}, writableSource, readOnlyReason, update, select: setSelection }
        : null,
    [model, schema, baseTypeId, codeSourceId, sourceInfos, colors, page, writableSource, readOnlyReason, update],
  );

  // a selection whose target has gone (deleted, or another draft loaded) is dropped
  useEffect(() => {
    if (!model || !selection) return;
    const alive =
      selection.kind === "type" ? !!model.NodeTypes[selection.id]
      : selection.kind === "property" ? !!model.NodeTypes[selection.typeId]?.Properties[selection.id]
      : selection.kind === "relation" ? !!model.Relations[selection.id]
      : model.Sources.some((s) => s.Id === selection.id);
    if (!alive) setSelection(null);
  }, [model, selection]);

  // ---- actions ----

  async function run<T>(what: string, action: () => Promise<T>): Promise<T | undefined> {
    setBusy(what);
    try {
      return await action();
    } catch (e) {
      await showError("Could not " + what.toLowerCase(), e instanceof Error ? e.message : String(e));
      return undefined;
    } finally {
      setBusy(null);
    }
  }

  async function save() {
    if (!model) return;
    const info = await run("Save the draft", () => saveDraft(db.id, model));
    if (!info) return;
    setBaseline(JSON.stringify(model));
    setPage((p) => (p ? { ...p, draft: { ...info, model } } : p));
  }

  async function validate() {
    if (!model) return;
    const v = await run("Validate the draft", () => validateDraft(db.id, model, true));
    if (!v) return;
    setValidation(v);
    setIssuesOpen(true);
  }

  async function activate() {
    if (!model) return;
    const v = await runWithProgress("Checking the draft", () => validateDraft(db.id, model, true));
    if (!v) return;
    setValidation(v);
    setIssuesOpen(true);
    const errors = v.issues.filter((i) => i.severity === "error").length;
    const warnings = v.issues.filter((i) => i.severity === "warning").length;
    if (v.hasErrors) {
      await showError("The draft has errors", `${errors} error${errors === 1 ? "" : "s"} must be fixed before the model can be activated. They are listed below the model.`);
      return;
    }
    const files = v.plan?.files.filter((f) => f.changed) ?? [];
    const writes = files.filter((f) => f.action === "write").length;
    const deletes = files.filter((f) => f.action === "delete").length;
    const lines: string[] = [];
    if (writes > 0) lines.push(`${writes} file${writes === 1 ? "" : "s"} will be written.`);
    if (deletes > 0) lines.push(`${deletes} file${deletes === 1 ? "" : "s"} will be deleted.`);
    if (v.plan?.settingsChange) lines.push("The source list in the settings file changes.");
    if (v.requiresRebuild) lines.push("Some of the changes go into the application's own source code: they take effect after the application is rebuilt and restarted, and the draft is kept until then.");
    else if (page?.open) lines.push("The database is reopened with the new model, which rebuilds its state and indexes when index settings changed.");
    else lines.push("The database is closed; it uses the new model when it opens.");
    if (warnings > 0) lines.push(`${warnings} warning${warnings === 1 ? "" : "s"} (listed below the model) will be accepted.`);
    if (files.length === 0 && !v.plan?.settingsChange) lines.push("Nothing differs from the active model; activating only clears the draft.");
    const confirmed = await showConfirm("Activate this model?", lines.join(" "), { confirmLabel: v.requiresRebuild ? "Write to source code" : "Activate", danger: warnings > 0 || deletes > 0 });
    if (!confirmed.ok) return;
    const result = await runWithProgress("Activating the model", () => activateDraft(db.id, model, true));
    if (!result) return;
    setValidation(result.validation);
    if (result.validation.hasErrors || (!result.activated && !result.awaitingRebuild)) {
      await showError("The model was not activated", result.message ?? "See the issues below the model.");
    } else {
      await showInfo(result.awaitingRebuild ? "Written to source code" : "Model activated", result.message ?? "", [...result.filesWritten.map((f) => "written: " + f), ...result.filesDeleted.map((f) => "deleted: " + f)]);
      // the plan it showed has been carried out; keeping it would describe writes already made
      if (result.activated) setValidation(null);
    }
    await load();
  }

  async function discard() {
    const confirmed = await showConfirm("Discard the draft?", "The draft is deleted and the page goes back to the active model. The active model itself is not touched.", { confirmLabel: "Discard", danger: true });
    if (!confirmed.ok) return;
    if (page?.draft) await run("Discard the draft", () => discardDraft(db.id));
    setValidation(null);
    await load();
  }

  async function loadFromHistory(entry: HistoryEntry) {
    if (hasDraft) {
      const confirmed = await showConfirm("Replace the current draft?", "The draft you have now is replaced by the model from " + formatTime(entry.savedUtc) + ". Nothing is activated until you activate it.", { confirmLabel: "Replace draft", danger: true });
      if (!confirmed.ok) return;
    }
    const loaded = await run("Load the model", () => loadHistory(db.id, entry.key));
    if (!loaded) return;
    await run("Save the draft", () => saveDraft(db.id, loaded.model, "Loaded from history " + formatTime(entry.savedUtc)));
    await load();
    setView("list");
  }

  async function removeHistory(entry: HistoryEntry) {
    const confirmed = await showConfirm("Delete this history entry?", "The model from " + formatTime(entry.savedUtc) + " is removed from the history.", { confirmLabel: "Delete", danger: true });
    if (!confirmed.ok) return;
    await run("Delete the history entry", () => deleteHistory(db.id, entry.key));
    await load();
  }

  async function doExport(format: "csharp" | "json") {
    setMenuOpen(false);
    const result = await run("Export the model", () => exportModel(db.id, model, format));
    if (result) download(result.fileName, result.content, result.contentType);
  }

  function defaultSource(): SourceJson | null {
    if (!model) return null;
    const usable = model.Sources.filter((s) => s.Type !== "Code" && s.Enabled && writableSource(s.Id));
    return usable.find((s) => s.Type === "JsonFile" || s.Type === "CSharpCodeFile") ?? usable[0] ?? null;
  }
  function uniqueName(base: string, taken: (name: string) => boolean): string {
    let name = base;
    let n = 2;
    while (taken(name)) name = base + n++;
    return name;
  }
  function addType() {
    setMenuOpen(false);
    if (!model || !schema) return;
    const source = defaultSource();
    if (!source) {
      showError("No writable source", "Every source is read only or turned off. Add a JSON or C# source, or set a source code folder on a compiled one, before adding types.");
      return;
    }
    const id = newGuid();
    const siblings = Object.values(model.NodeTypes).filter((t) => t.DatamodelSourceId === source.Id);
    const ns = source.Namespace || siblings[0]?.Namespace || "Models";
    update((m) => {
      const t: NodeTypeJson = {
        ...defaultsOf(schema.nodeType),
        Id: id,
        CodeName: uniqueName("NewType", (n) => Object.values(m.NodeTypes).some((x) => x.CodeName.toLowerCase() === n.toLowerCase() && (x.Namespace ?? "") === ns)),
        Namespace: ns,
        ModelType: "Class",
        Parents: [],
        Properties: {},
        DatamodelSourceId: source.Id,
      } as NodeTypeJson;
      m.NodeTypes[id] = t;
    });
    setSelection({ kind: "type", id });
    if (view === "sources" || view === "history") setView("list");
  }
  function addRelation() {
    setMenuOpen(false);
    if (!model || !schema) return;
    const source = defaultSource();
    if (!source) {
      showError("No writable source", "Every source is read only or turned off.");
      return;
    }
    const id = newGuid();
    const selectedType = selection?.kind === "type" ? selection.id : selection?.kind === "property" ? selection.typeId : null;
    const ns = source.Namespace || (selectedType ? model.NodeTypes[selectedType]?.Namespace : null) || "Models";
    update((m) => {
      const r: RelationJson = {
        ...defaultsOf(schema.relation),
        Id: id,
        CodeName: uniqueName("NewRelation", (n) => Object.values(m.Relations).some((x) => x.CodeName.toLowerCase() === n.toLowerCase())),
        Namespace: ns,
        RelationType: "OneToMany",
        SourceTypes: selectedType ? [selectedType] : [],
        TargetTypes: [],
        DatamodelSourceId: source.Id,
      } as RelationJson;
      m.Relations[id] = r;
    });
    setSelection({ kind: "relation", id });
    if (view === "sources" || view === "history") setView("list");
  }
  function addSource() {
    setMenuOpen(false);
    if (!model) return;
    const id = newGuid();
    update((m) => {
      m.Sources.push({ Id: id, Name: uniqueName("New source", (n) => m.Sources.some((s) => (s.Name ?? "") === n)), Type: "JsonFile", Filepath: "Models/Json", Enabled: true, AutoDeduceRelations: false });
    });
    setSelection({ kind: "source", id });
    setView("sources");
  }
  async function deleteSelected() {
    if (!model || !selection) return;
    if (selection.kind === "type") {
      const t = model.NodeTypes[selection.id];
      const count = page?.typeCounts[t.Id];
      const children = Object.values(model.NodeTypes).filter((x) => (x.Parents ?? []).includes(t.Id)).length;
      const confirmed = await showConfirm(
        `Delete the type ${t.CodeName}?`,
        (count ? `${count} node${count === 1 ? "" : "s"} of this type lose their type when the draft is activated. ` : "") + (children ? `${children} type${children === 1 ? "" : "s"} inherit from it and stop doing so. ` : "") + "Relations and references that point at it are updated.",
        { confirmLabel: "Delete", danger: true },
      );
      if (!confirmed.ok) return;
      update((m) => {
        delete m.NodeTypes[t.Id];
        for (const x of Object.values(m.NodeTypes)) {
          x.Parents = (x.Parents ?? []).filter((p) => p !== t.Id);
          for (const p of Object.values(x.Properties)) {
            if (p.NodeTypes) p.NodeTypes = p.NodeTypes.filter((id) => id !== t.Id);
            if (p.InnerNodeTypes) p.InnerNodeTypes = p.InnerNodeTypes.filter((id) => id !== t.Id);
          }
        }
        for (const r of Object.values(m.Relations)) {
          r.SourceTypes = r.SourceTypes.filter((id) => id !== t.Id);
          r.TargetTypes = r.TargetTypes.filter((id) => id !== t.Id);
        }
      });
    } else if (selection.kind === "property") {
      const t = model.NodeTypes[selection.typeId];
      const p = t.Properties[selection.id];
      const confirmed = await showConfirm(`Delete ${t.CodeName}.${p.CodeName}?`, "Its values are dropped when the draft is activated.", { confirmLabel: "Delete", danger: true });
      if (!confirmed.ok) return;
      update((m) => {
        delete m.NodeTypes[t.Id].Properties[p.Id];
      });
      setSelection({ kind: "type", id: t.Id });
    } else if (selection.kind === "relation") {
      const r = model.Relations[selection.id];
      const members = Object.values(model.NodeTypes).flatMap((t) => Object.values(t.Properties).filter((p) => p.PropertyType === "Relation" && p.RelationId === r.Id));
      const confirmed = await showConfirm(`Delete the relation ${r.CodeName}?`, "Every link it holds is dropped when the draft is activated." + (members.length ? ` ${members.length} relation member${members.length === 1 ? "" : "s"} on the types are removed with it.` : ""), { confirmLabel: "Delete", danger: true });
      if (!confirmed.ok) return;
      update((m) => {
        delete m.Relations[r.Id];
        for (const t of Object.values(m.NodeTypes)) for (const p of Object.values(t.Properties)) if (p.PropertyType === "Relation" && p.RelationId === r.Id) delete t.Properties[p.Id];
      });
    } else {
      const s = model.Sources.find((x) => x.Id === selection.id);
      if (!s) return;
      const confirmed = await showConfirm(`Remove the source ${s.Name || s.Id}?`, "It leaves the settings when the draft is activated. Its files stay where they are.", { confirmLabel: "Remove", danger: true });
      if (!confirmed.ok) return;
      update((m) => {
        m.Sources = m.Sources.filter((x) => x.Id !== s.Id);
      });
    }
    setSelection(null);
  }

  function toggleSource(id: string) {
    setHiddenSources((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  function goTo(issue: Issue) {
    if (issue.propertyId && issue.nodeTypeId) setSelection({ kind: "property", id: issue.propertyId, typeId: issue.nodeTypeId });
    else if (issue.nodeTypeId) setSelection({ kind: "type", id: issue.nodeTypeId });
    else if (issue.relationId) setSelection({ kind: "relation", id: issue.relationId });
    else if (issue.sourceId) setSelection({ kind: "source", id: issue.sourceId });
    if (view === "history") setView("list");
  }

  // ---- render ----

  if (error) {
    return (
      <div className="dm">
        <div className="dm-notice error">
          <IconAlertCircle size={16} stroke={2} /> {error}
        </div>
      </div>
    );
  }
  if (!page || !schema || !ctx) return <div className="dm placeholder muted">Loading the model…</div>;
  if (!model) {
    return (
      <div className="dm">
        <div className="dm-notice error">
          <IconAlertCircle size={16} stroke={2} /> The model could not be loaded. {page.activeError}
        </div>
      </div>
    );
  }

  const hits = query.trim() ? searchModel(model, sourceInfos, query, baseTypeId) : [];
  const activeChanged = !!page.draft?.baseChecksum && !!page.active && page.draft.baseChecksum !== page.active.checksum;
  const errorCount = validation?.issues.filter((i) => i.severity === "error").length ?? 0;
  const warningCount = validation?.issues.filter((i) => i.severity === "warning").length ?? 0;

  const status = page.draft?.awaitingRebuild && !dirty
    ? { cls: "rebuild", icon: <IconRefreshAlert size={14} stroke={2} />, text: "Written, awaiting rebuild" }
    : hasDraft
      ? { cls: "draft", icon: <IconDeviceFloppy size={14} stroke={2} />, text: dirty ? "Draft · unsaved changes" : "Draft · saved " + (page.draft ? formatTime(page.draft.savedUtc) : "") }
      : { cls: "active", icon: <IconCircleCheck size={14} stroke={2} />, text: page.open ? "Active model" : "Model as configured (database closed)" };

  const viewProps = { ctx, visibleTypes, ghostTypes, query, selection, diff };
  const selectedEditor = (() => {
    if (!selection) return null;
    if (selection.kind === "type") {
      const t = model.NodeTypes[selection.id];
      return t ? <TypeEditor type={t} ctx={ctx} onDelete={deleteSelected} /> : null;
    }
    if (selection.kind === "property") {
      const t = model.NodeTypes[selection.typeId];
      const p = t?.Properties[selection.id];
      return t && p ? <PropertyEditor type={t} property={p} ctx={ctx} onDelete={deleteSelected} /> : null;
    }
    if (selection.kind === "relation") {
      const r = model.Relations[selection.id];
      return r ? <RelationEditor relation={r} ctx={ctx} onDelete={deleteSelected} /> : null;
    }
    const s = model.Sources.find((x) => x.Id === selection.id);
    return s ? <SourceEditor source={s} info={page.sources.find((x) => x.id === s.Id)} ctx={ctx} locked={page.sourcesLocked} onDelete={deleteSelected} /> : null;
  })();

  return (
    <div className="dm">
      <div className="dm-toolbar">
        <div className="dm-tabs">
          {views.map((v) => {
            const Icon = v.icon;
            return (
              <button key={v.id} className={"dm-tab" + (view === v.id ? " active" : "")} onClick={() => setView(v.id)} title={v.label}>
                <Icon size={15} stroke={1.9} />
                <span>{v.label}</span>
              </button>
            );
          })}
        </div>
        <div className="dm-search">
          <IconSearch size={15} stroke={2} />
          <input
            ref={searchRef}
            className="dm-search-input"
            placeholder="Find types, properties, relations…"
            value={query}
            onChange={(e) => {
              setQuery(e.target.value);
              setHitsOpen(true);
            }}
            onFocus={() => setHitsOpen(true)}
            onBlur={() => setTimeout(() => setHitsOpen(false), 150)}
            onKeyDown={(e) => {
              if (e.key === "Escape") setQuery("");
              if (e.key === "Enter" && hits[0]) {
                const h = hits[0];
                if (h.kind === "property") setSelection({ kind: "property", id: h.id, typeId: h.typeId! });
                else setSelection({ kind: h.kind, id: h.id } as Selection);
                setHitsOpen(false);
              }
            }}
          />
          {query && (
            <button className="icon-button" onClick={() => setQuery("")} title="Clear">
              <IconX size={13} stroke={2} />
            </button>
          )}
          {hitsOpen && hits.length > 0 && (
            <div className="dm-hits">
              {hits.slice(0, 12).map((h) => (
                <button
                  key={h.kind + h.id}
                  className="dm-hit"
                  onMouseDown={(e) => e.preventDefault()}
                  onClick={() => {
                    if (h.kind === "property") setSelection({ kind: "property", id: h.id, typeId: h.typeId! });
                    else setSelection({ kind: h.kind, id: h.id } as Selection);
                    if (h.kind === "source") setView("sources");
                    else if (view === "history" || view === "sources") setView("list");
                    setHitsOpen(false);
                  }}
                >
                  {h.kind === "type" ? <KindIcon kind={model.NodeTypes[h.id].ModelType} size={14} /> : h.kind === "property" ? <PropertyIcon propertyType={model.NodeTypes[h.typeId!].Properties[h.id].PropertyType} /> : h.kind === "relation" ? <RelationIcon kind={model.Relations[h.id].RelationType} size={14} /> : <SourceIcon type={sourceInfos.find((s) => s.id === h.id)?.type ?? "Code"} size={14} color={colors.get(h.id)} />}
                  <span className="dm-hit-label">{h.label}</span>
                  <span className="muted">{h.detail}</span>
                </button>
              ))}
            </div>
          )}
        </div>
        <div className="dm-chips" title="Click a source to show or hide its types">
          {model.Sources.map((s) => {
            const n = Object.values(model.NodeTypes).filter((t) => t.DatamodelSourceId === s.Id).length;
            const off = hiddenSources.has(s.Id);
            return (
              <button key={s.Id} className={"dm-chip-source" + (off ? " off" : "")} onClick={() => toggleSource(s.Id)} title={(s.Name || s.Id) + " · " + s.Type + (off ? " · hidden" : "")}>
                <SourceDot color={colors.get(s.Id) ?? "#888"} />
                <span>{s.Name || "?"}</span>
                <span className="dm-chip-count">{n}</span>
              </button>
            );
          })}
        </div>
        <span className="query-spacer" />
        <span className={"dm-status " + status.cls} title={page.draft?.note ?? undefined}>
          {status.icon} {status.text}
        </span>
        <button className="action-button dm-button" onClick={save} disabled={!dirty || busy !== null} title="Keep the draft on the server without activating it">
          <IconDeviceFloppy size={15} stroke={2} /> Save draft
        </button>
        <button className="action-button dm-button" onClick={validate} disabled={busy !== null} title="Check the draft, and show what activating it would write">
          <IconCheck size={15} stroke={2} /> Validate
        </button>
        <button className="action-button dm-button primary" onClick={activate} disabled={!hasDraft || busy !== null} title="Write the draft into its sources and make it the active model">
          <IconRocket size={15} stroke={2} /> Activate…
        </button>
        <div className="dm-menu-wrap">
          <button className="icon-button" onClick={() => setMenuOpen(!menuOpen)} title="More">
            <IconDots size={18} stroke={2} />
          </button>
          {menuOpen && (
            <>
              <div className="dm-menu-backdrop" onClick={() => setMenuOpen(false)} />
              <div className="dm-menu">
                <button onClick={addType}>
                  <IconCube size={15} stroke={1.9} /> New type
                </button>
                <button onClick={addRelation}>
                  <IconArrowsExchange size={15} stroke={1.9} /> New relation
                </button>
                {!page.sourcesLocked && (
                  <button onClick={addSource}>
                    <IconPlus size={15} stroke={1.9} /> New source
                  </button>
                )}
                <hr />
                <button onClick={() => doExport("csharp")}>
                  <IconBrandCSharp size={15} stroke={1.9} /> Export as C#
                </button>
                <button onClick={() => doExport("json")}>
                  <IconBraces size={15} stroke={1.9} /> Export as JSON
                </button>
                <hr />
                <button
                  onClick={() => {
                    setMenuOpen(false);
                    discard();
                  }}
                  disabled={!hasDraft}
                  className="danger"
                >
                  <IconTrash size={15} stroke={1.9} /> Discard draft
                </button>
              </div>
            </>
          )}
        </div>
      </div>

      {page.activeError && (
        <div className="dm-notice error">
          <IconAlertCircle size={16} stroke={2} /> The configured sources could not be loaded: {page.activeError}
        </div>
      )}
      {page.draftError && (
        <div className="dm-notice warn">
          <IconAlertTriangle size={16} stroke={2} /> {page.draftError}
        </div>
      )}
      {page.draft?.awaitingRebuild && !dirty && (
        <div className="dm-notice warn">
          <IconRefreshAlert size={16} stroke={2} /> This draft was written into the application's source code {page.draft.awaitingRebuildSinceUtc ? "at " + formatTime(page.draft.awaitingRebuildSinceUtc) : ""}. Rebuild and restart the application to make it the active model; the draft goes away by itself once the database opens with it.
        </div>
      )}
      {activeChanged && (
        <div className="dm-notice warn">
          <IconAlertTriangle size={16} stroke={2} /> The active model has changed since this draft was started. Activating the draft would take the model back to what the draft says; compare the two before you do.
        </div>
      )}
      {!page.open && !page.activeError && (
        <div className="dm-notice">
          <IconInfoCircle size={16} stroke={2} /> The database is closed. The model shown is what its sources say; node counts and the data impact of a change are only known while it is open.
        </div>
      )}

      <div className={"dm-body" + (selectedEditor ? " with-editor" : "")}>
        <div className="dm-main">
          {view === "list" && <ListView {...viewProps} />}
          {view === "tree" && <TreeView {...viewProps} />}
          {view === "diagram" && <DatamodelDiagram ctx={ctx} visibleTypes={visibleTypes} ghostTypes={ghostTypes} selection={selection} query={query} storeId={db.id} />}
          {view === "matrix" && <MatrixView {...viewProps} />}
          {view === "sources" && <SourcesView ctx={ctx} selection={selection} hiddenSources={hiddenSources} onToggleVisible={toggleSource} onAdd={addSource} locked={page.sourcesLocked} />}
          {view === "history" && <HistoryView history={page.history} activeChecksum={page.active?.checksum ?? null} draftBaseChecksum={page.draft?.baseChecksum ?? null} onLoad={loadFromHistory} onDelete={removeHistory} />}
        </div>
        {selectedEditor && (
          <aside className="dm-side">
            <div className="dm-side-head">
              <span className="page-kicker">{selection?.kind}</span>
              <button className="icon-button" onClick={() => setSelection(null)} title="Close">
                <IconX size={16} stroke={2} />
              </button>
            </div>
            <div className="dm-side-body">{selectedEditor}</div>
          </aside>
        )}
      </div>

      {validation && (
        <div className={"dm-issues" + (issuesOpen ? " open" : "")}>
          <button className="dm-issues-head" onClick={() => setIssuesOpen(!issuesOpen)}>
            {issuesOpen ? <IconChevronDown size={14} stroke={2} /> : <IconChevronDown size={14} stroke={2} style={{ transform: "rotate(180deg)" }} />}
            <span>Validation</span>
            {errorCount > 0 && <span className="badge danger">{errorCount} error{errorCount === 1 ? "" : "s"}</span>}
            {warningCount > 0 && <span className="badge dm-badge-warn">{warningCount} warning{warningCount === 1 ? "" : "s"}</span>}
            {errorCount === 0 && warningCount === 0 && <span className="badge dm-badge-ok">no problems</span>}
            {validation.compiled && <span className="badge dm-badge-ok">compiled</span>}
            {validation.requiresRebuild && <span className="badge dm-badge-warn">needs rebuild</span>}
            <span className="query-spacer" />
            {validation.plan && (
              <button
                className="link-button"
                onClick={(e) => {
                  e.stopPropagation();
                  setPlanOpen(!planOpen);
                  setIssuesOpen(true);
                }}
              >
                <IconDatabaseImport size={13} stroke={2} /> {validation.plan.files.filter((f) => f.changed).length} file change{validation.plan.files.filter((f) => f.changed).length === 1 ? "" : "s"}
              </button>
            )}
            <button
              className="icon-button"
              onClick={(e) => {
                e.stopPropagation();
                setValidation(null);
              }}
              title="Dismiss"
            >
              <IconX size={14} stroke={2} />
            </button>
          </button>
          {issuesOpen && (
            <div className="dm-issues-body">
              {validation.issues.length === 0 && <div className="muted dm-empty">Nothing to report: the draft is sound and compiles.</div>}
              {validation.issues.map((i, n) => (
                <button key={n} className={"dm-issue " + i.severity} onClick={() => goTo(i)} title={i.code}>
                  {i.severity === "error" ? <IconAlertCircle size={14} stroke={2} /> : i.severity === "warning" ? <IconAlertTriangle size={14} stroke={2} /> : <IconInfoCircle size={14} stroke={2} />}
                  <span>{i.message}</span>
                </button>
              ))}
              {planOpen && validation.plan && (
                <div className="dm-plan">
                  <div className="page-kicker">What activation writes</div>
                  {validation.plan.files.filter((f) => f.changed).length === 0 && <div className="muted">No file changes.</div>}
                  {validation.plan.files
                    .filter((f) => f.changed)
                    .map((f) => (
                      <div key={f.path} className={"dm-plan-file " + f.action} title={f.path}>
                        <span className="dm-plan-action">{f.action === "delete" ? "delete" : f.exists ? "rewrite" : "create"}</span>
                        <span className="dm-mono">{f.path}</span>
                        <span className="muted">
                          {f.nodeTypeIds.length} type{f.nodeTypeIds.length === 1 ? "" : "s"}
                          {f.relationIds.length ? `, ${f.relationIds.length} relation${f.relationIds.length === 1 ? "" : "s"}` : ""}
                        </span>
                      </div>
                    ))}
                  {validation.plan.settingsChange && <div className="dm-plan-file write"><span className="dm-plan-action">settings</span><span>The source list in the settings file changes.</span></div>}
                  {validation.plan.sources
                    .filter((s) => s.hasModelChanges || s.removed || s.added)
                    .map((s) => (
                      <div key={s.sourceId} className="dm-plan-source">
                        <SourceDot color={colors.get(s.sourceId) ?? "#888"} /> <strong>{s.name}</strong>
                        {s.added && <span className="badge dm-badge-new">new source</span>}
                        {s.removed && <span className="badge dm-badge-warn">removed</span>}
                        {s.requiresRebuild && s.hasModelChanges && <span className="badge dm-badge-warn">needs rebuild</span>}
                        {!s.writable && s.hasModelChanges && <span className="badge danger">read only</span>}
                        <span className="muted">
                          {" "}
                          {s.addedTypes.length ? `+${s.addedTypes.length} ` : ""}
                          {s.changedTypes.length ? `~${s.changedTypes.length} ` : ""}
                          {s.removedTypes.length ? `−${s.removedTypes.length} ` : ""}types
                          {s.addedRelations.length + s.changedRelations.length + s.removedRelations.length > 0 ? ` · ${s.addedRelations.length ? `+${s.addedRelations.length} ` : ""}${s.changedRelations.length ? `~${s.changedRelations.length} ` : ""}${s.removedRelations.length ? `−${s.removedRelations.length} ` : ""}relations` : ""}
                        </span>
                      </div>
                    ))}
                </div>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
