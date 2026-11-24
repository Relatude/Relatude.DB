import { send } from "./channel";
import { formatBytes, formatCount } from "../format";
import type { ProgressController } from "../dialogs";
import type { UploadEntry } from "./files";

export interface LocalFolder {
  name: string;
  list(): Promise<LocalListing>;
}

export interface LocalListing {
  folders: LocalFolder[];
  files: File[];
  unreadable: string[];
}

export function folderFromHandle(handle: FileSystemDirectoryHandle): LocalFolder {
  return {
    name: handle.name,
    async list() {
      const folders: LocalFolder[] = [];
      const handles: FileSystemFileHandle[] = [];
      for await (const entry of handle.values()) {
        if (entry.kind === "directory") folders.push(folderFromHandle(entry as FileSystemDirectoryHandle));
        else handles.push(entry as FileSystemFileHandle);
      }
      const opened = await Promise.allSettled(handles.map((h) => h.getFile()));
      const files: File[] = [];
      const unreadable: string[] = [];
      opened.forEach((result, i) => (result.status === "fulfilled" ? files.push(result.value) : unreadable.push(handles[i].name)));
      return { folders, files, unreadable };
    },
  };
}

export function folderFromFileList(list: FileList): LocalFolder | null {
  interface Node {
    folders: Map<string, Node>;
    files: File[];
  }
  const root: Node = { folders: new Map(), files: [] };
  let rootName: string | null = null;
  for (const file of Array.from(list)) {
    const parts = (file.webkitRelativePath || file.name).split("/");
    rootName ??= parts.length > 1 ? parts[0] : "";
    let node = root;
    for (const part of parts.slice(1, -1)) {
      let next = node.folders.get(part);
      if (!next) node.folders.set(part, (next = { folders: new Map(), files: [] }));
      node = next;
    }
    node.files.push(file);
  }
  if (rootName === null) return null;
  const wrap = (name: string, node: Node): LocalFolder => ({
    name,
    list: async () => ({ folders: [...node.folders].map(([childName, child]) => wrap(childName, child)), files: node.files, unreadable: [] }),
  });
  return wrap(rootName, root);
}

const storeFolderName = /^[0-9a-f]{2}$/i;
const systemFileName = /^(thumbs\.db|desktop\.ini|\.ds_store|\._.*)$/i;
const exampleId = "3fa05d41402abc4b2a76b9719d911017";
const listLanes = 16;
const compareLanes = 6;

export interface StoreFolderScan {
  name: string;
  innerFolder: string | null;
  files: UploadEntry[];
  bytes: number;
  leftOut: { path: string; reason: string; size: number }[];
  leftOutBytes: number;
  atOtherDepth: number;
  otherFolders: string[];
  systemFiles: number;
  topLevel: string[];
  problem: string | null;
}

const idPatterns = new Map<number, RegExp>();

function storeFileProblem(name: string, depth: number, size: number): string | null {
  const idRest = 32 - depth * 2;
  if (idRest <= 0) return "too many folders down";
  let pattern = idPatterns.get(idRest);
  if (!pattern) idPatterns.set(idRest, (pattern = new RegExp(`\\.[0-9a-f]{${idRest}}(\\.[^.]*)?$`, "i")));
  if (pattern.test(name)) return null;
  const hashed = /^([0-9a-f]+)-(\d+)$/i.exec(name);
  if (hashed && (hashed[1].length === idRest || hashed[1].length === 64 - depth * 2)) {
    return Number(hashed[2]) === size ? null : `${formatCount(size)} bytes where its name says ${formatCount(Number(hashed[2]))}: a broken copy`;
  }
  return "not named the way a file store names its files";
}

export function storeLayoutExample(depth: number): string {
  const folders = Array.from({ length: depth }, (_, i) => exampleId.slice(i * 2, i * 2 + 2) + "/").join("");
  return `${folders}photo.${exampleId.slice(depth * 2)}.jpg`;
}

function looksLikeStore(listing: LocalListing): boolean {
  return listing.folders.some((f) => storeFolderName.test(f.name)) || listing.files.some((f) => storeFileProblem(f.name, 0, f.size) === null);
}

export async function scanStoreFolder(ctl: ProgressController, picked: LocalFolder, storeDepth: number | null): Promise<StoreFolderScan> {
  const scan: StoreFolderScan = {
    name: picked.name,
    innerFolder: null,
    files: [],
    bytes: 0,
    leftOut: [],
    leftOutBytes: 0,
    atOtherDepth: 0,
    otherFolders: [],
    systemFiles: 0,
    topLevel: [],
    problem: null,
  };
  ctl.set({ label: `Reading ${picked.name}…`, total: null, meta: null });
  let top = await picked.list();
  scan.topLevel = [...top.folders.map((f) => f.name + "/"), ...top.files.map((f) => f.name)];
  if (!looksLikeStore(top)) {
    const inner = top.folders.find((f) => f.name.toLowerCase() === "files");
    const innerListing = inner ? await inner.list() : null;
    if (inner && innerListing && looksLikeStore(innerListing)) {
      scan.innerFolder = inner.name;
      top = innerListing;
    } else {
      scan.problem =
        top.folders.length + top.files.length === 0
          ? "it is empty"
          : "nothing at its top is named the way a file store names its folders and files";
      return scan;
    }
  }
  const shownRoot = scan.innerFolder ? `${scan.name}/${scan.innerFolder}` : scan.name;
  const queue: { folder: LocalFolder; path: string; depth: number }[] = [];
  let walked = 0;
  const take = (listing: LocalListing, path: string, depth: number) => {
    walked++;
    const at = (name: string) => (path ? `${path}/${name}` : name);
    for (const file of listing.files) {
      if (systemFileName.test(file.name)) {
        scan.systemFiles++;
        continue;
      }
      const nameProblem = storeFileProblem(file.name, depth, file.size);
      const depthProblem =
        !nameProblem && storeDepth != null && depth !== storeDepth
          ? `${depth} folder${depth === 1 ? "" : "s"} down, where the store keeps its files ${storeDepth} down`
          : null;
      if (depthProblem) scan.atOtherDepth++;
      const problem = nameProblem ?? depthProblem;
      if (problem) {
        scan.leftOut.push({ path: at(file.name), reason: problem, size: file.size });
        scan.leftOutBytes += file.size;
      } else {
        scan.files.push({ file, relativePath: at(file.name) });
        scan.bytes += file.size;
      }
    }
    for (const name of listing.unreadable) scan.leftOut.push({ path: at(name), reason: "the browser could not open it", size: 0 });
    for (const sub of listing.folders) {
      if (storeFolderName.test(sub.name)) queue.push({ folder: sub, path: at(sub.name), depth: depth + 1 });
      else scan.otherFolders.push(at(sub.name));
    }
  };
  take(top, "", 0);
  while (queue.length > 0) {
    throwIfAborted(ctl.signal);
    const batch = queue.splice(0, listLanes);
    ctl.set({
      label: `Reading ${shownRoot}/${batch[0].path}`,
      total: walked + batch.length + queue.length,
      done: walked,
      meta:
        `${formatCount(walked)} folders · ${formatCount(scan.files.length)} store files · ${formatBytes(scan.bytes)}` +
        (scan.leftOut.length > 0 ? ` · ${formatCount(scan.leftOut.length)} left out` : ""),
    });
    const listings = await Promise.all(batch.map((b) => b.folder.list()));
    listings.forEach((listing, i) => take(listing, batch[i].path, batch[i].depth));
  }
  if (scan.files.length === 0) {
    scan.problem = scan.atOtherDepth > 0
      ? `its files are not ${storeDepth} folders down, where this database's file store keeps them, so the store would never find them`
      : scan.leftOut.length === 0
        ? "it holds no files"
        : "none of its files is named the way a file store names them";
  } else if (scan.leftOut.length > scan.files.length) {
    scan.problem = `only ${formatCount(scan.files.length)} of its ${formatCount(scan.files.length + scan.leftOut.length)} files are named the way a file store names them`;
  }
  return scan;
}

export interface StoreUploadPlan {
  fresh: UploadEntry[];
  freshBytes: number;
  differ: UploadEntry[];
  differBytes: number;
  same: number;
  sameBytes: number;
}

export async function compareWithStore(
  ctl: ProgressController,
  ioId: string,
  storeFolder: string,
  storageName: string,
  files: UploadEntry[],
): Promise<StoreUploadPlan> {
  const groups = new Map<string, UploadEntry[]>();
  for (const entry of files) {
    const cut = entry.relativePath.indexOf("/");
    const group = cut < 0 ? "" : entry.relativePath.slice(0, cut);
    const list = groups.get(group);
    if (list) list.push(entry);
    else groups.set(group, [entry]);
  }
  const names = [...groups.keys()].sort();
  const plan: StoreUploadPlan = { fresh: [], freshBytes: 0, differ: [], differBytes: 0, same: 0, sameBytes: 0 };
  let next = 0;
  let done = 0;
  let failed = false;
  ctl.set({ label: `Comparing with ${storageName}…`, total: names.length, done: 0, meta: null });
  const lane = async () => {
    while (!failed && next < names.length) {
      throwIfAborted(ctl.signal);
      const group = names[next++];
      const path = group === "" ? storeFolder : `${storeFolder}/${group}`;
      try {
        const there = await send<{ keys: string[]; sizes: number[] }>("io-file-sizes", { ioId, path, recursive: group !== "" }, ctl.signal);
        const sizes = new Map<string, number>();
        there.keys.forEach((key, i) => sizes.set(key.toLowerCase(), there.sizes[i]));
        for (const entry of groups.get(group)!) {
          const below = group === "" ? entry.relativePath : entry.relativePath.slice(group.length + 1);
          const size = sizes.get(below.toLowerCase());
          if (size === undefined) {
            plan.fresh.push(entry);
            plan.freshBytes += entry.file.size;
          } else if (size === entry.file.size) {
            plan.same++;
            plan.sameBytes += entry.file.size;
          } else {
            plan.differ.push(entry);
            plan.differBytes += entry.file.size;
          }
        }
      } catch (error) {
        failed = true;
        throw error;
      }
      done++;
      ctl.set({
        label: `Comparing with ${storageName} — ${path}`,
        done,
        total: names.length,
        meta:
          `${formatCount(done)} / ${formatCount(names.length)} folders · ${formatCount(plan.same)} already there` +
          ` · ${formatCount(plan.fresh.length + plan.differ.length)} to upload`,
      });
    }
  };
  await Promise.all(Array.from({ length: Math.min(compareLanes, names.length) }, lane));
  const order = new Map<UploadEntry, number>();
  files.forEach((entry, i) => order.set(entry, i));
  plan.fresh.sort((a, b) => order.get(a)! - order.get(b)!);
  plan.differ.sort((a, b) => order.get(a)! - order.get(b)!);
  return plan;
}

function throwIfAborted(signal: AbortSignal): void {
  if (signal.aborted) throw new DOMException("Aborted", "AbortError");
}
