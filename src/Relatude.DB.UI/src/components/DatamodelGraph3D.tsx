import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { IconArrowBackUp, IconArrowsMaximize, IconArrowsShuffle, IconBinoculars, IconCrosshair, IconFileTypePng, IconFocusCentered, IconHierarchy3, IconPencil, IconPlayerPause, IconPlayerPlay, IconRotate360, IconVolume, IconVolumeOff, IconZoomIn, IconZoomOut } from "@tabler/icons-react";
import type { EditorContext, Selection } from "./DatamodelEditors";
import type { GraphShell } from "./DatamodelGraphView";
import { embeddedColor, kindMeta, propertyColor, relationColor } from "./DatamodelIcons";
import { FullscreenButton, GraphModeSwitch, NamesButton, TypePicker } from "./DatamodelGraph";
import { buildWorld, edgeKinds, edgesKey, expandedKey, readEdges, readExpanded, readRoot, recall, remember, rootKey, unfold, type EdgeKind, type GraphLink, type GraphNode } from "./datamodelGraphModel";
import { fullName, type NodeTypeJson } from "../server/datamodel";
import { formatCount } from "../format";
import { FlyCamera } from "../graph3d/camera";
import { createRenderer, type Renderer, type RGB } from "../graph3d/renderer";
import { add, cross, distance, dot, lerp3, multiply, normalize, perspective, rayPlane, raySphere, scale, sub, transform, view as viewMatrix, type Vec3 } from "../graph3d/math";
import { createScenery, scenePalette, type ScenePalette, type Scenery } from "../graph3d/scenery";
import { startEngineSound, type EngineSound } from "../graph3d/enginesound";
import { groundAt as terrainGroundAt } from "../graph3d/terrain";
import { AC, bodyMatrix, newAircraft, newControls, placeAircraft, stepFlight, toward, V_STALL, V_TRIM, type Aircraft, type Controls } from "../graph3d/flight";

interface Props {
  ctx: EditorContext;
  visibleTypes: Set<string>;
  selection: Selection | null;
  query: string;
  storeId: string;
  /** what the flat and the spatial graph share: the mode switch, the names switch, fullscreen */
  shell: GraphShell;
}

/** A node as the simulation moves it through space. Pinned nodes (fx, fy, fz) stay where they are put. */
interface SimNode {
  id: string;
  kind: "type" | "property";
  x: number;
  y: number;
  z: number;
  vx: number;
  vy: number;
  vz: number;
  r: number;
  charge: number;
  fx: number | null;
  fy: number | null;
  fz: number | null;
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

/** What the pointer is doing between down and up. */
type Drag =
  | { kind: "orbit" | "look" | "pan"; x: number; y: number; t: number }
  | { kind: "node"; id: string; offset: Vec3; normal: Vec3; moved: boolean; sx: number; sy: number };

/** A node's place on the screen this frame, in CSS pixels; depth is the distance along the view axis. */
interface Projected {
  x: number;
  y: number;
  r: number;
  depth: number;
  front: boolean;
}

/** The page's colours, read from the stylesheet so the scene follows the theme. */
interface Theme {
  panel: string;
  panelRgb: RGB;
  bgRgb: RGB;
  /** whether the chosen theme is a dark one, which flips how the range is painted */
  dark: boolean;
  text: string;
  textRgb: RGB;
  textMuted: string;
  textSoft: string;
  textSoftRgb: RGB;
  textFaint: string;
  textFaintRgb: RGB;
  borderRgb: RGB;
  accent: string;
  accentRgb: RGB;
}

// the same simulation as the flat graph, with a third axis
const alphaDecay = 0.024;
const alphaMin = 0.004;
const velocityDecay = 0.62;
const typeLinkLength = 140;
const leafLinkLength = 52;
const typeCharge = -1100;
const leafCharge = -140;
const badgeR = 10.5;
const orbitKey = (storeId: string) => "dmGraph3dOrbit:" + storeId;
const funKey = (storeId: string) => "dmGraph3dFun:" + storeId;
const invertKey = (storeId: string) => "dmGraph3dInvert:" + storeId;
const soundKey = "dmGraph3dSound"; // not per database: whether a page may make a noise is about the room

// ---- fun mode's scales ----
// The flight model works in metres; the graph is drawn in its own units. Two graph units to the
// metre puts a type sphere at about the size of a hangar and a node link at a couple of hundred
// metres, so an aeroplane at cruise passes one every few seconds - close enough to read the names
// off them, far enough apart to have to fly between them.
const unitsPerMetre = 0.5;
const metresPerUnit = 1 / unitsPerMetre;
// The range is modelled small and drawn large, so a peak stands a kilometre and a half over the
// valley the graph floats in and the mesh still covers eleven kilometres of country.
const terrainScale = 7;
const terrainY = -420;
/** the low sun of the front page, ahead and to the left */
const sunDir: Vec3 = normalize([-0.42, 0.3, 0.85]);
const funKeys = new Set([
  "KeyW", "KeyA", "KeyS", "KeyD", "KeyZ", "KeyX", "KeyQ", "KeyE",
  "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight",
  "ShiftLeft", "ShiftRight", "ControlLeft", "ControlRight", "PageUp", "PageDown",
  // N centres a stick that does not centre itself; Home does the same, and has to be swallowed here
  // or it scrolls the page out from under the aeroplane
  "KeyN", "Home",
]);
const relationRgb = parseColor(relationColor);
const embeddedRgb = parseColor(embeddedColor);

/**
 * The graph view again, in space: the same start type, the same unfolding, the same lines, but the
 * layout settles in three dimensions and the camera flies through it. Drawn with WebGL - spheres for
 * types and their properties, ribbons and arrowheads for the lines - with names, badges and line
 * labels written on a canvas over it.
 *
 * The mouse: the left button orbits round the point in focus, the right button pans, the middle
 * button turns the camera where it stands, the wheel flies toward what is under the cursor, and
 * W A S D Q E fly. Every motion carries on a little after the hand lets go. Double-click a type to
 * fly to it, double-click the space around to fit everything in. Dragging a type moves it in the
 * plane facing the camera and leaves it pinned there; the shake button lets go of every pinned type.
 */
export function DatamodelGraph3D({ ctx, visibleTypes, selection, query, storeId, shell }: Props) {
  const baseId = ctx.baseTypeId;
  const [root, setRoot] = useState<string | null>(() => readRoot(storeId));
  const [expanded, setExpanded] = useState<Set<string>>(() => readExpanded(storeId));
  const [edges, setEdges] = useState<Set<EdgeKind>>(() => readEdges(storeId));
  const [autoOrbit, setAutoOrbit] = useState<boolean>(() => recall(orbitKey(storeId)) === true);
  const [glOk, setGlOk] = useState(true);
  // counts the times the GPU has handed the context back, so the renderer is rebuilt on it
  const [glGeneration, setGlGeneration] = useState(0);
  const [fun, setFun] = useState<boolean>(() => recall(funKey(storeId)) === true);
  /** mirrors flight.paused so the toolbar's button shows what the space bar did */
  const [paused, setPaused] = useState(false);
  // The engine, on unless someone has turned it off - and then off for good, across databases and
  // reloads: a page that makes a noise the reader has already declined is worse than a silent one.
  const [engineOn, setEngineOn] = useState<boolean>(() => recall(soundKey) !== false);
  /** the menu a right-click on a type opens, at the point it was clicked */
  const [menu, setMenu] = useState<{ x: number; y: number; id: string } | null>(null);
  const q = query.trim().toLowerCase();

  useEffect(() => remember(rootKey(storeId), root), [storeId, root]);
  useEffect(() => remember(expandedKey(storeId), [...expanded]), [storeId, expanded]);
  useEffect(() => remember(edgesKey(storeId), [...edges]), [storeId, edges]);
  useEffect(() => remember(orbitKey(storeId), autoOrbit), [storeId, autoOrbit]);
  useEffect(() => remember(funKey(storeId), fun), [storeId, fun]);
  useEffect(() => {
    const v = recall(invertKey(storeId));
    if (v && typeof v === "object") {
      flight.current.invertPitch = (v as { pitch?: boolean }).pitch === true;
      flight.current.invertRoll = (v as { roll?: boolean }).roll === true;
    }
  }, [storeId]);
  // leaving fun mode puts the aeroplane away and hands the view back to the camera where it was -
  // and takes the engine with it, quietly: a page that keeps making a noise after the aeroplane has
  // gone is a bug people report as a haunted browser tab
  useEffect(() => {
    if (fun) {
      flight.current.started = false;
      setPaused(false);
      // entering fun mode is a keystroke, which is the gesture a browser wants before a page may
      // make a sound; started here rather than on the first frame so that is still true
      if (engineOn) sound.current = startEngineSound();
      invalidate();
    } else {
      camera.current.stop();
      keys.current.clear();
    }
    return () => {
      sound.current?.stop();
      sound.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- invalidate is stable enough for this
  }, [fun, engineOn]);

  useEffect(() => remember(soundKey, engineOn), [engineOn]);

  const world = useMemo(() => buildWorld(ctx, visibleTypes, edges), [ctx, visibleTypes, edges]);
  // the start type has to be one that is still there and still shown; otherwise it is picked again
  const rootId = root !== null && world.eligible.has(root) ? root : null;
  const { nodes, links } = useMemo(() => unfold(world, ctx, expanded, rootId), [world, ctx, expanded, rootId]);
  // whatever changes the picture under the menu closes it: another start type, a fold, fun mode
  useEffect(() => setMenu(null), [rootId, expanded, fun, edges]);

  const stageRef = useRef<HTMLDivElement>(null);
  const glRef = useRef<HTMLCanvasElement>(null);
  const overlayRef = useRef<HTMLCanvasElement>(null);
  const iconHost = useRef<HTMLSpanElement>(null);
  const renderer = useRef<Renderer | null>(null);
  const camera = useRef(new FlyCamera());
  const sim = useRef<Sim>({ nodes: new Map(), links: [], alpha: 0, alphaTarget: 0 });
  const raf = useRef(0);
  const lastTime = useRef(0);
  const drag = useRef<Drag | null>(null);
  const keys = useRef(new Set<string>());
  const hover = useRef<{ id: string; badge: boolean } | null>(null);
  const size = useRef({ w: 1, h: 1, dpr: 1 });
  const theme = useRef<Theme | null>(null);
  const icons = useRef(new Map<string, HTMLImageElement>());
  const projected = useRef(new Map<string, Projected>());
  // ---- fun mode ----
  const scenery = useRef<Scenery | null>(null);
  const sound = useRef<EngineSound | null>(null);
  const ac = useRef<Aircraft>(newAircraft());
  const ctl = useRef<Controls>(newControls());
  const flight = useRef({
    /** where the camera sits and looks, in graph units; both chase the aeroplane with weight */
    eye: [0, 0, 0] as Vec3,
    at: [0, 0, 0] as Vec3,
    up: [0, 1, 0] as Vec3,
    cockpit: false,
    paused: false,
    prop: 0,
    /** the node the aeroplane last flew through, and how long ago */
    buzz: null as { id: string; label: string; kind: string } | null,
    buzzT: 0,
    visited: new Set<string>(),
    /**
     * How fast time is running for the aeroplane: 1 flying, 0 stopped. Pausing eases it down rather
     * than cutting it, so the aeroplane slows and settles in mid air instead of freezing on a frame.
     */
    timeScale: 1,
    /** where the camera is looking from while it is stopped, and how it keeps drifting after a drag */
    orbit: { yaw: 0, pitch: 0.2, dist: 60, vYaw: 0, vPitch: 0, panX: 0, panY: 0, ready: false },
    /** counts up while the aeroplane is wrecked, then puts it back in the air */
    down: 0,
    started: false,
    invertPitch: false,
    invertRoll: false,
  });
  const planeMat = useRef(new Float32Array(16));
  const funOn = useRef(fun);
  funOn.current = fun && rootId !== null && glOk;
  // what a frame needs from React, read at draw time rather than bound into the handlers
  const names = shell.names;
  const scene = useRef({ nodes, links, selection, q, ctx, names });
  scene.current = { nodes, links, selection, q, ctx, names };
  camera.current.autoOrbit = autoOrbit;
  // the wheel is listened to natively: React registers wheel listeners as passive, and a passive
  // listener cannot keep the page from scrolling under the graph
  const wheelHandler = useRef<(e: WheelEvent) => void>(() => {});
  wheelHandler.current = onWheel;

  const hasStage = rootId !== null && glOk;

  // ---- the canvases: made when the stage appears, sized with it, coloured with the theme ----

  useLayoutEffect(() => {
    const stage = stageRef.current;
    const gl = glRef.current;
    if (!stage || !gl) return;
    let r: Renderer | null = null;
    try {
      r = createRenderer(gl);
    } catch {
      r = null;
    }
    if (!r) {
      setGlOk(false);
      return;
    }
    renderer.current = r;
    try {
      scenery.current = createScenery(r.gl);
    } catch {
      scenery.current = null; // no scenery, no fun mode; the graph itself is unaffected
    }
    theme.current = readTheme(stage);
    const measure = () => {
      const rect = stage.getBoundingClientRect();
      const dpr = window.devicePixelRatio || 1;
      size.current = { w: Math.max(1, rect.width), h: Math.max(1, rect.height), dpr };
      r!.resize(Math.round(rect.width * dpr), Math.round(rect.height * dpr));
      const o = overlayRef.current;
      if (o) {
        o.width = Math.round(rect.width * dpr);
        o.height = Math.round(rect.height * dpr);
      }
      invalidate();
    };
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(stage);
    const mo = new MutationObserver(() => {
      theme.current = readTheme(stage);
      invalidate();
    });
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    const wheel = (e: WheelEvent) => wheelHandler.current(e);
    stage.addEventListener("wheel", wheel, { passive: false });
    // a context the GPU takes away can be given back, if the loss is not treated as final; the
    // renderer is then built again on the restored context
    const lost = (e: Event) => e.preventDefault();
    const restored = () => setGlGeneration((g) => g + 1);
    gl.addEventListener("webglcontextlost", lost);
    gl.addEventListener("webglcontextrestored", restored);
    // the kind icons, as images the canvas can draw: rendered once by React off screen, read back as svg
    const host = iconHost.current;
    if (host) {
      for (const svg of host.querySelectorAll("svg")) {
        const kind = svg.getAttribute("data-kind");
        if (!kind || icons.current.has(kind)) continue;
        let text = svg.outerHTML;
        if (!text.includes("xmlns=")) text = text.replace("<svg", '<svg xmlns="http://www.w3.org/2000/svg"');
        const img = new Image();
        img.onload = () => invalidate();
        img.src = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(text);
        icons.current.set(kind, img);
      }
    }
    return () => {
      ro.disconnect();
      mo.disconnect();
      stage.removeEventListener("wheel", wheel);
      gl.removeEventListener("webglcontextlost", lost);
      gl.removeEventListener("webglcontextrestored", restored);
      scenery.current?.destroy();
      scenery.current = null;
      r!.destroy();
      renderer.current = null;
      cancelAnimationFrame(raf.current);
      raf.current = 0;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- lives with the stage element
  }, [hasStage, glGeneration]);

  // another start type is another picture: nothing carries over, and the camera goes back to its post
  useLayoutEffect(() => {
    sim.current.nodes.clear();
    sim.current.links = [];
    const cam = camera.current;
    cam.stop();
    cam.setPose(cam.poseLookingAt([0, 0, 0], 460, -0.55, -0.32));
  }, [rootId]);

  // ---- the simulation follows the graph ----

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
      const dir = randomDirection();
      const dist = n.kind === "property" ? 12 : 24;
      const from: Vec3 = sponsor ? [sponsor.x, sponsor.y, sponsor.z] : [(Math.random() - 0.5) * 40, (Math.random() - 0.5) * 40, (Math.random() - 0.5) * 40];
      const p = sponsor ? add(from, scale(dir, dist)) : from;
      const isRoot = n.kind === "type" && n.root;
      s.nodes.set(n.id, {
        id: n.id,
        kind: n.kind,
        x: isRoot ? 0 : p[0],
        y: isRoot ? 0 : p[1],
        z: isRoot ? 0 : p[2],
        vx: 0,
        vy: 0,
        vz: 0,
        r: n.r,
        charge: n.kind === "type" ? typeCharge : leafCharge,
        fx: isRoot ? 0 : null,
        fy: isRoot ? 0 : null,
        fz: isRoot ? 0 : null,
      });
    }
    s.links = links.map((l) => ({ a: l.from, b: l.to, length: l.kind === "property" ? leafLinkLength : typeLinkLength, strength: l.kind === "property" ? 0.9 : 0.45 }));
    if (changed || s.nodes.size !== nodes.length) s.alpha = Math.max(s.alpha, 0.7);
    invalidate();
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the sim syncs to the graph, not to the helpers
  }, [nodes, links]);

  // what is selected, searched for, named or set turning changes the picture without touching the graph
  useEffect(() => {
    invalidate();
  }, [selection, q, autoOrbit, names]);

  // the frame id is cleared with the frame: a stale id would make run() believe a loop is still going
  useEffect(
    () => () => {
      cancelAnimationFrame(raf.current);
      raf.current = 0;
    },
    [],
  );

  // ---- frames ----

  /** Asks for a frame; the frame decides whether more must follow. */
  function invalidate() {
    if (raf.current) return;
    lastTime.current = performance.now();
    raf.current = requestAnimationFrame(step);
  }

  function step(now: number) {
    raf.current = 0;
    if (!renderer.current) return;
    const dt = Math.min(0.05, Math.max(0.001, (now - lastTime.current) / 1000));
    lastTime.current = now;
    const s = sim.current;
    let busy = false;
    if (s.alpha > alphaMin || drag.current?.kind === "node") {
      tick(s);
      busy = true;
    }
    const cam = camera.current;
    if (funOn.current) {
      // the aeroplane has the keyboard and the camera; the graph keeps drifting underneath it
      stepFun(dt, now);
      busy = true;
    } else {
      if (keys.current.size > 0) {
        const k = keys.current;
        const fast = k.has("ShiftLeft") || k.has("ShiftRight") ? 2.5 : 1;
        const x = (k.has("KeyD") || k.has("ArrowRight") ? 1 : 0) - (k.has("KeyA") || k.has("ArrowLeft") ? 1 : 0);
        const y = (k.has("KeyE") || k.has("PageUp") ? 1 : 0) - (k.has("KeyQ") || k.has("PageDown") ? 1 : 0);
        const z = (k.has("KeyW") || k.has("ArrowUp") ? 1 : 0) - (k.has("KeyS") || k.has("ArrowDown") ? 1 : 0);
        if (x || y || z) cam.thrust(x * fast, y * fast, z * fast, dt);
        busy = true;
      }
      if (cam.step(dt, now)) busy = true;
    }
    if (drag.current) busy = true;
    draw();
    if (busy) raf.current = requestAnimationFrame(step);
  }

  function reheat(alpha = 0.6) {
    sim.current.alpha = Math.max(sim.current.alpha, alpha);
    invalidate();
  }

  // ---- fun mode: the flight ----

  function rememberInvert() {
    remember(invertKey(storeId), { pitch: flight.current.invertPitch, roll: flight.current.invertRoll });
  }

  /** The ground under a point, in the metres the flight model works in. */
  function groundMetres(xm: number, zm: number) {
    const g = terrainGroundAt((xm * unitsPerMetre) / terrainScale, (zm * unitsPerMetre) / terrainScale);
    // the scale is the same in every direction, so the normal carries over untouched
    return { y: (g.y * terrainScale + terrainY) * metresPerUnit, nx: g.nx, ny: g.ny, nz: g.nz };
  }

  /** Puts the aeroplane in the air off to one side of the graph, pointed at it. */
  function launch() {
    const f = flight.current;
    const ns = [...sim.current.nodes.values()];
    let cx = 0;
    let cy = 0;
    let cz = 0;
    for (const n of ns) {
      cx += n.x / Math.max(1, ns.length);
      cy += n.y / Math.max(1, ns.length);
      cz += n.z / Math.max(1, ns.length);
    }
    let radius = 160;
    for (const n of ns) radius = Math.max(radius, distance([cx, cy, cz], [n.x, n.y, n.z]));
    // Far enough out to see the whole graph against the range, and at the height the graph itself
    // floats at rather than above it: now that the aeroplane holds its attitude it flies dead
    // straight, so whatever height it starts at is the height it arrives at, and starting over the
    // top of the model means never flying through it.
    const startUnits: Vec3 = [cx, cy + radius * 0.12, cz + radius + 240];
    const startM: Vec3 = [startUnits[0] * metresPerUnit, startUnits[1] * metresPerUnit, startUnits[2] * metresPerUnit];
    const groundM = groundMetres(startM[0], startM[2]).y;
    startM[1] = Math.max(startM[1], groundM + 90);
    // heading pi points along -z, which is where the graph is from here
    placeAircraft(ac.current, startM, Math.PI, V_TRIM * 1.15);
    ctl.current = newControls();
    f.paused = false;
    f.timeScale = 1;
    f.orbit.ready = false;
    f.down = 0;
    f.buzz = null;
    f.buzzT = 0;
    f.prop = 0;
    f.started = true;
    // the camera starts already behind it rather than sweeping in from the last view
    const { eye, at } = chasePoint();
    f.eye = eye;
    f.at = at;
    f.up = [0, 1, 0];
  }

  /** Where the camera wants to be this instant: behind and above, in the cockpit, or in orbit. */
  function chasePoint(): { eye: Vec3; at: Vec3 } {
    const a = ac.current;
    const f = flight.current;
    const p: Vec3 = [a.p[0] * unitsPerMetre, a.p[1] * unitsPerMetre, a.p[2] * unitsPerMetre];
    // stopped in mid air: the mouse walks round the aeroplane instead of flying it
    if (f.orbit.ready) {
      const o = f.orbit;
      const cp = Math.cos(o.pitch);
      const dir: Vec3 = [Math.sin(o.yaw) * cp, Math.sin(o.pitch), -Math.cos(o.yaw) * cp];
      const at = add(p, add(scale(a.right, o.panX), scale([0, 1, 0] as Vec3, o.panY)));
      return { eye: sub(at, scale(dir, o.dist)), at };
    }
    if (flight.current.cockpit) {
      const eye = add(p, add(scale(a.fwd, 1.1 * unitsPerMetre), scale(a.up, 0.75 * unitsPerMetre)));
      return { eye, at: add(eye, scale(a.fwd, 100)) };
    }
    // The seat is behind and above, and it looks a little ahead of the aeroplane rather than at it,
    // so what you are about to fly into is on screen and not behind the tail. "Above" is above in
    // the WORLD, not above the aeroplane: a camera that took its height from the wing swung out
    // sideways through every turn and hung upside down through a roll, and the horizon went with it.
    // Along the fuselage it still follows the nose, so the camera trails the flight path.
    const back = 22 + Math.min(18, a.V * 0.22);
    const eye = add(p, add(scale(a.fwd, -back), scale([0, 1, 0] as Vec3, 7)));
    return { eye, at: add(p, scale(a.fwd, 26)) };
  }

  /** One step of the flight, the camera that follows it, and what it flies through. */
  function stepFun(dt: number, now: number) {
    const f = flight.current;
    if (!f.started) launch();
    const a = ac.current;
    const c = ctl.current;
    const k = keys.current;
    const held = (...codes: string[]) => codes.some((x) => k.has(x));

    // ---- what the pilot is asking for ----
    let ail = 0;
    let elev = 0;
    let rud = 0;
    let centre = false;
    if (!f.paused && !a.crashed) {
      // Roll and yaw are mapped the way the glider maps them: the left key rolls right. Reversed at
      // the input rather than in the aerodynamics, so the moments and the surfaces still agree.
      if (held("ArrowLeft", "KeyA")) ail += 1;
      if (held("ArrowRight", "KeyD")) ail -= 1;
      if (f.invertRoll) ail = -ail;
      // stick forward is nose down, as it would be in the hand
      if (held("ArrowUp", "KeyW")) elev -= 1;
      if (held("ArrowDown", "KeyS")) elev += 1;
      if (f.invertPitch) elev = -elev;
      if (held("KeyZ")) rud += AC.RUD_MAX;
      if (held("KeyX")) rud -= AC.RUD_MAX;
      centre = held("KeyN", "Home");
      const up = held("ShiftLeft", "ShiftRight", "PageUp", "KeyE") ? 1 : 0;
      const dn = held("ControlLeft", "ControlRight", "PageDown", "KeyQ") ? 1 : 0;
      c.throttle = Math.max(0, Math.min(1, c.throttle + (up - dn) * dt * 0.55));
    }
    // The stick STAYS WHERE IT IS PUT. A key moves it and letting go leaves it there, which is how
    // the glider flew and what makes trimming out a climb or holding a turn possible with a
    // keyboard: a stick that springs back to neutral has to be held against the whole flight, and
    // every manoeuvre becomes a key held down rather than an attitude set and left. C centres both
    // again, which is the one thing a stick that does not self-centre has to offer.
    const stickRate = 1.9; // full travel in about half a second of holding the key
    if (centre) {
      c.ail = toward(c.ail, 0, 7, dt);
      c.elev = toward(c.elev, 0, 7, dt);
    } else {
      if (ail !== 0) c.ail = Math.max(-1, Math.min(1, c.ail + ail * stickRate * dt));
      if (elev !== 0) c.elev = Math.max(-1, Math.min(1, c.elev + elev * stickRate * dt));
    }
    // the pedals do spring back: a rudder left standing in a corner is a spin nobody asked for
    c.rud = toward(c.rud, rud, 5.0, dt);

    // ---- the flight ----
    // Pausing does not cut the film: time slows over about a second and a half and the aeroplane
    // settles where it is, which is what makes it possible to stop mid manoeuvre and walk round it.
    const wantScale = f.paused ? 0 : 1;
    f.timeScale += (wantScale - f.timeScale) * (1 - Math.exp(-dt * (f.paused ? 2.6 : 5)));
    if (f.paused && f.timeScale < 0.02) f.timeScale = 0;
    if (!f.paused && f.timeScale > 0.995) f.timeScale = 1;
    // the mouse gets the camera once the aeroplane has all but stopped, and gives it back on the
    // first frame of flying again
    if (f.timeScale < 0.25 && f.paused) {
      if (!f.orbit.ready) {
        // start from where the chase camera already is, so nothing jumps
        const p: Vec3 = [a.p[0] * unitsPerMetre, a.p[1] * unitsPerMetre, a.p[2] * unitsPerMetre];
        const d = sub(f.eye, p);
        const len = Math.max(12, Math.hypot(d[0], d[1], d[2]));
        f.orbit.dist = len;
        f.orbit.pitch = Math.asin(Math.max(-1, Math.min(1, d[1] / len)));
        f.orbit.yaw = Math.atan2(d[0], -d[2]);
        f.orbit.panX = 0;
        f.orbit.panY = 0;
        f.orbit.vYaw = 0;
        f.orbit.vPitch = 0;
        f.orbit.ready = true;
      }
    } else if (!f.paused) f.orbit.ready = false;

    const scaled = dt * f.timeScale;
    if (scaled > 1e-4) {
      // a fixed step, so the physics behaves the same on a slow frame as on a fast one
      let left = Math.min(scaled, 0.1);
      while (left > 1e-5) {
        const h = Math.min(0.008, left);
        stepFlight(a, c, h, now / 1000, groundMetres, { speed: 4.6, dirX: 0.2, dirZ: -0.98 });
        left -= h;
      }
      f.prop += scaled * (6 + 42 * c.throttle);
      // wrecked: let it lie there a moment, then put it back in the air
      if (a.crashed) {
        f.down += scaled;
        if (f.down > 1.6) launch();
      }
    }
    // The engine answers to the throttle and the canopy to the airspeed. Outside the stepped block
    // on purpose: pausing has to be heard, and the step is skipped entirely once time has stopped.
    sound.current?.update(c.throttle, a.V, a.crashed || f.timeScale < 0.05);

    // the orbit keeps turning a moment after the hand lets go, like everything else here
    if (f.orbit.ready && drag.current === null) {
      const o = f.orbit;
      if (Math.abs(o.vYaw) + Math.abs(o.vPitch) > 1e-4) {
        o.yaw += o.vYaw * dt;
        o.pitch = Math.max(-1.45, Math.min(1.45, o.pitch + o.vPitch * dt));
        const decay = Math.exp(-dt * 3.4);
        o.vYaw *= decay;
        o.vPitch *= decay;
      }
    }

    // ---- what it flew through ----
    const pu: Vec3 = [a.p[0] * unitsPerMetre, a.p[1] * unitsPerMetre, a.p[2] * unitsPerMetre];
    f.buzzT = Math.max(0, f.buzzT - dt);
    for (const n of sim.current.nodes.values()) {
      if (n.kind !== "type") continue;
      if (distance(pu, [n.x, n.y, n.z]) > n.r + 14) continue;
      const node = scene.current.nodes.find((x) => x.id === n.id);
      if (!node || node.kind !== "type") continue;
      if (f.buzz?.id !== n.id) {
        f.buzz = { id: n.id, label: node.type.CodeName, kind: kindMeta[node.type.ModelType]?.label ?? "type" };
        f.visited.add(n.id);
      }
      f.buzzT = 2.4;
      break;
    }

    // ---- the camera follows, with weight ----
    // the camera lags toward the aeroplane, so anything non-finite in it would stay there for good
    if (!isFinite(f.eye[0] + f.eye[1] + f.eye[2] + f.at[0] + f.at[1] + f.at[2] + f.up[0] + f.up[1] + f.up[2])) {
      const fresh = chasePoint();
      f.eye = fresh.eye;
      f.at = fresh.at;
      f.up = [0, 1, 0];
    }
    const want = chasePoint();
    // a first-order lag: the camera never quite catches up, so a hard pull swings it out behind
    const kEye = 1 - Math.exp(-dt * (f.cockpit || f.orbit.ready ? 60 : 5.5));
    const kAt = 1 - Math.exp(-dt * (f.cockpit || f.orbit.ready ? 60 : 8));
    f.eye = lerp3(f.eye, want.eye, kEye);
    f.at = lerp3(f.at, want.at, kAt);
    // The horizon stays level. In the cockpit it cannot - the aeroplane is what you are strapped
    // into, and the world turning round you is the whole point - but from the chase seat a horizon
    // that rolls with the wing is what makes a barrel roll unwatchable and a spin unflyable. The one
    // exception is a view line straight up or straight down, where the world's up and the line of
    // sight are the same direction and there is no frame to build from them: there the aeroplane's
    // own up breaks the tie, and it is the only thing that can.
    const dir = normalize(sub(want.at, want.eye));
    const vertical = Math.abs(dir[1]);
    const level: Vec3 = vertical > 0.985 ? normalize(lerp3([0, 1, 0], a.up, Math.min(1, (vertical - 0.985) / 0.014))) : [0, 1, 0];
    const wantUp: Vec3 = f.cockpit ? a.up : level;
    f.up = normalize(lerp3(f.up, wantUp, 1 - Math.exp(-dt * 6)));
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
      n.fx = n.fy = n.fz = null;
      n.vx += (Math.random() - 0.5) * 30;
      n.vy += (Math.random() - 0.5) * 30;
      n.vz += (Math.random() - 0.5) * 30;
    }
    reheat(0.9);
  }

  // ---- the camera ----

  /**
   * Backs the camera off, on its present heading, until every node with its label is in the frame:
   * each node says how far back the eye must stand for it to fit sideways and vertically, given
   * where it lies along the view axis, and the farthest of those answers wins.
   */
  function fit() {
    const cam = camera.current;
    const ns = [...sim.current.nodes.values()];
    const center: Vec3 = [0, 0, 0];
    if (ns.length > 0) {
      let lo: Vec3 = [Infinity, Infinity, Infinity];
      let hi: Vec3 = [-Infinity, -Infinity, -Infinity];
      for (const n of ns) {
        lo = [Math.min(lo[0], n.x), Math.min(lo[1], n.y), Math.min(lo[2], n.z)];
        hi = [Math.max(hi[0], n.x), Math.max(hi[1], n.y), Math.max(hi[2], n.z)];
      }
      center[0] = (lo[0] + hi[0]) / 2;
      center[1] = (lo[1] + hi[1]) / 2;
      center[2] = (lo[2] + hi[2]) / 2;
    }
    const f = cam.forward();
    const r = cam.right();
    const u = cam.up();
    const tanV = Math.tan(cam.fov / 2);
    const tanH = tanV * (size.current.w / size.current.h);
    let dist = 80;
    for (const n of ns) {
      const rel = sub([n.x, n.y, n.z], center);
      const z = dot(rel, f);
      const x = Math.abs(dot(rel, r)) + n.r + (n.kind === "type" ? 70 : 90);
      const y = Math.abs(dot(rel, u)) + n.r + (n.kind === "type" ? 45 : 20);
      dist = Math.max(dist, x / tanH - z, y / tanV - z);
    }
    cam.animateTo(cam.poseLookingAt(center, dist * 1.03));
    invalidate();
  }
  function zoomBy(factor: number) {
    const cam = camera.current;
    cam.animateTo(cam.poseLookingAt(cam.target(), Math.max(12, cam.dist / factor)), 300);
    invalidate();
  }
  function flyTo(id: string) {
    const n = sim.current.nodes.get(id);
    if (!n) return;
    const cam = camera.current;
    cam.animateTo(cam.poseLookingAt([n.x, n.y, n.z], Math.max(110, n.r * 7)), 600);
    invalidate();
  }

  // ---- the pointer ----

  /** The ray from the eye through a point of the stage, in world space. */
  function rayAt(clientX: number, clientY: number): { origin: Vec3; dir: Vec3; px: number; py: number } {
    const rect = stageRef.current!.getBoundingClientRect();
    const px = clientX - rect.left;
    const py = clientY - rect.top;
    const ndcX = (px / size.current.w) * 2 - 1;
    const ndcY = 1 - (py / size.current.h) * 2;
    const cam = camera.current;
    return { origin: cam.pos, dir: cam.ray(ndcX, ndcY, size.current.w / size.current.h), px, py };
  }

  /** What is under the pointer: a badge, a node, a line, or nothing - the nearest first. */
  function hit(clientX: number, clientY: number): { kind: "badge" | "node"; id: string } | { kind: "link"; link: GraphLink } | null {
    const { origin, dir, px, py } = rayAt(clientX, clientY);
    const sc = scene.current;
    // the badge sits on the screen, not in space: tested where it is drawn
    let best: { depth: number; id: string } | null = null;
    for (const n of sc.nodes) {
      if (n.kind !== "type" || (n.hidden === 0 && !n.open)) continue;
      const p = projected.current.get(n.id);
      if (!p || !p.front || p.r < 6) continue;
      const bx = p.x + p.r * 0.74;
      const by = p.y - p.r * 0.74;
      if (Math.hypot(px - bx, py - by) <= badgeR + 1 && !occluded(n.id, p) && (best === null || p.depth < best.depth)) best = { depth: p.depth, id: n.id };
    }
    if (best) return { kind: "badge", id: best.id };
    let nearest: { t: number; id: string } | null = null;
    for (const n of sim.current.nodes.values()) {
      const t = raySphere(origin, dir, [n.x, n.y, n.z], n.kind === "type" ? n.r : Math.max(n.r, 6));
      if (t !== null && (nearest === null || t < nearest.t)) nearest = { t, id: n.id };
    }
    if (nearest) return { kind: "node", id: nearest.id };
    // a line, by its distance on the screen
    let bestLink: { d: number; link: GraphLink } | null = null;
    for (const l of sc.links) {
      if (l.kind !== "relation" && l.kind !== "reference" && l.kind !== "embeds") continue;
      const a = projected.current.get(l.from);
      const b = projected.current.get(l.to);
      if (!a || !b || !a.front || !b.front || l.from === l.to) continue;
      const d = pointToSegment(px, py, a.x, a.y, b.x, b.y);
      if (d <= 6 && (bestLink === null || d < bestLink.d)) bestLink = { d, link: l };
    }
    return bestLink ? { kind: "link", link: bestLink.link } : null;
  }

  /** Whether another type's sphere stands between the eye and this node's middle. */
  function occluded(id: string, p: Projected) {
    for (const n of scene.current.nodes) {
      if (n.kind !== "type" || n.id === id) continue;
      const o = projected.current.get(n.id);
      if (!o || !o.front || o.depth >= p.depth) continue;
      if (Math.hypot(o.x - p.x, o.y - p.y) < o.r) return true;
    }
    return false;
  }

  function onPointerDown(e: React.PointerEvent) {
    const stage = stageRef.current;
    if (!stage) return;
    stage.focus({ preventScroll: true });
    if (funOn.current) {
      // flying, the keyboard has it; stopped in mid air, the mouse walks round the aeroplane
      if (!flight.current.orbit.ready || e.button === 1) return;
      const kind = e.button === 2 || e.shiftKey ? "pan" : "orbit";
      drag.current = { kind, x: e.clientX, y: e.clientY, t: performance.now() };
      flight.current.orbit.vYaw = 0;
      flight.current.orbit.vPitch = 0;
      try {
        stage.setPointerCapture(e.pointerId);
      } catch {
        // a pointer the browser has forgotten; the drag still works over the stage
      }
      invalidate();
      return;
    }
    const cam = camera.current;
    const now = performance.now();
    if (e.button === 1 || (e.button === 0 && (e.ctrlKey || e.altKey))) {
      e.preventDefault();
      drag.current = { kind: "look", x: e.clientX, y: e.clientY, t: now };
      cam.hold("look");
    } else if (e.button === 2 || (e.button === 0 && e.shiftKey)) {
      drag.current = { kind: "pan", x: e.clientX, y: e.clientY, t: now };
      cam.hold("pan");
    } else if (e.button === 0) {
      const h = hit(e.clientX, e.clientY);
      if (h?.kind === "badge") {
        toggle(h.id);
        return;
      }
      if (h?.kind === "node") {
        const n = sim.current.nodes.get(h.id)!;
        // moved in the plane through the node facing the camera, keeping the grip where it took hold
        const normal = scale(cam.forward(), -1);
        const { origin, dir } = rayAt(e.clientX, e.clientY);
        const at = rayPlane(origin, dir, [n.x, n.y, n.z], normal) ?? [n.x, n.y, n.z];
        drag.current = { kind: "node", id: h.id, offset: sub([n.x, n.y, n.z], at), normal, moved: false, sx: e.clientX, sy: e.clientY };
        cam.stop();
      } else if (h?.kind === "link") {
        selectLink(h.link);
        return;
      } else {
        drag.current = { kind: "orbit", x: e.clientX, y: e.clientY, t: now };
        cam.hold("orbit");
      }
    } else return;
    try {
      stage.setPointerCapture(e.pointerId);
    } catch {
      // a pointer the browser no longer knows (a synthetic event, a lost touch): the drag still works while it stays over the stage
    }
    invalidate();
  }

  function onPointerMove(e: React.PointerEvent) {
    if (funOn.current) {
      const d = drag.current;
      const o = flight.current.orbit;
      if (!d || !o.ready || d.kind === "node") return;
      const now = performance.now();
      const dt = Math.max(1 / 240, (now - d.t) / 1000);
      const dx = e.clientX - d.x;
      const dy = e.clientY - d.y;
      d.x = e.clientX;
      d.y = e.clientY;
      d.t = now;
      const perPixel = (Math.PI * 1.4) / size.current.h;
      if (d.kind === "orbit") {
        o.yaw += dx * perPixel;
        o.pitch = Math.max(-1.45, Math.min(1.45, o.pitch - dy * perPixel));
        // remembered as a speed, so letting go leaves it turning
        o.vYaw = (dx * perPixel) / dt;
        o.vPitch = (-dy * perPixel) / dt;
      } else {
        const perUnit = (2 * o.dist * Math.tan(31 * (Math.PI / 180))) / Math.max(1, size.current.h);
        o.panX -= dx * perUnit;
        o.panY += dy * perUnit;
      }
      invalidate();
      return;
    }
    const d = drag.current;
    const cam = camera.current;
    if (!d) {
      // nothing held: only the hover changes
      const h = hit(e.clientX, e.clientY);
      const next = h && h.kind !== "link" ? { id: h.id, badge: h.kind === "badge" } : null;
      const prev = hover.current;
      if ((prev?.id ?? null) !== (next?.id ?? null) || (prev?.badge ?? false) !== (next?.badge ?? false)) {
        hover.current = next;
        invalidate();
      }
      if (stageRef.current) stageRef.current.style.cursor = h ? "pointer" : "grab";
      return;
    }
    if (d.kind === "node") {
      if (Math.abs(e.clientX - d.sx) + Math.abs(e.clientY - d.sy) > 3) d.moved = true;
      const n = sim.current.nodes.get(d.id);
      if (!n || !d.moved) return;
      const { origin, dir } = rayAt(e.clientX, e.clientY);
      const at = rayPlane(origin, dir, [n.x, n.y, n.z], d.normal);
      if (!at) return;
      const p = add(at, d.offset);
      // held where the pointer is; the rest of the graph keeps moving around it while it is held
      n.fx = p[0];
      n.fy = p[1];
      n.fz = p[2];
      sim.current.alphaTarget = 0.3;
      if (sim.current.alpha < 0.3) sim.current.alpha = 0.3;
      invalidate();
      return;
    }
    const now = performance.now();
    const dt = Math.max(1 / 240, (now - d.t) / 1000);
    const dx = e.clientX - d.x;
    const dy = e.clientY - d.y;
    d.x = e.clientX;
    d.y = e.clientY;
    d.t = now;
    const perPixel = (Math.PI * 1.4) / size.current.h;
    if (d.kind === "orbit") cam.orbit(dx * perPixel, -dy * perPixel, dt);
    else if (d.kind === "look") cam.look(dx * perPixel * 0.7, -dy * perPixel * 0.7, dt);
    else {
      const u = cam.unitsPerPixel(size.current.h);
      cam.pan(-dx * u, dy * u, dt);
    }
    invalidate();
  }

  function onPointerUp() {
    const d = drag.current;
    drag.current = null;
    const cam = camera.current;
    if (!d) return;
    if (funOn.current) {
      invalidate();
      return;
    }
    if (d.kind !== "node") {
      cam.release(d.kind);
      invalidate();
      return;
    }
    sim.current.alphaTarget = 0;
    const n = sim.current.nodes.get(d.id);
    if (d.moved) {
      // let go where it was put: a dragged node stays pinned there, or the springs would carry it
      // straight back to where it came from. The shake button releases every pinned node.
      if (n) n.vx = n.vy = n.vz = 0;
      invalidate();
      return;
    }
    // a click on the node itself selects it; unfolding is the badge's job, so reading a type in the
    // editor does not rearrange the graph
    const node = scene.current.nodes.find((x) => x.id === d.id);
    if (!node) return;
    if (node.kind === "type") ctx.select({ kind: "type", id: node.id });
    else ctx.select({ kind: "property", id: node.property.Id, typeId: node.ownerId });
  }

  function selectLink(l: GraphLink) {
    if (l.kind === "relation") ctx.select({ kind: "relation", id: l.relationId });
    else if (l.kind === "reference" || l.kind === "embeds") ctx.select({ kind: "property", id: l.propertyId, typeId: l.from });
  }

  function onWheel(e: WheelEvent) {
    e.preventDefault();
    if (funOn.current) {
      // stopped, the wheel walks in and out; flying, it has nothing to do
      const o = flight.current.orbit;
      if (o.ready) {
        o.dist = Math.max(14, Math.min(4000, o.dist * (e.deltaY < 0 ? 1 / 1.12 : 1.12)));
        invalidate();
      }
      return;
    }
    const { dir } = rayAt(e.clientX, e.clientY);
    // a notch is a hundred units on most mice; a trackpad sends many small ones that add up the same
    const amount = Math.max(-1, Math.min(1, e.deltaY / 100)) * -1.2;
    camera.current.dolly(amount, dir);
    invalidate();
  }

  /**
   * The right button on a type opens its menu, put where the click was. On the empty space between
   * types there is nothing to act on, so the browser's own menu is suppressed and nothing opens.
   */
  function onContextMenu(e: React.MouseEvent) {
    e.preventDefault();
    if (funOn.current) return;
    const h = hit(e.clientX, e.clientY);
    const id = h && (h.kind === "node" || h.kind === "badge") ? h.id : null;
    const node = id === null ? null : scene.current.nodes.find((n) => n.id === id);
    if (!node || node.kind !== "type") {
      setMenu(null);
      return;
    }
    // a drag started by the press is not wanted once the menu is up
    drag.current = null;
    camera.current.release("orbit");
    camera.current.release("pan");
    camera.current.release("look");
    setMenu({ x: e.clientX, y: e.clientY, id: node.id });
  }

  function onDoubleClick(e: React.MouseEvent) {
    if (funOn.current) return;
    const h = hit(e.clientX, e.clientY);
    if (h?.kind === "node") flyTo(h.id);
    else if (!h) fit();
  }

  function onKeyDown(e: React.KeyboardEvent) {
    if (e.target !== e.currentTarget) return;
    // The way in and the way back out of the aeroplane. Nothing on screen says so - it is meant to
    // be found rather than offered, the way the glider hides its eagle behind SHIFT+E.
    if (e.code === "KeyP" && e.shiftKey) {
      e.preventDefault();
      if (!e.repeat) {
        setFun((v) => !v);
        stageRef.current?.focus({ preventScroll: true });
      }
      return;
    }
    if (funOn.current) {
      const f = flight.current;
      if (funKeys.has(e.code)) {
        e.preventDefault();
        keys.current.add(e.code);
        invalidate();
        return;
      }
      if (e.code === "Space") {
        // space holds the aeroplane still in mid air rather than freezing the picture
        e.preventDefault();
        if (!e.repeat) {
          f.paused = !f.paused;
          setPaused(f.paused);
          invalidate();
        }
        return;
      }
      if (e.repeat) return;
      switch (e.code) {
        case "KeyC":
          f.cockpit = !f.cockpit;
          return;
        case "KeyP":
          f.paused = !f.paused;
          setPaused(f.paused);
          invalidate();
          return;
        case "KeyR":
          launch();
          invalidate();
          return;
        case "KeyM":
          setEngineOn((v) => !v);
          return;
        case "KeyI":
          f.invertPitch = !f.invertPitch;
          rememberInvert();
          return;
        case "KeyO":
          f.invertRoll = !f.invertRoll;
          rememberInvert();
          return;
        case "Escape":
          setFun(false);
          return;
      }
    }
    if (flyKeys.has(e.code)) {
      e.preventDefault();
      keys.current.add(e.code);
      invalidate();
      return;
    }
    // a keypress is a gesture the browser accepts fullscreen from
    if (e.code === "KeyF" && !e.ctrlKey && !e.metaKey && !e.altKey) {
      e.preventDefault();
      toggleFullscreen();
    }
  }
  function onKeyUp(e: React.KeyboardEvent) {
    keys.current.delete(e.code);
  }

  // ---- the whole screen ----

  /**
   * The shell owns the screen; the canvases follow by themselves, since the stage's ResizeObserver
   * measures the new size. The keys go back to the stage afterwards, so flying carries on at once.
   */
  function toggleFullscreen() {
    void shell.toggleFullscreen().then(() => stageRef.current?.focus({ preventScroll: true }));
  }

  // ---- the file ----

  function exportPng() {
    const gl = glRef.current;
    const o = overlayRef.current;
    if (!gl || !o) return;
    draw();
    const out = document.createElement("canvas");
    out.width = gl.width;
    out.height = gl.height;
    const c = out.getContext("2d")!;
    c.drawImage(gl, 0, 0);
    c.drawImage(o, 0, 0);
    out.toBlob((blob) => {
      if (!blob) return;
      const a = document.createElement("a");
      a.href = URL.createObjectURL(blob);
      a.download = "datamodel-graph-3d.png";
      a.click();
      setTimeout(() => URL.revokeObjectURL(a.href), 1000);
    }, "image/png");
  }

  // ---- drawing ----

  /** Everything that stands behind the graph in fun mode: the sky, the range, the aeroplane. */
  function drawScenery(viewProj: ReturnType<typeof multiply>, eye: Vec3, pal: ScenePalette) {
    const s = scenery.current;
    const th = theme.current;
    if (!s || !th) return;
    const a = ac.current;
    const t = performance.now() / 1000;
    s.sky(viewProj, eye, sunDir, pal, t);
    s.terrain(viewProj, eye, sunDir, pal, [(a.p[0] * unitsPerMetre) / terrainScale, (a.p[2] * unitsPerMetre) / terrainScale], terrainScale, terrainY);
    // from inside the cockpit there is nothing to draw but the country
    if (!flight.current.cockpit) {
      const m = bodyMatrix(planeMat.current, a, a.p[0] * unitsPerMetre, a.p[1] * unitsPerMetre, a.p[2] * unitsPerMetre, unitsPerMetre);
      s.plane(viewProj, eye, sunDir, pal, m, flight.current.prop, th.accentRgb);
    }
    // specks in the air, last of the scenery so they blend over the range rather than under it
    s.dust(viewProj, eye, pal, th.dark, t);
  }

  function draw() {
    const r = renderer.current;
    const stage = stageRef.current;
    const o = overlayRef.current;
    const th = theme.current;
    if (!r || !stage || !o || !th) return;
    const { w, h, dpr } = size.current;
    const cam = camera.current;
    const sc = scene.current;
    const positions = sim.current.nodes;
    const aspect = w / h;
    const flying = funOn.current && scenery.current !== null;
    const f = flight.current;

    // In fun mode the aeroplane's chase camera takes over: a wider lens, a far plane out past the
    // range, and a fog that reaches much further, or the graph would vanish a wingspan ahead.
    const fov = flying ? (62 * Math.PI) / 180 : cam.fov;
    // A near plane of 2 against a far of 90000 spends almost all of the depth buffer on the first
    // few metres and leaves neighbouring hillsides fighting over the same value, which shimmers.
    // The chase camera never sits closer than a wingspan, and the haze hides anything past the
    // mesh, so both planes can be pulled in a long way.
    const near = flying ? 6 : cam.near;
    const far = flying ? 20000 : cam.far;
    let eye: Vec3;
    let viewM: ReturnType<typeof viewMatrix>;
    if (flying) {
      eye = f.eye;
      const fwd = normalize(sub(f.at, f.eye));
      // the up given to the view has to be square to the heading, or the picture shears
      const right = normalize(cross(fwd, f.up));
      viewM = viewMatrix(eye, fwd, cross(right, fwd));
    } else {
      eye = cam.pos;
      viewM = cam.viewMatrix();
    }
    const projM = perspective(fov, aspect, near, far);
    const viewProj = multiply(projM, viewM);
    // Flying, the air is thick on purpose: the range runs for kilometres while the graph is a few
    // hundred units across, and without it every ridge to the horizon reads at the same strength.
    const fogNear = flying ? 320 : cam.dist * 1.3;
    const fogFar = flying ? 2100 : cam.dist * 4.5 + 500;
    const fog = (depth: number) => smoothstep(fogNear, fogFar, depth);
    const focal = h / 2 / Math.tan(fov / 2);

    // where everything lands on the screen this frame
    const proj = projected.current;
    proj.clear();
    for (const n of positions.values()) {
      const [cx, cy, , cw] = transform(viewProj, [n.x, n.y, n.z]);
      const front = cw > near;
      proj.set(n.id, { x: (cx / cw + 1) * 0.5 * w, y: (1 - cy / cw) * 0.5 * h, r: front ? (n.r * focal) / cw : 0, depth: cw, front });
    }

    const selectedType = sc.selection?.kind === "type" ? sc.selection.id : sc.selection?.kind === "property" ? sc.selection.typeId : null;
    const selectedProperty = sc.selection?.kind === "property" ? sc.selection.id : null;
    const selectedRelation = sc.selection?.kind === "relation" ? sc.selection.id : null;
    const hov = hover.current;
    const matches = (n: GraphNode) => !sc.q || (n.kind === "type" ? n.type.CodeName.toLowerCase().includes(sc.q) : n.property.CodeName.toLowerCase().includes(sc.q));
    // the axes a self-loop and a label are laid out in: the live camera's, not the parked one's
    const camFwd = flying ? normalize(sub(f.at, f.eye)) : cam.forward();
    const right = normalize(cross(camFwd, flying ? f.up : [0, 1, 0]));
    const up = cross(right, camFwd);
    // built either way: the aeroplane flies through it, and the graph on its own takes the motes from it
    const pal = scenePalette(th.panelRgb, th.bgRgb, th.textRgb, th.dark);

    r.begin({
      view: viewM,
      proj: projM,
      eye,
      width: Math.round(w * dpr),
      height: Math.round(h * dpr),
      fog: pal.fog,
      fogNear,
      fogFar,
      near,
      far,
      // a dark shadow side reads as depth on a dark page and as dirt on a light one
      ambient: th.dark ? 0.42 : 0.7,
      background: flying ? () => drawScenery(viewProj, eye, pal) : undefined,
    });

    // the lines, from border to border, with their arrowheads
    const labels: { x: number; y: number; text: string; color: string; bold: boolean; alpha: number; depth: number }[] = [];
    for (const l of sc.links) {
      const a = positions.get(l.from);
      const b = positions.get(l.to);
      if (!a || !b) continue;
      const highlighted =
        (hov !== null && (l.from === hov.id || l.to === hov.id)) ||
        (selectedType !== null && (l.from === selectedType || l.to === selectedType) && l.kind !== "property") ||
        (l.kind === "relation" && selectedRelation === l.relationId) ||
        (l.kind === "property" && selectedProperty === l.propertyId);
      const style = lineStyle(l, th, highlighted);
      const width = style.width * dpr;
      const pa: Vec3 = [a.x, a.y, a.z];
      const pb: Vec3 = [b.x, b.y, b.z];
      if (a === b) {
        // a relation from a type to itself: a small loop beside it, in the plane facing the camera
        const loopR = 18;
        const c = add(pa, scale(right, a.r + loopR + 2));
        const segments = 20;
        let prev: Vec3 | null = null;
        for (let i = 0; i <= segments; i++) {
          const t = (i / segments) * Math.PI * 2;
          const p = add(c, add(scale(right, Math.cos(t) * loopR), scale(up, Math.sin(t) * loopR)));
          if (prev) r.line(prev, p, style.color, width, style.dash, style.alpha);
          prev = p;
        }
        if ("label" in l) {
          const top = add(c, scale(up, loopR + 6));
          const [tx, ty, , tw] = transform(viewProj, top);
          const pr = proj.get(a.id);
          if (tw > near && pr && pr.r > 10) labels.push({ x: ((tx / tw + 1) * 0.5) * w, y: ((1 - ty / tw) * 0.5) * h, text: l.label, color: style.labelColor, bold: style.bold, alpha: 1 - fog(tw) * 0.85, depth: tw });
        }
        continue;
      }
      const dir = normalize(sub(pb, pa));
      const tail = l.kind === "property" ? 1 : 3;
      const p1 = add(pa, scale(dir, a.r + 2));
      const p2 = sub(pb, scale(dir, b.r + tail));
      if (distance(p1, p2) < 1) continue;
      const head = style.head;
      const lineEnd = head > 0 ? sub(p2, scale(dir, head * 0.85)) : p2;
      r.line(p1, lineEnd, style.color, width, style.dash, style.alpha);
      // A body is opaque, whatever the line it belongs to is drawn at: an arrowhead or an end dot
      // with the light coming through it is a body the eye refuses to read as one, and these are
      // the only solid things in the picture besides the balls. The lines themselves keep their
      // alpha - they are drawn as ribbons in the see-through pass and want to be slightly soft.
      if (head > 0) r.cone(p2, dir, head, style.headColor, 1);
      if (l.kind === "relation" && !l.directed) {
        r.sphere(p1, 3, style.color, 1);
        r.sphere(p2, 3, style.color, 1);
      }
      if (l.kind === "embeds") r.sphere(add(p1, scale(dir, 3)), 3.5, style.color, 1);
      if ("label" in l) {
        const sa = proj.get(a.id);
        const sb = proj.get(b.id);
        if (sa && sb && sa.front && sb.front && Math.hypot(sb.x - sa.x, sb.y - sa.y) > 70) {
          const mid = scale(add(p1, p2), 0.5);
          const [mx, my, , mw] = transform(viewProj, mid);
          if (mw > near) labels.push({ x: ((mx / mw + 1) * 0.5) * w, y: ((1 - my / mw) * 0.5) * h - 5, text: l.label, color: style.labelColor, bold: style.bold, alpha: 1 - fog(mw) * 0.85, depth: mw });
        }
      }
    }

    // the nodes
    for (const n of sc.nodes) {
      const p = positions.get(n.id);
      if (!p) continue;
      const c: Vec3 = [p.x, p.y, p.z];
      const dim = !matches(n);
      let color: RGB;
      if (n.kind === "property") color = parseColor(propertyColor(n.property.PropertyType));
      else color = parseColor(sc.ctx.colors.get(n.type.DatamodelSourceId) ?? "#8a8781");
      if (dim) color = mix(color, th.panelRgb, 0.75);
      const selected = n.kind === "type" ? selectedType === n.id : selectedProperty === n.property.Id;
      const hovered = hov !== null && hov.id === n.id && !hov.badge;
      const rim: RGB = selected ? th.accentRgb : th.textRgb;
      const rimStrength = selected ? 0.9 : hovered ? 0.55 : 0;
      // A type is a dodecahedron and a property a little cube: the two kinds of node are told apart
      // by shape before colour or size say anything, and a solid turned off the axes shows several
      // faces at several brightnesses, which is what makes either read as an object rather than a
      // circle. Twelve faces is nearly a ball at a distance and clearly a made thing up close, and
      // its corners sit exactly where the sphere's surface was, so nothing grew and a click still
      // lands where it did. The cube's side is a shade under the ball's diameter for the same reason.
      if (n.kind === "property") r.box(c, n.r * 1.45, color, 1, rim, rimStrength, spinOf(n.id));
      else r.dodeca(c, n.r, color, 1, rim, rimStrength, spinOf(n.id));
      // the ring round the start type, and round every unfolded type in its own colour
      if (n.kind === "type" && (n.root || n.open)) r.halo(c, n.r + 5, n.root ? th.textFaintRgb : color, n.root ? 0.1 : 0.12);
    }
    r.end();

    // A few motes in the air round the graph, over everything the renderer just drew. Not the
    // aeroplane's dust - a fraction of it, at a fraction of its brightness: enough that turning the
    // camera shows something moving between it and the nodes, which is what tells the eye the space
    // is a space and not a picture of one. Fun mode draws its own, thicker, behind the graph instead.
    if (!flying && scenery.current) scenery.current.dust(viewProj, eye, pal, th.dark, performance.now() / 1000, 0.35);

    // ---- the writing over it ----

    const g = o.getContext("2d")!;
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, h);
    g.textBaseline = "middle";
    const order = sc.nodes
      .map((n) => ({ n, p: proj.get(n.id) }))
      .filter((x): x is { n: GraphNode; p: Projected } => !!x.p && x.p.front && x.p.x > -200 && x.p.x < w + 200 && x.p.y > -100 && x.p.y < h + 100)
      .sort((a, b) => b.p.depth - a.p.depth);
    // with the names switched off only the icons and the badges are written: the graph reads as shape
    if (sc.names) {
      for (const lb of labels.sort((a, b) => b.depth - a.depth)) {
        if (lb.alpha < 0.05) continue;
        writeText(g, lb.text, lb.x, lb.y, (lb.bold ? "600 " : "") + "10.5px system-ui, sans-serif", lb.color, th.panel, "center", lb.alpha);
      }
    }
    for (const { n, p } of order) {
      const dim = !matches(n);
      const alpha = (1 - fog(p.depth) * 0.85) * (dim ? 0.3 : 1);
      if (alpha < 0.04) continue;
      if (n.kind === "property") {
        if (sc.names && p.r >= 2.5) {
          const selected = selectedProperty === n.property.Id;
          const hovered = hov !== null && hov.id === n.id;
          writeText(g, n.property.CodeName, p.x + p.r + 4, p.y, (selected || hovered ? "600 " : "") + "10.5px system-ui, sans-serif", selected || hovered ? th.accent : th.textSoft, th.panel, "left", alpha);
        }
        continue;
      }
      const covered = occluded(n.id, p);
      if (p.r >= 7 && !covered) {
        const img = icons.current.get(n.type.ModelType) ?? icons.current.get("Class");
        if (img && img.complete && img.naturalWidth > 0) {
          const s = p.r * 1.05;
          g.globalAlpha = alpha;
          g.drawImage(img, p.x - s / 2, p.y - s / 2, s, s);
          g.globalAlpha = 1;
        }
      }
      if (sc.names && p.r >= 5) {
        writeText(g, n.type.CodeName, p.x, p.y + p.r + 12, "600 12px system-ui, sans-serif", th.text, th.panel, "center", alpha);
        if (n.root) writeText(g, n.id === baseId ? "base type" : "start type", p.x, p.y + p.r + 25, "10px system-ui, sans-serif", th.textMuted, th.panel, "center", alpha);
      }
      // the switch on the node's shoulder: plus and what it would unfold, minus once it is open.
      // Nothing to press while flying, so it is not drawn then.
      if ((n.hidden > 0 || n.open) && p.r >= 6 && !covered && !flying) {
        const bx = p.x + p.r * 0.74;
        const by = p.y - p.r * 0.74;
        const hot = hov !== null && hov.id === n.id && hov.badge;
        g.globalAlpha = alpha;
        g.beginPath();
        g.arc(bx, by, badgeR, 0, Math.PI * 2);
        g.fillStyle = hot ? th.accent : n.open ? th.textFaint : th.textSoft;
        g.fill();
        g.lineWidth = 1.5;
        g.strokeStyle = th.panel;
        g.stroke();
        g.fillStyle = th.panel;
        g.textAlign = "center";
        g.font = n.open ? "600 15px system-ui, sans-serif" : "700 10px system-ui, sans-serif";
        g.fillText(n.open ? "−" : "+" + (n.hidden > 99 ? "99" : n.hidden), bx, by + (n.open ? -0.5 : 0.5));
        g.globalAlpha = 1;
      }
    }
    if (flying) drawHud(g, w, h, th);
    if (stage.style.cursor !== "grabbing" && drag.current && drag.current.kind !== "node") stage.style.cursor = "grabbing";
    else if (!drag.current && stage.style.cursor === "grabbing") stage.style.cursor = "grab";
  }

  /**
   * The instruments: what the aeroplane is doing, drawn straight onto the overlay. Airspeed and
   * height either side, a horizon that stays level while the aeroplane rolls against it, the
   * throttle along the bottom, and the name of whatever was last flown through.
   */
  function drawHud(g: CanvasRenderingContext2D, w: number, h: number, th: Theme) {
    const a = ac.current;
    const c = ctl.current;
    const f = flight.current;
    const ink = th.text;
    const dim = th.textMuted;
    const cx = w / 2;

    // --- the numbers ---
    const box = (x: number, label: string, value: string, align: CanvasTextAlign) => {
      writeText(g, value, x, h - 56, "600 22px system-ui, sans-serif", ink, th.panel, align, 0.95);
      writeText(g, label, x, h - 36, "10px system-ui, sans-serif", dim, th.panel, align, 0.8);
    };
    // what is on the clock is what the aeroplane is doing on screen: as time eases to a stop, so
    // does the needle, rather than sitting at cruise while the aeroplane hangs motionless
    const shownV = a.V * f.timeScale;
    box(26, a.V < V_STALL && f.timeScale > 0.5 ? "km/h · below stalling speed" : "km/h", Math.round(shownV * 3.6).toString(), "left");
    box(w - 26, "metres above the ground", Math.round(Math.max(0, a.agl)).toString(), "right");

    // --- the throttle ---
    const tw = Math.min(230, w * 0.3);
    const tx = cx - tw / 2;
    g.globalAlpha = 0.45;
    g.fillStyle = dim;
    g.fillRect(tx, h - 26, tw, 3);
    g.globalAlpha = 1;
    g.fillStyle = th.accent;
    g.fillRect(tx, h - 26, tw * c.throttle, 3);
    writeText(g, "throttle " + Math.round(c.throttle * 100) + "%", cx, h - 38, "10px system-ui, sans-serif", dim, th.panel, "center", 0.85);

    // --- what it is doing wrong, and what it has just flown through ---
    let warn: string | null = null;
    if (a.crashed) warn = "wrecked — back in the air in a moment";
    else if (f.orbit.ready) warn = "stopped — drag to look round it, wheel to come closer, space to fly on";
    else if (f.paused) warn = "slowing to a stop";
    else if (a.stall > 0.35) warn = "stalled — stick forward";
    else if (a.V > AC.vne) warn = "too fast";
    if (warn) writeText(g, warn, cx, 36, "600 14px system-ui, sans-serif", a.crashed || a.stall > 0.35 ? th.accent : ink, th.panel, "center", 0.95);
    if (f.buzz && f.buzzT > 0) {
      const alpha = Math.min(1, f.buzzT / 0.6);
      const total = scene.current.nodes.filter((n) => n.kind === "type").length;
      writeText(g, f.buzz.label, cx, h - 98, "600 17px system-ui, sans-serif", ink, th.panel, "center", alpha);
      writeText(g, f.buzz.kind + " · " + f.visited.size + " of " + total + " flown through", cx, h - 80, "10.5px system-ui, sans-serif", dim, th.panel, "center", alpha * 0.9);
    }
    writeText(
      g,
      "arrows or W A S D fly · N centres the stick · Z X rudder · shift and ctrl throttle · M sound · space stops · C view · R restart · Esc lands",
      cx,
      16,
      "10.5px system-ui, sans-serif",
      dim,
      th.panel,
      "center",
      0.7,
    );
  }

  // ---- what is on the page ----

  const modeSwitch = <GraphModeSwitch mode={shell.mode} onMode={shell.setMode} />;

  if (rootId === null) return <TypePicker ctx={ctx} eligible={world.eligible} query={q} onPick={start} tools={modeSwitch} />;

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
        <button className="icon-button" title="Fly closer" onClick={() => zoomBy(1.4)}>
          <IconZoomIn size={16} stroke={1.9} />
        </button>
        <button className="icon-button" title="Fly back" onClick={() => zoomBy(1 / 1.4)}>
          <IconZoomOut size={16} stroke={1.9} />
        </button>
        <button className="icon-button" title="Fit everything into view" onClick={fit}>
          <IconArrowsMaximize size={16} stroke={1.9} />
        </button>
        <button className="icon-button" title="Stir the layout and let it settle again" onClick={shake}>
          <IconArrowsShuffle size={16} stroke={1.9} />
        </button>
        <button className={"icon-button" + (autoOrbit ? " active" : "")} aria-pressed={autoOrbit} title={autoOrbit ? "Stop the slow turn" : "Turn slowly round the graph"} onClick={() => setAutoOrbit((v) => !v)} disabled={fun}>
          <IconRotate360 size={16} stroke={1.9} />
        </button>
        {/* there is no button for the aeroplane: SHIFT+P over the graph is the way in, and Esc the
            way out. Only the pause button appears, and only once it is up. */}
        {fun && (
          <button
            className={"icon-button" + (paused ? " active" : "")}
            aria-pressed={paused}
            title={paused ? "Fly on (space)" : "Stop in mid air and look round it (space)"}
            onClick={() => {
              flight.current.paused = !flight.current.paused;
              setPaused(flight.current.paused);
              stageRef.current?.focus({ preventScroll: true });
              invalidate();
            }}
          >
            {paused ? <IconPlayerPlay size={16} stroke={1.9} /> : <IconPlayerPause size={16} stroke={1.9} />}
          </button>
        )}
        {fun && (
          <button
            className={"icon-button" + (engineOn ? " active" : "")}
            aria-pressed={engineOn}
            title={engineOn ? "Silence the engine (M)" : "Let the engine be heard (M)"}
            onClick={() => {
              setEngineOn((v) => !v);
              stageRef.current?.focus({ preventScroll: true });
            }}
          >
            {engineOn ? <IconVolume size={16} stroke={1.9} /> : <IconVolumeOff size={16} stroke={1.9} />}
          </button>
        )}
        <span className="dm-tools-gap" />
        <button className="icon-button" title="Unfold every type" onClick={expandAll} disabled={expanded.size >= world.eligible.size}>
          <IconHierarchy3 size={16} stroke={1.9} />
        </button>
        <button className="icon-button" title="Fold everything back to the start type" onClick={collapseAll} disabled={expanded.size === 0}>
          <IconFocusCentered size={16} stroke={1.9} />
        </button>
        <span className="dm-tools-gap" />
        <NamesButton on={shell.names} onToggle={shell.toggleNames} />
        <button className="icon-button" title="Save what is on screen as a PNG image" onClick={exportPng}>
          <IconFileTypePng size={16} stroke={1.9} />
        </button>
        <FullscreenButton on={shell.fullscreen} onToggle={toggleFullscreen} />
        {/* the legend is the switchboard: each kind of line can be turned off, and with it the types it leads to */}
        <span className="dg-edge-toggles" role="group" aria-label="Kinds of line to show">
          {edgeKinds.map((e) => (
            <button key={e.kind} className={"dg-edge-toggle" + (edges.has(e.kind) ? " on" : "")} aria-pressed={edges.has(e.kind)} title={e.hint + (edges.has(e.kind) ? " — click to hide" : " — click to show")} onClick={() => toggleEdge(e.kind)}>
              <span className={"dm-legend-line " + e.kind} /> {e.label}
            </button>
          ))}
        </span>
        {/* the flying hint is four words; the one that spells out the camera needs far more room before it is worth showing */}
        <span className={"muted dm-diagram-legend dg-hint" + (fun ? "" : " dg-hint-long")}>
          {fun ? "flying — Esc lands" : "left drag orbits · right drag pans · middle drag looks · wheel flies · W A S D Q E · double-click a type to fly to it"}
        </span>
      </div>
      {glOk ? (
        <div
          ref={stageRef}
          className="dg3-stage"
          tabIndex={0}
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onPointerCancel={onPointerUp}
          onDoubleClick={onDoubleClick}
          onContextMenu={onContextMenu}
          onKeyDown={onKeyDown}
          onKeyUp={onKeyUp}
          onBlur={() => keys.current.clear()}
        >
          <canvas ref={glRef} className="dg3-gl" />
          <canvas ref={overlayRef} className="dg3-overlay" />
          {/* the kind icons, rendered once so the canvas can draw them; never seen here */}
          <span ref={iconHost} className="dg3-icon-host" aria-hidden>
            {Object.entries(kindMeta).map(([kind, meta]) => (
              <meta.icon key={kind} data-kind={kind} size={64} stroke={1.7} color="#ffffff" />
            ))}
          </span>
        </div>
      ) : (
        <div className="dg3-nogl muted">This browser does not offer WebGL 2, which the 3D view needs. The Graph view shows the same picture flat.</div>
      )}
      {menu &&
        (() => {
          const node = nodes.find((n) => n.id === menu.id);
          if (!node || node.kind !== "type") return null;
          return (
            <NodeMenu
              x={menu.x}
              y={menu.y}
              type={node.type}
              open={node.open}
              isRoot={node.root}
              hidden={node.hidden}
              count={ctx.typeCounts[node.id]}
              onClose={() => setMenu(null)}
              onStart={() => {
                start(node.id);
                setMenu(null);
              }}
              onSelect={() => {
                ctx.select({ kind: "type", id: node.id });
                setMenu(null);
              }}
              onToggle={() => {
                toggle(node.id);
                setMenu(null);
              }}
              onFlyTo={() => {
                flyTo(node.id);
                setMenu(null);
              }}
            />
          );
        })()}
    </>
  );
}

/**
 * What a right-click on a type offers: make it the start of the graph, read it in the editor, unfold
 * or fold it, or fly the camera to it. Put where the click was, the way the treemap's tile menu is.
 */
function NodeMenu({
  x,
  y,
  type,
  open,
  isRoot,
  hidden,
  count,
  onClose,
  onStart,
  onSelect,
  onToggle,
  onFlyTo,
}: {
  x: number;
  y: number;
  type: NodeTypeJson;
  open: boolean;
  isRoot: boolean;
  hidden: number;
  count: number | undefined;
  onClose: () => void;
  onStart: () => void;
  onSelect: () => void;
  onToggle: () => void;
  onFlyTo: () => void;
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);
  const width = 248;
  const height = 176;
  const left = Math.max(8, Math.min(x, window.innerWidth - width - 8));
  const top = Math.max(8, Math.min(y, window.innerHeight - height - 8));
  const kind = kindMeta[type.ModelType] ?? kindMeta.Class;
  return (
    <>
      <div
        className="db-menu-backdrop"
        onPointerDown={(e) => e.stopPropagation()}
        onClick={onClose}
        onContextMenu={(e) => {
          e.preventDefault();
          onClose();
        }}
      />
      <div className="db-menu type-menu" role="menu" style={{ top, left, width }} onPointerDown={(e) => e.stopPropagation()}>
        <div className="type-menu-head">
          <kind.icon size={14} stroke={1.9} color={kind.color} />
          <b title={fullName(type)}>{type.CodeName}</b>
          <span className="muted">{count === undefined ? kind.label : formatCount(count) + (count === 1 ? " node" : " nodes")}</span>
        </div>
        <button className="db-menu-item" role="menuitem" onClick={onStart} disabled={isRoot}>
          <IconCrosshair size={15} stroke={1.8} /> {isRoot ? "Already the start type" : "Start the graph here"}
        </button>
        <button className="db-menu-item" role="menuitem" onClick={onToggle} disabled={!open && hidden === 0}>
          <IconHierarchy3 size={15} stroke={1.8} /> {open ? "Fold what it unfolded" : hidden > 0 ? "Unfold " + hidden + " more" : "Nothing to unfold"}
        </button>
        <button className="db-menu-item" role="menuitem" onClick={onFlyTo}>
          <IconBinoculars size={15} stroke={1.8} /> Fly the camera to it
        </button>
        <button className="db-menu-item" role="menuitem" onClick={onSelect}>
          <IconPencil size={15} stroke={1.8} /> Open in the editor
        </button>
      </div>
    </>
  );
}

const flyKeys = new Set(["KeyW", "KeyA", "KeyS", "KeyD", "KeyQ", "KeyE", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "PageUp", "PageDown", "ShiftLeft", "ShiftRight"]);

// ---- how a line looks ----

interface LineStyle {
  color: RGB;
  width: number;
  dash: 0 | 1 | 2;
  alpha: number;
  /** the arrowhead's length in world units; 0 for none */
  head: number;
  headColor: RGB;
  labelColor: string;
  bold: boolean;
}

/** The lines mean what they mean in the diagram: dashed inheritance, relation red, dotted accent references, embedded purple, a thin grey stem to a property. */
function lineStyle(l: GraphLink, th: Theme, highlighted: boolean): LineStyle {
  const wide = highlighted ? 3 : 0;
  switch (l.kind) {
    case "inherits":
      return { color: th.textFaintRgb, width: wide || 1.4, dash: 1, alpha: 0.95, head: 12, headColor: th.textFaintRgb, labelColor: th.textMuted, bold: false };
    case "relation":
      return { color: relationRgb, width: wide || 1.7, dash: 0, alpha: 0.95, head: l.directed ? 9 : 0, headColor: relationRgb, labelColor: relationColor, bold: true };
    case "reference":
      return { color: th.accentRgb, width: wide || 1.5, dash: 2, alpha: 0.95, head: 9, headColor: th.accentRgb, labelColor: th.textMuted, bold: false };
    case "embeds":
      return { color: embeddedRgb, width: wide || 1.7, dash: 0, alpha: 0.95, head: 9, headColor: embeddedRgb, labelColor: embeddedColor, bold: true };
    case "property":
      return { color: highlighted ? th.textFaintRgb : th.borderRgb, width: highlighted ? 1.7 : 1.2, dash: 0, alpha: 0.9, head: 0, headColor: th.borderRgb, labelColor: th.textMuted, bold: false };
  }
}

/**
 * How a node's solid is turned: a base tilt that shows several faces, varied per node so a row of
 * them does not read as one crystal lattice. Derived from the id, so a solid keeps its own angle
 * across frames, layouts and reloads - a tilt that changed as the graph settled would be the one
 * thing on screen moving for no reason.
 */
function spinOf(id: string): [number, number] {
  let hash = 0;
  for (let i = 0; i < id.length; i++) hash = (hash * 31 + id.charCodeAt(i)) | 0;
  const spread = (bits: number) => ((bits >>> 0) % 1000) / 1000 - 0.5; // -0.5..0.5
  return [0.62 + spread(hash) * 0.9, 0.34 + spread(hash >> 10) * 0.5];
}

function writeText(g: CanvasRenderingContext2D, text: string, x: number, y: number, font: string, fill: string, halo: string, align: CanvasTextAlign, alpha: number) {
  g.globalAlpha = alpha;
  g.font = font;
  g.textAlign = align;
  g.lineWidth = 3;
  g.lineJoin = "round";
  g.strokeStyle = halo;
  g.strokeText(text, x, y);
  g.fillStyle = fill;
  g.fillText(text, x, y);
  g.globalAlpha = 1;
}

// ---- the physics, as in the flat graph, one axis more ----

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
    let dz = b.z + b.vz - a.z - a.vz;
    const len = Math.sqrt(dx * dx + dy * dy + dz * dz) || 1e-6;
    const f = ((len - l.length) / len) * alpha * l.strength;
    dx *= f;
    dy *= f;
    dz *= f;
    // the lighter end gives more: a leaf follows its type, a type barely feels a leaf
    const wa = a.kind === "property" ? 0.85 : b.kind === "property" ? 0.15 : 0.5;
    b.vx -= dx * (1 - wa);
    b.vy -= dy * (1 - wa);
    b.vz -= dz * (1 - wa);
    a.vx += dx * wa;
    a.vy += dy * wa;
    a.vz += dz * wa;
  }
  for (let i = 0; i < nodes.length; i++) {
    const a = nodes[i];
    for (let j = i + 1; j < nodes.length; j++) {
      const b = nodes[j];
      let dx = b.x - a.x;
      let dy = b.y - a.y;
      let dz = b.z - a.z;
      let d2 = dx * dx + dy * dy + dz * dz;
      if (d2 > 450 * 450) continue;
      if (d2 < 1e-6) {
        dx = Math.random() - 0.5;
        dy = Math.random() - 0.5;
        dz = Math.random() - 0.5;
        d2 = dx * dx + dy * dy + dz * dz;
      }
      if (d2 < 64) d2 = 64;
      // each feels the other's charge, as in d3: a leaf is pushed hard by a type, a type barely by a leaf
      const fa = (b.charge * alpha) / d2;
      const fb = (a.charge * alpha) / d2;
      a.vx += dx * fa;
      a.vy += dy * fa;
      a.vz += dz * fa;
      b.vx -= dx * fb;
      b.vy -= dy * fb;
      b.vz -= dz * fb;
    }
  }
  for (const n of nodes) {
    if (n.kind !== "type") continue;
    n.vx -= n.x * 0.018 * alpha;
    n.vy -= n.y * 0.018 * alpha;
    n.vz -= n.z * 0.018 * alpha;
  }
  for (let i = 0; i < nodes.length; i++) {
    const a = nodes[i];
    for (let j = i + 1; j < nodes.length; j++) {
      const b = nodes[j];
      const min = a.r + b.r + (a.kind === "type" && b.kind === "type" ? 14 : 5);
      let dx = b.x - a.x;
      let dy = b.y - a.y;
      let dz = b.z - a.z;
      let d = Math.sqrt(dx * dx + dy * dy + dz * dz);
      if (d >= min) continue;
      if (d < 1e-6) {
        dx = Math.random() - 0.5;
        dy = Math.random() - 0.5;
        dz = Math.random() - 0.5;
        d = Math.sqrt(dx * dx + dy * dy + dz * dz);
      }
      const push = ((min - d) / d) * 0.5;
      a.vx -= dx * push;
      a.vy -= dy * push;
      a.vz -= dz * push;
      b.vx += dx * push;
      b.vy += dy * push;
      b.vz += dz * push;
    }
  }
  for (const n of nodes) {
    if (n.fx !== null && n.fy !== null && n.fz !== null) {
      n.x = n.fx;
      n.y = n.fy;
      n.z = n.fz;
      n.vx = n.vy = n.vz = 0;
      continue;
    }
    n.vx *= velocityDecay;
    n.vy *= velocityDecay;
    n.vz *= velocityDecay;
    n.x += n.vx;
    n.y += n.vy;
    n.z += n.vz;
  }
}

function randomDirection(): Vec3 {
  const z = Math.random() * 2 - 1;
  const t = Math.random() * Math.PI * 2;
  const r = Math.sqrt(1 - z * z);
  return [r * Math.cos(t), r * Math.sin(t), z];
}

// ---- colours and geometry on the screen ----

function readTheme(el: HTMLElement): Theme {
  const cs = getComputedStyle(el);
  const v = (name: string, fallback: string) => cs.getPropertyValue(name).trim() || fallback;
  const panel = v("--panel", "#ffffff");
  const text = v("--text", "#1d1c1a");
  const textMuted = v("--text-muted", "#6f6c66");
  const textSoft = v("--text-soft", "#45433f");
  const textFaint = v("--text-faint", "#a6a39d");
  const border = v("--border", "#c6c1b9");
  const accent = v("--accent", "#0960b2");
  const bg = v("--bg", "#f5f4f2");
  const panelRgb = parseColor(panel);
  const textRgb = parseColor(text);
  return {
    panel,
    panelRgb,
    bgRgb: parseColor(bg),
    // the page decides light or dark; the scene only mirrors that answer
    dark: panelRgb[0] + panelRgb[1] + panelRgb[2] < textRgb[0] + textRgb[1] + textRgb[2],
    text,
    textRgb: parseColor(text),
    textMuted,
    textSoft,
    textSoftRgb: parseColor(textSoft),
    textFaint,
    textFaintRgb: parseColor(textFaint),
    borderRgb: parseColor(border),
    accent,
    accentRgb: parseColor(accent),
  };
}

/** A CSS colour as 0..1 floats: #rgb, #rrggbb or rgb()/rgba(); anything else comes out mid grey. */
function parseColor(css: string): RGB {
  const s = css.trim();
  if (s.startsWith("#")) {
    const hex = s.slice(1);
    if (hex.length === 3 || hex.length === 4) return [parseInt(hex[0] + hex[0], 16) / 255, parseInt(hex[1] + hex[1], 16) / 255, parseInt(hex[2] + hex[2], 16) / 255];
    if (hex.length >= 6) return [parseInt(hex.slice(0, 2), 16) / 255, parseInt(hex.slice(2, 4), 16) / 255, parseInt(hex.slice(4, 6), 16) / 255];
  }
  const m = /rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)/.exec(s);
  if (m) return [Number(m[1]) / 255, Number(m[2]) / 255, Number(m[3]) / 255];
  return [0.54, 0.53, 0.5];
}

function mix(a: RGB, b: RGB, t: number): RGB {
  return [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];
}

function smoothstep(e0: number, e1: number, x: number) {
  const t = Math.max(0, Math.min(1, (x - e0) / (e1 - e0)));
  return t * t * (3 - 2 * t);
}

function pointToSegment(px: number, py: number, ax: number, ay: number, bx: number, by: number) {
  const dx = bx - ax;
  const dy = by - ay;
  const l2 = dx * dx + dy * dy || 1e-6;
  const t = Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / l2));
  return Math.hypot(px - (ax + dx * t), py - (ay + dy * t));
}
