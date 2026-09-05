import { useEffect, useRef, useState } from "react";
import { IconChevronDown, IconChevronRight, IconLoader2, IconPlus, IconRefreshAlert, IconSearch, IconTrash, IconWand, IconX } from "@tabler/icons-react";
import { KindIcon, PropertyIcon, RelationIcon, SourceDot, SourceIcon, relationMeta, sourceKindMeta } from "./DatamodelIcons";
import { Combobox, type ComboOption } from "./Combobox";
import { ColorField } from "./ColorField";
import {
  allProperties,
  fullName,
  hasWildcard,
  newGuid,
  peekAssemblyScan,
  peekNamespaceScan,
  probeTypes,
  relationsOf,
  scanAssemblies,
  scanNamespaces,
  sourceColors,
  type AssemblyScan,
  type FieldDef,
  type ModelJson,
  type NamespaceScan,
  type NodeTypeJson,
  type PropertyJson,
  type RelationJson,
  type Schema,
  type SourceInfo,
  type SourceJson,
  type TypeProbe,
  isCSharpFiles,
  isJsonFiles,
} from "../server/datamodel";
import type { ModelKind, RelationKind } from "../server/datamodel";

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

/**
 * What the editor panel shows. focusField names a field of the thing selected that takes the
 * keyboard as soon as the editor is on screen - set when something has just been created, so its
 * placeholder name can be typed over without reaching for the mouse. It is cleared once used.
 */
export type Selection =
  | { kind: "type"; id: string; focusField?: string }
  | { kind: "property"; id: string; typeId: string; focusField?: string }
  | { kind: "relation"; id: string; focusField?: string }
  | { kind: "source"; id: string; focusField?: string };

// ---- one field ----

interface FieldProps {
  field: FieldDef;
  value: unknown;
  onChange: (value: unknown) => void;
  disabled: boolean;
  ctx: EditorContext;
  /** for propertyRef: the type whose properties (and inherited ones) are offered */
  typeId?: string;
  /** for color: the colour in force while the field is empty, so the swatch shows what unset means */
  fallbackColor?: string;
  /** the field takes the keyboard as soon as it is rendered (a just added type's name) */
  autoFocus?: boolean;
  /** called once the field has taken the keyboard, so the caller can clear the flag */
  onFocused?: () => void;
}

/**
 * Renders one schema field. The value lives in the model object under field.path; an unset value
 * shows the default the server read off a fresh model object, which is what the engine will use.
 */
export function FieldEditor({ field, value, onChange, disabled, ctx, typeId, fallbackColor, autoFocus, onFocused }: FieldProps) {
  const focusRef = useRef<HTMLInputElement | HTMLTextAreaElement | null>(null);
  // only text fields carry the ref; asking a select or a checkbox for the keyboard would be noise
  useEffect(() => {
    if (!autoFocus) return;
    focusRef.current?.focus();
    focusRef.current?.select();
    onFocused?.();
    // one shot: the caller clears the flag, and a later render must not take the keyboard again
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [autoFocus]);
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
    case "color":
      control = <ColorField value={typeof current === "string" ? current : null} fallback={fallbackColor} disabled={off} onChange={(v) => onChange(v)} />;
      break;
    case "textarea":
      control = (
        <textarea
          ref={(el) => {
            focusRef.current = el;
          }}
          className="text-input dm-wide"
          rows={3}
          value={String(current ?? "")}
          disabled={off}
          onChange={(e) => onChange(e.target.value)}
        />
      );
      break;
    default:
      control = (
        <input
          ref={(el) => {
            focusRef.current = el;
          }}
          className="text-input dm-wide"
          type="text"
          value={current === null || current === undefined ? "" : String(current)}
          disabled={off}
          onChange={(e) => onChange(e.target.value === "" && field.optional ? null : e.target.value)}
        />
      );
  }
  return (
    <FieldFrame label={field.label} help={field.help} isDefault={isDefault} canReset={!off} onReset={() => onChange(undefined)}>
      {control}
    </FieldFrame>
  );
}

/** The chrome around one field: label, the back-to-default button, the control, the help line. */
function FieldFrame({ label, help, isDefault, canReset, onReset, children }: { label: string; help: string; isDefault: boolean; canReset: boolean; onReset: () => void; children: React.ReactNode }) {
  return (
    <div className={"dm-field" + (isDefault ? "" : " changed")} title={help}>
      <div className="dm-field-label">
        <span>{label}</span>
        {!isDefault && canReset && (
          <button className="dm-reset" title="Back to the default" onClick={onReset}>
            <IconX size={11} stroke={2} />
          </button>
        )}
      </div>
      <div className="dm-field-control">{children}</div>
      {help && <div className="dm-field-help">{help}</div>}
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

function Groups({ fields, target, onChange, disabled, ctx, typeId, open, focusPath, onFocused }: { fields: FieldDef[]; target: Record<string, unknown>; onChange: (path: string, value: unknown) => void; disabled: boolean; ctx: EditorContext; typeId?: string; open: string[]; focusPath?: string | null; onFocused?: () => void }) {
  const [collapsed, setCollapsed] = useState<Set<string>>(() => new Set(ctx.schema.groups.filter((g) => !open.includes(g))));
  const groups = ctx.schema.groups.filter((g) => fields.some((f) => f.group === g));
  // a field that is to be focused has to be on screen first: its group opens, collapsed or not
  const focusGroup = focusPath ? fields.find((f) => f.path === focusPath)?.group : undefined;
  useEffect(() => {
    if (!focusGroup) return;
    setCollapsed((prev) => {
      if (!prev.has(focusGroup)) return prev;
      const next = new Set(prev);
      next.delete(focusGroup);
      return next;
    });
  }, [focusGroup]);
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
                    <FieldEditor key={f.path} field={f} value={target[f.path]} onChange={(v) => onChange(f.path, v)} disabled={disabled} ctx={ctx} typeId={typeId} autoFocus={f.path === focusPath} onFocused={onFocused} />
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

// ---- the two model dialogs ----

/** Escape closes a dialog wherever the keyboard happens to be, not only inside it. */
function useEscape(onClose: () => void) {
  const latest = useRef(onClose);
  latest.current = onClose;
  useEffect(() => {
    const close = (e: KeyboardEvent) => {
      if (e.key === "Escape") latest.current();
    };
    window.addEventListener("keydown", close);
    return () => window.removeEventListener("keydown", close);
  }, []);
}

// ---- picking the source a new type or relation goes into ----

/**
 * The modal in front of adding a type or a relation when more than one source could hold it. The
 * choice decides which file the new thing is written into, and whether the application has to be
 * rebuilt for it, so the sources are shown as the same panels the Sources view shows - same color,
 * same icon, same facts - rather than as a list of names.
 */
export function SourcePickerDialog({ what, sources, ctx, onPick, onClose }: { what: "type" | "relation"; sources: SourceJson[]; ctx: EditorContext; onPick: (source: SourceJson) => void; onClose: () => void }) {
  useEscape(onClose);
  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="dialog dm-source-pick">
        <h3>Which source holds the new {what}?</h3>
        <div className="dialog-body">The {what} is written into the source you pick when the draft is activated.</div>
        <div className="dm-source-cards dm-source-pick-list">
          {sources.map((s) => {
            const info = ctx.sources.find((x) => x.id === s.Id);
            const color = ctx.colors.get(s.Id) ?? "#888";
            const types = Object.values(ctx.model.NodeTypes).filter((t) => t.DatamodelSourceId === s.Id && t.Id !== ctx.baseTypeId).length;
            const relations = Object.values(ctx.model.Relations).filter((r) => r.DatamodelSourceId === s.Id).length;
            const path = info?.resolvedPath ?? s.Filepath ?? s.SourceCodePath;
            return (
              <button key={s.Id} type="button" className="dm-source-card" style={{ borderLeftColor: color }} onClick={() => onPick(s)}>
                <div className="dm-source-card-head">
                  <SourceIcon type={s.Type} fileFormat={s.FileFormat} color={color} size={20} />
                  <div className="dm-source-card-title">
                    <div className="dm-source-card-name">{s.Name || s.Id}</div>
                    <div className="muted">{sourceKindMeta(s.Type, s.FileFormat).label}</div>
                  </div>
                </div>
                <div className="dm-source-card-body">
                  <div className="dm-source-badges">
                    {info?.requiresRebuild ? (
                      <span className="badge dm-badge-warn">
                        <IconRefreshAlert size={11} stroke={2} /> writable, needs rebuild
                      </span>
                    ) : (
                      <span className="badge dm-badge-ok">writable</span>
                    )}
                    {info && !info.inSettings && <span className="badge dm-badge-warn">not in settings</span>}
                    {!info && <span className="badge dm-badge-new">new</span>}
                    {types + relations === 0 && <span className="badge">empty</span>}
                  </div>
                  <div className="dm-source-facts">
                    {s.Namespace && (
                      <div>
                        <span className="fact-k">Namespace</span> {s.Namespace}
                      </div>
                    )}
                    {path && (
                      <div className={info?.pathExists === false ? "dm-missing" : ""}>
                        <span className="fact-k">{s.Type === "TypeReference" ? "Generated code" : "Path"}</span> {path}
                        {info?.pathExists === false ? " (missing)" : ""}
                      </div>
                    )}
                    <div>
                      <span className="fact-k">Holds</span> {types} type{types === 1 ? "" : "s"}, {relations} relation{relations === 1 ? "" : "s"}
                    </div>
                  </div>
                </div>
              </button>
            );
          })}
        </div>
        <div className="dialog-row">
          <div className="header-spacer" />
          <button className="action-button" onClick={onClose}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}

// ---- picking what a new property holds ----

/** The order the groups of property types are offered in; anything the server adds later follows. */
const propertyTypeGroups = ["Values", "Collections", "Links", "Special"];

/**
 * The modal behind "Add property": every property type the server offers, grouped, each with the
 * sentence explaining what it holds. The kind cannot be changed once the property exists (the model
 * stores a different class per kind), so this is the one place the choice is made, and it is made
 * with the explanations in view rather than from a list of bare names.
 */
function PropertyTypeDialog({ typeName, schema, onPick, onClose }: { typeName: string; schema: Schema; onPick: (propertyType: string) => void; onClose: () => void }) {
  const [query, setQuery] = useState("");
  useEscape(onClose);
  const q = query.trim().toLowerCase();
  const matching = schema.propertyTypes.filter((p) => !q || p.label.toLowerCase().includes(q) || p.value.toLowerCase().includes(q) || p.help.toLowerCase().includes(q));
  const groups = [...propertyTypeGroups, ...schema.propertyTypes.map((p) => p.group).filter((g) => !propertyTypeGroups.includes(g))].filter((g, i, all) => all.indexOf(g) === i).filter((g) => matching.some((p) => p.group === g));
  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      {/* Enter on the filter takes the one type still standing, so a kind can be added by typing alone */}
      <div className="dialog dm-ptype-dialog" onKeyDown={(e) => e.key === "Enter" && matching.length === 1 && onPick(matching[0].value)}>
        <h3>Add a property to {typeName}</h3>
        <div className="dialog-body">What the property holds. This cannot be changed afterwards: a property of another kind is a different property.</div>
        <div className="dm-ptype-search">
          <IconSearch size={15} stroke={2} />
          <input className="dm-search-input" autoFocus placeholder="Filter by name or description…" value={query} onChange={(e) => setQuery(e.target.value)} spellCheck={false} />
          {query && (
            <button className="icon-button" onClick={() => setQuery("")} title="Clear">
              <IconX size={13} stroke={2} />
            </button>
          )}
        </div>
        <div className="dm-ptype-list">
          {groups.map((g) => (
            <div key={g} className="dm-ptype-group">
              <div className="dm-ptype-group-label">{g}</div>
              {matching
                .filter((p) => p.group === g)
                .map((p) => (
                  <button key={p.value} className="dm-ptype" onClick={() => onPick(p.value)}>
                    <span className="dm-ptype-icon">
                      <PropertyIcon propertyType={p.value} size={16} />
                    </span>
                    <span className="dm-ptype-label">{p.label}</span>
                    <span className="dm-ptype-help">{p.help}</span>
                  </button>
                ))}
            </div>
          ))}
          {groups.length === 0 && <div className="muted dm-empty">No property type matches “{query}”.</div>}
        </div>
        <div className="dialog-row">
          <div className="header-spacer" />
          <button className="action-button" onClick={onClose}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}

// ---- a node type ----

export function TypeEditor({ type, ctx, onDelete, focusField, onFocused }: { type: NodeTypeJson; ctx: EditorContext; onDelete: () => void; focusField?: string | null; onFocused?: () => void }) {
  const writable = ctx.writableSource(type.DatamodelSourceId);
  const color = ctx.colors.get(type.DatamodelSourceId) ?? "#888";
  const source = ctx.sources.find((s) => s.id === type.DatamodelSourceId);
  const own = Object.values(type.Properties);
  const inherited = allProperties(ctx.model, type.Id, ctx.baseTypeId).filter((p) => p.inherited);
  const relations = relationsOf(ctx.model, type.Id, ctx.baseTypeId);
  const children = Object.values(ctx.model.NodeTypes).filter((t) => (t.Parents ?? []).includes(type.Id));
  const count = ctx.typeCounts[type.Id];
  const [picking, setPicking] = useState(false);
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
    setPicking(false);
    ctx.select({ kind: "property", id, typeId: type.Id, focusField: "CodeName" });
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
      {writable && (
        <div className="dm-editor-actions">
          <button className="action-button dm-button" onClick={() => setPicking(true)} title="Add a property to this type">
            <IconPlus size={15} stroke={2} /> Add property
          </button>
        </div>
      )}
      {picking && <PropertyTypeDialog typeName={type.CodeName} schema={ctx.schema} onPick={addProperty} onClose={() => setPicking(false)} />}
      {!writable && <ReadOnlyBanner reason={ctx.readOnlyReason(type.DatamodelSourceId)} />}
      <Groups
        fields={ctx.schema.nodeType}
        target={type as unknown as Record<string, unknown>}
        disabled={!writable}
        ctx={ctx}
        open={["General", "Text search"]}
        focusPath={focusField}
        onFocused={onFocused}
        onChange={(path, value) => ctx.update((m) => setField(m.NodeTypes[type.Id] as unknown as Record<string, unknown>, path, value))}
      />
      <div className="dm-group">
        <div className="dm-group-head static">
          <span>Properties</span>
          <span className="badge">{own.length}</span>
          {writable && (
            <button className="link-button" onClick={() => setPicking(true)}>
              <IconPlus size={12} stroke={2} /> add
            </button>
          )}
        </div>
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

export function PropertyEditor({ type, property, ctx, onDelete, focusField, onFocused }: { type: NodeTypeJson; property: PropertyJson; ctx: EditorContext; onDelete: () => void; focusField?: string | null; onFocused?: () => void }) {
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
        focusPath={focusField}
        onFocused={onFocused}
        onChange={(path, value) => ctx.update((m) => setField(m.NodeTypes[type.Id].Properties[property.Id] as unknown as Record<string, unknown>, path, value))}
      />
    </div>
  );
}

// ---- a relation ----

export function RelationEditor({ relation, ctx, onDelete, focusField, onFocused }: { relation: RelationJson; ctx: EditorContext; onDelete: () => void; focusField?: string | null; onFocused?: () => void }) {
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
        focusPath={focusField}
        onFocused={onFocused}
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

/** Which source fields apply to which kind (and, for text files, which format); the same rule the settings page uses. */
const sourceFieldVisibility: Record<string, (s: SourceJson) => boolean> = {
  FileFormat: (s) => s.Type === "TextFiles",
  Reference: (s) => s.Type === "TypeReference" || isJsonFiles(s),
  Namespace: (s) => s.Type === "TypeReference" || isCSharpFiles(s),
  Filepath: (s) => s.Type === "TextFiles",
  FileIO: (s) => isJsonFiles(s),
  GenerateModelFile: (s) => s.Type === "TypeReference",
  // the folder only means something when code is generated into it
  SourceCodePath: (s) => s.Type === "TypeReference" && !!s.GenerateModelFile,
};

export function SourceEditor({ source, info, ctx, locked, onDelete }: { source: SourceJson; info: SourceInfo | undefined; ctx: EditorContext; locked: boolean; onDelete: () => void }) {
  const isCode = source.Type === "Code" || source.Id === ctx.codeSourceId;
  const compiled = source.Type === "TypeReference";
  const disabled = locked || isCode;
  const color = ctx.colors.get(source.Id) ?? "#888";
  // what the source would be marked with if it named no colour: the same palette pick every page
  // makes from its position, so the field shows what clearing it goes back to
  const autoColor = sourceColors(ctx.model.Sources.map((s) => ({ id: s.Id, type: s.Type })), ctx.codeSourceId).get(source.Id) ?? "#888";
  const types = Object.values(ctx.model.NodeTypes).filter((t) => t.DatamodelSourceId === source.Id && t.Id !== ctx.baseTypeId);
  const relations = Object.values(ctx.model.Relations).filter((r) => r.DatamodelSourceId === source.Id);
  const fields = ctx.schema.source.filter((f) => {
    if (f.path === "Type" && isCode) return false;
    // the code source is always grey: it is not one of the configured sources, so there is nowhere to keep a colour for it
    if (f.path === "Color" && isCode) return false;
    const applies = sourceFieldVisibility[f.path];
    return !applies || applies(source);
  });
  const typeField = fields.find((f) => f.path === "Type");
  const choices = typeField?.choices?.filter((c) => c !== "Code") ?? [];
  function set(path: string, value: unknown) {
    ctx.update((m) => setField(m.Sources.find((s) => s.Id === source.Id) as unknown as Record<string, unknown>, path, value));
  }
  return (
    <div className="dm-editor">
      <div className="dm-editor-head">
        <SourceIcon type={source.Type} fileFormat={source.FileFormat} color={color} size={20} />
        <div className="dm-editor-title">
          <div className="dm-editor-name">{source.Name || source.Id}</div>
          <div className="dm-editor-sub">
            {info ? (info.writable ? (info.requiresRebuild ? (source.GenerateModelFile ? "Generates source code, needs a rebuild" : "Writable, needs a rebuild") : "Writable") : "Read only") : "New source"}
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
          {fields.map((f) => {
            if (f.path === "Type") {
              return <FieldEditor key={f.path} field={{ ...f, choices }} value={source.Type} disabled={disabled || types.length + relations.length > 0} ctx={ctx} onChange={(v) => set("Type", v)} />;
            }
            if (compiled && f.path === "Reference") {
              return <AssemblyField key={f.path} field={f} value={source.Reference ?? null} disabled={disabled} onChange={(v) => set("Reference", v)} />;
            }
            if (compiled && f.path === "Namespace") {
              return <NamespaceField key={f.path} field={f} reference={source.Reference ?? null} value={source.Namespace ?? null} disabled={disabled} onChange={(v) => set("Namespace", v)} />;
            }
            if (f.path === "Reference") {
              // JSON files read through a provider: the same setting names the file
              return <FieldEditor key={f.path} field={{ ...f, label: "File name", help: "The model file to read from the storage provider. Only used when a provider is set." }} value={source.Reference} disabled={disabled} ctx={ctx} onChange={(v) => set("Reference", v)} />;
            }
            if (f.path === "GenerateModelFile") {
              // the folder goes with the box: unchecked, there is nothing for the folder to mean
              return (
                <FieldEditor
                  key={f.path}
                  field={f}
                  value={source.GenerateModelFile}
                  disabled={disabled}
                  ctx={ctx}
                  onChange={(v) =>
                    ctx.update((m) => {
                      const s = m.Sources.find((x) => x.Id === source.Id) as unknown as Record<string, unknown>;
                      setField(s, "GenerateModelFile", v);
                      // only when there is something to clear: a source that never had a folder stays byte for byte as it was
                      if (v !== true && s.SourceCodePath) setField(s, "SourceCodePath", null);
                    })
                  }
                />
              );
            }
            return (
              <FieldEditor
                key={f.path}
                field={f}
                value={(source as Record<string, unknown>)[f.path]}
                disabled={disabled}
                ctx={ctx}
                fallbackColor={autoColor}
                onChange={(v) => set(f.path, v)}
              />
            );
          })}
        </div>
      </div>
      {compiled && !isCode && <FoundTypes reference={source.Reference ?? null} namespace={source.Namespace ?? null} sourceId={source.Id} ctx={ctx} />}
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

function errorText(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

/**
 * The assembly of a type reference. Empty means the current project - the assembly the application
 * runs as - and is the first choice; the rest of the list is what a scan of the running application
 * finds, which is asked for rather than run on opening, since reflecting over every loaded assembly
 * takes a moment. Anything can be typed: a name the scan does not know is used as typed.
 */
function AssemblyField({ field, value, disabled, onChange }: { field: FieldDef; value: string | null; disabled: boolean; onChange: (v: string | null) => void }) {
  const [scan, setScan] = useState<AssemblyScan | null>(() => peekAssemblyScan());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  function run(fresh: boolean) {
    setBusy(true);
    setError(null);
    scanAssemblies(fresh).then(
      (r) => {
        setScan(r);
        setBusy(false);
      },
      (e) => {
        setError(errorText(e));
        setBusy(false);
      },
    );
  }
  const options: ComboOption[] = [
    { value: null, label: "Current project", hint: scan?.current ? scan.current : "the assembly the application runs as" },
    ...(scan?.assemblies ?? []).map((a) => ({ value: a.name, label: a.name, hint: a.loaded ? undefined : "not loaded yet" })),
  ];
  const status = error ?? (scan ? `${scan.assemblies.length} other assembl${scan.assemblies.length === 1 ? "y" : "ies"} the application can see.` : "Type an assembly name, or scan the running application for the assemblies it can see.");
  return (
    <FieldFrame label="Assembly" help={field.help} isDefault={value === null} canReset={!disabled} onReset={() => onChange(null)}>
      <Combobox
        value={value}
        onChange={onChange}
        options={options}
        disabled={disabled}
        placeholder="Current project"
        action={{ label: scan ? "Scan again" : "Scan for assemblies", run: () => run(!!scan), busy, done: !!scan }}
        status={status}
        statusKind={error ? "error" : "info"}
      />
    </FieldFrame>
  );
}

/**
 * The namespace of a type reference: typed, with * as a wildcard, or picked from the namespaces a scan
 * of the chosen assembly finds. A namespace that has namespaces under it is offered a second time as
 * "Name.*", which takes the lot.
 */
function NamespaceField({ field, reference, value, disabled, onChange }: { field: FieldDef; reference: string | null; value: string | null; disabled: boolean; onChange: (v: string | null) => void }) {
  const [scan, setScan] = useState<NamespaceScan | null>(() => peekNamespaceScan(reference));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    // another assembly, another list
    setScan(peekNamespaceScan(reference));
    setError(null);
  }, [reference]);
  function run(fresh: boolean) {
    setBusy(true);
    setError(null);
    scanNamespaces(reference, fresh).then(
      (r) => {
        setScan(r);
        setBusy(false);
      },
      (e) => {
        setError(errorText(e));
        setBusy(false);
      },
    );
  }
  const options: ComboOption[] = [];
  if (scan) {
    const names = scan.namespaces.map((n) => n.name);
    for (const n of scan.namespaces) {
      options.push({ value: n.name, label: n.name, hint: `${n.types} type${n.types === 1 ? "" : "s"}` });
      const under = names.filter((x) => x.startsWith(n.name + ".")).length;
      if (under > 0) options.push({ value: n.name + ".*", label: n.name + ".*", hint: `and ${under} namespace${under === 1 ? "" : "s"} under it` });
    }
  }
  const status =
    error ??
    (scan
      ? `${scan.namespaces.length} namespace${scan.namespaces.length === 1 ? "" : "s"} in ${scan.assembly}. * stands for any run of characters.`
      : "Type a namespace, or scan the assembly for the namespaces it holds. * stands for any run of characters: MyApp.Models.* takes MyApp.Models and everything under it.");
  return (
    <FieldFrame label={field.label} help={field.help} isDefault={value === null} canReset={!disabled} onReset={() => onChange(null)}>
      <Combobox
        value={value}
        onChange={onChange}
        options={options}
        disabled={disabled}
        placeholder="MyApp.Models"
        action={{ label: scan ? "Scan again" : "Scan for namespaces", run: () => run(!!scan), busy, done: !!scan }}
        status={status}
        statusKind={error ? "error" : "info"}
      />
    </FieldFrame>
  );
}

/**
 * What the assembly and namespace above would load, read from the running application a moment after
 * they change: the node types and relations, with the ones a namespace pulls in from elsewhere (a base
 * class, a related type) marked as such, and the ones the model already holds under another source.
 */
function FoundTypes({ reference, namespace, sourceId, ctx }: { reference: string | null; namespace: string | null; sourceId: string; ctx: EditorContext }) {
  const [probe, setProbe] = useState<TypeProbe | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    if (!namespace) {
      setProbe(null);
      setError(null);
      setBusy(false);
      return;
    }
    let cancelled = false;
    setBusy(true);
    const timer = setTimeout(() => {
      probeTypes(reference, namespace).then(
        (r) => {
          if (cancelled) return;
          setProbe(r);
          setError(null);
          setBusy(false);
        },
        (e) => {
          if (cancelled) return;
          setProbe(null);
          setError(errorText(e));
          setBusy(false);
        },
      );
    }, 400);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [reference, namespace]);
  if (!namespace) return null;
  const byDirect = <T extends { direct: boolean }>(a: T, b: T) => (a.direct === b.direct ? 0 : a.direct ? -1 : 1);
  const nodeTypes = probe ? [...probe.nodeTypes].sort(byDirect) : [];
  const relations = probe ? [...probe.relations].sort(byDirect) : [];
  const elsewhere = (id: string): string | null => {
    const t = ctx.model.NodeTypes[id] ?? ctx.model.Relations[id];
    if (!t || t.DatamodelSourceId === sourceId) return null;
    return ctx.sources.find((s) => s.id === t.DatamodelSourceId)?.name ?? "another source";
  };
  // the namespace has its own column, so the tags only say what is special about the row
  const tags = (detail: string | null, direct: boolean, other: string | null) => [detail, !direct ? "referenced" : null, other ? "already in " + other : null].filter(Boolean).join(" · ");
  return (
    <div className="dm-group">
      <div className="dm-group-head static">
        <span className="dm-found-head">
          Found in {probe?.assembly ?? (reference || "the current project")}
          {busy && <IconLoader2 size={13} stroke={2} className="spin" />}
        </span>
        {probe && <span className="badge">{nodeTypes.length + relations.length}</span>}
      </div>
      <div className="dm-group-body">
        {(error || probe?.error) && <div className="dm-note dm-found-error">{error ?? probe?.error}</div>}
        {probe && !probe.error && nodeTypes.length + relations.length === 0 && (
          <div className="dm-note">
            No model types in {hasWildcard(namespace) ? "namespaces matching " : ""}
            {namespace}. The source loads empty until a type is written into it.
          </div>
        )}
        {probe && nodeTypes.length + relations.length > 0 && <div className="dm-note">What a database opened with this source loads. A type marked referenced sits in another namespace and comes along because a type here points at it.</div>}
      </div>
      {nodeTypes.length > 0 && (
        <div className="dm-proplist">
          {nodeTypes.map((t) => (
            <div key={t.id} className="dm-proprow static" title={(t.namespace ? t.namespace + "." : "") + t.codeName}>
              <KindIcon kind={t.kind as ModelKind} size={14} />
              <span className="dm-propname">{t.codeName}</span>
              <span className="muted">{t.namespace ?? "(no namespace)"}</span>
              <span className="dm-tag">{tags(t.properties ? `${t.properties} propert${t.properties === 1 ? "y" : "ies"}` : null, t.direct, elsewhere(t.id))}</span>
            </div>
          ))}
        </div>
      )}
      {relations.length > 0 && (
        <div className="dm-proplist">
          {relations.map((r) => (
            <div key={r.id} className="dm-proprow static" title={(r.namespace ? r.namespace + "." : "") + r.codeName}>
              <RelationIcon kind={r.kind as RelationKind} size={14} />
              <span className="dm-propname">{r.codeName}</span>
              <span className="muted">{r.namespace ?? "(no namespace)"}</span>
              <span className="dm-tag">{tags(relationMeta[r.kind as RelationKind]?.label ?? r.kind, r.direct, elsewhere(r.id))}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
