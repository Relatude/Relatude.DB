// The changes dialog: everything THIS SERVER holds that SHARED does not - settings, custom settings,
// datamodel overrides, log and GraphQL definitions, runtime types - in one tree, to download as the
// application's own files, move into SHARED or discard. It is the one dialog behind every Move to shared
// button (ShareButton) and behind Download changes; each opens it on what it is about.
//
// The dialog is drawn once, by ChangesHost in App, so any page can open it with openChanges. What it
// changes is told through changesVersion, which the buttons that count what THIS SERVER holds read again
// on, and through the opener's own onChanged.

import { useEffect, useRef, useSyncExternalStore } from "react";
import type { ExportEntry, ExportGroup } from "./server/overridesExport";
import type { OverridePath } from "./server/datamodel";

export interface ChangesRequest {
  /**
   * The entries the page that opened it is about: their branch is opened and scrolled to, the others
   * closed. Everything is listed whatever it says.
   */
  focus?: (entry: ExportEntry, group: ExportGroup) => boolean;
  /** what the page is about, said when nothing of it is held here: "Log definitions of Main" */
  focusTitle?: string;
  /**
   * download: Download changes - everything that is not a secret starts chosen, and Download is the main
   * button. move: a page's Move to shared - nothing starts chosen, and Move is.
   */
  intent: "download" | "move";
  /** after anything was moved or discarded */
  onChanged?: () => void;
}

export interface ChangesDialogState extends ChangesRequest {
  /** a new one every time it is opened, so the dialog starts over */
  id: number;
}

let current: ChangesDialogState | null = null;
let nextId = 1;
const dialogListeners = new Set<() => void>();
const emitDialog = () => dialogListeners.forEach((l) => l());

export function openChanges(request: ChangesRequest): void {
  current = { ...request, id: nextId++ };
  emitDialog();
}

export function closeChanges(): void {
  current = null;
  emitDialog();
}

export function useChangesDialog(): ChangesDialogState | null {
  return useSyncExternalStore(
    (l) => {
      dialogListeners.add(l);
      return () => dialogListeners.delete(l);
    },
    () => current,
  );
}

// ---- what the dialog changed ----

let version = 0;
const versionListeners = new Set<() => void>();

/** Said by the dialog after it moved or discarded anything. */
export function notifyChangesMade(): void {
  version++;
  versionListeners.forEach((l) => l());
}

/** Goes up every time the dialog changed what THIS SERVER holds: a page counting it reads again. */
export function useChangesVersion(): number {
  return useSyncExternalStore(
    (l) => {
      versionListeners.add(l);
      return () => versionListeners.delete(l);
    },
    () => version,
  );
}

/** Calls back each time the dialog changes what THIS SERVER holds, from now on: a page reads again. */
export function useOnChangesMade(callback: () => void): void {
  const now = useChangesVersion();
  const seen = useRef(now);
  const latest = useRef(callback);
  latest.current = callback;
  useEffect(() => {
    if (now === seen.current) return;
    seen.current = now;
    latest.current();
  }, [now]);
}

// ---- the data model editor ----

/**
 * Discarding a datamodel override changes the draft of the model, which only the data model editor of
 * that database holds. While it is open it says so here, and the dialog offers Discard for that
 * database's overrides; otherwise they can be moved and downloaded, not discarded.
 */
export interface DatamodelEditorLink {
  /** the database's name, for the dialog's words */
  database: string;
  /** puts back what SHARED, or the source, says in the draft; resolves with how many it found */
  discard: (paths: OverridePath[]) => Promise<number>;
  /** what the dialog should warn of: the draft changes overrides that are not activated yet */
  warning?: string | null;
}

const editors = new Map<string, DatamodelEditorLink>();
let editorsVersion = 0;
const editorListeners = new Set<() => void>();

export function linkDatamodelEditor(storeId: string, link: DatamodelEditorLink): () => void {
  editors.set(storeId, link);
  editorsVersion++;
  editorListeners.forEach((l) => l());
  return () => {
    if (editors.get(storeId) !== link) return;
    editors.delete(storeId);
    editorsVersion++;
    editorListeners.forEach((l) => l());
  };
}

export function datamodelEditorOf(storeId: string | null | undefined): DatamodelEditorLink | null {
  return storeId ? (editors.get(storeId) ?? null) : null;
}

/** Re-renders when an editor comes or goes; read the links with datamodelEditorOf. */
export function useDatamodelEditors(): number {
  return useSyncExternalStore(
    (l) => {
      editorListeners.add(l);
      return () => editorListeners.delete(l);
    },
    () => editorsVersion,
  );
}
