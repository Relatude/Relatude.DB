import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { IconArrowNarrowDown, IconArrowNarrowUp, IconFocusCentered, IconListDetails } from "@tabler/icons-react";
import type { PivotBase } from "./PivotView";
import { bytesOf, fetchNodeGuid, fetchPivotModel, runVisual, type PivotModel, type PivotProperty, type VisualGroup, type VisualRequest, type VisualResult } from "../server/query";
import { useLiveResult } from "../server/hooks";
import { formatCount, formatQuery } from "../format";
import type { VisualDefinition } from "../queryTabs";
import { createCardField, transitionSeconds, type CardField, type FieldTheme, type RGBf } from "../visual/cardField";
import { barLayout, gridLayout, type Bar, type Layout } from "../visual/layouts";
import { buildPalette, palettes, parseCssColor, type PaletteColor, type RGB } from "../visual/palette";
import { shapeLabel, shapeMaskUrl, shapeSlotFor } from "../visual/shapes";
import { IntMap } from "../visual/intMap";
import { createCardMedia, type CardMedia } from "../visual/cardMedia";
import { createCardLabels, type CardLabels, type LabelColors } from "../visual/cardLabels";

/** A visual pivot before anyone has chosen anything: a grid of one colour, in the result's order. */
export const emptyVisual: VisualDefinition = { colorProperty: null, colorMode: "auto", shapeProperty: null, shapeMode: "auto", barProperty: null, barMode: "auto", sortProperty: null, sortDescending: false, legend: true, palette: palettes[0].id };

/** what a group stands for: a value of the property, the nodes without one, or the ones outside the groups kept */
type GroupKind = "value" | "none" | "other";

interface DecodedGroup extends VisualGroup {
  kind: GroupKind;
  /** the group's place in the palette (see paletteSlots); -1 for the two greys */
  ordinal: number;
  /** the silhouette this group's cards are cut out to when the picture is shaped by this property (see shapeSlotFor); 0 is the plain card */
  shape: number;
}

interface DecodedProperty {
  propertyId: string;
  name: string;
  groups: DecodedGroup[];
  /** the group of every card, an index into `groups` */
  assignment: Uint16Array;
}

/** The result with its byte arrays turned into typed arrays, done once per result. */
interface Decoded {
  count: number;
  total: number;
  ids: Int32Array;
  /** the cards as sorted by the sort property, as indexes into `ids`; null for the result's order */
  order: Int32Array | null;
  byProperty: Map<string, DecodedProperty>;
}

interface Theme extends FieldTheme {
  /** the page behind the cards: what the palette holds its contrast against */
  panel: RGB;
  /** the blue the app is drawn in: the hue a palette that names none of its own takes */
  accent: RGB;
  none: RGB;
  other: RGB;
}

interface Tooltip {
  x: number;
  y: number;
  lines: string[];
}

/** what the pointer is doing between down and up */
interface Drag {
  x: number;
  y: number;
  t: number;
  moved: boolean;
  vx: number;
  vy: number;
}

const modeOptions = [
  { value: "auto", label: "auto" },
  { value: "values", label: "values" },
  { value: "ranges", label: "ranges" },
];
const paletteSize = 512; // above the server's cap of groups per property
const fitPadding = 28;
const labelMinWidth = 64; // css px a bar label needs before its neighbours are thinned out
const refitSeconds = 0.7; // a fit asked for on its own - the button, a double-click, a resize - with nothing else moving

/**
 * The visual pivot: every node of the result on screen as a card, in a grid or stacked into bars by
 * a property, coloured by another. Pan by dragging, zoom with the wheel, click a card to open it.
 *
 * The picture is drawn by the card field (visual/cardField.ts); this component decides what it
 * shows. The server hands over the result as ids and a group index per card per property, a few
 * bytes a card, and everything from there on is a pass over typed arrays: a layout is a position per
 * card (visual/layouts.ts), a colouring is an index per card into a palette. Changing either uploads
 * the new arrays and the cards travel there themselves.
 *
 * What is laid over the canvas in html - the bar labels, the tooltip, the legend - follows the
 * camera through the field's frame callback, without going through React on every frame.
 */
export function VisualPivotView({
  base,
  definition,
  onChange,
  refreshToken,
  showQuery,
  onOpen,
  selected,
}: {
  base: PivotBase;
  /** The definition as the page keeps it - null until this view has opened once for the type. */
  definition: VisualDefinition | null;
  onChange: (definition: VisualDefinition) => void;
  /** Changes when the page is asked to run again with nothing else changed. */
  refreshToken: number;
  showQuery: boolean;
  /** A card was clicked: open this node in the form beside the picture. */
  onOpen: (nodeId: string) => void;
  /** The node the form has open, so the picture can stop marking a card once the form is closed. */
  selected: string | null;
}) {
  const [model, setModel] = useState<PivotModel | null>(null);
  const [modelError, setModelError] = useState<string | null>(null);
  const def = definition ?? emptyVisual;

  useEffect(() => {
    let cancelled = false;
    setModel(null);
    setModelError(null);
    fetchPivotModel(base.storeId, base.typeId)
      .then((m) => !cancelled && setModel(m))
      .catch((e) => !cancelled && setModelError(e instanceof Error ? e.message : String(e)));
    return () => {
      cancelled = true;
    };
  }, [base.storeId, base.typeId]);

  const groupable = useMemo(() => model?.properties.filter((p) => p.groupable) ?? [], [model]);

  // the first time the view opens for a type it colours by the first property that can be grouped,
  // so there is a picture before anyone chooses; the choice is then the query's own
  useEffect(() => {
    if (model && definition === null) onChange({ ...emptyVisual, colorProperty: groupable[0]?.id ?? null });
  }, [model, definition, groupable, onChange]);

  const colorProperty = groupable.some((p) => p.id === def.colorProperty) ? def.colorProperty : null;
  const barProperty = groupable.some((p) => p.id === def.barProperty) ? def.barProperty : null;
  // read defensively: a definition saved before there were shapes has neither field
  const shapeProperty = groupable.some((p) => p.id === def.shapeProperty) ? def.shapeProperty! : null;
  const shapeMode = def.shapeMode ?? "auto";
  // sorting needs a single value per node with an order to it, which is what an indexed scalar is
  const sortable = useMemo(() => model?.properties.filter((p) => p.aggregatable) ?? [], [model]);
  // read defensively: a definition saved before there was a sort has neither field
  const sortProperty = sortable.some((p) => p.id === def.sortProperty) ? def.sortProperty! : null;
  const sortDescending = def.sortDescending === true;

  const request = useMemo<VisualRequest | null>(() => {
    if (model === null || definition === null) return null;
    const properties = [];
    if (colorProperty) properties.push({ propertyId: colorProperty, mode: def.colorMode });
    if (shapeProperty && shapeProperty !== colorProperty) properties.push({ propertyId: shapeProperty, mode: shapeMode });
    if (barProperty && barProperty !== colorProperty && barProperty !== shapeProperty) properties.push({ propertyId: barProperty, mode: def.barMode });
    return {
      storeId: base.storeId,
      typeId: base.typeId,
      text: base.text,
      semanticRatio: base.semanticRatio,
      minimumSimilarity: base.minimumSimilarity,
      selections: base.selections,
      properties,
      sortBy: sortProperty,
      sortDescending,
    };
    // the token is not part of the request; a new object is how the runner is told to run again
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [model, definition === null, base, colorProperty, def.colorMode, shapeProperty, shapeMode, barProperty, def.barMode, sortProperty, sortDescending, refreshToken]);
  const { result, loading, error } = useLiveResult(request, runVisual);
  const decoded = useMemo(() => (result ? decode(result) : null), [result]);

  // ---- the canvas and the field ----

  const stageRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const textRef = useRef<HTMLCanvasElement>(null);
  const labelsRef = useRef<HTMLDivElement>(null);
  const field = useRef<CardField | null>(null);
  // the names and pictures of the cards in view, and the names drawn over the picture
  const media = useRef<CardMedia | null>(null);
  const labels = useRef<CardLabels | null>(null);
  const labelColors = useRef<LabelColors | null>(null);
  const [glOk, setGlOk] = useState(true);
  const [theme, setTheme] = useState<Theme | null>(null);
  const [bars, setBars] = useState<{ bar: Bar; group: DecodedGroup }[]>([]);
  const layoutRef = useRef<Layout | null>(null);
  const [tooltip, setTooltip] = useState<Tooltip | null>(null);
  const [selectedIndex, setSelectedIndex] = useState(-1);
  const drag = useRef<Drag | null>(null);
  const lastHoverPick = useRef(0);
  const previous = useRef<{ decoded: Decoded; layout: Layout } | null>(null);
  const refit = useRef(0);
  // the canvas exists once the model is known (nothing is rendered before), so the field is made then
  const hasStage = model !== null && glOk;

  useLayoutEffect(() => {
    const stage = stageRef.current;
    const canvas = canvasRef.current;
    if (!stage || !canvas) return;
    let f: CardField | null = null;
    try {
      f = createCardField(canvas);
    } catch (e) {
      console.error("The visual pivot could not set up its drawing:", e);
      f = null;
    }
    if (!f) {
      setGlOk(false);
      return;
    }
    field.current = f;
    previous.current = null;
    const t = readTheme(stage);
    setTheme(t);
    f.setTheme(t);
    f.resize();
    const m = createCardMedia(f, base.storeId);
    media.current = m;
    const l = textRef.current ? createCardLabels(textRef.current) : null;
    labels.current = l;
    // the labels follow the camera on every frame the field draws; the pictures of the cards in view
    // are kept coming from the same place, and the names drawn over them
    f.onFrame(() => {
      placeLabels();
      m.frame(performance.now());
      l?.draw(f, m, labelColors.current);
    });
    const ro = new ResizeObserver(() => {
      f!.resize();
      // the picture is fitted to its new room, once the resizing has settled: a dragged splitter
      // fires this many times a second and a fit per event would fight the drag
      window.clearTimeout(refit.current);
      refit.current = window.setTimeout(() => fitToLayout(refitSeconds), 180);
    });
    ro.observe(canvas);
    const mo = new MutationObserver(() => {
      const next = readTheme(stage);
      setTheme(next);
      f!.setTheme(next);
    });
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    // the wheel is listened to natively: React registers wheel listeners as passive, and a passive
    // listener cannot keep the page from scrolling under the picture
    const wheel = (e: WheelEvent) => {
      e.preventDefault();
      const rect = canvas.getBoundingClientRect();
      const step = e.deltaMode === 1 ? e.deltaY * 16 : e.deltaMode === 2 ? e.deltaY * 400 : e.deltaY;
      f!.zoomBy(Math.exp(-step * 0.0016), e.clientX - rect.left, e.clientY - rect.top);
    };
    canvas.addEventListener("wheel", wheel, { passive: false });
    return () => {
      ro.disconnect();
      mo.disconnect();
      window.clearTimeout(refit.current);
      canvas.removeEventListener("wheel", wheel);
      m.destroy();
      media.current = null;
      labels.current = null;
      f!.destroy();
      field.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- lives with the canvas element
  }, [hasStage]);

  const palette = useMemo(() => buildPalette(paletteSize, theme?.panel ?? [255, 255, 255], theme?.accent ?? [9, 96, 178], def.palette), [theme, def.palette]);

  // another database: nothing known about the cards carries over
  useEffect(() => {
    media.current?.setStore(base.storeId);
  }, [base.storeId]);

  // What the picture is coloured and stacked by. When a picker changes, the answer for the new
  // property is a round trip away and the answer on hand has nothing for it - so until it arrives
  // the old grouping stays on screen. Without that the legend would vanish for the wait, the canvas
  // widen into its place, the cards fall back to one colour and the grid be re-laid for the wider
  // canvas, and the picture would jump twice for a change that moves nothing at all.
  const colorNow = decoded && colorProperty ? (decoded.byProperty.get(colorProperty) ?? null) : null;
  const barNow = decoded && barProperty ? (decoded.byProperty.get(barProperty) ?? null) : null;
  const shapeNow = decoded && shapeProperty ? (decoded.byProperty.get(shapeProperty) ?? null) : null;
  const lastColor = useRef<DecodedProperty | null>(null);
  const lastBar = useRef<DecodedProperty | null>(null);
  const lastShape = useRef<DecodedProperty | null>(null);
  const colorData = colorNow ?? (colorProperty !== null ? lastColor.current : null);
  const barData = barNow ?? (barProperty !== null ? lastBar.current : null);
  const shapeData = shapeNow ?? (shapeProperty !== null ? lastShape.current : null);
  lastColor.current = colorData;
  lastBar.current = barData;
  lastShape.current = shapeData;

  // The picture follows the data. What changed is worked out from the answer itself rather than
  // from which picker was touched: other cards (the ids differ) are a new set, and the ones that
  // were already on screen leave from where they are; the same cards with other positions - bars
  // by something else, another sort, a colouring that re-bands the bars - are a move; the same
  // cards in the same places are a new palette and nothing else, so a colour change never moves
  // the camera, resets the selection or rebuilds anything.
  useEffect(() => {
    const f = field.current;
    if (!f || !decoded || !theme) return;
    const canvas = canvasRef.current;
    const aspect = canvas && canvas.clientHeight > 0 ? canvas.clientWidth / canvas.clientHeight : 1.6;
    const layout = barData
      ? barLayout(decoded.count, barData.assignment, barData.groups.length, colorData?.assignment ?? null, aspect, decoded.order)
      : gridLayout(decoded.count, aspect, decoded.order);
    layoutRef.current = layout;
    const prev = previous.current;
    if (prev === null || !sameValues(prev.decoded.ids, decoded.ids)) {
      // A new set of cards: the ones that were already on screen leave from where they are, and the
      // ones that were not fade in where they belong - so what carried over is seen to travel and
      // what is new is seen to arrive, rather than the whole picture being replaced at once.
      let from: Float32Array | null = null;
      let fresh: Uint8Array | null = null;
      if (prev !== null && prev.decoded.count > 0 && decoded.count > 0) {
        const where = f.positions();
        const byId = new IntMap(prev.decoded.count);
        for (let j = 0; j < prev.decoded.count; j++) byId.set(prev.decoded.ids[j], j);
        from = new Float32Array(layout.positions);
        const newborn = new Uint8Array(decoded.count);
        let arriving = 0;
        for (let i = 0; i < decoded.count; i++) {
          const j = byId.get(decoded.ids[i]);
          if (j >= 0) {
            from[i * 2] = where[j * 2];
            from[i * 2 + 1] = where[j * 2 + 1];
          } else {
            newborn[i] = 1;
            arriving++;
          }
        }
        if (arriving > 0) fresh = newborn;
      } else if (decoded.count > 0) {
        // the first picture of a query: every card of it is new, so the whole of it washes in
        fresh = new Uint8Array(decoded.count).fill(1);
      }
      f.setCards(decoded.count, from, layout.positions, fresh);
      media.current?.setCards(decoded.ids);
      setSelectedIndex(-1);
      // the camera keeps pace with the cards: it arrives on the new picture as the last of them do
      f.fit(fitBounds(layout), fitPadding, prev !== null ? transitionSeconds : 0);
    } else if (!sameValues(prev.layout.positions, layout.positions)) {
      f.moveTo(layout.positions);
      f.fit(fitBounds(layout), fitPadding, transitionSeconds);
    }
    previous.current = { decoded, layout };
    media.current?.setLayout(layout);
    const colors = paletteBytes(colorData, palette, theme);
    f.setGroups(colorData ? colorData.assignment : new Uint16Array(decoded.count), colors);
    f.setShapes(shapeData ? cardShapes(shapeData, decoded.count) : null);
    labelColors.current = { assignment: colorData ? colorData.assignment : null, palette: colors, shaped: shapeData !== null, panel: theme.panel };
    setBars(layout.bars ? layout.bars.map((bar) => ({ bar, group: barData!.groups[bar.group] })) : []);
    setTooltip(null);
  }, [decoded, colorData, barData, shapeData, theme, palette]);

  // the form closed: the card it showed is no longer the one being looked at
  useEffect(() => {
    if (selected === null) {
      setSelectedIndex(-1);
      field.current?.setSelected(-1);
    }
  }, [selected]);

  function fitBounds(layout: Layout) {
    const b = layout.bounds;
    // the bars stand on their labels, which need a strip of the picture below the baseline
    return layout.bars ? { ...b, y1: b.y1 + (b.y1 - b.y0) * 0.16 } : b;
  }

  function fitToLayout(seconds: number) {
    const f = field.current;
    const layout = layoutRef.current;
    if (f && layout) f.fit(fitBounds(layout), fitPadding, seconds);
  }

  // The labels under the bars, placed straight on the elements from the camera of the frame just
  // drawn. When the bars are narrower than a label, every n-th label is shown and given the room of
  // the n bars it stands under, so labels never overlap and the ones shown are always readable.
  function placeLabels() {
    const f = field.current;
    const host = labelsRef.current;
    const layout = layoutRef.current;
    if (!f || !host || !layout?.bars) return;
    const bars = layout.bars;
    const pitch = bars.length > 1 ? bars[1].x0 - bars[0].x0 : bars[0].x1 - bars[0].x0 + 1;
    const pitchCss = pitch * f.camera().zoom;
    const step = Math.max(1, Math.ceil(labelMinWidth / Math.max(1, pitchCss)));
    const children = host.children;
    for (let i = 0; i < children.length && i < bars.length; i++) {
      const el = children[i] as HTMLElement;
      const bar = bars[i];
      if (i % step !== 0) {
        el.style.visibility = "hidden";
        continue;
      }
      const [x0, y] = f.worldToCss(bar.x0, 0);
      const width = Math.max(pitchCss * step - 6, (bar.x1 - bar.x0) * f.camera().zoom);
      el.style.visibility = "visible";
      el.style.transform = `translate(${x0.toFixed(1)}px, ${(y + 5).toFixed(1)}px)`;
      el.style.width = width.toFixed(1) + "px";
    }
  }

  // ---- the pointer ----

  function canvasPoint(e: React.PointerEvent): [number, number] {
    const rect = e.currentTarget.getBoundingClientRect();
    return [e.clientX - rect.left, e.clientY - rect.top];
  }

  function onPointerDown(e: React.PointerEvent<HTMLCanvasElement>) {
    if (e.button !== 0) return;
    const [x, y] = canvasPoint(e);
    drag.current = { x, y, t: performance.now(), moved: false, vx: 0, vy: 0 };
    e.currentTarget.setPointerCapture(e.pointerId);
    field.current?.setHover(-1);
    setTooltip(null);
  }

  function onPointerMove(e: React.PointerEvent<HTMLCanvasElement>) {
    const f = field.current;
    if (!f) return;
    const [x, y] = canvasPoint(e);
    const d = drag.current;
    if (d) {
      const now = performance.now();
      const dt = Math.max(1, now - d.t) / 1000;
      const dx = x - d.x;
      const dy = y - d.y;
      if (!d.moved && Math.hypot(dx, dy) > 4) d.moved = true;
      if (d.moved) f.panBy(dx, dy);
      // a running estimate of the speed, for the coast after the drag ends
      d.vx = d.vx * 0.6 + (dx / dt) * 0.4;
      d.vy = d.vy * 0.6 + (dy / dt) * 0.4;
      d.x = x;
      d.y = y;
      d.t = now;
      return;
    }
    // the card under the pointer, asked of the GPU no more than about thirty times a second
    const now = performance.now();
    if (now - lastHoverPick.current < 33) return;
    lastHoverPick.current = now;
    const i = f.pick(x, y);
    f.setHover(i);
    if (i < 0 || !decoded) {
      setTooltip(null);
      return;
    }
    const lines: string[] = [];
    const name = media.current?.nameOf(i);
    if (name) lines.push(name);
    for (const p of [colorData, shapeData, barData]) {
      if (!p || lines.some((l) => l.startsWith(p.name + ": "))) continue;
      lines.push(p.name + ": " + p.groups[p.assignment[i]].label);
    }
    setTooltip({ x, y, lines });
  }

  function onPointerUp(e: React.PointerEvent<HTMLCanvasElement>) {
    const f = field.current;
    const d = drag.current;
    drag.current = null;
    if (!f || !d) return;
    if (d.moved) {
      // a hand that stopped before letting go leaves the picture where it is
      if (performance.now() - d.t < 80) f.fling(d.vx, d.vy);
      return;
    }
    const [x, y] = canvasPoint(e);
    const i = f.pick(x, y);
    if (i < 0 || !decoded) return;
    setSelectedIndex(i);
    f.setSelected(i);
    fetchNodeGuid(base.storeId, decoded.ids[i])
      .then((r) => onOpen(r.id))
      .catch(() => {
        setSelectedIndex(-1);
        f.setSelected(-1);
      });
  }

  function onPointerLeave() {
    field.current?.setHover(-1);
    setTooltip(null);
  }

  if (modelError) return <div className="query-error">{modelError}</div>;
  if (!model) return null;
  const colorInfo = groupable.find((p) => p.id === colorProperty);
  const barInfo = groupable.find((p) => p.id === barProperty);
  const shapeInfo = groupable.find((p) => p.id === shapeProperty);
  const propertySelect = (value: string | null, none: string, title: string, onPick: (id: string | null) => void) => (
    <select className="select" value={value ?? ""} title={title} onChange={(e) => onPick(e.target.value || null)}>
      <option value="">{none}</option>
      {groupable.map((p) => (
        <option key={p.id} value={p.id}>
          {p.name}
          {p.declaredBy ? " (" + p.declaredBy + ")" : ""}
        </option>
      ))}
    </select>
  );
  const modeSelect = (property: PivotProperty | undefined, value: string, onPick: (mode: string) => void) =>
    property && hasModes(property) ? (
      <select className="select" value={value} title="How the values are grouped: one group per value, or ranges of them" onChange={(e) => onPick(e.target.value)}>
        {modeOptions.map((m) => (
          <option key={m.value} value={m.value}>
            {m.label}
          </option>
        ))}
      </select>
    ) : null;
  return (
    <div className="visual">
      <div className="pivot-builder">
        <div className="pivot-builder-row">
          <span className="pivot-builder-label">Colour by</span>
          <span className="pivot-chip">
            {propertySelect(colorProperty, "(one colour)", "The property whose values colour the cards", (id) => onChange({ ...def, colorProperty: id }))}
            {modeSelect(colorInfo, def.colorMode, (mode) => onChange({ ...def, colorMode: mode }))}
            <select className="select" value={def.palette ?? palettes[0].id} title="The colours the cards are painted with" onChange={(e) => onChange({ ...def, palette: e.target.value })}>
              {palettes.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          </span>
          <span className="pivot-builder-label visual-label-2">Shape by</span>
          <span className="pivot-chip">
            {propertySelect(shapeProperty, "(one shape)", "The property whose values give the cards their shapes; without one every card is a square", (id) => onChange({ ...def, shapeProperty: id }))}
            {modeSelect(shapeInfo, shapeMode, (mode) => onChange({ ...def, shapeMode: mode }))}
          </span>
          <span className="pivot-builder-label visual-label-2">Bars by</span>
          <span className="pivot-chip">
            {propertySelect(barProperty, "(grid)", "The property whose values the cards are stacked into bars by; without one they form a grid", (id) => onChange({ ...def, barProperty: id }))}
            {modeSelect(barInfo, def.barMode, (mode) => onChange({ ...def, barMode: mode }))}
          </span>
          <span className="pivot-builder-label visual-label-2">Sort by</span>
          <span className="pivot-chip">
            <select
              className="select"
              value={sortProperty ?? ""}
              title="The property the cards are laid in the order of: along the grid, and up each bar; without one they keep the order the search found them in"
              onChange={(e) => onChange({ ...def, sortProperty: e.target.value || null })}
            >
              <option value="">(result order)</option>
              {sortable.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                  {p.declaredBy ? " (" + p.declaredBy + ")" : ""}
                </option>
              ))}
            </select>
            {sortProperty && (
              <button
                className="icon-button"
                title={sortDescending ? "Largest first — click for smallest first" : "Smallest first — click for largest first"}
                onClick={() => onChange({ ...def, sortDescending: !sortDescending })}
              >
                {sortDescending ? <IconArrowNarrowDown size={14} stroke={2} /> : <IconArrowNarrowUp size={14} stroke={2} />}
              </button>
            )}
          </span>
          <div className="pivot-options">
            <button className="icon-button" title="Fit the whole picture in view (or double-click it)" onClick={() => fitToLayout(refitSeconds)}>
              <IconFocusCentered size={16} stroke={1.9} />
            </button>
            <button className={"icon-button" + (def.legend ? " active" : "")} title={def.legend ? "Hide the legend" : "Show the legend"} onClick={() => onChange({ ...def, legend: !def.legend })}>
              <IconListDetails size={16} stroke={1.9} />
            </button>
          </div>
        </div>
      </div>

      {showQuery && result && <div className="query-string">{formatQuery(result.query)}</div>}
      {error && <div className="query-error">{error}</div>}

      <div className="pivot-head">
        {decoded ? (
          <span>
            <strong>{formatCount(decoded.count)}</strong> {decoded.count === 1 ? "card" : "cards"}
            {decoded.count < decoded.total && <span className="query-filters"> the first {formatCount(decoded.count)} of {formatCount(decoded.total)}</span>}
            {" · "}
            {result!.durationMs.toFixed(1)} ms
            {loading && " · updating…"}
          </span>
        ) : (
          <span>{loading ? "Loading the cards…" : ""}</span>
        )}
        <div className="query-spacer" />
        <span className="muted">drag to pan · wheel to zoom · click a card to open it</span>
      </div>

      <div className="visual-stage" ref={stageRef}>
        <div className="visual-canvas">
          {glOk ? (
            <>
              <canvas ref={canvasRef} onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerUp={onPointerUp} onPointerCancel={onPointerUp} onPointerLeave={onPointerLeave} onDoubleClick={() => fitToLayout(refitSeconds)} />
              <canvas className="visual-text" ref={textRef} />
            </>
          ) : (
            <div className="query-empty">This browser has no WebGL 2, which the picture is drawn with.</div>
          )}
          <div className="visual-labels" ref={labelsRef}>
            {bars.map(({ bar, group }) => (
              <div className="visual-label" key={bar.group} style={{ visibility: "hidden" }} title={group.label + " · " + formatCount(bar.count)}>
                <span className="visual-label-name">{group.label}</span>
                <span className="visual-label-count">{formatCount(bar.count)}</span>
              </div>
            ))}
          </div>
          {tooltip && tooltip.lines.length > 0 && (
            <div className="visual-tooltip" style={{ transform: `translate(${tooltip.x + 14}px, ${tooltip.y + 14}px)` }}>
              {tooltip.lines.map((line) => (
                <div key={line}>{line}</div>
              ))}
            </div>
          )}
          {decoded && decoded.count === 0 && <div className="visual-empty">Nothing matched.</div>}
          {selectedIndex >= 0 && <span className="visual-selected-note">card {formatCount(selectedIndex + 1)} open</span>}
        </div>
        {def.legend && theme && (colorData || shapeData) && (
          <div className="visual-legend">
            {colorData && (
              <>
                <div className="visual-legend-head">{colorData.name}</div>
                {colorData.groups.map((g, i) => (
                  <button className="visual-legend-item" key={"colour" + i} title={groupTitle(g)} onClick={() => field.current?.pulseGroup(i)}>
                    {/* the swatch carries the shape as well when the picture is shaped by the same property, so one row says all of it */}
                    <span className={"visual-swatch" + (shapeData?.propertyId === colorData.propertyId ? " shaped" : "")} style={{ background: swatchCss(g, palette, theme), ...maskOf(shapeData?.propertyId === colorData.propertyId ? g.shape : null) }} />
                    <span className="visual-legend-label">{g.label}</span>
                    <span className="visual-legend-count">{formatCount(g.count)}</span>
                  </button>
                ))}
              </>
            )}
            {shapeData && shapeData.propertyId !== colorData?.propertyId && (
              <>
                <div className="visual-legend-head">{shapeData.name}</div>
                {shapeData.groups.map((g, i) => (
                  <button className="visual-legend-item" key={"shape" + i} title={groupTitle(g) + " \u2014 " + shapeLabel(g.shape).toLowerCase()} onClick={() => field.current?.pulseShape(g.shape)}>
                    <span className="visual-swatch shaped" style={maskOf(g.shape)} />
                    <span className="visual-legend-label">{g.label}</span>
                    <span className="visual-legend-count">{formatCount(g.count)}</span>
                  </button>
                ))}
              </>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

/** Element-wise equality of two typed arrays: a pass over a million in a millisecond or two. */
function sameValues(a: Int32Array | Float32Array, b: Int32Array | Float32Array): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
  return true;
}

/** Numbers, dates and durations can be grouped by ranges as well as by value. */
function hasModes(property: PivotProperty): boolean {
  return property.numeric || property.isDate || property.type === "TimeSpan";
}

function decode(result: VisualResult): Decoded {
  const idBytes = bytesOf(result.ids);
  const ids = new Int32Array(idBytes.buffer, idBytes.byteOffset, result.count);
  let order: Int32Array | null = null;
  if (result.order) {
    const orderBytes = bytesOf(result.order);
    if (orderBytes.length === result.count * 4) order = new Int32Array(orderBytes.buffer, orderBytes.byteOffset, result.count);
  }
  const byProperty = new Map<string, DecodedProperty>();
  for (const p of result.properties) {
    const bytes = bytesOf(p.assignment);
    const assignment = new Uint16Array(bytes.buffer, bytes.byteOffset, result.count);
    const groups: DecodedGroup[] = p.groups.map((g) => ({
      ...g,
      kind: g.value === null && g.value2 === null ? "none" : "value",
      ordinal: -1,
      shape: 0,
    }));
    paletteSlots(groups);
    shapeSlots(groups);
    if (p.unassigned > 0) {
      // the cards outside the groups kept become one group of their own, so every card has a place
      const other = groups.length;
      groups.push({ label: "(other)", value: null, value2: null, count: p.unassigned, kind: "other", ordinal: -1, shape: 0 });
      for (let i = 0; i < assignment.length; i++) if (assignment[i] === 0xffff) assignment[i] = other;
    }
    byProperty.set(p.propertyId, { propertyId: p.propertyId, name: p.name, groups, assignment });
  }
  return { count: result.count, total: result.total, ids, order, byProperty };
}

/**
 * Which colour of the palette each value group gets. The slot is hashed from the value itself rather
 * than counted off the list, so a value keeps its colour when the picture is narrowed to fewer
 * groups - the brand that was blue stays blue with the others filtered out - and two values that
 * hash to the same slot are moved apart by probing, which only shifts colours when a property has
 * a great many values.
 */
function paletteSlots(groups: DecodedGroup[]) {
  const taken = new Set<number>();
  for (const g of groups) {
    if (g.kind !== "value") continue;
    let slot = hashText((g.value ?? "") + "|" + (g.value2 ?? "")) % paletteSize;
    while (taken.has(slot)) slot = (slot + 1) % paletteSize;
    taken.add(slot);
    g.ordinal = slot;
  }
}

/**
 * Which silhouette each value gets when the picture is shaped by this property. Hashed from the
 * value like the colour is, and for the same reason - a value keeps its shape when the picture is
 * narrowed - but handed out through shapeSlotFor, which gives out whole shapes before it starts
 * turning and shrinking them. The nodes with no value, and the ones outside the groups kept, stay
 * plain cards: the plain card is what "nothing to say here" looks like.
 */
function shapeSlots(groups: DecodedGroup[]) {
  const taken = new Set<number>();
  for (const g of groups) {
    if (g.kind !== "value") continue;
    g.shape = shapeSlotFor(hashText((g.value ?? "") + "|" + (g.value2 ?? "")), taken);
    taken.add(g.shape);
  }
}

/** FNV-1a, 32 bits: the same slot for the same value on every visit. */
function hashText(text: string): number {
  let h = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i);
    h = Math.imul(h, 0x01000193) >>> 0;
  }
  return h >>> 0;
}

function groupColor(g: DecodedGroup, palette: PaletteColor[], theme: Theme): [number, number, number] {
  if (g.kind === "none") return theme.none;
  if (g.kind === "other") return theme.other;
  return palette[g.ordinal % palette.length].rgb;
}

function swatchCss(g: DecodedGroup, palette: PaletteColor[], theme: Theme): string {
  const [r, gg, b] = groupColor(g, palette, theme);
  return `rgb(${r} ${gg} ${b})`;
}

function groupTitle(g: DecodedGroup): string {
  const what = g.kind === "other" ? "The values with fewer nodes than the groups kept" : g.kind === "none" ? "The nodes without a value" : g.label;
  return what + " — click to see where these cards are";
}

/** The legend's swatch wears the silhouette as a mask, so it is the shape its cards are cut out to, in whatever colour the swatch is painted. */
function maskOf(slot: number | null): React.CSSProperties {
  if (slot === null) return {};
  const url = `url("${shapeMaskUrl(slot)}")`;
  return { maskImage: url, WebkitMaskImage: url };
}

/** The silhouette of every card: its group's, looked up through a table of the groups' own. */
function cardShapes(property: DecodedProperty, count: number): Uint16Array {
  const byGroup = new Uint16Array(property.groups.length);
  property.groups.forEach((g, i) => (byGroup[i] = g.shape));
  const shapes = new Uint16Array(count);
  for (let i = 0; i < count; i++) shapes[i] = byGroup[property.assignment[i]];
  return shapes;
}

/** The palette texture: rgba bytes per group of the colour property, or one colour when there is none. */
function paletteBytes(property: DecodedProperty | null, palette: PaletteColor[], theme: Theme): Uint8Array {
  if (!property) {
    const [r, g, b] = palette[0].rgb;
    return new Uint8Array([r, g, b, 255]);
  }
  const bytes = new Uint8Array(property.groups.length * 4);
  property.groups.forEach((g, i) => {
    const [r, gg, b] = groupColor(g, palette, theme);
    bytes[i * 4] = r;
    bytes[i * 4 + 1] = gg;
    bytes[i * 4 + 2] = b;
    bytes[i * 4 + 3] = 255;
  });
  return bytes;
}

/** The page's colours, read from the stylesheet so the picture follows the theme. */
function readTheme(el: HTMLElement): Theme {
  const cs = getComputedStyle(el);
  const v = (name: string, fallback: string) => cs.getPropertyValue(name).trim() || fallback;
  const panel = parseCssColor(v("--panel", "#ffffff"));
  const text = parseCssColor(v("--text", "#1d1c1a"));
  const accent = parseCssColor(v("--accent", "#0960b2"));
  const faint = parseCssColor(v("--text-faint", "#a6a39d"));
  const border = parseCssColor(v("--border", "#c6c1b9"));
  const f = (c: RGB): RGBf => [c[0] / 255, c[1] / 255, c[2] / 255];
  return {
    panel,
    accent,
    clear: f(panel),
    outline: f(accent),
    ink: f(text),
    // the nodes without a value, and the ones outside the groups kept: two greys the palette does not use
    none: faint,
    other: border,
  };
}
