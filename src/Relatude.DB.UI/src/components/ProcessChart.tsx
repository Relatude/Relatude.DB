import { useCallback, useRef, useState } from "react";
import { Chart, type ChartBand, type ChartOverlay } from "./Chart";
import { useRefreshInterval } from "../refresh";
import { useLive } from "../live";
import { formatBytes, formatRate } from "../format";
import type { SeriesPoint } from "../server/logs";
import type { IoActivity, IoCounters } from "../server/dashboard";

// The server process on a graph: the managed heap as a level, the cpu it is using as a share of
// every core, drawn over it in another colour, and - where the server reports it - how full the
// drive the databases are written to is. Under it, in a band of its own, what the storage providers
// moved: bytes read and written a second, summed over every provider the server has. The same
// picture serves the database dashboard (where the process is the one thing on the page that is not
// this database) and the server overview (where it is the whole subject), so the sampling, the
// arithmetic and the chart live here.

/** One reading of the process, taken at `at`. The processor time is cumulative, like a counter. */
export interface ProcessSample {
  at: number;
  iso: string;
  managedMemory: number;
  processMemory: number;
  /** ms of cpu time used so far, all cores added together; 0 when the server did not report it */
  processorTimeMs: number;
  processorCount: number;
  /** the data drive, 0 on a server that has no local folder to report on */
  diskTotalBytes?: number;
  diskFreeBytes?: number;
  /** what the storage providers have done so far, cumulative; unset from a server older than this page */
  io?: IoActivity;
}

/** Three minutes of history at the default refresh rate: enough to see a burst arrive and drain. */
export const defaultMaxSamples = 90;

export const formatPercent = (value: number) => (value >= 10 ? Math.round(value) : value.toFixed(1)) + "%";
export const formatBytesPerSecond = (value: number) => formatBytes(value) + "/s";

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
 * How full the data drive is, as a percentage, so it rides the same 0..100 axis as the cpu. A drive
 * of no size is a server that reported none - a gap, like everything else nothing was measured for.
 */
export function diskPoints(samples: ProcessSample[]): SeriesPoint[] {
  return samples.map((s) => {
    const total = s.diskTotalBytes ?? 0;
    const free = s.diskFreeBytes ?? 0;
    const has = total > 0;
    return { fromUtc: s.iso, hasValue: has, value: has ? Math.min(100, Math.max(0, ((total - free) / total) * 100)) : null };
  });
}

/** The latest reading of the data drive, or null on a server that does not report one. */
export function currentDisk(samples: ProcessSample[]): { total: number; free: number; usedPercent: number } | null {
  for (let i = samples.length - 1; i >= 0; i--) {
    const total = samples[i].diskTotalBytes ?? 0;
    if (total <= 0) continue;
    const free = samples[i].diskFreeBytes ?? 0;
    return { total, free, usedPercent: ((total - free) / total) * 100 };
  }
  return null;
}

/** One direction of the storage traffic: which counters of a reading it is made of. */
export interface IoSide {
  bytes: (counters: IoCounters) => number;
  ops: (counters: IoCounters) => number;
}
export const ioRead: IoSide = { bytes: (c) => c.bytesRead, ops: (c) => c.reads };
export const ioWrite: IoSide = { bytes: (c) => c.bytesWritten, ops: (c) => c.writes };

/**
 * The storage traffic between each pair of samples, as bytes a second summed over every provider,
 * with the operations a second in `count` and each provider's own bytes a second in `values`. A
 * sample without a reading, or a counter that went backwards (a provider dropped and rebuilt), is a
 * gap rather than a rate.
 */
export function ioPoints(samples: ProcessSample[], side: IoSide): SeriesPoint[] {
  const points: SeriesPoint[] = [];
  for (let i = 1; i < samples.length; i++) {
    const previous = samples[i - 1];
    const sample = samples[i];
    const seconds = (sample.at - previous.at) / 1000;
    const before = previous.io;
    const now = sample.io;
    if (!before || !now || seconds <= 0 || side.bytes(now) < side.bytes(before)) {
      points.push({ fromUtc: sample.iso, hasValue: false, value: null });
      continue;
    }
    const values: Record<string, number> = {};
    for (const provider of now.providers) {
      const was = before.providers.find((p) => p.id === provider.id);
      if (!was || side.bytes(provider) < side.bytes(was)) continue;
      values[provider.name] = (side.bytes(provider) - side.bytes(was)) / seconds;
    }
    points.push({
      fromUtc: sample.iso,
      hasValue: true,
      value: (side.bytes(now) - side.bytes(before)) / seconds,
      count: Math.max(0, side.ops(now) - side.ops(before)) / seconds,
      values,
    });
  }
  return points;
}

/** Whether any sample carries a storage reading: a server older than this page sends none. */
export function hasIo(samples: ProcessSample[]): boolean {
  return samples.some((s) => s.io != null);
}

/** The latest storage rates, both directions, or null before there are two readings to compare. */
export function currentIo(samples: ProcessSample[]): { read: SeriesPoint; written: SeriesPoint } | null {
  const read = ioPoints(samples, ioRead);
  const written = ioPoints(samples, ioWrite);
  const r = read[read.length - 1];
  const w = written[written.length - 1];
  return r?.hasValue && w?.hasValue ? { read: r, written: w } : null;
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
 * Memory and cpu on one chart, the storage traffic in a band under them. The memory is the axis on
 * the left; the cpu rides over it on a 0..100% scale labelled on the right. The band has a scale of
 * its own, since bytes a second and bytes in a heap are not the same kind of number. The cpu and the
 * traffic are rates between two samples and so have one point fewer than the memory - padding all of
 * them to the same window keeps them lined up.
 */
export function ProcessChart({ samples, maxSamples = defaultMaxSamples, showDisk = false }: { samples: ProcessSample[]; maxSamples?: number; showDisk?: boolean }) {
  const refreshMs = useRefreshInterval();
  const memory = padToWindow(memoryPoints(samples), maxSamples, samples, refreshMs);
  const cpu = padToWindow(cpuPoints(samples), maxSamples, samples, refreshMs);
  const overlays: ChartOverlay[] = [{ points: cpu, max: 100, format: formatPercent, label: "cpu" }];
  // the drive joins the same percentage axis, and only where the server reported one: an empty
  // line under the legend would read as a disk that is somehow at zero
  if (showDisk && currentDisk(samples)) {
    overlays.push({ points: padToWindow(diskPoints(samples), maxSamples, samples, refreshMs), max: 100, format: formatPercent, label: "disk used" });
  }
  const band: ChartBand | undefined = hasIo(samples)
    ? {
        format: formatBytesPerSecond,
        series: [
          { label: "read", tone: "read", points: padToWindow(ioPoints(samples, ioRead), maxSamples, samples, refreshMs), detail: (p) => formatRate(p.count ?? 0) + " reads/s" },
          { label: "written", tone: "write", points: padToWindow(ioPoints(samples, ioWrite), maxSamples, samples, refreshMs), detail: (p) => formatRate(p.count ?? 0) + " writes/s" },
        ],
      }
    : undefined;
  return <Chart kind="sum" points={memory} groups={[]} interval="Second" format={formatBytes} compactAxis={false} height="fill" overlays={overlays} band={band} />;
}

/**
 * Follows one reading of the process and keeps the last `maxSamples` of them. The server takes the
 * readings on the page's refresh cadence and pushes them (see live.ts); `read` turns one into a
 * sample, and a reading that cannot be turned into one is a gap rather than an error worth taking
 * the page over. The returned array is a new one after every sample, so anything drawn from it
 * redraws.
 */
export function useProcessSamples<T>(type: string, payload: unknown, read: (data: T) => ProcessSample, maxSamples = defaultMaxSamples): ProcessSample[] {
  const samples = useRef<ProcessSample[]>([]);
  const [, setTick] = useState(0);
  const add = useCallback(
    (data: T) => {
      try {
        samples.current = [...samples.current, read(data)].slice(-maxSamples);
        setTick((t) => t + 1);
      } catch {
        // a gap
      }
    },
    [read, maxSamples],
  );
  useLive<T>(type, payload, add);
  return samples.current;
}
