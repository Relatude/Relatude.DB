import { useState } from "react";
import { showConfirm, showError } from "../dialogs";
import { discardCustomLogData, moveCustomLogData, type CustomLogDataEntry, type CustomLogsInfo } from "../server/customLogs";
import { count, DataDialog, DataValue, deployedCopyWarning, type DataDialogEntry } from "./DataDialog";
import { RemovedTag } from "./SourceTag";

/**
 * The custom log definitions on THIS SERVER - logs added, changed or taken away on this installation - and moving
 * them into SHARED (relatude.settings/[short name]/logs/, in source control and deployed with the
 * application) or discarding them. A whole definition is one entry: the THIS SERVER file replaces the SHARED one
 * with the same key, and a marker on THIS SERVER takes a SHARED log away.
 */
export function CustomLogsDataDialog({ storeId, info, onClose, onChanged }: { storeId: string; info: CustomLogsInfo; onClose: () => void; onChanged: () => void }) {
  const [entries, setEntries] = useState<CustomLogDataEntry[]>(info.dataEntries);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const byKey = new Map(entries.map((e) => [e.key, e]));
  const settingsFolder = info.settingsFolder ?? "relatude.settings";

  const list: DataDialogEntry[] = entries.map((e) => ({
    key: e.key,
    group: "Logs",
    label: (
      <>
        {e.name || e.key} <span className="muted">{e.key}</span>
      </>
    ),
    badges: (
      <>
        {e.change === "added" && <span className="setting-badge overrides">added here</span>}
        {e.change === "changed" && <span className="setting-badge overrides">changed here</span>}
        {e.change === "removed" && <RemovedTag small title="THIS SERVER takes this SHARED log away on this installation" />}
        {e.change === "same" && (
          <span className="setting-badge faint" title="Says what SHARED says: it comes from there anyway">
            same as shared
          </span>
        )}
      </>
    ),
    values: (
      <>
        <DataValue side="settings">{e.settingsFile ?? "not there"}</DataValue>
        <DataValue side="data">{e.change === "removed" ? "taken away" : e.dataFile}</DataValue>
      </>
    ),
  }));

  async function move(keys: string[]): Promise<void> {
    if (keys.length === 0) return;
    const { ok } = await showConfirm(
      `Move ${count(keys.length, "log definition")} into ${settingsFolder}?`,
      [
        `${keys.length === 1 ? "It is" : "They are"} written into SHARED, ${settingsFolder}, which every installation has, and removed from THIS SERVER, ${info.dataFolder}. A log taken away here has its SHARED file deleted. Nothing that runs changes.`,
        info.development ? `Commit ${settingsFolder} to take ${keys.length === 1 ? "it" : "them"} to the other installations.` : deployedCopyWarning(info.environment, settingsFolder, false),
      ].join(" "),
      { confirmLabel: "Move", danger: !info.development },
    );
    if (!ok) return;
    setBusy(true);
    try {
      const result = await moveCustomLogData(storeId, keys);
      setEntries(result.dataEntries);
      setMessage(`${count(result.moved, "log definition")} moved into ${settingsFolder}.`);
      onChanged();
    } catch (e) {
      await showError("Could not move the log definitions", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  async function discard(keys: string[]): Promise<void> {
    if (keys.length === 0) return;
    const picked = keys.map((k) => byKey.get(k)).filter((e): e is CustomLogDataEntry => !!e);
    const added = picked.filter((e) => e.change === "added").length;
    const removed = picked.filter((e) => e.change === "removed").length;
    const { ok } = await showConfirm(
      `Discard ${count(keys.length, "log definition")} on THIS SERVER?`,
      [
        "Each log goes back to its definition in SHARED; entries are moved as a change of definition would move them.",
        added > 0 ? `${count(added, "log is", "logs are")} only on THIS SERVER and ${added === 1 ? "goes" : "go"}: what ${added === 1 ? "it" : "they"} recorded is kept, for a log made later with the same key.` : "",
        removed > 0 ? `${count(removed, "log")} taken away here ${removed === 1 ? "comes" : "come"} back.` : "",
      ]
        .filter(Boolean)
        .join(" "),
      { confirmLabel: "Discard", danger: true },
    );
    if (!ok) return;
    setBusy(true);
    try {
      const result = await discardCustomLogData(storeId, keys);
      setEntries(result.dataEntries);
      setMessage(`${count(result.discarded.length, "log definition")} discarded.`);
      onChanged();
    } catch (e) {
      await showError("Could not discard the log definitions", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <DataDialog
      title="Log definitions"
      intro={
        <>
          Logs defined or changed here are saved on THIS SERVER, <code>{info.dataFolder}</code>, and replace the ones every installation has in SHARED, <code>{settingsFolder}</code> -
          part of the application, in source control and deployed with it. Move them there to have them on every installation, or discard them to go back to SHARED.
        </>
      }
      warnings={[!info.development ? deployedCopyWarning(info.environment, settingsFolder, false) : null]}
      entries={list}
      loadingLabel="Reading the log definitions…"
      emptyText={
        <>
          Nothing is saved on THIS SERVER: every log is defined in <code>{settingsFolder}</code>.
        </>
      }
      message={message}
      busy={busy}
      discard={{ label: (n) => "Discard" + (n > 0 ? " " + n : ""), title: "Back to SHARED", onRun: discard }}
      move={{ label: (n) => "Move" + (n > 0 ? " " + n : "") + " into shared", onRun: move }}
      onClose={onClose}
    />
  );
}
