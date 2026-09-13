// The activity logs of one database: what is recorded, the entries themselves, and the statistics
// kept next to them. Every log describes its own columns and its own graphable series (UILogs.cs on
// the server), so nothing here names a log, a column or a statistic - adding one on the server adds
// it to the UI.

import { adminBase } from "./base";
import { send } from "./channel";

/** How a log value is stored, which is all the client needs to know to format it. */
export type LogDataType = "DateTime" | "TimeSpan" | "String" | "Integer" | "Double" | "Bytes";

/** How a statistic is drawn. Decided on the server from the statistic and the data type. */
export type SeriesKind = "count" | "sum" | "avgminmax" | "full" | "groups";

/** The buckets a statistic is kept in; the range picker chooses one. */
export type IntervalType = "Second" | "Minute" | "Hour" | "Day" | "Week" | "Month";

export interface LogColumn {
  key: string;
  name: string;
  dataType: LogDataType;
}

/** One graph a log can draw. `property` is null for the log's own entry count. */
export interface LogSeries {
  property: string | null;
  statistic: string;
  kind: SeriesKind;
  label: string;
  dataType: LogDataType;
}

export interface LogInfo {
  key: string;
  name: string;
  enabledLog: boolean;
  enabledStatistics: boolean;
  /** What the settings file holds: what this log records again after a restart. */
  savedLog: boolean;
  savedStatistics: boolean;
  firstRecordUtc: string | null;
  lastRecordUtc: string | null;
  logBytes: number;
  statisticsBytes: number;
  totalBytes: number;
  maxAgeInDays: number;
  maxSizeInMb: number;
  columns: LogColumn[];
  series: LogSeries[];
}

export interface LogsInfo {
  open: boolean;
  state: string;
  scansRecording: boolean;
  /** Queries faster than this are not recorded; 0 records every one of them. */
  minQueryDurationMs: number;
  savedMinQueryDurationMs: number;
  /** false when configuration decides the recording settings, so saving them here would not hold. */
  canSave: boolean;
  totalBytes: number;
  logs: LogInfo[];
}

export interface LogEntry {
  timestampUtc: string;
  values: Record<string, unknown>;
}

export interface LogPage {
  /** Entries in the range, or - when a search was made - entries in it that the search matched. */
  total: number;
  skip: number;
  take: number;
  /** true when a search narrowed the page, so `total` counts matches rather than entries. */
  searched: boolean;
  entries: LogEntry[];
}

export interface SeriesPoint {
  fromUtc: string;
  /** false for an interval nothing was recorded in: a gap, not a zero. */
  hasValue: boolean;
  value: number | null;
  min?: number | null;
  max?: number | null;
  sum?: number | null;
  count?: number | null;
  /** kind "groups" only: the count per value in this interval. */
  values?: Record<string, number>;
}

export interface SeriesSummary {
  total?: number;
  avg?: number;
  min?: number | null;
  max?: number | null;
  sum?: number;
  count?: number;
  groups?: { name: string; count: number }[];
}

export interface SeriesData {
  logKey: string;
  property: string | null;
  statistic: string;
  kind: SeriesKind;
  interval: IntervalType;
  fromUtc: string;
  toUtc: string;
  /** true when the range asked for reaches further back than the statistic keeps. */
  clamped: boolean;
  enabledStatistics: boolean;
  groups: string[];
  summary: SeriesSummary | null;
  points: SeriesPoint[];
}

export interface TraceEntry {
  timestampUtc: string;
  type: "Info" | "Warning" | "Error" | "Backup";
  text: string;
  details: string | null;
}

export interface StartupError {
  timeUtc: string | null;
  message: string;
  details: string | null;
}

export interface TraceInfo {
  open: boolean;
  entries: TraceEntry[];
  startupError: StartupError | null;
}

export interface ScanInfo {
  recording: boolean;
  open: boolean;
  hits: { name: string; count: number }[];
}

export function fetchLogsInfo(storeId: string): Promise<LogsInfo> {
  return send<LogsInfo>("logs-info", { storeId });
}

/**
 * A page of a log: the entries of a range, newest first, or the ones a search matches.
 *
 * The search is read on the server, which tests every record of the range against it - there is no
 * index behind it, so it is the range that decides what it costs, not the search. See
 * {@link searchHelp} for what a search may say.
 */
export function fetchLogPage(
  storeId: string,
  logKey: string,
  fromUtc: string | null,
  toUtc: string | null,
  skip: number,
  take: number,
  search?: string,
  caseSensitive?: boolean,
): Promise<LogPage> {
  return send<LogPage>("logs-extract", { storeId, logKey, fromUtc, toUtc, skip, take, search: search ?? null, caseSensitive: caseSensitive ?? false });
}

/** What a search may say, as the page shows it under the search box. */
export const searchHelp = [
  ["timeout", "an entry with this anywhere in it"],
  ["get*nodes", "* is any run of characters, ? exactly one"],
  ['"could not open"', "a phrase, since a space is otherwise two terms"],
  ["error -shutdown", "both hold: one anywhere, the other nowhere"],
  ["type:error", "one column, by its name or its key"],
] as const;

/**
 * Whether a value holds what was typed into a search or a filter field.
 *
 * The same rule the server searches by, so the filter row under the column headings and the
 * search box above the table agree: the text is looked for anywhere in the value, and `*` (any
 * run of characters) and `?` (exactly one) stand for what is not being typed out. Both sides are
 * lower-cased by the caller when case is not being told apart.
 */
export function matchesTerm(text: string, term: string): boolean {
  if (term.length === 0) return true;
  if (!/[*?]/.test(term)) return text.includes(term);
  // the pattern covers the whole of the text, so it is padded to mean "anywhere in it"
  return matchesWildcard(text, "*" + term + "*");
}

function matchesWildcard(text: string, pattern: string): boolean {
  let t = 0;
  let p = 0;
  let starP = -1; // the last * met, to come back to when the run after it does not fit
  let starT = 0;
  while (t < text.length) {
    if (p < pattern.length && (pattern[p] === "?" || pattern[p] === text[t])) {
      t++;
      p++;
    } else if (p < pattern.length && pattern[p] === "*") {
      starP = p++;
      starT = t;
    } else if (starP >= 0) {
      p = starP + 1;
      t = ++starT;
    } else {
      return false;
    }
  }
  while (p < pattern.length && pattern[p] === "*") p++;
  return p === pattern.length;
}

export function fetchSeries(
  storeId: string,
  logKey: string,
  series: LogSeries,
  interval: IntervalType,
  fromUtc: string,
  toUtc: string,
): Promise<SeriesData> {
  return send<SeriesData>("logs-series", {
    storeId,
    logKey,
    property: series.property,
    statistic: series.statistic,
    interval,
    fromUtc,
    toUtc,
  });
}

export function fetchTrace(storeId: string, take = 200): Promise<TraceInfo> {
  return send<TraceInfo>("logs-trace", { storeId, take });
}

/** Turns recording, statistics, or both on or off. Omitted switches are left alone. */
export function enableLog(storeId: string, logKey: string, change: { log?: boolean; statistics?: boolean }): Promise<{ log: boolean; statistics: boolean }> {
  return send<{ log: boolean; statistics: boolean }>("logs-enable", {
    storeId,
    logKey,
    log: change.log ?? null,
    statistics: change.statistics ?? null,
  });
}

/** Deletes recorded entries, statistics, or both; a null logKey covers every log. */
export function clearLog(storeId: string, logKey: string | null, what: { log: boolean; statistics: boolean }): Promise<{ cleared: boolean }> {
  return send<{ cleared: boolean }>("logs-clear", { storeId, logKey, log: what.log, statistics: what.statistics });
}

/** Re-aggregates the statistics from the log files, covering entries recorded while they were off. */
export function rebuildStatistics(storeId: string, logKey: string): Promise<{ rebuilt: boolean }> {
  return send<{ rebuilt: boolean }>("logs-rebuild-statistics", { storeId, logKey });
}

/** Leaves queries faster than this out of the query log; 0 records every one of them. */
export function setMinQueryDuration(storeId: string, ms: number): Promise<{ ms: number }> {
  return send<{ ms: number }>("logs-min-duration", { storeId, ms });
}

/**
 * Writes what every log is recording right now into the settings file, so a restart brings it back.
 * The database is not reopened: the switches are already live, this only makes them survive.
 */
export function saveLogSettings(storeId: string): Promise<{ saved: boolean; logs: number; recording: number }> {
  return send<{ saved: boolean; logs: number; recording: number }>("logs-save", { storeId });
}

/** Puts every switch back to what the settings file holds - the other half of saving. */
export function restoreLogSettings(storeId: string): Promise<{ restored: boolean; recording: number }> {
  return send<{ restored: boolean; recording: number }>("logs-restore", { storeId });
}

/**
 * Downloads a log as tab separated text: the range between the two bounds, or - with both left
 * null - the whole log. A search narrows the file to the entries matching it, the same ones the
 * table is showing. This is not a command but a file, so it goes to a route of its own and comes
 * back as an attachment the browser saves.
 */
export async function downloadLogTsv(
  storeId: string,
  logKey: string,
  fromUtc: string | null,
  toUtc: string | null,
  search?: string,
  caseSensitive?: boolean,
): Promise<void> {
  const response = await fetch(`${adminBase}/ui/log-tsv`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ storeId, logKey, fromUtc, toUtc, search: search ?? null, caseSensitive: caseSensitive ?? false }),
  });
  if (!response.ok) {
    let message = `The download failed (HTTP ${response.status}).`;
    try {
      const body = await response.json();
      if (typeof body?.error === "string") message = body.error;
    } catch {
      // not json, keep the default message
    }
    throw new Error(message);
  }
  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  try {
    const link = document.createElement("a");
    link.href = url;
    link.download = filenameOf(response.headers.get("content-disposition")) ?? `${logKey}-log.tsv`;
    link.click();
  } finally {
    URL.revokeObjectURL(url);
  }
}

function filenameOf(contentDisposition: string | null): string | null {
  const match = contentDisposition?.match(/filename="([^"]+)"/);
  return match ? match[1] : null;
}

export function fetchScans(storeId: string): Promise<ScanInfo> {
  return send<ScanInfo>("logs-scans", { storeId });
}

export function recordScans(storeId: string, enable: boolean): Promise<{ recording: boolean }> {
  return send<{ recording: boolean }>("logs-scans-record", { storeId, enable });
}
