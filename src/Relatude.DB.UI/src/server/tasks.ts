// The background work of one database: text indexing, semantic indexing and log rewrites. See
// UITasks.cs - a database has two queues (memory and persisted) and which one a task type lands in
// is decided by the type, so the page shows both rather than one total.

import { send } from "./channel";

/** Mirrors BatchState. */
export type BatchState = "Pending" | "Running" | "Completed" | "Failed" | "Cancelled" | "Waiting" | "AbortedOnStartup";

/** Which queue a call is about. Not an id of a queue object: a database has exactly these two. */
export type QueueId = "memory" | "persisted";

export interface StateCount {
  state: BatchState;
  batches: number;
  tasks: number;
}

export interface QueueInfo {
  id: QueueId;
  label: string;
  /** the engine behind the persisted queue (a setting); null for the memory queue, which has no choice */
  engine: string | null;
  persisted: boolean;
  /** only the states that have something in them */
  counts: StateCount[];
  /** an extrapolation from the rate tasks are draining at, and only once there is a rate to use */
  estimatedEmptyMs: number | null;
}

export interface TaskType {
  id: string;
  name: string;
  priority: "Low" | "Medium" | "High";
  /** the queue tasks of this type are enqueued into */
  queue: QueueId;
  maxTasksPerBatch: number;
  /** true means a batch is gone the moment it succeeds, which is why Completed is usually empty */
  deleteOnSuccess: boolean;
  /** how long a finished batch is kept before it is swept; null means forever */
  retentionMs: number | null;
  restartOnStartup: boolean;
}

/** One batch: the unit the queue actually holds, carrying up to maxTasksPerBatch tasks of one type. */
export interface TaskBatch {
  batchId: string;
  typeId: string;
  type: string;
  state: BatchState;
  priority: "Low" | "Medium" | "High";
  taskCount: number;
  createdUtc: string;
  completedUtc: string | null;
  jobId: string | null;
  errorType: string | null;
  errorMessage: string | null;
}

export interface TasksInfo {
  /** the queues live in the open store: a closed database has a queue file but nothing reading it */
  open: boolean;
  state: string;
  /** 0-100, how much of the machine background processing may take. Runtime only. */
  throttle?: number;
  queues: QueueInfo[];
  types: TaskType[];
  /** the queue the batch page below is from */
  queue?: QueueId;
  batches: TaskBatch[];
  total: number;
  /** the page actually returned, which is not the one asked for if the queue drained meanwhile */
  page?: number;
  pageSize?: number;
}

export interface TasksQuery {
  queue: QueueId;
  states: BatchState[];
  typeIds: string[];
  page: number;
  pageSize: number;
}

export function fetchTasks(storeId: string, query: TasksQuery): Promise<TasksInfo> {
  return send<TasksInfo>("tasks", { storeId, ...query });
}

/**
 * Puts batches back in line (`Pending`) or takes them out of it (`Cancelled`). Nothing else can be
 * set: a batch marked Completed would stand for work that never happened, and one marked Running
 * would be waited on by nobody.
 */
export function setTaskState(storeId: string, queue: QueueId, batchIds: string[], state: "Pending" | "Cancelled"): Promise<{ changed: number }> {
  return send<{ changed: number }>("tasks-set-state", { storeId, queue, batchIds, state });
}

export function deleteTasks(storeId: string, queue: QueueId, batchIds: string[]): Promise<{ deleted: number }> {
  return send<{ deleted: number }>("tasks-delete", { storeId, queue, batchIds });
}

/** Deletes by state and type instead of by id, so clearing stays one call however long the list is.
    Both filters empty empties the whole queue. */
export function clearTasks(storeId: string, queue: QueueId, states: BatchState[], typeIds: string[] = []): Promise<{ done: boolean }> {
  return send<{ done: boolean }>("tasks-clear", { storeId, queue, states, typeIds });
}

export function setTaskThrottle(storeId: string, throttle: number): Promise<{ throttle: number }> {
  return send<{ throttle: number }>("tasks-throttle", { storeId, throttle });
}
