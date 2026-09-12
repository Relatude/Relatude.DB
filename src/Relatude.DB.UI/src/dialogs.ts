// Generic modal dialogs. Kinds so far:
//  - progress: a task runs behind a modal with a progress bar and a cancel button; the task reports
//    through a ProgressController and honors its AbortSignal. A task started as minimizable can be
//    put away into the top bar, where it keeps running and keeps reporting - that is for the long
//    jobs that leave the database usable while they run (adding demo content, truncating), where
//    holding the whole UI behind a modal buys nothing. Everything else stays modal, so there is at
//    most one un-minimized task at a time and the others are chips in the bar.
//  - message: a reusable result/alert dialog (title, body, optional detail list), used
//    when an action could not be completed (failed deletions, downloads, uploads, ...).
//  - confirm: a question with confirm/cancel and an optional checkbox.
//  - choice: pick one of a list (used when an action has several possible targets).

export interface ProgressState {
  kind: "progress";
  id: number;
  key: string | null; // identifies the job, so the same one cannot be started twice while minimized
  title: string;
  label: string;
  done: number;
  total: number | null; // null = indeterminate
  meta: string | null; // replaces the "done / total · pct" line when the task counts in its own unit
  status: "running" | "done" | "error" | "cancelled";
  message: string | null;
  minimizable: boolean;
  minimized: boolean;
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
  // required = the confirm button stays disabled until it is ticked; used by the extra dialog in
  // front of deleting primary data, where the point is that the choice cannot be made absently
  option: { label: string; checked: boolean; required: boolean } | null;
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

export interface PromptState {
  kind: "prompt";
  title: string;
  body: string;
  label: string;
  value: string;
  confirmLabel: string;
  error: string | null; // what validate said about the current value
  validate: ((value: string) => string | null) | null;
  selectEnd: number | null; // how much of the initial value starts selected (a file name's stem)
}

export type DialogState = ProgressState | MessageState | ConfirmState | ChoiceState | PromptState;

export interface ProgressController {
  signal: AbortSignal;
  set(update: { label?: string; done?: number; total?: number | null; meta?: string | null }): void;
}

export interface ProgressOptions {
  // lets the user put the task in the top bar and carry on; only for work that does not need the
  // rest of the UI held still
  minimizable?: boolean;
  // a stable name for the job. Starting it again while a minimized one with the same key is still
  // running brings that one forward instead of running a second copy.
  key?: string;
}

// how long a finished task stays in the bar before it takes itself away; failures stay until
// they are looked at
const finishedChipMs = 5000;

let other: DialogState | null = null; // the modal that is not a task: message / confirm / choice / prompt
let tasks: ProgressState[] = []; // progress tasks, oldest first
let visible: DialogState | null = null; // what DialogHost renders: the front task, else `other`
let minimized: ProgressState[] = []; // what the top bar renders
const aborts = new Map<number, AbortController>();
let nextId = 1;
let messageResolve: (() => void) | null = null;
let confirmResolve: ((result: ConfirmResult) => void) | null = null;
let choiceResolve: ((index: number | null) => void) | null = null;
let promptResolve: ((value: string | null) => void) | null = null;
const listeners = new Set<() => void>();

function emit(): void {
  for (const listener of listeners) listener();
}

// Recomputes the two snapshots the components read and tells them. Both are kept as stored values
// rather than derived on read, because useSyncExternalStore compares snapshots by identity.
function refresh(): void {
  visible = tasks.find((t) => !t.minimized) ?? other;
  const next = tasks.filter((t) => t.minimized);
  if (next.length !== minimized.length || next.some((t, i) => t !== minimized[i])) minimized = next;
  emit();
}

export function subscribeDialogs(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function getDialogState(): DialogState | null {
  return visible;
}

/** The tasks that were put away into the top bar, in the order they were started. */
export function getMinimizedProgress(): ProgressState[] {
  return minimized;
}

/** The task started under this key, whether it is showing or minimized; for buttons that must not start a second one. */
export function getProgressByKey(key: string | null): ProgressState | null {
  if (!key) return null;
  return tasks.find((t) => t.key === key) ?? null;
}

function findTask(id: number): ProgressState | null {
  return tasks.find((t) => t.id === id) ?? null;
}

function updateTask(id: number, patch: Partial<ProgressState>): void {
  const index = tasks.findIndex((t) => t.id === id);
  if (index < 0) return;
  tasks = tasks.slice();
  tasks[index] = { ...tasks[index], ...patch };
  refresh();
}

function removeTask(id: number): void {
  if (!tasks.some((t) => t.id === id)) return;
  tasks = tasks.filter((t) => t.id !== id);
  refresh();
}

// Runs the task behind a progress dialog. Resolves with the task's result, or undefined when it was
// cancelled or failed (the dialog says what happened). A minimizable task goes on running after the
// user puts it in the bar, so the caller's code after the await may run with the page it started
// from long since left - it must not assume it is still on screen.
export async function runWithProgress<T>(
  title: string,
  task: (ctl: ProgressController) => Promise<T>,
  options?: ProgressOptions,
): Promise<T | undefined> {
  const key = options?.key ?? null;
  const running = key ? tasks.find((t) => t.key === key && t.status === "running") : undefined;
  if (running) {
    // the same job is already going, minimized: show it rather than start a second one
    restoreProgress(running.id);
    return undefined;
  }
  if (visible?.kind === "progress" && visible.status === "running") throw new Error("Another task is already running.");
  const id = nextId++;
  const abort = new AbortController();
  aborts.set(id, abort);
  tasks = [
    ...tasks,
    {
      kind: "progress",
      id,
      key,
      title,
      label: "",
      done: 0,
      total: null,
      meta: null,
      status: "running",
      message: null,
      minimizable: options?.minimizable ?? false,
      minimized: false,
    },
  ];
  refresh();
  const ctl: ProgressController = {
    signal: abort.signal,
    set(update) {
      const current = findTask(id);
      if (!current || current.status !== "running") return;
      updateTask(id, update);
    },
  };
  try {
    const result = await task(ctl);
    const cancelled = abort.signal.aborted;
    settleTask(id, cancelled ? "cancelled" : "done", cancelled ? "Cancelled." : null);
    return cancelled ? undefined : result;
  } catch (error) {
    if (abort.signal.aborted) settleTask(id, "cancelled", "Cancelled.");
    else settleTask(id, "error", error instanceof Error ? error.message : String(error));
    return undefined;
  } finally {
    aborts.delete(id);
  }
}

// A finished task closes itself when there is nothing to read: a successful one after a beat, a
// minimized one after long enough to be noticed in the bar. A failure stays until it is closed.
function settleTask(id: number, status: ProgressState["status"], message: string | null): void {
  const current = findTask(id);
  if (!current) return;
  updateTask(id, { status, message });
  if (status === "error") return;
  const inBar = current.minimized;
  const delay = inBar ? finishedChipMs : status === "done" ? 700 : 0;
  if (delay === 0) return; // a cancelled modal stays until it is closed, as it always has
  setTimeout(() => {
    const later = findTask(id);
    if (!later || later.status !== status) return;
    if (inBar && !later.minimized) return; // opened from the bar to be read: it closes when the reader says so
    removeTask(id);
  }, delay);
}

/** Puts a running task in the top bar; it keeps running and keeps reporting there. */
export function minimizeProgress(id?: number): void {
  const task = id != null ? findTask(id) : visible?.kind === "progress" ? visible : null;
  if (!task || !task.minimizable || task.minimized) return;
  updateTask(task.id, { minimized: true });
}

/** Brings a task from the bar back in front. */
export function restoreProgress(id: number): void {
  const task = findTask(id);
  if (!task || !task.minimized) return;
  updateTask(task.id, { minimized: false });
}

/** Takes a finished task out of the bar without showing it again. */
export function dismissProgress(id: number): void {
  const task = findTask(id);
  if (!task || task.status === "running") return;
  removeTask(id);
}

/** Cancels a task: the one in front by default, or the given one (a chip in the bar). */
export function cancelProgress(id?: number): void {
  const task = id != null ? findTask(id) : visible?.kind === "progress" ? visible : null;
  if (!task) return;
  aborts.get(task.id)?.abort();
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
      other = message;
      messageResolve = resolve;
      refresh();
    });
  });
}

// A modal question with confirm/cancel (and an optional checkbox). Resolves with the
// choice; closing counts as cancel. Waits for a running task to finish first.
export function showConfirm(
  title: string,
  body: string,
  options?: { confirmLabel?: string; danger?: boolean; option?: { label: string; checked?: boolean; required?: boolean } },
): Promise<ConfirmResult> {
  return new Promise((resolve) => {
    whenIdle(() => {
      other = {
        kind: "confirm",
        title,
        body,
        confirmLabel: options?.confirmLabel ?? "OK",
        danger: options?.danger ?? false,
        option: options?.option
          ? { label: options.option.label, checked: options.option.checked ?? false, required: options.option.required ?? false }
          : null,
      };
      confirmResolve = resolve;
      refresh();
    });
  });
}

// A modal list to pick one entry from; resolves with its index, or null when closed.
// Waits for a running task to finish first.
export function showChoice(title: string, body: string, options: { label: string; hint?: string }[]): Promise<number | null> {
  return new Promise((resolve) => {
    whenIdle(() => {
      other = { kind: "choice", title, body, options: options.map((o) => ({ label: o.label, hint: o.hint ?? null })) };
      choiceResolve = resolve;
      refresh();
    });
  });
}

// A modal text question (a name to give something); resolves with the text, or null when closed.
// validate turns a value into an error message shown under the input, and blocks the confirm.
export function showPrompt(
  title: string,
  body: string,
  options?: { label?: string; initial?: string; confirmLabel?: string; validate?: (value: string) => string | null; selectEnd?: number },
): Promise<string | null> {
  return new Promise((resolve) => {
    whenIdle(() => {
      const validate = options?.validate ?? null;
      const value = options?.initial ?? "";
      other = {
        kind: "prompt",
        title,
        body,
        label: options?.label ?? "Name",
        value,
        confirmLabel: options?.confirmLabel ?? "OK",
        error: validate ? validate(value) : null,
        validate,
        selectEnd: options?.selectEnd ?? null,
      };
      promptResolve = resolve;
      refresh();
    });
  });
}

export function setPromptValue(value: string): void {
  if (other?.kind !== "prompt") return;
  other = { ...other, value, error: other.validate ? other.validate(value) : null, selectEnd: null };
  refresh();
}

export function acceptPrompt(): void {
  if (other?.kind !== "prompt") return;
  if (other.error || other.value.trim().length === 0) return;
  const resolve = promptResolve;
  const value = other.value;
  promptResolve = null;
  other = null;
  refresh();
  resolve?.(value);
}

export function acceptChoice(index: number): void {
  if (other?.kind !== "choice") return;
  const resolve = choiceResolve;
  choiceResolve = null;
  other = null;
  refresh();
  resolve?.(index);
}

export function toggleConfirmOption(): void {
  if (other?.kind !== "confirm" || !other.option) return;
  other = { ...other, option: { ...other.option, checked: !other.option.checked } };
  refresh();
}

export function acceptConfirm(): void {
  if (other?.kind !== "confirm") return;
  if (other.option?.required && !other.option.checked) return;
  const resolve = confirmResolve;
  const option = other.option?.checked ?? false;
  confirmResolve = null;
  other = null;
  refresh();
  resolve?.({ ok: true, option });
}

// runs the given show-function once no task is in front; replaced dialogs resolve first. A task
// that was minimized is not in the way: the point of minimizing it is that the UI carries on.
function whenIdle(showNow: () => void): void {
  const attempt = () => {
    if (visible?.kind === "progress" && visible.status === "running") return false;
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
  const prompt = promptResolve;
  const option = other?.kind === "confirm" ? (other.option?.checked ?? false) : false;
  messageResolve = null;
  confirmResolve = null;
  choiceResolve = null;
  promptResolve = null;
  message?.();
  confirm?.({ ok: confirmOk, option });
  choice?.(null);
  prompt?.(null);
}

export function closeDialog(): void {
  if (!visible) return;
  if (visible.kind === "progress") {
    if (visible.status === "running") return; // running tasks are cancelled or minimized, not closed
    removeTask(visible.id);
    return;
  }
  settlePending(false);
  other = null;
  refresh();
}
