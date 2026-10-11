import { createContext, useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { connect, getConnectionState, subscribe, subscribeConnectionState, type ConnectionState } from "./channel";

// Runs the handler for every server event of the given name, for as long as the component is mounted.
export function useServerEvent<T = unknown>(event: string, handler: (payload: T) => void): void {
  const latest = useRef(handler);
  latest.current = handler;
  useEffect(() => subscribe<T>(event, (payload) => latest.current(payload)), [event]);
}

// The state of the SSE connection, connecting it if it is not already.
export function useConnectionState(): ConnectionState {
  useEffect(() => connect(), []);
  return useSyncExternalStore(subscribeConnectionState, getConnectionState);
}

export interface LiveResult<T> {
  result: T | null;
  loading: boolean;
  error: string | null;
  /** True once stop() ended a run, until the next request runs: whatever result is shown is the one before it. */
  stopped: boolean;
  /** Runs the current request again, for when the data behind it has been changed from this page. */
  refresh(): void;
  /**
   * Stops the request in flight and drops any waiting behind it. The runner is handed an AbortSignal for
   * this; passed on to send(), aborting it stops the command on the server as well, not only the wait for
   * its answer. The result shown stays as it was.
   */
  stop(): void;
}

/**
 * The live results one Stop stops together - the Query page's search and the summary view beside it.
 * A result joins with useLiveResult's group argument; the page asks isBusy() (through useRunGroup) to
 * know when there is anything to stop.
 */
export class RunGroup {
  private readonly members = new Set<() => void>();
  private readonly listeners = new Set<() => void>();
  private running = 0;
  private wasStopped = false;
  private snapshot: RunGroupState = { busy: false, stopped: false };
  /** Adds a result's stop to the group; returns how to leave it. */
  join(stop: () => void): () => void {
    this.members.add(stop);
    return () => {
      this.members.delete(stop);
    };
  }
  runStarted(): void {
    this.running++;
    this.wasStopped = false;
    this.emit();
  }
  runEnded(): void {
    this.running = Math.max(0, this.running - 1);
    this.emit();
  }
  stopAll(): void {
    this.wasStopped = this.running > 0;
    for (const stop of [...this.members]) stop();
    this.emit();
  }
  readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };
  readonly getState = (): RunGroupState => this.snapshot;
  private emit(): void {
    const busy = this.running > 0;
    const stopped = this.wasStopped && !busy;
    if (busy === this.snapshot.busy && stopped === this.snapshot.stopped) return;
    this.snapshot = { busy, stopped }; // a new object only on a change: useSyncExternalStore compares by reference
    for (const listener of [...this.listeners]) listener();
  }
}
export interface RunGroupState {
  /** a run of the group is in flight */
  busy: boolean;
  /** the group was stopped, and nothing has run since */
  stopped: boolean;
}
/** The run group of the page around a component: a summary view joins the one its Query tab made. */
export const RunGroupContext = createContext<RunGroup | null>(null);
const idleGroupState: RunGroupState = { busy: false, stopped: false };
const noSubscription = () => () => {};
export function useRunGroup(group: RunGroup | null): RunGroupState {
  return useSyncExternalStore(group ? group.subscribe : noSubscription, group ? group.getState : () => idleGroupState);
}

// how long a run that a newer request has taken over may go on before it is abandoned (see useLiveResult)
const abandonTakenOverAfterMs = 750;

/**
 * Keeps a result in step with a request, with no delay of its own: the wait between clicking a facet
 * and seeing the answer is the query and nothing else.
 *
 * What a debounce is usually there for is handled by only ever having one request in flight. A
 * request that arrives while one is running becomes the next one to run and replaces whatever was
 * already waiting, so a burst of changes - typing - costs the round trips that fit in the time they
 * take rather than one per keystroke, and the answer shown is always the answer to the newest
 * request. Pass null while there is nothing to ask for.
 *
 * A run that has been taken over is let finish while it is quick - its answer shows on the way, as
 * typing goes on - but one still running 750 ms in is abandoned: aborted through the signal its runner
 * was handed, which stops the query on the server too, so a slow query does not hold up the newer one.
 * A component that goes away abandons its run the same way.
 *
 * `request` has to be stable per value (useMemo it): a new object every render is a new request.
 * `run` does not - it is read fresh on every call. A runner that ignores the signal still works: its
 * runs are simply always waited for, as before.
 */
export function useLiveResult<TRequest, TResult>(
  request: TRequest | null,
  run: (request: TRequest, signal: AbortSignal) => Promise<TResult>,
  group: RunGroup | null = null,
): LiveResult<TResult> {
  const [result, setResult] = useState<TResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [stopped, setStopped] = useState(false);
  const next = useRef<TRequest | null>(null); // what to run when the current one lands
  const running = useRef(false);
  const inFlight = useRef<{ controller: AbortController; started: number } | null>(null);
  const abandonTimer = useRef<number | null>(null);
  const alive = useRef(true);
  const current = useRef(request);
  current.current = request;
  const runner = useRef(run);
  runner.current = run;
  const groupRef = useRef(group);
  groupRef.current = group;
  // Set on the way in as well as cleared on the way out: a component whose effects are torn down and
  // re-run on the same instance - React's fast refresh in development, StrictMode's double mount -
  // would otherwise be left permanently dead, dropping every answer it asked for and never turning
  // its loading state off. It is one line to make the hook survive that. A run in flight when the
  // component goes is abandoned: nobody is left to show its answer.
  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
      inFlight.current?.controller.abort();
    };
  }, []);

  const pump = useCallback(async () => {
    if (running.current) return; // the call in flight picks up whatever is newest when it returns
    running.current = true;
    try {
      while (next.current !== null) {
        const request = next.current;
        next.current = null;
        const flight = { controller: new AbortController(), started: performance.now() };
        inFlight.current = flight;
        setLoading(true);
        setStopped(false);
        const runGroup = groupRef.current;
        runGroup?.runStarted();
        try {
          const value = await runner.current(request, flight.controller.signal);
          if (!alive.current) return;
          setResult(value); // even if it was aborted meanwhile: an answer that arrived is a whole one
          setError(null);
          setStopped(false);
        } catch (e) {
          if (!alive.current) return;
          if (flight.controller.signal.aborted) continue; // stopped, or taken over: not an error, and the answer shown stays
          setResult(null);
          setError(e instanceof Error ? e.message : String(e));
        } finally {
          inFlight.current = null;
          if (abandonTimer.current !== null) {
            window.clearTimeout(abandonTimer.current);
            abandonTimer.current = null;
          }
          runGroup?.runEnded();
        }
      }
    } finally {
      running.current = false;
      if (alive.current) setLoading(false);
    }
  }, []);

  // a newer request has arrived while a run is in flight: see the comment on the hook
  const abandonIfSlow = useCallback(() => {
    const flight = inFlight.current;
    if (flight === null || abandonTimer.current !== null) return;
    const wait = Math.max(0, flight.started + abandonTakenOverAfterMs - performance.now());
    abandonTimer.current = window.setTimeout(() => {
      abandonTimer.current = null;
      if (inFlight.current === flight && next.current !== null) flight.controller.abort();
    }, wait);
  }, []);

  useEffect(() => {
    if (request === null) return;
    next.current = request;
    abandonIfSlow();
    void pump();
  }, [request, pump, abandonIfSlow]);

  const refresh = useCallback(() => {
    if (current.current === null) return;
    next.current = current.current;
    abandonIfSlow();
    void pump();
  }, [pump, abandonIfSlow]);

  const stop = useCallback(() => {
    next.current = null;
    const flight = inFlight.current;
    if (flight === null) return;
    flight.controller.abort();
    setStopped(true);
  }, []);

  useEffect(() => (group ? group.join(stop) : undefined), [group, stop]);

  return { result, loading, error, stopped, refresh, stop };
}
