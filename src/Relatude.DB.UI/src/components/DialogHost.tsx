import { useEffect, useRef, useSyncExternalStore } from "react";
import { createPortal } from "react-dom";
import { IconAlertTriangle, IconCheck, IconLoader2, IconMinus, IconX } from "@tabler/icons-react";
import { DialogTools } from "./DialogTools";
import { SpeedGraph } from "./SpeedGraph";
import { useLive } from "../live";
import { formatTime } from "../format";
import type { SharedTaskInfo } from "../server/sharedTasks";
import {
  acceptChoice,
  acceptConfirm,
  acceptPrompt,
  applySharedTasks,
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
//
// While part of the page is full screen (the query view, the model diagrams), the dialog is drawn
// inside that element instead. The browser shows nothing of the page outside it then, so a dialog
// in its usual place - the confirmation before deleting what was selected in a full screen query
// view, say - stayed invisible until full screen was left, and the view looked as if it had hung.
export function DialogHost() {
  const fullscreen = useSyncExternalStore(subscribeFullscreen, () => document.fullscreenElement);
  const dialog = <ActiveDialog />;
  return fullscreen ? createPortal(dialog, fullscreen) : dialog;
}

function subscribeFullscreen(listener: () => void): () => void {
  document.addEventListener("fullscreenchange", listener);
  return () => document.removeEventListener("fullscreenchange", listener);
}

function ActiveDialog() {
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
            <DialogTools onClose={closeDialog} />
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
          <h3>
            {dialog.title}
            <DialogTools onClose={closeDialog} closeTitle="Cancel" />
          </h3>
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
            <DialogTools onClose={closeDialog} closeTitle="Cancel" />
          </h3>
          <div className="dialog-body">{dialog.body}</div>
          {dialog.details.length > 0 && (
            <div className="dialog-details">
              {dialog.details.map((detail, i) => (
                <div key={i}>{detail}</div>
              ))}
            </div>
          )}
          {dialog.steps && (
            <div className="dialog-steps">
              {dialog.steps.intro && <div className="dialog-steps-intro">{dialog.steps.intro}</div>}
              <ol>
                {dialog.steps.items.map((step, i) => (
                  <li key={i}>{step}</li>
                ))}
              </ol>
              {dialog.steps.note && (
                <div className="dialog-steps-note">
                  <IconAlertTriangle size={14} stroke={2} />
                  <span>{dialog.steps.note}</span>
                </div>
              )}
            </div>
          )}
          {dialog.option && (
            <label className="login-remember dialog-option">
              <input type="checkbox" checked={dialog.option.checked} onChange={toggleConfirmOption} />
              {dialog.option.label}
            </label>
          )}
          {/* the doing button first, the way out last: the one on the right is the one a hand
              coming back from the cross in the corner reaches first */}
          <div className="dialog-row">
            <div className="header-spacer" />
            <button
              className={"action-button dialog-confirm" + (dialog.danger ? " danger" : "")}
              disabled={blocked}
              onClick={acceptConfirm}
            >
              {dialog.confirmLabel}
            </button>
            <button className="action-button" onClick={closeDialog}>
              Cancel
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
        <h3>
          {dialog.title}
          <DialogTools onClose={closeDialog} closeTitle="Cancel" />
        </h3>
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
          <button className="action-button dialog-confirm" disabled={blocked} onClick={acceptPrompt}>
            {dialog.confirmLabel}
          </button>
          <button className="action-button" onClick={closeDialog}>
            Cancel
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

// "Started by ole at 14:02", for a task this tab only follows
function startedBy(task: ProgressState): string | null {
  if (!task.remote) return null;
  const at = formatTime(task.remote.startedUtc);
  return task.remote.startedBy ? `Started by ${task.remote.startedBy}, ${at}` : `Started ${at}`;
}

function ProgressDialog({ dialog }: { dialog: ProgressState }) {
  const running = dialog.status === "running";
  const pct = percentOf(dialog);
  const canMinimize = running && dialog.minimizable;
  const canCancel = running && dialog.cancellable;
  const origin = startedBy(dialog);
  return (
    // a task that may be put away is not truly modal: clicking beside it puts it in the bar rather
    // than doing nothing, the way the button does
    <div className="dialog-backdrop" onClick={canMinimize ? (e) => e.target === e.currentTarget && minimizeProgress(dialog.id) : undefined}>
      <div className="dialog">
        <h3>
          {dialog.title}
          {/* the cross does what the button below it does: a running task is closed by cancelling
              it, a finished one by dismissing it - and one nothing can stop is put away */}
          <DialogTools
            onMinimize={canMinimize ? () => minimizeProgress(dialog.id) : undefined}
            onClose={canCancel ? () => cancelProgress(dialog.id) : running ? () => minimizeProgress(dialog.id) : closeDialog}
            closeTitle={canCancel ? "Cancel" : running ? "Minimize" : "Close"}
          />
        </h3>
        {origin && <div className="dialog-origin muted">{origin}</div>}
        <div className="dialog-label" title={dialog.message ?? dialog.label}>
          {dialog.message ?? dialog.label ?? ""}
        </div>
        <div className={"progress-bar" + (running && pct === null ? " indeterminate" : "") + (dialog.status === "error" ? " error" : "")}>
          <div className="progress-fill" style={{ width: (running ? (pct ?? 100) : 100) + "%" }} />
        </div>
        {/* kept once the task ends: how fast it went is worth a look when it stopped short */}
        {dialog.speed && <SpeedGraph speed={dialog.speed} />}
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
            canCancel ? (
              <button className="action-button" onClick={() => cancelProgress(dialog.id)}>
                Cancel
              </button>
            ) : canMinimize ? (
              <button className="action-button" onClick={() => minimizeProgress(dialog.id)} title="It runs on the server until it is done; put it in the top bar meanwhile">
                Minimize
              </button>
            ) : null
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
 *
 * The bar is also where the long jobs of every other session show up: it follows the server's task
 * board (see dialogs.ts, applySharedTasks), on a cadence of its own - the refresh rate in the top bar
 * is about the pages, and pausing them must not freeze a chip that is counting.
 */
export function MinimizedProgress() {
  useLive<SharedTaskInfo[]>("shared-tasks", null, (list) => applySharedTasks(Array.isArray(list) ? list : []), { fixedMs: 1000 });
  const tasks = useSyncExternalStore(subscribeDialogs, getMinimizedProgress);
  if (tasks.length === 0) return null;
  return (
    <div className="task-chips">
      {tasks.map((task) => {
        const pct = percentOf(task);
        const running = task.status === "running";
        const line = task.message ?? task.label;
        const origin = startedBy(task);
        const tip = [line ? `${task.title} - ${line}` : task.title, origin].filter(Boolean).join("\n");
        return (
          <div key={task.id} className={"task-chip " + task.status + (task.remote ? " remote" : "")}>
            <button className="task-chip-open" onClick={() => restoreProgress(task.id)} title={tip}>
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
              task.cancellable && (
                <button className="icon-button task-chip-side" onClick={() => cancelProgress(task.id)} title="Cancel">
                  <IconX size={13} stroke={2} />
                </button>
              )
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
