// How often the admin UI is told what changed. One setting for the whole app: every page that
// follows something live (the dashboard counters, the conversion queue, the task queues, the system
// trace, a log that is being watched) asks the server to sample it on this cadence and push what
// comes back (see live.ts), so slowing the UI down slows all of it down - which is the point on a
// database that is busy, or on a connection that is not local.
//
// Pages that follow something genuinely expensive raise a floor of their own through `minMs`; the
// setting never makes such a sample run more often than it should, only less often.

import { useSyncExternalStore } from "react";

/**
 * The stops on the slider, slowest first. 0 is paused: pages load once and then stay as they are,
 * and the refresh buttons still work. The bottom of the scale is a tenth of a second, which is for
 * watching something that moves too quickly to read otherwise - a queue draining, a rebuild
 * running - and the steps crowd together down there because that is where the difference between
 * one rate and the next is worth having. Nothing stacks up at any of them: the server starts the
 * next wait when the last sample finished, so a database that cannot answer that fast simply sends
 * less often than the slider says.
 */
export const refreshSteps = [0, 30000, 10000, 5000, 2000, 1000, 500, 200, 100] as const;

const defaultInterval = 2000;
const storageKey = "refreshIntervalMs";

function read(): number {
  try {
    // deliberately not Number(): nothing stored parses to 0, which is a step of its own (paused) and
    // would silently become the default setting for anyone opening the UI for the first time
    const saved = localStorage.getItem(storageKey);
    if (saved !== null && refreshSteps.includes(Number(saved) as (typeof refreshSteps)[number])) return Number(saved);
  } catch {
    // storage unavailable, fall through to the default
  }
  return defaultInterval;
}

let interval = read();
const listeners = new Set<() => void>();

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** The current interval in milliseconds, 0 while paused. */
export function getRefreshInterval(): number {
  return interval;
}

export function setRefreshInterval(ms: number): void {
  if (ms === interval) return;
  interval = ms;
  try {
    localStorage.setItem(storageKey, String(ms));
  } catch {
    // storage unavailable, the setting just won't outlive the tab
  }
  for (const listener of listeners) listener();
}

/** The interval, as a component that has to re-render when it changes. */
export function useRefreshInterval(): number {
  return useSyncExternalStore(subscribe, getRefreshInterval, getRefreshInterval);
}

/** "2s", "30s", or "Off" - short enough to sit next to the slider. */
export function describeInterval(ms: number): string {
  if (ms === 0) return "Off";
  return ms >= 60000 ? ms / 60000 + "m" : ms / 1000 + "s";
}

/** The interval a page should quote when it says how often a number is measured. */
export function useMeasuredEvery(minMs = 0): string {
  const global = useRefreshInterval();
  return describeInterval(global === 0 ? 0 : Math.max(global, minMs));
}
