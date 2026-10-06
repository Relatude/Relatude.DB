import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import {
  IconAlertTriangle,
  IconApi,
  IconArrowBackUp,
  IconBraces,
  IconCode,
  IconCompass,
  IconCube3dSphere,
  IconDeviceFloppy,
  IconDownload,
  IconEye,
  IconFileCode,
  IconFlask,
  IconLayoutList,
  IconListTree,
  IconPlayerPlay,
  IconPlus,
  IconRefresh,
  IconSettings,
  IconTrash,
  IconWand,
} from "@tabler/icons-react";
import "../api.css";
import { lint } from "../code/lint";
import type { Language } from "../code/language";
import { showConfirm, showError } from "../dialogs";
import {
  deleteEndpoint,
  executeEndpoint,
  fetchEndpoint,
  fetchEndpoints,
  fetchExamples,
  fetchExplorer,
  newEndpointDefinition,
  normalizeDefinition,
  previewEndpoint,
  reloadEndpoints,
  saveEndpoint,
  type CatalogType,
  type EndpointDefinition,
  type EndpointExample,
  type EndpointExampleGroup,
  type EndpointPreview,
  type EndpointsInfo,
  type EndpointTypeDef,
  type EndpointViewDef,
} from "../server/graphql";
import type { DatabaseInfo } from "../server/serverInfo";
import { CodeEditor } from "./CodeEditor";
import { SourceTag } from "./SourceTag";
import { ShareButton } from "./ShareButton";
import { EndpointsDataDialog } from "./EndpointsDataDialog";
import { GraphQLExplorer, type ExplorerSource } from "./GraphQLExplorer";
import { CopyText } from "./CopyText";
import { Loading } from "./Loading";
import { Switch } from "./LogsSection";

// The API section: the GraphQL endpoints of the database. One tab per endpoint plus an overview; an
// endpoint is edited as a form (settings, the types and properties it exposes, views), as json, and is
// shown as the schema and code it amounts to, with a small playground to try it on the database and an
// explorer to find out what it can answer (GraphQLExplorer.tsx).

const overviewTab = "__overview";
const newTab = "__new";

type EditorView = "settings" | "types" | "views" | "code" | "try" | "explorer" | "json";

export function ApiSection({ db }: { db: DatabaseInfo }) {
  const [info, setInfo] = useState<EndpointsInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tab, setTab] = useState(overviewTab);
  const [saved, setSaved] = useState<Record<string, EndpointDefinition>>({});
  const [drafts, setDrafts] = useState<Record<string, EndpointDefinition>>({});
  // the view each endpoint's tab was left on
  const [views, setViews] = useState<Record<string, EditorView>>({});
  const [dataOpen, setDataOpen] = useState(false);

  const load = useCallback(async () => {
    try {
      setInfo(await fetchEndpoints(db.id));
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, [db.id]);
  useEffect(() => {
    load();
  }, [load]);

  async function reload() {
    try {
      setInfo(await reloadEndpoints(db.id));
      setSaved({});
    } catch (e) {
      await showError("Reload failed", e instanceof Error ? e.message : String(e));
    }
  }

  function setDraft(key: string, draft: EndpointDefinition | null) {
    setDrafts((d) => {
      const next = { ...d };
      if (draft) next[key] = draft;
      else delete next[key];
      return next;
    });
  }

  async function afterSave(key: string, id: string) {
    setDraft(key, null);
    setSaved((s) => {
      const next = { ...s };
      delete next[id];
      return next;
    });
    await load();
    setTab(id);
  }

  async function afterDelete(id: string) {
    setDraft(id, null);
    await load();
    setTab(overviewTab);
  }

  if (error && !info) return <div className="placeholder">{error}</div>;
  if (!info) return <Loading label="Loading the endpoints…" />;

  const endpoints = info.endpoints;
  const activeEndpoint = endpoints.find((e) => e.id === tab);
  const editing = tab === newTab || !!activeEndpoint;

  return (
    <div className="logs api">
      <div className="logs-tabs">
        <button className={"logs-tab" + (tab === overviewTab ? " active" : "")} onClick={() => setTab(overviewTab)}>
          <IconLayoutList size={15} stroke={1.8} /> All endpoints
        </button>
        {endpoints
          .filter((e) => e.id)
          .map((e) => (
            <button key={e.id} className={"logs-tab" + (tab === e.id ? " active" : "")} onClick={() => setTab(e.id!)} title={e.url ?? e.file}>
              <IconApi size={15} stroke={1.8} />
              {e.name || e.file}
              {drafts[e.id!] && <span className="clog-draft-dot" title="Unsaved changes" />}
              {!e.enabled && <span className="badge">off</span>}
            </button>
          ))}
        <button className={"logs-tab clog-add-tab" + (tab === newTab ? " active" : "")} onClick={() => setTab(newTab)}>
          <IconPlus size={15} stroke={1.8} /> New endpoint
          {drafts[newTab] && <span className="clog-draft-dot" title="Unsaved changes" />}
        </button>
        {/* the top right of every module that saves on this server: what is held there, and the way into SHARED */}
        {info.dataFolder && (
          <ShareButton
            className="at-end"
            count={info.dataEntries.length}
            title={`Endpoints defined or changed here are saved on THIS SERVER, ${info.dataFolder} - this installation's - over SHARED, ${info.settingsFolder}.`}
            onClick={() => setDataOpen(true)}
          />
        )}
      </div>
      {dataOpen && <EndpointsDataDialog storeId={db.id} info={info} onClose={() => setDataOpen(false)} onChanged={load} />}
      <div className="api-body">
        {!editing ? (
          <Overview
            info={info}
            drafts={drafts}
            onOpen={setTab}
            onNew={() => setTab(newTab)}
            onReload={reload}
          />
        ) : (
          <EndpointEditor
            key={tab}
            storeId={db.id}
            info={info}
            endpointId={tab === newTab ? null : tab}
            saved={tab === newTab ? undefined : saved[tab]}
            onLoaded={(id, def) => setSaved((s) => ({ ...s, [id]: def }))}
            draft={drafts[tab]}
            onDraft={(d) => setDraft(tab, d)}
            view={views[tab] ?? "settings"}
            onView={(v) => setViews((all) => ({ ...all, [tab]: v }))}
            onSaved={(id) => afterSave(tab, id)}
            onDeleted={() => afterDelete(tab)}
          />
        )}
      </div>
    </div>
  );
}

// ---- overview ----

function Overview({
  info,
  drafts,
  onOpen,
  onNew,
  onReload,
}: {
  info: EndpointsInfo;
  drafts: Record<string, EndpointDefinition>;
  onOpen: (id: string) => void;
  onNew: () => void;
  onReload: () => void;
}) {
  return (
    <div className="logs-body">
      <section className="panel">
        <h3>
          GraphQL endpoints <span className="panel-sub">{info.endpoints.length === 0 ? "none defined yet" : info.endpoints.length + " defined"}</span>
        </h3>
        <div className="logs-toolbar">
          <button className="action-button primary" onClick={onNew}>
            <IconPlus size={15} stroke={1.8} /> New endpoint
          </button>
          <span className="logs-spacer" />
          <button className="action-button" onClick={onReload} title="Read the endpoint files again, for files edited by hand">
            <IconRefresh size={15} stroke={1.8} /> Reload from disk
          </button>
        </div>
        {!info.open && <div className="logs-note">The database is {info.state.toLowerCase()}; its endpoints answer with 503 until it is open, and the schema cannot be previewed.</div>}
        {info.endpoints.length === 0 ? (
          <p className="muted api-empty">
            An endpoint is a json file - in SHARED, <code>{info.settingsFolder ?? "relatude.settings/graphql"}</code>, kept in source control, or on THIS SERVER,{" "}
            <code>{info.dataFolder ?? "overrides/graphql"}</code>, where saving here writes: the url it answers on, the node types and properties it exposes and under which
            names, views defined by a query, and whether it takes mutations. Create one here, or drop a file in either folder and reload.
          </p>
        ) : (
          <div className="log-table api-table">
            <div className="log-table-row log-table-head">
              <span>Name</span>
              <span>Url</span>
              <span>Mode</span>
              <span className="num">Types</span>
              <span className="num">Views</span>
              <span>Mutations</span>
              <span>File</span>
              <span />
            </div>
            {info.endpoints.map((e) => (
              <div key={e.id ?? e.file} className={"log-table-row" + (e.id ? " clickable" : "")} onClick={() => e.id && onOpen(e.id)}>
                <span>
                  {e.name || e.file}
                  {e.id && drafts[e.id] && <span className="clog-draft-dot" title="Unsaved changes" />}
                  {!e.enabled && !e.error && <span className="badge">off</span>}
                  {e.error && (
                    <span className="badge api-badge-error" title={e.error}>
                      <IconAlertTriangle size={11} stroke={2} /> unreadable
                    </span>
                  )}
                </span>
                <span className="mono">{e.url}</span>
                <span>{e.error ? "" : e.mode === "WholeDatamodel" ? "Whole datamodel" + (e.exactNames ? ", exact names" : "") : "Selected types"}</span>
                <span className="num">{e.error ? "" : e.typeCount}</span>
                <span className="num">{e.error ? "" : e.viewCount}</span>
                <span>{e.error ? "" : e.allowMutations ? "allowed" : "read only"}</span>
                <span className="muted mono api-file">
                  <SourceTag
                    kind={e.source}
                    small
                    title={
                      e.source === "settings"
                        ? `Defined in SHARED, ${e.file} - part of the application, on every installation. A change saved here is written to THIS SERVER, ${info.dataFolder}.`
                        : `Defined or changed on this installation: THIS SERVER, ${e.file}. Move it into SHARED with Move to shared at the top right.`
                    }
                  />{" "}
                  {e.file}
                </span>
                <span className="api-row-pages">
                  {e.id && !e.error && <PublicPageButtons pages={e} look="row" />}
                </span>
              </div>
            ))}
          </div>
        )}
      </section>
    </div>
  );
}

// ---- editor ----

const previewSettleMs = 400;

function EndpointEditor({
  storeId,
  info,
  endpointId,
  saved,
  onLoaded,
  draft,
  onDraft,
  view,
  onView: setView,
  onSaved,
  onDeleted,
}: {
  storeId: string;
  info: EndpointsInfo;
  endpointId: string | null;
  saved: EndpointDefinition | undefined;
  onLoaded: (id: string, def: EndpointDefinition) => void;
  draft: EndpointDefinition | undefined;
  onDraft: (def: EndpointDefinition | null) => void;
  view: EditorView;
  onView: (view: EditorView) => void;
  onSaved: (id: string) => void;
  onDeleted: () => void;
}) {
  const [loadError, setLoadError] = useState<string | null>(null);
  const [preview, setPreview] = useState<EndpointPreview | null>(null);
  const [previewBusy, setPreviewBusy] = useState(false);
  const [saving, setSaving] = useState(false);

  // a saved endpoint is loaded once per tab; a new one starts from the defaults
  useEffect(() => {
    if (!endpointId || saved) return;
    let dropped = false;
    fetchEndpoint(storeId, endpointId).then(
      (r) => {
        if (dropped) return;
        onLoaded(endpointId, normalizeDefinition(r.definition));
        setPreview(r.preview);
      },
      (e) => {
        if (!dropped) setLoadError(e instanceof Error ? e.message : String(e));
      },
    );
    return () => {
      dropped = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [storeId, endpointId, saved]);

  // a new endpoint is named and placed apart from the others when its tab opens, and keeps that while it is edited
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const base = useMemo(() => saved ?? (endpointId ? null : newEndpointDefinition(info)), [saved, endpointId]);
  const current = draft ?? base;
  const dirty = !!draft && (!base || JSON.stringify(draft) !== JSON.stringify(base));
  const currentJson = current ? JSON.stringify(current) : null;

  // the schema preview follows the definition as it is typed
  useEffect(() => {
    if (!currentJson || !info.open) return;
    const controller = new AbortController();
    setPreviewBusy(true);
    const timer = window.setTimeout(() => {
      previewEndpoint(storeId, JSON.parse(currentJson) as EndpointDefinition, controller.signal).then(
        (p) => {
          setPreview(p);
          setPreviewBusy(false);
        },
        () => setPreviewBusy(false),
      );
    }, previewSettleMs);
    return () => {
      controller.abort();
      window.clearTimeout(timer);
    };
  }, [storeId, currentJson, info.open]);

  if (loadError) return <div className="placeholder">{loadError}</div>;
  if (!current) return <Loading label="Loading the endpoint…" />;

  function update(patch: Partial<EndpointDefinition>) {
    onDraft({ ...current!, ...patch });
  }

  async function save() {
    if (!current) return;
    setSaving(true);
    try {
      const r = await saveEndpoint(storeId, current);
      onSaved(r.id);
    } catch (e) {
      await showError("The endpoint was not saved", e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }

  async function remove() {
    if (!endpointId) {
      onDraft(null);
      onDeleted();
      return;
    }
    const { ok } = await showConfirm(
      "Delete endpoint",
      `The endpoint "${current!.name}" and its file are deleted. Clients calling ${current!.url} get nothing back from then on. This cannot be undone.`,
      { confirmLabel: "Delete", danger: true },
    );
    if (!ok) return;
    try {
      await deleteEndpoint(storeId, endpointId);
      onDeleted();
    } catch (e) {
      await showError("The endpoint was not deleted", e instanceof Error ? e.message : String(e));
    }
  }

  // the url serves the endpoint as it is saved, so a new one has nothing there yet
  const pages = saved ? publicPagesOf(saved) : null;
  const errors = preview?.issues.filter((i) => i.isError) ?? [];
  const warnings = [...(preview?.issues.filter((i) => !i.isError).map((i) => i.message) ?? []), ...(preview?.warnings ?? [])];
  const status = errors.length > 0 ? errors[0].message + (errors.length > 1 ? ` (+${errors.length - 1} more)` : "") : dirty ? "Unsaved changes" : endpointId ? "Saved" : "New endpoint";

  return (
    <div className="clog-editor api-editor">
      <div className="clog-editor-bar">
        <div className="module-switch compact">
          <button className={view === "settings" ? "active" : ""} onClick={() => setView("settings")}>
            <IconSettings size={14} stroke={1.8} /> Settings
          </button>
          <button className={view === "types" ? "active" : ""} onClick={() => setView("types")}>
            <IconListTree size={14} stroke={1.8} /> Types
          </button>
          <button className={view === "views" ? "active" : ""} onClick={() => setView("views")}>
            <IconEye size={14} stroke={1.8} /> Views{current.views.length > 0 && <span className="badge">{current.views.length}</span>}
          </button>
          <button className={view === "code" ? "active" : ""} onClick={() => setView("code")}>
            <IconCode size={14} stroke={1.8} /> Code
          </button>
          <button className={view === "try" ? "active" : ""} onClick={() => setView("try")}>
            <IconFlask size={14} stroke={1.8} /> Try it
          </button>
          <button className={view === "explorer" ? "active" : ""} onClick={() => setView("explorer")} title="Build, run and read about queries, with a guide of runnable examples">
            <IconCompass size={14} stroke={1.8} /> Explorer
          </button>
          <button className={view === "json" ? "active" : ""} onClick={() => setView("json")}>
            <IconBraces size={14} stroke={1.8} /> Json
          </button>
        </div>
        <span className={"clog-editor-status" + (errors.length > 0 ? " bad" : "")} title={errors.map((e) => e.message).join("\n")}>
          {previewBusy && <IconRefresh size={13} stroke={1.8} className="spinning" />}
          {status}
        </span>
        <PublicPageButtons pages={pages} look="bar" unsaved={dirty} />
        <Switch label="Enabled" checked={current.enabled} onChange={(v) => update({ enabled: v })} />
        <button className="action-button" onClick={() => onDraft(null)} disabled={!dirty}>
          <IconArrowBackUp size={15} stroke={1.8} /> Revert
        </button>
        <button className="action-button primary" onClick={save} disabled={saving || (!dirty && !!endpointId) || errors.length > 0}>
          <IconDeviceFloppy size={15} stroke={1.8} /> {saving ? "Saving…" : "Save"}
        </button>
        <button className="icon-button danger" onClick={remove} title={endpointId ? "Delete the endpoint" : "Discard"}>
          <IconTrash size={16} stroke={1.8} />
        </button>
      </div>
      {warnings.length > 0 && view !== "code" && view !== "try" && view !== "explorer" && (
        <div className="logs-note api-warnings">
          {warnings.slice(0, 6).map((w, i) => (
            <div key={i}>
              <IconAlertTriangle size={13} stroke={1.8} /> {w}
            </div>
          ))}
          {warnings.length > 6 && <div className="muted">… and {warnings.length - 6} more</div>}
        </div>
      )}
      <div className={"api-editor-body" + (view === "try" || view === "code" || view === "json" || view === "explorer" ? " fill" : "")}>
        {view === "settings" && <SettingsForm def={current} adminRoot={info.adminRoot} catalog={info.catalog} onChange={update} />}
        {view === "types" && <TypesEditor def={current} catalog={info.catalog} onChange={update} />}
        {view === "views" && <ViewsEditor def={current} onChange={update} />}
        {view === "code" && <CodeView def={current} preview={preview} busy={previewBusy} open={info.open} />}
        {view === "try" && <TryIt storeId={storeId} def={current} preview={preview} open={info.open} />}
        {view === "explorer" && <AdminExplorer storeId={storeId} endpointKey={endpointId ?? "new"} def={current} open={info.open} tools={<PublicPageButtons pages={pages} look="toolbar" unsaved={dirty} />} />}
        {view === "json" && <JsonView def={current} onChange={(d) => onDraft(d)} />}
      </div>
    </div>
  );
}

// ---- settings ----

function SettingsForm({
  def,
  adminRoot,
  catalog,
  onChange,
}: {
  def: EndpointDefinition;
  adminRoot: string;
  catalog: EndpointsInfo["catalog"];
  onChange: (patch: Partial<EndpointDefinition>) => void;
}) {
  const origin = typeof window === "undefined" ? "" : window.location.origin;
  const url = normalizeUrl(def.url);
  // the types the explorer and the facet search can open on: the exposed ones, in the order they were picked
  const byId = new Map((catalog?.types ?? []).map((t) => [t.id, t]));
  const exposed =
    def.mode === "WholeDatamodel"
      ? (catalog?.types ?? []).filter((t) => def.includeSystemTypes || !t.isSystem).map((t) => ({ id: t.id, label: t.name }))
      : def.types.flatMap((t) => {
          const model = byId.get(t.nodeTypeId);
          if (!model) return [];
          return [{ id: t.nodeTypeId, label: model.name + (t.name && t.name !== model.name ? ` (${t.name})` : "") }];
        });
  const defaultType = def.defaultNodeTypeId ?? null;
  const defaultMissing = !!defaultType && !exposed.some((t) => t.id === defaultType);
  const number = (key: "maxQueryDepth" | "maxIncludeDepth" | "defaultPageSize" | "maxPageSize", label: string, hint: string) => (
    <label className="clog-field">
      <span className="clog-field-label">{label}</span>
      <input
        className="text-input number"
        type="number"
        min={key === "maxIncludeDepth" ? 0 : 1}
        value={def[key]}
        onChange={(e) => onChange({ [key]: Number(e.target.value) } as Partial<EndpointDefinition>)}
      />
      <span className="clog-field-hint">{hint}</span>
    </label>
  );
  return (
    <section className="panel">
      <h3>
        Endpoint <span className="panel-sub">what it answers on and how</span>
      </h3>
      <div className="clog-fields">
        <label className="clog-field">
          <span className="clog-field-label">Name</span>
          <input className="text-input" value={def.name} onChange={(e) => onChange({ name: e.target.value })} placeholder="Public API" />
          <span className="clog-field-hint">Shown here and in the generated code; the file is named after it.</span>
        </label>
        <label className="clog-field">
          <span className="clog-field-label">Url</span>
          <input className="text-input mono" value={def.url} onChange={(e) => onChange({ url: e.target.value })} placeholder="/graphql" />
          <span className={"clog-field-hint" + (url && adminRoot && url.toLowerCase().startsWith(adminRoot.toLowerCase() + "/") ? " bad" : "")}>
            {url ? `POST ${origin}${url} · GET ${url}?sdl for the schema` : "A path such as /graphql or /api/public"}
          </span>
        </label>
        <label className="clog-field wide">
          <span className="clog-field-label">Description</span>
          <input className="text-input" value={def.description ?? ""} onChange={(e) => onChange({ description: e.target.value || null })} placeholder="Optional, shown in the schema's description" />
        </label>
        <label className="clog-field">
          <span className="clog-field-label">Mode</span>
          <select
            className="select"
            value={def.mode}
            onChange={(e) => {
              const mode = e.target.value as EndpointDefinition["mode"];
              onChange(mode === "WholeDatamodel" ? { mode, exactNames: true } : { mode });
            }}
          >
            <option value="Selected">Selected types and properties</option>
            <option value="WholeDatamodel">Whole datamodel, as it is at any time</option>
          </select>
          <span className="clog-field-hint">
            {def.mode === "Selected" ? "Only what is picked under Types is exposed; names can be changed there." : "Every type and property, following the datamodel as it changes."}
          </span>
        </label>
        <label className="clog-field">
          <span className="clog-field-label">Default type</span>
          <select className="select" value={defaultType ?? ""} onChange={(e) => onChange({ defaultNodeTypeId: e.target.value || null })}>
            <option value="">Automatic</option>
            {exposed.map((t) => (
              <option key={t.id} value={t.id}>
                {t.label}
              </option>
            ))}
            {defaultMissing && <option value={defaultType}>{(byId.get(defaultType)?.name ?? defaultType) + " (not exposed)"}</option>}
          </select>
          <span className={"clog-field-hint" + (defaultMissing ? " bad" : "")}>
            {defaultMissing
              ? "The endpoint does not expose this type, so the explorer and the facet search choose one themselves."
              : "The type the explorer's first query and guide, and the facet search's pivot, start on. Automatic: the first exposed type in the pivot, the one with the most to show in the explorer."}
          </span>
        </label>
        <div className="clog-field api-switches">
          <span className="clog-field-label">Names and features</span>
          <Switch label="Use names exactly as in the datamodel" checked={def.exactNames} onChange={(v) => onChange({ exactNames: v })} />
          <Switch label="Allow mutations (create, update, delete)" checked={def.allowMutations} onChange={(v) => onChange({ allowMutations: v })} />
          <Switch label="Introspection (__schema, __type)" checked={def.enableIntrospection} onChange={(v) => onChange({ enableIntrospection: v })} />
          <Switch label="GET requests with ?query=" checked={def.enableGetRequests} onChange={(v) => onChange({ enableGetRequests: v })} />
          <Switch label="Explorer page on the url, for browsers (like GraphiQL)" checked={!!def.enableExplorer} onChange={(v) => onChange({ enableExplorer: v })} />
          <Switch label="Facet search, shown as a 3D visual pivot on the url" checked={!!def.enableFacetSearch} onChange={(v) => onChange({ enableFacetSearch: v })} />
          {def.mode === "WholeDatamodel" && <Switch label="Include the system types (users, groups…)" checked={def.includeSystemTypes} onChange={(v) => onChange({ includeSystemTypes: v })} />}
          <span className="clog-field-hint">
            {def.exactNames ? "Type Article, field Title, root fields Article / Articles / CreateArticle." : "Type Article, field title, root fields article / articles / createArticle."}
          </span>
          {def.enableExplorer && !def.enableIntrospection && (
            <span className="clog-field-hint bad">The explorer reads the schema through introspection, which is off: the page will say so and stay empty.</span>
          )}
        </div>
        <label className="clog-field">
          <span className="clog-field-label">API key</span>
          <span className="api-key-row">
            <input className="text-input mono" value={def.apiKey ?? ""} onChange={(e) => onChange({ apiKey: e.target.value || null })} placeholder="None: the url is open" spellCheck={false} />
            <button className="icon-button" title="Generate a key" onClick={() => onChange({ apiKey: newKey() })}>
              <IconWand size={16} stroke={1.8} />
            </button>
          </span>
          <span className={"clog-field-hint" + (def.allowMutations && !def.apiKey ? " bad" : "")}>
            {def.apiKey ? "Clients send it in the X-Api-Key header or as a bearer token." : def.allowMutations ? "Mutations without a key: anyone who can reach the url can change data." : "Optional. Required in X-Api-Key or Authorization: Bearer when set."}
          </span>
        </label>
        {number("defaultPageSize", "Default page size", "Items per page when a query gives no pageSize.")}
        {number("maxPageSize", "Maximum page size", "The pageSize argument is capped here.")}
        {number("maxQueryDepth", "Maximum query depth", "Nesting of the selection, fragments included.")}
        {number("maxIncludeDepth", "Maximum relation depth", "How far relations can be followed in one query.")}
        {def.enableFacetSearch && (
          <label className="clog-field">
            <span className="clog-field-label">Maximum cards</span>
            <input className="text-input number" type="number" min={1} value={def.maxFacetCards ?? 200_000} onChange={(e) => onChange({ maxFacetCards: Number(e.target.value) })} />
            <span className="clog-field-hint">How many nodes the facet search's picture may hold; a larger result shows its first ones.</span>
          </label>
        )}
      </div>
    </section>
  );
}

function normalizeUrl(url: string): string | null {
  const trimmed = url.trim().split(/[?#]/)[0].replace(/^\/+|\/+$/g, "");
  if (!trimmed || /\s/.test(trimmed) || trimmed.includes("//")) return null;
  return "/" + trimmed;
}

function newKey(): string {
  const bytes = new Uint8Array(24);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

// ---- types ----

function TypesEditor({ def, catalog, onChange }: { def: EndpointDefinition; catalog: EndpointsInfo["catalog"]; onChange: (patch: Partial<EndpointDefinition>) => void }) {
  const [filter, setFilter] = useState("");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  if (!catalog) return <div className="logs-note">The datamodel is not available while the database is closed.</div>;
  const whole = def.mode === "WholeDatamodel";
  const types = catalog.types.filter((t) => !filter || t.name.toLowerCase().includes(filter.toLowerCase()) || t.fullName.toLowerCase().includes(filter.toLowerCase()));
  const byId = new Map(catalog.types.map((t) => [t.id, t]));
  const included = new Map(def.types.map((t) => [t.nodeTypeId, t]));
  const selected = selectedId ? byId.get(selectedId) : undefined;
  const selectedDef = selectedId ? included.get(selectedId) : undefined;

  function toggleType(t: CatalogType, on: boolean) {
    if (on) {
      if (included.has(t.id)) return;
      const entry: EndpointTypeDef = { nodeTypeId: t.id, properties: t.properties.filter((p) => p.supported).map((p) => ({ propertyId: p.id })) };
      onChange({ types: [...def.types, entry] });
      setSelectedId(t.id);
    } else {
      onChange({ types: def.types.filter((x) => x.nodeTypeId !== t.id) });
    }
  }

  function updateType(id: string, patch: Partial<EndpointTypeDef>) {
    onChange({ types: def.types.map((t) => (t.nodeTypeId === id ? { ...t, ...patch } : t)) });
  }

  return (
    <div className="api-types">
      <section className="panel api-type-list">
        <h3>
          Node types <span className="panel-sub">{whole ? "all exposed" : `${def.types.length} of ${catalog.types.length} exposed`}</span>
        </h3>
        <input className="text-input" placeholder="Filter types" value={filter} onChange={(e) => setFilter(e.target.value)} />
        {whole && <div className="clog-field-hint">The endpoint exposes every type. Switch the mode to Selected types to pick.</div>}
        <div className="api-type-rows">
          {types.map((t) => {
            const on = whole ? !t.isSystem || def.includeSystemTypes : included.has(t.id);
            return (
              <div key={t.id} className={"api-type-row" + (selectedId === t.id ? " selected" : "") + (on ? "" : " off")} onClick={() => setSelectedId(t.id)}>
                <input type="checkbox" checked={on} disabled={whole} onClick={(e) => e.stopPropagation()} onChange={(e) => toggleType(t, e.target.checked)} />
                <span className="api-type-name">{t.name}</span>
                <span className="muted api-type-meta">
                  {t.isInterface ? "interface" : ""}
                  {t.isSystem ? " system" : ""}
                </span>
              </div>
            );
          })}
        </div>
      </section>
      <section className="panel api-type-detail">
        {!selected ? (
          <div className="muted api-empty">Pick a type on the left to see its properties{whole ? "." : " and to choose what the endpoint exposes."}</div>
        ) : (
          <TypeDetail type={selected} def={selectedDef} whole={whole} exact={def.exactNames} onChange={(patch) => updateType(selected.id, patch)} onInclude={() => toggleType(selected, true)} />
        )}
      </section>
    </div>
  );
}

function TypeDetail({
  type,
  def,
  whole,
  exact,
  onChange,
  onInclude,
}: {
  type: CatalogType;
  def: EndpointTypeDef | undefined;
  whole: boolean;
  exact: boolean;
  onChange: (patch: Partial<EndpointTypeDef>) => void;
  onInclude: () => void;
}) {
  const typeName = def?.name?.trim() || type.name;
  const derivedField = (name: string) => (exact ? name : camel(name));
  const allProps = def?.properties === null || def?.properties === undefined;
  const propDefs = new Map((def?.properties ?? []).map((p) => [p.propertyId, p]));
  const isOn = (id: string) => (whole ? true : allProps ? true : propDefs.has(id));

  function toggleProp(id: string, on: boolean) {
    const list = allProps ? type.properties.filter((p) => p.supported).map((p) => propDefs.get(p.id) ?? { propertyId: p.id }) : [...(def?.properties ?? [])];
    const next = on ? (list.some((p) => p.propertyId === id) ? list : [...list, { propertyId: id }]) : list.filter((p) => p.propertyId !== id);
    onChange({ properties: next });
  }

  function setPropName(id: string, name: string) {
    const list = allProps ? type.properties.filter((p) => p.supported).map((p) => propDefs.get(p.id) ?? { propertyId: p.id }) : [...(def?.properties ?? [])];
    onChange({ properties: list.map((p) => (p.propertyId === id ? { ...p, name: name || null } : p)) });
  }

  return (
    <>
      <h3>
        {type.name} <span className="panel-sub">{type.fullName}</span>
      </h3>
      {!whole && !def && (
        <div className="logs-note">
          This type is not part of the endpoint.{" "}
          <button className="link-button" onClick={onInclude}>
            Include it
          </button>
        </div>
      )}
      {!whole && def && (
        <div className="clog-fields api-type-fields">
          <label className="clog-field">
            <span className="clog-field-label">GraphQL type name</span>
            <input className="text-input mono" value={def.name ?? ""} placeholder={type.name} onChange={(e) => onChange({ name: e.target.value || null })} />
          </label>
          <label className="clog-field">
            <span className="clog-field-label">Single root field</span>
            <input className="text-input mono" value={def.singleName ?? ""} placeholder={exact ? typeName : camel(typeName)} onChange={(e) => onChange({ singleName: e.target.value || null })} />
            <span className="clog-field-hint">Fetches one node by id.</span>
          </label>
          <label className="clog-field">
            <span className="clog-field-label">List root field</span>
            <input className="text-input mono" value={def.listName ?? ""} placeholder={plural(exact ? typeName : camel(typeName))} onChange={(e) => onChange({ listName: e.target.value || null })} />
            <span className="clog-field-hint">Filter, search, order and page.</span>
          </label>
          <div className="clog-field">
            <span className="clog-field-label">Mutations</span>
            <Switch label="Read only" checked={!!def.readOnly} onChange={(v) => onChange({ readOnly: v })} />
            <span className="clog-field-hint">No create, update or delete for this type even when the endpoint allows mutations.</span>
          </div>
        </div>
      )}
      <div className="log-table api-props">
        <div className="log-table-row log-table-head">
          <span />
          <span>Property</span>
          <span>Kind</span>
          <span>Field name</span>
        </div>
        {type.properties.map((p) => {
          const on = p.supported && isOn(p.id);
          const override = propDefs.get(p.id)?.name ?? "";
          return (
            <div key={p.id} className={"log-table-row" + (on ? "" : " off")}>
              <span>
                <input type="checkbox" checked={on} disabled={whole || !def || !p.supported} onChange={(e) => toggleProp(p.id, e.target.checked)} />
              </span>
              <span>
                {p.name}
                {p.inherited && (
                  <span className="muted" title="Declared on a base type">
                    {" "}
                    ↑
                  </span>
                )}
              </span>
              <span className="muted">
                {p.kind}
                {p.target ? ` → ${p.target}` : ""}
                {!p.supported ? " (not in GraphQL)" : ""}
              </span>
              <span>
                {whole || !def || !p.supported ? (
                  <span className="mono muted">{p.supported ? derivedField(p.name) : "—"}</span>
                ) : (
                  <input className="text-input mono" value={override} placeholder={derivedField(p.name)} disabled={!on} onChange={(e) => setPropName(p.id, e.target.value)} />
                )}
              </span>
            </div>
          );
        })}
      </div>
    </>
  );
}

function camel(name: string): string {
  const s = name.replace(/[^A-Za-z0-9_]/g, "");
  let run = 0;
  while (run < s.length && s[run] >= "A" && s[run] <= "Z") run++;
  if (run === 0) return s;
  const lower = run < s.length && s[run] >= "a" && s[run] <= "z" ? Math.max(1, run - 1) : run;
  return s.slice(0, lower).toLowerCase() + s.slice(lower);
}

function plural(name: string): string {
  if (/(s|x|z|ch|sh)$/.test(name)) return name + "es";
  if (/[^aeiouAEIOU]y$/.test(name)) return name.slice(0, -1) + "ies";
  return name + "s";
}

// ---- views ----

function ViewsEditor({ def, onChange }: { def: EndpointDefinition; onChange: (patch: Partial<EndpointDefinition>) => void }) {
  function updateView(index: number, patch: Partial<EndpointViewDef>) {
    onChange({ views: def.views.map((v, i) => (i === index ? { ...v, ...patch } : v)) });
  }
  return (
    <section className="panel">
      <h3>
        Views <span className="panel-sub">root fields defined by a query</span>
      </h3>
      <p className="muted api-help">
        A view is a Relatude query that starts from a type, such as <code>Article.Where(a =&gt; a.Published == true)</code>. It becomes a root field with the usual filter,
        search, orderBy and paging arguments on top of the query. Where, WhereSearch, OrderBy and Relates are allowed; paging and includes are added by the endpoint.
      </p>
      {def.views.map((v, i) => (
        <div key={i} className="api-view-row">
          <input className="text-input mono api-view-name" value={v.name} placeholder="publishedArticles" onChange={(e) => updateView(i, { name: e.target.value })} />
          <input className="text-input mono api-view-query" value={v.query} placeholder="Article.Where(a => a.Published == true)" spellCheck={false} onChange={(e) => updateView(i, { query: e.target.value })} />
          <input className="text-input api-view-description" value={v.description ?? ""} placeholder="Description" onChange={(e) => updateView(i, { description: e.target.value || null })} />
          <button className="icon-button danger" title="Remove the view" onClick={() => onChange({ views: def.views.filter((_, j) => j !== i) })}>
            <IconTrash size={16} stroke={1.8} />
          </button>
        </div>
      ))}
      <div className="logs-toolbar">
        <button className="action-button" onClick={() => onChange({ views: [...def.views, { name: "", query: "" }] })}>
          <IconPlus size={15} stroke={1.8} /> Add view
        </button>
      </div>
    </section>
  );
}

// ---- code ----

type CodeKind = "sdl" | "ts-types" | "ts-client" | "cs-types" | "cs-client";

const codeKinds: { kind: CodeKind; label: string; language: Language; extension: string; pick: (p: EndpointPreview) => string | null }[] = [
  { kind: "sdl", label: "Schema (SDL)", language: "graphql", extension: ".graphql", pick: (p) => p.sdl },
  { kind: "ts-types", label: "TypeScript types", language: "typescript", extension: ".types.ts", pick: (p) => p.types },
  { kind: "ts-client", label: "TypeScript client", language: "typescript", extension: ".client.ts", pick: (p) => p.sample },
  { kind: "cs-types", label: "C# types", language: "csharp", extension: ".Types.cs", pick: (p) => p.csharpTypes },
  { kind: "cs-client", label: "C# client", language: "csharp", extension: ".Client.cs", pick: (p) => p.csharpSample },
];

function CodeView({ def, preview, busy, open }: { def: EndpointDefinition; preview: EndpointPreview | null; busy: boolean; open: boolean }) {
  const [kind, setKind] = useState<CodeKind>("sdl");
  const current = codeKinds.find((k) => k.kind === kind) ?? codeKinds[0];
  const text = preview ? current.pick(preview) : null;
  const language = current.language;
  const fileName = fileSlug(def.name) + current.extension;
  return (
    <section className="panel api-code-panel">
      <div className="api-code-bar">
        <div className="module-switch compact">
          {codeKinds.map((k) => (
            <button key={k.kind} className={kind === k.kind ? "active" : ""} onClick={() => setKind(k.kind)}>
              {k.label}
            </button>
          ))}
        </div>
        <span className="muted api-code-meta">
          {preview ? `${preview.typeCount} types` + (preview.mutationCount > 0 ? `, ${preview.mutationCount} mutations` : ", read only") : ""}
          {busy ? " · updating…" : ""}
        </span>
        <CopyText text={text ?? ""} />
        <button className="icon-button" title="Download" disabled={!text} onClick={() => download(fileName, text ?? "")}>
          <IconDownload size={16} stroke={1.8} />
        </button>
      </div>
      {!open ? (
        <div className="logs-note">The database is closed, so the schema cannot be built.</div>
      ) : !preview ? (
        <Loading label="Building the schema…" />
      ) : (
        <>
          {preview.warnings.length > 0 && kind === "sdl" && (
            <div className="logs-note api-warnings">
              {preview.warnings.map((w, i) => (
                <div key={i}>
                  <IconAlertTriangle size={13} stroke={1.8} /> {w}
                </div>
              ))}
            </div>
          )}
          <div className="api-code">
            <CodeEditor value={text ?? ""} onChange={() => {}} language={language} issues={[]} readOnly />
          </div>
        </>
      )}
    </section>
  );
}

function download(name: string, text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: "text/plain" }));
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  a.click();
  URL.revokeObjectURL(url);
}

function fileSlug(name: string): string {
  const slug = name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
  return slug || "endpoint";
}

// ---- try it ----

const examplesSettleMs = 300;

function TryIt({ storeId, def, preview, open }: { storeId: string; def: EndpointDefinition; preview: EndpointPreview | null; open: boolean }) {
  const [groups, setGroups] = useState<EndpointExampleGroup[] | null>(null);
  const [groupLabel, setGroupLabel] = useState<string | null>(null);
  const [exampleId, setExampleId] = useState<string | null>(null);
  const [query, setQuery] = useState<string | null>(null);
  const [variables, setVariables] = useState("{}");
  const [result, setResult] = useState("");
  const [running, setRunning] = useState(false);
  const defJson = JSON.stringify(def);

  // the examples follow the definition, a little behind the typing
  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      fetchExamples(storeId, JSON.parse(defJson) as EndpointDefinition, controller.signal).then(
        (r) => setGroups(r.groups),
        () => {},
      );
    }, examplesSettleMs);
    return () => {
      controller.abort();
      window.clearTimeout(timer);
    };
  }, [storeId, defJson, open]);

  const group = groups?.find((g) => g.label === groupLabel) ?? groups?.[0] ?? null;
  const example = group?.examples.find((e) => e.id === exampleId) ?? group?.examples[0] ?? null;

  // the first example fills the editors until something has been typed
  useEffect(() => {
    if (query === null && example) {
      setQuery(example.query);
      setVariables(example.variables ?? "{}");
    }
  }, [example, query]);

  function pick(g: EndpointExampleGroup, e: EndpointExample) {
    setGroupLabel(g.label);
    setExampleId(e.id);
    setQuery(e.query);
    setVariables(e.variables ?? "{}");
    setResult("");
  }

  const text = query ?? preview?.sampleQuery ?? "{ __typename }";

  async function run() {
    let vars: unknown = undefined;
    if (variables.trim()) {
      try {
        vars = JSON.parse(variables);
      } catch (e) {
        setResult("The variables are not valid JSON: " + (e instanceof Error ? e.message : String(e)));
        return;
      }
    }
    setRunning(true);
    try {
      const r = await executeEndpoint(storeId, { definition: def, query: text, variables: vars });
      setResult(JSON.stringify(r.result, null, 2));
    } catch (e) {
      setResult(e instanceof Error ? e.message : String(e));
    } finally {
      setRunning(false);
    }
  }

  if (!open) return <div className="logs-note">The database is closed, so there is nothing to query.</div>;
  return (
    <section className="panel api-try">
      <div className="api-code-bar api-try-bar">
        <label className="api-try-pick">
          <span className="muted">Type</span>
          <select
            className="select compact"
            value={group?.label ?? ""}
            disabled={!groups}
            onChange={(e) => {
              const g = groups?.find((x) => x.label === e.target.value);
              if (g && g.examples[0]) pick(g, g.examples[0]);
            }}
          >
            {(groups ?? []).map((g) => (
              <option key={g.label} value={g.label}>
                {g.label}
              </option>
            ))}
          </select>
        </label>
        <label className="api-try-pick">
          <span className="muted">Example</span>
          <select
            className="select compact"
            value={example?.id ?? ""}
            disabled={!group}
            onChange={(e) => {
              const ex = group?.examples.find((x) => x.id === e.target.value);
              if (group && ex) pick(group, ex);
            }}
          >
            {(group?.examples ?? []).map((e) => (
              <option key={e.id} value={e.id}>
                {e.title}
              </option>
            ))}
          </select>
        </label>
        <span className="muted api-try-note" title="The query runs against the database with the definition as it is here, saved or not. Ctrl+S in an editor runs it too.">
          Runs with the definition as it is here, saved or not.
        </span>
        <button className="action-button primary" onClick={run} disabled={running}>
          <IconPlayerPlay size={15} stroke={1.8} /> {running ? "Running…" : "Run"}
        </button>
      </div>
      <div className="api-try-inputs">
        <div className="api-try-query">
          <span className="clog-field-label">Query</span>
          <div className="api-code small">
            <CodeEditor value={text} onChange={setQuery} language="graphql" issues={lint(text, "graphql")} onSave={run} />
          </div>
        </div>
        <div className="api-try-variables">
          <span className="clog-field-label">Variables</span>
          <div className="api-code small">
            <CodeEditor value={variables} onChange={setVariables} language="json" issues={lint(variables, "json")} onSave={run} />
          </div>
        </div>
      </div>
      <div className="api-try-result">
        <span className="clog-field-label">Result</span>
        <div className="api-code">
          <CodeEditor value={result} onChange={() => {}} language="json" issues={[]} readOnly />
        </div>
      </div>
    </section>
  );
}

// ---- explorer ----

/** The explorer in the admin UI: the schema and the queries go through the admin API, with the definition as it is here. */
function AdminExplorer({ storeId, endpointKey, def, open, tools }: { storeId: string; endpointKey: string; def: EndpointDefinition; open: boolean; tools?: ReactNode }) {
  const defJson = JSON.stringify(def);
  const source = useMemo<ExplorerSource>(() => {
    const definition = JSON.parse(defJson) as EndpointDefinition;
    const url = normalizeUrl(definition.url) ?? "/" + definition.url.replace(/^\/+/, "");
    return {
      storageKey: endpointKey,
      version: defJson,
      load: (signal) => fetchExplorer(storeId, definition, signal),
      execute: async (request) => (await executeEndpoint(storeId, { definition, ...request })).result,
      endpoint: window.location.origin + url,
      apiKey: !!definition.apiKey,
      introspection: definition.enableIntrospection,
      audience: "admin",
    };
  }, [storeId, endpointKey, defJson]);
  if (!open) return <div className="logs-note">The database is closed, so there is nothing to explore.</div>;
  return <GraphQLExplorer source={source} tools={tools} />;
}

// ---- the public side ----

/** What decides what the endpoint serves a browser on its url: the saved definition's switches. */
interface PublicPages {
  url: string | null;
  enabled: boolean;
  explorer: boolean;
  facets: boolean;
  introspection: boolean;
  apiKey?: boolean;
}

function publicPagesOf(def: EndpointDefinition): PublicPages {
  return {
    url: normalizeUrl(def.url),
    enabled: def.enabled,
    explorer: !!def.enableExplorer,
    facets: !!def.enableFacetSearch,
    introspection: def.enableIntrospection,
    apiKey: !!def.apiKey,
  };
}

/**
 * The endpoint's url as a browser sees it, one button per thing served there - the explorer, the facet search's visual
 * pivot and the schema as SDL - each opening it in a new tab. Only the ones that answer are shown: a switched off
 * endpoint answers nothing, the explorer reads the schema through introspection, and ?sdl asks for the API key like
 * any other request, which a browser opening it does not send. Drawn as a list row's buttons, in the editor's bar,
 * or in the explorer's toolbar (which can fill the window, bar and all); `unsaved` says the page is not what the
 * editor holds.
 */
function PublicPageButtons({ pages: e, look, unsaved }: { pages: PublicPages | null; look: "row" | "bar" | "toolbar"; unsaved?: boolean }) {
  if (!e || !e.url || !e.enabled) return null;
  const href = window.location.origin + e.url;
  const iconSize = look === "bar" ? 15 : 14;
  const className = (look === "row" ? "action-button" : "icon-button labelled" + (look === "toolbar" ? " small" : "")) + " api-page-button";
  const note = unsaved ? " It serves the saved endpoint: the changes here are not on it yet." : "";
  const button = (target: string, icon: ReactNode, label: string, title: string) => (
    <a className={className} href={target} target="_blank" rel="noreferrer" title={title + note} onClick={(ev) => ev.stopPropagation()}>
      {icon} {label}
    </a>
  );
  return (
    <>
      {e.explorer && e.introspection && button(href + "?explorer", <IconCompass size={iconSize} stroke={1.8} />, "Explore", `Open the explorer at ${href}, as anyone with a browser sees it.`)}
      {e.facets && button(href, <IconCube3dSphere size={iconSize} stroke={1.8} />, "Pivot", `Open the facet search and its visual pivot at ${href}, as anyone with a browser sees it.`)}
      {e.introspection && !e.apiKey && button(href + "?sdl", <IconFileCode size={iconSize} stroke={1.8} />, "SDL", `Open the schema as SDL at ${href}?sdl.`)}
    </>
  );
}

// ---- json ----

function JsonView({ def, onChange }: { def: EndpointDefinition; onChange: (def: EndpointDefinition) => void }) {
  const pretty = useMemo(() => JSON.stringify(def, null, 2), [def]);
  const [text, setText] = useState(pretty);
  const [problem, setProblem] = useState<string | null>(null);
  const lastApplied = useRef(pretty);
  useEffect(() => {
    // the form may have changed the definition since; follow it unless this view holds an unapplied edit
    if (pretty !== lastApplied.current && problem === null) {
      setText(pretty);
      lastApplied.current = pretty;
    }
  }, [pretty, problem]);
  function edit(value: string) {
    setText(value);
    try {
      const parsed = normalizeDefinition(JSON.parse(value) as Partial<EndpointDefinition>);
      setProblem(null);
      lastApplied.current = JSON.stringify(parsed, null, 2);
      onChange(parsed);
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    }
  }
  return (
    <section className="panel api-code-panel">
      <h3>
        Definition file <span className={"panel-sub" + (problem ? " api-bad" : "")}>{problem ?? "what is saved as json in the graphql folder"}</span>
      </h3>
      <div className="api-code">
        <CodeEditor value={text} onChange={edit} language="json" issues={lint(text, "json")} />
      </div>
    </section>
  );
}
