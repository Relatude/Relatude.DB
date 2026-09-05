import { useCallback, useRef, useState } from "react";
import { Chart } from "./Chart";
import { usePoll, useRefreshInterval } from "../refresh";
import { formatBytes } from "../format";
import type { SeriesPoint } from "../server/logs";

// The server process on a graph: the managed heap as a level, the cpu it is using as a share of
// every core, drawn over it in another colour. The same picture serves the database dashboard
// (where the process is the one thing on the page that is not this database) and the server
// overview (where it is the whole subject), so the sampling, the arithmetic and the chart live here.

/** One reading of the process, taken at `at`. The processor time is cumulative, like a counter. */
export interface ProcessSample {
  at: number;
  iso: string;
  managedMemory: number;
  processMemory: number;
  /** ms of cpu time used so far, all cores added together; 0 when the server did not report it */
  processorTimeMs: number;
  processorCount: number;
}

/** Three minutes of history at the default refresh rate: enough to see a burst arrive and drain. */
export const defaultMaxSamples = 90;

export const formatPercent = (value: number) => (value >= 10 ? Math.round(value) : value.toFixed(1)) + "%";

/**
 * The cpu between each pair of samples, as a share of every core the process could have used:
 * processor time is summed over the cores, so a process saturating one core of eight reads 12.5%.
 * A drop in the cumulative time (a process restarted under the page) is a gap, like the counters;
 * so is a server too old to report the time at all.
 */
export function cpuPoints(samples: ProcessSample[]): SeriesPoint[] {
  const points: SeriesPoint[] = [];
  for (let i = 1; i < samples.length; i++) {
    const previous = samples[i - 1];
    const sample = samples[i];
    const wall = sample.at - previous.at;
    const used = sample.processorTimeMs - previous.processorTimeMs;
    const valid = wall > 0 && used >= 0 && sample.processorTimeMs > 0;
    const share = valid ? Math.min(100, (used / wall / Math.max(1, sample.processorCount)) * 100) : null;
    points.push({ fromUtc: sample.iso, hasValue: valid, value: share });
  }
  return points;
}

/** The heap as it was measured, one point a sample; zero is a server with nothing to report, a gap. */
export function memoryPoints(samples: ProcessSample[]): SeriesPoint[] {
  return samples.map((s) => ({ fromUtc: s.iso, hasValue: s.managedMemory > 0, value: s.managedMemory > 0 ? s.managedMemory : null }));
}

/**
 * Fills the graph out to `count` points by putting empty ones before the measured ones, spaced the
 * way the samples are - by the gap between the last two, else by the refresh interval, else a second.
 * The empty points are gaps to the chart (nothing recorded), so nothing is drawn there; they only
 * hold the axis open so time runs at the same speed at every width.
 */
export function padToWindow(points: SeriesPoint[], count: number, samples: { at: number }[], refreshMs: number): SeriesPoint[] {
  const missing = count - points.length;
  if (missing <= 0) return points;
  const n = samples.length;
  const spacing = n >= 2 && samples[n - 1].at > samples[n - 2].at ? samples[n - 1].at - samples[n - 2].at : refreshMs > 0 ? refreshMs : 1000;
  const firstAt = points.length > 0 ? new Date(points[0].fromUtc).getTime() : n > 0 ? samples[n - 1].at : Date.now();
  const padding: SeriesPoint[] = [];
  for (let i = missing; i >= 1; i--) padding.push({ fromUtc: new Date(firstAt - i * spacing).toISOString(), hasValue: false, value: null });
  return padding.concat(points);
}

/** The latest cpu share, or null before there are two samples to compare (or with a server that does not report it). */
export function currentCpu(samples: ProcessSample[]): number | null {
  const points = cpuPoints(samples);
  const last = points[points.length - 1];
  return last?.hasValue ? last.value : null;
}

/**
 * Memory and cpu on one chart. The memory is the axis on the left; the cpu rides over it on a
 * 0..100% scale labelled on the right. The cpu is a rate between two samples and so has one point
 * fewer than the memory - padding both to the same window keeps them lined up.
 */
export function ProcessChart({ samples, maxSamples = defaultMaxSamples }: { samples: ProcessSample[]; maxSamples?: number }) {
  const refreshMs = useRefreshInterval();
  const memory = padToWindow(memoryPoints(samples), maxSamples, samples, refreshMs);
  const cpu = padToWindow(cpuPoints(samples), maxSamples, samples, refreshMs);
  return <Chart kind="sum" points={memory} groups={[]} interval="Second" format={formatBytes} compactAxis={false} height="fill" overlay={{ points: cpu, max: 100, format: formatPercent, label: "cpu" }} />;
}

/**
 * Samples the process at the page's refresh rate and keeps the last `maxSamples`. `read` fetches one
 * reading; a failed read is a gap, not an error worth taking the page over. The returned array is a
 * new one after every sample, so anything drawn from it redraws.
 */
export function useProcessSamples(read: () => Promise<ProcessSample>, maxSamples = defaultMaxSamples): ProcessSample[] {
  const samples = useRef<ProcessSample[]>([]);
  const [, setTick] = useState(0);
  const sample = useCallback(async () => {
    try {
      const s = await read();
      samples.current = [...samples.current, s].slice(-maxSamples);
      setTick((t) => t + 1);
    } catch {
      // a gap
    }
  }, [read, maxSamples]);
  usePoll(sample);
  // the first reading straight away rather than a refresh interval from now
  const started = useRef(false);
  if (!started.current) {
    started.current = true;
    void sample();
  }
  return samples.current;
}
