import { useCallback, useEffect, useRef, useState } from "react";
import {
  IconDatabase,
  IconDatabaseExport,
  IconDatabaseImport,
  IconDatabasePlus,
  IconDeviceFloppy,
  IconFolder,
  IconDownload,
  IconFileSearch,
  IconFolderDown,
  IconPhotoCancel,
  IconListSearch,
  IconTextRecognition,
  IconRefresh,
  IconRestore,
  IconTrash,
} from "@tabler/icons-react";
import { runWithProgress, showChoice, showConfirm, showError, showInfo } from "../dialogs";
import { deleteFiles, downloadUrl, pickDirectory } from "../server/files";
import {
  addDemoContent,
  backupNow,
  databaseDownloadUrl,
  deleteConvertedFiles,
  deleteUnusedDbFiles,
  downloadFileStorage,
  fetchBackupList,
  fetchConvertedInfo,
  fetchDemoInfo,
  fetchDbFileInfo,
  fetchFileStorages,
  fetchMaintenanceInfo,
  rebuildTextIndex,
  revertToBackup,
  runFileScan,
  saveStateSnapshot,
  truncateDatabase,
  uploadDatabase,
  type BackupFile,
  type BackupList,
  type DbFileInfo,
  type DemoContentInfo,
  type FileStorageInfo,
  type MaintenanceInfo,
  type UnreferencedResult,
} from "../server/storage";
import type { DatabaseInfo } from "../server/serverInfo";
import { usePoll } from "../refresh";
import { formatBytes, formatCount, formatTime } from "../format";

export function StorageSection({ db }: { db: DatabaseInfo }) {
  const [backups, setBackups] = useState<BackupList | null>(null);
  const [dbFile, setDbFile] = useState<DbFileInfo | null>(null);
  const [maintenance, setMaintenance] = useState<MaintenanceInfo | null>(null);
  const [fileStorages, setFileStorages] = useState<FileStorageInfo[]>([]);
  const [demo, setDemo] = useState<DemoContentInfo | null>(null);
  const [demoCount, setDemoCount] = useState("1000");
  const [demoWikipedia, setDemoWikipedia] = useState(false);
  const [demoMessage, setDemoMessage] = useState<string | null>(null);
  const [truncate, setTruncate] = useState(false);
  const [keepForever, setKeepForever] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [maintenanceMessage, setMaintenanceMessage] = useState<string | null>(null);
  const [filesMessage, setFilesMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const uploadInput = useRef<HTMLInputElement>(null);

  const load = useCallback(() => {
    fetchBackupList(db.id)
      .then((list) => {
        setBackups(list);
        setError(null);
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
    fetchDbFileInfo(db.id)
      .then(setDbFile)
      .catch(() => {});
    fetchMaintenanceInfo(db.id)
      .then(setMaintenance)
      .catch(() => {});
    fetchFileStorages(db.id)
      .then(setFileStorages)
      .catch(() => {});
    fetchDemoInfo(db.id)
      .then(setDemo)
      .catch(() => {});
  }, [db.id]);
  useEffect(load, [load]);
  // a queued rebuild drains over minutes: while tasks are outstanding the panel follows them, and
  // stops asking the moment the queues are empty
  usePoll(() => fetchMaintenanceInfo(db.id).then(setMaintenance).catch(() => {}), { enabled: !!maintenance?.tasksQueued });

  async function onBackupNow() {
    const beforeKeys = new Set((backups?.files ?? []).map((f) => f.key));
    const done = await runWithProgress(`Backup ${db.name}`, async (ctl) => {
      ctl.set({ label: "Requesting backup…" });
      await backupNow(db.id, truncate, keepForever);
      // the backup runs as a background task on the server: wait for the new file to appear
      // (cancel stops the waiting, not the server task)
      for (;;) {
        if (ctl.signal.aborted) throw new DOMException("Aborted", "AbortError");
        ctl.set({ label: "Backup running on the server…" });
        await new Promise((r) => setTimeout(r, 1500));
        const list = await fetchBackupList(db.id);
        const fresh = list.files.find((f) => !beforeKeys.has(f.key));
        if (fresh) {
          ctl.set({ label: `${fresh.name} (${formatBytes(fresh.size)})` });
          return true;
        }
      }
    });
    if (done) setMessage("Backup created.");
    load();
  }

  async function onDeleteBackup(backup: BackupFile) {
    if (!backups) return;
    const { ok } = await showConfirm(
      "Delete backup",
      `Delete "${backup.name}" (${formatBytes(backup.size)})? The database itself is untouched, but this copy of it is gone for good.`,
      { confirmLabel: "Delete", danger: true },
    );
    if (!ok) return;
    const result = await deleteFiles(backups.ioId, [backup.key]);
    if (result.errors.length > 0) showError("Could not delete the backup", result.errors[0]);
    load();
  }

  async function onRevertBackup(backup: BackupFile) {
    const choice = await showConfirm(
      "Revert to backup",
      `Replace the current database with "${backup.name}"? The backup is copied into place as the new current database file; the old one is kept as an older version.`,
      { confirmLabel: "Revert", danger: true },
    );
    if (!choice.ok) return;
    const newKey = await runWithProgress(`Revert to ${backup.name}`, (ctl) => revertToBackup(ctl, db.id, backup.key));
    if (newKey) setMessage(`Reverted. New database file: ${newKey}.`);
    load();
  }

  async function onDeleteUnused() {
    if (!maintenance) return;
    const choice = await showConfirm(
      "Delete unused database files",
      `Delete ${maintenance.unusedFiles} old database log file${maintenance.unusedFiles === 1 ? "" : "s"} (${formatBytes(maintenance.unusedBytes)}) that are no longer in use?`,
      { confirmLabel: "Delete", danger: true },
    );
    if (!choice.ok) return;
    try {
      const result = await deleteUnusedDbFiles(db.id);
      if (result.errors.length > 0) {
        showError("Could not delete everything", `${result.deleted} deleted, ${result.errors.length} failed.`, result.errors);
      } else {
        setMaintenanceMessage(`Deleted ${result.deleted} file${result.deleted === 1 ? "" : "s"}, freed ${formatBytes(result.freed)}.`);
      }
    } catch (e) {
      showError("Delete failed", e instanceof Error ? e.message : String(e));
    }
    load();
  }

  /**
   * Re-extracts the search text of every text indexed node. The work itself is background tasks,
   * so what takes time here is only queueing them - on a large database that is still a wait worth
   * showing, and the count it comes back with is what says the queueing actually covered anything.
   */
  async function onRebuildTextIndex() {
    const choice = await showConfirm(
      "Rebuild the text index",
      "Every text indexed node has its search text extracted again and written back, one background task per node."
        + " Searching keeps working while it runs, on the index as it is now.",
      { confirmLabel: "Rebuild" },
    );
    if (!choice.ok) return;
    const result = await runWithProgress(`Rebuild the text index of ${db.name}`, async (ctl) => {
      ctl.set({ label: "Queueing every indexed node…" });
      return await rebuildTextIndex(db.id);
    });
    if (!result) return;
    setMaintenanceMessage(
      result.queued === 0
        ? "No node type has text indexing turned on, so there was nothing to queue."
        : `Queued ${formatCount(result.queued)} node${result.queued === 1 ? "" : "s"}. The tasks run in the background.`,
    );
    load();
  }

  async function onTruncate() {
    const choice = await showConfirm(
      "Truncate database",
      "Rewrites the database file so it only contains the current state, dropping history. This can take a while on a large database.",
      { confirmLabel: "Truncate", option: { label: "Keep the old database file", checked: true } },
    );
    if (!choice.ok) return;
    const done = await runWithProgress(`Truncate ${db.name}`, async (ctl) => {
      ctl.set({ label: "Truncating… (runs on the server, cancel only stops waiting)" });
      await truncateDatabase(db.id, choice.option);
      // the rewrite continues in the background: wait until it is no longer running
      for (;;) {
        if (ctl.signal.aborted) throw new DOMException("Aborted", "AbortError");
        await new Promise((r) => setTimeout(r, 1500));
        const info = await fetchMaintenanceInfo(db.id);
        if (!info.runningRewrite) return true;
        ctl.set({ label: `Rewriting ${info.runningRewrite}…` });
      }
    });
    if (done) setMaintenanceMessage(choice.option ? "Truncated. The old file is kept." : "Truncated.");
    load();
  }

  async function onSaveState() {
    const done = await runWithProgress(`Update state snapshot`, async (ctl) => {
      ctl.set({ label: "Writing the state snapshot…" });
      await saveStateSnapshot(db.id);
      return true;
    });
    if (done) setMaintenanceMessage("State snapshot updated.");
    load();
  }

  // Scans the file stores for files no node references anymore, and offers to delete what it found.
  // Counting first is the whole point: the deletion is then a confirmation of a known number rather
  // than a blind sweep, which is why there is no way to start straight at the deletion.
  async function onUnreferenced() {
    const progress = await runWithProgress(`Unreferenced files in ${db.name}`, (ctl) => runFileScan(ctl, db.id, "unreferenced", true));
    const found = progress?.unreferenced;
    if (!found) return;
    setFilesMessage(
      found.totalFilesDeleted === 0
        ? "No unreferenced files."
        : `${formatCount(found.totalFilesDeleted)} unreferenced file${found.totalFilesDeleted === 1 ? "" : "s"} · ${formatBytes(found.totalBytesDeleted)}.`,
    );
    if (found.totalFilesDeleted === 0) {
      showInfo("Unreferenced files", "Every file in the file stores is referenced by a node. Nothing to clean up.");
      return;
    }
    const choice = await showConfirm(
      "Delete unreferenced files",
      `${formatCount(found.totalFilesDeleted)} file${found.totalFilesDeleted === 1 ? "" : "s"} (${formatBytes(found.totalBytesDeleted)}) in the file stores are not referenced by any node. Delete them, and the folders left empty? Everything under a file store counts as this database's, so files put there by anything else go too. This cannot be undone.`,
      { confirmLabel: "Delete", danger: true },
    );
    if (choice.ok) await deleteUnreferenced();
  }

  async function deleteUnreferenced() {
    const progress = await runWithProgress(`Delete unreferenced files in ${db.name}`, (ctl) => runFileScan(ctl, db.id, "unreferenced", false));
    const result: UnreferencedResult | null | undefined = progress?.unreferenced;
    if (!result) return;
    const summary = `Deleted ${formatCount(result.totalFilesDeleted)} file${result.totalFilesDeleted === 1 ? "" : "s"} and ${formatCount(result.totalFoldersDeleted)} folder${result.totalFoldersDeleted === 1 ? "" : "s"}, freed ${formatBytes(result.totalBytesDeleted)}.`;
    setFilesMessage(summary);
    showInfo("Unreferenced files deleted", summary);
  }

  // The reverse audit: every file value in the database checked against the store it points at.
  async function onCheckMissing() {
    const progress = await runWithProgress(`Missing files in ${db.name}`, (ctl) => runFileScan(ctl, db.id, "missing", false));
    const result = progress?.missing;
    if (!result) return;
    const checked = `${formatCount(result.nodesScanned)} node${result.nodesScanned === 1 ? "" : "s"} scanned, ${formatCount(result.filesChecked)} file${result.filesChecked === 1 ? "" : "s"} checked`;
    if (result.missingCount === 0) {
      setFilesMessage(`${checked}, none missing.`);
      showInfo("No missing files", `${checked}. Every file value has its file in the file store.`);
      return;
    }
    setFilesMessage(`${checked}, ${formatCount(result.missingCount)} missing (${formatBytes(result.missingBytes)}).`);
    const shown = result.missing.slice(0, maxListedMissing);
    const details = shown.map(
      (m) => `${m.nodeType.split(".").pop()}.${m.property} — ${m.fileName} (${formatBytes(m.size)}) — ${m.reason}`,
    );
    if (result.missing.length > shown.length) details.push(`…and ${formatCount(result.missing.length - shown.length)} more`);
    else if (result.listTruncated) details.push("…the list was capped by the server; the counts above are complete");
    showError("Missing files", `${checked}. ${formatCount(result.missingCount)} file${result.missingCount === 1 ? " is" : "s are"} missing (${formatBytes(result.missingBytes)}).`, details);
  }

  // Empties the converted file cache. Measured first, so the confirmation names a number and an
  // empty cache costs nothing but the measurement.
  async function onDeleteConverted() {
    const info = await runWithProgress(`Converted files in ${db.name}`, async (ctl) => {
      ctl.set({ label: "Measuring the converted file cache…" });
      return await fetchConvertedInfo(db.id);
    });
    if (!info) return;
    if (info.files === 0) {
      setFilesMessage("No converted files.");
      showInfo("Converted files", "The converted file cache is empty. Nothing to delete.");
      return;
    }
    const choice = await showConfirm(
      "Delete converted files",
      `Delete ${formatCount(info.files)} converted file${info.files === 1 ? "" : "s"} (${formatBytes(info.bytes)})? These are the resized images and converted media derived from the stored files; each one is created again the next time it is requested.`,
      { confirmLabel: "Delete", danger: true },
    );
    if (!choice.ok) return;
    const result = await runWithProgress(`Delete converted files in ${db.name}`, async (ctl) => {
      ctl.set({ label: "Deleting…" });
      return await deleteConvertedFiles(db.id);
    });
    if (!result) return;
    const summary = `Deleted ${formatCount(result.deleted)} converted file${result.deleted === 1 ? "" : "s"}, freed ${formatBytes(result.freed)}.`;
    setFilesMessage(summary);
    if (result.remaining > 0) {
      showError("Converted files left behind", `${summary} ${formatCount(result.remaining)} could not be deleted — they are most likely in use right now.`);
    }
  }

  // Downloads a whole file storage to disk, the same way the files section downloads a storage
  // folder. Which storage is only a question when the database has more than one.
  async function onDownloadFileStorage() {
    // freshly listed rather than taken from the panel: a single file store's file keys are a
    // snapshot, and the one the download reads must be the one that is there now
    const storages = await fetchFileStorages(db.id).catch(() => fileStorages);
    setFileStorages(storages);
    if (storages.length === 0) {
      showError("No file storage", "This database has no file storage to download.");
      return;
    }
    let storage = storages[0];
    if (storages.length > 1) {
      const picked = await showChoice(
        "Download file storage",
        "This database has more than one file storage. Pick the one to download.",
        storages.map((s) => ({ label: s.name, hint: storageHint(s) + (s.isDefault ? " · default" : "") })),
      );
      if (picked == null) return;
      storage = storages[picked];
    }
    const directory = await pickDirectory();
    if (directory === "unsupported") {
      setFilesMessage("Downloading a file storage requires a Chromium based browser (File System Access API).");
      return;
    }
    if (!directory) return;
    const failed = await runWithProgress(`Download file storage ${storage.name}`, (ctl) => downloadFileStorage(ctl, db.id, storage, directory));
    if (failed) {
      if (failed.length > 0) {
        showError("Download incomplete", `${failed.length} file${failed.length === 1 ? "" : "s"} could not be downloaded.`, failed);
      } else {
        setFilesMessage("File storage downloaded.");
      }
    }
  }

  /**
   * Fills the database with generated demo articles - what makes an empty installation searchable
   * enough to try the query page on. The count is how many to add on top of what is already there:
   * the generator continues where the last run stopped, so a second run does not repeat the first.
   */
  async function onAddDemoContent() {
    const count = Math.floor(Number(demoCount));
    if (!Number.isFinite(count) || count < 1) {
      showError("Demo content", "Enter how many articles to create.");
      return;
    }
    setDemoMessage(null); // a cancelled or failed run must not leave the last run's line standing
    const progress = await runWithProgress(`Add demo content to ${db.name}`, (ctl) =>
      addDemoContent(ctl, db.id, count, demoWikipedia && !!demo?.wikipedia),
    );
    const result = progress?.result;
    if (result) {
      const seconds = result.elapsedMs / 1000;
      const perSecond = seconds > 0 ? Math.round(result.created / seconds) : result.created;
      setDemoMessage(
        `Created ${formatCount(result.created)} article${result.created === 1 ? "" : "s"} in ${seconds.toFixed(1)} s`
          + ` (${formatCount(perSecond)}/s). Indexing them runs as background tasks.`,
      );
    }
    // a cancelled or failed run keeps what it managed to insert, so the stored count is read back either way
    load();
  }

  async function onDownloadDatabase() {
    const choice = await showConfirm(
      "Download database",
      "The complete copy is the current database file including its history. The truncated version contains only the current state, is smaller, and is prepared on the server first.",
      { confirmLabel: "Download", option: { label: "Truncated version (current state only)", checked: false } },
    );
    if (!choice.ok) return;
    const link = document.createElement("a");
    link.href = databaseDownloadUrl(db.id, choice.option);
    link.download = "";
    document.body.appendChild(link);
    link.click();
    link.remove();
    if (choice.option) setMessage("Preparing the truncated copy — the download starts when the rewrite is done.");
  }

  async function onUploadDatabase(list: FileList | null) {
    if (!list || list.length === 0 || !dbFile) return;
    const file = list[0];
    const info = dbFile;
    const uploadedKey = await runWithProgress(`Upload database to ${db.name}`, (ctl) => uploadDatabase(ctl, db.id, info, file));
    if (uploadedKey) setMessage(`Uploaded as ${uploadedKey}. The database was reopened.`);
    load();
  }

  return (
    <div className="storage">
      {error && <div className="login-error">{error}</div>}
      {/* Four things live on this page and they are not alike: copies of the database, the file it
          is, the files it points to, and content to test with. Each is a group with a name and a
          line saying what it is about, and the panels of one group share a row. */}
      <div className="storage-group">
        <div className="storage-group-head">
          <IconDeviceFloppy size={16} stroke={1.8} />
          <h2>Backups</h2>
          <span className="muted">copies of the database file, kept beside it - the way back when something has gone wrong</span>
        </div>
      <div className="overview-columns">
        <section className="panel">
          <h3>
            Backups
            {backups && <span className="panel-sub"> {backups.files.length}</span>}
            <button className="icon-button storage-refresh" title="Refresh" onClick={load}>
              <IconRefresh size={14} stroke={1.8} />
            </button>
          </h3>
          <div className="db-table">
            <div className="backup-row db-table-head">
              <span>Name</span>
              <span className="num">Size</span>
              <span>Created</span>
              <span />
              <span />
              <span />
            </div>
            {(backups?.files ?? []).map((b) => (
              <div key={b.key} className="backup-row">
                <span className="file-name" title={b.key}>
                  {b.name}
                  {b.keepForever && <span className="badge">keep forever</span>}
                </span>
                <span className="num">{formatBytes(b.size)}</span>
                <span className="muted">{formatTime(b.timeUtc)}</span>
                <button className="icon-button" title="Revert the database to this backup" onClick={() => onRevertBackup(b)}>
                  <IconRestore size={15} stroke={1.8} />
                </button>
                <a className="icon-button" href={backups ? downloadUrl(db.id, backups.ioId, b.key) : "#"} title="Download" download>
                  <IconDownload size={15} stroke={1.8} />
                </a>
                <button className="icon-button danger" title="Delete this backup" onClick={() => onDeleteBackup(b)}>
                  <IconTrash size={15} stroke={1.8} />
                </button>
              </div>
            ))}
            {backups && backups.files.length === 0 && <div className="muted files-empty">No backups yet.</div>}
          </div>
        </section>
        <section className="panel">
          <h3>Create backup</h3>
          <label className="login-remember">
            <input type="checkbox" checked={truncate} onChange={(e) => setTruncate(e.target.checked)} />
            Truncate the log first (smaller backup)
          </label>
          <label className="login-remember">
            <input type="checkbox" checked={keepForever} onChange={(e) => setKeepForever(e.target.checked)} />
            Keep forever (never expires)
          </label>
          <div className="process-action">
            <button className="action-button" onClick={onBackupNow} disabled={db.state !== "Open"}>
              <IconDeviceFloppy size={14} stroke={1.8} /> Backup now
            </button>
            <span className="muted">{db.state !== "Open" ? "the database must be open" : (message ?? "")}</span>
          </div>
        </section>
      </div>
      </div>

      <div className="storage-group">
        <div className="storage-group-head">
          <IconDatabase size={16} stroke={1.8} />
          <h2>Database file</h2>
          <span className="muted">the transaction log the database lives in, and the maintenance that keeps it small and quick to open</span>
        </div>
      <div className="overview-columns even">
      <section className="panel">
        <h3>Database file</h3>
        {dbFile && (
          <>
            <div className="facts-grid storage-facts">
              <div className="fact">
                <div className="fact-k">Current file</div>
                <div className="fact-v" title={dbFile.currentKey}>
                  {dbFile.currentKey}
                </div>
              </div>
              <div className="fact">
                <div className="fact-k">Size</div>
                <div className="fact-v">{formatBytes(dbFile.size)}</div>
              </div>
              <div className="fact">
                <div className="fact-k">State</div>
                <div className="fact-v">{dbFile.state}</div>
              </div>
            </div>
            <div className="process-action">
              <button className="action-button" onClick={onDownloadDatabase}>
                <IconDatabaseExport size={14} stroke={1.8} /> Download database
              </button>
              <span className="muted">a complete copy with history, or a truncated version</span>
            </div>
            <div className="process-action">
              <button className="action-button" onClick={() => uploadInput.current?.click()}>
                <IconDatabaseImport size={14} stroke={1.8} /> Upload database
              </button>
              <span className="muted">{message ?? `closes the database, uploads as ${dbFile.nextKey}, clears the state file and reopens`}</span>
            </div>
            <input
              ref={uploadInput}
              type="file"
              hidden
              onChange={(e) => {
                onUploadDatabase(e.target.files);
                e.target.value = "";
              }}
            />
          </>
        )}
      </section>
      <section className="panel">
        <h3>Maintenance</h3>
        {maintenance && (
          <>
            <div className="facts-grid storage-facts">
              <div className="fact">
                <div className="fact-k">Actions not in state snapshot</div>
                <div className="fact-v">{maintenance.open ? formatCount(maintenance.actionsNotInState ?? 0) : "—"}</div>
              </div>
              <div className="fact">
                <div className="fact-k">Truncatable actions</div>
                <div className="fact-v">{maintenance.open ? formatCount(maintenance.truncatableActions ?? 0) : "—"}</div>
              </div>
              <div className="fact">
                <div className="fact-k">State snapshot size</div>
                <div className="fact-v">{maintenance.open ? formatBytes(maintenance.stateFileSize ?? 0) : "—"}</div>
              </div>
              <div className="fact">
                <div className="fact-k">Unused database files</div>
                <div className="fact-v">
                  {maintenance.unusedFiles === 0 ? "none" : `${maintenance.unusedFiles} · ${formatBytes(maintenance.unusedBytes)}`}
                </div>
              </div>
            </div>
            <div className="process-action">
              <button className="action-button" onClick={onSaveState} disabled={!maintenance.open}>
                Update state snapshot
              </button>
              <span className="muted">writes the current state so the next open replays fewer actions</span>
            </div>
            <div className="process-action">
              <button className="action-button" onClick={onTruncate} disabled={!maintenance.open}>
                Truncate database
              </button>
              <span className="muted">rewrites the database file to only the current state</span>
            </div>
            <div className="process-action">
              <button className="action-button" onClick={onDeleteUnused} disabled={maintenance.unusedFiles === 0}>
                Delete unused database files
              </button>
              <span className="muted">removes old database log files that are no longer in use</span>
            </div>
            <div className="process-action">
              <button className="action-button" onClick={onRebuildTextIndex} disabled={!maintenance.open}>
                <IconTextRecognition size={14} stroke={1.8} /> Rebuild text index
              </button>
              <span className="muted">
                {maintenanceMessage ??
                  (maintenance.tasksQueued
                    ? `${formatCount(maintenance.tasksQueued)} background task${maintenance.tasksQueued === 1 ? "" : "s"} still to run`
                    : "extracts the searchable text of every indexed node again and rewrites the search index")}
              </span>
            </div>
          </>
        )}
      </section>
      </div>
      </div>

      <div className="storage-group">
        <div className="storage-group-head">
          <IconFolder size={16} stroke={1.8} />
          <h2>File storage</h2>
          <span className="muted">the files behind the file properties, and generated content to try things out with</span>
        </div>
      <div className="overview-columns even">
      <section className="panel">
        <h3>File storage</h3>
        <div className="process-action">
          <button className="action-button" onClick={onUnreferenced} disabled={db.state !== "Open"}>
            <IconListSearch size={14} stroke={1.8} /> Unreferenced files
          </button>
          <span className="muted">
            {db.state !== "Open" ? "the database must be open" : "counts the files no node references anymore, and offers to delete them"}
          </span>
        </div>
        <div className="process-action">
          <button className="action-button" onClick={onCheckMissing} disabled={db.state !== "Open"}>
            <IconFileSearch size={14} stroke={1.8} /> Check for missing files
          </button>
          <span className="muted">checks every file value in the database against its file store</span>
        </div>
        <div className="process-action">
          <button className="action-button" onClick={onDeleteConverted} disabled={db.state !== "Open"}>
            <IconPhotoCancel size={14} stroke={1.8} /> Reset converted file cache
          </button>
          <span className="muted">empties the cache of resized images and converted media; they are recreated on demand</span>
        </div>
        <div className="process-action">
          <button className="action-button" onClick={onDownloadFileStorage} disabled={fileStorages.length === 0}>
            <IconFolderDown size={14} stroke={1.8} /> Download file storage
          </button>
          <span className="muted">
            {fileStorages.length === 0
              ? "no file storage configured"
              : fileStorages.length === 1
                ? `copies ${fileStorages[0].name} (${storageHint(fileStorages[0])}) to a folder on disk`
                : `copies one of the ${fileStorages.length} file storages to a folder on disk`}
          </span>
        </div>
        {filesMessage && (
          <div className="process-action">
            <span className="muted">{filesMessage}</span>
          </div>
        )}
      </section>
      <section className="panel">
        <h3>Demo content</h3>
        {demo && (
          <>
            <div className="facts-grid storage-facts">
              <div className="fact">
                <div className="fact-k">Node type</div>
                <div className="fact-v" title={demo.nodeType}>
                  {demo.available ? demo.nodeType : "not in this datamodel"}
                </div>
              </div>
              <div className="fact">
                <div className="fact-k">Demo articles stored</div>
                <div className="fact-v">{demo.available ? formatCount(demo.existing) : "—"}</div>
              </div>
            </div>
            <label className="login-remember">
              Articles to add
              <input
                className="text-input demo-count"
                type="number"
                min={1}
                step={1000}
                value={demoCount}
                disabled={!demo.available}
                onChange={(e) => setDemoCount(e.target.value)}
              />
            </label>
            {demo.wikipedia && (
              <label className="login-remember">
                <input type="checkbox" checked={demoWikipedia} onChange={(e) => setDemoWikipedia(e.target.checked)} disabled={!demo.available} />
                Use real articles from {demo.wikipediaPath}
              </label>
            )}
            <div className="process-action">
              <button className="action-button" onClick={onAddDemoContent} disabled={!demo.available}>
                <IconDatabasePlus size={14} stroke={1.8} /> Add demo content
              </button>
              <span className="muted">
                {demoMessage ??
                  (!demo.open
                    ? "the database must be open"
                    : !demo.available
                      ? `this datamodel has no ${demo.nodeType} node type to fill in`
                      : "inserts generated articles, continuing from the ones already stored")}
              </span>
            </div>
          </>
        )}
      </section>
      </div>
      </div>
    </div>
  );
}

// the server caps its own list at 1000; the dialog shows the first of those and counts the rest
const maxListedMissing = 200;

// what a file storage holds, in one line: the folder it is, or the file it appends to
function storageHint(storage: FileStorageInfo): string {
  const where = storage.folder ?? storage.files.map((f) => f.key).join(", ");
  return storage.type + (where ? " · " + where : "");
}

