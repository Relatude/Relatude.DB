import { useCallback, useEffect, useRef, useState } from "react";
import {
  IconAlertTriangle,
  IconChartHistogram,
  IconChevronDown,
  IconChevronLeft,
  IconChevronRight,
  IconDeviceFloppy,
  IconDownload,
  IconEraser,
  IconHelpCircle,
  IconLetterCase,
  IconReload,
  IconRotate,
  IconSearch,
  IconTrash,
  IconX,
} from "@tabler/icons-react";
import { Chart, groupColor, intervalLabel } from "./Chart";
import { showChoice, showConfirm, showError, showInfo } from "../dialogs";
import {
  clearLog,
  downloadLogTsv,
  enableLog,
  fetchLogsInfo,
  matchesTerm,
  rebuildStatistics,
  recordScans,
  restoreLogSettings,
  saveLogSettings,
  searchHelp,
  setMinQueryDuration,
  type IntervalType,
  type LogColumn,
  type LogDataType,
  type LogEntry,
  type LogInfo,
  type LogPage,
  type LogSeries,
  type LogsInfo,
  type ScanInfo,
  type SeriesData,
  type TraceInfo,
} from "../server/logs";
import type { DatabaseInfo } from "../server/serverInfo";
import { useLive } from "../live";
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
  const [tab, setTab] = useState("overview");
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
        <Tab id="overview" label="All logs" active={tab} onSelect={setTab} />
        <Tab id="trace" label="Trace" active={tab} onSelect={setTab} />
        {info.logs.map((l) => (
          <Tab key={l.key} id={l.key} label={l.name} active={tab} onSelect={setTab} recording={l.enabledLog || l.enabledStatistics} />
        ))}
        <Tab id="scans" label="Scans" active={tab} onSelect={setTab} />
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
  { id: "60s", label: "60 seconds", ms: 60_000, interval: "Second" as IntervalType },
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

/**
 * How many entries a column filter searches.
 *
 * The filters run in the browser over the entries it already has, so they need a window worth
 * searching: with one set, a call brings back this many of the newest entries in the range rather
 * than a page of a hundred, and the filter pages through the matches among those. Reading a range
 * on the server reads every record in it whatever the take is, so the window costs a larger
 * response and no more work there - but it is still a window, and the table says so when the range
 * holds more entries than fit in it.
 *
 * The search box above the table is the other half of this, and the one to reach for when the
 * window is not enough: it is read on the server, over every entry in the range.
 */
const filterWindow = 5000;

/** The key the time column's filter is kept under. No log declares a property named like this. */
const timeKey = "*time";

/**
 * A search as it was asked for: the text and whether case was being told apart.
 *
 * The field is not this. A search reads every record of the range - minutes of it, on a log of a
 * busy database kept for a month - so it is run when it is asked for and not while it is being
 * written. This is what was asked for, and the page holds it until it is asked for again.
 */
interface AppliedSearch {
  text: string;
  caseSensitive: boolean;
}
const noSearch: AppliedSearch = { text: "", caseSensitive: false };

/**
 * How the two panels of a log share the page: whether each is open, and how tall the graph is.
 *
 * One reader comes for the graph and one comes for the entries, and they are usually the same
 * reader an hour apart - so either panel folds away to its heading, and the divider between them
 * gives the graph as much of the page as it is worth today. It is kept for every log rather than
 * per log: a reader who has folded the graph away has folded away graphs, not this one's.
 */
interface LogsLayout {
  statistics: boolean;
  entries: boolean;
  chartHeight: number;
}
const defaultLayout: LogsLayout = { statistics: true, entries: true, chartHeight: 210 };
const layoutKey = "logs:layout";
const minChartHeight = 90;
const maxChartHeight = 900;

function readLayout(): LogsLayout {
  try {
    const saved = localStorage.getItem(layoutKey);
    if (!saved) return defaultLayout;
    const parsed = JSON.parse(saved) as Partial<LogsLayout>;
    return {
      statistics: parsed.statistics !== false,
      entries: parsed.entries !== false,
      // a height saved by a version that drew the graph differently is still a number
      chartHeight:
        typeof parsed.chartHeight === "number" && isFinite(parsed.chartHeight)
          ? Math.min(maxChartHeight, Math.max(minChartHeight, parsed.chartHeight))
          : defaultLayout.chartHeight,
    };
  } catch {
    return defaultLayout; // private windows and cleared site data: the default is no worse
  }
}
function writeLayout(layout: LogsLayout) {
  try {
    localStorage.setItem(layoutKey, JSON.stringify(layout));
  } catch {
    // nothing to do about it, and nothing depends on it holding
  }
}

function LogTab({ db, log, onChanged }: { db: DatabaseInfo; log: LogInfo; onChanged: () => void }) {
  const [rangeId, setRangeId] = useState("24h");
  const [seriesKey, setSeriesKey] = useState(seriesId(log.series[0]));
  const [series, setSeries] = useState<SeriesData | null>(null);
  const [seriesError, setSeriesError] = useState<string | null>(null);
  const [page, setPage] = useState<LogPage | null>(null);
  const [pageError, setPageError] = useState<string | null>(null);
  const [skip, setSkip] = useState(0);
  const [live, setLive] = useState(false);
  const [tick, setTick] = useState(0); // an explicit refresh: a button, or an action that changed something
  const [filters, setFilters] = useState<Record<string, string>>({});
  const [downloading, setDownloading] = useState(false);
  // what the search box holds and what has been asked for: the field runs ahead of the search
  // until the button (or Enter) sends it, since reading the range is what a search costs
  const [searchText, setSearchText] = useState("");
  const [matchCase, setMatchCase] = useState(false);
  const [applied, setApplied] = useState<AppliedSearch>(noSearch);
  const [loading, setLoading] = useState(false);
  const [layout, setLayout] = useState<LogsLayout>(readLayout);
  const [resizing, setResizing] = useState(false);
  const range = ranges.find((r) => r.id === rangeId) ?? ranges[2];
  const selected = log.series.find((s) => seriesId(s) === seriesKey) ?? log.series[0];
  const columns = log.columns;

  // What the filter row holds, as one needle per column that has something in it. The empty ones
  // are dropped here, so a field typed into and emptied again is the same as one never touched.
  const needles = Object.entries(filters)
    .map(([key, text]) => ({ key, column: columns.find((c) => c.key === key) ?? null, needle: text.trim().toLowerCase() }))
    .filter((f) => f.needle.length > 0);
  const filtering = needles.length > 0;
  const searching = applied.text.length > 0; // what the server was asked for, not what the field holds
  // the field says something the last search did not, so there is a search to run
  const unsearched = searchText.trim() !== applied.text || matchCase !== applied.caseSensitive;
  const filterKey = needles.map((f) => f.key + "=" + f.needle).join("\n"); // what a fetch or a page reset depends on

  useEffect(() => setSkip(0), [rangeId, log.key, filterKey, applied]);

  // Runs what the field holds. A search asked for again with the same words is run again rather
  // than ignored - the button is also how a search is repeated over what has been recorded since.
  function runSearch(text = searchText, caseSensitive = matchCase) {
    setApplied({ text: text.trim(), caseSensitive });
  }
  // Emptying the field needs no button: there is nothing to read, and the whole range is what the
  // table falls back to.
  function clearSearch() {
    setSearchText("");
    if (searching) setApplied(noSearch);
  }
  // The case button is a search of its own once one is on screen: it was clicked to see the answer
  // change, not to arm a second click.
  function toggleCase(on: boolean) {
    setMatchCase(on);
    if (searching) runSearch(searchText, on);
  }

  function togglePanel(which: "statistics" | "entries") {
    setLayout((current) => {
      const next = { ...current, [which]: !current[which] };
      writeLayout(next);
      return next;
    });
  }
  // The divider sizes the graph above it, in pixels, the way the dashboard's rows are sized: the
  // page scrolls, so there is no total height to hand back and forth between the two panels.
  function startResize(e: React.MouseEvent) {
    e.preventDefault();
    const startY = e.clientY;
    const startHeight = layout.chartHeight;
    setResizing(true);
    document.body.style.cursor = "row-resize";
    const move = (ev: MouseEvent) => {
      const height = Math.min(maxChartHeight, Math.max(minChartHeight, startHeight + ev.clientY - startY));
      setLayout((current) => ({ ...current, chartHeight: height }));
    };
    const up = () => {
      window.removeEventListener("mousemove", move);
      window.removeEventListener("mouseup", up);
      document.body.style.cursor = "";
      setResizing(false);
      setLayout((current) => {
        writeLayout(current); // once, at the end: a height is not worth a write per pixel
        return current;
      });
    };
    window.addEventListener("mousemove", move);
    window.addEventListener("mouseup", up);
  }
  function resetChartHeight() {
    setLayout((current) => {
      const next = { ...current, chartHeight: defaultLayout.chartHeight };
      writeLayout(next);
      return next;
    });
  }
  // A filter pages in the browser, over one window of the newest entries; without one the server
  // pages, a hundred rows at a time. So typing in a filter field never fetches anything: only
  // arriving at one, or leaving the last one, changes what is asked for.
  const take = filtering ? filterWindow : pageSize;
  const windowSkip = filtering ? 0 : skip;

  // Both halves of the page cover the same range, and the range ends now: it is asked for as "the
  // last so many milliseconds" rather than as two timestamps, so the server works out where now is
  // on every sample and a log being watched slides along instead of standing still at the moment the
  // page was opened (see UILogs.cs).
  //
  // The graph is read from the statistics and costs nothing, so it follows the live switch. The
  // entries are read from the log files, and a search reads every one of them in the range -
  // repeating that is the one thing the button is there to stop. So a live refresh leaves an applied
  // search alone, and the refresh button is how it is run over what has come in since.
  const applySeries = useCallback((data: SeriesData) => {
    setSeries(data);
    setSeriesError(null);
  }, []);
  const failSeries = useCallback((message: string) => {
    setSeries(null);
    setSeriesError(message);
  }, []);
  // one sample per bucket at the fastest: a graph drawn a second at a time that only moved every
  // five seconds would stand still for five of its points and then jump
  useLive<SeriesData>(
    "logs-series",
    { storeId: db.id, logKey: log.key, property: selected.property, statistic: selected.statistic, interval: range.interval, lastMs: range.ms },
    applySeries,
    { once: !live, restartOn: tick, minMs: range.interval === "Second" ? 1000 : 5000, onError: failSeries },
  );

  const applyPage = useCallback((p: LogPage) => {
    setPage(p);
    setPageError(null);
    setLoading(false);
  }, []);
  const failPage = useCallback((message: string) => {
    setPage(null);
    setPageError(message);
    setLoading(false);
  }, []);
  useLive<LogPage>(
    "logs-extract",
    {
      storeId: db.id,
      logKey: log.key,
      lastMs: range.ms,
      skip: windowSkip,
      take,
      search: applied.text.length > 0 ? applied.text : null,
      caseSensitive: applied.caseSensitive,
    },
    applyPage,
    { once: !live || searching, restartOn: tick, minMs: range.interval === "Second" ? 1000 : 5000, onError: failPage },
  );

  // the search button says it is working from the moment it is pressed until the entries arrive
  useEffect(() => {
    setLoading(true);
  }, [applied, db.id, log.key, range, windowSkip, take]);

  function setFilter(key: string, text: string) {
    setFilters((current) => ({ ...current, [key]: text }));
  }

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

  /**
   * The download, asked for by range first.
   *
   * The range the page is showing is the one usually wanted, but a log is kept for days beyond it,
   * and reading the whole of it is a walk through every file it has - a choice worth making on
   * purpose rather than discovering as a wait. So both are offered, each saying how much it is.
   *
   * A search holds for the file as well as for the table: the file is the entries on screen, not
   * the ones the search was written to leave out. Searching the whole log is the way to reach past
   * the range without widening it, so the search is said in both choices.
   */
  async function download() {
    const to = new Date();
    const from = new Date(to.getTime() - range.ms);
    const inRange = page ? ` · ${formatCount(page.total)} ${page.total === 1 ? "entry" : "entries"}` : "";
    const matching = searching ? " matching the search" : "";
    const choices = [{ label: `The last ${range.label}${inRange}`, hint: "the range the page is showing" + matching }];
    // what the log holds beyond the range is only known once its files have been looked at
    if (log.firstRecordUtc && log.lastRecordUtc) {
      choices.push({
        label: "The whole log",
        hint: `${formatTime(log.firstRecordUtc)} — ${formatTime(log.lastRecordUtc)} · ${formatBytes(log.logBytes)} on disk${matching}`,
      });
    }
    const picked = await showChoice(
      "Download as tab separated text",
      searching ? `How much of this log should be searched for "${applied.text}"?` : "How much of this log should the file hold?",
      choices,
    );
    if (picked === null) return;
    setDownloading(true);
    try {
      // both bounds left out is the whole log, however far back its files reach
      if (picked === 0) await downloadLogTsv(db.id, log.key, from.toISOString(), to.toISOString(), applied.text, applied.caseSensitive);
      else await downloadLogTsv(db.id, log.key, null, null, applied.text, applied.caseSensitive);
    } catch (e) {
      await showError("Could not download the log", e instanceof Error ? e.message : String(e));
    } finally {
      setDownloading(false);
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
  // every filter has to match, so a row narrows with each field typed into. A filter takes the
  // same wildcards the search box does, over what the browser holds rather than the whole range
  const matches = filtering
    ? entries.filter((entry) => needles.every((f) => haystack(entry, f.key, f.column).some((text) => matchesTerm(text, f.needle))))
    : entries;
  const rows = filtering ? matches.slice(skip, skip + pageSize) : matches;
  const total = filtering ? matches.length : (page?.total ?? 0);
  // the range holds more than the filter window brought back, so the filter has not seen all of it
  const beyondWindow = filtering && (page?.total ?? 0) > entries.length;
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
        <button
          className={"action-button" + (live ? " armed" : "")}
          onClick={() => setLive(!live)}
          title={
            (range.interval === "Second" ? "Refresh every second" : "Refresh every five seconds") +
            (searching ? " — a search is not run again on a timer, only by the Search button" : "")
          }
        >
          <IconReload size={15} stroke={1.8} /> Live
        </button>
        {/* the sizes and the first and last record of the log come with its description, so a
            refresh reloads that too - the download offers the whole log out of it */}
        <button
          className="action-button"
          onClick={() => {
            onChanged();
            setTick((t) => t + 1);
          }}
          title="Refresh now"
        >
          <IconReload size={15} stroke={1.8} />
        </button>
        <button
          className="action-button"
          onClick={download}
          disabled={downloading || (!log.firstRecordUtc && total === 0)}
          title={log.firstRecordUtc || total > 0 ? "Save the entries as a tab separated file" : "This log has recorded nothing to download"}
        >
          <IconDownload size={15} stroke={1.8} /> {downloading ? "Writing…" : "Download"}
        </button>
        <button className="action-button" onClick={() => clear({ log: true, statistics: false })} title="Delete the recorded entries">
          <IconTrash size={15} stroke={1.8} /> Entries
        </button>
        <button className="action-button" onClick={() => clear({ log: false, statistics: true })} title="Delete the statistics">
          <IconEraser size={15} stroke={1.8} /> Statistics
        </button>
      </div>

      {/* the two panels and the divider that shares the page between them: the divider is the gap,
          so a folded graph leaves nothing to drag and the panels sit together */}
      <div className={"logs-split" + (resizing ? " resizing" : "")}>
      <section className={"panel" + (layout.statistics ? "" : " folded")}>
        <h3 className="with-fold">
          <FoldButton open={layout.statistics} label="the statistics" onToggle={() => togglePanel("statistics")} />
          Statistics <span className="panel-sub">{summaryText(series)}</span>
        </h3>
        {layout.statistics && (
          <>
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
              height={layout.chartHeight}
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
          </>
        )}
      </section>

      {layout.statistics && (
        <div
          className={"pg-bar pg-hbar logs-divider" + (resizing ? " active" : "")}
          role="separator"
          aria-orientation="horizontal"
          aria-label="Resize the graph"
          onMouseDown={startResize}
          onDoubleClick={resetChartHeight}
          title="Drag to resize the graph — double-click to reset"
        />
      )}

      <section className={"panel" + (layout.entries ? "" : " folded")}>
        <h3 className="with-fold">
          <FoldButton open={layout.entries} label="the entries" onToggle={() => togglePanel("entries")} />
          Entries <span className="panel-sub">{entriesText(range.label, total, skip, rows.length, filtering, entries.length, searching)}</span>
          {filtering && layout.entries && (
            <button className="link-button" onClick={() => setFilters({})} title="Empty every filter field">
              Clear filter
            </button>
          )}
        </h3>
        {layout.entries && (
          <>
        {/* The search reads the whole range on the server, the filter row under the headings only
            what the browser holds: this is the one to reach for when the range is large. */}
        <SearchBox
          value={searchText}
          onChange={setSearchText}
          onSearch={() => runSearch()}
          onClear={clearSearch}
          caseSensitive={matchCase}
          onCaseSensitive={toggleCase}
          unsearched={unsearched}
          busy={loading && searching}
          rangeLabel={range.label}
        />
        {!log.enabledLog && (
          <div className="logs-note">
            Entries are not being recorded.
            <button className="link-button" onClick={() => toggle({ log: true })}>
              Start recording
            </button>
          </div>
        )}
        {pageError && <div className="logs-note">{pageError}</div>}
        {beyondWindow && (
          <div className="logs-note">
            The filter searches the newest {formatCount(entries.length)} of the {formatCount(page?.total ?? 0)}{" "}
            {searching ? "entries matching the search" : "entries in this range"}; the older ones are not searched.
            {searching ? " The search itself reads every one of them." : " The search box above reads every one of them."}
          </div>
        )}
        <div className="log-table">
          <div className="log-table-row log-table-head with-filter-row" style={rowStyle}>
            <span>Time</span>
            {columns.map((c) => (
              <span key={c.key}>{c.name}</span>
            ))}
          </div>
          {/* one field per column, under the heading it filters, so which column it narrows needs no
              saying. They search what the browser has, so typing in one fetches nothing */}
          <div className="log-table-row log-table-filter" style={rowStyle}>
            <FilterCell columnKey={timeKey} label="Time" value={filters[timeKey] ?? ""} onChange={setFilter} />
            {columns.map((c) => (
              <FilterCell key={c.key} columnKey={c.key} label={c.name} value={filters[c.key] ?? ""} onChange={setFilter} />
            ))}
          </div>
          {rows.map((entry, i) => (
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
          {rows.length === 0 && (
            <div className="log-table-empty">
              {filtering
                ? "Nothing matches the filter."
                : searching
                  ? `Nothing in the last ${range.label} matches the search.`
                  : "No entries in this range."}
            </div>
          )}
        </div>
        {total > pageSize && (
          <div className="logs-paging">
            <button className="action-button" disabled={skip === 0} onClick={() => setSkip(Math.max(0, skip - pageSize))}>
              <IconChevronLeft size={15} stroke={1.8} /> Newer
            </button>
            <button className="action-button" disabled={skip + pageSize >= total} onClick={() => setSkip(skip + pageSize)}>
              Older <IconChevronRight size={15} stroke={1.8} />
            </button>
          </div>
        )}
          </>
        )}
      </section>
      </div>
    </div>
  );
}

/** Folds a panel away to its heading, and back. The heading keeps saying what is in there. */
function FoldButton({ open, label, onToggle }: { open: boolean; label: string; onToggle: () => void }) {
  return (
    <button className="panel-fold" onClick={onToggle} title={(open ? "Fold away " : "Open ") + label} aria-expanded={open}>
      {open ? <IconChevronDown size={14} stroke={2} /> : <IconChevronRight size={14} stroke={2} />}
    </button>
  );
}

/**
 * The search over the whole range.
 *
 * It is read on the server, which tests every record in the range against it: a search of the last
 * hour is an instant, and a search of a month of a busy log is a wait of minutes. So it is asked
 * for, not typed into - the button (or Enter) is what sends it, and the field says what would be
 * sent until then. What may be written in it is otherwise only found by guessing, so the syntax
 * stands under the field while it is in use.
 */
function SearchBox({
  value,
  onChange,
  onSearch,
  onClear,
  caseSensitive,
  onCaseSensitive,
  unsearched,
  busy,
  rangeLabel,
}: {
  value: string;
  onChange: (text: string) => void;
  onSearch: () => void;
  onClear: () => void;
  caseSensitive: boolean;
  onCaseSensitive: (on: boolean) => void;
  /** the field says something the last search did not, so the button has something to do */
  unsearched: boolean;
  busy: boolean;
  rangeLabel: string;
}) {
  const [showHelp, setShowHelp] = useState(false);
  return (
    <div className="logs-search">
      <div className={"logs-search-field" + (value.trim() ? " active" : "")}>
        <IconSearch size={15} stroke={1.8} />
        <input
          value={value}
          placeholder={`Search the last ${rangeLabel} — timeout, get*nodes, "could not open", -shutdown, type:error`}
          onChange={(e) => onChange(e.currentTarget.value)}
          onFocus={() => setShowHelp(true)}
          onKeyDown={(e) => {
            if (e.key === "Enter") onSearch();
            if (e.key === "Escape") onClear();
          }}
          spellCheck={false}
          autoComplete="off"
        />
        {busy && <span className="logs-search-pending" title="Reading the range…" />}
        {value && (
          <button className="logs-search-clear" onClick={onClear} title="Empty the search and list every entry again">
            <IconX size={14} stroke={2} />
          </button>
        )}
      </div>
      {/* the button is marked while the field holds something unsearched, so a search written and
          left unsent is never mistaken for one the table is already answering */}
      <button
        className={"action-button" + (unsearched && value.trim() ? " primary" : "")}
        onClick={onSearch}
        disabled={busy}
        title={busy ? "Reading the range" : "Search the last " + rangeLabel + " — the whole of it, not only the entries on screen"}
      >
        <IconSearch size={15} stroke={1.8} /> {busy ? "Searching…" : "Search"}
      </button>
      <button
        className={"action-button" + (caseSensitive ? " armed" : "")}
        onClick={() => onCaseSensitive(!caseSensitive)}
        title={caseSensitive ? "Upper and lower case are told apart" : "Upper and lower case are the same"}
      >
        <IconLetterCase size={15} stroke={1.8} /> Match case
      </button>
      <button className="link-button" onClick={() => setShowHelp(!showHelp)} title="What a search may say">
        <IconHelpCircle size={14} stroke={1.8} />
      </button>
      {showHelp && (
        <div className="logs-search-help">
          {searchHelp.map(([term, meaning]) => (
            <span key={term} className="logs-search-help-item">
              <code>{term}</code> {meaning}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

/** One column's filter: the text that column has to contain for a row to be listed. */
function FilterCell({
  columnKey,
  label,
  value,
  onChange,
}: {
  columnKey: string;
  label: string;
  value: string;
  onChange: (key: string, text: string) => void;
}) {
  return (
    <input
      className={"log-filter-input" + (value.trim() ? " active" : "")}
      value={value}
      placeholder="Filter"
      title={`List only entries whose ${label} holds this — * and ? work here too`}
      onChange={(e) => onChange(columnKey, e.currentTarget.value)}
      onKeyDown={(e) => e.key === "Escape" && onChange(columnKey, "")}
    />
  );
}

/**
 * What a filter searches in one cell: the text the table shows, and the value it was made from.
 * Both, because a row count written "1,234" and recorded as 1234 has to be found by either, and a
 * time reads as the local clock on screen while the entry carries the UTC instant. They are two
 * strings rather than one, so a wildcard cannot run from the end of the one into the other.
 */
function haystack(entry: LogEntry, key: string, column: LogColumn | null): string[] {
  if (key === timeKey) return [formatTime(entry.timestampUtc).toLowerCase(), entry.timestampUtc.toLowerCase()];
  const value = entry.values[key];
  return [formatValue(value, column?.dataType ?? "String").toLowerCase(), value == null ? "" : String(value).toLowerCase()];
}

/** The line under the "Entries" heading: which rows are on screen, out of what. */
function entriesText(rangeLabel: string, total: number, skip: number, shown: number, filtering: boolean, searched: number, searching: boolean): string {
  if (filtering) {
    // a filter counts twice over: what it matched, and how much it was able to look at
    if (total === 0) return `nothing matches in the ${formatCount(searched)} ${searched === 1 ? "entry" : "entries"} searched`;
    if (total <= shown) return `${formatCount(total)} matching, of ${formatCount(searched)} searched`;
    return `${formatCount(skip + 1)}–${formatCount(skip + shown)} of ${formatCount(total)} matching, in ${formatCount(searched)} searched`;
  }
  // a search counts matches, and it read the whole range to know that number
  if (searching) {
    if (total === 0) return `nothing in the last ${rangeLabel} matches the search`;
    return `${formatCount(skip + 1)}–${formatCount(skip + shown)} of ${formatCount(total)} matching in the last ${rangeLabel}`;
  }
  if (total === 0) return `nothing recorded in the last ${rangeLabel}`;
  return `${formatCount(skip + 1)}–${formatCount(skip + shown)} of ${formatCount(total)} in the last ${rangeLabel}`;
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
  const apply = useCallback((t: TraceInfo) => {
    setTrace(t);
    setError(null);
  }, []);
  // the trace is what the database is saying right now, so it follows by default: while it does,
  // the server sends every new message rather than being asked for them. With the switch off it is
  // read once, and again whenever the refresh button asks
  useLive<TraceInfo>("logs-trace", { storeId: db.id }, apply, { once: !live, restartOn: tick, onError: setError });
  // and following means the newest line, at the top, is the one in view
  const term = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (live && term.current) term.current.scrollTop = 0;
  }, [trace, live]);

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
      {/* the dashboard's terminal, with the room of a page: a machine talking, shown the way it
          talks, newest first so the line that just arrived is where the eye already is */}
      <section className="panel panel-fill logs-trace">
        <h3>
          Trace <span className="panel-sub">{trace.open ? `${trace.entries.length} messages, newest first` : "the database is closed"}</span>
        </h3>
        <div className="term logs-term" ref={term}>
          {trace.entries.length > 0 && <div className="term-idle term-idle-top">_</div>}
          {trace.entries.map((entry, i) => (
            <div
              key={i}
              className={"term-line " + entry.type.toLowerCase() + (entry.details ? " clickable" : "")}
              onClick={() => entry.details && showInfo(entry.text, "", [entry.details])}
              title={entry.details ? "Click for the details" : undefined}
            >
              <span className="term-time">{formatTime(entry.timestampUtc)}</span>
              <span className={"term-tag " + entry.type.toLowerCase()}>{entry.type}</span>
              <span className="term-text">
                {entry.text}
                {entry.details && <span className="term-more"> · details</span>}
              </span>
            </div>
          ))}
          {trace.entries.length === 0 && <div className="term-empty">{trace.open ? "Nothing traced yet." : "Open the database to see its trace."}</div>}
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
  const apply = useCallback((s: ScanInfo) => {
    setScans(s);
    setError(null);
  }, []);
  // while the scans are being recorded they change with every query the database answers, so they
  // are followed; with the recording off they are read once, and again when something switches it
  useLive<ScanInfo>("logs-scans", { storeId: db.id }, apply, { once: !scans?.recording, restartOn: tick, onError: setError });

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
