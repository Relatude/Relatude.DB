import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { IconArrowNarrowDown, IconArrowNarrowUp, IconFocusCentered, IconListDetails } from "@tabler/icons-react";
import type { PivotBase } from "./PivotView";
import { bytesOf, fetchNodeGuid, fetchPivotModel, runVisual, type PivotModel, type PivotProperty, type VisualGroup, type VisualRequest, type VisualResult } from "../server/query";
import { useLiveResult } from "../server/hooks";
import { formatCount, formatQuery } from "../format";
import { showConfirm } from "../dialogs";
import type { VisualDefinition } from "../queryTabs";
import { createCardField, transitionSeconds, type CardField, type CardFieldCommon, type FieldSurface, type FieldTheme, type RGBf } from "../visual/cardField";
import { createCardField3D, defaultDepth, DetailLevel, type CardField3D } from "../visual/cardField3d";
import { barLayout, gridLayout, type Bar, type DepthGrouping, type Layout, type Row } from "../visual/layouts";
import { buildPalette, palettes, parseCssColor, type PaletteColor, type RGB } from "../visual/palette";
import { shapeLabel, shapeMaskUrl, shapeSlotFor } from "../visual/shapes";
import { IntMap } from "../visual/intMap";
import { createCardMedia, type CardMedia } from "../visual/cardMedia";
import { createCardLabels, type CardLabels, type LabelColors } from "../visual/cardLabels";

/** A visual pivot before anyone has chosen anything: a grid of one colour, in the result's order. */
export const emptyVisual: VisualDefinition = { colorProperty: null, colorMode: "auto", shapeProperty: null, shapeMode: "auto", depthProperty: null, depthMode: "auto", depthGroupProperty: null, depthGroupMode: "auto", barProperty: null, barMode: "auto", sortProperty: null, sortDescending: false, legend: true, palette: palettes[0].id };

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

/** Where a name is written: the far end of its line on the floor, in the picture's own coordinates. */
interface Anchor {
  x: number;
  y: number;
  z: number;
}

interface Tooltip {
  x: number;
  y: number;
  lines: string[];
}

/** what the pointer is doing between down and up; `kind` is which camera channel it holds in 3D */
interface Drag {
  x: number;
  y: number;
  t: number;
  moved: boolean;
  vx: number;
  vy: number;
  kind: "flat" | "orbit" | "pan" | "look";
}

const modeOptions = [
  { value: "auto", label: "auto" },
  { value: "values", label: "values" },
  { value: "ranges", label: "ranges" },
];
const paletteSize = 512; // above the server's cap of groups per property
const fitPadding = 28;
/**
 * and the margin a picture of solids is fitted with. Smaller, because a solid picture already keeps
 * room round itself: what is fitted is the box the cards stand in, and a box seen at an angle takes
 * up more of the view than the cards inside it do.
 */
const solidFitPadding = 10;
const labelMinWidth = 64; // css px a bar label needs before its neighbours are thinned out
/** the clear space a name in a solid picture keeps around itself, in css pixels */
const namePad = 3;
/** how far past the picture a line on the floor runs to reach its name, as a share of the picture */
const leadShare = 0.07;
const refitSeconds = 0.7; // a fit asked for on its own - the button, a double-click, a resize - with nothing else moving
/**
 * How thick a card can be, in cells of the grid, so 1 is as deep as a card is wide. The thinnest is
 * a card that still reads as a solid seen edge on; the thickest is a tower two cards deep, which is
 * as far as a picture can go before the near ones start hiding the ones behind them.
 */
const depthMin = 0.16;
const depthMax = 2;
/**
 * How many cards are enough to ask before the picture is drawn as solids. A card is six triangles
 * there rather than two, with a face to shade rather than a quad, so a set that the flat picture
 * carries comfortably can be several times the work - and the browser has no way back from a frame
 * it has already begun. Past this the question is put; the guard in cardField3d.ts then gives up
 * detail on its own if the frames still run long.
 */
const heavyCards = 250_000;
/** and past this the raymarched solid is not offered at all until the frames prove there is room for it */
const veryHeavyCards = 600_000;
/** a frame this long, with every bit of detail already given up, is a picture this machine cannot draw */
const hopelessFrameMs = 130;

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
 * Two things turn the picture into a picture of solids, and either alone will do it: a thickness
 * property, which says how deep each card is, and a depth grouping, which lays the cards in rows one
 * behind another - bars by one property across and rows by another into the distance, which is a bar
 * chart on two axes. Solids are seen through a camera that orbits (drag), slides (shift-drag, or the
 * right button) and closes in on what is under the pointer (the wheel).
 *
 * That is a second renderer (visual/cardField3d.ts) on a canvas of its own rather than a mode of the
 * first, so the flat picture goes on costing exactly what it did; only one of the two exists at a
 * time, and switching builds the other. Everything else about the picture - what the server is
 * asked, how a result is decoded, the layouts, the palette, the pictures on the cards, the legend -
 * is shared, which is why turning depth on and off does not move a card sideways.
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
  // and the same for the two depth channels, either of which makes the picture a solid one
  const depthProperty = groupable.some((p) => p.id === def.depthProperty) ? def.depthProperty! : null;
  const depthMode = def.depthMode ?? "auto";
  const rowProperty = groupable.some((p) => p.id === def.depthGroupProperty) ? def.depthGroupProperty! : null;
  const rowMode = def.depthGroupMode ?? "auto";
  const solidPicture = depthProperty !== null || rowProperty !== null;
  // sorting needs a single value per node with an order to it, which is what an indexed scalar is
  const sortable = useMemo(() => model?.properties.filter((p) => p.aggregatable) ?? [], [model]);
  // read defensively: a definition saved before there was a sort has neither field
  const sortProperty = sortable.some((p) => p.id === def.sortProperty) ? def.sortProperty! : null;
  const sortDescending = def.sortDescending === true;

  const request = useMemo<VisualRequest | null>(() => {
    if (model === null || definition === null) return null;
    const properties: { propertyId: string; mode: string }[] = [];
    const asked = new Set<string>();
    // one grouping per property however many channels it feeds: the answer is the same either way
    const ask = (id: string | null, mode: string) => {
      if (id === null || asked.has(id)) return;
      asked.add(id);
      properties.push({ propertyId: id, mode });
    };
    ask(colorProperty, def.colorMode);
    ask(shapeProperty, shapeMode);
    ask(depthProperty, depthMode);
    ask(rowProperty, rowMode);
    ask(barProperty, def.barMode);
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
  }, [model, definition === null, base, colorProperty, def.colorMode, shapeProperty, shapeMode, depthProperty, depthMode, rowProperty, rowMode, barProperty, def.barMode, sortProperty, sortDescending, refreshToken]);
  const { result, loading, error } = useLiveResult(request, runVisual);
  const decoded = useMemo(() => (result ? decode(result) : null), [result]);

  // ---- the canvas and the field ----

  const stageRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const textRef = useRef<HTMLCanvasElement>(null);
  const labelsRef = useRef<HTMLDivElement>(null);
  const rowLabelsRef = useRef<HTMLDivElement>(null);
  /** whichever of the two renderers is drawing: everything the picture is driven with is common to both */
  const field = useRef<(CardFieldCommon & FieldSurface) | null>(null);
  /** and the one that is drawing, when it is that one: the camera is worked differently in each */
  const flat = useRef<CardField | null>(null);
  const solid = useRef<CardField3D | null>(null);
  // the names and pictures of the cards in view, and the names drawn over the picture
  const media = useRef<CardMedia | null>(null);
  const labels = useRef<CardLabels | null>(null);
  const labelColors = useRef<LabelColors | null>(null);
  const [glOk, setGlOk] = useState(true);
  const [theme, setTheme] = useState<Theme | null>(null);
  const [bars, setBars] = useState<{ bar: Bar; group: DecodedGroup }[]>([]);
  const [rowLabels, setRowLabels] = useState<{ row: Row; group: DecodedGroup }[]>([]);
  /** where the names go in a picture of solids, in the same order as `bars` and `rowLabels` */
  const anchors = useRef<{ bars: Anchor[]; rows: Anchor[] }>({ bars: [], rows: [] });
  const layoutRef = useRef<Layout | null>(null);
  const [tooltip, setTooltip] = useState<Tooltip | null>(null);
  const [selectedIndex, setSelectedIndex] = useState(-1);
  /** why the solids are being drawn more simply than they can be, or null when they are not */
  const [reduced, setReduced] = useState<"size" | "frames" | null>(null);
  /** whether the way back to the flat picture has already been offered for this picture */
  const askedTheWayBack = useRef(false);
  /** and the offer itself, kept fresh: the watcher that calls it was set up with the field, renders ago */
  const wayBack = useRef<() => void>(() => {});
  const drag = useRef<Drag | null>(null);
  const lastHoverPick = useRef(0);
  const previous = useRef<{ decoded: Decoded; layout: Layout; depths: Float32Array | null } | null>(null);
  const refit = useRef(0);
  // the canvas exists once the model is known (nothing is rendered before), so the field is made then
  const hasStage = model !== null && glOk;

  useLayoutEffect(() => {
    const stage = stageRef.current;
    const canvas = canvasRef.current;
    if (!stage || !canvas) return;
    // The canvas is a new element on either side of the switch (it carries the mode as its key), so
    // each renderer gets a drawing context of its own: a canvas hands out one context for its life.
    let f: (CardFieldCommon & FieldSurface) | null = null;
    try {
      f = solidPicture ? createCardField3D(canvas) : createCardField(canvas);
    } catch (e) {
      console.error("The visual pivot could not set up its drawing:", e);
      f = null;
    }
    if (!f) {
      setGlOk(false);
      return;
    }
    field.current = f;
    flat.current = solidPicture ? null : (f as CardField);
    solid.current = solidPicture ? (f as CardField3D) : null;
    previous.current = null;
    setReduced(null);
    askedTheWayBack.current = false;
    const t = readTheme(stage);
    setTheme(t);
    f.setTheme(t);
    f.resize();
    // a solid stops at the largest picture level: there is no part of a picture "in view" to cut a
    // sharper tile out of when what is on screen is a face seen at an angle
    const m = createCardMedia(f, base.storeId, { tiles: !solidPicture });
    media.current = m;
    // the names on the cards belong to the flat picture: they are drawn over the canvas in two
    // dimensions, and a face turned away from the camera has no upright strip to write them in
    const l = !solidPicture && textRef.current ? createCardLabels(textRef.current) : null;
    labels.current = l;
    // the labels follow the camera on every frame the field draws; the pictures of the cards in view
    // are kept coming from the same place, and the names drawn over them
    f.onFrame(() => {
      placeLabels();
      m.frame(performance.now());
      l?.draw(f!, m, labelColors.current);
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
      if (solid.current) {
        // a notch is a hundred units on most mice; a trackpad sends many small ones that add up the same
        solid.current.zoomAt(Math.max(-1, Math.min(1, step / 100)) * -1.2, e.clientX - rect.left, e.clientY - rect.top);
      } else {
        flat.current?.zoomBy(Math.exp(-step * 0.0016), e.clientX - rect.left, e.clientY - rect.top);
      }
    };
    canvas.addEventListener("wheel", wheel, { passive: false });
    // Whether the guard has had to give up detail is asked for now and then rather than watched: it
    // changes a few times in the life of a picture, and a frame must not go through React. When it
    // has given up everything it has and the frames are still long, there is nothing left for it to
    // do and the way out is offered instead - which is the last thing standing between a picture
    // someone asked for and a tab that will not answer.
    const watch = solidPicture
      ? window.setInterval(() => {
          const d = solid.current?.detail();
          if (d === undefined) return;
          // said whether the guard gave the detail up or the size of the set never allowed it: both
          // are worth knowing, and only one of them is about this machine
          setReduced(d.level >= DetailLevel.Solid ? null : d.level < d.ceiling ? "frames" : "size");
          if (d.level === DetailLevel.Flat && d.frameMs > hopelessFrameMs) wayBack.current();
        }, 1000)
      : 0;
    return () => {
      ro.disconnect();
      mo.disconnect();
      window.clearTimeout(refit.current);
      window.clearInterval(watch);
      canvas.removeEventListener("wheel", wheel);
      m.destroy();
      media.current = null;
      labels.current = null;
      f!.destroy();
      field.current = null;
      flat.current = null;
      solid.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- lives with the canvas element
  }, [hasStage, solidPicture]);

  // How much the picture may cost before it has drawn a frame anyone can measure. A very large set
  // starts as plain boxes; the guard in the field takes it up to solids if the frames allow.
  useEffect(() => {
    solid.current?.setDetailCeiling((decoded?.count ?? 0) > veryHeavyCards ? DetailLevel.Boxes : DetailLevel.Solid);
  }, [decoded]);

  /**
   * The frames are long, the picture has given up every bit of detail it has, and it is still not
   * keeping up: the only thing left is the flat picture, and that is a question rather than a
   * decision. Asked once, and not again for this picture however long it goes on taking.
   */
  wayBack.current = offerTheWayBack;

  async function offerTheWayBack() {
    if (askedTheWayBack.current) return;
    askedTheWayBack.current = true;
    const answer = await showConfirm(
      "This machine cannot draw the solids smoothly",
      "The picture is already drawn as simply as it can be and the frames are still slow. Going back to the flat picture will make it answer again; fewer cards - a search, or a facet - would let the solids back.",
      { confirmLabel: "Back to the flat picture" },
    );
    if (answer.ok) onChange({ ...def, depthProperty: null });
  }

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
  const depthNow = decoded && depthProperty ? (decoded.byProperty.get(depthProperty) ?? null) : null;
  const rowNow = decoded && rowProperty ? (decoded.byProperty.get(rowProperty) ?? null) : null;
  const lastColor = useRef<DecodedProperty | null>(null);
  const lastBar = useRef<DecodedProperty | null>(null);
  const lastShape = useRef<DecodedProperty | null>(null);
  const lastDepth = useRef<DecodedProperty | null>(null);
  const lastRow = useRef<DecodedProperty | null>(null);
  const colorData = colorNow ?? (colorProperty !== null ? lastColor.current : null);
  const barData = barNow ?? (barProperty !== null ? lastBar.current : null);
  const shapeData = shapeNow ?? (shapeProperty !== null ? lastShape.current : null);
  const depthData = depthNow ?? (depthProperty !== null ? lastDepth.current : null);
  const rowData = rowNow ?? (rowProperty !== null ? lastRow.current : null);
  lastColor.current = colorData;
  lastBar.current = barData;
  lastShape.current = shapeData;
  lastDepth.current = depthData;
  lastRow.current = rowData;

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
    // how thick every card is, when the picture is one of solids; worked out before the layout,
    // which has to leave room behind each row for the thickest card standing on it
    const depths = solid.current && depthData ? cardDepths(depthData, decoded.count) : null;
    const thickest = depths === null ? defaultDepth : depths.reduce((a, b) => (b > a ? b : a), 0);
    const grouping: DepthGrouping | null = solid.current && rowData ? { groupOf: rowData.assignment, groupCount: rowData.groups.length, clearance: thickest } : null;
    const layout = barData
      ? barLayout(decoded.count, barData.assignment, barData.groups.length, colorData?.assignment ?? null, aspect, decoded.order, grouping)
      : gridLayout(decoded.count, aspect, decoded.order, grouping);
    layoutRef.current = layout;
    const padding = solid.current ? solidFitPadding : fitPadding;
    const prev = previous.current;
    if (prev === null || !sameValues(prev.decoded.ids, decoded.ids)) {
      // A new set of cards: the ones that were already on screen leave from where they are, and the
      // ones that were not fade in where they belong - so what carried over is seen to travel and
      // what is new is seen to arrive, rather than the whole picture being replaced at once.
      let from: Float32Array | null = null;
      let fresh: Uint8Array | null = null;
      // and, when the picture is one of solids laid in rows, the row each of them leaves from: a
      // filter that empties a group moves every row behind it forward, and a card that was on screen
      // should be seen to travel there rather than to appear on its new row
      let rowsFrom: Float32Array | null = null;
      if (prev !== null && prev.decoded.count > 0 && decoded.count > 0) {
        const where = f.positions();
        const wereOn = solid.current?.rows() ?? null;
        const byId = new IntMap(prev.decoded.count);
        for (let j = 0; j < prev.decoded.count; j++) byId.set(prev.decoded.ids[j], j);
        from = new Float32Array(layout.positions);
        if (layout.rows !== null && wereOn !== null) rowsFrom = new Float32Array(layout.rows);
        const newborn = new Uint8Array(decoded.count);
        let arriving = 0;
        for (let i = 0; i < decoded.count; i++) {
          const j = byId.get(decoded.ids[i]);
          if (j >= 0) {
            from[i * 2] = where[j * 2];
            from[i * 2 + 1] = where[j * 2 + 1];
            if (rowsFrom !== null && j < wereOn!.length) rowsFrom[i] = wereOn![j];
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
      // the rows and the thickness are told before the fit, which has to know how far back the
      // picture reaches and how far it stands out
      solid.current?.setRows(layout.rows, rowsFrom);
      solid.current?.setDepths(depths, 0);
      media.current?.setCards(decoded.ids);
      setSelectedIndex(-1);
      // the camera keeps pace with the cards: it arrives on the new picture as the last of them do
      f.fit(fitBounds(layout), padding, prev !== null ? transitionSeconds : 0);
    } else if (!sameValues(prev.layout.positions, layout.positions) || !sameDepths(prev.layout.rows, layout.rows)) {
      // the same cards somewhere else: across, or into another row, or both. The rows are handed
      // over first, while the old timeline is still there for them to leave from, and the move that
      // follows is what carries them - even when nothing moves across and only the rows change.
      solid.current?.setRows(layout.rows);
      solid.current?.setDepths(depths, transitionSeconds);
      f.moveTo(layout.positions);
      f.fit(fitBounds(layout), padding, transitionSeconds);
    } else if (!sameDepths(prev.depths, depths)) {
      // the same cards in the same places, given another thickness: they grow or shrink where they
      // stand, and the camera draws back or comes in as the picture changes height
      solid.current?.setDepths(depths, transitionSeconds);
      f.fit(fitBounds(layout), padding, transitionSeconds);
    }
    previous.current = { decoded, layout, depths };
    media.current?.setLayout(layout);
    const colors = paletteBytes(colorData, palette, theme);
    f.setGroups(colorData ? colorData.assignment : new Uint16Array(decoded.count), colors);
    const shapes = shapeData ? cardShapes(shapeData, decoded.count) : null;
    f.setShapes(shapes);
    labelColors.current = { assignment: colorData ? colorData.assignment : null, palette: colors, shaped: shapeData !== null, shapes, panel: theme.panel };
    setBars(layout.bars ? layout.bars.map((bar) => ({ bar, group: barData!.groups[bar.group] })) : []);
    setRowLabels(layout.rowSlots && rowData ? layout.rowSlots.map((row) => ({ row, group: rowData.groups[row.group] })) : []);
    if (solid.current) {
      const floor = floorGeometry(layout, thickest);
      anchors.current = { bars: floor.bars, rows: floor.rows };
      solid.current.setFloorLines(floor.points);
    }
    setTooltip(null);
  }, [decoded, colorData, barData, shapeData, depthData, rowData, theme, palette]);

  // the form closed: the card it showed is no longer the one being looked at
  useEffect(() => {
    if (selected === null) {
      setSelectedIndex(-1);
      field.current?.setSelected(-1);
    }
  }, [selected]);

  /**
   * The lines on the floor of a solid picture, and where each name goes: one line under every bar,
   * running the whole depth of the picture and out past its front, and one under every row, running
   * its whole width and out past its right-hand end. The name sits at the far end of its own line.
   *
   * The lines are laid on the floor the cards stand on - y = 0 under the bars, the underside of the
   * sheet in a grid - which is a hair below their feet, so a line disappears under the block it
   * belongs to and shows in the empty floor between the blocks. That is the whole point of them: a
   * name at the end of a line that visibly comes out from under one block cannot be read as
   * belonging to another, which is what the names hanging in the air beside the picture were.
   */
  function floorGeometry(layout: Layout, thickest: number): { points: Float32Array; bars: Anchor[]; rows: Anchor[] } {
    const b = layout.bounds;
    const back = b.z0 ?? 0;
    const front = (b.z1 ?? 0) + thickest;
    const lead = Math.max(0.8, Math.max(b.x1 - b.x0, front - back) * leadShare);
    const floor = b.y1;
    const points: number[] = [];
    const bars: Anchor[] = [];
    const rows: Anchor[] = [];
    // the shader takes world coordinates, where the depth of the layout is up
    const line = (x0: number, z0: number, x1: number, z1: number) => points.push(x0, -floor, z0, x1, -floor, z1);
    if (layout.bars) {
      for (const bar of layout.bars) {
        const middle = (bar.x0 + bar.x1) / 2;
        line(middle, back, middle, front + lead);
        bars.push({ x: middle, y: floor, z: front + lead });
      }
    }
    if (layout.rowSlots) {
      for (const row of layout.rowSlots) {
        // a row is several cells deep in a chart of bars, and its line runs down the middle of it
        const middle = row.z - (layout.rowDepth - 1) / 2;
        line(b.x0 - lead * 0.35, middle, b.x1 + lead, middle);
        rows.push({ x: b.x1 + lead, y: floor, z: middle });
      }
    }
    return { points: new Float32Array(points), bars, rows };
  }

  function fitBounds(layout: Layout) {
    const b = layout.bounds;
    const room = { ...b };
    if (solid.current) {
      // the names are at the ends of the lines on the floor, which run out past the front of the
      // picture and past its right-hand end: both need room in view
      const span = Math.max(1, b.x1 - b.x0);
      if (layout.bars) room.z1 = (b.z1 ?? 0) + span * (leadShare + 0.05);
      if (layout.rowSlots) room.x1 = b.x1 + span * (leadShare + 0.06);
      return room;
    }
    // flat, the bars stand on their labels, which need a strip of the picture below the baseline
    if (layout.bars) room.y1 = b.y1 + (b.y1 - b.y0) * 0.16;
    return room;
  }

  function fitToLayout(seconds: number) {
    const f = field.current;
    const layout = layoutRef.current;
    if (f && layout) f.fit(fitBounds(layout), solid.current ? solidFitPadding : fitPadding, seconds);
  }

  // The names of the bars and of the rows, placed straight on the elements from the camera of the
  // frame just drawn. Flat, a name sits centred under its bar and is given the room of the bars it
  // stands under. Solid, it sits at the far end of that bar's line on the floor.
  function placeLabels() {
    placeBarLabels();
    placeRowLabels();
  }

  /**
   * Names at the ends of the lines on the floor. The ends are not evenly spaced on the screen - seen
   * at an angle the near ones are further apart than the far ones - so a name is dropped when it
   * would land on the last one shown, rather than every n-th of them being kept the way the flat
   * picture does it.
   */
  function placeAtAnchors(host: HTMLDivElement, list: Anchor[], beside: boolean) {
    const f = field.current;
    if (!f) return;
    const children = host.children;
    // What is already written, as boxes on the screen. A name is dropped when it would land on one
    // of them - measured against the whole name rather than against the point it hangs from, because
    // a name is sixty pixels wide and two of them twenty pixels apart are one illegible smudge.
    const taken: { x0: number; y0: number; x1: number; y1: number }[] = [];
    for (let i = 0; i < children.length && i < list.length; i++) {
      const el = children[i] as HTMLElement;
      // the flat picture gives its labels a width; this one lets them size to their text
      if (el.style.width) el.style.width = "";
      const [x, y] = f.worldToCss(list[i].x, list[i].y, list[i].z);
      const w = el.offsetWidth;
      const h = el.offsetHeight;
      // beside the end of the line for a row, just past the end of it for a bar
      const left = (beside ? x + 7 : x - w / 2) - namePad;
      const top = (beside ? y - h / 2 : y + 3) - namePad;
      const box = { x0: left, y0: top, x1: left + w + 2 * namePad, y1: top + h + 2 * namePad };
      let clash = false;
      for (const t of taken) {
        if (box.x0 < t.x1 && box.x1 > t.x0 && box.y0 < t.y1 && box.y1 > t.y0) {
          clash = true;
          break;
        }
      }
      if (clash) {
        el.style.visibility = "hidden";
        continue;
      }
      taken.push(box);
      el.style.visibility = "visible";
      el.style.transform = `translate(${x.toFixed(1)}px, ${y.toFixed(1)}px) translate(${beside ? "7px, -50%" : "-50%, 3px"})`;
    }
  }

  function placeRowLabels() {
    const host = rowLabelsRef.current;
    if (!host || !solid.current) return;
    placeAtAnchors(host, anchors.current.rows, true);
  }

  function placeBarLabels() {
    const f = field.current;
    const host = labelsRef.current;
    const layout = layoutRef.current;
    if (!f || !host || !layout?.bars) return;
    if (solid.current) {
      placeAtAnchors(host, anchors.current.bars, false);
      return;
    }
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
      // at the front row: z 0 is the row nearest the viewer, and a label behind the picture would be read through it
      const [x0, y] = f.worldToCss(bar.x0, 0, 0);
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

  /**
   * The mouse. Flat, a drag pans the picture and coasts on when it is let go. Solid, it works the
   * way the 3D datamodel graph does: the left button turns the picture about the point in focus, the
   * right button (or shift and the left) slides it, the middle button turns the camera where it
   * stands, and the wheel flies toward what is under the cursor.
   */
  function onPointerDown(e: React.PointerEvent<HTMLCanvasElement>) {
    const s = solid.current;
    let kind: Drag["kind"] = "flat";
    if (s) {
      if (e.button === 1 || (e.button === 0 && (e.ctrlKey || e.altKey))) kind = "look";
      else if (e.button === 2 || (e.button === 0 && e.shiftKey)) kind = "pan";
      else if (e.button === 0) kind = "orbit";
      else return;
      e.preventDefault();
      s.hold(kind);
    } else if (e.button !== 0) return;
    const [x, y] = canvasPoint(e);
    drag.current = { x, y, t: performance.now(), moved: false, vx: 0, vy: 0, kind };
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
      const s = solid.current;
      if (d.moved && s) {
        if (d.kind === "orbit") s.orbit(dx, dy, dt);
        else if (d.kind === "look") s.look(dx, dy, dt);
        else s.panBy(dx, dy, dt);
      } else if (d.moved) {
        flat.current?.panBy(dx, dy);
      }
      // a running estimate of the speed, for the coast after the drag ends
      d.vx = d.vx * 0.6 + (dx / dt) * 0.4;
      d.vy = d.vy * 0.6 + (dy / dt) * 0.4;
      d.x = x;
      d.y = y;
      d.t = now;
      return;
    }
    // the card under the pointer, asked of the GPU no more than about thirty times a second - and
    // half as often in a picture of solids, where the question is a raymarched pass over the cards
    const now = performance.now();
    if (now - lastHoverPick.current < (solid.current ? 66 : 33)) return;
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
    for (const p of [colorData, shapeData, depthData, rowData, barData]) {
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
    if (d.kind !== "flat") solid.current?.release(d.kind);
    if (d.moved) {
      // a hand that stopped before letting go leaves the picture where it is (the solid picture
      // keeps its own speed per channel, which is what release has just let go of)
      if (d.kind === "flat" && performance.now() - d.t < 80) flat.current?.fling(d.vx, d.vy);
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
  const depthInfo = groupable.find((p) => p.id === depthProperty);
  const rowInfo = groupable.find((p) => p.id === rowProperty);

  /**
   * Turning either depth channel on is what turns the picture into a picture of solids, and that is
   * several times the work of the flat one - so past a certain number of cards it is asked for
   * rather than done. A machine has no way back out of a frame it has begun, and the browser would
   * be the thing that stopped answering, so these are the two choices in the builder that are put as
   * a question.
   */
  async function chooseDepth(field: "depthProperty" | "depthGroupProperty", id: string | null) {
    const cards = decoded?.count ?? 0;
    if (id !== null && !solidPicture && cards > heavyCards) {
      const answer = await showConfirm(
        "Draw " + formatCount(cards) + " cards as solids?",
        "Every card becomes a solid with sides of its own, which is several times the work of the flat picture. On a machine without much of a graphics card this one may be slow to answer, or stop answering for a while. The picture gives up detail on its own if the frames run long.",
        { confirmLabel: "Show the solids" },
      );
      if (!answer.ok) return;
    }
    onChange({ ...def, [field]: id });
  }
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
          <span className="pivot-builder-label visual-label-2">Depth by</span>
          <span className="pivot-chip">
            {propertySelect(depthProperty, "(flat)", "The property whose values give the cards their thickness; choosing one draws the picture as solids you can turn", (id) => chooseDepth("depthProperty", id))}
            {modeSelect(depthInfo, depthMode, (mode) => onChange({ ...def, depthMode: mode }))}
          </span>
          <span className="pivot-builder-label visual-label-2">Depth grouping</span>
          <span className="pivot-chip">
            {propertySelect(
              rowProperty,
              "(one row)",
              "The property whose values lay the cards in rows one behind another, into the distance — a second axis for the bars; choosing one draws the picture as solids you can turn",
              (id) => chooseDepth("depthGroupProperty", id),
            )}
            {modeSelect(rowInfo, rowMode, (mode) => onChange({ ...def, depthGroupMode: mode }))}
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
        <span className="muted">{solidPicture ? "drag to turn · shift-drag or right-drag to slide · wheel to zoom · click a card to open it" : "drag to pan · wheel to zoom · click a card to open it"}</span>
      </div>

      <div className="visual-stage" ref={stageRef}>
        <div className="visual-canvas">
          {glOk ? (
            <>
              {/* the mode is the canvas' key: a canvas hands out one drawing context for its life, so each renderer gets an element of its own */}
              <canvas
                key={solidPicture ? "solid" : "flat"}
                ref={canvasRef}
                onPointerDown={onPointerDown}
                onPointerMove={onPointerMove}
                onPointerUp={onPointerUp}
                onPointerCancel={onPointerUp}
                onPointerLeave={onPointerLeave}
                onDoubleClick={() => fitToLayout(refitSeconds)}
                onContextMenu={(e) => solidPicture && e.preventDefault()}
              />
              {!solidPicture && <canvas className="visual-text" ref={textRef} />}
            </>
          ) : (
            <div className="query-empty">This browser has no WebGL 2, which the picture is drawn with.</div>
          )}
          <div className={"visual-labels" + (solidPicture ? " outlined" : "")} ref={labelsRef}>
            {bars.map(({ bar, group }) => (
              <div className="visual-label" key={bar.group} style={{ visibility: "hidden" }} title={group.label + " · " + formatCount(bar.count)}>
                <span className="visual-label-name">{group.label}</span>
                <span className="visual-label-count">{formatCount(bar.count)}</span>
              </div>
            ))}
          </div>
          <div className={"visual-labels" + (solidPicture ? " outlined" : "")} ref={rowLabelsRef}>
            {rowLabels.map(({ row, group }) => (
              <div className="visual-row-label" key={row.group} style={{ visibility: "hidden" }} title={group.label + " · " + formatCount(row.count)}>
                <span className="visual-label-name">{group.label}</span>
                <span className="visual-label-count">{formatCount(row.count)}</span>
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
          {reduced && (
            <span
              className="visual-detail-note"
              title={
                reduced === "size"
                  ? "There are enough cards here that they are drawn as plain boxes with a shaded edge rather than as the shapes cut out and extruded. A smaller set - a search, or a facet - gets those back."
                  : "The frames were running long, so the solids are drawn more simply. A smaller set, or a closer view, takes the detail back."
              }
            >
              drawn simply
            </span>
          )}
        </div>
        {def.legend && theme && (colorData || shapeData || depthData) && (
          <div className="visual-legend">
            {colorData && (
              <>
                <div className="visual-legend-head">{colorData.name}</div>
                {colorData.groups.map((g, i) => (
                  <button className="visual-legend-item" key={"colour" + i} title={groupTitle(g)} onClick={() => field.current?.pulseGroup(i)}>
                    {/* the swatch carries the shape as well when the picture is shaped by the same property, so one row says all of it */}
                    <span className={"visual-swatch" + (shapeData?.propertyId === colorData.propertyId ? " shaped" : "")} style={{ background: swatchCss(g, palette, theme), ...maskOf(shapeData?.propertyId === colorData.propertyId ? g.shape : null) }} />
                    {depthData?.propertyId === colorData.propertyId && depthBar(depthData, i)}
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
                    {depthData?.propertyId === shapeData.propertyId && depthBar(depthData, i)}
                    <span className="visual-legend-label">{g.label}</span>
                    <span className="visual-legend-count">{formatCount(g.count)}</span>
                  </button>
                ))}
              </>
            )}
            {/* depth on a property of its own: the rows say how thick, and nothing else - a thickness
                is not a colour or a silhouette, so there is no swatch to blink and nothing to click */}
            {depthData && depthData.propertyId !== colorData?.propertyId && depthData.propertyId !== shapeData?.propertyId && (
              <>
                <div className="visual-legend-head">{depthData.name}</div>
                {depthData.groups.map((g, i) => (
                  <span className="visual-legend-item static" key={"depth" + i} title={groupTitle(g)}>
                    {depthBar(depthData, i)}
                    <span className="visual-legend-label">{g.label}</span>
                    <span className="visual-legend-count">{formatCount(g.count)}</span>
                  </span>
                ))}
              </>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

/**
 * How thick each group's cards are: the value groups run from the thinnest to the deepest in the
 * order the server sent them, which for ranges of a number or a date is ascending, so a deeper card
 * is a larger value. The nodes with no value, and the ones outside the groups kept, stay as thin as
 * a card gets - the same thing a plain card says in the flat picture.
 */
function cardDepths(property: DecodedProperty, count: number): Float32Array {
  const groups = property.groups;
  const rank = new Int32Array(groups.length).fill(-1);
  let ranks = 0;
  for (let g = 0; g < groups.length; g++) if (groups[g].kind === "value") rank[g] = ranks++;
  const byGroup = new Float32Array(groups.length);
  for (let g = 0; g < groups.length; g++) {
    if (rank[g] < 0) byGroup[g] = depthMin;
    // one value on its own has no scale to be read against, so it takes the middle of the range
    else byGroup[g] = ranks <= 1 ? (depthMin + depthMax) / 2 : depthMin + (depthMax - depthMin) * (rank[g] / (ranks - 1));
  }
  const out = new Float32Array(count);
  for (let i = 0; i < count; i++) out[i] = byGroup[property.assignment[i]];
  return out;
}

/** The legend's picture of a thickness: how far up the range this group's cards stand. */
function depthBar(property: DecodedProperty, group: number): React.ReactNode {
  const groups = property.groups;
  let ranks = 0;
  let rank = -1;
  for (let g = 0; g < groups.length; g++) {
    if (groups[g].kind !== "value") continue;
    if (g === group) rank = ranks;
    ranks++;
  }
  const share = rank < 0 ? 0 : ranks <= 1 ? 0.5 : rank / (ranks - 1);
  return (
    <span className="visual-depth" style={{ ["--depth" as string]: (0.12 + 0.88 * share).toFixed(3) }}>
      <i />
    </span>
  );
}

/** Element-wise equality of two typed arrays, either of which may be absent. */
function sameDepths(a: Float32Array | null, b: Float32Array | null): boolean {
  if (a === null || b === null) return a === b;
  return sameValues(a, b);
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
