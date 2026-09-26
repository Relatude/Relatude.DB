import { useCallback, useEffect, useRef, useState } from "react";
import {
  IconAlertTriangle,
  IconChartHistogram,
  IconChartLine,
  IconCode,
  IconDatabaseCog,
  IconFileAnalytics,
  IconFileImport,
  IconLayoutList,
  IconListDetails,
  IconPlus,
  IconReload,
  IconRefresh,
  IconSettings,
  IconTrash,
  IconX,
} from "@tabler/icons-react";
import "../customLogs.css";
import { CodeEditor } from "./CodeEditor";
import { DialogTools } from "./DialogTools";
import { Sparkline } from "./LogCharts";
import { Switch } from "./LogsSection";
import { CustomLogGraphs } from "./CustomLogGraphs";
import { CustomLogEntries } from "./CustomLogEntries";
import { CustomLogAnalyse } from "./CustomLogAnalyse";
import { CustomLogEditor } from "./CustomLogEditor";
import { CustomLogData } from "./CustomLogData";
import { showChoice, showConfirm, showError } from "../dialogs";
import { useLive } from "../live";
import { formatBytes, formatCount, formatDateTime } from "../format";
import { defaultRange, fromLocalInput, presetOf, rangeLabel, rangePresets, toLocalInput, formatAgo, type LogRange } from "../customLogRange";
import {
  definitionFromFileJson,
  deleteBrokenDefinition,
  enableCustomLog,
  readBrokenDefinition,
  recordingCode,
  reloadCustomLogs,
  repairBrokenDefinition,
  templates,
  type CustomLogLoadError,
  type CustomLogsInfo,
  type CustomLogSummary,
  type LogDefinition,
} from "../server/customLogs";
import type { DatabaseInfo } from "../server/serverInfo";
import { lint } from "../code/lint";

/**
 * The Logs page: logs someone defines for a database, rather than the logs the database keeps about
 * itself (the Activity page, LogsSection): what an application records - requests, orders, jobs, errors - defined here as
 * a list of columns, recorded by the application with store.CustomLogs.Record(...), and read back
 * here as graphs, entries and distributions.
 *
 * The page is a tab per log beside an overview of all of them. A log's own page switches between
 * five views of it - its graphs, its entries, the spread of one column's values, its definition, and
 * its files - which share one time range, so moving from a spike in a graph to the entries behind
 * it keeps looking at the same stretch of time.
 *
 * Definitions being edited are held here, not in the editor, so leaving the editor for the graphs
 * and coming back finds the edit where it was left; they are lost only when the page is.
 */

export type LogView = "graphs" | "entries" | "analyse" | "definition" | "data";

const views: { id: LogView; label: string; icon: typeof IconChartLine }[] = [
  { id: "graphs", label: "Graphs", icon: IconChartLine },
  { id: "entries", label: "Entries", icon: IconListDetails },
  { id: "analyse", label: "Analyse", icon: IconChartHistogram },
  { id: "definition", label: "Definition", icon: IconSettings },
  { id: "data", label: "Data", icon: IconDatabaseCog },
];

// the tabs that are not a log: a key cannot start with an asterisk, so these never collide with one
const overviewTab = "*overview";
const newTab = "*new";

function readStored(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null; // private windows and cleared site data
  }
}
function writeStored(key: string, value: string) {
  try {
    localStorage.setItem(key, value);
  } catch {
    // nothing depends on it holding
  }
}

export function CustomLogsSection({ db }: { db: DatabaseInfo }) {
  const [info, setInfo] = useState<CustomLogsInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tick, setTick] = useState(0);
  const tabKey = "customLogs:tab:" + db.id;
  const [tab, setTabState] = useState(() => readStored(tabKey) ?? overviewTab);
  const [view, setViewState] = useState<LogView>(() => (readStored("customLogs:view") as LogView | null) ?? "graphs");
  // the log being created, before it exists; and the unsaved edits of the logs that do
  const [newDraft, setNewDraft] = useState<LogDefinition | null>(null);
  const [drafts, setDrafts] = useState<Record<string, LogDefinition>>({});
  const [range, setRange] = useState<LogRange>(defaultRange);
  const [live, setLive] = useState(false);

  const apply = useCallback((i: CustomLogsInfo) => {
    setInfo(i);
    setError(null);
  }, []);
  // what every log holds changes as the application records, so the overview follows it - but no
  // faster than every few seconds: it reads the sizes and the first and last entry of every log
  useLive<CustomLogsInfo>("custom-logs-info", { storeId: db.id }, apply, { minMs: 5000, restartOn: tick, onError: setError });
  const refresh = useCallback(() => setTick((t) => t + 1), []);

  function setTab(next: string) {
    setTabState(next);
    writeStored(tabKey, next);
  }
  function setView(next: LogView) {
    setViewState(next);
    writeStored("customLogs:view", next);
  }

  async function startNew(from?: LogDefinition) {
    if (from) {
      setNewDraft(from);
      setTab(newTab);
      return;
    }
    if (newDraft) {
      // one new log at a time: the one already begun is the one to finish or throw away
      setTab(newTab);
      return;
    }
    const picked = await showChoice(
      "New log",
      "Start from a blank log or from one of these, and change it to fit: every column and setting can be edited before the log is created.",
      templates.map((t) => ({ label: t.name, hint: t.description })),
    );
    if (picked === null) return;
    const draft = templates[picked].make();
    // a template's key may be taken already: the editor says so, and a suffix is a better start
    if (draft.key && info?.logs.some((l) => l.key.toLowerCase() === draft.key.toLowerCase())) {
      let n = 2;
      while (info.logs.some((l) => l.key.toLowerCase() === `${draft.key}-${n}`)) n++;
      draft.key = `${draft.key}-${n}`;
    }
    setNewDraft(draft);
    setTab(newTab);
  }

  async function discardNew() {
    const { ok } = await showConfirm("Discard the new log", "Throw away the log that is being defined? Nothing of it has been saved.", {
      confirmLabel: "Discard",
      danger: true,
    });
    if (!ok) return;
    setNewDraft(null);
    setTab(overviewTab);
  }

  function setDraft(logKey: string, draft: LogDefinition | null) {
    setDrafts((current) => {
      const next = { ...current };
      if (draft) next[logKey] = draft;
      else delete next[logKey];
      return next;
    });
  }

  if (error && !info) return <div className="placeholder">{error}</div>;
  if (!info) return null;
  const log = info.logs.find((l) => l.key === tab) ?? null;
  // a tab for a log that is gone - deleted here, or in another window - falls back to the overview
  const activeTab = tab === newTab ? (newDraft ? newTab : overviewTab) : tab === overviewTab || log ? tab : overviewTab;

  return (
    <div className="logs clogs">
      <div className="logs-tabs">
        <button className={"logs-tab" + (activeTab === overviewTab ? " active" : "")} onClick={() => setTab(overviewTab)}>
          <IconLayoutList size={15} stroke={1.8} /> All logs
        </button>
        {info.logs.map((l) => (
          <button key={l.key} className={"logs-tab" + (activeTab === l.key ? " active" : "")} onClick={() => setTab(l.key)} title={l.description || l.key}>
            <IconFileAnalytics size={15} stroke={1.8} />
            {l.name || l.key}
            {drafts[l.key] && <span className="clog-draft-dot" title="Unsaved changes to the definition" />}
            {(l.enabledLog || l.enabledStatistics) && <span className="logs-rec" title="Recording" />}
          </button>
        ))}
        {newDraft && (
          <button className={"logs-tab" + (activeTab === newTab ? " active" : "")} onClick={() => setTab(newTab)}>
            <IconPlus size={15} stroke={1.8} /> {newDraft.name || "New log"}
            <span className="clog-draft-dot" title="Not created yet" />
          </button>
        )}
        <button className="logs-tab clog-add-tab" onClick={() => startNew()} title="Define a new log">
          <IconPlus size={15} stroke={1.8} /> {newDraft ? "" : "New log"}
        </button>
      </div>

      {activeTab === overviewTab ? (
        <Overview db={db} info={info} onOpen={setTab} onNew={startNew} onChanged={refresh} />
      ) : activeTab === newTab && newDraft ? (
        <CustomLogEditor
          db={db}
          info={info}
          log={null}
          draft={newDraft}
          onDraft={(d) => setNewDraft(d)}
          onDiscard={discardNew}
          onSaved={(key) => {
            setNewDraft(null);
            refresh();
            setTab(key);
            setView("data"); // a new log has nothing to graph yet: its page for recording a first entry
          }}
        />
      ) : log ? (
        <LogPage
          key={log.key}
          db={db}
          info={info}
          log={log}
          view={view}
          onView={setView}
          range={range}
          onRange={setRange}
          live={live}
          onLive={setLive}
          tick={tick}
          onChanged={refresh}
          draft={drafts[log.key] ?? null}
          onDraft={(d) => setDraft(log.key, d)}
          onDeleted={() => {
            setDraft(log.key, null);
            setTab(overviewTab);
            refresh();
          }}
          onDuplicate={(d) => startNew(d)}
        />
      ) : null}
    </div>
  );
}

// ---- one log ----

function LogPage({
  db,
  info,
  log,
  view,
  onView,
  range,
  onRange,
  live,
  onLive,
  tick,
  onChanged,
  draft,
  onDraft,
  onDeleted,
  onDuplicate,
}: {
  db: DatabaseInfo;
  info: CustomLogsInfo;
  log: CustomLogSummary;
  view: LogView;
  onView: (view: LogView) => void;
  range: LogRange;
  onRange: (range: LogRange) => void;
  live: boolean;
  onLive: (live: boolean) => void;
  tick: number;
  onChanged: () => void;
  draft: LogDefinition | null;
  onDraft: (draft: LogDefinition | null) => void;
  onDeleted: () => void;
  onDuplicate: (draft: LogDefinition) => void;
}) {
  // a refresh of this log's own panels, on top of the overview's: an action here has to be seen here
  const [localTick, setLocalTick] = useState(0);
  const changed = useCallback(() => {
    onChanged();
    setLocalTick((t) => t + 1);
  }, [onChanged]);
  // a search handed from one view to another - a value clicked in the analysis, say
  const [handedSearch, setHandedSearch] = useState<string | null>(null);

  async function toggle(change: { log?: boolean; statistics?: boolean }) {
    try {
      await enableCustomLog(db.id, log.key, change);
      changed();
    } catch (e) {
      showError("Could not change the log", e instanceof Error ? e.message : String(e));
    }
  }

  function showEntriesBetween(fromUtc: string, toUtc: string, label: string) {
    onRange({ kind: "between", fromUtc, toUtc, label });
    onView("entries");
  }
  function showEntriesMatching(search: string) {
    setHandedSearch(search);
    onView("entries");
  }

  const ranged = view === "graphs" || view === "entries" || view === "analyse";
  const refreshTick = tick + localTick;
  return (
    <div className="logs-body clog">
      <div className="clog-head">
        <div className="clog-title">
          <IconFileAnalytics size={20} stroke={1.7} className={log.enabledLog || log.enabledStatistics ? "log-icon on" : "log-icon"} />
          <h2>{log.name || log.key}</h2>
          <code className="clog-key" title="The key the application records into this log by">
            {log.key}
          </code>
          {log.lastRecordUtc && (
            <span className="muted clog-last" title={formatDateTime(log.lastRecordUtc)}>
              last entry {formatAgo(log.lastRecordUtc)}
            </span>
          )}
          <span className="logs-spacer" />
          <Switch label="Record" checked={log.enabledLog} title="Write every entry of this log to disk" onChange={(v) => toggle({ log: v })} />
          <Switch
            label="Statistics"
            checked={log.enabledStatistics}
            title="Aggregate the entries into the statistics the graphs are drawn from"
            onChange={(v) => toggle({ statistics: v })}
          />
        </div>
        {log.description && <div className="clog-desc">{log.description}</div>}
      </div>
      <div className="clog-bar">
        <div className="module-switch">
          {views.map((v) => {
            const Icon = v.icon;
            return (
              <button key={v.id} className={view === v.id ? "active" : ""} onClick={() => onView(v.id)}>
                <Icon size={14} stroke={1.8} /> {v.label}
                {v.id === "definition" && draft && <span className="clog-draft-dot" title="Unsaved changes" />}
              </button>
            );
          })}
        </div>
        {ranged && <RangeBar range={range} onRange={onRange} live={live} onLive={onLive} onRefresh={changed} showLive={view !== "analyse"} />}
      </div>

      {view === "graphs" ? (
        <CustomLogGraphs
          db={db}
          log={log}
          range={range}
          live={live}
          tick={refreshTick}
          onPick={showEntriesBetween}
          onTurnOnStatistics={() => toggle({ statistics: true })}
          onChanged={changed}
          onRecord={() => onView("data")}
        />
      ) : view === "entries" ? (
        <CustomLogEntries
          db={db}
          log={log}
          range={range}
          onRange={onRange}
          live={live}
          tick={refreshTick}
          handedSearch={handedSearch}
          onHandedSearchTaken={() => setHandedSearch(null)}
          onStartRecording={() => toggle({ log: true })}
        />
      ) : view === "analyse" ? (
        <CustomLogAnalyse db={db} log={log} range={range} tick={refreshTick} onShowMatching={showEntriesMatching} />
      ) : view === "definition" ? (
        <CustomLogEditor
          db={db}
          info={info}
          log={log}
          draft={draft}
          onDraft={onDraft}
          onDiscard={() => onDraft(null)}
          onSaved={() => {
            onDraft(null);
            changed();
          }}
          onDuplicate={onDuplicate}
        />
      ) : (
        <CustomLogData db={db} log={log} tick={refreshTick} onChanged={changed} onDeleted={onDeleted} onDuplicate={onDuplicate} onView={onView} />
      )}
    </div>
  );
}

/**
 * The stretch of time a log's views look at: one of the ranges that end now, or two moments typed
 * in, or the interval picked on a graph. A picked interval says where it came from and has a way
 * back to the range it was picked from.
 */
function RangeBar({
  range,
  onRange,
  live,
  onLive,
  onRefresh,
  showLive,
}: {
  range: LogRange;
  onRange: (range: LogRange) => void;
  live: boolean;
  onLive: (live: boolean) => void;
  onRefresh: () => void;
  showLive: boolean;
}) {
  // the range to go back to from a picked or typed one
  const lastPreset = useRef(range.kind === "last" ? range.presetId : "24h");
  if (range.kind === "last") lastPreset.current = range.presetId;
  const [editing, setEditing] = useState(false);
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  function openCustom() {
    const now = new Date();
    const fromIso = range.kind === "between" ? range.fromUtc : new Date(now.getTime() - (presetOf(range)?.ms ?? 86_400_000)).toISOString();
    const toIso = range.kind === "between" ? range.toUtc : now.toISOString();
    setFrom(toLocalInput(fromIso));
    setTo(toLocalInput(toIso));
    setEditing(true);
  }
  function applyCustom() {
    const f = fromLocalInput(from);
    const t = fromLocalInput(to);
    if (!f || !t || Date.parse(f) >= Date.parse(t)) return;
    onRange({ kind: "between", fromUtc: f, toUtc: t });
    setEditing(false);
  }
  const fixed = range.kind === "between";
  return (
    <div className="clog-range">
      <select
        className="select"
        value={fixed ? "*custom" : range.presetId}
        title="Time range"
        onChange={(e) => {
          const value = e.currentTarget.value;
          if (value === "*custom") openCustom();
          else {
            setEditing(false);
            onRange({ kind: "last", presetId: value });
          }
        }}
      >
        {rangePresets.map((p) => (
          <option key={p.id} value={p.id}>
            Last {p.label}
          </option>
        ))}
        <option value="*custom">{fixed ? "Between two moments" : "Between two moments…"}</option>
      </select>
      {fixed && !editing && (
        <span className="clog-range-chip" title="The views show this stretch of time">
          <button className="clog-range-chip-label" onClick={openCustom} title="Change the two moments">
            {rangeLabel(range)}
          </button>
          <button className="icon-button" onClick={() => onRange({ kind: "last", presetId: lastPreset.current })} title="Back to the last range that ends now">
            <IconX size={13} stroke={2} />
          </button>
        </span>
      )}
      {editing && (
        <span className="clog-range-edit">
          <input className="text-input" type="datetime-local" value={from} onChange={(e) => setFrom(e.currentTarget.value)} />
          <span className="muted">to</span>
          <input className="text-input" type="datetime-local" value={to} onChange={(e) => setTo(e.currentTarget.value)} />
          <button className="action-button primary" onClick={applyCustom} disabled={!from || !to || from >= to}>
            Show
          </button>
          <button className="action-button" onClick={() => setEditing(false)}>
            Cancel
          </button>
        </span>
      )}
      {showLive && (
        <button
          className={"action-button" + (live ? " armed" : "")}
          onClick={() => onLive(!live)}
          title={fixed ? "A range between two fixed moments does not move; Live refreshes what was recorded in it" : "Follow what is recorded, as it is recorded"}
        >
          <IconReload size={15} stroke={1.8} /> Live
        </button>
      )}
      <button className="action-button" onClick={onRefresh} title="Refresh now">
        <IconRefresh size={15} stroke={1.8} />
      </button>
    </div>
  );
}

// ---- every log at once ----

function Overview({
  db,
  info,
  onOpen,
  onNew,
  onChanged,
}: {
  db: DatabaseInfo;
  info: CustomLogsInfo;
  onOpen: (key: string) => void;
  onNew: (from?: LogDefinition) => void;
  onChanged: () => void;
}) {
  const importInput = useRef<HTMLInputElement>(null);
  const [broken, setBroken] = useState<CustomLogLoadError | null>(null);

  async function toggle(logKey: string, change: { log?: boolean; statistics?: boolean }) {
    try {
      await enableCustomLog(db.id, logKey, change);
      onChanged();
    } catch (e) {
      showError("Could not change the log", e instanceof Error ? e.message : String(e));
    }
  }

  async function reload() {
    try {
      const result = await reloadCustomLogs(db.id);
      onChanged();
      if (result.errors > 0) {
        showError(
          result.errors === 1 ? "A settings file could not be read" : "Some settings files could not be read",
          `${result.errors === 1 ? "One settings file in the log folder is" : `${result.errors} settings files in the log folder are`} not a log. They are listed under the logs, to be fixed or deleted.`,
        );
      }
    } catch (e) {
      showError("Could not read the settings files", e instanceof Error ? e.message : String(e));
    }
  }

  async function importFile(file: File) {
    try {
      const text = await file.text();
      onNew(definitionFromFileJson(text));
    } catch (e) {
      showError("Not a log definition", `${file.name} could not be read as the settings of a log: ${e instanceof Error ? e.message : String(e)}`);
    }
  }

  const empty = info.logs.length === 0;
  return (
    <div className="logs-body">
      {!info.open && (
        <div className="logs-note">
          The database is {info.state.toLowerCase()}. Its logs can still be defined, read and changed here, but the application records into them only
          while the database is open.
        </div>
      )}
      <section className="panel">
        <h3>
          Logs{" "}
          <span className="panel-sub">
            {empty ? "none defined yet" : `${info.logs.length} ${info.logs.length === 1 ? "log" : "logs"} · ${formatBytes(info.totalBytes)} on disk`}
          </span>
        </h3>
        <div className="logs-toolbar clog-overview-toolbar">
          <button className="action-button primary" onClick={() => onNew()}>
            <IconPlus size={15} stroke={1.8} /> New log
          </button>
          <button className="action-button" onClick={() => importInput.current?.click()} title="Start a new log from a settings file saved from this or another database">
            <IconFileImport size={15} stroke={1.8} /> Import definition
          </button>
          <input
            ref={importInput}
            type="file"
            accept=".json,application/json"
            hidden
            onChange={(e) => {
              const file = e.currentTarget.files?.[0];
              e.currentTarget.value = "";
              if (file) importFile(file);
            }}
          />
          <span className="logs-spacer" />
          <button className="action-button" onClick={reload} title="Read the settings files in the log folder again, for definitions changed on disk">
            <IconReload size={15} stroke={1.8} /> Reload from disk
          </button>
        </div>
        {empty ? (
          <EmptyState onNew={onNew} />
        ) : (
          <div className="log-table">
            <div className="log-table-row log-table-head clog-overview-row">
              <span>Log</span>
              <span>Last 24 hours</span>
              <span className="num" title="Entries recorded in the last 24 hours">
                In 24 h
              </span>
              <span>Record</span>
              <span>Statistics</span>
              <span>Last entry</span>
              <span className="num">On disk</span>
              <span>Keeps</span>
            </div>
            {info.logs.map((log) => (
              <div key={log.key} className="log-table-row clog-overview-row clickable" onClick={() => onOpen(log.key)} title="Open this log">
                <span className="log-cell log-name">
                  <IconFileAnalytics size={15} stroke={1.8} className={log.enabledLog || log.enabledStatistics ? "log-icon on" : "log-icon"} />
                  <span className="clog-name-text">
                    {log.name || log.key}
                    <span className="clog-name-key">{log.key}</span>
                  </span>
                </span>
                <span>
                  {log.activity ? (
                    <Sparkline values={log.activity} title="Entries per hour, the last 24 hours" />
                  ) : (
                    <span className="muted" title="Statistics are off, and they are what this is drawn from">
                      —
                    </span>
                  )}
                </span>
                <span className="num">{log.entriesLastDay == null ? "—" : formatCount(log.entriesLastDay)}</span>
                {/* the switches are their own targets: clicking one must not also open the log */}
                <span onClick={(e) => e.stopPropagation()}>
                  <Switch checked={log.enabledLog} onChange={(v) => toggle(log.key, { log: v })} />
                </span>
                <span onClick={(e) => e.stopPropagation()}>
                  <Switch checked={log.enabledStatistics} onChange={(v) => toggle(log.key, { statistics: v })} />
                </span>
                <span className="log-cell muted" title={log.lastRecordUtc ? formatDateTime(log.lastRecordUtc) : undefined}>
                  {log.lastRecordUtc ? formatAgo(log.lastRecordUtc) : "nothing yet"}
                </span>
                <span className="num">{log.totalBytes > 0 ? formatBytes(log.totalBytes) : "—"}</span>
                <span className="log-cell muted">{keepsText(log)}</span>
              </div>
            ))}
          </div>
        )}
      </section>

      {info.loadErrors.length > 0 && (
        <section className="panel clog-broken">
          <h3>
            <IconAlertTriangle size={15} stroke={1.8} /> Settings files that are not a log{" "}
            <span className="panel-sub">in the log folder, and left alone until they are fixed or deleted</span>
          </h3>
          {info.loadErrors.map((e) => (
            <div key={e.fileKey} className="clog-broken-row">
              <code>{e.fileKey}</code>
              <span className="clog-broken-message">{e.message}</span>
              <button className="action-button" onClick={() => setBroken(e)}>
                Fix…
              </button>
              <button
                className="action-button danger"
                onClick={async () => {
                  const { ok } = await showConfirm("Delete the settings file", `Delete ${e.fileKey}? What a log recorded under it is not touched.`, {
                    confirmLabel: "Delete",
                    danger: true,
                  });
                  if (!ok) return;
                  try {
                    await deleteBrokenDefinition(db.id, e.fileKey);
                    onChanged();
                  } catch (error) {
                    showError("Could not delete the file", error instanceof Error ? error.message : String(error));
                  }
                }}
              >
                <IconTrash size={15} stroke={1.8} />
              </button>
            </div>
          ))}
        </section>
      )}

      {!empty && (
        <section className="panel">
          <h3>
            <IconCode size={15} stroke={1.8} /> Recording from the application <span className="panel-sub">what a log is for: the application writes into it</span>
          </h3>
          <div className="clog-code-note muted">
            A log records nothing until the application does. The key is all it needs: each log's Definition page has the lines for its own columns.
          </div>
          <CodeEditor value={recordingCode(info.logs[0] ? { key: info.logs[0].key, properties: info.logs[0].columns } : { key: "", properties: [] })} onChange={() => {}} language="csharp" issues={[]} readOnly />
        </section>
      )}

      {broken && <BrokenFileDialog db={db} error={broken} onClose={() => setBroken(null)} onFixed={() => {
        setBroken(null);
        onChanged();
      }} />}
    </div>
  );
}

function keepsText(log: CustomLogSummary): string {
  const age = log.maxAgeInDays > 0 ? `${log.maxAgeInDays} days` : "no age limit";
  const size = log.maxSizeInMb > 0 ? `${log.maxSizeInMb} MB` : "no size limit";
  return `${age} · ${size}`;
}

function EmptyState({ onNew }: { onNew: (from?: LogDefinition) => void }) {
  return (
    <div className="clog-empty">
      <p>
        A log here is one of your own: a list of columns, each with a type and the statistics worth keeping about it. The application records into it by
        its key, and this page draws what it recorded - graphs over time, the entries themselves, and how the values are spread.
      </p>
      <div className="clog-templates">
        {templates.map((t) => (
          <button key={t.id} className="clog-template" onClick={() => onNew(t.make())}>
            <span className="clog-template-name">{t.name}</span>
            <span className="clog-template-desc">{t.description}</span>
          </button>
        ))}
      </div>
    </div>
  );
}

/**
 * A settings file in the log folder that did not read as a log, opened to be fixed: its text as it
 * is, in an editor that marks what does not parse, saved back only once the server reads it as a log.
 */
function BrokenFileDialog({ db, error, onClose, onFixed }: { db: DatabaseInfo; error: CustomLogLoadError; onClose: () => void; onFixed: () => void }) {
  const [text, setText] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  useEffect(() => {
    let cancelled = false;
    readBrokenDefinition(db.id, error.fileKey)
      .then((r) => !cancelled && setText(r.text))
      .catch((e) => !cancelled && setProblem(e instanceof Error ? e.message : String(e)));
    return () => {
      cancelled = true;
    };
  }, [db.id, error.fileKey]);
  async function save() {
    if (text == null) return;
    setSaving(true);
    try {
      await repairBrokenDefinition(db.id, error.fileKey, text);
      onFixed();
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }
  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="dialog dialog-wide clog-broken-dialog">
        <h3>
          <IconAlertTriangle size={16} stroke={2} /> {error.fileKey}
          <DialogTools onClose={onClose} closeTitle="Cancel" />
        </h3>
        <div className="dialog-body">{problem ?? error.message}</div>
        {text != null && (
          <div className="clog-json-editor">
            <CodeEditor value={text} onChange={setText} language="json" issues={lint(text, "json")} onSave={save} />
          </div>
        )}
        <div className="dialog-row">
          <span className="header-spacer" />
          <button className="action-button primary" onClick={save} disabled={saving || text == null}>
            {saving ? "Saving…" : "Save and start the log"}
          </button>
          <button className="action-button" onClick={onClose}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}
