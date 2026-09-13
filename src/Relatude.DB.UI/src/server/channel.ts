// The single communication channel to the Relatude.DB server. Two routes, one abstraction:
//
//   send(type, payload)        client -> server: POST {base}/command, result on the response
//   subscribe(event, handler)  server -> client: one shared SSE connection to {base}/stream
//
// Anything a page follows over time rides the same stream: see live.ts, which asks the server to
// sample a command on a cadence and push what comes back. That is why the connection has an id -
// the server needs to know which stream a subscription belongs to - and why a new connection has to
// be announced: the id changes, so every feed has to be asked for again.
//
// The stream connects lazily on the first subscription (or an explicit connect()). The browser's
// EventSource survives server restarts on its own (network errors and 502/503 are retried), the
// code below covers the rest: a backoff reconnect for responses EventSource gives up on (e.g. 401),
// and a resync notification after every reconnect since events sent while down are lost.

import { isLoggedIn } from "./auth";
import { adminBase } from "./base";

const base = adminBase + "/ui"; // must match ApiUrlRoot + "/ui" on the server

export type ConnectionState = "connecting" | "open" | "closed";

export class CommandError extends Error {
  readonly status: number;
  constructor(message: string, status: number) {
    super(message);
    this.status = status;
  }
}

export async function send<T = unknown>(type: string, payload?: unknown, signal?: AbortSignal): Promise<T> {
  const response = await fetch(`${base}/command`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ type, payload: payload ?? null }),
    signal, // a long running listing can be abandoned when its progress dialog is cancelled
  });
  if (!response.ok) {
    if (response.status === 401) notifyUnauthorized(); // session expired or logged out elsewhere
    let message = `Command "${type}" failed (HTTP ${response.status}).`;
    try {
      const body = await response.json();
      if (typeof body?.error === "string") message = body.error;
    } catch {
      // response was not JSON, keep the default message
    }
    throw new CommandError(message, response.status);
  }
  return (await response.json()) as T;
}

// fired when a command is rejected with 401, so the app can drop back to the login screen
const unauthorizedHandlers = new Set<() => void>();

export function subscribeUnauthorized(handler: () => void): () => void {
  unauthorizedHandlers.add(handler);
  return () => {
    unauthorizedHandlers.delete(handler);
  };
}

function notifyUnauthorized(): void {
  for (const handler of unauthorizedHandlers) handler();
}

type Handler = (payload: unknown) => void;

let source: EventSource | null = null;
const handlers = new Map<string, Set<Handler>>();
const attachedEvents = new Set<string>();
const stateHandlers = new Set<(state: ConnectionState) => void>();

export function getConnectionState(): ConnectionState {
  if (!source) return "closed";
  return source.readyState === EventSource.OPEN ? "open" : source.readyState === EventSource.CONNECTING ? "connecting" : "closed";
}

// the id the server knows this connection by, from the "connected" event it opens with. Null while
// the stream is down, and different after every reconnect
let connectionId: string | null = null;
const connectedHandlers = new Set<(id: string) => void>();

/** The current connection's id, or null while the stream is not up. */
export function getConnectionId(): string | null {
  return connectionId;
}

/**
 * Fired every time the stream opens with an id, the first time included. Anything the server keeps
 * per connection - a live feed - has to be asked for again here: the old connection took its
 * subscriptions with it.
 */
export function subscribeConnected(handler: (id: string) => void): () => void {
  connectedHandlers.add(handler);
  return () => {
    connectedHandlers.delete(handler);
  };
}

let everOpened = false;
let reconnectTimer: number | null = null;
let reconnectDelayMs = 2000;
const resyncHandlers = new Set<() => void>();

// liveness watchdog: the server sends a "ping" event every 10s, so a connection that has been
// silent much longer is dead even if the browser still reports it open (a proxy can keep the
// socket alive after the server died) — rebuild it
const silenceTimeoutMs = 25000;
let lastActivity = 0;
let watchdog: number | null = null;

function markActivity(): void {
  lastActivity = Date.now();
}

function checkAlive(): void {
  if (!source || Date.now() - lastActivity < silenceTimeoutMs) return;
  rebuild();
}

function rebuild(): void {
  if (!source) return;
  source.close();
  source = null;
  connectionId = null;
  attachedEvents.clear();
  notifyState();
  connect();
}

// fired after the stream comes back following a drop; events sent while down are lost,
// so subscribers should refetch the state they mirror
export function subscribeResync(handler: () => void): () => void {
  resyncHandlers.add(handler);
  return () => {
    resyncHandlers.delete(handler);
  };
}

/**
 * The same signal, raised on purpose: after something that changed the database under every
 * page at once - a revert window rolled back, a database reopened - so the pages refetch what
 * they show instead of keeping a picture the database no longer matches.
 */
export function notifyResync(): void {
  for (const handler of resyncHandlers) handler();
}

export function connect(): void {
  if (source) return;
  source = new EventSource(`${base}/stream`);
  markActivity();
  if (watchdog === null) watchdog = window.setInterval(checkAlive, 5000);
  source.onopen = () => {
    const reconnected = everOpened;
    everOpened = true;
    reconnectDelayMs = 2000;
    markActivity();
    notifyState();
    if (reconnected) for (const handler of resyncHandlers) handler();
  };
  source.onerror = () => {
    notifyState();
    // EventSource retries transparently while readyState is CONNECTING; CLOSED means it
    // gave up for good (e.g. a 401 once the session expired) and we must rebuild it ourselves
    if (source && source.readyState === EventSource.CLOSED) scheduleReconnect();
  };
  source.addEventListener("ping", markActivity);
  source.addEventListener("connected", (e) => {
    markActivity();
    try {
      const id = (JSON.parse((e as MessageEvent).data)?.connectionId ?? null) as string | null;
      connectionId = id;
      if (id) for (const handler of connectedHandlers) handler(id);
    } catch {
      // a connected event we cannot read leaves the feeds unsubscribed rather than wrongly keyed
    }
  });
  // the server says so when the session it accepted has since expired, so a page that is only
  // listening still ends up back at the login screen instead of going quiet
  source.addEventListener("unauthorized", () => {
    markActivity();
    notifyUnauthorized();
  });
  for (const event of handlers.keys()) attach(event);
  notifyState();
}

function scheduleReconnect(): void {
  if (reconnectTimer !== null) return;
  reconnectTimer = window.setTimeout(async () => {
    reconnectTimer = null;
    if (!source) return; // disconnect() was called in the meantime
    try {
      if (!(await isLoggedIn())) {
        notifyUnauthorized(); // the session is gone: back to the login screen instead of retrying
        return;
      }
    } catch {
      scheduleReconnect(); // server still down, keep trying with backoff
      return;
    }
    rebuild();
  }, reconnectDelayMs);
  reconnectDelayMs = Math.min(reconnectDelayMs * 2, 15000);
}

// closes the stream (used at logout); the next connect() opens a fresh connection
export function disconnect(): void {
  if (reconnectTimer !== null) {
    clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }
  if (watchdog !== null) {
    clearInterval(watchdog);
    watchdog = null;
  }
  everOpened = false;
  reconnectDelayMs = 2000;
  connectionId = null;
  if (!source) return;
  source.close();
  source = null;
  attachedEvents.clear();
  notifyState();
}

export function subscribe<T = unknown>(event: string, handler: (payload: T) => void): () => void {
  let set = handlers.get(event);
  if (!set) handlers.set(event, (set = new Set()));
  set.add(handler as Handler);
  connect();
  attach(event);
  return () => {
    set.delete(handler as Handler);
  };
}

export function subscribeConnectionState(handler: (state: ConnectionState) => void): () => void {
  stateHandlers.add(handler);
  return () => {
    stateHandlers.delete(handler);
  };
}

function notifyState(): void {
  for (const handler of stateHandlers) handler(getConnectionState());
}

function attach(event: string): void {
  if (!source || attachedEvents.has(event)) return;
  attachedEvents.add(event);
  source.addEventListener(event, (e) => {
    markActivity();
    const payload = e.data ? JSON.parse(e.data) : null;
    for (const handler of handlers.get(event) ?? []) handler(payload);
  });
}
