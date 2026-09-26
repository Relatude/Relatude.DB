// The time range a custom log's page is looking at, shared by its graphs, its entries and its
// analysis, and the formatting its values need. A range is either "the last so many" - which moves
// with the clock, so a page left open keeps showing the latest - or two fixed moments, which is what
// picking an interval on a graph, or typing a range in, gives.

import type { IntervalType, LogDataType } from "./server/logs";
import { formatBytes, formatCount, formatDateTime } from "./format";

export interface RangePreset {
  id: string;
  label: string;
  ms: number;
  /** The bucket its graphs are drawn in unless another one is chosen. */
  interval: IntervalType;
}

/**
 * The ranges offered, each with a bucket size that gives it a readable number of points: statistics
 * are only kept for so many intervals of each size, so a longer range is not the same graph zoomed
 * out but a coarser bucket.
 */
export const rangePresets: RangePreset[] = [
  { id: "5m", label: "5 minutes", ms: 5 * 60_000, interval: "Second" },
  { id: "1h", label: "1 hour", ms: 3_600_000, interval: "Minute" },
  { id: "6h", label: "6 hours", ms: 6 * 3_600_000, interval: "Minute" },
  { id: "24h", label: "24 hours", ms: 24 * 3_600_000, interval: "Hour" },
  { id: "7d", label: "7 days", ms: 7 * 86_400_000, interval: "Hour" },
  { id: "30d", label: "30 days", ms: 30 * 86_400_000, interval: "Day" },
  { id: "90d", label: "90 days", ms: 90 * 86_400_000, interval: "Day" },
  { id: "12m", label: "12 months", ms: 365 * 86_400_000, interval: "Week" },
];

export const intervals: IntervalType[] = ["Second", "Minute", "Hour", "Day", "Week", "Month"];
const intervalMs: Record<IntervalType, number> = {
  Second: 1000,
  Minute: 60_000,
  Hour: 3_600_000,
  Day: 86_400_000,
  Week: 7 * 86_400_000,
  Month: 30 * 86_400_000,
};

export type LogRange =
  | { kind: "last"; presetId: string }
  /** Two fixed moments, [from, to). `label` names where they came from, a picked interval say. */
  | { kind: "between"; fromUtc: string; toUtc: string; label?: string };

export const defaultRange: LogRange = { kind: "last", presetId: "24h" };

export function presetOf(range: LogRange): RangePreset | null {
  return range.kind === "last" ? (rangePresets.find((p) => p.id === range.presetId) ?? rangePresets[3]) : null;
}

/** How long the range is, in milliseconds. */
export function rangeMs(range: LogRange): number {
  const preset = presetOf(range);
  if (preset) return preset.ms;
  if (range.kind === "between") return Math.max(1, Date.parse(range.toUtc) - Date.parse(range.fromUtc));
  return 86_400_000;
}

/** The bucket a range is drawn in when none is chosen: its preset's, or the finest one that fits. */
export function autoInterval(range: LogRange): IntervalType {
  const preset = presetOf(range);
  if (preset) return preset.interval;
  const ms = rangeMs(range);
  // a graph of a few hundred points at most: the server clamps to 400, and fewer read better
  return intervals.find((i) => ms / intervalMs[i] <= 240) ?? "Month";
}

/** What a command is sent for the range: "the last so many" moves with the clock on the server. */
export function rangePayload(range: LogRange): { lastMs: number | null; fromUtc: string | null; toUtc: string | null } {
  const preset = presetOf(range);
  if (preset) return { lastMs: preset.ms, fromUtc: null, toUtc: null };
  if (range.kind === "between") return { lastMs: null, fromUtc: range.fromUtc, toUtc: range.toUtc };
  return { lastMs: 86_400_000, fromUtc: null, toUtc: null };
}

/** The two moments of the range as they are now, for a download of what is on screen. */
export function rangeBounds(range: LogRange): { fromUtc: string; toUtc: string } {
  if (range.kind === "between") return { fromUtc: range.fromUtc, toUtc: range.toUtc };
  const to = new Date();
  return { fromUtc: new Date(to.getTime() - rangeMs(range)).toISOString(), toUtc: to.toISOString() };
}

export function rangeLabel(range: LogRange): string {
  const preset = presetOf(range);
  if (preset) return "the last " + preset.label;
  if (range.kind === "between") return range.label ?? `${formatDateTime(range.fromUtc)} – ${formatDateTime(range.toUtc)}`;
  return "";
}

/** A moment one interval later: the end of the bucket a graph point stands for. */
export function addInterval(iso: string, interval: IntervalType): string {
  const d = new Date(iso);
  switch (interval) {
    case "Second":
      return new Date(d.getTime() + 1000).toISOString();
    case "Minute":
      return new Date(d.getTime() + 60_000).toISOString();
    case "Hour":
      return new Date(d.getTime() + 3_600_000).toISOString();
    case "Day":
      return new Date(d.getTime() + 86_400_000).toISOString();
    case "Week":
      return new Date(d.getTime() + 7 * 86_400_000).toISOString();
    case "Month": {
      const next = new Date(d);
      next.setUTCMonth(next.getUTCMonth() + 1);
      return next.toISOString();
    }
  }
}

/** A local "yyyy-MM-ddTHH:mm" for a datetime-local input, from an ISO moment, and back. */
export function toLocalInput(iso: string): string {
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
export function fromLocalInput(value: string): string | null {
  if (!value) return null;
  const d = new Date(value);
  return isNaN(d.getTime()) ? null : d.toISOString();
}

// ---- values ----

const trim = (v: number) => v.toLocaleString("en-US", { maximumFractionDigits: 2 });

/** Milliseconds as a duration a person reads: 850 ms, 2.4 s, 3 min 12 s, 2 h 5 min, 3 d 4 h. */
export function formatMs(ms: number): string {
  const abs = Math.abs(ms);
  const sign = ms < 0 ? "-" : "";
  if (abs < 1) return sign + trim(abs) + " ms";
  if (abs < 1000) return sign + trim(Math.round(abs * 10) / 10) + " ms";
  if (abs < 60_000) return sign + trim(Math.round(abs / 100) / 10) + " s";
  if (abs < 3_600_000) return `${sign}${Math.floor(abs / 60_000)} min ${Math.round((abs % 60_000) / 1000)} s`;
  if (abs < 86_400_000) return `${sign}${Math.floor(abs / 3_600_000)} h ${Math.round((abs % 3_600_000) / 60_000)} min`;
  return `${sign}${Math.floor(abs / 86_400_000)} d ${Math.round((abs % 86_400_000) / 3_600_000)} h`;
}

/** A .NET TimeSpan as json writes it ("1.02:03:04.5000000", "00:00:00.2500000") in milliseconds. */
export function timeSpanMs(value: unknown): number | null {
  if (typeof value === "number") return value;
  if (typeof value !== "string") return null;
  const m = value.match(/^(-)?(?:(\d+)\.)?(\d+):(\d+):(\d+)(?:\.(\d+))?$/);
  if (!m) return null;
  const [, neg, days, hours, minutes, seconds, fraction] = m;
  const ms =
    (Number(days ?? 0) * 86_400 + Number(hours) * 3600 + Number(minutes) * 60 + Number(seconds)) * 1000 +
    (fraction ? Number(("0." + fraction).slice(0, 12)) * 1000 : 0);
  return neg ? -ms : ms;
}

/** One value of a column as the table shows it. */
export function formatLogValue(value: unknown, type: LogDataType): string {
  if (value == null || value === "") return "—";
  switch (type) {
    case "Integer":
      return typeof value === "number" ? formatCount(value) : String(value);
    case "Double":
      return typeof value === "number" ? trim(value) : String(value);
    case "DateTime":
      return typeof value === "string" && !isNaN(Date.parse(value)) ? formatDateTime(value) : String(value);
    case "TimeSpan": {
      const ms = timeSpanMs(value);
      return ms == null ? String(value) : formatMs(ms);
    }
    case "Bytes":
      // json carries bytes as base64; its length is what can be said about them in a cell
      return typeof value === "string" ? formatBytes(Math.floor((value.length * 3) / 4) - (value.endsWith("==") ? 2 : value.endsWith("=") ? 1 : 0)) : "bytes";
    default:
      return typeof value === "object" ? JSON.stringify(value) : String(value);
  }
}

/**
 * How a number from a column's analysis reads: the server measures a duration in milliseconds, a
 * moment in milliseconds since 1970 and bytes by their length, so each is turned back here.
 */
export function formatMeasure(value: number, type: LogDataType): string {
  switch (type) {
    case "TimeSpan":
      return formatMs(value);
    case "DateTime":
      return formatDateTime(new Date(value).toISOString());
    case "Bytes":
      return formatBytes(value);
    case "Integer":
      return Math.abs(value) >= 1000 ? formatCount(Math.round(value)) : trim(value);
    default:
      return trim(value);
  }
}

/** The same, shorter: an axis label. */
export function formatMeasureShort(value: number, type: LogDataType): string {
  if (type === "DateTime") {
    const d = new Date(value);
    return d.toLocaleDateString([], { month: "short", day: "numeric" }) + " " + d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  }
  if (type === "TimeSpan" || type === "Bytes") return formatMeasure(value, type);
  // the edges of a histogram's bars fall between whole numbers; a count of 802.27 is not one
  if (type === "Integer") value = Math.round(value);
  const abs = Math.abs(value);
  if (abs >= 1_000_000) return (value / 1_000_000).toFixed(abs >= 10_000_000 ? 0 : 1) + "M";
  if (abs >= 10_000) return (value / 1000).toFixed(0) + "k";
  return trim(value);
}

/** How long ago a moment was, in the largest unit that says it: "just now", "4 min ago", "2 d ago". */
export function formatAgo(iso: string, now = Date.now()): string {
  const s = Math.round((now - Date.parse(iso)) / 1000);
  if (s < 5) return "just now";
  if (s < 60) return `${s} s ago`;
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86_400) return `${Math.round(s / 3600)} h ago`;
  return `${Math.round(s / 86_400)} d ago`;
}
