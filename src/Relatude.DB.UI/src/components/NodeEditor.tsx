import { useCallback, useEffect, useMemo, useState } from "react";
import { IconArrowBackUp, IconDeviceFloppy, IconPlus, IconRefresh, IconSearch, IconX } from "@tabler/icons-react";
import {
  fetchNode,
  lookupNodes,
  saveNode,
  type GeoValue,
  type FileValueView,
  type InnerNodeView,
  type NodeRef,
  type NodeView,
  type PropertyView,
} from "../server/query";
import { showError } from "../dialogs";
import { useLiveResult } from "../server/hooks";
import { formatCount, formatTime } from "../format";
import { FilePreview } from "./MediaPreview";
import { NodeMetaTab } from "./NodeMetaTab";
import { NodeHistoryTab } from "./NodeHistoryTab";

type EditorTab = "properties" | "meta" | "history";

/**
 * One node as a form, built from the data model rather than from a class: every property of the
 * node's type gets the editor its property type calls for.
 *
 * Only what was touched is sent. `values` holds the changed properties keyed by property id and
 * `relations` the changed relation lists, so a save writes exactly the fields someone edited -
 * which also means two people editing different fields of the same node do not overwrite each
 * other. Reverting a field is dropping it from those maps, not writing the old value back.
 *
 * Some property types are shown but not editable, because a text field is the wrong way to change
 * them: a file (the files section owns uploads), a stored vector, a byte array, and the inner
 * nodes of an embedded property, which are a document of their own inside the node.
 *
 * Three tabs: the properties (this form), the meta (access, publishing window, revision - see
 * NodeMetaTab) and the history (the older versions in the transaction log - see NodeHistoryTab).
 * The head, and its Save, belong to the properties; the meta tab saves on its own, since its edits
 * are a different write to the store.
 */
export function NodeEditor({
  storeId,
  nodeId,
  onSaved,
  onClose,
}: {
  storeId: string;
  nodeId: string;
  onSaved?: () => void;
  onClose?: () => void;
}) {
  const [node, setNode] = useState<NodeView | null>(null);
  const [values, setValues] = useState<Record<string, unknown>>({});
  // the edited node lists of reference, references and relation properties. They are kept as whole
  // node refs rather than as ids because a picker has to keep showing the name of what was picked;
  // which of the two payloads they end up in is decided at save time, by the property.
  const [targets, setTargets] = useState<Record<string, NodeRef[]>>({});
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState<string | null>(null);
  const [tab, setTab] = useState<EditorTab>("properties");

  const load = useCallback(() => {
    setNode(null);
    fetchNode(storeId, nodeId)
      .then((n) => {
        setNode(n);
        setValues({});
        setTargets({});
        setError(null);
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, [storeId, nodeId]);
  useEffect(load, [load]);

  const dirty = Object.keys(values).length + Object.keys(targets).length;

  function setValue(property: PropertyView, value: unknown) {
    setSaved(null);
    setValues((prev) => ({ ...prev, [property.id]: value }));
  }
  function setTargetList(property: PropertyView, list: NodeRef[]) {
    setSaved(null);
    setTargets((prev) => ({ ...prev, [property.id]: list }));
  }
  function revert(property: PropertyView) {
    setSaved(null);
    setValues((prev) => {
      const next = { ...prev };
      delete next[property.id];
      return next;
    });
    setTargets((prev) => {
      const next = { ...prev };
      delete next[property.id];
      return next;
    });
  }

  async function save() {
    if (!node || dirty === 0) return;
    setSaving(true);
    try {
      const editedValues = { ...values };
      const relations: Record<string, string[]> = {};
      for (const [propertyId, list] of Object.entries(targets)) {
        const ids = list.map((t) => t.id);
        const editor = node.properties.find((p) => p.id === propertyId)?.editor;
        // a relation is an edge and is saved as one; a reference is an ordinary property value
        if (editor === "relation") relations[propertyId] = ids;
        else if (editor === "references") editedValues[propertyId] = ids;
        else editedValues[propertyId] = ids[0] ?? null;
      }
      const result = await saveNode(storeId, node.id, editedValues, relations);
      setSaved(result.changed === 0 ? "Nothing changed." : `Saved ${formatCount(result.changed)} ${result.changed === 1 ? "change" : "changes"}.`);
      load();
      onSaved?.();
    } catch (e) {
      await showError("Could not save", e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }

  if (error) return <div className="placeholder">{error}</div>;
  if (!node) return null;

  return (
    <div className="node-editor">
      <div className="node-editor-head">
        <div className="node-editor-title">
          <h3>{node.displayName}</h3>
          <span className="muted">
            {node.typeName} · id {node.id} · #{node.intId}
            {node.address ? " · " + node.address : ""}
          </span>
          <span className="muted">
            created {formatTime(node.createdUtc)} · changed {formatTime(node.changedUtc)}
          </span>
        </div>
        <div className="query-spacer" />
        {tab === "properties" && (
          <>
            {saved && <span className="muted">{saved}</span>}
            <button className="action-button" onClick={load} disabled={saving} title="Read the node again, dropping unsaved edits">
              <IconRefresh size={15} stroke={1.8} />
              Reload
            </button>
            <button className="action-button primary" onClick={save} disabled={dirty === 0 || saving}>
              <IconDeviceFloppy size={15} stroke={1.8} />
              {dirty === 0 ? "Save" : `Save ${dirty} ${dirty === 1 ? "field" : "fields"}`}
            </button>
          </>
        )}
        {onClose && (
          <button className="icon-button" title="Close" onClick={onClose}>
            <IconX size={16} stroke={1.8} />
          </button>
        )}
      </div>
      <div className="tabs" role="tablist">
        <button className={"tab" + (tab === "properties" ? " active" : "")} role="tab" onClick={() => setTab("properties")}>
          Properties
          {dirty > 0 && <span className="tab-dot" title={`${dirty} unsaved`} />}
        </button>
        <button className={"tab" + (tab === "meta" ? " active" : "")} role="tab" onClick={() => setTab("meta")} title="Access, publishing window, revision and culture">
          Meta
        </button>
        <button className={"tab" + (tab === "history" ? " active" : "")} role="tab" onClick={() => setTab("history")} title="Older versions of the node, from the transaction log">
          History
        </button>
      </div>
      {tab === "meta" && (
        <NodeMetaTab
          storeId={storeId}
          nodeId={nodeId}
          onSaved={() => {
            load(); // a meta write is a new version of the node: the head's timestamps move
            onSaved?.();
          }}
        />
      )}
      {tab === "history" && <NodeHistoryTab storeId={storeId} nodeId={nodeId} />}
      <div className="node-fields" hidden={tab !== "properties"}>
        {node.properties.map((property) => (
          <Field
            key={property.id}
            storeId={storeId}
            property={property}
            edited={property.id in values || property.id in targets}
            value={property.id in values ? values[property.id] : property.value}
            targets={targets[property.id] ?? property.targets ?? []}
            onChange={(v) => setValue(property, v)}
            onTargets={(t) => setTargetList(property, t)}
            onRevert={() => revert(property)}
          />
        ))}
      </div>
    </div>
  );
}

function Field({
  storeId,
  property,
  value,
  targets,
  edited,
  onChange,
  onTargets,
  onRevert,
}: {
  storeId: string;
  property: PropertyView;
  value: unknown;
  targets: NodeRef[];
  edited: boolean;
  onChange: (value: unknown) => void;
  onTargets: (targets: NodeRef[]) => void;
  onRevert: () => void;
}) {
  return (
    <div className={"node-field" + (edited ? " edited" : "") + (property.readOnly ? " readonly" : "")}>
      <div className="node-field-label">
        <span className="node-field-name">{property.name}</span>
        <span className="node-field-type">{property.type}</span>
        {edited && <span className="setting-badge unsaved">unsaved</span>}
        {property.notes.map((note) => (
          <span className="setting-badge faint" key={note}>
            {note}
          </span>
        ))}
        {property.declaredBy && <span className="node-field-owner">from {property.declaredBy}</span>}
        {edited && (
          <button className="icon-button" title="Undo this change" onClick={onRevert}>
            <IconArrowBackUp size={14} stroke={1.8} />
          </button>
        )}
      </div>
      <div className="node-field-control">
        <Editor storeId={storeId} property={property} value={value} targets={targets} onChange={onChange} onTargets={onTargets} />
      </div>
    </div>
  );
}

function Editor({
  storeId,
  property,
  value,
  targets,
  onChange,
  onTargets,
}: {
  storeId: string;
  property: PropertyView;
  value: unknown;
  targets: NodeRef[];
  onChange: (value: unknown) => void;
  onTargets: (targets: NodeRef[]) => void;
}) {
  // a fresh array every render would look like a new lookup to the picker
  const typeIds = useMemo(() => (property.targetTypes ?? []).map((t) => t.id), [property.targetTypes]);
  switch (property.editor) {
    case "bool":
      return (
        <label className="setting-toggle">
          <input type="checkbox" checked={value === true} onChange={(e) => onChange(e.target.checked)} />
          <span>{value === true ? "True" : "False"}</span>
        </label>
      );
    case "enum":
      return (
        <select className="select" value={String(value ?? 0)} onChange={(e) => onChange(Number(e.target.value))}>
          {(property.options ?? []).some((o) => String(o.value) === String(value ?? 0)) ? null : <option value={String(value ?? 0)}>{String(value ?? 0)}</option>}
          {(property.options ?? []).map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
      );
    case "enumList": {
      const selected = Array.isArray(value) ? (value as number[]) : [];
      return (
        <div className="node-checks">
          {(property.options ?? []).map((o) => (
            <label className="node-check" key={o.value}>
              <input
                type="checkbox"
                checked={selected.includes(o.value)}
                onChange={(e) => onChange(e.target.checked ? [...selected, o.value] : selected.filter((v) => v !== o.value))}
              />
              <span>{o.label}</span>
            </label>
          ))}
          {(property.options ?? []).length === 0 && <span className="muted">No members declared for this enum.</span>}
        </div>
      );
    }
    case "integer":
    case "number":
      return (
        <input
          className="text-input number"
          type="number"
          step={property.editor === "integer" ? 1 : "any"}
          min={property.min ?? undefined}
          max={property.max ?? undefined}
          value={value === null || value === undefined ? "" : String(value)}
          onChange={(e) => onChange(e.target.value)}
        />
      );
    case "code":
      return (
        <textarea
          className="text-input code"
          rows={12}
          spellCheck={false}
          value={String(value ?? "")}
          placeholder={property.language ?? undefined}
          onChange={(e) => onChange(e.target.value)}
        />
      );
    case "text":
      return property.multiline ? (
        <textarea className="text-input" rows={6} value={String(value ?? "")} maxLength={property.maxLength ?? undefined} onChange={(e) => onChange(e.target.value)} />
      ) : (
        <input
          className="text-input wide"
          value={String(value ?? "")}
          maxLength={property.maxLength ?? undefined}
          spellCheck={false}
          onChange={(e) => onChange(e.target.value)}
        />
      );
    case "guid":
      return <input className="text-input wide mono" value={String(value ?? "")} spellCheck={false} placeholder="00000000-0000-0000-0000-000000000000" onChange={(e) => onChange(e.target.value)} />;
    case "stringList":
      return <ListEditor values={Array.isArray(value) ? (value as string[]) : []} onChange={onChange} placeholder="value" />;
    case "guidList":
      return <ListEditor values={Array.isArray(value) ? (value as string[]) : []} onChange={onChange} placeholder="00000000-0000-0000-0000-000000000000" mono />;
    case "datetime": {
      // the store keeps UTC, so the field is UTC: the ISO string is cut to what the input wants and
      // put back whole, with no local-time conversion in either direction
      const iso = typeof value === "string" ? value : "";
      return (
        <>
          <input className="text-input" type="datetime-local" value={iso.slice(0, 16)} onChange={(e) => onChange(e.target.value ? e.target.value : null)} />
          <span className="setting-unit">UTC</span>
        </>
      );
    }
    case "datetimeoffset":
      return (
        <>
          <input
            className="text-input wide mono"
            value={typeof value === "string" ? value : ""}
            spellCheck={false}
            placeholder="2026-08-30T12:00:00.0000000+02:00"
            onChange={(e) => onChange(e.target.value ? e.target.value : null)}
          />
          <span className="setting-unit">with offset</span>
        </>
      );
    case "timespan":
      return (
        <>
          <input className="text-input mono" value={String(value ?? "")} spellCheck={false} placeholder="d.hh:mm:ss" onChange={(e) => onChange(e.target.value)} />
          <span className="setting-unit">d.hh:mm:ss</span>
        </>
      );
    case "geo": {
      const geo = (value ?? null) as GeoValue | null;
      const set = (lat: number, lon: number) => onChange({ latitude: lat, longitude: lon });
      return (
        <div className="node-geo">
          <input
            className="text-input number"
            type="number"
            step="any"
            placeholder="latitude"
            value={geo ? geo.latitude : ""}
            onChange={(e) => set(Number(e.target.value), geo?.longitude ?? 0)}
          />
          <input
            className="text-input number"
            type="number"
            step="any"
            placeholder="longitude"
            value={geo ? geo.longitude : ""}
            onChange={(e) => set(geo?.latitude ?? 0, Number(e.target.value))}
          />
          {geo && (
            <button className="icon-button" title="Clear the coordinate" onClick={() => onChange(null)}>
              <IconX size={14} stroke={1.8} />
            </button>
          )}
        </div>
      );
    }
    case "reference":
      return <NodePicker storeId={storeId} typeIds={typeIds} targets={targets} multiple={false} onChange={onTargets} />;
    case "references":
      return <NodePicker storeId={storeId} typeIds={typeIds} targets={targets} multiple onChange={onTargets} />;
    case "relation":
      return <NodePicker storeId={storeId} typeIds={typeIds} targets={targets} multiple={property.isMany === true} onChange={onTargets} />;
    case "file": {
      const file = (value ?? null) as FileValueView | null;
      if (!file) return <span className="muted">No file.</span>;
      return <FilePreview storeId={storeId} file={file} />;
    }
    case "embedded": {
      const inner = Array.isArray(value) ? (value as InnerNodeView[]) : [];
      if (inner.length === 0) return <span className="muted">{property.info ?? "empty"}</span>;
      return (
        <div className="node-inner">
          <span className="muted">{property.info}</span>
          {inner.map((n) => (
            <div className="node-inner-node" key={n.id}>
              <span className="node-inner-type">{n.typeName}</span>
              {n.values.map((v) => (
                <span key={v.codeName}>
                  <em>{v.codeName}</em> {v.file ? <FilePreview storeId={storeId} file={v.file} compact /> : v.value}
                </span>
              ))}
            </div>
          ))}
        </div>
      );
    }
    default:
      return <span className="muted">{property.info ?? "Not editable here."}</span>;
  }
}

/** A repeated scalar: one row per element, in order, with nothing clever about it. */
function ListEditor({ values, onChange, placeholder, mono }: { values: string[]; onChange: (values: string[]) => void; placeholder: string; mono?: boolean }) {
  return (
    <div className="node-list">
      {values.map((v, i) => (
        <div className="node-list-row" key={i}>
          <input
            className={"text-input wide" + (mono ? " mono" : "")}
            value={v}
            placeholder={placeholder}
            spellCheck={false}
            onChange={(e) => onChange(values.map((old, j) => (i === j ? e.target.value : old)))}
          />
          <button className="icon-button" title="Remove" onClick={() => onChange(values.filter((_, j) => j !== i))}>
            <IconX size={14} stroke={1.8} />
          </button>
        </div>
      ))}
      <button className="link-button" onClick={() => onChange([...values, ""])}>
        <IconPlus size={13} stroke={1.8} /> add
      </button>
    </div>
  );
}

/**
 * Picks nodes for a reference or a relation. The search is the same free text search the query page
 * runs, narrowed to the types the property can point at, so finding a node here works the same way
 * as finding one there.
 */
function NodePicker({
  storeId,
  typeIds,
  targets,
  multiple,
  onChange,
}: {
  storeId: string;
  typeIds: string[];
  targets: NodeRef[];
  multiple: boolean;
  onChange: (targets: NodeRef[]) => void;
}) {
  const [open, setOpen] = useState(false);
  const [text, setText] = useState("");
  // nothing to look up until the picker is opened; after that every keystroke runs at once
  const lookup = useMemo(() => (open ? { storeId, typeIds, text } : null), [open, storeId, typeIds, text]);
  const { result, loading: busy, error } = useLiveResult(lookup, (r) => lookupNodes(r.storeId, r.typeIds, r.text));
  const found = result ?? [];

  function add(ref: NodeRef) {
    if (multiple) {
      if (!targets.some((t) => t.id === ref.id)) onChange([...targets, ref]);
    } else {
      onChange([ref]);
      setOpen(false);
    }
  }

  return (
    <div className="node-picker">
      <div className="node-picker-targets">
        {targets.map((t) => (
          <span className="node-chip" key={t.id} title={t.id}>
            {t.name}
            {t.typeName && <em>{t.typeName}</em>}
            <button className="icon-button" title="Remove" onClick={() => onChange(targets.filter((x) => x.id !== t.id))}>
              <IconX size={12} stroke={2} />
            </button>
          </span>
        ))}
        {targets.length === 0 && <span className="muted">none</span>}
        {(multiple || targets.length === 0) && (
          <button className="link-button" onClick={() => setOpen(!open)}>
            <IconPlus size={13} stroke={1.8} /> {open ? "close" : "add"}
          </button>
        )}
        {!multiple && targets.length > 0 && (
          <button className="link-button" onClick={() => setOpen(!open)}>
            {open ? "close" : "change"}
          </button>
        )}
      </div>
      {open && (
        <div className="node-picker-search">
          <div className="query-search">
            <IconSearch size={14} stroke={1.8} />
            <input className="text-input wide" value={text} placeholder="search…" spellCheck={false} autoFocus onChange={(e) => setText(e.target.value)} />
          </div>
          <div className="node-picker-results">
            {error && <span className="query-error">{error}</span>}
            {busy && found.length === 0 && <span className="muted">searching…</span>}
            {!busy && !error && found.length === 0 && <span className="muted">nothing found</span>}
            {found.map((r) => (
              <button className="node-picker-result" key={r.id} onClick={() => add(r)} disabled={targets.some((t) => t.id === r.id)}>
                <span>{r.name}</span>
                <em>{r.typeName}</em>
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
