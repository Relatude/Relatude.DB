import { send } from "./channel";
import { adminBase } from "./base";
import { downloadFilesToDirectory, downloadFolderToDirectory, uploadFile } from "./files";
import { formatBytes } from "../format";
import type { ProgressController } from "../dialogs";

export interface BackupFile {
  key: string;
  name: string;
  size: number;
  timeUtc: string;
  keepForever: boolean;
}

export interface BackupList {
  ioId: string;
  files: BackupFile[];
}

export interface DbFileInfo {
  ioId: string;
  currentKey: string;
  nextKey: string;
  size: number;
  state: string;
  canUpload: boolean;
}

export function fetchBackupList(storeId: string): Promise<BackupList> {
  return send<BackupList>("backup-list", { storeId });
}

export function backupNow(storeId: string, truncate: boolean, keepForever: boolean): Promise<{ done: boolean }> {
  return send<{ done: boolean }>("backup-now", { storeId, truncate, keepForever });
}

export interface MaintenanceInfo {
  open: boolean;
  unusedFiles: number;
  unusedBytes: number;
  actionsNotInState?: number;
  transactionsNotInState?: number;
  truncatableActions?: number;
  logFileSize?: number;
  stateFileSize?: number;
  runningRewrite?: string | null;
  /** Background tasks waiting or running - a text index rebuild is watched through this. */
  tasksQueued?: number;
}

/**
 * Queues text extraction for every text indexed node, which is what rebuilds the search index.
 * Returns how many nodes were queued; the work itself runs as background tasks.
 */
export function rebuildTextIndex(storeId: string): Promise<{ queued: number }> {
  return send<{ queued: number }>("db-rebuild-text-index", { storeId });
}

export function fetchDbFileInfo(storeId: string): Promise<DbFileInfo> {
  return send<DbFileInfo>("db-file-info", { storeId });
}

// the existing (authenticated) database download endpoints; the truncated variant rewrites
// the store to a temp file first, so the download starts once the rewrite is done
export function databaseDownloadUrl(storeId: string, truncated: boolean): string {
  return `${adminBase}/maintenance/${truncated ? "download-truncated-db" : "download-full-db"}?storeId=${storeId}&namePrefix=`;
}

export function fetchMaintenanceInfo(storeId: string): Promise<MaintenanceInfo> {
  return send<MaintenanceInfo>("db-maintenance-info", { storeId });
}

export function deleteUnusedDbFiles(storeId: string): Promise<{ deleted: number; freed: number; errors: string[] }> {
  return send<{ deleted: number; freed: number; errors: string[] }>("db-delete-unused", { storeId });
}

export function truncateDatabase(storeId: string, keepOld: boolean): Promise<{ done: boolean }> {
  return send<{ done: boolean }>("db-truncate", { storeId, keepOld });
}

export function saveStateSnapshot(storeId: string): Promise<{ done: boolean }> {
  return send<{ done: boolean }>("db-save-state", { storeId });
}

// Reverts to a backup: closes the database, copies the backup into place as the next WAL
// file key (the current file is kept as an older version), clears the state file and reopens.
export async function revertToBackup(ctl: ProgressController, storeId: string, backupKey: string): Promise<string> {
  ctl.set({ label: "Closing the database…", total: null });
  await closeStore(storeId);
  try {
    ctl.set({ label: `Copying ${backupKey}…` });
    const result = await send<{ newKey: string }>("backup-restore", { storeId, key: backupKey });
    return result.newKey;
  } finally {
    ctl.set({ label: "Opening the database…" });
    await openStore(storeId);
  }
}

export function openStore(storeId: string): Promise<{ done: boolean }> {
  return send<{ done: boolean }>("store-open", { storeId });
}

export function closeStore(storeId: string): Promise<{ done: boolean }> {
  return send<{ done: boolean }>("store-close", { storeId });
}

// Replaces the database: closes it, uploads the file as the next WAL file key, clears the
// state file, and reopens. The database comes back up in every outcome (failure or cancel
// included) — reopening a cancelled upload is safe since the partial file is deleted.
export async function uploadDatabase(ctl: ProgressController, storeId: string, info: DbFileInfo, file: File): Promise<string> {
  ctl.set({ label: "Closing the database…", total: null });
  await closeStore(storeId);
  try {
    ctl.set({ label: `${file.name} → ${info.nextKey}`, total: file.size, done: 0 });
    await uploadFile(
      info.ioId,
      info.nextKey,
      file,
      (sent, total) => ctl.set({ done: sent, label: `${info.nextKey} — ${formatBytes(sent)} / ${formatBytes(total)}` }),
      ctl.signal,
    );
    ctl.set({ label: "Clearing the state file…", done: file.size });
    await send("db-upload-finalize", { storeId });
  } finally {
    ctl.set({ label: "Opening the database…", total: null });
    await openStore(storeId);
  }
  return info.nextKey;
}

// ---- converted file cache ----
// The resized images and transcoded media the conversion engine derives from stored files.
// Measuring walks the whole cache tree, so it is asked for on demand rather than polled.

export interface ConvertedCacheInfo {
  files: number;
  bytes: number;
}

export function fetchConvertedInfo(storeId: string): Promise<ConvertedCacheInfo> {
  return send<ConvertedCacheInfo>("db-converted-info", { storeId });
}

export function deleteConvertedFiles(storeId: string): Promise<{ deleted: number; freed: number; remaining: number }> {
  return send<{ deleted: number; freed: number; remaining: number }>("db-delete-converted", { storeId });
}

// ---- file storages ----
// Where a database keeps its uploaded files. A MultiFile store is a folder in its IO provider,
// a SingleFile store is one file at the provider root instead, so it comes with its file keys.

export interface FileStorageInfo {
  id: string;
  name: string; // the IO provider it lives in
  ioId: string;
  type: string; // MultiFile | SingleFile
  folder: string | null; // MultiFile only: the folder holding the files
  files: { key: string; size: number }[]; // SingleFile only: the files the store appends to
  isDefault: boolean;
}

export function fetchFileStorages(storeId: string): Promise<FileStorageInfo[]> {
  return send<FileStorageInfo[]>("file-store-list", { storeId });
}

// Downloads one file storage into a local directory, the same way a storage folder is downloaded.
export function downloadFileStorage(
  ctl: ProgressController,
  storeId: string,
  storage: FileStorageInfo,
  directory: FileSystemDirectoryHandle,
): Promise<string[]> {
  if (storage.folder != null) return downloadFolderToDirectory(ctl, storeId, storage.ioId, storage.folder, directory);
  return downloadFilesToDirectory(ctl, storeId, storage.ioId, storage.files, "", directory);
}

// ---- file store audits ----
// Both scans walk every node of the database, so they run as a background job on the server:
// start it, poll its progress (feeding the progress dialog), and cancel it when the dialog is.

export interface UnreferencedResult {
  // named after the delete run; on a count-only run these are the files that would be deleted
  totalBytesDeleted: number;
  totalFilesDeleted: number;
  totalFoldersDeleted: number;
}

export interface MissingFileInfo {
  nodeId: string;
  nodeType: string;
  property: string;
  fileName: string;
  size: number;
  fileId: string;
  storageId: string;
  reason: string;
}

export interface MissingResult {
  nodesScanned: number;
  filesChecked: number;
  missingCount: number;
  missingBytes: number;
  missing: MissingFileInfo[];
  listTruncated: boolean;
}

export interface FileScanProgress {
  state: "running" | "done" | "cancelled" | "failed";
  description: string;
  percent: number;
  error: string | null;
  // set once the job is done, so a long missing-file list travels once instead of on every poll
  unreferenced: UnreferencedResult | null;
  missing: MissingResult | null;
}

// Runs one file store scan behind a progress dialog. Resolves with the finished progress
// (result included), throws on failure and on cancellation (which also cancels the server job).
export async function runFileScan(
  ctl: ProgressController,
  storeId: string,
  scan: "unreferenced" | "missing",
  countOnly: boolean,
): Promise<FileScanProgress> {
  ctl.set({ label: "Starting…", total: 100, done: 0, meta: "0%" }); // the job reports percent, so the bar counts to 100
  const { jobId } = await send<{ jobId: string }>("files-scan-start", { storeId, scan, countOnly });
  const cancelJob = () => {
    void send("files-scan-cancel", { jobId }).catch(() => {}); // a job that already finished is not an error worth showing
  };
  ctl.signal.addEventListener("abort", cancelJob, { once: true });
  try {
    for (;;) {
      const progress = await send<FileScanProgress>("files-scan-progress", { jobId });
      ctl.set({ label: progress.description || "Scanning…", done: progress.percent, meta: progress.percent + "%" });
      if (progress.state === "running") {
        await new Promise((r) => setTimeout(r, 400));
        continue;
      }
      if (progress.state === "failed") throw new Error(progress.error ?? "The scan failed.");
      // a cancelled scan ends here: the dialog is already showing "cancelled"
      if (progress.state === "cancelled") throw new DOMException("Aborted", "AbortError");
      return progress;
    }
  } finally {
    ctl.signal.removeEventListener("abort", cancelJob);
  }
}
