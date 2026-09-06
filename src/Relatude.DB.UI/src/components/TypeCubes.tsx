import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { TypeSlice } from "./TypeChart";
import { formatCount } from "../format";
import { FlyCamera } from "../graph3d/camera";
import { createRenderer, type Renderer, type RGB } from "../graph3d/renderer";
import { multiply, transform, type Vec3 } from "../graph3d/math";

/**
 * The database as a pile of solids, one per node type, where **the volume of a cube is the number of
 * nodes**. That is the whole point of drawing it in three dimensions rather than two: a treemap
 * spends area on a count, so a type with a thousand times more nodes is a thousand times the tile
 * and the small ones vanish; a cube spends volume, so the same thousandfold is only ten times the
 * edge and the long tail stays visible beside the giants without anything being fudged.
 *
 * The cubes are shelved largest first, sitting on a floor, and the camera orbits round the pile with
 * the same weight as the model graph's - drag turns it, the right button slides it, the wheel walks
 * in, and everything carries on a moment after the hand lets go.
 */
export function TypeCubes({ slices, total, onTileClick }: { slices: TypeSlice[]; total: number; onTileClick?: (slice: TypeSlice, at: { x: number; y: number }) => void }) {
  const hostRef = useRef<HTMLDivElement>(null);
  const glRef = useRef<HTMLCanvasElement>(null);
  const overlayRef = useRef<HTMLCanvasElement>(null);
  const renderer = useRef<Renderer | null>(null);
  const camera = useRef(
    (() => {
      const c = new FlyCamera();
      // the cubes stand on a floor: the camera may come down to eye level but not under it
      c.pitchLimit = [-1.45, -0.03];
      return c;
    })(),
  );
  const raf = useRef(0);
  const last = useRef(0);
  const size = useRef({ w: 1, h: 1, dpr: 1 });
  const theme = useRef<ReturnType<typeof readTheme> | null>(null);
  const drag = useRef<{ kind: "orbit" | "pan"; x: number; y: number; t: number; moved: boolean } | null>(null);
  const hover = useRef<string | null>(null);
  const placed = useRef<Placed[]>([]);
  const screen = useRef(new Map<string, { x: number; y: number; depth: number; front: boolean }>());
  const [glOk, setGlOk] = useState(true);
  const data = useRef({ slices, total, onTileClick });
  data.current = { slices, total, onTileClick };

  // ---- where every cube sits ----
  useLayoutEffect(() => {
    placed.current = layOut(slices);
    frameAll();
    invalidate();
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the layout follows the slices alone
  }, [slices]);

  // ---- the canvases ----
  useLayoutEffect(() => {
    const host = hostRef.current;
    const canvas = glRef.current;
    if (!host || !canvas) return;
    let r: Renderer | null = null;
    try {
      r = createRenderer(canvas);
    } catch {
      r = null;
    }
    if (!r) {
      setGlOk(false);
      return;
    }
    renderer.current = r;
    theme.current = readTheme(host);
    const measure = () => {
      const rect = host.getBoundingClientRect();
      const dpr = window.devicePixelRatio || 1;
      size.current = { w: Math.max(1, rect.width), h: Math.max(1, rect.height), dpr };
      r!.resize(Math.round(rect.width * dpr), Math.round(rect.height * dpr));
      const o = overlayRef.current;
      if (o) {
        o.width = Math.round(rect.width * dpr);
        o.height = Math.round(rect.height * dpr);
      }
      frameAll();
      invalidate();
    };
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(host);
    const mo = new MutationObserver(() => {
      theme.current = readTheme(host);
      invalidate();
    });
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    const wheel = (e: WheelEvent) => {
      e.preventDefault();
      const cam = camera.current;
      cam.animateTo(cam.poseLookingAt(cam.target(), Math.max(40, Math.min(6000, cam.dist * (e.deltaY < 0 ? 1 / 1.15 : 1.15)))), 200);
      invalidate();
    };
    host.addEventListener("wheel", wheel, { passive: false });
    return () => {
      ro.disconnect();
      mo.disconnect();
      host.removeEventListener("wheel", wheel);
      r!.destroy();
      renderer.current = null;
      cancelAnimationFrame(raf.current);
      raf.current = 0;
    };
  }, []);

  useEffect(
    () => () => {
      cancelAnimationFrame(raf.current);
      raf.current = 0;
    },
    [],
  );

  function invalidate() {
    if (raf.current) return;
    last.current = performance.now();
    raf.current = requestAnimationFrame(step);
  }

  function step(now: number) {
    raf.current = 0;
    if (!renderer.current) return;
    const dt = Math.min(0.05, Math.max(0.001, (now - last.current) / 1000));
    last.current = now;
    const busy = camera.current.step(dt, now) || drag.current !== null;
    draw();
    if (busy) raf.current = requestAnimationFrame(step);
  }

  /** Backs the camera off until the whole pile is in the frame, on the heading it already has. */
  function frameAll() {
    const cam = camera.current;
    const ps = placed.current;
    if (ps.length === 0) return;
    let lo: Vec3 = [Infinity, 0, Infinity];
    let hi: Vec3 = [-Infinity, 0, -Infinity];
    for (const p of ps) {
      const h = p.side / 2;
      lo = [Math.min(lo[0], p.x - h), 0, Math.min(lo[2], p.z - h)];
      hi = [Math.max(hi[0], p.x + h), Math.max(hi[1], p.side), Math.max(hi[2], p.z + h)];
    }
    const centre: Vec3 = [(lo[0] + hi[0]) / 2, hi[1] * 0.42, (lo[2] + hi[2]) / 2];
    // the sphere round the pile, then back off far enough for the tighter of the two half-angles
    const radius = Math.max(60, Math.hypot(hi[0] - lo[0], hi[1], hi[2] - lo[2]) * 0.5 + 20);
    const aspect = size.current.w / Math.max(1, size.current.h);
    const halfV = cam.fov / 2;
    const halfH = Math.atan(Math.tan(halfV) * aspect);
    cam.stop();
    cam.setPose(cam.poseLookingAt(centre, Math.max(120, (radius / Math.sin(Math.min(halfV, halfH))) * 1.08), -0.6, -0.42));
  }

  // ---- the pointer ----
  function onPointerDown(e: React.PointerEvent) {
    if (e.button !== 0 && e.button !== 2) return;
    const kind = e.button === 2 || e.shiftKey ? "pan" : "orbit";
    drag.current = { kind, x: e.clientX, y: e.clientY, t: performance.now(), moved: false };
    camera.current.hold(kind);
    try {
      (e.currentTarget as Element).setPointerCapture(e.pointerId);
    } catch {
      // a pointer the browser has forgotten; the drag still works over the canvas
    }
    invalidate();
  }
  function onPointerMove(e: React.PointerEvent) {
    const d = drag.current;
    if (!d) {
      const id = pick(e.clientX, e.clientY);
      if (id !== hover.current) {
        hover.current = id;
        if (hostRef.current) hostRef.current.style.cursor = id ? "pointer" : "grab";
        invalidate();
      }
      return;
    }
    const now = performance.now();
    const dt = Math.max(1 / 240, (now - d.t) / 1000);
    const dx = e.clientX - d.x;
    const dy = e.clientY - d.y;
    if (Math.abs(dx) + Math.abs(dy) > 2) d.moved = true;
    d.x = e.clientX;
    d.y = e.clientY;
    d.t = now;
    const cam = camera.current;
    const perPixel = (Math.PI * 1.4) / size.current.h;
    if (d.kind === "orbit") cam.orbit(dx * perPixel, -dy * perPixel, dt);
    else {
      const u = cam.unitsPerPixel(size.current.h);
      cam.pan(-dx * u, dy * u, dt);
    }
    invalidate();
  }
  function onPointerUp(e: React.PointerEvent) {
    const d = drag.current;
    drag.current = null;
    if (!d) return;
    camera.current.release(d.kind);
    invalidate();
    if (d.moved || d.kind !== "orbit") return;
    const id = pick(e.clientX, e.clientY);
    const hit = placed.current.find((p) => p.slice.type.id === id);
    if (hit && data.current.onTileClick) data.current.onTileClick(hit.slice, { x: e.clientX, y: e.clientY });
  }

  /** Which cube is under the pointer: the nearest whose box the ray enters. */
  function pick(clientX: number, clientY: number): string | null {
    const host = hostRef.current;
    const cam = camera.current;
    if (!host) return null;
    const rect = host.getBoundingClientRect();
    const ndcX = ((clientX - rect.left) / size.current.w) * 2 - 1;
    const ndcY = 1 - ((clientY - rect.top) / size.current.h) * 2;
    const dir = cam.ray(ndcX, ndcY, size.current.w / size.current.h);
    let best: { t: number; id: string } | null = null;
    for (const p of placed.current) {
      const h = p.side * 0.5;
      const t = raySlab(cam.pos, dir, [p.x - h, p.side * 0 - 0, p.z - h], [p.x + h, p.side, p.z + h]);
      if (t !== null && (best === null || t < best.t)) best = { t, id: p.slice.type.id };
    }
    return best ? best.id : null;
  }

  // ---- drawing ----
  function draw() {
    const r = renderer.current;
    const host = hostRef.current;
    const o = overlayRef.current;
    const th = theme.current;
    if (!r || !host || !o || !th) return;
    const { w, h, dpr } = size.current;
    const cam = camera.current;
    const viewM = cam.viewMatrix();
    const projM = cam.projMatrix(w / h);
    const viewProj = multiply(projM, viewM);
    const fogNear = cam.dist * 1.2;
    const fogFar = cam.dist * 4 + 400;

    r.begin({
      view: viewM,
      proj: projM,
      eye: cam.pos,
      width: Math.round(w * dpr),
      height: Math.round(h * dpr),
      fog: th.panel,
      fogNear,
      fogFar,
      near: cam.near,
      far: cam.far,
    });

    const ps = placed.current;
    // the floor they stand on: a thin slab under the whole pile, so the cubes read as sitting
    // somewhere rather than floating in the dark
    let minX = 0;
    let maxX = 0;
    let minZ = 0;
    let maxZ = 0;
    for (const p of ps) {
      minX = Math.min(minX, p.x - p.side);
      maxX = Math.max(maxX, p.x + p.side);
      minZ = Math.min(minZ, p.z - p.side);
      maxZ = Math.max(maxZ, p.z + p.side);
    }
    if (ps.length > 0) {
      const cx = (minX + maxX) / 2;
      const cz = (minZ + maxZ) / 2;
      r.box([cx, -2, cz], [maxX - minX + 30, 4, maxZ - minZ + 30], mix(th.panel, th.textRgb, 0.07), 1);
      // and a stem under each, so a small cube at the back is still findable
      for (const p of ps) r.line([p.x, 0, p.z], [p.x, Math.max(p.side, 10), p.z], mix(th.panel, th.textRgb, 0.16), 1.1 * dpr, 0, 0.45);
    }
    for (const p of ps) {
      const hot = hover.current === p.slice.type.id;
      r.box([p.x, p.side * 0.5, p.z], p.side, parseColor(p.slice.color), 1, hot ? th.accentRgb : th.textRgb, hot ? 0.85 : 0);
    }
    r.end();

    // ---- the writing ----
    const sc = screen.current;
    sc.clear();
    for (const p of ps) {
      const [cx, cy, , cw] = transform(viewProj, [p.x, p.side + 6, p.z]);
      sc.set(p.slice.type.id, { x: ((cx / cw + 1) * 0.5) * w, y: ((1 - cy / cw) * 0.5) * h, depth: cw, front: cw > cam.near });
    }
    const g = o.getContext("2d")!;
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, h);
    g.textBaseline = "middle";
    const order = [...ps].sort((a, b) => (sc.get(b.slice.type.id)?.depth ?? 0) - (sc.get(a.slice.type.id)?.depth ?? 0));
    for (const p of order) {
      const at = sc.get(p.slice.type.id);
      if (!at || !at.front) continue;
      const fade = 1 - smoothstep(fogNear, fogFar, at.depth) * 0.85;
      // a cube too small on screen to carry its name keeps its number and loses the name
      const px = (p.side / at.depth) * (h / 2 / Math.tan(cam.fov / 2));
      if (fade < 0.08 || px < 9) continue;
      const hot = hover.current === p.slice.type.id;
      write(g, p.slice.type.name, at.x, at.y, "600 12px system-ui, sans-serif", hot ? th.accentCss : th.text, th.panelCss, fade);
      if (px > 26) write(g, formatCount(p.slice.value) + (p.slice.value === 1 ? " node" : " nodes"), at.x, at.y + 14, "10.5px system-ui, sans-serif", th.muted, th.panelCss, fade * 0.9);
    }
    write(g, "the volume of a cube is the number of nodes · drag to turn · right drag to slide · wheel to come closer · double-click to fit", w / 2, h - 12, "10.5px system-ui, sans-serif", th.muted, th.panelCss, 0.75);
  }

  if (!glOk) return <div className="muted dash-chart-empty">This browser does not offer WebGL 2, which the cubes need. The treemap shows the same thing flat.</div>;

  return (
    <div ref={hostRef} className="tc-stage" onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerUp={onPointerUp} onPointerCancel={onPointerUp} onContextMenu={(e) => e.preventDefault()} onDoubleClick={frameAll}>
      <canvas ref={glRef} className="tc-gl" />
      <canvas ref={overlayRef} className="tc-overlay" />
    </div>
  );
}

// ---- the layout ----

interface Placed {
  slice: TypeSlice;
  x: number;
  z: number;
  side: number;
}

/**
 * Cube volume is node count, so the edge is the cube root of it - and the whole set is scaled so the
 * biggest is a comfortable size on screen. They are then shelved largest first into rows of about
 * the same total width, which keeps the pile roughly square from above however lopsided the counts.
 */
function layOut(slices: TypeSlice[]): Placed[] {
  const live = slices.filter((s) => s.value > 0);
  if (live.length === 0) return [];
  const sorted = [...live].sort((a, b) => b.value - a.value);
  const biggest = Math.cbrt(sorted[0].value);
  const scale = 90 / Math.max(1e-6, biggest);
  // no floor under the small ones beyond keeping them from being degenerate: a minimum size would
  // draw every type below a few dozen nodes at the same size, which is the distortion this chart
  // exists to avoid. A type with one node is a speck, and the stem under it is how it is found.
  const sides = sorted.map((s) => Math.max(0.8, Math.cbrt(s.value) * scale));
  const gap = 14;
  // a row width that comes out roughly as deep as it is wide
  const totalWidth = sides.reduce((a, b) => a + b + gap, 0);
  const target = Math.max(sides[0] * 1.2, Math.sqrt(totalWidth * (sides[0] + gap)) * 1.5);

  const rows: { items: { slice: TypeSlice; side: number }[]; width: number; depth: number }[] = [];
  let row: { items: { slice: TypeSlice; side: number }[]; width: number; depth: number } = { items: [], width: 0, depth: 0 };
  sorted.forEach((slice, i) => {
    const side = sides[i];
    if (row.items.length > 0 && row.width + side + gap > target) {
      rows.push(row);
      row = { items: [], width: 0, depth: 0 };
    }
    row.items.push({ slice, side });
    row.width += side + gap;
    row.depth = Math.max(row.depth, side);
  });
  if (row.items.length > 0) rows.push(row);

  const totalDepth = rows.reduce((a, r) => a + r.depth + gap, 0);
  const out: Placed[] = [];
  let z = -totalDepth / 2;
  for (const r of rows) {
    let x = -r.width / 2;
    for (const it of r.items) {
      out.push({ slice: it.slice, x: x + it.side / 2, z: z + r.depth / 2, side: it.side });
      x += it.side + gap;
    }
    z += r.depth + gap;
  }
  return out;
}

// ---- odds and ends ----

/** Where a ray enters an axis-aligned box, or null when it misses it. */
function raySlab(o: Vec3, d: Vec3, lo: Vec3, hi: Vec3): number | null {
  let tmin = -Infinity;
  let tmax = Infinity;
  for (let i = 0; i < 3; i++) {
    const inv = 1 / (d[i] || 1e-9);
    let t1 = (lo[i] - o[i]) * inv;
    let t2 = (hi[i] - o[i]) * inv;
    if (t1 > t2) [t1, t2] = [t2, t1];
    tmin = Math.max(tmin, t1);
    tmax = Math.min(tmax, t2);
    if (tmax < tmin) return null;
  }
  return tmax < 0 ? null : Math.max(tmin, 0);
}

function write(g: CanvasRenderingContext2D, text: string, x: number, y: number, font: string, fill: string, halo: string, alpha: number) {
  g.globalAlpha = alpha;
  g.font = font;
  g.textAlign = "center";
  g.lineWidth = 3;
  g.lineJoin = "round";
  g.strokeStyle = halo;
  g.strokeText(text, x, y);
  g.fillStyle = fill;
  g.fillText(text, x, y);
  g.globalAlpha = 1;
}

function readTheme(el: HTMLElement) {
  const cs = getComputedStyle(el);
  const v = (name: string, fallback: string) => cs.getPropertyValue(name).trim() || fallback;
  const panelCss = v("--panel", "#ffffff");
  const text = v("--text", "#1d1c1a");
  const accent = v("--accent", "#0960b2");
  return { panel: parseColor(panelCss), panelCss, text, accentCss: accent, muted: v("--text-muted", "#6f6c66"), textRgb: parseColor(text), accentRgb: parseColor(accent) };
}

function parseColor(css: string): RGB {
  const s = css.trim();
  if (s.startsWith("#")) {
    const hex = s.slice(1);
    if (hex.length === 3) return [parseInt(hex[0] + hex[0], 16) / 255, parseInt(hex[1] + hex[1], 16) / 255, parseInt(hex[2] + hex[2], 16) / 255];
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
