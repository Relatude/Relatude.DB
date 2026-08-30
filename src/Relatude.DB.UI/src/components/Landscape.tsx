import { useEffect, useRef } from "react";

// A flight down a winding valley, drawn behind the login form. After the three.js demo
// "Interactive Landscape" by Codrops (tympanus.net/Development/InteractiveLandscape), with its
// height field kept intact — Perlin noise scrolling towards the viewer, multiplied by a sine
// mask that carves a meandering road down the middle — and its surface thrown away: no
// palette, no sky, no fill. What is left is a monochrome contour drawing, which is the only
// form of it that survives being faded down far enough to sit behind a form. The one piece of
// the demo's shading that is kept is the term that dims the ground by its own height, because
// that is what picks the road out of the landscape.
//
// It is drawn on a 2D canvas rather than in WebGL, so the file adds no dependency to a project
// that has almost none. Two things make that affordable. The terrain is fixed in view space
// and only the height field scrolls through it (exactly as in the demo, where the plane never
// moves and the noise is sampled at uv.y + time), so every screen x on the mesh is a constant
// and only the y values are recomputed. And the mesh is laid out in screen space rather than
// world space: column c sits at the same pixel column on every row, so a row's worth of terrain
// costs one height sample per visible column instead of one per world-space vertex, most of
// which would fall off the sides of the near rows.
//
// Colour and overall fade come from CSS (.landscape in app.css), so it follows the theme.

// --- camera, in the demo's units: a 100-wide corridor seen from 8 above the road ---
const FOV = 60; // vertical, degrees
const CAM_HEIGHT = 8;
// nothing nearer than this is worth carrying: the road already projects past the bottom of
// the window there, and only the sides of the corridor are still in frame
const NEAR = 11;
const FAR = 380;
const MAX_HEIGHT = 12;
// the horizon sits below the middle, so the form has clear sky behind it and the terrain
// fills the lower part of the window
const HORIZON = 0.66;

// --- height field ---
// Noise cells are 10 units across and 40 deep, so ridges run lengthwise down the corridor.
// The second octave only adds the fine break-up that keeps the near slopes from reading as
// perfectly smooth folds.
const NOISE_X1 = 0.075;
const NOISE_Y1 = 0.025;
const NOISE_X2 = 0.15;
const NOISE_Y2 = 0.052;
const OCTAVE2 = 0.24;
const NOISE_GAIN = 1.35; // perlin2 returns about ±0.75, so this lands the ridge tops near 2
const SPEED = 10; // world units per second the field travels towards the camera

// The road: the height field is multiplied by |sin| across the corridor, which pins it to
// nothing along the road and lets it up to full height at the crests halfway between two
// roads. The demo puts those 50 units apart; here they are far enough apart that the second
// road never comes into frame before the haze, so what is seen is one valley rather than a
// row of them.
const ROAD_SPACING = 200; // world units from one road to the next
// how far the valley wanders off centre, in world units. The two sines below reach 1.5
// between them, and the total has to stay well inside ROAD_SPACING / 2, or the camera ends
// up flying through a hillside instead of along the floor.
const ROAD_SWAY = 10;
const ROAD_SWAY_SWING = 4;
const ROAD_EXP = 1.2; // exponent on the mask: higher opens the valley floor out wider
const ROAD_EXP_SWING = 0.45;
// the mask never quite closes, so the valley floor keeps a little of the noise instead of
// being milled flat. Without it the road is a run of dead straight, evenly spaced contours.
const ROAD_FLOOR = 0.07;
// one meander every ~200 units of depth. The phase is tied to the same travel distance as
// the noise, so the valley is carried along with the ground it is carved into instead of
// sliding over it.
const ROAD_FREQ = 0.0314; // radians per unit of depth (the demo's uv.y * 4PI over 400 units)
const ROAD_K = Math.PI / ROAD_SPACING;

// --- look ---
const ROW_SPACING = 15; // target gap in CSS pixels between contours on the flat road
const COL_SPACING = 12; // and between samples along one
const PAD = 30; // contours run this far past the window, so no line ends mid-edge
const FOG_NEAR = 40;
const FOG_FAR = 300; // beyond this nothing is drawn, which is also what hides the corridor
// The nearest contours are held back too. Distance haze does not work that way, but the
// foreground is where they crowd together and where the form has to stay readable, and a
// background is the wrong place to put the busiest part of a picture.
const FOREGROUND = 0.45;
const FOREGROUND_FAR = 55;
// The demo's fragment shader dims the ground by its own height (stripColor *= 1 - vDisplace),
// which is what picks the road out of the landscape as a bright ribbon. The same term here has
// to vary along a contour rather than per pixel, so it is applied as a gradient across the
// window: a dozen stops sampled off the contour's own height is far more resolution than a
// fade this gentle needs.
const SHADE_SLOPE = 0.7; // how fast height dims a contour, in normalised height units
const SHADE_MIN = 0.18; // ridge lines dim this far and no further, so the skyline survives
const SHADE_STOPS = 12;
const INTRO_SECONDS = 3; // the landscape rises out of nothing rather than being there at once
const MOUSE_EASE = 0.06;

// Motion is on. Set this to false to keep the layout and paint a single still frame instead;
// it runs through the same path as prefers-reduced-motion, so the still frame is one that was
// already being produced.
const ANIMATE: boolean = true;

// mulberry32, only ever used to shuffle the permutation table below
function makeRandom(seed: number) {
  let s = seed >>> 0;
  return () => {
    s = (s + 0x6d2b79f5) >>> 0;
    let t = Math.imul(s ^ (s >>> 15), 1 | s);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

// Classic Perlin gradient noise, the CPU counterpart of the demo's cnoise. Two dimensions is
// enough: its third coordinate is a constant, so the shader is sampling a fixed slice too.
const PERM = (() => {
  const base = new Uint8Array(256);
  for (let i = 0; i < 256; i++) base[i] = i;
  const random = makeRandom(0x1a4d5e);
  for (let i = 255; i > 0; i--) {
    const j = Math.floor(random() * (i + 1));
    const t = base[i];
    base[i] = base[j];
    base[j] = t;
  }
  const p = new Uint8Array(512);
  for (let i = 0; i < 512; i++) p[i] = base[i & 255];
  return p;
})();

const fadeCurve = (t: number) => t * t * t * (t * (t * 6 - 15) + 10);

// the eight diagonal and axial gradients, as sums rather than a table lookup
function grad2(hash: number, x: number, y: number) {
  switch (hash & 7) {
    case 0:
      return x + y;
    case 1:
      return y - x;
    case 2:
      return x - y;
    case 3:
      return -x - y;
    case 4:
      return x;
    case 5:
      return -x;
    case 6:
      return y;
    default:
      return -y;
  }
}

function perlin2(x: number, y: number) {
  const xi = Math.floor(x);
  const yi = Math.floor(y);
  const xf = x - xi;
  const yf = y - yi;
  const u = fadeCurve(xf);
  const v = fadeCurve(yf);
  const a = PERM[xi & 255] + (yi & 255);
  const b = PERM[(xi + 1) & 255] + (yi & 255);
  const n00 = grad2(PERM[a], xf, yf);
  const n10 = grad2(PERM[b], xf - 1, yf);
  const n01 = grad2(PERM[a + 1], xf, yf - 1);
  const n11 = grad2(PERM[b + 1], xf - 1, yf - 1);
  const nx0 = n00 + u * (n10 - n00);
  const nx1 = n01 + u * (n11 - n01);
  return nx0 + v * (nx1 - nx0);
}

// The road mask is pow(|sin(...)|, exponent), and the exponent is the same for every sample in
// a frame — so the power is worth tabulating once a frame rather than calling Math.pow tens of
// thousands of times. 256 steps is far finer than the mask is ever seen at.
const ROAD_LUT_STEPS = 256;
const roadLut = new Float32Array(ROAD_LUT_STEPS + 1);

const smoothstep = (edge0: number, edge1: number, x: number) => {
  const t = Math.min(1, Math.max(0, (x - edge0) / (edge1 - edge0)));
  return t * t * (3 - 2 * t);
};

export function Landscape() {
  const canvasRef = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext("2d");
    if (!ctx) return;

    let ink = "128, 128, 128";
    let veil = 0.1; // only a fallback; readTheme overwrites it from the CSS token below
    const readTheme = () => {
      const style = getComputedStyle(canvas);
      // the element's CSS colour is --backdrop-ink, reported as "rgb(r, g, b)"
      const parts = style.color.match(/\d+/g);
      if (parts && parts.length >= 3) ink = `${parts[0]}, ${parts[1]}, ${parts[2]}`;
      // folded into every alpha below rather than applied as CSS opacity, which would mean
      // blending a window-sized layer over the page on every frame
      const declared = parseFloat(style.getPropertyValue("--landscape-opacity"));
      if (!Number.isNaN(declared)) veil = declared;
    };
    readTheme();

    let w = 0;
    let h = 0;
    let cx = 0;
    let cy = 0;
    let focal = 0;
    let cols = 0;
    let rows = 0;
    // screen x of every column, and that x as a direction out of the camera: multiplying it by
    // a row's depth gives the world x the column samples on that row
    let colX = new Float32Array(0);
    let colDir = new Float32Array(0);
    let rowDepth = new Float32Array(0);
    // one row of projected y values, plus the row behind it: a strip only ever needs two
    let nearY = new Float32Array(0);
    let farY = new Float32Array(0);

    const layout = () => {
      const dpr = window.devicePixelRatio || 1;
      w = canvas.clientWidth;
      h = canvas.clientHeight;
      if (w === 0 || h === 0) return;
      canvas.width = Math.round(w * dpr);
      canvas.height = Math.round(h * dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

      cx = w / 2;
      cy = h * HORIZON;
      focal = h / 2 / Math.tan(((FOV * Math.PI) / 180) / 2);

      cols = Math.min(300, Math.max(16, Math.round((w + 2 * PAD) / COL_SPACING) + 1));
      colX = new Float32Array(cols);
      colDir = new Float32Array(cols);
      const step = (w + 2 * PAD) / (cols - 1);
      for (let c = 0; c < cols; c++) {
        colX[c] = -PAD + c * step;
        colDir[c] = (colX[c] - cx) / focal;
      }

      // Rows are spaced so that 1/depth is linear, which is what puts them an even distance
      // apart on screen: the flat road projects to cy + CAM_HEIGHT * focal / depth.
      const span = CAM_HEIGHT * focal * (1 / NEAR - 1 / FAR);
      rows = Math.min(240, Math.max(24, Math.round(span / ROW_SPACING)));
      rowDepth = new Float32Array(rows);
      for (let i = 0; i < rows; i++) {
        // index 0 is the farthest row: the strips are painted back to front
        const t = i / (rows - 1);
        rowDepth[i] = 1 / (1 / FAR + t * (1 / NEAR - 1 / FAR));
      }

      nearY = new Float32Array(cols);
      farY = new Float32Array(cols);
    };
    layout();

    // the mouse steers the valley, as it does in the demo, but only as an offset on top of a
    // drift that keeps the landscape alive when nothing is moving
    const mouse = { x: 0, y: 0, xEased: 0, yEased: 0 };

    let rowTop = 0; // the projected extent of the row computeRow last wrote, used to skip
    let rowBottom = 0; // strips and contours that fall entirely outside the window

    const computeRow = (out: Float32Array, depth: number, travel: number, sway: number) => {
      const k = focal / depth;
      const eyeY = cy + CAM_HEIGHT * k;
      const scale = MAX_HEIGHT * k;
      // the noise and the road both scroll with the distance travelled, and both depend on
      // depth alone across a row, so their y coordinates are constants here
      const nx1 = depth * NOISE_X1;
      const ny1 = (depth + travel) * NOISE_Y1;
      const nx2 = depth * NOISE_X2;
      const ny2 = (depth + travel) * NOISE_Y2;
      const meander = (depth + travel) * ROAD_FREQ;
      const centre = (Math.sin(meander) + Math.sin(meander * 0.5)) * sway;
      // the mask is |sin(PI * (worldX - centre) / ROAD_SPACING)|, and worldX is the column's
      // direction times the depth, so across a row it is a straight line through the sine
      const slope = depth * ROAD_K;
      const phase = -centre * ROAD_K;

      let top = Infinity;
      let bottom = -Infinity;
      for (let c = 0; c < cols; c++) {
        const dir = colDir[c];
        let height = (perlin2(dir * nx1, ny1) + perlin2(dir * nx2, ny2) * OCTAVE2) * NOISE_GAIN + 1;
        if (height < 0) height = 0;
        const mask = Math.abs(Math.sin(dir * slope + phase));
        height *= roadLut[(mask * ROAD_LUT_STEPS) | 0];
        const y = eyeY - height * scale;
        out[c] = y;
        if (y < top) top = y;
        if (y > bottom) bottom = y;
      }
      rowTop = top;
      rowBottom = bottom;
    };

    const draw = (time: number, elapsed: number) => {
      const travel = time * SPEED;
      const sway = ROAD_SWAY + ROAD_SWAY_SWING * Math.sin(time * 0.05) + mouse.xEased * ROAD_SWAY_SWING;
      const exponent = ROAD_EXP + ROAD_EXP_SWING * Math.sin(time * 0.037) + mouse.yEased * ROAD_EXP_SWING;
      for (let i = 0; i <= ROAD_LUT_STEPS; i++) {
        roadLut[i] = ROAD_FLOOR + (1 - ROAD_FLOOR) * Math.pow(i / ROAD_LUT_STEPS, exponent);
      }

      // smoothstep on the intro, so the landscape neither snaps on nor lingers at the threshold
      const t = Math.min(1, elapsed / INTRO_SECONDS);
      const shown = veil * t * t * (3 - 2 * t);

      ctx.clearRect(0, 0, w, h);
      ctx.fillStyle = "#000"; // only its alpha matters: it is never composited as colour
      ctx.lineJoin = "round";

      let far = farY;
      let near = nearY;
      computeRow(far, rowDepth[0], travel, sway);
      let farTop = rowTop;
      let farBottom = rowBottom;

      for (let i = 0; i < rows; i++) {
        const depth = rowDepth[i];
        let nearTop = 0;
        let nearBottom = 0;
        if (i < rows - 1) {
          computeRow(near, rowDepth[i + 1], travel, sway);
          nearTop = rowTop;
          nearBottom = rowBottom;
          // The surface between this contour and the one in front of it hides everything
          // already painted behind it. Erasing rather than filling with the page colour keeps
          // the canvas transparent, so it does not have to know what it is sitting on.
          if (Math.min(farTop, nearTop) < h && Math.max(farBottom, nearBottom) > 0) {
            ctx.globalCompositeOperation = "destination-out";
            ctx.beginPath();
            ctx.moveTo(colX[0], far[0]);
            for (let c = 1; c < cols; c++) ctx.lineTo(colX[c], far[c]);
            for (let c = cols - 1; c >= 0; c--) ctx.lineTo(colX[c], near[c]);
            ctx.closePath();
            ctx.fill();
          }
        }

        // the contour itself, drawn after its own strip so the erase cannot nibble at it, and
        // before every nearer strip, which is what occludes it
        const fog = 1 - smoothstep(FOG_NEAR, FOG_FAR, depth);
        const front = FOREGROUND + (1 - FOREGROUND) * smoothstep(NEAR, FOREGROUND_FAR, depth);
        const alpha = shown * fog * front;
        if (alpha > 0.002 && farTop < h && farBottom > 0) {
          const k = focal / depth;
          const groundY = cy + CAM_HEIGHT * k; // where this contour would run with no terrain
          const relief = MAX_HEIGHT * k; // and how far one unit of height lifts it off that
          const shade = ctx.createLinearGradient(colX[0], 0, colX[cols - 1], 0);
          for (let s = 0; s <= SHADE_STOPS; s++) {
            const at = s / SHADE_STOPS;
            const lift = (groundY - far[Math.round((cols - 1) * at)]) / relief;
            const dim = Math.max(SHADE_MIN, Math.min(1, 1 - lift * SHADE_SLOPE));
            shade.addColorStop(at, `rgba(${ink}, ${alpha * dim})`);
          }
          ctx.globalCompositeOperation = "source-over";
          ctx.strokeStyle = shade;
          ctx.lineWidth = Math.min(1.5, 0.55 + 16 / depth);
          ctx.beginPath();
          ctx.moveTo(colX[0], far[0]);
          for (let c = 1; c < cols; c++) ctx.lineTo(colX[c], far[c]);
          ctx.stroke();
        }

        const swap = far;
        far = near;
        near = swap;
        farTop = nearTop;
        farBottom = nearBottom;
      }
      ctx.globalCompositeOperation = "source-over";
    };

    const reduced = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ?? false;
    const still = !ANIMATE || reduced;

    let frame = 0;
    let last = 0;
    let time = 0;
    let elapsed = 0;
    const tick = (now: number) => {
      // clamp dt so a backgrounded tab does not resume with one enormous step
      const dt = last === 0 ? 1 / 60 : Math.min(0.05, (now - last) / 1000);
      last = now;
      time += dt;
      elapsed += dt;
      mouse.xEased += (mouse.x - mouse.xEased) * MOUSE_EASE;
      mouse.yEased += (mouse.y - mouse.yEased) * MOUSE_EASE;
      draw(time, elapsed);
      frame = requestAnimationFrame(tick);
    };
    // the still frame skips the intro as well: straight to full strength
    const renderStill = () => draw(0, INTRO_SECONDS);
    if (still) renderStill();
    else frame = requestAnimationFrame(tick);

    // listened for on the window, since the canvas is pointer-events: none and never comes
    // between the viewer and the form
    const onMouseMove = (e: MouseEvent) => {
      mouse.x = w > 0 ? (e.clientX / w) * 2 - 1 : 0;
      mouse.y = h > 0 ? (e.clientY / h) * 2 - 1 : 0;
    };
    if (!still) window.addEventListener("mousemove", onMouseMove);
    const onResize = () => {
      layout();
      // setting canvas.width clears it, and with the loop off there is no next frame to
      // put anything back
      if (still) renderStill();
    };
    window.addEventListener("resize", onResize);
    // a theme switch changes the computed colour but not the markup, so watch the attribute.
    // With the loop off there is no next frame to pick the new ink up, so repaint here too.
    const themeObserver = new MutationObserver(() => {
      readTheme();
      if (still) renderStill();
    });
    themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    return () => {
      cancelAnimationFrame(frame);
      window.removeEventListener("mousemove", onMouseMove);
      window.removeEventListener("resize", onResize);
      themeObserver.disconnect();
    };
  }, []);

  return <canvas className="landscape" ref={canvasRef} aria-hidden="true" />;
}
