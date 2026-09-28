import { useCallback, useEffect, useMemo, useState } from "react";
import { IconArrowsMaximize, IconArrowsMinimize, IconChartBar, IconChartHistogram, IconChartLine, IconDownload, IconEye, IconMap, IconPercentage } from "@tabler/icons-react";
import { Chart, groupColor, intervalLabel } from "./Chart";
import { ShareBars, StatTiles, type Tile } from "./LogCharts";
import { CentresView, HeatmapView, bearingText, spreadTiles } from "./CustomLogGeo";
import { rebuildCustomStatistics, saveText, type CustomLogSeries, type CustomLogSummary } from "../server/customLogs";
import type { IntervalType, SeriesData, SeriesPoint } from "../server/logs";
import type { DatabaseInfo } from "../server/serverInfo";
import { useLive } from "../live";
import { showError, showInfo } from "../dialogs";
import { formatCount } from "../format";
import { addInterval, autoInterval, formatAgo, formatArea, formatDistance, intervals, rangeLabel, rangePayload, type LogRange } from "../customLogRange";

/**
 * A log's statistics, drawn: a panel per statistic its columns keep, over the range the page is
 * looking at, with the numbers that sum the range up above them.
 *
 * Every panel follows its own statistic (a feed each), so a slow one never holds up the others, and
 * each reports what it got to the tiles at the top. An interval clicked on any graph opens the
 * entries recorded in it - the question a spike in a graph always raises.
 *
 * What is drawn is what the statistics kept, which is not the entries: statistics are aggregated as
 * entries arrive and kept for a fixed number of intervals of each size, so a long range at a fine
 * bucket reaches back only so far, and a statistic added to a log covers what came after it -
 * unless it is rebuilt from the entries, which the foot of the page offers.
 */

type Measure = "avg" | "sum" | "count" | "min" | "max" | "spread" | "major" | "minor" | "drift";

const measureLabels: Record<Measure, string> = {
  avg: "Average",
  sum: "Total",
  count: "Count",
  min: "Lowest",
  max: "Highest",
  spread: "Spread",
  major: "Along the spread",
  minor: "Across the spread",
  drift: "Centre moved",
};
// what the graph of a centre-and-spread series can draw, and what each one is (see viewOf)
const geoMeasures: Measure[] = ["spread", "major", "minor", "drift", "count"];

function readJson<T>(key: string, fallback: T): T {
  try {
    const saved = localStorage.getItem(key);
    return saved ? (JSON.parse(saved) as T) : fallback;
  } catch {
    return fallback;
  }
}
function writeJson(key: string, value: unknown) {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // nothing depends on it holding
  }
}

export const seriesId = (s: { property: string | null; statistic: string }) => (s.property ?? "*") + ":" + s.statistic;

const startsWide = (s: CustomLogSeries) => s.kind === "heatmap";

export function CustomLogGraphs({
  db,
  log,
  range,
  live,
  tick,
  onPick,
  onTurnOnStatistics,
  onChanged,
  onRecord,
}: {
  db: DatabaseInfo;
  log: CustomLogSummary;
  range: LogRange;
  live: boolean;
  tick: number;
  onPick: (fromUtc: string, toUtc: string, label: string) => void;
  /** Left out for one of the database's own logs, whose switches are on the Activity page. */
  onTurnOnStatistics?: () => void;
  onChanged: () => void;
  /** Left out where there is nothing to record test entries into (the database's own logs). */
  onRecord?: () => void;
}) {
  const [chosenInterval, setChosenInterval] = useState<IntervalType | "auto">(() => readJson("customLogs:interval:" + log.key, "auto"));
  const [columns, setColumns] = useState<number>(() => readJson("customLogs:graphColumns", 2));
  const [hidden, setHidden] = useState<string[]>(() => readJson("customLogs:hidden:" + log.key, []));
  // the graphs whose width was toggled from where they start: a heatmap starts across the whole width
  // (a map squeezed into half the page is a letterbox of sea), every other graph in its column
  const [toggledWidth, setToggledWidth] = useState<string[]>([]);
  const isExpanded = (s: CustomLogSeries) => toggledWidth.includes(seriesId(s)) !== startsWide(s);
  const [data, setData] = useState<Record<string, SeriesData>>({});
  const [rebuilding, setRebuilding] = useState(false);
  const interval = chosenInterval === "auto" ? autoInterval(range) : chosenInterval;

  const report = useCallback((id: string, d: SeriesData | null) => {
    setData((current) => {
      if (!d) {
        if (!(id in current)) return current;
        const next = { ...current };
        delete next[id];
        return next;
      }
      return { ...current, [id]: d };
    });
  }, []);

  function chooseInterval(value: IntervalType | "auto") {
    setChosenInterval(value);
    writeJson("customLogs:interval:" + log.key, value);
  }
  function chooseColumns(n: number) {
    setColumns(n);
    writeJson("customLogs:graphColumns", n);
  }
  function toggleHidden(id: string) {
    setHidden((current) => {
      const next = current.includes(id) ? current.filter((h) => h !== id) : [...current, id];
      writeJson("customLogs:hidden:" + log.key, next);
      return next;
    });
  }

  async function rebuild() {
    setRebuilding(true);
    try {
      await rebuildCustomStatistics(db.id, log.key);
      onChanged();
      showInfo("Statistics rebuilt", "The statistics were aggregated again from every entry the log holds.");
    } catch (e) {
      showError("Could not rebuild the statistics", e instanceof Error ? e.message : String(e));
    } finally {
      setRebuilding(false);
    }
  }

  const visible = log.series.filter((s) => !hidden.includes(seriesId(s)));
  const tiles = useMemo(() => summaryTiles(log, data, interval), [log, data, interval]);
  const nothingYet = !log.firstRecordUtc && (data["*:Count"]?.summary?.total ?? 0) === 0;

  return (
    <div className="clog-graphs">
      <div className="logs-toolbar clog-graphs-toolbar">
        <label className="clog-inline-field">
          One point per
          <select className="select" value={chosenInterval} onChange={(e) => chooseInterval(e.currentTarget.value as IntervalType | "auto")}>
            <option value="auto">{autoInterval(range).toLowerCase()} (fits the range)</option>
            {intervals.map((i) => (
              <option key={i} value={i}>
                {i.toLowerCase()}
              </option>
            ))}
          </select>
        </label>
        <div className="module-switch compact" title="Graphs side by side">
          {[1, 2, 3].map((n) => (
            <button key={n} className={columns === n ? "active" : ""} onClick={() => chooseColumns(n)}>
              {n === 1 ? "1 column" : `${n}`}
            </button>
          ))}
        </div>
        <span className="logs-spacer" />
        <span className="muted clog-range-note">{rangeLabel(range)}</span>
      </div>

      {log.series.length > 1 && (
        <div className="logs-series clog-series-pick" title="Which graphs to show">
          <IconEye size={14} stroke={1.8} className="muted" />
          {log.series.map((s) => (
            <button key={seriesId(s)} className={"logs-chip" + (hidden.includes(seriesId(s)) ? "" : " active")} onClick={() => toggleHidden(seriesId(s))}>
              {s.label}
            </button>
          ))}
        </div>
      )}

      {!log.enabledStatistics && (
        <div className="logs-note">
          Statistics are off: nothing new is aggregated, and what was kept earlier is not read until they are back on.
          {onTurnOnStatistics ? (
            <button className="link-button" onClick={onTurnOnStatistics}>
              Turn them on
            </button>
          ) : (
            " They are switched on the Activity page."
          )}
        </div>
      )}
      {nothingYet &&
        (onRecord ? (
          <div className="logs-note">
            This log has recorded nothing yet. The application records into it by its key - or record a test entry, or a few hundred made-up ones, to see what
            the graphs will look like.
            <button className="link-button" onClick={onRecord}>
              Record test entries
            </button>
          </div>
        ) : (
          <div className="logs-note">This log has recorded nothing yet. The database records into it while it is switched on, on the Activity page.</div>
        ))}

      <StatTiles tiles={tiles} />

      <div className={"clog-graph-grid cols-" + columns}>
        {visible.map((s) => (
          <SeriesPanel
            key={seriesId(s)}
            db={db}
            log={log}
            series={s}
            range={range}
            interval={interval}
            live={live}
            tick={tick}
            expanded={isExpanded(s)}
            onExpand={() => setToggledWidth((t) => (t.includes(seriesId(s)) ? t.filter((id) => id !== seriesId(s)) : [...t, seriesId(s)]))}
            onReport={report}
            onPick={onPick}
          />
        ))}
      </div>
      {visible.length === 0 && <div className="logs-note">Every graph is hidden: pick the ones to show above.</div>}

      <div className="logs-chart-foot">
        <span className="muted">
          The graphs are drawn from the statistics, which are kept as entries arrive and for a fixed number of intervals of each size. Click an interval to see
          the entries recorded in it.
        </span>
        {log.enabledStatistics && log.logBytes > 0 && (
          <button className="link-button" onClick={rebuild} disabled={rebuilding} title="Aggregate the statistics again from every recorded entry">
            <IconChartHistogram size={14} stroke={1.8} /> {rebuilding ? "Rebuilding…" : "Rebuild from entries"}
          </button>
        )}
      </div>
    </div>
  );
}

/** The numbers above the graphs: what the range adds up to, read from what the panels received. */
function summaryTiles(log: CustomLogSummary, data: Record<string, SeriesData>, interval: IntervalType): Tile[] {
  const tiles: Tile[] = [];
  const rows = data["*:Count"];
  if (rows?.summary?.total != null) {
    tiles.push({ label: "Entries", value: formatCount(rows.summary.total), hint: "Recorded in the range, by the entry count statistic" });
    const withValue = rows.points.filter((p) => p.hasValue && (p.value ?? 0) > 0);
    if (withValue.length > 0) {
      const busiest = withValue.reduce((a, b) => ((b.value ?? 0) > (a.value ?? 0) ? b : a));
      tiles.push({
        label: `Busiest ${interval.toLowerCase()}`,
        value: formatCount(busiest.value ?? 0),
        hint: intervalLabel(busiest.fromUtc, interval),
      });
      const average = rows.summary.total / Math.max(1, rows.points.length);
      tiles.push({ label: `Per ${interval.toLowerCase()}`, value: average >= 10 ? formatCount(Math.round(average)) : trim(average), hint: "The average over every interval of the range" });
    }
  }
  if (log.lastRecordUtc) tiles.push({ label: "Last entry", value: formatAgo(log.lastRecordUtc), tone: "muted" });
  for (const s of log.series) {
    if (tiles.length >= 8) break;
    const d = data[seriesId(s)];
    if (!d?.summary) continue;
    const name = s.label.replace(/ · .*$/, "");
    if (s.kind === "geo") {
      tiles.push(...spreadTiles(name, d.summary));
    } else if (s.kind === "heatmap" && d.summary.total != null && d.summary.halfWithinSquareMeters != null) {
      tiles.push({ label: name + " · half within", value: formatArea(d.summary.halfWithinSquareMeters), hint: "the densest ground holding half of the positions" });
    } else if (s.kind === "full" && s.unit === "meters" && d.summary.avg != null) {
      tiles.push({
        label: name + " · average distance",
        value: formatDistance(d.summary.avg),
        hint: `nearest ${d.summary.min == null ? "—" : formatDistance(d.summary.min)}, farthest ${d.summary.max == null ? "—" : formatDistance(d.summary.max)}`,
      });
    } else if ((s.kind === "full" || s.kind === "avgminmax") && d.summary.avg != null) {
      tiles.push({
        label: s.label.replace(/ · .*$/, "") + " · avg",
        value: trim(d.summary.avg),
        hint: `lowest ${d.summary.min == null ? "—" : trim(d.summary.min)}, highest ${d.summary.max == null ? "—" : trim(d.summary.max)}`,
      });
    } else if (s.kind === "sum" && d.summary.total != null) {
      tiles.push({ label: s.label.replace(/ · .*$/, "") + " · total", value: trim(d.summary.total) });
    } else if (s.kind === "groups" && d.summary.groups && d.summary.groups.length > 0) {
      const top = d.summary.groups[0];
      tiles.push({ label: s.label.replace(/ · .*$/, "") + " · most common", value: top.name || "(empty)", hint: `${formatCount(top.count)} of ${formatCount(d.summary.total ?? 0)}` });
    }
  }
  return tiles;
}

const trim = (v: number) => v.toLocaleString("en-US", { maximumFractionDigits: 2 });

function SeriesPanel({
  db,
  log,
  series,
  range,
  interval,
  live,
  tick,
  expanded,
  onExpand,
  onReport,
  onPick,
}: {
  db: DatabaseInfo;
  log: CustomLogSummary;
  series: CustomLogSeries;
  range: LogRange;
  interval: IntervalType;
  live: boolean;
  tick: number;
  expanded: boolean;
  onExpand: () => void;
  onReport: (id: string, data: SeriesData | null) => void;
  onPick: (fromUtc: string, toUtc: string, label: string) => void;
}) {
  const id = seriesId(series);
  const [data, setData] = useState<SeriesData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [measure, setMeasure] = useState<Measure>(series.kind === "geo" ? "spread" : "avg");
  const [bars, setBars] = useState(series.kind === "count" || series.kind === "sum");
  const [percent, setPercent] = useState(false);
  // a centre-and-spread series is a line of numbers, or the centres themselves on a map
  const [onMap, setOnMap] = useState(false);
  const apply = useCallback(
    (d: SeriesData) => {
      setData(d);
      setError(null);
      onReport(id, d);
    },
    [id, onReport],
  );
  const fail = useCallback(
    (message: string) => {
      setData(null);
      setError(message);
      onReport(id, null);
    },
    [id, onReport],
  );
  // a graph hidden, or gone with a change of definition, takes its numbers out of the tiles with it
  useEffect(() => () => onReport(id, null), [id, onReport]);
  // one sample per bucket at the fastest: a graph drawn every second that only moves every five
  // seconds stands still for four of its points and then jumps
  useLive<SeriesData>(
    "custom-logs-series",
    { storeId: db.id, logKey: log.key, property: series.property, statistic: series.statistic, interval, ...rangePayload(range) },
    apply,
    { once: !live, restartOn: tick, minMs: interval === "Second" ? 1000 : 5000, onError: fail },
  );

  const measures: Measure[] =
    series.kind === "full" ? ["avg", "sum", "count", "max", "min"] : series.kind === "avgminmax" ? ["avg", "max", "min"] : series.kind === "geo" && !onMap ? geoMeasures : [];
  const view = useMemo(() => (data ? viewOf(data, measure, percent) : null), [data, measure, percent]);
  const integer =
    series.kind === "count" ||
    series.kind === "groups" ||
    series.kind === "heatmap" ||
    (series.dataType === "Integer" && series.kind !== "geo") ||
    ((series.kind === "full" || series.kind === "geo") && measure === "count");
  const tall = series.kind === "heatmap" || (series.kind === "geo" && onMap);
  const intervalOf = useCallback((i: number) => (data ? intervalLabel(data.points[i]?.fromUtc ?? data.fromUtc, data.interval) : ""), [data]);

  function downloadCsv() {
    if (!data) return;
    saveText(seriesCsv(data), `${log.key}-${(series.property ?? "entries") + "-" + series.statistic.toLowerCase()}-${interval.toLowerCase()}.csv`, "text/csv");
  }

  const groupTotals = data?.summary?.groups ?? [];
  return (
    <section className={"panel clog-graph" + (expanded ? " expanded" : "")}>
      <h3>
        <span className="clog-graph-title" title={series.label}>
          {series.label}
        </span>
        <span className="panel-sub">{data ? summaryLine(data, measure, series.unit) : ""}</span>
        <span className="clog-graph-tools">
          {measures.length > 0 && (
            <select className="select compact" value={measure} onChange={(e) => setMeasure(e.currentTarget.value as Measure)} title="Which number to draw">
              {measures.map((m) => (
                <option key={m} value={m}>
                  {measureLabels[m]}
                </option>
              ))}
            </select>
          )}
          {(series.kind === "count" || series.kind === "sum" || (series.kind === "full" && (measure === "sum" || measure === "count"))) && (
            <button className="icon-button" onClick={() => setBars(!bars)} title={bars ? "Draw as a line" : "Draw as bars"}>
              {bars ? <IconChartLine size={15} stroke={1.8} /> : <IconChartBar size={15} stroke={1.8} />}
            </button>
          )}
          {series.kind === "groups" && (
            <button className={"icon-button" + (percent ? " on" : "")} onClick={() => setPercent(!percent)} title={percent ? "Show counts" : "Show each interval as shares of 100%"}>
              <IconPercentage size={15} stroke={1.8} />
            </button>
          )}
          {series.kind === "geo" && (
            <button className={"icon-button" + (onMap ? " on" : "")} onClick={() => setOnMap(!onMap)} title={onMap ? "Back to the graph" : "The centre of every interval on a map, each as large as its entries"}>
              {onMap ? <IconChartLine size={15} stroke={1.8} /> : <IconMap size={15} stroke={1.8} />}
            </button>
          )}
          <button className="icon-button" onClick={downloadCsv} disabled={!data} title="Download the points as comma separated text">
            <IconDownload size={15} stroke={1.8} />
          </button>
          <button className="icon-button" onClick={onExpand} title={expanded ? "Back to its place" : "Across the whole width"}>
            {expanded ? <IconArrowsMinimize size={15} stroke={1.8} /> : <IconArrowsMaximize size={15} stroke={1.8} />}
          </button>
        </span>
      </h3>
      {error ? (
        <div className="logs-note">{error}</div>
      ) : data && series.kind === "heatmap" ? (
        <>
          <HeatmapView logKey={log.key} data={data} height={expanded ? 560 : 340} />
          <div className="clog-graph-foot muted">
            {intervalLabel(data.fromUtc, data.interval)} — {intervalLabel(data.toUtc, data.interval)}
            {data.clamped && <span className="logs-warn"> · kept only this far back at one point per {data.interval.toLowerCase()}</span>}
          </div>
        </>
      ) : data && series.kind === "geo" && onMap ? (
        <>
          <CentresView logKey={log.key} data={data} height={expanded ? 560 : 340} label={intervalOf} />
          <div className="clog-graph-foot muted">
            one dot per {data.interval.toLowerCase()}, as large as its entries
            {data.summary?.bearing != null && <> · the positions spread {bearingText(data.summary.bearing)}</>}
          </div>
        </>
      ) : data && view ? (
        <>
          <Chart
            kind={view.kind}
            points={view.points}
            groups={data.groups}
            interval={data.interval}
            format={view.format ?? valueFormatter(series, data.kind, measure)}
            integer={integer && !percent}
            // distances say their own unit on the axis ("400 km"), which "400k" would not
            compactAxis={!((series.kind === "geo" || series.unit === "meters") && measure !== "count")}
            height={expanded ? 320 : tall ? 340 : 190}
            bars={bars}
            valueLabel={view.valueLabel}
            onPick={(index) => {
              const point = view.points[index];
              if (!point) return;
              onPick(point.fromUtc, addInterval(point.fromUtc, data.interval), pickedLabel(point.fromUtc, data.interval));
            }}
          />
          {series.kind === "groups" && groupTotals.length > 0 && (
            <div className="clog-graph-groups">
              <ShareBars
                colored
                items={data.groups
                  .filter((g) => g !== "Other")
                  .map((g) => ({ label: g, count: groupTotals.find((x) => x.name === g)?.count ?? 0, color: groupColor(data.groups.indexOf(g)) }))
                  .sort((a, b) => b.count - a.count)}
                total={data.summary?.total ?? 0}
                format={(n) => formatCount(n)}
              />
            </div>
          )}
          <div className="clog-graph-foot muted">
            {intervalLabel(data.fromUtc, data.interval)} — {intervalLabel(data.toUtc, data.interval)}
            {data.clamped && <span className="logs-warn"> · kept only this far back at one point per {data.interval.toLowerCase()}</span>}
          </div>
        </>
      ) : (
        <div className="chart clog-graph-loading" style={{ height: expanded ? 320 : 190 }} />
      )}
    </section>
  );
}

/** An interval picked on a graph, in words: what the entries page says it is showing. */
function pickedLabel(fromUtc: string, interval: IntervalType): string {
  const label = intervalLabel(fromUtc, interval);
  switch (interval) {
    case "Second":
      return "the second at " + label;
    case "Minute":
      return "the minute from " + label;
    case "Hour":
      return "the hour from " + label;
    case "Week":
      return label.replace(/^Week of /, "the week of ");
    default:
      return label;
  }
}

/**
 * What the chart is handed for the chosen number: a CountSumAvgMinMax statistic holds five of them
 * per interval, and each is its own graph - the average with its band, or the total, the count, the
 * lowest or the highest as a plain series. A breakdown by value can be drawn as shares of each
 * interval instead of as counts, which is how a change in the mix shows through a change in volume.
 */
function viewOf(
  data: SeriesData,
  measure: Measure,
  percent: boolean,
): { kind: SeriesData["kind"]; points: SeriesPoint[]; valueLabel?: string; format?: (v: number) => string } {
  if (data.kind === "groups" && percent) {
    return {
      kind: "groups",
      points: data.points.map((p) => {
        if (!p.hasValue || !p.values) return p;
        const total = Object.values(p.values).reduce((sum, v) => sum + v, 0);
        if (total === 0) return p;
        const values: Record<string, number> = {};
        for (const [k, v] of Object.entries(p.values)) values[k] = (v / total) * 100;
        return { ...p, values };
      }),
      format: (v) => (Math.round(v * 10) / 10).toString() + "%",
    };
  }
  if (data.kind === "geo") {
    // one number of the several each interval holds, drawn as a plain line (count as bars)
    const pick = (p: SeriesPoint) =>
      measure === "major" ? p.major : measure === "minor" ? p.minor : measure === "drift" ? p.drift : measure === "count" ? p.count : p.value;
    return {
      kind: measure === "count" ? "count" : "sum",
      points: data.points.map((p) => ({ fromUtc: p.fromUtc, hasValue: p.hasValue && pick(p) != null, value: pick(p) ?? null })),
      valueLabel: measure === "count" ? "positions" : measureLabels[measure].toLowerCase(),
      format: measure === "count" ? (v) => formatCount(Math.round(v)) : (v) => formatDistance(v),
    };
  }
  if (data.kind === "heatmap") return { kind: "count", points: data.points, valueLabel: "positions" };
  if (data.kind === "full" || data.kind === "avgminmax") {
    if (measure === "avg") return { kind: data.kind, points: data.points };
    const pick = (p: SeriesPoint) => (measure === "sum" ? p.sum : measure === "count" ? p.count : measure === "min" ? p.min : p.max);
    return {
      kind: measure === "count" ? "count" : "sum",
      points: data.points.map((p) => ({ fromUtc: p.fromUtc, hasValue: p.hasValue && pick(p) != null, value: pick(p) ?? null })),
      valueLabel: measure === "sum" ? "total" : measure === "count" ? "entries" : measure === "min" ? "lowest" : "highest",
    };
  }
  return { kind: data.kind, points: data.points };
}

function valueFormatter(series: CustomLogSeries, kind: SeriesData["kind"], measure: Measure): (v: number) => string {
  if (kind === "count" || kind === "groups" || kind === "heatmap" || (kind === "full" && measure === "count")) return (v) => formatCount(Math.round(v));
  if (series.unit === "meters") return (v) => formatDistance(v);
  if (series.dataType === "Integer") return (v) => (Math.abs(v) >= 1000 ? formatCount(Math.round(v)) : trim(v));
  return (v) => trim(v);
}

/** The line beside a panel's title: what the whole range adds up to, for the number drawn. */
function summaryLine(data: SeriesData, measure: Measure, unit?: string | null): string {
  const s = data.summary;
  if (!s) return "";
  const n = (v: number | null | undefined) => (v == null ? "—" : unit === "meters" ? formatDistance(v) : Math.abs(v) >= 1000 ? formatCount(Math.round(v)) : trim(v));
  switch (data.kind) {
    case "count":
      return s.total == null ? "" : `${formatCount(s.total)} in the range`;
    case "groups":
      return s.total == null ? "" : `${formatCount(s.total)} in the range${s.groups ? ` · ${s.groups.length}${data.groups.includes("Other") ? "+" : ""} values` : ""}`;
    case "sum":
      return s.total == null ? "" : `${n(s.total)} in total`;
    case "avgminmax":
      return `avg ${n(s.avg)} · min ${n(s.min)} · max ${n(s.max)}`;
    case "full":
      if (measure === "sum") return `${n(s.sum)} in total`;
      if (measure === "count") return `${formatCount(s.count ?? 0)} entries`;
      return `${formatCount(s.count ?? 0)} entries · avg ${n(s.avg)} · min ${n(s.min)} · max ${n(s.max)}`;
    case "geo":
      if (s.count == null) return "";
      return `${formatCount(s.count)} positions${s.standardDistance != null ? ` · spread ${formatDistance(s.standardDistance)}` : ""}${
        s.major != null && s.minor != null ? ` (${formatDistance(s.major)} × ${formatDistance(s.minor)})` : ""
      }`;
    case "heatmap":
      return s.total == null ? "" : `${formatCount(s.total)} positions${s.halfWithinSquareMeters ? ` · half within ${formatArea(s.halfWithinSquareMeters)}` : ""}`;
  }
}

/** The points of a graph as comma separated text: one row per interval, gaps as empty cells. */
function seriesCsv(data: SeriesData): string {
  const cell = (v: number | null | undefined) => (v == null ? "" : String(v));
  const quote = (t: string) => (/[",\r\n]/.test(t) ? `"${t.replace(/"/g, '""')}"` : t);
  const lines: string[] = [];
  switch (data.kind) {
    case "groups":
      lines.push(["Interval", ...data.groups].map(quote).join(","));
      for (const p of data.points) lines.push([p.fromUtc, ...data.groups.map((g) => (p.hasValue ? cell(p.values?.[g] ?? 0) : ""))].join(","));
      break;
    case "full":
      lines.push("Interval,Count,Total,Average,Lowest,Highest");
      for (const p of data.points) lines.push([p.fromUtc, cell(p.count), cell(p.sum), cell(p.value), cell(p.min), cell(p.max)].join(","));
      break;
    case "avgminmax":
      lines.push("Interval,Average,Lowest,Highest");
      for (const p of data.points) lines.push([p.fromUtc, cell(p.value), cell(p.min), cell(p.max)].join(","));
      break;
    case "geo":
      lines.push("Interval,Positions,Latitude,Longitude,Spread (m),Along (m),Across (m),Bearing,Centre moved (m)");
      for (const p of data.points) lines.push([p.fromUtc, cell(p.count), cell(p.latitude), cell(p.longitude), cell(p.value), cell(p.major), cell(p.minor), cell(p.bearing), cell(p.drift)].join(","));
      break;
    default:
      lines.push(`Interval,${data.kind === "sum" ? "Total" : "Count"}`);
      for (const p of data.points) lines.push([p.fromUtc, p.hasValue ? cell(p.value) : ""].join(","));
  }
  return lines.join("\r\n") + "\r\n";
}

