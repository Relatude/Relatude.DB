// The activity logs of one database: what is recorded, the entries themselves, and the statistics
// kept next to them. Every log describes its own columns and its own graphable series (UILogs.cs on
// the server), so nothing here names a log, a column or a statistic - adding one on the server adds
// it to the UI.

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
  total: number;
  skip: number;
  take: number;
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

export function fetchLogPage(storeId: string, logKey: string, fromUtc: string | null, toUtc: string | null, skip: number, take: number): Promise<LogPage> {
  return send<LogPage>("logs-extract", { storeId, logKey, fromUtc, toUtc, skip, take });
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

export function fetchScans(storeId: string): Promise<ScanInfo> {
  return send<ScanInfo>("logs-scans", { storeId });
}

export function recordScans(storeId: string, enable: boolean): Promise<{ recording: boolean }> {
  return send<{ recording: boolean }>("logs-scans-record", { storeId, enable });
}
