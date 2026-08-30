import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
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
  /** Runs the current request again, for when the data behind it has been changed from this page. */
  refresh(): void;
}

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
 * `request` has to be stable per value (useMemo it): a new object every render is a new request.
 * `run` does not - it is read fresh on every call.
 */
export function useLiveResult<TRequest, TResult>(request: TRequest | null, run: (request: TRequest) => Promise<TResult>): LiveResult<TResult> {
  const [result, setResult] = useState<TResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const next = useRef<TRequest | null>(null); // what to run when the current one lands
  const running = useRef(false);
  const alive = useRef(true);
  const current = useRef(request);
  current.current = request;
  const runner = useRef(run);
  runner.current = run;
  useEffect(
    () => () => {
      alive.current = false;
    },
    [],
  );

  const pump = useCallback(async () => {
    if (running.current) return; // the call in flight picks up whatever is newest when it returns
    running.current = true;
    try {
      while (next.current !== null) {
        const request = next.current;
        next.current = null;
        setLoading(true);
        try {
          const value = await runner.current(request);
          if (!alive.current) return;
          setResult(value);
          setError(null);
        } catch (e) {
          if (!alive.current) return;
          setResult(null);
          setError(e instanceof Error ? e.message : String(e));
        }
      }
    } finally {
      running.current = false;
      if (alive.current) setLoading(false);
    }
  }, []);

  useEffect(() => {
    if (request === null) return;
    next.current = request;
    void pump();
  }, [request, pump]);

  const refresh = useCallback(() => {
    if (current.current === null) return;
    next.current = current.current;
    void pump();
  }, [pump]);

  return { result, loading, error, refresh };
}
