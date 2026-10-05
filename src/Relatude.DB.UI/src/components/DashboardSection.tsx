import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  IconAlertTriangle,
  IconChartArcs3,
  IconChartDonut,
  IconChartTreemap,
  IconCube3dSphere,
  IconDatabaseSearch,
  IconEyeOff,
  IconLayoutList,
  IconArrowsExchange,
  IconCircles,
  IconClock,
  IconCpu,
  IconDatabase,
  IconPlayerPlayFilled,
  IconRefresh,
  IconReload,
  IconSchema,
} from "@tabler/icons-react";
import { Chart } from "./Chart";
import { ProcessChart, currentCpu, formatPercent, padToWindow, type ProcessSample } from "./ProcessChart";
import { MemoryPanel } from "./MemoryPanel";
import { PanelGrid, type PanelRow } from "./PanelGrid";
import { PowerOrb, powerTone } from "./PowerOrb";
import { FadeText } from "./FadeText";
import { TypeChart, otherSliceId, shade, type TypeChartShape, type TypeSlice } from "./TypeChart";
import { KindIcon } from "./DatamodelIcons";
import { openInDatamodel, openInQuery } from "../navigate";
import { showConfirm, showError, showInfo } from "../dialogs";
import { fetchDashboard, type DashboardInfo, type DashboardLive, type TypeCount } from "../server/dashboard";
import { codeSourceGuid, sourceColors } from "../server/datamodel";
import type { TraceInfo } from "../server/logs";
import { useMeasuredEvery, useRefreshInterval } from "../refresh";
import { useLive } from "../live";
import { closeStore, openStore } from "../server/storage";
import type { DatabaseInfo } from "../server/serverInfo";
import type { SeriesPoint } from "../server/logs";
import { formatBytes, formatCount, formatDuration, formatTime } from "../format";
import { Loading } from "./Loading";
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
/*
 * Memory and cpu lead, and are what the panel opens on. They are the reading that means something
 * whatever the database is doing - an idle one still has a heap and a process - where the four rates
 * are flat lines until something is asking the database for something, which is not what anyone
 * wants a dashboard to open on.
 */
const metrics = [
  { id: "managedMemory", label: "Memory & CPU", kind: "level", unit: "in the managed heap" },
  { id: "queries", label: "Queries", kind: "rate", unit: "queries/s" },
  { id: "transactions", label: "Transactions", kind: "rate", unit: "transactions/s" },
  { id: "actions", label: "Actions", kind: "rate", unit: "actions/s" },
  { id: "nodeReads", label: "Node reads", kind: "rate", unit: "reads/s" },
] as const;

type MetricId = (typeof metrics)[number]["id"];

export function DashboardSection({ db }: { db: DatabaseInfo }) {
  const [info, setInfo] = useState<DashboardInfo | null>(null);
  const [live, setLive] = useState<DashboardLive | null>(null);
  const [trace, setTrace] = useState<TraceInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [metric, setMetric] = useState<MetricId>(metrics[0].id);
  const [openBusy, setOpenBusy] = useState(false);
  // the page asked for an open itself: from the click on it is opening, not from the first sample
  // that happens to say so a couple of seconds later
  const [starting, setStarting] = useState(false);
  // bumped when the page has just changed the database's state, so the live feeds answer with a
  // fresh sample at once rather than on the next tick
  const [kick, setKick] = useState(0);
  const samples = useRef<Sample[]>([]);
  const [, setSampleTick] = useState(0);
  const measuredEvery = useMeasuredEvery();
  const refreshMs = useRefreshInterval();

  // asked for by hand after something that changes the whole picture - a database opened, closed or
  // emptied - rather than waiting for the next sample of a call this expensive. Only the latest
  // request's answer is taken: around an open, one asked while it was still opening can come back
  // after one asked once it was open, and would put an empty database back on the page
  const infoRequest = useRef(0);
  const loadInfo = useCallback(async () => {
    const ticket = ++infoRequest.current;
    try {
      const next = await fetchDashboard(db.id);
      if (ticket !== infoRequest.current) return;
      setInfo(next);
      setError(null);
    } catch (e) {
      if (ticket === infoRequest.current) setError(e instanceof Error ? e.message : String(e));
    }
  }, [db.id]);

  // the shape of the database: counted over every type, every backup and every state file, so it is
  // sampled at its own floor however fast the refresh rate is set
  useLive<DashboardInfo>("dashboard", { storeId: db.id }, setInfo, { minMs: infoIntervalMs, onError: setError });

  useEffect(() => {
    samples.current = [];
  }, [db.id]);

  const applySample = useCallback(
    (sample: DashboardLive) => {
      setLive((previous) => {
        // the full picture is sampled slowly and would otherwise describe a database that has since
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
    },
    [loadInfo],
  );

  // the counters, which is what the page is mostly made of: cheap to read, and the only thing here
  // that moves every second
  useLive<DashboardLive>("dashboard-live", { storeId: db.id }, applySample, { restartOn: kick });

  // the live sample is seconds old, the full picture up to a minute: the state comes from the live
  // one - and from the page itself while an open it asked for has not been reported yet
  const reportedState = live?.state ?? info?.state ?? null;
  const stateNow = starting && reportedState !== "Open" ? "Opening" : reportedState;

  // the last messages the database wrote, on the same cadence as everything else here. An open
  // database shares the page with five other panels and sends its latest few; while it opens, or
  // after it stopped, the trace is most of what there is to read, so it sends a terminal's worth
  useLive<TraceInfo>("logs-trace", { storeId: db.id, take: stateNow === "Open" ? 12 : 200 }, setTrace, { restartOn: kick });

  // the opening clock counts every second, not only when a sample lands
  useTick(1000, stateNow === "Opening");

  // the running activities, with the ones that just finished kept a moment longer so they fade out
  // instead of vanishing between two samples (a hook, so it sits here with the others, before the
  // early returns below)
  const running = useLeaving(live?.activities ?? [], activityKey, 450);

  // The open request only answers when the database is open, which on a large one is minutes away;
  // what happens meanwhile is told by the live feeds, asked again shortly after the open has begun
  // so the first of its trace lines and its first step show without waiting for the next tick.
  async function openAndFollow() {
    setStarting(true);
    const early = setTimeout(() => setKick((k) => k + 1), 600);
    try {
      await openStore(db.id);
    } finally {
      clearTimeout(early);
      setStarting(false);
      setKick((k) => k + 1);
    }
  }

  async function onOpen() {
    setOpenBusy(true);
    try {
      await openAndFollow();
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
      await openAndFollow();
      await loadInfo();
    } catch (e) {
      showError("Could not restart the database", e instanceof Error ? e.message : String(e));
    } finally {
      setOpenBusy(false);
    }
  }

  // the power orb stops the database at once, without asking first: the owner's call - the orb says
  // "press to stop" while it is pointed at, nothing is lost by a stop, and pressing it again starts it
  async function onClose() {
    setOpenBusy(true);
    try {
      await closeStore(db.id);
      samples.current = []; // the counters start over with the next open
      setKick((k) => k + 1);
      await loadInfo();
    } catch (e) {
      showError("Could not stop the database", e instanceof Error ? e.message : String(e));
    } finally {
      setOpenBusy(false);
    }
  }

  if (error) return <div className="placeholder">{error}</div>;
  if (!info) return <Loading label="Loading the dashboard…" />;

  const state = stateNow ?? info.state;
  const open = state === "Open";
  const opening = state === "Opening";
  // a sample from before the open reported itself carries no progress: the page's own click is all
  // there is then, and the bar says only that something has started
  const progress = opening ? (live?.opening ?? null) : null;
  const openingSince = progress?.sinceUtc ? new Date(progress.sinceUtc).getTime() : null;
  const openingFor = openingSince != null ? Math.max(0, Date.now() - openingSince) : (progress?.timeElapsedMs ?? null);
  const openingPercent = Math.round(Math.min(100, Math.max(0, progress?.progressPercentage ?? 0)));
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

  // what the database is busy with, one row each - in "Right now" when it is open, and as the steps
  // of the open while it opens
  const activityRows = running.map(({ item: a, key, leaving }) => (
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
  ));

  // the running work takes the middle of the panel, scrolling when there is more than fits; the
  // two counts sit at the bottom whatever is running above them
  const nowPanel = (
    <section className="panel panel-fill">
      <h3>
        Right now <span className="panel-sub">{(live?.activities?.length ?? 0) === 0 ? "idle" : `${live!.activities!.length} running`}</span>
      </h3>
      <div className="dash-now fill-body">
        {activityRows}
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

  // the hit counts come from the full picture and the entry counts from the live sample: the panel
  // writes both out when it is maximized
  const memoryPanel = <MemoryPanel storeId={db.id} cache={info.cache} counts={live ?? undefined} onChanged={loadInfo} />;

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

  // Watching a database open. The first part of an open has no percentage to give - the model is
  // loaded and the mappers built before the store exists - so until the store starts reading its
  // state the bar only says that something is happening, and the step says what. The estimate is
  // the store's own, made while it replays the log and counted down on the server after that.
  const openingPanel = (
    <section className="panel panel-fill">
      <h3>
        Opening{" "}
        <span className="panel-sub">{progress?.sinceUtc ? `started ${formatTime(progress.sinceUtc)}` : "starting…"}</span>
      </h3>
      <div className="dash-opening">
        <span
          className={"progress-bar" + (openingPercent > 0 ? "" : " indeterminate")}
          role="progressbar"
          aria-valuenow={openingPercent > 0 ? openingPercent : undefined}
          aria-valuemin={0}
          aria-valuemax={100}
        >
          <span className="progress-fill" style={{ width: openingPercent + "%" }} />
        </span>
        <span className="num">{openingPercent > 0 ? `${openingPercent}%` : ""}</span>
        <span className="muted">
          {openingFor != null ? `${formatDuration(openingFor)} so far` : ""}
          {progress?.timeRemainingMs ? ` · about ${formatDuration(progress.timeRemainingMs)} left` : ""}
        </span>
      </div>
      <div className="dash-opening-step">{progress?.step ?? (progress ? "Opening" : "Asking the server to open it…")}</div>
      <div className="dash-now fill-body">{activityRows}</div>
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
  // Newest first, so a line that just arrived is at the top where the eye already is - and a line
  // carrying details is still worth a click
  //
  // While the database opens this is the open's own account of itself, from the first step on: the
  // panel gets the room of a terminal, and the replay's progress is one line that rewrites itself
  // (the store replaces it rather than adding one per tick). Once it has stopped - closed, or failed
  // to open - the lines are what it said last, and the panel says that they are not live.
  const traceLines = (trace?.entries ?? []).slice(0, open ? 12 : undefined);
  const traceKept = !trace?.open && !!trace?.keptUtc && traceLines.length > 0;
  const tracePanel = (
    <section className="panel panel-fill">
      <h3>
        Latest messages{" "}
        <span className="panel-sub">
          {opening
            ? "what the database says as it opens"
            : traceKept
              ? `what it said last, before it stopped at ${formatTime(trace!.keptUtc!)}`
              : "the trace the database keeps in memory"}
        </span>
      </h3>
      <div className={"term fill-body" + (traceKept ? " term-kept" : "")}>
        {traceLines.length > 0 && !traceKept && <div className="term-idle term-idle-top">_</div>}
        {traceLines.map((entry, i) => (
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
        {traceLines.length === 0 && (
          <div className="term-empty">{open || opening ? "Nothing traced yet." : "Open the database to see its trace."}</div>
        )}
      </div>
    </section>
  );

  /**
   * The rows of the resizable grid, and the only place the page decides what sits beside what.
   * The ids are what a dragged height is remembered by, so the three states of a database never
   * inherit each other's - a closed one has a single panel where an open one has five. The trace of
   * an opening or a stopped database is a terminal's worth of lines, so its row has a height and
   * scrolls; the steps of an open take what is left above it.
   */
  const rows: PanelRow[] = opening
    ? [
        { id: "opening", height: 220, cells: [openingPanel] },
        { id: "opening-trace", height: 380, cells: [tracePanel] },
      ]
    : !open
      ? [
          { id: "closed", cells: [closedPanel] },
          { id: "closed-trace", height: 320, cells: [tracePanel] },
        ]
      : [
          // rows given a height of their own hold a panel that fills whatever it is handed - a chart
          // or a terminal sized to its own content has no height at all
          // the budgets take the sized row: a row per part with a bar has a shape of its own, and
          // what is running is a list of nothing most of the time, which does not need the height
          { id: "activity", height: 300, cells: [activityPanel, memoryPanel] },
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
          {info.startupError.restart?.dueUtc && (
            <div className="startup-exception-restart">
              Another process held one of its files, so the server restarts itself at {formatTime(info.startupError.restart.dueUtc)} to
              try again ({info.startupError.restart.attempt} of {info.startupError.restart.attempts}).
            </div>
          )}
        </div>
      )}

      <div className="dash-tiles">
        {/* The orb is the database's power button: pressed while it is up it stops it (at once),
            pressed while it is down or failed it starts it. Nothing to press while it opens - the
            ring is the progress then. The restart sits beside it as the one other thing to do. */}
        <StateTile
          state={state}
          progress={opening ? openingPercent : null}
          detail={
            open
              ? "serving requests"
              : opening
                ? openingPercent > 0
                  ? `${openingPercent}%` + (progress?.timeRemainingMs ? ` · ${formatDuration(progress.timeRemainingMs)} left` : "")
                  : "starting…"
                : state === "Error"
                  ? "failed to start"
                  : state === "Closing"
                    ? "flushing and closing"
                    : "not serving"
          }
          hint={open ? "press to stop" : opening || state === "Closing" ? undefined : state === "Error" ? "press to try again" : "press to start"}
          powerTitle={open ? "Stop the database" : state === "Error" ? "Start the database again" : "Start the database"}
          onPower={opening || state === "Closing" ? undefined : open ? onClose : onOpen}
          busy={openBusy}
        >
          {/* only while it is up, but in place in every state: hidden rather than taken out, so the
              text beside it keeps its width when the state changes */}
          <span className={"dash-tile-actions" + (open ? "" : " power-away")} aria-hidden={!open}>
            <button
              className="dash-tile-button quiet"
              title="Restart the database — close it and open it again"
              disabled={openBusy || !open}
              tabIndex={open ? undefined : -1}
              onClick={onRestart}
            >
              <IconReload size={15} stroke={2} />
            </button>
          </span>
        </StateTile>
        {/* a store that is not up has counted nothing yet: a zero there would read as an empty database */}
        <Tile label="Nodes" icon={IconCircles} value={open ? formatCount(live?.nodeCount ?? 0) : "—"} />
        <Tile label="Relations" icon={IconArrowsExchange} value={open ? formatCount(live?.relationCount ?? 0) : "—"} />
        {opening ? (
          <Tile label="Opening for" icon={IconClock} value={openingFor == null ? "—" : formatDuration(openingFor)} />
        ) : (
          <Tile label="Open for" icon={IconClock} value={uptime == null ? "—" : formatDuration(uptime)} />
        )}
        <Tile label="On disk" icon={IconDatabase} value={formatBytes(totalDisk)} />
        {/* the one tile that is the server process rather than this database: one heap serves every
            database on it */}
        <Tile label="Memory" icon={IconCpu} value={formatBytes(live?.processMemory ?? 0)} />
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

const formatRate = (value: number) => (value >= 100 ? formatCount(Math.round(value)) : value.toFixed(value >= 10 ? 0 : 1));

const chartShapes: { id: TypeChartShape; label: string; icon: typeof IconLayoutList; help: string }[] = [
  { id: "bars", label: "Bars", icon: IconLayoutList, help: "Compare the amounts, down to the long tail" },
  { id: "treemap", label: "Treemap", icon: IconChartTreemap, help: "The database as a whole made of its types" },
  { id: "cubes", label: "Cubes", icon: IconCube3dSphere, help: "The same whole in three dimensions, where the volume of a cube is the count" },
  { id: "sunburst", label: "Sunburst", icon: IconChartArcs3, help: "The same whole arranged by inheritance: a ring holds what the ring inside it is derived into" },
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
 * The sunburst takes neither side of the switch, which is why it does not offer it: its rings are
 * the inheritance itself, so a node counts once, under the type it is, and a parent is as big as
 * what is nested inside it. It is also the one shape that keeps a type with no nodes of its own -
 * an interface or an abstract base is a ring, not a blank - and the one that is never folded into a
 * tail, since a fold there would cut branches off rather than tidy a corner.
 *
 * A tile, a cube or an arc opens a small menu: take the type out of the chart, query its nodes, or
 * open it in the model editor. Hiding is for those three shapes and is remembered per database - one
 * type that is most of the database squashes the rest into slivers, and the picture of the rest is
 * what they are for; the bars and the donut still show everything. A hidden type in the middle of
 * the sunburst's tree leaves its subtypes standing on their own in the innermost ring.
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
  // the sunburst is the one shape that draws the inheritance instead of counting it in: a node is
  // counted once, under the type it is, and a parent's arc is that plus everything nested inside it
  const sunburst = shape === "sunburst";
  // the cubes are the treemap in three dimensions, the sunburst the same whole wrapped round the
  // model: same hiding, same tile menu
  const treemap = shape === "treemap" || shape === "cubes" || sunburst;
  const { slices, total, folded, overlapping, hiddenCount } = useMemo(() => {
    const all = info.types ?? [];
    const colors = sourceColors(info.sources ?? [], codeSourceGuid);
    const valueOf = (t: TypeCount) => (inherited && !sunburst ? t.countAll : t.count);
    // a type with no nodes of its own is nothing to the other shapes, but it is a ring of the
    // sunburst as long as something below it has some - that is what the shape is for
    let kept = all.filter((t) => (sunburst ? t.countAll > 0 : valueOf(t) > 0));
    // taken out of the treemap by hand; counted so the note below can say so and offer them back
    const hiddenHere = treemap ? kept.filter((t) => hidden.has(t.id)).length : 0;
    if (treemap) kept = kept.filter((t) => !hidden.has(t.id));
    // with inheritance counted in, whatever is already inside something else shown here would be
    // counted twice by a picture of shares
    let dropped = 0;
    if (inherited && partOfWhole && !sunburst) {
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
    // the sunburst is not folded: the tail is what hangs off the types above it, and a fold would
    // cut branches off the rings rather than tidy a corner of the picture
    const limit = sunburst ? kept.length : maxSlices;
    const head = kept.slice(0, limit);
    const tail = kept.slice(limit);
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
  }, [info, inherited, partOfWhole, treemap, sunburst, hidden]);

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
        <div className="module-switch compact" role="tablist">
          {chartShapes.map((s) => {
            const Icon = s.icon;
            return (
              <button key={s.id} role="tab" aria-selected={shape === s.id} className={shape === s.id ? "active" : ""} onClick={() => setShape(s.id)} title={s.help}>
                <Icon size={14} stroke={1.9} />
                <span>{s.label}</span>
              </button>
            );
          })}
        </div>
        <span className="query-spacer" />
        {/* the sunburst has no use for the switch: its rings are the inheritance, and a node counted
            under every type above it would be counted once per ring it falls in */}
        {!sunburst && (
          <label
            className="dash-chart-switch"
            title="Count a node under every type above it as well, so an interface or a base class shows what is under it. The types then overlap."
          >
            <input type="checkbox" checked={inherited} onChange={(e) => setInherited(e.target.checked)} />
            <span>Include inherited</span>
          </label>
        )}
      </div>
      {/* the chart takes what the panel has left in a row with a height of its own, and every shape
          but the bars grows into it; below their minimum the panel scrolls rather than shrinking a
          chart away, and the bars are a list, so they scroll whenever there are more than fit */}
      <div className="dash-chart-body fill-body">
        <TypeChart shape={shape} slices={slices} total={total} onTileClick={(slice, at) => setMenu({ slice, x: at.x, y: at.y })} />
      </div>
      {(folded > 0 || overlapping > 0 || inherited || sunburst || hiddenCount > 0) && (
        <div className="muted dash-type-more">
          {[
            sunburst ? "a ring holds what the ring inside it is derived into, and a node counts once, under the type it is" : null,
            inherited && !partOfWhole ? "a node counts under its own type and every type above it, so these overlap" : null,
            inherited && partOfWhole && !sunburst && overlapping > 0
              ? `${overlapping} ${overlapping === 1 ? "type is" : "types are"} inside another type shown here and left out, so the shares still add up`
              : null,
            inherited && partOfWhole && !sunburst && overlapping === 0 ? "counted with everything below each type" : null,
            folded > 0 ? `${folded} smaller ${folded === 1 ? "type" : "types"} in the last group` : null,
            hiddenCount > 0 ? (
              <>
                {hiddenCount} {hiddenCount === 1 ? "type" : "types"} hidden from this chart ·{" "}
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
 * The menu a tile, a cube or an arc opens: what to do with the type from here. A small popover at
 * the click, kept inside the window; a click anywhere else or Escape closes it.
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
          <IconEyeOff size={15} stroke={1.8} /> Hide from this chart
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

/** Renders again every `ms` while `on`: for a clock that would otherwise only move when a sample lands. */
function useTick(ms: number, on: boolean): void {
  const [, setTick] = useState(0);
  useEffect(() => {
    if (!on) return;
    const timer = setInterval(() => setTick((t) => t + 1), ms);
    return () => clearInterval(timer);
  }, [ms, on]);
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

/**
 * One number about the database, with the icon of what it counts.
 *
 * The icon goes beside the LABEL rather than over the number: the top right corner of a tile is
 * where its buttons are, and the number is what the tile is for and should have the row to itself.
 */
function Tile({ label, icon: Icon, value }: { label: string; icon?: typeof IconCircles; value: string }) {
  // a count that goes up is the same number moving, and changes in place; a dash that becomes a
  // number (or a number that becomes a dash, as the database closes) is what crossfades
  return (
    <div className="dash-tile">
      <div className="dash-tile-value">
        <FadeText fadeKey={value === "—" ? "none" : "value"}>{value}</FadeText>
      </div>
      <div className="dash-tile-label">
        {Icon && <Icon size={13} stroke={1.8} />}
        <FadeText fadeKey={label}>{label}</FadeText>
      </div>
    </div>
  );
}

/**
 * How the database stands, and its switch. The power orb says it before a word is read - lit green,
 * dark, turning while it opens, red when it failed - so the state itself is a small word beside it,
 * with a line under it saying what that means right now. Hovering the orb turns that line into
 * what pressing it would do, since a power button never says which way it is about to go.
 */
function StateTile({
  state,
  progress,
  detail,
  hint,
  powerTitle,
  onPower,
  busy,
  children,
}: {
  state: string;
  progress: number | null;
  detail: string;
  hint?: string;
  powerTitle: string;
  onPower?: () => void;
  busy: boolean;
  children?: React.ReactNode;
}) {
  return (
    <div className={"dash-tile dash-state power-" + powerTone(state)}>
      <PowerOrb state={state} size={46} progress={progress} onClick={onPower} disabled={busy} title={onPower ? powerTitle : state} />
      <div className="dash-state-text">
        {/* a new state crossfades with the last one; the line about it crossfades when it says a
            different kind of thing, with its numbers taken out, so the countdown of an open
            changes in place instead of fading every second */}
        <div className="dash-state-word">
          <FadeText fadeKey={state}>{state}</FadeText>
        </div>
        <div className="dash-state-detail">
          <span className="dash-state-now">
            <FadeText fadeKey={state + ":" + detail.replace(/[\d.,:%]+/g, "#")}>{detail}</FadeText>
          </span>
          {hint && <span className="dash-state-hint">{hint}</span>}
        </div>
      </div>
      {children}
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

