import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  IconArrowBackUp,
  IconChevronDown,
  IconChevronUp,
  IconDeviceFloppy,
  IconExternalLink,
  IconPlus,
  IconRefresh,
  IconSearch,
  IconTrash,
  IconUpload,
  IconX,
} from "@tabler/icons-react";
import {
  clearNodeFile,
  commitNodeFile,
  createNode,
  deleteNodes,
  fetchNodes,
  fileUploadTarget,
  lookupNodes,
  saveEmbedded,
  saveNodes,
  type EditorKind,
  type GeoValue,
  type FileValueView,
  type InnerNodeView,
  type NodeRef,
  type NodeView,
  type PropertyView,
  type TypeRef,
} from "../server/query";
import { abortUpload, newUploadId, uploadStaged } from "../server/files";
import { notifyNodePicture } from "../nodeMedia";
import { showChoice, showConfirm, showError } from "../dialogs";
import { openInDatamodel } from "../navigate";
import { IndexMarks } from "./DatamodelIcons";
import { useLiveResult } from "../server/hooks";
import { formatBytes, formatCount, formatTime } from "../format";
import { FilePreview } from "./MediaPreview";
import { NodeMetaTab } from "./NodeMetaTab";
import { NodeHistoryTab } from "./NodeHistoryTab";

type EditorTab = "properties" | "meta" | "history";

/** how many of a selection's nodes are named in the head before the rest are a count */
const maxChips = 24;

/** A property every selected node has: the first node's view of it, every node's, and whether they agree on its value. */
interface SharedProperty {
  property: PropertyView;
  all: PropertyView[];
  mixed: boolean;
}

/**
 * One node as a form - or several, edited together - built from the data model rather than from a
 * class: every property of the node's type gets the editor its property type calls for.
 *
 * Only what was touched is sent. `values` holds the changed properties keyed by property id and
 * `relations` the changed relation lists, so a save writes exactly the fields someone edited -
 * which also means two people editing different fields of the same node do not overwrite each
 * other. Reverting a field is dropping it from those maps, not writing the old value back.
 *
 * With several nodes open the form shows the properties they all have, and a field whose value is
 * not the same on every one of them is shown blank and marked as differing rather than showing one
 * node's value as though it were everyone's. Anything entered is written to every selected node in
 * one transaction, so a value one of them cannot take leaves all of them as they were. The other
 * two tabs, the file uploads and the inner nodes are one node's business and step aside while a
 * selection is open.
 *
 * Some property types are shown but not editable, because a text field is the wrong way to change
 * them: a stored vector, a byte array, and the inner nodes of an embedded property, which are a
 * document of their own inside the node. A file is editable but not through Save - see FileField.
 *
 * Three tabs: the properties (this form), the meta (access, publishing window, revision - see
 * NodeMetaTab) and the history (the older versions in the transaction log - see NodeHistoryTab).
 * The head, and its Save, belong to the properties; the meta tab saves on its own, since its edits
 * are a different write to the store.
 */
export function NodeEditor({
  storeId,
  nodeIds,
  onSaved,
  onClose,
  onDeleted,
  onDeselect,
}: {
  storeId: string;
  /** the nodes the form has open: one, or a selection edited together */
  nodeIds: readonly string[];
  onSaved?: () => void;
  onClose?: () => void;
  /** the nodes were deleted from here: the list they came from is stale and the form has nothing to show */
  onDeleted?: () => void;
  /** one node of a selection was taken out of it from the form's own head */
  onDeselect?: (nodeId: string) => void;
}) {
  const [nodes, setNodes] = useState<NodeView[] | null>(null);
  const [values, setValues] = useState<Record<string, unknown>>({});
  // the edited node lists of reference, references and relation properties. They are kept as whole
  // node refs rather than as ids because a picker has to keep showing the name of what was picked;
  // which of the two payloads they end up in is decided at save time, by the property.
  const [targets, setTargets] = useState<Record<string, NodeRef[]>>({});
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState<string | null>(null);
  const [tab, setTab] = useState<EditorTab>("properties");

  // the ids as one value, so a render that hands over the same ids in a new array changes nothing
  const idsKey = nodeIds.join("\n");
  const ids = useMemo(() => idsKey.split("\n").filter((id) => id.length > 0), [idsKey]);
  const multi = ids.length > 1;

  const load = useCallback(() => {
    setNodes(null);
    fetchNodes(storeId, ids)
      .then((list) => {
        if (list.length === 0) throw new Error(ids.length === 1 ? "Node not found." : "None of the selected nodes could be read.");
        setNodes(list);
        setError(null);
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, [storeId, ids]);

  /** Reads the nodes again and drops every unsaved edit: the Reload button, and what follows a write. */
  function reload() {
    setValues({});
    setTargets({});
    load();
  }

  // The selection changed under the form. Edits are kept when it grew or shrank - select several,
  // set a field, add one more, save - and dropped when it is a different selection altogether: an
  // edit made to one node must not turn up unsaved on the next one clicked.
  const previous = useRef<string[]>(ids);
  useEffect(() => {
    const before = previous.current;
    previous.current = ids;
    const grew = before.every((id) => ids.includes(id));
    const shrank = ids.every((id) => before.includes(id));
    if (!grew && !shrank) {
      setValues({});
      setTargets({});
    }
    setSaved(null);
    load();
  }, [ids, load]);

  // the other two tabs are one node's: several nodes have only their properties in common
  useEffect(() => {
    if (multi) setTab("properties");
  }, [multi]);

  /** The properties every selected node has, in the first node's order, and whether the nodes agree on each. */
  const shared = useMemo<{ properties: SharedProperty[]; hidden: number }>(() => {
    if (!nodes || nodes.length === 0) return { properties: [], hidden: 0 };
    const [first, ...rest] = nodes;
    const union = new Set<string>();
    for (const n of nodes) for (const p of n.properties) union.add(p.id);
    const properties: SharedProperty[] = [];
    for (const p of first.properties) {
      const all: PropertyView[] = [p];
      for (const n of rest) {
        const q = n.properties.find((x) => x.id === p.id);
        if (q) all.push(q);
      }
      if (all.length !== nodes.length) continue;
      properties.push({ property: p, all, mixed: all.some((q) => !sameValue(p, q)) });
    }
    return { properties, hidden: union.size - properties.length };
  }, [nodes]);

  // an edit of a property the selection no longer shares has nowhere to go, and is let go of
  useEffect(() => {
    if (!nodes) return;
    const keep = new Set(shared.properties.map((s) => s.property.id));
    setValues((prev) => prune(prev, keep));
    setTargets((prev) => prune(prev, keep));
  }, [nodes, shared]);

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
    if (!nodes || dirty === 0) return;
    setSaving(true);
    try {
      const editedValues = { ...values };
      const relations: Record<string, string[]> = {};
      for (const [propertyId, list] of Object.entries(targets)) {
        const linked = list.map((t) => t.id);
        const editor = nodes[0].properties.find((p) => p.id === propertyId)?.editor;
        // a relation is an edge and is saved as one; a reference is an ordinary property value
        if (editor === "relation") relations[propertyId] = linked;
        else if (editor === "references") editedValues[propertyId] = linked;
        else editedValues[propertyId] = linked[0] ?? null;
      }
      const result = await saveNodes(
        storeId,
        nodes.map((n) => n.id),
        editedValues,
        relations,
      );
      setSaved(
        result.changed === 0
          ? "Nothing changed."
          : `Saved ${formatCount(result.changed)} ${result.changed === 1 ? "change" : "changes"}` + (multi ? ` across ${formatCount(nodes.length)} nodes.` : "."),
      );
      reload();
      onSaved?.();
    } catch (e) {
      await showError("Could not save", e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }

  /**
   * Deleting is asked about first, and says what is being deleted rather than "are you sure": the
   * name and the type are what tell someone whether this is the node they meant - and for a
   * selection, how many of what.
   */
  async function remove() {
    if (!nodes) return;
    const one = nodes.length === 1 ? nodes[0] : null;
    const confirmed = await showConfirm(
      one ? `Delete ${one.displayName || "this node"}?` : `Delete ${formatCount(nodes.length)} nodes?`,
      one
        ? `The ${one.typeName} node is removed from the database. Relations and references to it are cleared with it. This cannot be undone from here - a revert window can take it back.`
        : `${typeSummary(nodes)} are removed from the database, all at once. Relations and references to them are cleared with them. This cannot be undone from here - a revert window can take it back.`,
      { confirmLabel: one ? "Delete" : `Delete ${formatCount(nodes.length)} nodes`, danger: true },
    );
    if (!confirmed.ok) return;
    setSaving(true);
    try {
      await deleteNodes(
        storeId,
        nodes.map((n) => n.id),
      );
      onDeleted?.();
      onClose?.();
    } catch (e) {
      await showError("Could not delete", e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }

  if (error) return <div className="placeholder">{error}</div>;
  if (!nodes) return null;
  const node = nodes[0];
  const missing = ids.length - nodes.length;

  return (
    <div className="node-editor">
      <div className="node-editor-head">
        {multi ? (
          <div className="node-editor-title">
            <h3>{formatCount(nodes.length)} nodes selected</h3>
            <span className="muted">{typeSummary(nodes)} · what is changed here is written to all of them</span>
            {missing > 0 && (
              <span className="muted">
                {formatCount(missing)} of the selected nodes could not be read - {missing === 1 ? "it" : "they"} may have been deleted.
              </span>
            )}
          </div>
        ) : (
          <div className="node-editor-title">
            <h3>{node.displayName}</h3>
            <span className="muted">
              <button className="link-button" title={`Open ${node.fullName} in the data model`} onClick={() => openInDatamodel({ typeId: node.typeId })}>
                {node.typeName}
              </button>{" "}
              · id {node.id} · #{node.intId}
              {node.address ? " · " + node.address : ""}
            </span>
            <span className="muted">
              created {formatTime(node.createdUtc)} · changed {formatTime(node.changedUtc)}
            </span>
          </div>
        )}
        <div className="query-spacer" />
        {tab === "properties" && (
          <>
            {saved && <span className="muted">{saved}</span>}
            <button className="action-button" onClick={reload} disabled={saving} title={multi ? "Read the nodes again, dropping unsaved edits" : "Read the node again, dropping unsaved edits"}>
              <IconRefresh size={15} stroke={1.8} />
              Reload
            </button>
            <button className="action-button primary" onClick={save} disabled={dirty === 0 || saving} title={multi ? "Write the edited fields to every selected node" : undefined}>
              <IconDeviceFloppy size={15} stroke={1.8} />
              {dirty === 0 ? "Save" : `Save ${dirty} ${dirty === 1 ? "field" : "fields"}`}
            </button>
            <button className="icon-button danger" title={multi ? `Delete these ${formatCount(nodes.length)} nodes` : "Delete this node"} onClick={remove} disabled={saving}>
              <IconTrash size={16} stroke={1.8} />
            </button>
          </>
        )}
        {onClose && (
          <button className="icon-button" title={multi ? "Close, and let go of the selection" : "Close"} onClick={onClose}>
            <IconX size={16} stroke={1.8} />
          </button>
        )}
      </div>
      {multi && (
        // which nodes these are, one chip each; the cross on a chip takes that node out of the selection
        <div className="node-editor-chips">
          {nodes.slice(0, maxChips).map((n) => (
            <span className="node-chip" key={n.id} title={`${n.typeName} · id ${n.id} · #${n.intId}`}>
              {n.displayName || n.typeName}
              <em>{n.typeName}</em>
              {onDeselect && (
                <button className="icon-button" title="Take this node out of the selection" onClick={() => onDeselect(n.id)}>
                  <IconX size={12} stroke={2} />
                </button>
              )}
            </span>
          ))}
          {nodes.length > maxChips && <span className="muted">and {formatCount(nodes.length - maxChips)} more</span>}
        </div>
      )}
      <div className="tabs" role="tablist">
        <button className={"tab" + (tab === "properties" ? " active" : "")} role="tab" onClick={() => setTab("properties")}>
          Properties
          {dirty > 0 && <span className="tab-dot" title={`${dirty} unsaved`} />}
        </button>
        {!multi && (
          <>
            <button className={"tab" + (tab === "meta" ? " active" : "")} role="tab" onClick={() => setTab("meta")} title="Access, publishing window, revision and culture">
              Meta
            </button>
            <button className={"tab" + (tab === "history" ? " active" : "")} role="tab" onClick={() => setTab("history")} title="Older versions of the node, from the transaction log">
              History
            </button>
          </>
        )}
      </div>
      {tab === "meta" && !multi && (
        <NodeMetaTab
          storeId={storeId}
          nodeId={node.id}
          onSaved={() => {
            reload(); // a meta write is a new version of the node: the head's timestamps move
            onSaved?.();
          }}
        />
      )}
      {tab === "history" && !multi && (
        <NodeHistoryTab
          storeId={storeId}
          nodeId={node.id}
          onRestored={() => {
            reload(); // the restore is the current version now, so the form and its head are stale
            onSaved?.();
          }}
        />
      )}
      <div className="node-fields" hidden={tab !== "properties"}>
        {shared.hidden > 0 && (
          <div className="node-editor-note">
            {formatCount(shared.hidden)} {shared.hidden === 1 ? "property is" : "properties are"} not on every selected node, and {shared.hidden === 1 ? "is" : "are"} not shown.
          </div>
        )}
        {shared.properties.map(({ property, all, mixed }) => {
          const edited = property.id in values || property.id in targets;
          return (
            <Field
              key={property.id}
              storeId={storeId}
              nodeId={node.id}
              intId={multi ? null : node.intId}
              multi={multi}
              onSaved={() => {
                reload();
                onSaved?.();
              }}
              property={property}
              edited={edited}
              mixed={mixed && !edited}
              differing={mixed ? distinctValues(property, all) : null}
              value={property.id in values ? values[property.id] : mixed ? undefined : property.value}
              targets={targets[property.id] ?? (mixed ? [] : property.targets ?? [])}
              onChange={(v) => setValue(property, v)}
              onTargets={(t) => setTargetList(property, t)}
              onRevert={() => revert(property)}
            />
          );
        })}
      </div>
    </div>
  );
}

/** Whether two nodes hold the same thing in a property: the same linked nodes, or the same value. */
function sameValue(a: PropertyView, b: PropertyView): boolean {
  if (a.editor === "reference" || a.editor === "references" || a.editor === "relation") {
    const x = (a.targets ?? []).map((t) => t.id);
    const y = (b.targets ?? []).map((t) => t.id);
    return x.length === y.length && x.every((id, i) => id === y[i]);
  }
  return JSON.stringify(a.value ?? null) === JSON.stringify(b.value ?? null);
}

/** The values a differing scalar property holds across the selection, as text, a handful at most; null for a property with no short text. */
function distinctValues(property: PropertyView, all: PropertyView[]): string[] | null {
  const scalar: EditorKind[] = ["text", "integer", "number", "bool", "enum", "guid", "datetime", "datetimeoffset", "timespan"];
  if (!scalar.includes(property.editor)) return null;
  const seen = new Set<string>();
  for (const p of all) {
    const v = p.value;
    let text: string;
    if (v === null || v === undefined || v === "") text = "(empty)";
    else if (property.editor === "enum") text = property.options?.find((o) => String(o.value) === String(v))?.label ?? String(v);
    else if (property.editor === "bool") text = v === true ? "True" : "False";
    else text = String(v);
    seen.add(text.length > 40 ? text.slice(0, 40) + "…" : text);
    if (seen.size > 6) break;
  }
  return [...seen];
}

/** "2 Product · 1 Article": what a selection is made of, most of a kind first. */
function typeSummary(nodes: NodeView[]): string {
  const counts = new Map<string, number>();
  for (const n of nodes) counts.set(n.typeName, (counts.get(n.typeName) ?? 0) + 1);
  return [...counts.entries()]
    .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
    .map(([type, count]) => formatCount(count) + " " + type)
    .join(" · ");
}

/** The record without the keys not in `keep`; the same object when nothing goes, so nothing re-renders for it. */
function prune<T>(record: Record<string, T>, keep: Set<string>): Record<string, T> {
  const gone = Object.keys(record).filter((key) => !keep.has(key));
  if (gone.length === 0) return record;
  const next = { ...record };
  for (const key of gone) delete next[key];
  return next;
}

function Field({
  storeId,
  nodeId,
  intId,
  multi,
  property,
  value,
  targets,
  edited,
  mixed,
  differing,
  onChange,
  onTargets,
  onRevert,
  onSaved,
}: {
  storeId: string;
  nodeId: string;
  /** the node's int id: the file field announces a new picture by it (see nodeMedia.ts); null while a selection is open */
  intId: number | null;
  /** whether the form has several nodes open, which the editors that write on their own step aside for */
  multi: boolean;
  onSaved: () => void;
  property: PropertyView;
  value: unknown;
  targets: NodeRef[];
  edited: boolean;
  /** the selected nodes do not agree on this value: the editor is blank, and the badge says so */
  mixed: boolean;
  /** the values they do hold, when they are short enough to be listed in the badge's tooltip */
  differing: string[] | null;
  onChange: (value: unknown) => void;
  onTargets: (targets: NodeRef[]) => void;
  onRevert: () => void;
}) {
  const mixedTitle =
    "The selected nodes do not agree on this value" +
    (differing ? ": " + differing.slice(0, 6).join(", ") + (differing.length > 6 ? ", …" : "") : "") +
    ". What is entered here is written to all of them.";
  return (
    <div className={"node-field" + (edited ? " edited" : "") + (mixed ? " mixed" : "") + (property.readOnly ? " readonly" : "")}>
      <div className="node-field-label">
        <span className="node-field-name">{property.name}</span>
        <IndexMarks flags={{ indexed: property.indexed, wordIndex: property.wordIndex, semanticIndex: property.semanticIndex }} />
        <span className="node-field-type">{property.type}</span>
        {edited && <span className="setting-badge unsaved">unsaved</span>}
        {mixed && (
          <span className="setting-badge mixed" title={mixedTitle}>
            differs
          </span>
        )}
        {/* the three index notes are the icons above; the rest are still worth spelling out */}
        {property.notes
          .filter((note) => note !== "indexed" && note !== "word index" && note !== "semantic index")
          .map((note) => (
            <span className="setting-badge faint" key={note}>
              {note}
            </span>
          ))}
        {property.declaredBy && <span className="node-field-owner">from {property.declaredBy}</span>}
        <button
          className="icon-button node-field-link"
          title={`Open ${property.declaredBy ?? ""}${property.declaredBy ? "." : ""}${property.name} in the data model`}
          onClick={() => openInDatamodel({ typeId: property.ownerTypeId, propertyId: property.id })}
        >
          <IconExternalLink size={13} stroke={1.9} />
        </button>
        {edited && (
          <button className="icon-button" title="Undo this change" onClick={onRevert}>
            <IconArrowBackUp size={14} stroke={1.8} />
          </button>
        )}
      </div>
      <div className="node-field-control">
        <Editor
          storeId={storeId}
          nodeId={nodeId}
          intId={intId}
          multi={multi}
          mixed={mixed}
          property={property}
          value={value}
          targets={targets}
          onChange={onChange}
          onTargets={onTargets}
          onSaved={onSaved}
        />
      </div>
    </div>
  );
}

function Editor({
  storeId,
  nodeId,
  intId,
  multi = false,
  mixed = false,
  property,
  value,
  targets,
  onChange,
  onTargets,
  onSaved,
}: {
  storeId: string;
  nodeId: string;
  /** the node's int id, or null for an inner node - which has no file editor, see innerFields */
  intId: number | null;
  /** several nodes are open: a file or an inner node list, which are written one node at a time, is shown but not edited */
  multi?: boolean;
  /** the value differs between the nodes open: the control is blank, or says so where blank would read as a value */
  mixed?: boolean;
  property: PropertyView;
  value: unknown;
  targets: NodeRef[];
  onChange: (value: unknown) => void;
  onTargets: (targets: NodeRef[]) => void;
  /** an embedded list writes on its own, and the form has to read the node again after it */
  onSaved: () => void;
}) {
  // a fresh array every render would look like a new lookup to the picker
  const typeIds = useMemo(() => (property.targetTypes ?? []).map((t) => t.id), [property.targetTypes]);
  const differs = mixed ? "differs" : undefined;
  switch (property.editor) {
    case "bool":
      return (
        <label className="setting-toggle">
          {/* neither on nor off while the nodes disagree: the box's own third state */}
          <input
            type="checkbox"
            checked={value === true}
            ref={(el) => {
              if (el) el.indeterminate = mixed;
            }}
            onChange={(e) => onChange(e.target.checked)}
          />
          <span>{mixed ? "differs" : value === true ? "True" : "False"}</span>
        </label>
      );
    case "enum": {
      const current = mixed ? "" : String(value ?? 0);
      const options = property.options ?? [];
      return (
        <select className="select" value={current} onChange={(e) => e.target.value !== "" && onChange(Number(e.target.value))}>
          {mixed && (
            <option value="" disabled>
              (differs)
            </option>
          )}
          {!mixed && !options.some((o) => String(o.value) === current) && <option value={current}>{current}</option>}
          {options.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
      );
    }
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
          placeholder={differs}
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
          placeholder={differs ?? property.language ?? undefined}
          onChange={(e) => onChange(e.target.value)}
        />
      );
    case "text":
      return property.multiline ? (
        <textarea className="text-input" rows={6} value={String(value ?? "")} maxLength={property.maxLength ?? undefined} placeholder={differs} onChange={(e) => onChange(e.target.value)} />
      ) : (
        <input
          className="text-input wide"
          value={String(value ?? "")}
          maxLength={property.maxLength ?? undefined}
          spellCheck={false}
          placeholder={differs}
          onChange={(e) => onChange(e.target.value)}
        />
      );
    case "guid":
      return (
        <input
          className="text-input wide mono"
          value={String(value ?? "")}
          spellCheck={false}
          placeholder={differs ?? "00000000-0000-0000-0000-000000000000"}
          onChange={(e) => onChange(e.target.value)}
        />
      );
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
            placeholder={differs ?? "2026-08-30T12:00:00.0000000+02:00"}
            onChange={(e) => onChange(e.target.value ? e.target.value : null)}
          />
          <span className="setting-unit">with offset</span>
        </>
      );
    case "timespan":
      return (
        <>
          <input className="text-input mono" value={String(value ?? "")} spellCheck={false} placeholder={differs ?? "d.hh:mm:ss"} onChange={(e) => onChange(e.target.value)} />
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
            placeholder={differs ?? "latitude"}
            value={geo ? geo.latitude : ""}
            onChange={(e) => set(Number(e.target.value), geo?.longitude ?? 0)}
          />
          <input
            className="text-input number"
            type="number"
            step="any"
            placeholder={differs ?? "longitude"}
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
      return <NodePicker storeId={storeId} types={property.targetTypes ?? []} typeIds={typeIds} targets={targets} multiple={false} onChange={onTargets} />;
    case "references":
      return <NodePicker storeId={storeId} types={property.targetTypes ?? []} typeIds={typeIds} targets={targets} multiple onChange={onTargets} />;
    case "relation":
      return <NodePicker storeId={storeId} types={property.targetTypes ?? []} typeIds={typeIds} targets={targets} multiple={property.isMany === true} onChange={onTargets} />;
    case "file": {
      const file = (value ?? null) as FileValueView | null;
      if (multi) {
        // a file is staged and stored on one node the moment it arrives (see FileField): there is
        // no "the same file on all of them" to offer, so the field only says what is there
        return (
          <div className="node-file-field">
            {mixed ? <span className="muted">Different files.</span> : file ? <FilePreview storeId={storeId} file={file} /> : <span className="muted">No file.</span>}
            <span className="muted">Files are put on one node at a time: open a node on its own to upload one.</span>
          </div>
        );
      }
      return <FileField storeId={storeId} nodeId={nodeId} intId={intId} property={property} file={file} />;
    }
    case "embedded": {
      const inner = Array.isArray(value) ? (value as InnerNodeView[]) : [];
      if (property.readOnly || multi) {
        // a list too long to edit a row at a time is still worth seeing - and so is the list several
        // nodes share, though it is written one node at a time
        return (
          <div className="node-inner">
            <span className="muted">
              {multi ? (mixed ? "The inner nodes differ between the selected nodes. They are edited one node at a time." : "Inner nodes are edited one node at a time.") : property.info}
            </span>
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
      return <EmbeddedEditor storeId={storeId} nodeId={nodeId} property={property} inner={inner} onSaved={onSaved} />;
    }
    default:
      return <span className="muted">{property.info ?? "Not editable here."}</span>;
  }
}

/**
 * The file on a file property: what is there, and how to put something else there.
 *
 * The upload is not part of the form's Save and cannot be. A file store takes a whole stream and
 * hashes it, so the bytes have to arrive somewhere before there is a value to write at all: the
 * file is staged slice by slice in the database's own storage (uploadStaged), and the commit hands
 * that staged file to the store and writes the property in one go. So this field changes the node
 * the moment the upload finishes, which is why it says so rather than showing an "unsaved" badge
 * it could not honour.
 *
 * Slicing is what makes the progress bar mean anything on a file worth watching, and on storage
 * that can shorten a file it also means a broken connection resumes from the byte the server holds
 * rather than from zero. The server says which of the two this database's storage is.
 *
 * A picture that changes here is also a picture the visual pivot may be holding in a texture, so the
 * change is announced (see nodeMedia.ts) rather than left for it to find out about.
 */
function FileField({
  storeId,
  nodeId,
  intId,
  property,
  file,
}: {
  storeId: string;
  nodeId: string;
  /** the node's int id, which is what the card views know it by; null leaves the field read-only */
  intId: number | null;
  property: PropertyView;
  file: FileValueView | null;
}) {
  // what the field shows: the node's file until an upload replaces it, and the node's again after a
  // reload hands down a different one
  const [shown, setShown] = useState<FileValueView | null>(file);
  useEffect(() => setShown(file), [file]);
  const [sent, setSent] = useState<{ bytes: number; total: number } | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [resumable, setResumable] = useState<boolean | null>(null);
  const [clearing, setClearing] = useState(false);
  const input = useRef<HTMLInputElement>(null);
  const abort = useRef<AbortController | null>(null);
  // an upload outlives the field when the form is closed under it, so nothing is set after unmount
  const live = useRef(true);
  useEffect(() => {
    live.current = true;
    return () => {
      live.current = false;
      abort.current?.abort();
    };
  }, []);

  async function upload(picked: FileList | null) {
    const chosen = picked?.[0];
    if (!chosen || sent) return;
    setNote(null);
    setSent({ bytes: 0, total: chosen.size });
    const controller = new AbortController();
    abort.current = controller;
    let target: { ioId: string; uploadId: string } | null = null;
    try {
      const where = await fileUploadTarget(storeId, property.id);
      if (live.current) setResumable(where.resumable);
      target = { ioId: where.ioId, uploadId: newUploadId() };
      await uploadStaged(where.ioId, target.uploadId, chosen, (bytes, total) => live.current && setSent({ bytes, total }), controller.signal);
      const stored = await commitNodeFile(storeId, nodeId, property.id, target.uploadId, chosen.name, chosen.size);
      // said whether this field is still on screen or not: what is holding the old picture is not
      if (intId !== null) notifyNodePicture(storeId, intId);
      if (!live.current) return;
      setShown(stored);
      setNote("Stored on the node.");
    } catch (e) {
      if (target) abortUpload(target.ioId, target.uploadId); // the staged bytes are nobody's now
      if (!live.current) return;
      const message = controller.signal.aborted ? "Upload cancelled." : e instanceof Error ? e.message : String(e);
      setNote(message);
      if (!controller.signal.aborted) await showError("Could not upload the file", message);
    } finally {
      abort.current = null;
      if (live.current) setSent(null);
    }
  }

  /**
   * Off the property and out of the file store. Replacing a file leaves the old one behind for the
   * storage page's redundant-file sweep to find; removing one deletes it, because nothing is left
   * pointing at it - which is exactly why it is asked about first, and why the question says so.
   */
  async function remove() {
    if (!shown || sent || clearing) return;
    const confirmed = await showConfirm(
      `Remove ${shown.name}?`,
      `The file is taken off ${property.name} and deleted from the file store. This is not part of Save and cannot be undone from here.`,
      { confirmLabel: "Remove", danger: true },
    );
    if (!confirmed.ok) return;
    setClearing(true);
    try {
      await clearNodeFile(storeId, nodeId, property.id);
      if (intId !== null) notifyNodePicture(storeId, intId);
      if (!live.current) return;
      setShown(null);
      setNote("Removed from the node.");
    } catch (e) {
      const message = e instanceof Error ? e.message : String(e);
      if (live.current) setNote(message);
      await showError("Could not remove the file", message);
    } finally {
      if (live.current) setClearing(false);
    }
  }

  const percent = sent && sent.total > 0 ? Math.min(100, (sent.bytes / sent.total) * 100) : 0;
  return (
    <div className="node-file-field">
      {shown ? <FilePreview storeId={storeId} file={shown} /> : <span className="muted">No file.</span>}
      {sent ? (
        <div className="node-file-progress">
          <div className="node-file-bar">
            <div className="node-file-fill" style={{ width: Math.max(1, percent) + "%" }} />
          </div>
          <span className="muted">
            {formatBytes(sent.bytes)} of {formatBytes(sent.total)} · {Math.round(percent)}%
            {resumable === false ? " · this storage cannot resume a broken transfer" : ""}
          </span>
          <button className="action-button" onClick={() => abort.current?.abort()}>
            Cancel
          </button>
        </div>
      ) : intId === null ? null : (
        <div className="node-file-actions">
          <button className="action-button" onClick={() => input.current?.click()} disabled={clearing}>
            <IconUpload size={14} stroke={1.8} /> {shown ? "Replace file" : "Upload file"}
          </button>
          {shown && (
            <button className="icon-button danger" title={`Remove ${shown.name} from this property and delete it`} onClick={remove} disabled={clearing}>
              <IconTrash size={15} stroke={1.8} />
            </button>
          )}
          <span className="muted">{note ?? "uploaded in parts, and stored on the node as soon as it is there - not on Save"}</span>
        </div>
      )}
      <input
        ref={input}
        type="file"
        hidden
        onChange={(e) => {
          void upload(e.target.files);
          e.target.value = ""; // picking the same file again has to count as a new upload
        }}
      />
    </div>
  );
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
 * The inner nodes of an embedded property, as a list of small forms.
 *
 * The list is a document inside the node rather than a set of node properties, so it is written as
 * one and on its own: adding, removing, reordering and editing change a local copy, and Apply sends
 * the whole list. That is why this has its own dirty state and its own button rather than joining
 * the form's Save - the store rewrites the property, not the fields inside it.
 */
function EmbeddedEditor({
  storeId,
  nodeId,
  property,
  inner,
  onSaved,
}: {
  storeId: string;
  nodeId: string;
  property: PropertyView;
  inner: InnerNodeView[];
  onSaved: () => void;
}) {
  const initial = useMemo<InnerRow[]>(
    () => inner.map((n) => ({ id: n.id, typeId: n.typeId, typeName: n.typeName, fields: n.fields, values: {} })),
    [inner],
  );
  const [rows, setRows] = useState<InnerRow[]>(initial);
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  useEffect(() => {
    setRows(initial);
    setDirty(false);
  }, [initial]);

  const types = property.targetTypes ?? [];

  function edit(i: number, field: PropertyView, value: unknown) {
    setDirty(true);
    setRows((prev) => prev.map((r, j) => (i === j ? { ...r, values: { ...r.values, [field.id]: value } } : r)));
  }
  function remove(i: number) {
    setDirty(true);
    setRows((prev) => prev.filter((_, j) => j !== i));
  }
  function move(i: number, by: number) {
    const to = i + by;
    if (to < 0 || to >= rows.length) return;
    setDirty(true);
    setRows((prev) => {
      const next = [...prev];
      const [row] = next.splice(i, 1);
      next.splice(to, 0, row);
      return next;
    });
  }
  async function add() {
    if (types.length === 0) return;
    let type = types[0];
    if (types.length > 1) {
      const pick = await showChoice("Add an inner node", "Which type?", types.map((t) => ({ label: t.name })));
      if (pick === null) return;
      type = types[pick];
    }
    // a new row borrows the fields of an existing one of its type, so it opens with editors to fill
    // in; with none to borrow it is added bare and takes its shape after Apply
    const like = rows.find((r) => r.typeId === type.id) ?? initial.find((r) => r.typeId === type.id);
    setDirty(true);
    setRows((prev) => [...prev, { id: null, typeId: type.id, typeName: type.name, fields: like?.fields ?? [], values: {} }]);
  }
  async function apply() {
    setSaving(true);
    try {
      await saveEmbedded(
        storeId,
        nodeId,
        property.id,
        rows.map((r) => ({
          id: r.id,
          typeId: r.typeId,
          // the server takes the whole row, so an untouched field sends what it already had
          values: Object.fromEntries(r.fields.map((f) => [f.id, f.id in r.values ? r.values[f.id] : f.value])),
        })),
      );
      setDirty(false);
      onSaved();
    } catch (e) {
      await showError("Could not save the inner nodes", e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="node-inner">
      <div className="node-inner-tools">
        <span className="muted">{rows.length === 0 ? "empty" : rows.length + (rows.length === 1 ? " inner node" : " inner nodes")}</span>
        <div className="query-spacer" />
        {dirty && (
          <button className="action-button primary" onClick={apply} disabled={saving}>
            <IconDeviceFloppy size={14} stroke={1.8} /> Apply
          </button>
        )}
        {dirty && (
          <button
            className="link-button"
            disabled={saving}
            onClick={() => {
              setRows(initial);
              setDirty(false);
            }}
          >
            undo
          </button>
        )}
        <button className="link-button" onClick={add} disabled={saving || types.length === 0} title={types.length === 0 ? "The property names no type to embed" : undefined}>
          <IconPlus size={13} stroke={1.8} /> add
        </button>
      </div>
      {rows.map((row, i) => (
        <div className="node-inner-node editable" key={(row.id ?? "new") + ":" + i}>
          <div className="node-inner-head">
            <span className="node-inner-type">{row.typeName}</span>
            {row.id === null && <span className="setting-badge unsaved">new</span>}
            <div className="query-spacer" />
            <button className="icon-button" title="Move up" onClick={() => move(i, -1)} disabled={i === 0}>
              <IconChevronUp size={14} stroke={2} />
            </button>
            <button className="icon-button" title="Move down" onClick={() => move(i, 1)} disabled={i === rows.length - 1}>
              <IconChevronDown size={14} stroke={2} />
            </button>
            <button className="icon-button danger" title="Remove" onClick={() => remove(i)}>
              <IconX size={13} stroke={2} />
            </button>
          </div>
          <div className="node-inner-fields">
            {row.fields.map((f) => (
              <label className="node-inner-field" key={f.id}>
                <span className="node-inner-field-name">
                  {f.name}
                  <IndexMarks flags={{ indexed: f.indexed, wordIndex: f.wordIndex, semanticIndex: f.semanticIndex }} />
                </span>
                <Editor
                  storeId={storeId}
                  nodeId={nodeId}
                  intId={null}
                  property={f}
                  value={f.id in row.values ? row.values[f.id] : f.value}
                  targets={[]}
                  onChange={(v) => edit(i, f, v)}
                  onTargets={() => undefined}
                  onSaved={onSaved}
                />
              </label>
            ))}
            {row.fields.length === 0 && <span className="muted">Apply writes the row; its fields appear once it is stored.</span>}
          </div>
        </div>
      ))}
    </div>
  );
}

/** One inner node while it is being edited: what it was, plus the fields that were touched. */
interface InnerRow {
  id: string | null;
  typeId: string;
  typeName: string;
  fields: PropertyView[];
  values: Record<string, unknown>;
}

/**
 * Picks nodes for a reference or a relation. The search is the same free text search the query page
 * runs, narrowed to the types the property can point at, so finding a node here works the same way
 * as finding one there. What is not there yet can be made from here: "new" writes an empty node of
 * the target type and points at it, and it is then editable like any other hit.
 */
function NodePicker({
  storeId,
  types,
  typeIds,
  targets,
  multiple,
  onChange,
}: {
  storeId: string;
  types: TypeRef[];
  typeIds: string[];
  targets: NodeRef[];
  multiple: boolean;
  onChange: (targets: NodeRef[]) => void;
}) {
  const [open, setOpen] = useState(false);
  const [text, setText] = useState("");
  const [making, setMaking] = useState(false);
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

  /** Makes a node of the target type and points at it. The node is written now; the link on save. */
  async function makeOne() {
    if (types.length === 0) return;
    let type = types[0];
    if (types.length > 1) {
      const pick = await showChoice("New node", "Which type?", types.map((t) => ({ label: t.name })));
      if (pick === null) return;
      type = types[pick];
    }
    setMaking(true);
    try {
      add(await createNode(storeId, type.id));
    } catch (e) {
      await showError("Could not create the node", e instanceof Error ? e.message : String(e));
    } finally {
      setMaking(false);
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
            {types.length > 0 && (
              <button className="node-picker-result new" onClick={makeOne} disabled={making}>
                <span>
                  <IconPlus size={13} stroke={2} /> New {types.length === 1 ? types[0].name : "node…"}
                </span>
                <em>empty, to fill in</em>
              </button>
            )}
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
