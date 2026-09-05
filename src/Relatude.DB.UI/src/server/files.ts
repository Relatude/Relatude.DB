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

export function fetchFolder(ioId: string, path: string): Promise<FolderListing> {
  return send<FolderListing>("io-folder", { ioId, path });
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

// the upload endpoint replaces the file: what the editor holds becomes the whole file
export function saveText(ioId: string, key: string, name: string, content: TextContent): Promise<void> {
  const text = content.crlf ? content.text.replaceAll("\n", "\r\n") : content.text;
  const parts: BlobPart[] = content.bom ? [new Uint8Array([0xef, 0xbb, 0xbf]), text] : [text];
  const file = new File(parts, name, { type: "text/plain" });
  return uploadFile(ioId, key, file, () => {}, new AbortController().signal);
}

// XMLHttpRequest instead of fetch: it reports upload progress and can be aborted
export function uploadFile(
  ioId: string,
  key: string,
  file: File,
  onProgress: (sent: number, total: number) => void,
  signal: AbortSignal,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open("POST", `${adminBase}/ui/upload?ioId=${ioId}&key=${encodeURIComponent(key)}`);
    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable) onProgress(e.loaded, e.total);
    };
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) resolve();
      else reject(new Error(`Upload failed (HTTP ${xhr.status}).`));
    };
    xhr.onerror = () => reject(new Error("Upload failed (network error)."));
    xhr.onabort = () => reject(new DOMException("Aborted", "AbortError"));
    signal.addEventListener("abort", () => xhr.abort(), { once: true });
    xhr.send(file);
  });
}

export interface UploadEntry {
  file: File;
  relativePath: string; // path below the target folder, e.g. "sub/name.txt" or just "name.txt"
}

// Uploads entries under basePath, one at a time with byte progress. Returns the entries that failed.
export async function uploadEntries(ctl: ProgressController, ioId: string, basePath: string, entries: UploadEntry[]): Promise<string[]> {
  ctl.set({ total: entries.length, done: 0 });
  const failed: string[] = [];
  for (let i = 0; i < entries.length; i++) {
    throwIfAborted(ctl.signal);
    const entry = entries[i];
    ctl.set({ label: entry.relativePath, done: i });
    const key = (basePath ? basePath + "/" : "") + entry.relativePath;
    try {
      await uploadFile(
        ioId,
        key,
        entry.file,
        (sent, total) => ctl.set({ label: `${entry.relativePath} — ${formatBytes(sent)} / ${formatBytes(total)}` }),
        ctl.signal,
      );
    } catch (error) {
      throwIfAborted(ctl.signal);
      failed.push(`${entry.relativePath} (${error instanceof Error ? error.message : error})`);
    }
    ctl.set({ done: i + 1 });
  }
  return failed;
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

// Downloads the given files into the directory handle, one at a time, recreating the folders
// below basePath. Returns the files that failed (e.g. locked by the engine).
export async function downloadFilesToDirectory(
  ctl: ProgressController,
  storeId: string,
  ioId: string,
  all: { key: string; size: number }[],
  basePath: string,
  directory: FileSystemDirectoryHandle,
): Promise<string[]> {
  ctl.set({ total: all.length, done: 0 });
  const failed: string[] = [];
  for (let i = 0; i < all.length; i++) {
    throwIfAborted(ctl.signal);
    const file = all[i];
    const relative = file.key.startsWith(basePath) ? file.key.slice(basePath.length) : file.key;
    ctl.set({ label: `${relative} (${formatBytes(file.size)})`, done: i });
    try {
      const response = await fetch(downloadUrl(storeId, ioId, file.key), { signal: ctl.signal });
      if (!response.ok || !response.body) {
        failed.push(`${relative} (HTTP ${response.status}${response.status === 423 ? ", locked" : ""})`);
        continue;
      }
      const handle = await fileHandleForPath(directory, relative);
      const writable = await handle.createWritable();
      await response.body.pipeTo(writable, { signal: ctl.signal }); // pipeTo also closes the writable
    } catch (error) {
      throwIfAborted(ctl.signal);
      failed.push(`${relative} (${error instanceof Error ? error.message : error})`);
    }
    ctl.set({ done: i + 1 });
  }
  return failed;
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
