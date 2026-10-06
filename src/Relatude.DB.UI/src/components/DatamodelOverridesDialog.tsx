import { useEffect, useState } from "react";
import { showConfirm, showError, showInfo } from "../dialogs";
import {
  fetchDataOverrides,
  moveOverridesToSettings,
  overridesKey,
  settingsOverridesText,
  type DataOverride,
  type DataOverrides,
  type OverridePath,
  type OverridesFileInfo,
  type OverridesJson,
} from "../server/datamodel";
import { declaringType, setOverride } from "../server/overrides";
import type { EditorContext } from "./DatamodelEditors";
import { count, DataDialog, DataValue, deployedCopyWarning, saveText, shownValue, type DataDialogEntry } from "./DataDialog";
import { RemovedTag } from "./SourceTag";

/**
 * The datamodel overrides on THIS SERVER, and moving them into SHARED.
 *
 * SHARED, relatude.settings/[short name]/datamodel.json, is part of the application: in source control and
 * deployed with it. THIS SERVER, overrides/datamodel.overrides.json with the database, holds what the editor changed
 * on this installation and is merged over SHARED. The list is the THIS SERVER file as the last activation wrote
 * it, entry by entry, beside what SHARED says.
 *
 * Moving writes the selection into SHARED and takes it out of THIS SERVER: nothing in force changes, so the
 * database is not reopened. That only reaches the other installations through source control, so outside the
 * Development environment - where relatude.settings is usually the deployed copy the next deployment replaces
 * - the dialog says so, and offers the SHARED file as a download instead. Discarding goes back to the
 * SHARED override (or the source) in the draft, which is activated like any other change.
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
  const [view, setView] = useState<DataOverrides | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    fetchDataOverrides(storeId)
      .then(setView)
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, [storeId]);

  const files = view?.files;
  const byKey = new Map((view?.entries ?? []).map((e) => [keyOf(e), e]));
  const draftDiffers = overridesKey(ctx.model.Overrides) !== overridesKey(activeOverrides);

  function label(e: DataOverride): string {
    const property = e.propertyId ? declaringType(ctx.model, e.propertyId)?.Properties[e.propertyId] : undefined;
    const fields = property ? [...ctx.schema.propertyCommon, ...(ctx.schema.propertyByType[property.PropertyType] ?? [])] : e.propertyId ? ctx.schema.propertyCommon : ctx.schema.nodeType;
    const attribute = fields.find((f) => f.path === e.attribute)?.label ?? e.attribute;
    return e.propertyId ? (property?.CodeName ?? e.propertyName ?? e.propertyId) + " · " + attribute : attribute;
  }
  function typeTitle(e: DataOverride): string {
    const t = ctx.model.NodeTypes[e.typeId];
    return t ? (t.Namespace ? t.Namespace + "." + t.CodeName : t.CodeName) : (e.typeName ?? e.typeId) + " (not in the model)";
  }
  const paths = (keys: string[]) => keys.map((k) => byKey.get(k)).filter((e): e is DataOverride => !!e).map(pathOf);

  const entries: DataDialogEntry[] | null = view
    ? view.entries.map((e) => ({
        key: keyOf(e),
        group: typeTitle(e),
        label: label(e),
        badges: (
          <>
            {e.reset && <RemovedTag small title="THIS SERVER takes the SHARED override away on this installation, so the source's value applies here" />}
            {e.sameAsSettings && (
              <span className="setting-badge faint" title="Says what SHARED says: it comes from there anyway, and goes from THIS SERVER at the next activation">
                same as shared
              </span>
            )}
          </>
        ),
        values: (
          <>
            <DataValue side="settings">{e.inSettings ? shownValue(e.settingsValue) : "not set"}</DataValue>
            <DataValue side="data">{e.reset ? "the source's value" : shownValue(e.value)}</DataValue>
          </>
        ),
      }))
    : null;

  async function move(keys: string[]): Promise<void> {
    if (!files || keys.length === 0) return;
    const { ok } = await showConfirm(
      `Move ${count(keys.length, "override")} into ${files.settingsLocation}?`,
      [
        `${keys.length === 1 ? "It is" : "They are"} written into SHARED, ${files.settingsLocation}, which every installation has, and removed from THIS SERVER, ${files.dataLocation}. Nothing in force changes, so the database is not reopened.`,
        `${files.settingsLocation} is rewritten as a whole, so comments in it are not kept.`,
        files.development ? `Commit ${files.settingsLocation} to take ${keys.length === 1 ? "it" : "them"} to the other installations.` : deployedCopyWarning(files.environment, files.settingsLocation, true),
      ].join(" "),
      { confirmLabel: "Move", danger: !files.development },
    );
    if (!ok) return;
    setBusy(true);
    try {
      const result = await moveOverridesToSettings(storeId, paths(keys));
      setView(result.view);
      onFilesChanged(result.view.files);
      setMessage(`${count(result.moved, "override")} moved into ${result.view.files.settingsLocation}.`);
    } catch (e) {
      await showError("Could not move the overrides", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  async function download(keys: string[]): Promise<void> {
    if (!files) return;
    setBusy(true);
    try {
      const result = await settingsOverridesText(storeId, paths(keys));
      saveText(result.fileName, result.content);
      setMessage(
        keys.length > 0
          ? `Downloaded ${result.fileName} with ${count(keys.length, "override")} moved in. Nothing here was changed: put it in ${result.settingsLocation} in source control.`
          : `Downloaded ${result.fileName} as it is.`,
      );
    } catch (e) {
      await showError("Could not make the SHARED file", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  async function discard(keys: string[]): Promise<void> {
    if (!files || keys.length === 0) return;
    const { ok } = await showConfirm(
      `Discard ${count(keys.length, "override")} on THIS SERVER?`,
      `Each goes back to the SHARED override, or to what the source says where SHARED has none. This changes the draft: activate it to apply the change.`,
      { confirmLabel: "Discard in the draft" },
    );
    if (!ok) return;
    const picked = keys.map((k) => byKey.get(k)).filter((e): e is DataOverride => !!e);
    ctx.update((m) => {
      for (const e of picked) setOverride(m, e.typeId, e.propertyId, e.attribute, e.inSettings ? e.settingsValue : undefined);
    });
    onClose();
    await showInfo("Discarded in the draft", `${count(picked.length, "override")} on THIS SERVER ${picked.length === 1 ? "goes" : "go"} back to SHARED or the source when the draft is activated.`);
  }

  return (
    <DataDialog
      title="Datamodel overrides saved on this installation"
      intro={
        files && (
          <>
            Overrides set in the editor are kept on THIS SERVER, <code>{files.dataLocation}</code>, merged over the ones every installation has in SHARED, <code>{files.settingsLocation}</code> -
            part of the application, in source control and deployed with it. Move overrides there to have them on every installation, or discard them to go back to SHARED, or to the source.
          </>
        )
      }
      warnings={[
        files?.settingsError ?? null,
        files && !files.development ? deployedCopyWarning(files.environment, files.settingsLocation, true) : null,
        draftDiffers ? "The draft changes overrides that are not activated yet. They are not on THIS SERVER, so they are not listed here: activate the draft first to move them." : null,
      ]}
      entries={entries}
      error={error}
      loadingLabel="Reading the overrides files…"
      emptyText={
        <>
          Nothing is overridden on THIS SERVER: every override in force comes from <code>{files?.settingsLocation}</code>.
        </>
      }
      message={message}
      busy={busy}
      discard={{ label: (n) => "Discard" + (n > 0 ? " " + n : ""), title: "Back to SHARED, or the source, in the draft", onRun: discard }}
      download={{
        label: () => "Download shared file",
        title: "The SHARED file with the selected overrides moved in, to put into source control. Nothing here changes.",
        allowNone: files?.settingsExists ?? false,
        onRun: download,
      }}
      move={{ label: (n) => "Move" + (n > 0 ? " " + n : "") + " into shared", onRun: move }}
      onClose={onClose}
    />
  );
}

const keyOf = (e: OverridePath | DataOverride) => e.typeId + "/" + (e.propertyId ?? "") + "/" + e.attribute;
const pathOf = (e: DataOverride): OverridePath => ({ typeId: e.typeId, propertyId: e.propertyId, attribute: e.attribute });
