import { useState } from "react";
import { showConfirm, showError } from "../dialogs";
import { discardEndpointData, moveEndpointData, type EndpointDataEntry, type EndpointsInfo } from "../server/graphql";
import { count, DataDialog, DataValue, deployedCopyWarning, type DataDialogEntry } from "./DataDialog";
import { RemovedTag } from "./SourceTag";

/**
 * The GraphQL endpoint definitions on THIS SERVER - endpoints added, changed or taken away on this installation -
 * and moving them into SHARED (relatude.settings/[short name]/graphql/, in source control and deployed with
 * the application) or discarding them. A whole definition is one entry: the THIS SERVER file replaces the SHARED
 * one with the same id, and a marker on THIS SERVER takes a SHARED endpoint away.
 */
export function EndpointsDataDialog({ storeId, info, onClose, onChanged }: { storeId: string; info: EndpointsInfo; onClose: () => void; onChanged: () => void }) {
  const [entries, setEntries] = useState<EndpointDataEntry[]>(info.dataEntries);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const byId = new Map(entries.map((e) => [e.id, e]));
  const settingsFolder = info.settingsFolder ?? "relatude.settings/graphql";
  const dataFolder = info.dataFolder ?? "overrides/graphql";

  const list: DataDialogEntry[] = entries.map((e) => ({
    key: e.id,
    group: "Endpoints",
    label: e.name,
    badges: (
      <>
        {e.change === "added" && <span className="setting-badge overrides">added here</span>}
        {e.change === "changed" && <span className="setting-badge overrides">changed here</span>}
        {e.change === "removed" && <RemovedTag small title="THIS SERVER takes this SHARED endpoint away on this installation" />}
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

  async function move(ids: string[]): Promise<void> {
    if (ids.length === 0) return;
    const { ok } = await showConfirm(
      `Move ${count(ids.length, "endpoint definition")} into ${settingsFolder}?`,
      [
        `${ids.length === 1 ? "It is" : "They are"} written into SHARED, ${settingsFolder}, which every installation has, and removed from THIS SERVER, ${dataFolder}. An endpoint taken away here has its SHARED file deleted. Nothing that is served changes.`,
        info.development ? `Commit ${settingsFolder} to take ${ids.length === 1 ? "it" : "them"} to the other installations.` : deployedCopyWarning(info.environment, settingsFolder, false),
      ].join(" "),
      { confirmLabel: "Move", danger: !info.development },
    );
    if (!ok) return;
    setBusy(true);
    try {
      const result = await moveEndpointData(storeId, ids);
      setEntries(result.dataEntries);
      setMessage(`${count(result.moved, "endpoint definition")} moved into ${settingsFolder}.`);
      onChanged();
    } catch (e) {
      await showError("Could not move the endpoint definitions", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  async function discard(ids: string[]): Promise<void> {
    if (ids.length === 0) return;
    const picked = ids.map((id) => byId.get(id)).filter((e): e is EndpointDataEntry => !!e);
    const added = picked.filter((e) => e.change === "added").length;
    const removed = picked.filter((e) => e.change === "removed").length;
    const { ok } = await showConfirm(
      `Discard ${count(ids.length, "endpoint definition")} on THIS SERVER?`,
      [
        "Each endpoint goes back to its definition in SHARED.",
        added > 0 ? `${count(added, "endpoint is", "endpoints are")} only on THIS SERVER and ${added === 1 ? "is" : "are"} no longer served.` : "",
        removed > 0 ? `${count(removed, "endpoint")} taken away here ${removed === 1 ? "is" : "are"} served again.` : "",
      ]
        .filter(Boolean)
        .join(" "),
      { confirmLabel: "Discard", danger: true },
    );
    if (!ok) return;
    setBusy(true);
    try {
      const result = await discardEndpointData(storeId, ids);
      setEntries(result.dataEntries);
      setMessage(`${count(result.discarded, "endpoint definition")} discarded.`);
      onChanged();
    } catch (e) {
      await showError("Could not discard the endpoint definitions", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <DataDialog
      title="GraphQL endpoints"
      intro={
        <>
          Endpoints defined or changed here are saved on THIS SERVER, <code>{dataFolder}</code>, and replace the ones every installation has in SHARED, <code>{settingsFolder}</code> -
          part of the application, in source control and deployed with it. Move them there to have them on every installation, or discard them to go back to SHARED.
        </>
      }
      warnings={[!info.development ? deployedCopyWarning(info.environment, settingsFolder, false) : null]}
      entries={list}
      loadingLabel="Reading the endpoint definitions…"
      emptyText={
        <>
          Nothing is saved on THIS SERVER: every endpoint is defined in <code>{settingsFolder}</code>.
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
