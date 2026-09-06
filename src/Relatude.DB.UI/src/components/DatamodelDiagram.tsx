import { useEffect, useMemo, useRef, useState } from "react";
import { IconArrowsMaximize, IconArrowsShuffle, IconCircleDotted, IconFileTypeSvg, IconLayoutColumns, IconLayoutGrid, IconLayoutRows, IconPalette, IconZoomIn, IconZoomOut } from "@tabler/icons-react";
import { downloadSvg } from "../svgExport";
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
export type LayoutMode = "layers" | "columns" | "grid" | "sources" | "circle";

const layouts: { id: LayoutMode; label: string; hint: string; icon: typeof IconLayoutRows }[] = [
  { id: "layers", label: "Layers", hint: "Inheritance top down: parents above their children", icon: IconLayoutRows },
  { id: "columns", label: "Columns", hint: "Inheritance left to right: parents left of their children", icon: IconLayoutColumns },
  { id: "grid", label: "Grid", hint: "Every type in one grid, by name", icon: IconLayoutGrid },
  { id: "sources", label: "Sources", hint: "One block per model source, in load order", icon: IconPalette },
  { id: "circle", label: "Circle", hint: "Types on a ring, so the lines between them read", icon: IconCircleDotted },
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
  const [view, setView] = useState({ x: 20, y: 20, k: 1 });
  const [fitted, setFitted] = useState(false);
  const svgRef = useRef<SVGSVGElement>(null);
  const drag = useRef<{ kind: "pan"; sx: number; sy: number; ox: number; oy: number } | { kind: "node"; id: string; sx: number; sy: number; ox: number; oy: number; moved: boolean } | null>(null);
  const q = query.trim().toLowerCase();

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
  function fit() {
    const svg = svgRef.current;
    if (!svg) return;
    const rect = svg.getBoundingClientRect();
    const b = bounds();
    const k = Math.min(1.25, Math.max(0.15, Math.min((rect.width - 40) / Math.max(1, b.w), (rect.height - 40) / Math.max(1, b.h))));
    setView({ k, x: (rect.width - b.w * k) / 2 - b.x * k, y: (rect.height - b.h * k) / 2 - b.y * k });
  }
  function zoom(factor: number, cx?: number, cy?: number) {
    const svg = svgRef.current;
    if (!svg) return;
    const rect = svg.getBoundingClientRect();
    const px = cx ?? rect.width / 2;
    const py = cy ?? rect.height / 2;
    setView((v) => {
      const k = Math.min(3, Math.max(0.1, v.k * factor));
      return { k, x: px - ((px - v.x) * k) / v.k, y: py - ((py - v.y) * k) / v.k };
    });
  }
  function onWheel(e: React.WheelEvent) {
    e.preventDefault();
    const rect = svgRef.current!.getBoundingClientRect();
    zoom(e.deltaY < 0 ? 1.12 : 1 / 1.12, e.clientX - rect.left, e.clientY - rect.top);
  }
  function onPointerDown(e: React.PointerEvent) {
    if (e.button !== 0) return;
    drag.current = { kind: "pan", sx: e.clientX, sy: e.clientY, ox: view.x, oy: view.y };
    (e.currentTarget as Element).setPointerCapture(e.pointerId);
  }
  function onNodePointerDown(e: React.PointerEvent, b: Box) {
    if (e.button !== 0) return;
    e.stopPropagation();
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

  function autoLayout() {
    setPositions({});
    try {
      localStorage.removeItem(positionsKey(storeId, layout));
    } catch {
      // nothing to forget
    }
    setFitted(false);
  }

  /** Another arrangement, with whatever was dragged in that one; the view refits to what it shows. */
  function chooseLayout(mode: LayoutMode) {
    setLayout(mode);
    setPositions(readPositions(storeId, mode));
    setFitted(false);
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

  const boxById = new Map(boxes.map((b) => [b.id, b]));
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
    <div className="dm-diagram">
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
      <svg ref={svgRef} className="dm-diagram-svg" onWheel={onWheel} onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerUp={onPointerUp} onPointerCancel={onPointerUp}>
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
          {boxes.map((b) => {
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
