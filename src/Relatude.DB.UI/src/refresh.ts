// How often the admin UI asks the server what changed. One setting for the whole app: every page
// that follows something live (the dashboard counters, the conversion queue, the task queues, the
// system trace, a log that is being watched) polls on this cadence rather than on a pace of its own,
// so slowing the UI down slows all of it down - which is the point on a database that is busy, or on
// a connection that is not local.
//
// Pages that ask for something genuinely expensive raise a floor of their own through `minMs`; the
// setting never makes such a call run more often than it should, only less often.

import { useEffect, useRef, useSyncExternalStore } from "react";

/**
 * The stops on the slider, slowest first. 0 is paused: nothing polls, the refresh buttons still work.
 * The fastest stop is well under a second, for watching something that moves quickly - a queue
 * draining, a rebuild running. Nothing stacks up at that rate: a poll is scheduled once the previous
 * one has answered, so a server that cannot keep up simply answers less often.
 */
export const refreshSteps = [0, 30000, 10000, 5000, 2000, 1000, 200] as const;

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

export interface PollOptions {
  /** False parks the timer without unmounting anything (a page nobody is watching, a paused view). */
  enabled?: boolean;
  /**
   * The fastest this particular call may be made, whatever the global setting says. For calls that
   * cost the server real work, or that answer a question that cannot change faster than this anyway.
   */
  minMs?: number;
}

/**
 * Calls `fn` on the global refresh cadence, and not while it is already running: the next wait starts
 * when the last call finished, so a slow answer delays the next request instead of stacking up behind
 * it. Does not call `fn` on mount - the first load belongs to the page, which usually wants it
 * unconditionally and often wants it in the same effect that sets up its state.
 */
export function usePoll(fn: () => unknown, options: PollOptions = {}): void {
  const { enabled = true, minMs = 0 } = options;
  const global = useRefreshInterval();
  const every = global === 0 ? 0 : Math.max(global, minMs);
  // the callback is almost always a fresh closure on every render; keeping it in a ref means the
  // timer is not torn down and restarted by every state change the page makes
  const latest = useRef(fn);
  latest.current = fn;
  useEffect(() => {
    if (!enabled || every === 0) return;
    let stopped = false;
    let timer = 0;
    const run = async () => {
      try {
        await latest.current();
      } catch {
        // a failed refresh is the page's business, not the timer's: keep the cadence
      }
      if (!stopped) timer = window.setTimeout(run, every);
    };
    timer = window.setTimeout(run, every);
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
  }, [enabled, every]);
}

/** The interval a page should quote when it says how often a number is measured. */
export function useMeasuredEvery(minMs = 0): string {
  const global = useRefreshInterval();
  return describeInterval(global === 0 ? 0 : Math.max(global, minMs));
}
