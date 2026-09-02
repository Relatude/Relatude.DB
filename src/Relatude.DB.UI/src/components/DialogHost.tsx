import { useSyncExternalStore } from "react";
import { IconAlertTriangle } from "@tabler/icons-react";
import { acceptChoice, acceptConfirm, cancelProgress, closeDialog, getDialogState, subscribeDialogs, toggleConfirmOption } from "../dialogs";

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
  if (dialog.kind === "choice") {
    return (
      <div className="dialog-backdrop">
        <div className="dialog">
          <h3>{dialog.title}</h3>
          <div className="dialog-body">{dialog.body}</div>
          <div className="dialog-choices">
            {dialog.options.map((option, i) => (
              <button key={i} className="dialog-choice" onClick={() => acceptChoice(i)}>
                <span>{option.label}</span>
                {option.hint && <span className="muted">{option.hint}</span>}
              </button>
            ))}
          </div>
          <div className="dialog-row">
            <div className="header-spacer" />
            <button className="action-button" onClick={closeDialog}>
              Cancel
            </button>
          </div>
        </div>
      </div>
    );
  }
  if (dialog.kind === "confirm") {
    const blocked = dialog.option?.required === true && !dialog.option.checked;
    return (
      <div className="dialog-backdrop">
        <div className="dialog">
          <h3 className={dialog.danger ? "dialog-title-error" : ""}>
            {dialog.danger && <IconAlertTriangle size={16} stroke={2} />}
            {dialog.title}
          </h3>
          <div className="dialog-body">{dialog.body}</div>
          {dialog.option && (
            <label className="login-remember dialog-option">
              <input type="checkbox" checked={dialog.option.checked} onChange={toggleConfirmOption} />
              {dialog.option.label}
            </label>
          )}
          <div className="dialog-row">
            <div className="header-spacer" />
            <button className="action-button" onClick={closeDialog}>
              Cancel
            </button>
            <button
              className={"action-button dialog-confirm" + (dialog.danger ? " danger" : "")}
              disabled={blocked}
              onClick={acceptConfirm}
            >
              {dialog.confirmLabel}
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
            {dialog.meta ?? (
              <>
                {dialog.total != null ? `${dialog.done} / ${dialog.total}` : ""}
                {pct != null && running ? ` · ${pct}%` : ""}
              </>
            )}
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
