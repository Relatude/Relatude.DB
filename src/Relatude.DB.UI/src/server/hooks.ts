import { useEffect, useRef, useSyncExternalStore } from "react";
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
