import { useCallback, useEffect, useRef, useState } from "react";
import {
  IconChevronDown,
  IconChevronRight,
  IconDownload,
  IconFile,
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
  fetchFolder,
  fetchFolderSize,
  fetchIoList,
  uploadEntries,
  type FolderListing,
  type FolderSize,
  type IoInfo,
  type UploadEntry,
} from "../server/files";
import type { DatabaseInfo } from "../server/serverInfo";
import { formatBytes, formatTime } from "../format";
import { runWithProgress, showError } from "../dialogs";

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

  function openFolder(folderPath: string) {
    setPath(folderPath);
    setSelected(new Set());
    setExpanded((prev) => new Set(prev).add(folderPath));
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
    const io = ioId;
    const failed = await runWithProgress(`Upload to ${path || "storage root"}`, (ctl) => uploadEntries(ctl, io, path, entries));
    if (failed) {
      if (failed.length > 0) {
        showError("Upload incomplete", `${entries.length - failed.length} of ${entries.length} files were uploaded.`, failed);
      } else {
        setMessage(`Uploaded ${entries.length} file${entries.length === 1 ? "" : "s"}.`);
      }
    }
    loadFolder(io, path); // also after a cancel: some files may have landed
  }

  const [deleteFolderArmed, setDeleteFolderArmed] = useState(false);
  useEffect(() => {
    if (!deleteFolderArmed) return;
    const t = setTimeout(() => setDeleteFolderArmed(false), 4000);
    return () => clearTimeout(t);
  }, [deleteFolderArmed]);

  async function onDeleteFolder() {
    if (!ioId || path === "") return;
    if (!deleteFolderArmed) {
      setDeleteFolderArmed(true);
      return;
    }
    setDeleteFolderArmed(false);
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
    if (!window.showDirectoryPicker) {
      setMessage("Folder download requires a Chromium based browser (File System Access API).");
      return;
    }
    let directory: FileSystemDirectoryHandle;
    try {
      directory = await window.showDirectoryPicker({ mode: "readwrite" });
    } catch {
      return; // picker dismissed
    }
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
    <div className="files">
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
          className={"icon-button danger" + (deleteFolderArmed ? " armed" : "")}
          title={path === "" ? "The storage root cannot be deleted" : deleteFolderArmed ? "Click again to delete the folder" : "Delete this folder and everything in it"}
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
        {selected.size > 0 && <DeleteSelectedButton count={selected.size} onDelete={deleteSelected} />}
      </div>
      {error && <div className="login-error">{error}</div>}
      <div className="files-body">
        <section className="panel files-tree">
          <FolderNode
            path=""
            name="Storage root"
            hasSubFolders
            depth={0}
            listings={listings}
            expanded={expanded}
            currentPath={path}
            sizes={treeSizes}
            onComputeSize={computeTreeSize}
            onOpen={openFolder}
            onToggle={toggleExpand}
          />
        </section>
        <section className="panel files-list">
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
              <div key={f.key} className={"file-row" + (selected.has(f.key) ? " selected" : "")}>
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
  depth: number;
  listings: Record<string, FolderListing>;
  expanded: Set<string>;
  currentPath: string;
  sizes: Record<string, FolderSize | "pending">;
  onComputeSize: (path: string) => void;
  onOpen: (path: string) => void;
  onToggle: (path: string) => void;
}

function FolderNode(p: FolderNodeProps) {
  const isExpanded = p.expanded.has(p.path);
  const children = p.listings[p.path]?.subFolders ?? [];
  const size = p.sizes[p.path];
  return (
    <>
      <div className={"tree-row" + (p.currentPath === p.path ? " active" : "")} style={{ paddingLeft: 4 + p.depth * 14 }}>
        <button
          className="tree-chevron"
          onClick={() => p.onToggle(p.path)}
          style={{ visibility: p.hasSubFolders ? "visible" : "hidden" }}
        >
          {isExpanded ? <IconChevronDown size={13} stroke={2} /> : <IconChevronRight size={13} stroke={2} />}
        </button>
        <button className="tree-label" onClick={() => p.onOpen(p.path)} title={p.name}>
          {p.currentPath === p.path ? <IconFolderOpen size={15} stroke={1.7} /> : <IconFolder size={15} stroke={1.7} />}
          <span>{p.name}</span>
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
            depth={p.depth + 1}
            listings={p.listings}
            expanded={p.expanded}
            currentPath={p.currentPath}
            sizes={p.sizes}
            onComputeSize={p.onComputeSize}
            onOpen={p.onOpen}
            onToggle={p.onToggle}
          />
        ))}
    </>
  );
}

// same two-step confirmation pattern as the overview's process actions
function DeleteSelectedButton({ count, onDelete }: { count: number; onDelete: () => void }) {
  const [armed, setArmed] = useState(false);
  useEffect(() => {
    if (!armed) return;
    const t = setTimeout(() => setArmed(false), 4000);
    return () => clearTimeout(t);
  }, [armed]);
  return (
    <button
      className={"action-button danger" + (armed ? " armed" : "")}
      onClick={() => {
        if (!armed) {
          setArmed(true);
          return;
        }
        setArmed(false);
        onDelete();
      }}
    >
      <IconTrash size={14} stroke={1.8} /> {armed ? "Click again to confirm" : `Delete ${count} selected`}
    </button>
  );
}
