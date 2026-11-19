import { useCallback, useEffect, useState } from "react";
import { openChanges, useChangesVersion, useOnChangesMade } from "../changes";
import { serverOverridesApi, type OverrideEntry, type OverrideGroup, type OverridesApi, type OverridesView } from "../server/settings";
import { ShareButton } from "./ShareButton";

/** The part of relatude.db.overrides.json a page writes, and so counts and opens the changes dialog on. */
export interface OverridesScope {
  /** what the page is about: "Settings of MyDatabase" */
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
 * The Move to shared button of a page that writes relatude.db.overrides.json (or a set of custom settings'
 * THIS SERVER file). It reads the file itself, so the status is this page's part of it, and opens the
 * changes dialog (ChangesDialog) on that part: its branch open and in sight, the rest of what THIS SERVER
 * holds closed beside it. changeKey is anything that changes when the page has saved, which reads it again;
 * so does anything the dialog moves or discards, which also calls onChanged.
 */
export function SettingsShareButton({
  scope,
  api = serverOverridesApi,
  changeKey,
  onChanged,
  className,
}: {
  scope: OverridesScope;
  /** the pair of files: relatude.db.overrides.json and relatude.db.json unless a set of custom settings names its own;
   *  keep it the same object from render to render, or the file is read at every one */
  api?: OverridesApi;
  changeKey?: unknown;
  onChanged?: () => void;
  className?: string;
}) {
  const [view, setView] = useState<OverridesView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const changesVersion = useChangesVersion();
  const read = useCallback(() => {
    api
      .fetch()
      .then((v) => {
        setView(v);
        setError(null);
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, [api]);
  useEffect(read, [read, changeKey, changesVersion]);
  // the page reads again after anything the dialog moved or discarded, wherever it was opened from
  useOnChangesMade(() => onChanged?.());
  // the server writes straight into relatude.db.json: there is nothing kept on this server to move
  if (view && !view.enabled) return null;
  const groups = view ? scopedGroups(view, scope) : [];
  const count = view ? groups.reduce((n, g) => n + g.entries.length, 0) : null;
  return (
    <ShareButton
      className={className}
      count={count}
      error={error ?? view?.error ?? null}
      title={`What is saved here is kept on THIS SERVER, ${view?.file ?? "relatude.db.overrides.json"}, merged over SHARED, ${view?.settingsFile ?? "relatude.db.json"}, at every start.`}
      onClick={() => {
        // the dialog lists entries by the ids of overrides-export-list, which carry the paths of this file
        const ids = new Set(groups.flatMap((g) => g.entries.map((e) => api.entryId(e.path))));
        openChanges({
          intent: "move",
          focus: (entry) => ids.has(entry.id),
          focusTitle: scope.title,
        });
      }}
    />
  );
}
