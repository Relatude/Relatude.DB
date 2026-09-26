import { useEffect, useRef, useState } from "react";
import { IconChartHistogram, IconDownload, IconPlayerPlay } from "@tabler/icons-react";
import { Histogram, ShareBars, StatTiles, type Tile } from "./LogCharts";
import { analyseColumn, dataTypeLabel, saveText, type AnalyseResult, type CustomLogSummary } from "../server/customLogs";
import type { DatabaseInfo } from "../server/serverInfo";
import { formatCount, formatDateTime } from "../format";
import { formatMeasure, formatMeasureShort, rangeLabel, rangePayload, type LogRange } from "../customLogRange";

/**
 * How the values of one column are spread over the range - read from the entries themselves, which
 * is what the statistics cannot say: they keep a total and an average per interval, and nothing
 * about the middle of the values or their tail. The slowest request in a hundred is a percentile,
 * and it is found here.
 *
 * It reads every entry in the range, the way a search does, so it runs when asked and stops after a
 * set number of entries (the newest), saying when it did. On a small log it runs by itself as soon as
 * a column is picked.
 */

const caps = [50_000, 200_000, 1_000_000];
// below this, reading the whole range is quick enough to do without being asked
const autoRunBytes = 20 * 1024 * 1024;

export function CustomLogAnalyse({
  db,
  log,
  range,
  tick,
  onShowMatching,
}: {
  db: DatabaseInfo;
  log: CustomLogSummary;
  range: LogRange;
  tick: number;
  onShowMatching: (search: string) => void;
}) {
  const [property, setProperty] = useState(() => log.columns.find((c) => c.dataType !== "String")?.key ?? log.columns[0]?.key ?? "");
  const [search, setSearch] = useState("");
  const [cap, setCap] = useState(200_000);
  const [result, setResult] = useState<AnalyseResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const request = useRef(0);
  const column = log.columns.find((c) => c.key === property) ?? null;

  async function run() {
    if (!property) return;
    const id = ++request.current;
    setBusy(true);
    setError(null);
    try {
      const r = await analyseColumn(db.id, log.key, property, rangePayload(range), search.trim() || null, false, cap);
      if (id !== request.current) return; // a newer question was asked meanwhile
      setResult(r);
    } catch (e) {
      if (id !== request.current) return;
      setResult(null);
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      if (id === request.current) setBusy(false);
    }
  }
  // a small log answers in a moment, so picking a column or a range is asking
  const rangeKey = JSON.stringify(range);
  useEffect(() => {
    if (log.logBytes <= autoRunBytes) run();
    else setResult(null);
  }, [property, rangeKey, tick]);

  if (log.columns.length === 0) return <div className="logs-note">This log has no columns to analyse. Add some on its Definition page.</div>;
  const type = result?.dataType ?? column?.dataType ?? "String";
  const fmt = (v: number) => formatMeasure(v, type);
  const d = result?.distribution ?? null;
  const b = result?.breakdown ?? null;

  const tiles: Tile[] = [];
  if (result) {
    tiles.push({ label: "Entries read", value: formatCount(result.scanned), hint: result.truncated ? `stopped at ${formatCount(result.cap)}` : "every entry in the range" });
    tiles.push({ label: "With a value", value: formatCount(result.withValue), hint: `${formatCount(result.matched - result.withValue)} of the entries have none` });
  }
  if (d) {
    const p = (rank: number) => d.percentiles.find((x) => x.p === rank)?.value;
    tiles.push({ label: "Lowest", value: fmt(d.min) });
    tiles.push({ label: "Median", value: fmt(p(50) ?? d.mean), hint: "half the values are below this" });
    tiles.push({ label: "90th percentile", value: fmt(p(90) ?? d.max), hint: "nine in ten are below this" });
    tiles.push({ label: "99th percentile", value: fmt(p(99) ?? d.max), hint: "one in a hundred is above this" });
    tiles.push({ label: "Highest", value: fmt(d.max) });
    tiles.push({ label: "Average", value: fmt(d.mean), hint: `standard deviation ${fmt(d.stdDev)}` });
    if (type === "Integer" || type === "Double") tiles.push({ label: "Total", value: fmt(d.sum) });
  }
  if (b) {
    tiles.push({ label: "Distinct values", value: formatCount(b.distinct) + (b.distinctCapped ? "+" : ""), hint: b.distinctCapped ? "more than could be kept count of" : undefined });
    if (b.top[0]) tiles.push({ label: "Most common", value: b.top[0].value || "(empty)", hint: `${formatCount(b.top[0].count)} times` });
  }

  function downloadCsv() {
    if (!result) return;
    const lines: string[] = [];
    const quote = (t: string) => (/[",\r\n]/.test(t) ? `"${t.replace(/"/g, '""')}"` : t);
    if (d) {
      lines.push("Percentile,Value");
      for (const p of d.percentiles) lines.push(`${p.p},${p.value}`);
      lines.push("");
      lines.push("From,To,Entries");
      for (const bin of d.histogram) lines.push(`${bin.from},${bin.to},${bin.count}`);
    } else if (b) {
      lines.push("Value,Entries");
      for (const t of b.top) lines.push(`${quote(t.value)},${t.count}`);
      if (b.other > 0) lines.push(`(other),${b.other}`);
    }
    saveText(lines.join("\r\n") + "\r\n", `${log.key}-${property}-distribution.csv`, "text/csv");
  }

  return (
    <div className="clog-analyse">
      <div className="logs-toolbar">
        <label className="clog-inline-field">
          Column
          <select className="select" value={property} onChange={(e) => setProperty(e.currentTarget.value)}>
            {log.columns.map((c) => (
              <option key={c.key} value={c.key}>
                {c.name} ({dataTypeLabel[c.dataType].toLowerCase()})
              </option>
            ))}
          </select>
        </label>
        <input
          className="text-input clog-analyse-search"
          value={search}
          placeholder="Only entries matching… (optional, the search syntax of the entries)"
          onChange={(e) => setSearch(e.currentTarget.value)}
          onKeyDown={(e) => e.key === "Enter" && run()}
        />
        <label className="clog-inline-field" title="The newest this many entries of the range are read; the rest are left out">
          Read at most
          <select className="select" value={cap} onChange={(e) => setCap(Number(e.currentTarget.value))}>
            {caps.map((c) => (
              <option key={c} value={c}>
                {formatCount(c)}
              </option>
            ))}
          </select>
        </label>
        <button className="action-button primary" onClick={run} disabled={busy || !property}>
          <IconPlayerPlay size={15} stroke={1.8} /> {busy ? "Reading…" : "Analyse"}
        </button>
        <span className="logs-spacer" />
        <button className="action-button" onClick={downloadCsv} disabled={!result || (!d && !b)} title="The percentiles and the histogram, or the values and their counts, as comma separated text">
          <IconDownload size={15} stroke={1.8} />
        </button>
      </div>

      {error && <div className="logs-note">{error}</div>}
      {!result && !busy && !error && (
        <div className="logs-note">
          Reads the entries of {rangeLabel(range)} and shows how the values of the column are spread. This log is large enough that it waits to be asked:
          press Analyse.
        </div>
      )}
      {result && (
        <>
          <StatTiles tiles={tiles} />
          {result.truncated && (
            <div className="logs-note">
              The read stopped after the newest {formatCount(result.cap)} entries of {rangeLabel(range)}
              {result.firstUtc ? `, reaching back to ${formatDateTime(result.firstUtc)}` : ""}. Narrow the range, or read more, to cover all of it.
            </div>
          )}
          {d ? (
            <div className="clog-analyse-grid">
              <section className="panel">
                <h3>
                  <IconChartHistogram size={15} stroke={1.8} /> Spread of {column?.name ?? property}{" "}
                  <span className="panel-sub">
                    {formatCount(d.count)} values, {d.histogram.length} {d.histogram.length === 1 ? "bar" : "bars"}
                  </span>
                </h3>
                <Histogram
                  bins={d.histogram}
                  format={(v) => formatMeasureShort(v, type)}
                  height={240}
                  marks={[50, 90, 99]
                    .map((rank) => ({ rank, value: d.percentiles.find((x) => x.p === rank)?.value }))
                    .filter((m): m is { rank: number; value: number } => m.value != null)
                    .map((m) => ({ value: m.value, label: m.rank === 50 ? "median" : `p${m.rank}` }))}
                />
              </section>
              <section className="panel">
                <h3>Percentiles</h3>
                <div className="clog-percentiles">
                  {d.percentiles.map((p) => (
                    <div key={p.p} className="clog-percentile">
                      <span className="muted">{p.p === 50 ? "median" : `${p.p}%`}</span>
                      <span className="clog-percentile-bar">
                        <span style={{ width: (d.max > d.min ? ((p.value - d.min) / (d.max - d.min)) * 100 : 100) + "%" }} />
                      </span>
                      <span className="clog-percentile-value">{fmt(p.value)}</span>
                    </div>
                  ))}
                </div>
                <div className="muted clog-analyse-foot">A percentile is the value that share of the entries stays under.</div>
              </section>
            </div>
          ) : b ? (
            <section className="panel">
              <h3>
                Values of {column?.name ?? property}{" "}
                <span className="panel-sub">
                  the {Math.min(b.top.length, b.distinct)} most common of {formatCount(b.distinct)}
                  {b.distinctCapped ? "+" : ""} · click one to list its entries
                </span>
              </h3>
              <ShareBars
                items={[
                  ...b.top.map((t) => ({
                    label: t.value,
                    count: t.count,
                    hint: "List the entries with this value",
                    onClick: () => onShowMatching(`${property}:${/\s/.test(t.value) || t.value === "" ? `"${t.value}"` : t.value}`),
                  })),
                  ...(b.other > 0 ? [{ label: `${formatCount(b.distinct - b.top.length)} other values`, count: b.other }] : []),
                ]}
                total={b.total}
                format={(n) => formatCount(n)}
                empty="No entry in the range has a value in this column."
              />
            </section>
          ) : (
            <div className="logs-note">No entry in {rangeLabel(range)} has a value in this column.</div>
          )}
        </>
      )}
    </div>
  );
}
