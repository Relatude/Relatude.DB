import { useCallback, useEffect, useRef, useState, type DragEvent } from "react";
import {
  IconAlertTriangle,
  IconChevronDown,
  IconChevronRight,
  IconDownload,
  IconFile,
  IconFileZip,
  IconFolder,
  IconFolderDown,
  IconFolderOpen,
  IconFolderUp,
  IconFolderX,
  IconRefresh,
  IconSum,
  IconTrash,
  IconUpload,
} from "@tabler/icons-react";
import {
  deleteFiles,
  deleteFolderWithProgress,
  downloadFolderToDirectory,
  downloadUrl,
  downloadZipToSink,
  fetchFolder,
  fetchFolderSize,
  fetchIoList,
  folderNote,
  itemsFromDrop,
  pickDirectory,
  resolveDroppedItems,
  uploadEntries,
  zipFolderUrl,
  type FileInfo,
  type FolderListing,
  type FolderSize,
  type IoInfo,
  type UploadEntry,
  type ZipSink,
} from "../server/files";
import type { DatabaseInfo } from "../server/serverInfo";
import { formatBytes, formatTime } from "../format";
import { runWithProgress, showConfirm, showError } from "../dialogs";

export function FilesSection({ db }: { db: DatabaseInfo }) {
  const [ios, setIos] = useState<IoInfo[]>([]);
  const [ioId, setIoId] = useState<string | null>(null);
  const [listings, setListings] = useState<Record<string, FolderListing>>({});
  const [expanded, setExpanded] = useState<Set<string>>(new Set([""]));
  const [path, setPath] = useState("");
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [treeSizes, setTreeSizes] = useState<Record<string, FolderSize | "pending">>({});
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  // the providers of the active database; reset everything when the database changes
  useEffect(() => {
    setIos([]);
    setIoId(null);
    setListings({});
    setExpanded(new Set([""]));
    setPath("");
    setSelected(new Set());
    setTreeSizes({});
    setError(null);
    fetchIoList(db.id)
      .then((list) => {
        setIos(list);
        setIoId(list[0]?.id ?? null);
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
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
    setListings({});
    setExpanded(new Set([""]));
    setPath("");
    setSelected(new Set());
    setTreeSizes({});
    loadFolder(ioId, "");
  }, [ioId, loadFolder]);

  function computeTreeSize(folderPath: string) {
    if (!ioId) return;
    setTreeSizes((prev) => ({ ...prev, [folderPath]: "pending" }));
    fetchFolderSize(ioId, folderPath)
      .then((size) => setTreeSizes((prev) => ({ ...prev, [folderPath]: size })))
      .catch(() => setTreeSizes((prev) => ({ ...prev, [folderPath]: { size: -1, fileCount: 0, folderCount: 0 } })));
  }

  // selects the folder (its files show in the list) without expanding its tree node;
  // expanding and collapsing is the chevron's job
  function openFolder(folderPath: string) {
    setPath(folderPath);
    setSelected(new Set());
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

  const listing = listings[path];
  const files = listing?.files ?? [];
  const listedSize = files.reduce((sum, f) => sum + f.size, 0);
  const allSelected = files.length > 0 && files.every((f) => selected.has(f.key));
  // the open folder belongs to the database's own storage: everything in it is the real data
  const primaryData = listing?.isPrimaryData === true;

  function toggleAll() {
    setSelected(allSelected ? new Set() : new Set(files.map((f) => f.key)));
  }

  function toggleOne(key: string) {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  async function deleteSelected() {
    if (!ioId) return;
    const count = selected.size;
    const label = `${count} file${count === 1 ? "" : "s"}`;
    if (primaryData && !(await confirmPrimaryData(`Deleting ${label} from ${path || "the storage root"}.`))) return;
    const { ok } = await showConfirm(
      `Delete ${label}?`,
      `${label} in ${path || "the storage root"} ${count === 1 ? "is" : "are"} deleted from the storage. This cannot be undone.`,
      { confirmLabel: "Delete", danger: true },
    );
    if (!ok) return;
    try {
      const result = await deleteFiles(ioId, [...selected]);
      if (result.errors.length > 0) {
        showError("Could not delete everything", `${result.deleted} deleted, ${result.errors.length} failed.`, result.errors);
      } else {
        setMessage(`Deleted ${result.deleted} file${result.deleted === 1 ? "" : "s"}.`);
      }
      setSelected(new Set());
      loadFolder(ioId, path);
    } catch (e) {
      showError("Delete failed", e instanceof Error ? e.message : String(e));
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
    const failed = await runWithProgress(`Upload to ${target || "storage root"}`, (ctl) => uploadEntries(ctl, io, target, entries));
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
    const keys = files.filter((f) => selected.has(f.key)).map((f) => f.key);
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
      `Upload ${entries.length} file${entries.length === 1 ? "" : "s"} (${formatBytes(totalBytes)}) to ${target || "the storage root"}? Existing files with the same names are overwritten.`,
      { confirmLabel: "Upload" },
    );
    if (!ok) return;
    await uploadWithDialog(io, target, entries);
  }

  async function onDeleteFolder() {
    if (!ioId || path === "") return;
    if (primaryData && !(await confirmPrimaryData(`Deleting ${path} and everything below it.`))) return;
    const { ok } = await showConfirm(
      `Delete ${path}?`,
      "The folder is deleted with every file in it and every folder below it. This cannot be undone.",
      { confirmLabel: "Delete", danger: true },
    );
    if (!ok) return;
    const io = ioId;
    const folder = path;
    const parent = folder.includes("/") ? folder.slice(0, folder.lastIndexOf("/")) : "";
    const failed = await runWithProgress(`Delete ${folder}`, (ctl) => deleteFolderWithProgress(ctl, io, folder));
    if (failed) {
      if (failed.length > 0) {
        showError("Could not delete the folder", `${failed.length} file${failed.length === 1 ? "" : "s"} could not be deleted, so the folder was kept.`, failed);
      } else {
        setMessage(`Deleted ${folder}.`);
      }
    }
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
    const failed = await runWithProgress(`Download ${path || "storage root"}`, (ctl) => downloadFolderToDirectory(ctl, db.id, io, path, directory));
    if (failed) {
      if (failed.length > 0) {
        showError("Download incomplete", `${failed.length} file${failed.length === 1 ? "" : "s"} could not be downloaded.`, failed);
      } else {
        setMessage("Folder downloaded.");
      }
    }
  }

  return (
    <div className="files" onDragEnter={onDragEnter} onDragOver={onDragOver} onDragLeave={onDragLeave} onDrop={onDrop}>
      {dropActive && (
        <div className="drop-overlay">
          <IconFolderUp size={20} stroke={1.8} />
          <span>Drop to upload to {path || "the storage root"}</span>
        </div>
      )}
      <div className="files-toolbar">
        <select className="select" value={ioId ?? ""} onChange={(e) => setIoId(e.target.value)} disabled={ios.length === 0}>
          {ios.length === 0 && <option value="">No IO providers</option>}
          {ios.map((io) => (
            <option key={io.id} value={io.id}>
              {io.name} ({io.type})
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
        <div className="header-spacer" />
        {message && <span className="muted files-message">{message}</span>}
        {selected.size > 0 && (
          <button className="action-button" onClick={onDownloadSelectionZip}>
            <IconFileZip size={14} stroke={1.8} /> Download {selected.size} as zip
          </button>
        )}
        {selected.size > 0 && (
          <button className="action-button danger" onClick={deleteSelected}>
            <IconTrash size={14} stroke={1.8} /> Delete {selected.size} selected
          </button>
        )}
      </div>
      {error && <div className="login-error">{error}</div>}
      <div className="files-body">
        <section className="panel files-tree">
          <FolderNode
            path=""
            name="Storage root"
            hasSubFolders
            isPrimaryData={false}
            depth={0}
            listings={listings}
            expanded={expanded}
            currentPath={path}
            sizes={treeSizes}
            onComputeSize={computeTreeSize}
            onOpen={openFolder}
            onToggle={toggleExpand}
            onDragOut={onFolderDragStart}
          />
        </section>
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
          <div className="file-table">
            <div className="file-row file-head">
              <input type="checkbox" checked={allSelected} onChange={toggleAll} disabled={files.length === 0} />
              <span>Name</span>
              <span>Type</span>
              <span className="num">Size</span>
              <span>Modified</span>
              <span />
            </div>
            {files.map((f) => (
              <div
                key={f.key}
                className={"file-row" + (selected.has(f.key) ? " selected" : "")}
                draggable={!!ioId}
                onDragStart={(e) => onFileDragStart(e, f)}
              >
                <input type="checkbox" checked={selected.has(f.key)} onChange={() => toggleOne(f.key)} />
                <span className="file-name">
                  <IconFile size={14} stroke={1.6} />
                  {fileName(f.key)}
                  {(f.readers > 0 || f.writers > 0) && (
                    <span className="badge" title={`${f.readers} readers, ${f.writers} writers`}>
                      in use
                    </span>
                  )}
                </span>
                <span className="muted">{f.description ?? ""}</span>
                <span className="num">{formatBytes(f.size)}</span>
                <span className="muted">{formatTime(f.lastModifiedUtc)}</span>
                <a className="icon-button" href={ioId ? downloadUrl(db.id, ioId, f.key) : "#"} title="Download" download>
                  <IconDownload size={15} stroke={1.8} />
                </a>
              </div>
            ))}
            {listing && files.length === 0 && <div className="muted files-empty">No files in this folder.</div>}
          </div>
          <div className="files-footer muted">
            {files.length} files · {formatBytes(listedSize)}
            {selected.size > 0 ? ` · ${selected.size} selected` : ""}
          </div>
        </section>
      </div>
    </div>
  );
}

function fileName(key: string): string {
  const i = key.lastIndexOf("/");
  return i < 0 ? key : key.slice(i + 1);
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
  sizes: Record<string, FolderSize | "pending">;
  onComputeSize: (path: string) => void;
  onOpen: (path: string) => void;
  onToggle: (path: string) => void;
  onDragOut: (e: DragEvent, path: string) => void;
}

function FolderNode(p: FolderNodeProps) {
  const isExpanded = p.expanded.has(p.path);
  const listing = p.listings[p.path];
  const children = listing?.subFolders ?? [];
  const size = p.sizes[p.path];
  const note = folderNote(listing) ?? p.note;
  return (
    <>
      <div
        className={"tree-row" + (p.currentPath === p.path ? " active" : "") + (p.isPrimaryData ? " primary-data" : "")}
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
        <button
          className="tree-label"
          onClick={() => p.onOpen(p.path)}
          title={p.isPrimaryData ? `${p.name} — ${note ?? "the database's own data"}. ${primaryDataTip}` : note ? `${p.name} — ${note}` : p.name}
        >
          {p.currentPath === p.path ? <IconFolderOpen size={15} stroke={1.7} /> : <IconFolder size={15} stroke={1.7} />}
          <span>{p.name}</span>
          {p.isPrimaryData && note && <span className="badge data">actual data</span>}
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
            sizes={p.sizes}
            onComputeSize={p.onComputeSize}
            onOpen={p.onOpen}
            onToggle={p.onToggle}
            onDragOut={p.onDragOut}
          />
        ))}
    </>
  );
}

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
