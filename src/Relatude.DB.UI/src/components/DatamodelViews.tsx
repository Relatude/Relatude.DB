import { useEffect, useMemo, useState } from "react";
import { IconChevronDown, IconChevronRight, IconChevronUp, IconEye, IconEyeOff, IconList, IconListDetails, IconLock, IconPlus, IconRefreshAlert, IconRestore, IconSitemap, IconTrash } from "@tabler/icons-react";
import { IndexMarks, KindIcon, PropertyIcon, RelationIcon, SourceDot, SourceIcon, kindMeta, relationMeta, sourceKindMeta, type IndexFlags } from "./DatamodelIcons";
import type { EditorContext, Selection } from "./DatamodelEditors";
import { allProperties, fullName, type HistoryEntry, type ModelDiff, type NodeTypeJson, type PropertyJson, type SourceInfo } from "../server/datamodel";
import { formatBytes, formatTime } from "../format";

export interface ViewProps {
  ctx: EditorContext;
  /** types whose source is switched on */
  visibleTypes: Set<string>;
  /** types of a switched off source that something visible points at: shown, but grayed out */
  ghostTypes: Set<string>;
  query: string;
  selection: Selection | null;
  diff: ModelDiff | null;
  /** a type just added here: its properties start unfolded, so what is added to it next is visible */
  justAdded?: string | null;
}

function matches(query: string, ...texts: (string | null | undefined)[]): boolean {
  const q = query.trim().toLowerCase();
  if (!q) return true;
  return texts.some((t) => t && t.toLowerCase().includes(q));
}

function changeBadge(diff: ModelDiff | null, id: string, kind: "type" | "relation") {
  if (!diff) return null;
  const added = kind === "type" ? diff.addedTypes : diff.addedRelations;
  const changed = kind === "type" ? diff.changedTypes : diff.changedRelations;
  if (added.includes(id)) return <span className="badge dm-badge-new">new</span>;
  if (changed.includes(id)) return <span className="badge dm-badge-changed">changed</span>;
  return null;
}

/** What a property row says after its name: the value type, and where it points when it points. */
function propertyDetail(ctx: EditorContext, p: PropertyJson): string {
  if (p.PropertyType === "Relation" && p.RelationId) return p.PropertyType + " · " + (ctx.model.Relations[p.RelationId]?.CodeName ?? "?");
  const targets = p.PropertyType === "Embedded" ? p.InnerNodeTypes : p.PropertyType === "Reference" || p.PropertyType === "References" ? p.NodeTypes : null;
  if (!targets || targets.length === 0) return p.PropertyType;
  return p.PropertyType + " → " + targets.map((id) => ctx.model.NodeTypes[id]?.CodeName ?? "?").join(", ");
}

function isSelected(selection: Selection | null, kind: Selection["kind"], id: string): boolean {
  return selection !== null && selection.kind === kind && selection.id === id;
}

// ---- sorting a table by one of its columns ----

/** Which column a table is ordered by, and which way round. */
interface Sort {
  key: string;
  dir: "asc" | "desc";
}

/**
 * Clicking a column orders by it; clicking the one already ordered by turns it round. A column of
 * numbers starts at the largest and one of names at A, which is what each is normally wanted by.
 */
function useSort(initial: Sort) {
  const [sort, setSort] = useState<Sort>(initial);
  const sortBy = (key: string, numeric: boolean) =>
    setSort((s) => (s.key === key ? { key, dir: s.dir === "asc" ? "desc" : "asc" } : { key, dir: numeric ? "desc" : "asc" }));
  return [sort, sortBy] as const;
}

/** One column heading, as the control that orders by it. */
function SortHead({ label, col, numeric, sort, onSort, title }: { label: string; col: string; numeric?: boolean; sort: Sort; onSort: (key: string, numeric: boolean) => void; title?: string }) {
  const active = sort.key === col;
  return (
    <th className={"dm-sortable" + (numeric ? " num" : "") + (active ? " sorted" : "")} aria-sort={active ? (sort.dir === "asc" ? "ascending" : "descending") : "none"}>
      <button onClick={() => onSort(col, numeric === true)} title={title ?? `Sort by ${label.toLowerCase()}`}>
        {label}
        {active ? sort.dir === "asc" ? <IconChevronUp size={12} stroke={2.6} /> : <IconChevronDown size={12} stroke={2.6} /> : null}
      </button>
    </th>
  );
}

/**
 * The rows in the order the head asks for. Values come from `columns`; a column that is not there
 * leaves the order to `tiebreak`, which also settles rows the chosen column cannot tell apart - so
 * ordering by something coarse, like a source, still reads alphabetically inside each group.
 */
function sortRows<T>(rows: T[], sort: Sort, columns: Record<string, (row: T) => string | number>, tiebreak: (row: T) => string): T[] {
  const pick = columns[sort.key];
  const dir = sort.dir === "asc" ? 1 : -1;
  return [...rows].sort((a, b) => {
    let c = 0;
    if (pick) {
      const va = pick(a);
      const vb = pick(b);
      c = typeof va === "number" && typeof vb === "number" ? va - vb : String(va).localeCompare(String(vb));
    }
    return dir * c || tiebreak(a).localeCompare(tiebreak(b));
  });
}

// ---- the types, as a flat list or as the tree they inherit down ----

/** Which arrangement the Models view is in. Kept for the database, like the graph's own settings. */
export type ModelsLayout = "list" | "tree";

const layoutKey = "dmModelsLayout";
const treePropertiesKey = "dmTreeProperties";
const listPropertiesKey = "dmListProperties";

/** A remembered switch. Absent means the default: the tree carries properties, the table does not. */
function readSwitch(key: string, fallback: boolean) {
  const v = localStorage.getItem(key);
  return v === null ? fallback : v === "true";
}

/**
 * Every type in the model, arranged either way: a flat table of them all, or the tree they inherit
 * down. The two are one view rather than two, because they answer the same question and the switch
 * between them is worth less room than a tab each. Properties can be switched off in both, and each
 * arrangement remembers its own answer: the tree is short enough to carry them, the table is not.
 */
export function ModelsView(props: ViewProps) {
  const [layout, setLayout] = useState<ModelsLayout>(() => (localStorage.getItem(layoutKey) === "tree" ? "tree" : "list"));
  const [treeProperties, setTreeProperties] = useState(() => readSwitch(treePropertiesKey, true));
  const [listProperties, setListProperties] = useState(() => readSwitch(listPropertiesKey, false));
  useEffect(() => localStorage.setItem(layoutKey, layout), [layout]);
  useEffect(() => localStorage.setItem(treePropertiesKey, String(treeProperties)), [treeProperties]);
  useEffect(() => localStorage.setItem(listPropertiesKey, String(listProperties)), [listProperties]);
  const showProperties = layout === "tree" ? treeProperties : listProperties;
  const setShowProperties = layout === "tree" ? setTreeProperties : setListProperties;
  const { ctx, visibleTypes, ghostTypes, query } = props;
  const count = Object.values(ctx.model.NodeTypes).filter((t) => t.Id !== ctx.baseTypeId && (visibleTypes.has(t.Id) || ghostTypes.has(t.Id)) && matches(query, t.CodeName, t.Namespace, ...Object.values(t.Properties).map((p) => p.CodeName))).length;
  return (
    <section className="panel dm-models">
      <div className="dm-models-head">
        <h3>
          Types <span className="panel-sub">{count}</span>
        </h3>
        <div className="dm-models-tools">
          <div className="dm-layout-picker" role="tablist" aria-label="How the types are arranged">
            <button role="tab" aria-selected={layout === "list"} className={layout === "list" ? "active" : ""} title="Every type in one flat table" onClick={() => setLayout("list")}>
              <IconList size={14} stroke={1.9} /> List
            </button>
            <button role="tab" aria-selected={layout === "tree"} className={layout === "tree" ? "active" : ""} title="The types as the tree they inherit down" onClick={() => setLayout("tree")}>
              <IconSitemap size={14} stroke={1.9} /> Inheritance
            </button>
          </div>
          <button className={"dm-chip-source dm-tree-toggle" + (showProperties ? "" : " off")} onClick={() => setShowProperties((v) => !v)} title={showProperties ? "Hide properties" : "Show properties"}>
            <IconListDetails size={14} stroke={1.9} /> Properties
          </button>
        </div>
      </div>
      <div className="dm-panel-scroll">{layout === "list" ? <TypesTable {...props} showProperties={listProperties} /> : <TypeTree {...props} showProperties={treeProperties} />}</div>
    </section>
  );
}

// ---- the types, flat ----

function TypesTable({ ctx, visibleTypes, ghostTypes, query, selection, diff, justAdded, showProperties }: ViewProps & { showProperties: boolean }) {
  // which rows are shown against the grain of the Properties switch, so one type can be opened
  // while the rest stay shut - and flipping the switch is a fresh start for all of them
  const [flipped, setFlipped] = useState<Set<string>>(new Set());
  useEffect(() => setFlipped(new Set()), [showProperties]);
  useEffect(() => {
    if (justAdded) setFlipped((prev) => new Set(prev).add(justAdded));
  }, [justAdded]);
  const [sort, sortBy] = useSort({ key: "name", dir: "asc" });
  const toggle = (id: string) =>
    setFlipped((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  const sourceName = (id: string) => ctx.sources.find((s) => s.id === id)?.name ?? ctx.model.Sources.find((s) => s.Id === id)?.Name ?? "?";
  const parentNames = (t: NodeTypeJson) =>
    (t.Parents ?? [])
      .filter((p) => p !== ctx.baseTypeId)
      .map((p) => ctx.model.NodeTypes[p]?.CodeName ?? "?")
      .join(", ");
  const shown = Object.values(ctx.model.NodeTypes)
    .filter((t) => t.Id !== ctx.baseTypeId && (visibleTypes.has(t.Id) || ghostTypes.has(t.Id)))
    .filter((t) => matches(query, t.CodeName, t.Namespace, ...Object.values(t.Properties).map((p) => p.CodeName)));
  const types = sortRows(
    shown,
    sort,
    {
      name: (t) => t.CodeName,
      namespace: (t) => t.Namespace ?? "",
      source: (t) => sourceName(t.DatamodelSourceId),
      props: (t) => Object.values(t.Properties).length,
      inherits: (t) => parentNames(t),
      // a type of a database that is closed has no count at all: those go last either way round
      nodes: (t) => ctx.typeCounts[t.Id] ?? -1,
    },
    (t) => t.CodeName,
  );
  return (
    <>
        <table className="dm-table">
          <thead>
            <tr>
              <th></th>
              <SortHead label="Name" col="name" sort={sort} onSort={sortBy} />
              <SortHead label="Namespace" col="namespace" sort={sort} onSort={sortBy} />
              <SortHead label="Source" col="source" sort={sort} onSort={sortBy} />
              <SortHead label="Props" col="props" numeric sort={sort} onSort={sortBy} title="Sort by how many properties the type declares itself" />
              <SortHead label="Inherits" col="inherits" sort={sort} onSort={sortBy} />
              <SortHead label="Nodes" col="nodes" numeric sort={sort} onSort={sortBy} title="Sort by how many nodes of the type the database holds" />
              <th></th>
            </tr>
          </thead>
          <tbody>
            {types.map((t) => {
              const ghost = ghostTypes.has(t.Id) && !visibleTypes.has(t.Id);
              const open = flipped.has(t.Id) !== showProperties || (query.trim().length > 0 && Object.values(t.Properties).some((p) => matches(query, p.CodeName)) && !matches(query, t.CodeName));
              const own = Object.values(t.Properties);
              const count = ctx.typeCounts[t.Id];
              return [
                <tr key={t.Id} className={"dm-row" + (ghost ? " dm-ghost" : "") + (isSelected(selection, "type", t.Id) ? " selected" : "")} onClick={() => ctx.select({ kind: "type", id: t.Id })}>
                  <td className="dm-cell-icon">
                    <button
                      className="dm-expander"
                      onClick={(e) => {
                        e.stopPropagation();
                        toggle(t.Id);
                      }}
                      title={open ? "Hide properties" : "Show properties"}
                    >
                      {open ? <IconChevronDown size={13} stroke={2} /> : <IconChevronRight size={13} stroke={2} />}
                    </button>
                    <KindIcon kind={t.ModelType} />
                  </td>
                  <td className="dm-cell-name">
                    {t.CodeName}
                    {changeBadge(diff, t.Id, "type")}
                    {t.Hidden && <span className="badge">hidden</span>}
                    {t.IsInnerNode && <span className="badge">inner</span>}
                  </td>
                  <td className="muted">{t.Namespace}</td>
                  <td>
                    <SourceDot color={ctx.colors.get(t.DatamodelSourceId) ?? "#888"} /> {sourceName(t.DatamodelSourceId)}
                    {ghost && <span className="muted"> (off)</span>}
                  </td>
                  <td className="num">{own.length}</td>
                  <td className="muted dm-cell-parents">{parentNames(t)}</td>
                  <td className="num">{count !== undefined ? count : ""}</td>
                  <td></td>
                </tr>,
                open &&
                  own.map((p) => (
                    <tr key={p.Id} className={"dm-row dm-proprow-table" + (ghost ? " dm-ghost" : "") + (isSelected(selection, "property", p.Id) ? " selected" : "")} onClick={() => ctx.select({ kind: "property", id: p.Id, typeId: t.Id })}>
                      <td className="dm-cell-icon">
                        <span className="dm-indent" />
                        <PropertyIcon propertyType={p.PropertyType} />
                      </td>
                      <td className="dm-cell-name">
                        {p.CodeName}
                        <IndexMarks flags={{ indexed: p.Indexed, wordIndex: p.IndexedByWords, semanticIndex: p.IndexedBySemantic }} />
                      </td>
                      <td className="muted" colSpan={2}>
                        {propertyDetail(ctx, p)}
                      </td>
                      <td colSpan={4} className="dm-cell-flags">
                        {p.UniqueValues && <span className="badge">unique</span>}
                        {p.DisplayName && <span className="badge">display name</span>}
                      </td>
                    </tr>
                  )),
              ];
            })}
            {types.length === 0 && (
              <tr>
                <td colSpan={8} className="muted dm-empty">
                  No types match.
                </td>
              </tr>
            )}
          </tbody>
        </table>
    </>
  );
}

// ---- the relations ----

/**
 * Every relation in the model: what it joins, which way round, and where it is declared. A relation
 * belongs to no one type - both ends are equal - so it is read here rather than under either end.
 */
export function RelationsView({ ctx, visibleTypes, ghostTypes, query, selection, diff }: ViewProps) {
  const [sort, sortBy] = useSort({ key: "name", dir: "asc" });
  const sourceName = (id: string) => ctx.sources.find((s) => s.id === id)?.name ?? ctx.model.Sources.find((s) => s.Id === id)?.Name ?? "?";
  const typeNames = (ids: string[]) => ids.map((id) => ctx.model.NodeTypes[id]?.CodeName ?? "?").join(", ");
  const shown = Object.values(ctx.model.Relations)
    .filter((r) => [...r.SourceTypes, ...r.TargetTypes].some((id) => visibleTypes.has(id) || ghostTypes.has(id)))
    .filter((r) => matches(query, r.CodeName, r.Namespace, r.CodeNameSources, r.CodeNameTargets));
  const relations = sortRows(
    shown,
    sort,
    {
      name: (r) => r.CodeName,
      kind: (r) => relationMeta[r.RelationType]?.short ?? r.RelationType,
      from: (r) => typeNames(r.SourceTypes),
      to: (r) => typeNames(r.TargetTypes),
      source: (r) => sourceName(r.DatamodelSourceId),
    },
    (r) => r.CodeName,
  );
  return (
      <section className="panel dm-relations">
        <h3>
          Relations <span className="panel-sub">{relations.length}</span>
        </h3>
        <div className="dm-panel-scroll">
        <table className="dm-table">
          <thead>
            <tr>
              <th></th>
              <SortHead label="Name" col="name" sort={sort} onSort={sortBy} />
              <SortHead label="Kind" col="kind" sort={sort} onSort={sortBy} />
              <SortHead label="From" col="from" sort={sort} onSort={sortBy} />
              <SortHead label="To" col="to" sort={sort} onSort={sortBy} />
              <SortHead label="Source" col="source" sort={sort} onSort={sortBy} />
            </tr>
          </thead>
          <tbody>
            {relations.map((r) => (
              <tr key={r.Id} className={"dm-row" + (isSelected(selection, "relation", r.Id) ? " selected" : "")} onClick={() => ctx.select({ kind: "relation", id: r.Id })}>
                <td className="dm-cell-icon">
                  <RelationIcon kind={r.RelationType} />
                </td>
                <td className="dm-cell-name">
                  {r.CodeName}
                  {changeBadge(diff, r.Id, "relation")}
                </td>
                <td className="muted" title={relationMeta[r.RelationType]?.label}>
                  {relationMeta[r.RelationType]?.short ?? r.RelationType}
                </td>
                <td>{r.SourceTypes.map((id) => ctx.model.NodeTypes[id]?.CodeName ?? "?").join(", ")}{r.CodeNameSources ? <span className="muted"> .{r.CodeNameSources}</span> : null}</td>
                <td>{r.TargetTypes.map((id) => ctx.model.NodeTypes[id]?.CodeName ?? "?").join(", ")}{r.CodeNameTargets ? <span className="muted"> .{r.CodeNameTargets}</span> : null}</td>
                <td>
                  <SourceDot color={ctx.colors.get(r.DatamodelSourceId) ?? "#888"} /> {sourceName(r.DatamodelSourceId)}
                </td>
              </tr>
            ))}
            {relations.length === 0 && (
              <tr>
                <td colSpan={6} className="muted dm-empty">
                  No relations match.
                </td>
              </tr>
            )}
          </tbody>
        </table>
        </div>
      </section>
  );
}

// ---- the types, as the tree they inherit down ----

function TypeTree({ ctx, visibleTypes, ghostTypes, query, selection, diff, showProperties }: ViewProps & { showProperties: boolean }) {
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const shown = new Set([...visibleTypes, ...ghostTypes]);
  const children = useMemo(() => {
    const map = new Map<string, NodeTypeJson[]>();
    for (const t of Object.values(ctx.model.NodeTypes)) {
      if (!shown.has(t.Id)) continue;
      const parents = (t.Parents ?? []).filter((p) => p !== ctx.baseTypeId && shown.has(p));
      const key = parents.length === 0 ? "" : "";
      if (parents.length === 0) map.set(key, [...(map.get(key) ?? []), t]);
      for (const p of parents) map.set(p, [...(map.get(p) ?? []), t]);
    }
    for (const list of map.values()) list.sort((a, b) => a.CodeName.localeCompare(b.CodeName));
    return map;
  }, [ctx.model, visibleTypes, ghostTypes]);
  const q = query.trim().toLowerCase();
  const ownProperties = (t: NodeTypeJson) => Object.values(t.Properties).filter((p) => !p.Internal);
  // with a search, a branch stays when anything below it matches
  const branchMatches = (t: NodeTypeJson, depth: number): boolean => {
    if (!q) return true;
    if (t.CodeName.toLowerCase().includes(q) || (t.Namespace ?? "").toLowerCase().includes(q)) return true;
    if (showProperties && ownProperties(t).some((p) => p.CodeName.toLowerCase().includes(q))) return true;
    if (depth > 30) return false;
    return (children.get(t.Id) ?? []).some((c) => branchMatches(c, depth + 1));
  };
  const render = (t: NodeTypeJson, depth: number, path: string): React.ReactNode => {
    if (!branchMatches(t, depth) || depth > 30) return null;
    const kids = children.get(t.Id) ?? [];
    const key = path + "/" + t.Id;
    const isCollapsed = collapsed.has(key);
    const ghost = ghostTypes.has(t.Id) && !visibleTypes.has(t.Id);
    const parents = (t.Parents ?? []).filter((p) => p !== ctx.baseTypeId);
    const count = ctx.typeCounts[t.Id];
    const own = showProperties ? ownProperties(t) : [];
    return (
      <div key={key} className="dm-tree-node">
        <div className={"dm-tree-row" + (ghost ? " dm-ghost" : "") + (isSelected(selection, "type", t.Id) ? " selected" : "")} style={{ paddingLeft: depth * 22 + 6 }} onClick={() => ctx.select({ kind: "type", id: t.Id })}>
          <button
            className="dm-expander"
            style={{ visibility: kids.length > 0 || own.length > 0 ? "visible" : "hidden" }}
            onClick={(e) => {
              e.stopPropagation();
              setCollapsed((prev) => {
                const next = new Set(prev);
                if (next.has(key)) next.delete(key);
                else next.add(key);
                return next;
              });
            }}
          >
            {isCollapsed ? <IconChevronRight size={13} stroke={2} /> : <IconChevronDown size={13} stroke={2} />}
          </button>
          <KindIcon kind={t.ModelType} />
          <span className="dm-tree-name">{t.CodeName}</span>
          <SourceDot color={ctx.colors.get(t.DatamodelSourceId) ?? "#888"} title={ctx.sources.find((s) => s.id === t.DatamodelSourceId)?.name} />
          {changeBadge(diff, t.Id, "type")}
          {t.IsInnerNode && <span className="badge">inner</span>}
          <span className="muted dm-tree-meta">
            {kindMeta[t.ModelType]?.label} · {ownProperties(t).length} props
            {count !== undefined ? ` · ${count} nodes` : ""}
            {parents.length > 1 ? ` · also under ${parents.filter((p) => p !== (path.split("/").pop() ?? "")).map((p) => ctx.model.NodeTypes[p]?.CodeName ?? "?").join(", ")}` : ""}
          </span>
        </div>
        {!isCollapsed &&
          own.map((p) => (
            <div
              key={key + "/" + p.Id}
              className={"dm-tree-row dm-tree-prop" + (ghost ? " dm-ghost" : "") + (isSelected(selection, "property", p.Id) ? " selected" : "")}
              style={{ paddingLeft: (depth + 1) * 22 + 6 }}
              onClick={() => ctx.select({ kind: "property", id: p.Id, typeId: t.Id })}
            >
              <span className="dm-tree-spacer" />
              <PropertyIcon propertyType={p.PropertyType} />
              <span className="dm-tree-propname">{p.CodeName}</span>
              <IndexMarks flags={{ indexed: p.Indexed, wordIndex: p.IndexedByWords, semanticIndex: p.IndexedBySemantic }} />
              <span className="muted dm-tree-meta">{propertyDetail(ctx, p)}</span>
            </div>
          ))}
        {!isCollapsed && kids.map((c) => render(c, depth + 1, key))}
      </div>
    );
  };
  const roots = children.get("") ?? [];
  return (
    <div className="dm-tree-body">
      {roots.map((t) => render(t, 0, ""))}
      {roots.length === 0 && <div className="muted dm-empty">No types to show.</div>}
    </div>
  );
}

// ---- properties matrix ----

export function MatrixView({ ctx, visibleTypes, ghostTypes, query, selection }: ViewProps) {
  const types = Object.values(ctx.model.NodeTypes)
    .filter((t) => t.Id !== ctx.baseTypeId && (visibleTypes.has(t.Id) || ghostTypes.has(t.Id)) && !t.IsInnerNode)
    .sort((a, b) => a.CodeName.localeCompare(b.CodeName));
  const rows = useMemo(() => {
    const byName = new Map<string, { name: string; propertyType: string; flags: IndexFlags; own: Set<string>; inherited: Set<string> }>();
    for (const t of types) {
      for (const p of allProperties(ctx.model, t.Id, ctx.baseTypeId)) {
        if (p.property.Internal) continue;
        const key = p.property.CodeName.toLowerCase();
        let row = byName.get(key);
        if (!row) byName.set(key, (row = { name: p.property.CodeName, propertyType: p.property.PropertyType, flags: {}, own: new Set(), inherited: new Set() }));
        if (p.property.Indexed) row.flags.indexed = true;
        if (p.property.IndexedByWords) row.flags.wordIndex = true;
        if (p.property.IndexedBySemantic) row.flags.semanticIndex = true;
        (p.inherited ? row.inherited : row.own).add(t.Id);
      }
    }
    return [...byName.values()].filter((r) => matches(query, r.name)).sort((a, b) => b.own.size + b.inherited.size - (a.own.size + a.inherited.size) || a.name.localeCompare(b.name));
  }, [ctx.model, types, query]);
  const maxColumns = 60;
  const columns = types.slice(0, maxColumns);
  return (
    <div className="dm-matrix panel">
      <h3>
        Properties by type <span className="panel-sub">{rows.length} names · {types.length} types</span>
      </h3>
      <div className="dm-matrix-scroll">
        <table className="dm-matrix-table">
          <thead>
            <tr>
              <th className="dm-matrix-corner"></th>
              {columns.map((t) => (
                <th key={t.Id} className={"dm-matrix-col" + (ghostTypes.has(t.Id) && !visibleTypes.has(t.Id) ? " dm-ghost" : "") + (isSelected(selection, "type", t.Id) ? " selected" : "")} onClick={() => ctx.select({ kind: "type", id: t.Id })} title={fullName(t)}>
                  <div className="dm-matrix-colname">
                    <KindIcon kind={t.ModelType} size={13} />
                    <span>{t.CodeName}</span>
                  </div>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.name}>
                <th className="dm-matrix-rowhead">
                  <PropertyIcon propertyType={r.propertyType} />
                  <span>{r.name}</span>
                  <IndexMarks flags={r.flags} />
                  <span className="muted">{r.own.size + r.inherited.size}</span>
                </th>
                {columns.map((t) => {
                  const own = r.own.has(t.Id);
                  const inh = r.inherited.has(t.Id);
                  const p = own ? Object.values(t.Properties).find((x) => x.CodeName.toLowerCase() === r.name.toLowerCase()) : undefined;
                  return (
                    <td key={t.Id} className={"dm-matrix-cell" + (own ? " own" : inh ? " inherited" : "")} onClick={() => (p ? ctx.select({ kind: "property", id: p.Id, typeId: t.Id }) : own || inh ? ctx.select({ kind: "type", id: t.Id }) : undefined)} title={own ? `${t.CodeName}.${r.name}` : inh ? `${t.CodeName} inherits ${r.name}` : undefined}>
                      {own ? <span className="dm-matrix-dot" /> : inh ? <span className="dm-matrix-ring" /> : null}
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
        {types.length > maxColumns && <div className="muted dm-empty">Showing the first {maxColumns} of {types.length} types. Switch some sources off to see the rest.</div>}
      </div>
    </div>
  );
}

// ---- sources ----

export function SourcesView({ ctx, selection, hiddenSources, onToggleVisible, onAdd, locked }: { ctx: EditorContext; selection: Selection | null; hiddenSources: Set<string>; onToggleVisible: (id: string) => void; onAdd: () => void; locked: boolean }) {
  return (
    <div className="dm-sources">
      <div className="dm-sources-head">
        <span className="muted dm-help-text">
          Every source is loaded into one model, in this order. A type is written back into the source it belongs to; sources that cannot be written show their types read only. A source with nothing in it
          yet loads as an empty one, so it can be added before its first type exists.
        </span>
        {!locked && (
          <button className="action-button" onClick={onAdd}>
            <IconPlus size={15} stroke={2} /> Add source
          </button>
        )}
      </div>
      <div className="dm-source-cards">
        {ctx.model.Sources.map((s) => {
          const info = ctx.sources.find((x) => x.id === s.Id);
          const color = ctx.colors.get(s.Id) ?? "#888";
          const types = Object.values(ctx.model.NodeTypes).filter((t) => t.DatamodelSourceId === s.Id && t.Id !== ctx.baseTypeId).length;
          const relations = Object.values(ctx.model.Relations).filter((r) => r.DatamodelSourceId === s.Id).length;
          const hidden = hiddenSources.has(s.Id);
          const isCode = s.Type === "Code";
          return (
            <div key={s.Id} className={"dm-source-card" + (isSelected(selection, "source", s.Id) ? " selected" : "") + (hidden ? " off" : "") + (!s.Enabled ? " disabled" : "")} style={{ borderLeftColor: color }} onClick={() => ctx.select({ kind: "source", id: s.Id })}>
              <div className="dm-source-card-head">
                <SourceIcon type={s.Type} fileFormat={s.FileFormat} color={color} size={20} />
                <div className="dm-source-card-title">
                  <div className="dm-source-card-name">{s.Name || s.Id}</div>
                  <div className="muted">{sourceKindMeta(s.Type, s.FileFormat).label}</div>
                </div>
                <button
                  className="icon-button"
                  title={hidden ? "Show its types" : "Hide its types"}
                  onClick={(e) => {
                    e.stopPropagation();
                    onToggleVisible(s.Id);
                  }}
                >
                  {hidden ? <IconEyeOff size={16} stroke={1.9} /> : <IconEye size={16} stroke={1.9} />}
                </button>
              </div>
              <div className="dm-source-card-body">
                <div className="dm-source-badges">
                  {!s.Enabled && <span className="badge dm-badge-warn">turned off</span>}
                  {isCode ? (
                    <span className="badge">
                      <IconLock size={11} stroke={2} /> read only
                    </span>
                  ) : info ? (
                    info.writable ? (
                      info.requiresRebuild ? (
                        <span className="badge dm-badge-warn">
                          <IconRefreshAlert size={11} stroke={2} /> writable, needs rebuild
                        </span>
                      ) : (
                        <span className="badge dm-badge-ok">writable</span>
                      )
                    ) : (
                      <span className="badge" title={info.readOnlyReason ?? undefined}>
                        <IconLock size={11} stroke={2} /> read only
                      </span>
                    )
                  ) : (
                    <span className="badge dm-badge-new">new</span>
                  )}
                  {info && !info.inSettings && !isCode && <span className="badge dm-badge-warn">not in settings</span>}
                  {/* allowed, not an error: a source is empty until the first type is written into it */}
                  {s.Enabled && types + relations === 0 && <span className="badge" title="The source loads, but nothing in it defines a type yet. Add one, or check the namespace and path below if it was meant to hold types.">empty</span>}
                </div>
                <div className="dm-source-facts">
                  {s.Namespace && (
                    <div>
                      <span className="fact-k">Namespace</span> {s.Namespace}
                    </div>
                  )}
                  {s.Type === "TypeReference" && (
                    <div>
                      <span className="fact-k">Assembly</span> {s.Reference || "Current project"}
                    </div>
                  )}
                  {s.Type !== "TypeReference" && s.Reference && (
                    <div>
                      <span className="fact-k">File name</span> {s.Reference}
                    </div>
                  )}
                  {(info?.resolvedPath || s.Filepath || s.SourceCodePath) && (
                    <div className={info?.pathExists === false ? "dm-missing" : ""}>
                      <span className="fact-k">{s.Type === "TypeReference" ? (s.GenerateModelFile ? "Generated code" : "Source code") : "Path"}</span> {info?.resolvedPath ?? s.Filepath ?? s.SourceCodePath}
                      {info?.pathExists === false ? " (missing)" : ""}
                    </div>
                  )}
                  <div>
                    <span className="fact-k">Holds</span> {types} type{types === 1 ? "" : "s"}, {relations} relation{relations === 1 ? "" : "s"}
                    {info && info.files.length > 0 ? ` in ${info.files.length} file${info.files.length === 1 ? "" : "s"}` : ""}
                  </div>
                </div>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}

// ---- history ----

export function HistoryView({ history, activeChecksum, draftBaseChecksum, onLoad, onDelete }: { history: HistoryEntry[]; activeChecksum: string | null; draftBaseChecksum: string | null; onLoad: (entry: HistoryEntry) => void; onDelete: (entry: HistoryEntry) => void }) {
  return (
    <div className="dm-history panel">
      <h3>
        Model history <span className="panel-sub">{history.length} of at most 50</span>
      </h3>
      <p className="muted dm-history-intro dm-help-text">
        Every model that has been active, newest first: recorded when the database opens with a model the newest entry does not already hold, and just before an activation replaces one. Loading an
        entry makes it the draft; nothing changes until that draft is activated.
      </p>
      <table className="dm-table">
        <thead>
          <tr>
            <th>Saved</th>
            <th>Recorded because</th>
            <th className="num">Types</th>
            <th className="num">Relations</th>
            <th className="num">Properties</th>
            <th className="num">Size</th>
            <th>Checksum</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {history.map((h) => {
            const isActive = activeChecksum !== null && h.checksum === activeChecksum;
            const isBase = draftBaseChecksum !== null && h.checksum === draftBaseChecksum && !isActive;
            return (
              <tr key={h.key} className="dm-row static">
                <td>{formatTime(h.savedUtc)}</td>
                <td>
                  {h.reason === "open" ? "the database opened with it" : h.reason === "replaced" ? "an activation replaced it" : h.reason}
                  {isActive && <span className="badge dm-badge-ok">active now</span>}
                  {isBase && <span className="badge">draft started here</span>}
                </td>
                <td className="num">{h.nodeTypes}</td>
                <td className="num">{h.relations}</td>
                <td className="num">{h.properties}</td>
                <td className="num">{formatBytes(h.size)}</td>
                <td className="muted dm-mono">{h.checksum.slice(0, 8)}</td>
                <td className="dm-cell-actions">
                  <button className="link-button" onClick={() => onLoad(h)} title="Make this model the draft">
                    <IconRestore size={13} stroke={2} /> Load as draft
                  </button>
                  <button className="icon-button danger" onClick={() => onDelete(h)} title="Delete this history entry">
                    <IconTrash size={14} stroke={1.9} />
                  </button>
                </td>
              </tr>
            );
          })}
          {history.length === 0 && (
            <tr>
              <td colSpan={8} className="muted dm-empty">
                No history yet. The first entry is written when the database opens.
              </td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  );
}

export type { SourceInfo };
