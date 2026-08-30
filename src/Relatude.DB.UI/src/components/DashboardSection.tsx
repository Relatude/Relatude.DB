import { useCallback, useEffect, useRef, useState } from "react";
import { IconAlertTriangle, IconPlayerPlayFilled, IconRefresh } from "@tabler/icons-react";
import { Chart } from "./Chart";
import { showError, showInfo } from "../dialogs";
import { fetchDashboard, fetchDashboardLive, type DashboardInfo, type DashboardLive } from "../server/dashboard";
import { fetchTrace, type TraceInfo } from "../server/logs";
import { openStore } from "../server/storage";
import type { DatabaseInfo } from "../server/serverInfo";
import type { SeriesPoint } from "../server/logs";
import { formatBytes, formatCount, formatDuration, formatTime } from "../format";

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
const liveIntervalMs = 2000;
const infoIntervalMs = 60000;
// three minutes of history at the live interval: enough to see a burst arrive and drain
const maxSamples = 90;

interface Sample {
  at: number;
  iso: string;
  queries: number;
  transactions: number;
  actions: number;
  nodeReads: number;
}

const metrics = [
  { id: "queries", label: "Queries", unit: "queries/s" },
  { id: "transactions", label: "Transactions", unit: "transactions/s" },
  { id: "actions", label: "Actions", unit: "actions/s" },
  { id: "nodeReads", label: "Node reads", unit: "reads/s" },
] as const;

type MetricId = (typeof metrics)[number]["id"];

export function DashboardSection({ db }: { db: DatabaseInfo }) {
  const [info, setInfo] = useState<DashboardInfo | null>(null);
  const [live, setLive] = useState<DashboardLive | null>(null);
  const [trace, setTrace] = useState<TraceInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [metric, setMetric] = useState<MetricId>("queries");
  const [openBusy, setOpenBusy] = useState(false);
  const samples = useRef<Sample[]>([]);
  const [, setSampleTick] = useState(0);

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
    const timer = window.setInterval(loadInfo, infoIntervalMs);
    return () => clearInterval(timer);
  }, [loadInfo]);

  useEffect(() => {
    let stopped = false;
    const tick = async () => {
      try {
        const sample = await fetchDashboardLive(db.id);
        if (stopped) return;
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
            },
          ].slice(-maxSamples);
          setSampleTick((t) => t + 1);
        }
      } catch {
        // a failed sample is a gap, not an error worth taking the page over
      }
    };
    tick();
    const timer = window.setInterval(tick, liveIntervalMs);
    return () => {
      stopped = true;
      clearInterval(timer);
    };
  }, [db.id, loadInfo]);

  // the last messages the database wrote, refreshed at the same pace as everything else here
  useEffect(() => {
    let stopped = false;
    const load = () =>
      fetchTrace(db.id, 12)
        .then((t) => !stopped && setTrace(t))
        .catch(() => {});
    load();
    const timer = window.setInterval(load, 5000);
    return () => {
      stopped = true;
      clearInterval(timer);
    };
  }, [db.id]);

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
  const points = ratePoints(samples.current, metric);
  const current = points.length > 0 ? (points[points.length - 1].value ?? 0) : 0;
  const unit = metrics.find((m) => m.id === metric)!.unit;

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
        <Tile label="State" value={state} tone={open ? "ok" : state === "Error" ? "bad" : undefined} />
        <Tile label="Nodes" value={formatCount(live?.nodeCount ?? 0)} />
        <Tile label="Relations" value={formatCount(live?.relationCount ?? 0)} />
        <Tile label="Open for" value={uptime == null ? "—" : formatDuration(uptime)} />
        <Tile label="On disk" value={formatBytes(totalDisk)} />
      </div>

      {opening ? (
        <section className="panel">
          <h3>
            Opening <span className="panel-sub">replaying the transaction log and rebuilding the indexes</span>
          </h3>
          <div className="dash-opening">
            <span className="progress-bar">
              <span className="progress-fill" style={{ width: Math.max(2, Math.min(100, live?.opening?.progressPercentage ?? 0)) + "%" }} />
            </span>
            <span className="num">{Math.round(live?.opening?.progressPercentage ?? 0)}%</span>
            <span className="muted">
              {live?.opening?.timeRemainingMs ? `about ${formatDuration(live.opening.timeRemainingMs)} left` : "estimating…"}
            </span>
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
      ) : !open ? (
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
      ) : (
        <>
          <section className="panel">
            <h3>
              Activity{" "}
              <span className="panel-sub">
                {formatRate(current)} {unit} · {formatCount(live?.[metric] ?? 0)} since the caches were last cleared
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
            <Chart kind="sum" points={points} groups={[]} interval="Second" format={formatRate} height={180} />
            <div className="logs-chart-foot">
              <span className="muted">
                measured here, every {liveIntervalMs / 1000} seconds, from counters the database keeps anyway — no logging needed
              </span>
            </div>
          </section>

          <div className="overview-columns">
            <section className="panel">
              <h3>
                Right now <span className="panel-sub">{(live?.activities?.length ?? 0) === 0 ? "idle" : `${live!.activities!.length} running`}</span>
              </h3>
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
                {(live?.activities?.length ?? 0) === 0 && <div className="muted">Nothing running.</div>}
              </div>
              <div className="facts-grid storage-facts">
                <Fact k="Background tasks" v={formatCount(live?.tasksQueued ?? 0)} />
                <Fact
                  k="File conversions"
                  v={
                    (live?.conversions?.running ?? 0) + (live?.conversions?.queued ?? 0) === 0
                      ? "none"
                      : `${formatCount(live!.conversions!.running)} running · ${formatCount(live!.conversions!.queued)} queued`
                  }
                />
                <Fact k="Indexes out of sync" v={formatCount(info.maintenance?.indexesOutOfSync ?? 0)} />
                <Fact k="Actions not in snapshot" v={formatCount(info.maintenance?.actionsNotInState ?? 0)} />
              </div>
              {info.maintenance?.runningRewrite && <div className="logs-note">Rewriting {info.maintenance.runningRewrite}…</div>}
            </section>

            <section className="panel">
              <h3>
                Caches <span className="panel-sub">what is being answered from memory</span>
              </h3>
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
            </section>
          </div>

          <div className="overview-columns">
            <section className="panel">
              <h3>
                Content{" "}
                <span className="panel-sub">
                  {formatCount(info.datamodel?.nodeTypes ?? 0)} types · {formatCount(info.datamodel?.properties ?? 0)} properties ·{" "}
                  {formatCount(info.datamodel?.indexes ?? 0)} indexes
                </span>
              </h3>
              <div className="dash-types">
                {(info.types ?? []).map((t) => (
                  <div key={t.full} className="dash-type" title={t.full}>
                    <span className="log-cell">{t.name}</span>
                    <span className="scan-bar">
                      <span className="scan-bar-fill" style={{ width: share(t.count, info.types) + "%" }} />
                    </span>
                    <span className="num">{formatCount(t.count)}</span>
                  </div>
                ))}
                {(info.types ?? []).length === 0 && <div className="muted">No nodes yet.</div>}
                {(info.otherTypes ?? 0) > 0 && (
                  <div className="muted dash-type-more">
                    {info.otherTypes} more {info.otherTypes === 1 ? "type" : "types"} · {formatCount(info.otherTypeNodes ?? 0)} nodes
                  </div>
                )}
              </div>
            </section>

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
          </div>
        </>
      )}

      <section className="panel">
        <h3>
          Latest messages <span className="panel-sub">the trace the database keeps in memory</span>
        </h3>
        <div className="log-table">
          {(trace?.entries ?? []).slice(0, 8).map((entry, i) => (
            <div
              key={i}
              className={"log-table-row trace-row" + (entry.details ? " clickable" : "")}
              onClick={() => entry.details && showInfo(entry.text, "", [entry.details])}
            >
              <span className="log-time">{formatTime(entry.timestampUtc)}</span>
              <span className={"log-cell " + entry.type.toLowerCase()}>{entry.type}</span>
              <span className="log-cell" title={entry.text}>
                {entry.text}
              </span>
            </div>
          ))}
          {(trace?.entries ?? []).length === 0 && <div className="log-table-empty">{open ? "Nothing traced yet." : "Open the database to see its trace."}</div>}
        </div>
      </section>
    </div>
  );
}

/**
 * Counter samples turned into a rate per second. The first sample has nothing to compare against,
 * and a counter that went backwards was reset by a cache clear - both are gaps, so the line breaks
 * instead of inventing a value.
 */
function ratePoints(samples: Sample[], metric: MetricId): SeriesPoint[] {
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

const formatRate = (value: number) => (value >= 100 ? formatCount(Math.round(value)) : value.toFixed(value >= 10 ? 0 : 1));

function share(count: number, types: DashboardInfo["types"]): number {
  const top = Math.max(1, ...(types ?? []).map((t) => t.count));
  return Math.max(2, Math.round((count / top) * 100));
}

function Tile({ label, value, tone }: { label: string; value: string; tone?: "ok" | "bad" }) {
  return (
    <div className={"dash-tile" + (tone ? " " + tone : "")}>
      <div className="dash-tile-value">{value}</div>
      <div className="dash-tile-label">{label}</div>
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
    </div>
  );
}
