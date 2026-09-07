import type { Bounds } from "./layouts";

/**
 * Draws the cards of the visual pivot: one instanced quad per card, WebGL 2, and nothing per card on
 * the CPU while the picture is still.
 *
 * The design is set by the size of the sets it has to show - a hundred thousand cards is ordinary,
 * a million is allowed - so the per-card state is kept on the GPU and touched only when something
 * changes for every card at once:
 *
 *  - Motion is computed in the vertex shader. Each card carries where it came from, where it is
 *    going, and when it leaves; a frame is one uniform (the time) and one draw call. A layout change
 *    uploads a new set of targets and the cards travel on their own, on a spring that starts slow
 *    and settles with a small overshoot, each leaving a little after the one before it, so a change
 *    is a flow across the picture rather than a jump. When targets change mid-flight the current
 *    positions are captured once on the CPU (the same spring, evaluated per card) and become the
 *    new starting points, so nothing ever jumps.
 *  - Colour is a group index per card (two bytes) and a palette texture the shader reads it from.
 *    Colouring by another property is one small buffer upload, not a rewrite of the picture.
 *  - Picking is done by the GPU: a click renders the ids as colours into a single pixel under the
 *    pointer. It is exact for whatever is on the screen, including cards half way through a move,
 *    and costs one extra draw on a click rather than a spatial index for every card.
 *
 * The camera is two-dimensional (a centre and a zoom) and eases toward its targets every frame, so a
 * wheel step or a fit is smooth. Cards are one world unit apart; a card's own size is a fraction of
 * that, and the fraction goes to one when a card is only a few pixels wide, so a picture zoomed out
 * to the whole set is a solid mosaic rather than a screen full of dust.
 *
 * Nothing here knows what a card stands for. The cards are a count, and a card is told apart from
 * its neighbours by its index alone, which is what a later version can hang a sprite or an image on.
 */

export type RGBf = [number, number, number];

export interface FieldTheme {
  /** what the frame is cleared to: the panel behind the picture */
  clear: RGBf;
  /** what a card fades toward when another group is highlighted */
  dim: RGBf;
  /** the ring around the selected card */
  outline: RGBf;
  /** what the hovered card is mixed toward: the page's text colour, so it darkens on light and lightens on dark */
  hover: RGBf;
}

export interface Camera {
  x: number;
  y: number;
  /** css pixels per world unit */
  zoom: number;
}

export interface CardField {
  /**
   * A new set of cards. `to` is where they go (two floats each); `from` is where they start, or null
   * for "in place". Cards that carried over from the previous set are handed their old positions by
   * the caller, which is what makes a filtered result flow out of the full one.
   */
  setCards(count: number, from: Float32Array | null, to: Float32Array): void;
  /** New targets for the same cards; they leave where they are now, staggered over `stagger` seconds and travelling for `duration`. */
  moveTo(to: Float32Array, stagger: number, duration: number): void;
  /** Where every card is right now, mid-flight or not; a fresh array. */
  positions(): Float32Array;
  /** The group of every card (uint16, an index into the palette) and the palette itself, rgba bytes per group. */
  setGroups(assignment: Uint16Array, palette: Uint8Array): void;
  setTheme(theme: FieldTheme): void;
  setHover(index: number): void;
  setSelected(index: number): void;
  /** Dims every card outside this group; -1 for none. */
  setHighlightGroup(group: number): void;
  /** The card under a css pixel of the canvas, or -1. */
  pick(cssX: number, cssY: number): number;
  /** Brings the bounds into view with a margin, animated unless told to jump. */
  fit(bounds: Bounds, paddingPx: number, animate: boolean): void;
  /** Zooms by a factor about a css pixel of the canvas, which stays put. */
  zoomBy(factor: number, cssX: number, cssY: number): void;
  /** Moves the view by css pixels, with no easing: this is the picture following a drag. */
  panBy(dx: number, dy: number): void;
  /** Lets the view coast on after a drag ends, at css pixels per second, slowing to a stop. */
  fling(vx: number, vy: number): void;
  worldToCss(x: number, y: number): [number, number];
  camera(): Camera;
  /** Whether cards or the camera are moving. */
  moving(): boolean;
  /** Called after every frame drawn, for whatever is laid over the canvas in html (labels). */
  onFrame(callback: (() => void) | null): void;
  /** Asks for a frame; cheap to call often. */
  invalidate(): void;
  /** Reads the canvas' css size again and resizes the drawing buffer to it. */
  resize(): void;
  destroy(): void;
}

// The spring the cards travel on: underdamped, so it starts from rest, arrives with a little
// overshoot and settles. ζ (damping) sets the overshoot - 0.66 is about six percent - and ζω the
// settling: e^-6 has it done by t = 1. Normalised by its value at 1 so t = 1 is exactly there, with
// no snap at the end. The GLSL below is this function, character for character in what matters.
const zeta = 0.66;
const omega = 6 / zeta;
const omegaD = omega * Math.sqrt(1 - zeta * zeta);
const springAtOne = 1 - Math.exp(-zeta * omega) * (Math.cos(omegaD) + ((zeta * omega) / omegaD) * Math.sin(omegaD));
function spring(t: number): number {
  if (t <= 0) return 0;
  if (t >= 1) return 1;
  const e = Math.exp(-zeta * omega * t);
  return (1 - e * (Math.cos(omegaD * t) + ((zeta * omega) / omegaD) * Math.sin(omegaD * t))) / springAtOne;
}

/** how much of the pitch a card fills when there is room to see the gap */
const cardFill = 0.84;
const maxZoom = 640; // css px per unit: a card fills most of a panel
const cameraRate = 11; // per second: how fast the camera closes on its target
const flingDecay = 4.2; // per second

const vertexSource = `#version 300 es
precision highp float;
precision highp int;
layout(location = 0) in vec2 aCorner;
layout(location = 1) in vec2 aFrom;
layout(location = 2) in vec2 aTo;
layout(location = 3) in float aDelay;
layout(location = 4) in uint aGroup;
uniform vec2 uCenter;
uniform float uZoom;      // device pixels per world unit
uniform vec2 uHalfSize;   // half the canvas, device pixels
uniform float uTime;      // seconds since the move began
uniform float uDuration;
uniform float uFill;
uniform sampler2D uPalette;
uniform int uHover;
uniform int uSelected;
uniform int uHighlightGroup;
uniform vec3 uDim;
uniform vec3 uHoverTint;
uniform int uPick;
out vec2 vUv;
out vec4 vColor;
flat out float vHalfPx;
flat out int vFlags;
const float ZETA = ${zeta.toFixed(6)};
const float OMEGA = ${omega.toFixed(6)};
const float OMEGA_D = ${omegaD.toFixed(6)};
const float AT_ONE = ${springAtOne.toFixed(8)};
float spring(float t) {
  if (t <= 0.0) return 0.0;
  if (t >= 1.0) return 1.0;
  float e = exp(-ZETA * OMEGA * t);
  return (1.0 - e * (cos(OMEGA_D * t) + (ZETA * OMEGA / OMEGA_D) * sin(OMEGA_D * t))) / AT_ONE;
}
void main() {
  float t = (uTime - aDelay) / uDuration;
  vec2 pos = mix(aFrom, aTo, spring(t));
  // the gap between cards appears as they get room for it; a card a few pixels wide fills its cell
  float fill = mix(1.0, uFill, smoothstep(2.5, 7.0, uZoom));
  vec2 corner = (aCorner - 0.5) * fill + 0.5;
  vec2 px = (pos + corner - uCenter) * uZoom;
  gl_Position = vec4(px.x / uHalfSize.x, -px.y / uHalfSize.y, 0.0, 1.0);
  vUv = aCorner;
  vHalfPx = 0.5 * fill * uZoom;
  int id = gl_InstanceID;
  vFlags = id == uSelected ? 1 : 0;
  if (uPick == 1) {
    vColor = vec4(float(id & 255) / 255.0, float((id >> 8) & 255) / 255.0, float((id >> 16) & 255) / 255.0, 1.0);
    return;
  }
  vec4 c = texelFetch(uPalette, ivec2(int(aGroup), 0), 0);
  if (uHighlightGroup >= 0 && int(aGroup) != uHighlightGroup) c.rgb = mix(c.rgb, uDim, 0.82);
  if (id == uHover) c.rgb = mix(c.rgb, uHoverTint, 0.28);
  vColor = c;
}`;

const fragmentSource = `#version 300 es
precision highp float;
precision highp int;
in vec2 vUv;
in vec4 vColor;
flat in float vHalfPx;
flat in int vFlags;
uniform int uPick;
uniform vec3 uOutline;
out vec4 outColor;
void main() {
  // signed distance to the edge of a rounded rectangle, in device pixels
  vec2 p = (vUv - 0.5) * 2.0 * vHalfPx;
  float r = clamp(vHalfPx * 0.16, 0.0, 6.0);
  vec2 q = abs(p) - (vHalfPx - r);
  float d = length(max(q, 0.0)) - r;
  if (uPick == 1) {
    if (d > 0.0) discard;
    outColor = vColor;
    return;
  }
  // one pixel of anti-aliasing on the rounded edge; a tiny card is left to the multisampling
  float alpha = vHalfPx > 2.5 ? clamp(0.5 - d, 0.0, 1.0) : 1.0;
  vec3 c = vColor.rgb;
  if (vFlags == 1 && vHalfPx > 4.0) c = mix(c, uOutline, clamp(d + 2.5, 0.0, 1.0));
  outColor = vec4(c, alpha * vColor.a);
}`;

export function createCardField(canvas: HTMLCanvasElement): CardField | null {
  const context = canvas.getContext("webgl2", { antialias: true, alpha: false, premultipliedAlpha: false, powerPreference: "high-performance" });
  if (!context) return null;
  const gl: WebGL2RenderingContext = context;

  const prog = program(gl, vertexSource, fragmentSource);
  const u = (name: string) => gl.getUniformLocation(prog, name);
  const uCenter = u("uCenter");
  const uZoom = u("uZoom");
  const uHalfSize = u("uHalfSize");
  const uTime = u("uTime");
  const uDuration = u("uDuration");
  const uFill = u("uFill");
  const uPalette = u("uPalette");
  const uHover = u("uHover");
  const uSelected = u("uSelected");
  const uHighlightGroup = u("uHighlightGroup");
  const uDim = u("uDim");
  const uHoverTint = u("uHoverTint");
  const uPick = u("uPick");
  const uOutline = u("uOutline");

  // the quad every card is an instance of, corner (0,0) to (1,1)
  const quad = gl.createBuffer()!;
  gl.bindBuffer(gl.ARRAY_BUFFER, quad);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([0, 0, 1, 0, 0, 1, 1, 1]), gl.STATIC_DRAW);
  const fromBuffer = gl.createBuffer()!;
  const toBuffer = gl.createBuffer()!;
  const delayBuffer = gl.createBuffer()!;
  const groupBuffer = gl.createBuffer()!;
  const vao = gl.createVertexArray()!;
  gl.bindVertexArray(vao);
  gl.bindBuffer(gl.ARRAY_BUFFER, quad);
  gl.enableVertexAttribArray(0);
  gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
  for (const [location, buffer, size] of [
    [1, fromBuffer, 2],
    [2, toBuffer, 2],
    [3, delayBuffer, 1],
  ] as const) {
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.enableVertexAttribArray(location);
    gl.vertexAttribPointer(location, size, gl.FLOAT, false, 0, 0);
    gl.vertexAttribDivisor(location, 1);
  }
  gl.bindBuffer(gl.ARRAY_BUFFER, groupBuffer);
  gl.enableVertexAttribArray(4);
  gl.vertexAttribIPointer(4, 1, gl.UNSIGNED_SHORT, 0, 0);
  gl.vertexAttribDivisor(4, 1);
  gl.bindVertexArray(null);

  // the palette: one texel per group
  const palette = gl.createTexture()!;
  gl.bindTexture(gl.TEXTURE_2D, palette);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([128, 128, 128, 255]));

  // one pixel to pick into
  const pickTexture = gl.createTexture()!;
  gl.bindTexture(gl.TEXTURE_2D, pickTexture);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
  const pickFramebuffer = gl.createFramebuffer()!;
  gl.bindFramebuffer(gl.FRAMEBUFFER, pickFramebuffer);
  gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, pickTexture, 0);
  gl.bindFramebuffer(gl.FRAMEBUFFER, null);
  const pickPixel = new Uint8Array(4);

  // ---- state ----
  let count = 0;
  let from: Float32Array = new Float32Array(0);
  let to: Float32Array = new Float32Array(0);
  let delay: Float32Array = new Float32Array(0);
  let moveStart = 0; // performance.now() when the current move began
  let duration = 1;
  let maxDelay = 0;
  let cardsMoving = false;
  let paletteSize = 1;
  let hover = -1;
  let selected = -1;
  let highlight = -1;
  let theme: FieldTheme = { clear: [1, 1, 1], dim: [1, 1, 1], outline: [0.04, 0.38, 0.7], hover: [0.1, 0.1, 0.1] };
  let width = 1;
  let height = 1;
  let dpr = 1;
  const cur: Camera = { x: 0, y: 0, zoom: 20 };
  const target: Camera = { x: 0, y: 0, zoom: 20 };
  let minZoom = 0.01;
  let flingV: [number, number] = [0, 0];
  let frameCallback: (() => void) | null = null;
  let raf = 0;
  let lastFrame = 0;
  let dirty = true;
  let destroyed = false;

  function schedule() {
    if (raf === 0 && !destroyed) raf = requestAnimationFrame(frame);
  }

  function elapsed(now: number): number {
    return (now - moveStart) / 1000;
  }

  function cameraMoving(): boolean {
    return (
      Math.abs(target.x - cur.x) * cur.zoom > 0.05 ||
      Math.abs(target.y - cur.y) * cur.zoom > 0.05 ||
      Math.abs(Math.log(target.zoom / cur.zoom)) > 0.0005 ||
      flingV[0] !== 0 ||
      flingV[1] !== 0
    );
  }

  function stepCamera(dt: number) {
    if (flingV[0] !== 0 || flingV[1] !== 0) {
      cur.x -= (flingV[0] * dt) / cur.zoom;
      cur.y -= (flingV[1] * dt) / cur.zoom;
      target.x = cur.x;
      target.y = cur.y;
      const k = Math.exp(-flingDecay * dt);
      flingV = [flingV[0] * k, flingV[1] * k];
      if (Math.hypot(flingV[0], flingV[1]) < 2) flingV = [0, 0];
    }
    const k = 1 - Math.exp(-cameraRate * dt);
    cur.x += (target.x - cur.x) * k;
    cur.y += (target.y - cur.y) * k;
    cur.zoom = Math.exp(Math.log(cur.zoom) + (Math.log(target.zoom) - Math.log(cur.zoom)) * k);
    if (!cameraMoving()) {
      cur.x = target.x;
      cur.y = target.y;
      cur.zoom = target.zoom;
    }
  }

  function setCommonUniforms(now: number, pickMode: boolean) {
    gl.useProgram(prog);
    gl.uniform2f(uCenter, cur.x, cur.y);
    gl.uniform1f(uZoom, cur.zoom * dpr);
    gl.uniform2f(uHalfSize, width / 2, height / 2);
    gl.uniform1f(uTime, cardsMoving ? elapsed(now) : 1e6);
    gl.uniform1f(uDuration, duration);
    gl.uniform1f(uFill, cardFill);
    gl.uniform1i(uHover, hover);
    gl.uniform1i(uSelected, selected);
    gl.uniform1i(uHighlightGroup, highlight);
    gl.uniform3fv(uDim, theme.dim);
    gl.uniform3fv(uHoverTint, theme.hover);
    gl.uniform3fv(uOutline, theme.outline);
    gl.uniform1i(uPick, pickMode ? 1 : 0);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, palette);
    gl.uniform1i(uPalette, 0);
  }

  function draw() {
    if (count === 0) return;
    gl.bindVertexArray(vao);
    gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, count);
    gl.bindVertexArray(null);
  }

  function frame(now: number) {
    raf = 0;
    if (destroyed) return;
    // a frame asked for by something that then turned out to change nothing draws nothing
    if (!dirty && !cardsMoving && !cameraMoving()) {
      lastFrame = 0;
      return;
    }
    const dt = lastFrame === 0 ? 1 / 60 : Math.min(0.1, (now - lastFrame) / 1000);
    lastFrame = now;
    stepCamera(dt);
    if (cardsMoving && elapsed(now) > duration + maxDelay) cardsMoving = false;

    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.viewport(0, 0, width, height);
    gl.clearColor(theme.clear[0], theme.clear[1], theme.clear[2], 1);
    gl.clear(gl.COLOR_BUFFER_BIT);
    gl.disable(gl.DEPTH_TEST);
    gl.disable(gl.CULL_FACE);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
    setCommonUniforms(now, false);
    draw();
    dirty = false;
    frameCallback?.();
    if (cardsMoving || cameraMoving()) schedule();
    else lastFrame = 0;
  }

  function upload(buffer: WebGLBuffer, data: ArrayBufferView) {
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.bufferData(gl.ARRAY_BUFFER, data, gl.DYNAMIC_DRAW);
  }

  // every card leaves a little after the one before it, in result order, with a little jitter so
  // the wave has a soft front rather than a ruled edge
  function planDelays(stagger: number) {
    if (delay.length !== count) delay = new Float32Array(count);
    let h = 0x9e3779b9;
    for (let i = 0; i < count; i++) {
      h = (h ^ (h << 13)) >>> 0;
      h = (h ^ (h >>> 17)) >>> 0;
      h = (h ^ (h << 5)) >>> 0;
      const jitter = (h & 0xffff) / 0xffff;
      delay[i] = stagger * (0.72 * (i / Math.max(1, count - 1)) + 0.28 * jitter);
    }
    maxDelay = stagger;
    upload(delayBuffer, delay);
  }

  function currentPositions(): Float32Array {
    const out = new Float32Array(count * 2);
    if (!cardsMoving) {
      out.set(to.subarray(0, count * 2));
      return out;
    }
    const time = elapsed(performance.now());
    for (let i = 0; i < count; i++) {
      const s = spring((time - delay[i]) / duration);
      out[i * 2] = from[i * 2] + (to[i * 2] - from[i * 2]) * s;
      out[i * 2 + 1] = from[i * 2 + 1] + (to[i * 2 + 1] - from[i * 2 + 1]) * s;
    }
    return out;
  }

  const field: CardField = {
    setCards(n, start, targets) {
      count = n;
      to = targets;
      from = start ?? targets;
      cardsMoving = start !== null && n > 0;
      moveStart = performance.now();
      duration = 1.1;
      planDelays(cardsMoving ? 0.45 : 0);
      upload(fromBuffer, from);
      upload(toBuffer, to);
      // a fresh set of cards has no groups yet; until it is told, every card is group 0
      const groups = new Uint16Array(n);
      upload(groupBuffer, groups);
      hover = -1;
      selected = -1;
      dirty = true;
      schedule();
    },
    moveTo(targets, stagger, seconds) {
      if (count === 0) return;
      from = currentPositions();
      to = targets;
      cardsMoving = true;
      moveStart = performance.now();
      duration = seconds;
      planDelays(stagger);
      upload(fromBuffer, from);
      upload(toBuffer, to);
      dirty = true;
      schedule();
    },
    positions: currentPositions,
    setGroups(assignment, colors) {
      upload(groupBuffer, assignment);
      paletteSize = Math.max(1, colors.length / 4);
      gl.bindTexture(gl.TEXTURE_2D, palette);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, paletteSize, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, colors);
      dirty = true;
      schedule();
    },
    setTheme(t) {
      theme = t;
      dirty = true;
      schedule();
    },
    setHover(i) {
      if (hover === i) return;
      hover = i;
      dirty = true;
      schedule();
    },
    setSelected(i) {
      if (selected === i) return;
      selected = i;
      dirty = true;
      schedule();
    },
    setHighlightGroup(g) {
      if (highlight === g) return;
      highlight = g;
      dirty = true;
      schedule();
    },
    pick(cssX, cssY) {
      if (count === 0) return -1;
      const px = Math.floor(cssX * dpr);
      const py = Math.floor(cssY * dpr);
      if (px < 0 || py < 0 || px >= width || py >= height) return -1;
      gl.bindFramebuffer(gl.FRAMEBUFFER, pickFramebuffer);
      // the viewport is the whole canvas, shifted so the pixel under the pointer is the one pixel
      // the framebuffer has; everything else is rasterized away
      gl.viewport(-px, -(height - 1 - py), width, height);
      gl.disable(gl.BLEND);
      gl.clearColor(1, 1, 1, 1);
      gl.clear(gl.COLOR_BUFFER_BIT);
      setCommonUniforms(performance.now(), true);
      draw();
      gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pickPixel);
      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
      const id = pickPixel[0] | (pickPixel[1] << 8) | (pickPixel[2] << 16);
      return id === 0xffffff || id >= count ? -1 : id;
    },
    fit(bounds, padding, animate) {
      const cssW = width / dpr;
      const cssH = height / dpr;
      const bw = Math.max(1e-6, bounds.x1 - bounds.x0);
      const bh = Math.max(1e-6, bounds.y1 - bounds.y0);
      const zoom = Math.min(maxZoom, Math.max(0.001, Math.min((cssW - 2 * padding) / bw, (cssH - 2 * padding) / bh)));
      minZoom = zoom * 0.2;
      target.x = (bounds.x0 + bounds.x1) / 2;
      target.y = (bounds.y0 + bounds.y1) / 2;
      target.zoom = zoom;
      flingV = [0, 0];
      if (!animate) {
        cur.x = target.x;
        cur.y = target.y;
        cur.zoom = target.zoom;
      }
      dirty = true;
      schedule();
    },
    zoomBy(factor, cssX, cssY) {
      const cssW = width / dpr;
      const cssH = height / dpr;
      const zoom = Math.min(maxZoom, Math.max(minZoom, target.zoom * factor));
      // the world point under the pointer stays under it
      const wx = target.x + (cssX - cssW / 2) / target.zoom;
      const wy = target.y + (cssY - cssH / 2) / target.zoom;
      target.x = wx - (cssX - cssW / 2) / zoom;
      target.y = wy - (cssY - cssH / 2) / zoom;
      target.zoom = zoom;
      flingV = [0, 0];
      schedule();
    },
    panBy(dx, dy) {
      cur.x -= dx / cur.zoom;
      cur.y -= dy / cur.zoom;
      target.x = cur.x;
      target.y = cur.y;
      target.zoom = cur.zoom;
      flingV = [0, 0];
      dirty = true;
      schedule();
    },
    fling(vx, vy) {
      flingV = Math.hypot(vx, vy) < 40 ? [0, 0] : [vx, vy];
      schedule();
    },
    worldToCss(x, y) {
      return [(x - cur.x) * cur.zoom + width / dpr / 2, (y - cur.y) * cur.zoom + height / dpr / 2];
    },
    camera: () => ({ ...cur }),
    moving: () => cardsMoving || cameraMoving(),
    onFrame(callback) {
      frameCallback = callback;
    },
    invalidate() {
      dirty = true;
      schedule();
    },
    resize() {
      const rect = canvas.getBoundingClientRect();
      dpr = window.devicePixelRatio || 1;
      width = Math.max(1, Math.round(rect.width * dpr));
      height = Math.max(1, Math.round(rect.height * dpr));
      if (canvas.width !== width || canvas.height !== height) {
        canvas.width = width;
        canvas.height = height;
      }
      dirty = true;
      schedule();
    },
    destroy() {
      destroyed = true;
      if (raf !== 0) cancelAnimationFrame(raf);
      raf = 0;
      gl.deleteBuffer(quad);
      gl.deleteBuffer(fromBuffer);
      gl.deleteBuffer(toBuffer);
      gl.deleteBuffer(delayBuffer);
      gl.deleteBuffer(groupBuffer);
      gl.deleteVertexArray(vao);
      gl.deleteTexture(palette);
      gl.deleteTexture(pickTexture);
      gl.deleteFramebuffer(pickFramebuffer);
      gl.deleteProgram(prog);
    },
  };
  return field;
}

function program(gl: WebGL2RenderingContext, vertex: string, fragment: string): WebGLProgram {
  const compile = (type: number, source: string) => {
    const shader = gl.createShader(type)!;
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
      const log = gl.getShaderInfoLog(shader);
      gl.deleteShader(shader);
      throw new Error("shader: " + log);
    }
    return shader;
  };
  const p = gl.createProgram()!;
  const v = compile(gl.VERTEX_SHADER, vertex);
  const f = compile(gl.FRAGMENT_SHADER, fragment);
  gl.attachShader(p, v);
  gl.attachShader(p, f);
  gl.linkProgram(p);
  gl.deleteShader(v);
  gl.deleteShader(f);
  if (!gl.getProgramParameter(p, gl.LINK_STATUS)) {
    const log = gl.getProgramInfoLog(p);
    gl.deleteProgram(p);
    throw new Error("program: " + log);
  }
  return p;
}
