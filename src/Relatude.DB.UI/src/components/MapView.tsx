import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { IconFocusCentered, IconGrid3x3, IconListDetails, IconMinus, IconPlus, IconRotate360, IconWorld } from "@tabler/icons-react";
import { ColorField } from "./ColorField";
import { BareButton, FullscreenButton } from "./DatamodelGraph";
import type { PivotBase } from "./PivotView";
import { fetchCards, fetchNodeGuid, fetchPivotModel, runMap, type MapRequest, type PivotModel, type PivotProperty } from "../server/query";
import { useLiveResult } from "../server/hooks";
import { formatCount, formatQuery } from "../format";
import type { MapDefinition, MapMarks as MarkKind, MapStyle as SavedStyle } from "../queryTabs";
import { buildPalette, buildRamp, buildRodRamp, palettes, parseCssColor, type PaletteColor, type RGB } from "../visual/palette";
import { clampView, fitView, projections, projectionOf, type View } from "../map/projection";
import { countCountries, countryAt, countryRaster, countryRasterSize, rankedCountries } from "../map/countries";
import { decodeMap, PointIndex, type MapGroup } from "../map/points";
import { clusterPoints, clusterRadius, drawClusters, rodsOf, type Cluster } from "../map/clusters";
import { createMapField, maxGlobeZoom, type GlobeCamera, type MapField, type MapScene, type MapStyle } from "../map/mapField";
import { Momentum } from "../map/motion";
import { world } from "../map/worldMap";
import { readColor } from "../server/datamodel";

/** A map before anyone has chosen anything: a dot per node on a world map, in one colour. */
export const emptyMap: MapDefinition = {
  property: null,
  marks: "dots",
  colorProperty: null,
  colorMode: "auto",
  globe: false,
  projection: "natural",
  legend: true,
  graticule: true,
  palette: palettes[0].id,
};

const markOptions: { id: MarkKind; label: string; hint: string }[] = [
  { id: "dots", label: "Dots", hint: "A dot per node — the plainest picture of where they are, and the one that holds a million of them" },
  { id: "pins", label: "Pins", hint: "A marker per node, standing on its place; for a set small enough to pick individual nodes out of" },
  { id: "heat", label: "Heat", hint: "How thickly the nodes lie, as a field of colour — where they crowd rather than where each one is" },
  { id: "clusters", label: "Clusters", hint: "One bubble per patch of the map, sized and coloured by how many nodes are in it, with the count written in" },
  { id: "countries", label: "Countries", hint: "Every country shaded by how many nodes are in it" },
  { id: "rods", label: "Rods", hint: "A rod per patch of the world, as long and as hot as the patch is full — best on the globe, where they stand out into space" },
];

/**
 * What the Look panel falls back to before anyone has touched it: a globe with air round it, which
 * is what stops a dark ball on a dark page reading as a hole, and nothing else turned on.
 */
const styleDefaults: Required<Omit<SavedStyle, "atmosphereColor" | "landColor" | "oceanColor" | "lineColor">> = {
  atmosphere: 16,
  stars: 0,
  starDrift: 18,
  starDensity: 60,
  starTrail: 15,
  land: false,
  shading: 18,
  // the light over the viewer's left shoulder and a little above, which is where a light belongs
  // in every painting ever made
  lightAround: 320,
  lightUp: 22,
  brightness: 95,
  ambient: 24,
  specular: 45,
  shine: 40,
  landShine: 12,
  lines: true,
  lineWidth: 13,
  earth: "off",
  rodColors: "heat",
  rodHeight: 22,
  rodWidth: 45,
  rodCell: 35,
};

/** The photographs of the Earth, fetched the first time anyone asks and kept for the session. */
let earthImages: Promise<{ day: HTMLImageElement; night: HTMLImageElement }> | null = null;
function loadEarth(): Promise<{ day: HTMLImageElement; night: HTMLImageElement }> {
  if (earthImages !== null) return earthImages;
  // a megabyte of scenery, in a chunk of its own that nobody downloads until they turn it on
  earthImages = import("../map/earth").then(async (m) => {
    const decode = (src: string) =>
      new Promise<HTMLImageElement>((resolve, reject) => {
        const image = new Image();
        image.onload = () => resolve(image);
        image.onerror = reject;
        image.src = src;
      });
    return { day: await decode(m.dayImage), night: await decode(m.nightImage) };
  });
  return earthImages;
}

const modeOptions = [
  { value: "auto", label: "auto" },
  { value: "values", label: "values" },
  { value: "ranges", label: "ranges" },
];

/** What a pin is when nothing else has been said: the red of every map pin ever drawn. */
const pinRed: RGB = [222, 58, 48];

/** How many pins are ever drawn: past this they are a smear rather than markers, and the view says so. */
const pinBudget = 20_000;
/** How near a pointer has to be to a node to pick it, in pixels. */
const pickPixels = 9;
const minScale = 40;
const maxScale = 400_000;
/** The colour ramp's resolution: enough that a heat field has no bands in it. */
const rampSteps = 64;
/** How fast the globe turns when it is left to turn on its own, in degrees a second. */
const spinDegrees = 6;

interface Theme {
  panel: RGB;
  accent: RGB;
  text: RGB;
  line: RGB;
  faint: RGB;
  none: RGB;
  other: RGB;
}

interface Tooltip {
  x: number;
  y: number;
  lines: string[];
}

/** What the pointer is doing between down and up. */
interface Drag {
  x: number;
  y: number;
  moved: boolean;
  /** the view and the camera as they were when the hand went down */
  from: { view: View; camera: GlobeCamera };
}

/**
 * The map: every node of the result standing on the place one of its properties says it is.
 *
 * Two pictures of the same points, and either will do - a flat world map in one of three
 * projections, or a globe - drawn by one WebGL renderer from one set of buffers (map/mapField.ts).
 * Nothing about a node reaches the graphics card but its longitude and latitude: changing
 * projection, or going from the map to the globe, is a uniform rather than a pass over the data,
 * which is what lets a million of them be on screen at once.
 *
 * Five ways of showing where they are, from one mark per node to a picture of the crowd: pins and
 * dots (coloured by a property, as the visual pivot colours its cards), a heat field, bubbles per
 * patch of the map with counts in them, and every country shaded by what it holds. The first two
 * are the nodes themselves and can be clicked open; the last three are counts, and what they count
 * follows the search and the facet selection like everything else on this page.
 *
 * The server sends a position per node read straight from the geo index - no node is read, whatever
 * the size of the result - and, for the colouring, the same groups the visual pivot uses. Which
 * countries the points are in is worked out here, from a raster of the world drawn once.
 */
export function MapView({
  base,
  definition,
  onChange,
  refreshToken,
  showQuery,
  onOpen,
  fullscreen,
  onToggleFullscreen,
  head,
}: {
  base: PivotBase;
  /** The definition as the page keeps it - null until this view has opened once for the type. */
  definition: MapDefinition | null;
  onChange: (definition: MapDefinition) => void;
  /** Changes when the page is asked to run again with nothing else changed. */
  refreshToken: number;
  showQuery: boolean;
  /** A node was clicked: open it in the form beside the map. */
  onOpen: (nodeId: string) => void;
  fullscreen: boolean;
  onToggleFullscreen: () => void;
  /** What the result's own head would say, when the page has folded that head away (see the visual pivot). */
  head?: React.ReactNode;
}) {
  const [model, setModel] = useState<PivotModel | null>(null);
  const [modelError, setModelError] = useState<string | null>(null);
  const def = definition ?? emptyMap;
  /** the Look panel as it was saved; every field of it is optional and filled in by `style` below */
  const saved = def.style ?? {};
  const setStyle = (patch: SavedStyle) => onChange({ ...def, style: { ...saved, ...patch } });

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

  const positions = useMemo(() => model?.properties.filter((p) => p.geo) ?? [], [model]);
  const groupable = useMemo(() => model?.properties.filter((p) => p.groupable) ?? [], [model]);

  // the first time the view opens for a type it places the nodes by the first position property it
  // finds, so there is a map before anyone chooses; the choice is then the query's own
  useEffect(() => {
    if (model && definition === null) onChange({ ...emptyMap, property: positions[0]?.id ?? null });
  }, [model, definition, positions, onChange]);

  const property = positions.some((p) => p.id === def.property) ? def.property : (positions[0]?.id ?? null);
  const colorProperty = groupable.some((p) => p.id === def.colorProperty) ? def.colorProperty : null;
  const colorInfo = groupable.find((p) => p.id === colorProperty);
  const marks = def.marks;
  const coloured = marks === "dots" || marks === "pins";
  const projection = projectionOf(def.projection);
  const globeMode = def.globe;

  const request = useMemo<MapRequest | null>(() => {
    if (model === null || definition === null || property === null) return null;
    return {
      storeId: base.storeId,
      typeId: base.typeId,
      text: base.text,
      semanticRatio: base.semanticRatio,
      minimumSimilarity: base.minimumSimilarity,
      selections: base.selections,
      propertyId: property,
      // the colouring is only asked for when something is drawn in it; a heat field has no use for it
      properties: coloured && colorProperty !== null ? [{ propertyId: colorProperty, mode: def.colorMode }] : [],
    };
    // the token is not part of the request; a new object is how the runner is told to run again
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [model, definition === null, base, property, coloured, colorProperty, def.colorMode, refreshToken]);
  const { result, loading, error } = useLiveResult(request, runMap);
  const points = useMemo(() => (result ? decodeMap(result) : null), [result]);
  const colorData = colorProperty === null ? null : (points?.byProperty.get(colorProperty) ?? null);

  // ---- the canvas, and what is on it ----

  const stageRef = useRef<HTMLDivElement>(null);
  /** The part of the stage the map is drawn in - the stage less whatever the legend takes. */
  const frameRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const bubblesRef = useRef<HTMLCanvasElement>(null);
  const field = useRef<MapField | null>(null);
  const [size, setSize] = useState({ width: 0, height: 0 });
  const [theme, setTheme] = useState<Theme | null>(null);
  const [glOk, setGlOk] = useState(true);
  const [tooltip, setTooltip] = useState<Tooltip | null>(null);

  const [view, setView] = useState<View>({ cx: 0, cy: 0, scale: 200 });
  const [camera, setCamera] = useState<GlobeCamera>({ lat: 20, lon: 0, zoom: 1 });
  const drag = useRef<Drag | null>(null);
  const momentum = useRef(new Momentum());
  const frame = useRef(0);
  const pending = useRef<{ view?: View; camera?: GlobeCamera } | null>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    let made: MapField | null = null;
    try {
      made = createMapField(canvas);
    } catch {
      made = null;
    }
    field.current = made;
    setGlOk(made !== null);
    return () => {
      made?.destroy();
      field.current = null;
    };
  }, []);

  useLayoutEffect(() => {
    const el = frameRef.current;
    if (!el) return;
    const measure = () => setSize((was) => (was.width === el.clientWidth && was.height === el.clientHeight ? was : { width: el.clientWidth, height: el.clientHeight }));
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  // The page's colours, read from the stylesheet so the map follows the theme - including the
  // switch, which changes an attribute on the document rather than anything this view can see.
  useLayoutEffect(() => {
    const el = stageRef.current;
    if (!el) return;
    const read = () => setTheme(readTheme(el));
    read();
    const observer = new MutationObserver(read);
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    return () => observer.disconnect();
  }, [def.bare, size.width]);

  const index = useMemo(() => (points === null || !coloured ? null : new PointIndex(points.lat, points.lon, points.count)), [points, coloured]);
  const counts = useMemo(() => (points !== null && marks === "countries" ? countCountries(points.lat, points.lon, points.count) : null), [points, marks]);
  /** One rod per patch of the world, gathered on the ground rather than on the canvas (see rodsOf). */
  const rods = useMemo(() => (points !== null && marks === "rods" ? rodsOf(points.lat, points.lon, points.count, (saved.rodCell ?? styleDefaults.rodCell) / 10) : null), [points, marks, saved.rodCell]);

  const palette = useMemo(() => (theme === null ? [] : buildPalette(colorData ? colorData.groups.length : 1, theme.panel, theme.accent, def.palette)), [theme, colorData, def.palette]);
  const ramp = useMemo(() => (theme === null ? [] : buildRamp(rampSteps, theme.panel, theme.accent, def.palette)), [theme, def.palette]);
  /**
   * The ramp as bytes. Its low end is transparent and reaches solid a third of the way up: the thin
   * outer edge of a crowd has to leave the map underneath readable, while everything from a real
   * crowd upwards is a colour to be read off the scale.
   */
  const rampBytes = useMemo(() => {
    const bytes = new Uint8Array(ramp.length * 4);
    ramp.forEach((c, i) => {
      bytes[i * 4] = c.rgb[0];
      bytes[i * 4 + 1] = c.rgb[1];
      bytes[i * 4 + 2] = c.rgb[2];
      bytes[i * 4 + 3] = Math.round(255 * Math.min(1, (i / Math.max(1, ramp.length - 1)) * 3));
    });
    return bytes;
  }, [ramp]);
  /** The colour of every group, as the bytes the renderer paints the marks from. */
  const markColors = useMemo(() => {
    const groups = colorData ? colorData.groups : null;
    const bytes = new Uint8Array(Math.max(1, groups ? groups.length : 1) * 3);
    // A pin nobody has asked to colour is RED, which is the colour a pin in a map is, rather than
    // whatever the palette happens to begin with. Choose a property to colour by and the palette
    // takes over, the same as it does for dots.
    if (groups === null && marks === "pins") bytes.set(pinRed);
    else if (groups === null || theme === null) bytes.set(palette[0]?.rgb ?? [128, 128, 128]);
    else groups.forEach((g, i) => bytes.set(groupColor(g.kind, g.ordinal, palette, theme), i * 3));
    return bytes;
  }, [colorData, palette, theme, marks]);

  /**
   * What each country is painted with: 256 colours the shader looks a country's number up in, with
   * 0 - the sea - and every country holding nothing left transparent. The raster itself goes to the
   * card once and unchanged, so a new count is this kilobyte and nothing more.
   */
  const surfaceColors = useMemo(() => {
    const colors = new Uint8Array(256 * 4);
    if (counts === null || ramp.length === 0) return colors;
    const countries = Math.min(255, counts.counts.length);
    for (let i = 0; i < countries; i++) {
      const n = counts.counts[i];
      if (n === 0) continue;
      const c = ramp[rampAt(n, counts.max)].rgb;
      const at = (i + 1) * 4;
      colors[at] = c[0];
      colors[at + 1] = c[1];
      colors[at + 2] = c[2];
      colors[at + 3] = 225;
    }
    return colors;
  }, [counts, ramp]);

  // ---- what one frame is ----

  /** The Look panel's settings, filled in with the defaults and turned into what a shader wants. */
  const style = useMemo<MapStyle>(() => {
    const n = (value: number | undefined, fallback: number) => (typeof value === "number" ? value : fallback);
    const colour = (value: string | null | undefined, fallback: RGB): RGB => {
      const set = readColor(value);
      return set === null ? fallback : parseCssColor(set);
    };
    const panel = theme?.panel ?? [255, 255, 255];
    const text = theme?.text ?? [0, 0, 0];
    // where the light stands, from how far round and how far up it was put
    const around = (n(saved.lightAround, styleDefaults.lightAround) * Math.PI) / 180;
    const up = (n(saved.lightUp, styleDefaults.lightUp) * Math.PI) / 180;
    return {
      atmosphere: n(saved.atmosphere, styleDefaults.atmosphere) / 100,
      atmosphereColor: colour(saved.atmosphereColor, theme?.accent ?? [120, 150, 200]),
      stars: n(saved.stars, styleDefaults.stars) / 100,
      starDrift: (n(saved.starDrift, styleDefaults.starDrift) / 100) * 6,
      starDensity: 0.03 + (n(saved.starDensity, styleDefaults.starDensity) / 100) * 0.75,
      starTrail: (n(saved.starTrail, styleDefaults.starTrail) / 100) * 20,
      land: saved.land === true,
      landColor: colour(saved.landColor, mix(panel, text, 0.3)),
      oceanColor: colour(saved.oceanColor, mix(panel, text, 0.05)),
      shading: n(saved.shading, styleDefaults.shading) / 100,
      light: {
        direction: [Math.cos(up) * Math.sin(around), Math.sin(up), Math.cos(up) * Math.cos(around)],
        brightness: n(saved.brightness, styleDefaults.brightness) / 100,
        ambient: n(saved.ambient, styleDefaults.ambient) / 100,
        specular: n(saved.specular, styleDefaults.specular) / 100,
        // a tight highlight is a high exponent, and the slider reads the other way round
        shine: 2 + Math.pow(n(saved.shine, styleDefaults.shine) / 100, 2) * 220,
        landShine: n(saved.landShine, styleDefaults.landShine) / 100,
      },
      lines: saved.lines !== false,
      lineColor: colour(saved.lineColor, theme ? mix(theme.line, theme.text, 0.55) : [140, 140, 140]),
      lineWidth: n(saved.lineWidth, styleDefaults.lineWidth) / 10,
      earth: saved.earth ?? styleDefaults.earth,
      rodHeight: n(saved.rodHeight, styleDefaults.rodHeight) / (globeMode ? 100 : 200),
      // how thick a rod is, on the ground, in degrees: a share of the patch it stands for
      rodWidth: (n(saved.rodWidth, styleDefaults.rodWidth) / 100) * (n(saved.rodCell, styleDefaults.rodCell) / 10),
    };
  }, [saved, theme, globeMode]);

  const scene = useMemo<MapScene>(
    () => ({
      globe: globeMode,
      projection,
      view,
      camera,
      marks: marks === "dots" || marks === "pins" || marks === "heat" || marks === "rods" ? marks : "none",
      size: currentSize(def, marks),
      alpha: marks === "pins" ? 1 : dotAlpha(points?.count ?? 0),
      limit: marks === "pins" ? pinBudget : Number.MAX_SAFE_INTEGER,
      radius: def.radius ?? 26,
      graticule: def.graticule !== false,
      surface: marks === "countries",
      style,
    }),
    [globeMode, projection, view, camera, marks, def.size, def.pinSize, def.radius, def.graticule, points, style],
  );
  /** The scene as the handlers see it, without re-binding every one of them on every frame. */
  const sceneRef = useRef(scene);
  sceneRef.current = scene;

  // ---- the view, and the hand that moves it ----

  /**
   * Whether anyone has moved the map. A map nobody has touched belongs to the view and is refitted
   * whenever the view changes shape - the legend opening, the window, the picture going fullscreen;
   * one somebody has panned or zoomed is theirs, and is left exactly where they put it.
   */
  const touched = useRef(false);
  const fit = useCallback(() => {
    touched.current = false;
    momentum.current.stop();
    if (globeMode) setCamera({ lat: 20, lon: 0, zoom: 1 });
    else if (size.width > 0) setView(fitView(projection, size.width, size.height));
  }, [globeMode, projection, size.width, size.height]);

  const fitted = useRef("");
  useEffect(() => {
    if (size.width <= 0) return;
    // a change of projection is a different world and always refits, whatever was done to the old one
    if (fitted.current !== projection.id) {
      fitted.current = projection.id;
      touched.current = false;
    } else if (touched.current) return;
    setView(fitView(projection, size.width, size.height));
  }, [projection, size.width, size.height]);

  /** One re-render a frame while something is moving, however many events arrive. */
  const nudge = useCallback((next: { view?: View; camera?: GlobeCamera }) => {
    touched.current = true;
    pending.current = { ...pending.current, ...next };
    if (frame.current !== 0) return;
    frame.current = requestAnimationFrame(() => {
      frame.current = 0;
      const p = pending.current;
      pending.current = null;
      if (p?.view) setView(p.view);
      if (p?.camera) setCamera(p.camera);
    });
  }, []);

  useEffect(
    () => () => {
      if (frame.current !== 0) cancelAnimationFrame(frame.current);
    },
    [],
  );

  /**
   * The field flying past. A frame of it changes nothing React knows about - only the clock the
   * shader reads - so this draws straight from the renderer rather than going round through state,
   * which would be sixty re-renders a second to move some stars.
   */
  const drifting = style.stars > 0 && style.starDrift > 0;
  useEffect(() => {
    if (!drifting || !glOk) return;
    let running = true;
    const tick = () => {
      if (!running) return;
      field.current?.draw(sceneRef.current);
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
    return () => {
      running = false;
    };
  }, [drifting, glOk]);

  /** Where the map ends up after a hand has dragged it `dx, dy` pixels from where it was. */
  const dragged = useCallback(
    (from: { view: View; camera: GlobeCamera }, dx: number, dy: number) => {
      if (globeMode) {
        // a drag turns the ball under the pointer: how many degrees a pixel is depends on how close
        // the camera has come, so the ground keeps up with the hand at every zoom
        const perPixel = (field.current?.degreesPerPixel({ ...sceneRef.current, camera: from.camera }, from.camera.lat) ?? 0.2) * 1.15;
        return {
          camera: {
            lat: Math.max(-88, Math.min(88, from.camera.lat + dy * perPixel)),
            lon: wrapLongitude(from.camera.lon - dx * perPixel),
            zoom: from.camera.zoom,
          },
        };
      }
      return { view: clampView({ ...from.view, cx: from.view.cx - dx / from.view.scale, cy: from.view.cy - dy / from.view.scale }, projection, size.width, size.height) };
    },
    [globeMode, projection, size.width, size.height],
  );

  /** And after being zoomed by `factor`, closing in on a point of the canvas. */
  const zoomed = useCallback(
    (from: { view: View; camera: GlobeCamera }, factor: number, anchor: [number, number] | null) => {
      if (globeMode) return { camera: { ...from.camera, zoom: Math.max(1, Math.min(maxGlobeZoom, from.camera.zoom * factor)) } };
      const px = anchor ? anchor[0] : size.width / 2;
      const py = anchor ? anchor[1] : size.height / 2;
      // the place under the pointer stays under the pointer, which is what makes a wheel feel like a zoom
      const wx = from.view.cx + (px - size.width / 2) / from.view.scale;
      const wy = from.view.cy + (py - size.height / 2) / from.view.scale;
      const scale = Math.max(minScale, Math.min(maxScale, from.view.scale * factor));
      return { view: clampView({ scale, cx: wx - (px - size.width / 2) / scale, cy: wy - (py - size.height / 2) / scale }, projection, size.width, size.height) };
    },
    [globeMode, projection, size.width, size.height],
  );

  /**
   * The map carrying on after the hand has let go, and the globe turning on its own: one loop for
   * both, running only while there is something for it to do. Every step of it goes through the
   * same `dragged` and `zoomed` a pointer does, so a glide cannot drift away from what a drag would
   * have done - and both pieces of state are moved together, since a step needs to see the view and
   * the camera at once (see `advance`).
   */
  const spinning = globeMode && def.spin === true;
  const [gliding, setGliding] = useState(false);
  useEffect(() => {
    if (!gliding && !spinning) return;
    let running = true;
    let last = performance.now();
    const tick = (now: number) => {
      if (!running) return;
      const dt = Math.min(0.05, (now - last) / 1000);
      last = now;
      const step = drag.current === null ? momentum.current.step(dt) : null;
      if (step === null && !spinning) {
        setGliding(false);
        return;
      }
      advance((state) => {
        let next = state;
        if (step !== null) {
          next = { ...next, ...dragged(next, step.dx, step.dy) };
          if (Math.abs(step.zoom - 1) > 1e-6) next = { ...next, ...zoomed(next, step.zoom, momentum.current.anchor) };
        } else if (spinning && drag.current === null) {
          next = { ...next, camera: { ...next.camera, lon: wrapLongitude(next.camera.lon + dt * spinDegrees) } };
        }
        return next;
      });
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
    return () => {
      running = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [gliding, spinning, dragged, zoomed]);

  /**
   * The view and the camera moved as one. React hands an updater only the state it is updating, and
   * a step of the loop above has to see both to work out the next of each - so the camera's update
   * runs inside the view's, and each is handed back unchanged when only the other moved.
   */
  function advance(step: (state: { view: View; camera: GlobeCamera }) => { view: View; camera: GlobeCamera }) {
    setView((v) => {
      let nextView = v;
      setCamera((c) => {
        const next = step({ view: v, camera: c });
        nextView = next.view;
        return next.camera;
      });
      return nextView;
    });
  }

  const onPointerDown = (e: React.PointerEvent) => {
    (e.target as Element).setPointerCapture?.(e.pointerId);
    momentum.current.stop();
    drag.current = { x: e.clientX, y: e.clientY, moved: false, from: { view, camera } };
    momentum.current.track(e.clientX, e.clientY);
  };

  const onPointerMove = (e: React.PointerEvent) => {
    const d = drag.current;
    const rect = e.currentTarget.getBoundingClientRect();
    if (d === null) {
      hover(e.clientX - rect.left, e.clientY - rect.top);
      return;
    }
    const dx = e.clientX - d.x;
    const dy = e.clientY - d.y;
    momentum.current.track(e.clientX, e.clientY);
    if (!d.moved && Math.abs(dx) + Math.abs(dy) < 3) return;
    d.moved = true;
    setTooltip(null);
    nudge(dragged(d.from, dx, dy));
  };

  const onPointerUp = (e: React.PointerEvent) => {
    const d = drag.current;
    drag.current = null;
    if (d === null) return;
    if (!d.moved) {
      momentum.current.stop();
      const rect = e.currentTarget.getBoundingClientRect();
      click(e.clientX - rect.left, e.clientY - rect.top);
      return;
    }
    momentum.current.release();
    if (momentum.current.moving) setGliding(true);
  };

  const onWheel = (e: React.WheelEvent) => {
    if (size.width <= 0) return;
    const rect = e.currentTarget.getBoundingClientRect();
    // NONE of the notch now: all of it is owed and paid off over the next few frames, which is what
    // turns a wheel's steps into a zoom rather than a staircase (see map/motion.ts)
    touched.current = true;
    momentum.current.push(-e.deltaY * 0.0016, [e.clientX - rect.left, e.clientY - rect.top]);
    setGliding(true);
  };

  /** The node nearest a point of the canvas, or -1. */
  const pointAt = useCallback(
    (px: number, py: number): number => {
      if (points === null || index === null || field.current === null) return -1;
      const at = field.current.placeAt(sceneRef.current, px, py);
      if (at === null) return -1;
      return index.nearest(at[1], at[0], field.current.degreesPerPixel(sceneRef.current, at[0]) * pickPixels);
    },
    [points, index],
  );

  const names = useRef(new Map<number, string>());
  const hover = (px: number, py: number) => {
    if (points === null) return;
    if (marks === "countries") {
      const at = field.current?.placeAt(sceneRef.current, px, py) ?? null;
      const country = at === null ? -1 : countryAt(at[1], at[0]);
      if (country < 0 || counts === null) {
        setTooltip(null);
        return;
      }
      setTooltip({ x: px, y: py, lines: [world().countries[country].name, formatCount(counts.counts[country]) + (counts.counts[country] === 1 ? " node" : " nodes")] });
      return;
    }
    if (marks === "clusters") {
      const bubble = clusterUnder(px, py);
      setTooltip(bubble === null ? null : { x: px, y: py, lines: [formatCount(bubble.count) + (bubble.count === 1 ? " node here" : " nodes here"), placeLabel(bubble.lat, bubble.lon)] });
      return;
    }
    if (marks === "heat" || marks === "rods") {
      const at = field.current?.placeAt(sceneRef.current, px, py) ?? null;
      if (at === null || rods === null) {
        setTooltip(at === null ? null : { x: px, y: py, lines: [placeLabel(at[0], at[1])] });
        return;
      }
      // the rod nearest the place under the pointer, if one is near enough to be the one meant
      const cell = (saved.rodCell ?? styleDefaults.rodCell) / 10;
      let best = -1;
      let nearest = cell * cell;
      for (let i = 0; i < rods.counts.length; i++) {
        const dLat = rods.rods[i * 3 + 1] - at[0];
        let dLon = rods.rods[i * 3] - at[1];
        if (dLon > 180) dLon -= 360;
        else if (dLon < -180) dLon += 360;
        const squeeze = Math.max(0.05, Math.cos((at[0] * Math.PI) / 180));
        const d = dLat * dLat + dLon * squeeze * (dLon * squeeze);
        if (d < nearest) {
          nearest = d;
          best = i;
        }
      }
      const lines = [placeLabel(at[0], at[1])];
      if (best >= 0) lines.unshift(formatCount(rods.counts[best]) + (rods.counts[best] === 1 ? " node here" : " nodes here"));
      setTooltip({ x: px, y: py, lines });
      return;
    }
    const i = pointAt(px, py);
    if (i < 0) {
      setTooltip(null);
      return;
    }
    const id = points.ids[i];
    const lines = [names.current.get(id) ?? "…", placeLabel(points.lat[i], points.lon[i])];
    if (colorData) lines.push(colorData.name + ": " + colorData.groups[colorData.assignment[i]].label);
    setTooltip({ x: px, y: py, lines });
    if (!names.current.has(id)) {
      names.current.set(id, "…");
      fetchCards(base.storeId, [id])
        .then((r) => {
          const name = r.cards[0]?.name ?? "";
          names.current.set(id, name);
          setTooltip((t) => (t === null ? null : { ...t, lines: [name, ...t.lines.slice(1)] }));
        })
        .catch(() => names.current.delete(id));
    }
  };

  const lastClusters = useRef<Cluster[]>([]);
  const lastCell = useRef(48);
  const clusterUnder = (px: number, py: number): Cluster | null => {
    let max = 1;
    for (const c of lastClusters.current) if (c.count > max) max = c.count;
    // last first: the ones drawn on top are the ones the pointer lands on
    for (let i = lastClusters.current.length - 1; i >= 0; i--) {
      const c = lastClusters.current[i];
      const r = clusterRadius(c.count, max, lastCell.current);
      if ((c.x - px) * (c.x - px) + (c.y - py) * (c.y - py) <= r * r) return c;
    }
    return null;
  };

  const click = (px: number, py: number) => {
    if (points === null) return;
    if (marks === "clusters") {
      // a bubble is a patch of the map: clicking it closes in on what is inside it
      const bubble = clusterUnder(px, py);
      if (bubble !== null) zoomTo(bubble.lat, bubble.lon, 3);
      return;
    }
    if (marks === "countries") {
      const at = field.current?.placeAt(sceneRef.current, px, py) ?? null;
      const country = at === null ? -1 : countryAt(at[1], at[0]);
      if (country >= 0) zoomToBounds(...world().countries[country].bounds);
      return;
    }
    if (marks === "heat") return;
    const i = pointAt(px, py);
    if (i < 0) return;
    fetchNodeGuid(base.storeId, points.ids[i])
      .then((r) => onOpen(r.id))
      .catch(() => undefined);
  };

  const zoomTo = (lat: number, lon: number, times: number) => {
    touched.current = true;
    momentum.current.stop();
    setTooltip(null); // what it named is about to be somewhere else
    if (globeMode) {
      setCamera({ lat, lon, zoom: Math.min(maxGlobeZoom, camera.zoom * times) });
      return;
    }
    const [x, y] = projection.project(lon, lat);
    const scale = Math.max(minScale, Math.min(maxScale, view.scale * times));
    setView(clampView({ scale, cx: x, cy: y }, projection, size.width, size.height));
  };

  const zoomToBounds = (west: number, south: number, east: number, north: number) => {
    touched.current = true;
    momentum.current.stop();
    setTooltip(null);
    const lat = (south + north) / 2;
    const lon = (west + east) / 2;
    if (globeMode) {
      const span = Math.max(east - west, north - south, 1);
      setCamera({ lat, lon, zoom: Math.max(1, Math.min(maxGlobeZoom, 120 / span)) });
      return;
    }
    const a = projection.project(west, north);
    const b = projection.project(east, south);
    const scale = Math.max(minScale, Math.min(maxScale, Math.min((size.width * 0.8) / Math.max(1e-6, Math.abs(b[0] - a[0])), (size.height * 0.8) / Math.max(1e-6, Math.abs(b[1] - a[1])))));
    const [x, y] = projection.project(lon, lat);
    setView(clampView({ scale, cx: x, cy: y }, projection, size.width, size.height));
  };

  // ---- what the renderer is told, and when ----

  useEffect(() => {
    if (points === null) return;
    field.current?.setPoints(points.lon, points.lat, points.count);
  }, [points, glOk]);
  useEffect(() => {
    field.current?.setColors(markColors, colorData ? colorData.assignment : null);
  }, [markColors, colorData, points, glOk]);
  useEffect(() => {
    field.current?.setRamp(rampBytes);
  }, [rampBytes, glOk]);
  /**
   * What a rod is painted by its height: white-hot at the bottom through yellow and red to a deep
   * purple at the top. The other way round from every other scale here, and on purpose - a rod's
   * height already says "a lot", and the eye reads a small bright thing as ordinary and a dark
   * saturated one as extreme, so the two agree instead of arguing.
   */
  const rodRamp = useMemo(() => {
    const heat = (saved.rodColors ?? styleDefaults.rodColors) === "heat";
    const colors = heat ? buildRodRamp(rampSteps) : ramp;
    const bytes = new Uint8Array(Math.max(1, colors.length) * 4);
    colors.forEach((c, i) => {
      bytes[i * 4] = c.rgb[0];
      bytes[i * 4 + 1] = c.rgb[1];
      bytes[i * 4 + 2] = c.rgb[2];
      bytes[i * 4 + 3] = 255;
    });
    return bytes;
  }, [saved.rodColors, ramp]);
  useEffect(() => {
    field.current?.setRodRamp(rodRamp);
  }, [rodRamp, glOk]);
  useEffect(() => {
    if (style.earth === "off") {
      field.current?.setEarth("day", null);
      field.current?.setEarth("night", null);
      return;
    }
    let alive = true;
    loadEarth()
      .then((images) => {
        if (!alive || field.current === null) return;
        field.current.setEarth("day", images.day);
        field.current.setEarth("night", images.night);
        setRedraw((n) => n + 1);
      })
      .catch(() => undefined);
    return () => {
      alive = false;
    };
  }, [style.earth, glOk]);
  /** bumped when something outside React's own state has changed what a frame would look like */
  const [redraw, setRedraw] = useState(0);
  // The raster is eight megabytes and takes a moment to draw, so it is asked for only once
  // something actually needs it - the shaded countries, or land painted as land - and then never
  // again, since the world does not change.
  const rastered = useRef(false);
  useEffect(() => {
    // the land mask is what tells the water from the rock, so the highlight wants it too
    if ((marks !== "countries" && !style.land && style.light.specular <= 0) || rastered.current || field.current === null) return;
    const [w, h] = countryRasterSize();
    field.current.setSurface(countryRaster(), w, h);
    rastered.current = true;
  }, [marks, style.land, style.light.specular, glOk]);
  useEffect(() => {
    if (rods === null) return;
    field.current?.setRods(rods.rods, rods.counts.length);
  }, [rods, glOk]);
  useEffect(() => {
    field.current?.setSurfaceColors(surfaceColors);
  }, [surfaceColors, glOk]);
  useEffect(() => {
    if (theme === null) return;
    field.current?.setTheme({
      clear: theme.panel,
      // The ball is grey, and very nearly the page it stands on - almost white in the light theme
      // and almost black in the dark one. Grey rather than tinted because every colour on a map
      // means something, and the planet is not one of the things being said; near the page because
      // what should stand out is the data and not the globe it is drawn on.
      ground: grey(mix(theme.panel, theme.text, 0.05)),
      line: mix(theme.line, theme.text, 0.55),
      grid: mix(theme.panel, theme.line, 0.75),
      glow: grey(mix(theme.panel, theme.text, 0.4)),
      outline: theme.panel,
    });
  }, [theme, glOk]);

  const size$ = size.width + "x" + size.height;
  useEffect(() => {
    if (field.current === null || theme === null || size.width <= 0) return;
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    field.current.resize(size.width, size.height, dpr);
    field.current.draw(scene);

    // the bubbles, over the top: a few hundred of them at most, each with a count written in it
    const bubbles = bubblesRef.current;
    if (!bubbles) return;
    if (bubbles.width !== Math.round(size.width * dpr) || bubbles.height !== Math.round(size.height * dpr)) {
      bubbles.width = Math.round(size.width * dpr);
      bubbles.height = Math.round(size.height * dpr);
    }
    const ctx = bubbles.getContext("2d");
    if (!ctx) return;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, bubbles.width, bubbles.height);
    if (marks !== "clusters" || points === null || points.count === 0) {
      lastClusters.current = [];
      return;
    }
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    const cell = def.cell ?? 48;
    lastCell.current = cell;
    const place = field.current.project(scene);
    lastClusters.current = clusterPoints(points.count, 1, (i, out) => place(points.lon[i], points.lat[i], out), points.lat, points.lon, size.width, size.height, cell);
    drawClusters(ctx, lastClusters.current, cell, rampBytes, cssOf(theme.panel), formatCount);
  }, [scene, points, theme, size$, size.width, size.height, marks, def.cell, rampBytes, surfaceColors, redraw, glOk]);

  // ---- what is written beside it ----

  const groups = colorData?.groups ?? null;
  const ranked = useMemo(() => (counts === null ? [] : rankedCountries(counts).slice(0, 40)), [counts]);
  const legendKind = marks === "countries" ? "countries" : coloured && colorData !== null ? "colours" : marks === "heat" || marks === "clusters" ? "scale" : "none";

  const propertySelect = (value: string | null, none: string, title: string, options: PivotProperty[], onPick: (id: string | null) => void) => (
    <select className="select" value={value ?? ""} title={title} onChange={(e) => onPick(e.target.value || null)}>
      <option value="">{none}</option>
      {options.map((p) => (
        <option key={p.id} value={p.id}>
          {p.name}
          {p.declaredBy ? " (" + p.declaredBy + ")" : ""}
        </option>
      ))}
    </select>
  );

  if (modelError) return <div className="query-error">{modelError}</div>;
  if (model !== null && positions.length === 0) {
    return (
      <div className="query-empty">
        <p>Nothing on this type says where a node is, so there is nothing to put on a map.</p>
        <p className="muted">A map places nodes by a property holding a position — a GeoCoordinate. Give the type one, index it, and it will be offered here.</p>
      </div>
    );
  }

  return (
    <div className="visual map">
      <div className="pivot-builder">
        <div className="pivot-builder-row">
          <span className="pivot-builder-label">Place by</span>
          <span className="pivot-chip">{propertySelect(property, "(none)", "The property holding the position each node is drawn at", positions, (id) => onChange({ ...def, property: id }))}</span>
          <span className="pivot-builder-label visual-label-2">Show</span>
          <span className="pivot-chip">
            <div className="query-view" role="tablist">
              {markOptions.map((m) => (
                <button key={m.id} role="tab" aria-selected={marks === m.id} className={marks === m.id ? "active" : ""} title={m.hint} onClick={() => onChange({ ...def, marks: m.id })}>
                  {m.label}
                </button>
              ))}
            </div>
          </span>
          {coloured && (
            <>
              <span className="pivot-builder-label visual-label-2">Colour by</span>
              <span className="pivot-chip">
                {propertySelect(colorProperty, "(one colour)", "The property whose values colour the marks", groupable, (id) => onChange({ ...def, colorProperty: id }))}
                {colorInfo && hasModes(colorInfo) && (
                  <select className="select" value={def.colorMode} title="How the values are grouped: one group per value, or ranges of them" onChange={(e) => onChange({ ...def, colorMode: e.target.value })}>
                    {modeOptions.map((m) => (
                      <option key={m.value} value={m.value}>
                        {m.label}
                      </option>
                    ))}
                  </select>
                )}
              </span>
            </>
          )}
          <span className="pivot-builder-label visual-label-2">Colours</span>
          <span className="pivot-chip">
            <select className="select" value={def.palette ?? palettes[0].id} title="The colours the marks are painted with" onChange={(e) => onChange({ ...def, palette: e.target.value })}>
              {palettes.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          </span>
          {!globeMode && (
            <>
              <span className="pivot-builder-label visual-label-2">Projection</span>
              <span className="pivot-chip">
                <select className="select" value={projection.id} title={projection.hint} onChange={(e) => onChange({ ...def, projection: e.target.value })}>
                  {projections.map((p) => (
                    <option key={p.id} value={p.id} title={p.hint}>
                      {p.name}
                    </option>
                  ))}
                </select>
              </span>
            </>
          )}
          {marks !== "countries" && (
            <>
              <span className="pivot-builder-label visual-label-2">{sizeLabel(marks)}</span>
              <span className="pivot-chip">
                <span className="visual-stretch">
                  <input
                    type="range"
                    min={sizeRange(marks)[0]}
                    max={sizeRange(marks)[1]}
                    step={1}
                    value={currentSize(def, marks)}
                    title={sizeHint(marks)}
                    onChange={(e) => onChange(withSize(def, marks, Number(e.target.value)))}
                  />
                  <span className="visual-stretch-value">{currentSize(def, marks)}</span>
                </span>
              </span>
            </>
          )}
          <div className="pivot-options">
            {head}
            <button
              className={"icon-button" + (globeMode ? " active" : "")}
              aria-pressed={globeMode}
              title={globeMode ? "Back to the flat map" : "Put the world on a globe — drag to turn it, wheel to close in"}
              onClick={() => onChange({ ...def, globe: !globeMode })}
            >
              <IconWorld size={16} stroke={1.9} />
            </button>
            <button
              className={"icon-button" + (def.graticule !== false ? " active" : "")}
              aria-pressed={def.graticule !== false}
              title={def.graticule !== false ? "Hide the lines of latitude and longitude" : "Show the lines of latitude and longitude"}
              onClick={() => onChange({ ...def, graticule: def.graticule === false })}
            >
              <IconGrid3x3 size={16} stroke={1.9} />
            </button>
            {globeMode && (
              <button
                className={"icon-button" + (def.spin === true ? " active" : "")}
                aria-pressed={def.spin === true}
                title={def.spin === true ? "Stop the slow turn" : "Turn the globe slowly, on its own"}
                onClick={() => onChange({ ...def, spin: def.spin !== true })}
              >
                <IconRotate360 size={16} stroke={1.9} />
              </button>
            )}
            <button className="icon-button" title={globeMode ? "Back to the whole globe" : "Fit the whole world in view (or double-click it)"} onClick={fit}>
              <IconFocusCentered size={16} stroke={1.9} />
            </button>
            <BareButton on={def.bare === true} onToggle={() => onChange({ ...def, bare: def.bare !== true })} what="map" offTitle="Quieten the map — a black ground in the dark theme" />
            <button className={"icon-button" + (def.legend ? " active" : "")} title={def.legend ? "Hide the legend" : "Show the legend"} onClick={() => onChange({ ...def, legend: !def.legend })}>
              <IconListDetails size={16} stroke={1.9} />
            </button>
            <button
              className={"icon-button" + (def.look ? " active" : "")}
              aria-pressed={def.look === true}
              title={def.look ? "Hide how the world is drawn" : "How the world is drawn: the air round it, the stars behind it, the land on it"}
              onClick={() => onChange({ ...def, look: def.look !== true })}
            >
              {def.look ? <IconMinus size={16} stroke={1.9} /> : <IconPlus size={16} stroke={1.9} />}
            </button>
            <FullscreenButton on={fullscreen} onToggle={onToggleFullscreen} what="map" />
          </div>
        </div>
        {def.look && (
          /* How the world itself is drawn, as against what is drawn on it. Folded away by default:
             none of it changes what the data says, and a line of knobs about the scenery has no
             business being the first thing on the page. */
          <div className="pivot-builder-row map-look">
            <span className="pivot-builder-label">Air</span>
            <span className="pivot-chip">
              {slider("How far the air glows past the globe's edge — what stops a dark ball on a dark page reading as a hole in it", 0, 60, value(saved.atmosphere, styleDefaults.atmosphere), (v) => setStyle({ atmosphere: v }))}
              <ColorField value={saved.atmosphereColor} fallback={theme ? cssOf(theme.accent) : undefined} onChange={(c) => setStyle({ atmosphereColor: c })} />
            </span>
            <span className="pivot-builder-label visual-label-2">Stars</span>
            <span className="pivot-chip">
              {slider("A field of stars behind the world, flying past the view — how bright they are, and nothing at all takes them away", 0, 100, value(saved.stars, styleDefaults.stars), (v) => setStyle({ stars: v }))}
              {slider("How fast they come at you; nothing at all holds the field still", 0, 100, value(saved.starDrift, styleDefaults.starDrift), (v) => setStyle({ starDrift: v }))}
              {slider("How crowded the field is", 0, 100, value(saved.starDensity, styleDefaults.starDensity), (v) => setStyle({ starDensity: v }))}
              {slider("How far each one smears out behind itself — none is a field of points, plenty is a jump to lightspeed", 0, 100, value(saved.starTrail, styleDefaults.starTrail), (v) => setStyle({ starTrail: v }))}
            </span>
            <span className="pivot-builder-label visual-label-2">Lines</span>
            <span className="pivot-chip">
              {toggle(style.lines, style.lines ? "Take the coastlines and borders away" : "Draw the coastlines and borders", () => setStyle({ lines: !style.lines }))}
              {style.lines && (
                <>
                  {slider("How thick a coastline is, in tenths of a pixel", 4, 40, value(saved.lineWidth, styleDefaults.lineWidth), (v) => setStyle({ lineWidth: v }))}
                  <ColorField value={saved.lineColor} fallback={theme ? cssOf(mix(theme.line, theme.text, 0.55)) : undefined} onChange={(c) => setStyle({ lineColor: c })} />
                </>
              )}
            </span>
            <span className="pivot-builder-label visual-label-2">Land</span>
            <span className="pivot-chip">
              {toggle(style.land, style.land ? "Back to a ball of one colour" : "Paint the land as land and the water as water", () => setStyle({ land: !style.land }))}
              {style.land && (
                <>
                  <ColorField value={saved.landColor} fallback={theme ? cssOf(mix(theme.panel, theme.text, 0.3)) : undefined} onChange={(c) => setStyle({ landColor: c })} />
                  <ColorField value={saved.oceanColor} fallback={theme ? cssOf(mix(theme.panel, theme.text, 0.05)) : undefined} onChange={(c) => setStyle({ oceanColor: c })} />
                </>
              )}
            </span>
            {globeMode && (
              <>
                <span className="pivot-builder-label visual-label-2">Earth</span>
                <span className="pivot-chip">
                  <select
                    className="select"
                    value={saved.earth ?? styleDefaults.earth}
                    title="A photograph of the Earth wrapped round the globe — NASA's, and a megabyte of it, so it is only fetched the first time you ask for it"
                    onChange={(e) => setStyle({ earth: e.target.value as SavedStyle["earth"] })}
                  >
                    <option value="off">no photo</option>
                    <option value="day">daylight</option>
                    <option value="night">city lights</option>
                    <option value="both">both, by the light</option>
                    <option value="moon">both, by moonlight</option>
                  </select>
                </span>
              </>
            )}
            {globeMode && (
              <>
                <span className="pivot-builder-label visual-label-2">Light</span>
                <span className="pivot-chip">
                  {slider("Where the light stands, round the viewer", 0, 360, value(saved.lightAround, styleDefaults.lightAround), (v) => setStyle({ lightAround: v }))}
                  {slider("and how far above them", -90, 90, value(saved.lightUp, styleDefaults.lightUp), (v) => setStyle({ lightUp: v }))}
                  {slider("How bright the lit side is; past a hundred the ground burns out, which is sometimes the point", 0, 300, value(saved.brightness, styleDefaults.brightness), (v) => setStyle({ brightness: v }))}
                  {slider("How much light reaches the dark side, so a night is dim rather than absent", 0, 100, value(saved.ambient, styleDefaults.ambient), (v) => setStyle({ ambient: v }))}
                </span>
                <span className="pivot-builder-label visual-label-2">Shine</span>
                <span className="pivot-chip">
                  {slider("How strong the highlight is: the sun on the sea", 0, 150, value(saved.specular, styleDefaults.specular), (v) => setStyle({ specular: v }))}
                  {slider("and how tight — a wide sheen or a hard point", 0, 100, value(saved.shine, styleDefaults.shine), (v) => setStyle({ shine: v }))}
                  {slider("How much of it the land gets. Water is a mirror and rock is not, so this is usually low", 0, 100, value(saved.landShine, styleDefaults.landShine), (v) => setStyle({ landShine: v }))}
                </span>
              </>
            )}
            {marks === "rods" && (
              <>
                <span className="pivot-builder-label visual-label-2">Rods</span>
                <span className="pivot-chip">
                  <select className="select" value={saved.rodColors ?? styleDefaults.rodColors} title="What a rod is painted by its height" onChange={(e) => setStyle({ rodColors: e.target.value as SavedStyle["rodColors"] })}>
                    <option value="heat">white to purple</option>
                    <option value="palette">the palette</option>
                  </select>
                  {slider("How far the tallest rod reaches", 2, 100, value(saved.rodHeight, styleDefaults.rodHeight), (v) => setStyle({ rodHeight: v }))}
                  {slider("How thick a rod is, as a share of the patch it stands on — at the top of the scale they close up into a honeycomb", 2, 100, value(saved.rodWidth, styleDefaults.rodWidth), (v) => setStyle({ rodWidth: v }))}
                  {slider("How wide a patch of the world each rod stands for, in tenths of a degree", 5, 100, value(saved.rodCell, styleDefaults.rodCell), (v) => setStyle({ rodCell: v }))}
                </span>
              </>
            )}
            <div className="pivot-options">
              <button className="link-button" title="Put everything here back to what it started as" onClick={() => onChange({ ...def, style: {} })}>
                reset
              </button>
            </div>
          </div>
        )}
      </div>

      {showQuery && result && <div className="query-string">{formatQuery(result.query)}</div>}
      {error && <div className="query-error">{error}</div>}

      <div className={"visual-stage" + (def.bare === true ? " bare" : "")} ref={stageRef}>
        <div
          className="visual-canvas map-canvas"
          ref={frameRef}
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onPointerCancel={onPointerUp}
          onPointerLeave={() => setTooltip(null)}
          onWheel={onWheel}
          onDoubleClick={fit}
        >
          <canvas ref={canvasRef} />
          <canvas className="map-bubbles" ref={bubblesRef} />
          {!glOk && <div className="query-empty">This browser has no WebGL 2, which the map is drawn with.</div>}
          {tooltip && (
            <div className="visual-tooltip" style={{ transform: `translate(${tooltip.x + 14}px, ${tooltip.y + 14}px)` }}>
              {tooltip.lines.map((line, i) => (
                <div key={i}>{line}</div>
              ))}
            </div>
          )}
          {points && points.count === 0 && !loading && (
            <div className="visual-empty">{points.read === 0 ? "Nothing matched." : "None of the " + formatCount(points.read) + " nodes found has a position."}</div>
          )}
          {loading && <span className="visual-loading-note">{points ? "updating…" : "loading the map…"}</span>}
          {points && points.count > 0 && points.count < points.read && (
            <span className="visual-count-note" title={formatCount(points.read - points.count) + " of the nodes found have no position, so they are not on the map."}>
              {formatCount(points.count)} of {formatCount(points.read)} placed
            </span>
          )}
          {points && points.read < points.total && (
            <span className="visual-count-note second" title={"The map holds the first " + formatCount(points.read) + " of " + formatCount(points.total) + " nodes; narrow the set to see the rest."}>
              first {formatCount(points.read)} of {formatCount(points.total)}
            </span>
          )}
          {marks === "pins" && points && points.count > pinBudget && (
            <span className="visual-detail-note" title="Pins are drawn one at a time and stop meaning anything in a crowd. Dots, clusters or heat show the whole set.">
              {formatCount(pinBudget)} pins of {formatCount(points.count)}
            </span>
          )}
        </div>
        {def.legend && theme !== null && legendKind !== "none" && (
          <div className="visual-legend">
            {legendKind === "colours" && groups !== null && colorData !== null && (
              <>
                <div className="visual-legend-head">{colorData.name}</div>
                {groups.map((g, i) => (
                  <span className="visual-legend-item static" key={i} title={g.kind === "other" ? "The values with fewer nodes than the groups kept" : g.kind === "none" ? "The nodes without a value" : g.label}>
                    <span className="visual-swatch" style={{ background: cssOf(groupColor(g.kind, g.ordinal, palette, theme)) }} />
                    <span className="visual-legend-label">{g.label}</span>
                    <span className="visual-legend-count">{formatCount(g.count)}</span>
                  </span>
                ))}
              </>
            )}
            {legendKind === "scale" && (
              <>
                <div className="visual-legend-head">{marks === "heat" ? "How thickly" : "Nodes per bubble"}</div>
                <div className="map-scale">
                  <span className="map-scale-bar" style={{ background: `linear-gradient(to top, ${ramp.map((c) => c.css).join(",")})` }} />
                  <span className="map-scale-ends">
                    <span>most</span>
                    <span>least</span>
                  </span>
                </div>
              </>
            )}
            {legendKind === "countries" && counts !== null && (
              <>
                <div className="visual-legend-head">Countries</div>
                {ranked.map((r) => (
                  <button className="visual-legend-item" key={r.index} title={"Close in on " + r.country.name} onClick={() => zoomToBounds(...r.country.bounds)}>
                    <span className="visual-swatch" style={{ background: ramp[rampAt(r.count, counts.max)].css }} />
                    <span className="visual-legend-label">{r.country.name}</span>
                    <span className="visual-legend-count">{formatCount(r.count)}</span>
                  </button>
                ))}
                {counts.atSea > 0 && (
                  <span className="visual-legend-item static" title="Nodes that fell outside every country's outline: at sea, or more than ten kilometres out from a coast, which is as finely as the countries are measured here">
                    <span className="visual-swatch" style={{ background: cssOf(theme.none) }} />
                    <span className="visual-legend-label">(no country)</span>
                    <span className="visual-legend-count">{formatCount(counts.atSea)}</span>
                  </span>
                )}
              </>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

/** A switch on the Look panel, reading on or off rather than carrying an icon nobody would guess. */
function toggle(on: boolean, title: string, onClick: () => void) {
  return (
    <button className={"icon-button labelled" + (on ? " active" : "")} aria-pressed={on} title={title} onClick={onClick}>
      {on ? "on" : "off"}
    </button>
  );
}

/** One knob of the Look panel: narrow, unlabelled, and showing what it is set to. */
function slider(title: string, min: number, max: number, at: number, onChange: (value: number) => void) {
  return (
    <span className="visual-stretch" title={title}>
      <input type="range" min={min} max={max} step={1} value={at} onChange={(e) => onChange(Number(e.target.value))} />
      <span className="visual-stretch-value">{at}</span>
    </span>
  );
}

const value = (saved: number | undefined, fallback: number) => (typeof saved === "number" ? saved : fallback);

/** The page's own colours, which is what keeps the map in the theme the rest of the app is in. */
function readTheme(el: HTMLElement): Theme {
  const cs = getComputedStyle(el);
  const v = (name: string, fallback: string) => parseCssColor(cs.getPropertyValue(name).trim() || fallback);
  const faint = v("--text-faint", "#a6a39d");
  return {
    panel: v("--stage", "#ffffff"),
    accent: v("--accent", "#0960b2"),
    text: v("--text", "#1d1c1a"),
    line: v("--border", "#c6c1b9"),
    faint,
    // the nodes without a value, and the ones outside the groups kept: two greys the palette does not use
    none: faint,
    other: v("--border", "#c6c1b9"),
  };
}

/** Which step of the ramp a count sits on: by square root, so the small ones are still told apart. */
function rampAt(count: number, max: number): number {
  if (max <= 0) return 0;
  return Math.min(rampSteps - 1, Math.max(0, Math.round(Math.sqrt(count / max) * (rampSteps - 1))));
}

function groupColor(kind: MapGroup["kind"], ordinal: number, palette: PaletteColor[], theme: Theme): RGB {
  if (kind === "none") return theme.none;
  if (kind === "other") return theme.other;
  return palette[Math.max(0, ordinal) % Math.max(1, palette.length)]?.rgb ?? theme.faint;
}

const cssOf = (rgb: RGB) => `rgb(${rgb[0]} ${rgb[1]} ${rgb[2]})`;
const mix = (a: RGB, b: RGB, t: number): RGB => [Math.round(a[0] + (b[0] - a[0]) * t), Math.round(a[1] + (b[1] - a[1]) * t), Math.round(a[2] + (b[2] - a[2]) * t)];

/** The same colour with the colour taken out of it: as light as it was, and no hue at all. */
function grey(rgb: RGB): RGB {
  const light = Math.round(0.299 * rgb[0] + 0.587 * rgb[1] + 0.114 * rgb[2]);
  return [light, light, light];
}

const wrapLongitude = (lon: number) => ((((lon + 180) % 360) + 360) % 360) - 180;

/** A place, written the way a map writes one. */
function placeLabel(lat: number, lon: number): string {
  return Math.abs(lat).toFixed(3) + "° " + (lat >= 0 ? "N" : "S") + ", " + Math.abs(lon).toFixed(3) + "° " + (lon >= 0 ? "E" : "W");
}

/** Numbers, dates and durations can be grouped by ranges as well as by value. */
function hasModes(property: PivotProperty): boolean {
  return property.numeric || property.isDate || property.type === "TimeSpan";
}

/**
 * How solid one dot is. A handful of them should be plainly there; a million on a world map are a
 * cloud, and a cloud made of solid dots is a blot - so the more there are, the more each one lets
 * through, and the crowd shows its shape through what piles up.
 */
function dotAlpha(count: number): number {
  if (count <= 2000) return 0.95;
  if (count <= 50_000) return 0.6;
  if (count <= 300_000) return 0.35;
  return 0.22;
}

const defaultSize = (marks: MarkKind) => (marks === "pins" ? 22 : marks === "clusters" ? 48 : marks === "heat" ? 26 : 3);
const sizeLabel = (marks: MarkKind) => (marks === "heat" ? "Spread" : marks === "clusters" ? "Patch" : "Size");
const sizeRange = (marks: MarkKind): [number, number] => (marks === "heat" ? [6, 90] : marks === "clusters" ? [20, 140] : marks === "pins" ? [8, 64] : [1, 14]);
const sizeHint = (marks: MarkKind) =>
  marks === "heat"
    ? "How far one node's heat spreads, in pixels"
    : marks === "clusters"
      ? "How wide one patch of the map is, in pixels: wider patches, fewer and larger bubbles"
      : marks === "pins"
        ? "How tall a pin stands, in pixels"
        : "How large one mark is drawn, in pixels";
const currentSize = (def: MapDefinition, marks: MarkKind) =>
  marks === "heat" ? (def.radius ?? 26) : marks === "clusters" ? (def.cell ?? 48) : marks === "pins" ? (def.pinSize ?? defaultSize(marks)) : (def.size ?? defaultSize(marks));
const withSize = (def: MapDefinition, marks: MarkKind, value: number): MapDefinition =>
  marks === "heat" ? { ...def, radius: value } : marks === "clusters" ? { ...def, cell: value } : marks === "pins" ? { ...def, pinSize: value } : { ...def, size: value };
