import { useEffect, useMemo, useRef, useState } from "react";
import { IconArrowsMaximize, IconLayoutGrid, IconZoomIn, IconZoomOut } from "@tabler/icons-react";
import type { EditorContext, Selection } from "./DatamodelEditors";
import { embeddedColor, kindMeta, propertyColor, relationColor, relationMeta } from "./DatamodelIcons";
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
  rows: { id: string; name: string; propertyType: string }[];
  more: number;
  ghost: boolean;
}

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

function positionsKey(storeId: string) {
  return "dmDiagram:" + storeId;
}
function readPositions(storeId: string): Record<string, { x: number; y: number }> {
  try {
    const raw = localStorage.getItem(positionsKey(storeId));
    return raw ? (JSON.parse(raw) as Record<string, { x: number; y: number }>) : {};
  } catch {
    return {};
  }
}
function writePositions(storeId: string, positions: Record<string, { x: number; y: number }>) {
  try {
    localStorage.setItem(positionsKey(storeId), JSON.stringify(positions));
  } catch {
    // storage may be unavailable; the layout then simply is not remembered
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
  const [positions, setPositions] = useState(() => readPositions(storeId));
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
      const rows = props.slice(0, maxRows).map((p) => ({ id: p.Id, name: p.CodeName, propertyType: p.PropertyType }));
      return { id: t.Id, type: t, x: 0, y: 0, w: nodeWidth, h: headerHeight + Math.max(1, rows.length + (props.length > maxRows ? 1 : 0)) * rowHeight + 8, rows, more: props.length - rows.length, ghost: ghostTypes.has(t.Id) && !visibleTypes.has(t.Id) };
    });
    const boxById = new Map(boxes.map((b) => [b.id, b]));
    // layers
    const layers = new Map<number, Box[]>();
    for (const b of boxes) {
      const d = depth.get(b.id) ?? 0;
      layers.set(d, [...(layers.get(d) ?? []), b]);
    }
    const layerKeys = [...layers.keys()].sort((a, b) => a - b);
    // order the first layer by name, then every next layer by the mean x of its parents. A layer
    // wider than the budget wraps into rows, so a flat model (many roots, little inheritance) reads
    // as a grid rather than a line off the edge of the screen
    const rowWidthBudget = Math.max(3 * (nodeWidth + columnGap), Math.ceil(Math.sqrt(boxes.length)) * (nodeWidth + columnGap) * 1.3);
    let y = 0;
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
      const rows: Box[][] = [[]];
      let rowWidth = 0;
      for (const b of layer) {
        if (rowWidth > 0 && rowWidth + b.w > rowWidthBudget) {
          rows.push([]);
          rowWidth = 0;
        }
        rows[rows.length - 1].push(b);
        rowWidth += b.w + columnGap;
      }
      const widest = Math.max(...rows.map((r) => r.reduce((s, b) => s + b.w + columnGap, -columnGap)));
      for (const row of rows) {
        const width = row.reduce((s, b) => s + b.w + columnGap, -columnGap);
        let x = (widest - width) / 2;
        const rowHeight = Math.max(...row.map((b) => b.h));
        for (const b of row) {
          b.x = x;
          b.y = y;
          center.set(b.id, x + b.w / 2);
          x += b.w + columnGap;
        }
        y += rowHeight + layerGap / 2;
      }
      y += layerGap / 2;
    }
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
  }, [ctx.model, visibleTypes, ghostTypes, positions, ctx.baseTypeId]);

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
      else writePositions(storeId, { ...positions, [d.id]: positions[d.id] ?? { x: d.ox, y: d.oy } });
    }
  }
  // the last drag's position is in state by now; persist whatever is there
  useEffect(() => {
    if (Object.keys(positions).length > 0) writePositions(storeId, positions);
  }, [positions, storeId]);

  function autoLayout() {
    setPositions({});
    try {
      localStorage.removeItem(positionsKey(storeId));
    } catch {
      // nothing to forget
    }
    setFitted(false);
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
        <button className="icon-button" title="Auto layout (forgets dragged positions)" onClick={autoLayout}>
          <IconLayoutGrid size={16} stroke={1.9} />
        </button>
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
                {b.rows.map((r, i) => (
                  <g
                    key={r.id}
                    className={"dm-node-row" + (selection?.kind === "property" && selection.id === r.id ? " selected" : "")}
                    onPointerDown={(e) => e.stopPropagation()}
                    onClick={() => ctx.select({ kind: "property", id: r.id, typeId: b.id })}
                  >
                    <rect x={4} y={headerHeight + 4 + i * rowHeight} width={b.w - 8} height={rowHeight} rx={3} className="dm-node-row-bg" />
                    <circle cx={14} cy={headerHeight + 4 + i * rowHeight + rowHeight / 2} r={3.5} fill={propertyColor(r.propertyType)} />
                    <text x={24} y={headerHeight + 4 + i * rowHeight + rowHeight / 2 + 4} className="dm-node-prop">
                      {r.name.length > 22 ? r.name.slice(0, 21) + "…" : r.name}
                    </text>
                    <text x={b.w - 10} y={headerHeight + 4 + i * rowHeight + rowHeight / 2 + 4} className="dm-node-proptype" textAnchor="end">
                      {r.propertyType}
                    </text>
                  </g>
                ))}
                {b.more > 0 && (
                  <text x={24} y={headerHeight + 4 + b.rows.length * rowHeight + rowHeight / 2 + 4} className="dm-node-proptype">
                    +{b.more} more
                  </text>
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
