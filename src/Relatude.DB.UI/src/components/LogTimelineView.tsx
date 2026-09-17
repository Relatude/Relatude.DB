import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { formatBytes, formatCount, formatDateTime } from "../format";
import type { LogTimeline } from "../server/storage";

/**
 * A log file's actions over time, drawn as plain SVG - the admin UI carries no chart library, and
 * what a scan produces is narrow enough to draw directly: a count per slice of time.
 *
 * The count is actions rather than transactions, because that is the size of what was written: one
 * transaction can hold a single edit or an import of a hundred thousand nodes, and a picture drawn
 * on transactions makes those the same column. Transactions are still what the file is cut at, so
 * they are named in the tooltip beside the actions - but they are not what the shape is about.
 *
 * It is here to answer one question - which moment to go back to - so it is not only a picture:
 *   - clicking picks the last transaction in the column clicked, which is a moment the log actually
 *     holds rather than a time typed at it. Clicking where nothing was written picks that time as it
 *     is;
 *   - the wheel zooms about the pointer and dragging pans, so a month of history and the three
 *     seconds somebody is looking for are the same picture at two magnifications. Neither goes
 *     outside what was scanned: there is nothing to see there;
 *   - what would be left out is shaded, and the line where the two sides meet is the moment picked.
 *
 * A slice is a stretch of time, not an instant, so a column is drawn across the width of the time it
 * covers and named by the stretch in the tooltip. Zoomed in past the width the scan was gathered at,
 * the columns stand apart with gaps between them: the picture cannot be finer than the scan, and
 * drawing it as if it were would invent write bursts that never happened. The dialog offers to scan
 * the stretch again at that point, which is what closes the gaps.
 */

const pad = { top: 18, right: 16, bottom: 26, left: 62 };
const columnPx = 3; // the finest a column is drawn at; wider only when the scan cannot fill it
const dragSlop = 4; // px below which a drag is a click, not a pan
const tipHalf = 135; // half the tooltip's widest, which is what keeps it inside the plot (see .log-timeline-tip)
const minSpanMs = 1; // a view narrower than a millisecond is not a view: the log is not read that finely
const zoomPerPixel = 0.0015; // a wheel notch of 100 is about a sixth in or out, which reads as smooth
const linesPx = 16; // a wheel that reports lines rather than pixels, as an old mouse on Firefox does
const pagesPx = 400; // ... or pages, which nothing common does, but the event allows it

const clamp = (value: number, low: number, high: number) => Math.min(Math.max(value, low), high);

export interface TimelineView {
  fromMs: number;
  toMs: number;
}

interface Column {
  transactions: number;
  actions: number;
  bytes: number;
  firstMs: number;
  lastMs: number;
}

/** The whole of what a scan covered: the view that is drawn until somebody zooms, and the bounds. */
export function wholeView(timeline: LogTimeline): TimelineView {
  const slices = timeline.slices;
  if (slices.length === 0) return { fromMs: 0, toMs: 1 };
  const fromMs = slices[0].fromMs;
  const toMs = Math.max(slices[slices.length - 1].toMs, fromMs + 1);
  return { fromMs, toMs };
}

/** A view held inside what was scanned: zoomed out no further than the whole, and never off it. */
function clampView(view: TimelineView, bounds: TimelineView): TimelineView {
  const widest = Math.max(minSpanMs, bounds.toMs - bounds.fromMs);
  const span = clamp(view.toMs - view.fromMs, minSpanMs, widest);
  const fromMs = clamp(view.fromMs, bounds.fromMs, bounds.toMs - span);
  return { fromMs, toMs: fromMs + span };
}

export function LogTimelineView({
  timeline,
  view,
  onView,
  pickedMs,
  onPick,
  logScale,
}: {
  timeline: LogTimeline;
  view: TimelineView;
  onView: (view: TimelineView) => void;
  /** The moment the dialog is going back to, drawn as the line the shading starts at. */
  pickedMs: number | null;
  onPick: (ms: number) => void;
  /** Write bursts are orders of magnitude apart; a log axis is what makes the quiet stretches visible. */
  logScale: boolean;
}) {
  const wrap = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ width: 0, height: 0 });
  const [hover, setHover] = useState<number | null>(null); // column index
  const [panning, setPanning] = useState(false);

  // drawn in pixels rather than scaled from a viewBox: a stretched viewBox would take the text and
  // the stroke widths with it
  useLayoutEffect(() => {
    const element = wrap.current;
    if (!element) return;
    const measure = () => setSize({ width: element.clientWidth, height: element.clientHeight });
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    measure();
    return () => observer.disconnect();
  }, []);

  const plotW = Math.max(0, size.width - pad.left - pad.right);
  const plotH = Math.max(0, size.height - pad.top - pad.bottom);
  const span = Math.max(1, view.toMs - view.fromMs);
  const bounds = useMemo(() => wholeView(timeline), [timeline]);

  const { columns, columnMs } = useMemo(() => {
    const count = Math.max(1, Math.floor(plotW / columnPx));
    const width = span / count;
    const cols: Column[] = Array.from({ length: count }, () => ({ transactions: 0, actions: 0, bytes: 0, firstMs: 0, lastMs: 0 }));
    for (const slice of timeline.slices) {
      if (slice.lastMs < view.fromMs || slice.firstMs > view.toMs) continue;
      const index = Math.min(count - 1, Math.max(0, Math.floor((slice.firstMs - view.fromMs) / width)));
      const column = cols[index];
      column.transactions += slice.transactions;
      column.actions += slice.actions;
      column.bytes += slice.bytes;
      if (column.firstMs === 0 || slice.firstMs < column.firstMs) column.firstMs = slice.firstMs;
      if (slice.lastMs > column.lastMs) column.lastMs = slice.lastMs;
    }
    return { columns: cols, columnMs: width };
  }, [timeline, view.fromMs, view.toMs, span, plotW]);

  // What the wheel and the pan read to work out where they are moving from. They are handled off the
  // window rather than off the element - a pan that leaves the plot is still a pan - so they cannot
  // close over a render; and a wheel turned twice inside one frame has to see the first turn, which
  // the state it caused has not delivered yet.
  const live = useRef({ view, bounds, plotW, columns, onView, onPick });
  live.current = { view, bounds, plotW, columns, onView, onPick };

  /** Moves the view, held inside what was scanned, and lets the next gesture see it immediately. */
  function moveTo(next: TimelineView) {
    const clamped = clampView(next, live.current.bounds);
    live.current.view = clamped;
    live.current.onView(clamped);
  }

  const peak = columns.reduce((max, c) => Math.max(max, c.actions), 0);
  const height = (value: number) => {
    if (value <= 0 || peak <= 0) return 0;
    const share = logScale ? Math.log(1 + value) / Math.log(1 + peak) : value / peak;
    return Math.max(1, share * plotH); // a slice with anything in it is a mark, never nothing
  };
  const x = (ms: number) => pad.left + ((ms - view.fromMs) / span) * plotW;
  const columnWidth = Math.max(1, plotW / columns.length - 0.5);

  const ticks = useMemo(() => timeTicks(view.fromMs, view.toMs, Math.max(2, Math.floor(plotW / 110))), [view.fromMs, view.toMs, plotW]);
  const axis = useMemo(() => countTicks(peak, logScale, plotH), [peak, logScale, plotH]);

  /** The moment under a pixel, read against the view as it stands rather than as it was drawn. */
  function msAtLive(px: number): number {
    const { view: current, plotW: plot } = live.current;
    const width = Math.max(1, current.toMs - current.fromMs);
    return current.fromMs + ((clamp(px, pad.left, pad.left + plot) - pad.left) / Math.max(1, plot)) * width;
  }

  function columnAt(clientX: number): number | null {
    const box = wrap.current?.getBoundingClientRect();
    if (!box || columns.length === 0) return null;
    const px = clientX - box.left;
    if (px < pad.left || px > pad.left + plotW) return null;
    return Math.min(columns.length - 1, Math.max(0, Math.floor(((px - pad.left) / Math.max(1, plotW)) * columns.length)));
  }

  /**
   * The moment a click means: a column holding transactions picks its last one - a moment the log
   * really has - and an empty stretch picks the time itself, which is what pointing at it means.
   */
  function pickAt(clientX: number) {
    const box = wrap.current?.getBoundingClientRect();
    if (!box) return;
    const { plotW: plot, columns: cols, onPick: pick } = live.current;
    const px = clamp(clientX - box.left, pad.left, pad.left + plot);
    const index = cols.length === 0 ? -1 : Math.min(cols.length - 1, Math.max(0, Math.floor(((px - pad.left) / Math.max(1, plot)) * cols.length)));
    const column = index >= 0 ? cols[index] : null;
    pick(column && column.transactions > 0 ? column.lastMs : Math.round(msAtLive(px)));
  }

  /**
   * The wheel zooms about the pointer: what is under it stays under it, so zooming in on a burst is
   * pointing at it and turning, not aiming a rectangle. A trackpad pushed sideways pans instead,
   * which is the same thing dragging does; a pinch arrives as a wheel with ctrl held and zooms.
   *
   * Bound here rather than through React, which listens passively and would leave the page scrolling
   * underneath the zoom.
   */
  useEffect(() => {
    const element = wrap.current;
    if (!element) return;
    const onWheel = (e: WheelEvent) => {
      const plot = live.current.plotW;
      if (plot <= 0) return;
      e.preventDefault();
      const px = e.clientX - element.getBoundingClientRect().left;
      const current = live.current.view;
      const width = Math.max(1, current.toMs - current.fromMs);
      const unit = e.deltaMode === 1 ? linesPx : e.deltaMode === 2 ? pagesPx : 1;
      if (!e.ctrlKey && Math.abs(e.deltaX) > Math.abs(e.deltaY)) {
        const by = ((e.deltaX * unit) / plot) * width;
        moveTo({ fromMs: current.fromMs + by, toMs: current.toMs + by });
        return;
      }
      const anchor = msAtLive(px);
      const widest = Math.max(minSpanMs, live.current.bounds.toMs - live.current.bounds.fromMs);
      const next = clamp(width * Math.exp(e.deltaY * unit * zoomPerPixel), minSpanMs, widest);
      const fromMs = anchor - ((anchor - current.fromMs) * next) / width;
      moveTo({ fromMs, toMs: fromMs + next });
    };
    element.addEventListener("wheel", onWheel, { passive: false });
    return () => element.removeEventListener("wheel", onWheel);
    // everything the handler reads comes off `live`, so it is bound once
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  /**
   * Dragging carries the plot: the moment grabbed stays under the pointer, and the view is held
   * inside what was scanned. Off the window, so a pan that runs out past the edge of the plot keeps
   * going and a button released anywhere ends it. A press that never moved is a click, and picks.
   */
  const drag = useRef<{ startPx: number; anchorMs: number; moved: number } | null>(null);
  useEffect(() => {
    if (!panning) return;
    const onMove = (e: MouseEvent) => {
      const element = wrap.current;
      const held = drag.current;
      if (!element || !held) return;
      const px = e.clientX - element.getBoundingClientRect().left;
      held.moved = Math.max(held.moved, Math.abs(px - held.startPx));
      if (held.moved < dragSlop) return;
      const { view: current, plotW: plot } = live.current;
      const width = Math.max(1, current.toMs - current.fromMs);
      const fromMs = held.anchorMs - ((clamp(px, pad.left, pad.left + plot) - pad.left) / Math.max(1, plot)) * width;
      moveTo({ fromMs, toMs: fromMs + width });
    };
    const onUp = (e: MouseEvent) => {
      const held = drag.current;
      drag.current = null;
      setPanning(false);
      if (held && held.moved < dragSlop) pickAt(e.clientX);
    };
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
    return () => {
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [panning]);

  const hovered = hover != null ? columns[hover] : null;
  const hoveredFrom = hover != null ? view.fromMs + hover * columnMs : 0;
  const empty = timeline.slices.length === 0;
  const zoomed = view.fromMs > bounds.fromMs || view.toMs < bounds.toMs;
  // above the plot: how much of the scan is on screen while it is less than all of it, and - until
  // the pointer is on the plot, by which time it has been found out - what the plot answers to
  const strip = empty
    ? ""
    : [
        zoomed ? `${formatSpan(span)} of ${formatSpan(bounds.toMs - bounds.fromMs)}` : "",
        hover == null && !panning ? "scroll to zoom · drag to pan · click to pick a moment" : "",
      ]
        .filter(Boolean)
        .join(" · ");

  return (
    <div className={"log-timeline" + (panning ? " panning" : "")} ref={wrap}>
      {size.width > 0 && (
        <svg
          width={size.width}
          height={size.height}
          onMouseDown={(e) => {
            if (e.button !== 0) return;
            const box = wrap.current?.getBoundingClientRect();
            if (!box) return;
            e.preventDefault(); // a drag across a plot is a pan, not a text selection
            const px = e.clientX - box.left;
            drag.current = { startPx: px, anchorMs: msAtLive(px), moved: 0 };
            setPanning(true);
          }}
          onMouseMove={(e) => setHover(columnAt(e.clientX))}
          onMouseLeave={() => setHover(null)}
        >
          {/* the counts axis */}
          {axis.map((tick) => (
            <g key={tick.value}>
              <line className="log-timeline-grid" x1={pad.left} x2={pad.left + plotW} y1={pad.top + plotH - tick.y} y2={pad.top + plotH - tick.y} />
              <text className="log-timeline-label" x={pad.left - 8} y={pad.top + plotH - tick.y + 4} textAnchor="end">
                {tick.label}
              </text>
            </g>
          ))}
          {/* the time axis */}
          {ticks.marks.map((ms) => (
            <g key={ms}>
              <line className="log-timeline-grid" x1={x(ms)} x2={x(ms)} y1={pad.top} y2={pad.top + plotH} />
              <text className="log-timeline-label" x={x(ms)} y={pad.top + plotH + 16} textAnchor="middle">
                {ticks.format(ms)}
              </text>
            </g>
          ))}
          <line className="log-timeline-axis" x1={pad.left} x2={pad.left + plotW} y1={pad.top + plotH} y2={pad.top + plotH} />
          {/* what the columns are counted in, and above them how much is on screen and how to move it */}
          <text className="log-timeline-label" x={pad.left} y={pad.top - 6}>
            actions
          </text>
          {strip && (
            <text className="log-timeline-label" x={pad.left + plotW} y={pad.top - 6} textAnchor="end">
              {strip}
            </text>
          )}
          {/* what the moment would leave out */}
          {pickedMs != null && pickedMs < view.toMs && (
            <rect
              className="log-timeline-dropped"
              x={Math.max(pad.left, x(pickedMs))}
              y={pad.top}
              width={Math.max(0, pad.left + plotW - Math.max(pad.left, x(pickedMs)))}
              height={plotH}
            />
          )}
          {columns.map((column, i) =>
            column.actions > 0 ? (
              <rect
                key={i}
                className={"log-timeline-bar" + (pickedMs != null && column.firstMs > pickedMs ? " dropped" : "")}
                x={pad.left + (i * plotW) / columns.length}
                y={pad.top + plotH - height(column.actions)}
                width={columnWidth}
                height={height(column.actions)}
              />
            ) : null,
          )}
          {pickedMs != null && pickedMs >= view.fromMs && pickedMs <= view.toMs && (
            <line className="log-timeline-pick" x1={x(pickedMs)} x2={x(pickedMs)} y1={pad.top - 4} y2={pad.top + plotH + 4} />
          )}
          {hover != null && !panning && (
            <line
              className="log-timeline-cursor"
              x1={pad.left + ((hover + 0.5) * plotW) / columns.length}
              x2={pad.left + ((hover + 0.5) * plotW) / columns.length}
              y1={pad.top}
              y2={pad.top + plotH}
            />
          )}
        </svg>
      )}
      {empty && <div className="log-timeline-empty muted">This file holds no transactions at all.</div>}
      {hovered && !panning && (
        <div
          className="log-timeline-tip"
          style={{
            // centred on the column, but never so far out that half of it falls off the plot
            left: clamp(pad.left + ((hover! + 0.5) * plotW) / columns.length, tipHalf, Math.max(tipHalf, size.width - tipHalf)),
          }}
        >
          <div className="log-timeline-tip-time">
            {formatDateTime(new Date(hoveredFrom).toISOString())}
            {columnMs >= 1 && <span className="muted"> + {formatSpan(columnMs)}</span>}
          </div>
          {hovered.transactions > 0 ? (
            <div className="muted">
              {formatCount(hovered.actions)} action{hovered.actions === 1 ? "" : "s"} in {formatCount(hovered.transactions)} transaction
              {hovered.transactions === 1 ? "" : "s"}, {formatBytes(hovered.bytes)}
            </div>
          ) : (
            <div className="muted">nothing was written</div>
          )}
        </div>
      )}
    </div>
  );
}

/** A span of milliseconds said the shortest way that is still true. */
export function formatSpan(ms: number): string {
  if (ms < 1000) return Math.max(1, Math.round(ms)) + " ms";
  const seconds = ms / 1000;
  if (seconds < 90) return round(seconds) + " s";
  const minutes = seconds / 60;
  if (minutes < 90) return round(minutes) + " min";
  const hours = minutes / 60;
  if (hours < 36) return round(hours) + " h";
  const days = hours / 24;
  if (days < 400) return round(days) + " days";
  return round(days / 365) + " years";
}

const round = (value: number) => (value >= 10 ? Math.round(value) : Math.round(value * 10) / 10).toString();

/**
 * Times to label the axis with: the finest step from the table below that does not crowd the axis,
 * landing on whole local seconds, minutes, hours or days rather than on multiples of the left edge.
 */
function timeTicks(fromMs: number, toMs: number, maxTicks: number): { marks: number[]; format: (ms: number) => string } {
  const second = 1000;
  const day = 86400 * second;
  // down to a millisecond, because a log written in one burst is a picture of a second or two, and
  // an axis that cannot say anything below a second has nothing to label such a picture with
  const steps = [1, 2, 5, 10, 25, 50, 100, 250, 500, second, 2 * second, 5 * second, 15 * second, 30 * second, 60 * second,
    5 * 60 * second, 15 * 60 * second, 30 * 60 * second, 3600 * second, 3 * 3600 * second, 6 * 3600 * second, 12 * 3600 * second, day];
  const span = Math.max(1, toMs - fromMs);
  let step = steps.find((s) => span / s <= maxTicks) ?? day;
  while (span / step > maxTicks) step *= 2; // past a day the steps just double; the labels are dates by then
  // the local zone's offset, so a tick falls on a local boundary rather than a UTC one
  const offset = new Date(fromMs).getTimezoneOffset() * 60000;
  const first = Math.ceil((fromMs - offset) / step) * step + offset;
  const marks: number[] = [];
  for (let ms = first; ms <= toMs && marks.length < 200; ms += step) marks.push(ms);
  const withDate = step >= day || new Date(fromMs).toDateString() !== new Date(toMs).toDateString();
  const time: Intl.DateTimeFormatOptions = {
    hour: "2-digit",
    minute: "2-digit",
    ...(step < 60 * second ? { second: "2-digit" as const } : {}),
    ...(step < second ? { fractionalSecondDigits: 3 as const } : {}),
  };
  return {
    marks,
    format: (ms) => {
      const date = new Date(ms);
      if (!withDate) return date.toLocaleTimeString(undefined, time);
      const dayLabel = date.toLocaleDateString(undefined, { month: "short", day: "numeric" });
      return step >= day ? dayLabel : dayLabel + " " + date.toLocaleTimeString(undefined, time);
    },
  };
}

/** Counts to label the left edge with, at the heights the bars are actually drawn at. */
function countTicks(peak: number, logScale: boolean, plotH: number): { value: number; y: number; label: string }[] {
  if (peak <= 0 || plotH <= 0) return [];
  const at = (value: number) => (logScale ? (Math.log(1 + value) / Math.log(1 + peak)) * plotH : (value / peak) * plotH);
  const values: number[] = [];
  if (logScale) {
    for (let value = 1; value <= peak; value *= 10) values.push(value);
    if (values[values.length - 1] !== peak) values.push(peak);
  } else {
    const step = niceStep(peak / 4);
    for (let value = step; value <= peak; value += step) values.push(value);
  }
  return values.filter((v, i) => i === 0 || at(v) - at(values[i - 1]) > 14).map((value) => ({ value, y: at(value), label: compact(value) }));
}

function niceStep(raw: number): number {
  const magnitude = Math.pow(10, Math.floor(Math.log10(Math.max(1, raw))));
  for (const factor of [1, 2, 5, 10]) {
    if (factor * magnitude >= raw) return factor * magnitude;
  }
  return 10 * magnitude;
}

function compact(value: number): string {
  if (value >= 1_000_000) return Math.round(value / 100_000) / 10 + "M";
  if (value >= 1000) return Math.round(value / 100) / 10 + "k";
  return String(Math.round(value));
}
