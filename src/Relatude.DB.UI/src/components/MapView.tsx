import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { IconFocusCentered, IconGrid3x3, IconListDetails, IconRotate360, IconWorld } from "@tabler/icons-react";
import { BareButton, FullscreenButton } from "./DatamodelGraph";
import type { PivotBase } from "./PivotView";
import { fetchCards, fetchNodeGuid, fetchPivotModel, runMap, type MapRequest, type PivotModel, type PivotProperty } from "../server/query";
import { useLiveResult } from "../server/hooks";
import { formatCount, formatQuery } from "../format";
import type { MapDefinition, MapMarks } from "../queryTabs";
import { buildPalette, buildRamp, palettes, parseCssColor, type PaletteColor, type RGB } from "../visual/palette";
import { clampView, fitView, projections, projectionOf, screenX, screenY, worldX, worldY, type View } from "../map/projection";
import { countryPath, graticulePath, outlinePath, pathScale } from "../map/paths";
import { countCountries, countryAt, countryRaster, countryRasterSize, rankedCountries } from "../map/countries";
import { decodeMap, PointIndex, type MapGroup } from "../map/points";
import { clusterPoints, clusterRadius, drawClusters, drawDots, drawHeat, drawPins, MarkSurface, type Cluster, type Place, type PointColors } from "../map/marks";
import { createGlobe, maxGlobeZoom, unitVectors, type Globe, type GlobeCamera } from "../map/globe";
import { world } from "../map/worldMap";

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

const markOptions: { id: MapMarks; label: string; hint: string }[] = [
  { id: "dots", label: "Dots", hint: "A dot per node — the plainest picture of where they are, and the one that holds a million of them" },
  { id: "pins", label: "Pins", hint: "A marker per node, standing on its place; for a set small enough to pick individual nodes out of" },
  { id: "heat", label: "Heat", hint: "How thickly the nodes lie, as a field of colour — where they crowd rather than where each one is" },
  { id: "clusters", label: "Clusters", hint: "One bubble per patch of the map, sized and coloured by how many nodes are in it, with the count written in" },
  { id: "countries", label: "Countries", hint: "Every country shaded by how many nodes are in it" },
];

const modeOptions = [
  { value: "auto", label: "auto" },
  { value: "values", label: "values" },
  { value: "ranges", label: "ranges" },
];

/** How many marks are drawn while a hand is still moving the map; the rest arrive when it settles. */
const movingBudget = 120_000;
/** and how many pins are ever drawn: past this they are a smear rather than markers, and the view says so */
const pinBudget = 20_000;
const settleMs = 130;
/** How near a pointer has to be to a node to pick it, in pixels. */
const pickPixels = 9;
const minScale = 40;
const maxScale = 400_000;
/** The colour ramp's resolution: enough that a heat map has no bands in it. */
const rampSteps = 64;
/** The picture wrapped round the globe when countries are shaded; half the raster, which is plenty at this size. */
const surfaceShrink = 2;

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
  /** the view (or the camera) as it was when the hand went down */
  fromView: View;
  fromCamera: GlobeCamera;
}

/**
 * The map: every node of the result standing on the place one of its properties says it is.
 *
 * Two pictures of the same points, and either will do: a flat world map, which is svg - the world
 * is six hundred lines of coastline and border and belongs in the dom, where it costs nothing to
 * keep - and a globe, which is WebGL, because a sphere is not something svg does. The nodes
 * themselves are never dom in either: they go on a canvas over the flat map and into a vertex
 * buffer on the globe, and there can be a million of them.
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
      // the colouring is only asked for when something is drawn in it; a heat map has no use for it
      properties: coloured && colorProperty !== null ? [{ propertyId: colorProperty, mode: def.colorMode }] : [],
    };
    // the token is not part of the request; a new object is how the runner is told to run again
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [model, definition === null, base, property, coloured, colorProperty, def.colorMode, refreshToken]);
  const { result, loading, error } = useLiveResult(request, runMap);
  const points = useMemo(() => (result ? decodeMap(result) : null), [result]);
  const colorData = colorProperty === null ? null : (points?.byProperty.get(colorProperty) ?? null);

  // ---- the canvases and what is on them ----

  const stageRef = useRef<HTMLDivElement>(null);
  /** The part of the stage the map is actually drawn in - the stage less whatever the legend takes. */
  const frameRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const globeRef = useRef<HTMLCanvasElement>(null);
  const globe = useRef<Globe | null>(null);
  const surface = useRef(new MarkSurface());
  const [size, setSize] = useState({ width: 0, height: 0 });
  const [theme, setTheme] = useState<Theme | null>(null);
  const [glOk, setGlOk] = useState(true);
  const [tooltip, setTooltip] = useState<Tooltip | null>(null);
  /** how many marks the last frame actually drew, and whether it had to thin them out to do it */
  const [drawn, setDrawn] = useState<{ count: number; thinned: boolean }>({ count: 0, thinned: false });

  const [view, setView] = useState<View>({ cx: 0, cy: 0, scale: 200 });
  const [camera, setCamera] = useState<GlobeCamera>({ lat: 20, lon: 0, zoom: 1 });
  const drag = useRef<Drag | null>(null);
  const moving = useRef(false);
  const settle = useRef(0);
  const frame = useRef(0);
  const pending = useRef<{ view?: View; camera?: GlobeCamera } | null>(null);

  useEffect(
    () => () => {
      if (frame.current !== 0) cancelAnimationFrame(frame.current);
    },
    [],
  );

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

  // the world, drawn for this projection: three strings, rebuilt only when the projection changes
  const outline = useMemo(() => outlinePath(projection), [projection]);
  const graticule = useMemo(() => graticulePath(projection), [projection]);

  /**
   * Where every node is on the flat map, in world units: one pass per result and per projection.
   * Only for the flat map - at a million nodes this is eight megabytes and a pass over all of them,
   * and the globe has no use for it. The ball's own form below is the same bargain the other way.
   */
  const flatXY = useMemo(() => {
    if (points === null || globeMode) return null;
    const out = new Float32Array(points.count * 2);
    for (let i = 0; i < points.count; i++) {
      const [x, y] = projection.project(points.lon[i], points.lat[i]);
      out[i * 2] = x;
      out[i * 2 + 1] = y;
    }
    return out;
  }, [points, projection, globeMode]);

  /** And the same as points on a ball, which is what the globe is given. */
  const unit = useMemo(() => (points === null || !globeMode ? null : unitVectors(points.lat, points.lon, points.count)), [points, globeMode]);
  /** The grid that finds a node under the pointer; only the two views that HAVE nodes to find need it. */
  const index = useMemo(() => (points === null || !coloured ? null : new PointIndex(points.lat, points.lon, points.count)), [points, coloured]);

  const counts = useMemo(() => (points !== null && marks === "countries" ? countCountries(points.lat, points.lon, points.count) : null), [points, marks]);

  const palette = useMemo(() => (theme === null ? [] : buildPalette(colorData ? colorData.groups.length : 1, theme.panel, theme.accent, def.palette)), [theme, colorData, def.palette]);
  const ramp = useMemo(() => (theme === null ? [] : buildRamp(rampSteps, theme.panel, theme.accent, def.palette)), [theme, def.palette]);
  /** The ramp as bytes with the low end faded out, which is what a heat field is read through. */
  const rampBytes = useMemo(() => {
    const bytes = new Uint8Array(ramp.length * 4);
    ramp.forEach((c, i) => {
      bytes[i * 4] = c.rgb[0];
      bytes[i * 4 + 1] = c.rgb[1];
      bytes[i * 4 + 2] = c.rgb[2];
      // the thin edge of a crowd has to let the map show through, the thick middle does not
      bytes[i * 4 + 3] = Math.round(255 * Math.min(1, 0.12 + (i / Math.max(1, ramp.length - 1)) * 1.4));
    });
    return bytes;
  }, [ramp]);
  const markColors = useMemo<PointColors>(() => {
    const groups = colorData ? colorData.groups : null;
    const bytes = new Uint8Array(Math.max(1, groups ? groups.length : 1) * 3);
    if (groups === null || theme === null) {
      const first = palette[0]?.rgb ?? [128, 128, 128];
      bytes.set(first);
    } else {
      groups.forEach((g, i) => bytes.set(groupColor(g.kind, g.ordinal, palette, theme), i * 3));
    }
    return { palette: bytes, assignment: colorData ? colorData.assignment : null };
  }, [colorData, palette, theme]);

  /** The countries as a picture to wrap round the globe: the raster painted with the counts. */
  const globeSurface = useMemo(() => {
    if (counts === null || theme === null || marks !== "countries" || !globeMode) return null;
    const raster = countryRaster();
    const [rw, rh] = countryRasterSize();
    const w = Math.floor(rw / surfaceShrink);
    const h = Math.floor(rh / surfaceShrink);
    const image = new ImageData(w, h);
    const words = new Uint32Array(image.data.buffer);
    for (let y = 0; y < h; y++) {
      for (let x = 0; x < w; x++) {
        const country = raster[y * surfaceShrink * rw + x * surfaceShrink];
        if (country === 0) continue;
        const n = counts.counts[country - 1];
        if (n === 0) continue;
        const c = ramp[rampAt(n, counts.max)].rgb;
        words[y * w + x] = (230 << 24) | (c[2] << 16) | (c[1] << 8) | c[0];
      }
    }
    return image;
  }, [counts, theme, marks, globeMode, ramp]);

  /** The shape of each country that holds anything, for the flat map to fill. */
  const countryShapes = useMemo(() => {
    if (counts === null || globeMode) return null;
    return rankedCountries(counts).map((r) => ({ ...r, d: countryPath(projection, r.country) }));
  }, [counts, projection, globeMode]);

  // ---- the view, and the hand that moves it ----

  /** Whether anyone has moved the map. A map nobody has touched belongs to the view and is refitted
   *  whenever the view changes shape - the legend opening, the window, the picture going fullscreen;
   *  one somebody has panned or zoomed is theirs, and is left exactly where they put it. */
  const touched = useRef(false);
  const fit = useCallback(() => {
    touched.current = false;
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

  /** One re-render a frame while a hand is moving something, however many events arrive. */
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

  /** A gesture has begun: the map thins its marks out until the hand stops. */
  const startMoving = useCallback(() => {
    moving.current = true;
    window.clearTimeout(settle.current);
    settle.current = window.setTimeout(() => {
      moving.current = false;
      setSettled((n) => n + 1); // and everything is drawn again, in full
    }, settleMs);
  }, []);
  const [settled, setSettled] = useState(0);

  const onPointerDown = (e: React.PointerEvent) => {
    (e.target as Element).setPointerCapture?.(e.pointerId);
    drag.current = { x: e.clientX, y: e.clientY, moved: false, fromView: view, fromCamera: camera };
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
    if (!d.moved && Math.abs(dx) + Math.abs(dy) < 3) return;
    d.moved = true;
    setTooltip(null);
    startMoving();
    if (globeMode) {
      // a drag turns the ball under the pointer: how many degrees a pixel is depends on how close
      // the camera has come, so the ground keeps up with the hand at every zoom
      const perPixel = (globe.current?.degreesPerPixel(d.fromCamera) ?? 0.2) * 1.15;
      nudge({
        camera: {
          lat: Math.max(-88, Math.min(88, d.fromCamera.lat + dy * perPixel)),
          lon: wrapLongitude(d.fromCamera.lon - dx * perPixel),
          zoom: d.fromCamera.zoom,
        },
      });
    } else {
      nudge({ view: clampView({ ...d.fromView, cx: d.fromView.cx - dx / d.fromView.scale, cy: d.fromView.cy - dy / d.fromView.scale }, projection, size.width, size.height) });
    }
  };

  const onPointerUp = (e: React.PointerEvent) => {
    const d = drag.current;
    drag.current = null;
    if (d === null || d.moved) return;
    const rect = e.currentTarget.getBoundingClientRect();
    click(e.clientX - rect.left, e.clientY - rect.top);
  };

  const onWheel = (e: React.WheelEvent) => {
    if (size.width <= 0) return;
    startMoving();
    const factor = Math.pow(1.0016, -e.deltaY);
    if (globeMode) {
      nudge({ camera: { ...camera, zoom: Math.max(1, Math.min(maxGlobeZoom, camera.zoom * factor)) } });
      return;
    }
    const rect = e.currentTarget.getBoundingClientRect();
    const px = e.clientX - rect.left;
    const py = e.clientY - rect.top;
    // the place under the pointer stays under the pointer, which is what makes a wheel feel like a zoom
    const wx = worldX(view, px, size.width);
    const wy = worldY(view, py, size.height);
    const scale = Math.max(minScale, Math.min(maxScale, view.scale * factor));
    nudge({
      view: clampView({ scale, cx: wx - (px - size.width / 2) / scale, cy: wy - (py - size.height / 2) / scale }, projection, size.width, size.height),
    });
  };

  /** The node nearest a point of the canvas, or -1. */
  const pointAt = useCallback(
    (px: number, py: number): number => {
      if (points === null || index === null) return -1;
      if (globeMode) {
        const place = globe.current?.placeAt(camera, px, py);
        if (!place) return -1;
        return index.nearest(place[1], place[0], (globe.current?.degreesPerPixel(camera) ?? 0.2) * pickPixels);
      }
      const at = projection.invert(worldX(view, px, size.width), worldY(view, py, size.height));
      if (at === null) return -1;
      // a pick radius in pixels, said in degrees: what the projection does to a degree there
      const [x0] = projection.project(at[0], at[1]);
      const [x1] = projection.project(at[0] + 0.1, at[1]);
      const perDegree = Math.max(1e-9, Math.abs(x1 - x0) / 0.1) * view.scale;
      return index.nearest(at[0], at[1], pickPixels / perDegree);
    },
    [points, index, globeMode, camera, projection, view, size.width, size.height],
  );

  const names = useRef(new Map<number, string>());
  const hover = (px: number, py: number) => {
    if (points === null) return;
    if (marks === "countries") {
      const at = placeUnder(px, py);
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
    if (marks === "heat") {
      const at = placeUnder(px, py);
      setTooltip(at === null ? null : { x: px, y: py, lines: [placeLabel(at[0], at[1])] });
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

  /** The place under a point of the canvas, in degrees, whichever picture is on screen. */
  const placeUnder = useCallback(
    (px: number, py: number): [number, number] | null => {
      if (globeMode) return globe.current?.placeAt(camera, px, py) ?? null;
      const at = projection.invert(worldX(view, px, size.width), worldY(view, py, size.height));
      return at === null ? null : [at[1], at[0]];
    },
    [globeMode, camera, projection, view, size.width, size.height],
  );

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
      const at = placeUnder(px, py);
      const country = at === null ? -1 : countryAt(at[1], at[0]);
      if (country >= 0) {
        const [west, south, east, north] = world().countries[country].bounds;
        zoomToBounds(west, south, east, north);
      }
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

  // ---- the globe ----

  useEffect(() => {
    if (!globeMode) return;
    const canvas = globeRef.current;
    if (!canvas) return;
    let made: Globe | null = null;
    try {
      made = createGlobe(canvas);
    } catch {
      made = null;
    }
    globe.current = made;
    setGlOk(made !== null);
    return () => {
      made?.destroy();
      globe.current = null;
    };
  }, [globeMode]);

  useEffect(() => {
    if (globe.current === null || unit === null || points === null) return;
    globe.current.setPoints(unit, points.count);
    globe.current.setColors(markColors.palette, markColors.assignment);
  }, [unit, points, markColors, globeMode, glOk]);

  useEffect(() => {
    globe.current?.setSurface(globeSurface);
  }, [globeSurface, globeMode, glOk]);

  /** The globe turning on its own: only when nothing else is happening to it. */
  useEffect(() => {
    if (!globeMode || def.spin !== true) return;
    let running = true;
    let last = performance.now();
    const step = (now: number) => {
      if (!running) return;
      const dt = (now - last) / 1000;
      last = now;
      if (drag.current === null) setCamera((c) => ({ ...c, lon: wrapLongitude(c.lon + dt * 6) }));
      requestAnimationFrame(step);
    };
    requestAnimationFrame(step);
    return () => {
      running = false;
    };
  }, [globeMode, def.spin]);

  // ---- drawing ----

  const size$ = size.width + "x" + size.height;
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || theme === null || size.width <= 0) return;
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    const deviceWidth = Math.round(size.width * dpr);
    const deviceHeight = Math.round(size.height * dpr);
    if (canvas.width !== deviceWidth || canvas.height !== deviceHeight) {
      canvas.width = deviceWidth;
      canvas.height = deviceHeight;
    }
    const ctx = canvas.getContext("2d");
    if (!ctx) return;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, deviceWidth, deviceHeight);

    if (globeMode && globe.current !== null) {
      globe.current.resize(size.width, size.height, dpr);
      globe.current.setTheme({
        clear: toFloat(theme.panel),
        ground: toFloat(mix(theme.panel, theme.line, 0.35)),
        line: toFloat(mix(theme.line, theme.text, 0.45)),
        glow: toFloat(theme.accent),
      });
      globe.current.draw(camera, marks === "dots" ? "dots" : marks === "pins" ? "pins" : "none", def.size ?? defaultSize(marks), marks === "pins" ? 1 : dotAlpha(points?.count ?? 0), marks === "pins" ? pinBudget : Number.MAX_SAFE_INTEGER);
    }

    if (points === null || points.count === 0) {
      setDrawn((was) => (was.count === 0 && !was.thinned ? was : { count: 0, thinned: false }));
      return;
    }
    const place = placer(globeMode, globe.current, camera, view, flatXY, unit, size.width, size.height);
    if (place === null) return;
    const budget = moving.current ? movingBudget : points.count;
    const step = Math.max(1, Math.ceil(points.count / Math.max(1, budget)));
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    let count = 0;
    if (marks === "dots" && !globeMode) {
      surface.current.begin(deviceWidth, deviceHeight);
      count = drawDots(surface.current, points.count, step, place, markColors, def.size ?? defaultSize(marks), dotAlpha(points.count), dpr);
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      surface.current.put(ctx);
    } else if (marks === "heat") {
      surface.current.begin(deviceWidth, deviceHeight);
      count = drawHeat(surface.current, points.count, step, place, def.radius ?? 26, rampBytes, dpr);
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      surface.current.put(ctx);
    } else if (marks === "pins" && !globeMode) {
      count = drawPins(ctx, points.count, place, size.width, size.height, markColors, def.size ?? defaultSize(marks), cssOf(theme.panel), pinBudget);
    } else if (marks === "clusters") {
      const cell = def.cell ?? 48;
      lastCell.current = cell;
      lastClusters.current = clusterPoints(points.count, step, place, points.lat, points.lon, size.width, size.height, cell);
      count = lastClusters.current.reduce((sum, c) => sum + c.count, 0);
      drawClusters(ctx, lastClusters.current, cell, rampBytes, cssOf(theme.panel), formatCount);
    } else if (globeMode && (marks === "dots" || marks === "pins")) {
      count = points.count; // the globe drew them itself; how many were on the near side it does not say
    }
    const thinned = step > 1 || (marks === "pins" && points.count > pinBudget);
    setDrawn((was) => (was.count === count && was.thinned === thinned ? was : { count, thinned }));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [points, flatXY, unit, view, camera, marks, markColors, rampBytes, theme, size$, globeMode, glOk, def.size, def.radius, def.cell, settled, globeSurface]);

  // ---- what is written beside it ----

  const groups = colorData?.groups ?? null;
  const ranked = useMemo(() => (counts === null ? [] : rankedCountries(counts).slice(0, 40)), [counts]);
  const legendKind = marks === "countries" ? "countries" : coloured && colorData !== null ? "colours" : marks === "heat" || marks === "clusters" ? "scale" : "none";
  const transform = `translate(${size.width / 2 - view.cx * view.scale} ${size.height / 2 - view.cy * view.scale}) scale(${view.scale / pathScale})`;

  /**
   * The world itself: the grid, the shaded countries and the outlines. Held apart from the render
   * so that panning - which changes only the transform on the group holding them - never asks React
   * to look at eight thousand points of path again, and neither does the pointer moving over them.
   */
  const worldElements = useMemo(
    () => (
      <>
        {def.graticule !== false && <path className="map-graticule" d={graticule} vectorEffect="non-scaling-stroke" />}
        {countryShapes?.map((c) => (
          <path key={c.index} className="map-country" d={c.d} fillRule="evenodd" vectorEffect="non-scaling-stroke" fill={counts === null ? "none" : ramp[rampAt(c.count, counts.max)].css} fillOpacity={0.88} />
        ))}
        <path className="map-outline" d={outline} vectorEffect="non-scaling-stroke" />
      </>
    ),
    [def.graticule, graticule, countryShapes, counts, ramp, outline],
  );

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
            {!globeMode && (
              <button
                className={"icon-button" + (def.graticule !== false ? " active" : "")}
                aria-pressed={def.graticule !== false}
                title={def.graticule !== false ? "Hide the lines of latitude and longitude" : "Show the lines of latitude and longitude"}
                onClick={() => onChange({ ...def, graticule: def.graticule === false })}
              >
                <IconGrid3x3 size={16} stroke={1.9} />
              </button>
            )}
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
            <FullscreenButton on={fullscreen} onToggle={onToggleFullscreen} what="map" />
          </div>
        </div>
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
          {globeMode ? (
            glOk ? (
              <canvas ref={globeRef} />
            ) : (
              <div className="query-empty">This browser has no WebGL 2, which the globe is drawn with. The flat map needs none.</div>
            )
          ) : (
            <svg className="map-world" width="100%" height="100%">
              <g transform={transform}>{worldElements}</g>
            </svg>
          )}
          <canvas className="map-marks" ref={canvasRef} />
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
          {drawn.thinned && marks === "pins" && points && points.count > pinBudget && (
            <span className="visual-detail-note" title="Pins are drawn one at a time and stop meaning anything in a crowd. Dots, clusters or heat show the whole set.">
              {formatCount(pinBudget)} pins of {formatCount(points.count)}
            </span>
          )}
          {drawn.thinned && marks !== "pins" && <span className="visual-detail-note" title="Some of the marks are left out while the map is moving; they come back the moment it settles.">drawn thinly</span>}
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
                  <span className="visual-legend-item static" title="Nodes that fell outside every country's outline: at sea, or within about twenty kilometres of a coast, which is as fine as this map is drawn">
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

/** Where each node goes on the canvas, for whichever picture is being drawn. */
function placer(globeMode: boolean, globe: Globe | null, camera: GlobeCamera, view: View, flat: Float32Array | null, unit: Float32Array | null, width: number, height: number): Place | null {
  if (globeMode) {
    if (globe === null || unit === null) return null;
    const project = globe.projectUnit(camera);
    return (i, out) => project(unit[i * 3], unit[i * 3 + 1], unit[i * 3 + 2], out);
  }
  if (flat === null) return null;
  return (i, out) => {
    out[0] = screenX(view, flat[i * 2], width);
    out[1] = screenY(view, flat[i * 2 + 1], height);
    return true;
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
const toFloat = (rgb: RGB): [number, number, number] => [rgb[0] / 255, rgb[1] / 255, rgb[2] / 255];
const mix = (a: RGB, b: RGB, t: number): RGB => [Math.round(a[0] + (b[0] - a[0]) * t), Math.round(a[1] + (b[1] - a[1]) * t), Math.round(a[2] + (b[2] - a[2]) * t)];

const wrapLongitude = (lon: number) => ((((lon + 180) % 360) + 360) % 360) - 180;

/** A place, written the way a map writes one. */
function placeLabel(lat: number, lon: number): string {
  const ns = lat >= 0 ? "N" : "S";
  const ew = lon >= 0 ? "E" : "W";
  return Math.abs(lat).toFixed(3) + "° " + ns + ", " + Math.abs(lon).toFixed(3) + "° " + ew;
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

const defaultSize = (marks: MapMarks) => (marks === "pins" ? 14 : marks === "clusters" ? 48 : marks === "heat" ? 26 : 3);
const sizeLabel = (marks: MapMarks) => (marks === "heat" ? "Spread" : marks === "clusters" ? "Patch" : marks === "countries" ? "" : "Size");
const sizeRange = (marks: MapMarks): [number, number] => (marks === "heat" ? [6, 90] : marks === "clusters" ? [20, 140] : marks === "pins" ? [6, 40] : [1, 14]);
const sizeHint = (marks: MapMarks) =>
  marks === "heat"
    ? "How far one node's heat spreads, in pixels"
    : marks === "clusters"
      ? "How wide one patch of the map is, in pixels: wider patches, fewer and larger bubbles"
      : "How large one mark is drawn, in pixels";
const currentSize = (def: MapDefinition, marks: MapMarks) => (marks === "heat" ? (def.radius ?? 26) : marks === "clusters" ? (def.cell ?? 48) : (def.size ?? defaultSize(marks)));
const withSize = (def: MapDefinition, marks: MapMarks, value: number): MapDefinition =>
  marks === "heat" ? { ...def, radius: value } : marks === "clusters" ? { ...def, cell: value } : { ...def, size: value };
