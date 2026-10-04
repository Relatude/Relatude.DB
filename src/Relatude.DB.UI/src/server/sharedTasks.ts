// The server's board of long jobs (UISharedTasks.cs): every minimizable task is put there, so the
// other sessions - another tab, another user, this tab after a reload - see it in their top bar too.
//
//   shared-tasks         the board: what is running, and what finished in the last minute
//   shared-task-report   this tab saying where one of its tasks is (and that it is still here)
//   shared-task-cancel   stops one, whichever session started it
//   shared-task-job      the server side of one task alone: the job a command attached to it
//
// A task's id is made here, before anything is sent, so the dialog and the board agree on which task
// it is from the first request on: a command that starts work outliving the request is handed it as
// `taskId` and attaches its job to the task, which is what lets the task go on counting after the tab
// that started it is gone.

import type { ProgressController } from "../dialogs";
import { leave, send } from "./channel";

export type SharedStatus = "running" | "done" | "error" | "cancelled";

/** One task as the board lists it. */
export interface SharedTaskInfo {
  id: string;
  key: string | null;
  storeId: string | null;
  title: string;
  startedBy: string | null; // the signed in user, "localhost" under the bypass
  startedUtc: string;
  status: SharedStatus;
  label: string | null;
  done: number;
  total: number | null;
  meta: string | null;
  message: string | null;
  finishedUtc: string | null;
  cancellable: boolean;
}

/** Where a task stands, as this tab reports it. */
export interface SharedReport {
  id: string;
  key: string | null;
  title: string;
  status: SharedStatus;
  label: string;
  done: number;
  total: number | null;
  meta: string | null;
  message: string | null;
  seq: number; // counts this tab's reports on the task: one that arrives after a newer one is ignored
}

/** What the job behind a task says (see shared-task-job). */
export interface SharedJob {
  status: SharedStatus;
  label: string | null;
  done: number;
  total: number | null;
  meta: string | null;
  message: string | null;
}

/** A task id, made here; crypto.randomUUID needs a secure context, which an admin UI on plain http is not. */
export function newTaskId(): string {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function" && window.isSecureContext) return crypto.randomUUID();
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** Says where the task is. Answers whether another session asked for it to be cancelled. */
export async function reportSharedTask(report: SharedReport): Promise<boolean> {
  const answer = await send<{ cancelRequested?: boolean }>("shared-task-report", report);
  return answer?.cancelRequested === true;
}

/**
 * The last word on tasks this tab was running when it goes away (closed, reloaded): sent in a way that
 * outlives the page, so the board says at once that they stopped rather than wait for the silence. A
 * task whose job runs on the server goes on there, and the board goes on showing it.
 */
export function reportLeaving(reports: SharedReport[]): void {
  for (const report of reports) leave("shared-task-report", { ...report, left: true });
}

export function cancelSharedTask(id: string): Promise<unknown> {
  return send("shared-task-cancel", { id });
}

/**
 * Waits for the job a command attached to this task - a truncation, a backup - showing what it says
 * as it goes. Resolves with its last word when it is done, throws when it failed. No job at all (a
 * command that ran its work there and then, or a server that does not attach) counts as done.
 */
export async function waitForSharedJob(ctl: ProgressController, everyMs = 1000): Promise<SharedJob | null> {
  if (!ctl.taskId) return null;
  for (;;) {
    if (ctl.signal.aborted) throw new DOMException("Aborted", "AbortError");
    const job = await send<SharedJob | null>("shared-task-job", { id: ctl.taskId });
    if (!job) return null;
    if (job.status === "running") {
      ctl.set({ label: job.label ?? "", ...(job.total != null ? { done: job.done, total: job.total } : {}), meta: job.meta });
      await new Promise((r) => setTimeout(r, everyMs));
      continue;
    }
    if (job.status === "error") throw new Error(job.message ?? "It failed.");
    if (job.status === "cancelled") throw new DOMException("Aborted", "AbortError");
    return job;
  }
}
