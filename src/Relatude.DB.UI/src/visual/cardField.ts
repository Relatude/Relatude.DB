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
 *    uploads a new set of targets and the cards travel on their own, on an eased curve that starts
 *    from rest and settles with a hint of overshoot, each leaving a little after the one before it,
 *    so a change is a flow across the picture rather than a jump. When targets change mid-flight the
 *    current positions are captured once on the CPU (the same curve, evaluated per card) and become
 *    the new starting points, so nothing ever jumps.
 *  - Colour is a group index per card (two bytes) and a palette texture the shader reads it from.
 *    Colouring by another property is one small buffer upload, not a rewrite of the picture. The
 *    group index is also what a pulse addresses, so drawing attention to one value of a property
 *    costs two uniforms rather than a pass over the cards.
 *  - A card that was not on screen before grows out of nothing and fades in as it arrives, on its
 *    own place in the same staggered timeline, so a result that brings new cards washes in rather
 *    than appearing at once.
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
  /** the ring around the selected card */
  outline: RGBf;
  /**
   * The page's own text colour: what a card is mixed toward to stand out, under the pointer and
   * through a pulse. Being the text colour it is light on a dark page and dark on a light one, so
   * "stands out" is brighter or darker according to the theme rather than always one of them.
   */
  ink: RGBf;
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
   * the caller, which is what makes a filtered result flow out of the full one. `fresh` marks the
   * cards that were not on screen before, one byte each, and those fade in where they land; null
   * when none of them are new.
   */
  setCards(count: number, from: Float32Array | null, to: Float32Array, fresh?: Uint8Array | null): void;
  /** New targets for the same cards; they leave where they are now, staggered over `stagger` seconds and travelling for `duration` (the field's own pace unless given). */
  moveTo(to: Float32Array, stagger?: number, duration?: number): void;
  /** Where every card is right now, mid-flight or not; a fresh array. */
  positions(): Float32Array;
  /** The group of every card (uint16, an index into the palette) and the palette itself, rgba bytes per group. */
  setGroups(assignment: Uint16Array, palette: Uint8Array): void;
  setTheme(theme: FieldTheme): void;
  setHover(index: number): void;
  setSelected(index: number): void;
  /**
   * Draws the cards of one group in and lets them back out to the size they were, holding them
   * brighter (darker on a light page) while it lasts, so it can be seen where in the picture they
   * are; -1 stops it. A card too small to see the movement of still keeps a few pixels of it.
   */
  pulseGroup(group: number): void;
  /** The card under a css pixel of the canvas, or -1. */
  pick(cssX: number, cssY: number): number;
  /** Brings the bounds into view with a margin, gliding there over `seconds` (0 jumps). */
  fit(bounds: Bounds, paddingPx: number, seconds: number): void;
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

// The curve a card travels on. A quintic ease-in-out (6t⁵ - 15t⁴ + 10t³: no velocity and no
// acceleration at either end, so a move starts from a standstill and comes to rest, with its speed
// spread over the whole of the move rather than spent in a dash) plus a small bump, t³(1-t)², that
// carries the card a touch past its place around t ≈ 0.88 and brings it back by t = 1 - about one
// percent of the distance, felt as weight rather than seen as a bounce. Both terms are flat at t = 0
// and t = 1, so the overshoot never adds a kick at either end. The GLSL below is this function.
const overshoot = 2.55;
function ease(t: number): number {
  if (t <= 0) return 0;
  if (t >= 1) return 1;
  const t3 = t * t * t;
  const back = 1 - t;
  return t3 * (t * (t * 6 - 15) + 10) + overshoot * t3 * back * back;
}
/** the same quintic without the bump: what the camera glides on */
function smootherstep(t: number): number {
  if (t <= 0) return 0;
  if (t >= 1) return 1;
  return t * t * t * (t * (t * 6 - 15) + 10);
}

/** how long one move takes, in seconds, and over how many seconds the cards set off */
export const moveDuration = 1.6;
export const moveStagger = 0.6;
/** the whole of a transition: the last card sets off at the end of the stagger and travels the full move */
export const transitionSeconds = moveDuration + moveStagger;

/** how long a card takes to arrive where it lands, in seconds, and the size it starts out at */
const fadeSeconds = 0.75;
const bornScale = 0.35;
/**
 * The pulse: how long it lasts, how far into its cell a card is drawn, the least of that movement it
 * keeps whatever the scale, and how far toward the page's ink it is taken at the turn.
 */
export const pulseSeconds = 1.25;
const pulseAmount = 0.42;
const pulseMinPx = 2.5;
const pulseGlow = 0.45;

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
layout(location = 3) in vec2 aTiming;  // when this card sets off, and whether it is fading in
layout(location = 4) in uint aGroup;
uniform vec2 uCenter;
uniform float uZoom;      // device pixels per world unit
uniform vec2 uHalfSize;   // half the canvas, device pixels
uniform float uTime;      // seconds since the move began
uniform float uDuration;
uniform float uFill;
uniform float uFade;      // how long a newborn card takes to come up to full colour
uniform sampler2D uPalette;
uniform int uHover;
uniform int uSelected;
uniform vec3 uInk;
uniform int uPick;
uniform int uPulseGroup;  // the group pulsing right now, or -1
uniform float uPulseT;    // how far through its pulse that group is, 0..1
out vec2 vUv;
out vec4 vColor;
flat out float vHalfPx;
flat out int vFlags;
const float OVERSHOOT = ${overshoot.toFixed(4)};
const float PULSE = ${pulseAmount.toFixed(4)};
const float PULSE_MIN_PX = ${pulseMinPx.toFixed(4)};
const float PULSE_GLOW = ${pulseGlow.toFixed(4)};
const float BORN = ${bornScale.toFixed(4)};
float ease(float t) {
  if (t <= 0.0) return 0.0;
  if (t >= 1.0) return 1.0;
  float t3 = t * t * t;
  float back = 1.0 - t;
  return t3 * (t * (t * 6.0 - 15.0) + 10.0) + OVERSHOOT * t3 * back * back;
}
// One movement, inward and back: a card is drawn into its cell and let out again to the size it
// was, and no further. Squaring the half sine leaves the curve flat at both ends as well as at
// nothing, so the card sets off and comes to rest without a kick at either.
float pulse(float t) {
  if (t <= 0.0 || t >= 1.0) return 0.0;
  float s = sin(3.1415927 * t);
  return -s * s;
}
void main() {
  float t = (uTime - aTiming.x) / uDuration;
  vec2 pos = mix(aFrom, aTo, ease(t));
  // the gap between cards appears as they get room for it; a card a few pixels wide fills its cell
  float fill = mix(1.0, uFill, smoothstep(2.5, 7.0, uZoom));
  // a card that was not on screen before grows out of nothing into its place as it fades in
  float born = 1.0;
  if (aTiming.y > 0.5) {
    born = clamp((uTime - aTiming.x) / uFade, 0.0, 1.0);
    born = born * born * (3.0 - 2.0 * born);
  }
  float base = fill * (BORN + (1.0 - BORN) * born);
  // -1 at the turn of a pulse, 0 for every card outside the group pulsing
  float p = int(aGroup) == uPulseGroup ? pulse(uPulseT) : 0.0;
  float side = base * (1.0 + PULSE * p);
  // zoomed out to the whole set a card is a pixel or two, and taking a fraction off that is no
  // signal at all, so what is left of it is worth a couple of pixels of the screen
  side = max(side, min(base, 2.0 * PULSE_MIN_PX / uZoom));
  vec2 corner = (aCorner - 0.5) * side + 0.5;
  vec2 px = (pos + corner - uCenter) * uZoom;
  gl_Position = vec4(px.x / uHalfSize.x, -px.y / uHalfSize.y, 0.0, 1.0);
  vUv = aCorner;
  vHalfPx = 0.5 * side * uZoom;
  int id = gl_InstanceID;
  vFlags = id == uSelected ? 1 : 0;
  if (uPick == 1) {
    vColor = vec4(float(id & 255) / 255.0, float((id >> 8) & 255) / 255.0, float((id >> 16) & 255) / 255.0, 1.0);
    return;
  }
  vec4 c = texelFetch(uPalette, ivec2(int(aGroup), 0), 0);
  if (id == uHover) c.rgb = mix(c.rgb, uInk, 0.28);
  // through a pulse the group is held toward the page's ink as well as moved, so it stands out for
  // the whole of the movement - brighter on a dark page, darker on a light one
  c.rgb = mix(c.rgb, uInk, PULSE_GLOW * abs(p));
  // a new card comes up to its colour as it grows, so it arrives rather than appearing at once
  c.a *= born;
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
  const uFade = u("uFade");
  const uPalette = u("uPalette");
  const uHover = u("uHover");
  const uSelected = u("uSelected");
  const uInk = u("uInk");
  const uPick = u("uPick");
  const uOutline = u("uOutline");
  const uPulseGroup = u("uPulseGroup");
  const uPulseT = u("uPulseT");

  // the quad every card is an instance of, corner (0,0) to (1,1)
  const quad = gl.createBuffer()!;
  gl.bindBuffer(gl.ARRAY_BUFFER, quad);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([0, 0, 1, 0, 0, 1, 1, 1]), gl.STATIC_DRAW);
  const fromBuffer = gl.createBuffer()!;
  const toBuffer = gl.createBuffer()!;
  const timingBuffer = gl.createBuffer()!;
  const groupBuffer = gl.createBuffer()!;
  const vao = gl.createVertexArray()!;
  gl.bindVertexArray(vao);
  gl.bindBuffer(gl.ARRAY_BUFFER, quad);
  gl.enableVertexAttribArray(0);
  gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
  for (const [location, buffer, size] of [
    [1, fromBuffer, 2],
    [2, toBuffer, 2],
    [3, timingBuffer, 2],
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
  // two floats per card: when it sets off, and 1 when it is fading in where it lands
  let timing: Float32Array = new Float32Array(0);
  let moveStart = 0; // performance.now() when the current move began
  let duration = 1;
  let maxDelay = 0;
  let cardsMoving = false;
  let paletteSize = 1;
  let hover = -1;
  let selected = -1;
  let pulsedGroup = -1;
  let pulseStart = 0;
  let theme: FieldTheme = { clear: [1, 1, 1], outline: [0.04, 0.38, 0.7], ink: [0.1, 0.1, 0.1] };
  let width = 1;
  let height = 1;
  let dpr = 1;
  const cur: Camera = { x: 0, y: 0, zoom: 20 };
  const target: Camera = { x: 0, y: 0, zoom: 20 };
  // A fit glides on a timed curve (from, to, start, seconds) so it can keep pace with the cards: the
  // exponential chase below is right for a wheel step, which should answer at once, but wrong for a
  // layout change, where it would swing the whole picture in its first tenth of a second while the
  // cards had barely set off. Zoom glides in log space, so a two-fold zoom in feels like a two-fold zoom out.
  let glide: { fx: number; fy: number; fz: number; tx: number; ty: number; tz: number; start: number; seconds: number } | null = null;
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

  function pulsing(): boolean {
    return pulsedGroup >= 0;
  }

  function cameraMoving(): boolean {
    return (
      glide !== null ||
      Math.abs(target.x - cur.x) * cur.zoom > 0.05 ||
      Math.abs(target.y - cur.y) * cur.zoom > 0.05 ||
      Math.abs(Math.log(target.zoom / cur.zoom)) > 0.0005 ||
      flingV[0] !== 0 ||
      flingV[1] !== 0
    );
  }

  function stepCamera(dt: number, now: number) {
    if (glide !== null) {
      const p = smootherstep((now - glide.start) / 1000 / glide.seconds);
      cur.x = glide.fx + (glide.tx - glide.fx) * p;
      cur.y = glide.fy + (glide.ty - glide.fy) * p;
      cur.zoom = Math.exp(glide.fz + (glide.tz - glide.fz) * p);
      if (p >= 1) glide = null;
      return;
    }
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
    gl.uniform1f(uFade, fadeSeconds);
    gl.uniform1i(uHover, hover);
    gl.uniform1i(uSelected, selected);
    gl.uniform1i(uPulseGroup, pulsedGroup);
    gl.uniform1f(uPulseT, pulsing() ? (now - pulseStart) / 1000 / pulseSeconds : 1);
    gl.uniform3fv(uInk, theme.ink);
    gl.uniform3fv(uOutline, theme.outline);
    gl.uniform1i(uPick, pickMode ? 1 : 0);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, palette);
    gl.uniform1i(uPalette, 0);
  }

  // One pass, whatever is happening: a pulsing card is drawn into its own cell and never past it,
  // so it can never cover the card beside it and there is no order to get right.
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
    if (!dirty && !cardsMoving && !pulsing() && !cameraMoving()) {
      lastFrame = 0;
      return;
    }
    const dt = lastFrame === 0 ? 1 / 60 : Math.min(0.1, (now - lastFrame) / 1000);
    lastFrame = now;
    stepCamera(dt, now);
    if (cardsMoving && elapsed(now) > duration + maxDelay) cardsMoving = false;
    // ended before the uniforms are set, so the last frame of a pulse is the picture at rest
    if (pulsing() && (now - pulseStart) / 1000 >= pulseSeconds) pulsedGroup = -1;

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
    if (cardsMoving || pulsing() || cameraMoving()) schedule();
    else lastFrame = 0;
  }

  function upload(buffer: WebGLBuffer, data: ArrayBufferView) {
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.bufferData(gl.ARRAY_BUFFER, data, gl.DYNAMIC_DRAW);
  }

  // Every card leaves a little after the one before it, in result order, with a little jitter so
  // the wave has a soft front rather than a ruled edge. `fresh` marks the cards that fade in as
  // they arrive rather than being there already.
  function planTiming(stagger: number, fresh: Uint8Array | null) {
    if (timing.length !== count * 2) timing = new Float32Array(count * 2);
    let h = 0x9e3779b9;
    for (let i = 0; i < count; i++) {
      h = (h ^ (h << 13)) >>> 0;
      h = (h ^ (h >>> 17)) >>> 0;
      h = (h ^ (h << 5)) >>> 0;
      const jitter = (h & 0xffff) / 0xffff;
      timing[i * 2] = stagger * (0.72 * (i / Math.max(1, count - 1)) + 0.28 * jitter);
      timing[i * 2 + 1] = fresh !== null && fresh[i] !== 0 ? 1 : 0;
    }
    maxDelay = stagger;
    upload(timingBuffer, timing);
  }

  function currentPositions(): Float32Array {
    const out = new Float32Array(count * 2);
    if (!cardsMoving) {
      out.set(to.subarray(0, count * 2));
      return out;
    }
    const time = elapsed(performance.now());
    for (let i = 0; i < count; i++) {
      const s = ease((time - timing[i * 2]) / duration);
      out[i * 2] = from[i * 2] + (to[i * 2] - from[i * 2]) * s;
      out[i * 2 + 1] = from[i * 2 + 1] + (to[i * 2 + 1] - from[i * 2 + 1]) * s;
    }
    return out;
  }

  const field: CardField = {
    setCards(n, start, targets, fresh = null) {
      count = n;
      to = targets;
      from = start ?? targets;
      // the timeline runs for a move, for a fade, or for both: a card with nowhere to travel from
      // still has to come up to its colour
      cardsMoving = n > 0 && (start !== null || fresh !== null);
      moveStart = performance.now();
      duration = moveDuration;
      planTiming(cardsMoving ? moveStagger : 0, fresh);
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
    moveTo(targets, stagger = moveStagger, seconds = moveDuration) {
      if (count === 0) return;
      from = currentPositions();
      to = targets;
      cardsMoving = true;
      moveStart = performance.now();
      duration = seconds;
      planTiming(stagger, null); // the same cards, so none of them are new
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
    pulseGroup(group) {
      // clicked again while it is still going: it starts over, which is what a second click means
      pulsedGroup = group;
      pulseStart = performance.now();
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
    fit(bounds, padding, seconds) {
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
      if (seconds > 0) {
        glide = { fx: cur.x, fy: cur.y, fz: Math.log(cur.zoom), tx: target.x, ty: target.y, tz: Math.log(zoom), start: performance.now(), seconds };
      } else {
        glide = null;
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
      glide = null; // a hand on the wheel takes the camera back
      schedule();
    },
    panBy(dx, dy) {
      cur.x -= dx / cur.zoom;
      cur.y -= dy / cur.zoom;
      target.x = cur.x;
      target.y = cur.y;
      target.zoom = cur.zoom;
      flingV = [0, 0];
      glide = null;
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
    moving: () => cardsMoving || pulsing() || cameraMoving(),
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
      gl.deleteBuffer(timingBuffer);
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
