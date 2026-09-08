import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { IconArrowNarrowDown, IconArrowNarrowUp, IconFocusCentered, IconListDetails, IconMinus, IconPhoto, IconPhotoOff, IconPlus } from "@tabler/icons-react";
import { FullscreenButton } from "./DatamodelGraph";
import type { PivotBase } from "./PivotView";
import { bytesOf, fetchNodeGuid, fetchPivotModel, runVisual, type PivotModel, type PivotProperty, type VisualGroup, type VisualRequest, type VisualResult } from "../server/query";
import { useLiveResult } from "../server/hooks";
import { formatCount, formatQuery } from "../format";
import { showConfirm } from "../dialogs";
import type { VisualDefinition } from "../queryTabs";
import { createCardField, transitionSeconds, type CardField, type CardFieldCommon, type FieldSurface, type FieldTheme, type RGBf } from "../visual/cardField";
import { createCardField3D, defaultDepth, DetailLevel, type CardField3D } from "../visual/cardField3d";
import { barLayout, gridLayout, type Bar, type DepthGrouping, type Layout, type Row, type Spread } from "../visual/layouts";
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
/**
 * How fast the keys work the camera: the arrows in pointer pixels a second, so they read against the
 * same measure a drag does, and the wheel's own notch is about 1.2 - so plus and minus held down are
 * a bit under three notches a second.
 */
const keySlidePxPerSecond = 560;
const keyTurnPxPerSecond = 210;
const keyZoomPerSecond = 3.2;
/** how often a held key is acted on, in milliseconds */
const keyTickMs = 16;
const ourKeys = new Set(["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Shift", "+", "=", "-", "_", "Add", "Subtract"]);
const keyIsOurs = (key: string) => ourKeys.has(key);
/** the clear space a name in a solid picture keeps around itself, in css pixels */
const namePad = 3;
/** and how far past the end of its line it stands, away from the middle of the picture */
const nameStandoff = 13;
/** how many cards may be seen flying out of the picture at once; past this a filter simply replaces them */
const maxLeaving = 150_000;
/**
 * The two sliders that stretch a solid picture. The slider runs 0 to 100 and the scale it stands for
 * doubles every `scaleSteps` of it, so the middle is the shape the layout chose for itself and the
 * ends are a tenth and ten times it. A scale is stored rather than a position, so the sliders mean
 * the same thing whatever the range is set to later.
 */
const scaleSteps = 15;
const scaleToSlider = (scale: number) => Math.round(50 + scaleSteps * Math.log2(Math.max(0.05, scale)));
const sliderToScale = (at: number) => Math.round(Math.pow(2, (at - 50) / scaleSteps) * 100) / 100;
/** how long after the last nudge of a slider the picture is laid out again */
const slideSettleMs = 140;
/** how far past the picture a line on the floor runs to reach its name, as a share of the picture */
const leadShare = 0.07;
const refitSeconds = 0.7; // a fit asked for on its own - the button, a double-click, a resize - with nothing else moving
/** how long the solid picture takes to settle back into place after its canvas has changed shape */
const driftSeconds = 0.55;
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
  fullscreen,
  onToggleFullscreen,
  head,
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
  /** Whether the row this picture is in - the facet rail with it - is filling the screen. */
  fullscreen: boolean;
  /** Fills the screen with that row, or hands it back; the page owns it, since the rail is not ours. */
  onToggleFullscreen: () => void;
  /**
   * What the result's own head would say, when the page has folded that head away and handed it here
   * instead: how many nodes were found, and the switch for the facet rail. Filling the screen with a
   * picture and then giving a line of it back to a heading is not what filling the screen is for, so
   * the page passes them down and they ride along with the picture's own controls.
   */
  head?: React.ReactNode;
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
  // The pictures on the cards, remembered once for each kind of picture: a flat field is a wall of
  // photographs and starts with them on, a field of solids is usually read as a shape and starts with
  // them off. Which of the two the switch writes follows which picture is on screen.
  const picturesOn = solidPicture ? def.solidPictures === true : def.pictures !== false;
  // thickness and shape are folded away unless asked for, or unless one of them is being used
  const inUse = depthProperty !== null || shapeProperty !== null;
  const extras = inUse || def.extras === true;
  /**
   * The two stretches. What is laid out is the committed value, and what the thumb shows is the one
   * being dragged: a nudge of a slider re-lays every card and hands the whole picture to the card
   * field again, which at a million of them is not something to do sixty times a second, so the
   * commit waits for the hand to settle. The thumb follows it all the way regardless.
   */
  const [dragged, setDragged] = useState<{ xScale?: number; depthScale?: number }>({});
  const settle = useRef(0);
  const pending = useRef<{ xScale?: number; depthScale?: number }>({});
  const xScale = def.xScale ?? 1;
  const depthScale = def.depthScale ?? 1;
  const spread: Spread = { x: xScale, z: depthScale };

  function slide(patch: { xScale?: number; depthScale?: number }) {
    pending.current = { ...pending.current, ...patch };
    setDragged({ ...dragged, ...patch });
    window.clearTimeout(settle.current);
    settle.current = window.setTimeout(() => {
      const done = pending.current;
      pending.current = {};
      setDragged({});
      onChange({ ...def, ...done });
    }, slideSettleMs);
  }
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
  // read from the frame callback, which outlives the render that set it up
  const showPictures = useRef(picturesOn);
  showPictures.current = picturesOn;
  const [glOk, setGlOk] = useState(true);
  const [theme, setTheme] = useState<Theme | null>(null);
  const [bars, setBars] = useState<{ bar: Bar; group: DecodedGroup }[]>([]);
  const [rowLabels, setRowLabels] = useState<{ row: Row; group: DecodedGroup }[]>([]);
  /** where the names go in a picture of solids, in the same order as `bars` and `rowLabels` */
  const anchors = useRef<{ bars: Anchor[]; rows: Anchor[]; middle: Anchor }>({ bars: [], rows: [], middle: { x: 0, y: 0, z: 0 } });
  /**
   * Which side of the picture the lines on the floor run out to, and the names with them: +1 or -1
   * along each axis, whichever way the camera is. Kept so it can be seen to change.
   */
  const facing = useRef<{ x: number; z: number }>({ x: 1, z: 1 });
  /** how thick the thickest card is, which is how far forward of its row the picture reaches */
  const thickestRef = useRef(defaultDepth);
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
  /** the keys being held down, the gesture they are driving, and the clock they are driven on */
  const keys = useRef(new Set<string>());
  const keyClock = useRef(0);
  const keyGesture = useRef<"pan" | "orbit" | null>(null);
  const keyTimer = useRef(0);
  const lastHoverPick = useRef(0);
  const previous = useRef<{ decoded: Decoded; layout: Layout; depths: Float32Array | null; colorData: DecodedProperty | null; shapeData: DecodedProperty | null } | null>(null);
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
    f.setPictures(showPictures.current);
    m.setPictures(showPictures.current);
    // the names on the cards belong to the flat picture: they are drawn over the canvas in two
    // dimensions, and a face turned away from the camera has no upright strip to write them in
    const l = !solidPicture && textRef.current ? createCardLabels(textRef.current) : null;
    labels.current = l;
    // the labels follow the camera on every frame the field draws; the pictures of the cards in view
    // are kept coming from the same place, and the names drawn over them
    f.onFrame(() => {
      placeLabels();
      m.frame(performance.now());
      if (showPictures.current) l?.draw(f!, m, labelColors.current);
      else l?.clear();
    });
    let was = f.size();
    const ro = new ResizeObserver(() => {
      f!.resize();
      const now = f!.size();
      const grewBy = now.width - was.width;
      const roseBy = now.height - was.height;
      was = now;
      // A dragged splitter fires this many times a second, so what is done about it waits for the
      // resizing to settle. The flat picture is fitted to whatever room it has and is simply fitted
      // again. A solid one is NOT: someone who has turned and closed in on a corner of it does not
      // want that thrown away because a form opened beside the picture. The canvas keeps its middle
      // where the middle of the canvas is, so a canvas that narrows from the right carries the
      // picture left with it - and all that is wanted is to undo that much, gently: half the width
      // it lost, panned back, so the picture stays where it was on the screen.
      window.clearTimeout(refit.current);
      refit.current = window.setTimeout(() => {
        if (solid.current) {
          if (Math.abs(grewBy) >= 1 || Math.abs(roseBy) >= 1) solid.current.driftBy(-grewBy / 2, -roseBy / 2, driftSeconds);
        } else {
          fitToLayout(refitSeconds);
        }
      }, 180);
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

  // a key held as the view goes would otherwise leave its clock running for the life of the page
  useEffect(() => () => window.clearInterval(keyTimer.current), []);

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

  // the switch, thrown on a picture that is already up (a picture built after it is told when it is
  // made, above); off, what was fetched is let go of and the cards go back to plain colour
  useEffect(() => {
    (flat.current ?? solid.current)?.setPictures(picturesOn);
    media.current?.setPictures(picturesOn);
  }, [picturesOn]);

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
    const depths = solid.current && depthData ? cardDepths(depthData, decoded.count, depthScale) : null;
    const thickest = depths === null ? defaultDepth : depths.reduce((a, b) => (b > a ? b : a), 0);
    const grouping: DepthGrouping | null = solid.current && rowData ? { groupOf: rowData.assignment, groupCount: rowData.groups.length, clearance: thickest } : null;
    const layout = barData
      ? barLayout(decoded.count, barData.assignment, barData.groups.length, colorData?.assignment ?? null, aspect, decoded.order, grouping, solid.current ? spread : undefined)
      : gridLayout(decoded.count, aspect, decoded.order, grouping, solid.current ? spread : undefined);
    layoutRef.current = layout;
    const padding = solid.current ? solidFitPadding : fitPadding;
    const prev = previous.current;
    /** the cards flying out of the picture, whose colours and silhouettes are appended to the new ones */
    let leaving: Leaving | null = null;
    if (prev === null || !sameValues(prev.decoded.ids, decoded.ids)) {
      // A new set of cards: the ones that were already on screen leave from where they are, and the
      // ones that were not fade in where they belong - so what carried over is seen to travel and
      // what is new is seen to arrive, rather than the whole picture being replaced at once.
      const flight = planFlight({
        solid: solid.current !== null,
        prev,
        decoded,
        layout,
        depths,
        where: prev !== null && prev.decoded.count > 0 ? f.positions() : null,
        wereOn: solid.current?.rows() ?? null,
        colorData,
        palette,
        theme,
      });
      leaving = flight.leaving;
      f.setCards(flight.count, flight.from, flight.to, flight.state);
      // the rows and the thickness are told before the fit, which has to know how far back the
      // picture reaches and how far it stands out
      solid.current?.setRows(flight.rows, flight.rowsFrom);
      solid.current?.setDepths(flight.depths, 0);
      // the pictures are only ever asked for the cards of the result; the ones on their way out
      // fly out in their own colour
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
    previous.current = { decoded, layout, depths, colorData, shapeData };
    media.current?.setLayout(layout);
    const colors = paletteBytes(colorData, palette, theme);
    const groups = colorData ? colorData.assignment : new Uint16Array(decoded.count);
    const shapes = shapeData ? cardShapes(shapeData, decoded.count) : null;
    if (leaving === null) {
      f.setGroups(groups, colors);
      f.setShapes(shapes);
    } else {
      // the leavers' own colours follow the new palette rather than replacing anything in it, so a
      // card flying out keeps exactly the colour it had while it was in the picture
      f.setGroups(joinGroups(groups, leaving.groups), joinBytes(colors, leaving.palette));
      f.setShapes(shapes !== null || leaving.shaped ? joinGroups(shapes ?? new Uint16Array(decoded.count), leaving.shapes) : null);
    }
    labelColors.current = { assignment: colorData ? colorData.assignment : null, palette: colors, shaped: shapeData !== null, shapes, panel: theme.panel };
    setBars(layout.bars ? layout.bars.map((bar) => ({ bar, group: barData!.groups[bar.group] })) : []);
    setRowLabels(layout.rowSlots && rowData ? layout.rowSlots.map((row) => ({ row, group: rowData.groups[row.group] })) : []);
    if (solid.current) {
      thickestRef.current = thickest;
      keepNamesFacing();
      applyFloor(layout);
    }
    setTooltip(null);
  }, [decoded, colorData, barData, shapeData, depthData, rowData, theme, palette, xScale, depthScale]);

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
  function floorGeometry(layout: Layout, thickest: number, side: { x: number; z: number }): { points: Float32Array; bars: Anchor[]; rows: Anchor[]; middle: Anchor } {
    const b = layout.bounds;
    const back = b.z0 ?? 0;
    const front = (b.z1 ?? 0) + thickest;
    const lead = Math.max(0.8, Math.max(b.x1 - b.x0, front - back) * leadShare);
    const floor = b.y1;
    // out to whichever side the camera is on: a name written on the far side of the picture is a
    // name read through it
    const zEnd = side.z > 0 ? front + lead : back - lead;
    const zStart = side.z > 0 ? back : front;
    const xEnd = side.x > 0 ? b.x1 + lead : b.x0 - lead;
    const xStart = side.x > 0 ? b.x0 - lead * 0.35 : b.x1 + lead * 0.35;
    const points: number[] = [];
    const bars: Anchor[] = [];
    const rows: Anchor[] = [];
    // the shader takes world coordinates, where the depth of the layout is up
    const line = (x0: number, z0: number, x1: number, z1: number) => points.push(x0, -floor, z0, x1, -floor, z1);
    if (layout.bars) {
      for (const bar of layout.bars) {
        const middle = (bar.x0 + bar.x1) / 2;
        line(middle, zStart, middle, zEnd);
        bars.push({ x: middle, y: floor, z: zEnd });
      }
    }
    if (layout.rowSlots) {
      for (const row of layout.rowSlots) {
        // a row is several cells deep in a chart of bars, and its line runs down the middle of it
        const middle = row.z - (layout.rowDepth - 1) / 2;
        line(xStart, middle, xEnd, middle);
        rows.push({ x: xEnd, y: floor, z: middle });
      }
    }
    return { points: new Float32Array(points), bars, rows, middle: { x: (b.x0 + b.x1) / 2, y: floor, z: (back + front) / 2 } };
  }

  /** Lays the lines on the floor and works out where the names go, for the side now facing. */
  function applyFloor(layout: Layout) {
    const s = solid.current;
    if (!s) return;
    const floor = floorGeometry(layout, thickestRef.current, facing.current);
    anchors.current = { bars: floor.bars, rows: floor.rows, middle: floor.middle };
    s.setFloorLines(floor.points);
  }

  /**
   * Turning the picture round brings another side of it toward the camera, and the names go with it:
   * they always stand off the near side, never behind the picture where they would be read through
   * it. Asked on every frame, acted on only when the answer changes, and with a margin about the
   * middle so a camera hovering over it does not flip them back and forth.
   */
  function keepNamesFacing() {
    const s = solid.current;
    const layout = layoutRef.current;
    if (!s || !layout) return;
    const b = layout.bounds;
    const eye = s.eye();
    const midX = (b.x0 + b.x1) / 2;
    const midZ = ((b.z0 ?? 0) + (b.z1 ?? 0) + thickestRef.current) / 2;
    const marginX = Math.max(0.5, (b.x1 - b.x0) * 0.06);
    const marginZ = Math.max(0.5, ((b.z1 ?? 0) - (b.z0 ?? 0) + thickestRef.current) * 0.06);
    const now = facing.current;
    const x = eye[0] > midX + marginX ? 1 : eye[0] < midX - marginX ? -1 : now.x;
    const z = eye[2] > midZ + marginZ ? 1 : eye[2] < midZ - marginZ ? -1 : now.z;
    if (x === now.x && z === now.z) return;
    facing.current = { x, z };
    applyFloor(layout);
  }

  function fitBounds(layout: Layout) {
    const b = layout.bounds;
    const room = { ...b };
    if (solid.current) {
      // the names are at the ends of the lines on the floor, which run out past the front of the
      // picture and past its right-hand end: both need room in view
      const span = Math.max(1, b.x1 - b.x0);
      const side = facing.current;
      if (layout.bars) {
        if (side.z > 0) room.z1 = (b.z1 ?? 0) + span * (leadShare + 0.05);
        else room.z0 = (b.z0 ?? 0) - span * (leadShare + 0.05);
      }
      if (layout.rowSlots) {
        if (side.x > 0) room.x1 = b.x1 + span * (leadShare + 0.06);
        else room.x0 = b.x0 - span * (leadShare + 0.06);
      }
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
    keepNamesFacing();
    placeBarLabels();
    placeRowLabels();
  }

  /**
   * Names at the ends of the lines on the floor. The ends are not evenly spaced on the screen - seen
   * at an angle the near ones are further apart than the far ones - so a name is dropped when it
   * would land on the last one shown, rather than every n-th of them being kept the way the flat
   * picture does it.
   */
  function placeAtAnchors(host: HTMLDivElement, list: Anchor[]) {
    const f = field.current;
    if (!f) return;
    const middle = anchors.current.middle;
    const [mx, my] = f.worldToCss(middle.x, middle.y, middle.z);
    const room = f.size();
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
      // Nothing to write when the end of the line has no place on the canvas: behind the camera, or
      // - a view along the floor being what it is - away toward the horizon, thousands of pixels
      // off the side. Either way there is nowhere sensible for the name to stand.
      if (!isFinite(x) || !isFinite(y) || x < -w || y < -h || x > room.width + w || y > room.height + h) {
        el.style.visibility = "hidden";
        continue;
      }
      // a step further out from the middle of the picture, along the line the name already stands
      // on: whichever way the picture has been turned, a name sits outside it rather than over it
      let ax = x - mx;
      let ay = y - my;
      const reach = Math.hypot(ax, ay);
      if (reach < 1) {
        ax = 0;
        ay = 1;
      } else {
        ax /= reach;
        ay /= reach;
      }
      const px = x + ax * nameStandoff;
      const py = y + ay * nameStandoff;
      const left = px - w / 2 - namePad;
      const top = py - h / 2 - namePad;
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
      el.style.transform = `translate(${px.toFixed(1)}px, ${py.toFixed(1)}px) translate(-50%, -50%)`;
    }
  }

  function placeRowLabels() {
    const host = rowLabelsRef.current;
    if (!host || !solid.current) return;
    placeAtAnchors(host, anchors.current.rows);
  }

  function placeBarLabels() {
    const f = field.current;
    const host = labelsRef.current;
    const layout = layoutRef.current;
    if (!f || !host || !layout?.bars) return;
    if (solid.current) {
      placeAtAnchors(host, anchors.current.bars);
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

  /**
   * The keys, applied on a clock of their own while they are held: the arrows slide the picture, the
   * arrows with shift turn it, and plus and minus close in and draw back. A held key has to be acted
   * on over and over rather than once per press, and on a timer rather than on the frames the
   * picture happens to be drawing: a picture of three quarters of a million solids may be drawing
   * five frames a second, and a hand on a key should move the camera at the same rate whether it is
   * drawing five or sixty. The camera is told where to go in real time and the picture catches up.
   *
   * They go through the same three calls the pointer does, holds and all, so a keyboard slide is
   * measured at the depth of whatever is in the middle of the view and a keyboard turn goes about
   * the same point a drag there would - which is the only reason they feel like the same camera.
   */
  function applyKeys() {
    const s = solid.current;
    const held = keys.current;
    if (!s || held.size === 0) {
      if (s !== null && keyGesture.current !== null) {
        s.release(keyGesture.current);
        keyGesture.current = null;
      }
      window.clearInterval(keyTimer.current);
      keyTimer.current = 0;
      keyClock.current = 0;
      return;
    }
    const now = performance.now();
    const dt = keyClock.current === 0 ? 1 / 60 : Math.min(0.1, (now - keyClock.current) / 1000);
    keyClock.current = now;
    const room = s.size();
    const midX = room.width / 2;
    const midY = room.height / 2;
    let dx = 0;
    let dy = 0;
    if (held.has("ArrowLeft")) dx -= 1;
    if (held.has("ArrowRight")) dx += 1;
    if (held.has("ArrowUp")) dy -= 1;
    if (held.has("ArrowDown")) dy += 1;
    const turning = held.has("Shift");
    // a hold is taken on the middle of the view, and given up when the arrows are, so switching
    // between sliding and turning takes hold afresh of whatever is there now
    const wants = dx !== 0 || dy !== 0 ? (turning ? "orbit" : "pan") : null;
    if (wants !== keyGesture.current) {
      if (keyGesture.current !== null) s.release(keyGesture.current);
      if (wants !== null) s.hold(wants, midX, midY);
      keyGesture.current = wants;
    }
    // an arrow is a hand on the picture: it takes it the way it points, the same way a drag in that
    // direction would, which is also what shift and an arrow already do to turn it
    if (wants === "orbit") s.orbit(dx * keyTurnPxPerSecond * dt, dy * keyTurnPxPerSecond * dt);
    else if (wants === "pan") s.panBy(dx * keySlidePxPerSecond * dt, dy * keySlidePxPerSecond * dt);
    const closer = held.has("+") || held.has("=") || held.has("Add");
    const further = held.has("-") || held.has("_") || held.has("Subtract");
    if (closer !== further) s.zoomAt((closer ? 1 : -1) * keyZoomPerSecond * dt, midX, midY);
    // nothing else may be moving, so the next frame has to be asked for here
    s.invalidate();
  }

  function onKeyDown(e: React.KeyboardEvent) {
    // a keypress is a gesture the browser accepts fullscreen from, and it works in either picture;
    // held down it is still one gesture, not a screen flickering in and out at the repeat rate
    if (e.code === "KeyF" && !e.repeat && !e.ctrlKey && !e.metaKey && !e.altKey) {
      e.preventDefault();
      onToggleFullscreen();
      return;
    }
    if (!solid.current || !keyIsOurs(e.key)) return;
    e.preventDefault();
    if (e.repeat) return; // the key is already held; the clock below is what repeats it
    keys.current.add(e.key === "Shift" ? "Shift" : e.key);
    if (e.shiftKey) keys.current.add("Shift");
    if (keyTimer.current === 0) keyTimer.current = window.setInterval(() => applyKeys(), keyTickMs);
    applyKeys();
  }

  function onKeyUp(e: React.KeyboardEvent) {
    if (!keyIsOurs(e.key)) return;
    keys.current.delete(e.key);
    if (!e.shiftKey) keys.current.delete("Shift");
    if (keys.current.size === 0) applyKeys();
  }

  /** Letting go of the picture lets go of every key: a key held through a blur is never released. */
  function onCanvasBlur() {
    keys.current.clear();
    applyKeys();
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
      const [hx, hy] = canvasPoint(e);
      s.hold(kind, hx, hy);
    } else if (e.button !== 0) return;
    const [x, y] = canvasPoint(e);
    drag.current = { x, y, t: performance.now(), moved: false, vx: 0, vy: 0, kind };
    // the keys are the canvas', so a hand on the picture is what gives them to it
    e.currentTarget.focus({ preventScroll: true });
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
        if (d.kind === "orbit") s.orbit(dx, dy);
        else if (d.kind === "look") s.look(dx, dy);
        else s.panBy(dx, dy);
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
    // a card flying out of the picture is still one of the field's, but it is no longer one of the
    // result's: there is nothing to say about it and nothing to open
    if (i < 0 || !decoded || i >= decoded.count) {
      f.setHover(-1);
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
    // a hand that stopped before letting go leaves the picture where it is; one still going hands
    // its speed over and the picture carries it a little way further, turning or sliding
    const carried = d.moved && performance.now() - d.t < 80;
    if (d.kind !== "flat") solid.current?.release(d.kind, carried ? d.vx : 0, carried ? d.vy : 0);
    if (d.moved) {
      if (d.kind === "flat" && carried) flat.current?.fling(d.vx, d.vy);
      return;
    }
    const [x, y] = canvasPoint(e);
    const i = f.pick(x, y);
    if (i < 0 || !decoded || i >= decoded.count) return;
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
  /** One of the two stretches: a slider whose middle is the shape the layout chose for itself. */
  const stretch = (label: string, title: string, committed: number, dragging: number | undefined, onSlide: (scale: number) => void) => {
    const scale = dragging ?? committed;
    return (
      <span className="visual-stretch" title={title}>
        <span className="visual-stretch-label">{label}</span>
        <input type="range" min={0} max={100} step={1} value={scaleToSlider(scale)} onChange={(e) => onSlide(sliderToScale(Number(e.target.value)))} />
        <span className="visual-stretch-value">{scale.toFixed(2)}×</span>
      </span>
    );
  };

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
          <span className="pivot-builder-label visual-label-2">Bars by</span>
          <span className="pivot-chip">
            {propertySelect(barProperty, "(grid)", "The property whose values the cards are stacked into bars by; without one they form a grid", (id) => onChange({ ...def, barProperty: id }))}
            {modeSelect(barInfo, def.barMode, (mode) => onChange({ ...def, barMode: mode }))}
          </span>
          <span className="pivot-builder-label visual-label-2">Depth</span>
          <span className="pivot-chip">
            {propertySelect(
              rowProperty,
              "(one row)",
              "The property whose values lay the cards in rows one behind another, into the distance — a second axis for the bars; choosing one draws the picture as solids you can turn",
              (id) => chooseDepth("depthGroupProperty", id),
            )}
            {modeSelect(rowInfo, rowMode, (mode) => onChange({ ...def, depthGroupMode: mode }))}
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
          {/* The two channels the picture can do without: how thick a card is, and what it is cut out
              to. They are folded away behind the plus at the end of the line, and unfolded when one
              of them is in use - a picture whose cards are hearts has to say somewhere why. */}
          {extras && (
            <>
              <span className="pivot-builder-label visual-label-2">Thickness</span>
              <span className="pivot-chip">
                {propertySelect(depthProperty, "(all alike)", "The property whose values give the cards their thickness; choosing one draws the picture as solids you can turn", (id) => chooseDepth("depthProperty", id))}
                {modeSelect(depthInfo, depthMode, (mode) => onChange({ ...def, depthMode: mode }))}
              </span>
              <span className="pivot-builder-label visual-label-2">Shape by</span>
              <span className="pivot-chip">
                {propertySelect(shapeProperty, "(one shape)", "The property whose values give the cards their shapes; without one every card is a square", (id) => onChange({ ...def, shapeProperty: id }))}
                {modeSelect(shapeInfo, shapeMode, (mode) => onChange({ ...def, shapeMode: mode }))}
              </span>
            </>
          )}
          {/* only in a picture of solids: there is nothing to stretch in a flat one, which is fitted
              to the panel it is drawn in */}
          {solidPicture && (
            <>
              <span className="pivot-builder-label visual-label-2">Stretch</span>
              <span className="pivot-chip">
                {stretch("Across", "How far the picture reaches across: wider bars, standing lower", xScale, dragged.xScale, (v) => slide({ xScale: v }))}
                {stretch("Deep", "How far the picture reaches back: more floor between the rows, and thicker cards", depthScale, dragged.depthScale, (v) => slide({ depthScale: v }))}
              </span>
            </>
          )}
          <div className="pivot-options">
            {head}
            {/* forced open while one of them is in use, so it cannot be folded away and forgotten */}
            <button
              className={"icon-button" + (extras ? " active" : "")}
              disabled={inUse}
              title={inUse ? "Thickness and shape are in use, so they stay on show" : extras ? "Hide thickness and shape" : "Show thickness and shape"}
              onClick={() => onChange({ ...def, extras: !extras })}
            >
              {extras ? <IconMinus size={16} stroke={1.9} /> : <IconPlus size={16} stroke={1.9} />}
            </button>
            <button className="icon-button" title="Fit the whole picture in view (or double-click it)" onClick={() => fitToLayout(refitSeconds)}>
              <IconFocusCentered size={16} stroke={1.9} />
            </button>
            <button
              className={"icon-button" + (picturesOn ? " active" : "")}
              title={
                picturesOn
                  ? "Hide the pictures: the cards stay plain blocks of colour however far you close in" + (solidPicture ? " (remembered for the picture of solids)" : " (remembered for the flat picture)")
                  : "Show the pictures: a card close enough to be read gets its own, or a placeholder with its name" + (solidPicture ? " (remembered for the picture of solids)" : " (remembered for the flat picture)")
              }
              onClick={() => onChange(solidPicture ? { ...def, solidPictures: !picturesOn } : { ...def, pictures: !picturesOn })}
            >
              {picturesOn ? <IconPhoto size={16} stroke={1.9} /> : <IconPhotoOff size={16} stroke={1.9} />}
            </button>
            <button className={"icon-button" + (def.legend ? " active" : "")} title={def.legend ? "Hide the legend" : "Show the legend"} onClick={() => onChange({ ...def, legend: !def.legend })}>
              <IconListDetails size={16} stroke={1.9} />
            </button>
            <FullscreenButton on={fullscreen} onToggle={onToggleFullscreen} what="picture" />
          </div>
        </div>
      </div>

      {showQuery && result && <div className="query-string">{formatQuery(result.query)}</div>}
      {error && <div className="query-error">{error}</div>}

      {/* No line of its own between the controls and the picture: what was found is on the result's
          own head above, and how to turn the picture is something the picture teaches by being
          dragged. Both were costing the canvas height it is better off keeping. */}
      <div className="visual-stage" ref={stageRef}>
        <div className="visual-canvas">
          {glOk ? (
            <>
              {/* the mode is the canvas' key: a canvas hands out one drawing context for its life, so each renderer gets an element of its own */}
              <canvas
                key={solidPicture ? "solid" : "flat"}
                ref={canvasRef}
                tabIndex={0}
                onKeyDown={onKeyDown}
                onKeyUp={onKeyUp}
                onBlur={onCanvasBlur}
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
          {decoded && decoded.count === 0 && !loading && <div className="visual-empty">Nothing matched.</div>}
          {/* the one thing the head above still had to say, moved onto the picture itself */}
          {loading && <span className="visual-loading-note">{decoded ? "updating…" : "loading the cards…"}</span>}
          {decoded && decoded.count < decoded.total && (
            <span className="visual-count-note" title={"The picture holds the first " + formatCount(decoded.count) + " of " + formatCount(decoded.total) + " nodes; narrow the set to see the rest."}>
              first {formatCount(decoded.count)} of {formatCount(decoded.total)}
            </span>
          )}
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
function cardDepths(property: DecodedProperty, count: number, scale: number): Float32Array {
  const groups = property.groups;
  const rank = new Int32Array(groups.length).fill(-1);
  let ranks = 0;
  for (let g = 0; g < groups.length; g++) if (groups[g].kind === "value") rank[g] = ranks++;
  const byGroup = new Float32Array(groups.length);
  for (let g = 0; g < groups.length; g++) {
    const low = depthMin * scale;
    const high = depthMax * scale;
    if (rank[g] < 0) byGroup[g] = low;
    // one value on its own has no scale to be read against, so it takes the middle of the range
    else byGroup[g] = ranks <= 1 ? (low + high) / 2 : low + (high - low) * (rank[g] / (ranks - 1));
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

/**
 * What a card is doing on the way to where it belongs: nothing (it was already there), arriving, or
 * leaving. The renderer grows an arriving card into place and shrinks a leaving one away.
 */
const CardState = { Settled: 0, Arriving: 1, Leaving: 2 } as const;

/** The cards flying out of the picture: what they are coloured and cut out to, appended to the new ones. */
interface Leaving {
  groups: Uint16Array;
  shapes: Uint16Array;
  palette: Uint8Array;
  shaped: boolean;
}

/** Where every card sets off from, where it is going, and what it is doing on the way. */
interface Flight {
  count: number;
  from: Float32Array;
  to: Float32Array;
  state: Uint8Array | null;
  rows: Float32Array | null;
  rowsFrom: Float32Array | null;
  depths: Float32Array | null;
  leaving: Leaving | null;
}

/**
 * What the cards do when the result changes.
 *
 * A card that was on screen and still is travels from where it stands to where it now belongs. In a
 * picture of solids the other two also have somewhere to be: a card the result has just gained comes
 * DOWN out of the sky onto its place, growing and colouring as it descends, and one the result has
 * just lost is DROPPED - it falls straight down under its own weight, shrinking and paling, and is
 * gone before it lands anywhere. Both start or end well outside what the camera is fitted to, so a
 * filter reads as the picture gaining and letting go of things rather than as a redraw.
 *
 * The leavers are simply appended to the set of cards. They are not in the result and nothing asks
 * them anything - no pictures, no picking - and by the end of the move they are nothing, standing
 * outside the picture at no size at all until the next result drops them. Past `maxLeaving` none of
 * them fly: a filter that takes a million cards away would otherwise double the geometry for two
 * seconds, exactly when the machine is busiest, and at that density nobody is following one card.
 *
 * The flat picture keeps what it always did: the cards it gains come up where they land.
 */
function planFlight(input: {
  solid: boolean;
  prev: { decoded: Decoded; layout: Layout; depths: Float32Array | null; colorData: DecodedProperty | null; shapeData: DecodedProperty | null } | null;
  decoded: Decoded;
  layout: Layout;
  depths: Float32Array | null;
  where: Float32Array | null;
  wereOn: Float32Array | null;
  colorData: DecodedProperty | null;
  palette: PaletteColor[];
  theme: Theme;
}): Flight {
  const { solid, prev, decoded, layout, depths, where, wereOn, colorData, palette, theme } = input;
  const real = decoded.count;
  const b = layout.bounds;
  // How far above the picture a card starts, and how far below it a dropped one gets: y grows
  // downward in a layout, so up is the smaller number. Both are a good deal more than the picture is
  // tall, since the camera is fitted to the picture and anything that far off it is off the screen.
  const tall = b.y1 - b.y0;
  const sky = Math.max(8, tall * 1.8);
  const abyss = Math.max(10, tall * 2.4);

  // which of the old cards the result no longer has
  let leavers: number[] = [];
  if (solid && prev !== null && where !== null) {
    const now = new IntMap(Math.max(1, real));
    for (let i = 0; i < real; i++) now.set(decoded.ids[i], i);
    for (let j = 0; j < prev.decoded.count && leavers.length <= maxLeaving; j++) {
      if (now.get(prev.decoded.ids[j]) < 0) leavers.push(j);
    }
    if (leavers.length > maxLeaving) leavers = [];
  }
  const count = real + leavers.length;
  const wide = count !== real;

  const to = wide ? new Float32Array(count * 2) : layout.positions;
  if (wide) to.set(layout.positions);
  const from = new Float32Array(count * 2);
  const state = new Uint8Array(count);
  let moving = false;
  const rows = layout.rows === null ? null : wide ? new Float32Array(count) : layout.rows;
  if (rows !== null && wide) rows.set(layout.rows as Float32Array);
  const rowsFrom = rows === null ? null : new Float32Array(count);
  const thickness = wide ? new Float32Array(count) : depths;
  if (wide && thickness !== null) {
    if (depths !== null) thickness.set(depths);
    else thickness.fill(defaultDepth, 0, real);
  }

  const byId = prev !== null && where !== null ? new IntMap(Math.max(1, prev.decoded.count)) : null;
  if (byId !== null && prev !== null) for (let j = 0; j < prev.decoded.count; j++) byId.set(prev.decoded.ids[j], j);
  for (let i = 0; i < real; i++) {
    const x = layout.positions[i * 2];
    const y = layout.positions[i * 2 + 1];
    const z = layout.rows === null ? 0 : layout.rows[i];
    const j = byId === null ? -1 : byId.get(decoded.ids[i]);
    if (j >= 0 && where !== null) {
      from[i * 2] = where[j * 2];
      from[i * 2 + 1] = where[j * 2 + 1];
      if (rowsFrom !== null) rowsFrom[i] = wereOn !== null && j < wereOn.length ? wereOn[j] : z;
      continue;
    }
    state[i] = CardState.Arriving;
    moving = true;
    // straight down out of the sky onto its own place, in the solid picture; the flat one still has
    // its cards come up where they land
    from[i * 2] = x;
    from[i * 2 + 1] = solid ? y - sky : y;
    if (rowsFrom !== null) rowsFrom[i] = z;
  }

  let leaving: Leaving | null = null;
  if (leavers.length > 0 && prev !== null && where !== null) {
    const prevColor = prev.colorData;
    const prevShape = prev.shapeData;
    const groupCount = colorData ? colorData.groups.length : 1;
    const groups = new Uint16Array(leavers.length);
    const shapes = new Uint16Array(leavers.length);
    let shaped = false;
    for (let k = 0; k < leavers.length; k++) {
      const j = leavers[k];
      const i = real + k;
      const x = where[j * 2];
      const y = where[j * 2 + 1];
      const z = wereOn !== null && j < wereOn.length ? wereOn[j] : 0;
      from[i * 2] = x;
      from[i * 2 + 1] = y;
      // dropped: straight down, and nowhere else
      to[i * 2] = x;
      to[i * 2 + 1] = y + abyss;
      state[i] = CardState.Leaving;
      moving = true;
      if (rows !== null && rowsFrom !== null) {
        rowsFrom[i] = z;
        rows[i] = z;
      }
      if (thickness !== null) thickness[i] = prev.depths !== null && j < prev.depths.length ? prev.depths[j] : defaultDepth;
      groups[k] = groupCount + (prevColor !== null ? prevColor.assignment[j] : 0);
      const slot = prevShape !== null ? prevShape.groups[prevShape.assignment[j]].shape : 0;
      shapes[k] = slot;
      if (slot !== 0) shaped = true;
    }
    leaving = { groups, shapes, palette: paletteBytes(prevColor, palette, theme), shaped };
  }

  return { count, from, to, state: moving ? state : null, rows, rowsFrom, depths: thickness, leaving };
}

/** Two typed arrays end to end: the cards of the result, and the ones flying out behind them. */
function joinGroups(a: Uint16Array, b: Uint16Array): Uint16Array {
  const out = new Uint16Array(a.length + b.length);
  out.set(a);
  out.set(b, a.length);
  return out;
}

function joinBytes(a: Uint8Array, b: Uint8Array): Uint8Array {
  const out = new Uint8Array(a.length + b.length);
  out.set(a);
  out.set(b, a.length);
  return out;
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
