import { useEffect, useMemo, useRef, useState } from "react";
import { IconArrowsMaximize, IconArrowsShuffle, IconFileTypeSvg, IconHandStop, IconViewportNarrow, IconViewportWide, IconZoomIn, IconZoomOut } from "@tabler/icons-react";
import { downloadSvg } from "../../svgExport";
import { FullscreenButton } from "../DatamodelGraph";
import type { EditorContext, Selection } from "../DatamodelEditors";
import { DiagramEdges, EdgeMarkers, gapClearance } from "./DiagramEdges";
import { DiagramNode } from "./DiagramNode";
import { layoutInfo, layouts, spacings, type EdgeKind, type LayoutMode } from "./layouts";
import { buildDiagram, edgeKinds, type Box, type Edge } from "./model";
import { crossingGaps, moveSegment, routeAll, routeMargin, routeTouches, simplify, validManual, type Gap, type Pt, type Route } from "./router";
import { readKinds, readLayout, readLines, readPanMode, readPositions, readSpacing, writeKinds, writeLayout, writeLines, writePanMode, writePositions, writeSpacing } from "./storage";

interface Props {
  ctx: EditorContext;
  visibleTypes: Set<string>;
  ghostTypes: Set<string>;
  selection: Selection | null;
  query: string;
  storeId: string;
}

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

/**
 * The model as boxes and lines, arranged by one of four published methods (see ./layouts) and joined
 * by right-angled lines that go round the boxes rather than through them (see ./router).
 * Everything can then be moved by hand: a box is dragged and stays where it was put, a line is
 * pushed sideways piece by piece, both per database and per arrangement, until "Arrange again".
 * Everything is plain SVG - no library, the model is small enough.
 */
export function DatamodelDiagram({ ctx, visibleTypes, ghostTypes, selection, query, storeId }: Props) {
  const [layout, setLayout] = useState<LayoutMode>(() => readLayout(storeId));
  const [positions, setPositions] = useState(() => readPositions(storeId, layout));
  // how much room the arrangement leaves round the boxes, as a step of `spacings`
  const [spacing, setSpacing] = useState(() => readSpacing(storeId));
  // which kinds of line are drawn at all
  const [kinds, setKinds] = useState(() => readKinds(storeId));
  // lines a hand has moved, by edge id: drawn as they are for as long as they still fit the boxes
  const [lines, setLines] = useState(() => readLines(storeId, layout));
  // types whose box shows every property rather than the first few
  const [openBoxes, setOpenBoxes] = useState<Set<string>>(new Set());
  // the types picked out to be moved together, by id: a picture of this model rather than a
  // preference, so it is not remembered anywhere
  const [picked, setPicked] = useState<Set<string>>(new Set());
  // whether dragging the background pans the drawing or draws a square round boxes to pick them
  const [panMode, setPanMode] = useState(readPanMode);
  // the square being dragged now, in the drawing's own coordinates
  const [band, setBand] = useState<{ x0: number; y0: number; x1: number; y1: number } | null>(null);
  const [view, setView] = useState<View>({ x: 20, y: 20, k: 1 });
  const [fitted, setFitted] = useState(false);
  const svgRef = useRef<SVGSVGElement>(null);
  const drag = useRef<
    | { kind: "pan"; sx: number; sy: number; ox: number; oy: number }
    | { kind: "band"; x0: number; y0: number; x1: number; y1: number; add: boolean }
    | { kind: "node"; id: string; ids: string[]; from: Map<string, { x: number; y: number }>; sx: number; sy: number; moved: boolean; additive: boolean }
    | { kind: "segment"; id: string; from: string; to: string; seg: number; points: Pt[]; sx: number; sy: number; moved: boolean; select?: () => void }
    | null
  >(null);
  // where each box is drawn this frame, so the next rearrangement starts from where the eye left it
  const drawn = useRef(new Map<string, { x: number; y: number }>());
  // the lines as drawn this frame, and where they set out from while a rearrangement is on its way
  const drawnRoutes = useRef(new Map<string, Pt[]>());
  const routeMove = useRef<Map<string, Pt[]> | null>(null);
  // the lines last worked out, where they cross, and the picture they were worked out for
  const routeCache = useRef<{ sig: string; routes: Map<string, Route>; gaps: Map<string, Gap[]> }>({ sig: "", routes: new Map(), gaps: new Map() });
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

  // the model, not the whole editor context: the node counts in it change on their own, and neither
  // the boxes nor the arrangement have anything to do with them
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const { boxes: shape, edges } = useMemo(() => buildDiagram(ctx, visibleTypes, ghostTypes, openBoxes, kinds), [ctx.model, ctx.baseTypeId, visibleTypes, ghostTypes, openBoxes, kinds]);
  // the arrangement is worked out for a picture, not again for every frame of a drag: some of the
  // methods are a good deal of arithmetic, and dragging one box changes none of what they are given
  const arranged = useMemo(() => {
    const copy = shape.map((b) => ({ ...b }));
    layoutInfo(layout).run(copy, edges, spacings[spacing]);
    return copy;
  }, [shape, edges, layout, spacing]);
  const boxes = useMemo(() => arranged.map((b) => (positions[b.id] ? { ...b, ...positions[b.id] } : b)), [arranged, positions]);

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
    routeMove.current = new Map(drawnRoutes.current);
    setRearranged((n) => n + 1);
    runMove();
  }

  // The view goes with them. One arrangement's extent is usually nothing like the next one's - a
  // layered model is as wide as a stressed one is tall - so without this the boxes would glide off
  // the edge of the screen. It waits for the render that has the new places, since that is what it
  // measures.
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
    if (e.code === "Escape" && picked.size > 0) setPicked(new Set());
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

  // ---- the hand ----

  /** Where a point on the screen is in the drawing. */
  function at(clientX: number, clientY: number): Pt {
    const rect = svgRef.current!.getBoundingClientRect();
    return { x: (clientX - rect.left - view.x) / view.k, y: (clientY - rect.top - view.y) / view.k };
  }

  /**
   * A hand on the background. It pans in pan mode, and otherwise draws a square that picks up every
   * box it touches - shift or ctrl adding to what is picked already rather than replacing it. The
   * middle and right buttons always pan, whichever mode it is in, so there is a way round without
   * leaving the square behind.
   */
  function onPointerDown(e: React.PointerEvent) {
    // a hand on the drawing outranks a transition still playing under it
    viewMove.current = null;
    const additive = e.shiftKey || e.ctrlKey || e.metaKey;
    if (e.button === 0 && (!panMode || additive)) {
      const p = at(e.clientX, e.clientY);
      drag.current = { kind: "band", x0: p.x, y0: p.y, x1: p.x, y1: p.y, add: panMode ? false : additive };
      setBand({ x0: p.x, y0: p.y, x1: p.x, y1: p.y });
    } else if (e.button === 0 || e.button === 1 || e.button === 2) {
      drag.current = { kind: "pan", sx: e.clientX, sy: e.clientY, ox: view.x, oy: view.y };
    } else return;
    (e.currentTarget as Element).setPointerCapture(e.pointerId);
  }
  /**
   * A hand on a box. Shift or ctrl adds it to what is picked or takes it out again; a box that is
   * already picked drags everything picked with it, and a plain hand on any other box picks that one
   * alone.
   */
  function onNodePointerDown(e: React.PointerEvent, b: Box) {
    if (e.button !== 0) return;
    e.stopPropagation();
    move.current = null;
    viewMove.current = null;
    const additive = e.shiftKey || e.ctrlKey || e.metaKey;
    let ids: string[];
    if (additive) {
      const next = new Set(picked);
      if (next.has(b.id)) next.delete(b.id);
      else next.add(b.id);
      setPicked(next);
      // taken out of the selection: there is nothing to drag by a box that is no longer in it
      if (!next.has(b.id)) return;
      ids = [...next];
    } else if (picked.has(b.id)) {
      ids = [...picked];
    } else {
      ids = [b.id];
      if (picked.size > 0) setPicked(new Set());
    }
    const from = new Map(ids.filter((id) => boxById.has(id)).map((id) => [id, { x: boxById.get(id)!.x, y: boxById.get(id)!.y }]));
    drag.current = { kind: "node", id: b.id, ids: [...from.keys()], from, sx: e.clientX, sy: e.clientY, moved: false, additive };
    svgRef.current!.setPointerCapture(e.pointerId);
  }
  /** A hand on one piece of a line: it is about to be pushed sideways or, if it does not move, clicked. */
  function onSegmentPointerDown(e: React.PointerEvent, edge: Edge, route: Route, seg: number, select?: () => void) {
    if (e.button !== 0) return;
    e.stopPropagation();
    viewMove.current = null;
    drag.current = { kind: "segment", id: edge.id, from: edge.from, to: edge.to, seg, points: route.points, sx: e.clientX, sy: e.clientY, moved: false, select };
    svgRef.current!.setPointerCapture(e.pointerId);
  }
  function onPointerMove(e: React.PointerEvent) {
    const d = drag.current;
    if (!d) return;
    if (d.kind === "pan") {
      setView((v) => ({ ...v, x: d.ox + (e.clientX - d.sx), y: d.oy + (e.clientY - d.sy) }));
      return;
    }
    if (d.kind === "band") {
      const p = at(e.clientX, e.clientY);
      // the square lives on the gesture rather than in state: the state is what draws it, and a
      // whole gesture can be over before React has committed a frame of it
      d.x1 = p.x;
      d.y1 = p.y;
      setBand({ x0: d.x0, y0: d.y0, x1: p.x, y1: p.y });
      return;
    }
    const dx = (e.clientX - d.sx) / view.k;
    const dy = (e.clientY - d.sy) / view.k;
    if (Math.abs(dx) + Math.abs(dy) > 2) d.moved = true;
    if (d.kind === "node") {
      setPositions((prev) => {
        const next = { ...prev };
        for (const [id, start] of d.from) next[id] = { x: start.x + dx, y: start.y + dy };
        return next;
      });
    } else if (d.moved) {
      const a = boxes.find((x) => x.id === d.from);
      const b = boxes.find((x) => x.id === d.to);
      if (!a || !b) return;
      // a horizontal piece goes up and down, a vertical one left and right
      const horizontal = Math.abs(d.points[d.seg].y - d.points[d.seg + 1].y) < 0.01;
      setLines((prev) => ({ ...prev, [d.id]: moveSegment(d.points, d.seg, horizontal ? dy : dx, a, b) }));
    }
  }
  // two clicks in quick succession on a hand-placed line hand it back to the router
  const lastLineClick = useRef<{ id: string; at: number } | null>(null);
  // let go, or the pointer taken away: either way the gesture ends where it was last seen, which is
  // the release point for a hand on a mouse. A cancelled pointer carries no place worth reading.
  function onPointerUp() {
    const d = drag.current;
    drag.current = null;
    if (d?.kind === "band") {
      setBand(null);
      // a square with no width or height is a click on the background: it drops what was picked
      const touched = Math.abs(d.x1 - d.x0) + Math.abs(d.y1 - d.y0) > 3 ? boxesIn(d) : [];
      setPicked(d.add ? new Set([...picked, ...touched]) : new Set(touched));
    } else if (d?.kind === "node") {
      if (!d.moved && !d.additive) {
        ctx.select({ kind: "type", id: d.id });
        setPicked(new Set([d.id]));
      }
    } else if (d?.kind === "segment") {
      if (d.moved) {
        // let go: pieces pushed into line with their neighbours fold away, and a line pushed through
        // a box is not kept - it goes back to finding its own way round
        setLines((prev) => {
          const pts = prev[d.id];
          if (!pts) return prev;
          const next = { ...prev };
          const tidy = simplify(pts);
          if (
            validManual(
              tidy,
              boxes.find((x) => x.id === d.from),
              boxes.find((x) => x.id === d.to),
              boxes,
            )
          )
            next[d.id] = tidy;
          else delete next[d.id];
          return next;
        });
      } else {
        const now = performance.now();
        const last = lastLineClick.current;
        if (last && last.id === d.id && now - last.at < 400 && lines[d.id]) {
          setLines((prev) => {
            const next = { ...prev };
            delete next[d.id];
            return next;
          });
          lastLineClick.current = null;
        } else {
          lastLineClick.current = { id: d.id, at: now };
          d.select?.();
        }
      }
    }
  }
  /** Every box the square touches, however little. */
  function boxesIn(square: { x0: number; y0: number; x1: number; y1: number }): string[] {
    const x1 = Math.min(square.x0, square.x1);
    const x2 = Math.max(square.x0, square.x1);
    const y1 = Math.min(square.y0, square.y1);
    const y2 = Math.max(square.y0, square.y1);
    return boxes.filter((b) => b.x < x2 && b.x + b.w > x1 && b.y < y2 && b.y + b.h > y1).map((b) => b.id);
  }

  /** A kind of line in or out of the drawing. The boxes travel with it: the arrangement is worked out
   *  from the lines that are drawn, so leaving relations out is a different picture, not a hidden one. */
  function toggleKind(kind: EdgeKind) {
    startMove();
    // the set this is toggling, not the one this render closed over: three clicks in a row would
    // otherwise keep only the last of them
    setKinds((prev) => {
      const next = new Set(prev);
      if (next.has(kind)) next.delete(kind);
      else next.add(kind);
      return next;
    });
  }

  /** Whether the hand pans the background or picks boxes off it. */
  function togglePanMode() {
    setPanMode((prev) => !prev);
  }

  useEffect(() => writeSpacing(storeId, spacing), [spacing, storeId]);
  useEffect(() => writeKinds(storeId, kinds), [kinds, storeId]);
  useEffect(() => writePanMode(panMode), [panMode]);
  useEffect(() => writePositions(storeId, layout, positions), [positions, storeId, layout]);
  useEffect(() => writeLines(storeId, layout, lines), [lines, storeId, layout]);

  /** Back to the arranged places, forgetting what was dragged here - and the boxes walk back. */
  function autoLayout() {
    startMove();
    setPositions({});
    setLines({});
  }

  /** Another arrangement, with whatever was dragged in that one. The boxes travel to their new places. */
  function chooseLayout(mode: LayoutMode) {
    if (mode === layout) return;
    startMove();
    setLayout(mode);
    setPositions(readPositions(storeId, mode));
    setLines(readLines(storeId, mode));
    writeLayout(storeId, mode);
  }

  /** Tighter or airier: the same arrangement worked out again with less or more room between the
   *  boxes, and they travel to where that puts them. */
  function respace(step: number) {
    if (spacing + step < 0 || spacing + step >= spacings.length) return;
    startMove();
    setSpacing((prev) => Math.min(spacings.length - 1, Math.max(0, prev + step)));
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
  // simply where they belong. Everything downstream reads these - the lines take their ends from the
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
  const q = query.trim().toLowerCase();

  /**
   * The lines for the boxes where they belong (not where they are drawn on the way there), kept until
   * the picture changes. A box being dragged changes the picture every frame, so then only the lines
   * it disturbs are found again: those into it and those it has been dragged onto; the rest stay as
   * they were and are what the new ones get round. A line pushed by hand disturbs nothing.
   */
  function currentRoutes(): { routes: Map<string, Route>; gaps: Map<string, Gap[]> } {
    const draggingLine = drag.current?.kind === "segment" ? drag.current.id : null;
    const settled = new Map(boxes.map((b) => [b.id, b]));
    const manual = new Map<string, Pt[]>();
    for (const e of edges) {
      const pts = lines[e.id];
      if (!pts) continue;
      // the line under the hand is drawn wherever the hand has it; it is judged when let go
      if (e.id === draggingLine || validManual(pts, settled.get(e.from), settled.get(e.to), boxes)) manual.set(e.id, pts);
    }
    const sig = layout + "|" + boxes.map((b) => `${b.id}:${b.x},${b.y},${b.w},${b.h}`).join(";") + "|" + edges.map((e) => e.id).join(",");
    const cache = routeCache.current;
    const keep = new Map<string, Route>();
    if (sig === cache.sig) {
      // the same picture: only the hand-placed lines can have changed
      for (const [id, r] of cache.routes) if (r.manual ? manual.get(id) === r.points : !manual.has(id)) keep.set(id, r);
    } else if (drag.current?.kind === "node" && cache.routes.size > 0) {
      const moving = drag.current.ids.map((id) => settled.get(id)).filter((b) => b !== undefined);
      const held = new Set(moving.map((b) => b.id));
      const reach = moving.map((b) => ({ ...b, x: b.x - routeMargin, y: b.y - routeMargin, w: b.w + 2 * routeMargin, h: b.h + 2 * routeMargin }));
      const edgeById = new Map(edges.map((e) => [e.id, e]));
      for (const [id, r] of cache.routes) {
        const e = edgeById.get(id);
        if (!e || held.has(e.from) || held.has(e.to)) continue;
        if (r.manual ? manual.get(id) !== r.points : reach.some((box) => routeTouches(r.points, box))) continue;
        keep.set(id, r);
      }
    }
    const routed = routeAll(boxes, edges, manual, keep, layoutInfo(layout).axis);
    // where lines cross is worked out again only when some line is new; a pan or a zoom changes none
    let changed = routed.size !== cache.routes.size;
    if (!changed) for (const [id, r] of routed) if (cache.routes.get(id) !== r) changed = true;
    const gaps = changed ? crossingGaps(routed.values(), gapClearance) : cache.gaps;
    routeCache.current = { sig, routes: routed, gaps };
    return { routes: routed, gaps };
  }
  const routing = currentRoutes();
  drawnRoutes.current = new Map();

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
        <button className="icon-button" title="More compact: less room between the boxes" onClick={() => respace(-1)} disabled={spacing === 0}>
          <IconViewportNarrow size={16} stroke={1.9} />
        </button>
        <button className="icon-button" title="More spaced out: more room between the boxes, so the lines are easier to follow" onClick={() => respace(1)} disabled={spacing === spacings.length - 1}>
          <IconViewportWide size={16} stroke={1.9} />
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
        <button
          className={"icon-button" + (panMode ? " active" : "")}
          aria-pressed={panMode}
          title={
            panMode
              ? "Dragging the background moves the drawing. Hold shift to draw a square round boxes instead. Click for the other way round"
              : "Dragging the background draws a square that picks up every box it touches — shift or ctrl to add to what is picked, and a picked box drags the whole selection. The middle or right mouse button always moves the drawing. Click to make dragging move the drawing instead"
          }
          onClick={togglePanMode}
        >
          <IconHandStop size={16} stroke={1.9} />
        </button>
        {/* how the boxes are arranged; each layout keeps whatever was dragged in it */}
        <div className="dm-layout-picker" role="tablist" aria-label="How the boxes are arranged">
          {layouts.map((l) => (
            <button key={l.id} role="tab" aria-selected={layout === l.id} className={layout === l.id ? "active" : ""} title={l.hint} onClick={() => chooseLayout(l.id)}>
              <l.icon size={14} stroke={1.9} />
              {l.label}
            </button>
          ))}
        </div>
        {/* the legend is the switch as well: what it names, it draws */}
        <div className="dm-diagram-legend" role="group" aria-label="Which lines are drawn">
          {edgeKinds.map((k) => (
            <button
              key={k.id}
              className={"dm-legend-toggle" + (kinds.has(k.id) ? "" : " off")}
              aria-pressed={kinds.has(k.id)}
              title={(kinds.has(k.id) ? "Drawn: " : "Left out: ") + k.hint + ". Click to " + (kinds.has(k.id) ? "leave it out of the drawing" : "draw it again")}
              onClick={() => toggleKind(k.id)}
            >
              <span className={"dm-legend-line " + k.id} />
              {k.label}
            </button>
          ))}
        </div>
      </div>
      <svg
        ref={svgRef}
        className={"dm-diagram-svg" + (panMode ? " panning" : "")}
        tabIndex={0}
        onKeyDown={onKeyDown}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={onPointerUp}
        onContextMenu={(e) => e.preventDefault()}
      >
        <EdgeMarkers />
        <g transform={`translate(${view.x} ${view.y}) scale(${view.k})`}>
          <DiagramEdges
            edges={edges}
            boxById={boxById}
            routes={routing.routes}
            // the gaps are drawn once the lines are where they belong; while they travel nothing crosses cleanly
            gaps={t >= 1 ? routing.gaps : null}
            from={routeMove.current}
            t={t}
            selectedType={selectedType}
            selectedRelation={selectedRelation}
            query={query}
            select={ctx.select}
            onSegment={onSegmentPointerDown}
            record={(id, points) => drawnRoutes.current.set(id, points)}
          />
          {placed.map((b) => (
            <DiagramNode
              key={b.id}
              box={b}
              color={ctx.colors.get(b.type.DatamodelSourceId) ?? "#888"}
              selected={selectedType === b.id || picked.has(b.id)}
              selectedProperty={selection?.kind === "property" ? selection.id : null}
              dim={q.length > 0 && !(b.type.CodeName.toLowerCase().includes(q) || b.rows.some((r) => r.name.toLowerCase().includes(q)))}
              open={openBoxes.has(b.id)}
              onPointerDown={(e) => onNodePointerDown(e, b)}
              onSelectProperty={(id) => ctx.select({ kind: "property", id, typeId: b.id })}
              onToggle={() => toggleBox(b.id)}
            />
          ))}
          {band && <rect className="dm-select-band" x={Math.min(band.x0, band.x1)} y={Math.min(band.y0, band.y1)} width={Math.abs(band.x1 - band.x0)} height={Math.abs(band.y1 - band.y0)} vectorEffect="non-scaling-stroke" />}
        </g>
        {boxes.length === 0 && (
          <text x="50%" y="50%" textAnchor="middle" className="dm-node-proptype">
            No types to draw. Switch a source on.
          </text>
        )}
      </svg>
    </div>
  );
}
