// Generic modal dialogs. Kinds so far:
//  - progress: one task at a time runs behind a modal with a progress bar and a cancel
//    button; the task reports through a ProgressController and honors its AbortSignal.
//  - message: a reusable result/alert dialog (title, body, optional detail list), used
//    when an action could not be completed (failed deletions, downloads, uploads, ...).
//  - confirm: a question with confirm/cancel and an optional checkbox.
//  - choice: pick one of a list (used when an action has several possible targets).

export interface ProgressState {
  kind: "progress";
  title: string;
  label: string;
  done: number;
  total: number | null; // null = indeterminate
  meta: string | null; // replaces the "done / total · pct" line when the task counts in its own unit
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

export interface ConfirmState {
  kind: "confirm";
  title: string;
  body: string;
  confirmLabel: string;
  danger: boolean;
  option: { label: string; checked: boolean } | null;
}

export interface ConfirmResult {
  ok: boolean;
  option: boolean;
}

export interface ChoiceState {
  kind: "choice";
  title: string;
  body: string;
  options: { label: string; hint: string | null }[];
}

export type DialogState = ProgressState | MessageState | ConfirmState | ChoiceState;

export interface ProgressController {
  signal: AbortSignal;
  set(update: { label?: string; done?: number; total?: number | null; meta?: string | null }): void;
}

let state: DialogState | null = null;
let currentAbort: AbortController | null = null;
let messageResolve: (() => void) | null = null;
let confirmResolve: ((result: ConfirmResult) => void) | null = null;
let choiceResolve: ((index: number | null) => void) | null = null;
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
  state = { kind: "progress", title, label: "", done: 0, total: null, meta: null, status: "running", message: null };
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
    whenIdle(() => {
      state = message;
      messageResolve = resolve;
      emit();
    });
  });
}

// A modal question with confirm/cancel (and an optional checkbox). Resolves with the
// choice; closing counts as cancel. Waits for a running task to finish first.
export function showConfirm(
  title: string,
  body: string,
  options?: { confirmLabel?: string; danger?: boolean; option?: { label: string; checked?: boolean } },
): Promise<ConfirmResult> {
  return new Promise((resolve) => {
    whenIdle(() => {
      state = {
        kind: "confirm",
        title,
        body,
        confirmLabel: options?.confirmLabel ?? "OK",
        danger: options?.danger ?? false,
        option: options?.option ? { label: options.option.label, checked: options.option.checked ?? false } : null,
      };
      confirmResolve = resolve;
      emit();
    });
  });
}

// A modal list to pick one entry from; resolves with its index, or null when closed.
// Waits for a running task to finish first.
export function showChoice(title: string, body: string, options: { label: string; hint?: string }[]): Promise<number | null> {
  return new Promise((resolve) => {
    whenIdle(() => {
      state = { kind: "choice", title, body, options: options.map((o) => ({ label: o.label, hint: o.hint ?? null })) };
      choiceResolve = resolve;
      emit();
    });
  });
}

export function acceptChoice(index: number): void {
  if (state?.kind !== "choice") return;
  const resolve = choiceResolve;
  choiceResolve = null;
  state = null;
  emit();
  resolve?.(index);
}

export function toggleConfirmOption(): void {
  if (state?.kind !== "confirm" || !state.option) return;
  state = { ...state, option: { ...state.option, checked: !state.option.checked } };
  emit();
}

export function acceptConfirm(): void {
  if (state?.kind !== "confirm") return;
  const resolve = confirmResolve;
  const option = state.option?.checked ?? false;
  confirmResolve = null;
  state = null;
  emit();
  resolve?.({ ok: true, option });
}

// runs the given show-function once no task is running; replaced dialogs resolve first
function whenIdle(showNow: () => void): void {
  const attempt = () => {
    if (state?.kind === "progress" && state.status === "running") return false;
    settlePending(false);
    showNow();
    return true;
  };
  if (attempt()) return;
  const unsubscribe = subscribeDialogs(() => {
    if (attempt()) unsubscribe();
  });
}

function settlePending(confirmOk: boolean): void {
  const message = messageResolve;
  const confirm = confirmResolve;
  const choice = choiceResolve;
  const option = state?.kind === "confirm" ? (state.option?.checked ?? false) : false;
  messageResolve = null;
  confirmResolve = null;
  choiceResolve = null;
  message?.();
  confirm?.({ ok: confirmOk, option });
  choice?.(null);
}

export function closeDialog(): void {
  if (!state) return;
  if (state.kind === "progress" && state.status === "running") return; // running tasks are cancelled, not closed
  settlePending(false);
  state = null;
  emit();
}
