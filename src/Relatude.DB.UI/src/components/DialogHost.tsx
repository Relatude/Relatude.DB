import { useSyncExternalStore } from "react";
import { IconAlertTriangle } from "@tabler/icons-react";
import { cancelProgress, closeDialog, getDialogState, subscribeDialogs } from "../dialogs";

// Renders whatever dialog is active (a running task's progress, or a message). Mounted once in App.
export function DialogHost() {
  const dialog = useSyncExternalStore(subscribeDialogs, getDialogState);
  if (!dialog) return null;
  if (dialog.kind === "message") {
    return (
      <div className="dialog-backdrop">
        <div className="dialog">
          <h3 className={dialog.tone === "error" ? "dialog-title-error" : ""}>
            {dialog.tone === "error" && <IconAlertTriangle size={16} stroke={2} />}
            {dialog.title}
          </h3>
          <div className="dialog-body">{dialog.body}</div>
          {dialog.details.length > 0 && (
            <div className="dialog-details">
              {dialog.details.map((detail, i) => (
                <div key={i}>{detail}</div>
              ))}
            </div>
          )}
          <div className="dialog-row">
            <div className="header-spacer" />
            <button className="action-button" onClick={closeDialog}>
              Close
            </button>
          </div>
        </div>
      </div>
    );
  }
  const running = dialog.status === "running";
  const pct = dialog.total != null && dialog.total > 0 ? Math.min(100, Math.round((dialog.done / dialog.total) * 100)) : null;
  return (
    <div className="dialog-backdrop">
      <div className="dialog">
        <h3>{dialog.title}</h3>
        <div className="dialog-label" title={dialog.message ?? dialog.label}>
          {dialog.message ?? dialog.label ?? ""}
        </div>
        <div className={"progress-bar" + (running && pct === null ? " indeterminate" : "") + (dialog.status === "error" ? " error" : "")}>
          <div className="progress-fill" style={{ width: (running ? (pct ?? 100) : 100) + "%" }} />
        </div>
        <div className="dialog-row">
          <span className="dialog-meta muted">
            {dialog.total != null ? `${dialog.done} / ${dialog.total}` : ""}
            {pct != null && running ? ` · ${pct}%` : ""}
          </span>
          <div className="header-spacer" />
          {running ? (
            <button className="action-button" onClick={cancelProgress}>
              Cancel
            </button>
          ) : (
            <button className="action-button" onClick={closeDialog}>
              Close
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
