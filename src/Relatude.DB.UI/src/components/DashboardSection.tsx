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
import { ProcessChart, currentCpu, currentIo, formatBytesPerSecond, formatPercent, hasIo, type ProcessSample } from "./ProcessChart";
import { MemoryPanel } from "./MemoryPanel";
import { PanelGrid, type PanelRow } from "./PanelGrid";
import { PowerOrb, powerTone } from "./PowerOrb";
import { FadeText } from "./FadeText";
import { TypeChart, otherSliceId, shade, type TypeChartShape, type TypeSlice } from "./TypeChart";
import { KindIcon } from "./DatamodelIcons";
import { openInDatamodel, openInQuery } from "../navigate";
import { showConfirm, showError, showInfo } from "../dialogs";
import { fetchDashboard, type DashboardInfo, type DashboardLive, type LiveCacheCounters, type TypeCount } from "../server/dashboard";
import { codeSourceGuid, sourceColors } from "../server/datamodel";
import type { TraceInfo } from "../server/logs";
import { useLive } from "../live";
import { useRefreshInterval } from "../refresh";
import { closeStore, openStore } from "../server/storage";
import type { DatabaseInfo } from "../server/serverInfo";
import { formatBytes, formatCount, formatDuration, formatTime } from "../format";
import { ActivityList, ActivityPanel, RunningPanel, useTick } from "./RightNow";
import { Loading } from "./Loading";
import "../datamodel.css";

/**
 * The landing page of one database: what it holds, what it is doing right now, and anything worth
 * noticing before the other sections.
 *
 * The performance graph is the server process - its heap, its cpu and what its storages read and write -
 * sampled here on the refresh cadence. The database's own rates (queries, transactions, actions,
 * node reads) are drawn in the "Activity" panel from the same samples.
 */
// the full picture takes the store's write lock and counts every type, backup and state file: worth
// it when the page opens, never worth it on the refresh rate, however fast that is set
const infoIntervalMs = 60000;
// three minutes of history at the default rate: enough to see a burst arrive and drain
const maxSamples = 90;

/** A reading of the counters, taken together with one of the process (see ProcessChart). */
interface Sample extends ProcessSample {
  cache: LiveCacheCounters | null;
  queries: number;
  transactions: number;
  actions: number;
  nodeReads: number;
}

export function DashboardSection({ db }: { db: DatabaseInfo }) {
  const [info, setInfo] = useState<DashboardInfo | null>(null);
  const [live, setLive] = useState<DashboardLive | null>(null);
  const [trace, setTrace] = useState<TraceInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [openBusy, setOpenBusy] = useState(false);
  // the page asked for an open itself: from the click on it is opening, not from the first sample
  // that happens to say so a couple of seconds later
  const [starting, setStarting] = useState(false);
  // bumped when the page has just changed the database's state, so the live feeds answer with a
  // fresh sample at once rather than on the next tick
  const [kick, setKick] = useState(0);
  const samples = useRef<Sample[]>([]);
  const [, setSampleTick] = useState(0);
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
            io: sample.io,
            cache: sample.cache ?? null,
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
  const cpuNow = currentCpu(samples.current);
  const ioNow = currentIo(samples.current);
  const ioShown = hasIo(samples.current);

  const performancePanel = (
    <section className="panel panel-fill">
      <h3>
        Performance{" "}
        <span className="panel-sub">
          {`${formatBytes(live?.managedMemory ?? 0)} in the managed heap · ${formatBytes(live?.processMemory ?? 0)} resident in the process${cpuNow === null ? "" : ` · ${formatPercent(cpuNow)} cpu`}${ioNow === null ? "" : ` · ${formatBytesPerSecond(ioNow.read.value ?? 0)} read · ${formatBytesPerSecond(ioNow.written.value ?? 0)} written`}`}
        </span>
        <button className="icon-button storage-refresh" title="Refresh" onClick={loadInfo}>
          <IconRefresh size={14} stroke={1.8} />
        </button>
      </h3>
      <ProcessChart samples={samples.current} maxSamples={maxSamples} />
      <div className="logs-chart-foot">
        <span className="muted">
          <span className="chart-key chart-key-second">CPU</span> · <span className="chart-key chart-key-first">Memory</span>
          {ioShown && (
            <>
              {" · "}
              <span className="chart-key chart-key-read">Read</span> · <span className="chart-key chart-key-write">Written</span>
            </>
          )}{" "}
          · the whole server process{ioShown ? ", the storage summed over every provider" : ""}
        </span>
      </div>
    </section>
  );

  // what the database is asked to do, a chart per rate, and what it is busy with this second (see
  // RightNow.tsx); both are drawn from the same samples as the performance graph
  const activityPanel = <ActivityPanel samples={samples.current} maxSamples={maxSamples} refreshMs={refreshMs} />;
  const runningPanel = <RunningPanel live={live} rewrite={info.maintenance?.runningRewrite} />;

  // the budgets, and how well the caches answer: the hit counts come from the live sample, so they
  // move with the rest of the page
  const memoryPanel = <MemoryPanel storeId={db.id} cache={info.cache} counts={live ?? undefined} cacheSamples={samples.current} onChanged={loadInfo} />;

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
      {/* the steps of the open, one row each, as "Activity" shows the work of an open database */}
      <div className="dash-now fill-body">
        <ActivityList activities={live?.activities ?? []} />
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
          { id: "performance", height: 400, cells: [performancePanel, memoryPanel] },
          { id: "activity-rates", height: 340, cells: [runningPanel, activityPanel] },
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
        <Tile
          label="Nodes"
          icon={IconCircles}
          value={open ? formatCount(live?.nodeCount ?? 0) : "—"}
          detail={open ? nodeDetail(live) : null}
          title="Revisions: every stored version, a node without revisions counting as one. Embedded: the inner nodes of embedded properties, in every revision. Marked deleted: still stored until deleted for good."
        />
        <Tile label="Relations" icon={IconArrowsExchange} value={open ? formatCount(live?.relationCount ?? 0) : "—"} />
        {opening ? (
          <Tile label="Opening for" icon={IconClock} value={openingFor == null ? "—" : formatDuration(openingFor)} />
        ) : (
          <Tile label="Open for" icon={IconClock} value={uptime == null ? "—" : formatDuration(uptime)} />
        )}
        <Tile label="Database file" icon={IconDatabase} value={formatBytes(info.files.database)} />
        {/* the one tile that is the server process rather than this database: one heap serves every
            database on it */}
        <Tile label="Memory" icon={IconCpu} value={formatBytes(live?.processMemory ?? 0)} />
      </div>

      <PanelGrid id="dashboard" rows={rows} defaultSplit={0.5} />
    </div>
  );
}

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

/**
 * One number about the database, with the icon of what it counts.
 *
 * The icon goes beside the LABEL rather than over the number: the top right corner of a tile is
 * where its buttons are, and the number is what the tile is for and should have the row to itself.
 */
function Tile({
  label,
  icon: Icon,
  value,
  detail,
  title,
}: {
  label: string;
  icon?: typeof IconCircles;
  value: string;
  /** a line under the label for what the number leaves out */
  detail?: string | null;
  title?: string;
}) {
  // a count that goes up is the same number moving, and changes in place; a dash that becomes a
  // number (or a number that becomes a dash, as the database closes) is what crossfades
  return (
    <div className="dash-tile" title={title}>
      <div className="dash-tile-value">
        <FadeText fadeKey={value === "—" ? "none" : "value"}>{value}</FadeText>
      </div>
      <div className="dash-tile-label">
        {Icon && <Icon size={13} stroke={1.8} />}
        <FadeText fadeKey={label}>{label}</FadeText>
      </div>
      {detail && <div className="dash-tile-detail">{detail}</div>}
    </div>
  );
}

/** What the node count leaves out: the revisions, the embedded nodes they hold, and the nodes only marked deleted.
 * Nothing from a server older than this page, which does not send them. */
function nodeDetail(live: DashboardLive | null): string | null {
  if (live?.revisionCount == null) return null;
  const parts = [`${formatCount(live.revisionCount)} revisions`, `${formatCount(live.embeddedNodeCount ?? 0)} embedded`];
  if (live.markedDeletedNodeCount) parts.push(`${formatCount(live.markedDeletedNodeCount)} marked deleted`);
  return parts.join(" · ");
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

