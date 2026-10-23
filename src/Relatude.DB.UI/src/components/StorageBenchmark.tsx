import { Fragment, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import type { RefObject } from "react";
import { IconAlertTriangle, IconFileDownload, IconGauge } from "@tabler/icons-react";
import { DialogTools } from "./DialogTools";
import { CopyButton } from "./CopyButton";
import { sizeLabel } from "./NodeSizesDialog";
import type { TableData } from "../clipboard";
import type { IoInfo } from "../server/files";
import type { BenchmarkGroup, BenchmarkRun, BenchmarkStorage, BenchmarkTest } from "../server/benchmark";
import { formatBytes, formatCount, formatDateTime } from "../format";

export const benchmarkDurations = [
  { ms: 5_000, label: "Quick" },
  { ms: 15_000, label: "Standard" },
  { ms: 60_000, label: "Thorough" },
];

const groupOrder: BenchmarkGroup[] = ["write", "durable", "sequential", "scattered", "uncached", "files"];

const groupTitles: Record<BenchmarkGroup, string> = {
  write: "Writes",
  durable: "Durable writes",
  sequential: "Sequential reads",
  scattered: "Scattered reads",
  uncached: "Reads past the cache",
  files: "File operations",
};

const groupHelp: Record<BenchmarkGroup, string> = {
  write:
    "Appends handed to the storage and flushed to it, without waiting for them to be stored for good: how fast the storage takes data. On a local disk that is the operating system's cache, not the drive.",
  durable:
    "Writes the storage confirms as stored before the next one starts. A commit is what every transaction waits for, so its time is the floor under how fast the database can save; the stream is the sustained write speed of the drive or the service itself.",
  sequential: "A file read from start to end, the way the database reads its log and state file when it opens.",
  scattered:
    "Pieces read from random places in a file, the way the database reads nodes that are not in its cache. Here the time one read takes matters more than the throughput.",
  uncached:
    "The same reads with the operating system's file cache out of the way, so they come from the drive: what a read costs when nothing is cached, after a restart or on a database larger than memory.",
  files:
    "The small requests around the data: whether a file exists, how big it is, opening one, creating and deleting one, listing a folder. On a network storage every one is a round trip.",
};

const fileLabels: Record<string, string> = {
  "file-exists": "Exists",
  "file-size": "Size",
  "file-open": "Open",
  "file-create": "Create",
  "folder-list": "List",
};

const roleLabels: Record<string, string> = {
  database: "database file",
  secondary: "secondary log",
  indexes: "indexes",
  files: "file store",
  backup: "backups",
  log: "logs",
};

type Measure = "throughput" | "rate" | "latency";

interface Headline {
  id: string;
  label: string;
  hint: string;
  measure: Measure;
  secondary?: Measure;
}

const headlines: Headline[] = [
  { id: "write-4194304", label: "Large writes", hint: "appending 4 MB at a time", measure: "throughput" },
  { id: "write-4096", label: "Small writes", hint: "appending 4 KB at a time", measure: "rate", secondary: "throughput" },
  { id: "commit-4096", label: "Commit", hint: "4 KB made durable before the next, as a transaction is", measure: "latency", secondary: "rate" },
  { id: "durable-stream", label: "Durable throughput", hint: "1 MB pieces, made durable as they go", measure: "throughput" },
  { id: "sequential-1048576", label: "Large reads", hint: "a file in order, 1 MB at a time", measure: "throughput" },
  { id: "scattered-4096", label: "Random reads", hint: "4 KB from random places", measure: "latency", secondary: "rate" },
  { id: "uncached-4096", label: "Random reads past the cache", hint: "4 KB from random places, from the drive itself", measure: "latency", secondary: "rate" },
  { id: "file-open", label: "Open a file", hint: "open it for reading and close it again", measure: "latency", secondary: "rate" },
  { id: "file-exists", label: "Smallest request", hint: "ask whether a file exists", measure: "latency", secondary: "rate" },
];

const summaryColumns: { id: string; label: string; measure: Measure }[] = [
  { id: "write-4194304", label: "Large writes", measure: "throughput" },
  { id: "commit-4096", label: "Commit 4 KB", measure: "latency" },
  { id: "sequential-1048576", label: "Large reads", measure: "throughput" },
  { id: "scattered-4096", label: "Random 4 KB read", measure: "latency" },
  { id: "uncached-4096", label: "Uncached 4 KB read", measure: "latency" },
];

const smallestLatencyUs = 0.05;
const pickKey = (dbId: string) => `benchmarkPick:${dbId}`;
const durationKey = "benchmarkDuration";
const groupKey = "benchmarkGroup";

export function seriesColor(index: number): string {
  return `var(--viz-${(index % 8) + 1})`;
}

export function typeLabel(type: string): string {
  if (type === "LocalDisk") return "Local disk";
  if (type === "AzureBlobStorage") return "Azure Blob Storage";
  return type;
}

export function formatThroughput(bytesPerSecond: number): string {
  return bytesPerSecond > 0 ? formatBytes(bytesPerSecond) + "/s" : "—";
}

export function formatPerSecond(n: number): string {
  if (n >= 1e7) return Math.round(n / 1e6) + "M";
  if (n >= 1e6) return (n / 1e6).toFixed(1) + "M";
  if (n >= 1e4) return Math.round(n / 1e3) + "K";
  if (n >= 1e3) return (n / 1e3).toFixed(1) + "K";
  return n >= 10 ? String(Math.round(n)) : n.toFixed(1);
}

export function formatLatency(us: number): string {
  if (us < 0.1) return "< 0.1 µs";
  if (us < 10) return `${us.toFixed(1)} µs`;
  if (us < 1000) return `${Math.round(us)} µs`;
  if (us < 10_000) return `${(us / 1000).toFixed(2)} ms`;
  if (us < 1_000_000) return `${(us / 1000).toFixed(us < 100_000 ? 1 : 0)} ms`;
  return `${(us / 1e6).toFixed(2)} s`;
}

function formatResolution(us: number): string {
  if (us >= 1) return formatLatency(us);
  return us >= 0.1 ? `${+us.toFixed(2)} µs` : `${Math.max(1, Math.round(us * 1000))} ns`;
}

export function formatSpan(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)} ms`;
  if (ms < 90_000) return `${+(ms / 1000).toFixed(ms < 10_000 ? 1 : 0)} s`;
  return `${+(ms / 60_000).toFixed(1)} min`;
}

function rateUnit(test: BenchmarkTest): string {
  switch (test.group) {
    case "write":
      return " writes/s";
    case "durable":
      return test.id.startsWith("commit") ? " commits/s" : " writes/s";
    case "files":
      return "/s";
    default:
      return " reads/s";
  }
}

export function testOf(storage: BenchmarkStorage, id: string): BenchmarkTest | null {
  const test = storage.report.tests.find((t) => t.id === id);
  return test && !test.error && test.operations > 0 ? test : null;
}

function valueOf(test: BenchmarkTest, measure: Measure): number | null {
  if (measure === "throughput") return test.bytesPerSecond > 0 ? test.bytesPerSecond : null;
  if (measure === "rate") return test.operationsPerSecond > 0 ? test.operationsPerSecond : null;
  return test.latency ? Math.max(test.latency.p50Us, smallestLatencyUs) : null;
}

export function formatMeasure(test: BenchmarkTest, measure: Measure): string {
  if (measure === "throughput") return formatThroughput(test.bytesPerSecond);
  if (measure === "rate") return formatPerSecond(test.operationsPerSecond) + rateUnit(test);
  return test.latency ? formatLatency(test.latency.p50Us) : "—";
}

function shortLabel(test: BenchmarkTest): string {
  if (test.group === "files") return fileLabels[test.id] ?? test.name;
  if (test.id === "durable-stream") return `${sizeLabel(test.blockSize)} stream`;
  if (test.id === "uncached-sequential") return `${sizeLabel(test.blockSize)} in order`;
  return sizeLabel(test.blockSize);
}

function readStored(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function writeStored(key: string, value: string) {
  try {
    localStorage.setItem(key, value);
  } catch {
    return;
  }
}

function describeStorage(storage: IoInfo): string {
  if (storage.kind === "projectRoot") return "the website project folder" + (storage.localPath ? " · " + storage.localPath : "");
  const roles = (storage.roles ?? []).map((r) => roleLabels[r] ?? r);
  return roles.length > 0 ? roles.join(" · ") : "not used by this database";
}

function initialPick(dbId: string, storages: IoInfo[]): Set<string> {
  const stored = readStored(pickKey(dbId));
  if (stored) {
    const ids = stored.split(",").filter((id) => storages.some((s) => s.id === id));
    if (ids.length > 0) return new Set(ids);
  }
  const used = storages.filter((s) => s.kind === "storage" && (s.roles?.length ?? 0) > 0);
  return new Set((used.length > 0 ? used : storages.filter((s) => s.kind === "storage")).map((s) => s.id));
}

function storedDuration(): number {
  const stored = Number(readStored(durationKey));
  return benchmarkDurations.some((d) => d.ms === stored) ? stored : benchmarkDurations[0].ms;
}

export function BenchmarkSetupDialog({
  dbId,
  storages,
  onCancel,
  onRun,
}: {
  dbId: string;
  storages: IoInfo[];
  onCancel: () => void;
  onRun: (ioIds: string[], durationMs: number) => void;
}) {
  const [picked, setPicked] = useState<Set<string>>(() => initialPick(dbId, storages));
  const [durationMs, setDurationMs] = useState(storedDuration);
  const dialogRef = useRef<HTMLDivElement>(null);
  useEffect(() => dialogRef.current?.focus(), []);
  const chosen = storages.filter((s) => picked.has(s.id));

  function toggle(id: string) {
    setPicked((previous) => {
      const next = new Set(previous);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  function run() {
    if (chosen.length === 0) return;
    writeStored(pickKey(dbId), chosen.map((s) => s.id).join(","));
    writeStored(durationKey, String(durationMs));
    onRun(
      chosen.map((s) => s.id),
      durationMs,
    );
  }

  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onCancel()}>
      <div
        ref={dialogRef}
        className="dialog bench-setup"
        role="dialog"
        aria-label="Benchmark storage"
        tabIndex={-1}
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            e.preventDefault();
            onCancel();
          }
        }}
      >
        <h3>
          <IconGauge size={16} stroke={1.9} /> Benchmark storage
          <DialogTools onClose={onCancel} />
        </h3>
        <div className="dialog-body">
          Measures how fast each storage you pick writes and reads - in small and large pieces, in order and at random - and how long a single
          operation takes. The storages are tested one after the other and compared in one report.
        </div>
        <div className="bench-pick-list">
          {storages.map((s) => (
            <label key={s.id} className={"bench-pick" + (picked.has(s.id) ? " on" : "")}>
              <input type="checkbox" checked={picked.has(s.id)} onChange={() => toggle(s.id)} />
              <span className="bench-pick-text">
                <span className="bench-pick-name">{s.name}</span>
                <span className="muted bench-ellipsis" title={describeStorage(s)}>
                  {describeStorage(s)}
                </span>
              </span>
              <span className="badge">{typeLabel(s.type)}</span>
            </label>
          ))}
        </div>
        <div className="bench-setup-row">
          <span className="ns-control-label">Time per storage</span>
          <div className="module-switch compact">
            {benchmarkDurations.map((d) => (
              <button key={d.ms} className={d.ms === durationMs ? "active" : ""} onClick={() => setDurationMs(d.ms)} title={`${formatSpan(d.ms)} per storage`}>
                {d.label}
              </button>
            ))}
          </div>
          <span className="muted">
            {formatSpan(durationMs)} each
            {chosen.length > 1 ? `, about ${formatSpan(durationMs * chosen.length)} in all` : ""}
          </span>
        </div>
        <div className="muted bench-setup-note">
          The test files are written to the upload folder of each storage and deleted when it is done. The database stays open while the test runs: its
          own reads and writes slow the test down, and the test slows them. A longer run gives steadier numbers, above all on network storage where every
          operation is a round trip.
        </div>
        <div className="dialog-row">
          <div className="header-spacer" />
          <button className="action-button dialog-confirm" disabled={chosen.length === 0} onClick={run}>
            Run benchmark
          </button>
          <button className="action-button" onClick={onCancel}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}

export function BenchmarkReportDialog({ run, dbName, onClose }: { run: BenchmarkRun; dbName: string; onClose: () => void }) {
  const dialogRef = useRef<HTMLDivElement>(null);
  useEffect(() => dialogRef.current?.focus(), []);
  const failed = run.storages.filter((s) => s.report.error);
  const count = run.storages.length;
  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div
        ref={dialogRef}
        className="dialog bench-dialog"
        role="dialog"
        aria-label="Storage benchmark"
        tabIndex={-1}
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            e.preventDefault();
            onClose();
          }
        }}
      >
        <h3>
          <IconGauge size={16} stroke={1.9} /> Storage benchmark — {dbName}
          <DialogTools onClose={onClose} />
        </h3>
        <div className="bench-body">
          <div className="ns-controls bench-meta">
            <span className="muted">
              {formatDateTime(run.startedUtc)} · {formatSpan(run.durationMs)} per storage · {count} storage{count === 1 ? "" : "s"} · on {run.machine.name} (
              {run.machine.os}, {run.machine.processors} cores)
            </span>
            <span className="header-spacer" />
            <CopyButton title="Copy every result as a table" table={() => runTable(run)} />
            <button className="icon-button" title="Download the results as JSON" onClick={() => downloadJson(run, dbName)}>
              <IconFileDownload size={16} stroke={1.8} />
            </button>
          </div>
          {run.cancelled && (
            <div className="files-notice">
              <IconAlertTriangle size={15} stroke={1.8} />
              <span>
                <b>Stopped before the end.</b> What is shown is what was measured until then.
              </span>
            </div>
          )}
          {failed.map((s) => (
            <div key={s.ioId} className="dialog-error">
              {s.name}: {s.report.error}
            </div>
          ))}
          <section className="ns-panel">
            <div className="ns-panel-head">
              <span className="ns-panel-title">At a glance</span>
              <span className="muted">
                {count > 1 ? "the numbers that say most about a storage, side by side; the longest bar is the best" : "the numbers that say most about a storage"}
              </span>
            </div>
            <Glance run={run} />
          </section>
          <GroupCharts run={run} />
          <Details run={run} />
          <Help />
        </div>
      </div>
    </div>
  );
}

export function BenchmarkSummary({ run }: { run: BenchmarkRun }) {
  const columns = summaryColumns.filter((c) => run.storages.some((s) => testOf(s, c.id)));
  return (
    <div className="bench-scroll">
      <div className="bench-summary-table" style={{ gridTemplateColumns: `minmax(160px, 1.4fr) repeat(${columns.length}, minmax(96px, 1fr))` }}>
        <span className="bench-th">Storage</span>
        {columns.map((c) => (
          <span key={c.id} className="bench-th num" title={c.measure === "latency" ? "Typical time of one operation" : "Throughput"}>
            {c.label}
          </span>
        ))}
        {run.storages.map((s, i) => (
          <Fragment key={s.ioId}>
            <span className="bench-summary-name" title={s.report.location ?? undefined}>
              <span className="ns-swatch" style={{ background: seriesColor(i) }} />
              <span className="bench-ellipsis">{s.name}</span>
              <span className="badge">{typeLabel(s.type)}</span>
            </span>
            {columns.map((c) => {
              const test = testOf(s, c.id);
              return (
                <span key={c.id} className="num" title={test?.description}>
                  {test ? formatMeasure(test, c.measure) : "—"}
                </span>
              );
            })}
          </Fragment>
        ))}
      </div>
    </div>
  );
}

function storageTitle(storage: BenchmarkStorage): string {
  const kind = storage.report.kind || typeLabel(storage.type);
  return kind === storage.name ? kind : `${storage.name} · ${kind}`;
}

function StorageHead({ storage, index }: { storage: BenchmarkStorage; index: number }) {
  return (
    <div className="bench-storage-head">
      <span className="bench-storage-name">
        <span className="ns-swatch" style={{ background: seriesColor(index) }} />
        <span className="bench-ellipsis">{storage.name}</span>
      </span>
      <span className="muted bench-ellipsis" title={storage.report.location ?? undefined}>
        {storage.report.kind || typeLabel(storage.type)}
        {storage.report.location ? " · " + storage.report.location : ""}
      </span>
    </div>
  );
}

function Glance({ run }: { run: BenchmarkRun }) {
  const rows = headlines.filter((h) => run.storages.some((s) => testOf(s, h.id)));
  const many = run.storages.length > 1;
  return (
    <div className="bench-scroll">
      <div className="bench-glance" style={{ gridTemplateColumns: `minmax(180px, 1.3fr) repeat(${run.storages.length}, minmax(150px, 1fr))` }}>
        <div className="bench-glance-corner" />
        {run.storages.map((s, i) => (
          <StorageHead key={s.ioId} storage={s} index={i} />
        ))}
        {rows.map((h) => {
          const values = run.storages.map((s) => {
            const test = testOf(s, h.id);
            return test ? valueOf(test, h.measure) : null;
          });
          const present = values.filter((v): v is number => v != null);
          const best = h.measure === "latency" ? Math.min(...present) : Math.max(...present);
          return (
            <Fragment key={h.id}>
              <div className="bench-glance-label">
                <span>{h.label}</span>
                <span className="muted">{h.hint}</span>
              </div>
              {run.storages.map((s, i) => {
                const test = testOf(s, h.id);
                const value = values[i];
                if (!test || value == null) {
                  return (
                    <div key={s.ioId} className="bench-glance-cell missing" title="Not measured on this storage">
                      —
                    </div>
                  );
                }
                const share = h.measure === "latency" ? best / value : value / best;
                const secondary = h.secondary ? formatMeasure(test, h.secondary) : null;
                return (
                  <div key={s.ioId} className={"bench-glance-cell" + (many && share >= 0.995 ? " best" : "")} title={test.description}>
                    <span className="bench-glance-value">{formatMeasure(test, h.measure)}</span>
                    {(h.measure === "latency" || secondary) && (
                      <span className="muted">
                        {h.measure === "latency" ? "typical" : ""}
                        {h.measure === "latency" && secondary ? " · " : ""}
                        {secondary}
                      </span>
                    )}
                    {many && (
                      <span className="bench-bar">
                        <span className="bench-bar-fill" style={{ width: `${Math.max(2, Math.round(share * 100))}%`, background: seriesColor(i) }} />
                      </span>
                    )}
                  </div>
                );
              })}
            </Fragment>
          );
        })}
      </div>
    </div>
  );
}

interface Category {
  id: string;
  label: string;
}

function categoriesOf(run: BenchmarkRun, group: BenchmarkGroup): Category[] {
  const seen = new Map<string, Category>();
  for (const storage of run.storages) {
    for (const test of storage.report.tests) {
      if (test.group === group && !seen.has(test.id) && !test.error && test.operations > 0) seen.set(test.id, { id: test.id, label: shortLabel(test) });
    }
  }
  return [...seen.values()];
}

function GroupCharts({ run }: { run: BenchmarkRun }) {
  const groups = groupOrder.filter((g) => run.storages.some((s) => s.report.tests.some((t) => t.group === g && !t.error && t.operations > 0)));
  const [chosen, setChosen] = useState<BenchmarkGroup>(() => (readStored(groupKey) ?? "write") as BenchmarkGroup);
  const group = groups.includes(chosen) ? chosen : groups[0];
  const categories = useMemo(() => (group ? categoriesOf(run, group) : []), [run, group]);
  if (!group) return null;
  const measure: Measure = group === "files" ? "rate" : "throughput";
  function pick(g: BenchmarkGroup) {
    setChosen(g);
    writeStored(groupKey, g);
  }
  return (
    <section className="ns-panel bench-charts">
      <div className="ns-panel-head">
        <span className="ns-panel-title">{group === "files" ? "Operation by operation" : "Size by size"}</span>
        <div className="module-switch compact bench-group-switch">
          {groups.map((g) => (
            <button key={g} className={g === group ? "active" : ""} onClick={() => pick(g)}>
              {groupTitles[g]}
            </button>
          ))}
        </div>
      </div>
      <div className="muted bench-group-help">{groupHelp[group]}</div>
      <div className="bench-chart-row">
        <div className="bench-chart-cell">
          <div className="bench-chart-title">
            {measure === "rate" ? "Operations per second" : "Throughput"} <span className="muted">higher is better · log scale</span>
          </div>
          <RateChart key={group} run={run} categories={categories} measure={measure} />
        </div>
        <div className="bench-chart-cell">
          <div className="bench-chart-title">
            Time per operation <span className="muted">lower is better · log scale</span>
          </div>
          <LatencyChart key={group} run={run} categories={categories} />
        </div>
      </div>
      <div className="bench-legend">
        {run.storages.map((s, i) => (
          <span key={s.ioId} className="bench-legend-item">
            <span className="ns-swatch" style={{ background: seriesColor(i) }} />
            {s.name}
          </span>
        ))}
        <span className="bench-legend-key">
          <LatencyGlyph /> typical · bar to the 99th percentile · tick at the slowest
        </span>
      </div>
    </section>
  );
}

function LatencyGlyph() {
  return (
    <svg width="22" height="16" viewBox="0 0 22 16" aria-hidden="true">
      <line x1="7" x2="15" y1="2" y2="2" className="bench-glyph" />
      <rect x="8" y="4" width="6" height="8" rx="3" className="bench-glyph-bar" />
      <circle cx="11" cy="11" r="3.5" className="bench-glyph-dot" />
    </svg>
  );
}

function useWidth<T extends HTMLElement>(): [RefObject<T | null>, number] {
  const ref = useRef<T>(null);
  const [width, setWidth] = useState(0);
  useLayoutEffect(() => {
    const element = ref.current;
    if (!element) return;
    setWidth(element.clientWidth);
    const observer = new ResizeObserver(() => setWidth(element.clientWidth));
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  return [ref, width];
}

type ScaleKind = "bytes" | "count" | "time";

interface LogScale {
  ticks: number[];
  label: (value: number) => string;
  fraction: (value: number) => number;
}

const byteUnits = ["B", "KB", "MB", "GB", "TB"];
const byteSteps = byteUnits.flatMap((unit, k) => [1, 10, 100].map((m) => ({ value: m * Math.pow(1024, k), label: `${m} ${unit}/s` })));
const countSteps = [0.1, 1, 10, 100, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9].map((value) => ({ value, label: value < 1 ? "0.1/s" : formatPerSecond(value) + "/s" }));
const timeSteps = [0.01, 0.1, 1, 10, 100, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8].map((value) => ({ value, label: value < 0.1 ? "0.01 µs" : formatLatency(value) }));

function logScale(values: number[], kind: ScaleKind): LogScale {
  const steps = kind === "bytes" ? byteSteps : kind === "count" ? countSteps : timeSteps;
  const positive = values.filter((v) => v > 0);
  const min = positive.length > 0 ? Math.min(...positive) : steps[1].value;
  const max = positive.length > 0 ? Math.max(...positive) : steps[2].value;
  let low = 0;
  while (low < steps.length - 2 && steps[low + 1].value <= min) low++;
  let high = steps.length - 1;
  while (high > low + 1 && steps[high - 1].value >= max) high--;
  const span = steps.slice(low, high + 1);
  const every = Math.ceil(span.length / 6);
  const shown = span.filter((_, i) => (span.length - 1 - i) % every === 0);
  const lo = Math.log(span[0].value);
  const range = Math.log(span[span.length - 1].value) - lo || 1;
  const labels = new Map(span.map((s) => [s.value, s.label]));
  return {
    ticks: shown.map((s) => s.value),
    label: (v) => labels.get(v) ?? String(v),
    fraction: (v) => (v <= 0 ? 0 : Math.min(1, Math.max(0, (Math.log(v) - lo) / range))),
  };
}

const chartHeight = 240;
const chartPad = { left: 70, right: 12, top: 12, bottom: 30 };

interface Tip {
  x: number;
  y: number;
  title: string;
  lines: { color: string; label: string; value: string }[];
}

function ChartTip({ tip, width }: { tip: Tip | null; width: number }) {
  if (!tip) return null;
  const left = tip.x > width / 2 ? undefined : tip.x + 14;
  const right = tip.x > width / 2 ? width - tip.x + 14 : undefined;
  return (
    <div className="ns-tip bench-tip" style={{ left, right, top: Math.max(0, tip.y - 10) }}>
      <div className="ns-tip-title">{tip.title}</div>
      {tip.lines.map((line, i) => (
        <div key={i} className="ns-tip-line">
          <span className="ns-swatch" style={{ background: line.color }} />
          <span className="bench-ellipsis">{line.label}</span>
          <span className="num">{line.value}</span>
        </div>
      ))}
    </div>
  );
}

function Axis({ scale, width, plotH }: { scale: LogScale; width: number; plotH: number }) {
  const y = (v: number) => chartPad.top + plotH - scale.fraction(v) * plotH;
  return (
    <>
      {scale.ticks.map((t) => (
        <g key={t}>
          <line className="ns-grid" x1={chartPad.left} x2={width - chartPad.right} y1={y(t)} y2={y(t)} />
          <text className="ns-axis" x={chartPad.left - 6} y={y(t)} dy="0.32em" textAnchor="end">
            {scale.label(t)}
          </text>
        </g>
      ))}
      <line className="ns-baseline" x1={chartPad.left} x2={width - chartPad.right} y1={chartPad.top + plotH} y2={chartPad.top + plotH} />
    </>
  );
}

function CategoryLabels({ categories, x }: { categories: Category[]; x: (i: number) => number }) {
  return (
    <>
      {categories.map((c, i) => (
        <text key={c.id} className="ns-axis" x={x(i)} y={chartHeight - 10} textAnchor="middle">
          {c.label}
        </text>
      ))}
    </>
  );
}

function HitColumns({
  categories,
  x,
  colW,
  plotH,
  host,
  onHover,
}: {
  categories: Category[];
  x: (i: number) => number;
  colW: number;
  plotH: number;
  host: RefObject<HTMLDivElement | null>;
  onHover: (hover: { index: number; x: number; y: number }) => void;
}) {
  return (
    <>
      {categories.map((c, i) => (
        <rect
          key={c.id}
          className="ns-hit"
          x={x(i) - colW / 2}
          y={chartPad.top}
          width={colW}
          height={plotH}
          onMouseMove={(e) => {
            const box = host.current!.getBoundingClientRect();
            onHover({ index: i, x: e.clientX - box.left, y: e.clientY - box.top });
          }}
        />
      ))}
    </>
  );
}

function RateChart({ run, categories, measure }: { run: BenchmarkRun; categories: Category[]; measure: Measure }) {
  const [ref, width] = useWidth<HTMLDivElement>();
  const [hovered, setHover] = useState<{ index: number; x: number; y: number } | null>(null);
  const hover = hovered && hovered.index < categories.length ? hovered : null;
  const series = run.storages.map((storage, si) => ({
    storage,
    color: seriesColor(si),
    points: categories.map((c) => {
      const test = testOf(storage, c.id);
      return { test, value: test ? valueOf(test, measure) : null };
    }),
  }));
  const scale = logScale(
    series.flatMap((s) => s.points.map((p) => p.value ?? 0)),
    measure === "throughput" ? "bytes" : "count",
  );
  const plotW = Math.max(10, width - chartPad.left - chartPad.right);
  const plotH = chartHeight - chartPad.top - chartPad.bottom;
  const colW = plotW / Math.max(1, categories.length);
  const x = (i: number) => chartPad.left + (i + 0.5) * colW;
  const y = (v: number) => chartPad.top + plotH - scale.fraction(v) * plotH;

  function tipFor(index: number, at: { x: number; y: number }): Tip {
    return {
      ...at,
      title: categories[index].label + (measure === "rate" ? "" : " at a time"),
      lines: series
        .filter((s) => s.points[index].test)
        .map((s) => {
          const test = s.points[index].test!;
          const value = measure === "throughput" ? `${formatThroughput(test.bytesPerSecond)} · ${formatPerSecond(test.operationsPerSecond)}/s` : formatPerSecond(test.operationsPerSecond) + "/s";
          return { color: s.color, label: s.storage.name, value };
        }),
    };
  }

  return (
    <div className="ns-chart" ref={ref} onMouseLeave={() => setHover(null)}>
      {width > 0 && (
        <svg width={width} height={chartHeight} role="img" aria-label={measure === "rate" ? "Operations per second by operation" : "Throughput by size"}>
          <Axis scale={scale} width={width} plotH={plotH} />
          {hover && <rect className="ns-hover-band" x={x(hover.index) - colW / 2} y={chartPad.top} width={colW} height={plotH} />}
          {series.map((s) => {
            const runs: { i: number; v: number }[][] = [];
            let current: { i: number; v: number }[] = [];
            s.points.forEach((p, i) => {
              if (p.value != null) current.push({ i, v: p.value });
              else if (current.length > 0) {
                runs.push(current);
                current = [];
              }
            });
            if (current.length > 0) runs.push(current);
            return (
              <g key={s.storage.ioId}>
                {runs.map((segment, k) => (
                  <path key={k} className="bench-line" stroke={s.color} d={segment.map((p, j) => `${j === 0 ? "M" : "L"}${x(p.i).toFixed(1)},${y(p.v).toFixed(1)}`).join(" ")} />
                ))}
                {s.points.map((p, i) => (p.value != null ? <circle key={i} className="bench-dot" cx={x(i)} cy={y(p.value)} r={4} fill={s.color} /> : null))}
              </g>
            );
          })}
          <CategoryLabels categories={categories} x={x} />
          <HitColumns categories={categories} x={x} colW={colW} plotH={plotH} host={ref} onHover={setHover} />
        </svg>
      )}
      <ChartTip tip={hover ? tipFor(hover.index, hover) : null} width={width} />
    </div>
  );
}

function LatencyChart({ run, categories }: { run: BenchmarkRun; categories: Category[] }) {
  const [ref, width] = useWidth<HTMLDivElement>();
  const [hovered, setHover] = useState<{ index: number; x: number; y: number } | null>(null);
  const hover = hovered && hovered.index < categories.length ? hovered : null;
  const series = run.storages.map((storage, si) => ({
    storage,
    color: seriesColor(si),
    tests: categories.map((c) => testOf(storage, c.id)),
  }));
  const values = series.flatMap((s) => s.tests.flatMap((t) => (t?.latency ? [t.latency.p50Us, t.latency.p99Us, t.latency.maxUs].map((v) => Math.max(v, smallestLatencyUs)) : [])));
  const scale = logScale(values, "time");
  const plotW = Math.max(10, width - chartPad.left - chartPad.right);
  const plotH = chartHeight - chartPad.top - chartPad.bottom;
  const colW = plotW / Math.max(1, categories.length);
  const x = (i: number) => chartPad.left + (i + 0.5) * colW;
  const y = (v: number) => chartPad.top + plotH - scale.fraction(Math.max(v, smallestLatencyUs)) * plotH;
  const slot = Math.min(16, colW / (series.length + 1));

  function tipFor(index: number, at: { x: number; y: number }): Tip {
    return {
      ...at,
      title: categories[index].label,
      lines: series
        .filter((s) => s.tests[index]?.latency)
        .map((s) => {
          const l = s.tests[index]!.latency!;
          return { color: s.color, label: s.storage.name, value: `${formatLatency(l.p50Us)} · 99 % ${formatLatency(l.p99Us)} · slowest ${formatLatency(l.maxUs)}` };
        }),
    };
  }

  return (
    <div className="ns-chart" ref={ref} onMouseLeave={() => setHover(null)}>
      {width > 0 && (
        <svg width={width} height={chartHeight} role="img" aria-label="Time per operation">
          <Axis scale={scale} width={width} plotH={plotH} />
          {hover && <rect className="ns-hover-band" x={x(hover.index) - colW / 2} y={chartPad.top} width={colW} height={plotH} />}
          {series.map((s, si) => (
            <g key={s.storage.ioId}>
              {s.tests.map((test, i) => {
                const l = test?.latency;
                if (!l) return null;
                const cx = x(i) + (si - (series.length - 1) / 2) * slot;
                const top = y(l.p99Us);
                const bottom = y(l.p50Us);
                return (
                  <g key={i}>
                    <line className="bench-max" x1={cx - 4} x2={cx + 4} y1={y(l.maxUs)} y2={y(l.maxUs)} stroke={s.color} />
                    <rect className="bench-range" x={cx - 3} y={top} width={6} height={Math.max(6, bottom - top + 3)} rx={3} fill={s.color} />
                    <circle className="bench-dot" cx={cx} cy={bottom} r={4} fill={s.color} />
                  </g>
                );
              })}
            </g>
          ))}
          <CategoryLabels categories={categories} x={x} />
          <HitColumns categories={categories} x={x} colW={colW} plotH={plotH} host={ref} onHover={setHover} />
        </svg>
      )}
      <ChartTip tip={hover ? tipFor(hover.index, hover) : null} width={width} />
    </div>
  );
}

function Details({ run }: { run: BenchmarkRun }) {
  const [index, setIndex] = useState(0);
  const storage = run.storages[Math.min(index, run.storages.length - 1)];
  if (!storage) return null;
  const report = storage.report;
  const verified = report.tests.reduce((n, t) => n + t.verified, 0);
  const mismatches = report.tests.reduce((n, t) => n + t.mismatches, 0);
  const groups = groupOrder.filter((g) => report.tests.some((t) => t.group === g));
  return (
    <section className="ns-panel bench-details">
      <div className="ns-panel-head">
        <span className="ns-panel-title">Every test</span>
        {run.storages.length > 1 && (
          <div className="module-switch compact">
            {run.storages.map((s, i) => (
              <button key={s.ioId} className={s === storage ? "active" : ""} onClick={() => setIndex(i)}>
                <span className="ns-swatch" style={{ background: seriesColor(i) }} />
                {s.name}
              </button>
            ))}
          </div>
        )}
        <span className="header-spacer" />
        <CopyButton title={`Copy the results of ${storage.name}`} table={() => runTable({ ...run, storages: [storage] })} />
      </div>
      <div className="facts-grid bench-facts">
        <Fact label="Storage" value={storageTitle(storage)} />
        <Fact label="Location" value={report.location ?? "—"} />
        <Fact label="Test file" value={report.fileBytes > 0 ? formatBytes(report.fileBytes) : "—"} title={`Written in ${formatSpan(report.preparationMs)}`} />
        <Fact label="Ran for" value={formatSpan(report.elapsedMs)} title={`${formatSpan(report.testDurationMs)} asked for, plus writing the test file and cleaning up`} />
        <Fact label="Reads checked" value={verified > 0 ? `${formatCount(verified)}${mismatches > 0 ? `, ${formatCount(mismatches)} wrong` : ", all right"}` : "—"} />
        <Fact label="Timer" value={`${formatResolution(report.timerResolutionUs)} steps`} title="The resolution of the clock the operations were timed with" />
      </div>
      {report.error && <div className="dialog-error">{report.error}</div>}
      <div className="bench-scroll">
        <div className="bench-table">
          <span className="bench-th">Test</span>
          <span className="bench-th num" title="How many times the operation ran">Runs</span>
          <span className="bench-th num" title="The data the runs moved together">Data</span>
          <span className="bench-th num" title="The time spent in the operations themselves">Time</span>
          <span className="bench-th num">Throughput</span>
          <span className="bench-th num">Per second</span>
          <span className="bench-th num" title="The fastest single run">Fastest</span>
          <span className="bench-th num" title="The median: half the runs were faster">Typical</span>
          <span className="bench-th num" title="Nine runs in ten were this fast or faster">90 %</span>
          <span className="bench-th num" title="Ninety-nine runs in a hundred were this fast or faster">99 %</span>
          <span className="bench-th num" title="The slowest single run">Slowest</span>
          <span
            className="bench-th num"
            title="What the storage provider itself read and wrote during the test. Buffering makes it differ from what was asked for, and it includes anything else using the storage at the same time."
          >
            At the storage
          </span>
          {groups.map((g) => (
            <Fragment key={g}>
              <span className="bench-table-group" title={groupHelp[g]}>
                {groupTitles[g]}
              </span>
              {report.tests
                .filter((t) => t.group === g)
                .map((t) => (
                  <TestRow key={t.id} test={t} />
                ))}
            </Fragment>
          ))}
        </div>
      </div>
      {report.notes.length > 0 && (
        <ul className="bench-notes">
          {report.notes.map((note, i) => (
            <li key={i}>{note}</li>
          ))}
        </ul>
      )}
    </section>
  );
}

function TestRow({ test }: { test: BenchmarkTest }) {
  const name = (
    <span className="bench-test-name" title={test.description}>
      {test.name}
    </span>
  );
  if (test.error) {
    return (
      <>
        {name}
        <span className="bench-error" title={test.error}>
          {test.error}
        </span>
      </>
    );
  }
  const l = test.latency;
  const moved = test.storage;
  return (
    <>
      {name}
      <span className="num">{formatCount(test.operations)}</span>
      <span className="num">{test.bytes > 0 ? formatBytes(test.bytes) : "—"}</span>
      <span className="num">{formatSpan(test.elapsedMs)}</span>
      <span className="num">{test.bytes > 0 ? formatThroughput(test.bytesPerSecond) : "—"}</span>
      <span className="num">{formatPerSecond(test.operationsPerSecond)}</span>
      <span className="num">{l ? formatLatency(l.minUs) : "—"}</span>
      <span className="num bench-typical">{l ? formatLatency(l.p50Us) : "—"}</span>
      <span className="num">{l ? formatLatency(l.p90Us) : "—"}</span>
      <span className="num">{l ? formatLatency(l.p99Us) : "—"}</span>
      <span className="num">{l ? formatLatency(l.maxUs) : "—"}</span>
      <span
        className="num"
        title={
          moved
            ? `${formatCount(moved.reads)} reads (${formatBytes(moved.bytesRead)}) and ${formatCount(moved.writes)} writes (${formatBytes(moved.bytesWritten)}) at the storage provider`
            : "Not counted by this storage"
        }
      >
        {moved ? formatBytes(moved.bytesRead + moved.bytesWritten) : "—"}
      </span>
    </>
  );
}

function Fact({ label, value, title }: { label: string; value: string; title?: string }) {
  return (
    <div className="fact" title={title ?? value}>
      <div className="fact-k">{label}</div>
      <div className="fact-v">{value}</div>
    </div>
  );
}

function Help() {
  return (
    <details className="bench-help">
      <summary>How to read the numbers</summary>
      <p>
        Every test runs one kind of operation over and over for its share of the time. <b>Throughput</b> is the data moved per second while the
        operations ran, and <b>per second</b> how many of them that was. Every single run is timed: <b>typical</b> is the median - half the runs
        were faster - <b>90 %</b> and <b>99 %</b> are the times only one run in ten and one in a hundred took longer than, and <b>slowest</b> is the
        single slowest run. The slow tail is where a storage shows its hiccups: a flush landing, a buffer filling up, a request retried.
      </p>
      {groupOrder.map((g) => (
        <p key={g}>
          <b>{groupTitles[g]}.</b> {groupHelp[g]}
        </p>
      ))}
      <p>
        <b>At the storage</b> is what the storage provider itself read and wrote while a test ran. A read stream that fetches a megabyte to answer a
        64 byte read shows here, as does anything else that used the storage at the same time. Every read the tests make is checked against what was
        written, and the report says if one came back different.
      </p>
    </details>
  );
}

function testColumns(test: BenchmarkTest): string[] {
  const l = test.latency;
  const s = test.storage;
  const n = (v: number | undefined) => (v == null ? "" : String(Math.round(v * 1000) / 1000));
  return [
    groupTitles[test.group] ?? test.group,
    test.name,
    String(test.blockSize),
    String(test.operations),
    String(test.bytes),
    n(test.elapsedMs),
    n(test.bytesPerSecond),
    n(test.operationsPerSecond),
    n(l?.minUs),
    n(l?.meanUs),
    n(l?.p50Us),
    n(l?.p90Us),
    n(l?.p99Us),
    n(l?.maxUs),
    s ? String(s.reads) : "",
    s ? String(s.bytesRead) : "",
    s ? String(s.writes) : "",
    s ? String(s.bytesWritten) : "",
    String(test.verified),
    String(test.mismatches),
    test.error ?? "",
  ];
}

function runTable(run: BenchmarkRun): TableData {
  return {
    header: [
      "Storage",
      "Kind",
      "Group",
      "Test",
      "Block bytes",
      "Runs",
      "Bytes",
      "Time ms",
      "Bytes per second",
      "Runs per second",
      "Fastest µs",
      "Mean µs",
      "Typical µs",
      "90 % µs",
      "99 % µs",
      "Slowest µs",
      "Storage reads",
      "Storage bytes read",
      "Storage writes",
      "Storage bytes written",
      "Reads checked",
      "Reads wrong",
      "Error",
    ],
    rows: run.storages.flatMap((s) => s.report.tests.map((t) => [s.name, s.report.kind || typeLabel(s.type), ...testColumns(t)])),
  };
}

function downloadJson(run: BenchmarkRun, dbName: string) {
  const blob = new Blob([JSON.stringify(run, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  const stamp = run.startedUtc.slice(0, 19).replace(/[:T]/g, "-");
  link.href = url;
  link.download = `storage-benchmark-${dbName.replace(/[^\w-]+/g, "_")}-${stamp}.json`;
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
