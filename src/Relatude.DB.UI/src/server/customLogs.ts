// The custom logs of one database: logs defined here, in the admin UI, rather than in the database's
// code (UICustomLogs.cs on the server). A log is its definition - a name, a file layout, limits, and
// a list of columns each with a data type and the statistics it keeps - saved as a json file in the
// database's log folder, and an application records into it with store.CustomLogs.Record(...).
//
// Reading a custom log is reading a log like any other: the same entries, the same search and the
// same statistics as the system logs (server/logs.ts), through commands of their own.

import { adminBase } from "./base";
import { send } from "./channel";
import type { LogColumn, LogDataType, LogSeries } from "./logs";

export type FileInterval = "Minute" | "Hour" | "Day" | "Month";
export type DayOfWeek = "Sunday" | "Monday" | "Tuesday" | "Wednesday" | "Thursday" | "Friday" | "Saturday";
export type StatisticsType =
  | "Count"
  | "Sum"
  | "AvgMinMax"
  | "CountSumAvgMinMax"
  | "UniqueCountWithValues"
  | "UniqueCountHashedValues"
  | "UniqueCountEstimate";

export const fileIntervals: FileInterval[] = ["Minute", "Hour", "Day", "Month"];
export const daysOfWeek: DayOfWeek[] = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
export const dataTypes: LogDataType[] = ["String", "Integer", "Double", "DateTime", "TimeSpan", "Bytes"];

/** What a data type is called on the page: what it holds, not what .NET calls it. */
export const dataTypeLabel: Record<LogDataType, string> = {
  String: "Text",
  Integer: "Whole number",
  Double: "Decimal number",
  DateTime: "Date and time",
  TimeSpan: "Duration",
  Bytes: "Bytes",
};

export interface StatisticInfo {
  type: StatisticsType;
  label: string;
  short: string;
  help: string;
  /** Only numbers can be added up and averaged. */
  numericOnly?: boolean;
  /** A breakdown or a unique count of bytes has nothing to tell apart. */
  notBytes?: boolean;
}

/** Every statistic a column can keep, in the order the editor offers them. */
export const statisticInfos: StatisticInfo[] = [
  { type: "Count", label: "Count", short: "count", help: "How many entries have a value in this column, per interval." },
  { type: "Sum", label: "Total", short: "total", help: "The values added up, per interval.", numericOnly: true },
  { type: "AvgMinMax", label: "Average, min, max", short: "avg", help: "The average of the values per interval, with the lowest and highest.", numericOnly: true },
  {
    type: "CountSumAvgMinMax",
    label: "Count, total, average, min, max",
    short: "all",
    help: "Everything above in one statistic: how many, their total, average, lowest and highest.",
    numericOnly: true,
  },
  {
    type: "UniqueCountWithValues",
    label: "Count per value",
    short: "by value",
    help: "How often each value occurs, per interval. Exact, and meant for columns with few distinct values - under a hundred or so.",
    notBytes: true,
  },
  {
    type: "UniqueCountHashedValues",
    label: "Unique count",
    short: "unique",
    help: "How many different values there were per interval. Exact up to about ten thousand values.",
    notBytes: true,
  },
  {
    type: "UniqueCountEstimate",
    label: "Unique count, estimated",
    short: "≈ unique",
    help: "How many different values there were per interval, within about one percent, however many there are.",
    notBytes: true,
  },
];

export function statisticApplies(type: StatisticsType, dataType: LogDataType): boolean {
  const info = statisticInfos.find((s) => s.type === type);
  if (!info) return false;
  if (info.numericOnly && dataType !== "Integer" && dataType !== "Double") return false;
  if (info.notBytes && dataType === "Bytes") return false;
  return true;
}

/**
 * How far back a statistic of this resolution reaches, per bucket size: the numbers the logger
 * multiplies the resolution by (StatisticsBase.getDefaultNoIntervals).
 */
export function retentionOf(resolution: number): { interval: string; count: number; span: string }[] {
  const r = Math.max(1, Math.round(resolution));
  const span = (n: number, unit: string, per: number, bigger: string) => (n >= per * 2 ? `${Math.round((n / per) * 10) / 10} ${bigger}` : `${n} ${unit}`);
  return [
    { interval: "Second", count: 60 * r, span: span(60 * r, "seconds", 60, "minutes") },
    { interval: "Minute", count: 60 * r, span: span(60 * r, "minutes", 60, "hours") },
    { interval: "Hour", count: 48 * r, span: span(48 * r, "hours", 24, "days") },
    { interval: "Day", count: 60 * r, span: span(60 * r, "days", 30, "months") },
    { interval: "Week", count: 52 * r, span: span(52 * r, "weeks", 52, "years") },
    { interval: "Month", count: 60 * r, span: span(60 * r, "months", 12, "years") },
  ];
}

export interface StatisticDefinition {
  statisticsType: StatisticsType;
  resolution: number;
}

export interface ColumnDefinition {
  key: string;
  name: string;
  dataType: LogDataType;
  statistics: StatisticDefinition[];
}

/** A log's definition, as the editor edits it: the columns are a list, in the order they are shown. */
export interface LogDefinition {
  key: string;
  name: string;
  description: string;
  enableLog: boolean;
  enableStatistics: boolean;
  enableLogTextFormat: boolean;
  fileInterval: FileInterval;
  compressed: boolean;
  maxAgeOfLogFilesInDays: number;
  maxTotalSizeOfLogFilesInMb: number;
  resolutionRowStats: number;
  firstDayOfWeek: DayOfWeek;
  properties: ColumnDefinition[];
}

export interface CustomLogSeries extends LogSeries {
  resolution: number;
}

export interface CustomLogSummary {
  key: string;
  name: string;
  description: string;
  enabledLog: boolean;
  enabledStatistics: boolean;
  enabledText: boolean;
  compressed: boolean;
  fileInterval: FileInterval;
  maxAgeInDays: number;
  maxSizeInMb: number;
  resolutionRowStats: number;
  firstDayOfWeek: DayOfWeek;
  firstRecordUtc: string | null;
  lastRecordUtc: string | null;
  logBytes: number;
  statisticsBytes: number;
  totalBytes: number;
  /** Entries recorded in the last day, from the row statistic; null while statistics are off. */
  entriesLastDay: number | null;
  /** The last 24 hours, one count per hour, oldest first; null while statistics are off. */
  activity: number[] | null;
  columns: LogColumn[];
  series: CustomLogSeries[];
}

export interface CustomLogLoadError {
  fileKey: string;
  message: string;
}

export interface CustomLogsInfo {
  open: boolean;
  state: string;
  /** The IO provider the log folder is in, for downloading a log's files as they are. */
  ioId: string | null;
  /** The keys of the database's activity logs, which a log defined here cannot take. */
  reservedKeys: string[];
  totalBytes: number;
  loadErrors: CustomLogLoadError[];
  logs: CustomLogSummary[];
}

export interface DefinitionPlan {
  valid: boolean;
  error: string | null;
  changed: boolean;
  movesEntries?: boolean;
  discardsStatistics?: boolean;
  canRebuildStatistics?: boolean;
  /** What the change does to what the log has recorded, a sentence each. */
  notes: string[];
}

export interface SaveResult {
  created: boolean;
  key: string;
  changed: boolean;
  entriesMoved: number;
  statisticsRebuilt: boolean;
  notes: string[];
}

export interface CustomLogFile {
  fileKey: string;
  kind: "entries" | "text" | "statistics" | "statistics-backup" | "settings" | "left-over";
  size: number;
}

export interface Distribution {
  count: number;
  min: number;
  max: number;
  mean: number;
  sum: number;
  stdDev: number;
  percentiles: { p: number; value: number }[];
  histogram: { from: number; to: number; count: number }[];
}

export interface Breakdown {
  distinct: number;
  /** More distinct values than were tracked: `distinct` is a floor. */
  distinctCapped: boolean;
  total: number;
  top: { value: string; count: number }[];
  other: number;
}

export interface AnalyseResult {
  logKey: string;
  property: string;
  dataType: LogDataType;
  scanned: number;
  matched: number;
  withValue: number;
  /** The read stopped at `cap` entries: the answer covers the newest of the range, not all of it. */
  truncated: boolean;
  cap: number;
  firstUtc: string | null;
  lastUtc: string | null;
  distribution: Distribution | null;
  breakdown: Breakdown | null;
}

export function fetchDefinition(storeId: string, logKey: string): Promise<{ settings: LogDefinition; json: string }> {
  return send("custom-logs-definition", { storeId, logKey });
}

/** What saving the definition would do, without doing it; an unusable definition is an answer, not an error. */
export function planDefinition(storeId: string, definition: LogDefinition | string, isNew: boolean): Promise<DefinitionPlan> {
  return send<DefinitionPlan>("custom-logs-plan", { storeId, isNew, ...(typeof definition === "string" ? { json: definition } : { settings: definition }) });
}

export function saveDefinition(storeId: string, definition: LogDefinition | string, isNew: boolean, rebuildStatistics = false): Promise<SaveResult> {
  return send<SaveResult>("custom-logs-save", {
    storeId,
    isNew,
    rebuildStatistics,
    ...(typeof definition === "string" ? { json: definition } : { settings: definition }),
  });
}

export function enableCustomLog(storeId: string, logKey: string, change: { log?: boolean; statistics?: boolean }): Promise<{ log: boolean; statistics: boolean }> {
  return send("custom-logs-enable", { storeId, logKey, log: change.log ?? null, statistics: change.statistics ?? null });
}

export function deleteCustomLog(storeId: string, logKey: string, deleteRecorded: boolean): Promise<{ deleted: boolean }> {
  return send("custom-logs-delete", { storeId, logKey, deleteRecorded });
}

/** Deletes entries (every one, or the files entirely older than a moment), statistics, or both. */
export function clearCustomLog(
  storeId: string,
  logKey: string,
  what: { entries: boolean; statistics: boolean; olderThanUtc?: string | null },
): Promise<{ cleared: boolean; logBytes: number; statisticsBytes: number }> {
  return send("custom-logs-clear", { storeId, logKey, entries: what.entries, statistics: what.statistics, olderThanUtc: what.olderThanUtc ?? null });
}

export function rebuildCustomStatistics(storeId: string, logKey: string): Promise<{ rebuilt: boolean }> {
  return send("custom-logs-rebuild-statistics", { storeId, logKey });
}

/** Records one entry; the values are converted to their columns' types on the server. */
export function recordCustomEntry(storeId: string, logKey: string, values: Record<string, unknown>, timestampUtc: string | null): Promise<{ recorded: boolean; timestampUtc: string }> {
  return send("custom-logs-record", { storeId, logKey, values, timestampUtc });
}

/** Made-up entries for trying a log out, spread over the stretch of time that ends now. */
export function addSampleEntries(storeId: string, logKey: string, count: number, spanMs: number): Promise<{ recorded: number; rebuilt: boolean; written: boolean }> {
  return send("custom-logs-sample", { storeId, logKey, count, spanMs });
}

export function fetchCustomLogFiles(storeId: string, logKey: string): Promise<{ ioId: string | null; files: CustomLogFile[] }> {
  return send("custom-logs-files", { storeId, logKey });
}

export function flushCustomLogs(storeId: string): Promise<{ flushed: boolean }> {
  return send("custom-logs-flush", { storeId });
}

/** Reads every settings file again, for definitions changed on disk while the database runs. */
export function reloadCustomLogs(storeId: string): Promise<{ reloaded: boolean; logs: number; errors: number }> {
  return send("custom-logs-reload", { storeId });
}

export function readBrokenDefinition(storeId: string, fileKey: string): Promise<{ text: string }> {
  return send("custom-logs-broken-read", { storeId, fileKey });
}

export function repairBrokenDefinition(storeId: string, fileKey: string, json: string): Promise<{ repaired: boolean }> {
  return send("custom-logs-broken-repair", { storeId, fileKey, json });
}

export function deleteBrokenDefinition(storeId: string, fileKey: string): Promise<{ deleted: boolean }> {
  return send("custom-logs-broken-delete", { storeId, fileKey });
}

/** How one column's values are spread over a range, read from the entries themselves. */
export function analyseColumn(
  storeId: string,
  logKey: string,
  property: string,
  range: { lastMs?: number | null; fromUtc?: string | null; toUtc?: string | null },
  search: string | null,
  caseSensitive: boolean,
  maxEntries: number,
): Promise<AnalyseResult> {
  return send("custom-logs-analyse", {
    storeId,
    logKey,
    property,
    lastMs: range.lastMs ?? null,
    fromUtc: range.fromUtc ?? null,
    toUtc: range.toUtc ?? null,
    search,
    caseSensitive,
    maxEntries,
  });
}

export type ExportFormat = "tsv" | "csv" | "jsonl";

/**
 * Downloads a log's entries as a file: the range between the two bounds, or - with both null - the
 * whole log, narrowed to what a search matches when there is one. A file rather than a command, so
 * it goes to a route of its own and comes back as an attachment the browser saves.
 */
export async function downloadCustomLog(
  storeId: string,
  logKey: string,
  format: ExportFormat,
  fromUtc: string | null,
  toUtc: string | null,
  search: string | null,
  caseSensitive: boolean,
): Promise<void> {
  const response = await fetch(`${adminBase}/ui/custom-log-export`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ storeId, logKey, fromUtc, toUtc, search, caseSensitive, format }),
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
  const match = response.headers.get("content-disposition")?.match(/filename="([^"]+)"/);
  saveBlob(blob, match ? match[1] : `${logKey}-log.${format}`);
}

/** Hands something made in the page to the browser as a file to save. */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  try {
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    link.click();
  } finally {
    // revoked on the next turn: some browsers start reading the url only after the click returns
    setTimeout(() => URL.revokeObjectURL(url), 0);
  }
}

export function saveText(text: string, fileName: string, type = "text/plain"): void {
  saveBlob(new Blob([text], { type: type + ";charset=utf-8" }), fileName);
}

/** The url of a log file as it is on disk, through the admin API's download route. */
export function rawFileUrl(storeId: string, ioId: string, fileKey: string): string {
  return `${adminBase}/maintenance/download-file?storeId=${storeId}&ioId=${ioId}&fileName=${encodeURIComponent(fileKey)}`;
}

// ---- definitions as files ----

/**
 * A definition as its settings file holds it: the columns an object keyed by column, the names as
 * the server writes them. Written here rather than asked for, so the json on the page follows the
 * form as it is edited - the server writes the same shape (LogSettings.ToJson).
 */
export function definitionToFileJson(d: LogDefinition): string {
  const properties: Record<string, unknown> = {};
  for (const column of d.properties) {
    properties[column.key] = {
      Name: column.name,
      DataType: column.dataType,
      Statistics: column.statistics.map((s) => ({ StatisticsType: s.statisticsType, Resolution: s.resolution })),
    };
  }
  return JSON.stringify(
    {
      Key: d.key,
      Name: d.name,
      Description: d.description,
      Properties: properties,
      FileInterval: d.fileInterval,
      EnableLog: d.enableLog,
      EnableStatistics: d.enableStatistics,
      EnableLogTextFormat: d.enableLogTextFormat,
      ResolutionRowStats: d.resolutionRowStats,
      FirstDayOfWeek: d.firstDayOfWeek,
      MaxAgeOfLogFilesInDays: d.maxAgeOfLogFilesInDays,
      MaxTotalSizeOfLogFilesInMb: d.maxTotalSizeOfLogFilesInMb,
      Compressed: d.compressed,
    },
    null,
    2,
  );
}

/**
 * The other way: a settings file read into the form. Names are matched without regard to case and
 * enums by name or number, the way the server reads them; anything left out gets the default a new
 * log would have. Throws with what is wrong when the text is no settings file at all - whether the
 * settings are usable is the server's to say (planDefinition).
 */
export function definitionFromFileJson(text: string): LogDefinition {
  const parsed = JSON.parse(text) as unknown;
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) throw new Error("A settings file is one json object.");
  const get = (o: Record<string, unknown>, name: string): unknown => {
    const key = Object.keys(o).find((k) => k.toLowerCase() === name.toLowerCase());
    return key === undefined ? undefined : o[key];
  };
  const o = parsed as Record<string, unknown>;
  const text_ = (v: unknown, fallback = "") => (typeof v === "string" ? v : fallback);
  const bool = (v: unknown, fallback: boolean) => (typeof v === "boolean" ? v : fallback);
  const num = (v: unknown, fallback: number) => (typeof v === "number" && isFinite(v) ? v : fallback);
  const enumOf = <T extends string>(v: unknown, values: readonly T[], fallback: T, numbered?: readonly T[]): T => {
    if (typeof v === "string") return values.find((x) => x.toLowerCase() === v.toLowerCase()) ?? fallback;
    if (typeof v === "number" && numbered && numbered[v] !== undefined) return numbered[v];
    return fallback;
  };
  // the .NET enum order, for files that write them as numbers
  const intervalOrder: FileInterval[] = ["Minute", "Hour", "Day", "Month"];
  const dayOrder: DayOfWeek[] = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
  const typeOrder: LogDataType[] = ["DateTime", "TimeSpan", "String", "Integer", "Double", "Bytes"];
  const statisticOrder: StatisticsType[] = [
    "Count",
    "Sum",
    "AvgMinMax",
    "CountSumAvgMinMax",
    "UniqueCountWithValues",
    "UniqueCountHashedValues",
    "UniqueCountEstimate",
  ];
  const columns: ColumnDefinition[] = [];
  const properties = get(o, "Properties");
  if (properties && typeof properties === "object" && !Array.isArray(properties)) {
    for (const [key, value] of Object.entries(properties as Record<string, unknown>)) {
      const p = (value && typeof value === "object" ? value : {}) as Record<string, unknown>;
      const statistics = get(p, "Statistics");
      columns.push({
        key,
        name: text_(get(p, "Name")),
        dataType: enumOf(get(p, "DataType"), typeOrder, "String", typeOrder),
        statistics: Array.isArray(statistics)
          ? statistics
              .filter((s): s is Record<string, unknown> => !!s && typeof s === "object")
              .map((s) => ({
                statisticsType: enumOf(get(s, "StatisticsType"), statisticOrder, "Count", statisticOrder),
                resolution: Math.max(1, Math.round(num(get(s, "Resolution"), 3))),
              }))
          : [],
      });
    }
  }
  return {
    key: text_(get(o, "Key")),
    name: text_(get(o, "Name")),
    description: text_(get(o, "Description")),
    enableLog: bool(get(o, "EnableLog"), true),
    enableStatistics: bool(get(o, "EnableStatistics"), true),
    enableLogTextFormat: bool(get(o, "EnableLogTextFormat"), false),
    fileInterval: enumOf(get(o, "FileInterval"), intervalOrder, "Day", intervalOrder),
    compressed: bool(get(o, "Compressed"), false),
    maxAgeOfLogFilesInDays: num(get(o, "MaxAgeOfLogFilesInDays"), 100),
    maxTotalSizeOfLogFilesInMb: num(get(o, "MaxTotalSizeOfLogFilesInMb"), 100),
    resolutionRowStats: num(get(o, "ResolutionRowStats"), 10),
    firstDayOfWeek: enumOf(get(o, "FirstDayOfWeek"), dayOrder, "Monday", dayOrder),
    properties: columns,
  };
}

/** A key made from a name: what the editor suggests until the key is typed into. */
export function keyFromName(name: string): string {
  return name
    .normalize("NFKD")
    .replace(/\p{M}/gu, "") // the accents NFKD split off: "Ordrer på nett" becomes "ordrer-pa-nett"
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 64);
}

/** Why a key cannot be used, or null; the same rule the server checks (CustomLogs.CheckNewKey). */
export function keyProblem(key: string, taken: string[], reserved: string[]): string | null {
  if (key.trim().length === 0) return "A log needs a key.";
  if (!/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(key)) return "Letters, digits, '-' and '_', starting with a letter or a digit, at most 64.";
  const lower = key.toLowerCase();
  if (reserved.some((r) => r.toLowerCase() === lower)) return `"${key}" is the key of one of the database's activity logs.`;
  if (taken.some((t) => t.toLowerCase() === lower)) return `There is already a log with the key "${key}".`;
  return null;
}

/** Why a column key cannot be used, or null. */
export function columnKeyProblem(key: string, others: string[]): string | null {
  if (key.trim().length === 0) return "A column needs a key.";
  if (key !== key.trim()) return "No spaces at the start or the end.";
  if (others.some((o) => o.toLowerCase() === key.toLowerCase())) return "Two columns have this key (keys ignore case).";
  return null;
}

export function blankDefinition(): LogDefinition {
  return {
    key: "",
    name: "",
    description: "",
    enableLog: true,
    enableStatistics: true,
    enableLogTextFormat: false,
    fileInterval: "Day",
    compressed: false,
    maxAgeOfLogFilesInDays: 30,
    maxTotalSizeOfLogFilesInMb: 100,
    resolutionRowStats: 3,
    firstDayOfWeek: "Monday",
    properties: [],
  };
}

const stat = (statisticsType: StatisticsType, resolution = 3): StatisticDefinition => ({ statisticsType, resolution });

/** Starting points for a new log: the columns a log of that kind usually has, with statistics to match. */
export const templates: { id: string; name: string; description: string; make: () => LogDefinition }[] = [
  {
    id: "blank",
    name: "Blank log",
    description: "No columns yet: add the ones the log is for.",
    make: () => blankDefinition(),
  },
  {
    id: "requests",
    name: "Web requests",
    description: "Path, status and duration of the requests a site answers.",
    make: () => ({
      ...blankDefinition(),
      key: "requests",
      name: "Web requests",
      description: "The requests the site answers: what was asked for, how it went and how long it took.",
      fileInterval: "Hour",
      maxAgeOfLogFilesInDays: 14,
      properties: [
        { key: "method", name: "Method", dataType: "String", statistics: [stat("UniqueCountWithValues")] },
        { key: "path", name: "Path", dataType: "String", statistics: [stat("UniqueCountEstimate")] },
        { key: "status", name: "Status", dataType: "Integer", statistics: [stat("UniqueCountWithValues")] },
        { key: "duration", name: "Duration (ms)", dataType: "Double", statistics: [stat("CountSumAvgMinMax")] },
        { key: "bytes", name: "Bytes sent", dataType: "Integer", statistics: [stat("Sum")] },
        { key: "user", name: "User", dataType: "String", statistics: [stat("UniqueCountHashedValues")] },
      ],
    }),
  },
  {
    id: "events",
    name: "Business events",
    description: "Orders, sign-ups and other things worth counting, with an amount.",
    make: () => ({
      ...blankDefinition(),
      key: "events",
      name: "Business events",
      description: "Things that happen in the application that are worth counting over time.",
      maxAgeOfLogFilesInDays: 365,
      resolutionRowStats: 10,
      properties: [
        { key: "event", name: "Event", dataType: "String", statistics: [stat("UniqueCountWithValues", 10)] },
        { key: "amount", name: "Amount", dataType: "Double", statistics: [stat("CountSumAvgMinMax", 10)] },
        { key: "customer", name: "Customer", dataType: "String", statistics: [stat("UniqueCountEstimate", 10)] },
        { key: "reference", name: "Reference", dataType: "String", statistics: [] },
      ],
    }),
  },
  {
    id: "errors",
    name: "Errors and warnings",
    description: "Problems the application runs into, by level and where they came from.",
    make: () => ({
      ...blankDefinition(),
      key: "errors",
      name: "Errors and warnings",
      description: "Problems the application runs into.",
      maxAgeOfLogFilesInDays: 60,
      properties: [
        { key: "level", name: "Level", dataType: "String", statistics: [stat("UniqueCountWithValues")] },
        { key: "source", name: "Source", dataType: "String", statistics: [stat("UniqueCountWithValues")] },
        { key: "message", name: "Message", dataType: "String", statistics: [] },
        { key: "details", name: "Details", dataType: "String", statistics: [] },
      ],
    }),
  },
  {
    id: "jobs",
    name: "Background jobs",
    description: "Scheduled work: which job, whether it succeeded, how long it ran.",
    make: () => ({
      ...blankDefinition(),
      key: "jobs",
      name: "Background jobs",
      description: "Scheduled and background work, one entry per run.",
      properties: [
        { key: "job", name: "Job", dataType: "String", statistics: [stat("UniqueCountWithValues")] },
        { key: "outcome", name: "Outcome", dataType: "String", statistics: [stat("UniqueCountWithValues")] },
        { key: "duration", name: "Duration", dataType: "TimeSpan", statistics: [stat("Count")] },
        { key: "items", name: "Items handled", dataType: "Integer", statistics: [stat("CountSumAvgMinMax")] },
      ],
    }),
  },
];

/** How a value of a column is written in C#, for the recording example on the page. */
export function csharpSample(dataType: LogDataType, key: string): string {
  const k = key.toLowerCase();
  const named = (...hints: string[]) => hints.some((h) => k.includes(h));
  switch (dataType) {
    case "Integer":
      if (named("status", "code")) return "200";
      if (named("bytes", "size", "length")) return "2048";
      return "3";
    case "Double":
      if (named("amount", "price", "total")) return "249.90";
      return "12.5";
    case "DateTime":
      return "DateTime.UtcNow";
    case "TimeSpan":
      return "TimeSpan.FromMilliseconds(250)";
    case "Bytes":
      return "new byte[] { 1, 2, 3 }";
    default: {
      // what a column with this name usually holds, so the example reads like the real call
      const text = named("method")
        ? "GET"
        : named("path", "url")
          ? "/products"
          : named("level")
            ? "Error"
            : named("outcome", "result", "success")
              ? "Success"
              : named("user", "customer", "account")
                ? "user-42"
                : named("event")
                  ? "order"
                  : named("message", "text", "details")
                    ? "Something happened"
                    : "text";
      return `"${text}"`;
    }
  }
}

/** The application code that records into a log, written for its columns. */
export function recordingCode(d: { key: string; properties: { key: string; dataType: LogDataType }[] }): string {
  const key = d.key || "my-log";
  const columns = d.properties.length > 0 ? d.properties : [{ key: "message", dataType: "String" as LogDataType }];
  const pairs = columns.map((c, i) => `    ("${c.key}", ${csharpSample(c.dataType, c.key)})${i < columns.length - 1 ? "," : ");"}`);
  const pascal = (k: string) => k.replace(/(^|[-_])([a-z0-9])/gi, (_, __, ch: string) => ch.toUpperCase()).replace(/[^A-Za-z0-9]/g, "");
  const props = columns.map((c) => `    ${pascal(c.key) || "Value"} = ${csharpSample(c.dataType, c.key)},`);
  return [
    `// Record into the "${key}" log from the application. A value is converted to its column's`,
    `// type, and a log that is turned off (or not defined) records nothing.`,
    `store.CustomLogs.Record("${key}",`,
    ...pairs,
    ``,
    `// ...or from an object: properties are matched to the columns without regard to case`,
    `store.CustomLogs.RecordObject("${key}", new {`,
    ...props,
    `});`,
    ``,
    `// ...or with a time of its own (UTC) and a dictionary of values`,
    `store.CustomLogs.Record("${key}", new Dictionary<string, object?> {`,
    `    ["${columns[0].key}"] = ${csharpSample(columns[0].dataType, columns[0].key)},`,
    `}, timestampUtc: DateTime.UtcNow.AddMinutes(-5));`,
  ].join("\n");
}
