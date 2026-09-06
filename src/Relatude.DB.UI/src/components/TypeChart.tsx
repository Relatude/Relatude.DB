import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { KindIcon } from "./DatamodelIcons";
import { formatCount } from "../format";
import type { TypeCount } from "../server/dashboard";
import { TypeCubes } from "./TypeCubes";

export type TypeChartShape = "bars" | "treemap" | "cubes" | "donut";

export interface TypeSlice {
  type: TypeCount;
  value: number;
  color: string;
}

/**
 * How much of a database each node type is, drawn four ways. They are not decoration of one
 * another: bars compare exact amounts and stay readable down to the long tail, a treemap shows a
 * whole made of parts and is the only one of the three that survives fifty types, and a donut is
 * for the handful of types that actually dominate, and the cubes spend volume rather than area on
 * the count, which is the only one of the four where a type a thousand times smaller than another is
 * still something you can see. The colour is the model source the type comes
 * from - the same colour the model editor gives it - with the types of one source separated by
 * lightness, so a type keeps its identity across pages while a source stays recognisable as a group.
 *
 * A treemap tile can be clicked when a handler is given: it reports the type and where the click
 * was, and the caller decides what to offer there. The folded tail of small types is not a type and
 * takes no click.
 */
export function TypeChart({
  shape,
  slices,
  total,
  onTileClick,
}: {
  shape: TypeChartShape;
  slices: TypeSlice[];
  total: number;
  onTileClick?: (slice: TypeSlice, at: { x: number; y: number }) => void;
}) {
  if (slices.length === 0) return <div className="muted dash-chart-empty">No nodes yet.</div>;
  if (shape === "bars") return <Bars slices={slices} />;
  if (shape === "treemap") return <Treemap slices={slices} total={total} onTileClick={onTileClick} />;
  if (shape === "cubes") return <TypeCubes slices={slices} total={total} onTileClick={onTileClick} />;
  return <Donut slices={slices} total={total} />;
}

/** The id of the slice that stands for the types beyond what the chart shows one by one. */
export const otherSliceId = "__other";

function share(value: number, total: number): string {
  if (total <= 0) return "0%";
  const pct = (value / total) * 100;
  return pct >= 10 ? Math.round(pct) + "%" : pct >= 1 ? pct.toFixed(1) + "%" : pct > 0 ? "<1%" : "0%";
}

function title(s: TypeSlice, total: number): string {
  return `${s.type.full} — ${formatCount(s.value)} nodes (${share(s.value, total)})`;
}

// ---- bars ----

function Bars({ slices }: { slices: TypeSlice[] }) {
  const top = Math.max(1, ...slices.map((s) => s.value));
  return (
    <div className="dash-types">
      {slices.map((s) => (
        <div key={s.type.id} className="dash-type" title={s.type.full}>
          <span className="log-cell dash-type-name">
            <KindIcon kind={s.type.kind} size={13} />
            {s.type.name}
          </span>
          <span className="scan-bar">
            <span className="scan-bar-fill" style={{ width: Math.max(2, Math.round((s.value / top) * 100)) + "%", background: s.color }} />
          </span>
          <span className="num">{formatCount(s.value)}</span>
        </div>
      ))}
    </div>
  );
}

// ---- treemap ----

interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

/** The height the treemap has on its own; in a panel with room to spare it takes the room instead. */
const treemapMinHeight = 230;
/** How long a tile takes to get where the layout wants it. */
const tweenMs = 380;

/** A tile as it is on screen, on its way to where the layout wants it. */
interface Tile {
  slice: TypeSlice;
  rect: Rect;
  opacity: number;
  leaving: boolean;
}

function Treemap({ slices, total, onTileClick }: { slices: TypeSlice[]; total: number; onTileClick?: (slice: TypeSlice, at: { x: number; y: number }) => void }) {
  // laid out in real pixels rather than in a stretched viewBox: the labels are ordinary text and a
  // non-uniform scale would squash them. The box is measured both ways: the stylesheet gives it its
  // minimum height, and a panel with room to spare - a row dragged taller, the panel maximized -
  // hands it the rest through flex, which the observer picks up.
  const box = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ width: 0, height: 0 });
  useLayoutEffect(() => {
    const el = box.current;
    if (!el) return;
    const measure = () => {
      const r = el.getBoundingClientRect();
      setSize((prev) => (prev.width === r.width && prev.height === r.height ? prev : { width: r.width, height: r.height }));
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);
  const width = size.width;
  const height = Math.max(treemapMinHeight, Math.floor(size.height));
  // where the layout wants every tile
  const targets = useMemo(() => {
    const out = new Map<string, { slice: TypeSlice; rect: Rect }>();
    if (width <= 0) return out;
    squarify(slices.map((s) => s.value), width, height).forEach((r, i) => {
      const s = slices[i];
      if (s && r.w > 0 && r.h > 0) out.set(s.type.id, { slice: s, rect: r });
    });
    return out;
  }, [slices, width, height]);

  // What is on screen moves toward the targets rather than jumping: a tile that changes place slides
  // and resizes, a new tile grows out of its spot, a tile that is gone shrinks into its own middle
  // and is dropped once it is there. A change in the middle of a move starts the next move from
  // wherever the tiles are, so a row being dragged is followed rather than fought. The first picture
  // simply appears.
  const shown = useRef<Map<string, Tile>>(new Map());
  const anim = useRef<{ from: Map<string, Tile>; to: Map<string, Tile>; start: number } | null>(null);
  const raf = useRef(0);
  const [, setFrame] = useState(0);
  useLayoutEffect(() => {
    if (targets.size === 0 && shown.current.size === 0) return;
    if (shown.current.size === 0) {
      shown.current = new Map([...targets].map(([id, t]) => [id, { slice: t.slice, rect: t.rect, opacity: 1, leaving: false }]));
      setFrame((f) => f + 1);
      return;
    }
    const from = new Map(shown.current);
    const to = new Map<string, Tile>();
    for (const [id, t] of targets) {
      to.set(id, { slice: t.slice, rect: t.rect, opacity: 1, leaving: false });
      if (!from.has(id)) from.set(id, { slice: t.slice, rect: middleOf(t.rect), opacity: 0, leaving: false });
    }
    for (const [id, tile] of from) if (!targets.has(id)) to.set(id, { slice: tile.slice, rect: middleOf(tile.rect), opacity: 0, leaving: true });
    anim.current = { from, to, start: performance.now() };
    cancelAnimationFrame(raf.current);
    const step = (now: number) => {
      const a = anim.current;
      if (!a) return;
      const p = Math.min(1, (now - a.start) / tweenMs);
      const e = 1 - Math.pow(1 - p, 3);
      const next = new Map<string, Tile>();
      for (const [id, target] of a.to) {
        if (p >= 1 && target.leaving) continue;
        const start = a.from.get(id) ?? target;
        next.set(id, { slice: target.slice, rect: lerpRect(start.rect, target.rect, e), opacity: start.opacity + (target.opacity - start.opacity) * e, leaving: target.leaving });
      }
      shown.current = next;
      setFrame((f) => f + 1);
      if (p < 1) raf.current = requestAnimationFrame(step);
      else anim.current = null;
    };
    raf.current = requestAnimationFrame(step);
  }, [targets]);
  useEffect(
    () => () => {
      cancelAnimationFrame(raf.current);
      raf.current = 0;
    },
    [],
  );

  const tiles = [...shown.current.values()];
  return (
    <div className="dash-treemap" ref={box}>
      {width > 0 && (
        <svg width={width} height={height}>
          {tiles.map(({ slice: s, rect: r, opacity, leaving }) => {
            const clickable = !leaving && onTileClick !== undefined && s.type.id !== otherSliceId;
            return (
              <g key={s.type.id} className={"dash-tile-g" + (clickable ? " clickable" : "")} opacity={opacity} onClick={clickable ? (e) => onTileClick(s, { x: e.clientX, y: e.clientY }) : undefined}>
                <title>{title(s, total)}</title>
                <rect x={r.x} y={r.y} width={Math.max(0, r.w)} height={Math.max(0, r.h)} fill={s.color} className="dash-tile-rect" />
                {/* below the size where a name fits, the tile is a colour and a tooltip */}
                {r.w > 54 && r.h > 26 && (
                  <foreignObject x={r.x} y={r.y} width={r.w} height={r.h}>
                    <div className="dash-tile-label" style={{ color: readable(s.color) }}>
                      <span className="dash-tile-name">{s.type.name}</span>
                      {r.h > 40 && <span className="dash-tile-count">{formatCount(s.value)}</span>}
                    </div>
                  </foreignObject>
                )}
              </g>
            );
          })}
        </svg>
      )}
    </div>
  );
}

function middleOf(r: Rect): Rect {
  return { x: r.x + r.w / 2, y: r.y + r.h / 2, w: 0, h: 0 };
}

function lerpRect(a: Rect, b: Rect, t: number): Rect {
  return { x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t, w: a.w + (b.w - a.w) * t, h: a.h + (b.h - a.h) * t };
}

/**
 * Squarified treemap (Bruls, Huizing, van Wijk): fills the rectangle row by row along its shorter
 * side, extending a row while that keeps the tiles closer to square and starting a new one when it
 * would not. Values must be positive and are laid out in the order given, largest first.
 */
export function squarify(values: number[], width: number, height: number): Rect[] {
  const out: Rect[] = [];
  let x = 0;
  let y = 0;
  let w = width;
  let h = height;
  let i = 0;
  while (i < values.length) {
    const remaining = values.slice(i).reduce((a, b) => a + b, 0);
    if (remaining <= 0 || w <= 0 || h <= 0) {
      for (; i < values.length; i++) out.push({ x, y, w: 0, h: 0 });
      break;
    }
    const scale = (w * h) / remaining;
    const alongX = w < h; // rows run along the shorter side, which is what keeps tiles square
    const side = alongX ? w : h;
    const row: number[] = [];
    let rowSum = 0;
    let best = Infinity;
    let j = i;
    for (; j < values.length; j++) {
      const area = values[j] * scale;
      const worst = worstRatio([...row, area], rowSum + area, side);
      if (row.length > 0 && worst > best) break;
      row.push(area);
      rowSum += area;
      best = worst;
    }
    const thickness = rowSum / side;
    let pos = alongX ? x : y;
    for (const area of row) {
      const length = thickness > 0 ? area / thickness : 0;
      out.push(alongX ? { x: pos, y, w: length, h: thickness } : { x, y: pos, w: thickness, h: length });
      pos += length;
    }
    if (alongX) {
      y += thickness;
      h -= thickness;
    } else {
      x += thickness;
      w -= thickness;
    }
    i = j;
  }
  return out;
}

/** The least square-like tile a row would have: what the algorithm minimises. */
function worstRatio(areas: number[], sum: number, side: number): number {
  if (sum <= 0 || side <= 0) return Infinity;
  const thickness = sum / side;
  let worst = 0;
  for (const a of areas) {
    if (a <= 0) return Infinity;
    const length = a / thickness;
    worst = Math.max(worst, Math.max(thickness / length, length / thickness));
  }
  return worst;
}

// ---- donut ----

function Donut({ slices, total }: { slices: TypeSlice[]; total: number }) {
  const size = 132;
  const r = 58;
  const inner = 34;
  const sum = slices.reduce((a, s) => a + s.value, 0);
  let angle = -Math.PI / 2; // twelve o'clock
  const arcs = slices.map((s) => {
    const sweep = sum > 0 ? (s.value / sum) * Math.PI * 2 : 0;
    const from = angle;
    angle += sweep;
    return { slice: s, from, to: angle, sweep };
  });
  return (
    <div className="dash-donut">
      <svg viewBox={`0 0 ${size} ${size}`} className="dash-donut-svg">
        {arcs.map(({ slice, from, to, sweep }) =>
          sweep <= 0 ? null : (
            <path
              key={slice.type.id}
              d={arcPath(size / 2, size / 2, r, inner, from, to)}
              fill={slice.color}
              className="dash-donut-arc"
            >
              <title>{title(slice, total)}</title>
            </path>
          ),
        )}
        <text x={size / 2} y={size / 2 - 2} className="dash-donut-total" textAnchor="middle">
          {formatCount(sum)}
        </text>
        <text x={size / 2} y={size / 2 + 12} className="dash-donut-caption" textAnchor="middle">
          nodes
        </text>
      </svg>
      <div className="dash-donut-legend">
        {slices.map((s) => (
          <div key={s.type.id} className="dash-legend-row" title={title(s, total)}>
            <span className="dash-legend-swatch" style={{ background: s.color }} />
            <span className="dash-legend-name">{s.type.name}</span>
            <span className="dash-legend-share">{share(s.value, sum)}</span>
            <span className="num">{formatCount(s.value)}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

/** One donut segment: outer arc out, inner arc back. A full circle is drawn as two half arcs. */
function arcPath(cx: number, cy: number, outer: number, inner: number, from: number, to: number): string {
  const full = to - from >= Math.PI * 2 - 0.0001;
  if (full) return arcPath(cx, cy, outer, inner, from, from + Math.PI) + arcPath(cx, cy, outer, inner, from + Math.PI, from + Math.PI * 2);
  const large = to - from > Math.PI ? 1 : 0;
  const p = (radius: number, a: number) => `${(cx + radius * Math.cos(a)).toFixed(2)} ${(cy + radius * Math.sin(a)).toFixed(2)}`;
  return `M${p(outer, from)} A${outer} ${outer} 0 ${large} 1 ${p(outer, to)} L${p(inner, to)} A${inner} ${inner} 0 ${large} 0 ${p(inner, from)} Z`;
}

// ---- colours ----

/** Mixes a hex colour towards white (amount > 0) or black (amount < 0). */
export function shade(hex: string, amount: number): string {
  const { r, g, b } = parseHex(hex);
  const target = amount >= 0 ? 255 : 0;
  const t = Math.abs(amount);
  const mix = (c: number) => Math.round(c + (target - c) * t);
  return `#${[mix(r), mix(g), mix(b)].map((c) => c.toString(16).padStart(2, "0")).join("")}`;
}

/** Black or white, whichever can be read on the colour. */
export function readable(hex: string): string {
  const { r, g, b } = parseHex(hex);
  // perceived luminance, the sRGB weights
  return (0.299 * r + 0.587 * g + 0.114 * b) / 255 > 0.6 ? "#14130f" : "#ffffff";
}

function parseHex(hex: string): { r: number; g: number; b: number } {
  const clean = hex.replace("#", "");
  const full = clean.length === 3 ? [...clean].map((c) => c + c).join("") : clean;
  const n = parseInt(full, 16);
  return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255 };
}
