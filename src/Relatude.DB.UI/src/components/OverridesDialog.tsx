import { useEffect, useMemo, useState } from "react";
import { IconArrowBackUp, IconFileDiff, IconFileImport, IconLock } from "@tabler/icons-react";
import { DialogTools } from "./DialogTools";
import { Loading } from "./Loading";
import { showConfirm, showError } from "../dialogs";
import { discardOverrides, fetchOverrides, moveOverrides, type OverrideEntry, type OverridesView } from "../server/settings";

/**
 * What relatude.db.overrides.json holds: every setting saved from the settings pages, which the server
 * keeps apart from relatude.db.json and merges over it at every start. From here a selection - or all
 * of it - is moved into relatude.db.json, making it part of the application's own settings, or
 * discarded, which puts back what relatude.db.json says.
 *
 * The file is one for the whole server, so the list is too: the server's own settings and every
 * database, whichever settings page it was opened from. A setting the configuration section decides
 * is listed but cannot be picked: configuration never goes into relatude.db.json, and dropping the
 * file's value would change nothing that is running.
 *
 * Nothing starts selected. Both actions rewrite a file, and each asks first, naming how many settings
 * it touches.
 */
export function OverridesDialog({ onClose, onChanged }: { onClose: () => void; onChanged: () => void }) {
  const [view, setView] = useState<OverridesView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    fetchOverrides()
      .then(setView)
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, []);

  const entries = useMemo(() => (view?.groups ?? []).flatMap((g) => g.entries), [view]);
  const selectable = useMemo(() => entries.filter((e) => e.canMove || e.canDiscard), [entries]);
  const chosen = entries.filter((e) => selected.has(e.path));
  const toMove = chosen.filter((e) => e.canMove);
  const toDiscard = chosen.filter((e) => e.canDiscard);
  const allSelected = selectable.length > 0 && selectable.every((e) => selected.has(e.path));

  function toggle(paths: string[], on: boolean): void {
    setSelected((prev) => {
      const next = new Set(prev);
      for (const path of paths) {
        if (on) next.add(path);
        else next.delete(path);
      }
      return next;
    });
  }

  function settle(next: OverridesView): void {
    setView(next);
    // what is no longer in the file can no longer be selected
    const still = new Set(next.groups.flatMap((g) => g.entries.map((e) => e.path)));
    setSelected((prev) => new Set([...prev].filter((p) => still.has(p))));
    onChanged();
  }

  async function move(): Promise<void> {
    if (!view || toMove.length === 0) return;
    const secrets = toMove.filter((e) => e.secret).length;
    const { ok } = await showConfirm(
      `Move ${count(toMove.length, "setting")} into ${view.settingsFile}?`,
      [
        `${toMove.length === 1 ? "It is" : "They are"} written into ${view.settingsFile} and removed from ${view.file}. Nothing that is running changes: the same values then come from the other file.`,
        `${view.settingsFile} is rewritten as a whole, so comments in it are not kept.`,
        secrets > 0
          ? `${count(secrets, "of them is a secret", "of them are secrets")}, which ${view.settingsFile} then holds in plain text. Keep that file out of source control, or leave secrets to the configuration section.`
          : "",
      ]
        .filter(Boolean)
        .join(" "),
      { confirmLabel: "Move" },
    );
    if (!ok) return;
    setBusy(true);
    try {
      const result = await moveOverrides(toMove.map((e) => e.path));
      settle(result.overrides);
      setMessage(`${count(result.moved.length, "setting")} moved into ${view.settingsFile}.`);
    } catch (e) {
      await showError("Could not move the settings", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  async function discard(): Promise<void> {
    if (!view || toDiscard.length === 0) return;
    const { ok } = await showConfirm(
      `Discard ${count(toDiscard.length, "change")}?`,
      `Each goes back to what ${view.settingsFile} says and is removed from ${view.file}. The values saved here are lost.`
        + " A setting that only applies when a database opens takes effect at its next open.",
      { confirmLabel: "Discard", danger: true },
    );
    if (!ok) return;
    setBusy(true);
    try {
      const result = await discardOverrides(toDiscard.map((e) => e.path));
      settle(result.overrides);
      setMessage(`${count(result.discarded.length, "change")} discarded.`);
      if (result.rejected.length > 0) {
        const labels = new Map(entries.map((e) => [e.path, e.label]));
        await showError(
          "Some changes were not discarded",
          `${result.discarded.length} discarded, ${result.rejected.length} left as they were.`,
          result.rejected.map((r) => `${labels.get(r.path) ?? r.path}: ${r.reason}`),
        );
      }
    } catch (e) {
      await showError("Could not discard the changes", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && !busy && onClose()}>
      <div
        className="dialog dialog-wide overrides-dialog"
        role="dialog"
        aria-label="Overrides"
        onKeyDown={(e) => {
          if (e.key === "Escape" && !busy) {
            e.preventDefault();
            onClose();
          }
        }}
      >
        <h3>
          <IconFileDiff size={16} stroke={1.8} className="tone-override" /> Overrides{view?.file ? ` — ${view.file}` : ""}
          <DialogTools onClose={onClose} />
        </h3>
        {error && <div className="dialog-body dialog-title-error">{error}</div>}
        {!error && !view && <Loading label="Reading the overrides file…" />}
        {view?.error && <div className="dialog-body dialog-title-error">{view.error}</div>}
        {view && (
          <>
            <div className="dialog-body overrides-intro">
              Settings saved on these pages are kept in <code>{view.file}</code>, apart from <code>{view.settingsFile}</code>, and merged over it at
              every start{view.configSection ? `, with the ${view.configSection} configuration section over both` : ""}. Move them into{" "}
              <code>{view.settingsFile}</code> to make them part of the application's own settings, or discard them to go back to what it says.
              {view.configSection ? ` Settings from the ${view.configSection} section are never moved.` : ""}
            </div>
            {entries.length === 0 ? (
              <div className="overrides-empty">
                Nothing is saved in <code>{view.file}</code>: every setting comes from <code>{view.settingsFile}</code>, configuration or its default.
              </div>
            ) : (
              <>
                <div className="overrides-toolbar">
                  <label className="settings-check">
                    <input type="checkbox" checked={allSelected} disabled={busy || selectable.length === 0} onChange={(e) => toggle(selectable.map((x) => x.path), e.target.checked)} />
                    Select all
                  </label>
                  <span className="muted">
                    {chosen.length} of {count(entries.length, "entry", "entries")} selected
                  </span>
                </div>
                <div className="overrides-list">
                  {view.groups.map((group) => {
                    const groupSelectable = group.entries.filter((e) => e.canMove || e.canDiscard).map((e) => e.path);
                    const groupAll = groupSelectable.length > 0 && groupSelectable.every((p) => selected.has(p));
                    return (
                      <section className="overrides-group" key={group.storeId ?? "server"}>
                        <h4>
                          <label className="settings-check">
                            <input type="checkbox" checked={groupAll} disabled={busy || groupSelectable.length === 0} onChange={(e) => toggle(groupSelectable, e.target.checked)} />
                            {group.title}
                          </label>
                          {group.scope === "database" && <span className="muted">database</span>}
                        </h4>
                        {group.entries.map((entry) => (
                          <EntryRow key={entry.path} entry={entry} settingsFile={view.settingsFile} checked={selected.has(entry.path)} disabled={busy} onToggle={(on) => toggle([entry.path], on)} />
                        ))}
                      </section>
                    );
                  })}
                </div>
              </>
            )}
          </>
        )}
        <div className="dialog-row">
          {message && <span className="muted">{message}</span>}
          <div className="header-spacer" />
          <button className="action-button danger" disabled={busy || toDiscard.length === 0} onClick={() => void discard()} title="Put back what relatude.db.json says">
            <IconArrowBackUp size={14} stroke={1.8} /> Discard{toDiscard.length > 0 ? ` ${toDiscard.length}` : ""}
          </button>
          <button className="action-button dialog-confirm primary" disabled={busy || toMove.length === 0} onClick={() => void move()}>
            <IconFileImport size={14} stroke={1.8} /> Move{toMove.length > 0 ? ` ${toMove.length}` : ""} into {view?.settingsFile ?? "relatude.db.json"}
          </button>
          <button className="action-button" onClick={onClose} disabled={busy}>
            Close
          </button>
        </div>
      </div>
    </div>
  );
}

function EntryRow({
  entry,
  settingsFile,
  checked,
  disabled,
  onToggle,
}: {
  entry: OverrideEntry;
  settingsFile: string;
  checked: boolean;
  disabled: boolean;
  onToggle: (on: boolean) => void;
}) {
  const pickable = entry.canMove || entry.canDiscard;
  // neither action applies: say why, the way a locked setting on the page says it
  const locked = pickable ? null : (entry.moveBlocked ?? entry.discardBlocked ?? null);
  return (
    <label className={"overrides-entry" + (pickable ? "" : " blocked")} title={entry.path}>
      <input type="checkbox" checked={checked && pickable} disabled={disabled || !pickable} onChange={(e) => onToggle(e.target.checked)} />
      <div className="overrides-entry-text">
        <div className="overrides-entry-label">
          <span>{entry.label}</span>
          {entry.kind === "added" && <span className="setting-badge overrides">added here</span>}
          {entry.kind === "removed" && <span className="setting-badge overrides">removed here</span>}
          {entry.secret && <span className="setting-badge faint">secret</span>}
        </div>
        {entry.where && <div className="overrides-entry-where">{entry.where}</div>}
        {entry.kind === "value" ? (
          <div className="overrides-entry-values">
            <span className="overrides-value">
              <span className="muted">{settingsFile}</span> <code>{shown(entry.fileValue, entry.fileHasValue, entry.secret)}</code>
            </span>
            <span className="overrides-value">
              <span className="muted">here</span> <code>{shown(entry.value, entry.hasValue, entry.secret)}</code>
            </span>
          </div>
        ) : (
          entry.summary && <div className="overrides-entry-values muted">{entry.summary}</div>
        )}
        {locked && (
          <div className="overrides-entry-note">
            <IconLock size={12} stroke={1.8} /> {locked}
          </div>
        )}
        {pickable && !entry.canMove && entry.moveBlocked && <div className="overrides-entry-note">Can be discarded, not moved: {entry.moveBlocked}</div>}
        {pickable && !entry.canDiscard && entry.discardBlocked && <div className="overrides-entry-note">Can be moved, not discarded: {entry.discardBlocked}</div>}
      </div>
    </label>
  );
}

// a value as the list shows it: what a secret is never shown as, and the three ways of having nothing
function shown(value: unknown, has: boolean, secret: boolean): string {
  if (secret) return has ? "secret set" : "not set";
  if (value === null || value === undefined) return "not set";
  if (value === "") return "empty";
  return typeof value === "string" ? value : JSON.stringify(value);
}

function count(n: number, one: string, many?: string): string {
  return `${n} ${n === 1 ? one : (many ?? one + "s")}`;
}
