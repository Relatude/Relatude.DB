import { send } from "./channel";
import type { ProgressController } from "../dialogs";

export type BenchmarkGroup = "write" | "durable" | "sequential" | "scattered" | "uncached" | "files";

export interface BenchmarkLatency {
  samples: number;
  minUs: number;
  meanUs: number;
  p50Us: number;
  p90Us: number;
  p99Us: number;
  maxUs: number;
}

export interface BenchmarkCounters {
  reads: number;
  writes: number;
  bytesRead: number;
  bytesWritten: number;
}

export interface BenchmarkTest {
  id: string;
  group: BenchmarkGroup;
  name: string;
  description: string;
  blockSize: number;
  operations: number;
  bytes: number;
  elapsedMs: number;
  bytesPerSecond: number;
  operationsPerSecond: number;
  latency: BenchmarkLatency | null;
  storage: BenchmarkCounters | null;
  verified: number;
  mismatches: number;
  error: string | null;
}

export interface BenchmarkReport {
  kind: string;
  location: string | null;
  startedUtc: string;
  elapsedMs: number;
  preparationMs: number;
  testDurationMs: number;
  fileBytes: number;
  timerResolutionUs: number;
  cancelled: boolean;
  error: string | null;
  tests: BenchmarkTest[];
  notes: string[];
}

export interface BenchmarkStorage {
  ioId: string;
  name: string;
  type: string;
  report: BenchmarkReport;
}

export interface BenchmarkRun {
  storeId: string;
  startedUtc: string;
  finishedUtc: string;
  durationMs: number;
  cancelled: boolean;
  machine: { name: string; os: string; architecture: string; processors: number; runtime: string };
  storages: BenchmarkStorage[];
}

interface BenchmarkProgress {
  state: "running" | "done" | "cancelled" | "failed";
  description: string;
  percent: number;
  error: string | null;
  result: BenchmarkRun | null;
}

const pollMs = 400;

export function fetchLatestBenchmark(storeId: string): Promise<BenchmarkRun | null> {
  return send<BenchmarkRun | null>("io-benchmark-latest", { storeId });
}

export async function runStorageBenchmark(
  ctl: ProgressController,
  storeId: string,
  ioIds: string[],
  durationMs: number,
  onFinished: (run: BenchmarkRun) => void,
): Promise<BenchmarkRun> {
  ctl.set({ label: "Starting…", done: 0, total: 100, meta: null });
  const { jobId } = await send<{ jobId: string }>("io-benchmark-start", { storeId, ioIds, durationMs, taskId: ctl.taskId });
  const started = Date.now();
  let cancelling = false;
  let misses = 0;
  let percent = 0;
  for (;;) {
    if (ctl.signal.aborted && !cancelling) {
      cancelling = true;
      ctl.set({ label: "Cancelling - finishing the test in progress…", meta: `${percent}%` });
      void send("io-benchmark-cancel", { jobId }).catch(() => {});
    }
    let p: BenchmarkProgress;
    try {
      p = await send<BenchmarkProgress>("io-benchmark-progress", { jobId });
      misses = 0;
    } catch (error) {
      if (++misses > 5) throw error;
      await pause(1000 * misses, cancelling ? null : ctl.signal);
      continue;
    }
    if (p.state === "done" && p.result) {
      onFinished(p.result);
      return p.result;
    }
    if (p.state === "failed") throw new Error(p.error ?? "The benchmark failed.");
    if (p.state === "cancelled") throw new DOMException("Aborted", "AbortError");
    percent = p.percent;
    if (!cancelling) ctl.set({ label: p.description || "Measuring…", done: p.percent, total: 100, meta: timeLeft(started, p.percent) });
    await pause(pollMs, cancelling ? null : ctl.signal);
  }
}

function timeLeft(started: number, percent: number): string {
  if (percent < 3) return `${percent}%`;
  const left = ((Date.now() - started) / percent) * (100 - percent);
  const seconds = Math.max(1, Math.round(left / 1000));
  return `${percent}% · about ${seconds < 90 ? `${seconds} s` : `${Math.round(seconds / 60)} min`} left`;
}

function pause(ms: number, signal: AbortSignal | null): Promise<void> {
  return new Promise((resolve) => {
    if (signal?.aborted) return resolve();
    const done = () => {
      clearTimeout(timer);
      signal?.removeEventListener("abort", done);
      resolve();
    };
    const timer = setTimeout(done, ms);
    signal?.addEventListener("abort", done);
  });
}
