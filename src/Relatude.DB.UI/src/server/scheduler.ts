// The scheduler of one database: its schedules (LocalSettings.Schedules - settings, saved on THIS SERVER
// like any other), the jobs it can run, and how the recent runs went. See UIScheduler.cs.

import { send } from "./channel";

/** Mirrors ScheduleUnit. */
export type ScheduleUnit = "Seconds" | "Minutes" | "Hours" | "Days" | "Weeks" | "Months";
/** Mirrors JobRunStatus. */
export type RunStatus = "Queued" | "Running" | "Succeeded" | "Failed" | "Cancelled" | "Skipped" | "Interrupted";
/** Mirrors JobTrigger: its schedule on time, its schedule late, or someone. */
export type RunTrigger = "Schedule" | "Missed" | "Manual";

export interface JobRun {
  runId: string;
  jobName: string;
  argument: string | null;
  scheduleId: string | null;
  scheduleName: string | null;
  trigger: RunTrigger;
  status: RunStatus;
  /** when it was queued - or, for a skipped one, when it was skipped */
  queuedUtc: string;
  /** the time its schedule said; null when started by hand */
  dueUtc: string | null;
  startedUtc: string | null;
  finishedUtc: string | null;
  durationMs: number | null;
  /** what the job said about how it went, or why it was skipped */
  message: string | null;
  error: string | null;
  /** skipped this many times in a row for the same reason */
  times: number;
}

/** The fields of a schedule, as the settings hold them. */
export interface ScheduleFields {
  job: string | null;
  name: string | null;
  argument: string | null;
  enabled: boolean;
  every: number;
  unit: ScheduleUnit;
  /** HH:mm: the time of day, or for seconds/minutes/hours the first run of the day */
  at: string | null;
  /** seconds/minutes/hours: the last start of the day */
  until: string | null;
  /** "Mon-Fri", "Mon,Wed,Fri"; empty is every day */
  days: string | null;
  /** months: 1-31, 31 being the last day */
  dayOfMonth: number | null;
  /** IANA id; empty is the server's own */
  timeZone: string | null;
  runMissed: boolean;
}

export interface ScheduleView extends ScheduleFields {
  id: string;
  /** "Every day at 02:00" */
  summary: string;
  /** what stops it from running as written, or null */
  problem: string | null;
  nextUtc: string | null;
  /** the next time on the schedule's own clock, "Mon 13 Oct 02:00" */
  nextAt: string | null;
  /** a run of it is queued or running */
  active: boolean;
  lastRun: JobRun | null;
  /** THIS SERVER holds it and SHARED does not */
  addedHere: boolean;
  /** fields of a SHARED schedule changed on THIS SERVER */
  changedHere: string[];
}

export interface JobView {
  name: string;
  description: string | null;
  /** what the argument means to it; null when it takes none */
  argumentHelp: string | null;
  builtIn: boolean;
  /** schedules naming it */
  schedules: number;
  /** runs of it queued or running */
  active: number;
  lastRun: JobRun | null;
}

export interface SchedulerInfo {
  /** the jobs and the runs live in the open database; a closed one still lists its schedules */
  open: boolean;
  state: string;
  /** LocalSettings.AutoDequeTasks: off, runs are queued and nothing takes them */
  autoDequeue: boolean;
  nowUtc: string;
  serverTimeZone: { id: string; name: string; offsetMinutes: number };
  settingsFile: string;
  dataFile: string | null;
  configSection: string | null;
  /** the list is decided outside the settings files, and read-only here */
  locked: "appsettings" | "code" | null;
  schedules: ScheduleView[];
  /** SHARED schedules removed on THIS SERVER */
  removedHere: string[];
  jobs: JobView[];
  /** newest first */
  runs: JobRun[];
}

export interface Rejected {
  path: string;
  reason: string;
}

export interface SchedulePreview {
  summary: string;
  problem: string | null;
  /** the time zone the times are in, by IANA id where it has one */
  zone: string | null;
  upcoming: { utc: string; at: string | null }[];
}

/** Fields by their settings names; a field left out is left as it is. */
export type ScheduleValues = Partial<{ [K in keyof ScheduleFields]: ScheduleFields[K] }>;

export function fetchScheduler(storeId: string): Promise<SchedulerInfo> {
  return send<SchedulerInfo>("scheduler", { storeId });
}

export function addSchedule(storeId: string, values: ScheduleValues): Promise<{ id: string; rejected: Rejected[] }> {
  return send("scheduler-add", { storeId, values });
}

export function saveSchedule(storeId: string, id: string, values: ScheduleValues): Promise<{ id: string; rejected: Rejected[] }> {
  return send("scheduler-save", { storeId, id, values });
}

export function removeSchedule(storeId: string, id: string): Promise<{ removed: string }> {
  return send("scheduler-remove", { storeId, id });
}

/** A schedule's job with its argument, now; or a job by name with an argument of its own. */
export function runJob(storeId: string, what: { scheduleId: string } | { job: string; argument: string | null }): Promise<{ run: JobRun }> {
  return send("scheduler-run", { storeId, ...what });
}

export function cancelRun(storeId: string, runId: string): Promise<{ cancelled: boolean }> {
  return send("scheduler-cancel", { storeId, runId });
}

export function previewSchedule(storeId: string, values: ScheduleValues): Promise<SchedulePreview> {
  return send("scheduler-preview", { storeId, values });
}
