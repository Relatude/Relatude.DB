import { useCallback, useEffect, useRef, useState } from "react";
import {
  IconAlertTriangle,
  IconChartHistogram,
  IconChevronLeft,
  IconChevronRight,
  IconDeviceFloppy,
  IconEraser,
  IconReload,
  IconRotate,
  IconTrash,
} from "@tabler/icons-react";
import { Chart, groupColor, intervalLabel } from "./Chart";
import { showConfirm, showError, showInfo } from "../dialogs";
import {
  clearLog,
  enableLog,
  fetchLogPage,
  fetchLogsInfo,
  fetchScans,
  fetchSeries,
  fetchTrace,
  rebuildStatistics,
  recordScans,
  restoreLogSettings,
  saveLogSettings,
  setMinQueryDuration,
  type IntervalType,
  type LogDataType,
  type LogInfo,
  type LogPage,
  type LogSeries,
  type LogsInfo,
  type ScanInfo,
  type SeriesData,
  type TraceInfo,
} from "../server/logs";
import type { DatabaseInfo } from "../server/serverInfo";
import { formatBytes, formatCount, formatTime } from "../format";

/**
 * What the database has been doing: the trace it keeps in memory, the logs it writes to disk, the
 * statistics kept alongside them, and the property scans.
 *
 * The page knows nothing about any particular log. The server describes each one - its columns with
 * their data types, and the statistics each column declares - and this renders that description, so
 * a log added to the server appears here with its table and its graphs already working.
 */
export function LogsSection({ db }: { db: DatabaseInfo }) {
  const [info, setInfo] = useState<LogsInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tab, setTab] = useState("trace");
  const load = useCallback(async () => {
    try {
      setInfo(await fetchLogsInfo(db.id));
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, [db.id]);
  useEffect(() => {
    load();
  }, [load]);

  if (error) return <div className="placeholder">{error}</div>;
  if (!info) return null;
  const log = info.logs.find((l) => l.key === tab) ?? null;
  return (
    <div className="logs">
      <div className="logs-tabs">
        <Tab id="trace" label="Trace" active={tab} onSelect={setTab} />
        {info.logs.map((l) => (
          <Tab key={l.key} id={l.key} label={l.name} active={tab} onSelect={setTab} recording={l.enabledLog || l.enabledStatistics} />
        ))}
        <Tab id="scans" label="Scans" active={tab} onSelect={setTab} />
        <Tab id="overview" label="All logs" active={tab} onSelect={setTab} />
      </div>
      <SaveBar db={db} info={info} onSaved={load} />
      {tab === "trace" ? (
        <TraceTab db={db} />
      ) : tab === "scans" ? (
        <ScansTab db={db} />
      ) : tab === "overview" ? (
        <OverviewTab db={db} info={info} onChanged={load} />
      ) : log ? (
        <LogTab key={log.key} db={db} log={log} onChanged={load} />
      ) : null}
    </div>
  );
}

function Tab({
  id,
  label,
  active,
  onSelect,
  recording,
}: {
  id: string;
  label: string;
  active: string;
  onSelect: (id: string) => void;
  recording?: boolean;
}) {
  return (
    <button className={"logs-tab" + (active === id ? " active" : "")} onClick={() => onSelect(id)}>
      {label}
      {recording && <span className="logs-rec" title="Recording" />}
    </button>
  );
}

// ---- remembering what is being recorded ----

/**
 * How many switches are live but not written down. A logger is built with every log off, so a
 * switch flipped here stops at the next close unless it is saved into the settings file - which is
 * the one thing about this page that is not obvious, so it says so rather than waiting to be found
 * out after a restart.
 */
function unsavedCount(info: LogsInfo): number {
  let count = 0;
  for (const log of info.logs) {
    if (log.enabledLog !== log.savedLog) count++;
    if (log.enabledStatistics !== log.savedStatistics) count++;
  }
  if (info.minQueryDurationMs !== info.savedMinQueryDurationMs) count++;
  return count;
}

function SaveBar({ db, info, onSaved }: { db: DatabaseInfo; info: LogsInfo; onSaved: () => void }) {
  const [saving, setSaving] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const noteTimer = useRef<number | null>(null);
  useEffect(
    () => () => {
      if (noteTimer.current !== null) clearTimeout(noteTimer.current);
    },
    [],
  );
  async function save() {
    setSaving(true);
    try {
      const result = await saveLogSettings(db.id);
      // the bar disappears with the last unsaved change, so what happened is said where it stood
      setNote(
        result.recording === 0
          ? "Saved. No log is recording, and none will be after a restart."
          : `Saved. ${result.recording} of ${result.logs} logs record again after a restart.`,
      );
      if (noteTimer.current !== null) clearTimeout(noteTimer.current);
      noteTimer.current = window.setTimeout(() => setNote(null), 8000);
      onSaved();
    } catch (e) {
      await showError("Could not save the log settings", e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }
  // the other direction: the settings file decides again, live. A log the file never mentioned
  // goes off, which is what the next start would give it anyway
  async function restore() {
    setSaving(true);
    try {
      const result = await restoreLogSettings(db.id);
      setNote(
        result.recording === 0
          ? "Back to the saved settings. No log is recording."
          : `Back to the saved settings. ${result.recording} ${result.recording === 1 ? "log is" : "logs are"} recording.`,
      );
      if (noteTimer.current !== null) clearTimeout(noteTimer.current);
      noteTimer.current = window.setTimeout(() => setNote(null), 8000);
      onSaved();
    } catch (e) {
      await showError("Could not restore the log settings", e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }
  const unsaved = unsavedCount(info);
  if (unsaved === 0) return note ? <div className="logs-savebar saved">{note}</div> : null;
  return (
    <div className="logs-savebar">
      <span>
        {unsaved} unsaved {unsaved === 1 ? "change" : "changes"} <span className="muted">· recording stops when the database closes unless it is saved</span>
      </span>
      <span className="header-spacer" />
      <button className="action-button" onClick={restore} disabled={saving} title="Put every switch back to what the settings file holds">
        <IconRotate size={15} stroke={1.8} /> Back to saved
      </button>
      {info.canSave ? (
        <button className="action-button primary" onClick={save} disabled={saving}>
          <IconDeviceFloppy size={15} stroke={1.8} /> {saving ? "Saving…" : "Save and remember changes"}
        </button>
      ) : (
        <span className="logs-warn">Configuration decides what is recorded, so it cannot be saved here.</span>
      )}
    </div>
  );
}

// ---- ranges ----

/**
 * The ranges the page can ask for, each with the bucket size its graph is drawn in. Statistics are
 * only kept for so many intervals of each type, so a longer range is not the same graph zoomed out:
 * it is a coarser bucket. The server clamps a range that reaches past what is kept and says so.
 */
const ranges = [
  { id: "3m", label: "3 minutes", ms: 3 * 60_000, interval: "Second" as IntervalType },
  { id: "1h", label: "1 hour", ms: 3_600_000, interval: "Minute" as IntervalType },
  { id: "24h", label: "24 hours", ms: 24 * 3_600_000, interval: "Hour" as IntervalType },
  { id: "7d", label: "7 days", ms: 7 * 86_400_000, interval: "Hour" as IntervalType },
  { id: "30d", label: "30 days", ms: 30 * 86_400_000, interval: "Day" as IntervalType },
  { id: "90d", label: "90 days", ms: 90 * 86_400_000, interval: "Day" as IntervalType },
  { id: "12m", label: "12 months", ms: 365 * 86_400_000, interval: "Month" as IntervalType },
];

function RangePicker({ value, onChange }: { value: string; onChange: (id: string) => void }) {
  return (
    <select className="select" value={value} onChange={(e) => onChange(e.currentTarget.value)} title="Time range">
      {ranges.map((r) => (
        <option key={r.id} value={r.id}>
          Last {r.label}
        </option>
      ))}
    </select>
  );
}

// ---- one log ----

const pageSize = 100;

function LogTab({ db, log, onChanged }: { db: DatabaseInfo; log: LogInfo; onChanged: () => void }) {
  const [rangeId, setRangeId] = useState("24h");
  const [seriesKey, setSeriesKey] = useState(seriesId(log.series[0]));
  const [series, setSeries] = useState<SeriesData | null>(null);
  const [seriesError, setSeriesError] = useState<string | null>(null);
  const [page, setPage] = useState<LogPage | null>(null);
  const [skip, setSkip] = useState(0);
  const [live, setLive] = useState(false);
  const [tick, setTick] = useState(0);
  const range = ranges.find((r) => r.id === rangeId) ?? ranges[2];
  const selected = log.series.find((s) => seriesId(s) === seriesKey) ?? log.series[0];

  useEffect(() => setSkip(0), [rangeId, log.key]);
  useEffect(() => {
    if (!live) return;
    const timer = window.setInterval(() => setTick((t) => t + 1), 5000);
    return () => clearInterval(timer);
  }, [live]);

  // one window for both halves of the page: the graph and the entries under it always cover the
  // same range, so a spike in the graph is in the table below it
  useEffect(() => {
    let cancelled = false;
    const to = new Date();
    const from = new Date(to.getTime() - range.ms);
    fetchSeries(db.id, log.key, selected, range.interval, from.toISOString(), to.toISOString())
      .then((data) => {
        if (cancelled) return;
        setSeries(data);
        setSeriesError(null);
      })
      .catch((e) => {
        if (cancelled) return;
        setSeries(null);
        setSeriesError(e instanceof Error ? e.message : String(e));
      });
    fetchLogPage(db.id, log.key, from.toISOString(), to.toISOString(), skip, pageSize)
      .then((p) => !cancelled && setPage(p))
      .catch(() => !cancelled && setPage(null));
    return () => {
      cancelled = true;
    };
  }, [db.id, log.key, selected, range, skip, tick]);

  async function toggle(change: { log?: boolean; statistics?: boolean }) {
    try {
      await enableLog(db.id, log.key, change);
      onChanged();
      setTick((t) => t + 1);
    } catch (e) {
      showError("Could not change the log", e instanceof Error ? e.message : String(e));
    }
  }

  async function clear(what: { log: boolean; statistics: boolean }) {
    const subject = what.log && what.statistics ? "entries and statistics" : what.log ? "recorded entries" : "statistics";
    const confirmed = await showConfirm(`Clear the ${log.name.toLowerCase()} log`, `Delete the ${subject} of this log? This cannot be undone.`, {
      confirmLabel: "Clear",
      danger: true,
    });
    if (!confirmed.ok) return;
    try {
      await clearLog(db.id, log.key, what);
      onChanged();
      setSkip(0);
      setTick((t) => t + 1);
    } catch (e) {
      showError("Could not clear the log", e instanceof Error ? e.message : String(e));
    }
  }

  async function rebuild() {
    try {
      await rebuildStatistics(db.id, log.key);
      onChanged();
      setTick((t) => t + 1);
      showInfo("Statistics rebuilt", "The statistics were aggregated again from the log files.");
    } catch (e) {
      showError("Could not rebuild the statistics", e instanceof Error ? e.message : String(e));
    }
  }

  const entries = page?.entries ?? [];
  const columns = log.columns;
  const gridTemplate = "150px " + columns.map((c) => (c.dataType === "String" ? "minmax(0, 2fr)" : "minmax(0, 1fr)")).join(" ");
  // a log with many columns scrolls sideways rather than squeezing every one of them into an
  // ellipsis: below this width the table is unreadable, and the panel around it has a scrollbar
  const rowStyle = { gridTemplateColumns: gridTemplate, minWidth: 170 + columns.length * 110 };
  return (
    <div className="logs-body">
      <div className="logs-toolbar">
        <Switch label="Record" checked={log.enabledLog} title="Write every entry of this log to disk" onChange={(v) => toggle({ log: v })} />
        <Switch
          label="Statistics"
          checked={log.enabledStatistics}
          title="Aggregate this log into the statistics the graphs are drawn from"
          onChange={(v) => toggle({ statistics: v })}
        />
        <span className="logs-spacer" />
        <RangePicker value={rangeId} onChange={setRangeId} />
        <button className={"action-button" + (live ? " armed" : "")} onClick={() => setLive(!live)} title="Refresh every five seconds">
          <IconReload size={15} stroke={1.8} /> Live
        </button>
        <button className="action-button" onClick={() => setTick((t) => t + 1)} title="Refresh now">
          <IconReload size={15} stroke={1.8} />
        </button>
        <button className="action-button" onClick={() => clear({ log: true, statistics: false })} title="Delete the recorded entries">
          <IconTrash size={15} stroke={1.8} /> Entries
        </button>
        <button className="action-button" onClick={() => clear({ log: false, statistics: true })} title="Delete the statistics">
          <IconEraser size={15} stroke={1.8} /> Statistics
        </button>
      </div>

      <section className="panel">
        <h3>
          Statistics <span className="panel-sub">{summaryText(series)}</span>
        </h3>
        <div className="logs-series">
          {log.series.map((s) => (
            <button
              key={seriesId(s)}
              className={"logs-chip" + (seriesId(s) === seriesId(selected) ? " active" : "")}
              onClick={() => setSeriesKey(seriesId(s))}
            >
              {s.label}
            </button>
          ))}
        </div>
        {seriesError ? (
          <div className="logs-note">{seriesError}</div>
        ) : series ? (
          <>
            <Chart
              kind={series.kind}
              points={series.points}
              groups={series.groups}
              interval={series.interval}
              format={valueFormatter(selected, series)}
              integer={series.kind === "count" || series.kind === "groups" || selected.dataType === "Integer"}
            />
            {series.kind === "groups" && series.groups.length > 0 && (
              <div className="chart-legend">
                {series.groups.map((g, i) => (
                  <span key={g} className="chart-legend-item">
                    <span className="chart-swatch" style={{ background: groupColor(i) }} />
                    {g}
                    <span className="muted">{formatCount(series.summary?.groups?.find((x) => x.name === g)?.count ?? 0)}</span>
                  </span>
                ))}
              </div>
            )}
            <div className="logs-chart-foot">
              <span className="muted">
                {intervalLabel(series.fromUtc, series.interval)} — {intervalLabel(series.toUtc, series.interval)} · one point per{" "}
                {series.interval.toLowerCase()}
              </span>
              {series.clamped && <span className="logs-warn">The statistics only reach this far back at this resolution.</span>}
              {!series.enabledStatistics && (
                <span className="logs-warn">
                  Statistics are off: nothing new is aggregated, and what was recorded earlier stays unread until they are back on.
                  <button className="link-button" onClick={() => toggle({ statistics: true })}>
                    Turn them on
                  </button>
                </span>
              )}
              {series.enabledStatistics && log.logBytes > 0 && (
                <button className="link-button" onClick={rebuild} title="Aggregate the statistics again from the recorded entries">
                  <IconChartHistogram size={14} stroke={1.8} /> Rebuild from entries
                </button>
              )}
            </div>
          </>
        ) : null}
      </section>

      <section className="panel">
        <h3>
          Entries{" "}
          <span className="panel-sub">
            {page && page.total > 0
              ? `${formatCount(skip + 1)}–${formatCount(skip + entries.length)} of ${formatCount(page.total)} in the last ${range.label}`
              : `nothing recorded in the last ${range.label}`}
          </span>
        </h3>
        {!log.enabledLog && (
          <div className="logs-note">
            Entries are not being recorded.
            <button className="link-button" onClick={() => toggle({ log: true })}>
              Start recording
            </button>
          </div>
        )}
        <div className="log-table">
          <div className="log-table-row log-table-head" style={rowStyle}>
            <span>Time</span>
            {columns.map((c) => (
              <span key={c.key}>{c.name}</span>
            ))}
          </div>
          {entries.map((entry, i) => (
            <div
              key={entry.timestampUtc + i}
              className="log-table-row"
              style={rowStyle}
              onClick={() => showEntry(log, entry.timestampUtc, entry.values)}
              title="Show the whole entry"
            >
              <span className="log-time">{formatTime(entry.timestampUtc)}</span>
              {columns.map((c) => {
                const text = formatValue(entry.values[c.key], c.dataType);
                return (
                  <span key={c.key} className={"log-cell" + toneOf(c.key, text)} title={text}>
                    {text}
                  </span>
                );
              })}
            </div>
          ))}
          {entries.length === 0 && <div className="log-table-empty">No entries in this range.</div>}
        </div>
        {page && page.total > pageSize && (
          <div className="logs-paging">
            <button className="action-button" disabled={skip === 0} onClick={() => setSkip(Math.max(0, skip - pageSize))}>
              <IconChevronLeft size={15} stroke={1.8} /> Newer
            </button>
            <button className="action-button" disabled={skip + pageSize >= page.total} onClick={() => setSkip(skip + pageSize)}>
              Older <IconChevronRight size={15} stroke={1.8} />
            </button>
          </div>
        )}
      </section>
    </div>
  );
}

const seriesId = (s: LogSeries | undefined) => (s ? (s.property ?? "*") + ":" + s.statistic : "*:Count");

/** The one line above the chart that says what the whole range adds up to. */
function summaryText(series: SeriesData | null): string {
  const s = series?.summary;
  if (!series || !s) return "";
  const number = (v: number | null | undefined) => (v == null ? "—" : Math.abs(v) >= 1000 ? formatCount(Math.round(v)) : trim(v));
  switch (series.kind) {
    case "count":
    case "groups":
      return s.total == null ? "" : `${formatCount(s.total)} in this range`;
    case "sum":
      return s.total == null ? "" : `${number(s.total)} in total`;
    case "avgminmax":
      return `avg ${number(s.avg)} · min ${number(s.min)} · max ${number(s.max)}`;
    case "full":
      return `${formatCount(s.count ?? 0)} entries · avg ${number(s.avg)} · min ${number(s.min)} · max ${number(s.max)}`;
  }
}

/** How the chart writes its numbers: counts are whole, a measured value keeps its data type. */
function valueFormatter(selected: LogSeries, series: SeriesData): (v: number) => string {
  if (series.kind === "count" || series.kind === "groups") return (v) => formatCount(Math.round(v));
  if (selected.dataType === "Bytes") return (v) => formatBytes(v);
  if (selected.dataType === "Integer") return (v) => formatCount(Math.round(v));
  return (v) => trim(v);
}
const trim = (v: number) => v.toLocaleString("en-US", { maximumFractionDigits: 2 });

function showEntry(log: LogInfo, timestampUtc: string, values: Record<string, unknown>): void {
  const lines = log.columns.map((c) => `${c.name}: ${formatValue(values[c.key], c.dataType)}`);
  // values a log recorded before its columns changed still belong to the entry
  for (const key of Object.keys(values)) {
    if (!log.columns.some((c) => c.key === key)) lines.push(`${key}: ${formatValue(values[key], "String")}`);
  }
  showInfo(`${log.name} · ${formatTime(timestampUtc)}`, "", lines);
}

function formatValue(value: unknown, type: LogDataType): string {
  if (value == null || value === "") return "—";
  switch (type) {
    case "Integer":
      return formatCount(Number(value));
    case "Double":
      return trim(Number(value));
    case "DateTime":
      return formatTime(String(value));
    case "Bytes":
      return "binary";
    default:
      return String(value);
  }
}

// an error or a failure reads as one at a glance, wherever the log happens to put the word
function toneOf(key: string, text: string): string {
  if (key !== "type" && key !== "success" && key !== "error") return "";
  const value = text.toLowerCase();
  if (value === "error" || value === "false" || (key === "error" && text !== "—")) return " bad";
  if (value === "warning") return " warn";
  return "";
}

// ---- trace ----

function TraceTab({ db }: { db: DatabaseInfo }) {
  const [trace, setTrace] = useState<TraceInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [live, setLive] = useState(true);
  const [tick, setTick] = useState(0);
  useEffect(() => {
    let cancelled = false;
    fetchTrace(db.id)
      .then((t) => {
        if (cancelled) return;
        setTrace(t);
        setError(null);
      })
      .catch((e) => !cancelled && setError(e instanceof Error ? e.message : String(e)));
    return () => {
      cancelled = true;
    };
  }, [db.id, tick]);
  // the trace is what the database is saying right now, so it follows by default
  useEffect(() => {
    if (!live) return;
    const timer = window.setInterval(() => setTick((t) => t + 1), 2000);
    return () => clearInterval(timer);
  }, [live]);

  if (error) return <div className="placeholder">{error}</div>;
  if (!trace) return null;
  return (
    <div className="logs-body">
      <div className="logs-toolbar">
        <span className="logs-note-inline">
          The last messages the running database kept in memory. Nothing here is written to disk unless the system log is recording.
        </span>
        <span className="logs-spacer" />
        <button className={"action-button" + (live ? " armed" : "")} onClick={() => setLive(!live)} title="Follow new messages">
          <IconReload size={15} stroke={1.8} /> Live
        </button>
        <button className="action-button" onClick={() => setTick((t) => t + 1)} title="Refresh now">
          <IconReload size={15} stroke={1.8} />
        </button>
      </div>
      {trace.startupError && (
        <div className="startup-exception">
          <div className="startup-exception-head">
            <IconAlertTriangle size={14} stroke={2} /> The database failed to start
            {trace.startupError.timeUtc ? ` · ${formatTime(trace.startupError.timeUtc)}` : ""}
          </div>
          <div>{trace.startupError.message}</div>
          {trace.startupError.details && (
            <button className="link-button" onClick={() => showInfo("Startup error", trace.startupError!.message, [trace.startupError!.details!])}>
              Details
            </button>
          )}
        </div>
      )}
      <section className="panel">
        <h3>
          Trace <span className="panel-sub">{trace.open ? `${trace.entries.length} messages` : "the database is closed"}</span>
        </h3>
        <div className="log-table">
          <div className="log-table-row log-table-head trace-row">
            <span>Time</span>
            <span>Type</span>
            <span>Message</span>
          </div>
          {trace.entries.map((entry, i) => (
            <div
              key={i}
              className={"log-table-row trace-row" + (entry.details ? " clickable" : "")}
              onClick={() => entry.details && showInfo(entry.text, "", [entry.details])}
              title={entry.details ? "Show the details" : undefined}
            >
              <span className="log-time">{formatTime(entry.timestampUtc)}</span>
              <span className={"log-cell " + entry.type.toLowerCase()}>{entry.type}</span>
              <span className="log-cell" title={entry.text}>
                {entry.text}
                {entry.details && <span className="muted"> · details</span>}
              </span>
            </div>
          ))}
          {trace.entries.length === 0 && <div className="log-table-empty">{trace.open ? "Nothing traced yet." : "Open the database to see its trace."}</div>}
        </div>
      </section>
    </div>
  );
}

// ---- property scans ----

function ScansTab({ db }: { db: DatabaseInfo }) {
  const [scans, setScans] = useState<ScanInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tick, setTick] = useState(0);
  useEffect(() => {
    let cancelled = false;
    fetchScans(db.id)
      .then((s) => {
        if (cancelled) return;
        setScans(s);
        setError(null);
      })
      .catch((e) => !cancelled && setError(e instanceof Error ? e.message : String(e)));
    return () => {
      cancelled = true;
    };
  }, [db.id, tick]);
  useEffect(() => {
    if (!scans?.recording) return;
    const timer = window.setInterval(() => setTick((t) => t + 1), 3000);
    return () => clearInterval(timer);
  }, [scans?.recording]);

  async function record(enable: boolean) {
    try {
      await recordScans(db.id, enable);
      setTick((t) => t + 1);
    } catch (e) {
      showError("Could not change scan recording", e instanceof Error ? e.message : String(e));
    }
  }

  if (error) return <div className="placeholder">{error}</div>;
  if (!scans) return null;
  const total = scans.hits.reduce((sum, h) => sum + h.count, 0);
  return (
    <div className="logs-body">
      <div className="logs-toolbar">
        <Switch
          label="Record scans"
          checked={scans.recording}
          disabled={!scans.open}
          title={scans.open ? "Count every property read that has to scan" : "The database is closed"}
          onChange={record}
        />
        <span className="logs-note-inline">
          A query that filters or sorts on a property without an index scans it. Recording counts those reads, so an index can be aimed at the ones that
          matter. Turning it on again starts from zero.
        </span>
      </div>
      <section className="panel">
        <h3>
          Scanned properties <span className="panel-sub">{total > 0 ? `${formatCount(total)} scans` : "nothing counted yet"}</span>
        </h3>
        <div className="log-table">
          <div className="log-table-row log-table-head scan-row">
            <span>Property</span>
            <span className="num">Scans</span>
            <span>Share</span>
          </div>
          {scans.hits.map((hit) => (
            <div key={hit.name} className="log-table-row scan-row">
              <span className="log-cell" title={hit.name}>
                {hit.name}
              </span>
              <span className="num">{formatCount(hit.count)}</span>
              <span className="scan-bar">
                <span className="scan-bar-fill" style={{ width: (total > 0 ? (hit.count / total) * 100 : 0) + "%" }} />
              </span>
            </div>
          ))}
          {scans.hits.length === 0 && (
            <div className="log-table-empty">{scans.recording ? "No scans counted yet." : "Turn recording on and run the queries to measure."}</div>
          )}
        </div>
      </section>
    </div>
  );
}

// ---- every log at once ----

function OverviewTab({ db, info, onChanged }: { db: DatabaseInfo; info: LogsInfo; onChanged: () => void }) {
  async function toggle(logKey: string, change: { log?: boolean; statistics?: boolean }) {
    try {
      await enableLog(db.id, logKey, change);
      onChanged();
    } catch (e) {
      showError("Could not change the log", e instanceof Error ? e.message : String(e));
    }
  }
  async function setAll(change: { log?: boolean; statistics?: boolean }) {
    try {
      for (const log of info.logs) await enableLog(db.id, log.key, change);
      onChanged();
    } catch (e) {
      showError("Could not change the logs", e instanceof Error ? e.message : String(e));
    }
  }
  async function clearAll() {
    const confirmed = await showConfirm("Clear every log", "Delete the recorded entries and the statistics of every log of this database?", {
      confirmLabel: "Clear everything",
      danger: true,
    });
    if (!confirmed.ok) return;
    try {
      await clearLog(db.id, null, { log: true, statistics: true });
      onChanged();
    } catch (e) {
      showError("Could not clear the logs", e instanceof Error ? e.message : String(e));
    }
  }
  const allLogs = info.logs.every((l) => l.enabledLog);
  const allStats = info.logs.every((l) => l.enabledStatistics);
  const [minDuration, setMinDuration] = useState(String(info.minQueryDurationMs));
  async function applyMinDuration() {
    const ms = Number(minDuration);
    if (!Number.isFinite(ms) || ms < 0) {
      setMinDuration(String(info.minQueryDurationMs));
      return;
    }
    try {
      const result = await setMinQueryDuration(db.id, Math.round(ms));
      setMinDuration(String(result.ms));
      onChanged();
    } catch (e) {
      showError("Could not change the threshold", e instanceof Error ? e.message : String(e));
    }
  }
  return (
    <div className="logs-body">
      <section className="panel">
        <h3>
          Logs <span className="panel-sub">{formatBytes(info.totalBytes)} on disk</span>
        </h3>
        <div className="log-table">
          <div className="log-table-row log-table-head overview-row">
            <span>Log</span>
            <span>Record</span>
            <span>Statistics</span>
            <span>First</span>
            <span>Last</span>
            <span className="num">Entries</span>
            <span className="num">Statistics</span>
            <span>Keeps</span>
          </div>
          {info.logs.map((log) => (
            <div key={log.key} className="log-table-row overview-row">
              <span className="log-cell">{log.name}</span>
              <span>
                <Switch checked={log.enabledLog} onChange={(v) => toggle(log.key, { log: v })} />
              </span>
              <span>
                <Switch checked={log.enabledStatistics} onChange={(v) => toggle(log.key, { statistics: v })} />
              </span>
              <span className="log-cell muted">{log.firstRecordUtc ? formatTime(log.firstRecordUtc) : "—"}</span>
              <span className="log-cell muted">{log.lastRecordUtc ? formatTime(log.lastRecordUtc) : "—"}</span>
              <span className="num">{log.logBytes > 0 ? formatBytes(log.logBytes) : "—"}</span>
              <span className="num">{log.statisticsBytes > 0 ? formatBytes(log.statisticsBytes) : "—"}</span>
              <span className="log-cell muted">
                {log.maxAgeInDays} days · {log.maxSizeInMb} MB
              </span>
            </div>
          ))}
          <div className="log-table-row overview-row log-table-total">
            <span>All logs</span>
            <span>
              <Switch checked={allLogs} onChange={(v) => setAll({ log: v })} />
            </span>
            <span>
              <Switch checked={allStats} onChange={(v) => setAll({ statistics: v })} />
            </span>
            <span />
            <span />
            <span className="num">{formatBytes(info.logs.reduce((sum, l) => sum + l.logBytes, 0))}</span>
            <span className="num">{formatBytes(info.logs.reduce((sum, l) => sum + l.statisticsBytes, 0))}</span>
            <span />
          </div>
        </div>
        <div className="logs-threshold">
          <label htmlFor="min-query-duration">Only record queries slower than</label>
          <input
            id="min-query-duration"
            className="text-input number"
            type="number"
            min={0}
            value={minDuration}
            onChange={(e) => setMinDuration(e.currentTarget.value)}
            onBlur={applyMinDuration}
            onKeyDown={(e) => e.key === "Enter" && applyMinDuration()}
          />
          <span className="muted">milliseconds · 0 records every query</span>
        </div>
        <div className="logs-chart-foot">
          <span className="muted">
            A switch takes effect at once and holds until the database closes; "Save and remember changes" writes it to the settings file, so the same
            logs record again at the next start. Sizes are what has reached disk - statistics are written periodically, so a log can be graphing more
            than it lists here. A log enforces its own limits, dropping files older than its age limit and trimming to its size limit.
          </span>
          <button className="action-button danger" onClick={clearAll}>
            <IconTrash size={15} stroke={1.8} /> Clear every log
          </button>
        </div>
      </section>
    </div>
  );
}

// ---- shared bits ----

function Switch({
  label,
  checked,
  disabled,
  title,
  onChange,
}: {
  label?: string;
  checked: boolean;
  disabled?: boolean;
  title?: string;
  onChange: (value: boolean) => void;
}) {
  return (
    <label className={"logs-switch" + (disabled ? " disabled" : "")} title={title}>
      <input type="checkbox" checked={checked} disabled={disabled} onChange={(e) => onChange(e.currentTarget.checked)} />
      {label && <span>{label}</span>}
    </label>
  );
}
