import { useEffect, useMemo, useState } from "react";
import { IconAdjustments, IconAlertTriangle, IconArrowBackUp, IconDownload, IconFileImport } from "@tabler/icons-react";
import { DialogTools } from "./DialogTools";
import { Loading } from "./Loading";
import { showConfirm, showError, showInfo } from "../dialogs";
import {
  fetchInstallationOverrides,
  moveOverridesToShared,
  overridesKey,
  sharedOverridesText,
  type InstallationOverride,
  type InstallationOverrides,
  type OverridePath,
  type OverridesFileInfo,
  type OverridesJson,
} from "../server/datamodel";
import { declaringType, setOverride } from "../server/overrides";
import type { EditorContext } from "./DatamodelEditors";

/**
 * This installation's datamodel overrides, and moving them into the file every installation shares.
 *
 * A database keeps its overrides in two files. The shared one, relatude.settings/[short name]/
 * datamodel.overrides.json, is part of the application: in source control and deployed with it. This
 * installation's, with the database, holds what the editor changed here and is merged over the shared
 * one. The list is this installation's file as the last activation wrote it, entry by entry, beside what
 * the shared file says.
 *
 * Moving writes the selection into the shared file and takes it out of this installation's: nothing in
 * force changes, so the database is not reopened. That only reaches the other installations through
 * source control, so on a server outside the Development environment - where relatude.settings is
 * usually the deployed copy the next deployment replaces - the dialog says so, and offers the shared
 * file as a download instead. Discarding goes back to the shared override (or the source) in the draft,
 * which is activated like any other change.
 *
 * Nothing starts selected, and both actions that change a file ask first.
 */
export function DatamodelOverridesDialog({
  storeId,
  ctx,
  activeOverrides,
  onClose,
  onFilesChanged,
}: {
  storeId: string;
  ctx: EditorContext;
  /** the overrides of the active model, to tell whether the draft changes any that are not in the file yet */
  activeOverrides: OverridesJson | null | undefined;
  onClose: () => void;
  onFilesChanged: (files: OverridesFileInfo) => void;
}) {
  const [view, setView] = useState<InstallationOverrides | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    fetchInstallationOverrides(storeId)
      .then(setView)
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, [storeId]);

  const entries = view?.entries ?? [];
  const files = view?.files;
  const chosen = entries.filter((e) => selected.has(keyOf(e)));
  const allSelected = entries.length > 0 && entries.every((e) => selected.has(keyOf(e)));
  const draftDiffers = overridesKey(ctx.model.Overrides) !== overridesKey(activeOverrides);
  // by type, in the order the file has them
  const groups = useMemo(() => {
    const map = new Map<string, InstallationOverride[]>();
    for (const e of entries) {
      const list = map.get(e.typeId) ?? [];
      list.push(e);
      map.set(e.typeId, list);
    }
    return [...map.entries()];
  }, [entries]);

  function toggle(keys: string[], on: boolean): void {
    setSelected((prev) => {
      const next = new Set(prev);
      for (const key of keys) {
        if (on) next.add(key);
        else next.delete(key);
      }
      return next;
    });
  }

  function label(e: InstallationOverride): string {
    const property = e.propertyId ? declaringType(ctx.model, e.propertyId)?.Properties[e.propertyId] : undefined;
    const fields = property ? [...ctx.schema.propertyCommon, ...(ctx.schema.propertyByType[property.PropertyType] ?? [])] : e.propertyId ? ctx.schema.propertyCommon : ctx.schema.nodeType;
    const attribute = fields.find((f) => f.path === e.attribute)?.label ?? e.attribute;
    return e.propertyId ? (property?.CodeName ?? e.propertyName ?? e.propertyId) + " · " + attribute : attribute;
  }
  function typeTitle(typeId: string, first: InstallationOverride): string {
    const t = ctx.model.NodeTypes[typeId];
    return t ? (t.Namespace ? t.Namespace + "." + t.CodeName : t.CodeName) : (first.typeName ?? typeId) + " (not in the model)";
  }

  async function move(): Promise<void> {
    if (!files || chosen.length === 0) return;
    const { ok } = await showConfirm(
      `Move ${count(chosen.length, "override")} into ${files.sharedLocation}?`,
      [
        `${chosen.length === 1 ? "It is" : "They are"} written into ${files.sharedLocation}, which every installation shares, and removed from this installation's file, ${files.location}. Nothing in force changes, so the database is not reopened.`,
        `${files.sharedLocation} is rewritten as a whole, so comments in it are not kept.`,
        files.development
          ? `Commit ${files.sharedLocation} to take ${chosen.length === 1 ? "it" : "them"} to the other installations.`
          : `This server runs in the ${files.environment} environment. If ${files.sharedLocation} here is the deployed copy of the application, the next deployment replaces it, and the moved overrides are gone from this installation too. Download the shared file instead to put it into source control.`,
      ].join(" "),
      { confirmLabel: "Move", danger: !files.development },
    );
    if (!ok) return;
    setBusy(true);
    try {
      const result = await moveOverridesToShared(storeId, chosen.map(pathOf));
      setView(result.view);
      onFilesChanged(result.view.files);
      const still = new Set(result.view.entries.map(keyOf));
      setSelected((prev) => new Set([...prev].filter((k) => still.has(k))));
      setMessage(`${count(result.moved, "override")} moved into ${result.view.files.sharedLocation}.`);
    } catch (e) {
      await showError("Could not move the overrides", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  async function download(): Promise<void> {
    if (!files) return;
    setBusy(true);
    try {
      const result = await sharedOverridesText(storeId, chosen.map(pathOf));
      saveText(result.fileName, result.content);
      setMessage(
        chosen.length > 0
          ? `Downloaded ${result.fileName} with ${count(chosen.length, "override")} moved in. Nothing here was changed: put it in ${result.sharedLocation} in source control.`
          : `Downloaded ${result.fileName} as it is.`,
      );
    } catch (e) {
      await showError("Could not make the shared file", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  async function discard(): Promise<void> {
    if (!files || chosen.length === 0) return;
    const { ok } = await showConfirm(
      `Discard ${count(chosen.length, "override")} of this installation?`,
      `Each goes back to the shared override, or to what the source says where the shared file has none. This changes the draft: activate it to apply the change.`,
      { confirmLabel: "Discard in the draft" },
    );
    if (!ok) return;
    const picked = chosen.slice();
    ctx.update((m) => {
      for (const e of picked) setOverride(m, e.typeId, e.propertyId, e.attribute, e.inShared ? e.sharedValue : undefined);
    });
    onClose();
    await showInfo("Discarded in the draft", `${count(picked.length, "override")} of this installation ${picked.length === 1 ? "goes" : "go"} back to the shared override or the source when the draft is activated.`);
  }

  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && !busy && onClose()}>
      <div
        className="dialog dialog-wide overrides-dialog"
        role="dialog"
        aria-label="Datamodel overrides"
        onKeyDown={(e) => {
          if (e.key === "Escape" && !busy) {
            e.preventDefault();
            onClose();
          }
        }}
      >
        <h3>
          <IconAdjustments size={16} stroke={1.8} className="tone-override" /> Overrides of this installation
          <DialogTools onClose={onClose} />
        </h3>
        {error && <div className="dialog-body dialog-title-error">{error}</div>}
        {!error && !view && <Loading label="Reading the overrides files…" />}
        {files?.sharedError && <div className="dialog-body dialog-title-error">{files.sharedError}</div>}
        {view && files && (
          <>
            <div className="dialog-body overrides-intro">
              Overrides set in the editor are kept for this installation in <code>{files.location}</code>, merged over the ones every installation shares in{" "}
              <code>{files.sharedLocation}</code> - part of the application, in source control and deployed with it. Move overrides there to have them on every
              installation, or discard them to go back to the shared override, or to the source.
            </div>
            {!files.development && (
              <div className="dm-notice warn">
                <IconAlertTriangle size={15} stroke={1.8} />
                <span>
                  This server runs in the <b>{files.environment}</b> environment. Here <code>{files.sharedLocation}</code> is most likely the deployed copy of the
                  application, which the next deployment replaces, taking whatever was moved into it along. Download the shared file with the overrides
                  selected instead, and put it into source control.
                </span>
              </div>
            )}
            {draftDiffers && (
              <div className="dm-notice">
                <IconAlertTriangle size={15} stroke={1.8} />
                <span>The draft changes overrides that are not activated yet. They are not in the file, so they are not listed here: activate the draft first to move them.</span>
              </div>
            )}
            {entries.length === 0 ? (
              <div className="overrides-empty">
                Nothing is overridden for this installation alone: every override in force comes from <code>{files.sharedLocation}</code>.
              </div>
            ) : (
              <>
                <div className="overrides-toolbar">
                  <label className="settings-check">
                    <input type="checkbox" checked={allSelected} disabled={busy} onChange={(e) => toggle(entries.map(keyOf), e.target.checked)} />
                    Select all
                  </label>
                  <span className="muted">
                    {chosen.length} of {count(entries.length, "override")} selected
                  </span>
                </div>
                <div className="overrides-list">
                  {groups.map(([typeId, list]) => {
                    const keys = list.map(keyOf);
                    const groupAll = keys.every((k) => selected.has(k));
                    return (
                      <section className="overrides-group" key={typeId}>
                        <h4>
                          <label className="settings-check">
                            <input type="checkbox" checked={groupAll} disabled={busy} onChange={(e) => toggle(keys, e.target.checked)} />
                            {typeTitle(typeId, list[0])}
                          </label>
                        </h4>
                        {list.map((entry) => (
                          <label className="overrides-entry" key={keyOf(entry)}>
                            <input type="checkbox" checked={selected.has(keyOf(entry))} disabled={busy} onChange={(e) => toggle([keyOf(entry)], e.target.checked)} />
                            <div className="overrides-entry-text">
                              <div className="overrides-entry-label">
                                <span>{label(entry)}</span>
                                {entry.reset && <span className="setting-badge overrides">shared taken away</span>}
                                {entry.sameAsShared && <span className="setting-badge faint" title="Says what the shared file says: it comes from there anyway, and goes from this file at the next activation">same as shared</span>}
                              </div>
                              <div className="overrides-entry-values">
                                <span className="overrides-value">
                                  <span className="muted">shared</span> <code>{entry.inShared ? shown(entry.sharedValue) : "not set"}</code>
                                </span>
                                <span className="overrides-value">
                                  <span className="muted">here</span> <code>{entry.reset ? "the source's value" : shown(entry.value)}</code>
                                </span>
                              </div>
                            </div>
                          </label>
                        ))}
                      </section>
                    );
                  })}
                </div>
              </>
            )}
          </>
        )}
        {/* its own line: four buttons leave no room beside them for a sentence naming a file */}
        {message && <div className="dialog-body muted">{message}</div>}
        <div className="dialog-row">
          <div className="header-spacer" />
          <button className="action-button danger" disabled={busy || chosen.length === 0} onClick={() => void discard()} title="Back to the shared override, or the source, in the draft">
            <IconArrowBackUp size={14} stroke={1.8} /> Discard{chosen.length > 0 ? ` ${chosen.length}` : ""}
          </button>
          <button
            className="action-button"
            disabled={busy || !files || (chosen.length === 0 && !files.sharedExists)}
            onClick={() => void download()}
            title={chosen.length > 0 ? "The shared file with the selected overrides moved in, to put into source control. Nothing here changes." : "The shared file as it is"}
          >
            <IconDownload size={14} stroke={1.8} /> Download shared file
          </button>
          <button className={"action-button dialog-confirm" + (files?.development ? " primary" : "")} disabled={busy || chosen.length === 0} onClick={() => void move()}>
            <IconFileImport size={14} stroke={1.8} /> Move{chosen.length > 0 ? ` ${chosen.length}` : ""} into shared
          </button>
          <button className="action-button" onClick={onClose} disabled={busy}>
            Close
          </button>
        </div>
      </div>
    </div>
  );
}

const keyOf = (e: OverridePath | InstallationOverride) => e.typeId + "/" + (e.propertyId ?? "") + "/" + e.attribute;
const pathOf = (e: InstallationOverride): OverridePath => ({ typeId: e.typeId, propertyId: e.propertyId, attribute: e.attribute });

function shown(value: unknown): string {
  if (value === null || value === undefined) return "not set";
  if (value === true) return "yes";
  if (value === false) return "no";
  if (value === "") return "empty";
  return typeof value === "string" ? value : JSON.stringify(value);
}

function count(n: number, one: string, many?: string): string {
  return `${n} ${n === 1 ? one : (many ?? one + "s")}`;
}

function saveText(fileName: string, content: string) {
  const url = URL.createObjectURL(new Blob([content], { type: "application/json" }));
  const a = document.createElement("a");
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
