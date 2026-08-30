import { useEffect, useRef } from "react";

// Decorative animated background for the login screen. Drawn on a canvas rather than as SVG
// because everything moves every frame. Colour and overall fade come from CSS (.backdrop in
// app.css), so it follows the theme, and all three variants share the same radial edge fade
// and slow fade-in. Switch between them here; the ones not in use stay in the file.
//
//   "starfield"  a perspective starfield streaming past the viewer
//   "graph"      that starfield with a force-directed node graph drifting over it: nodes carry
//                a depth that oscillates so they swell and dim, and links form and dissolve as
//                nodes drift within reach of each other (buildGraph / stepGraph / drawGraph)
//   "particles"  space dust after deanwagman's "Particles in space" pen, in theme grey
//                (buildParticles / stepParticles / drawParticles). Click anywhere to re-burst.
type Variant = "starfield" | "graph" | "particles";
const VARIANT: Variant = "particles";

// Motion is switched off: the scene is still built and laid out, but only one frame is painted
// and nothing moves afterwards. Set this back to true to restore the animation loop — and, for
// the particles variant, its click-to-burst. It runs through exactly the same code path as
// prefers-reduced-motion, so the still frame is one that was already being produced.
const ANIMATE: boolean = true;

// roughly one graph node / one star / one particle per this many CSS pixels, so the density
// holds at any window size. The particle spacing is looser than the pen's: its field drains off
// the edges between clicks, whereas this one recycles, so the same count reads denser.
const NODE_AREA = 9000;
const STAR_AREA = 900;
const PARTICLE_AREA = 2400;
const MAX_NODES = 150;
const MAX_STARS = 700;
const MAX_PARTICLES = 900;

// the pen's own numbers, kept as they were
const MAX_PARTICLE_SIZE = 10;
const MAX_PARTICLE_SPEED = 40;
// the pen has no equivalent: see seedParticle for why a ceiling is needed here
const MAX_PARTICLE_DRIFT = 220;

// depth: 1 is the screen plane, larger is further away. Everything about a node — its size,
// its brightness, how far it swings as the field drifts — follows from this.
const Z_MID = 1.2;
const Z_SWING = 0.45;
// depth expressed in pixels: enough that separating in z breaks a link, not so much that it
// swamps the distance across the plane
const Z_TO_PX = 85;

// force tuning, in CSS pixels and seconds. Deliberately gentle: the graph should look like it
// is settling forever without ever quite getting there.
const REPULSION = 7000;
const REPULSION_RANGE = 230;
const REPULSION_CAP = 40; // keeps a near-coincident pair from exploding
const SPRING = 0.5; // only acts on established links, and only in proportion to their strength
const WANDER = 5; // the drift that stops the layout settling into a still image
const DAMPING = 0.5; // fraction of velocity retained per second
const MAX_SPEED = 18;
const EDGE_MARGIN = 40;

// links connect at CONNECT and only let go at DISCONNECT; the gap between the two is what stops
// a pair hovering at the threshold from flickering on and off
const CONNECT_FACTOR = 1.75;
const DISCONNECT_FACTOR = 2.3;
const LINK_EASE = 1.1; // per second, so a link takes about a second to fade in or out

// starfield: z runs from 1 (far) down to Z_NEAR (passing the viewer)
const Z_NEAR = 0.12;
const Z_SPEED = 0.024; // a star takes ~35s to cross the field
const TRAIL = 7; // how many frames of travel the streak behind a star represents
const INTRO_SECONDS = 5; // the field rises out of nothing rather than being there from frame one

// mulberry32: a tiny deterministic PRNG, so a given window size always yields the same layout
function makeRandom(seed: number) {
  let s = seed >>> 0;
  return () => {
    s = (s + 0x6d2b79f5) >>> 0;
    let t = Math.imul(s ^ (s >>> 15), 1 | s);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

// near nodes are bright and large, far ones dim and small: the whole 3D read comes from this
const depthAlpha = (z: number) => 0.4 + 0.6 * ((Z_MID + Z_SWING - z) / (2 * Z_SWING));

interface GraphNode {
  x: number; // position on the z = 1 plane; the projection divides by z
  y: number;
  vx: number;
  vy: number;
  z: number;
  r: number;
  phase: number; // rotates slowly, so the wander force never points the same way for long
  spin: number;
  zPhase: number;
  zSpin: number;
}

interface Link {
  a: GraphNode;
  b: GraphNode;
  dist: number;
  strength: number; // eases between 0 and target, which is what makes links fade in and out
  target: 0 | 1;
}

interface Star {
  x: number;
  y: number;
  z: number;
  brightness: number;
}

interface Particle {
  x: number;
  y: number;
  vx: number;
  vy: number;
  r: number;
  brightness: number; // stands in for the pen's per-particle colour, which we do not have
}

interface Scene {
  nodes: GraphNode[];
  links: Map<number, Link>;
  stars: Star[];
  particles: Particle[];
  connect: number;
  disconnect: number;
}

function buildGraph(w: number, h: number, random: () => number): GraphNode[] {
  const minDist = Math.sqrt(NODE_AREA) * 0.72;
  // the z = 1 plane has to be wider than the screen, or nothing far away would reach the edges.
  // The node count follows that plane's area, not the screen's, or the graph comes out sparse.
  const spread = Z_MID + Z_SWING;
  const target = Math.min(MAX_NODES, Math.max(20, Math.round((w * spread * h * spread) / NODE_AREA)));

  // rejection sampling, so no two nodes start crowded
  const nodes: GraphNode[] = [];
  for (let attempt = 0; attempt < 6000 && nodes.length < target; attempt++) {
    const x = (random() - 0.5) * w * spread;
    const y = (random() - 0.5) * h * spread;
    if (nodes.some((n) => (n.x - x) ** 2 + (n.y - y) ** 2 < minDist ** 2)) continue;
    nodes.push({
      x,
      y,
      vx: 0,
      vy: 0,
      z: Z_MID,
      r: 1.4 + random() * 2,
      phase: random() * Math.PI * 2,
      spin: 0.12 + random() * 0.28,
      zPhase: random() * Math.PI * 2,
      zSpin: 0.05 + random() * 0.1, // a full depth cycle takes one to two minutes
    });
  }
  return nodes;
}

// The pen's motion model, quirks intact: the direction is in degrees but handed straight to
// Math.sin, and the step is then divided by sin(speed). Neither is likely what its author meant,
// but together they are exactly what gives the field its uneven, scattered drift — so they stay.
// Two things do change. The per-frame step becomes a velocity, so the result no longer depends
// on the frame rate; and the speed is capped, because sin(speed) can land near zero and produce
// a particle that crosses the screen in a single frame. In the pen that particle simply left and
// was gone, but this field recycles, so an uncapped one would strobe.
function seedParticle(p: Particle, x: number, y: number, random: () => number) {
  const d = Math.round(random() * 360);
  const s = Math.pow(Math.ceil(random() * MAX_PARTICLE_SPEED), 0.7);
  const a = 180 - (d + 90);
  const stepX = (s * Math.sin(d)) / Math.sin(s);
  const stepY = (s * Math.sin(a)) / Math.sin(s);
  const vx = (d > 0 && d < 180 ? stepX : -stepX) * 60;
  const vy = (d > 90 && d < 270 ? stepY : -stepY) * 60;
  // Soft knee rather than a hard clamp: speed * MAX / (speed + MAX) leaves slow particles almost
  // untouched and bends fast ones asymptotically towards MAX. A hard clamp put every fast
  // particle at exactly the same speed, which after a burst showed up as expanding rings.
  const speed = Math.hypot(vx, vy) || 1e-4;
  const knee = MAX_PARTICLE_DRIFT / (speed + MAX_PARTICLE_DRIFT);
  p.x = x;
  p.y = y;
  p.vx = vx * knee;
  p.vy = vy * knee;
  p.r = Math.ceil(random() * MAX_PARTICLE_SIZE);
  // the pen varies both colour and alpha per particle; with a single theme ink to work from,
  // varying the alpha alone is what gives the field its range of greys
  p.brightness = 0.45 + random() * 0.55;
}

function buildParticles(w: number, h: number, random: () => number): Particle[] {
  const count = Math.min(MAX_PARTICLES, Math.max(60, Math.round((w * h) / PARTICLE_AREA)));
  return Array.from({ length: count }, () => {
    const p: Particle = { x: 0, y: 0, vx: 0, vy: 0, r: 1, brightness: 1 };
    seedParticle(p, random() * w, random() * h, random);
    return p;
  });
}

function buildScene(w: number, h: number): Scene {
  const random = makeRandom(0x5eed);
  const minDist = Math.sqrt(NODE_AREA) * 0.72;
  const nodes = VARIANT === "graph" ? buildGraph(w, h, random) : [];
  const particles = VARIANT === "particles" ? buildParticles(w, h, random) : [];

  // stars are seeded from where they should appear on screen and back-projected to z = 1, so
  // the field is already correctly distributed on the very first frame
  const cx = w / 2;
  const cy = h / 2;
  const starCount = Math.min(MAX_STARS, Math.round((w * h) / STAR_AREA));
  const stars: Star[] = Array.from({ length: starCount }, () => {
    const z = Z_NEAR + random() * (1 - Z_NEAR);
    return { x: (random() * w - cx) * z, y: (random() * h - cy) * z, z, brightness: 0.35 + random() * 0.65 };
  });

  return { nodes, links: new Map(), stars, particles, connect: minDist * CONNECT_FACTOR, disconnect: minDist * DISCONNECT_FACTOR };
}

export function Backdrop() {
  const canvasRef = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext("2d");
    if (!ctx) return;

    let ink = "128, 128, 128";
    let veil = 0.16; // only a fallback; readTheme overwrites it from the CSS token below
    const readTheme = () => {
      const style = getComputedStyle(canvas);
      // the element's CSS colour is --backdrop-ink, reported as "rgb(r, g, b)"
      const parts = style.color.match(/\d+/g);
      if (parts && parts.length >= 3) ink = `${parts[0]}, ${parts[1]}, ${parts[2]}`;
      // folded into every alpha below rather than set as CSS opacity: a viewport-sized layer
      // blended over the page on every frame is the most expensive thing this could do
      const declared = parseFloat(style.getPropertyValue("--backdrop-opacity"));
      if (!Number.isNaN(declared)) veil = declared;
    };
    readTheme();

    let w = 0;
    let h = 0;
    let scene: Scene = { nodes: [], links: new Map(), stars: [], particles: [], connect: 0, disconnect: 0 };
    const resize = () => {
      const dpr = window.devicePixelRatio || 1;
      w = canvas.clientWidth;
      h = canvas.clientHeight;
      if (w === 0 || h === 0) return;
      canvas.width = Math.round(w * dpr);
      canvas.height = Math.round(h * dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      scene = buildScene(w, h);
    };
    resize();

    const stepGraph = (dt: number, time: number) => {
      const { nodes, links } = scene;
      const halfW = (w / 2) * (Z_MID + Z_SWING);
      const halfH = (h / 2) * (Z_MID + Z_SWING);

      for (const n of nodes) {
        // depth breathes on its own slow cycle, independent of the layout forces
        n.z = Z_MID + Z_SWING * Math.sin(time * n.zSpin + n.zPhase);
        // a slowly rotating force vector: gives every node its own endless, unrepeating drift
        n.phase += n.spin * dt;
        n.vx += Math.cos(n.phase) * WANDER * dt;
        n.vy += Math.sin(n.phase) * WANDER * dt;
      }

      // one O(n^2) pass does double duty: repulsion, and deciding which pairs are linked.
      // 40-odd nodes is only a few hundred pairs, so this is nothing.
      for (let i = 0; i < nodes.length; i++) {
        const a = nodes[i];
        for (let j = i + 1; j < nodes.length; j++) {
          const b = nodes[j];
          const dx = a.x - b.x;
          const dy = a.y - b.y;
          const dz = (a.z - b.z) * Z_TO_PX;
          const flat2 = dx * dx + dy * dy;
          if (flat2 > 1e-4 && flat2 < REPULSION_RANGE * REPULSION_RANGE) {
            const d = Math.sqrt(flat2);
            const f = Math.min(REPULSION / flat2, REPULSION_CAP) * dt;
            a.vx += (dx / d) * f;
            a.vy += (dy / d) * f;
            b.vx -= (dx / d) * f;
            b.vy -= (dy / d) * f;
          }
          // separation in depth counts too, so nodes drifting apart in z let go of each other
          const spatial = Math.sqrt(flat2 + dz * dz);
          const key = i * 4096 + j;
          const existing = links.get(key);
          if (existing) {
            existing.dist = spatial;
            existing.target = spatial < scene.disconnect ? 1 : 0;
          } else if (spatial < scene.connect) {
            links.set(key, { a, b, dist: spatial, strength: 0, target: 1 });
          }
        }
      }

      const ease = Math.min(1, LINK_EASE * dt);
      const rest = scene.connect * 0.8;
      for (const [key, l] of links) {
        l.strength += (l.target - l.strength) * ease;
        if (l.target === 0 && l.strength < 0.01) {
          links.delete(key);
          continue;
        }
        // a link only pulls as hard as it is faded in, so a dissolving one lets go gradually
        const dx = l.b.x - l.a.x;
        const dy = l.b.y - l.a.y;
        const d = Math.hypot(dx, dy) || 1e-4;
        const f = (d - rest) * SPRING * l.strength * dt;
        l.a.vx += (dx / d) * f;
        l.a.vy += (dy / d) * f;
        l.b.vx -= (dx / d) * f;
        l.b.vy -= (dy / d) * f;
      }

      const decay = Math.pow(DAMPING, dt);
      for (const n of nodes) {
        // soft walls on the z = 1 plane, so the field breathes instead of piling up on an edge
        if (n.x < -halfW + EDGE_MARGIN) n.vx += (-halfW + EDGE_MARGIN - n.x) * 2 * dt;
        else if (n.x > halfW - EDGE_MARGIN) n.vx -= (n.x - (halfW - EDGE_MARGIN)) * 2 * dt;
        if (n.y < -halfH + EDGE_MARGIN) n.vy += (-halfH + EDGE_MARGIN - n.y) * 2 * dt;
        else if (n.y > halfH - EDGE_MARGIN) n.vy -= (n.y - (halfH - EDGE_MARGIN)) * 2 * dt;
        n.vx *= decay;
        n.vy *= decay;
        const speed = Math.hypot(n.vx, n.vy);
        if (speed > MAX_SPEED) {
          n.vx = (n.vx / speed) * MAX_SPEED;
          n.vy = (n.vy / speed) * MAX_SPEED;
        }
        n.x += n.vx * dt;
        n.y += n.vy * dt;
      }
    };

    const stepStars = (dt: number) => {
      const cx = w / 2;
      const cy = h / 2;
      for (const s of scene.stars) {
        s.z -= Z_SPEED * dt;
        if (s.z > Z_NEAR && Math.abs(s.x / s.z) < cx * 1.15 && Math.abs(s.y / s.z) < cy * 1.15) continue;
        // past the viewer or off the side: send it back to the far plane in a new spot
        s.z = 1;
        s.x = Math.random() * w - cx;
        s.y = Math.random() * h - cy;
        s.brightness = 0.35 + Math.random() * 0.65;
      }
    };

    const stepParticles = (dt: number) => {
      for (const p of scene.particles) {
        p.x += p.vx * dt;
        p.y += p.vy * dt;
        // the pen drops anything that leaves and tops the field up on the next click; this one
        // has to keep going unattended, so a particle that leaves comes back on the far side
        const m = p.r + 4;
        if (p.x < -m) p.x = w + m;
        else if (p.x > w + m) p.x = -m;
        if (p.y < -m) p.y = h + m;
        else if (p.y > h + m) p.y = -m;
      }
    };

    // the pen's "click anywhere": every particle is thrown again from the point clicked. The pen
    // pushed a fresh batch on top of the survivors, which grows the array on every click; re-
    // seeding the ones already there looks the same and keeps the count fixed.
    const burst = (x: number, y: number) => {
      // Math.random here, not the seeded PRNG: the initial layout is meant to be reproducible,
      // but two clicks in the same spot should not throw the same pattern twice
      for (const p of scene.particles) seedParticle(p, x, y, Math.random);
    };

    const step = (dt: number, time: number) => {
      if (VARIANT === "particles") {
        stepParticles(dt);
        return;
      }
      if (VARIANT === "graph") stepGraph(dt, time);
      stepStars(dt);
    };

    type Fade = (x: number, y: number) => number;

    // stars: the nearer they are, the bigger, brighter and longer-trailed
    const drawStars = (fadeAt: Fade) => {
      const cx = w / 2;
      const cy = h / 2;
      for (const s of scene.stars) {
        const sx = cx + s.x / s.z;
        const sy = cy + s.y / s.z;
        const near = 1 - s.z;
        const alpha = Math.min(1, near * 1.5) * s.brightness * fadeAt(sx, sy);
        if (alpha <= 0.002) continue;
        const r = 0.35 + near * 1.9;
        if (s.z < 0.55) {
          const pz = Math.min(1, s.z + (Z_SPEED * TRAIL) / 60);
          const px = cx + s.x / pz;
          const py = cy + s.y / pz;
          if ((px - sx) ** 2 + (py - sy) ** 2 > 0.36) {
            ctx.strokeStyle = `rgba(${ink}, ${alpha * 0.5})`;
            ctx.lineWidth = r;
            ctx.beginPath();
            ctx.moveTo(px, py);
            ctx.lineTo(sx, sy);
            ctx.stroke();
          }
        }
        ctx.fillStyle = `rgba(${ink}, ${alpha})`;
        ctx.beginPath();
        ctx.arc(sx, sy, r, 0, Math.PI * 2);
        ctx.fill();
      }
    };

    // space dust: plain filled circles, sized and shaded per particle, exactly as the pen draws
    // them. The pen paints an opaque background first; here the canvas is only cleared, so the
    // page's own background shows through.
    const drawParticles = (fadeAt: Fade) => {
      for (const p of scene.particles) {
        const alpha = p.brightness * fadeAt(p.x, p.y);
        if (alpha <= 0.002) continue;
        ctx.fillStyle = `rgba(${ink}, ${alpha})`;
        ctx.beginPath();
        ctx.arc(p.x, p.y, p.r, 0, Math.PI * 2);
        ctx.fill();
      }
    };

    const drawGraph = (fadeAt: Fade) => {
      const cx = w / 2;
      const cy = h / 2;
      // links, drawn at the depth of their two ends and faded by how strained they are
      for (const [, link] of scene.links) {
        const az = 1 / link.a.z;
        const bz = 1 / link.b.z;
        const ax = cx + link.a.x * az;
        const ay = cy + link.a.y * az;
        const bx = cx + link.b.x * bz;
        const by = cy + link.b.y * bz;
        const slack = 0.4 + 0.6 * Math.max(0, 1 - link.dist / scene.disconnect);
        const depth = (depthAlpha(link.a.z) + depthAlpha(link.b.z)) / 2;
        const alpha = link.strength * slack * depth * fadeAt((ax + bx) / 2, (ay + by) / 2);
        if (alpha <= 0.002) continue;
        ctx.strokeStyle = `rgba(${ink}, ${alpha})`;
        ctx.lineWidth = 0.5 + ((az + bz) / 2) * 0.35;
        ctx.beginPath();
        ctx.moveTo(ax, ay);
        ctx.lineTo(bx, by);
        ctx.stroke();
      }

      for (const n of scene.nodes) {
        const scale = 1 / n.z;
        const sx = cx + n.x * scale;
        const sy = cy + n.y * scale;
        const fade = fadeAt(sx, sy) * depthAlpha(n.z);
        if (fade <= 0.002) continue;
        const r = n.r * scale;
        // a soft halo around every node, tightest and brightest on the near ones, which is
        // what stops the far ones reading as flat specks
        const glow = ctx.createRadialGradient(sx, sy, 0, sx, sy, r * 3.2);
        glow.addColorStop(0, `rgba(${ink}, ${0.3 * fade})`);
        glow.addColorStop(0.4, `rgba(${ink}, ${0.09 * fade})`);
        glow.addColorStop(1, `rgba(${ink}, 0)`);
        ctx.fillStyle = glow;
        ctx.beginPath();
        ctx.arc(sx, sy, r * 3.2, 0, Math.PI * 2);
        ctx.fill();
        ctx.fillStyle = `rgba(${ink}, ${0.95 * fade})`;
        ctx.beginPath();
        ctx.arc(sx, sy, r, 0, Math.PI * 2);
        ctx.fill();
      }
    };

    const draw = (elapsed: number) => {
      const cx = w / 2;
      const fy = h * 0.42; // the fade is centred a little above the middle, behind the form
      const fadeRadius = Math.hypot(w, h) * 0.62;
      // smoothstep, so the field neither snaps on nor lingers at the threshold of visibility
      const t = Math.min(1, elapsed / INTRO_SECONDS);
      const shown = veil * t * t * (3 - 2 * t);
      // The radial falloff the static version got from an SVG mask, folded into each
      // primitive's alpha: compositing it as a full-canvas pass costs more than everything
      // else here combined.
      const fadeAt: Fade = (x, y) => {
        const d = Math.hypot(x - cx, y - fy) / fadeRadius;
        if (d >= 1) return 0;
        return d <= 0.5 ? shown * (1 - d * 0.5) : shown * 0.75 * (1 - (d - 0.5) / 0.5) ** 1.3;
      };
      ctx.clearRect(0, 0, w, h);
      if (VARIANT === "particles") {
        drawParticles(fadeAt);
        return;
      }
      drawStars(fadeAt);
      if (VARIANT === "graph") drawGraph(fadeAt);
    };

    const reduced = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ?? false;
    const still = !ANIMATE || reduced;
    let frame = 0;
    let last = 0;
    let elapsed = 0;
    const tick = (now: number) => {
      // clamp dt so a backgrounded tab does not resume with one enormous integration step
      const dt = last === 0 ? 1 / 60 : Math.min(0.05, (now - last) / 1000);
      last = now;
      elapsed += dt;
      step(dt, elapsed);
      draw(elapsed);
      frame = requestAnimationFrame(tick);
    };
    // one pass to work out which pairs are linked, then snap past the fade-in the links and the
    // field would otherwise ease through, and paint a single frame
    const renderStill = () => {
      step(1 / 60, 0);
      for (const l of scene.links.values()) l.strength = l.target;
      draw(INTRO_SECONDS); // no fade-in either: straight to full strength
    };
    if (still) {
      renderStill();
    } else {
      frame = requestAnimationFrame(tick);
    }

    // listened for on the window rather than the canvas, which is pointer-events: none so that
    // it never gets between the viewer and the form
    const onClick = (e: MouseEvent) => burst(e.clientX, e.clientY);
    if (VARIANT === "particles" && !still) window.addEventListener("click", onClick);
    const onResize = () => {
      resize();
      // setting canvas.width clears it, and with the loop switched off there is no next frame
      // to put anything back, so repaint here
      if (still) renderStill();
    };
    window.addEventListener("resize", onResize);
    // a theme switch changes the computed colour but not the markup, so watch the attribute
    const themeObserver = new MutationObserver(readTheme);
    themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    return () => {
      cancelAnimationFrame(frame);
      window.removeEventListener("click", onClick);
      window.removeEventListener("resize", onResize);
      themeObserver.disconnect();
    };
  }, []);

  return <canvas className="backdrop" ref={canvasRef} aria-hidden="true" />;
}
