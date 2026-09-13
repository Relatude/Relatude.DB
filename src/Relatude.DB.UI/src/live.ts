// What a page follows over time. Every page that shows something live - the dashboard counters, a
// queue draining, the system trace, a log being watched - names the command that answers its
// question, and the server samples that command on the cadence set in the top bar and pushes what
// comes back over the SSE stream (see UILiveFeeds.cs). The browser makes one request to start a
// feed and one to stop it, and nothing in between.
//
// Three things the old timer could not do follow from the server holding the cadence:
//   - a sample that says exactly what the last one said is never sent, so a quiet database costs
//     nothing on the wire however fast the refresh rate is set;
//   - a command slower than the interval delays the next sample instead of being asked again while
//     it is still answering;
//   - a page that is open but paused (the slider at Off) still gets its first picture, and then
//     stays exactly as it is - which is what pausing is for.
//
// A feed is one command with one payload: change what the page is asking for and the old feed is
// dropped and a new one started, which is what the effect below does by keying on the payload.

import { useEffect, useMemo, useRef } from "react";
import { getConnectionId, send, subscribe, subscribeConnected } from "./server/channel";
import { useRefreshInterval } from "./refresh";

interface Feed {
  id: string;
  /** the subscription this one takes over from, so the server can drop it whatever the order */
  replaces: string | null;
  type: string;
  payload: unknown;
  everyMs: number;
  apply: (data: unknown) => void;
  fail: (message: string) => void;
}

/** The feeds this tab has running, by the id the server pushes them under. */
const feeds = new Map<string, Feed>();
let listening = false;
let counter = 0;

function listen(): void {
  if (listening) return;
  listening = true;
  subscribe<{ feed: string; data?: unknown; error?: string }>("feed", (event) => {
    const feed = feeds.get(event.feed);
    if (!feed) return; // a sample that arrived after the page let go of it
    if (typeof event.error === "string") feed.fail(event.error);
    else feed.apply(event.data);
  });
  // the connection the feeds were started on is gone; the server dropped them with it, so they are
  // asked for again on the new one - which also refreshes what every page is showing
  subscribeConnected(() => {
    for (const feed of feeds.values()) void start(feed);
  });
}

async function start(feed: Feed): Promise<void> {
  const connectionId = getConnectionId();
  if (connectionId === null) return; // the stream is not up yet: subscribeConnected will start it
  try {
    // the subscription answers with the first sample, so starting a feed is also the page's first
    // load rather than a request of its own.
    //
    // `replaces` names the subscription this one takes over from - the same page asking the same
    // question at another rate. It is what keeps a rate change from racing itself: the two requests
    // are independent, so the "stop the old one" can arrive after the "start the new one", and
    // naming the old one here means the server drops exactly that one whichever order they land in.
    const first = await send("stream-subscribe", {
      connectionId,
      feed: feed.id,
      replaces: feed.replaces,
      type: feed.type,
      payload: feed.payload ?? null,
      everyMs: feed.everyMs,
    });
    if (feeds.get(feed.id) === feed) feed.apply(first);
    // the page let go of this feed while the subscription was in flight: the server has it now, so
    // it is stopped here rather than left running until the tab closes
    else stop(feed.id);
  } catch (e) {
    if (feeds.get(feed.id) === feed) feed.fail(e instanceof Error ? e.message : String(e));
  }
}

function stop(id: string): void {
  const connectionId = getConnectionId();
  if (connectionId === null) return; // nothing to stop: the connection it belonged to is gone
  void send("stream-unsubscribe", { connectionId, feed: id }).catch(() => {
    // a subscription the server no longer has is exactly what we wanted
  });
}

/** Keys the same payload the same way whichever order the page happened to build it in. */
function stableKey(payload: unknown): string {
  return JSON.stringify(payload ?? null, (_, value) =>
    value && typeof value === "object" && !Array.isArray(value)
      ? Object.fromEntries(Object.entries(value as Record<string, unknown>).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)))
      : value,
  );
}

export interface LiveOptions {
  /** False leaves the page as it is: nothing is subscribed and nothing is fetched. */
  enabled?: boolean;
  /**
   * True asks once and then stops - the same thing pausing does, for a page that has a live switch
   * of its own (a log that is being watched, or not).
   */
  once?: boolean;
  /**
   * Something that is not part of the question but should ask it again anyway: a refresh button, or
   * an action whose result the page has to see at once. Changing it restarts the feed, which answers
   * with a fresh sample.
   */
  restartOn?: unknown;
  /**
   * The fastest this particular command may be sampled, whatever the global setting says. For
   * commands that cost the server real work, or that answer a question which cannot change faster
   * than this anyway.
   */
  minMs?: number;
  /** What to do with a sample that failed. Without it a failure leaves the page on its last picture. */
  onError?: (message: string) => void;
}

/**
 * Follows one command: `apply` is called with its result straight away, and again every time the
 * server has something new to say. Pass the payload the command takes; changing it starts a new
 * feed, so a page can follow a range, a filter or a page number simply by passing it here.
 *
 * `apply` and `onError` are read fresh on every call, so they do not have to be stable - but the
 * payload does have to be the same shape from render to render, or every render restarts the feed.
 */
export function useLive<T = unknown>(type: string, payload: unknown, apply: (data: T) => void, options: LiveOptions = {}): void {
  const { enabled = true, once = false, minMs = 0, onError, restartOn } = options;
  const global = useRefreshInterval();
  const everyMs = once || global === 0 ? 0 : Math.max(global, minMs);
  const restart = stableKey(restartOn ?? null);
  const key = stableKey(payload);
  const sent = useMemo(() => (key === undefined ? null : (JSON.parse(key) as unknown)), [key]);
  // kept in a ref so a page re-rendering does not restart its feeds: only the question matters here,
  // never the closure that answers it
  const latest = useRef({ apply, onError });
  latest.current = { apply, onError };
  // one name per hook instance, and a number after it per subscription: every start and stop names
  // a subscription of its own, so no two of them can be about the same one
  const name = useRef<string>("");
  if (name.current === "") name.current = "f" + ++counter;
  const generation = useRef(0);
  const started = useRef<string | null>(null);
  useEffect(() => {
    if (!enabled) return;
    const feedId = name.current + "." + ++generation.current;
    const handle = (data: unknown) => latest.current.apply(data as T);
    const fail = (message: string) => latest.current.onError?.(message);
    // paused: the page still gets what it is showing now, and then nothing until the rate is turned
    // back up or something is refreshed by hand
    if (everyMs === 0) {
      let cancelled = false;
      send(type, sent ?? null)
        .then((data) => !cancelled && handle(data))
        .catch((e) => !cancelled && fail(e instanceof Error ? e.message : String(e)));
      return () => {
        cancelled = true;
      };
    }
    const feed: Feed = { id: feedId, replaces: started.current, type, payload: sent, everyMs, apply: handle, fail };
    started.current = feedId;
    feeds.set(feedId, feed);
    listen();
    void start(feed);
    return () => {
      feeds.delete(feedId);
      stop(feedId);
    };
  }, [type, sent, everyMs, enabled, restart]);
}
