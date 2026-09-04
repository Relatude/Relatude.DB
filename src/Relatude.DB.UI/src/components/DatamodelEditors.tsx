import { useState } from "react";
import { IconChevronDown, IconChevronRight, IconPlus, IconTrash, IconWand, IconX } from "@tabler/icons-react";
import { KindIcon, PropertyIcon, RelationIcon, SourceDot, SourceIcon, relationMeta } from "./DatamodelIcons";
import {
  allProperties,
  fullName,
  newGuid,
  relationsOf,
  type FieldDef,
  type ModelJson,
  type NodeTypeJson,
  type PropertyJson,
  type RelationJson,
  type Schema,
  type SourceInfo,
  type SourceJson,
  type SourceType,
} from "../server/datamodel";

const emptyGuid = "00000000-0000-0000-0000-000000000000";

/** What every editor needs to know about the model around the thing it edits. */
export interface EditorContext {
  model: ModelJson;
  schema: Schema;
  baseTypeId: string;
  codeSourceId: string;
  sources: SourceInfo[];
  colors: Map<string, string>;
  typeCounts: Record<string, number>;
  /** whether the source the item belongs to can be written; false makes the editor read only */
  writableSource: (sourceId: string) => boolean;
  readOnlyReason: (sourceId: string) => string | null;
  update: (mutate: (model: ModelJson) => void) => void;
  select: (selection: Selection | null) => void;
}

export type Selection =
  | { kind: "type"; id: string }
  | { kind: "property"; id: string; typeId: string }
  | { kind: "relation"; id: string }
  | { kind: "source"; id: string };

// ---- one field ----

interface FieldProps {
  field: FieldDef;
  value: unknown;
  onChange: (value: unknown) => void;
  disabled: boolean;
  ctx: EditorContext;
  /** for propertyRef: the type whose properties (and inherited ones) are offered */
  typeId?: string;
}

/**
 * Renders one schema field. The value lives in the model object under field.path; an unset value
 * shows the default the server read off a fresh model object, which is what the engine will use.
 */
export function FieldEditor({ field, value, onChange, disabled, ctx, typeId }: FieldProps) {
  const current = value === undefined ? field.default : value;
  const isDefault = value === undefined || JSON.stringify(value) === JSON.stringify(field.default);
  const off = disabled || field.readOnly;
  let control: React.ReactNode;
  switch (field.editor) {
    case "toggle":
      control = (
        <label className="dm-check">
          <input type="checkbox" checked={current === true} disabled={off} onChange={(e) => onChange(e.target.checked)} />
          <span>{current === true ? "Yes" : "No"}</span>
        </label>
      );
      break;
    case "tristate":
      control = (
        <select className="select" value={current === true ? "true" : current === false ? "false" : ""} disabled={off} onChange={(e) => onChange(e.target.value === "" ? null : e.target.value === "true")}>
          <option value="">Database default</option>
          <option value="true">Yes</option>
          <option value="false">No</option>
        </select>
      );
      break;
    case "choice":
      control = (
        <select className="select" value={String(current ?? "")} disabled={off} onChange={(e) => onChange(e.target.value === "" ? null : e.target.value)}>
          {field.optional && <option value="">(none)</option>}
          {(field.choices ?? []).map((c) => (
            <option key={c} value={c}>
              {c}
            </option>
          ))}
        </select>
      );
      break;
    case "integer":
    case "number":
      control = (
        <input
          className="text-input dm-number"
          type="text"
          inputMode="decimal"
          value={current === null || current === undefined ? "" : String(current)}
          disabled={off}
          onChange={(e) => {
            const text = e.target.value.trim();
            if (text === "") return onChange(field.optional ? null : 0);
            const n = field.editor === "integer" ? parseInt(text, 10) : parseFloat(text);
            if (!Number.isNaN(n)) onChange(n);
          }}
        />
      );
      break;
    case "guid":
      control = (
        <span className="dm-inline">
          <input className="text-input dm-guid" type="text" value={String(current ?? "")} disabled={off} onChange={(e) => onChange(e.target.value)} placeholder={emptyGuid} />
          {!off && (
            <button className="icon-button" title="New random id" onClick={() => onChange(newGuid())}>
              <IconWand size={15} stroke={1.9} />
            </button>
          )}
        </span>
      );
      break;
    case "typeRef":
      control = (
        <select className="select" value={String(current ?? emptyGuid)} disabled={off} onChange={(e) => onChange(e.target.value)}>
          <option value={emptyGuid}>(none)</option>
          {typeOptions(ctx).map((t) => (
            <option key={t.Id} value={t.Id}>
              {fullName(t)}
            </option>
          ))}
        </select>
      );
      break;
    case "typeRefs":
      control = <TypeRefs value={Array.isArray(current) ? (current as string[]) : []} onChange={onChange} disabled={off} ctx={ctx} />;
      break;
    case "relationRef":
      control = (
        <select className="select" value={String(current ?? emptyGuid)} disabled={off} onChange={(e) => onChange(e.target.value)}>
          <option value={emptyGuid}>(none)</option>
          {Object.values(ctx.model.Relations)
            .sort((a, b) => a.CodeName.localeCompare(b.CodeName))
            .map((r) => (
              <option key={r.Id} value={r.Id}>
                {fullName(r)}
              </option>
            ))}
        </select>
      );
      break;
    case "propertyRef": {
      const options = typeId
        ? allProperties(ctx.model, typeId, ctx.baseTypeId).map((p) => ({ id: p.property.Id, label: `${p.owner.CodeName}.${p.property.CodeName}` }))
        : Object.values(ctx.model.NodeTypes).flatMap((t) => Object.values(t.Properties).map((p) => ({ id: p.Id, label: `${t.CodeName}.${p.CodeName}` })));
      control = (
        <select className="select" value={String(current ?? emptyGuid)} disabled={off} onChange={(e) => onChange(e.target.value)}>
          <option value={emptyGuid}>(none)</option>
          {options
            .sort((a, b) => a.label.localeCompare(b.label))
            .map((o) => (
              <option key={o.id} value={o.id}>
                {o.label}
              </option>
            ))}
        </select>
      );
      break;
    }
    case "sourceRef":
      control = (
        <select className="select" value={String(current ?? "")} disabled={off} onChange={(e) => onChange(e.target.value)}>
          {ctx.model.Sources.map((s) => (
            <option key={s.Id} value={s.Id} disabled={s.Type === "Code" || !s.Enabled}>
              {s.Name || s.Id}
              {s.Type === "Code" ? " (read only)" : !s.Enabled ? " (off)" : !ctx.writableSource(s.Id) ? " (read only)" : ""}
            </option>
          ))}
        </select>
      );
      break;
    case "stringList":
    case "intList": {
      const items = Array.isArray(current) ? (current as unknown[]) : [];
      control = (
        <input
          className="text-input dm-wide"
          type="text"
          value={items.join(", ")}
          disabled={off}
          placeholder="comma separated"
          onChange={(e) => {
            const parts = e.target.value
              .split(",")
              .map((s) => s.trim())
              .filter((s) => s.length > 0);
            if (field.editor === "intList") onChange(parts.map((s) => parseInt(s, 10)).filter((n) => !Number.isNaN(n)));
            else onChange(parts);
          }}
        />
      );
      break;
    }
    case "textarea":
      control = <textarea className="text-input dm-wide" rows={3} value={String(current ?? "")} disabled={off} onChange={(e) => onChange(e.target.value)} />;
      break;
    default:
      control = (
        <input
          className="text-input dm-wide"
          type="text"
          value={current === null || current === undefined ? "" : String(current)}
          disabled={off}
          onChange={(e) => onChange(e.target.value === "" && field.optional ? null : e.target.value)}
        />
      );
  }
  return (
    <div className={"dm-field" + (isDefault ? "" : " changed")} title={field.help}>
      <div className="dm-field-label">
        <span>{field.label}</span>
        {!isDefault && !off && (
          <button className="dm-reset" title="Back to the default" onClick={() => onChange(undefined)}>
            <IconX size={11} stroke={2} />
          </button>
        )}
      </div>
      <div className="dm-field-control">{control}</div>
      {field.help && <div className="dm-field-help">{field.help}</div>}
    </div>
  );
}

function typeOptions(ctx: EditorContext): NodeTypeJson[] {
  return Object.values(ctx.model.NodeTypes)
    .filter((t) => t.Id !== ctx.baseTypeId)
    .sort((a, b) => fullName(a).localeCompare(fullName(b)));
}

function TypeRefs({ value, onChange, disabled, ctx }: { value: string[]; onChange: (v: unknown) => void; disabled: boolean; ctx: EditorContext }) {
  const chosen = value.filter((id) => id !== ctx.baseTypeId);
  const remaining = typeOptions(ctx).filter((t) => !chosen.includes(t.Id));
  return (
    <div className="dm-refs">
      {chosen.map((id) => {
        const t = ctx.model.NodeTypes[id];
        return (
          <span key={id} className="dm-chip" style={{ borderColor: t ? ctx.colors.get(t.DatamodelSourceId) : undefined }}>
            {t && <KindIcon kind={t.ModelType} size={13} />}
            <button className="link-button dm-chip-name" onClick={() => t && ctx.select({ kind: "type", id })}>
              {t ? t.CodeName : id}
            </button>
            {!disabled && (
              <button className="dm-chip-remove" title="Remove" onClick={() => onChange(chosen.filter((x) => x !== id))}>
                <IconX size={11} stroke={2} />
              </button>
            )}
          </span>
        );
      })}
      {!disabled && remaining.length > 0 && (
        <select className="select dm-chip-add" value="" onChange={(e) => e.target.value && onChange([...chosen, e.target.value])}>
          <option value="">+ add…</option>
          {remaining.map((t) => (
            <option key={t.Id} value={t.Id}>
              {fullName(t)}
            </option>
          ))}
        </select>
      )}
      {chosen.length === 0 && disabled && <span className="muted">none</span>}
    </div>
  );
}

// ---- field groups ----

function Groups({ fields, target, onChange, disabled, ctx, typeId, open }: { fields: FieldDef[]; target: Record<string, unknown>; onChange: (path: string, value: unknown) => void; disabled: boolean; ctx: EditorContext; typeId?: string; open: string[] }) {
  const [collapsed, setCollapsed] = useState<Set<string>>(() => new Set(ctx.schema.groups.filter((g) => !open.includes(g))));
  const groups = ctx.schema.groups.filter((g) => fields.some((f) => f.group === g));
  return (
    <>
      {groups.map((g) => {
        const isCollapsed = collapsed.has(g);
        const changed = fields.filter((f) => f.group === g && target[f.path] !== undefined && JSON.stringify(target[f.path]) !== JSON.stringify(f.default)).length;
        return (
          <div key={g} className="dm-group">
            <button
              className="dm-group-head"
              onClick={() =>
                setCollapsed((prev) => {
                  const next = new Set(prev);
                  if (next.has(g)) next.delete(g);
                  else next.add(g);
                  return next;
                })
              }
            >
              {isCollapsed ? <IconChevronRight size={13} stroke={2} /> : <IconChevronDown size={13} stroke={2} />}
              <span>{g}</span>
              {changed > 0 && <span className="badge">{changed} set</span>}
            </button>
            {!isCollapsed && (
              <div className="dm-group-body">
                {fields
                  .filter((f) => f.group === g)
                  .map((f) => (
                    <FieldEditor key={f.path} field={f} value={target[f.path]} onChange={(v) => onChange(f.path, v)} disabled={disabled} ctx={ctx} typeId={typeId} />
                  ))}
              </div>
            )}
          </div>
        );
      })}
    </>
  );
}

function setField(target: Record<string, unknown>, path: string, value: unknown) {
  if (value === undefined) delete target[path];
  else target[path] = value;
}

function ReadOnlyBanner({ reason }: { reason: string | null }) {
  return <div className="dm-readonly">Read only. {reason ?? "This source cannot be written from here."}</div>;
}

// ---- a node type ----

export function TypeEditor({ type, ctx, onDelete }: { type: NodeTypeJson; ctx: EditorContext; onDelete: () => void }) {
  const writable = ctx.writableSource(type.DatamodelSourceId);
  const color = ctx.colors.get(type.DatamodelSourceId) ?? "#888";
  const source = ctx.sources.find((s) => s.id === type.DatamodelSourceId);
  const own = Object.values(type.Properties);
  const inherited = allProperties(ctx.model, type.Id, ctx.baseTypeId).filter((p) => p.inherited);
  const relations = relationsOf(ctx.model, type.Id, ctx.baseTypeId);
  const children = Object.values(ctx.model.NodeTypes).filter((t) => (t.Parents ?? []).includes(type.Id));
  const count = ctx.typeCounts[type.Id];
  const [adding, setAdding] = useState(false);
  function addProperty(propertyType: string) {
    const id = newGuid();
    ctx.update((m) => {
      const t = m.NodeTypes[type.Id];
      const defaults: Record<string, unknown> = {};
      for (const f of ctx.schema.propertyCommon) if (f.default !== null && f.default !== undefined) defaults[f.path] = f.default;
      for (const f of ctx.schema.propertyByType[propertyType] ?? []) if (f.default !== null && f.default !== undefined) defaults[f.path] = f.default;
      let name = "NewProperty";
      let n = 2;
      while (Object.values(t.Properties).some((p) => p.CodeName.toLowerCase() === name.toLowerCase())) name = "NewProperty" + n++;
      t.Properties[id] = { ...defaults, Id: id, CodeName: name, PropertyType: propertyType, NodeType: type.Id } as PropertyJson;
    });
    setAdding(false);
    ctx.select({ kind: "property", id, typeId: type.Id });
  }
  return (
    <div className="dm-editor">
      <div className="dm-editor-head">
        <KindIcon kind={type.ModelType} size={20} />
        <div className="dm-editor-title">
          <div className="dm-editor-name">{type.CodeName}</div>
          <div className="dm-editor-sub">
            <SourceDot color={color} /> {source?.name ?? "unknown source"}
            {type.Namespace ? ` · ${type.Namespace}` : ""}
            {count !== undefined ? ` · ${count} node${count === 1 ? "" : "s"}` : ""}
          </div>
        </div>
        {writable && (
          <button className="icon-button danger" title="Delete this type" onClick={onDelete}>
            <IconTrash size={16} stroke={1.9} />
          </button>
        )}
      </div>
      {!writable && <ReadOnlyBanner reason={ctx.readOnlyReason(type.DatamodelSourceId)} />}
      <Groups
        fields={ctx.schema.nodeType}
        target={type as unknown as Record<string, unknown>}
        disabled={!writable}
        ctx={ctx}
        open={["General", "Text search"]}
        onChange={(path, value) => ctx.update((m) => setField(m.NodeTypes[type.Id] as unknown as Record<string, unknown>, path, value))}
      />
      <div className="dm-group">
        <div className="dm-group-head static">
          <span>Properties</span>
          <span className="badge">{own.length}</span>
          {writable && (
            <button className="link-button" onClick={() => setAdding(!adding)}>
              <IconPlus size={12} stroke={2} /> add
            </button>
          )}
        </div>
        {adding && (
          <div className="dm-add-menu">
            {["Values", "Collections", "Links", "Special"].map((g) => (
              <div key={g} className="dm-add-group">
                <div className="dm-add-group-label">{g}</div>
                {ctx.schema.propertyTypes
                  .filter((p) => p.group === g)
                  .map((p) => (
                    <button key={p.value} className="dm-add-item" title={p.help} onClick={() => addProperty(p.value)}>
                      <PropertyIcon propertyType={p.value} /> {p.label}
                    </button>
                  ))}
              </div>
            ))}
          </div>
        )}
        <div className="dm-proplist">
          {own.map((p) => (
            <button key={p.Id} className="dm-proprow" onClick={() => ctx.select({ kind: "property", id: p.Id, typeId: type.Id })}>
              <PropertyIcon propertyType={p.PropertyType} />
              <span className="dm-propname">{p.CodeName}</span>
              <span className="muted">{p.PropertyType}</span>
              {p.Indexed && <span className="badge">indexed</span>}
              {p.IndexedByWords && <span className="badge">words</span>}
              {p.UniqueValues && <span className="badge">unique</span>}
            </button>
          ))}
          {own.length === 0 && <div className="muted dm-empty">No properties of its own.</div>}
        </div>
        {inherited.length > 0 && (
          <>
            <div className="dm-group-head static">
              <span>Inherited</span>
              <span className="badge">{inherited.length}</span>
            </div>
            <div className="dm-proplist">
              {inherited.map((p) => (
                <button key={p.property.Id} className="dm-proprow inherited" onClick={() => ctx.select({ kind: "property", id: p.property.Id, typeId: p.owner.Id })}>
                  <PropertyIcon propertyType={p.property.PropertyType} />
                  <span className="dm-propname">{p.property.CodeName}</span>
                  <span className="muted">from {p.owner.CodeName}</span>
                </button>
              ))}
            </div>
          </>
        )}
      </div>
      {relations.length > 0 && (
        <div className="dm-group">
          <div className="dm-group-head static">
            <span>Relations</span>
            <span className="badge">{relations.length}</span>
          </div>
          <div className="dm-proplist">
            {relations.map(({ relation, asSource, asTarget }) => (
              <button key={relation.Id} className="dm-proprow" onClick={() => ctx.select({ kind: "relation", id: relation.Id })}>
                <RelationIcon kind={relation.RelationType} size={14} />
                <span className="dm-propname">{relation.CodeName}</span>
                <span className="muted">
                  {relationMeta[relation.RelationType]?.short ?? relation.RelationType} · {asSource && asTarget ? "both sides" : asSource ? "as source" : "as target"}
                </span>
              </button>
            ))}
          </div>
        </div>
      )}
      {children.length > 0 && (
        <div className="dm-group">
          <div className="dm-group-head static">
            <span>{type.ModelType === "Interface" ? "Implemented by" : "Extended by"}</span>
            <span className="badge">{children.length}</span>
          </div>
          <div className="dm-proplist">
            {children.map((c) => (
              <button key={c.Id} className="dm-proprow" onClick={() => ctx.select({ kind: "type", id: c.Id })}>
                <KindIcon kind={c.ModelType} size={14} />
                <span className="dm-propname">{c.CodeName}</span>
                <span className="muted">{c.Namespace}</span>
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

// ---- a property ----

export function PropertyEditor({ type, property, ctx, onDelete }: { type: NodeTypeJson; property: PropertyJson; ctx: EditorContext; onDelete: () => void }) {
  const writable = ctx.writableSource(type.DatamodelSourceId) && !property.Internal;
  const typeDef = ctx.schema.propertyTypes.find((p) => p.value === property.PropertyType);
  const fields = [...ctx.schema.propertyCommon, ...(ctx.schema.propertyByType[property.PropertyType] ?? [])];
  return (
    <div className="dm-editor">
      <div className="dm-editor-head">
        <PropertyIcon propertyType={property.PropertyType} size={20} />
        <div className="dm-editor-title">
          <div className="dm-editor-name">
            <button className="link-button dm-crumb" onClick={() => ctx.select({ kind: "type", id: type.Id })}>
              {type.CodeName}
            </button>
            .{property.CodeName}
          </div>
          <div className="dm-editor-sub" title={typeDef?.help}>
            {typeDef?.label ?? property.PropertyType} property{property.Internal ? " · internal" : ""}
            {property.AutoAssigned ? " · assigned by the relation" : ""}
          </div>
        </div>
        {writable && (
          <button className="icon-button danger" title="Delete this property" onClick={onDelete}>
            <IconTrash size={16} stroke={1.9} />
          </button>
        )}
      </div>
      {!ctx.writableSource(type.DatamodelSourceId) && <ReadOnlyBanner reason={ctx.readOnlyReason(type.DatamodelSourceId)} />}
      <Groups
        fields={fields}
        target={property as unknown as Record<string, unknown>}
        disabled={!writable}
        ctx={ctx}
        typeId={type.Id}
        open={["General", "Indexing", "Text search"]}
        onChange={(path, value) => ctx.update((m) => setField(m.NodeTypes[type.Id].Properties[property.Id] as unknown as Record<string, unknown>, path, value))}
      />
    </div>
  );
}

// ---- a relation ----

export function RelationEditor({ relation, ctx, onDelete }: { relation: RelationJson; ctx: EditorContext; onDelete: () => void }) {
  const writable = ctx.writableSource(relation.DatamodelSourceId);
  const color = ctx.colors.get(relation.DatamodelSourceId) ?? "#888";
  const source = ctx.sources.find((s) => s.id === relation.DatamodelSourceId);
  const members = Object.values(ctx.model.NodeTypes).flatMap((t) => Object.values(t.Properties).filter((p) => p.PropertyType === "Relation" && p.RelationId === relation.Id).map((p) => ({ t, p })));
  return (
    <div className="dm-editor">
      <div className="dm-editor-head">
        <RelationIcon kind={relation.RelationType} size={20} />
        <div className="dm-editor-title">
          <div className="dm-editor-name">{relation.CodeName}</div>
          <div className="dm-editor-sub">
            <SourceDot color={color} /> {source?.name ?? "unknown source"} · {relationMeta[relation.RelationType]?.label ?? relation.RelationType}
            {relation.AutoGenerated ? " · generated from a property" : ""}
          </div>
        </div>
        {writable && (
          <button className="icon-button danger" title="Delete this relation" onClick={onDelete}>
            <IconTrash size={16} stroke={1.9} />
          </button>
        )}
      </div>
      {!writable && <ReadOnlyBanner reason={ctx.readOnlyReason(relation.DatamodelSourceId)} />}
      <Groups
        fields={ctx.schema.relation}
        target={relation as unknown as Record<string, unknown>}
        disabled={!writable}
        ctx={ctx}
        open={["General", "Constraints"]}
        onChange={(path, value) => ctx.update((m) => setField(m.Relations[relation.Id] as unknown as Record<string, unknown>, path, value))}
      />
      {members.length > 0 && (
        <div className="dm-group">
          <div className="dm-group-head static">
            <span>Members</span>
            <span className="badge">{members.length}</span>
          </div>
          <div className="dm-proplist">
            {members.map(({ t, p }) => (
              <button key={p.Id} className="dm-proprow" onClick={() => ctx.select({ kind: "property", id: p.Id, typeId: t.Id })}>
                <PropertyIcon propertyType="Relation" />
                <span className="dm-propname">
                  {t.CodeName}.{p.CodeName}
                </span>
                <span className="muted">{p.FromTargetToSource ? "target → source" : "source → target"}{p.IsMany ? ", many" : ""}</span>
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

// ---- a source ----

/** Which source fields apply to which kind; the same rule the settings page uses. */
const sourceFieldVisibility: Record<string, SourceType[]> = {
  Reference: ["AssemblyNameReference", "TypeNameReference", "JsonFile"],
  Namespace: ["AssemblyNameReference", "CSharpCodeFile"],
  Filepath: ["JsonFile", "CSharpCodeFile"],
  FileIO: ["JsonFile"],
  SourceCodePath: ["AssemblyNameReference", "TypeNameReference"],
  AutoDeduceRelations: ["AssemblyNameReference", "TypeNameReference", "CSharpCodeFile"],
};

export function SourceEditor({ source, info, ctx, locked, onDelete }: { source: SourceJson; info: SourceInfo | undefined; ctx: EditorContext; locked: boolean; onDelete: () => void }) {
  const isCode = source.Type === "Code" || source.Id === ctx.codeSourceId;
  const disabled = locked || isCode;
  const color = ctx.colors.get(source.Id) ?? "#888";
  const types = Object.values(ctx.model.NodeTypes).filter((t) => t.DatamodelSourceId === source.Id && t.Id !== ctx.baseTypeId);
  const relations = Object.values(ctx.model.Relations).filter((r) => r.DatamodelSourceId === source.Id);
  const fields = ctx.schema.source.filter((f) => {
    if (f.path === "Type" && isCode) return false;
    const kinds = sourceFieldVisibility[f.path];
    return !kinds || kinds.includes(source.Type);
  });
  const typeField = fields.find((f) => f.path === "Type");
  const choices = typeField?.choices?.filter((c) => c !== "Code") ?? [];
  return (
    <div className="dm-editor">
      <div className="dm-editor-head">
        <SourceIcon type={source.Type} color={color} size={20} />
        <div className="dm-editor-title">
          <div className="dm-editor-name">{source.Name || source.Id}</div>
          <div className="dm-editor-sub">
            {info ? (info.writable ? (info.requiresRebuild ? "Writable, needs a rebuild" : "Writable") : "Read only") : "New source"}
            {" · "}
            {types.length} type{types.length === 1 ? "" : "s"}, {relations.length} relation{relations.length === 1 ? "" : "s"}
          </div>
        </div>
        {!disabled && types.length + relations.length === 0 && (
          <button className="icon-button danger" title="Remove this source" onClick={onDelete}>
            <IconTrash size={16} stroke={1.9} />
          </button>
        )}
      </div>
      {isCode && <ReadOnlyBanner reason="Types registered from application code (OnDatamodelInit). Change the code instead." />}
      {locked && !isCode && <ReadOnlyBanner reason="The source list is set by the configuration overlay." />}
      {info && !info.writable && !isCode && <div className="dm-note">{info.readOnlyReason}</div>}
      {info?.resolvedPath && (
        <div className="dm-note">
          {info.pathExists === false ? "Path does not exist: " : "Path: "}
          <code>{info.resolvedPath}</code>
        </div>
      )}
      <div className="dm-group">
        <div className="dm-group-body">
          {fields.map((f) =>
            f.path === "Type" ? (
              <FieldEditor key={f.path} field={{ ...f, choices }} value={source.Type} disabled={disabled || types.length + relations.length > 0} ctx={ctx} onChange={(v) => ctx.update((m) => setField(m.Sources.find((s) => s.Id === source.Id) as unknown as Record<string, unknown>, "Type", v))} />
            ) : (
              <FieldEditor key={f.path} field={f} value={(source as Record<string, unknown>)[f.path]} disabled={disabled} ctx={ctx} onChange={(v) => ctx.update((m) => setField(m.Sources.find((s) => s.Id === source.Id) as unknown as Record<string, unknown>, f.path, v))} />
            ),
          )}
        </div>
      </div>
      {info && info.files.length > 0 && (
        <div className="dm-group">
          <div className="dm-group-head static">
            <span>Files</span>
            <span className="badge">{info.files.length}</span>
          </div>
          <div className="dm-proplist">
            {info.files.map((f) => (
              <div key={f} className="dm-proprow static">
                <span className="dm-propname">{f}</span>
              </div>
            ))}
          </div>
        </div>
      )}
      {types.length > 0 && (
        <div className="dm-group">
          <div className="dm-group-head static">
            <span>Types</span>
            <span className="badge">{types.length}</span>
          </div>
          <div className="dm-proplist">
            {types
              .sort((a, b) => a.CodeName.localeCompare(b.CodeName))
              .map((t) => (
                <button key={t.Id} className="dm-proprow" onClick={() => ctx.select({ kind: "type", id: t.Id })}>
                  <KindIcon kind={t.ModelType} size={14} />
                  <span className="dm-propname">{t.CodeName}</span>
                  <span className="muted">{t.DatamodelSourceFilename ?? ""}</span>
                </button>
              ))}
          </div>
        </div>
      )}
    </div>
  );
}
