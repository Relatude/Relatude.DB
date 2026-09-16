import { useState } from "react";
import { IconAlertTriangle, IconHistory } from "@tabler/icons-react";
import { DialogTools } from "./DialogTools";
import type { TimeTravelInfo } from "../server/storage";
import { formatBytes, formatDateTime } from "../format";

/**
 * Picking the moment to copy the database up to.
 *
 * It is a dialog rather than a field on the page because the moment is the whole decision, and a
 * bare datetime box is a poor place to make it: what you want to type is almost always a moment the
 * database itself knows about. So the three it knows are listed underneath, and clicking one fills
 * the box - the ends of the log, and the last revert window somebody began, which is the only one
 * of the three that somebody chose deliberately.
 *
 * The dialog is its own confirmation: its body says what happens and the button is the one that
 * does it. A second "are you sure" on top of a dialog the user opened and filled in reads as a
 * stutter, and there is nothing it could add - nothing here deletes anything.
 */
export function TimeTravelDialog({
  dbName,
  info,
  onCancel,
  onConfirm,
}: {
  dbName: string;
  info: TimeTravelInfo;
  onCancel: () => void;
  onConfirm: (when: Date) => void;
}) {
  // the end of the log is where the database is now; going back starts from there
  const [value, setValue] = useState(() => toLocalInput(info.lastChangeUtc ?? new Date().toISOString()));
  const when = value ? new Date(value) : null;
  const valid = when !== null && !Number.isNaN(when.getTime());

  const moments: { label: string; utc: string | null; hint: string }[] = [
    { label: "First transaction", utc: info.firstChangeUtc, hint: "everything the database file holds" },
    { label: "Last transaction", utc: info.lastChangeUtc, hint: "where the database is now - nothing would be left out" },
    {
      label: "Last revert window",
      utc: info.revertWindowUtc,
      hint: info.revertWindowUtc
        ? (info.revertWindowActive ? "still open, begun " : "begun ") + formatDateTime(info.revertWindowBegunUtc ?? info.revertWindowUtc)
        : "no revert window has been begun on this database",
    },
  ];

  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onCancel()}>
      <div
        className="dialog time-travel-dialog"
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            e.preventDefault();
            onCancel();
          }
        }}
      >
        <h3 className="dialog-title-error">
          <IconAlertTriangle size={16} stroke={2} /> Go back in time
          <DialogTools onClose={onCancel} closeTitle="Cancel" />
        </h3>
        <div className="dialog-body">
          The database file is copied up to the moment below and {dbName} is reopened on the copy. Everything written after that moment is left
          out of the copy. Nothing is deleted: {info.currentKey} ({formatBytes(info.size)}) is kept beside the new file, and the Files page can
          make it the database again.
        </div>
        <label className="dialog-field">
          <span className="muted">Go back to</span>
          <input
            className="text-input"
            type="datetime-local"
            step={1}
            autoFocus
            value={value}
            onChange={(e) => setValue(e.target.value)}
          />
        </label>
        <div className="time-travel-moments">
          <span className="muted">Moments this database knows</span>
          {moments.map((moment) => (
            <button
              key={moment.label}
              className="dialog-choice time-travel-moment"
              disabled={!moment.utc}
              onClick={() => moment.utc && setValue(toLocalInput(moment.utc))}
              title={moment.utc ? "Use this moment" : moment.hint}
            >
              <span className="time-travel-moment-row">
                <span>{moment.label}</span>
                <span className="time-travel-moment-time">{moment.utc ? formatDateTime(moment.utc) : "—"}</span>
              </span>
              <span className="muted">{moment.hint}</span>
            </button>
          ))}
        </div>
        {!info.open && (
          <div className="muted time-travel-note">
            The database is closed, so these were read off the file itself.
          </div>
        )}
        <div className="dialog-row">
          <div className="header-spacer" />
          <button className="action-button dialog-confirm danger" disabled={!valid} onClick={() => valid && onConfirm(when)}>
            <IconHistory size={14} stroke={1.8} /> Go back
          </button>
          <button className="action-button" onClick={onCancel}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}

/** A UTC instant as a datetime-local value: local time, to the second, without a zone. */
export function toLocalInput(utc: string): string {
  const date = new Date(utc);
  if (Number.isNaN(date.getTime())) return "";
  const pad = (n: number) => n.toString().padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}
