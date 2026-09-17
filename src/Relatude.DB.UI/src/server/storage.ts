import { send } from "./channel";
import { adminBase } from "./base";
import { abortUpload, downloadFilesToDirectory, downloadFolderToDirectory, newUploadId, uploadStaged } from "./files";
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
  size: number;
  state: string;
  /** Memory | LocalDisk | AzureBlobStorage: what the database file is kept on. */
  ioType: string;
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

/**
 * What a database file put in place by any of the three routes below reports back: the key it
 * landed on, its size, and the time of its first transaction.
 */
export interface AdoptResult {
  newKey: string;
  size: number;
  firstChangeUtc: string | null;
}

/**
 * Replaces the database with an uploaded file: stages the upload, and once all of it is there
 * closes the database and lets the server put the staged file in place as the next log file,
 * dropping everything built from the old one. The database comes back up in every outcome,
 * failure and cancel included.
 *
 * The upload runs while the database is still open and serving - it goes to a temp file in the
 * upload folder, which is nothing the open database touches - so a slow link costs the transfer
 * time in downtime no longer. Only the swap itself needs the database down, and an upload that
 * fails or is cancelled never takes it down at all.
 *
 * The staged file only becomes the database once it has fully arrived and the database is closed,
 * and which key it lands on is the server’s decision at that moment: a key worked out before the
 * upload started can be the live database file by the time the last byte is in.
 */
export async function uploadDatabase(ctl: ProgressController, storeId: string, info: DbFileInfo, file: File): Promise<AdoptResult> {
  const uploadId = newUploadId();
  // A memory storage holds its files in the provider instance, and the server drops those when the
  // last database on it closes - a file staged there would not outlive the close. So that one keeps
  // the old order and is uploaded with the database already down; everything on disk or in blob
  // storage uploads first.
  const closeFirst = info.ioType === "Memory";
  let closed = false; // only what this call closed is reopened: a failed upload leaves the database alone
  async function close(): Promise<void> {
    ctl.set({ label: "Closing the database…", total: null });
    await closeStore(storeId);
    closed = true;
  }
  try {
    if (closeFirst) await close();
    ctl.set({ label: file.name, total: file.size, done: 0 });
    await uploadStaged(
      info.ioId,
      uploadId,
      file,
      (sent, total) => ctl.set({ done: sent, label: `${file.name} — ${formatBytes(sent)} / ${formatBytes(total)}` }),
      ctl.signal,
    );
    if (!closed) await close();
    ctl.set({ label: "Putting it in place…", total: null });
    return await send<AdoptResult>("db-upload-adopt", { storeId, ioId: info.ioId, uploadId, size: file.size });
  } catch (error) {
    abortUpload(info.ioId, uploadId); // a staged file nothing will commit is only taking up room
    throw error;
  } finally {
    if (closed) {
      ctl.set({ label: "Opening the database…", total: null });
      await openStore(storeId);
    }
  }
}

/**
 * Makes an existing file the database: it is copied onto the next log file key and the database is
 * opened on the copy. The file itself is only read, so this is also the way back from a time
 * travel — the log that was cut is still there, one file key behind.
 */
export async function adoptDbFile(ctl: ProgressController, storeId: string, ioId: string, key: string): Promise<AdoptResult> {
  ctl.set({ label: "Closing the database…", total: null });
  await closeStore(storeId);
  try {
    ctl.set({ label: `Copying ${key}…` });
    return await send<AdoptResult>("db-adopt-file", { storeId, ioId, key });
  } finally {
    ctl.set({ label: "Opening the database…" });
    await openStore(storeId);
  }
}

/**
 * What the "go back in time" dialog offers as moments to go back to. Asked for when the dialog
 * opens: a closed database has to be read off its log file, which means walking it.
 */
export interface TimeTravelInfo {
  currentKey: string;
  size: number;
  open: boolean;
  firstChangeUtc: string | null;
  lastChangeUtc: string | null;
  /** The start of the last revert window begun on this database, active or long since ended. */
  revertWindowUtc: string | null;
  /** When that window was begun, which is not the same as the moment it marks. */
  revertWindowBegunUtc: string | null;
  revertWindowActive: boolean;
}

export function fetchTimeTravelInfo(storeId: string): Promise<TimeTravelInfo> {
  return send<TimeTravelInfo>("db-time-travel-info", { storeId });
}

/** A log file named by the storage it lies in and its key there; what everything below works on. */
export interface LogFileSource {
  ioId: string;
  key: string;
}

/**
 * One database log file the dialog can go back in: the file the database is running on, one it has
 * moved on from, or a backup of either. Found in the data and backup folders of every storage the
 * database has, so a copy kept somewhere else is listed beside the one in use.
 *
 * `firstChangeUtc` is read from the file's own header, which is what tells two copies apart at a
 * glance - except on the file a running database holds (`inUse`), which nothing else can read; that
 * one is filled in from the database itself. `error` is what the file said when it could not be
 * read, and the file is listed with it rather than left out.
 */
export interface LogFileInfo extends LogFileSource {
  ioName: string;
  name: string;
  folder: string; // "data", "backup", or "" for a file in the storage root
  size: number;
  modifiedUtc: string | null;
  isCurrent: boolean; // the database's current log file key
  inUse: boolean; // ... and the database is open, so the file is held and cannot be read
  backupUtc: string | null;
  keepForever: boolean;
  firstChangeUtc: string | null;
  fileId: string | null;
  error: string | null;
}

/** `include` lists one more file wherever it lies, for the dialog opened from a file on the Files page. */
export function fetchLogFiles(storeId: string, include?: LogFileSource | null): Promise<LogFileInfo[]> {
  return send<LogFileInfo[]>("db-log-files", { storeId, ioId: include?.ioId ?? null, key: include?.key ?? null });
}

/**
 * One stretch of a log file's timeline and what it holds. The times are unix milliseconds - what the
 * picture is drawn on - and the positions are transaction boundaries, so handing a pair of them back
 * to a second scan reads only the stretch being looked at (see `scanLogFile`).
 */
export interface LogSlice {
  fromMs: number;
  toMs: number;
  /** The first and last transaction in the slice; lastMs is rounded up, so it can be gone back to. */
  firstMs: number;
  lastMs: number;
  transactions: number;
  actions: number;
  bytes: number;
  startPosition: number;
  endPosition: number;
}

/** What one scan of a log file found: see `scanLogFile`. Empty slices are left out. */
export interface LogTimeline {
  fileSize: number;
  scanStart: number;
  scanEnd: number;
  /** The width every slice was gathered at, in milliseconds; found by the scan, not asked of it. */
  sliceMs: number;
  transactions: number;
  actions: number;
  firstUtc: string | null;
  lastUtc: string | null;
  slices: LogSlice[];
}

export interface LogScanProgress {
  state: "running" | "done" | "cancelled" | "failed";
  description: string;
  percent: number;
  error: string | null;
  timeline: LogTimeline | null; // set once the job is done, so the slices travel once
}

/**
 * Walks a log file and gathers its transactions into slices of equal width, which is what the
 * timeline is drawn from. The walk reads the whole file, so like the file store scans it runs as a
 * server job that is polled and can be cancelled.
 *
 * `range` narrows it to part of the file, given as the positions of a previous scan's slices: that
 * is how a stretch of the picture is looked at more closely without reading the file again.
 */
export async function scanLogFile(
  ctl: ProgressController,
  storeId: string,
  source: LogFileSource,
  range?: { fromPosition: number; toPosition: number },
  slices = 1200,
): Promise<LogTimeline> {
  ctl.set({ label: "Starting…", total: 100, done: 0, meta: "0%" }); // the job reports percent, so the bar counts to 100
  const { jobId } = await send<{ jobId: string }>("db-log-scan-start", {
    storeId,
    ioId: source.ioId,
    key: source.key,
    fromPosition: range?.fromPosition ?? 0,
    toPosition: range?.toPosition ?? 0,
    slices,
  });
  const cancelJob = () => {
    void send("db-log-scan-cancel", { jobId }).catch(() => {}); // a job that already finished is not an error worth showing
  };
  ctl.signal.addEventListener("abort", cancelJob, { once: true });
  try {
    for (;;) {
      const progress = await send<LogScanProgress>("db-log-scan-progress", { jobId });
      ctl.set({ label: progress.description || "Reading…", done: progress.percent, meta: progress.percent + "%" });
      if (progress.state === "running") {
        await new Promise((r) => setTimeout(r, 300));
        continue;
      }
      if (progress.state === "failed") throw new Error(progress.error ?? "The scan failed.");
      if (progress.state === "cancelled") throw new DOMException("Aborted", "AbortError");
      if (!progress.timeline) throw new Error("The scan finished without a timeline.");
      return progress.timeline;
    }
  } finally {
    ctl.signal.removeEventListener("abort", cancelJob);
  }
}

/**
 * The same scan of the file a running database is holding: it keeps that file to itself, so it is
 * closed for the length of the scan and opened again afterwards, failure and cancel included. Only
 * for that one file - every other log file can be read with the database running.
 */
export async function scanLogFileClosed(
  ctl: ProgressController,
  storeId: string,
  source: LogFileSource,
  range?: { fromPosition: number; toPosition: number },
  slices?: number,
): Promise<LogTimeline> {
  ctl.set({ label: "Closing the database…", total: null });
  await closeStore(storeId);
  try {
    return await scanLogFile(ctl, storeId, source, range, slices);
  } finally {
    ctl.set({ label: "Opening the database…", total: null, meta: null });
    await openStore(storeId);
  }
}

/** What the database was left at after going back in time, and what was left out getting there. */
export interface TimeTravelResult {
  newKey: string;
  /** The log file that was copied - the one the database was running on unless another was picked. */
  sourceKey: string;
  /** The database file that was in place before, which is kept beside the new one. */
  previousKey: string;
  /** The newest transaction the copy holds: where the database now ends. */
  lastChangeUtc: string | null;
  /** The newest transaction the file it was copied from held. */
  droppedFromUtc: string | null;
  transactionsKept: number;
  transactionsDropped: number;
  actionsKept: number;
  actionsDropped: number;
  bytesKept: number;
  bytesDropped: number;
}

/**
 * Copies a log file up to a moment in time and opens the database on the copy. `source` is the file
 * to copy; without one it is the file the database is running on. Closing first is not a precaution
 * but a requirement: a running database holds its log file exclusively, so nothing can read it -
 * and the copy has to become the database, which is not something done underneath a running one.
 */
export async function timeTravel(
  ctl: ProgressController,
  storeId: string,
  untilUtc: Date,
  source?: LogFileSource | null,
): Promise<TimeTravelResult> {
  ctl.set({ label: "Closing the database…", total: null });
  await closeStore(storeId);
  try {
    ctl.set({ label: `Copying ${source ? source.key : "the database"} up to ${untilUtc.toLocaleString()}…` });
    return await send<TimeTravelResult>("db-time-travel", {
      storeId,
      untilUtc: untilUtc.toISOString(),
      ioId: source?.ioId ?? null,
      key: source?.key ?? null,
    });
  } finally {
    ctl.set({ label: "Opening the database…" });
    await openStore(storeId);
  }
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
  /** named when two scans share one dialog, so the bar starting again reads as the next of them */
  phase?: string,
): Promise<FileScanProgress> {
  const say = (text: string) => (phase ? phase + " — " + text : text);
  ctl.set({ label: say("Starting…"), total: 100, done: 0, meta: "0%" }); // the job reports percent, so the bar counts to 100
  const { jobId } = await send<{ jobId: string }>("files-scan-start", { storeId, scan, countOnly });
  const cancelJob = () => {
    void send("files-scan-cancel", { jobId }).catch(() => {}); // a job that already finished is not an error worth showing
  };
  ctl.signal.addEventListener("abort", cancelJob, { once: true });
  try {
    for (;;) {
      const progress = await send<FileScanProgress>("files-scan-progress", { jobId });
      ctl.set({ label: say(progress.description || "Scanning…"), done: progress.percent, meta: progress.percent + "%" });
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

// ---- demo content ----
// Generated articles for an empty database. Only databases whose datamodel has the demo node type
// can take them, and the wikipedia generator only exists where its dump file happens to be, so the
// panel asks the server what is possible before offering anything.

export interface DemoContentInfo {
  open: boolean;
  available: boolean; // the datamodel has the demo node type
  nodeType: string;
  existing: number; // demo articles already stored; a run continues from there
  wikipedia: boolean; // a wikipedia dump is present on the server
  wikipediaPath: string;
}

export interface DemoResult {
  created: number;
  elapsedMs: number;
}

export interface DemoProgress {
  state: "running" | "done" | "cancelled" | "failed";
  description: string;
  percent: number;
  error: string | null;
  result: DemoResult | null;
}

export function fetchDemoInfo(storeId: string): Promise<DemoContentInfo> {
  return send<DemoContentInfo>("demo-info", { storeId });
}

// Generates and inserts demo articles behind a progress dialog. Like the file scans this is a server
// job that is polled: the insert of a million articles outlives any request. Cancelling stops the
// server job too, and keeps what it had already inserted.
export async function addDemoContent(ctl: ProgressController, storeId: string, count: number, wikipedia: boolean): Promise<DemoProgress> {
  ctl.set({ label: "Starting…", total: 100, done: 0, meta: "0%" }); // the job reports percent, so the bar counts to 100
  const { jobId } = await send<{ jobId: string }>("demo-start", { storeId, count, wikipedia });
  const cancelJob = () => {
    void send("demo-cancel", { jobId }).catch(() => {}); // a job that already finished is not an error worth showing
  };
  ctl.signal.addEventListener("abort", cancelJob, { once: true });
  try {
    for (;;) {
      const progress = await send<DemoProgress>("demo-progress", { jobId });
      ctl.set({ label: progress.description || "Generating…", done: progress.percent, meta: progress.percent + "%" });
      if (progress.state === "running") {
        await new Promise((r) => setTimeout(r, 400));
        continue;
      }
      if (progress.state === "failed") throw new Error(progress.error ?? "Adding demo content failed.");
      if (progress.state === "cancelled") throw new DOMException("Aborted", "AbortError");
      return progress;
    }
  } finally {
    ctl.signal.removeEventListener("abort", cancelJob);
  }
}
