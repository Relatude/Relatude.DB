import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  IconAlertTriangle,
  IconChartDonut,
  IconChartTreemap,
  IconCube3dSphere,
  IconDatabaseSearch,
  IconEraser,
  IconEyeOff,
  IconLayoutList,
  IconPlayerPlayFilled,
  IconPlayerStopFilled,
  IconRecycle,
  IconRefresh,
  IconReload,
  IconSchema,
} from "@tabler/icons-react";
import { Chart } from "./Chart";
import { ProcessChart, currentCpu, formatPercent, padToWindow, type ProcessSample } from "./ProcessChart";
import { PanelGrid, type PanelRow } from "./PanelGrid";
import { TypeChart, otherSliceId, shade, type TypeChartShape, type TypeSlice } from "./TypeChart";
import { KindIcon } from "./DatamodelIcons";
import { openInDatamodel, openInQuery } from "../navigate";
import { showConfirm, showError, showInfo } from "../dialogs";
import { clearCaches, fetchDashboard, fetchDashboardLive, type DashboardInfo, type DashboardLive, type TypeCount } from "../server/dashboard";
import { codeSourceGuid, sourceColors } from "../server/datamodel";
import { fetchTrace, type TraceInfo } from "../server/logs";
import { useMeasuredEvery, usePoll, useRefreshInterval } from "../refresh";
import { collectGarbage } from "../server/overview";
import { closeStore, openStore } from "../server/storage";
import type { DatabaseInfo } from "../server/serverInfo";
import type { SeriesPoint } from "../server/logs";
import { formatBytes, formatCount, formatDuration, formatTime } from "../format";
import "../datamodel.css";

/**
 * The landing page of one database: what it holds, what it is doing right now, and anything worth
 * noticing before the other sections.
 *
 * The rate graph is built here rather than on the server. The database keeps cumulative counters
 * (queries, transactions, actions, node reads) which cost nothing to read; this page samples them
 * and takes the difference, so the graph exists without the activity log being recorded, and
 * without asking the store for anything expensive on a timer. Those counters reset when the caches
 * are cleared, which the store does on its own schedule - a drop is therefore a gap in the graph,
 * never a negative rate.
 */
// the full picture takes the store's write lock and counts every type, backup and state file: worth
// it when the page opens, never worth it on the refresh rate, however fast that is set
const infoIntervalMs = 60000;
// three minutes of history at the default rate: enough to see a burst arrive and drain
const maxSamples = 90;

/** A reading of the counters, taken together with one of the process (see ProcessChart). */
interface Sample extends ProcessSample {
  queries: number;
  transactions: number;
  actions: number;
  nodeReads: number;
}

/**
 * What the graph can draw, and how the samples turn into it. A "rate" is a cumulative counter and is
 * drawn as its difference per second; a "level" is a measurement that already stands on its own and
 * is drawn as it was sampled. Mixing the two would be a graph that lies: the difference between two
 * memory readings is not a rate of anything, and the level of a counter is only how long the
 * database has been up.
 */
const metrics = [
  { id: "queries", label: "Queries", kind: "rate", unit: "queries/s" },
  { id: "transactions", label: "Transactions", kind: "rate", unit: "transactions/s" },
  { id: "actions", label: "Actions", kind: "rate", unit: "actions/s" },
  { id: "nodeReads", label: "Node reads", kind: "rate", unit: "reads/s" },
  { id: "managedMemory", label: "Memory & CPU", kind: "level", unit: "in the managed heap" },
] as const;

type MetricId = (typeof metrics)[number]["id"];

export function DashboardSection({ db }: { db: DatabaseInfo }) {
  const [info, setInfo] = useState<DashboardInfo | null>(null);
  const [live, setLive] = useState<DashboardLive | null>(null);
  const [trace, setTrace] = useState<TraceInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [metric, setMetric] = useState<MetricId>("queries");
  const [openBusy, setOpenBusy] = useState(false);
  const [cacheBusy, setCacheBusy] = useState<"clear" | "collect" | null>(null);
  const [cacheMessage, setCacheMessage] = useState<string | null>(null);
  const samples = useRef<Sample[]>([]);
  const [, setSampleTick] = useState(0);
  const measuredEvery = useMeasuredEvery();
  const refreshMs = useRefreshInterval();

  const loadInfo = useCallback(async () => {
    try {
      setInfo(await fetchDashboard(db.id));
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, [db.id]);

  useEffect(() => {
    samples.current = [];
    loadInfo();
  }, [loadInfo]);
  usePoll(loadInfo, { minMs: infoIntervalMs });

  const sampleLive = useCallback(async () => {
    try {
      const sample = await fetchDashboardLive(db.id);
      setLive((previous) => {
        // the full picture is fetched once and would otherwise describe a database that has since
        // opened or closed - the live sample is what notices
        if (previous && previous.state !== sample.state) loadInfo();
        return sample;
      });
      if (sample.open) {
        samples.current = [
          ...samples.current,
          {
            at: new Date(sample.sampledUtc).getTime(),
            iso: sample.sampledUtc,
            queries: sample.queries ?? 0,
            transactions: sample.transactions ?? 0,
            actions: sample.actions ?? 0,
            nodeReads: sample.nodeReads ?? 0,
            managedMemory: sample.managedMemory ?? 0,
            processMemory: sample.processMemory ?? 0,
            processorTimeMs: sample.processorTimeMs ?? 0,
            processorCount: sample.processorCount ?? 1,
          },
        ].slice(-maxSamples);
        setSampleTick((t) => t + 1);
      }
    } catch {
      // a failed sample is a gap, not an error worth taking the page over
    }
  }, [db.id, loadInfo]);
  useEffect(() => {
    sampleLive();
  }, [sampleLive]);
  usePoll(sampleLive);

  // the last messages the database wrote, refreshed at the same pace as everything else here
  const loadTrace = useCallback(() => fetchTrace(db.id, 12).then(setTrace).catch(() => {}), [db.id]);
  useEffect(() => {
    loadTrace();
  }, [loadTrace]);
  usePoll(loadTrace);

  // the running activities, with the ones that just finished kept a moment longer so they fade out
  // instead of vanishing between two samples (a hook, so it sits here with the others, before the
  // early returns below)
  const running = useLeaving(live?.activities ?? [], activityKey, 450);

  async function onOpen() {
    setOpenBusy(true);
    try {
      await openStore(db.id);
      await loadInfo();
    } catch (e) {
      showError("Could not open the database", e instanceof Error ? e.message : String(e));
    } finally {
      setOpenBusy(false);
    }
  }

  // closed and opened again in one go: the settings and the model sources are read again on the way,
  // which is what a restart is for. It takes the database away for the duration, so it is asked about.
  async function onRestart() {
    const choice = await showConfirm(
      "Restart the database?",
      "It is closed and opened again: every index is flushed, the settings and the model sources are read again, and the log is replayed - which takes a while on a large one. Nothing is served from this database in between.",
      { confirmLabel: "Restart" },
    );
    if (!choice.ok) return;
    setOpenBusy(true);
    try {
      await closeStore(db.id);
      samples.current = []; // the counters start over with the open
      await openStore(db.id);
      await loadInfo();
    } catch (e) {
      showError("Could not restart the database", e instanceof Error ? e.message : String(e));
    } finally {
      setOpenBusy(false);
    }
  }

  // closing is the one thing on this page that takes the database away from whoever is using it,
  // so it is asked about first (see confirm-dialogs rule) - and never a second click on the same spot
  async function onClose() {
    const choice = await showConfirm(
      "Close the database?",
      "Every index is flushed and the file is released. Nothing is lost, but nothing is served from this database until it is opened again - which replays the log and takes a while on a large one.",
      { confirmLabel: "Close", danger: true },
    );
    if (!choice.ok) return;
    setOpenBusy(true);
    try {
      await closeStore(db.id);
      samples.current = []; // the counters start over with the next open
      await loadInfo();
    } catch (e) {
      showError("Could not close the database", e instanceof Error ? e.message : String(e));
    } finally {
      setOpenBusy(false);
    }
  }

  /**
   * Empties the caches of this database. Everything it held is read from the indexes again, so the
   * next queries are slower until they warm back up - which is the point when a measurement should
   * start from cold, and worth confirming when it is not.
   */
  async function onClearCaches() {
    const choice = await showConfirm(
      "Clear the caches",
      "Empties the node, result set and index caches of this database. Nothing is lost, but until they warm up again queries are"
        + " answered from the indexes instead of memory. The activity counters start over.",
      { confirmLabel: "Clear" },
    );
    if (!choice.ok) return;
    setCacheBusy("clear");
    try {
      const result = await clearCaches(db.id);
      setCacheMessage(
        `Cleared ${formatCount(result.entriesCleared)} entr${result.entriesCleared === 1 ? "y" : "ies"} in ${formatElapsed(result.elapsedMs)}` +
          `${result.freedBytes > 0 ? `, freeing ${formatBytes(result.freedBytes)}` : ""}.`,
      );
      await loadInfo();
    } catch (e) {
      showError("Could not clear the caches", e instanceof Error ? e.message : String(e));
    } finally {
      setCacheBusy(null);
    }
  }

  // the process, not this database: the collection is deep, blocking and compacting, and there is
  // one heap behind every database on this server
  async function onCollectGarbage() {
    setCacheBusy("collect");
    try {
      setCacheMessage((await collectGarbage()).message);
    } catch (e) {
      showError("Could not collect", e instanceof Error ? e.message : String(e));
    } finally {
      setCacheBusy(null);
    }
  }

  if (error) return <div className="placeholder">{error}</div>;
  if (!info) return null;

  // the live sample is seconds old, the full picture up to a minute: the state comes from the live one
  const state = live?.state ?? info.state;
  const open = state === "Open";
  const opening = state === "Opening";
  // counted from when the database opened rather than taken from the full picture, which is up to a
  // minute old: a clock that only moves once a minute reads as a broken one
  const uptime = open && info.openedUtc ? Math.max(0, Date.now() - new Date(info.openedUtc).getTime()) : null;
  const totalDisk = info.files.database + info.files.state + info.files.logs + info.files.backups + info.files.secondary;
  const chosen = metrics.find((m) => m.id === metric)!;
  const level = chosen.kind === "level";
  const measured = seriesPoints(samples.current, metric, chosen.kind);
  const current = measured.length > 0 ? (measured[measured.length - 1].value ?? 0) : 0;
  // the axis spans the whole window from the first sample on, and the line grows into it from the
  // right - a graph that stretches two points across the panel and then squeezes as more arrive
  // reads as activity that is not there
  const points = padToWindow(measured, maxSamples - 1, samples.current, refreshMs);
  const cpuNow = level ? currentCpu(samples.current) : null;

  const activityPanel = (
    <section className="panel panel-fill">
      <h3>
        Activity{" "}
        <span className="panel-sub">
          {level
            ? `${formatBytes(live?.managedMemory ?? 0)} in the managed heap · ${formatBytes(live?.processMemory ?? 0)} resident in the process${cpuNow === null ? "" : ` · ${formatPercent(cpuNow)} cpu`}`
            : `${formatRate(current)} ${chosen.unit} · ${formatCount(live?.[metric] ?? 0)} since the caches were last cleared`}
        </span>
        <button className="icon-button storage-refresh" title="Refresh" onClick={loadInfo}>
          <IconRefresh size={14} stroke={1.8} />
        </button>
      </h3>
      <div className="logs-series">
        {metrics.map((m) => (
          <button key={m.id} className={"logs-chip" + (m.id === metric ? " active" : "")} onClick={() => setMetric(m.id)}>
            {m.label}
          </button>
        ))}
      </div>
      {level ? (
        // the process, memory and cpu together: the same chart the server overview draws
        <ProcessChart samples={samples.current} maxSamples={maxSamples} />
      ) : (
        <Chart kind="sum" points={points} groups={[]} interval="Second" format={formatRate} height="fill" />
      )}
      <div className="logs-chart-foot">
        {level ? (
          // the two lines are on scales of their own, so which is which is worth saying in one word
          // each, in the colour it is drawn in; what the numbers are about takes four more
          <span className="muted">
            <span className="chart-key chart-key-second">CPU</span> · <span className="chart-key chart-key-first">Memory</span> · the whole server process
          </span>
        ) : (
          <span className="muted">measured here, {measuredEvery === "Off" ? "when the page is refreshed" : "every " + measuredEvery}</span>
        )}
      </div>
    </section>
  );

  // the running work takes the middle of the panel, scrolling when there is more than fits; the
  // two counts sit at the bottom whatever is running above them
  const nowPanel = (
    <section className="panel panel-fill">
      <h3>
        Right now <span className="panel-sub">{(live?.activities?.length ?? 0) === 0 ? "idle" : `${live!.activities!.length} running`}</span>
      </h3>
      <div className="dash-now fill-body">
        {running.map(({ item: a, key, leaving }) => (
          // keyed by what the activity is rather than its position, so a row is the same element
          // from the moment it appears to the moment it fades, and its bar moves rather than jumps
          <div key={key} className={"dash-activity" + (leaving ? " leaving" : "") + (a.percentageProgress != null ? " with-progress" : "")}>
            <span className="conv-chip dash-activity-cat" title={a.category}>
              {a.category}
            </span>
            <span className="log-cell dash-activity-text" title={a.description ?? undefined}>
              {a.description ?? "—"}
            </span>
            <span className="num dash-activity-pct">{a.percentageProgress != null ? `${Math.round(a.percentageProgress)}%` : ""}</span>
            {a.percentageProgress != null && (
              // the bar is two lines, not a colour: the track and how far along it the work is
              <span className="dash-progress" role="progressbar" aria-valuenow={Math.round(a.percentageProgress)} aria-valuemin={0} aria-valuemax={100}>
                <span className="dash-progress-fill" style={{ width: `${Math.min(100, Math.max(0, a.percentageProgress))}%` }} />
              </span>
            )}
          </div>
        ))}
        <div className={"muted dash-now-idle" + (running.length === 0 ? "" : " gone")}>Nothing running.</div>
      </div>
      <div className="facts-grid dash-now-facts">
        <Fact k="Background tasks" v={formatCount(live?.tasksQueued ?? 0)} />
        <Fact
          k="File conversions"
          v={
            (live?.conversions?.running ?? 0) + (live?.conversions?.queued ?? 0) === 0
              ? "none"
              : `${formatCount(live!.conversions!.running)} running · ${formatCount(live!.conversions!.queued)} queued`
          }
        />
      </div>
      {info.maintenance?.runningRewrite && <div className="logs-note">Rewriting {info.maintenance.runningRewrite}…</div>}
    </section>
  );

  // the three caches take the middle of the panel; the two actions and the line explaining them sit
  // at the bottom, where an action belongs when what it acts on is above it
  const cachePanel = (
    <section className="panel panel-fill">
      <h3>
        Caches <span className="panel-sub">what is being answered from memory</span>
      </h3>
      <div className="fill-body dash-cache-list">
      <CacheRow
        label="Nodes"
        count={live?.nodeCacheCount ?? 0}
        size={live?.nodeCacheSize ?? 0}
        fill={info.cache?.nodeCacheSizePercentage ?? 0}
        hits={info.cache?.nodeCacheHits ?? 0}
        misses={info.cache?.nodeCacheMisses ?? 0}
      />
      <CacheRow
        label="Result sets"
        count={live?.setCacheCount ?? 0}
        size={live?.setCacheSize ?? 0}
        fill={info.cache?.setCacheSizePercentage ?? 0}
        hits={info.cache?.setCacheHits ?? 0}
        misses={info.cache?.setCacheMisses ?? 0}
      />
      <CacheRow
        label="Aggregates"
        count={info.cache?.aggregateCacheCount ?? 0}
        size={null}
        fill={null}
        hits={info.cache?.aggregateCacheHits ?? 0}
        misses={info.cache?.aggregateCacheMisses ?? 0}
      />
      </div>
      {/* half a page wide, so the two actions sit side by side with one line under them,
          rather than each button pushing its own hint into a column too narrow to read */}
      <div className="dash-cache-actions">
        <button
          className="action-button"
          onClick={onClearCaches}
          disabled={cacheBusy !== null}
          title="Empties the caches of this database and frees the memory they held"
        >
          <IconEraser size={14} stroke={1.8} /> {cacheBusy === "clear" ? "Clearing…" : "Clear caches"}
        </button>
        <button
          className="action-button"
          onClick={onCollectGarbage}
          disabled={cacheBusy !== null}
          title="Deep, blocking, compacting collection of the whole server process"
        >
          <IconRecycle size={14} stroke={1.8} /> {cacheBusy === "collect" ? "Collecting…" : "Collect garbage"}
        </button>
      </div>
      <div className="muted dash-cache-note">
        {cacheMessage ?? "clearing empties this database and warms the indexes again in the background; collecting is the whole server process"}
      </div>
    </section>
  );

  const enginesPanel = (
    <section className="panel">
      <h3>
        Engines <span className="panel-sub">what is behind this database</span>
      </h3>
      <div className="facts-grid storage-facts">
        <Fact k="Text index" v={info.engines.textIndex} />
        <Fact k="Value indexes" v={info.engines.valueIndex} />
        <Fact k="Task queue" v={info.engines.queue} />
        <Fact k="Semantic index" v={info.engines.semanticIndex ?? "off"} />
        <Fact k="AI provider" v={info.ai?.provider ?? "none"} />
        <Fact k="Embedding model" v={info.ai?.embeddingModel ?? "—"} />
      </div>
      <div className="facts-grid storage-facts">
        <Fact k="Database file" v={formatBytes(info.files.database)} />
        <Fact k="State snapshot" v={formatBytes(info.files.state)} />
        <Fact k="Backups" v={formatBytes(info.files.backups)} />
        <Fact k="Activity logs" v={formatBytes(info.files.logs)} />
        <Fact k="Opened" v={info.openedUtc ? formatTime(info.openedUtc) : "—"} />
        <Fact k="Last change" v={info.lastChangeUtc ? formatTime(info.lastChangeUtc) : "—"} />
      </div>
    </section>
  );

  const openingPanel = (
    <section className="panel">
      <h3>
        Opening <span className="panel-sub">replaying the transaction log and rebuilding the indexes</span>
      </h3>
      <div className="dash-opening">
        <span className="progress-bar">
          <span className="progress-fill" style={{ width: Math.max(2, Math.min(100, live?.opening?.progressPercentage ?? 0)) + "%" }} />
        </span>
        <span className="num">{Math.round(live?.opening?.progressPercentage ?? 0)}%</span>
        <span className="muted">{live?.opening?.timeRemainingMs ? `about ${formatDuration(live.opening.timeRemainingMs)} left` : "estimating…"}</span>
      </div>
      <div className="dash-now">
        {(live?.activities ?? []).map((a, i) => (
          <div key={i} className="dash-activity">
            <span className="conv-chip">{a.category}</span>
            <span className="log-cell" title={a.description ?? undefined}>
              {a.description ?? "—"}
            </span>
            {a.percentageProgress != null && <span className="num">{a.percentageProgress}%</span>}
          </div>
        ))}
      </div>
    </section>
  );

  const closedPanel = (
    <section className="panel">
      <h3>
        Closed <span className="panel-sub">nothing is being served from this database</span>
      </h3>
      <div className="facts-grid storage-facts">
        <Fact k="Database file" v={formatBytes(info.files.database)} />
        <Fact k="State snapshot" v={formatBytes(info.files.state)} />
        <Fact k="Backups" v={formatBytes(info.files.backups)} />
        <Fact k="Activity logs" v={formatBytes(info.files.logs)} />
      </div>
      <div className="process-action">
        <button className="action-button" onClick={onOpen} disabled={openBusy}>
          <IconPlayerPlayFilled size={13} stroke={1.8} /> {openBusy ? "Opening…" : "Open database"}
        </button>
        <span className="muted">replays the transaction log and rebuilds the indexes</span>
      </div>
    </section>
  );

  // the same terminal as the server log on the overview: a machine talking, shown the way it talks.
  // Newest last, so a line that just arrived is where the eye already is - and a line carrying
  // details is still worth a click
  const tracePanel = (
    <section className="panel panel-fill">
      <h3>
        Latest messages <span className="panel-sub">the trace the database keeps in memory</span>
      </h3>
      <div className="term fill-body">
        {[...(trace?.entries ?? []).slice(0, 12)].reverse().map((entry, i) => (
          <div
            key={i}
            className={"term-line " + entry.type.toLowerCase() + (entry.details ? " clickable" : "")}
            onClick={() => entry.details && showInfo(entry.text, "", [entry.details])}
            title={entry.details ? "Click for the details" : undefined}
          >
            <span className="term-time">{formatTime(entry.timestampUtc)}</span>
            <span className={"term-tag " + entry.type.toLowerCase()}>{entry.type}</span>
            <span className="term-text">{entry.text}</span>
          </div>
        ))}
        {(trace?.entries ?? []).length === 0 && <div className="term-empty">{open ? "Nothing traced yet." : "Open the database to see its trace."}</div>}
        {(trace?.entries ?? []).length > 0 && <div className="term-idle">_</div>}
      </div>
    </section>
  );

  /**
   * The rows of the resizable grid, and the only place the page decides what sits beside what.
   * The ids are what a dragged height is remembered by, so the three states of a database never
   * inherit each other's - a closed one has a single panel where an open one has five.
   */
  const rows: PanelRow[] = opening
    ? [
        { id: "opening", cells: [openingPanel] },
        { id: "trace", cells: [tracePanel] },
      ]
    : !open
      ? [
          { id: "closed", cells: [closedPanel] },
          { id: "trace", cells: [tracePanel] },
        ]
      : [
          // rows given a height of their own hold a panel that fills whatever it is handed - a chart
          // or a terminal sized to its own content has no height at all
          // the caches take the sized row: three rows and two buttons have a shape of their own, and
          // what is running is a list of nothing most of the time, which does not need the height
          { id: "activity", height: 300, cells: [activityPanel, cachePanel] },
          { id: "engines", cells: [enginesPanel, nowPanel] },
          { id: "trace", height: 260, cells: [tracePanel, <ContentPanel key="content" info={info} storeId={db.id} />] },
        ];

  return (
    <div className="dashboard">
      {info.startupError && (
        <div className="startup-exception">
          <div className="startup-exception-head">
            <IconAlertTriangle size={14} stroke={2} /> The database failed to start
            {info.startupError.timeUtc ? ` · ${formatTime(info.startupError.timeUtc)}` : ""}
          </div>
          <div>{info.startupError.message}</div>
        </div>
      )}

      <div className="dash-tiles">
        <Tile
          label="State"
          value={state}
          tone={open ? "ok" : state === "Error" ? "bad" : undefined}
          action={
            // the switch for the database itself, on the tile that says which way it stands
            open ? (
              <span className="dash-tile-actions">
                <button className="icon-button dash-tile-action" title="Restart the database — close it and open it again" disabled={openBusy} onClick={onRestart}>
                  <IconReload size={14} stroke={1.8} />
                </button>
                <button className="icon-button dash-tile-action" title="Close the database" disabled={openBusy} onClick={onClose}>
                  <IconPlayerStopFilled size={14} stroke={1.8} />
                </button>
              </span>
            ) : opening ? null : (
              <button className="icon-button dash-tile-action" title="Open the database" disabled={openBusy} onClick={onOpen}>
                <IconPlayerPlayFilled size={14} stroke={1.8} />
              </button>
            )
          }
        />
        <Tile label="Nodes" value={formatCount(live?.nodeCount ?? 0)} />
        <Tile label="Relations" value={formatCount(live?.relationCount ?? 0)} />
        <Tile label="Open for" value={uptime == null ? "—" : formatDuration(uptime)} />
        <Tile label="On disk" value={formatBytes(totalDisk)} />
      </div>

      <PanelGrid id="dashboard" rows={rows} defaultSplit={0.5} />
    </div>
  );
}

/**
 * Samples turned into what the graph draws.
 *
 * A rate is the difference between two counter readings over the time between them. The first sample
 * has nothing to compare against, and a counter that went backwards was reset by a cache clear -
 * both are gaps, so the line breaks instead of inventing a value.
 *
 * A level is drawn as it was measured, one point per sample. Zero there means the server had nothing
 * to report rather than a heap holding nothing, so that is a gap too.
 */
function seriesPoints(samples: Sample[], metric: MetricId, kind: "rate" | "level"): SeriesPoint[] {
  if (kind === "level") return []; // the shared ProcessChart draws the memory and the cpu itself
  const points: SeriesPoint[] = [];
  for (let i = 1; i < samples.length; i++) {
    const previous = samples[i - 1];
    const sample = samples[i];
    const seconds = (sample.at - previous.at) / 1000;
    const delta = sample[metric] - previous[metric];
    const valid = seconds > 0 && delta >= 0;
    points.push({ fromUtc: sample.iso, hasValue: valid, value: valid ? delta / seconds : null });
  }
  return points;
}

// formatDuration counts in whole seconds, which reads as "00:00:00" for work that took milliseconds
const formatElapsed = (ms: number) => (ms < 1000 ? Math.round(ms) + " ms" : (ms / 1000).toFixed(1) + " s");

const formatRate = (value: number) => (value >= 100 ? formatCount(Math.round(value)) : value.toFixed(value >= 10 ? 0 : 1));

const chartShapes: { id: TypeChartShape; label: string; icon: typeof IconLayoutList; help: string }[] = [
  { id: "bars", label: "Bars", icon: IconLayoutList, help: "Compare the amounts, down to the long tail" },
  { id: "treemap", label: "Treemap", icon: IconChartTreemap, help: "The database as a whole made of its types" },
  { id: "cubes", label: "Cubes", icon: IconCube3dSphere, help: "The same whole in three dimensions, where the volume of a cube is the count" },
  { id: "donut", label: "Donut", icon: IconChartDonut, help: "The few types that dominate, as shares" },
];
/** Beyond this the tail is one entry: a treemap of two hundred slivers says less than a number. */
const maxSlices = 24;
const shapeKey = "dashTypeChart";
const inheritedKey = "dashTypeInherited";

/**
 * What the database holds, by node type.
 *
 * Two ways of counting live behind one switch. Off, a node counts once, under the type it actually
 * is: the types are disjoint and the picture is the database. On, a node counts under its own type
 * and under every type above it, which is the only way an interface or an abstract base ever shows
 * a number - and it means a parent and its children now count the same nodes. The bars can show
 * that (they compare types, not parts of a whole), but the treemap and the donut cannot without
 * lying about shares, so those drop any type that already sits inside another type being shown.
 *
 * A tile of the treemap opens a small menu: take the type out of the treemap, query its nodes, or
 * open it in the model editor. Hiding is for the treemap alone and is remembered per database - one
 * type that is most of the database squashes the rest into slivers, and the picture of the rest is
 * what the treemap is for; the bars and the donut still show everything.
 */
function ContentPanel({ info, storeId }: { info: DashboardInfo; storeId: string }) {
  const [shape, setShape] = useState<TypeChartShape>(() => (localStorage.getItem(shapeKey) as TypeChartShape | null) ?? "bars");
  const [inherited, setInherited] = useState(() => localStorage.getItem(inheritedKey) === "true");
  useEffect(() => localStorage.setItem(shapeKey, shape), [shape]);
  useEffect(() => localStorage.setItem(inheritedKey, String(inherited)), [inherited]);
  const hiddenKey = hiddenKeyPrefix + storeId;
  const [hidden, setHidden] = useState<Set<string>>(() => readHidden(hiddenKey));
  useEffect(() => localStorage.setItem(hiddenKey, JSON.stringify([...hidden])), [hiddenKey, hidden]);
  // the menu a tile opened, and where
  const [menu, setMenu] = useState<{ slice: TypeSlice; x: number; y: number } | null>(null);

  const partOfWhole = shape !== "bars";
  // the cubes are the treemap in three dimensions: same whole, same hiding, same tile menu
  const treemap = shape === "treemap" || shape === "cubes";
  const { slices, total, folded, overlapping, hiddenCount } = useMemo(() => {
    const all = info.types ?? [];
    const colors = sourceColors(info.sources ?? [], codeSourceGuid);
    const valueOf = (t: TypeCount) => (inherited ? t.countAll : t.count);
    let kept = all.filter((t) => valueOf(t) > 0);
    // taken out of the treemap by hand; counted so the note below can say so and offer them back
    const hiddenHere = treemap ? kept.filter((t) => hidden.has(t.id)).length : 0;
    if (treemap) kept = kept.filter((t) => !hidden.has(t.id));
    // with inheritance counted in, whatever is already inside something else shown here would be
    // counted twice by a picture of shares
    let dropped = 0;
    if (inherited && partOfWhole) {
      const shown = new Set(kept.map((t) => t.id));
      const byId = new Map(all.map((t) => [t.id, t]));
      const insideAnother = (t: TypeCount, depth: number): boolean => {
        if (depth > 40) return false;
        return (t.parents ?? []).some((p) => shown.has(p) || (byId.has(p) && insideAnother(byId.get(p)!, depth + 1)));
      };
      const outermost = kept.filter((t) => !insideAnother(t, 0));
      dropped = kept.length - outermost.length;
      kept = outermost;
    }
    kept = [...kept].sort((a, b) => valueOf(b) - valueOf(a) || a.name.localeCompare(b.name));
    // one colour per source, the types of a source separated by lightness so a group stays a group
    const seenPerSource = new Map<string, number>();
    const colorOf = (t: TypeCount): string => {
      const base = colors.get(t.sourceId) ?? "#8a8781";
      const n = seenPerSource.get(t.sourceId) ?? 0;
      seenPerSource.set(t.sourceId, n + 1);
      return shade(base, ((n % 5) - 2) * 0.11);
    };
    const head = kept.slice(0, maxSlices);
    const tail = kept.slice(maxSlices);
    const built: TypeSlice[] = head.map((t) => ({ type: t, value: valueOf(t), color: colorOf(t) }));
    if (tail.length > 0) {
      built.push({
        type: {
          id: otherSliceId, name: `${tail.length} more types`, full: tail.map((t) => t.name).join(", "),
          count: 0, countAll: 0, kind: "Class", isInterface: false, sourceId: "", parents: [],
        },
        value: tail.reduce((n, t) => n + valueOf(t), 0),
        color: "#8a8781",
      });
    }
    return { slices: built, total: built.reduce((n, s) => n + s.value, 0), folded: tail.length, overlapping: dropped, hiddenCount: hiddenHere };
  }, [info, inherited, partOfWhole, treemap, hidden]);

  return (
    <section className="panel panel-fill">
      <h3>
        Content{" "}
        <span className="panel-sub">
          {formatCount(info.datamodel?.nodeTypes ?? 0)} types · {formatCount(info.datamodel?.properties ?? 0)} properties ·{" "}
          {formatCount(info.datamodel?.indexes ?? 0)} indexes
        </span>
      </h3>
      <div className="dash-chart-toolbar">
        <div className="dm-tabs">
          {chartShapes.map((s) => {
            const Icon = s.icon;
            return (
              <button key={s.id} className={"dm-tab" + (shape === s.id ? " active" : "")} onClick={() => setShape(s.id)} title={s.help}>
                <Icon size={14} stroke={1.9} />
                <span>{s.label}</span>
              </button>
            );
          })}
        </div>
        <span className="query-spacer" />
        <label
          className="dash-chart-switch"
          title="Count a node under every type above it as well, so an interface or a base class shows what is under it. The types then overlap."
        >
          <input type="checkbox" checked={inherited} onChange={(e) => setInherited(e.target.checked)} />
          <span>Include inherited</span>
        </label>
      </div>
      {/* the chart takes what the panel has left in a row with a height of its own, and the treemap
          grows into it; the bars and the donut keep their size and scroll when there is less */}
      <div className="dash-chart-body fill-body">
        <TypeChart shape={shape} slices={slices} total={total} onTileClick={(slice, at) => setMenu({ slice, x: at.x, y: at.y })} />
      </div>
      {(folded > 0 || overlapping > 0 || inherited || hiddenCount > 0) && (
        <div className="muted dash-type-more">
          {[
            inherited && !partOfWhole ? "a node counts under its own type and every type above it, so these overlap" : null,
            inherited && partOfWhole && overlapping > 0
              ? `${overlapping} ${overlapping === 1 ? "type is" : "types are"} inside another type shown here and left out, so the shares still add up`
              : null,
            inherited && partOfWhole && overlapping === 0 ? "counted with everything below each type" : null,
            folded > 0 ? `${folded} smaller ${folded === 1 ? "type" : "types"} in the last group` : null,
            hiddenCount > 0 ? (
              <>
                {hiddenCount} {hiddenCount === 1 ? "type" : "types"} hidden from the treemap ·{" "}
                <button className="link-button" onClick={() => setHidden(new Set())}>
                  show {hiddenCount === 1 ? "it" : "them"} again
                </button>
              </>
            ) : null,
          ]
            .filter((part) => part !== null)
            .map((part, i) => (
              <span key={i}>
                {i > 0 && " · "}
                {part}
              </span>
            ))}
        </div>
      )}
      {menu && (
        <TypeMenu
          slice={menu.slice}
          x={menu.x}
          y={menu.y}
          onClose={() => setMenu(null)}
          onHide={() => {
            setHidden((h) => new Set([...h, menu.slice.type.id]));
            setMenu(null);
          }}
          onQuery={() => {
            setMenu(null);
            openInQuery({ typeId: menu.slice.type.id });
          }}
          onModel={() => {
            setMenu(null);
            openInDatamodel({ typeId: menu.slice.type.id });
          }}
        />
      )}
    </section>
  );
}

const hiddenKeyPrefix = "dashTypeHidden:";

function readHidden(key: string): Set<string> {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(key) ?? "[]");
    return new Set(Array.isArray(parsed) ? parsed.filter((v): v is string => typeof v === "string") : []);
  } catch {
    return new Set();
  }
}

/**
 * The menu a treemap tile opens: what to do with the type from here. A small popover at the click,
 * kept inside the window; a click anywhere else or Escape closes it.
 */
function TypeMenu({
  slice,
  x,
  y,
  onClose,
  onHide,
  onQuery,
  onModel,
}: {
  slice: TypeSlice;
  x: number;
  y: number;
  onClose: () => void;
  onHide: () => void;
  onQuery: () => void;
  onModel: () => void;
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);
  const width = 250;
  const height = 160;
  const left = Math.max(8, Math.min(x, window.innerWidth - width - 8));
  const top = Math.max(8, Math.min(y, window.innerHeight - height - 8));
  return (
    <>
      <div
        className="db-menu-backdrop"
        onClick={onClose}
        onContextMenu={(e) => {
          e.preventDefault();
          onClose();
        }}
      />
      <div className="db-menu type-menu" role="menu" style={{ top, left, width }}>
        <div className="type-menu-head">
          <KindIcon kind={slice.type.kind} size={14} />
          <b title={slice.type.full}>{slice.type.name}</b>
          <span className="muted">{formatCount(slice.value)} nodes</span>
        </div>
        <button className="db-menu-item" role="menuitem" onClick={onHide}>
          <IconEyeOff size={15} stroke={1.8} /> Hide from the treemap
        </button>
        <button className="db-menu-item" role="menuitem" onClick={onQuery}>
          <IconDatabaseSearch size={15} stroke={1.8} /> Query these nodes
        </button>
        <button className="db-menu-item" role="menuitem" onClick={onModel}>
          <IconSchema size={15} stroke={1.8} /> Open in the data model
        </button>
      </div>
    </>
  );
}

// What an activity is, for keeping its row between samples: the category and its description with
// the numbers taken out, since a description that counts up ("rewriting 4,201 of 9,000") is still
// the same piece of work. A second one just like it gets a suffix.
function activityKey(a: { category: string; description: string | null }, index: number, taken: Set<string>): string {
  const base = a.category + ":" + (a.description ?? "").replace(/[\d.,%]+/g, "#");
  let key = base;
  for (let n = 2; taken.has(key); n++) key = base + "#" + n;
  void index;
  return key;
}

/**
 * The items as they should be on screen: the current ones, plus any that were there a moment ago
 * and are now leaving. A removed item stays for `ms` with `leaving` set, which is what its exit
 * transition runs on; an item that comes back within that time is simply current again.
 */
function useLeaving<T>(items: T[], keyOf: (item: T, index: number, taken: Set<string>) => string, ms: number): { item: T; key: string; leaving: boolean }[] {
  const [gone, setGone] = useState<{ item: T; key: string; until: number }[]>([]);
  const previous = useRef<{ item: T; key: string }[]>([]);
  const taken = new Set<string>();
  const current = items.map((item, i) => {
    const key = keyOf(item, i, taken);
    taken.add(key);
    return { item, key };
  });
  useEffect(() => {
    const now = Date.now();
    const keys = new Set(current.map((c) => c.key));
    const left = previous.current.filter((p) => !keys.has(p.key));
    previous.current = current;
    if (left.length > 0) {
      setGone((g) => [...g.filter((x) => !keys.has(x.key) && !left.some((l) => l.key === x.key)), ...left.map((l) => ({ ...l, until: now + ms }))]);
      const timer = setTimeout(() => setGone((g) => g.filter((x) => x.until > Date.now())), ms + 20);
      return () => clearTimeout(timer);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- runs when the list changes, which is what `items` says
  }, [items]);
  const leaving = gone.filter((g) => !taken.has(g.key) && g.until > Date.now()).map((g) => ({ item: g.item, key: g.key, leaving: true }));
  return [...current.map((c) => ({ ...c, leaving: false })), ...leaving];
}

function Tile({ label, value, tone, action }: { label: string; value: string; tone?: "ok" | "bad"; action?: React.ReactNode }) {
  return (
    <div className={"dash-tile" + (tone ? " " + tone : "")}>
      <div className="dash-tile-value">{value}</div>
      <div className="dash-tile-label">{label}</div>
      {action}
    </div>
  );
}

function Fact({ k, v }: { k: string; v: string }) {
  return (
    <div className="fact">
      <div className="fact-k">{k}</div>
      <div className="fact-v" title={v}>
        {v}
      </div>
    </div>
  );
}

function CacheRow({
  label,
  count,
  size,
  fill,
  hits,
  misses,
}: {
  label: string;
  count: number;
  size: number | null;
  fill: number | null;
  hits: number;
  misses: number;
}) {
  const lookups = hits + misses;
  // the hit rate is the number that says whether the cache is doing anything; without a lookup
  // there is nothing to report rather than a rate of zero
  const rate = lookups > 0 ? Math.round((hits / lookups) * 100) : null;
  return (
    <div className="dash-cache">
      <span className="dash-cache-label">{label}</span>
      <span className="muted">
        {formatCount(count)} kept{size != null ? ` · ${formatBytes(size)}` : ""}
      </span>
      {fill != null && (
        <span className="scan-bar" title={`${Math.round(fill)}% of the memory budget`}>
          <span className="scan-bar-fill" style={{ width: Math.min(100, Math.max(0, fill)) + "%" }} />
        </span>
      )}
      <span className="num">{rate == null ? "—" : rate + "% hit"}</span>
      {/* The rate on its own hides how much work it stands for: 100% over four lookups and 90% over
          two million are not the same cache, and only the second says the cache is earning its
          memory. The two counts run from the moment the database opened, or from the last clearing;
          the total is their sum, which is not worth a third number. */}
      <span className="muted dash-cache-lookups">
        {lookups === 0
          ? "nothing has asked yet"
          : `${formatCount(hits)} ${hits === 1 ? "hit" : "hits"} · ${formatCount(misses)} ${misses === 1 ? "miss" : "misses"}`}
      </span>
    </div>
  );
}
