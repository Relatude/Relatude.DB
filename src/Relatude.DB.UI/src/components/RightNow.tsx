import { useEffect, useRef, useState, type ReactNode } from "react";
import {
  IconActivity,
  IconArrowBarToDown,
  IconBolt,
  IconCamera,
  IconCheck,
  IconCopy,
  IconEye,
  IconFlame,
  IconGitCommit,
  IconPhoto,
  IconPlayerPlay,
  IconPlayerStop,
  IconSearch,
  IconStack2,
  IconSubtask,
  IconWriting,
} from "@tabler/icons-react";
import { FadeText } from "./FadeText";
import type { DashboardLive } from "../server/dashboard";
import { formatCount, formatRate } from "../format";

/**
 * "Right now" on the dashboard: what the database is doing this second. Three parts, top to bottom -
 * how fast it is being asked for things (a strip of four rates, each with the last minute behind it
 * as a sparkline), what it is busy with (one row per running activity, an icon in a ring that is the
 * activity's progress), and the two queues at the foot.
 *
 * The rates are the same counters the activity graph draws, sampled by the page; the strip is there
 * so the panel says something even when nothing is running, which is most of the time.
 */

type Activity = NonNullable<DashboardLive["activities"]>[number];

/** The counters the strip is drawn from: cumulative, so a rate is the difference between two. */
export interface RateSample {
  at: number;
  queries: number;
  transactions: number;
  actions: number;
  nodeReads: number;
}

type RateId = "queries" | "transactions" | "actions" | "nodeReads";

const rates: { id: RateId; label: string; icon: typeof IconSearch; help: string }[] = [
  { id: "queries", label: "Queries", icon: IconSearch, help: "Queries per second" },
  { id: "transactions", label: "Transactions", icon: IconGitCommit, help: "Transactions committed per second" },
  { id: "actions", label: "Actions", icon: IconBolt, help: "Actions per second, the single changes a transaction is made of" },
  { id: "nodeReads", label: "Node reads", icon: IconEye, help: "Nodes read per second" },
];

/** a minute at the default refresh rate: long enough to see a burst, short enough to still be "now" */
const sparkSlots = 30;

/**
 * What each kind of work looks like: an icon and a word. The colour is the stylesheet's (`.cat-*`),
 * since it has to follow the theme.
 */
const categories: Record<string, { label: string; icon: typeof IconSearch }> = {
  Opening: { label: "Opening", icon: IconPlayerPlay },
  Closing: { label: "Closing", icon: IconPlayerStop },
  Querying: { label: "Query", icon: IconSearch },
  Executing: { label: "Executing", icon: IconBolt },
  Flushing: { label: "Flushing", icon: IconArrowBarToDown },
  Copying: { label: "Copying", icon: IconCopy },
  Rewriting: { label: "Rewriting", icon: IconWriting },
  SavingState: { label: "Saving state", icon: IconCamera },
  RunningTask: { label: "Task", icon: IconSubtask },
  IndexWarmup: { label: "Index warm-up", icon: IconFlame },
};

/**
 * How long a finished row stays: first it completes - the ring closes, the icon turns into a tick -
 * and then it fades and folds away. The stylesheet's delays on `.now-act.leaving` add up to this.
 */
const leaveMs = 1050;

export function RightNowPanel({ live, samples, rewrite }: { live: DashboardLive | null; samples: RateSample[]; rewrite?: string | null }) {
  const activities = live?.activities ?? [];
  const running = activities.length;
  const tasks = live?.tasksQueued ?? 0;
  const converting = live?.conversions?.running ?? 0;
  const waiting = live?.conversions?.queued ?? 0;

  return (
    <section className="panel panel-fill now-panel">
      <h3>
        Right now
        <span className={"now-status " + (running > 0 ? "busy" : "idle")}>
          {/* two marks in one place, crossfading: a dot that sits still while nothing runs, and
              three bars that keep time while something does */}
          <span className="now-status-mark" aria-hidden="true">
            <span className="now-status-dot" />
            <span className="now-status-eq">
              <i />
              <i />
              <i />
            </span>
          </span>
          <FadeText fadeKey={running > 0 ? "busy" : "idle"}>{running > 0 ? `${running} running` : "idle"}</FadeText>
        </span>
      </h3>

      <div className="now-pulse">
        {rates.map((r) => (
          <RateCell key={r.id} id={r.id} label={r.label} icon={r.icon} help={r.help} samples={samples} />
        ))}
      </div>

      <div className="dash-now fill-body">
        <ActivityList activities={activities} empty={<IdleLine />} />
      </div>

      <div className="now-foot">
        <QueueFact icon={IconStack2} label="Background tasks" active={tasks > 0} value={tasks === 0 ? "none queued" : `${formatCount(tasks)} queued`} />
        <QueueFact
          icon={IconPhoto}
          label="File conversions"
          active={converting > 0}
          value={converting + waiting === 0 ? "none" : `${formatCount(converting)} running · ${formatCount(waiting)} queued`}
        />
      </div>
      {rewrite && (
        <div className="now-rewrite">
          <IconWriting size={14} stroke={1.9} />
          <span className="now-rewrite-text">Rewriting {rewrite}…</span>
          <span className="progress-bar indeterminate">
            <span className="progress-fill" />
          </span>
        </div>
      )}
    </section>
  );
}

/**
 * The work going on, one row each, keyed by what the work is rather than where it is in the list:
 * a row is the same element from the moment it appears to the moment it is gone, so its ring and
 * its bar move rather than jump. A row that has just finished completes and folds away instead of
 * vanishing between two samples. Used by "Right now" and by the panel watching a database open.
 */
export function ActivityList({ activities, empty }: { activities: Activity[]; empty?: ReactNode }) {
  const rows = useLeaving(activities, activityKey, leaveMs);
  // when each row was first seen, and when it was seen to finish: the clock beside it counts from
  // the one and stops at the other. The server does not say when work began, so the page's first
  // sight of it is the best there is - a row a page opens onto starts at nought.
  const seen = useRef(new Map<string, { since: number; until?: number }>());
  const now = Date.now();
  const keys = new Set(rows.map((r) => r.key));
  for (const key of [...seen.current.keys()]) if (!keys.has(key)) seen.current.delete(key);
  for (const r of rows) {
    const s = seen.current.get(r.key);
    if (!s) seen.current.set(r.key, { since: now, until: r.leaving ? now : undefined });
    else if (r.leaving && s.until === undefined) s.until = now;
    else if (!r.leaving) s.until = undefined; // came back before it had gone
  }
  useTick(1000, rows.some((r) => !r.leaving));

  return (
    <>
      {rows.map(({ item: a, key, leaving }) => {
        const cat = categories[a.category] ?? { label: a.category, icon: IconActivity };
        const pct = a.percentageProgress == null ? null : Math.min(100, Math.max(0, a.percentageProgress));
        const s = seen.current.get(key);
        const elapsed = s ? (s.until ?? now) - s.since : 0;
        return (
          <div key={key} className={"now-act cat-" + a.category.toLowerCase() + (leaving ? " leaving" : "")} aria-hidden={leaving || undefined}>
            <div className="now-act-clip">
              <div className="now-act-body">
                <ActivityBadge icon={cat.icon} progress={pct} done={leaving} />
                <div className="now-act-head">
                  <span className="now-act-cat" title={a.category}>
                    {cat.label}
                  </span>
                  <span className="now-act-time">{formatElapsed(elapsed)}</span>
                </div>
                <span className="now-act-pct">{pct != null ? `${Math.round(leaving ? 100 : pct)}%` : ""}</span>
                <div className="now-act-text" title={a.description ?? undefined}>
                  {a.description ?? "—"}
                </div>
                {pct != null && (
                  <span className="now-act-bar" role="progressbar" aria-valuenow={Math.round(pct)} aria-valuemin={0} aria-valuemax={100}>
                    <span className="now-act-fill" style={{ width: `${leaving ? 100 : pct}%` }} />
                  </span>
                )}
              </div>
            </div>
          </div>
        );
      })}
      {empty !== undefined && (
        // in the flow and folded rather than taken out, so it unfolds as the last row folds away
        // instead of the panel jumping between the two
        <div className={"now-empty" + (rows.length === 0 ? "" : " gone")} aria-hidden={rows.length > 0 || undefined}>
          <div className="now-act-clip">{empty}</div>
        </div>
      )}
    </>
  );
}

/**
 * The icon of a kind of work in a ring, drawn the way the power orb draws an open: the lit share of
 * the ring is how far the work has come, and work that does not say turns a short arc instead. Once
 * it is done the ring closes and the icon becomes a tick.
 */
function ActivityBadge({ icon: Icon, progress, done }: { icon: typeof IconSearch; progress: number | null; done: boolean }) {
  const size = 32;
  const stroke = 2;
  const centre = size / 2;
  const radius = centre - stroke / 2 - 0.5;
  const circumference = 2 * Math.PI * radius;
  const lit = done ? 1 : progress != null ? progress / 100 : 0.28;
  // an arc that turns keeps turning as it closes: a whole ring turning looks the same as one at
  // rest, so there is no moment where it stops and jumps back to the top
  return (
    <span className={"now-badge" + (progress == null ? " turning" : "")}>
      <svg className="now-badge-ring" width={size} height={size} viewBox={`0 0 ${size} ${size}`} aria-hidden="true">
        <circle className="now-badge-track" cx={centre} cy={centre} r={radius} strokeWidth={stroke} />
        <circle
          className="now-badge-arc"
          cx={centre}
          cy={centre}
          r={radius}
          strokeWidth={stroke}
          strokeDasharray={`${circumference} ${circumference * 2}`}
          strokeDashoffset={circumference * (1 - lit)}
          transform={`rotate(-90 ${centre} ${centre})`}
        />
      </svg>
      <span className="now-badge-core">
        <Icon className="now-badge-icon" size={15} stroke={1.9} />
        <IconCheck className="now-badge-done" size={16} stroke={2.4} />
      </span>
    </span>
  );
}

/** What the middle of the panel says when nothing is running: a quiet trace with a dot running along it. */
function IdleLine() {
  // a flat line with one beat in it, drawn at a fixed size: stretched, the dot would be an oval
  const trace = "M1 14 H46 L51 14 L55 5 L60 23 L64 10 L67 14 H119";
  return (
    <div className="now-idle">
      <svg className="now-idle-trace" width={120} height={28} viewBox="0 0 120 28" aria-hidden="true">
        <path className="now-idle-line" d={trace} />
        <circle className="now-idle-dot" r={2.5}>
          <animateMotion dur="3.6s" repeatCount="indefinite" path={trace} />
        </circle>
      </svg>
      <div className="now-idle-text">
        <div className="now-idle-title">Nothing running</div>
        <div className="muted">queries, flushes and background work show up here while they last</div>
      </div>
    </div>
  );
}

/**
 * One rate: what it is, how many a second right now, and the last minute of it as a line. The
 * number counts its way to each new sample rather than jumping, and the icon takes the line's colour
 * while there is anything to count.
 */
function RateCell({ id, label, icon: Icon, help, samples }: { id: RateId; label: string; icon: typeof IconSearch; help: string; samples: RateSample[] }) {
  const values = rateSeries(samples, id).slice(-sparkSlots);
  const current = values.length > 0 ? (values[values.length - 1] ?? 0) : 0;
  const shown = useTweened(current, 650);
  return (
    <div className={"now-rate rate-" + id.toLowerCase() + (current > 0 ? " live" : "")} title={help}>
      <div className="now-rate-head">
        <Icon size={13} stroke={1.9} />
        <span>{label}</span>
      </div>
      <div className="now-rate-value">
        {shown < 0.05 ? "0" : formatRate(shown)}
        <span className="now-rate-unit">/s</span>
      </div>
      <Sparkline values={values} />
    </div>
  );
}

/**
 * The rate as a line across a fixed window, growing in from the right like the activity graph, on
 * a scale of its own so a quiet minute still shows its shape. A counter that went backwards was
 * reset, and that sample is a gap rather than a negative rate.
 */
function Sparkline({ values }: { values: (number | null)[] }) {
  const w = 100;
  const h = 26;
  const max = values.reduce<number>((m, v) => (v != null && v > m ? v : m), 0);
  const top = max > 0 ? max * 1.15 : 1;
  const x = (i: number) => ((sparkSlots - values.length + i) / (sparkSlots - 1)) * w;
  const y = (v: number) => h - 1 - (v / top) * (h - 4);
  let line = "";
  let area = "";
  let run: [number, number][] = [];
  const flush = () => {
    if (run.length === 0) return;
    line += run.map(([px, py], i) => `${i === 0 ? "M" : "L"}${px.toFixed(2)} ${py.toFixed(2)}`).join(" ") + " ";
    if (run.length > 1) area += `M${run[0][0].toFixed(2)} ${h} ` + run.map(([px, py]) => `L${px.toFixed(2)} ${py.toFixed(2)}`).join(" ") + ` L${run[run.length - 1][0].toFixed(2)} ${h} Z `;
    run = [];
  };
  values.forEach((v, i) => {
    if (v == null) flush();
    else run.push([x(i), y(v)]);
  });
  flush();
  const last = values.length > 0 ? values[values.length - 1] : null;
  return (
    <div className="now-spark">
      <svg viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none" aria-hidden="true">
        <line className="now-spark-base" x1={0} x2={w} y1={h - 0.5} y2={h - 0.5} vectorEffect="non-scaling-stroke" />
        {area && <path className="now-spark-area" d={area} />}
        {line && <path className="now-spark-line" d={line} vectorEffect="non-scaling-stroke" />}
      </svg>
      {/* the newest sample, as a dot that is round whatever the width: drawn over the svg, not in it */}
      {last != null && <span className="now-spark-dot" style={{ top: `${(y(last) / h) * 100}%` }} />}
    </div>
  );
}

/** A queue at the foot of the panel: its icon lights up while there is anything in it. */
function QueueFact({ icon: Icon, label, value, active }: { icon: typeof IconSearch; label: string; value: string; active: boolean }) {
  return (
    <div className={"now-queue" + (active ? " active" : "")}>
      <span className="now-queue-icon">
        <Icon size={15} stroke={1.8} />
      </span>
      <div className="now-queue-text">
        <div className="fact-k">{label}</div>
        <div className="fact-v" title={value}>
          <FadeText fadeKey={active ? "active" : value}>{value}</FadeText>
        </div>
      </div>
    </div>
  );
}

/** The counter as a rate per second, one value per pair of samples; a reset or a stalled clock is a gap. */
function rateSeries(samples: RateSample[], id: RateId): (number | null)[] {
  const out: (number | null)[] = [];
  for (let i = 1; i < samples.length; i++) {
    const seconds = (samples[i].at - samples[i - 1].at) / 1000;
    const delta = samples[i][id] - samples[i - 1][id];
    out.push(seconds > 0 && delta >= 0 ? delta / seconds : null);
  }
  return out;
}

/** "4s", "1m 05s", "2h 03m": how long a row has been running, short enough to sit beside its name. */
function formatElapsed(ms: number): string {
  const s = Math.floor(ms / 1000);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${String(s % 60).padStart(2, "0")}s`;
  return `${Math.floor(m / 60)}h ${String(m % 60).padStart(2, "0")}m`;
}

/**
 * A number that eases its way to each new value instead of jumping there. With reduced motion asked
 * for, or nothing to animate, it is simply the value.
 */
function useTweened(target: number, ms: number): number {
  const [shown, setShown] = useState(target);
  const current = useRef(target);
  useEffect(() => {
    const from = current.current;
    if (from === target || window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
      current.current = target;
      setShown(target);
      return;
    }
    const start = performance.now();
    let frame = requestAnimationFrame(function step(now) {
      const t = Math.min(1, (now - start) / ms);
      const eased = 1 - Math.pow(1 - t, 3);
      current.current = from + (target - from) * eased;
      setShown(current.current);
      if (t < 1) frame = requestAnimationFrame(step);
    });
    return () => cancelAnimationFrame(frame);
  }, [target, ms]);
  return shown;
}

// What an activity is, for keeping its row between samples: the category and its description with
// the numbers taken out, since a description that counts up ("rewriting 4,201 of 9,000") is still
// the same piece of work. A second one just like it gets a suffix.
function activityKey(a: Activity, _index: number, taken: Set<string>): string {
  const base = a.category + ":" + (a.description ?? "").replace(/[\d.,%]+/g, "#");
  let key = base;
  for (let n = 2; taken.has(key); n++) key = base + "#" + n;
  return key;
}

/** Renders again every `ms` while `on`: for a clock that would otherwise only move when a sample lands. */
export function useTick(ms: number, on: boolean): void {
  const [, setTick] = useState(0);
  useEffect(() => {
    if (!on) return;
    const timer = setInterval(() => setTick((t) => t + 1), ms);
    return () => clearInterval(timer);
  }, [ms, on]);
}

/**
 * The items as they should be on screen: the current ones, plus any that were there a moment ago
 * and are now leaving. A removed item stays for `ms` with `leaving` set, which is what its exit
 * transition runs on; an item that comes back within that time is simply current again.
 *
 * Two things decide whether that transition runs at all, and both are about the element staying the
 * same element. An item that has gone is noticed in the very render that no longer has it - noticed
 * in an effect, after that render, it would be unmounted for a frame and come back as a new element
 * already leaving, with nothing to transition from. And every row keeps the place it was shown in,
 * new ones going in after the one they follow: a row React has to move is taken out of the document
 * and put back, which restarts its animations and skips its transitions just the same.
 */
function useLeaving<T>(items: T[], keyOf: (item: T, index: number, taken: Set<string>) => string, ms: number): { item: T; key: string; leaving: boolean }[] {
  // what is on screen, in the order it is shown; `until` is set on the rows that are leaving
  const shown = useRef<{ item: T; key: string; until: number | null }[]>([]);
  const [, setSwept] = useState(0);
  const taken = new Set<string>();
  const current = items.map((item, i) => {
    const key = keyOf(item, i, taken);
    taken.add(key);
    return { item, key };
  });
  const byKey = new Map(current.map((c) => [c.key, c]));
  const now = Date.now();
  const next: { item: T; key: string; until: number | null }[] = [];
  for (const s of shown.current) {
    const c = byKey.get(s.key);
    if (c) next.push({ item: c.item, key: c.key, until: null });
    else if (s.until === null) next.push({ ...s, until: now + ms }); // gone since the last render
    else if (s.until > now) next.push(s); // still on its way out
  }
  current.forEach((c, i) => {
    if (next.some((n) => n.key === c.key)) return;
    const after = i > 0 ? next.findIndex((n) => n.key === current[i - 1].key) : -1;
    next.splice(after + 1, 0, { item: c.item, key: c.key, until: null });
  });
  shown.current = next;
  // a render of its own when the first of the leaving rows is due to go
  const due = next.reduce((m, n) => (n.until !== null && n.until < m ? n.until : m), Infinity);
  useEffect(() => {
    if (!Number.isFinite(due)) return;
    const timer = setTimeout(() => setSwept((t) => t + 1), Math.max(0, due - Date.now()) + 20);
    return () => clearTimeout(timer);
  }, [due]);
  return next.map((n) => ({ item: n.item, key: n.key, leaving: n.until !== null }));
}
