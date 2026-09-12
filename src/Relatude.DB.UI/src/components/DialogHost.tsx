import { useEffect, useRef, useSyncExternalStore } from "react";
import { IconAlertTriangle, IconCheck, IconLoader2, IconMinus, IconX } from "@tabler/icons-react";
import {
  acceptChoice,
  acceptConfirm,
  acceptPrompt,
  cancelProgress,
  closeDialog,
  dismissProgress,
  getDialogState,
  getMinimizedProgress,
  getProgressByKey,
  minimizeProgress,
  restoreProgress,
  setPromptValue,
  subscribeDialogs,
  toggleConfirmOption,
  type ProgressState,
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

/**
 * The task running under this key, if there is one - for the button that started it. A minimizable
 * task outlives the page it was started from, so the button has to read the state from the store
 * rather than from a local flag: navigating away and back must not offer to start a second copy.
 */
export function useProgressTask(key: string | null): ProgressState | null {
  return useSyncExternalStore(subscribeDialogs, () => getProgressByKey(key));
}

function percentOf(task: ProgressState): number | null {
  return task.total != null && task.total > 0 ? Math.min(100, Math.round((task.done / task.total) * 100)) : null;
}

function ProgressDialog({ dialog }: { dialog: ProgressState }) {
  const running = dialog.status === "running";
  const pct = percentOf(dialog);
  const canMinimize = running && dialog.minimizable;
  return (
    // a task that may be put away is not truly modal: clicking beside it puts it in the bar rather
    // than doing nothing, the way the button does
    <div className="dialog-backdrop" onClick={canMinimize ? (e) => e.target === e.currentTarget && minimizeProgress(dialog.id) : undefined}>
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
          {canMinimize && (
            <button
              className="action-button"
              onClick={() => minimizeProgress(dialog.id)}
              title="Keep it running and carry on - it goes to the top bar"
            >
              <IconMinus size={14} stroke={1.8} /> Minimize
            </button>
          )}
          {running ? (
            <button className="action-button" onClick={() => cancelProgress(dialog.id)}>
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

/**
 * The tasks that were put away, in the top bar. A job that leaves the database usable has no business
 * holding the UI still, but it still has to be somewhere: one chip per task, counting where the
 * dialog was counting, clicked to bring the dialog back. A finished one says how it went and takes
 * itself away shortly after; a failed one stays until it has been read.
 */
export function MinimizedProgress() {
  const tasks = useSyncExternalStore(subscribeDialogs, getMinimizedProgress);
  if (tasks.length === 0) return null;
  return (
    <div className="task-chips">
      {tasks.map((task) => {
        const pct = percentOf(task);
        const running = task.status === "running";
        const line = task.message ?? task.label;
        return (
          <div key={task.id} className={"task-chip " + task.status}>
            <button className="task-chip-open" onClick={() => restoreProgress(task.id)} title={line ? `${task.title} - ${line}` : task.title}>
              <span className="task-chip-icon">
                {task.status === "error" ? (
                  <IconAlertTriangle size={13} stroke={2} />
                ) : task.status === "done" ? (
                  <IconCheck size={13} stroke={2} />
                ) : task.status === "cancelled" ? (
                  <IconMinus size={13} stroke={2} />
                ) : (
                  <IconLoader2 size={13} stroke={2.2} className="spinning" />
                )}
              </span>
              <span className="task-chip-text">
                <span className="task-chip-title">{task.title}</span>
                <span className="task-chip-line">{running ? (task.meta ?? (pct != null ? pct + "%" : line)) : (line ?? "Done")}</span>
              </span>
            </button>
            {running ? (
              <button className="icon-button task-chip-side" onClick={() => cancelProgress(task.id)} title="Cancel">
                <IconX size={13} stroke={2} />
              </button>
            ) : (
              <button className="icon-button task-chip-side" onClick={() => dismissProgress(task.id)} title="Dismiss">
                <IconX size={13} stroke={2} />
              </button>
            )}
            <span className="task-chip-bar">
              <span
                className={"task-chip-fill" + (running && pct === null ? " indeterminate" : "")}
                style={{ width: (running ? (pct ?? 40) : 100) + "%" }}
              />
            </span>
          </div>
        );
      })}
    </div>
  );
}
