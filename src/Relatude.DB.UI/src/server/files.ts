import { send } from "./channel";
import { adminBase } from "./base";
import { formatBytes } from "../format";
import type { ProgressController } from "../dialogs";

export interface IoInfo {
  id: string;
  name: string;
  type: string; // Memory | LocalDisk | AzureBlobStorage
  // "storage" for a provider from the database settings; "projectRoot" for the one every server
  // has over the website project folder, which is not in any settings
  kind: "storage" | "projectRoot";
  canRenameFile: boolean;
  canRenameFolder: boolean;
  // false where folders are key prefixes (memory, blob storage): an empty folder is not stored
  supportsEmptyFolders: boolean;
  localPath?: string | null; // the project root's folder on the server
}

export interface FileInfo {
  key: string; // folder qualified, '/'-separated
  size: number;
  creationTimeUtc: string;
  lastModifiedUtc: string;
  readers: number;
  writers: number;
  description?: string | null;
}

export interface FolderListing {
  name: string;
  hasFiles: boolean;
  hasSubFolders: boolean;
  description?: string | null; // what the folder holds; "-" (or absent) for folders we know nothing about
  // this folder is, or is below, one of the database's own data folders (the log files and the file
  // store): what it holds exists nowhere else and nothing can rebuild it
  isPrimaryData?: boolean;
  subFolders: FolderListing[]; // stubs when not recursive, full trees when recursive
  files: FileInfo[];
}

// the server sends "-" for a folder it has no description for
export function folderNote(folder: FolderListing | undefined): string | null {
  const description = folder?.description;
  return description && description !== "-" ? description : null;
}

export interface FolderSize {
  size: number;
  fileCount: number;
  folderCount: number;
}

export function fetchIoList(storeId: string): Promise<IoInfo[]> {
  return send<IoInfo[]>("io-list", { storeId });
}

// What the guids inside file and folder names stand for - index files and the folders holding them
// are named after the property they index, and the folders below indexes/ after the engine. Keyed by
// the "N" form (32 lower case hex, no dashes).
export type NameMap = Record<string, string>;

export function fetchNameMap(storeId: string): Promise<NameMap> {
  return send<NameMap>("name-map", { storeId });
}

// a guid in either form: 8-4-4-4-12 hex, dashes optional
const guidInName = /[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}/gi;

/**
 * The name with every guid the map knows replaced by what it is called. Only a display form: the
 * real name is what every request keeps using. Guids the map has nothing for (a file store's own
 * ids, a property from a datamodel that has moved on) are left exactly as they are, so a name is
 * never made up - at worst it stays as unreadable as it was.
 */
export function friendlyName(name: string, names: NameMap): string {
  return name.replace(guidInName, (guid) => names[guid.replaceAll("-", "").toLowerCase()] ?? guid);
}

// the same over a '/'-separated path
export function friendlyPath(path: string, names: NameMap): string {
  return path
    .split("/")
    .map((segment) => friendlyName(segment, names))
    .join("/");
}

export function fetchFolder(ioId: string, path: string, signal?: AbortSignal): Promise<FolderListing> {
  return send<FolderListing>("io-folder", { ioId, path }, signal);
}

export function fetchFolderRecursive(ioId: string, path: string): Promise<FolderListing> {
  return send<FolderListing>("io-folder", { ioId, path, recursive: true });
}

export function fetchFolderSize(ioId: string, path: string): Promise<FolderSize> {
  return send<FolderSize>("io-folder-size", { ioId, path });
}

export function deleteFiles(ioId: string, keys: string[]): Promise<{ deleted: number; errors: string[] }> {
  return send<{ deleted: number; errors: string[] }>("io-delete-files", { ioId, keys });
}

export function deleteFolder(ioId: string, path: string): Promise<{ deleted: boolean }> {
  return send<{ deleted: boolean }>("io-delete-folder", { ioId, path });
}

// Deletes everything under the folder file by file (so progress and cancellation work),
// then removes the empty folder itself. Returns the files that could not be deleted.
export async function deleteFolderWithProgress(ctl: ProgressController, ioId: string, path: string): Promise<string[]> {
  ctl.set({ label: "Listing files…", total: null });
  const root = await fetchFolderRecursive(ioId, path);
  const all: FileInfo[] = [];
  collectFiles(root, all);
  ctl.set({ total: all.length, done: 0 });
  const failed: string[] = [];
  const chunkSize = 10;
  for (let i = 0; i < all.length; i += chunkSize) {
    throwIfAborted(ctl.signal);
    const chunk = all.slice(i, i + chunkSize);
    ctl.set({ label: chunk[0].key, done: i });
    const result = await deleteFiles(
      ioId,
      chunk.map((f) => f.key),
    );
    failed.push(...result.errors);
    ctl.set({ done: Math.min(i + chunkSize, all.length) });
  }
  if (failed.length === 0) {
    ctl.set({ label: "Removing folders…" });
    await deleteFolder(ioId, path);
  }
  return failed;
}

export interface DeepListing {
  files: FileInfo[];
  // The folders walked that hold the database's own data. A file below one of them is primary data
  // however innocent the folder it was listed from looks, which is what the extra warning in front
  // of deleting one goes by.
  primaryFolders: string[];
}

/**
 * Every file at or below the folder, gathered one folder at a time. rootLabel is what to call the
 * folder itself while it is being listed, since its path is the empty string at the storage root.
 *
 * The server can answer the whole tree in a single call (fetchFolderRecursive, which the folder
 * download and delete use), but a big tree then takes minutes with nothing to show for it and no way
 * out; walking it here costs a request per folder and gives both. Folders are listed several at a
 * time, and the total grows as more of them are found, so the bar moves towards an end that is only
 * known once the walk is over.
 */
export async function scanFolderRecursive(ctl: ProgressController, ioId: string, path: string, rootLabel: string): Promise<DeepListing> {
  const found: FileInfo[] = [];
  const primaryFolders: string[] = [];
  const queue: string[] = [path];
  let visited = 0;
  const parallel = 8;
  while (queue.length > 0) {
    throwIfAborted(ctl.signal);
    const batch = queue.splice(0, parallel);
    ctl.set({ label: batch[0] || rootLabel, total: visited + batch.length + queue.length, done: visited });
    const listings = await Promise.all(batch.map((folder) => fetchFolder(ioId, folder, ctl.signal)));
    for (let i = 0; i < listings.length; i++) {
      const folder = batch[i];
      if (listings[i].isPrimaryData === true) primaryFolders.push(folder);
      for (const file of listings[i].files) found.push(file);
      for (const sub of listings[i].subFolders) queue.push(folder === "" ? sub.name : `${folder}/${sub.name}`);
    }
    visited += batch.length;
    ctl.set({ done: visited, total: visited + queue.length, meta: `${visited} folder${visited === 1 ? "" : "s"} · ${found.length} file${found.length === 1 ? "" : "s"}` });
  }
  return { files: found, primaryFolders };
}

// renames within the folder: newName is a single name, not a path
export function renameFile(ioId: string, key: string, newName: string): Promise<{ key: string }> {
  return send<{ key: string }>("io-rename-file", { ioId, key, newName });
}

export function renameFolder(ioId: string, path: string, newName: string): Promise<{ path: string }> {
  return send<{ path: string }>("io-rename-folder", { ioId, key: path, newName });
}

// persisted is false on providers with virtual folders: the folder shows once it holds a file
export function createFolder(ioId: string, parentPath: string, name: string): Promise<{ path: string; persisted: boolean }> {
  return send<{ path: string; persisted: boolean }>("io-create-folder", { ioId, key: parentPath, newName: name });
}

// the existing (authenticated) download endpoint of the admin API
export function downloadUrl(storeId: string, ioId: string, key: string): string {
  return `${adminBase}/maintenance/download-file?storeId=${storeId}&ioId=${ioId}&fileName=${encodeURIComponent(key)}`;
}

// the file itself, inline with its own content type, for the viewer; version only keeps a
// changed file out of the browser cache
export function fileUrl(ioId: string, key: string, version?: string): string {
  return `${adminBase}/ui/file?ioId=${ioId}&key=${encodeURIComponent(key)}${version ? "&v=" + encodeURIComponent(version) : ""}`;
}

/**
 * A small picture of an image file, made on the server and scaled to fit a tile `width` wide. Answers
 * 415 for anything it cannot draw - a document, a video, an image format no converter reads - which
 * is the thumbnail grid's signal to show the file's type icon instead. The version keeps a replaced
 * file out of the browser cache; without one changing, the answer may be cached for a day.
 */
export function thumbUrl(ioId: string, key: string, width: number, version?: string): string {
  return `${adminBase}/ui/thumb?ioId=${ioId}&key=${encodeURIComponent(key)}&w=${width}${version ? "&v=" + encodeURIComponent(version) : ""}`;
}

// A text file as the editor gets it, plus what a textarea would silently lose: the byte order
// mark and CRLF line endings. Both go back on when the text is saved, so a file the editor
// touched keeps its encoding conventions.
export interface TextContent {
  text: string; // LF line endings, no BOM
  bom: boolean;
  crlf: boolean;
}

export async function fetchText(ioId: string, key: string, signal: AbortSignal): Promise<TextContent> {
  const response = await fetch(fileUrl(ioId, key, Date.now().toString()), { signal, cache: "no-store" });
  if (response.status === 423) throw new Error("The file is in use and cannot be read right now.");
  if (response.status === 404) throw new Error("The file was not found.");
  if (!response.ok) {
    let message = `Could not read the file (HTTP ${response.status}).`;
    try {
      const body = (await response.json()) as { error?: string };
      if (body.error) message = body.error;
    } catch {
      // not json
    }
    throw new Error(message);
  }
  const bytes = new Uint8Array(await response.arrayBuffer());
  const bom = bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf;
  const raw = new TextDecoder("utf-8").decode(bom ? bytes.subarray(3) : bytes);
  const crlf = raw.includes("\r\n");
  return { text: crlf ? raw.replaceAll("\r\n", "\n") : raw, bom, crlf };
}

// an upload replaces the file: what the editor holds becomes the whole file
export function saveText(ioId: string, key: string, name: string, content: TextContent): Promise<void> {
  const text = content.crlf ? content.text.replaceAll("\n", "\r\n") : content.text;
  const parts: BlobPart[] = content.bom ? [new Uint8Array([0xef, 0xbb, 0xbf]), text] : [text];
  const file = new File(parts, name, { type: "text/plain" });
  return uploadFile(ioId, key, file, () => {}, new AbortController().signal);
}

// ---- transfers ----
// Uploads, and the folder download further down, are shaped the same way (UIFileTransfer.cs): a
// big file gets a request of its own, which keeps its progress honest and - going up - lets a
// dropped connection resume from the byte the server holds, while small files are packed together
// so a folder of tiny files costs one round trip per batch instead of one per file, which is what
// the time such a transfer takes is really made of. An upload also lands in a temp folder and is
// moved onto its real key only once all of it is there, so a cancelled one leaves nothing behind.

// How many bytes one request carries - a slice of a big file, or the payload of a packed batch.
// The size is not fixed: every request is timed and the next one is sized from what the link has
// actually been doing, aiming at a request of around three quarters of a second. Long enough that
// the round trip is a rounding error against the bytes, short enough that the bar keeps moving and
// Cancel is answered at once. On a fast link it settles at the ceiling, on a slow one at the floor.
const startRequestBytes = 512 * 1024;
const minRequestBytes = 50 * 1024;
const maxRequestBytes = 2 * 1024 * 1024;
const targetRequestSeconds = 0.75;
const batchFileLimit = 200; // files in one packed request, however little they weigh
const sliceRetries = 3; // network failures survived per slice

function createRequestSizer() {
  let observedBytesPerSecond = 0; // the requests of this page, the recent ones weighted most
  let measurements = 0;
  return {
    bytes(): number {
      if (measurements < 2) return startRequestBytes; // a single timing is noise, not a measurement
      const wanted = observedBytesPerSecond * targetRequestSeconds;
      return Math.round(Math.min(maxRequestBytes, Math.max(minRequestBytes, wanted)));
    },
    // A request too small to say anything about the link is ignored: the last, part-filled batch
    // of a folder of tiny files times the server's per-file work, not the wire.
    note(bytes: number, ms: number): void {
      if (bytes < minRequestBytes || ms < 1) return;
      const rate = (bytes / ms) * 1000;
      observedBytesPerSecond = measurements === 0 ? rate : observedBytesPerSecond * 0.7 + rate * 0.3;
      measurements++;
    },
  };
}

// One each, because a link is rarely as fast in both directions and sizing an upload from what a
// download managed would be wrong on every home connection.
type RequestSizer = ReturnType<typeof createRequestSizer>;
const uploadSizer = createRequestSizer();
const downloadSizer = createRequestSizer();

// An item big enough to be worth a request of its own gets one; everything smaller is packed
// together until a batch is full. A generator, not a list: the budget a group is measured against
// is the one that holds when the group is formed, so the plan follows the link as it is learned
// rather than being fixed before the first byte has gone anywhere.
function* planGroups<T>(items: T[], sizeOf: (item: T) => number, sizer: RequestSizer): Generator<T[]> {
  let batch: T[] = [];
  let batchBytes = 0;
  for (const item of items) {
    const budget = sizer.bytes();
    const size = sizeOf(item);
    if (size >= budget) {
      yield [item];
      continue;
    }
    if (batch.length >= batchFileLimit || batchBytes + size > budget) {
      yield batch;
      batch = [];
      batchBytes = 0;
    }
    batch.push(item);
    batchBytes += size;
  }
  if (batch.length > 0) yield batch;
}

/**
 * The progress of a transfer, counted in bytes rather than files so the bar moves evenly through
 * one big file as well as through a thousand small ones. `report` redraws with the bytes of the
 * request in flight; `advance` books a finished group.
 */
function byteProgress(ctl: ProgressController, totalBytes: number, totalFiles: number) {
  const started = performance.now();
  let doneBytes = 0;
  let doneFiles = 0;
  return {
    report(inFlight: number, label: string): void {
      const done = Math.min(doneBytes + inFlight, totalBytes);
      const seconds = (performance.now() - started) / 1000;
      const rate = seconds > 1 ? done / seconds : 0;
      const left = rate > 0 ? (totalBytes - done) / rate : 0;
      ctl.set({
        done,
        total: totalBytes,
        label,
        meta:
          `${doneFiles} / ${totalFiles} files · ${formatBytes(done)} / ${formatBytes(totalBytes)}` +
          (rate > 0 ? ` · ${formatBytes(rate)}/s` : "") +
          (left > 1 ? ` · ${formatRemaining(left)} left` : ""),
      });
    },
    advance(bytes: number, files: number): void {
      doneBytes += bytes;
      doneFiles += files;
    },
  };
}

class UploadError extends Error {
  readonly status: number;
  readonly received: number | null; // how far the server says the upload got, when it answered 409
  constructor(message: string, status: number, received: number | null) {
    super(message);
    this.status = status;
    this.received = received;
  }
}

// XMLHttpRequest instead of fetch: it reports upload progress and can be aborted
function post(url: string, body: Blob, onProgress: (sent: number) => void, signal: AbortSignal): Promise<string> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) return reject(new DOMException("Aborted", "AbortError"));
    const xhr = new XMLHttpRequest();
    const abort = () => xhr.abort();
    signal.addEventListener("abort", abort);
    const settle = (action: () => void) => {
      signal.removeEventListener("abort", abort); // one listener per slice would pile up otherwise
      action();
    };
    xhr.open("POST", url);
    xhr.upload.onprogress = (e) => onProgress(e.loaded);
    xhr.onload = () =>
      settle(() => {
        if (xhr.status >= 200 && xhr.status < 300) return resolve(xhr.responseText);
        let message = `Upload failed (HTTP ${xhr.status}).`;
        let received: number | null = null;
        try {
          const answer = JSON.parse(xhr.responseText) as { error?: string; received?: number };
          if (answer.error) message = answer.error;
          if (typeof answer.received === "number") received = answer.received;
        } catch {
          // not json
        }
        reject(new UploadError(message, xhr.status, received));
      });
    xhr.onerror = () => settle(() => reject(new UploadError("Upload failed (network error).", 0, null)));
    xhr.onabort = () => settle(() => reject(new DOMException("Aborted", "AbortError")));
    xhr.send(body);
  });
}

// crypto.randomUUID is only there in a secure context, and the admin UI is not always served over one
function newUploadId(): string {
  if (typeof crypto.randomUUID === "function") return crypto.randomUUID();
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/**
 * One file, sliced. Progress counts the bytes of the whole file, not of the slice in flight, and a
 * slice lost to the network is sent again from wherever the server says the upload stands.
 */
export async function uploadFile(
  ioId: string,
  key: string,
  file: File,
  onProgress: (sent: number, total: number) => void,
  signal: AbortSignal,
): Promise<void> {
  const uploadId = newUploadId();
  const partUrl = `${adminBase}/ui/upload-part?ioId=${ioId}&uploadId=${uploadId}`;
  let offset = 0;
  let attempt = 0;
  try {
    for (;;) {
      throwIfAborted(signal);
      const at = offset;
      const slice = file.slice(at, Math.min(at + uploadSizer.bytes(), file.size));
      try {
        const started = performance.now();
        const answer = await post(`${partUrl}&offset=${at}`, slice, (sent) => onProgress(Math.min(at + sent, file.size), file.size), signal);
        uploadSizer.note(slice.size, performance.now() - started);
        offset = (JSON.parse(answer) as { received: number }).received;
        attempt = 0;
      } catch (error) {
        throwIfAborted(signal);
        if (error instanceof UploadError && error.received !== null) offset = error.received; // resync
        else if (error instanceof UploadError && error.status === 0 && ++attempt <= sliceRetries) await pause(300 * attempt);
        else throw error;
        continue;
      }
      onProgress(offset, file.size);
      if (offset >= file.size) break;
      if (offset <= at) throw new Error("The upload stopped making progress.");
    }
    await post(`${adminBase}/ui/upload-commit?ioId=${ioId}&uploadId=${uploadId}&key=${encodeURIComponent(key)}&size=${file.size}`, new Blob(), () => {}, signal);
  } catch (error) {
    // the signal is usually aborted by now, so the temp file is dropped with a plain fetch
    void fetch(`${adminBase}/ui/upload-abort?ioId=${ioId}&uploadId=${uploadId}`, { method: "POST" }).catch(() => {});
    throw error;
  }
}

// Whole files packed into one request: per file an int32 name length, the name in utf-8, an int64
// file length and its bytes, all little endian (see UIFileTransfer.cs). The blob only references the
// files, so nothing is read into memory. Returns what the server could not write.
async function uploadBatch(
  ioId: string,
  basePath: string,
  entries: UploadEntry[],
  onProgress: (sent: number) => void,
  signal: AbortSignal,
): Promise<string[]> {
  const encoder = new TextEncoder();
  const parts: BlobPart[] = [];
  for (const entry of entries) {
    const name = encoder.encode(entry.relativePath);
    const header = new ArrayBuffer(12 + name.length);
    const view = new DataView(header);
    view.setInt32(0, name.length, true);
    new Uint8Array(header, 4, name.length).set(name);
    view.setBigInt64(4 + name.length, BigInt(entry.file.size), true);
    parts.push(header, entry.file);
  }
  const url = `${adminBase}/ui/upload-batch?ioId=${ioId}&basePath=${encodeURIComponent(basePath)}`;
  const body = new Blob(parts);
  const started = performance.now();
  const answer = await post(url, body, onProgress, signal);
  uploadSizer.note(body.size, performance.now() - started);
  return (JSON.parse(answer) as { errors: string[] }).errors;
}

export interface UploadEntry {
  file: File;
  relativePath: string; // path below the target folder, e.g. "sub/name.txt" or just "name.txt"
}

/**
 * Uploads entries under basePath, big files sliced and small ones packed together. Returns the
 * entries that failed.
 */
export async function uploadEntries(ctl: ProgressController, ioId: string, basePath: string, entries: UploadEntry[]): Promise<string[]> {
  const progress = byteProgress(
    ctl,
    entries.reduce((sum, entry) => sum + entry.file.size, 0),
    entries.length,
  );
  const failed: string[] = [];
  progress.report(0, "Starting…");
  for (const group of planGroups(entries, (entry) => entry.file.size, uploadSizer)) {
    throwIfAborted(ctl.signal);
    const groupBytes = group.reduce((sum, entry) => sum + entry.file.size, 0);
    const label = group.length === 1 ? group[0].relativePath : `${group.length} files — ${group[0].relativePath} …`;
    progress.report(0, label);
    let done: number;
    if (group.length === 1) {
      done = await uploadEach(ctl, ioId, basePath, group, progress, failed);
    } else {
      try {
        const errors = await uploadBatch(ioId, basePath, group, (sent) => progress.report(Math.min(sent, groupBytes), label), ctl.signal);
        failed.push(...errors);
        done = group.length - errors.length;
      } catch {
        // A batch that fails as a whole says nothing about which file broke it - one file the
        // browser can no longer read (it changed on disk since it was picked) ends the request
        // for all of them. So the group goes again one at a time: the files that are fine still
        // land, and the one that is not is named.
        throwIfAborted(ctl.signal);
        done = await uploadEach(ctl, ioId, basePath, group, progress, failed);
      }
    }
    progress.advance(groupBytes, done);
    progress.report(0, label);
  }
  return failed;
}

// The entries one at a time, each sliced. Returns how many landed; the rest are named in failed.
async function uploadEach(
  ctl: ProgressController,
  ioId: string,
  basePath: string,
  entries: UploadEntry[],
  progress: ByteProgress,
  failed: string[],
): Promise<number> {
  let done = 0;
  let sent = 0;
  for (const entry of entries) {
    throwIfAborted(ctl.signal);
    const before = sent;
    const key = (basePath ? basePath + "/" : "") + entry.relativePath;
    try {
      await uploadFile(ioId, key, entry.file, (bytes) => progress.report(before + bytes, entry.relativePath), ctl.signal);
      done++;
    } catch (error) {
      throwIfAborted(ctl.signal);
      failed.push(`${entry.relativePath} (${error instanceof Error ? error.message : error})`);
    }
    sent += entry.file.size;
  }
  return done;
}

function formatRemaining(seconds: number): string {
  if (seconds < 60) return `${Math.ceil(seconds)} s`;
  if (seconds < 3600) return `${Math.round(seconds / 60)} min`;
  return `${(seconds / 3600).toFixed(1)} h`;
}

function pause(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// the zip-a-folder endpoint; used as the DownloadURL behind dragging a folder to the desktop
export function zipFolderUrl(ioId: string, path: string): string {
  return `${adminBase}/ui/zip?ioId=${ioId}&folder=${encodeURIComponent(path)}`;
}

// test-opens every file on the server; returns the ones that are locked (or unreadable)
export function checkLocks(ioId: string, keys: string[]): Promise<{ locked: string[] }> {
  return send<{ locked: string[] }>("io-check-locks", { ioId, keys });
}

// where the zip bytes go: a picked file on disk, or an in-memory collector as fallback
export interface ZipSink {
  write(chunk: Uint8Array): Promise<void>;
  close(): Promise<void>;
  abort(): Promise<void>;
}

// Checks every file for locks first — if any are locked nothing is downloaded and the locked
// list is returned — then streams the zip into the sink with byte progress and cancellation.
// basePath is stripped from the entry names inside the zip.
export async function downloadZipToSink(
  ctl: ProgressController,
  ioId: string,
  keys: string[],
  basePath: string,
  sink: ZipSink,
): Promise<{ locked: string[] } | { bytes: number }> {
  ctl.set({ label: `Checking ${keys.length} file${keys.length === 1 ? "" : "s"}…`, total: null });
  try {
    const check = await checkLocks(ioId, keys);
    if (check.locked.length > 0) {
      await sink.abort();
      return { locked: check.locked };
    }
    ctl.set({ label: "Creating zip…" });
    const response = await fetch(`${adminBase}/ui/zip`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ioId, keys, basePath }),
      signal: ctl.signal,
    });
    if (response.status === 423) {
      // a file got locked between the check and the zip
      await sink.abort();
      return { locked: ((await response.json()) as { locked?: string[] }).locked ?? [] };
    }
    if (!response.ok || !response.body) throw new Error(`Zip download failed (HTTP ${response.status}).`);
    const reader = response.body.getReader();
    let bytes = 0;
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      bytes += value.byteLength;
      await sink.write(value);
      ctl.set({ label: `${formatBytes(bytes)} received…` });
    }
    await sink.close();
    return { bytes };
  } catch (error) {
    await sink.abort().catch(() => {});
    throw error;
  }
}

// A file or folder dragged in from the OS; folders arrive as FileSystemEntry trees.
export type DroppedItem = FileSystemEntry | File;

// Grabs the dropped files and folders of a drop event. Must be called synchronously in
// the drop handler — the DataTransferItemList is gone once the handler returns.
export function itemsFromDrop(data: DataTransfer): DroppedItem[] {
  const result: DroppedItem[] = [];
  for (const item of data.items) {
    if (item.kind !== "file") continue;
    const entry = typeof item.webkitGetAsEntry === "function" ? item.webkitGetAsEntry() : null;
    if (entry) {
      result.push(entry);
    } else {
      const file = item.getAsFile();
      if (file) result.push(file);
    }
  }
  if (result.length === 0) result.push(...data.files); // browsers without DataTransferItem entries
  return result;
}

// Expands dropped items into a flat upload list — folders recursively, keeping their
// relative paths — with progress and cancellation.
export async function resolveDroppedItems(ctl: ProgressController, items: DroppedItem[]): Promise<UploadEntry[]> {
  ctl.set({ label: "Reading dropped items…", total: null });
  const result: UploadEntry[] = [];
  for (const item of items) {
    throwIfAborted(ctl.signal);
    if (item instanceof File) {
      result.push({ file: item, relativePath: item.name });
    } else {
      await collectDroppedEntry(ctl, item, "", result);
    }
  }
  return result;
}

async function collectDroppedEntry(ctl: ProgressController, entry: FileSystemEntry, prefix: string, into: UploadEntry[]): Promise<void> {
  throwIfAborted(ctl.signal);
  if (entry.isFile) {
    const file = await new Promise<File>((resolve, reject) => (entry as FileSystemFileEntry).file(resolve, reject));
    into.push({ file, relativePath: prefix + entry.name });
    if (into.length % 50 === 0) ctl.set({ label: `Reading dropped items… ${into.length} files` });
  } else if (entry.isDirectory) {
    const reader = (entry as FileSystemDirectoryEntry).createReader();
    for (;;) {
      // readEntries returns batches (Chromium caps them at 100) until an empty one
      const batch = await new Promise<FileSystemEntry[]>((resolve, reject) => reader.readEntries(resolve, reject));
      if (batch.length === 0) break;
      for (const child of batch) await collectDroppedEntry(ctl, child, prefix + entry.name + "/", into);
    }
  }
}

// Asks the browser for a local directory to write into. Null when the API is missing (only
// Chromium based browsers have it) or the picker was dismissed.
export async function pickDirectory(): Promise<FileSystemDirectoryHandle | null | "unsupported"> {
  if (!window.showDirectoryPicker) return "unsupported";
  try {
    return await window.showDirectoryPicker({ mode: "readwrite" });
  } catch {
    return null; // picker dismissed
  }
}

// Downloads the folder at path (recursively) into the given directory handle, using the
// File System Access API. Returns the files that failed (e.g. locked by the engine).
export async function downloadFolderToDirectory(
  ctl: ProgressController,
  storeId: string,
  ioId: string,
  path: string,
  directory: FileSystemDirectoryHandle,
): Promise<string[]> {
  ctl.set({ label: "Listing files…", total: null });
  const root = await fetchFolderRecursive(ioId, path);
  const all: FileInfo[] = [];
  collectFiles(root, all);
  return downloadFilesToDirectory(ctl, storeId, ioId, all, path === "" ? "" : path + "/", directory);
}

/**
 * Downloads the given files into the directory handle, recreating the folders below basePath.
 * Grouped exactly like an upload: a big file is streamed on its own, while small ones are asked
 * for together and arrive in one framed response, so a folder of thousands of tiny files costs a
 * round trip per batch instead of one per file. Returns the files that failed.
 */
export async function downloadFilesToDirectory(
  ctl: ProgressController,
  storeId: string,
  ioId: string,
  all: { key: string; size: number }[],
  basePath: string,
  directory: FileSystemDirectoryHandle,
): Promise<string[]> {
  const progress = byteProgress(
    ctl,
    all.reduce((sum, file) => sum + file.size, 0),
    all.length,
  );
  const relativeTo = (key: string) => (key.startsWith(basePath) ? key.slice(basePath.length) : key);
  const failed: string[] = [];
  progress.report(0, "Starting…");
  for (const group of planGroups(all, (file) => file.size, downloadSizer)) {
    throwIfAborted(ctl.signal);
    const groupBytes = group.reduce((sum, file) => sum + file.size, 0);
    const label = group.length === 1 ? relativeTo(group[0].key) : `${group.length} files — ${relativeTo(group[0].key)} …`;
    progress.report(0, label);
    let done: number;
    if (group.length === 1) {
      done = await downloadEach(ctl, storeId, ioId, group, relativeTo, directory, progress, failed);
    } else {
      try {
        const errors = await downloadBatch(ctl, ioId, group, relativeTo, directory, progress, label);
        failed.push(...errors);
        done = group.length - errors.length;
      } catch {
        // The response is one stream, so a file that ends early - it shrank while it was being
        // read - takes the rest of the batch down with it and names none of them. The group goes
        // again one at a time, which both gets the healthy files and finds the one at fault.
        throwIfAborted(ctl.signal);
        done = await downloadEach(ctl, storeId, ioId, group, relativeTo, directory, progress, failed);
      }
    }
    progress.advance(groupBytes, done);
    progress.report(0, label);
  }
  return failed;
}

type ByteProgress = ReturnType<typeof byteProgress>;

// The files one at a time, each streamed. Returns how many landed; the rest are named in failed.
async function downloadEach(
  ctl: ProgressController,
  storeId: string,
  ioId: string,
  group: { key: string; size: number }[],
  relativeTo: (key: string) => string,
  directory: FileSystemDirectoryHandle,
  progress: ByteProgress,
  failed: string[],
): Promise<number> {
  let done = 0;
  let received = 0;
  for (const file of group) {
    throwIfAborted(ctl.signal);
    const before = received;
    const relative = relativeTo(file.key);
    try {
      await downloadOne(ctl, storeId, ioId, file.key, relative, directory, (bytes) => progress.report(before + bytes, relative));
      done++;
    } catch (error) {
      throwIfAborted(ctl.signal);
      failed.push(`${relative} (${error instanceof Error ? error.message : error})`);
    }
    received += file.size;
  }
  return done;
}

// One file, streamed straight onto disk so its size never has to fit in memory.
async function downloadOne(
  ctl: ProgressController,
  storeId: string,
  ioId: string,
  key: string,
  relative: string,
  directory: FileSystemDirectoryHandle,
  onBytes: (received: number) => void,
): Promise<void> {
  const started = performance.now();
  const response = await fetch(downloadUrl(storeId, ioId, key), { signal: ctl.signal });
  if (!response.ok || !response.body) throw new Error(response.status === 423 ? "the file is in use" : `HTTP ${response.status}`);
  const writable = await (await fileHandleForPath(directory, relative)).createWritable();
  const reader = response.body.getReader();
  let received = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      await writable.write(value);
      received += value.byteLength;
      onBytes(received);
    }
    await writable.close();
  } catch (error) {
    await writable.abort().catch(() => {});
    throw error;
  }
  downloadSizer.note(received, performance.now() - started);
}

// Several whole files in one response, framed as UIFileTransfer describes. Returns the ones the
// server could not read; those frames carry the reason instead of the bytes.
async function downloadBatch(
  ctl: ProgressController,
  ioId: string,
  group: { key: string }[],
  relativeTo: (key: string) => string,
  directory: FileSystemDirectoryHandle,
  progress: ByteProgress,
  label: string,
): Promise<string[]> {
  const started = performance.now();
  const response = await fetch(`${adminBase}/ui/download-batch`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ ioId, keys: group.map((file) => file.key) }),
    signal: ctl.signal,
  });
  if (!response.ok || !response.body) throw new Error(`Download failed (HTTP ${response.status}).`);
  const frames = new FrameReader(response.body);
  const decoder = new TextDecoder();
  const errors: string[] = [];
  let received = 0;
  for (;;) {
    const head = await frames.take(4);
    if (head === null) break;
    const name = decoder.decode(await frames.takeOrThrow(dataView(head).getInt32(0, true)));
    const ok = (await frames.takeOrThrow(1))[0] === 1;
    const length = Number(dataView(await frames.takeOrThrow(8)).getBigInt64(0, true));
    if (!ok) {
      errors.push(`${relativeTo(name)} (${decoder.decode(await frames.takeOrThrow(length))})`);
      continue;
    }
    const writable = await (await fileHandleForPath(directory, relativeTo(name))).createWritable();
    try {
      for (let written = 0; written < length; ) {
        const chunk = await frames.takeOrThrow(Math.min(length - written, 1024 * 1024));
        await writable.write(chunk);
        written += chunk.length;
        received += chunk.length;
        progress.report(received, label);
      }
      await writable.close();
    } catch (error) {
      await writable.abort().catch(() => {});
      throw error;
    }
  }
  downloadSizer.note(received, performance.now() - started);
  return errors;
}

const dataView = (bytes: Uint8Array) => new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);

// Reads a framed response field by field, holding on to whatever a chunk brings past the field
// that was asked for.
class FrameReader {
  private readonly reader: ReadableStreamDefaultReader<Uint8Array>;
  private held = new Uint8Array(new ArrayBuffer(0));
  constructor(stream: ReadableStream<Uint8Array>) {
    this.reader = stream.getReader();
  }
  // count bytes, or null when the response ended exactly on a frame boundary
  async take(count: number): Promise<Uint8Array<ArrayBuffer> | null> {
    while (this.held.length < count) {
      const { done, value } = await this.reader.read();
      if (done) {
        if (this.held.length === 0) return null;
        throw new Error("The download ended mid file.");
      }
      const grown = new Uint8Array(new ArrayBuffer(this.held.length + value.length));
      grown.set(this.held);
      grown.set(value, this.held.length);
      this.held = grown;
    }
    const taken = this.held.subarray(0, count);
    this.held = this.held.subarray(count);
    return taken;
  }
  async takeOrThrow(count: number): Promise<Uint8Array<ArrayBuffer>> {
    const taken = await this.take(count);
    if (taken === null) throw new Error("The download ended mid file.");
    return taken;
  }
}

function collectFiles(folder: FolderListing, into: FileInfo[]): void {
  for (const file of folder.files) into.push(file);
  for (const sub of folder.subFolders) collectFiles(sub, into);
}

async function fileHandleForPath(root: FileSystemDirectoryHandle, relativePath: string): Promise<FileSystemFileHandle> {
  const parts = relativePath.split("/");
  let dir = root;
  for (const part of parts.slice(0, -1)) dir = await dir.getDirectoryHandle(part, { create: true });
  return dir.getFileHandle(parts[parts.length - 1], { create: true });
}

function throwIfAborted(signal: AbortSignal): void {
  if (signal.aborted) throw new DOMException("Aborted", "AbortError");
}
