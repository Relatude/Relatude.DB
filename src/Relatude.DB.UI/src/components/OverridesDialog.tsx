import { useCallback, useEffect, useState } from "react";
import { IconLock } from "@tabler/icons-react";
import { showConfirm, showError } from "../dialogs";
import { discardOverrides, fetchOverrides, moveOverrides, type OverrideEntry, type OverrideGroup, type OverridesView } from "../server/settings";
import { ShareButton } from "./ShareButton";
import { count, DataDialog, DataValue, type DataDialogEntry } from "./DataDialog";

/**
 * The settings on THIS SERVER: relatude.db.overrides.json, every setting saved from the settings pages, which the
 * server keeps apart from relatude.db.json (SHARED) and merges over it at every start. From here a
 * selection - or all of it - is moved into relatude.db.json, making it part of the application's own
 * settings, or discarded, which puts back what relatude.db.json says.
 *
 * The file is one for the whole server; a page that only writes part of it - one database's settings,
 * the databases list, what Activity records - passes a scope, and the dialog lists that part. Without one
 * it lists everything: the server's own settings and every database. A setting the configuration section
 * decides is listed but cannot be picked: configuration never goes into relatude.db.json, and dropping
 * the file's value would change nothing that is running.
 */
export function OverridesDialog({ scope, onClose, onChanged }: { scope?: OverridesScope; onClose: () => void; onChanged: () => void }) {
  const [view, setView] = useState<OverridesView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    fetchOverrides()
      .then(setView)
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, []);

  const groups = view ? scopedGroups(view, scope) : [];
  const all = groups.flatMap((g) => g.entries);
  const byPath = new Map(all.map((e) => [e.path, e]));
  const settingsFile = view?.settingsFile ?? "relatude.db.json";

  const entries: DataDialogEntry[] | null = view
    ? groups.flatMap((group) =>
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
      title={scope?.title ?? "Settings"}
      intro={
        view && (
          <>
            Settings saved on these pages are kept on THIS SERVER, <code>{view.file}</code>, apart from SHARED, <code>{settingsFile}</code>, and merged over it at every
            start{view.configSection ? `, with the ${view.configSection} configuration section (APPSETTINGS) over both` : ""}. Move them into <code>{settingsFile}</code> to
            make them part of the application's own settings, or discard them to go back to what it says.
            {view.configSection ? ` Settings from the ${view.configSection} section are never moved.` : ""}
            {scope?.filter ? ` Listed here: what this page writes. Server settings lists everything in the file.` : ""}
          </>
        )
      }
      warnings={view?.error ? [view.error] : []}
      entries={entries}
      error={error}
      loadingLabel="Reading the overrides file…"
      emptyText={
        <>
          Nothing {scope?.filter ? "of this page's " : ""}is saved in <code>{view?.file}</code>: every setting comes from <code>{settingsFile}</code>, configuration or its default.
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

/** The part of relatude.db.overrides.json a page writes, and so lists and counts. */
export interface OverridesScope {
  /** what is moved, after "Move to shared": "Settings of MyDatabase" */
  title: string;
  /** the entries this page writes; everything when left out */
  filter?: (entry: OverrideEntry, group: OverrideGroup) => boolean;
}

function scopedGroups(view: OverridesView, scope?: OverridesScope): OverrideGroup[] {
  const filter = scope?.filter;
  if (!filter) return view.groups;
  return view.groups.map((g) => ({ ...g, entries: g.entries.filter((e) => filter(e, g)) })).filter((g) => g.entries.length > 0);
}

/** The path below a database's own settings, "" for the database itself; null for a server setting. */
export function databasePath(entry: OverrideEntry, group: OverrideGroup): string | null {
  if (group.scope !== "database") return null;
  const close = entry.path.indexOf("]");
  return close < 0 || close === entry.path.length - 1 ? "" : entry.path.slice(close + 2);
}

/**
 * The Move to shared button of a page that writes relatude.db.overrides.json, with its dialog. It reads
 * the file itself, so the status is this page's part of it; changeKey is anything that changes when the
 * page has saved, which reads it again.
 */
export function SettingsShareButton({ scope, changeKey, onChanged, className }: { scope: OverridesScope; changeKey?: unknown; onChanged?: () => void; className?: string }) {
  const [view, setView] = useState<OverridesView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  const read = useCallback(() => {
    fetchOverrides()
      .then((v) => {
        setView(v);
        setError(null);
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, []);
  useEffect(read, [read, changeKey]);
  // the server writes straight into relatude.db.json: there is nothing kept on this server to move
  if (view && !view.enabled) return null;
  const count = view ? scopedGroups(view, scope).reduce((n, g) => n + g.entries.length, 0) : null;
  return (
    <>
      <ShareButton
        className={className}
        count={count}
        error={error ?? view?.error ?? null}
        title={`What is saved here is kept on THIS SERVER, ${view?.file ?? "relatude.db.overrides.json"}, merged over SHARED, ${view?.settingsFile ?? "relatude.db.json"}, at every start.`}
        onClick={() => setOpen(true)}
      />
      {open && (
        <OverridesDialog
          scope={scope}
          onClose={() => {
            setOpen(false);
            read();
          }}
          onChanged={() => {
            read();
            onChanged?.();
          }}
        />
      )}
    </>
  );
}
