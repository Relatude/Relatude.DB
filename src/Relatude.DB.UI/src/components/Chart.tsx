import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { CSSProperties } from "react";
import type { IntervalType, SeriesKind, SeriesPoint } from "../server/logs";

// A chart for one log statistic, drawn as plain SVG - the admin UI carries no chart library, and
// what the statistics produce is narrow enough to draw directly: a value per interval, sometimes
// with the min and max it was aggregated from, sometimes split per value of a property.
//
// Two things the data does that a naive chart gets wrong:
//   - an interval nothing was recorded in is a gap, not a zero. Lines break across it and bars are
//     absent, so a quiet hour never reads as a busy one that fell to zero;
//   - the buckets are intervals, not instants. A point is drawn at the middle of its interval and
//     the tooltip names the interval, so the last (still running) bucket is not read as a drop.
//
// A live update redraws the curve as it is: the values are not eased from the previous ones, and the
// plot does not creep along between samples either. At the rates the refresh slider allows, a tween
// is either invisible or still running when the next update arrives, and either way it draws numbers
// the database never reported. (A continuous leftward slide was tried and taken out again: with the
// samples arriving as unevenly as they do, it reads as the chart hunting rather than as time
// passing.)

const palette = [
  "#4c8dd8",
  "#e0873b",
  "#63a86c",
  "#c65f5f",
  "#8d78c9",
  "#4aa7b3",
  "#c9a227",
  "#b4629f",
  "#6fae5e",
  "#7f8fa3",
  "#9a6b4f",
];

export function groupColor(index: number): string {
  return palette[index % palette.length];
}

const pad = { top: 10, right: 14, bottom: 22, left: 52 };
const overlayAxisWidth = 40; // room for the second axis's labels on the right

/**
 * A series drawn over the first in another colour, on a scale of its own that runs 0..max against
 * the right edge. Two scales on one plot is a thing to be careful with - a reader can take the
 * lines to be comparable when they are not - so it is kept to measures that are a bounded share (a
 * percentage), which the right axis labels as such, and it is never given an area fill.
 *
 * More than one overlay shares that right axis, so they all have to be on the same scale: the axis
 * is labelled from the first one's max. Two is the sensible limit - a third line on two scales is a
 * puzzle, not a chart.
 */
export interface ChartOverlay {
  points: SeriesPoint[];
  /** The top of the overlay's scale (100 for a percentage). */
  max: number;
  format: (value: number) => string;
  /** Names the series in the tooltip. */
  label: string;
}

/** The colour an overlay is drawn in, by its place in the list; see the chart-line-overlay rules. */
const overlayTone = (index: number) => (index === 0 ? "overlay" : "overlay-b");

export interface ChartProps {
  kind: SeriesKind;
  points: SeriesPoint[];
  /** kind "groups": the values to stack, in the order they keep their colours in. */
  groups: string[];
  interval: IntervalType;
  /** Formats a value for the axis and the tooltip, from the data type of the property. */
  format: (value: number) => string;
  /** Counts cannot be half of one, so their axis steps whole numbers. */
  integer?: boolean;
  /**
   * Whether the axis may shorten a large number to "12k" / "3.4M" on its own. Off where `format`
   * already carries a scale of its own - bytes above all: 358000000 formats as "342 MB" and
   * compacting it would put "358M" on the axis of a chart that says 342 MB everywhere else.
   */
  compactAxis?: boolean;
  /**
   * A height in pixels, or "fill" to take whatever the box around it gives - which is what a chart
   * in a panel the reader can resize wants. "fill" needs an ancestor with a height of its own; in a
   * box that sizes itself to its content the chart would measure zero and never grow.
   */
  height?: number | "fill";
  /** Series on the right-hand scale, drawn over the first; see ChartOverlay. */
  overlays?: ChartOverlay[];
  /**
   * Bars instead of a line, for a count or a total: each interval is a quantity of its own, and a
   * line drawn between them reads as a rate changing smoothly from one to the next. Stacked groups
   * are bars already, and a band (avg, min, max) keeps its line.
   */
  bars?: boolean;
  /** Called with the index of the interval clicked, which makes the plot clickable. */
  onPick?: (index: number) => void;
  /** What the tooltip calls the value of a count or a total, when it is neither: "max", say. */
  valueLabel?: string;
}

export function Chart({ kind, points, groups, interval, format, integer = false, compactAxis = true, height: heightProp = 210, overlays, bars = false, onPick, valueLabel }: ChartProps) {
  const wrap = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(0);
  const [measuredHeight, setMeasuredHeight] = useState(0);
  const [hover, setHover] = useState<number | null>(null);
  const fill = heightProp === "fill";
  // the SVG is drawn in pixels rather than scaled from a viewBox: a stretched viewBox would take
  // the text and the stroke widths with it
  useLayoutEffect(() => {
    const element = wrap.current;
    if (!element) return;
    const measure = () => {
      setWidth(element.clientWidth);
      setMeasuredHeight(element.clientHeight);
    };
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    measure();
    return () => observer.disconnect();
  }, []);
  // below this the axis labels have nowhere to go and the plot is a line with no room under it
  const height = fill ? Math.max(90, measuredHeight) : heightProp;
  useEffect(() => setHover(null), [points, kind]);

  const over = overlays ?? [];
  const padRight = pad.right + (over.length > 0 ? overlayAxisWidth : 0);
  const plotW = Math.max(0, width - pad.left - padRight);
  const plotH = height - pad.top - pad.bottom;
  const band = points.length > 0 ? plotW / points.length : 0;
  const stacked = kind === "groups";

  // the top of the scale covers whatever is drawn: the band's max where there is one, the stack
  // total where the values are stacked
  let top = 0;
  for (const p of points) {
    if (!p.hasValue) continue;
    if (stacked) {
      let sum = 0;
      for (const g of groups) sum += p.values?.[g] ?? 0;
      top = Math.max(top, sum);
    } else {
      top = Math.max(top, p.max ?? p.value ?? 0);
    }
  }
  let bottom = 0;
  for (const p of points) {
    if (!p.hasValue || stacked) continue;
    bottom = Math.min(bottom, p.min ?? p.value ?? 0);
  }
  const ticks = axisTicks(bottom, top, integer);
  const scaleTop = ticks[ticks.length - 1];
  const scaleBottom = ticks[0];
  const y = (value: number) => {
    if (scaleTop === scaleBottom) return pad.top + plotH;
    return pad.top + plotH - ((value - scaleBottom) / (scaleTop - scaleBottom)) * plotH;
  };
  const xCenter = (index: number) => pad.left + band * (index + 0.5);
  // the overlays' own scale: 0 at the baseline, the max at the top, whatever the main axis says
  const overlayMax = over[0]?.max ?? 1;
  const yOverlay = (value: number, max = overlayMax) => pad.top + plotH - (Math.min(Math.max(value, 0), max) / max) * plotH;
  const overlayTicks = over.length > 0 ? [0, overlayMax / 2, overlayMax] : [];
  const overlayHit = (index: number) => (hover == null ? null : (over[index]?.points[hover] ?? null));
  const anyOverlayHit = over.some((_, i) => overlayHit(i)?.hasValue);

  const hasAny = points.some((p) => p.hasValue);
  const hovered = hover != null && hover >= 0 && hover < points.length ? points[hover] : null;

  return (
    <div className={"chart" + (fill ? " chart-fill" : "")} ref={wrap} style={fill ? undefined : { height }}>
      {width > 0 && (
        <svg width={width} height={height} role="img">
          {ticks.map((t) => (
            <g key={t}>
              <line className="chart-grid" x1={pad.left} x2={width - padRight} y1={y(t)} y2={y(t)} />
              <text className="chart-axis" x={pad.left - 8} y={y(t)} textAnchor="end" dominantBaseline="middle">
                {compactAxis ? compact(t, format) : format(t)}
              </text>
            </g>
          ))}
          {overlayTicks.map((t) => (
            <text key={"o" + t} className="chart-axis chart-axis-overlay" x={width - padRight + 8} y={yOverlay(t)} dominantBaseline="middle">
              {over[0].format(t)}
            </text>
          ))}
          {xLabels(points, interval, band, plotW).map((label) => (
            <text key={label.index} className="chart-axis" x={xCenter(label.index)} y={height - 6} textAnchor="middle">
              {label.text}
            </text>
          ))}
          {stacked
            ? points.map((p, i) => {
                if (!p.hasValue) return null;
                let acc = 0;
                return (
                  <g key={i}>
                    {groups.map((g, gi) => {
                      const value = p.values?.[g] ?? 0;
                      if (value <= 0) return null;
                      const y1 = y(acc + value);
                      const y0 = y(acc);
                      acc += value;
                      return <rect key={g} x={xCenter(i) - Math.max(1, band * 0.4)} width={Math.max(1, band * 0.8)} y={y1} height={Math.max(1, y0 - y1)} fill={groupColor(gi)} />;
                    })}
                  </g>
                );
              })
            : bars && (kind === "count" || kind === "sum")
              ? points.map((p, i) => {
                  if (!p.hasValue || p.value == null) return null;
                  const top = y(p.value);
                  const base = y(Math.max(scaleBottom, Math.min(0, scaleTop)));
                  return (
                    <rect
                      key={i}
                      className="chart-bar"
                      x={xCenter(i) - Math.max(1, band * 0.4)}
                      width={Math.max(1, band * 0.8)}
                      y={Math.min(top, base)}
                      height={Math.max(1, Math.abs(base - top))}
                    />
                  );
                })
              : segments(points).map((segment, si) => (
                <g key={si}>
                  {(kind === "avgminmax" || kind === "full") && <path className="chart-band" d={bandPath(segment, xCenter, y)} />}
                  <path className="chart-area" d={areaPath(segment, xCenter, y, pad.top + plotH)} />
                  <path className="chart-line" d={linePath(segment, xCenter, y)} />
                  {segment.length === 1 && <circle className="chart-point" cx={xCenter(segment[0].index)} cy={y(segment[0].point.value ?? 0)} r={2.5} />}
                </g>
              ))}
          {over.map((o, oi) =>
            segments(o.points).map((segment, si) => (
              <g key={"o" + oi + "-" + si}>
                <path className={"chart-line chart-line-" + overlayTone(oi)} d={linePath(segment, xCenter, (v) => yOverlay(v, o.max))} />
                {segment.length === 1 && (
                  <circle
                    className={"chart-point chart-point-" + overlayTone(oi)}
                    cx={xCenter(segment[0].index)}
                    cy={yOverlay(segment[0].point.value ?? 0, o.max)}
                    r={2.5}
                  />
                )}
              </g>
            )),
          )}
          {hovered && (
            <g>
              <line className="chart-cursor" x1={xCenter(hover!)} x2={xCenter(hover!)} y1={pad.top} y2={pad.top + plotH} />
              {!stacked && hovered.hasValue && <circle className="chart-point" cx={xCenter(hover!)} cy={y(points[hover!]?.value ?? 0)} r={3.5} />}
              {over.map((o, oi) =>
                overlayHit(oi)?.hasValue ? (
                  <circle key={oi} className={"chart-point chart-point-" + overlayTone(oi)} cx={xCenter(hover!)} cy={yOverlay(overlayHit(oi)!.value ?? 0, o.max)} r={3.5} />
                ) : null,
              )}
            </g>
          )}
          {/* one overlay takes the pointer for the whole plot, so there is nothing to hit or miss */}
          <rect
            x={pad.left}
            y={pad.top}
            width={plotW}
            height={plotH}
            fill="transparent"
            style={onPick ? { cursor: "pointer" } : undefined}
            onClick={(e) => {
              if (!onPick || points.length === 0) return;
              // from where the click landed, not from the last move: a tap or a click that came
              // without one has no hover to go by
              const box = e.currentTarget.getBoundingClientRect();
              const index = Math.floor(((e.clientX - box.left) / Math.max(1, box.width)) * points.length);
              onPick(Math.max(0, Math.min(points.length - 1, index)));
            }}
            onMouseMove={(e) => {
              const box = e.currentTarget.getBoundingClientRect();
              const index = Math.floor(((e.clientX - box.left) / Math.max(1, box.width)) * points.length);
              setHover(Math.max(0, Math.min(points.length - 1, index)));
            }}
            onMouseLeave={() => setHover(null)}
          />
        </svg>
      )}
      {hovered && (
        <div className="chart-tip" style={tipPosition(xCenter(hover!), width)}>
          <div className="chart-tip-time">{intervalLabel(hovered.fromUtc, interval)}</div>
          {!hovered.hasValue && !anyOverlayHit ? (
            <div className="muted">nothing recorded</div>
          ) : !hovered.hasValue ? null : stacked ? (
            groups
              .map((g, gi) => ({ g, gi, value: hovered.values?.[g] ?? 0 }))
              .filter((row) => row.value > 0)
              .reverse()
              .map((row) => (
                <div key={row.g} className="chart-tip-row">
                  <span className="chart-swatch" style={{ background: groupColor(row.gi) }} />
                  <span className="chart-tip-k">{row.g}</span>
                  <span className="chart-tip-v">{format(row.value)}</span>
                </div>
              ))
          ) : (
            tipRows(kind, hovered, valueLabel).map((row) => (
              <div key={row.k} className="chart-tip-row">
                <span className="chart-tip-k">{row.k}</span>
                <span className="chart-tip-v">{format(row.v)}</span>
              </div>
            ))
          )}
          {over.map((o, oi) =>
            overlayHit(oi)?.hasValue ? (
              <div key={oi} className="chart-tip-row">
                <span className={"chart-swatch chart-swatch-" + overlayTone(oi)} />
                <span className="chart-tip-k">{o.label}</span>
                <span className="chart-tip-v">{o.format(overlayHit(oi)!.value ?? 0)}</span>
              </div>
            ) : null,
          )}
        </div>
      )}
      {!hasAny && !over.some((o) => o.points.some((p) => p.hasValue)) && width > 0 && <div className="chart-empty">Nothing recorded in this range.</div>}
    </div>
  );
}

function tipRows(kind: SeriesKind, p: SeriesPoint, valueLabel?: string): { k: string; v: number }[] {
  const rows: { k: string; v: number }[] = [];
  if (kind === "avgminmax" || kind === "full") {
    rows.push({ k: "avg", v: p.value ?? 0 });
    if (p.min != null) rows.push({ k: "min", v: p.min });
    if (p.max != null) rows.push({ k: "max", v: p.max });
    if (p.count != null) rows.push({ k: "entries", v: p.count });
    if (p.sum != null) rows.push({ k: "total", v: p.sum });
  } else {
    rows.push({ k: valueLabel ?? (kind === "sum" ? "total" : "count"), v: p.value ?? 0 });
  }
  return rows;
}

// the tooltip follows the cursor but never leaves the chart; past the middle it flips to the left
function tipPosition(x: number, width: number): CSSProperties {
  return x > width / 2 ? { right: Math.max(4, width - x + 8) } : { left: Math.max(4, x + 8) };
}

/** Runs of consecutive intervals that have a value: a line is drawn per run, never across a gap. */
function segments(points: SeriesPoint[]): { index: number; point: SeriesPoint }[][] {
  const all: { index: number; point: SeriesPoint }[][] = [];
  let current: { index: number; point: SeriesPoint }[] = [];
  points.forEach((point, index) => {
    if (point.hasValue) {
      current.push({ index, point });
    } else if (current.length > 0) {
      all.push(current);
      current = [];
    }
  });
  if (current.length > 0) all.push(current);
  return all;
}

type Segment = { index: number; point: SeriesPoint }[];

function linePath(segment: Segment, x: (i: number) => number, y: (v: number) => number): string {
  return segment.map((s, i) => `${i === 0 ? "M" : "L"}${x(s.index).toFixed(1)},${y(s.point.value ?? 0).toFixed(1)}`).join(" ");
}

function areaPath(segment: Segment, x: (i: number) => number, y: (v: number) => number, baseline: number): string {
  if (segment.length === 0) return "";
  const top = linePath(segment, x, y);
  return `${top} L${x(segment[segment.length - 1].index).toFixed(1)},${baseline.toFixed(1)} L${x(segment[0].index).toFixed(1)},${baseline.toFixed(1)} Z`;
}

function bandPath(segment: Segment, x: (i: number) => number, y: (v: number) => number): string {
  const up = segment.map((s, i) => `${i === 0 ? "M" : "L"}${x(s.index).toFixed(1)},${y(s.point.max ?? s.point.value ?? 0).toFixed(1)}`).join(" ");
  const down = [...segment]
    .reverse()
    .map((s) => `L${x(s.index).toFixed(1)},${y(s.point.min ?? s.point.value ?? 0).toFixed(1)}`)
    .join(" ");
  return `${up} ${down} Z`;
}

/** Three to five round numbers covering the range, so the axis reads in 1 / 2 / 5 steps. */
function axisTicks(min: number, max: number, integer: boolean): number[] {
  if (!isFinite(max) || max <= 0) max = 1;
  if (min > 0) min = 0;
  const span = max - min;
  const rough = span / 4;
  const magnitude = Math.pow(10, Math.floor(Math.log10(rough || 1)));
  let step = [1, 2, 5, 10].map((m) => m * magnitude).find((s) => s >= rough) ?? magnitude * 10;
  // a count of one and a half is not a value the series can hold, and an axis that offers it
  // labels two neighbouring lines with the same rounded number
  if (integer) step = Math.max(1, Math.round(step));
  const first = Math.floor(min / step) * step;
  const ticks: number[] = [];
  for (let t = first; t < max + step * 0.5; t += step) ticks.push(round(t));
  if (ticks.length < 2) ticks.push(round(first + step));
  return ticks;
}
const round = (n: number) => Math.round(n * 1e6) / 1e6;

/** Axis numbers shorten; the tooltip keeps the exact value. */
function compact(value: number, format: (v: number) => string): string {
  const abs = Math.abs(value);
  if (abs >= 1_000_000) return (value / 1_000_000).toFixed(abs >= 10_000_000 ? 0 : 1) + "M";
  if (abs >= 1_000) return (value / 1_000).toFixed(abs >= 10_000 ? 0 : 1) + "k";
  return format(value);
}

function xLabels(points: SeriesPoint[], interval: IntervalType, band: number, plotW: number): { index: number; text: string }[] {
  if (points.length === 0 || band <= 0) return [];
  const room = Math.max(1, Math.floor(plotW / 78)); // ~78px per label before they touch
  // Hours over several days: a row of times of day reads as one day, so the days are labelled
  // instead, at their midnights. A timezone whose midnight falls between two hour buckets has no
  // point to hang a date on, and keeps the times.
  if ((interval === "Hour" || interval === "Minute") && points.length > 1) {
    const span = Date.parse(points[points.length - 1].fromUtc) - Date.parse(points[0].fromUtc);
    if (span >= 2 * 86_400_000) {
      const midnights = points
        .map((p, index) => ({ index, date: new Date(p.fromUtc) }))
        .filter((x) => x.date.getHours() === 0 && x.date.getMinutes() === 0);
      if (midnights.length > 0) {
        const every = Math.max(1, Math.ceil(midnights.length / room));
        return midnights
          .filter((_, k) => k % every === 0)
          .map((x) => ({ index: x.index, text: x.date.toLocaleDateString([], { month: "short", day: "numeric" }) }));
      }
    }
  }
  const every = Math.max(1, Math.ceil(points.length / room));
  const labels: { index: number; text: string }[] = [];
  for (let i = points.length - 1; i >= 0; i -= every) labels.push({ index: i, text: axisTime(points[i].fromUtc, interval) });
  return labels.reverse();
}

function axisTime(iso: string, interval: IntervalType): string {
  const d = new Date(iso);
  switch (interval) {
    case "Second":
      return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
    case "Minute":
    case "Hour":
      return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
    case "Day":
    case "Week":
      return d.toLocaleDateString([], { month: "short", day: "numeric" });
    case "Month":
      return d.toLocaleDateString([], { month: "short", year: "numeric" });
  }
}

/** The interval a point covers, named in full: what the tooltip is about. */
export function intervalLabel(iso: string, interval: IntervalType): string {
  const d = new Date(iso);
  switch (interval) {
    case "Second":
      return d.toLocaleString();
    case "Minute":
    case "Hour":
      return d.toLocaleDateString([], { month: "short", day: "numeric" }) + " " + d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
    case "Day":
      return d.toLocaleDateString([], { weekday: "short", month: "short", day: "numeric" });
    case "Week":
      return "Week of " + d.toLocaleDateString([], { month: "short", day: "numeric" });
    case "Month":
      return d.toLocaleDateString([], { month: "long", year: "numeric" });
  }
}
