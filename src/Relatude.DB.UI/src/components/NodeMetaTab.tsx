import { useCallback, useEffect, useState } from "react";
import { IconArrowBackUp, IconDeviceFloppy, IconRefresh } from "@tabler/icons-react";
import { fetchNodeMeta, saveNodeMeta, type AccessView, type IdView, type MetaKey, type NodeMetaView } from "../server/query";
import { showError } from "../dialogs";
import { formatCount, formatTime } from "../format";

type Edits = Partial<Record<MetaKey, string | boolean | null>>;

/**
 * The meta of one node: not what it says, but who may see it, when it is live, and which revision
 * and culture it is. Same form as the properties, same rule - only what was touched is written.
 *
 * The access values are groups. Most nodes leave them unspecified and inherit: from the type's
 * default, then from the database's, and that is what the filter applies when a query runs. So
 * beside the stored value the form says what actually applies and where that comes from; an
 * inherited "Everyone" and an explicit one look the same to a query but not to the person who has
 * to decide whether to change it.
 *
 * Culture and revision key identify the revision rather than describe it and are not editable here;
 * neither are the two audit guids, which are the application's to write.
 */
export function NodeMetaTab({ storeId, nodeId, onSaved }: { storeId: string; nodeId: string; onSaved?: () => void }) {
  const [meta, setMeta] = useState<NodeMetaView | null>(null);
  const [edits, setEdits] = useState<Edits>({});
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState<string | null>(null);

  const load = useCallback(() => {
    fetchNodeMeta(storeId, nodeId)
      .then((m) => {
        setMeta(m);
        setEdits({});
        setError(null);
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, [storeId, nodeId]);
  useEffect(load, [load]);

  const dirty = Object.keys(edits).length;
  function set(key: MetaKey, value: string | boolean | null) {
    setSaved(null);
    setEdits((prev) => ({ ...prev, [key]: value }));
  }
  function revert(key: MetaKey) {
    setSaved(null);
    setEdits((prev) => {
      const next = { ...prev };
      delete next[key];
      return next;
    });
  }

  async function save() {
    if (!meta || dirty === 0) return;
    setSaving(true);
    try {
      const result = await saveNodeMeta(storeId, nodeId, edits);
      setSaved(result.changed === 0 ? "Nothing changed." : `Saved ${formatCount(result.changed)} ${result.changed === 1 ? "value" : "values"}.`);
      load();
      onSaved?.();
    } catch (e) {
      await showError("Could not save the meta", e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }

  if (error) return <div className="placeholder">{error}</div>;
  if (!meta) return null;

  const guid = (key: MetaKey, stored: IdView) => (key in edits ? ((edits[key] as string | null) ?? "") : (stored.value ?? ""));
  const date = (key: MetaKey, stored: string | null) => (key in edits ? ((edits[key] as string | null) ?? "") : (stored ?? ""));

  return (
    <div className="node-meta">
      <div className="node-meta-actions">
        <span className="muted">
          {meta.stored ? "This node carries stored meta." : "This node carries no stored meta: every value below is a default."}
        </span>
        <div className="query-spacer" />
        {saved && <span className="muted">{saved}</span>}
        <button className="action-button" onClick={load} disabled={saving} title="Read the meta again, dropping unsaved edits">
          <IconRefresh size={15} stroke={1.8} />
          Reload
        </button>
        <button className="action-button primary" onClick={save} disabled={dirty === 0 || saving}>
          <IconDeviceFloppy size={15} stroke={1.8} />
          {dirty === 0 ? "Save" : `Save ${dirty} ${dirty === 1 ? "value" : "values"}`}
        </button>
      </div>

      <section className="node-meta-group">
        <h4>Access</h4>
        <MetaField name="ReadAccess" label="Read access" hint="who may see the node in queries" edited={"ReadAccess" in edits} onRevert={() => revert("ReadAccess")}>
          <AccessEditor view={meta.readAccess} value={guid("ReadAccess", meta.readAccess)} edited={"ReadAccess" in edits} onChange={(v) => set("ReadAccess", v)} />
        </MetaField>
        <MetaField
          name="EditViewAccess"
          label="Edit view access"
          hint="who may see the node in an editing view"
          edited={"EditViewAccess" in edits}
          onRevert={() => revert("EditViewAccess")}
        >
          <AccessEditor view={meta.editViewAccess} value={guid("EditViewAccess", meta.editViewAccess)} edited={"EditViewAccess" in edits} onChange={(v) => set("EditViewAccess", v)} />
        </MetaField>
        <MetaField name="EditAccess" label="Edit access" hint="who may change the node" edited={"EditAccess" in edits} onRevert={() => revert("EditAccess")}>
          <AccessEditor view={meta.editAccess} value={guid("EditAccess", meta.editAccess)} edited={"EditAccess" in edits} onChange={(v) => set("EditAccess", v)} />
        </MetaField>
        <MetaField name="PublishAccess" label="Publish access" hint="who may publish a revision" edited={"PublishAccess" in edits} onRevert={() => revert("PublishAccess")}>
          <AccessEditor view={meta.publishAccess} value={guid("PublishAccess", meta.publishAccess)} edited={"PublishAccess" in edits} onChange={(v) => set("PublishAccess", v)} />
        </MetaField>
      </section>

      <section className="node-meta-group">
        <h4>Publishing</h4>
        <MetaField name="Deleted" label="Deleted" hint="hidden from every query, including this page's search; the data stays" edited={"Deleted" in edits} onRevert={() => revert("Deleted")}>
          <label className="setting-toggle">
            <input type="checkbox" checked={"Deleted" in edits ? edits.Deleted === true : meta.deleted} onChange={(e) => set("Deleted", e.target.checked)} />
            <span>{("Deleted" in edits ? edits.Deleted === true : meta.deleted) ? "Deleted" : "Not deleted"}</span>
          </label>
        </MetaField>
        <MetaField name="ReleaseUtc" label="Release" hint="not published before this time" edited={"ReleaseUtc" in edits} onRevert={() => revert("ReleaseUtc")}>
          <DateEditor value={date("ReleaseUtc", meta.releaseUtc)} onChange={(v) => set("ReleaseUtc", v)} />
        </MetaField>
        <MetaField name="ExpireUtc" label="Expire" hint="not published from this time on" edited={"ExpireUtc" in edits} onRevert={() => revert("ExpireUtc")}>
          <DateEditor value={date("ExpireUtc", meta.expireUtc)} onChange={(v) => set("ExpireUtc", v)} />
        </MetaField>
        <MetaField name="CollectionId" label="Collection" hint="the collection the node belongs to, if any" edited={"CollectionId" in edits} onRevert={() => revert("CollectionId")}>
          <input
            className="text-input wide mono"
            value={guid("CollectionId", meta.collection)}
            spellCheck={false}
            placeholder="none"
            onChange={(e) => set("CollectionId", e.target.value || null)}
          />
          {!("CollectionId" in edits) && meta.collection.name && <span className="node-meta-hint">{meta.collection.name}</span>}
        </MetaField>
      </section>

      <section className="node-meta-group">
        <h4>Revision</h4>
        <ReadOnly name="Revisions" value={meta.hasRevisions ? "enabled" : "not enabled — the node holds one version of its content"} />
        {meta.hasRevisions && <ReadOnly name="Revision id" value={meta.revisionId ?? ""} mono />}
        <ReadOnly name="Revision type" value={meta.revisionType} />
        <ReadOnly name="Culture" value={meta.culture.value ? describeId(meta.culture) : "none (culture neutral)"} />
      </section>

      <section className="node-meta-group">
        <h4>Record</h4>
        <ReadOnly name="Created" value={formatTime(meta.createdUtc)} />
        <ReadOnly name="Changed" value={formatTime(meta.changedUtc)} />
        <ReadOnly name="Created by" value={meta.createdBy.value ? describeId(meta.createdBy) : "not recorded"} />
        <ReadOnly name="Changed by" value={meta.changedBy.value ? describeId(meta.changedBy) : "not recorded"} />
        <ReadOnly name="Id" value={meta.id} mono />
        <ReadOnly name="Internal id" value={String(meta.intId)} />
        <ReadOnly name="Address" value={meta.address ? meta.address + (meta.autoAddress ? " (automatic)" : "") : "none"} />
      </section>
    </div>
  );
}

function describeId(id: IdView): string {
  return id.name ? `${id.name} · ${id.value}` : (id.value ?? "");
}

function MetaField({
  name,
  label,
  hint,
  edited,
  onRevert,
  children,
}: {
  name: string;
  label: string;
  hint: string;
  edited: boolean;
  onRevert: () => void;
  children: React.ReactNode;
}) {
  return (
    <div className={"node-field" + (edited ? " edited" : "")} title={name}>
      <div className="node-field-label">
        <span className="node-field-name">{label}</span>
        {edited && <span className="setting-badge unsaved">unsaved</span>}
        {edited && (
          <button className="icon-button" title="Undo this change" onClick={onRevert}>
            <IconArrowBackUp size={14} stroke={1.8} />
          </button>
        )}
        <span className="node-field-owner">{hint}</span>
      </div>
      <div className="node-field-control">{children}</div>
    </div>
  );
}

function ReadOnly({ name, value, mono }: { name: string; value: string; mono?: boolean }) {
  return (
    <div className="node-field readonly">
      <div className="node-field-label">
        <span className="node-field-name">{name}</span>
      </div>
      <div className={"node-field-control" + (mono ? " mono" : "")}>{value}</div>
    </div>
  );
}

// the groups every database has; anything else is a group or user node, named by its guid
const wellKnownGroups = [
  { value: "", label: "Unspecified — inherit" },
  { value: "11111111-1111-1111-1111-111111111111", label: "Everyone" },
  { value: "22222222-2222-2222-2222-222222222222", label: "Members" },
  { value: "ffffffff-ffff-ffff-ffff-ffffffffffff", label: "Administrators" },
];

const sourceLabels = { node: "set on the node", type: "the type's default", database: "the database's default", none: "nothing applies" };

/**
 * A group, as a choice between the well known ones and any other guid. Next to it, while the value
 * is untouched, what the filter would actually apply: the resolved group and where it came from.
 */
function AccessEditor({ view, value, edited, onChange }: { view: AccessView; value: string; edited: boolean; onChange: (value: string | null) => void }) {
  const lower = value.toLowerCase();
  const known = wellKnownGroups.some((g) => g.value === lower);
  const [custom, setCustom] = useState(!known);
  const mode = custom ? "custom" : lower;
  return (
    <>
      <select
        className="select"
        value={mode}
        onChange={(e) => {
          if (e.target.value === "custom") {
            setCustom(true);
            onChange(value);
          } else {
            setCustom(false);
            onChange(e.target.value === "" ? null : e.target.value);
          }
        }}
      >
        {wellKnownGroups.map((g) => (
          <option key={g.value} value={g.value}>
            {g.label}
          </option>
        ))}
        <option value="custom">Another group or user, by id…</option>
      </select>
      {custom && <input className="text-input wide mono" value={value} spellCheck={false} placeholder="00000000-0000-0000-0000-000000000000" onChange={(e) => onChange(e.target.value)} />}
      {!edited && (
        <span className="node-meta-hint" title={view.effective ?? undefined}>
          {custom && view.name ? `${view.name} · ` : ""}
          applies: <strong>{view.effectiveName ?? view.effective ?? "nothing"}</strong> ({sourceLabels[view.effectiveSource]})
        </span>
      )}
    </>
  );
}

/** A UTC instant, edited as the store keeps it: no local-time conversion in either direction. */
function DateEditor({ value, onChange }: { value: string; onChange: (value: string | null) => void }) {
  return (
    <>
      <input className="text-input" type="datetime-local" value={value.slice(0, 16)} onChange={(e) => onChange(e.target.value ? e.target.value : null)} />
      <span className="setting-unit">UTC</span>
      {value && (
        <button className="link-button" onClick={() => onChange(null)}>
          clear
        </button>
      )}
    </>
  );
}
