import { useCallback, useEffect, useRef, useState } from "react";
import { IconChevronLeft, IconChevronRight, IconEraser, IconReload, IconTrash, IconX } from "@tabler/icons-react";
import { usePoll } from "../refresh";
import { showConfirm, showError, showInfo } from "../dialogs";
import {
  clearTasks,
  deleteTasks,
  fetchTasks,
  setTaskState,
  setTaskThrottle,
  type BatchState,
  type QueueId,
  type TaskBatch,
  type TasksInfo,
} from "../server/tasks";
import type { DatabaseInfo } from "../server/serverInfo";
import { formatCount, formatDuration, formatTime } from "../format";

const pageSize = 50;

// Fixed order, not the order the server happens to have counted them in: these tiles are the first
// thing read on the page and must not move under the pointer between two polls.
const stateOrder: BatchState[] = ["Pending", "Running", "Waiting", "Completed", "Failed", "Cancelled", "AbortedOnStartup"];
// shown even at zero - "nothing failed" is worth saying, and a tile that appears only on failure is
// a tile nobody knows is missing
const alwaysShown: BatchState[] = ["Pending", "Running", "Failed"];
const stateLabels: Record<BatchState, string> = {
  Pending: "Pending",
  Running: "Running",
  Waiting: "Waiting",
  Completed: "Completed",
  Failed: "Failed",
  Cancelled: "Cancelled",
  AbortedOnStartup: "Aborted",
};
const badStates: BatchState[] = ["Failed", "Cancelled", "AbortedOnStartup"];
/** A batch in one of these is not going to move on its own: putting it back in line is the point. */
const retryable: BatchState[] = ["Failed", "Cancelled", "AbortedOnStartup", "Waiting", "Running"];
/** A batch in one of these has not run to completion yet, so taking it out of the queue means something. */
const cancellable: BatchState[] = ["Pending", "Waiting", "Running"];

/**
 * What the database is doing in the background, and the controls for when it goes wrong.
 *
 * Everything here is a *batch* rather than a task: the queue holds batches of up to a couple of
 * hundred tasks of one type, they succeed or fail together, and a control that acted on single tasks
 * would be acting on something the queue cannot address. The tiles carry both numbers for that
 * reason - the tasks are the work, the batches are what the buttons operate on.
 */
export function TasksSection({ db }: { db: DatabaseInfo }) {
  const [data, setData] = useState<TasksInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [queue, setQueue] = useState<QueueId>("memory");
  const [states, setStates] = useState<BatchState[]>([]);
  const [typeId, setTypeId] = useState<string>("");
  const [page, setPage] = useState(0);
  const [tick, setTick] = useState(0);
  const [selected, setSelected] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  // the slider has to follow the pointer, so it holds its own value while it is being dragged and
  // the poll is not allowed to pull it back to what the server last said
  const [throttle, setThrottle] = useState<number | null>(null);
  const dragging = useRef(false);

  const load = useCallback(async (): Promise<TasksInfo | null> => {
    try {
      const info = await fetchTasks(db.id, { queue, states, typeIds: typeId ? [typeId] : [], page, pageSize });
      setData(info);
      setError(null);
      // the server steps back to the last page that exists when the queue drained under the page
      if (info.page != null && info.page !== page) setPage(info.page);
      if (!dragging.current && info.throttle != null) setThrottle(info.throttle);
      // a selection of batches that have since been run or deleted would make the bulk bar lie
      const alive = new Set(info.batches.map((b) => b.batchId));
      setSelected((prev) => (prev.length === 0 ? prev : prev.filter((id) => alive.has(id))));
      return info;
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      return null;
    }
  }, [db.id, queue, states, typeId, page]);

  // the first load, and one more whenever a filter changes or the refresh button is pressed; the
  // repeat is the whole UI's refresh rate, set in the top bar
  useEffect(() => {
    load();
  }, [load, tick]);
  usePoll(load);

  function toggleState(state: BatchState) {
    setPage(0);
    setStates((prev) => (prev.includes(state) ? prev.filter((s) => s !== state) : [...prev, state]));
  }

  /** Runs one control, then refreshes: every one of them changes what the list should show. */
  async function run(what: string, action: () => Promise<unknown>): Promise<void> {
    setBusy(true);
    try {
      await action();
      setSelected([]);
      await load();
    } catch (e) {
      await showError("Could not " + what, e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  async function retry(ids: string[], anyRunning: boolean): Promise<void> {
    if (anyRunning) {
      const confirmed = await showConfirm(
        ids.length === 1 ? "Put a running batch back in the queue?" : `Put ${ids.length} batches back in the queue?`,
        "One of them is marked as running. If it really is running, it will run a second time when it is picked up again — which is what you want for a batch left behind by a crash, and not what you want for one that is simply slow.",
        { confirmLabel: "Queue again", danger: true },
      );
      if (!confirmed.ok) return;
    }
    await run("queue the batches again", () => setTaskState(db.id, queue, ids, "Pending"));
  }

  async function cancel(ids: string[]): Promise<void> {
    await run("cancel the batches", () => setTaskState(db.id, queue, ids, "Cancelled"));
  }

  async function remove(ids: string[]): Promise<void> {
    if (ids.length > 1) {
      const confirmed = await showConfirm(`Delete ${ids.length} batches?`, "The tasks in them are dropped. Work that has not run will not run.", {
        confirmLabel: "Delete",
        danger: true,
      });
      if (!confirmed.ok) return;
    }
    await run("delete the batches", () => deleteTasks(db.id, queue, ids));
  }

  async function clear(what: "finished" | "failed" | "all"): Promise<void> {
    const target: BatchState[] = what === "finished" ? ["Completed"] : what === "failed" ? badStates : [];
    const question =
      what === "all"
        ? `Delete everything in the ${queueLabel(data, queue)} queue?`
        : what === "failed"
          ? "Delete every failed, cancelled and aborted batch?"
          : "Delete every completed batch?";
    const body =
      what === "all"
        ? "Every batch is dropped, including the ones still waiting to run. Work that has not run will not run."
        : "Nothing that is waiting or running is touched.";
    const confirmed = await showConfirm(question, body, { confirmLabel: "Delete", danger: true });
    if (!confirmed.ok) return;
    await run("clear the queue", () => clearTasks(db.id, queue, target));
  }

  function commitThrottle(value: number): void {
    dragging.current = false;
    setThrottle(value);
    setTaskThrottle(db.id, value).catch((e) => showError("Could not change the speed", e instanceof Error ? e.message : String(e)));
  }

  if (error) return <div className="placeholder">{error}</div>;
  if (!data) return null;
  if (!data.open) return <div className="placeholder">Open the database to see its background tasks.</div>;

  const activeQueue = data.queues.find((q) => q.id === (data.queue ?? queue)) ?? data.queues[0];
  const counts = new Map(activeQueue?.counts.map((c) => [c.state, c]) ?? []);
  const tiles = stateOrder.filter((s) => alwaysShown.includes(s) || (counts.get(s)?.tasks ?? 0) > 0 || states.includes(s));
  const batches = data.batches;
  const selectedSet = new Set(selected);
  const selectedBatches = batches.filter((b) => selectedSet.has(b.batchId));
  const allSelected = batches.length > 0 && selected.length === batches.length;
  const types = data.types.filter((t) => t.queue === activeQueue?.id);
  const filtered = states.length > 0 || typeId !== "";
  const totalPages = Math.max(1, Math.ceil(data.total / pageSize));

  return (
    <div className="tasks">
      <div className="tasks-head">
        <div className="tasks-queues">
          {data.queues.map((q) => (
            <button
              key={q.id}
              className={"tasks-queue" + (q.id === activeQueue?.id ? " active" : "")}
              title={
                q.persisted
                  ? `Tasks that have to survive a restart, kept by ${q.engine}`
                  : "Tasks that are cheap to lose: they are held in memory and go with the process"
              }
              onClick={() => {
                setQueue(q.id);
                setPage(0);
                setSelected([]);
              }}
            >
              <span className="tasks-queue-label">{q.label}</span>
              <span className="tasks-queue-count">{formatCount(sumTasks(q.counts, ["Pending", "Running"]))}</span>
              {q.engine && <span className="tasks-queue-engine">{q.engine}</span>}
            </button>
          ))}
        </div>
        <span className="tasks-spacer" />
        {activeQueue?.estimatedEmptyMs != null && (
          <span className="tasks-estimate" title="Estimated from the rate tasks are being taken off this queue">
            empty in about {formatElapsed(activeQueue.estimatedEmptyMs)}
          </span>
        )}
        <button className="action-button" onClick={() => setTick((t) => t + 1)} title="Refresh now">
          <IconReload size={15} stroke={1.8} /> Refresh
        </button>
      </div>

      {/* the tiles are the state filter: what you want to look at is what you just read the count of */}
      <div className="tasks-stats">
        {tiles.map((state) => {
          const count = counts.get(state);
          const tone = state === "Running" ? " running" : badStates.includes(state) && (count?.tasks ?? 0) > 0 ? " bad" : state === "Completed" ? " done" : "";
          return (
            <button
              key={state}
              className={"tasks-stat" + tone + (states.includes(state) ? " selected" : "")}
              onClick={() => toggleState(state)}
              title={states.includes(state) ? "Stop filtering on this state" : "Show only batches in this state"}
            >
              <div className="tasks-stat-value">{formatCount(count?.tasks ?? 0)}</div>
              <div className="tasks-stat-label">{stateLabels[state]}</div>
              <div className="tasks-stat-sub">{count?.batches ? `${formatCount(count.batches)} ${count.batches === 1 ? "batch" : "batches"}` : "—"}</div>
            </button>
          );
        })}
      </div>

      {/* The explicit filter, and the only place every state is listed: a state with nothing in it has
          no tile, and "why is Waiting not here" is a worse question than an empty chip. The tiles
          toggle the same selection - they are the shortcut, this is the control. */}
      <div className="tasks-filters">
        <span className="tasks-filter-label">Show</span>
        <button
          className={"tasks-chip" + (states.length === 0 ? " active" : "")}
          onClick={() => {
            setStates([]);
            setPage(0);
          }}
          title="List batches in any state"
        >
          All states
        </button>
        {stateOrder.map((state) => (
          <button
            key={state}
            className={"tasks-chip" + (states.includes(state) ? " active" : "")}
            onClick={() => toggleState(state)}
            title={states.includes(state) ? "Stop listing " + stateLabels[state].toLowerCase() + " batches" : "Also list " + stateLabels[state].toLowerCase() + " batches"}
          >
            {stateLabels[state]}
            <span className="tasks-chip-count">{formatCount(counts.get(state)?.batches ?? 0)}</span>
          </button>
        ))}
        <span className="tasks-spacer" />
        <select
          className="select compact"
          value={typeId}
          onChange={(e) => {
            setTypeId(e.target.value);
            setPage(0);
          }}
          title="Show one kind of task only"
        >
          <option value="">All types</option>
          {types.map((t) => (
            <option key={t.id} value={t.id}>
              {t.name}
            </option>
          ))}
        </select>
      </div>

      <div className="tasks-toolbar">
        <Throttle
          value={throttle}
          onDrag={(v) => {
            dragging.current = true;
            setThrottle(v);
          }}
          onCommit={commitThrottle}
        />
        <span className="tasks-spacer" />
        <button className="action-button" disabled={busy} onClick={() => clear("failed")} title="Delete every failed, cancelled and aborted batch">
          <IconEraser size={15} stroke={1.8} /> Failed
        </button>
        <button className="action-button" disabled={busy} onClick={() => clear("finished")} title="Delete every completed batch">
          <IconEraser size={15} stroke={1.8} /> Completed
        </button>
        <button className="action-button danger" disabled={busy} onClick={() => clear("all")} title="Empty this queue">
          <IconTrash size={15} stroke={1.8} /> Everything
        </button>
      </div>

      {selected.length > 0 && (
        <div className="tasks-bulk">
          <span>
            {formatCount(selected.length)} selected · {formatCount(selectedBatches.reduce((sum, b) => sum + b.taskCount, 0))} tasks
          </span>
          <span className="tasks-spacer" />
          <button
            className="action-button"
            disabled={busy || !selectedBatches.some((b) => retryable.includes(b.state))}
            onClick={() =>
              retry(
                selectedBatches.filter((b) => retryable.includes(b.state)).map((b) => b.batchId),
                selectedBatches.some((b) => b.state === "Running"),
              )
            }
          >
            <IconReload size={15} stroke={1.8} /> Queue again
          </button>
          <button
            className="action-button"
            disabled={busy || !selectedBatches.some((b) => cancellable.includes(b.state))}
            onClick={() => cancel(selectedBatches.filter((b) => cancellable.includes(b.state)).map((b) => b.batchId))}
          >
            <IconX size={15} stroke={1.8} /> Cancel
          </button>
          <button className="action-button danger" disabled={busy} onClick={() => remove(selected)}>
            <IconTrash size={15} stroke={1.8} /> Delete
          </button>
          <button className="link-button" onClick={() => setSelected([])}>
            clear
          </button>
        </div>
      )}

      <section className="panel">
        <h3>
          Batches{" "}
          <span className="panel-sub">
            {data.total === 0
              ? filtered
                ? "nothing matching the filter"
                : "nothing in this queue"
              : `${formatCount(data.total)} ${data.total === 1 ? "batch" : "batches"}${filtered ? " matching the filter" : ""}`}
          </span>
        </h3>
        <div className="tasks-table">
          <div className="tasks-row tasks-head-row">
            <span>
              <input
                type="checkbox"
                checked={allSelected}
                disabled={batches.length === 0}
                onChange={(e) => setSelected(e.target.checked ? batches.map((b) => b.batchId) : [])}
                title="Select every batch on this page"
              />
            </span>
            <span>Type</span>
            <span>State</span>
            <span className="num">Tasks</span>
            <span>Priority</span>
            <span>Created</span>
            <span>Finished</span>
            <span />
          </div>
          {batches.length === 0 && <div className="tasks-empty">{emptyText(data, filtered)}</div>}
          {batches.map((b) => (
            <Row
              key={b.batchId}
              batch={b}
              selected={selectedSet.has(b.batchId)}
              busy={busy}
              onSelect={(on) => setSelected((prev) => (on ? [...prev, b.batchId] : prev.filter((id) => id !== b.batchId)))}
              onRetry={() => retry([b.batchId], b.state === "Running")}
              onCancel={() => cancel([b.batchId])}
              onDelete={() => remove([b.batchId])}
            />
          ))}
        </div>
        {data.total > pageSize && (
          <div className="tasks-paging">
            <span className="muted">
              Page {page + 1} of {formatCount(totalPages)}
            </span>
            <button className="action-button" disabled={page === 0} onClick={() => setPage(Math.max(0, page - 1))}>
              <IconChevronLeft size={15} stroke={1.8} /> Newer
            </button>
            <button className="action-button" disabled={(page + 1) * pageSize >= data.total} onClick={() => setPage(page + 1)}>
              Older <IconChevronRight size={15} stroke={1.8} />
            </button>
          </div>
        )}
      </section>

      <section className="panel">
        <h3>
          Task types <span className="panel-sub">what this database runs in the background, and how</span>
        </h3>
        <div className="tasks-types">
          {data.types.map((t) => (
            <div className="tasks-type" key={t.id}>
              <span className="tasks-type-name" title={t.id}>
                {t.name}
              </span>
              <span className="tasks-type-meta">
                {t.priority.toLowerCase()} priority · up to {formatCount(t.maxTasksPerBatch)} per batch · {t.queue === "persisted" ? "persisted" : "in memory"}
              </span>
              <span className="tasks-type-keep">
                {t.deleteOnSuccess ? "removed once it succeeds" : t.retentionMs == null ? "kept until deleted" : `kept ${formatKept(t.retentionMs)} after it runs`}
              </span>
            </div>
          ))}
          {data.types.length === 0 && <div className="tasks-empty">No task types are registered.</div>}
        </div>
      </section>
    </div>
  );
}

function Row({
  batch,
  selected,
  busy,
  onSelect,
  onRetry,
  onCancel,
  onDelete,
}: {
  batch: TaskBatch;
  selected: boolean;
  busy: boolean;
  onSelect: (on: boolean) => void;
  onRetry: () => void;
  onCancel: () => void;
  onDelete: () => void;
}) {
  const took = batch.completedUtc ? new Date(batch.completedUtc).getTime() - new Date(batch.createdUtc).getTime() : null;
  return (
    <div className={"tasks-row" + (selected ? " selected" : "")}>
      <span>
        <input type="checkbox" checked={selected} onChange={(e) => onSelect(e.target.checked)} />
      </span>
      <span className="tasks-type-cell" title={batch.typeId + (batch.jobId ? "\njob " + batch.jobId : "")}>
        {batch.type}
        {batch.errorMessage && (
          <span className="tasks-error" title={(batch.errorType ?? "") + "\n" + batch.errorMessage}>
            {batch.errorMessage}
          </span>
        )}
      </span>
      <span className={"tasks-state " + batch.state.toLowerCase()}>{stateLabels[batch.state] ?? batch.state}</span>
      <span className="num">{formatCount(batch.taskCount)}</span>
      <span className="tasks-priority">{batch.priority.toLowerCase()}</span>
      <span className="tasks-time" title={new Date(batch.createdUtc).toLocaleString()}>
        {formatTime(batch.createdUtc)}
      </span>
      {/* how long it sat in the queue, not how long the runner took: a batch is timed from the moment
          something asked for the work, which is the number worth seeing when a backlog is draining */}
      <span className="tasks-time" title={took == null ? undefined : "queued for " + formatElapsed(took)}>
        {batch.completedUtc ? formatTime(batch.completedUtc) : "—"}
      </span>
      <span className="tasks-actions">
        {batch.errorMessage && (
          <button
            className="icon-button"
            title="Show the error"
            onClick={() => showInfo(batch.type + " failed", batch.errorMessage!, batch.errorType ? [batch.errorType] : [])}
          >
            <IconX size={15} stroke={1.8} />
          </button>
        )}
        {retryable.includes(batch.state) && (
          <button className="icon-button" disabled={busy} title="Put this batch back in the queue" onClick={onRetry}>
            <IconReload size={15} stroke={1.8} />
          </button>
        )}
        {cancellable.includes(batch.state) && (
          <button className="icon-button" disabled={busy} title="Take this batch out of the queue" onClick={onCancel}>
            <IconX size={15} stroke={1.8} />
          </button>
        )}
        <button className="icon-button danger" disabled={busy} title="Delete this batch" onClick={onDelete}>
          <IconTrash size={15} stroke={1.8} />
        </button>
      </span>
    </div>
  );
}

/**
 * How much of the machine background work may take. It is one knob over several - how long a pulse
 * runs, how often pulses happen, how busy the database has to be before one steps aside - which is
 * why it is a percentage and not a number of anything.
 */
function Throttle({ value, onDrag, onCommit }: { value: number | null; onDrag: (v: number) => void; onCommit: (v: number) => void }) {
  const shown = value ?? 90;
  return (
    <div className="tasks-throttle" title="How much of the machine background tasks may take. Goes back to the server default when the database reopens.">
      <span className="tasks-throttle-label">Speed</span>
      <input
        type="range"
        min={0}
        max={100}
        step={5}
        value={shown}
        disabled={value === null}
        onChange={(e) => onDrag(Number(e.target.value))}
        onPointerUp={(e) => onCommit(Number((e.target as HTMLInputElement).value))}
        onKeyUp={(e) => onCommit(Number((e.target as HTMLInputElement).value))}
      />
      <span className="tasks-throttle-value">{shown}%</span>
    </div>
  );
}

/**
 * A span at whatever scale it happens to be: a batch of text indexing is done in milliseconds and a
 * backlog takes minutes, and a clock format renders the first as 00:00:00.
 */
function formatElapsed(ms: number): string {
  if (ms < 1000) return Math.round(ms) + " ms";
  if (ms < 60000) return (ms / 1000).toFixed(1) + " s";
  return formatDuration(ms);
}

/** Retention is a policy, not a measurement: hours and days, not a clock. */
function formatKept(ms: number): string {
  const hours = ms / 3600000;
  const plural = (n: number, unit: string) => n + " " + unit + (n === 1 ? "" : "s");
  if (hours >= 24) return plural(Math.round(hours / 24), "day");
  if (hours >= 1) return plural(Math.round(hours), "hour");
  return plural(Math.max(1, Math.round(ms / 60000)), "minute");
}

function sumTasks(counts: { state: BatchState; tasks: number }[], states: BatchState[]): number {
  return counts.filter((c) => states.includes(c.state)).reduce((sum, c) => sum + c.tasks, 0);
}

function queueLabel(data: TasksInfo | null, id: QueueId): string {
  return (data?.queues.find((q) => q.id === id)?.label ?? id).toLowerCase();
}

/** An empty queue is the normal state, so it says why rather than just being blank. */
function emptyText(data: TasksInfo, filtered: boolean): string {
  if (filtered) return "No batch matches the filter.";
  const removed = data.types.some((t) => t.deleteOnSuccess);
  return removed
    ? "Nothing queued. Batches of most types are removed the moment they succeed, so an empty list is what a database that has caught up looks like."
    : "Nothing queued.";
}
