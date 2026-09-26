import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { groupColor } from "./Chart";

// The smaller pictures of the custom logs page, drawn as plain SVG like Chart.tsx beside them: a line
// of a day's activity in a table row, a share per value, a histogram of a column's values, and the
// tiles that carry the numbers a chart is about.

/** The width of an element, following it as it resizes. */
function useWidth<T extends HTMLElement>(): [React.RefObject<T | null>, number] {
  const ref = useRef<T>(null);
  const [width, setWidth] = useState(0);
  useLayoutEffect(() => {
    const element = ref.current;
    if (!element) return;
    const measure = () => setWidth(element.clientWidth);
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    measure();
    return () => observer.disconnect();
  }, []);
  return [ref, width];
}

/**
 * A day in a line: one count per hour, oldest first, scaled to its own peak. It says when a log was
 * busy and whether it still is, which is all a row in a table has room for - the numbers are in the
 * row beside it.
 */
export function Sparkline({ values, width = 120, height = 26, title }: { values: number[]; width?: number; height?: number; title?: string }) {
  if (values.length === 0) return <span className="spark-empty">—</span>;
  const max = Math.max(1, ...values);
  const step = values.length > 1 ? width / (values.length - 1) : width;
  const y = (v: number) => height - 2 - (v / max) * (height - 4);
  const line = values.map((v, i) => `${i === 0 ? "M" : "L"}${(i * step).toFixed(1)},${y(v).toFixed(1)}`).join(" ");
  const area = `${line} L${width.toFixed(1)},${height} L0,${height} Z`;
  const total = values.reduce((sum, v) => sum + v, 0);
  return (
    <svg className="spark" width={width} height={height} role="img" aria-label={title}>
      <title>{title}</title>
      {total > 0 && <path className="spark-area" d={area} />}
      <path className={"spark-line" + (total === 0 ? " idle" : "")} d={line} />
      {total > 0 && <circle className="spark-dot" cx={(values.length - 1) * step} cy={y(values[values.length - 1])} r={2} />}
    </svg>
  );
}

export interface ShareItem {
  label: string;
  count: number;
  /** A value the page can do something with - list its entries, say. */
  onClick?: () => void;
  hint?: string;
  /** The colour the value has elsewhere on the page - its stack in a graph - so the two are read together. */
  color?: string;
}

/**
 * The values of a column by how often they occur, longest bar first. A bar is a share of the whole,
 * so the lengths compare across the list and the percentage beside each says the same in a number.
 */
export function ShareBars({ items, total, format, colored = false, empty }: { items: ShareItem[]; total: number; format: (n: number) => string; colored?: boolean; empty?: ReactNode }) {
  if (items.length === 0) return <div className="share-empty">{empty ?? "Nothing to show."}</div>;
  const max = Math.max(1, ...items.map((i) => i.count));
  return (
    <div className="share-bars">
      {items.map((item, index) => {
        const share = total > 0 ? (item.count / total) * 100 : 0;
        const content = (
          <>
            <span className="share-label" title={item.label}>
              {colored && <span className="chart-swatch" style={{ background: item.color ?? groupColor(index) }} />}
              {item.label === "" ? <span className="muted">(empty)</span> : item.label}
            </span>
            <span className="share-track">
              <span className="share-fill" style={{ width: (item.count / max) * 100 + "%", background: colored ? (item.color ?? groupColor(index)) : undefined }} />
            </span>
            <span className="share-count">{format(item.count)}</span>
            <span className="share-pct">{share >= 10 ? share.toFixed(0) : share.toFixed(1)}%</span>
          </>
        );
        return item.onClick ? (
          <button key={item.label + index} className="share-row clickable" onClick={item.onClick} title={item.hint}>
            {content}
          </button>
        ) : (
          <div key={item.label + index} className="share-row" title={item.hint}>
            {content}
          </div>
        );
      })}
    </div>
  );
}

export interface HistogramBin {
  from: number;
  to: number;
  count: number;
}

/**
 * How a column's values are spread: a bar per range of values, as tall as the number of entries in
 * it. The x axis is the values, not time, so it carries its own labels; the marks above it are the
 * percentiles the page names beside it, so the picture and the numbers can be read against each other.
 */
export function Histogram({
  bins,
  format,
  height = 200,
  marks = [],
}: {
  bins: HistogramBin[];
  format: (v: number) => string;
  height?: number;
  marks?: { value: number; label: string }[];
}) {
  const [wrap, width] = useWidth<HTMLDivElement>();
  const [hover, setHover] = useState<number | null>(null);
  useEffect(() => setHover(null), [bins]);
  const pad = { top: 18, right: 12, bottom: 24, left: 46 };
  const plotW = Math.max(0, width - pad.left - pad.right);
  const plotH = height - pad.top - pad.bottom;
  const max = Math.max(1, ...bins.map((b) => b.count));
  const band = bins.length > 0 ? plotW / bins.length : 0;
  const min = bins.length > 0 ? bins[0].from : 0;
  const top = bins.length > 0 ? bins[bins.length - 1].to : 1;
  // whole numbers spanning fewer values than there are bins come one bin per value (from = to):
  // a value then stands in the middle of its bar rather than on the edge between two
  const whole = bins.length > 0 && bins.every((b) => b.to === b.from);
  const span = whole ? bins.length : top - min || 1;
  const xOf = (v: number) => pad.left + ((v - min + (whole ? 0.5 : 0)) / span) * plotW;
  const yOf = (c: number) => pad.top + plotH - (c / max) * plotH;
  const ticks = [0, max / 2, max].map((t) => Math.round(t));
  const labelEvery = Math.max(1, Math.ceil(bins.length / Math.max(1, Math.floor(plotW / 70))));
  const hovered = hover != null ? bins[hover] : null;
  return (
    <div className="chart histogram" ref={wrap} style={{ height }}>
      {width > 0 && (
        <svg width={width} height={height} role="img">
          {ticks.map((t) => (
            <g key={t}>
              <line className="chart-grid" x1={pad.left} x2={width - pad.right} y1={yOf(t)} y2={yOf(t)} />
              <text className="chart-axis" x={pad.left - 8} y={yOf(t)} textAnchor="end" dominantBaseline="middle">
                {t.toLocaleString("en-US")}
              </text>
            </g>
          ))}
          {bins.map((b, i) =>
            b.count > 0 ? (
              <rect
                key={i}
                className={"chart-bar" + (hover === i ? " hover" : "")}
                x={pad.left + i * band + Math.min(1, band * 0.1)}
                width={Math.max(1, band - Math.min(2, band * 0.2))}
                y={yOf(b.count)}
                height={Math.max(1, pad.top + plotH - yOf(b.count))}
              />
            ) : null,
          )}
          {bins.map((b, i) =>
            i % labelEvery === 0 ? (
              <text key={"x" + i} className="chart-axis" x={pad.left + i * band + band / 2} y={height - 6} textAnchor="middle">
                {format(b.from)}
              </text>
            ) : null,
          )}
          {marks.map((m) => (
            <g key={m.label}>
              <line className="histogram-mark" x1={xOf(m.value)} x2={xOf(m.value)} y1={pad.top - 4} y2={pad.top + plotH} />
              <text className="histogram-mark-label" x={xOf(m.value)} y={pad.top - 7} textAnchor="middle">
                {m.label}
              </text>
            </g>
          ))}
          <rect
            x={pad.left}
            y={pad.top}
            width={plotW}
            height={plotH}
            fill="transparent"
            onMouseMove={(e) => {
              const box = e.currentTarget.getBoundingClientRect();
              const index = Math.floor(((e.clientX - box.left) / Math.max(1, box.width)) * bins.length);
              setHover(Math.max(0, Math.min(bins.length - 1, index)));
            }}
            onMouseLeave={() => setHover(null)}
          />
        </svg>
      )}
      {hovered && (
        <div className="chart-tip" style={pad.left + (hover! + 0.5) * band > width / 2 ? { right: Math.max(4, width - pad.left - (hover! + 0.5) * band + 8) } : { left: pad.left + (hover! + 0.5) * band + 8 }}>
          <div className="chart-tip-time">{hovered.to === hovered.from ? format(hovered.from) : `${format(hovered.from)} – ${format(hovered.to)}`}</div>
          <div className="chart-tip-row">
            <span className="chart-tip-k">entries</span>
            <span className="chart-tip-v">{hovered.count.toLocaleString("en-US")}</span>
          </div>
        </div>
      )}
    </div>
  );
}

export interface Tile {
  label: string;
  value: string;
  hint?: string;
  tone?: "ok" | "warn" | "muted";
}

/** The numbers a panel is about, in a row of small tiles above it. */
export function StatTiles({ tiles }: { tiles: Tile[] }) {
  if (tiles.length === 0) return null;
  return (
    <div className="stat-tiles">
      {tiles.map((t) => (
        <div key={t.label} className={"stat-tile" + (t.tone ? " " + t.tone : "")} title={t.hint}>
          <span className="stat-tile-label">{t.label}</span>
          <span className="stat-tile-value">{t.value}</span>
        </div>
      ))}
    </div>
  );
}
