import { useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { IconArrowBackUp, IconArrowsMaximize, IconArrowsShuffle, IconContrast, IconCube3dSphere, IconFileTypeSvg, IconFocusCentered, IconHierarchy3, IconMaximize, IconMinimize, IconTopologyStar3, IconTypography, IconZoomIn, IconZoomOut } from "@tabler/icons-react";
import type { EditorContext, Selection } from "./DatamodelEditors";
import type { GraphShell } from "./DatamodelGraphView";
import { embeddedColor, kindMeta, propertyColor, relationColor } from "./DatamodelIcons";
import { fullName, type NodeTypeJson } from "../server/datamodel";
import { formatCount } from "../format";
import { downloadSvg } from "../svgExport";
import { buildWorld, edgeKinds, edgesKey, expandedKey, readEdges, readExpanded, readRoot, remember, rootKey, unfold, type EdgeKind, type GraphLink, type GraphMode, type GraphNode } from "./datamodelGraphModel";

interface Props {
  ctx: EditorContext;
  visibleTypes: Set<string>;
  selection: Selection | null;
  query: string;
  storeId: string;
  /** what the flat and the spatial graph share: the mode switch, the names switch, fullscreen */
  shell: GraphShell;
}

/** A node as the simulation moves it. Pinned nodes (fx, fy) stay where they are put. */
interface SimNode {
  id: string;
  kind: "type" | "property";
  x: number;
  y: number;
  vx: number;
  vy: number;
  r: number;
  charge: number;
  fx: number | null;
  fy: number | null;
}

interface SimLink {
  a: string;
  b: string;
  length: number;
  strength: number;
}

interface Sim {
  nodes: Map<string, SimNode>;
  links: SimLink[];
  alpha: number;
  alphaTarget: number;
}

interface View {
  x: number;
  y: number;
  k: number;
}

// the simulation's constants, in the spirit of d3-force: a spring per link, a charge per node, a
// weak pull to the middle, and a decay that lets it all settle in a few seconds
const alphaDecay = 0.024;
const alphaMin = 0.004;
const velocityDecay = 0.62;
const typeLinkLength = 140;
const leafLinkLength = 52;
const typeCharge = -1100;
const leafCharge = -140;

/**
 * The model as a living graph. It starts with a choice: one type, picked from all of them, becomes
 * the single node on screen. A click on a type unfolds what is connected to it - the types it
 * inherits from and the ones that inherit from it, the types it relates to, references and embeds,
 * and its own properties as leaves - and a second click folds them away again, except what some
 * other unfolded type still holds on to. Each kind of line can be switched off, and with it the
 * types it would have led to, so a relation-heavy model can be read by its relations alone.
 *
 * Everything shown floats in a force layout: links are springs, nodes repel, the whole thing drifts
 * toward the middle and settles, and a node can be dragged wherever it reads best, where it then
 * stays. The view pans and zooms, can fill the screen, and what is on it can be saved as an SVG file
 * that stands on its own and prints.
 *
 * The lines mean what they mean in the diagram: dashed for inheritance (arrow at the parent), the
 * relation colour for relations, dotted in the accent for references, the embedded colour for
 * inner nodes, and a thin grey stem from a type to each of its properties.
 */
export function DatamodelGraph({ ctx, visibleTypes, selection, query, storeId, shell }: Props) {
  const baseId = ctx.baseTypeId;
  const [root, setRoot] = useState<string | null>(() => readRoot(storeId));
  const [expanded, setExpanded] = useState<Set<string>>(() => readExpanded(storeId));
  const [edges, setEdges] = useState<Set<EdgeKind>>(() => readEdges(storeId));
  const [view, setView] = useState<View>({ x: 0, y: 0, k: 1 });
  const [hover, setHover] = useState<string | null>(null);
  const [, setFrame] = useState(0);
  const svgRef = useRef<SVGSVGElement>(null);
  const sim = useRef<Sim>({ nodes: new Map(), links: [], alpha: 0, alphaTarget: 0 });
  const raf = useRef(0);
  const tween = useRef<{ from: View; to: View; start: number; ms: number } | null>(null);
  const drag = useRef<{ kind: "pan"; sx: number; sy: number; ox: number; oy: number } | { kind: "node"; id: string; sx: number; sy: number; ox: number; oy: number; moved: boolean } | null>(null);
  const q = query.trim().toLowerCase();

  useEffect(() => remember(rootKey(storeId), root), [storeId, root]);
  useEffect(() => remember(expandedKey(storeId), [...expanded]), [storeId, expanded]);
  useEffect(() => remember(edgesKey(storeId), [...edges]), [storeId, edges]);

  // ---- the whole model as a graph, independent of what is unfolded ----

  const world = useMemo(() => buildWorld(ctx, visibleTypes, edges), [ctx, visibleTypes, edges]);

  // the start type has to be one that is still there and still shown; otherwise it is picked again
  const rootId = root !== null && world.eligible.has(root) ? root : null;

  // ---- what is unfolded right now ----

  const { nodes, links } = useMemo(() => unfold(world, ctx, expanded, rootId), [world, ctx, expanded, rootId]);

  // ---- the simulation follows the graph ----

  // another start type is another picture: nothing carries over from the last one
  useLayoutEffect(() => {
    sim.current.nodes.clear();
    sim.current.links = [];
  }, [rootId]);

  useLayoutEffect(() => {
    const s = sim.current;
    const wanted = new Set(nodes.map((n) => n.id));
    for (const id of [...s.nodes.keys()]) if (!wanted.has(id)) s.nodes.delete(id);
    let changed = false;
    for (const n of nodes) {
      const existing = s.nodes.get(n.id);
      if (existing) {
        existing.r = n.r;
        continue;
      }
      changed = true;
      // a new node starts where it came from - the type that was unfolded, the owner of a property -
      // and the springs carry it out from there, which is what makes the unfolding read as motion
      const sponsor =
        n.kind === "property"
          ? s.nodes.get(n.ownerId)
          : [...(world.neighbors.get(n.id) ?? [])].map((id) => s.nodes.get(id)).find((m) => m !== undefined && expanded.has(m.id));
      const angle = Math.random() * Math.PI * 2;
      const dist = n.kind === "property" ? 12 : 24;
      const x = sponsor ? sponsor.x + Math.cos(angle) * dist : (Math.random() - 0.5) * 40;
      const y = sponsor ? sponsor.y + Math.sin(angle) * dist : (Math.random() - 0.5) * 40;
      const isRoot = n.kind === "type" && n.root;
      s.nodes.set(n.id, { id: n.id, kind: n.kind, x: isRoot ? 0 : x, y: isRoot ? 0 : y, vx: 0, vy: 0, r: n.r, charge: n.kind === "type" ? typeCharge : leafCharge, fx: isRoot ? 0 : null, fy: isRoot ? 0 : null });
    }
    s.links = links.map((l) => ({ a: l.from, b: l.to, length: l.kind === "property" ? leafLinkLength : typeLinkLength, strength: l.kind === "property" ? 0.9 : 0.45 }));
    if (changed || s.nodes.size !== nodes.length) s.alpha = Math.max(s.alpha, 0.7);
    if (s.alpha > alphaMin) run();
    setFrame((f) => f + 1);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the sim syncs to the graph, not to the helpers
  }, [nodes, links]);

  // the middle of the drawing is the middle of the screen until someone pans - measured whenever the
  // drawing (re)appears, since the picker has no svg to measure
  useLayoutEffect(() => {
    const rect = svgRef.current?.getBoundingClientRect();
    if (rect) setView({ x: rect.width / 2, y: rect.height / 2, k: 1 });
  }, [rootId]);

  // the frame id is cleared with the frame: a stale id would make run() believe a loop is still going
  // (an effect cleanup runs on every hot reload in development, not only on unmount)
  useEffect(
    () => () => {
      cancelAnimationFrame(raf.current);
      raf.current = 0;
    },
    [],
  );

  // The wheel is listened to natively. React registers its wheel listeners as passive, and a passive
  // listener may not call preventDefault: the browser refuses it and logs, and the page scrolls under
  // the graph while it zooms. The listener stays put; the handler it calls is this render's. It is
  // hung on the drawing, which only exists once a start type is picked.
  const wheelHandler = useRef<(e: WheelEvent) => void>(() => {});
  wheelHandler.current = onWheel;
  useEffect(() => {
    const svg = svgRef.current;
    if (!svg) return;
    const wheel = (e: WheelEvent) => wheelHandler.current(e);
    svg.addEventListener("wheel", wheel, { passive: false });
    return () => svg.removeEventListener("wheel", wheel);
  }, [rootId]);

  /** Keeps the frames coming while there is motion left: the simulation, a drag, or a view tween. */
  function run() {
    if (raf.current) return;
    const step = (now: number) => {
      raf.current = 0;
      const s = sim.current;
      let busy = false;
      if (s.alpha > alphaMin || drag.current?.kind === "node") {
        tick(s);
        busy = true;
      }
      const t = tween.current;
      if (t) {
        const p = Math.min(1, (now - t.start) / t.ms);
        const e = 1 - Math.pow(1 - p, 3);
        setView({ x: t.from.x + (t.to.x - t.from.x) * e, y: t.from.y + (t.to.y - t.from.y) * e, k: t.from.k + (t.to.k - t.from.k) * e });
        if (p >= 1) tween.current = null;
        else busy = true;
      }
      setFrame((f) => f + 1);
      if (busy) raf.current = requestAnimationFrame(step);
    };
    raf.current = requestAnimationFrame(step);
  }

  function reheat(alpha = 0.6) {
    sim.current.alpha = Math.max(sim.current.alpha, alpha);
    run();
  }

  // ---- unfolding ----

  function start(id: string) {
    setExpanded(new Set());
    setRoot(id);
  }
  function startOver() {
    // the picker has no toolbar, so nothing there could bring the screen back: step out first
    shell.exitFullscreen();
    setRoot(null);
    setExpanded(new Set());
  }
  function toggle(id: string) {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }
  function toggleEdge(kind: EdgeKind) {
    setEdges((prev) => {
      const next = new Set(prev);
      if (next.has(kind)) next.delete(kind);
      else next.add(kind);
      return next;
    });
  }
  function expandAll() {
    setExpanded(new Set(world.eligible));
  }
  function collapseAll() {
    setExpanded(new Set());
  }
  /** Lets go of every dragged (pinned) node and stirs the layout, so it can find a better rest. */
  function shake() {
    for (const n of sim.current.nodes.values()) {
      if (n.id === rootId) continue;
      n.fx = null;
      n.fy = null;
      n.vx += (Math.random() - 0.5) * 30;
      n.vy += (Math.random() - 0.5) * 30;
    }
    reheat(0.9);
  }

  // ---- the view ----

  function bounds() {
    const ns = [...sim.current.nodes.values()];
    if (ns.length === 0) return { x: -50, y: -50, w: 100, h: 100 };
    const minX = Math.min(...ns.map((n) => n.x - n.r - 10));
    const maxX = Math.max(...ns.map((n) => n.x + n.r + (n.kind === "property" ? 90 : 30)));
    const minY = Math.min(...ns.map((n) => n.y - n.r - 10));
    const maxY = Math.max(...ns.map((n) => n.y + n.r + 24));
    return { x: minX, y: minY, w: maxX - minX, h: maxY - minY };
  }
  function animateTo(to: View) {
    tween.current = { from: view, to, start: performance.now(), ms: 320 };
    run();
  }
  function fit() {
    const svg = svgRef.current;
    if (!svg) return;
    const rect = svg.getBoundingClientRect();
    const b = bounds();
    const k = Math.min(1.6, Math.max(0.15, Math.min((rect.width - 40) / Math.max(1, b.w), (rect.height - 40) / Math.max(1, b.h))));
    animateTo({ k, x: (rect.width - b.w * k) / 2 - b.x * k, y: (rect.height - b.h * k) / 2 - b.y * k });
  }
  function zoomBy(factor: number, cx?: number, cy?: number, instant = false) {
    const svg = svgRef.current;
    if (!svg) return;
    const rect = svg.getBoundingClientRect();
    const px = cx ?? rect.width / 2;
    const py = cy ?? rect.height / 2;
    const k = Math.min(4, Math.max(0.08, view.k * factor));
    const next = { k, x: px - ((px - view.x) * k) / view.k, y: py - ((py - view.y) * k) / view.k };
    if (instant) {
      tween.current = null;
      setView(next);
    } else animateTo(next);
  }
  function onWheel(e: WheelEvent) {
    e.preventDefault();
    const rect = svgRef.current!.getBoundingClientRect();
    zoomBy(e.deltaY < 0 ? 1.1 : 1 / 1.1, e.clientX - rect.left, e.clientY - rect.top, true);
  }
  function onKeyDown(e: React.KeyboardEvent) {
    // a keypress is a gesture the browser accepts fullscreen from
    if (e.code === "KeyF" && !e.ctrlKey && !e.metaKey && !e.altKey) {
      e.preventDefault();
      void shell.toggleFullscreen();
    }
  }

  // ---- the pointer: pan on the background, drag a node, click to unfold or select ----

  function onPointerDown(e: React.PointerEvent) {
    if (e.button !== 0) return;
    tween.current = null;
    drag.current = { kind: "pan", sx: e.clientX, sy: e.clientY, ox: view.x, oy: view.y };
    (e.currentTarget as Element).setPointerCapture(e.pointerId);
  }
  function onNodePointerDown(e: React.PointerEvent, id: string) {
    if (e.button !== 0) return;
    e.stopPropagation();
    const n = sim.current.nodes.get(id);
    if (!n) return;
    drag.current = { kind: "node", id, sx: e.clientX, sy: e.clientY, ox: n.x, oy: n.y, moved: false };
    svgRef.current!.setPointerCapture(e.pointerId);
  }
  function onPointerMove(e: React.PointerEvent) {
    const d = drag.current;
    if (!d) return;
    if (d.kind === "pan") {
      setView((v) => ({ ...v, x: d.ox + (e.clientX - d.sx), y: d.oy + (e.clientY - d.sy) }));
      return;
    }
    const dx = (e.clientX - d.sx) / view.k;
    const dy = (e.clientY - d.sy) / view.k;
    if (Math.abs(dx) + Math.abs(dy) > 3) d.moved = true;
    const n = sim.current.nodes.get(d.id);
    if (!n || !d.moved) return;
    // held where the pointer is; the rest of the graph keeps moving around it while it is held
    n.fx = d.ox + dx;
    n.fy = d.oy + dy;
    sim.current.alphaTarget = 0.3;
    if (sim.current.alpha < 0.3) sim.current.alpha = 0.3;
    run();
  }
  function onPointerUp() {
    const d = drag.current;
    drag.current = null;
    if (d?.kind !== "node") return;
    const n = sim.current.nodes.get(d.id);
    sim.current.alphaTarget = 0;
    if (d.moved) {
      // let go where it was put: a dragged node stays pinned there, or the springs would carry it
      // straight back to where it came from. The shake button releases every pinned node.
      if (n) {
        n.vx = 0;
        n.vy = 0;
      }
      run();
      return;
    }
    // a click on the node itself selects it; unfolding is the badge's job, so reading a type in the
    // editor does not rearrange the graph
    const node = nodes.find((x) => x.id === d.id);
    if (!node) return;
    if (node.kind === "type") ctx.select({ kind: "type", id: node.id });
    else ctx.select({ kind: "property", id: node.property.Id, typeId: node.ownerId });
  }

  // ---- the file ----

  function exportSvg() {
    if (svgRef.current) downloadSvg(svgRef.current, "datamodel-graph.svg", "Relatude.DB data model graph");
  }

  // ---- drawing ----

  const modeSwitch = <GraphModeSwitch mode={shell.mode} onMode={shell.setMode} />;

  if (rootId === null) return <TypePicker ctx={ctx} eligible={world.eligible} query={q} onPick={start} tools={modeSwitch} />;

  const positions = sim.current.nodes;
  const selectedType = selection?.kind === "type" ? selection.id : selection?.kind === "property" ? selection.typeId : null;
  const selectedProperty = selection?.kind === "property" ? selection.id : null;
  const selectedRelation = selection?.kind === "relation" ? selection.id : null;
  // names go when switched off, and otherwise when the graph is too far out for them to be read
  const showLabels = shell.names && view.k > 0.45;
  const showLeafLabels = shell.names && view.k > 0.7;
  const matches = (n: GraphNode) => !q || (n.kind === "type" ? n.type.CodeName.toLowerCase().includes(q) : n.property.CodeName.toLowerCase().includes(q));
  const touching = (l: GraphLink) => hover !== null && (l.from === hover || l.to === hover);
  const rootType = ctx.model.NodeTypes[rootId];

  return (
    <>
      <div className="dm-diagram-tools">
        {modeSwitch}
        <button className="icon-button" title="Start over from another type" onClick={startOver}>
          <IconArrowBackUp size={16} stroke={1.9} />
        </button>
        <span className="dg-start" title={rootType ? fullName(rootType) : undefined}>
          {rootType?.CodeName ?? "?"}
        </span>
        <span className="dm-tools-gap" />
        <button className="icon-button" title="Zoom in" onClick={() => zoomBy(1.25)}>
          <IconZoomIn size={16} stroke={1.9} />
        </button>
        <button className="icon-button" title="Zoom out" onClick={() => zoomBy(1 / 1.25)}>
          <IconZoomOut size={16} stroke={1.9} />
        </button>
        <button className="icon-button" title="Fit everything into view" onClick={fit}>
          <IconArrowsMaximize size={16} stroke={1.9} />
        </button>
        <button className="icon-button" title="Stir the layout and let it settle again" onClick={shake}>
          <IconArrowsShuffle size={16} stroke={1.9} />
        </button>
        <span className="dm-tools-gap" />
        <button className="icon-button" title="Unfold every type" onClick={expandAll} disabled={expanded.size >= world.eligible.size}>
          <IconHierarchy3 size={16} stroke={1.9} />
        </button>
        <button className="icon-button" title="Fold everything back to the start type" onClick={collapseAll} disabled={expanded.size === 0}>
          <IconFocusCentered size={16} stroke={1.9} />
        </button>
        <span className="dm-tools-gap" />
        <NamesButton on={shell.names} onToggle={shell.toggleNames} />
        <BareButton on={shell.bare} onToggle={shell.toggleBare} />
        <button className="icon-button" title="Save what is on screen as an SVG file, ready to print" onClick={exportSvg}>
          <IconFileTypeSvg size={16} stroke={1.9} />
        </button>
        <FullscreenButton on={shell.fullscreen} onToggle={() => void shell.toggleFullscreen()} />
        {/* the legend is the switchboard: each kind of line can be turned off, and with it the types it leads to */}
        <span className="dg-edge-toggles" role="group" aria-label="Kinds of line to show">
          {edgeKinds.map((e) => (
            <button key={e.kind} className={"dg-edge-toggle" + (edges.has(e.kind) ? " on" : "")} aria-pressed={edges.has(e.kind)} title={e.hint + (edges.has(e.kind) ? " — click to hide" : " — click to show")} onClick={() => toggleEdge(e.kind)}>
              <span className={"dm-legend-line " + e.kind} /> {e.label}
            </button>
          ))}
        </span>
        <span className="muted dm-diagram-legend dg-hint">+ unfolds · − folds · click a type to read it · drag to move</span>
      </div>
      <svg
        ref={svgRef}
        className="dm-diagram-svg dg-svg"
        tabIndex={0}
        onKeyDown={onKeyDown}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={onPointerUp}
        onDoubleClick={(e) => {
          if (e.target === e.currentTarget) fit();
        }}
      >
        <defs>
          {/* the inheritance head is drawn hollow and sits on every type, so it is kept the size of the others */}
          <marker id="dg-arrow-inherit" viewBox="0 0 12 12" refX="11" refY="6" markerWidth="9" markerHeight="9" orient="auto-start-reverse">
            <path d="M1 1 L11 6 L1 11 z" className="dm-marker-inherit" />
          </marker>
          <marker id="dg-arrow-relation" viewBox="0 0 12 12" refX="11" refY="6" markerWidth="9" markerHeight="9" orient="auto-start-reverse">
            <path d="M1 1 L11 6 L1 11 z" fill={relationColor} />
          </marker>
          <marker id="dg-dot-relation" viewBox="0 0 12 12" refX="6" refY="6" markerWidth="7" markerHeight="7">
            <circle cx="6" cy="6" r="4" fill={relationColor} />
          </marker>
          <marker id="dg-arrow-reference" viewBox="0 0 12 12" refX="11" refY="6" markerWidth="9" markerHeight="9" orient="auto-start-reverse">
            <path d="M1 1 L11 6 L1 11 z" className="dm-marker-reference" />
          </marker>
          <marker id="dg-arrow-embed" viewBox="0 0 12 12" refX="11" refY="6" markerWidth="9" markerHeight="9" orient="auto-start-reverse">
            <path d="M1 1 L11 6 L1 11 z" fill={embeddedColor} />
          </marker>
          <marker id="dg-diamond-embed" viewBox="0 0 14 12" refX="0" refY="6" markerWidth="10" markerHeight="9" orient="auto">
            <path d="M0 6 L7 2 L14 6 L7 10 z" fill={embeddedColor} />
          </marker>
        </defs>
        <g transform={`translate(${view.x} ${view.y}) scale(${view.k})`}>
          {links.map((l) => {
            const a = positions.get(l.from);
            const b = positions.get(l.to);
            if (!a || !b) return null;
            const selfLoop = a === b;
            let d: string;
            let mid: { x: number; y: number };
            if (selfLoop) {
              d = `M${a.x + a.r} ${a.y - 6} C ${a.x + a.r + 50} ${a.y - 40}, ${a.x + a.r + 50} ${a.y + 40}, ${a.x + a.r} ${a.y + 6}`;
              mid = { x: a.x + a.r + 40, y: a.y - 4 };
            } else {
              const dx = b.x - a.x;
              const dy = b.y - a.y;
              const len = Math.sqrt(dx * dx + dy * dy) || 1;
              const ux = dx / len;
              const uy = dy / len;
              // from border to border, with room for the arrowhead at the far end
              const tail = l.kind === "property" ? 1 : 3;
              const p1 = { x: a.x + ux * (a.r + 2), y: a.y + uy * (a.r + 2) };
              const p2 = { x: b.x - ux * (b.r + tail), y: b.y - uy * (b.r + tail) };
              d = `M${p1.x} ${p1.y} L${p2.x} ${p2.y}`;
              mid = { x: (p1.x + p2.x) / 2, y: (p1.y + p2.y) / 2 };
            }
            const highlighted =
              touching(l) ||
              (selectedType !== null && (l.from === selectedType || l.to === selectedType) && l.kind !== "property") ||
              (l.kind === "relation" && selectedRelation === l.relationId) ||
              (l.kind === "property" && selectedProperty === l.propertyId);
            const markerEnd =
              l.kind === "inherits"
                ? "url(#dg-arrow-inherit)"
                : l.kind === "reference"
                  ? "url(#dg-arrow-reference)"
                  : l.kind === "embeds"
                    ? "url(#dg-arrow-embed)"
                    : l.kind === "relation"
                      ? l.directed
                        ? "url(#dg-arrow-relation)"
                        : "url(#dg-dot-relation)"
                      : undefined;
            const markerStart = l.kind === "embeds" ? "url(#dg-diamond-embed)" : l.kind === "relation" && !l.directed ? "url(#dg-dot-relation)" : undefined;
            const select =
              l.kind === "relation"
                ? () => ctx.select({ kind: "relation", id: l.relationId })
                : l.kind === "reference" || l.kind === "embeds"
                  ? () => ctx.select({ kind: "property", id: l.propertyId, typeId: l.from })
                  : undefined;
            const label = l.kind === "relation" || l.kind === "reference" || l.kind === "embeds" ? l.label : null;
            return (
              <g key={l.id} className={"dm-edge " + l.kind + (highlighted ? " highlighted" : "")} onPointerDown={select && ((ev) => ev.stopPropagation())} onClick={select}>
                {select && <path d={d} className="dm-edge-hit" />}
                <path d={d} className="dm-edge-line" markerEnd={markerEnd} markerStart={markerStart} />
                {label && showLabels && (
                  <text x={mid.x} y={mid.y - 4} className="dm-edge-label" textAnchor="middle">
                    {label}
                  </text>
                )}
              </g>
            );
          })}
          {nodes.map((n) => {
            const p = positions.get(n.id);
            if (!p) return null;
            const dim = !matches(n);
            if (n.kind === "property") {
              return (
                <g
                  key={n.id}
                  className={"dg-node leaf appear" + (selectedProperty === n.property.Id ? " selected" : "") + (dim ? " dim" : "")}
                  transform={`translate(${p.x} ${p.y})`}
                  onPointerDown={(e) => onNodePointerDown(e, n.id)}
                  onPointerEnter={() => setHover(n.id)}
                  onPointerLeave={() => setHover((h) => (h === n.id ? null : h))}
                >
                  <title>{`${n.property.CodeName}: ${n.property.PropertyType}`}</title>
                  <circle r={n.r} fill={propertyColor(n.property.PropertyType)} className="dg-body" />
                  {showLeafLabels && (
                    <text x={n.r + 4} y={3.5} className="dg-leaf-label">
                      {n.property.CodeName}
                    </text>
                  )}
                </g>
              );
            }
            const color = ctx.colors.get(n.type.DatamodelSourceId) ?? "#8a8781";
            const kind = kindMeta[n.type.ModelType] ?? kindMeta.Class;
            const iconSize = Math.round(n.r * 1.05);
            return (
              <g
                key={n.id}
                className={"dg-node type appear" + (n.root ? " root" : "") + (n.open ? " open" : "") + (n.type.IsInnerNode ? " inner" : "") + (selectedType === n.id ? " selected" : "") + (dim ? " dim" : "")}
                transform={`translate(${p.x} ${p.y})`}
                onPointerDown={(e) => onNodePointerDown(e, n.id)}
                onPointerEnter={() => setHover(n.id)}
                onPointerLeave={() => setHover((h) => (h === n.id ? null : h))}
              >
                <title>{`${n.type.CodeName} — ${kind.label}${n.hidden > 0 ? ` · ${n.hidden} more to unfold` : ""}`}</title>
                {(n.root || n.open) && <circle r={n.r + 5} className="dg-ring" stroke={n.root ? undefined : color} />}
                <circle r={n.r} fill={color} className="dg-body" />
                <g transform={`translate(${-iconSize / 2} ${-iconSize / 2})`} className="dg-icon">
                  <kind.icon size={iconSize} stroke={1.7} color="#fff" />
                </g>
                {showLabels && (
                  <text y={n.r + 14} textAnchor="middle" className="dg-label">
                    {n.type.CodeName}
                  </text>
                )}
                {showLabels && n.root && (
                  <text y={n.r + 26} textAnchor="middle" className="dg-sub">
                    {n.id === baseId ? "base type" : "start type"}
                  </text>
                )}
                {/* the switch on the node's shoulder: plus and what it would unfold, minus once it is open */}
                {(n.hidden > 0 || n.open) && (
                  <g
                    className={"dg-badge" + (n.open ? " open" : "")}
                    transform={`translate(${n.r * 0.74} ${-n.r * 0.74})`}
                    onPointerDown={(e) => e.stopPropagation()}
                    onClick={(e) => {
                      e.stopPropagation();
                      toggle(n.id);
                    }}
                  >
                    <title>{n.open ? "Fold what this type unfolded" : `Unfold ${n.hidden} more`}</title>
                    <circle r={10.5} />
                    <text y={n.open ? 4.6 : 3.6} textAnchor="middle">
                      {n.open ? "−" : "+" + (n.hidden > 99 ? "99" : n.hidden)}
                    </text>
                  </g>
                )}
              </g>
            );
          })}
        </g>
      </svg>
    </>
  );
}

// ---- what both graphs put in their toolbars ----

/** Flat or in space: the one control that turns one graph into the other, first in both toolbars. */
export function GraphModeSwitch({ mode, onMode }: { mode: GraphMode; onMode: (mode: GraphMode) => void }) {
  const modes: { id: GraphMode; label: string; icon: typeof IconTopologyStar3; hint: string }[] = [
    { id: "2d", label: "2D", icon: IconTopologyStar3, hint: "The graph laid out flat on the page" },
    { id: "3d", label: "3D", icon: IconCube3dSphere, hint: "The graph in space, with a camera that flies through it" },
  ];
  return (
    <div className="dm-layout-picker dg-mode" role="tablist" aria-label="Flat or in space">
      {modes.map((m) => (
        <button key={m.id} role="tab" aria-selected={mode === m.id} className={mode === m.id ? "active" : ""} title={m.hint} onClick={() => onMode(m.id)}>
          <m.icon size={14} stroke={1.9} />
          {m.label}
        </button>
      ))}
    </div>
  );
}

/** Whether names are written beside the nodes and lines. */
export function NamesButton({ on, onToggle }: { on: boolean; onToggle: () => void }) {
  return (
    <button className={"icon-button" + (on ? " active" : "")} aria-pressed={on} title={on ? "Hide the names" : "Show the names"} onClick={onToggle}>
      <IconTypography size={16} stroke={1.9} />
    </button>
  );
}

/**
 * The picture asked to be quieter. What that means is the theme's answer, and the two are not the
 * same thing: a dark page can give way, all the way to black, so nothing is then lit on the screen
 * but the drawing; a white page cannot go any further, so what gives way there is the drawing's own
 * tone. `offTitle` is how a picture says which of the two it does. Whatever it is, the controls above
 * the picture are never part of it.
 */
export function BareButton({ on, onToggle, what = "graph", offTitle }: { on: boolean; onToggle: () => void; what?: string; offTitle?: string }) {
  return (
    <button
      className={"icon-button" + (on ? " active" : "")}
      aria-pressed={on}
      title={on ? "Back to the usual " + what : (offTitle ?? "Put the " + what + " on a bare black ground (dark theme)")}
      onClick={onToggle}
    >
      <IconContrast size={16} stroke={1.9} />
    </button>
  );
}

/** The browser's fullscreen, on or off; F over the drawing does the same. Named for what it fills with. */
export function FullscreenButton({ on, onToggle, what = "graph" }: { on: boolean; onToggle: () => void; what?: string }) {
  return (
    <button className={"icon-button" + (on ? " active" : "")} aria-pressed={on} title={on ? `Leave fullscreen (Escape, or F over the ${what})` : `Fill the screen with the ${what} (F over the ${what})`} onClick={onToggle}>
      {on ? <IconMinimize size={16} stroke={1.9} /> : <IconMaximize size={16} stroke={1.9} />}
    </button>
  );
}

// ---- the start ----

/**
 * Every type there is, to pick the one the graph starts from: one group per model source in the order
 * the sources load, the base type first in its group, the rest by name; each with its kind and how
 * many nodes it holds. The page's search box narrows the choice. Whatever is given as tools sits in
 * the head, so a switch that belongs to the whole view is still within reach before a type is picked.
 */
export function TypePicker({ ctx, eligible, query, onPick, tools }: { ctx: EditorContext; eligible: Set<string>; query: string; onPick: (id: string) => void; tools?: ReactNode }) {
  const order = new Map(ctx.model.Sources.map((s, i) => [s.Id, i]));
  const groups = new Map<string, NodeTypeJson[]>();
  for (const id of eligible) {
    const t = ctx.model.NodeTypes[id];
    if (!t || (query && !t.CodeName.toLowerCase().includes(query) && !(t.Namespace ?? "").toLowerCase().includes(query))) continue;
    groups.set(t.DatamodelSourceId, [...(groups.get(t.DatamodelSourceId) ?? []), t]);
  }
  // the base type's own group comes first, whatever source it is counted under; the rest in load order
  const baseGroup = ctx.model.NodeTypes[ctx.baseTypeId]?.DatamodelSourceId;
  const rank = (key: string) => (key === baseGroup ? -1 : (order.get(key) ?? 99));
  const keys = [...groups.keys()].sort((a, b) => rank(a) - rank(b));
  const byName = (a: NodeTypeJson, b: NodeTypeJson) => (a.Id === ctx.baseTypeId ? -1 : b.Id === ctx.baseTypeId ? 1 : a.CodeName.localeCompare(b.CodeName));
  return (
    <div className="dg-picker">
      <div className="dg-picker-head">
        <div>
          <h2>Start from a type</h2>
          <p className="muted">
            The graph grows from the type you pick: the + on a type unfolds what it inherits, relates to, references and embeds, all the way down to its
            properties, and − folds it again. The search box above narrows the choice.
          </p>
        </div>
        {tools && <div className="dg-picker-tools">{tools}</div>}
      </div>
      {keys.map((key) => {
        const source = ctx.sources.find((s) => s.id === key);
        const color = ctx.colors.get(key) ?? "#8a8781";
        const types = [...groups.get(key)!].sort(byName);
        return (
          <section key={key} className="dg-picker-group">
            <h4>
              <span className="dg-picker-swatch" style={{ background: color }} />
              {source?.name ?? (key === ctx.codeSourceId ? "Code" : key === baseGroup ? "Base" : "Source")}
              <span className="dg-picker-n">{types.length}</span>
            </h4>
            <div className="dg-picker-types">
              {types.map((t) => {
                const kind = kindMeta[t.ModelType] ?? kindMeta.Class;
                const count = ctx.typeCounts[t.Id];
                const base = t.Id === ctx.baseTypeId;
                return (
                  <button key={t.Id} className={"dg-picker-type" + (base ? " base" : "")} style={{ borderLeftColor: color }} onClick={() => onPick(t.Id)} title={fullName(t) + " — " + kind.label}>
                    <kind.icon size={15} stroke={1.9} color={kind.color} />
                    <span className="dg-picker-name">{t.CodeName}</span>
                    <span className="dg-picker-count">{base ? "base type" : count === undefined ? kind.label : formatCount(count) + (count === 1 ? " node" : " nodes")}</span>
                  </button>
                );
              })}
            </div>
          </section>
        );
      })}
      {keys.length === 0 && <div className="muted">{query ? "No type matches the search." : "No types to show. Switch a source on."}</div>}
    </div>
  );
}

// ---- the physics ----

/** One step of the simulation: springs, charges, the pull to the middle, collisions, then motion. */
function tick(s: Sim) {
  const nodes = [...s.nodes.values()];
  s.alpha += (s.alphaTarget - s.alpha) * alphaDecay;
  const alpha = s.alpha;
  for (const l of s.links) {
    const a = s.nodes.get(l.a);
    const b = s.nodes.get(l.b);
    if (!a || !b || a === b) continue;
    let dx = b.x + b.vx - a.x - a.vx;
    let dy = b.y + b.vy - a.y - a.vy;
    const len = Math.sqrt(dx * dx + dy * dy) || 1e-6;
    const f = ((len - l.length) / len) * alpha * l.strength;
    dx *= f;
    dy *= f;
    // the lighter end gives more: a leaf follows its type, a type barely feels a leaf
    const wa = a.kind === "property" ? 0.85 : b.kind === "property" ? 0.15 : 0.5;
    b.vx -= dx * (1 - wa);
    b.vy -= dy * (1 - wa);
    a.vx += dx * wa;
    a.vy += dy * wa;
  }
  for (let i = 0; i < nodes.length; i++) {
    const a = nodes[i];
    for (let j = i + 1; j < nodes.length; j++) {
      const b = nodes[j];
      let dx = b.x - a.x;
      let dy = b.y - a.y;
      let d2 = dx * dx + dy * dy;
      if (d2 > 450 * 450) continue;
      if (d2 < 1e-6) {
        dx = Math.random() - 0.5;
        dy = Math.random() - 0.5;
        d2 = dx * dx + dy * dy;
      }
      if (d2 < 64) d2 = 64;
      // each feels the other's charge, as in d3: a leaf is pushed hard by a type, a type barely by a leaf
      const fa = (b.charge * alpha) / d2;
      const fb = (a.charge * alpha) / d2;
      a.vx += dx * fa;
      a.vy += dy * fa;
      b.vx -= dx * fb;
      b.vy -= dy * fb;
    }
  }
  for (const n of nodes) {
    if (n.kind !== "type") continue;
    n.vx -= n.x * 0.018 * alpha;
    n.vy -= n.y * 0.018 * alpha;
  }
  for (let i = 0; i < nodes.length; i++) {
    const a = nodes[i];
    for (let j = i + 1; j < nodes.length; j++) {
      const b = nodes[j];
      const min = a.r + b.r + (a.kind === "type" && b.kind === "type" ? 14 : 5);
      let dx = b.x - a.x;
      let dy = b.y - a.y;
      let d = Math.sqrt(dx * dx + dy * dy);
      if (d >= min) continue;
      if (d < 1e-6) {
        dx = Math.random() - 0.5;
        dy = Math.random() - 0.5;
        d = Math.sqrt(dx * dx + dy * dy);
      }
      const push = ((min - d) / d) * 0.5;
      a.vx -= dx * push;
      a.vy -= dy * push;
      b.vx += dx * push;
      b.vy += dy * push;
    }
  }
  for (const n of nodes) {
    if (n.fx !== null && n.fy !== null) {
      n.x = n.fx;
      n.y = n.fy;
      n.vx = 0;
      n.vy = 0;
      continue;
    }
    n.vx *= velocityDecay;
    n.vy *= velocityDecay;
    n.x += n.vx;
    n.y += n.vy;
  }
}
