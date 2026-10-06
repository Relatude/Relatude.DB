import { useState } from "react";
import { IconAlertTriangle } from "@tabler/icons-react";
import { DialogTools } from "./DialogTools";
import type { FileStoreChoice } from "../server/storage";

/** What the dialog was answered with: the ids of the two file stores (all zeros for the implicit one). */
export interface RewriteChoice {
  fromStore: string;
  toStore: string;
}

interface Props {
  stores: FileStoreChoice[];
  onCancel: () => void;
  onRewrite: (choice: RewriteChoice) => void;
}

/** How a store writes a file, in a few words: what the choice between two stores turns on. */
export function describeFileStore(s: FileStoreChoice): string {
  const parts = [s.implicit ? `Implicit store on ${s.name}` : s.name, s.type, s.hashAlgorithm];
  if (s.sameHashSameFile) parts.push("one copy per content");
  if (s.isDefault) parts.push("default");
  return parts.join(" · ");
}

/**
 * Which file store the files are read from and which one writes them again. Two uses, one dialog: moving
 * the files of a database to another store - off the implicit store, or onto blob storage - and, with the
 * same store on both sides, catching the files stored before "Same hash, same file" or a new hash was
 * turned on up with how the store writes files now.
 *
 * It starts out reading from a store that is not the default and writing to the default one, which is the
 * move people make after adding a store and making it the default. The dialog is its own confirmation:
 * it says what will happen, and the button does it.
 */
export function RewriteFilesDialog(p: Props) {
  const defaultStore = p.stores.find((s) => s.isDefault) ?? p.stores[0];
  const [fromId, setFromId] = useState(() => (p.stores.find((s) => !s.isDefault) ?? defaultStore).id);
  const [toId, setToId] = useState(defaultStore.id);
  const from = p.stores.find((s) => s.id === fromId) ?? defaultStore;
  const to = p.stores.find((s) => s.id === toId) ?? defaultStore;
  const same = from.id === to.id;
  // a SingleFile store hashes with MD5 and shares nothing, so rewriting one into itself changes nothing
  const problem = same && to.type !== "MultiFile" ? "A SingleFile store writes files the way they already are. Pick another store to write to." : null;

  function submit() {
    if (problem) return;
    p.onRewrite({ fromStore: from.id, toStore: to.id });
  }

  const select = (value: string, onChange: (id: string) => void) => (
    <select className="select" value={value} onChange={(e) => onChange(e.target.value)}>
      {p.stores.map((s) => (
        <option key={s.id} value={s.id}>
          {describeFileStore(s)}
        </option>
      ))}
    </select>
  );

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
          Every file of one file store is written again by another, and the file values point at the new copies from then on - revisions and
          embedded objects included. Names, image sizes and extracted text stay, nothing is indexed again, and the database stays in use.
        </div>
        <label className="dialog-field">
          <span className="muted">Read the files of</span>
          {select(from.id, setFromId)}
        </label>
        <label className="dialog-field">
          <span className="muted">Write them with</span>
          {select(to.id, setToId)}
          {problem ? (
            <span className="dialog-error">{problem}</span>
          ) : same ? (
            <span className="muted">
              The same store: only files not yet written the way it writes them now are rewritten
              {to.sameHashSameFile ? ` - with a ${to.hashAlgorithm} hash, one copy per content.` : ` - with a ${to.hashAlgorithm} hash.`}
            </span>
          ) : (
            <span className="muted">
              Each file gets a {to.hashAlgorithm} hash{to.sameHashSameFile ? ", and identical files share one copy" : ""}.
            </span>
          )}
        </label>
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
