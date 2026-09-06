import { useEffect, useMemo, useRef, useState } from "react";
import { IconAffiliate, IconArrowsMaximize, IconArrowsShuffle, IconCircleDotted, IconFileTypeSvg, IconLayoutColumns, IconLayoutGrid, IconLayoutRows, IconPalette, IconZoomIn, IconZoomOut } from "@tabler/icons-react";
import { downloadSvg } from "../svgExport";
import { FullscreenButton } from "./DatamodelGraph";
import type { EditorContext, Selection } from "./DatamodelEditors";
import { embeddedColor, indexMarks, kindMeta, propertyColor, relationColor, relationMeta } from "./DatamodelIcons";
import type { NodeTypeJson } from "../server/datamodel";

interface Props {
  ctx: EditorContext;
  visibleTypes: Set<string>;
  ghostTypes: Set<string>;
  selection: Selection | null;
  query: string;
  storeId: string;
}

interface Box {
  id: string;
  type: NodeTypeJson;
  x: number;
  y: number;
  w: number;
  h: number;
  rows: { id: string; name: string; propertyType: string; marks: string[] }[];
  more: number;
  ghost: boolean;
}

/**
 * How the boxes are arranged. Inheritance is the default reading and gets both directions; the other
 * three answer questions inheritance does not - what is in which source, what the whole model looks
 * like at once, and how a relation-heavy model connects when nothing inherits anything.
 */
export type LayoutMode = "layers" | "columns" | "grid" | "sources" | "circle" | "force";

const layouts: { id: LayoutMode; label: string; hint: string; icon: typeof IconLayoutRows }[] = [
  { id: "layers", label: "Layers", hint: "Inheritance top down: parents above their children", icon: IconLayoutRows },
  { id: "columns", label: "Columns", hint: "Inheritance left to right: parents left of their children", icon: IconLayoutColumns },
  { id: "grid", label: "Grid", hint: "Every type in one grid, by name", icon: IconLayoutGrid },
  { id: "sources", label: "Sources", hint: "One block per model source, in load order", icon: IconPalette },
  { id: "circle", label: "Circle", hint: "Types on a ring, so the lines between them read", icon: IconCircleDotted },
  { id: "force", label: "Force", hint: "Pulled together by what joins them, as in the Graph view: clusters fall out of the relations", icon: IconAffiliate },
];

type Edge =
  | { kind: "inherits"; from: string; to: string; id: string }
  | { kind: "relation"; from: string; to: string; id: string; label: string; directed: boolean; symmetric: boolean }
  | { kind: "reference"; from: string; to: string; id: string; label: string; propertyId: string }
  | { kind: "embeds"; from: string; to: string; id: string; label: string; propertyId: string };

const nodeWidth = 210;
const headerHeight = 28;
const rowHeight = 17;
const maxRows = 9;
const layerGap = 90;
const columnGap = 48;

/** Where the drawing is on screen: panned to (x, y) and scaled by k. */
interface View {
  x: number;
  y: number;
  k: number;
}

// One arrangement is a different reading of the same model, and watching a type travel from where it
// sat to where it belongs is what says so. Long enough to follow a box across the screen, short
// enough that switching twice in a row is not a wait.
const moveMs = 520;
/** Fast away, gentle in: the boxes settle rather than stop. */
const ease = (p: number) => 1 - Math.pow(1 - p, 3);

// dragged positions belong to the arrangement they were dragged in: a box moved in the grid has no
// meaning in the circle, so every layout remembers its own
function positionsKey(storeId: string, mode: LayoutMode) {
  return "dmDiagram:" + storeId + ":" + mode;
}
const layoutKey = (storeId: string) => "dmDiagramLayout:" + storeId;

function readPositions(storeId: string, mode: LayoutMode): Record<string, { x: number; y: number }> {
  try {
    const raw = localStorage.getItem(positionsKey(storeId, mode));
    return raw ? (JSON.parse(raw) as Record<string, { x: number; y: number }>) : {};
  } catch {
    return {};
  }
}
function writePositions(storeId: string, mode: LayoutMode, positions: Record<string, { x: number; y: number }>) {
  try {
    localStorage.setItem(positionsKey(storeId, mode), JSON.stringify(positions));
  } catch {
    // storage may be unavailable; the layout then simply is not remembered
  }
}

/** Arranges the boxes. Every mode writes x and y straight onto them; nothing else in the diagram
 *  knows which one ran. */
function place(mode: LayoutMode, boxes: Box[], depth: Map<string, number>, ctx: EditorContext) {
  if (boxes.length === 0) return;
  if (mode === "layers" || mode === "columns") layered(boxes, depth, mode === "columns");
  else if (mode === "grid") grid([...boxes].sort((a, b) => a.type.CodeName.localeCompare(b.type.CodeName)), 0, 0);
  else if (mode === "sources") bySource(boxes, ctx);
  else if (mode === "force") forceLayout(boxes, ctx);
  else circle(boxes);
}

/**
 * Inheritance, as layers. Roots first, then each next layer ordered by the mean position of its
 * parents so the lines between layers cross as little as they can. A layer wider than the budget
 * wraps, so a flat model (many roots, little inheritance) reads as a block rather than as a line off
 * the edge of the screen. Horizontal swaps the two axes: layers become columns and a layer stacks.
 */
function layered(boxes: Box[], depth: Map<string, number>, horizontal: boolean) {
  const layers = new Map<number, Box[]>();
  for (const b of boxes) {
    const d = depth.get(b.id) ?? 0;
    layers.set(d, [...(layers.get(d) ?? []), b]);
  }
  const layerKeys = [...layers.keys()].sort((a, b) => a - b);
  const size = (b: Box) => (horizontal ? b.h : b.w);
  const gap = horizontal ? rowHeight : columnGap;
  const budget = horizontal
    ? Math.max(4 * (60 + gap), Math.ceil(Math.sqrt(boxes.length)) * (90 + gap) * 1.6)
    : Math.max(3 * (nodeWidth + gap), Math.ceil(Math.sqrt(boxes.length)) * (nodeWidth + gap) * 1.3);
  let along = 0; // down the layers
  const center = new Map<string, number>();
  for (const d of layerKeys) {
    const layer = layers.get(d)!;
    if (d === 0) layer.sort((a, b) => a.type.CodeName.localeCompare(b.type.CodeName));
    else {
      const bary = (b: Box) => {
        const ps = (b.type.Parents ?? []).filter((p) => center.has(p));
        return ps.length === 0 ? Number.MAX_SAFE_INTEGER / 2 : ps.reduce((s, p) => s + center.get(p)!, 0) / ps.length;
      };
      layer.sort((a, b) => bary(a) - bary(b) || a.type.CodeName.localeCompare(b.type.CodeName));
    }
    const lines: Box[][] = [[]];
    let used = 0;
    for (const b of layer) {
      if (used > 0 && used + size(b) > budget) {
        lines.push([]);
        used = 0;
      }
      lines[lines.length - 1].push(b);
      used += size(b) + gap;
    }
    const widest = Math.max(...lines.map((line) => line.reduce((s, b) => s + size(b) + gap, -gap)));
    for (const line of lines) {
      const width = line.reduce((s, b) => s + size(b) + gap, -gap);
      let across = (widest - width) / 2;
      const thickness = Math.max(...line.map((b) => (horizontal ? b.w : b.h)));
      for (const b of line) {
        b.x = horizontal ? along : across;
        b.y = horizontal ? across : along;
        center.set(b.id, across + size(b) / 2);
        across += size(b) + gap;
      }
      along += thickness + layerGap / 2;
    }
    along += layerGap / 2;
  }
}

/** Boxes in reading order, wrapped into rows of roughly equal count. Returns the height it used. */
function grid(ordered: Box[], x0: number, y0: number): number {
  const perRow = Math.max(1, Math.ceil(Math.sqrt(ordered.length)));
  let x = x0;
  let y = y0;
  let rowHeightUsed = 0;
  ordered.forEach((b, i) => {
    if (i > 0 && i % perRow === 0) {
      x = x0;
      y += rowHeightUsed + layerGap / 2;
      rowHeightUsed = 0;
    }
    b.x = x;
    b.y = y;
    rowHeightUsed = Math.max(rowHeightUsed, b.h);
    x += b.w + columnGap;
  });
  return y + rowHeightUsed - y0;
}

/** One block per model source, in the order the sources load, each block a grid of its own types. */
function bySource(boxes: Box[], ctx: EditorContext) {
  const order = new Map(ctx.model.Sources.map((s, i) => [s.Id, i]));
  const groups = new Map<string, Box[]>();
  for (const b of boxes) groups.set(b.type.DatamodelSourceId, [...(groups.get(b.type.DatamodelSourceId) ?? []), b]);
  const keys = [...groups.keys()].sort((a, b) => (order.get(a) ?? 99) - (order.get(b) ?? 99));
  let y = 0;
  for (const key of keys) {
    const group = groups.get(key)!.sort((a, b) => a.type.CodeName.localeCompare(b.type.CodeName));
    const used = grid(group, 0, y);
    y += used + layerGap; // a clear band between one source and the next
  }
}

/** Types on a ring, big enough that the boxes do not touch, ordered by name. */
function circle(boxes: Box[]) {
  const ordered = [...boxes].sort((a, b) => a.type.CodeName.localeCompare(b.type.CodeName));
  const step = (2 * Math.PI) / ordered.length;
  // the ring has to fit every box side by side around it, plus room for the lines across the middle
  const radius = Math.max(nodeWidth, (ordered.length * (nodeWidth + columnGap)) / (2 * Math.PI));
  ordered.forEach((b, i) => {
    const a = i * step - Math.PI / 2;
    b.x = radius + radius * Math.cos(a) - b.w / 2;
    b.y = radius + radius * Math.sin(a) - b.h / 2;
  });
}

// ---- the force layout ----

// What a pair of joined types settles at, and how hard every pair pushes. The two are balanced so a
// joined pair comes to rest at about the link length; the pull to the middle then decides how wide
// the whole cloud ends up, since nothing else stops the unjoined ones drifting outward.
const linkLength = 330;
const boxCharge = -2600;
const centerPull = 0.045;
const boxPad = 26;

/** Every pair of types the diagram would draw a line between, as indexes into the boxes. */
function linksBetween(boxes: Box[], ctx: EditorContext): [number, number][] {
  const index = new Map(boxes.map((b, i) => [b.id, i]));
  const links: [number, number][] = [];
  const add = (a: string, b: string) => {
    const i = index.get(a);
    const j = index.get(b);
    if (i !== undefined && j !== undefined && i !== j) links.push([i, j]);
  };
  for (const b of boxes) {
    for (const p of b.type.Parents ?? []) add(b.id, p);
    for (const p of Object.values(b.type.Properties)) {
      if ((p.PropertyType === "Reference" || p.PropertyType === "References") && p.NodeTypes) for (const t of p.NodeTypes) add(b.id, t);
      if (p.PropertyType === "Embedded" && p.InnerNodeTypes) for (const t of p.InnerNodeTypes) add(b.id, t);
    }
  }
  for (const r of Object.values(ctx.model.Relations)) for (const s of r.SourceTypes) for (const t of r.TargetTypes) add(s, t);
  return links;
}

/**
 * Types pulled together by what joins them and pushed apart by everything else - the arrangement the
 * Graph view keeps alive under the hand, worked out here in one go and then left still, because the
 * diagram is a picture to read and print rather than something to play with. What it is good for is
 * the shape of a model nothing inherits: clusters fall out of the relations, and a type joined to
 * nothing drifts to the edge where it is easy to spot.
 *
 * It starts from the same spiral every time and has no randomness in it, so a model always relaxes
 * into the same picture. Opening a box or switching a source off would otherwise deal the whole
 * diagram again, which is no way to read one.
 */
function forceLayout(boxes: Box[], ctx: EditorContext) {
  const n = boxes.length;
  const links = linksBetween(boxes, ctx);
  // a sunflower spiral: an even spread to start from, and evenness is what keeps the first few
  // rounds from being one long shove apart
  const spread = Math.max(nodeWidth, Math.sqrt(n) * linkLength * 0.6);
  boxes.forEach((b, i) => {
    const a = i * 2.399963229728653; // the golden angle, or the start comes out in spokes
    const r = spread * Math.sqrt((i + 0.5) / n);
    b.x = Math.cos(a) * r;
    b.y = Math.sin(a) * r;
  });
  // a big model is not worth a long relax: it is past reading as one picture anyway
  const rounds = n > 160 ? 140 : 320;
  const vx = new Float64Array(n);
  const vy = new Float64Array(n);
  for (let round = 0; round < rounds; round++) {
    // the whole thing cools: big moves first, then only settling
    const alpha = Math.pow(1 - round / rounds, 1.2);
    for (const [i, j] of links) {
      const dx = boxes[j].x - boxes[i].x;
      const dy = boxes[j].y - boxes[i].y;
      const d = Math.hypot(dx, dy) || 1;
      const f = ((d - linkLength) / d) * alpha * 0.25;
      vx[i] += dx * f;
      vy[i] += dy * f;
      vx[j] -= dx * f;
      vy[j] -= dy * f;
    }
    for (let i = 0; i < n; i++) {
      for (let j = i + 1; j < n; j++) {
        let dx = boxes[j].x - boxes[i].x;
        let dy = boxes[j].y - boxes[i].y;
        let d2 = dx * dx + dy * dy;
        // far enough apart to ignore: the push is already down to nothing there
        if (d2 > 1600 * 1600) continue;
        if (d2 < 1) {
          // exactly on top of each other, and the push has no direction: give it one, the same one
          // every time this pair meets, so the layout stays repeatable
          dx = ((i * 7 + j * 13) % 11) / 11 - 0.5;
          dy = ((i * 11 + j * 3) % 11) / 11 - 0.5;
          d2 = dx * dx + dy * dy || 1;
        }
        const f = (boxCharge * alpha) / d2;
        vx[i] += dx * f;
        vy[i] += dy * f;
        vx[j] -= dx * f;
        vy[j] -= dy * f;
      }
    }
    for (let i = 0; i < n; i++) {
      // the pull to the middle, or anything joined to nothing drifts away for ever
      vx[i] -= boxes[i].x * centerPull * alpha;
      vy[i] -= boxes[i].y * centerPull * alpha;
      vx[i] *= 0.62;
      vy[i] *= 0.62;
      boxes[i].x += vx[i];
      boxes[i].y += vy[i];
    }
    // these are boxes, not dots: whatever the forces did, two of them may not cover each other
    for (let i = 0; i < n; i++) {
      for (let j = i + 1; j < n; j++) {
        const a = boxes[i];
        const b = boxes[j];
        const ox = (a.w + b.w) / 2 + boxPad - Math.abs(a.x + a.w / 2 - (b.x + b.w / 2));
        const oy = (a.h + b.h) / 2 + boxPad - Math.abs(a.y + a.h / 2 - (b.y + b.h / 2));
        if (ox <= 0 || oy <= 0) continue;
        // out the short way: the way apart that moves them least
        if (ox < oy) {
          const s = (a.x < b.x ? -1 : 1) * ox * 0.5;
          a.x += s;
          b.x -= s;
        } else {
          const s = (a.y < b.y ? -1 : 1) * oy * 0.5;
          a.y += s;
          b.y -= s;
        }
      }
    }
  }
  // every other arrangement starts at the origin, and the export and the fit read better from there
  const minX = Math.min(...boxes.map((b) => b.x));
  const minY = Math.min(...boxes.map((b) => b.y));
  for (const b of boxes) {
    b.x -= minX;
    b.y -= minY;
  }
}

/**
 * The model as boxes and lines. Boxes are types (header in the source's color, kind icon, the first
 * properties), lines are inheritance (dashed, hollow arrow at the parent), relations (solid, in the
 * relation color, labelled, arrowhead when directed), references (dotted) and embedded inner nodes
 * (solid, in the embedded color, with the filled diamond of containment at the type that owns the
 * property). A relation line selects its relation, a reference or embed line selects its property.
 * The layout is layered
 * by inheritance depth with parents above children and siblings ordered by their parents' position;
 * boxes can be dragged and stay where they were put (per database, in local storage) until "Auto
 * layout". Everything is plain SVG: no library, the model is small enough.
 */
export function DatamodelDiagram({ ctx, visibleTypes, ghostTypes, selection, query, storeId }: Props) {
  const [layout, setLayout] = useState<LayoutMode>(() => {
    const saved = localStorage.getItem(layoutKey(storeId));
    return layouts.some((l) => l.id === saved) ? (saved as LayoutMode) : "layers";
  });
  const [positions, setPositions] = useState(() => readPositions(storeId, layout));
  // types whose box shows every property rather than the first few
  const [openBoxes, setOpenBoxes] = useState<Set<string>>(new Set());
  const [view, setView] = useState<View>({ x: 20, y: 20, k: 1 });
  const [fitted, setFitted] = useState(false);
  const svgRef = useRef<SVGSVGElement>(null);
  const drag = useRef<{ kind: "pan"; sx: number; sy: number; ox: number; oy: number } | { kind: "node"; id: string; sx: number; sy: number; ox: number; oy: number; moved: boolean } | null>(null);
  const q = query.trim().toLowerCase();
  // where each box is drawn this frame, so the next rearrangement starts from where the eye left it
  const drawn = useRef(new Map<string, { x: number; y: number }>());
  // a rearrangement on its way: where the boxes set out from, and when
  const move = useRef<{ from: Map<string, { x: number; y: number }>; start: number } | null>(null);
  const viewMove = useRef<{ from: View; to: View; start: number } | null>(null);
  // counts rearrangements, so the view can be sent after one once the new places are known
  const [rearranged, setRearranged] = useState(0);
  const [, setFrame] = useState(0);
  const raf = useRef(0);
  const viewRef = useRef<HTMLDivElement>(null);
  const [fullscreen, setFullscreen] = useState(false);
  // the listeners below are hung once and live as long as the view, while what they call is this
  // render's: refs are how they reach it without being taken down and put up again every frame
  const liveView = useRef(view);
  liveView.current = view;
  const fitViewRef = useRef<() => View | null>(() => null);
  const runMoveRef = useRef<() => void>(() => {});

  const { boxes, edges } = useMemo(() => {
    const shown = new Set([...visibleTypes, ...ghostTypes]);
    const types = Object.values(ctx.model.NodeTypes).filter((t) => t.Id !== ctx.baseTypeId && shown.has(t.Id));
    const byId = new Map(types.map((t) => [t.Id, t]));
    // depth = longest chain of shown parents above
    const depth = new Map<string, number>();
    const depthOf = (t: NodeTypeJson, guard: number): number => {
      const known = depth.get(t.Id);
      if (known !== undefined) return known;
      if (guard > 40) return 0;
      const parents = (t.Parents ?? []).filter((p) => p !== ctx.baseTypeId && byId.has(p));
      const d = parents.length === 0 ? 0 : 1 + Math.max(...parents.map((p) => depthOf(byId.get(p)!, guard + 1)));
      depth.set(t.Id, d);
      return d;
    };
    for (const t of types) depthOf(t, 0);
    const boxes: Box[] = types.map((t) => {
      const props = Object.values(t.Properties).filter((p) => !p.Internal);
      const limit = openBoxes.has(t.Id) ? props.length : maxRows;
      const rows = props.slice(0, limit).map((p) => ({
        id: p.Id,
        name: p.CodeName,
        propertyType: p.PropertyType,
        marks: indexMarks.filter((m) => (m.key === "indexed" ? p.Indexed : m.key === "wordIndex" ? p.IndexedByWords : p.IndexedBySemantic)).map((m) => m.key),
      }));
      const more = props.length - rows.length;
      return { id: t.Id, type: t, x: 0, y: 0, w: nodeWidth, h: headerHeight + Math.max(1, rows.length + (more > 0 || openBoxes.has(t.Id) ? 1 : 0)) * rowHeight + 8, rows, more, ghost: ghostTypes.has(t.Id) && !visibleTypes.has(t.Id) };
    });
    const boxById = new Map(boxes.map((b) => [b.id, b]));
    place(layout, boxes, depth, ctx);
    // remembered positions win over the computed ones
    for (const b of boxes) {
      const p = positions[b.id];
      if (p) {
        b.x = p.x;
        b.y = p.y;
      }
    }
    // edges
    const edges: Edge[] = [];
    for (const b of boxes) {
      for (const p of b.type.Parents ?? []) if (boxById.has(p)) edges.push({ kind: "inherits", from: b.id, to: p, id: b.id + ">" + p });
      for (const p of Object.values(b.type.Properties)) {
        if ((p.PropertyType === "Reference" || p.PropertyType === "References") && p.NodeTypes) {
          for (const target of p.NodeTypes) if (boxById.has(target)) edges.push({ kind: "reference", from: b.id, to: target, id: p.Id + ">" + target, label: p.CodeName, propertyId: p.Id });
        }
        if (p.PropertyType === "Embedded" && p.InnerNodeTypes) {
          for (const target of p.InnerNodeTypes) if (boxById.has(target)) edges.push({ kind: "embeds", from: b.id, to: target, id: p.Id + ">" + target, label: p.CodeName, propertyId: p.Id });
        }
      }
    }
    for (const r of Object.values(ctx.model.Relations)) {
      const meta = relationMeta[r.RelationType];
      let n = 0;
      for (const s of r.SourceTypes) {
        for (const t of r.TargetTypes) {
          if (!boxById.has(s) || !boxById.has(t) || n++ > 12) continue;
          edges.push({ kind: "relation", from: s, to: t, id: r.Id + ":" + s + ">" + t, label: r.CodeName, directed: meta?.directed ?? true, symmetric: !(meta?.directed ?? true) });
        }
      }
    }
    return { boxes, edges };
  }, [ctx.model, visibleTypes, ghostTypes, positions, ctx.baseTypeId, layout, openBoxes]);

  // fit once the boxes exist, and again when the set of shown types changes a lot
  useEffect(() => {
    if (fitted || boxes.length === 0) return;
    fit();
    setFitted(true);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [boxes.length]);

  function bounds() {
    if (boxes.length === 0) return { x: 0, y: 0, w: 100, h: 100 };
    const minX = Math.min(...boxes.map((b) => b.x));
    const minY = Math.min(...boxes.map((b) => b.y));
    const maxX = Math.max(...boxes.map((b) => b.x + b.w));
    const maxY = Math.max(...boxes.map((b) => b.y + b.h));
    return { x: minX, y: minY, w: maxX - minX, h: maxY - minY };
  }
  /** Where the view has to sit for the whole model to fit, or null while there is nothing to fit. */
  function fitView(): View | null {
    const svg = svgRef.current;
    if (!svg) return null;
    const rect = svg.getBoundingClientRect();
    const b = bounds();
    const k = Math.min(1.25, Math.max(0.15, Math.min((rect.width - 40) / Math.max(1, b.w), (rect.height - 40) / Math.max(1, b.h))));
    return { k, x: (rect.width - b.w * k) / 2 - b.x * k, y: (rect.height - b.h * k) / 2 - b.y * k };
  }
  fitViewRef.current = fitView;

  function fit() {
    const v = fitView();
    if (!v) return;
    viewMove.current = null;
    setView(v);
  }

  /** Keeps the frames coming while the boxes are still travelling, and while the view follows them. */
  function runMove() {
    if (raf.current) return;
    const step = () => {
      raf.current = 0;
      const now = performance.now();
      let busy = false;
      if (move.current) {
        if (now - move.current.start >= moveMs) move.current = null;
        else busy = true;
      }
      const vm = viewMove.current;
      if (vm) {
        const p = Math.min(1, (now - vm.start) / moveMs);
        const e = ease(p);
        setView({ x: vm.from.x + (vm.to.x - vm.from.x) * e, y: vm.from.y + (vm.to.y - vm.from.y) * e, k: vm.from.k + (vm.to.k - vm.from.k) * e });
        if (p >= 1) viewMove.current = null;
        else busy = true;
      }
      setFrame((f) => f + 1);
      if (busy) raf.current = requestAnimationFrame(step);
    };
    raf.current = requestAnimationFrame(step);
  }
  runMoveRef.current = runMove;

  // the frame id is cleared with the frame: a stale one would make runMove believe a loop is running
  // (an effect cleanup runs on every hot reload in development, not only on unmount)
  useEffect(
    () => () => {
      cancelAnimationFrame(raf.current);
      raf.current = 0;
    },
    [],
  );

  /** Sets the boxes off from wherever they are drawn now toward wherever the new arrangement puts them. */
  function startMove() {
    move.current = { from: new Map(drawn.current), start: performance.now() };
    setRearranged((n) => n + 1);
    runMove();
  }

  // The view goes with them. One arrangement's extent is usually nothing like the next one's - a ring
  // is as wide as the layers are tall - so without this the boxes would glide off the edge of the
  // screen. It waits for the render that has the new places, since that is what it measures.
  useEffect(() => {
    if (rearranged === 0) return;
    const to = fitView();
    if (!to) return;
    viewMove.current = { from: view, to, start: performance.now() };
    runMove();
    // eslint-disable-next-line react-hooks/exhaustive-deps -- follows a rearrangement, not the view moving
  }, [rearranged]);

  // ---- the whole screen ----

  // the browser owns the fullscreen state - Escape and F11 change it without asking - so the button
  // follows the document rather than the other way round. Filling the screen gives the drawing far
  // more room than it had, so it is refitted into it, once the new size has actually been laid out:
  // the change fires before the layout, and one frame is not always enough to have it.
  useEffect(() => {
    const sync = () => {
      setFullscreen(document.fullscreenElement !== null && document.fullscreenElement === viewRef.current);
      requestAnimationFrame(() =>
        requestAnimationFrame(() => {
          const to = fitViewRef.current();
          if (!to) return;
          viewMove.current = { from: liveView.current, to, start: performance.now() };
          runMoveRef.current();
        }),
      );
    };
    document.addEventListener("fullscreenchange", sync);
    return () => document.removeEventListener("fullscreenchange", sync);
  }, []);

  /** Hands the view to the browser's fullscreen, toolbar and all, so the controls stay within reach. */
  function toggleFullscreen() {
    const el = viewRef.current;
    if (!el) return;
    if (document.fullscreenElement === el) {
      void document.exitFullscreen().catch(() => {});
      return;
    }
    // refused (a permissions policy, or no gesture behind this call): the view stays where it is
    void el
      .requestFullscreen?.()
      .then(() => svgRef.current?.focus({ preventScroll: true }))
      .catch(() => {});
  }
  function onKeyDown(e: React.KeyboardEvent) {
    // a keypress is a gesture the browser accepts fullscreen from
    if (e.code === "KeyF" && !e.ctrlKey && !e.metaKey && !e.altKey) {
      e.preventDefault();
      toggleFullscreen();
    }
  }

  function zoom(factor: number, cx?: number, cy?: number) {
    const svg = svgRef.current;
    if (!svg) return;
    viewMove.current = null;
    const rect = svg.getBoundingClientRect();
    const px = cx ?? rect.width / 2;
    const py = cy ?? rect.height / 2;
    setView((v) => {
      const k = Math.min(3, Math.max(0.1, v.k * factor));
      return { k, x: px - ((px - v.x) * k) / v.k, y: py - ((py - v.y) * k) / v.k };
    });
  }
  function onWheel(e: WheelEvent) {
    e.preventDefault();
    const rect = svgRef.current!.getBoundingClientRect();
    zoom(e.deltaY < 0 ? 1.12 : 1 / 1.12, e.clientX - rect.left, e.clientY - rect.top);
  }

  // The wheel is listened to natively. React registers its wheel listeners as passive, and a passive
  // listener may not call preventDefault: the browser refuses it and logs, the page scrolls under the
  // drawing, and zooming takes the diagram with it. The ref keeps the listener itself fixed while the
  // handler it calls is this render's, which is the one that can see the current view.
  const wheelHandler = useRef<(e: WheelEvent) => void>(() => {});
  wheelHandler.current = onWheel;
  useEffect(() => {
    const svg = svgRef.current;
    if (!svg) return;
    const wheel = (e: WheelEvent) => wheelHandler.current(e);
    svg.addEventListener("wheel", wheel, { passive: false });
    return () => svg.removeEventListener("wheel", wheel);
  }, []);
  function onPointerDown(e: React.PointerEvent) {
    if (e.button !== 0) return;
    // a hand on the drawing outranks a transition still playing under it
    viewMove.current = null;
    drag.current = { kind: "pan", sx: e.clientX, sy: e.clientY, ox: view.x, oy: view.y };
    (e.currentTarget as Element).setPointerCapture(e.pointerId);
  }
  function onNodePointerDown(e: React.PointerEvent, b: Box) {
    if (e.button !== 0) return;
    e.stopPropagation();
    move.current = null;
    viewMove.current = null;
    drag.current = { kind: "node", id: b.id, sx: e.clientX, sy: e.clientY, ox: b.x, oy: b.y, moved: false };
    svgRef.current!.setPointerCapture(e.pointerId);
  }
  function onPointerMove(e: React.PointerEvent) {
    const d = drag.current;
    if (!d) return;
    if (d.kind === "pan") setView((v) => ({ ...v, x: d.ox + (e.clientX - d.sx), y: d.oy + (e.clientY - d.sy) }));
    else {
      const dx = (e.clientX - d.sx) / view.k;
      const dy = (e.clientY - d.sy) / view.k;
      if (Math.abs(dx) + Math.abs(dy) > 2) d.moved = true;
      setPositions((prev) => ({ ...prev, [d.id]: { x: d.ox + dx, y: d.oy + dy } }));
    }
  }
  function onPointerUp() {
    const d = drag.current;
    drag.current = null;
    if (d?.kind === "node") {
      if (!d.moved) ctx.select({ kind: "type", id: d.id });
      else writePositions(storeId, layout, { ...positions, [d.id]: positions[d.id] ?? { x: d.ox, y: d.oy } });
    }
  }
  // the last drag's position is in state by now; persist whatever is there
  useEffect(() => {
    if (Object.keys(positions).length > 0) writePositions(storeId, layout, positions);
  }, [positions, storeId, layout]);

  /** Back to the computed places, forgetting what was dragged here - and the boxes walk back. */
  function autoLayout() {
    startMove();
    setPositions({});
    try {
      localStorage.removeItem(positionsKey(storeId, layout));
    } catch {
      // nothing to forget
    }
  }

  /** Another arrangement, with whatever was dragged in that one. The boxes travel to their new places. */
  function chooseLayout(mode: LayoutMode) {
    if (mode === layout) return;
    startMove();
    setLayout(mode);
    setPositions(readPositions(storeId, mode));
    try {
      localStorage.setItem(layoutKey(storeId), mode);
    } catch {
      // the choice then simply is not remembered
    }
  }

  function toggleBox(id: string) {
    setOpenBoxes((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  // The boxes as they are drawn this frame: part of the way from the last arrangement to this one, or
  // simply where they belong. Everything downstream reads these - the edges take their ends from the
  // same boxes, so a line travels with the two boxes it joins rather than snapping between them.
  const m = move.current;
  const t = m ? ease(Math.min(1, (performance.now() - m.start) / moveMs)) : 1;
  const placed =
    t >= 1
      ? boxes
      : boxes.map((b) => {
          const from = m?.from.get(b.id);
          // a type that was not on screen a moment ago has nowhere to come from: it starts where it belongs
          return from ? { ...b, x: from.x + (b.x - from.x) * t, y: from.y + (b.y - from.y) * t } : b;
        });
  drawn.current = new Map(placed.map((b) => [b.id, { x: b.x, y: b.y }]));

  const boxById = new Map(placed.map((b) => [b.id, b]));
  const selectedType = selection?.kind === "type" ? selection.id : selection?.kind === "property" ? selection.typeId : null;
  const selectedRelation = selection?.kind === "relation" ? selection.id : null;

  /** The point on the border of a box where a line to (tx, ty) leaves it. */
  function anchor(b: Box, tx: number, ty: number) {
    const cx = b.x + b.w / 2;
    const cy = b.y + b.h / 2;
    const dx = tx - cx;
    const dy = ty - cy;
    if (dx === 0 && dy === 0) return { x: cx, y: cy };
    const sx = dx === 0 ? Infinity : b.w / 2 / Math.abs(dx);
    const sy = dy === 0 ? Infinity : b.h / 2 / Math.abs(dy);
    const s = Math.min(sx, sy);
    return { x: cx + dx * s, y: cy + dy * s };
  }

  return (
    <div className="dm-diagram" ref={viewRef}>
      <div className="dm-diagram-tools">
        <button className="icon-button" title="Zoom in" onClick={() => zoom(1.25)}>
          <IconZoomIn size={16} stroke={1.9} />
        </button>
        <button className="icon-button" title="Zoom out" onClick={() => zoom(1 / 1.25)}>
          <IconZoomOut size={16} stroke={1.9} />
        </button>
        <button className="icon-button" title="Fit to view" onClick={fit}>
          <IconArrowsMaximize size={16} stroke={1.9} />
        </button>
        <button className="icon-button" title="Arrange again, forgetting what was dragged in this layout" onClick={autoLayout}>
          <IconArrowsShuffle size={16} stroke={1.9} />
        </button>
        <button
          className="icon-button"
          title="Save what is on screen as an SVG file, ready to print — fit to view first for the whole model"
          onClick={() => svgRef.current && downloadSvg(svgRef.current, "datamodel-diagram.svg", "Relatude.DB data model")}
        >
          <IconFileTypeSvg size={16} stroke={1.9} />
        </button>
        <FullscreenButton on={fullscreen} onToggle={toggleFullscreen} what="diagram" />
        {/* how the boxes are arranged; each layout keeps whatever was dragged in it */}
        <div className="dm-layout-picker" role="tablist">
          {layouts.map((l) => (
            <button key={l.id} role="tab" aria-selected={layout === l.id} className={layout === l.id ? "active" : ""} title={l.hint} onClick={() => chooseLayout(l.id)}>
              <l.icon size={14} stroke={1.9} />
              {l.label}
            </button>
          ))}
        </div>
        <span className="muted dm-diagram-legend">
          <span className="dm-legend-line inherits" /> inherits <span className="dm-legend-line relation" /> relation <span className="dm-legend-line reference" /> reference <span className="dm-legend-line embeds" /> embedded
        </span>
      </div>
      <svg ref={svgRef} className="dm-diagram-svg" tabIndex={0} onKeyDown={onKeyDown} onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerUp={onPointerUp} onPointerCancel={onPointerUp}>
        <defs>
          <marker id="dm-arrow-inherit" viewBox="0 0 12 12" refX="11" refY="6" markerWidth="12" markerHeight="12" orient="auto-start-reverse">
            <path d="M1 1 L11 6 L1 11 z" className="dm-marker-inherit" />
          </marker>
          <marker id="dm-arrow-relation" viewBox="0 0 12 12" refX="11" refY="6" markerWidth="9" markerHeight="9" orient="auto-start-reverse">
            <path d="M1 1 L11 6 L1 11 z" className="dm-marker-relation" />
          </marker>
          <marker id="dm-dot-relation" viewBox="0 0 12 12" refX="6" refY="6" markerWidth="7" markerHeight="7">
            <circle cx="6" cy="6" r="4" className="dm-marker-relation" />
          </marker>
          <marker id="dm-arrow-reference" viewBox="0 0 12 12" refX="11" refY="6" markerWidth="9" markerHeight="9" orient="auto-start-reverse">
            <path d="M1 1 L11 6 L1 11 z" className="dm-marker-reference" />
          </marker>
          <marker id="dm-arrow-embed" viewBox="0 0 12 12" refX="11" refY="6" markerWidth="9" markerHeight="9" orient="auto-start-reverse">
            <path d="M1 1 L11 6 L1 11 z" className="dm-marker-embed" />
          </marker>
          {/* the filled diamond sits at the owner end, as containment does in UML; the shape is symmetric so orient does not matter */}
          <marker id="dm-diamond-embed" viewBox="0 0 14 12" refX="0" refY="6" markerWidth="10" markerHeight="9" orient="auto">
            <path d="M0 6 L7 2 L14 6 L7 10 z" className="dm-marker-embed" />
          </marker>
        </defs>
        <g transform={`translate(${view.x} ${view.y}) scale(${view.k})`}>
          {edges.map((e) => {
            const a = boxById.get(e.from);
            const b = boxById.get(e.to);
            if (!a || !b) return null;
            const selfLoop = a.id === b.id;
            const ac = { x: a.x + a.w / 2, y: a.y + a.h / 2 };
            const bc = { x: b.x + b.w / 2, y: b.y + b.h / 2 };
            const p1 = selfLoop ? { x: a.x + a.w, y: a.y + 20 } : anchor(a, bc.x, bc.y);
            const p2 = selfLoop ? { x: a.x + a.w, y: a.y + a.h - 20 } : anchor(b, ac.x, ac.y);
            const highlighted = (selectedType !== null && (e.from === selectedType || e.to === selectedType)) || (e.kind === "relation" && selectedRelation !== null && e.id.startsWith(selectedRelation + ":"));
            const dim = (a.ghost && b.ghost) || (q && !(a.type.CodeName.toLowerCase().includes(q) || b.type.CodeName.toLowerCase().includes(q)));
            const cls = "dm-edge " + e.kind + (highlighted ? " highlighted" : "") + (dim ? " dim" : "");
            const d = selfLoop ? `M${p1.x} ${p1.y} C ${p1.x + 60} ${p1.y - 10}, ${p2.x + 60} ${p2.y + 10}, ${p2.x} ${p2.y}` : `M${p1.x} ${p1.y} L${p2.x} ${p2.y}`;
            const mid = { x: (p1.x + p2.x) / 2 + (selfLoop ? 45 : 0), y: (p1.y + p2.y) / 2 };
            const marker =
              e.kind === "inherits" ? "url(#dm-arrow-inherit)" : e.kind === "reference" ? "url(#dm-arrow-reference)" : e.kind === "embeds" ? "url(#dm-arrow-embed)" : e.directed ? "url(#dm-arrow-relation)" : "url(#dm-dot-relation)";
            const markerStart = e.kind === "embeds" ? "url(#dm-diamond-embed)" : e.kind === "relation" && e.symmetric ? "url(#dm-dot-relation)" : undefined;
            const select =
              e.kind === "relation"
                ? () => ctx.select({ kind: "relation", id: e.id.split(":")[0] })
                : e.kind === "reference" || e.kind === "embeds"
                  ? () => ctx.select({ kind: "property", id: e.propertyId, typeId: e.from })
                  : undefined;
            return (
              <g
                key={e.id}
                className={cls}
                // the pan on the svg captures the pointer, and a captured pointer sends the click to
                // the svg rather than here, so a clickable edge has to keep the pan from starting
                onPointerDown={select && ((ev) => ev.stopPropagation())}
                onClick={select}
              >
                <path d={d} className="dm-edge-hit" />
                <path d={d} className="dm-edge-line" markerEnd={marker} markerStart={markerStart} />
                {e.kind !== "inherits" && (
                  <text x={mid.x} y={mid.y - 4} className="dm-edge-label" textAnchor="middle">
                    {e.label}
                  </text>
                )}
              </g>
            );
          })}
          {placed.map((b) => {
            const color = ctx.colors.get(b.type.DatamodelSourceId) ?? "#888";
            const kind = kindMeta[b.type.ModelType] ?? kindMeta.Class;
            // the header holds the kind, an "inner" badge when the type only exists embedded, and
            // the name; the name takes what the other two leave, so it never runs into them
            const kindWidth = 12 + kind.label.length * 6.6;
            const badgeX = kindWidth + 8;
            const badgeWidth = b.type.IsInnerNode ? 36 : 0;
            const titleChars = Math.max(6, Math.floor((b.w - 10 - badgeX - badgeWidth - 8) / 7.2));
            const selected = selectedType === b.id;
            const match = q && (b.type.CodeName.toLowerCase().includes(q) || b.rows.some((r) => r.name.toLowerCase().includes(q)));
            const dim = q && !match;
            return (
              <g key={b.id} transform={`translate(${b.x} ${b.y})`} className={"dm-node" + (b.type.IsInnerNode ? " inner" : "") + (b.ghost ? " ghost" : "") + (selected ? " selected" : "") + (dim ? " dim" : "")} onPointerDown={(e) => onNodePointerDown(e, b)}>
                <rect width={b.w} height={b.h} rx={8} className="dm-node-body" />
                <path d={`M0 8 a8 8 0 0 1 8 -8 h${b.w - 16} a8 8 0 0 1 8 8 v${headerHeight - 8} h-${b.w} z`} fill={color} className="dm-node-head" />
                <text x={12} y={headerHeight / 2 + 4.5} className="dm-node-kind" fill="#fff" opacity={0.85}>
                  {kind.label}
                </text>
                {b.type.IsInnerNode && (
                  <g className="dm-node-badge">
                    <title>Only exists embedded inside another node</title>
                    <rect x={badgeX} y={6} width={badgeWidth} height={headerHeight - 12} rx={5} />
                    <text x={badgeX + badgeWidth / 2} y={headerHeight / 2 + 3.5} textAnchor="middle">
                      inner
                    </text>
                  </g>
                )}
                <text x={b.w - 10} y={headerHeight / 2 + 4.5} className="dm-node-title" textAnchor="end" fill="#fff">
                  {b.type.CodeName.length > titleChars ? b.type.CodeName.slice(0, titleChars - 1) + "…" : b.type.CodeName}
                </text>
                {b.rows.map((r, i) => {
                  const top = headerHeight + 4 + i * rowHeight;
                  const mid = top + rowHeight / 2;
                  // the index marks sit between the name and the type, and the name gives up the room
                  const marksWidth = r.marks.length * 11;
                  const nameRoom = Math.max(6, Math.floor((b.w - 34 - marksWidth - 62) / 6.3));
                  return (
                    <g
                      key={r.id}
                      className={"dm-node-row" + (selection?.kind === "property" && selection.id === r.id ? " selected" : "")}
                      onPointerDown={(e) => e.stopPropagation()}
                      onClick={() => ctx.select({ kind: "property", id: r.id, typeId: b.id })}
                    >
                      <rect x={4} y={top} width={b.w - 8} height={rowHeight} rx={3} className="dm-node-row-bg" />
                      <circle cx={14} cy={mid} r={3.5} fill={propertyColor(r.propertyType)} />
                      <text x={24} y={mid + 4} className="dm-node-prop">
                        {r.name.length > nameRoom ? r.name.slice(0, nameRoom - 1) + "…" : r.name}
                      </text>
                      {r.marks.map((key, m) => {
                        const mark = indexMarks.find((x) => x.key === key)!;
                        return (
                          <g key={key} transform={`translate(${26 + Math.min(r.name.length, nameRoom) * 6.3 + m * 11} ${mid - 5.5})`} className="dm-node-mark">
                            <title>{mark.title}</title>
                            <mark.icon size={11} stroke={2.4} color={mark.color} />
                          </g>
                        );
                      })}
                      <text x={b.w - 10} y={mid + 4} className="dm-node-proptype" textAnchor="end">
                        {r.propertyType}
                      </text>
                    </g>
                  );
                })}
                {(b.more > 0 || openBoxes.has(b.id)) && (
                  <g className="dm-node-row dm-node-more" onPointerDown={(e) => e.stopPropagation()} onClick={() => toggleBox(b.id)}>
                    <rect x={4} y={headerHeight + 4 + b.rows.length * rowHeight} width={b.w - 8} height={rowHeight} rx={3} className="dm-node-row-bg" />
                    <text x={24} y={headerHeight + 4 + b.rows.length * rowHeight + rowHeight / 2 + 4} className="dm-node-proptype">
                      {b.more > 0 ? `+${b.more} more — click to show` : "show fewer"}
                    </text>
                  </g>
                )}
                {b.rows.length === 0 && b.more === 0 && (
                  <text x={24} y={headerHeight + 4 + rowHeight / 2 + 4} className="dm-node-proptype">
                    no properties
                  </text>
                )}
              </g>
            );
          })}
        </g>
        {boxes.length === 0 && (
          <text x="50%" y="50%" textAnchor="middle" className="dm-node-proptype">
            No types to draw. Switch a source on.
          </text>
        )}
      </svg>
      <style>{`.dm-marker-relation{fill:${relationColor}}.dm-marker-embed{fill:${embeddedColor}}`}</style>
    </div>
  );
}
