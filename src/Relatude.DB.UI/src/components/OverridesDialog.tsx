import { useEffect, useState } from "react";
import { IconLock } from "@tabler/icons-react";
import { showConfirm, showError } from "../dialogs";
import { discardOverrides, fetchOverrides, moveOverrides, type OverrideEntry, type OverridesView } from "../server/settings";
import { count, DataDialog, DataValue, type DataDialogEntry } from "./DataDialog";

/**
 * The settings on THIS SERVER: relatude.db.overrides.json, every setting saved from the settings pages, which the
 * server keeps apart from relatude.db.json (SHARED) and merges over it at every start. From here a
 * selection - or all of it - is moved into relatude.db.json, making it part of the application's own
 * settings, or discarded, which puts back what relatude.db.json says.
 *
 * The file is one for the whole server, so the list is too: the server's own settings and every
 * database, whichever settings page it was opened from. A setting the configuration section decides is
 * listed but cannot be picked: configuration never goes into relatude.db.json, and dropping the file's
 * value would change nothing that is running.
 */
export function OverridesDialog({ onClose, onChanged }: { onClose: () => void; onChanged: () => void }) {
  const [view, setView] = useState<OverridesView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    fetchOverrides()
      .then(setView)
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, []);

  const all = (view?.groups ?? []).flatMap((g) => g.entries);
  const byPath = new Map(all.map((e) => [e.path, e]));
  const settingsFile = view?.settingsFile ?? "relatude.db.json";

  const entries: DataDialogEntry[] | null = view
    ? view.groups.flatMap((group) =>
        group.entries.map((entry) => ({
          key: entry.path,
          group: group.scope === "database" ? group.title + " · database" : group.title,
          label: entry.label,
          badges: (
            <>
              {entry.kind === "added" && <span className="setting-badge overrides">added here</span>}
              {entry.kind === "removed" && <span className="setting-badge overrides">removed here</span>}
              {entry.secret && <span className="setting-badge faint">secret</span>}
            </>
          ),
          values:
            entry.kind === "value" ? (
              <>
                <DataValue side="settings">{shown(entry.fileValue, entry.fileHasValue, entry.secret)}</DataValue>
                <DataValue side="data">{shown(entry.value, entry.hasValue, entry.secret)}</DataValue>
              </>
            ) : (
              entry.summary && <span className="muted">{entry.summary}</span>
            ),
          note: note(entry),
          selectable: entry.canMove || entry.canDiscard,
        })),
      )
    : null;

  function settle(next: OverridesView): void {
    setView(next);
    onChanged();
  }

  async function move(paths: string[]): Promise<void> {
    if (!view || paths.length === 0) return;
    const secrets = paths.filter((p) => byPath.get(p)?.secret).length;
    const { ok } = await showConfirm(
      `Move ${count(paths.length, "setting")} into ${settingsFile}?`,
      [
        `${paths.length === 1 ? "It is" : "They are"} written into ${settingsFile} - SHARED, part of the application - and removed from ${view.file} (THIS SERVER). Nothing that is running changes: the same values then come from the other file.`,
        `${settingsFile} is rewritten as a whole, so comments in it are not kept.`,
        secrets > 0
          ? `${count(secrets, "of them is a secret", "of them are secrets")}, which ${settingsFile} then holds in plain text. Keep that file out of source control, or leave secrets to the configuration section.`
          : "",
      ]
        .filter(Boolean)
        .join(" "),
      { confirmLabel: "Move" },
    );
    if (!ok) return;
    setBusy(true);
    try {
      const result = await moveOverrides(paths);
      settle(result.overrides);
      setMessage(`${count(result.moved.length, "setting")} moved into ${settingsFile}.`);
    } catch (e) {
      await showError("Could not move the settings", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  async function discard(paths: string[]): Promise<void> {
    if (!view || paths.length === 0) return;
    const { ok } = await showConfirm(
      `Discard ${count(paths.length, "change")}?`,
      `Each goes back to what ${settingsFile} says and is removed from ${view.file}. The values saved here are lost.`
        + " A setting that only applies when a database opens takes effect at its next open.",
      { confirmLabel: "Discard", danger: true },
    );
    if (!ok) return;
    setBusy(true);
    try {
      const result = await discardOverrides(paths);
      settle(result.overrides);
      setMessage(`${count(result.discarded.length, "change")} discarded.`);
      if (result.rejected.length > 0) {
        await showError(
          "Some changes were not discarded",
          `${result.discarded.length} discarded, ${result.rejected.length} left as they were.`,
          result.rejected.map((r) => `${byPath.get(r.path)?.label ?? r.path}: ${r.reason}`),
        );
      }
    } catch (e) {
      await showError("Could not discard the changes", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <DataDialog
      title={"Settings saved on this installation" + (view?.file ? " — " + view.file : "")}
      intro={
        view && (
          <>
            Settings saved on these pages are kept on THIS SERVER, <code>{view.file}</code>, apart from SHARED, <code>{settingsFile}</code>, and merged over it at every
            start{view.configSection ? `, with the ${view.configSection} configuration section (APPSETTINGS) over both` : ""}. Move them into <code>{settingsFile}</code> to
            make them part of the application's own settings, or discard them to go back to what it says.
            {view.configSection ? ` Settings from the ${view.configSection} section are never moved.` : ""}
          </>
        )
      }
      warnings={view?.error ? [view.error] : []}
      entries={entries}
      error={error}
      loadingLabel="Reading the overrides file…"
      emptyText={
        <>
          Nothing is saved in <code>{view?.file}</code>: every setting comes from <code>{settingsFile}</code>, configuration or its default.
        </>
      }
      message={message}
      busy={busy}
      discard={{ label: (n) => "Discard" + (n > 0 ? " " + n : ""), title: "Put back what " + settingsFile + " says", appliesTo: (p) => byPath.get(p)?.canDiscard ?? false, onRun: discard }}
      move={{ label: (n) => "Move" + (n > 0 ? " " + n : "") + " into " + settingsFile, appliesTo: (p) => byPath.get(p)?.canMove ?? false, onRun: move }}
      onClose={onClose}
    />
  );
}

function note(entry: OverrideEntry) {
  const pickable = entry.canMove || entry.canDiscard;
  // neither action applies: say why, the way a locked setting on the page says it
  const locked = pickable ? null : (entry.moveBlocked ?? entry.discardBlocked ?? null);
  return (
    <>
      {entry.where && <div className="overrides-entry-where">{entry.where}</div>}
      {locked && (
        <span>
          <IconLock size={12} stroke={1.8} /> {locked}
        </span>
      )}
      {pickable && !entry.canMove && entry.moveBlocked && <span>Can be discarded, not moved: {entry.moveBlocked}</span>}
      {pickable && !entry.canDiscard && entry.discardBlocked && <span>Can be moved, not discarded: {entry.discardBlocked}</span>}
    </>
  );
}

// a value as the list shows it: what a secret is never shown as, and the three ways of having nothing
function shown(value: unknown, has: boolean, secret: boolean): string {
  if (secret) return has ? "secret set" : "not set";
  if (value === null || value === undefined) return "not set";
  if (value === "") return "empty";
  return typeof value === "string" ? value : JSON.stringify(value);
}
