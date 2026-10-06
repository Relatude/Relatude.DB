import { useEffect, useMemo, useState, type ReactNode } from "react";
import { IconAlertTriangle, IconArrowBackUp, IconDownload, IconFileImport } from "@tabler/icons-react";
import { DialogTools } from "./DialogTools";
import { Loading } from "./Loading";
import { SourceTag } from "./SourceTag";

/** One thing THIS SERVER holds, as the dialog lists it. */
export interface DataDialogEntry {
  key: string;
  /** the heading the entry is listed under; entries of one group are listed together, in their order */
  group: string;
  label: ReactNode;
  /** small marks beside the label: added here, removed here... */
  badges?: ReactNode;
  /** what SHARED has, and what THIS SERVER has */
  values?: ReactNode;
  /** a line under it: why it cannot be picked, or what picking it does */
  note?: ReactNode;
  /** false: listed, but neither action applies to it */
  selectable?: boolean;
}

/** One of the dialog's buttons. The dialog keeps the selection; the page does the work, asking first. */
export interface DataDialogAction {
  label: (count: number) => string;
  title?: string;
  /** which of the selected entries it applies to; all of them when left out */
  appliesTo?: (key: string) => boolean;
  /** the button may be pressed with nothing selected (a download of the file as it is) */
  allowNone?: boolean;
  onRun: (keys: string[]) => Promise<void>;
}

/**
 * What THIS SERVER holds - settings, datamodel overrides, log or endpoint definitions saved in the admin
 * UI on this installation - and the way to move it into SHARED, where it becomes part of the application, or to
 * discard it. The pages differ in what an entry is; the dialog is one: nothing starts selected, a group can
 * be picked at once, and every action is the page's, which confirms before it changes a file.
 */
export function DataDialog({
  title,
  intro,
  warnings,
  entries,
  error,
  loadingLabel,
  emptyText,
  message,
  busy,
  discard,
  download,
  move,
  onClose,
}: {
  title: string;
  intro: ReactNode;
  warnings?: ReactNode[];
  /** null while loading */
  entries: DataDialogEntry[] | null;
  error?: string | null;
  loadingLabel: string;
  emptyText: ReactNode;
  message?: string | null;
  busy: boolean;
  discard?: DataDialogAction;
  download?: DataDialogAction;
  move: DataDialogAction;
  onClose: () => void;
}) {
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const list = useMemo(() => entries ?? [], [entries]);
  const selectable = useMemo(() => list.filter((e) => e.selectable !== false), [list]);
  // what is no longer listed can no longer be selected
  useEffect(() => {
    const still = new Set(list.map((e) => e.key));
    setSelected((prev) => (Array.from(prev).every((k) => still.has(k)) ? prev : new Set(Array.from(prev).filter((k) => still.has(k)))));
  }, [list]);
  const chosen = selectable.filter((e) => selected.has(e.key)).map((e) => e.key);
  const allSelected = selectable.length > 0 && selectable.every((e) => selected.has(e.key));
  const groups = useMemo(() => {
    const map = new Map<string, DataDialogEntry[]>();
    for (const e of list) map.set(e.group, [...(map.get(e.group) ?? []), e]);
    return Array.from(map.entries());
  }, [list]);

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
  const keysFor = (action: DataDialogAction) => (action.appliesTo ? chosen.filter(action.appliesTo) : chosen);
  const button = (action: DataDialogAction, kind: "discard" | "download" | "move") => {
    const keys = keysFor(action);
    const Icon = kind === "discard" ? IconArrowBackUp : kind === "download" ? IconDownload : IconFileImport;
    return (
      <button
        key={kind}
        className={"action-button" + (kind === "discard" ? " danger" : kind === "move" ? " dialog-confirm primary" : "")}
        disabled={busy || entries === null || (keys.length === 0 && !action.allowNone)}
        title={action.title}
        onClick={() => void action.onRun(keys)}
      >
        <Icon size={14} stroke={1.8} /> {action.label(keys.length)}
      </button>
    );
  };

  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && !busy && onClose()}>
      <div
        className="dialog dialog-wide overrides-dialog"
        role="dialog"
        aria-label={title}
        onKeyDown={(e) => {
          if (e.key === "Escape" && !busy) {
            e.preventDefault();
            onClose();
          }
        }}
      >
        <h3>
          <SourceTag kind="data" title="Saved on this installation, in its data folder" /> {title}
          <DialogTools onClose={onClose} />
        </h3>
        {error && <div className="dialog-body dialog-title-error">{error}</div>}
        {!error && entries === null && <Loading label={loadingLabel} />}
        {entries !== null && (
          <>
            <div className="dialog-body overrides-intro">{intro}</div>
            {(warnings ?? []).filter(Boolean).map((w, i) => (
              <div className="dm-notice warn" key={i}>
                <IconAlertTriangle size={15} stroke={1.8} />
                <span>{w}</span>
              </div>
            ))}
            {list.length === 0 ? (
              <div className="overrides-empty">{emptyText}</div>
            ) : (
              <>
                <div className="overrides-toolbar">
                  <label className="settings-check">
                    <input type="checkbox" checked={allSelected} disabled={busy || selectable.length === 0} onChange={(e) => toggle(selectable.map((x) => x.key), e.target.checked)} />
                    Select all
                  </label>
                  <span className="muted">
                    {chosen.length} of {list.length} selected
                  </span>
                </div>
                <div className="overrides-list">
                  {groups.map(([group, items]) => {
                    const keys = items.filter((e) => e.selectable !== false).map((e) => e.key);
                    const groupAll = keys.length > 0 && keys.every((k) => selected.has(k));
                    return (
                      <section className="overrides-group" key={group}>
                        <h4>
                          <label className="settings-check">
                            <input type="checkbox" checked={groupAll} disabled={busy || keys.length === 0} onChange={(e) => toggle(keys, e.target.checked)} />
                            {group}
                          </label>
                        </h4>
                        {items.map((entry) => {
                          const pickable = entry.selectable !== false;
                          return (
                            <label className={"overrides-entry" + (pickable ? "" : " blocked")} key={entry.key}>
                              <input type="checkbox" checked={pickable && selected.has(entry.key)} disabled={busy || !pickable} onChange={(e) => toggle([entry.key], e.target.checked)} />
                              <div className="overrides-entry-text">
                                <div className="overrides-entry-label">
                                  <span>{entry.label}</span>
                                  {entry.badges}
                                </div>
                                {entry.values && <div className="overrides-entry-values">{entry.values}</div>}
                                {entry.note && <div className="overrides-entry-note">{entry.note}</div>}
                              </div>
                            </label>
                          );
                        })}
                      </section>
                    );
                  })}
                </div>
              </>
            )}
          </>
        )}
        {/* its own line: the buttons leave no room beside them for a sentence naming a file */}
        {message && <div className="dialog-body muted">{message}</div>}
        <div className="dialog-row">
          <div className="header-spacer" />
          {discard && button(discard, "discard")}
          {download && button(download, "download")}
          {button(move, "move")}
          <button className="action-button" onClick={onClose} disabled={busy}>
            Close
          </button>
        </div>
      </div>
    </div>
  );
}

/** One side of an entry's values: what SHARED, or THIS SERVER, has. */
export function DataValue({ side, children }: { side: "settings" | "data"; children: ReactNode }) {
  return (
    <span className="overrides-value">
      <SourceTag kind={side} small title={side === "settings" ? "What SHARED has: relatude.settings, part of the application" : "What THIS SERVER has: this installation's data folder"} />{" "}
      <code>{children}</code>
    </span>
  );
}

/** A value as the dialogs show it. */
export function shownValue(value: unknown): string {
  if (value === null || value === undefined) return "not set";
  if (value === true) return "yes";
  if (value === false) return "no";
  if (value === "") return "empty";
  return typeof value === "string" ? value : JSON.stringify(value);
}

export function count(n: number, one: string, many?: string): string {
  return `${n} ${n === 1 ? one : (many ?? one + "s")}`;
}

/** Hands a text to the browser as a file to save. */
export function saveText(fileName: string, content: string, type = "application/json") {
  const url = URL.createObjectURL(new Blob([content], { type }));
  const a = document.createElement("a");
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** The warning every move dialog gives outside the Development environment. */
export function deployedCopyWarning(environment: string, file: string, download: boolean): string {
  return `This server runs in the ${environment} environment. Here ${file} is most likely the deployed copy of the application, which the next deployment replaces, taking whatever was moved into it along.`
    + (download ? " Download the file with the selection moved in instead, and put it into source control." : " Make the same change in source control instead.");
}
