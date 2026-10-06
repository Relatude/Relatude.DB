import { IconArrowRight, IconFileImport } from "@tabler/icons-react";
import type { ReactNode } from "react";
import { SourceTag } from "./SourceTag";

/**
 * The one way, on every page whose changes are saved on THIS SERVER, to see what is held there and move
 * it into SHARED: the settings pages, the databases list, what Activity records, the datamodel overrides,
 * log definitions and GraphQL endpoints. It looks and reads the same everywhere and sits in the same
 * place - the top right of the module it belongs to - and it always opens that module's dialog, where
 * entries are picked and moved or discarded.
 *
 * The status beside the label is the count of what THIS SERVER holds for this module and SHARED does
 * not: "all shared" when there is nothing to move.
 */
export function ShareButton({
  count,
  error,
  title,
  onClick,
  disabled,
  className,
}: {
  /** null while it is being read */
  count: number | null;
  error?: string | null;
  /** what is kept where, for this module; the status is added to it */
  title: string;
  onClick: () => void;
  disabled?: boolean;
  className?: string;
}) {
  const state = error ? "error" : count === null ? "loading" : count > 0 ? "pending" : "synced";
  const status = error ? "not read" : count === null ? "…" : count > 0 ? `${count} on this server` : "all shared";
  const said = error
    ? " It could not be read: " + error
    : count === null
      ? ""
      : count > 0
        ? ` ${count} ${count === 1 ? "entry is" : "entries are"} only on this server: open to move ${count === 1 ? "it" : "them"} into SHARED, or to discard.`
        : " Nothing is held only on this server.";
  return (
    <button className={"share-button " + state + (className ? " " + className : "")} title={title + said} onClick={onClick} disabled={disabled}>
      <IconFileImport size={15} stroke={1.8} className="share-button-icon" />
      <span className="share-button-label">Move to shared</span>
      <span className="share-button-status">{status}</span>
    </button>
  );
}

/** The title row of every move-to-shared dialog: the same icon and words as the button, what is moved, and which way. */
export function ShareDialogTitle({ what, children }: { what: string; children?: ReactNode }) {
  return (
    <>
      <IconFileImport size={17} stroke={1.8} className="share-button-icon" />
      <span className="share-dialog-title">
        Move to shared <span className="muted">· {what}</span>
      </span>
      <span className="share-dialog-way">
        <SourceTag kind="data" small title="From THIS SERVER: this installation's data folder, where the admin UI saves" />
        <IconArrowRight size={12} stroke={2} />
        <SourceTag kind="settings" small title="Into SHARED: relatude.settings, part of the application, in source control and deployed to every installation" />
      </span>
      {children}
    </>
  );
}
