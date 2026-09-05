import { useCallback, useEffect, useMemo, useRef, useState, type DragEvent, type MouseEvent as ReactMouseEvent } from "react";
import {
  IconAlertTriangle,
  IconArrowDown,
  IconArrowUp,
  IconChevronDown,
  IconChevronRight,
  IconDownload,
  IconEye,
  IconFile,
  IconFileZip,
  IconFolder,
  IconFolderDown,
  IconFolderOpen,
  IconFolderPlus,
  IconFolderUp,
  IconFolderX,
  IconPencil,
  IconRefresh,
  IconSum,
  IconTrash,
  IconUpload,
  IconWorld,
} from "@tabler/icons-react";
import {
  createFolder,
  deleteFiles,
  deleteFolderWithProgress,
  downloadFolderToDirectory,
  downloadUrl,
  downloadZipToSink,
  fetchFolder,
  fetchFolderSize,
  fetchIoList,
  fetchNameMap,
  folderNote,
  friendlyName,
  friendlyPath,
  itemsFromDrop,
  pickDirectory,
  renameFile,
  renameFolder,
  resolveDroppedItems,
  uploadEntries,
  zipFolderUrl,
  type FileInfo,
  type FolderListing,
  type FolderSize,
  type IoInfo,
  type NameMap,
  type UploadEntry,
  type ZipSink,
} from "../server/files";
import type { DatabaseInfo } from "../server/serverInfo";
import { formatBytes, formatTime } from "../format";
import { runWithProgress, showConfirm, showError, showPrompt, type ProgressController } from "../dialogs";
import { displayType } from "../code/language";
import { FileViewer } from "./FileViewer";

// Selection is one thing here: a click on a file row selects that file (ctrl toggles, shift takes a
// range, the checkbox toggles), and folders are ticked in the tree. Exactly one selected file and no
// selected folder opens the viewer panel to the right; anything else gives the list the whole width.

type SortColumn = "name" | "type" | "size" | "modified";
interface SortState {
  column: SortColumn;
  ascending: boolean;
}

export function FilesSection({ db }: { db: DatabaseInfo }) {
  const [ios, setIos] = useState<IoInfo[]>([]);
  const [ioId, setIoId] = useState<string | null>(null);
  const [listings, setListings] = useState<Record<string, FolderListing>>({});
  const [expanded, setExpanded] = useState<Set<string>>(new Set([""]));
  const [path, setPath] = useState("");
  const [selected, setSelected] = useState<Set<string>>(new Set()); // file keys, within the open folder
  const [selectedFolders, setSelectedFolders] = useState<Set<string>>(new Set()); // folder paths, anywhere in the tree
  const selectionAnchor = useRef<string | null>(null); // the row a shift-click ranges from
  const [treeSizes, setTreeSizes] = useState<Record<string, FolderSize | "pending">>({});
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  // whether the viewer's editor holds unsaved changes (a ref: the guards below read it inside
  // async handlers)
  const viewerDirty = useRef(false);
  const onViewerDirty = useCallback((dirty: boolean) => {
    viewerDirty.current = dirty;
  }, []);
  const [treeWidth, setTreeWidth] = useState(() => Number(localStorage.getItem(treeWidthKey)) || 260);
  const [viewerWidth, setViewerWidth] = useState(() => Number(localStorage.getItem(viewerWidthKey)) || 520);
  const [resizing, setResizing] = useState<"tree" | "viewer" | null>(null);
  const bodyRef = useRef<HTMLDivElement>(null); // measured while dragging the viewer's divider
  const [sort, setSort] = useState<SortState>(() => readSort());
  // index files are named after the property they index, so most of the tree reads as guids until
  // this is on; the map comes from the database and the substitution is display only
  const [names, setNames] = useState<NameMap>({});
  const [friendly, setFriendly] = useState(() => localStorage.getItem(friendlyNamesKey) === "true");
  const show = useCallback((name: string) => (friendly ? friendlyName(name, names) : name), [friendly, names]);
  const showPath = useCallback((p: string) => (friendly ? friendlyPath(p, names) : p), [friendly, names]);

  function toggleFriendly() {
    const next = !friendly;
    setFriendly(next);
    localStorage.setItem(friendlyNamesKey, String(next));
  }

  function resetView() {
    setListings({});
    setExpanded(new Set([""]));
    setPath("");
    setSelected(new Set());
    setSelectedFolders(new Set());
    selectionAnchor.current = null;
    setTreeSizes({});
    viewerDirty.current = false;
  }

  // the providers of the active database; reset everything when the database changes
  useEffect(() => {
    setIos([]);
    setIoId(null);
    resetView();
    setError(null);
    setNames({});
    fetchIoList(db.id)
      .then((list) => {
        setIos(list);
        setIoId(list[0]?.id ?? null);
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
    // names are a nicety: a database that cannot answer just keeps showing guids
    fetchNameMap(db.id)
      .then(setNames)
      .catch(() => setNames({}));
  }, [db.id]);

  const loadFolder = useCallback(
    (io: string, folderPath: string) => {
      fetchFolder(io, folderPath)
        .then((listing) => {
          setListings((prev) => ({ ...prev, [folderPath]: listing }));
          setError(null);
        })
        .catch((e) => setError(e instanceof Error ? e.message : String(e)));
    },
    [setListings],
  );

  // load the root whenever the provider changes
  useEffect(() => {
    if (!ioId) return;
    resetView();
    loadFolder(ioId, "");
  }, [ioId, loadFolder]);

  const listing = listings[path];
  const files = listing?.files ?? [];
  const io = ios.find((candidate) => candidate.id === ioId) ?? null;
  // the website project folder: not database storage, so the type column reads from the extension
  // and the notice is a different one
  const projectRoot = io?.kind === "projectRoot";
  const typeOf = useCallback((f: FileInfo) => (projectRoot ? displayType(fileName(f.key)) : (f.description ?? "")), [projectRoot]);
  const sortedFiles = useMemo(() => sortFiles(files, sort, typeOf), [files, sort, typeOf]);
  const listedSize = files.reduce((sum, f) => sum + f.size, 0);
  const allSelected = files.length > 0 && files.every((f) => selected.has(f.key));
  // the open folder belongs to the database's own storage: everything in it is the real data
  const primaryData = listing?.isPrimaryData === true;
  // the viewer: exactly one file and no folder selected
  const viewFile = selected.size === 1 && selectedFolders.size === 0 ? (files.find((f) => selected.has(f.key)) ?? null) : null;

  // the editor may hold changes; nothing that would take its file away goes ahead without asking
  async function confirmDiscard(): Promise<boolean> {
    if (!viewerDirty.current) return true;
    const { ok } = await showConfirm("Discard unsaved changes?", "The file open in the viewer has changes that have not been saved.", {
      confirmLabel: "Discard",
      danger: true,
    });
    if (ok) viewerDirty.current = false;
    return ok;
  }

  // every change of selection goes through here, so the viewer's unsaved changes are never lost
  // without a word: the viewed file stays viewed only if it remains the single selection
  async function changeSelection(nextFiles: Set<string>, nextFolders: Set<string> = selectedFolders): Promise<boolean> {
    const nextView = nextFiles.size === 1 && nextFolders.size === 0 ? [...nextFiles][0] : null;
    if (viewFile && nextView !== viewFile.key && !(await confirmDiscard())) return false;
    setSelected(nextFiles);
    if (nextFolders !== selectedFolders) setSelectedFolders(nextFolders);
    return true;
  }

  function computeTreeSize(folderPath: string) {
    if (!ioId) return;
    setTreeSizes((prev) => ({ ...prev, [folderPath]: "pending" }));
    fetchFolderSize(ioId, folderPath)
      .then((size) => setTreeSizes((prev) => ({ ...prev, [folderPath]: size })))
      .catch(() => setTreeSizes((prev) => ({ ...prev, [folderPath]: { size: -1, fileCount: 0, folderCount: 0 } })));
  }

  // opens the folder (its files show in the list) without expanding its tree node; expanding and
  // collapsing is the chevron's job. Ticked folders stay ticked: they may be gathered for a delete
  async function openFolder(folderPath: string) {
    if (folderPath !== path) {
      if (!(await confirmDiscard())) return;
      setSelected(new Set());
      selectionAnchor.current = null;
    }
    setPath(folderPath);
    if (ioId) loadFolder(ioId, folderPath);
  }

  function toggleExpand(folderPath: string) {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(folderPath)) {
        next.delete(folderPath);
      } else {
        next.add(folderPath);
        if (ioId && !listings[folderPath]) loadFolder(ioId, folderPath);
      }
      return next;
    });
  }

  function toggleFolderSelected(folderPath: string) {
    const next = new Set(selectedFolders);
    if (next.has(folderPath)) next.delete(folderPath);
    else next.add(folderPath);
    changeSelection(selected, next);
  }

  // whether the folder is, or sits below, one of the database's own data folders: from its own
  // listing when loaded, else from the stub in its parent's listing
  function isPrimaryFolder(folderPath: string): boolean {
    if (listings[folderPath]?.isPrimaryData) return true;
    const parent = parentOf(folderPath);
    const name = fileName(folderPath);
    return listings[parent]?.subFolders.some((sub) => sub.name === name && sub.isPrimaryData) === true;
  }

  // ---- file rows: click, ctrl-click, shift-click, checkbox ----

  function onRowClick(e: ReactMouseEvent, file: FileInfo) {
    if (e.shiftKey && selectionAnchor.current !== null) {
      const from = sortedFiles.findIndex((f) => f.key === selectionAnchor.current);
      const to = sortedFiles.findIndex((f) => f.key === file.key);
      if (from >= 0 && to >= 0) {
        const range = sortedFiles.slice(Math.min(from, to), Math.max(from, to) + 1).map((f) => f.key);
        const next = e.ctrlKey || e.metaKey ? new Set(selected) : new Set<string>();
        for (const key of range) next.add(key);
        changeSelection(next);
        return;
      }
    }
    selectionAnchor.current = file.key;
    if (e.ctrlKey || e.metaKey) {
      toggleOne(file.key);
      return;
    }
    changeSelection(new Set([file.key]));
  }

  function toggleOne(key: string) {
    const next = new Set(selected);
    if (next.has(key)) next.delete(key);
    else next.add(key);
    selectionAnchor.current = key;
    changeSelection(next);
  }

  function toggleAll() {
    changeSelection(allSelected ? new Set() : new Set(files.map((f) => f.key)));
  }

  function toggleSort(column: SortColumn) {
    setSort((prev) => {
      const next: SortState = prev.column === column ? { column, ascending: !prev.ascending } : { column, ascending: column === "name" || column === "type" };
      localStorage.setItem(sortKey, JSON.stringify(next));
      return next;
    });
  }

  // what a name may look like, as the server will judge it: database storage keeps to the file
  // key alphabet, the project folder takes any legal file system name
  function nameProblem(name: string): string | null {
    const trimmed = name.trim();
    if (trimmed.length === 0) return "A name is required.";
    if (/[/\\]/.test(trimmed)) return "A name cannot contain / or \\.";
    if (trimmed === "." || trimmed === "..") return "That is not a name.";
    if (projectRoot) {
      // eslint-disable-next-line no-control-regex
      if (/[<>:"|?*\x00-\x1f]/.test(trimmed)) return 'A file name cannot contain < > : " | ? * or control characters.';
      if (trimmed.length > 255) return "The name is too long.";
      return null;
    }
    if (!/^[a-z0-9()\-–_. ]+$/i.test(trimmed)) return "Names in database storage can only contain letters, numbers, dash, space, underscore, dot and parentheses.";
    if (trimmed.length > 100) return "The name can be at most 100 characters.";
    return null;
  }

  async function onRenameFile(file: FileInfo) {
    if (!ioId || !io?.canRenameFile) return;
    const current = fileName(file.key);
    const dot = current.lastIndexOf(".");
    const name = await showPrompt(`Rename ${current}`, "", {
      label: "New name",
      initial: current,
      confirmLabel: "Rename",
      selectEnd: dot > 0 ? dot : current.length,
      validate: nameProblem,
    });
    if (name === null || name.trim() === current) return;
    try {
      const result = await renameFile(ioId, file.key, name.trim());
      setSelected((prev) => {
        if (!prev.has(file.key)) return prev;
        const next = new Set(prev);
        next.delete(file.key);
        next.add(result.key);
        return next;
      });
      if (selectionAnchor.current === file.key) selectionAnchor.current = result.key;
      setMessage(`Renamed to ${fileName(result.key)}.`);
      loadFolder(ioId, path);
    } catch (e) {
      showError("Rename failed", e instanceof Error ? e.message : String(e));
    }
  }

  async function onRenameFolder() {
    if (!ioId || path === "" || !io?.canRenameFolder) return;
    if (!(await confirmDiscard())) return;
    const current = fileName(path);
    const name = await showPrompt(`Rename folder ${current}`, "", { label: "New name", initial: current, confirmLabel: "Rename", validate: nameProblem });
    if (name === null || name.trim() === current) return;
    try {
      const result = await renameFolder(ioId, path, name.trim());
      const oldPath = path;
      const parent = parentOf(oldPath);
      const moved = (key: string) => (key === oldPath || key.startsWith(oldPath + "/") ? result.path + key.slice(oldPath.length) : key);
      // everything cached at or below the old path is stale; expansion and ticks follow the folder
      setListings((prev) => Object.fromEntries(Object.entries(prev).filter(([key]) => key !== oldPath && !key.startsWith(oldPath + "/"))));
      setExpanded((prev) => new Set([...prev].map(moved)));
      setSelectedFolders((prev) => new Set([...prev].map(moved)));
      setTreeSizes({});
      setSelected(new Set());
      setPath(result.path);
      loadFolder(ioId, parent);
      loadFolder(ioId, result.path);
      setMessage(`Renamed folder to ${name.trim()}.`);
    } catch (e) {
      showError("Rename failed", e instanceof Error ? e.message : String(e));
    }
  }

  async function onCreateFolder() {
    if (!ioId) return;
    const name = await showPrompt("New folder", `Created in ${showPath(path) || (projectRoot ? "the server root" : "the storage root")}.`, {
      label: "Folder name",
      confirmLabel: "Create",
      validate: nameProblem,
    });
    if (name === null) return;
    try {
      const result = await createFolder(ioId, path, name.trim());
      if (result.persisted) {
        loadFolder(ioId, path);
        setExpanded((prev) => new Set(prev).add(path));
        setMessage(`Created ${name.trim()}.`);
      } else {
        // virtual folders: it exists once something is in it, so the open (empty) folder is where
        // the next upload should go
        setMessage(`${io?.type ?? "This"} storage keeps a folder only while it holds a file: upload something into ${name.trim()} to keep it.`);
      }
      openFolder(result.path);
    } catch (e) {
      showError("Could not create the folder", e instanceof Error ? e.message : String(e));
    }
  }

  // the dividers: the one between tree and list moves the tree's width, the one between list and
  // viewer moves the viewer's; the list takes what is left
  function startResize(e: ReactMouseEvent, which: "tree" | "viewer") {
    e.preventDefault();
    const startX = e.clientX;
    const startWidth = which === "tree" ? treeWidth : viewerWidth;
    setResizing(which);
    document.body.style.cursor = "col-resize";
    const move = (ev: MouseEvent) => {
      const delta = ev.clientX - startX;
      if (which === "tree") setTreeWidth(Math.max(160, Math.min(700, startWidth + delta)));
      else setViewerWidth(Math.max(320, Math.min(maxViewerWidth(), startWidth - delta)));
    };
    const up = () => {
      window.removeEventListener("mousemove", move);
      window.removeEventListener("mouseup", up);
      document.body.style.cursor = "";
      setResizing(null);
      if (which === "tree") {
        setTreeWidth((width) => {
          localStorage.setItem(treeWidthKey, String(width));
          return width;
        });
      } else {
        setViewerWidth((width) => {
          localStorage.setItem(viewerWidthKey, String(width));
          return width;
        });
      }
    };
    window.addEventListener("mousemove", move);
    window.addEventListener("mouseup", up);
  }

  // ---- delete: the selected files of the open folder and the ticked folders, in one go ----

  async function deleteSelected() {
    if (!ioId) return;
    const io = ioId;
    // a ticked folder inside another ticked folder goes with its parent, and so does a selected
    // file inside a ticked folder
    const folders = [...selectedFolders].filter((f) => f !== "").sort();
    const topFolders = folders.filter((f) => !folders.some((other) => other !== f && f.startsWith(other + "/")));
    const fileKeys = files.filter((f) => selected.has(f.key) && !topFolders.some((folder) => f.key.startsWith(folder + "/"))).map((f) => f.key);
    const fileCount = fileKeys.length;
    const folderCount = topFolders.length;
    if (fileCount + folderCount === 0) return;
    const parts: string[] = [];
    if (fileCount > 0) parts.push(`${fileCount} file${fileCount === 1 ? "" : "s"}`);
    if (folderCount > 0) parts.push(`${folderCount} folder${folderCount === 1 ? "" : "s"}`);
    const label = parts.join(" and ");
    const touchesPrimary = (fileCount > 0 && primaryData) || topFolders.some(isPrimaryFolder);
    if (touchesPrimary && !(await confirmPrimaryData(`Deleting ${label}.`))) return;
    const { ok } = await showConfirm(
      `Delete ${label}?`,
      (folderCount > 0 ? "Folders are deleted with every file in them and every folder below them. " : "") +
        `${label} ${fileCount + folderCount === 1 ? "is" : "are"} deleted from the storage. This cannot be undone.`,
      { confirmLabel: "Delete", danger: true },
    );
    if (!ok) return;
    viewerDirty.current = false;
    const failed = await runWithProgress(`Delete ${label}`, async (ctl: ProgressController) => {
      const failures: string[] = [];
      for (const folder of topFolders) {
        ctl.set({ label: showPath(folder), total: null, done: 0 });
        failures.push(...(await deleteFolderWithProgress(ctl, io, folder)));
      }
      if (fileKeys.length > 0) {
        ctl.set({ label: `${fileKeys.length} file${fileKeys.length === 1 ? "" : "s"}`, total: null });
        const result = await deleteFiles(io, fileKeys);
        failures.push(...result.errors);
      }
      return failures;
    });
    if (failed) {
      if (failed.length > 0) showError("Could not delete everything", `${failed.length} item${failed.length === 1 ? "" : "s"} could not be deleted.`, failed);
      else setMessage(`Deleted ${label}.`);
    }
    // what is gone leaves the caches, the ticks and, if the open folder went with it, the path
    setSelected(new Set());
    setSelectedFolders(new Set());
    selectionAnchor.current = null;
    setListings((prev) => Object.fromEntries(Object.entries(prev).filter(([key]) => !topFolders.some((f) => key === f || key.startsWith(f + "/")))));
    setTreeSizes({});
    const gone = topFolders.find((f) => path === f || path.startsWith(f + "/"));
    for (const folder of topFolders) loadFolder(io, parentOf(folder));
    if (gone) {
      setPath(parentOf(gone));
      loadFolder(io, parentOf(gone));
    } else {
      loadFolder(io, path);
    }
  }

  const fileInput = useRef<HTMLInputElement>(null);
  const folderInput = useRef<HTMLInputElement>(null);

  async function onUploadPicked(list: FileList | null, useRelativePaths: boolean) {
    if (!list || list.length === 0 || !ioId) return;
    const entries: UploadEntry[] = [...list].map((f) => ({
      file: f,
      relativePath: useRelativePaths && f.webkitRelativePath ? f.webkitRelativePath : f.name,
    }));
    await uploadWithDialog(ioId, path, entries);
  }

  async function uploadWithDialog(io: string, target: string, entries: UploadEntry[]) {
    const failed = await runWithProgress(`Upload to ${showPath(target) || "storage root"}`, (ctl) => uploadEntries(ctl, io, target, entries));
    if (failed) {
      if (failed.length > 0) {
        showError("Upload incomplete", `${entries.length - failed.length} of ${entries.length} files were uploaded.`, failed);
      } else {
        setMessage(`Uploaded ${entries.length} file${entries.length === 1 ? "" : "s"}.`);
      }
    }
    loadFolder(io, target); // also after a cancel: some files may have landed
  }

  // drag and drop from the OS: any mix of files and folders, dropped anywhere in the section
  const [dropActive, setDropActive] = useState(false);
  const dragDepth = useRef(0);

  function dragHasFiles(e: DragEvent) {
    return ioId !== null && e.dataTransfer.types.includes("Files");
  }

  function onDragEnter(e: DragEvent) {
    if (!dragHasFiles(e)) return;
    e.preventDefault();
    dragDepth.current += 1;
    setDropActive(true);
  }

  function onDragOver(e: DragEvent) {
    if (!dragHasFiles(e)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = "copy";
  }

  function onDragLeave(e: DragEvent) {
    if (!dragHasFiles(e)) return;
    dragDepth.current = Math.max(0, dragDepth.current - 1);
    if (dragDepth.current === 0) setDropActive(false);
  }

  // drag a file row OUT to the desktop: Chromium's DownloadURL type makes the browser
  // download the file (cookie-authed) to wherever it is dropped; other browsers ignore it
  function onFileDragStart(e: DragEvent, file: FileInfo) {
    if (!ioId) return;
    const url = location.origin + downloadUrl(db.id, ioId, file.key);
    e.dataTransfer.setData("DownloadURL", `application/octet-stream:${fileName(file.key)}:${url}`);
    e.dataTransfer.effectAllowed = "copy";
  }

  // drag a FOLDER out to the desktop: it arrives as a zip (a drag-out can only carry one
  // file); the endpoint checks all files for locks before streaming a single byte
  function onFolderDragStart(e: DragEvent, folderPath: string) {
    if (!ioId) return;
    const name = (folderPath === "" ? "storage-root" : folderPath.split("/").pop()) + ".zip";
    const url = location.origin + zipFolderUrl(ioId, folderPath);
    e.dataTransfer.setData("DownloadURL", `application/zip:${name}:${url}`);
    e.dataTransfer.effectAllowed = "copy";
  }

  // downloads the selected files as one zip: lock check first (stops with the locked list),
  // then the stream goes to a picked file (Chromium) or through memory to a normal download
  async function onDownloadSelectionZip() {
    if (!ioId || selected.size === 0) return;
    const io = ioId;
    const target = path;
    const keys = sortedFiles.filter((f) => selected.has(f.key)).map((f) => f.key);
    const zipName = (target === "" ? "storage-root" : target.split("/").pop()) + "-files.zip";
    let sink: ZipSink;
    let finish: (() => void) | null = null;
    if (window.showSaveFilePicker) {
      let writable: FileSystemWritableFileStream;
      try {
        const handle = await window.showSaveFilePicker({
          suggestedName: zipName,
          types: [{ description: "Zip archive", accept: { "application/zip": [".zip"] } }],
        });
        writable = await handle.createWritable();
      } catch {
        return; // picker dismissed
      }
      sink = { write: (chunk) => writable.write(chunk as Uint8Array<ArrayBuffer>), close: () => writable.close(), abort: () => writable.abort() };
    } else {
      const chunks: Uint8Array[] = [];
      sink = {
        write: async (chunk) => {
          chunks.push(chunk);
        },
        close: async () => {},
        abort: async () => {
          chunks.length = 0;
        },
      };
      finish = () => {
        const link = document.createElement("a");
        link.href = URL.createObjectURL(new Blob(chunks as BlobPart[], { type: "application/zip" }));
        link.download = zipName;
        link.click();
        setTimeout(() => URL.revokeObjectURL(link.href), 10_000);
      };
    }
    const result = await runWithProgress(`Zip ${keys.length} file${keys.length === 1 ? "" : "s"}`, (ctl) => downloadZipToSink(ctl, io, keys, target, sink));
    if (!result) return; // cancelled or failed (the dialog showed it); the sink was aborted
    if ("locked" in result) {
      showError("Files are in use", "The zip was not created because some files are locked.", result.locked);
    } else {
      finish?.();
      setMessage(`Zip created (${formatBytes(result.bytes)}).`);
    }
  }

  async function onDrop(e: DragEvent) {
    dragDepth.current = 0;
    setDropActive(false);
    if (!dragHasFiles(e) || !ioId) return;
    e.preventDefault();
    const items = itemsFromDrop(e.dataTransfer); // must run synchronously in the drop handler
    if (items.length === 0) return;
    const io = ioId;
    const target = path;
    const entries = await runWithProgress("Reading dropped items", (ctl) => resolveDroppedItems(ctl, items));
    if (!entries) return; // cancelled or failed
    if (entries.length === 0) {
      setMessage("The dropped items contained no files.");
      return;
    }
    const totalBytes = entries.reduce((sum, entry) => sum + entry.file.size, 0);
    const { ok } = await showConfirm(
      "Upload dropped items",
      `Upload ${entries.length} file${entries.length === 1 ? "" : "s"} (${formatBytes(totalBytes)}) to ${showPath(target) || "the storage root"}? Existing files with the same names are overwritten.`,
      { confirmLabel: "Upload" },
    );
    if (!ok) return;
    await uploadWithDialog(io, target, entries);
  }

  async function onDeleteFolder() {
    if (!ioId || path === "") return;
    if (primaryData && !(await confirmPrimaryData(`Deleting ${showPath(path)} and everything below it.`))) return;
    const { ok } = await showConfirm(
      `Delete ${showPath(path)}?`,
      "The folder is deleted with every file in it and every folder below it. This cannot be undone.",
      { confirmLabel: "Delete", danger: true },
    );
    if (!ok) return;
    viewerDirty.current = false;
    const io = ioId;
    const folder = path;
    const parent = parentOf(folder);
    const failed = await runWithProgress(`Delete ${showPath(folder)}`, (ctl) => deleteFolderWithProgress(ctl, io, folder));
    if (failed) {
      if (failed.length > 0) {
        showError("Could not delete the folder", `${failed.length} file${failed.length === 1 ? "" : "s"} could not be deleted, so the folder was kept.`, failed);
      } else {
        setMessage(`Deleted ${showPath(folder)}.`);
      }
    }
    setSelectedFolders((prev) => new Set([...prev].filter((f) => f !== folder && !f.startsWith(folder + "/"))));
    openFolder(parent); // also after cancel or failures: shows what is left
  }

  async function onDownloadFolder() {
    if (!ioId) return;
    const directory = await pickDirectory();
    if (directory === "unsupported") {
      setMessage("Folder download requires a Chromium based browser (File System Access API).");
      return;
    }
    if (!directory) return;
    const io = ioId;
    const failed = await runWithProgress(`Download ${showPath(path) || "storage root"}`, (ctl) => downloadFolderToDirectory(ctl, db.id, io, path, directory));
    if (failed) {
      if (failed.length > 0) {
        showError("Download incomplete", `${failed.length} file${failed.length === 1 ? "" : "s"} could not be downloaded.`, failed);
      } else {
        setMessage("Folder downloaded.");
      }
    }
  }

  // a sortable column heading
  function sortHeader(column: SortColumn, label: string, numeric = false) {
    const active = sort.column === column;
    return (
      <button className={"file-sort" + (numeric ? " num" : "") + (active ? " active" : "")} onClick={() => toggleSort(column)} title={`Sort by ${label.toLowerCase()}`}>
        {label}
        {active && (sort.ascending ? <IconArrowUp size={11} stroke={2.2} /> : <IconArrowDown size={11} stroke={2.2} />)}
      </button>
    );
  }

  const deletable = files.filter((f) => selected.has(f.key)).length + [...selectedFolders].filter((f) => f !== "").length;
  const compact = viewFile !== null; // the viewer is open: the list keeps the name and the size
  // the viewer may take everything but the tree, the bars and a readable list: a stored width from
  // a wider window is cut down to that here, and the drag never grows it past it
  const listMinWidth = 340; // room for the compact columns (name, size, actions) without clipping
  const maxViewerWidth = () => Math.max(320, (bodyRef.current?.clientWidth ?? Infinity) - treeWidth - 2 * barSize - listMinWidth);
  const columns =
    `${treeWidth}px ${barSize}px minmax(0, 1fr)` +
    (viewFile ? ` ${barSize}px min(${viewerWidth}px, calc(100% - ${treeWidth + 2 * barSize + listMinWidth}px))` : "");

  return (
    <div className="files" onDragEnter={onDragEnter} onDragOver={onDragOver} onDragLeave={onDragLeave} onDrop={onDrop}>
      {dropActive && (
        <div className="drop-overlay">
          <IconFolderUp size={20} stroke={1.8} />
          <span>Drop to upload to {showPath(path) || "the storage root"}</span>
        </div>
      )}
      <div className="files-toolbar">
        <select
          className="select"
          value={ioId ?? ""}
          onChange={async (e) => {
            const next = e.target.value;
            if (await confirmDiscard()) setIoId(next);
          }}
          disabled={ios.length === 0}
        >
          {ios.length === 0 && <option value="">No IO providers</option>}
          {ios.map((candidate) => (
            <option key={candidate.id} value={candidate.id}>
              {candidate.kind === "projectRoot" ? candidate.name : `${candidate.name} (${candidate.type})`}
            </option>
          ))}
        </select>
        <button className="icon-button" title="Refresh" onClick={() => ioId && loadFolder(ioId, path)}>
          <IconRefresh size={16} stroke={1.8} />
        </button>
        <button className="icon-button" title="Upload files to this folder" onClick={() => fileInput.current?.click()} disabled={!ioId}>
          <IconUpload size={16} stroke={1.8} />
        </button>
        <button className="icon-button" title="Upload a folder into this folder" onClick={() => folderInput.current?.click()} disabled={!ioId}>
          <IconFolderUp size={16} stroke={1.8} />
        </button>
        <button className="icon-button" title="Download this folder and everything in it to disk" onClick={onDownloadFolder} disabled={!ioId}>
          <IconFolderDown size={16} stroke={1.8} />
        </button>
        <button className="icon-button" title="Create a folder in this folder" onClick={onCreateFolder} disabled={!ioId}>
          <IconFolderPlus size={16} stroke={1.8} />
        </button>
        <button
          className="icon-button"
          title={
            !io?.canRenameFolder ? `${io?.type ?? "This"} storage cannot rename folders` : path === "" ? "The storage root cannot be renamed" : "Rename this folder"
          }
          onClick={onRenameFolder}
          disabled={!ioId || path === "" || !io?.canRenameFolder}
        >
          <IconPencil size={16} stroke={1.8} />
        </button>
        <button
          className="icon-button danger"
          title={path === "" ? "The storage root cannot be deleted" : "Delete this folder and everything in it"}
          onClick={onDeleteFolder}
          disabled={!ioId || path === ""}
        >
          <IconFolderX size={16} stroke={1.8} />
        </button>
        <input
          ref={fileInput}
          type="file"
          multiple
          hidden
          onChange={(e) => {
            onUploadPicked(e.target.files, false);
            e.target.value = "";
          }}
        />
        <input
          ref={folderInput}
          type="file"
          hidden
          {...({ webkitdirectory: "" } as object)}
          onChange={(e) => {
            onUploadPicked(e.target.files, true);
            e.target.value = "";
          }}
        />
        <label className="files-friendly" title="Show what the guids in file and folder names stand for - index files are named after the property they index">
          <input type="checkbox" checked={friendly} onChange={toggleFriendly} />
          Friendly names
        </label>
        <div className="header-spacer" />
        {message && <span className="muted files-message">{message}</span>}
        {selected.size > 0 && (
          <button className="action-button" onClick={onDownloadSelectionZip}>
            <IconFileZip size={14} stroke={1.8} /> Download {selected.size} as zip
          </button>
        )}
        {deletable > 0 && (
          <button className="action-button danger" onClick={deleteSelected}>
            <IconTrash size={14} stroke={1.8} /> Delete {deletable} selected
          </button>
        )}
      </div>
      {error && <div className="login-error">{error}</div>}
      <div ref={bodyRef} className={"files-body" + (resizing ? " resizing" : "")} style={{ gridTemplateColumns: columns }}>
        <section className="panel files-tree" style={{ minWidth: 0 }}>
          <FolderNode
            path=""
            name={projectRoot ? "[Server root]" : "Storage root"}
            hasSubFolders
            isPrimaryData={false}
            depth={0}
            listings={listings}
            expanded={expanded}
            currentPath={path}
            selectedFolders={selectedFolders}
            sizes={treeSizes}
            onComputeSize={computeTreeSize}
            onOpen={openFolder}
            onToggle={toggleExpand}
            onToggleSelect={toggleFolderSelected}
            onDragOut={onFolderDragStart}
            show={show}
          />
        </section>
        <div
          className={"pg-bar pg-vbar" + (resizing === "tree" ? " active" : "")}
          onMouseDown={(e) => startResize(e, "tree")}
          title="Drag to resize the folder tree"
          role="separator"
          aria-orientation="vertical"
        />
        <section className="panel files-list">
          {primaryData && (
            <div className="files-notice">
              <IconAlertTriangle size={15} stroke={1.8} />
              <span>
                <b>{folderNote(listing) ?? "The database's own data"}.</b> This is the actual data, not a cache and not a copy: nothing here can be
                generated again. Deleting any of it loses data for good.
              </span>
            </div>
          )}
          {projectRoot && (
            <div className="files-notice project">
              <IconWorld size={15} stroke={1.8} />
              <span>
                <b>[Server root]</b>
                {io?.localPath ? ` (${io.localPath})` : ""}: the website project folder on the server. These are the application's own files, not
                database storage: what is changed or deleted here changes the running site, and the database's data folder may be below here too.
              </span>
            </div>
          )}
          <div className={"file-table" + (compact ? " compact" : "")}>
            <div className="file-row file-head">
              <input type="checkbox" checked={allSelected} onChange={toggleAll} disabled={files.length === 0} />
              {sortHeader("name", "Name")}
              {!compact && sortHeader("type", "Type")}
              {sortHeader("size", "Size", true)}
              {!compact && sortHeader("modified", "Modified")}
              <span />
            </div>
            {sortedFiles.map((f) => (
              <div
                key={f.key}
                className={"file-row" + (selected.has(f.key) ? " selected" : "") + (viewFile?.key === f.key ? " viewing" : "")}
                draggable={!!ioId}
                onDragStart={(e) => onFileDragStart(e, f)}
                onClick={(e) => onRowClick(e, f)}
              >
                <input type="checkbox" checked={selected.has(f.key)} onChange={() => toggleOne(f.key)} onClick={(e) => e.stopPropagation()} />
                <span className="file-name" title={fileName(f.key)}>
                  <IconFile size={14} stroke={1.6} />
                  {show(fileName(f.key))}
                  {(f.readers > 0 || f.writers > 0) && (
                    <span className="badge" title={`${f.readers} readers, ${f.writers} writers`}>
                      in use
                    </span>
                  )}
                </span>
                {!compact && <span className="muted">{typeOf(f)}</span>}
                <span className="num">{formatBytes(f.size)}</span>
                {!compact && <span className="muted">{formatTime(f.lastModifiedUtc)}</span>}
                <span className="file-actions" onClick={(e) => e.stopPropagation()}>
                  {io?.canRenameFile && (
                    <button className="icon-button" title="Rename" onClick={() => onRenameFile(f)}>
                      <IconPencil size={15} stroke={1.8} />
                    </button>
                  )}
                  <a className="icon-button" href={ioId ? downloadUrl(db.id, ioId, f.key) : "#"} title="Download" download>
                    <IconDownload size={15} stroke={1.8} />
                  </a>
                </span>
              </div>
            ))}
            {listing && files.length === 0 && <div className="muted files-empty">No files in this folder.</div>}
          </div>
          <div className="files-footer muted">
            {files.length} files · {formatBytes(listedSize)}
            {selected.size > 0 ? ` · ${selected.size} selected` : ""}
            {selectedFolders.size > 0 ? ` · ${selectedFolders.size} folder${selectedFolders.size === 1 ? "" : "s"} ticked` : ""}
            {!compact && files.length > 0 && (
              <span className="files-hint">
                <IconEye size={12} stroke={1.8} /> Click one file to view it; ctrl or shift for more
              </span>
            )}
          </div>
        </section>
        {viewFile && ioId && (
          <>
            <div
              className={"pg-bar pg-vbar" + (resizing === "viewer" ? " active" : "")}
              onMouseDown={(e) => startResize(e, "viewer")}
              title="Drag to resize the viewer"
              role="separator"
              aria-orientation="vertical"
            />
            <section className="panel files-viewer">
              <FileViewer
                key={ioId + "|" + viewFile.key}
                storeId={db.id}
                ioId={ioId}
                file={viewFile}
                name={show(fileName(viewFile.key))}
                onSaved={() => loadFolder(ioId, path)}
                onDirtyChange={onViewerDirty}
                onClose={() => changeSelection(new Set())}
              />
            </section>
          </>
        )}
      </div>
    </div>
  );
}

const barSize = 14; // the same divider as the dashboard's panel grid (see PanelGrid)
const treeWidthKey = "filesTreeWidth";
const viewerWidthKey = "filesViewerWidth";
const sortKey = "filesSort";

function readSort(): SortState {
  try {
    const stored = JSON.parse(localStorage.getItem(sortKey) ?? "") as Partial<SortState>;
    if (stored && ["name", "type", "size", "modified"].includes(stored.column as string)) return { column: stored.column as SortColumn, ascending: stored.ascending !== false };
  } catch {
    // no or unreadable preference
  }
  return { column: "name", ascending: true };
}

const nameCollator = new Intl.Collator(undefined, { numeric: true, sensitivity: "base" });

function sortFiles(files: FileInfo[], sort: SortState, typeOf: (f: FileInfo) => string): FileInfo[] {
  const direction = sort.ascending ? 1 : -1;
  const byName = (a: FileInfo, b: FileInfo) => nameCollator.compare(fileName(a.key), fileName(b.key));
  return [...files].sort((a, b) => {
    let result = 0;
    switch (sort.column) {
      case "type":
        result = nameCollator.compare(typeOf(a), typeOf(b));
        break;
      case "size":
        result = a.size - b.size;
        break;
      case "modified":
        result = Date.parse(a.lastModifiedUtc) - Date.parse(b.lastModifiedUtc);
        break;
    }
    if (result === 0) result = byName(a, b); // the name settles ties, so the order is stable to the eye
    return result * direction;
  });
}

function fileName(key: string): string {
  const i = key.lastIndexOf("/");
  return i < 0 ? key : key.slice(i + 1);
}

function parentOf(folderPath: string): string {
  const i = folderPath.lastIndexOf("/");
  return i < 0 ? "" : folderPath.slice(0, i);
}

interface FolderNodeProps {
  path: string;
  name: string;
  hasSubFolders: boolean;
  isPrimaryData: boolean;
  note?: string | null; // from the stub in the parent's listing, until this folder's own is loaded
  depth: number;
  listings: Record<string, FolderListing>;
  expanded: Set<string>;
  currentPath: string;
  selectedFolders: Set<string>;
  sizes: Record<string, FolderSize | "pending">;
  onComputeSize: (path: string) => void;
  onOpen: (path: string) => void;
  onToggle: (path: string) => void;
  onToggleSelect: (path: string) => void;
  onDragOut: (e: DragEvent, path: string) => void;
  show: (name: string) => string; // the folder name as the user has asked to see it
}

/**
 * The database's own data folders head the list, the rest stay in the order the server sent them
 * (by name). They are what someone opening this tree is usually after, and what they most need to
 * have seen before deleting anything - "data" and "files" sorting into the middle of the copies and
 * the rebuildable folders is exactly where a marking does the least good. The sort is stable, so
 * nothing else moves, and below the top level it does nothing: everything under a data folder is
 * data itself.
 */
function dataFoldersFirst(folders: FolderListing[]): FolderListing[] {
  if (!folders.some((f) => f.isPrimaryData === true)) return folders;
  return [...folders].sort((a, b) => Number(b.isPrimaryData === true) - Number(a.isPrimaryData === true));
}

function FolderNode(p: FolderNodeProps) {
  const isExpanded = p.expanded.has(p.path);
  const listing = p.listings[p.path];
  const children = dataFoldersFirst(listing?.subFolders ?? []);
  const size = p.sizes[p.path];
  const note = folderNote(listing) ?? p.note;
  const name = p.show(p.name);
  const ticked = p.selectedFolders.has(p.path);
  return (
    <>
      <div
        className={"tree-row" + (p.currentPath === p.path ? " active" : "") + (ticked ? " ticked" : "")}
        style={{ paddingLeft: 4 + p.depth * 14 }}
        draggable
        onDragStart={(e) => p.onDragOut(e, p.path)}
      >
        <button
          className="tree-chevron"
          onClick={() => p.onToggle(p.path)}
          style={{ visibility: p.hasSubFolders ? "visible" : "hidden" }}
        >
          {isExpanded ? <IconChevronDown size={13} stroke={2} /> : <IconChevronRight size={13} stroke={2} />}
        </button>
        {p.path !== "" && (
          <input
            type="checkbox"
            className="tree-check"
            checked={ticked}
            onChange={() => p.onToggleSelect(p.path)}
            onClick={(e) => e.stopPropagation()}
            title="Select this folder, e.g. to delete several at once"
          />
        )}
        <button
          className="tree-label"
          onClick={() => p.onOpen(p.path)}
          title={p.isPrimaryData ? `${p.name} — ${note ?? "the database's own data"}. ${primaryDataTip}` : note ? `${p.name} — ${note}` : p.name}
        >
          {/* the database's own data keeps the outline every other folder has and is filled with a
              light wash of it (see .folder-data): a marking that reads at a glance in a tree of empty
              folders, survives a colour-blind reader and a greyscale screenshot, and does not look
              like a warning for something that is simply what the folder is */}
          {p.currentPath === p.path ? (
            <IconFolderOpen size={15} stroke={1.7} className={p.isPrimaryData ? "folder-data" : undefined} />
          ) : (
            <IconFolder size={15} stroke={1.7} className={p.isPrimaryData ? "folder-data" : undefined} />
          )}
          <span>{name}</span>
        </button>
        {size === undefined || size === "pending" ? (
          <button
            className="tree-size"
            onClick={() => p.onComputeSize(p.path)}
            title="Compute the size of this folder and everything in it"
            disabled={size === "pending"}
          >
            {size === "pending" ? "…" : <IconSum size={12} stroke={1.8} />}
          </button>
        ) : (
          <button
            className="tree-size computed"
            onClick={() => p.onComputeSize(p.path)}
            data-tip={size.size < 0 ? "Failed, click to retry" : `${size.fileCount} ${size.fileCount === 1 ? "file" : "files"} · ${size.folderCount} ${size.folderCount === 1 ? "folder" : "folders"}`}
          >
            {size.size < 0 ? "?" : formatBytes(size.size)}
          </button>
        )}
      </div>
      {isExpanded &&
        children.map((sub) => (
          <FolderNode
            key={sub.name}
            path={p.path === "" ? sub.name : `${p.path}/${sub.name}`}
            name={sub.name}
            hasSubFolders={sub.hasSubFolders}
            isPrimaryData={sub.isPrimaryData === true}
            note={folderNote(sub)}
            depth={p.depth + 1}
            listings={p.listings}
            expanded={p.expanded}
            currentPath={p.currentPath}
            selectedFolders={p.selectedFolders}
            sizes={p.sizes}
            onComputeSize={p.onComputeSize}
            onOpen={p.onOpen}
            onToggle={p.onToggle}
            onToggleSelect={p.onToggleSelect}
            onDragOut={p.onDragOut}
            show={p.show}
          />
        ))}
    </>
  );
}

const friendlyNamesKey = "filesFriendlyNames";

const primaryDataTip = "Nothing can generate it again, so deleting anything here loses data for good.";

/**
 * The extra dialog in front of deleting anything inside the database's own data folders - the log
 * files and the file store. Everything else below the storage root is a copy (backups) or rebuilt
 * on demand (state, indexes, converted files, logs); these two are the data itself, so this asks
 * before the normal delete confirmation does, and its acknowledgement has to be ticked: the point
 * is that it cannot be clicked through the way a second confirmation can.
 */
async function confirmPrimaryData(what: string): Promise<boolean> {
  const { ok } = await showConfirm(
    "This is the actual data",
    `${what} These files are the database's own storage - not a cache and not a copy. ${primaryDataTip} The database may end up missing content, or fail to open at all.`,
    {
      confirmLabel: "Continue",
      danger: true,
      option: { label: "I understand this data cannot be recovered", required: true },
    },
  );
  return ok;
}
