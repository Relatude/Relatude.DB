// Generic modal dialogs. Two kinds so far:
//  - progress: one task at a time runs behind a modal with a progress bar and a cancel
//    button; the task reports through a ProgressController and honors its AbortSignal.
//  - message: a reusable result/alert dialog (title, body, optional detail list), used
//    when an action could not be completed (failed deletions, downloads, uploads, ...).

export interface ProgressState {
  kind: "progress";
  title: string;
  label: string;
  done: number;
  total: number | null; // null = indeterminate
  status: "running" | "done" | "error" | "cancelled";
  message: string | null;
}

export interface MessageState {
  kind: "message";
  title: string;
  body: string;
  details: string[];
  tone: "error" | "info";
}

export type DialogState = ProgressState | MessageState;

export interface ProgressController {
  signal: AbortSignal;
  set(update: { label?: string; done?: number; total?: number | null }): void;
}

let state: DialogState | null = null;
let currentAbort: AbortController | null = null;
let messageResolve: (() => void) | null = null;
const listeners = new Set<() => void>();

function emit(): void {
  for (const listener of listeners) listener();
}

export function subscribeDialogs(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function getDialogState(): DialogState | null {
  return state;
}

// Runs the task behind a modal progress dialog. Resolves with the task's result, or
// undefined when it was cancelled or failed (the dialog shows what happened).
export async function runWithProgress<T>(title: string, task: (ctl: ProgressController) => Promise<T>): Promise<T | undefined> {
  if (state?.kind === "progress" && state.status === "running") throw new Error("Another task is already running.");
  const abort = new AbortController();
  currentAbort = abort;
  state = { kind: "progress", title, label: "", done: 0, total: null, status: "running", message: null };
  emit();
  const ctl: ProgressController = {
    signal: abort.signal,
    set(update) {
      if (state?.kind !== "progress" || state.status !== "running") return;
      state = { ...state, ...update };
      emit();
    },
  };
  try {
    const result = await task(ctl);
    if (state?.kind === "progress") {
      state = { ...state, status: abort.signal.aborted ? "cancelled" : "done", message: abort.signal.aborted ? "Cancelled." : null };
      emit();
    }
    if (!abort.signal.aborted) {
      // successful tasks close on their own after a beat
      setTimeout(() => {
        if (state?.kind === "progress" && state.status === "done") {
          state = null;
          emit();
        }
      }, 700);
      return result;
    }
    return undefined;
  } catch (error) {
    if (state?.kind === "progress") {
      if (abort.signal.aborted) {
        state = { ...state, status: "cancelled", message: "Cancelled." };
      } else {
        state = { ...state, status: "error", message: error instanceof Error ? error.message : String(error) };
      }
      emit();
    }
    return undefined;
  } finally {
    currentAbort = null;
  }
}

export function cancelProgress(): void {
  currentAbort?.abort();
}

// A modal message; resolves when the user closes it. Waits for a running task to finish first.
export function showError(title: string, body: string, details: string[] = []): Promise<void> {
  return show({ kind: "message", title, body, details, tone: "error" });
}

export function showInfo(title: string, body: string, details: string[] = []): Promise<void> {
  return show({ kind: "message", title, body, details, tone: "info" });
}

function show(message: MessageState): Promise<void> {
  return new Promise((resolve) => {
    const tryShow = () => {
      if (state?.kind === "progress" && state.status === "running") return false;
      messageResolve?.(); // an earlier message being replaced still resolves
      state = message;
      messageResolve = resolve;
      emit();
      return true;
    };
    if (tryShow()) return;
    const unsubscribe = subscribeDialogs(() => {
      if (tryShow()) unsubscribe();
    });
  });
}

export function closeDialog(): void {
  if (!state) return;
  if (state.kind === "progress" && state.status === "running") return; // running tasks are cancelled, not closed
  const resolve = messageResolve;
  messageResolve = null;
  state = null;
  emit();
  resolve?.();
}
