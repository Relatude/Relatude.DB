import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { KindIcon } from "./DatamodelIcons";
import { formatCount } from "../format";
import type { TypeCount } from "../server/dashboard";
import { TypeCubes } from "./TypeCubes";

export type TypeChartShape = "bars" | "treemap" | "cubes" | "sunburst" | "donut";

export interface TypeSlice {
  type: TypeCount;
  value: number;
  color: string;
}

/**
 * How much of a database each node type is, drawn five ways. They are not decoration of one
 * another: bars compare exact amounts and stay readable down to the long tail, a treemap shows a
 * whole made of parts and is the only one of the three that survives fifty types, and a donut is
 * for the handful of types that actually dominate, and the cubes spend volume rather than area on
 * the count, which is the only one of the four where a type a thousand times smaller than another is
 * still something you can see. The sunburst is the odd one out: its rings are the model's own
 * inheritance, so it answers what lives under an interface or a base class rather than only which
 * types are big. The colour is the model source the type comes
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
  if (shape === "sunburst") return <Sunburst slices={slices} total={total} onTileClick={onTileClick} />;
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
/**
 * The space between two tiles. The layout partitions the whole rectangle, so the gap is taken out of
 * the tiles themselves - half of it on each side of the edge they share - which is also what keeps
 * every tile's own border inside the picture instead of half of it falling on the edge of the svg or
 * under its neighbour. A tile too small to give the gap away keeps a quarter of its size instead, so
 * a sliver stays a sliver rather than disappearing into the margin.
 */
const tileGap = 4;
const inset = (size: number) => Math.min(tileGap / 2, size / 4);
/** How long a tile takes to get where the layout wants it. */
const tweenMs = 380;

/** A tile as it is on screen, on its way to where the layout wants it. */
interface Tile {
  slice: TypeSlice;
  rect: Rect;
  opacity: number;
  leaving: boolean;
}

/**
 * The room a chart has been given, in real pixels: the stylesheet's minimum height, plus whatever a
 * panel with space to spare - a row dragged taller, the panel maximized - hands it through flex,
 * which the observer picks up. Real pixels rather than a stretched viewBox because the labels are
 * ordinary text and a non-uniform scale would squash them.
 */
function useBoxSize() {
  const ref = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ width: 0, height: 0 });
  useLayoutEffect(() => {
    const el = ref.current;
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
  return { ref, width: size.width, height: size.height };
}

function Treemap({ slices, total, onTileClick }: { slices: TypeSlice[]; total: number; onTileClick?: (slice: TypeSlice, at: { x: number; y: number }) => void }) {
  const { ref: box, width, height: measured } = useBoxSize();
  const height = Math.max(treemapMinHeight, Math.floor(measured));
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
              <g
                key={s.type.id}
                className={"dash-tile-g" + (clickable ? " clickable" : "")}
                opacity={opacity}
                // the type's colour, which the stylesheet washes into the panel for the fill and
                // keeps at strength for the border - so one value serves both themes
                style={{ "--tile": s.color } as React.CSSProperties}
                onClick={clickable ? (e) => onTileClick(s, { x: e.clientX, y: e.clientY }) : undefined}
              >
                <title>{title(s, total)}</title>
                <rect
                  x={r.x + inset(r.w)}
                  y={r.y + inset(r.h)}
                  width={Math.max(0, r.w - inset(r.w) * 2)}
                  height={Math.max(0, r.h - inset(r.h) * 2)}
                  className="dash-tile-rect"
                />
                {/* below the size where a name fits, the tile is a colour and a tooltip */}
                {r.w > 54 && r.h > 26 && (
                  <foreignObject x={r.x + inset(r.w)} y={r.y + inset(r.h)} width={Math.max(0, r.w - inset(r.w) * 2)} height={Math.max(0, r.h - inset(r.h) * 2)}>
                    <div className="dash-tile-label">
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

// ---- sunburst ----

/** The height the sunburst has on its own, the treemap's; like it, it grows into a panel with more. */
const sunburstMinHeight = treemapMinHeight;
/** Below this a ring is a band of colour and a tooltip: no label would fit in it. */
const ringLabelHeight = 15;
/**
 * How thick a ring may get. Without a cap a model of one or two levels spends the whole radius on
 * them, which is a pie with a hole rather than a sunburst; with it a shallow model is simply a
 * smaller figure, drawn at the thickness a ring is meant to have.
 */
const maxRingThickness = 86;
/** The label's font, which has to match .dash-sun-name for the measurement below to mean anything. */
const sunLabelFont = '600 11.5px system-ui, "Segoe UI", sans-serif';

/**
 * How wide a label would actually be, measured rather than guessed from the letter count - the
 * guess decides whether a name is drawn at all, and it was wrong by about a letter either way.
 * One canvas, and each string measured once.
 */
const labelWidth = (() => {
  const cache = new Map<string, number>();
  let ctx: CanvasRenderingContext2D | null | undefined;
  return (text: string): number => {
    const hit = cache.get(text);
    if (hit !== undefined) return hit;
    if (ctx === undefined) ctx = document.createElement("canvas").getContext("2d");
    if (!ctx) return text.length * 6.3; // no 2d context to ask: back to the guess
    ctx.font = sunLabelFont;
    const w = ctx.measureText(text).width;
    cache.set(text, w);
    return w;
  };
})();

/** One arc: a type, the ring it sits in, and the sweep it was given. */
interface SunArc {
  slice: TypeSlice;
  /** 0 for a type with nothing above it, one more for each step down the model's inheritance */
  depth: number;
  from: number;
  to: number;
  /** the type's own nodes and every shown type under it: what the arc measures */
  total: number;
}

/**
 * The database by node type again, with the model's inheritance for its rings: the middle is
 * everything, the first ring the types that sit under nothing, and each ring outward what is
 * derived from the ring inside it. An arc is as wide as its own nodes and everything below it, so
 * an interface or an abstract base - which has no nodes of its own and is a blank in every other
 * shape here - is exactly as big as what it stands for.
 *
 * A node is still counted once, under the type it actually is. Where a parent has nodes of its own
 * as well as children, those nodes are the part of its sweep that the ring outside leaves bare.
 */
function Sunburst({ slices, total, onTileClick }: { slices: TypeSlice[]; total: number; onTileClick?: (slice: TypeSlice, at: { x: number; y: number }) => void }) {
  const { ref: box, width, height: measured } = useBoxSize();
  const height = Math.max(sunburstMinHeight, Math.floor(measured));
  const arcs = useMemo(() => nestByInheritance(slices), [slices]);

  const rings = arcs.reduce((n, a) => Math.max(n, a.depth + 1), 0);
  const cx = width / 2;
  const cy = height / 2;
  // the hole carries the total, and is what keeps the innermost ring from being a wedge of a pie.
  // A model of two or three levels does not spend the whole radius on them: the rings keep their
  // thickness and the figure is simply smaller, which is the difference between a sunburst of one
  // ring and a hoop with a number lost in the middle of it.
  const room = Math.min(width, height) / 2 - 6;
  const hole = Math.max(30, Math.min(room * 0.32, 74));
  const outer = Math.min(room, hole + rings * maxRingThickness);
  const thickness = rings > 0 ? Math.max(0, (outer - hole) / rings) : 0;
  const totalSize = Math.max(13, Math.min(hole * 0.34, 30));

  return (
    <div className="dash-sunburst" ref={box}>
      {width > 0 && rings > 0 && (
        <svg width={width} height={height}>
          {arcs.map((a) => {
            const r0 = hole + a.depth * thickness;
            const r1 = r0 + Math.max(1, thickness - 1.5); // a hairline of panel between the rings
            const mid = (a.from + a.to) / 2;
            const rm = (r0 + r1) / 2;
            const at = { x: cx + rm * Math.cos(mid), y: cy + rm * Math.sin(mid) };
            const band = r1 - r0;
            const along = rm * (a.to - a.from);
            const name = a.slice.type.name;
            const needed = labelWidth(name) + 9;
            // along the arc where the sweep carries the name, across the ring where it does not and
            // the ring is deep enough to take it lying on its side, nothing at all when neither
            const lie = along > needed && band > ringLabelHeight;
            const stand = !lie && band > needed && along > 13;
            const deg = (mid * 180) / Math.PI;
            const clickable = onTileClick !== undefined && a.slice.type.id !== otherSliceId;
            const own = a.slice.value;
            return (
              <g
                key={a.slice.type.id}
                className={"dash-sun-g" + (clickable ? " clickable" : "")}
                style={{ "--tile": a.slice.color } as React.CSSProperties}
                // reported with the arc's own number rather than the type's own nodes: what was
                // clicked is the ring, and a base class with nothing of its own is not "0 nodes"
                onClick={clickable ? (e) => onTileClick({ ...a.slice, value: a.total }, { x: e.clientX, y: e.clientY }) : undefined}
              >
                <title>
                  {title({ ...a.slice, value: a.total }, total)}
                  {own !== a.total ? ` · ${formatCount(own)} of its own` : ""}
                </title>
                <path d={arcPath(cx, cy, r1, r0, a.from, a.to)} className="dash-sun-arc" />
                {(lie || stand) && (
                  <g transform={`rotate(${upright(lie ? deg + 90 : deg)}, ${at.x.toFixed(2)}, ${at.y.toFixed(2)})`}>
                    <text x={at.x} y={at.y - (lie && band > 30 ? 6 : 0)} className="dash-sun-name" textAnchor="middle" dominantBaseline="central">
                      {name}
                    </text>
                    {lie && band > 30 && (
                      <text x={at.x} y={at.y + 7} className="dash-sun-count" textAnchor="middle" dominantBaseline="central">
                        {formatCount(a.total)}
                      </text>
                    )}
                  </g>
                )}
              </g>
            );
          })}
          <text x={cx} y={cy - totalSize * 0.18} className="dash-sun-total" style={{ fontSize: totalSize }} textAnchor="middle">
            {formatCount(total)}
          </text>
          <text x={cx} y={cy + totalSize * 0.76} className="dash-sun-caption" style={{ fontSize: Math.max(9, totalSize * 0.42) }} textAnchor="middle">
            nodes
          </text>
        </svg>
      )}
    </div>
  );
}

/** Text rotated into the lower half reads upside down; turned round it reads the other way along. */
function upright(deg: number): number {
  const d = ((deg % 360) + 360) % 360;
  return d > 90 && d < 270 ? deg + 180 : deg;
}

/**
 * The slices arranged by what is under what. A type hangs off the first of its parents that is also
 * being shown - the first is the one it is declared under, and counting a type under two parents
 * would count its nodes twice - and one whose parents are all missing (an outermost type, or one
 * whose parent was hidden) starts a ring of its own in the middle.
 *
 * An arc is its type's own nodes plus every arc below it, which is what makes the picture add up:
 * the ring outside a type covers exactly the part of it that its subtypes hold, and the bare
 * remainder is the type itself. Siblings go round largest first. A model that somehow has a type
 * above itself is cut loose rather than followed forever.
 */
function nestByInheritance(slices: TypeSlice[]): SunArc[] {
  const byId = new Map(slices.map((s) => [s.type.id, s]));
  const parentOf = new Map<string, string | null>();
  for (const s of slices) parentOf.set(s.type.id, (s.type.parents ?? []).find((p) => p !== s.type.id && byId.has(p)) ?? null);
  for (const id of [...parentOf.keys()]) {
    const seen = new Set([id]);
    for (let up = parentOf.get(id) ?? null; up !== null; up = parentOf.get(up) ?? null) {
      if (seen.has(up)) {
        parentOf.set(id, null);
        break;
      }
      seen.add(up);
    }
  }

  const children = new Map<string, TypeSlice[]>();
  const roots: TypeSlice[] = [];
  for (const s of slices) {
    const parent = parentOf.get(s.type.id) ?? null;
    if (parent === null) roots.push(s);
    else children.set(parent, [...(children.get(parent) ?? []), s]);
  }

  const totals = new Map<string, number>();
  const totalOf = (s: TypeSlice): number => {
    const hit = totals.get(s.type.id);
    if (hit !== undefined) return hit;
    const sum = (children.get(s.type.id) ?? []).reduce((n, c) => n + totalOf(c), s.value);
    totals.set(s.type.id, sum);
    return sum;
  };

  const out: SunArc[] = [];
  const place = (list: TypeSlice[], from: number, span: number, whole: number, depth: number) => {
    if (whole <= 0 || span <= 0 || depth > 16) return;
    let at = from;
    for (const s of [...list].sort((a, b) => totalOf(b) - totalOf(a) || a.type.name.localeCompare(b.type.name))) {
      const sum = totalOf(s);
      const sweep = (sum / whole) * span;
      if (sweep <= 0) continue;
      out.push({ slice: s, depth, from: at, to: at + sweep, total: sum });
      // the children take the leading part of what their parent was given; what they do not reach
      // is the parent's own nodes
      place(children.get(s.type.id) ?? [], at, sweep, sum, depth + 1);
      at += sweep;
    }
  };
  place(roots, -Math.PI / 2, Math.PI * 2, roots.reduce((n, s) => n + totalOf(s), 0), 0); // from twelve o'clock
  return out;
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

function parseHex(hex: string): { r: number; g: number; b: number } {
  const clean = hex.replace("#", "");
  const full = clean.length === 3 ? [...clean].map((c) => c + c).join("") : clean;
  const n = parseInt(full, 16);
  return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255 };
}
