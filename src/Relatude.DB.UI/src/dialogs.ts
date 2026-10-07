// Generic modal dialogs. Kinds so far:
//  - progress: a task runs behind a modal with a progress bar and a cancel button; the task reports
//    through a ProgressController and honors its AbortSignal. A task started as minimizable can be
//    put away into the top bar, where it keeps running and keeps reporting - that is for the long
//    jobs that leave the database usable while they run (adding demo content, truncating, backups,
//    file audits, uploads and downloads), where holding the whole UI behind a modal buys nothing.
//    Everything else stays modal, so there is at most one un-minimized task at a time and the others
//    are chips in the bar.
//    A minimizable task is also put on the server's task board (server/sharedTasks.ts), and the
//    board's tasks that this tab is not running itself are here too, as chips: started in another
//    tab, by another user, or by this tab before it was reloaded. Those are `remote`.
//  - message: a reusable result/alert dialog (title, body, optional detail list), used
//    when an action could not be completed (failed deletions, downloads, uploads, ...).
//  - confirm: a question with confirm/cancel and an optional checkbox.
//  - choice: pick one of a list (used when an action has several possible targets).

import { cancelSharedTask, newTaskId, reportLeaving, reportSharedTask, type SharedReport, type SharedTaskInfo } from "./server/sharedTasks";

export interface ProgressState {
  kind: "progress";
  id: number;
  key: string | null; // identifies the job, so the same one cannot be started twice while minimized
  shared: string | null; // its id on the server's task board; null for a task that is not put there
  remote: RemoteTask | null; // set for a task this tab only follows: someone else runs it
  cancellable: boolean; // false for work nothing stops once it started: no Cancel, only Minimize
  title: string;
  label: string;
  done: number;
  total: number | null; // null = indeterminate
  meta: string | null; // replaces the "done / total · pct" line when the task counts in its own unit
  speed: ProgressSpeed | null; // a transfer that reports its rate, drawn under the bar (see SpeedGraph)
  status: "running" | "done" | "error" | "cancelled";
  message: string | null;
  minimizable: boolean;
  minimized: boolean;
}

/**
 * How fast a transfer has been going, for the small graph in its dialog. The x axis is the transfer
 * itself, the way the progress bar above it is - the graph fills in under the bar as the bar fills -
 * cut into columns, each the bytes per second moved while the transfer was in that part of it.
 */
export interface ProgressSpeed {
  rates: (number | null)[]; // bytes per second, one per column; null for the part not reached yet
  reached: number; // how far through the transfer is, 0 to 1: where the line ends
  elapsedMs: number;
  now: number; // bytes per second over the last few seconds
  average: number; // bytes per second since the start
}

/** Who started a task this tab only follows, and when. */
export interface RemoteTask {
  startedBy: string | null;
  startedUtc: string;
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
  // the things it is about, one per line under the body, as a message's details are shown
  details: string[];
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
  /**
   * The task's id on the server's task board, for the command that starts the work: passed as
   * `taskId`, the server attaches its job to the task, and every session follows the job from then
   * on - this one too, after a reload. Null for a task that is not minimizable (not on the board).
   */
  taskId: string | null;
  set(update: { label?: string; done?: number; total?: number | null; meta?: string | null; speed?: ProgressSpeed | null }): void;
}

export interface ProgressOptions {
  // lets the user put the task in the top bar and carry on; only for work that does not need the
  // rest of the UI held still
  minimizable?: boolean;
  // a stable name for the job. Starting it again while a minimized one with the same key is still
  // running brings that one forward instead of running a second copy - one another session started
  // included, since those are here as well.
  key?: string;
  // false for work that cannot be stopped once it has started (a truncation queued on the server):
  // the dialog then offers no Cancel, and its cross puts it away instead
  cancellable?: boolean;
}

// how long a finished task stays in the bar before it takes itself away; failures stay until
// they are looked at
const finishedChipMs = 5000;
// how often a running task tells the board where it is at most, and at least
const reportEveryMs = 1000;
const heartbeatMs = 5000;
// a task another session finished is only brought in when it finished this recently: a page opened
// later has no use for a chip saying something went fine a minute ago
const recentlyFinishedMs = 15000;

let other: DialogState | null = null; // the modal that is not a task: message / confirm / choice / prompt
let tasks: ProgressState[] = []; // progress tasks, oldest first
let visible: DialogState | null = null; // what DialogHost renders: the front task, else `other`
let minimized: ProgressState[] = []; // what the top bar renders
const aborts = new Map<number, AbortController>();
// the board ids this tab is done with - its own tasks, and the ones it closed or dismissed: the board
// lists a finished task for a while, and it must not come back as a chip once it has gone
const doneWith = new Set<string>();
// what reports each of this tab's own shared tasks, by task id
const reporters = new Map<number, Reporter>();
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

/**
 * The task started under this key, whether it is showing or minimized; for buttons that must not
 * start a second one. A running task wins over a finished one under the same key: a failed run stays
 * in the bar until it is read, and a job started again meanwhile must still read as running.
 */
export function getProgressByKey(key: string | null): ProgressState | null {
  if (!key) return null;
  return tasks.find((t) => t.key === key && t.status === "running") ?? tasks.find((t) => t.key === key) ?? null;
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
  const minimizable = options?.minimizable ?? false;
  const shared = minimizable ? newTaskId() : null;
  if (shared) doneWith.add(shared);
  tasks = [
    ...tasks,
    {
      kind: "progress",
      id,
      key,
      shared,
      remote: null,
      cancellable: options?.cancellable ?? true,
      title,
      label: "",
      done: 0,
      total: null,
      meta: null,
      speed: null,
      status: "running",
      message: null,
      minimizable,
      minimized: false,
    },
  ];
  refresh();
  const reporter = shared ? startReporting(id, shared, abort) : null;
  const ctl: ProgressController = {
    signal: abort.signal,
    taskId: shared,
    set(update) {
      const current = findTask(id);
      if (!current || current.status !== "running") return;
      updateTask(id, update);
      reporter?.changed();
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
    reporter?.finish();
  }
}

// ---- the server's task board ----

interface Reporter {
  changed(): void;
  finish(): void;
  report(): SharedReport | null;
}

// Tells the board where one of this tab's tasks is: at once, then on every change but at most once a
// second, and every few seconds whatever happens - the board takes silence as the tab having gone. A
// report is also where a Cancel from another session arrives.
function startReporting(id: number, shared: string, abort: AbortController): Reporter {
  let timer: number | null = null;
  let lastSent = 0;
  let finished = false;
  let seq = 0;
  const report = (): SharedReport | null => {
    const t = findTask(id);
    if (!t) return null;
    seq++;
    return { id: shared, key: t.key, title: t.title, status: t.status, label: t.label, done: t.done, total: t.total, meta: t.meta, message: t.message, seq };
  };
  const sendNow = () => {
    if (timer !== null) window.clearTimeout(timer);
    timer = null;
    const r = report();
    if (!r) return;
    lastSent = Date.now();
    reportSharedTask(r)
      .then((cancel) => {
        if (cancel && !abort.signal.aborted) abort.abort();
      })
      .catch(() => {}); // the board is a courtesy to the other sessions: the task goes on without it
  };
  const heartbeat = window.setInterval(() => {
    if (Date.now() - lastSent >= heartbeatMs - 250) sendNow();
  }, heartbeatMs);
  const reporter: Reporter = {
    // sent straight away when a second has passed, so a tab in the background (where timers are
    // slowed down) still reports as often as its work moves
    changed() {
      if (finished || timer !== null) return;
      const wait = reportEveryMs - (Date.now() - lastSent);
      if (wait <= 0) sendNow();
      else timer = window.setTimeout(sendNow, wait);
    },
    finish() {
      if (finished) return;
      finished = true;
      window.clearInterval(heartbeat);
      sendNow();
      reporters.delete(id);
    },
    report,
  };
  reporters.set(id, reporter);
  sendNow(); // before the work's first command: the board knows the title and key from the start
  return reporter;
}

// the page is going away: the board hears it from here rather than from the silence that follows
if (typeof window !== "undefined") {
  window.addEventListener("pagehide", () => {
    const running = [...reporters.values()].map((r) => r.report()).filter((r): r is SharedReport => r !== null && r.status === "running");
    if (running.length > 0) reportLeaving(running);
  });
}

/**
 * The board as the server lists it (see MinimizedProgress in DialogHost, which follows it): every task this
 * tab is not running itself becomes a chip here, kept up to date, and settles the way a chip of its
 * own does once it is done. A task the tab already had, or closed, is not brought back once finished.
 */
export function applySharedTasks(list: SharedTaskInfo[]): void {
  let changed = false;
  let next = tasks;
  const settled: number[] = [];
  for (const r of list) {
    if (next.some((t) => t.remote === null && t.shared === r.id)) continue; // this tab runs it: it shows its own
    const index = next.findIndex((t) => t.remote !== null && t.shared === r.id);
    if (index < 0) {
      if (r.status !== "running") {
        if (doneWith.has(r.id)) continue;
        const finished = r.finishedUtc ? Date.parse(r.finishedUtc) : 0;
        if (!(Date.now() - finished < recentlyFinishedMs)) continue;
      }
      next = [...next, remoteTask(nextId++, r)];
      if (r.status !== "running") settled.push(next[next.length - 1].id);
      changed = true;
      continue;
    }
    const before = next[index];
    const after: ProgressState = {
      ...before,
      key: r.key,
      title: r.title,
      label: r.label ?? "",
      done: r.done,
      total: r.total,
      meta: r.meta,
      status: r.status,
      message: r.message,
      cancellable: r.cancellable,
    };
    if (sameProgress(before, after)) continue;
    next = next.slice();
    next[index] = after;
    if (before.status === "running" && after.status !== "running") settled.push(after.id);
    changed = true;
  }
  // a running task the board no longer lists is one the server forgot (it restarted): nothing will
  // ever say how it ended, so the chip goes rather than spin forever
  const listed = new Set(list.map((r) => r.id));
  const forgotten = next.filter((t) => t.remote !== null && t.status === "running" && !listed.has(t.shared!));
  if (forgotten.length > 0) {
    next = next.filter((t) => !forgotten.includes(t));
    changed = true;
  }
  if (!changed) return;
  tasks = next;
  refresh();
  for (const id of settled) settleRemote(id);
}

function remoteTask(id: number, r: SharedTaskInfo): ProgressState {
  return {
    kind: "progress",
    id,
    key: r.key,
    shared: r.id,
    remote: { startedBy: r.startedBy, startedUtc: r.startedUtc },
    cancellable: r.cancellable,
    title: r.title,
    label: r.label ?? "",
    done: r.done,
    total: r.total,
    meta: r.meta,
    speed: null,
    status: r.status,
    message: r.message,
    minimizable: true,
    minimized: true, // someone else's work is a chip until it is asked for
  };
}

function sameProgress(a: ProgressState, b: ProgressState): boolean {
  return (
    a.key === b.key && a.title === b.title && a.label === b.label && a.done === b.done && a.total === b.total && a.meta === b.meta
    && a.status === b.status && a.message === b.message && a.cancellable === b.cancellable
  );
}

// a remote task that finished goes the way a minimized one of this tab's own goes: a beat in the bar,
// unless it failed or is open to be read
function settleRemote(id: number): void {
  const task = findTask(id);
  if (!task || task.status === "error" || !task.minimized) return;
  setTimeout(() => {
    const later = findTask(id);
    if (!later || later.status === "running" || !later.minimized) return;
    forget(later);
    removeTask(id);
  }, finishedChipMs);
}

function forget(task: ProgressState): void {
  if (task.shared) doneWith.add(task.shared);
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
  forget(task);
  removeTask(id);
}

/** Cancels a task: the one in front by default, or the given one (a chip in the bar). One another
 * session runs is cancelled through the board, and its chip says so when the board does. */
export function cancelProgress(id?: number): void {
  const task = id != null ? findTask(id) : visible?.kind === "progress" ? visible : null;
  if (!task || !task.cancellable) return;
  if (task.remote) {
    if (task.shared) {
      updateTask(task.id, { label: "Cancelling…" });
      cancelSharedTask(task.shared).catch((e) => updateTask(task.id, { label: e instanceof Error ? e.message : String(e) }));
    }
    return;
  }
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
  options?: { confirmLabel?: string; danger?: boolean; details?: string[]; option?: { label: string; checked?: boolean; required?: boolean } },
): Promise<ConfirmResult> {
  return new Promise((resolve) => {
    whenIdle(() => {
      other = {
        kind: "confirm",
        title,
        body,
        confirmLabel: options?.confirmLabel ?? "OK",
        danger: options?.danger ?? false,
        details: options?.details ?? [],
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
    forget(visible);
    removeTask(visible.id);
    return;
  }
  settlePending(false);
  other = null;
  refresh();
}
