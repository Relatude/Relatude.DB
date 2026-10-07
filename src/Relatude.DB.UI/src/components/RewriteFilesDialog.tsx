import { useState } from "react";
import { IconAlertTriangle } from "@tabler/icons-react";
import { DialogTools } from "./DialogTools";
import type { FileStoreChoice } from "../server/storage";

/**
 * What the dialog was answered with: the store the files are written into (all zeros for the implicit
 * one), and - only where the store lets them be chosen - the hash it writes with and whether it keeps
 * one copy per content. Those two become the store's settings.
 */
export interface RewriteChoice {
  toStore: string;
  hashAlgorithm?: string; // MD5 | SHA256
  sameHashSameFile?: boolean;
}

interface Props {
  stores: FileStoreChoice[];
  onCancel: () => void;
  onRewrite: (choice: RewriteChoice) => void;
}

/** How a store writes a file, in a few words: what a rewrite into it turns on. */
export function describeFileStore(s: FileStoreChoice): string {
  const parts = [storeName(s), s.type, s.hashAlgorithm];
  if (s.sameHashSameFile) parts.push("one copy per content");
  if (s.isDefault) parts.push("default");
  return parts.join(" · ");
}

function storeName(s: FileStoreChoice): string {
  return s.implicit ? `Implicit store on ${s.name}` : s.name;
}

/** Why a store's hash and one copy per content cannot be chosen here, or null when they can. */
function writeOptionsFixed(s: FileStoreChoice): string | null {
  if (s.implicit) return "The implicit store has no settings of its own: it hashes with MD5 and stores every upload on its own. Add a file store under Settings to choose.";
  if (s.type !== "MultiFile") return "A SingleFile store hashes with MD5 and stores every upload on its own.";
  if (s.writeOptionsLockedBy) return `Set by ${s.writeOptionsLockedBy}, so it cannot be changed here.`;
  return null;
}

/**
 * Rewrite files: one store to write into, and how it writes. Every file that belongs in the store - the
 * files of the properties whose uploads go there - is written into it again, wherever it is stored now.
 * That one choice covers both reasons to rewrite: files stored elsewhere are moved in (off the implicit
 * store, onto blob storage), and files already there but written another way - before a new hash or
 * "Same hash, same file" was turned on - are caught up.
 *
 * The hash and one copy per content are the store's own settings, shown here because they are what the
 * rewrite does; changing them changes the settings, so uploads from then on are written the same way.
 * The dialog is its own confirmation: it says what will happen, and the button does it.
 */
export function RewriteFilesDialog(p: Props) {
  const defaultStore = p.stores.find((s) => s.isDefault) ?? p.stores[0];
  const [toId, setToId] = useState(defaultStore.id);
  const to = p.stores.find((s) => s.id === toId) ?? defaultStore;
  const [hash, setHash] = useState(to.hashAlgorithm);
  const [oneCopy, setOneCopy] = useState(to.sameHashSameFile);
  const fixed = writeOptionsFixed(to);
  const changed = !fixed && (hash !== to.hashAlgorithm || oneCopy !== to.sameHashSameFile);
  const problem = !to.receivesUploads
    ? "Nothing uploads into this store: it is not the default store, and no file property names it, so no file belongs in it. Make it the default store first."
    : null;
  const othersReceive = p.stores.some((s) => s.id !== to.id && s.receivesUploads);

  // a store picked brings its own settings with it
  function pick(id: string) {
    const store = p.stores.find((s) => s.id === id) ?? defaultStore;
    setToId(store.id);
    setHash(store.hashAlgorithm);
    setOneCopy(store.sameHashSameFile);
  }

  function submit() {
    if (problem) return;
    p.onRewrite(fixed ? { toStore: to.id } : { toStore: to.id, hashAlgorithm: hash, sameHashSameFile: oneCopy });
  }

  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && p.onCancel()}>
      <div
        className="dialog rewrite-dialog"
        role="dialog"
        aria-label="Rewrite files"
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            e.preventDefault();
            p.onCancel();
          }
        }}
      >
        <h3>
          Rewrite files
          <DialogTools onClose={p.onCancel} closeTitle="Cancel" />
        </h3>
        <div className="dialog-body">
          The files are written again into one store, and the file values point at the new copies from then on - revisions and embedded objects
          included. Names, image sizes and extracted text stay, nothing is indexed again, and the database stays in use.
        </div>
        <label className="dialog-field">
          <span className="muted">Write the files into</span>
          <select className="select" value={to.id} onChange={(e) => pick(e.target.value)}>
            {p.stores.map((s) => (
              <option key={s.id} value={s.id}>
                {storeName(s) + " · " + s.type + (s.isDefault ? " · default" : "")}
              </option>
            ))}
          </select>
          {problem ? (
            <span className="dialog-error">{problem}</span>
          ) : (
            <span className="muted">
              Files stored elsewhere are moved into it, and files already in it that were written another way are written again.
              {othersReceive ? " Files of properties that upload into another store stay where they are." : ""}
            </span>
          )}
        </label>
        <div className="dialog-field rewrite-options">
          <span className="muted">Written with</span>
          <div className="rewrite-options-row">
            <select className="select" value={hash} disabled={fixed !== null} onChange={(e) => setHash(e.target.value)} aria-label="File hash">
              <option value="SHA256">SHA256 hash</option>
              <option value="MD5">MD5 hash</option>
            </select>
            <label className={"rewrite-one-copy" + (fixed ? " disabled" : "")}>
              <input type="checkbox" checked={oneCopy} disabled={fixed !== null} onChange={(e) => setOneCopy(e.target.checked)} />
              Same hash, same file
              <span className="muted">- identical files share one copy</span>
            </label>
          </div>
          <span className={fixed ? "muted" : changed ? "rewrite-options-note" : "muted"}>
            {fixed ??
              (changed
                ? "Saved as this store's settings when the rewrite starts: new uploads are written the same way from then on."
                : "This store's own settings: what is chosen here is how new uploads are written too.")}
          </span>
        </div>
        <div className="files-notice">
          <IconAlertTriangle size={15} stroke={1.8} />
          <span>
            <b>The old copies stay where they are.</b> Older versions in a node's history still point at them. "Missing and redundant files" removes
            them once you no longer need those versions' files. Where new uploads go is decided by the default file store, not by this.
          </span>
        </div>
        <div className="dialog-row">
          <div className="header-spacer" />
          <button className="action-button dialog-confirm" disabled={problem !== null} onClick={submit}>
            Rewrite
          </button>
          <button className="action-button" onClick={p.onCancel}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}
