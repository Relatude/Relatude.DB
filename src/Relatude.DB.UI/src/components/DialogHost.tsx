import { useEffect, useRef, useSyncExternalStore } from "react";
import { IconAlertTriangle } from "@tabler/icons-react";
import {
  acceptChoice,
  acceptConfirm,
  acceptPrompt,
  cancelProgress,
  closeDialog,
  getDialogState,
  setPromptValue,
  subscribeDialogs,
  toggleConfirmOption,
  type PromptState,
} from "../dialogs";

// Renders whatever dialog is active (a running task's progress, or a message). Mounted once in App.
export function DialogHost() {
  const dialog = useSyncExternalStore(subscribeDialogs, getDialogState);
  if (!dialog) return null;
  if (dialog.kind === "prompt") return <PromptDialog dialog={dialog} />;
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
  return <ProgressDialog dialog={dialog} />;
}

// a text question; Enter confirms, Escape cancels, and the initial value's stem starts selected
// so typing replaces a file's name but keeps its extension
function PromptDialog({ dialog }: { dialog: PromptState }) {
  const input = useRef<HTMLInputElement>(null);
  useEffect(() => {
    const el = input.current;
    if (!el) return;
    el.focus();
    if (dialog.selectEnd !== null) el.setSelectionRange(0, Math.min(dialog.selectEnd, el.value.length));
    else el.select();
    // only on mount: later renders (typing) must leave the selection alone
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  const blocked = dialog.error !== null || dialog.value.trim().length === 0;
  return (
    <div className="dialog-backdrop">
      <div className="dialog">
        <h3>{dialog.title}</h3>
        {dialog.body && <div className="dialog-body">{dialog.body}</div>}
        <label className="dialog-field">
          <span className="muted">{dialog.label}</span>
          <input
            ref={input}
            className="text-input"
            value={dialog.value}
            onChange={(e) => setPromptValue(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") {
                e.preventDefault();
                acceptPrompt();
              } else if (e.key === "Escape") {
                e.preventDefault();
                closeDialog();
              }
            }}
            spellCheck={false}
            autoComplete="off"
          />
          {dialog.error && <span className="dialog-error">{dialog.error}</span>}
        </label>
        <div className="dialog-row">
          <div className="header-spacer" />
          <button className="action-button" onClick={closeDialog}>
            Cancel
          </button>
          <button className="action-button dialog-confirm" disabled={blocked} onClick={acceptPrompt}>
            {dialog.confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}

function ProgressDialog({ dialog }: { dialog: Extract<ReturnType<typeof getDialogState>, { kind: "progress" }> }) {
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
