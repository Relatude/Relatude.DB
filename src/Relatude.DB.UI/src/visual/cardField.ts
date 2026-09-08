import type { Bounds } from "./layouts";
import { shapeCount, shapeField, shapeFieldSize, shapeVariants } from "./shapes";

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
 *  - Shape works the same way and is the second thing a card can say without being read: another
 *    two bytes say which silhouette it is cut out to, and the silhouettes are distance fields in an
 *    array texture (see shapes.ts), so a card is a heart or a crown for one texture read and keeps a
 *    clean edge at any size. A card told nothing is the plain rounded card it has always been, and
 *    the shader never looks at the texture - a picture with no shape property costs what it did.
 *  - A card that was not on screen before grows out of nothing and fades in as it arrives, on its
 *    own place in the same staggered timeline, so a result that brings new cards washes in rather
 *    than appearing at once.
 *  - Picking is done by the GPU: a click renders the ids as colours into a single pixel under the
 *    pointer. It is exact for whatever is on the screen, including cards half way through a move,
 *    and costs one extra draw on a click rather than a spatial index for every card.
 *  - Pictures. A card wide enough to be read (detailCssPx) shows a picture over its upper part and
 *    keeps its colour as a strip along the bottom, where its name goes (drawn in html over the
 *    canvas, not here). The pictures live in one array texture per resolution level, a layer per
 *    picture, and each card carries three words saying which layer of which level it draws, which one
 *    it is fading from, and when the current one arrived. Who gets a layer, and when, is decided
 *    outside (cardMedia.ts); this only draws what it is handed, and only the words of the cards that
 *    changed are re-uploaded. A card told it has no picture draws a placeholder glyph instead, in the
 *    shader, so it is crisp at any zoom.
 *
 * The camera is two-dimensional (a centre and a zoom) and eases toward its targets every frame, so a
 * wheel step or a fit is smooth. Cards are one world unit apart; a card's own size is a fraction of
 * that, and the fraction goes to one when a card is only a few pixels wide, so a picture zoomed out
 * to the whole set is a solid mosaic rather than a screen full of dust.
 *
 * Nothing here knows what a card stands for. The cards are a count, and a card is told apart from
 * its neighbours by its index alone.
 */

export type RGBf = [number, number, number];

export interface FieldTheme {
  /** what the frame is cleared to: the panel behind the picture */
  clear: RGBf;
  /** the ring around the selected card */
  outline: RGBf;
  /**
   * The page's own text colour: what a card is mixed toward to stand out under the pointer, and the
   * ink of the placeholder glyph. Being the text colour it is light on a dark page and dark on a
   * light one, so "stands out" is brighter or darker according to the theme rather than always one.
   */
  ink: RGBf;
}

/** A pulse in progress: the group being pulsed or the silhouette slot (the other is -1), and how far into the page those cards are faded at this moment. */
export interface PulseFade {
  group: number;
  shape: number;
  amount: number;
}

export interface Camera {
  x: number;
  y: number;
  /**
   * Where the camera looks along the depth axis, in world units; 0 for the flat field, which has no
   * such axis. What reads it is the picture supply (cardMedia.ts), to prefer the rows in front.
   */
  z?: number;
  /** css pixels per world unit */
  zoom: number;
}

/** what a card draws over its upper part: nothing known yet (the plain colour), a picture (or the colour until it arrives), or the placeholder glyph for a node without one */
export const CardKind = { Flat: 0, Image: 1, Placeholder: 2 } as const;
export type CardKind = (typeof CardKind)[keyof typeof CardKind];

/** A tile on screen: whose card, which texture slot holds it, the part of the picture it shows (x0, y0, x1, y1 as fractions) and when it arrived. */
export interface TileOnScreen {
  card: number;
  slot: number;
  rect: ArrayLike<number>;
  arrivalMs: number;
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
  /** Where the given cards are right now, two floats each into `out`, in the order given. */
  positionsOf(indexes: ArrayLike<number>, count: number, out: Float32Array): void;
  /** The group of every card (uint16, an index into the palette) and the palette itself, rgba bytes per group. */
  setGroups(assignment: Uint16Array, palette: Uint8Array): void;
  /**
   * The silhouette of every card (uint16, a slot of shapes.ts; 0 is the plain card), or null for a
   * picture of plain cards. The fields of the silhouettes actually used are built here, the first
   * time they are asked for.
   */
  setShapes(assignment: Uint16Array | null): void;
  setTheme(theme: FieldTheme): void;
  setHover(index: number): void;
  setSelected(index: number): void;
  /**
   * Fades the cards of one group into the page and back, twice, so it can be seen where in the
   * picture they are; -1 stops it. Nothing moves and nothing is laid over the picture, so a card of
   * one pixel blinks as plainly as one that fills the screen, and the frame costs what it did.
   */
  pulseGroup(group: number): void;
  /** The same, for the cards cut out to one silhouette; -1 stops it. */
  pulseShape(slot: number): void;
  /** The pulse going on right now, or null; for the labels, which are drawn over the canvas and fade with the cards they name. */
  pulseFade(): PulseFade | null;
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
  /** A point of the picture in css pixels of the canvas; `z` is the depth axis, which the flat field has none of and ignores. */
  worldToCss(x: number, y: number, z?: number): [number, number];
  camera(): Camera;
  /** Where the camera is going: the end of a glide or of a wheel step, the camera itself when it is still. */
  cameraTarget(): Camera;
  /** The canvas in css pixels, and the device pixel ratio it is drawn at. */
  size(): { width: number; height: number; dpr: number };
  /** Whether cards or the camera are moving. */
  moving(): boolean;
  /** Called after every frame drawn, for whatever is laid over the canvas in html (labels). */
  onFrame(callback: (() => void) | null): void;
  /** Asks for a frame; cheap to call often. */
  invalidate(): void;
  /** Reads the canvas' css size again and resizes the drawing buffer to it. */
  resize(): void;

  // ---- pictures ----

  /** Makes room for `layers` pictures at a level (an index into imageLevels); nothing if it has room already. */
  ensureImageLevel(level: number, layers: number): void;
  /** How many layers a level has room for; 0 before ensureImageLevel. */
  imageLayers(level: number): number;
  /** Puts a picture into a layer of a level. The bitmap is expected at the level's own size. */
  uploadImage(level: number, layer: number, image: ImageBitmap): void;
  /**
   * What a card draws over its upper part: a kind, the level and layer of its picture (-1 for
   * none yet), the level and layer it is fading from (-1 for none), and when the current one
   * arrived, on the performance.now() clock. Cheap: the words are uploaded with the next frame.
   */
  setCardImage(index: number, kind: CardKind, level: number, layer: number, fromLevel: number, fromLayer: number, arrivalMs: number): void;
  /**
   * The tiles on screen, in the order they are laid over the picture - coarsest first, so a sharper
   * tile covers the one it replaces while it fades in. At most tileSlots of them.
   */
  setTiles(tiles: ReadonlyArray<TileOnScreen>): void;
  /** Puts a tile's picture into a texture slot, which takes the bitmap's size. */
  uploadTile(slot: number, image: ImageBitmap): void;
  /** Lets go of every tile texture and clears the tiles on screen. */
  freeTiles(): void;
  destroy(): void;
}

/**
 * What the pictures (cardMedia.ts) and the names (cardLabels.ts) need of the field they follow.
 * Both renderers - this one and the three-dimensional one in cardField3d.ts - provide it, so the
 * machinery that keeps the cards in view supplied with their pictures is written once and knows
 * nothing about which of the two is drawing.
 */
export type FieldSurface = Pick<
  CardField,
  "positionsOf" | "pulseFade" | "worldToCss" | "camera" | "cameraTarget" | "size" | "invalidate" | "ensureImageLevel" | "imageLayers" | "uploadImage" | "setCardImage" | "setTiles" | "uploadTile" | "freeTiles"
>;

/**
 * What the view drives a field with, whichever of the two it is: everything but the flat camera
 * (a wheel step, a drag) and the three-dimensional one (an orbit), which the view works differently.
 */
export type CardFieldCommon = Pick<
  CardField,
  | "setCards"
  | "moveTo"
  | "positions"
  | "positionsOf"
  | "setGroups"
  | "setShapes"
  | "setTheme"
  | "setHover"
  | "setSelected"
  | "pulseGroup"
  | "pulseShape"
  | "pulseFade"
  | "pick"
  | "fit"
  | "size"
  | "moving"
  | "onFrame"
  | "invalidate"
  | "resize"
  | "ensureImageLevel"
  | "imageLayers"
  | "uploadImage"
  | "setCardImage"
  | "setTiles"
  | "uploadTile"
  | "freeTiles"
  | "destroy"
>;

// The curve a card travels on. A quintic ease-in-out (6t⁵ - 15t⁴ + 10t³: no velocity and no
// acceleration at either end, so a move starts from a standstill and comes to rest, with its speed
// spread over the whole of the move rather than spent in a dash) plus a small bump, t³(1-t)², that
// carries the card a touch past its place around t ≈ 0.88 and brings it back by t = 1 - about one
// percent of the distance, felt as weight rather than seen as a bounce. Both terms are flat at t = 0
// and t = 1, so the overshoot never adds a kick at either end. The GLSL below is this function.
export const overshoot = 2.55;
export function ease(t: number): number {
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
export const fadeSeconds = 0.75;
export const bornScale = 0.35;
/**
 * The pulse: the cards addressed fade into the page and back, twice. How long the two blinks take
 * together, and how far into the page a card is taken at the turn of one - not the whole way, so a
 * ghost of the group is left where it is rather than a hole in the picture.
 */
export const pulseSeconds = 0.8;
export const pulseFadeDepth = 0.9;

/** how much of the pitch a card fills when there is room to see the gap */
export const cardFill = 0.84;
/** the least a card can be zoomed to, in css px per unit; the most is set by the canvas (see maxZoomFor) */
const zoomLimitFloor = 640;
const cameraRate = 11; // per second: how fast the camera closes on its target
const flingDecay = 4.2; // per second

// ---- pictures ----

/**
 * The widths a picture comes in, each twice the one before; the server makes them (cardImageLevels
 * there must agree). A card is handed the smallest level at least as wide as it is on the screen,
 * so the GPU never shrinks a picture by more than half - fine without mipmaps - and stretches the
 * last level as far as the zoom goes.
 */
export const imageLevels = [64, 128, 256, 512, 1024, 2048] as const;
/**
 * Past the largest level a card zoomed into shows tiles: parts of its picture, cut out by the server
 * at the width of the canvas (tileWidths, the browser picks), each laid over the part of the picture
 * it shows. This many can be on screen at once - a card wider than the canvas is at most four tiles
 * of it, and the ones a step coarser stay under the sharper ones while those arrive.
 */
export const tileSlots = 4;
export const tileWidths = [1024, 2048, 4096] as const;
/** a picture is this much of the card's height; the strip below it keeps the card's colour and carries the name */
export const imageShare = 0.75;
/**
 * How wide a card is, in css px, when its picture appears (it fades in over ±10% of this). Small on
 * purpose: a thumbnail this size is still recognizable as what it is, and a screen of them is the
 * picture the visual pivot is for. It sets how many layers the smallest level needs (see
 * cardMedia's capacityFor), so lowering it costs texture memory rather than frame time.
 */
export const detailCssPx = 25;
/** how long a picture takes to come up, or to take over from the level before it, in ms */
export const imageFadeMs = 320;
/**
 * The most layers any level is ever given: what bounds the GPU memory, together with the sizes in
 * imageLevels. The smallest level wants one layer per card on screen and a screen holds thousands
 * of the smallest cards, so this is as high as WebGL 2 allows - and clamped to what the driver
 * actually offers, which is 256 in the worst case the standard permits.
 */
const maxLayersWanted = 2048;

const vertexSource = `#version 300 es
precision highp float;
precision highp int;
layout(location = 0) in vec2 aCorner;
layout(location = 1) in vec2 aFrom;
layout(location = 2) in vec2 aTo;
layout(location = 3) in vec2 aTiming;  // when this card sets off, and whether it is fading in
layout(location = 4) in uint aGroup;
layout(location = 5) in uvec3 aTex;    // the picture words, see setCardImage
layout(location = 6) in uint aShape;   // which silhouette this card is cut out to, see shapes.ts
uniform vec2 uCenterHi;   // the camera's centre, split into a whole part and a fraction so that at
uniform vec2 uCenterLo;   // a deep zoom the subtraction from an integer cell is exact in float32
uniform float uZoom;      // device pixels per world unit
uniform vec2 uHalfSize;   // half the canvas, device pixels
uniform float uTime;      // seconds since the move began
uniform float uDuration;
uniform float uFill;
uniform float uFade;      // how long a newborn card takes to come up to full colour
uniform float uDetailPx;  // device pixels a card is wide when its picture and name appear
uniform sampler2D uPalette;
uniform int uHover;
uniform int uSelected;
uniform vec3 uInk;
uniform int uPick;
uniform int uPulseGroup;  // the group pulsing right now, or -1
uniform int uPulseShape;  // the silhouette pulsing right now, or -1
uniform float uPulseT;    // how far through its pulse that group is, 0..1
uniform int uShaped;      // 1 when the cards have silhouettes of their own
uniform vec2 uShapeTurn[${shapeVariants.length}];  // what each variation turns a shape by (cos, sin)
uniform float uShapeInvScale[${shapeVariants.length}]; // and one over how much it scales it
uniform int uTileCard[${tileSlots}]; // the card each tile slot belongs to, -1 for none
out vec2 vUv;
out vec4 vColor;
flat out float vHalfPx;
flat out int vFlags;
flat out uvec3 vTex;
flat out float vDetail;
flat out float vFade;     // how far into the page this card is faded by a pulse, 0 at rest
flat out int vTileMask;
flat out vec4 vShape;     // the turn (cos, sin), one over the scale, and the layer of the field; w < 0 is a plain card
flat out float vShapeMix; // how much of the silhouette is cut out, see below
const int TILE_SLOTS = ${tileSlots};
const int VARIANTS = ${shapeVariants.length};
const float OVERSHOOT = ${overshoot.toFixed(4)};
const float PULSE_FADE = ${pulseFadeDepth.toFixed(4)};
const float BORN = ${bornScale.toFixed(4)};
float ease(float t) {
  if (t <= 0.0) return 0.0;
  if (t >= 1.0) return 1.0;
  float t3 = t * t * t;
  float back = 1.0 - t;
  return t3 * (t * (t * 6.0 - 15.0) + 10.0) + OVERSHOOT * t3 * back * back;
}
// Two blinks: out into the page and back, twice. The squared whole sine is flat where it leaves,
// where it comes back between the two, and where it ends, so there is no kick anywhere in the fade -
// and it is one sine, which is what lets every card work its own fade out in the vertex shader.
float pulse(float t) {
  if (t <= 0.0 || t >= 1.0) return 0.0;
  float s = sin(6.2831853 * t);
  return s * s;
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
  float side = fill * (BORN + (1.0 - BORN) * born);
  // A pulse fades the cards addressed into the page - by their colour or by their silhouette,
  // whichever legend was clicked - and leaves every other card alone. Nothing moves and nothing is
  // drawn over the picture, so a pulse never disturbs what is being looked at, and a card of a
  // pixel blinks as clearly as one that fills the screen: a fade does not depend on the size.
  int slot = int(aShape);
  bool pulsed = int(aGroup) == uPulseGroup || slot == uPulseShape;
  vFade = pulsed ? PULSE_FADE * pulse(uPulseT) : 0.0;
  vec2 corner = (aCorner - 0.5) * side + 0.5;
  vec2 px = ((pos - uCenterHi) - uCenterLo + corner) * uZoom;
  gl_Position = vec4(px.x / uHalfSize.x, -px.y / uHalfSize.y, 0.0, 1.0);
  vUv = aCorner;
  vHalfPx = 0.5 * side * uZoom;
  vTex = aTex;
  // the picture and the name come in as the card passes the width they are readable at, measured on
  // the card at its full size so that one still growing into place does not flicker them
  vDetail = smoothstep(uDetailPx * 0.9, uDetailPx * 1.1, fill * uZoom);
  // The silhouette, if this card has one. A card a couple of pixels across has no room to show a
  // shape and a screen of them is meant to read as a solid mosaic, so the shape comes in with the
  // gap between the cards, on the same measure: below it the card is the plain rounded square.
  if (uShaped == 1 && slot > 0) {
    // the slot is the shape in its low byte and the variation in its high one (see shapes.ts), and
    // what the variation does to the shape is two uniforms rather than anything worked out here
    int variant = clamp(slot >> 8, 0, VARIANTS - 1);
    vShape = vec4(uShapeTurn[variant], uShapeInvScale[variant], float(slot & 255));
    vShapeMix = smoothstep(2.5, 7.0, uZoom);
  } else {
    vShape = vec4(1.0, 0.0, 1.0, -1.0);
    vShapeMix = 0.0;
  }
  int id = gl_InstanceID;
  vFlags = id == uSelected ? 1 : 0;
  int mask = 0;
  for (int s = 0; s < TILE_SLOTS; s++) if (uTileCard[s] == id) mask |= (1 << s);
  vTileMask = mask;
  if (uPick == 1) {
    vColor = vec4(float(id & 255) / 255.0, float((id >> 8) & 255) / 255.0, float((id >> 16) & 255) / 255.0, 1.0);
    return;
  }
  vec4 c = texelFetch(uPalette, ivec2(int(aGroup), 0), 0);
  if (id == uHover) c.rgb = mix(c.rgb, uInk, 0.28);
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
flat in uvec3 vTex;
flat in float vDetail;
flat in float vFade;
flat in int vTileMask;
flat in vec4 vShape;
flat in float vShapeMix;
uniform highp sampler2DArray uShapeAtlas; // one layer per shape: how far every point of a card is from its outline
uniform int uPick;
uniform vec3 uOutline;
uniform vec3 uInk;
uniform vec3 uClear;
uniform float uNowMs;
uniform mediump sampler2DArray uLevel0;
uniform mediump sampler2DArray uLevel1;
uniform mediump sampler2DArray uLevel2;
uniform mediump sampler2DArray uLevel3;
uniform mediump sampler2DArray uLevel4;
uniform mediump sampler2DArray uLevel5;
uniform mediump sampler2D uTile0;
uniform mediump sampler2D uTile1;
uniform mediump sampler2D uTile2;
uniform mediump sampler2D uTile3;
uniform vec4 uTileRect[${tileSlots}];  // the part of the picture each tile shows: x0, y0, x1, y1
uniform float uTileTime[${tileSlots}]; // when each tile arrived
out vec4 outColor;
const float SHARE = ${imageShare.toFixed(4)};
const float FADE_MS = ${imageFadeMs.toFixed(1)};
// samplers can only be indexed by a constant, so the level is a switch
vec3 sampleLevel(int level, vec3 uv) {
  switch (level) {
    case 0: return texture(uLevel0, uv).rgb;
    case 1: return texture(uLevel1, uv).rgb;
    case 2: return texture(uLevel2, uv).rgb;
    case 3: return texture(uLevel3, uv).rgb;
    case 4: return texture(uLevel4, uv).rgb;
    default: return texture(uLevel5, uv).rgb;
  }
}
// a tile laid over the picture: inside its part, the tile takes over as it fades in
vec3 laidOver(vec3 pic, vec2 uv, vec4 r, float since, vec3 tile) {
  if (uv.x < r.x || uv.y < r.y || uv.x > r.z || uv.y > r.w) return pic;
  return mix(pic, tile, clamp((uNowMs - since) / FADE_MS, 0.0, 1.0));
}
vec2 tileUv(vec2 uv, vec4 r) {
  return (uv - r.xy) / max(r.zw - r.xy, vec2(1e-6));
}
// signed distance to a triangle (Inigo Quilez)
float sdTriangle(vec2 p, vec2 p0, vec2 p1, vec2 p2) {
  vec2 e0 = p1 - p0, e1 = p2 - p1, e2 = p0 - p2;
  vec2 v0 = p - p0, v1 = p - p1, v2 = p - p2;
  vec2 pq0 = v0 - e0 * clamp(dot(v0, e0) / dot(e0, e0), 0.0, 1.0);
  vec2 pq1 = v1 - e1 * clamp(dot(v1, e1) / dot(e1, e1), 0.0, 1.0);
  vec2 pq2 = v2 - e2 * clamp(dot(v2, e2) / dot(e2, e2), 0.0, 1.0);
  float s = sign(e0.x * e2.y - e0.y * e2.x);
  vec2 d = min(min(vec2(dot(pq0, pq0), s * (v0.x * e0.y - v0.y * e0.x)),
                   vec2(dot(pq1, pq1), s * (v1.x * e1.y - v1.y * e1.x))),
                   vec2(dot(pq2, pq2), s * (v2.x * e2.y - v2.y * e2.x)));
  return -sqrt(d.x) * sign(d.y);
}
// The placeholder: a sun and two hills, the way a missing picture is drawn everywhere, in a tone of
// the card's own colour on a ground a little toward the panel. q spans 0..4/3 by 0..1 so the shapes
// keep their proportions whatever the card's size; aa is a device pixel and a half in those units.
vec3 placeholder(vec3 c, vec2 uv, float aa) {
  vec3 ground = mix(c, uClear, 0.35);
  vec3 glyph = mix(c, uInk, 0.42);
  vec2 q = vec2(uv.x * 1.3333, uv.y);
  float sun = length(q - vec2(1.02, 0.30)) - 0.12;
  float hill1 = sdTriangle(q, vec2(0.06, 1.0), vec2(0.90, 1.0), vec2(0.48, 0.42));
  float hill2 = sdTriangle(q, vec2(0.62, 1.0), vec2(1.34, 1.0), vec2(0.99, 0.60));
  float d = min(sun, min(hill1, hill2));
  return mix(ground, glyph, 1.0 - smoothstep(-aa, aa, d));
}
void main() {
  // signed distance to the edge of a rounded rectangle, in device pixels
  vec2 p = (vUv - 0.5) * 2.0 * vHalfPx;
  float r = clamp(vHalfPx * 0.16, 0.0, 6.0);
  vec2 q = abs(p) - (vHalfPx - r);
  float d = length(max(q, 0.0)) - r;
  // A card with a silhouette is cut out to it instead: the card's own square is turned and scaled
  // into the shape's box and the distance to the outline read from the field there. The field is
  // kept in fractions of the card, so it becomes pixels the same way the rectangle just did, and
  // the edge is one pixel wide whether the card is twenty pixels across or two thousand. Blending
  // the two distances is what lets the shape grow out of the square as the cards get room for it.
  if (vShapeMix > 0.002) {
    vec2 c = (vUv - 0.5) * vShape.z;
    vec2 uv = vec2(c.x * vShape.x + c.y * vShape.y, c.y * vShape.x - c.x * vShape.y) + 0.5;
    float ds = texture(uShapeAtlas, vec3(uv, vShape.w)).r;
    // past the shape's own box nothing was measured; what is there is the distance to the box
    vec2 e = max(abs(uv - 0.5) - 0.5, 0.0);
    ds = max(ds, length(e)) / vShape.z;
    d = mix(d, ds * 2.0 * vHalfPx, vShapeMix);
  }
  if (uPick == 1) {
    if (d > 0.0) discard;
    outColor = vColor;
    return;
  }
  // one pixel of anti-aliasing on the rounded edge; a tiny card is left to the multisampling
  float alpha = vHalfPx > 2.5 ? clamp(0.5 - d, 0.0, 1.0) : 1.0;
  vec3 c = vColor.rgb;
  // the upper part of a card wide enough to be read is its picture; the strip below stays the colour
  if (vDetail > 0.001 && vUv.y < SHARE) {
    vec2 uv = vec2(vUv.x, vUv.y / SHARE);
    uint w0 = vTex.x;
    int kind = int((w0 >> 24u) & 3u);
    vec3 pic = c;
    float show = 0.0;
    if (kind == 1) {
      int levelA = int((w0 >> 16u) & 15u) - 1;
      if (levelA >= 0) {
        int levelB = int((w0 >> 20u) & 15u) - 1;
        float fade = clamp((uNowMs - float(vTex.z)) / FADE_MS, 0.0, 1.0);
        vec3 a = sampleLevel(levelA, vec3(uv, float(w0 & 0xFFFFu)));
        if (levelB >= 0) {
          // the sharper level takes over from the one before it, which stays underneath
          pic = mix(sampleLevel(levelB, vec3(uv, float(vTex.y & 0xFFFFu))), a, fade);
          show = 1.0;
        } else {
          pic = a;
          show = fade;
        }
      }
      // zoomed past the largest level: the tiles of the part in view, coarsest first
      if (vTileMask != 0) {
        if ((vTileMask & 1) != 0) pic = laidOver(pic, uv, uTileRect[0], uTileTime[0], texture(uTile0, tileUv(uv, uTileRect[0])).rgb);
        if ((vTileMask & 2) != 0) pic = laidOver(pic, uv, uTileRect[1], uTileTime[1], texture(uTile1, tileUv(uv, uTileRect[1])).rgb);
        if ((vTileMask & 4) != 0) pic = laidOver(pic, uv, uTileRect[2], uTileTime[2], texture(uTile2, tileUv(uv, uTileRect[2])).rgb);
        if ((vTileMask & 8) != 0) pic = laidOver(pic, uv, uTileRect[3], uTileTime[3], texture(uTile3, tileUv(uv, uTileRect[3])).rgb);
        show = 1.0;
      }
    } else if (kind == 2) {
      pic = placeholder(c, uv, 1.5 / max(1.0, 2.0 * vHalfPx * SHARE));
      show = 1.0;
    }
    c = mix(c, pic, vDetail * show);
  }
  if (vFlags == 1 && vHalfPx > 4.0) c = mix(c, uOutline, clamp(d + 2.5, 0.0, 1.0));
  // the pulse, last of all: the whole card - picture, name strip and edge - is taken into the page
  // by the blend, which is a multiply here rather than anything drawn over the picture
  outColor = vec4(c, alpha * vColor.a * (1.0 - vFade));
}`;

export function createCardField(canvas: HTMLCanvasElement): CardField | null {
  const context = canvas.getContext("webgl2", { antialias: true, alpha: false, premultipliedAlpha: false, powerPreference: "high-performance" });
  if (!context) return null;
  const gl: WebGL2RenderingContext = context;

  const maxLayers = Math.max(1, Math.min(maxLayersWanted, gl.getParameter(gl.MAX_ARRAY_TEXTURE_LAYERS) as number));

  const prog = program(gl, vertexSource, fragmentSource);
  const u = (name: string) => gl.getUniformLocation(prog, name);
  const uCenterHi = u("uCenterHi");
  const uCenterLo = u("uCenterLo");
  const uZoom = u("uZoom");
  const uHalfSize = u("uHalfSize");
  const uTime = u("uTime");
  const uDuration = u("uDuration");
  const uFill = u("uFill");
  const uFade = u("uFade");
  const uDetailPx = u("uDetailPx");
  const uPalette = u("uPalette");
  const uHover = u("uHover");
  const uSelected = u("uSelected");
  const uInk = u("uInk");
  const uClear = u("uClear");
  const uNowMs = u("uNowMs");
  const uPick = u("uPick");
  const uOutline = u("uOutline");
  const uPulseGroup = u("uPulseGroup");
  const uPulseShape = u("uPulseShape");
  const uPulseT = u("uPulseT");
  const uShaped = u("uShaped");
  const uShapeAtlas = u("uShapeAtlas");
  const uLevels = imageLevels.map((_, i) => u("uLevel" + i));
  const uTiles = Array.from({ length: tileSlots }, (_, i) => u("uTile" + i));
  const uTileCard = u("uTileCard");
  const uTileRect = u("uTileRect");
  const uTileTime = u("uTileTime");
  // what a variation of a shape does to it: the same for every card, so it is said once
  gl.useProgram(prog);
  gl.uniform2fv(u("uShapeTurn"), shapeVariants.flatMap((v) => [Math.cos(v.angle), Math.sin(v.angle)]));
  gl.uniform1fv(u("uShapeInvScale"), shapeVariants.map((v) => 1 / v.scale));

  // the quad every card is an instance of, corner (0,0) to (1,1)
  const quad = gl.createBuffer()!;
  gl.bindBuffer(gl.ARRAY_BUFFER, quad);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([0, 0, 1, 0, 0, 1, 1, 1]), gl.STATIC_DRAW);
  const fromBuffer = gl.createBuffer()!;
  const toBuffer = gl.createBuffer()!;
  const timingBuffer = gl.createBuffer()!;
  const groupBuffer = gl.createBuffer()!;
  const texBuffer = gl.createBuffer()!;
  const shapeBuffer = gl.createBuffer()!;
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
  gl.bindBuffer(gl.ARRAY_BUFFER, texBuffer);
  gl.enableVertexAttribArray(5);
  gl.vertexAttribIPointer(5, 3, gl.UNSIGNED_INT, 0, 0);
  gl.vertexAttribDivisor(5, 1);
  gl.bindBuffer(gl.ARRAY_BUFFER, shapeBuffer);
  gl.enableVertexAttribArray(6);
  gl.vertexAttribIPointer(6, 1, gl.UNSIGNED_SHORT, 0, 0);
  gl.vertexAttribDivisor(6, 1);
  gl.bindVertexArray(null);

  // the palette: one texel per group
  const palette = gl.createTexture()!;
  gl.bindTexture(gl.TEXTURE_2D, palette);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([128, 128, 128, 255]));

  // The picture levels: an array texture each, made when first asked for. Until then a sampler is
  // bound to one grey layer, so every sampler is complete whatever the shader may branch to.
  const emptyLevel = gl.createTexture()!;
  gl.bindTexture(gl.TEXTURE_2D_ARRAY, emptyLevel);
  gl.texStorage3D(gl.TEXTURE_2D_ARRAY, 1, gl.RGBA8, 1, 1, 1);
  gl.texSubImage3D(gl.TEXTURE_2D_ARRAY, 0, 0, 0, 0, 1, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([128, 128, 128, 255]));
  arrayTextureParameters(gl);
  const levelTextures: (WebGLTexture | null)[] = imageLevels.map(() => null);
  const levelLayers: number[] = imageLevels.map(() => 0);
  // The tiles: a plain texture per slot, made at the size of the first picture put in it, and a
  // grey pixel for a slot with none. Which slot is laid where is told per frame (setTiles), so
  // the textures are bound in that order rather than by slot.
  const emptyTile = gl.createTexture()!;
  gl.bindTexture(gl.TEXTURE_2D, emptyTile);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([128, 128, 128, 255]));
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
  const tileTextures: (WebGLTexture | null)[] = Array.from({ length: tileSlots }, () => null);
  const tileOrder = new Int32Array(tileSlots).fill(-1); // the slot drawn at each place, -1 for none
  const tileCards = new Int32Array(tileSlots).fill(-1);
  const tileRects = new Float32Array(tileSlots * 4);
  const tileTimes = new Float32Array(tileSlots);
  // a bitmap that is not the size of its level is drawn onto this first, so a layer is always whole
  let resizeCanvas: HTMLCanvasElement | null = null;

  // The silhouettes: a layer per shape, each holding how far every point of a card is from that
  // shape's outline. Made when a picture first asks for a shape, and a layer filled in when a shape
  // is first seen, so a database nobody groups by shape pays nothing for any of it.
  let shapeAtlas: WebGLTexture | null = null;
  const shapeFilled = new Uint8Array(shapeCount);

  function ensureShape(shape: number) {
    if (shapeFilled[shape] === 1) return;
    if (shapeAtlas === null) {
      shapeAtlas = gl.createTexture()!;
      gl.bindTexture(gl.TEXTURE_2D_ARRAY, shapeAtlas);
      gl.texStorage3D(gl.TEXTURE_2D_ARRAY, 1, gl.R16F, shapeFieldSize, shapeFieldSize, shapeCount);
      arrayTextureParameters(gl);
    } else {
      gl.bindTexture(gl.TEXTURE_2D_ARRAY, shapeAtlas);
    }
    gl.texSubImage3D(gl.TEXTURE_2D_ARRAY, 0, 0, 0, shape, shapeFieldSize, shapeFieldSize, 1, gl.RED, gl.FLOAT, shapeField(shape));
    gl.bindTexture(gl.TEXTURE_2D_ARRAY, null);
    shapeFilled[shape] = 1;
  }

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
  // three words per card, see setCardImage; only the cards in `texDirty` are re-uploaded
  let texWords: Uint32Array = new Uint32Array(0);
  const texDirty = new Set<number>();
  let fadeUntil = 0; // performance.now() when the last picture fade ends: frames are drawn until then
  let moveStart = 0; // performance.now() when the current move began
  let duration = 1;
  let maxDelay = 0;
  let cardsMoving = false;
  let paletteSize = 1;
  let hover = -1;
  let selected = -1;
  let pulsedGroup = -1;
  let pulsedShape = -1;
  let pulseStart = 0;
  let shaped = false;
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
  let frameNow = 0; // the clock the last frame was drawn on: what the labels over the canvas share
  let dirty = true;
  let destroyed = false;

  function schedule() {
    if (raf === 0 && !destroyed) raf = requestAnimationFrame(frame);
  }

  function elapsed(now: number): number {
    return (now - moveStart) / 1000;
  }

  function pulsing(): boolean {
    return pulsedGroup >= 0 || pulsedShape >= 0;
  }

  /** the shader's pulse curve, for the labels: two blinks, flat at both ends and in the middle */
  function pulseAmountAt(now: number): number {
    const t = (now - pulseStart) / 1000 / pulseSeconds;
    if (t <= 0 || t >= 1) return 0;
    const s = Math.sin(2 * Math.PI * t);
    return pulseFadeDepth * s * s;
  }

  function fading(now: number): boolean {
    return now < fadeUntil;
  }

  // the most a card can be zoomed to: 48 canvas widths. Past the largest level the picture is
  // shown in tiles cut out at the canvas' width, so it stays sharp down to the pixels of the
  // original; from there on those pixels are simply magnified, which is what looking into it means
  function maxZoomFor(): number {
    return Math.max(zoomLimitFloor, (48 * width) / dpr / cardFill);
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
    const hx = Math.floor(cur.x);
    const hy = Math.floor(cur.y);
    gl.uniform2f(uCenterHi, hx, hy);
    gl.uniform2f(uCenterLo, cur.x - hx, cur.y - hy);
    gl.uniform1f(uZoom, cur.zoom * dpr);
    gl.uniform2f(uHalfSize, width / 2, height / 2);
    gl.uniform1f(uTime, cardsMoving ? elapsed(now) : 1e6);
    gl.uniform1f(uDuration, duration);
    gl.uniform1f(uFill, cardFill);
    gl.uniform1f(uFade, fadeSeconds);
    gl.uniform1f(uDetailPx, detailCssPx * dpr);
    gl.uniform1i(uHover, hover);
    gl.uniform1i(uSelected, selected);
    gl.uniform1i(uPulseGroup, pulsedGroup);
    gl.uniform1i(uPulseShape, pulsedShape);
    gl.uniform1f(uPulseT, pulsing() ? (now - pulseStart) / 1000 / pulseSeconds : 1);
    gl.uniform1i(uShaped, shaped ? 1 : 0);
    gl.uniform3fv(uInk, theme.ink);
    gl.uniform3fv(uClear, theme.clear);
    gl.uniform3fv(uOutline, theme.outline);
    gl.uniform1f(uNowMs, now);
    gl.uniform1i(uPick, pickMode ? 1 : 0);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, palette);
    gl.uniform1i(uPalette, 0);
    for (let i = 0; i < imageLevels.length; i++) {
      gl.activeTexture(gl.TEXTURE1 + i);
      gl.bindTexture(gl.TEXTURE_2D_ARRAY, levelTextures[i] ?? emptyLevel);
      gl.uniform1i(uLevels[i], 1 + i);
    }
    const tileBase = 1 + imageLevels.length;
    for (let k = 0; k < tileSlots; k++) {
      const slot = tileOrder[k];
      gl.activeTexture(gl.TEXTURE0 + tileBase + k);
      gl.bindTexture(gl.TEXTURE_2D, (slot >= 0 ? tileTextures[slot] : null) ?? emptyTile);
      gl.uniform1i(uTiles[k], tileBase + k);
    }
    gl.uniform1iv(uTileCard, tileCards);
    gl.uniform4fv(uTileRect, tileRects);
    gl.uniform1fv(uTileTime, tileTimes);
    const shapeUnit = tileBase + tileSlots;
    gl.activeTexture(gl.TEXTURE0 + shapeUnit);
    gl.bindTexture(gl.TEXTURE_2D_ARRAY, shapeAtlas ?? emptyLevel);
    gl.uniform1i(uShapeAtlas, shapeUnit);
    gl.activeTexture(gl.TEXTURE0);
  }

  // the picture words of the cards that changed since the last frame, a few bytes each
  function flushTexWords() {
    if (texDirty.size === 0) return;
    gl.bindBuffer(gl.ARRAY_BUFFER, texBuffer);
    for (const i of texDirty) {
      if (i < count) gl.bufferSubData(gl.ARRAY_BUFFER, i * 12, texWords, i * 3, 3);
    }
    texDirty.clear();
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
    frameNow = now;
    if (destroyed) return;
    // a frame asked for by something that then turned out to change nothing draws nothing
    if (!dirty && !cardsMoving && !pulsing() && !cameraMoving() && !fading(now) && texDirty.size === 0) {
      lastFrame = 0;
      return;
    }
    const dt = lastFrame === 0 ? 1 / 60 : Math.min(0.1, (now - lastFrame) / 1000);
    lastFrame = now;
    stepCamera(dt, now);
    if (cardsMoving && elapsed(now) > duration + maxDelay) cardsMoving = false;
    // ended before the uniforms are set, so the last frame of a pulse is the picture at rest
    if (pulsing() && (now - pulseStart) / 1000 >= pulseSeconds) {
      pulsedGroup = -1;
      pulsedShape = -1;
    }

    flushTexWords();
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
    if (cardsMoving || pulsing() || cameraMoving() || fading(now) || texDirty.size > 0) schedule();
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

  function positionOf(i: number, time: number, out: Float32Array, at: number) {
    const s = ease((time - timing[i * 2]) / duration);
    out[at] = from[i * 2] + (to[i * 2] - from[i * 2]) * s;
    out[at + 1] = from[i * 2 + 1] + (to[i * 2 + 1] - from[i * 2 + 1]) * s;
  }

  function currentPositions(): Float32Array {
    const out = new Float32Array(count * 2);
    if (!cardsMoving) {
      out.set(to.subarray(0, count * 2));
      return out;
    }
    const time = elapsed(performance.now());
    for (let i = 0; i < count; i++) positionOf(i, time, out, i * 2);
    return out;
  }

  function sizeOfLevel(level: number): [number, number] {
    const w = imageLevels[level];
    return [w, Math.round(w * imageShare)];
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
      // a fresh set of cards has no groups yet; until it is told, every card is group 0 and a plain
      // card (the buffers are still uploaded: an attribute must have as many of them as there are cards)
      const groups = new Uint16Array(n);
      upload(groupBuffer, groups);
      upload(shapeBuffer, groups);
      shaped = false;
      // and no pictures: every card is flat until it is told what it shows
      texWords = new Uint32Array(n * 3);
      texDirty.clear();
      upload(texBuffer, texWords);
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
    positionsOf(indexes, n, out) {
      if (!cardsMoving) {
        for (let k = 0; k < n; k++) {
          const i = indexes[k];
          out[k * 2] = to[i * 2];
          out[k * 2 + 1] = to[i * 2 + 1];
        }
        return;
      }
      const time = elapsed(performance.now());
      for (let k = 0; k < n; k++) positionOf(indexes[k], time, out, k * 2);
    },
    setGroups(assignment, colors) {
      upload(groupBuffer, assignment);
      paletteSize = Math.max(1, colors.length / 4);
      gl.bindTexture(gl.TEXTURE_2D, palette);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, paletteSize, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, colors);
      dirty = true;
      schedule();
    },
    setShapes(assignment) {
      if (assignment === null) {
        shaped = false;
      } else {
        // which silhouettes the picture actually holds: a pass over the cards, so that only those
        // shapes are measured and put in the texture (a card is a slot, a slot is a shape and a
        // variation of it, and slot 0 - the plain card - needs no field at all)
        const seen = new Uint8Array(shapeCount);
        let distinct = 0;
        for (let i = 0; i < assignment.length; i++) {
          const slot = assignment[i];
          if (slot === 0) continue;
          const shape = slot & 0xff;
          if (seen[shape] === 1) continue;
          seen[shape] = 1;
          ensureShape(shape);
          if (++distinct === shapeCount) break;
        }
        upload(shapeBuffer, assignment);
        shaped = true;
      }
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
      pulsedShape = -1;
      pulseStart = performance.now();
      dirty = true;
      schedule();
    },
    pulseShape(slot) {
      pulsedGroup = -1;
      pulsedShape = slot;
      pulseStart = performance.now();
      dirty = true;
      schedule();
    },
    pulseFade() {
      if (!pulsing()) return null;
      return { group: pulsedGroup, shape: pulsedShape, amount: pulseAmountAt(frameNow) };
    },
    pick(cssX, cssY) {
      if (count === 0) return -1;
      const px = Math.floor(cssX * dpr);
      const py = Math.floor(cssY * dpr);
      if (px < 0 || py < 0 || px >= width || py >= height) return -1;
      flushTexWords();
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
      const zoom = Math.min(maxZoomFor(), Math.max(0.001, Math.min((cssW - 2 * padding) / bw, (cssH - 2 * padding) / bh)));
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
      const zoom = Math.min(maxZoomFor(), Math.max(minZoom, target.zoom * factor));
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
    cameraTarget: () => ({ ...target }),
    size: () => ({ width: width / dpr, height: height / dpr, dpr }),
    moving: () => cardsMoving || pulsing() || cameraMoving() || fading(performance.now()),
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

    ensureImageLevel(level, layers) {
      layers = Math.max(1, Math.min(maxLayers, Math.floor(layers)));
      if (levelTextures[level] !== null && levelLayers[level] >= layers) return;
      // storage is immutable once made: a level asked to grow is made anew (its pictures are gone,
      // which the owner knows, having asked)
      if (levelTextures[level] !== null) gl.deleteTexture(levelTextures[level]);
      const [w, h] = sizeOfLevel(level);
      const texture = gl.createTexture()!;
      gl.bindTexture(gl.TEXTURE_2D_ARRAY, texture);
      gl.texStorage3D(gl.TEXTURE_2D_ARRAY, 1, gl.RGBA8, w, h, layers);
      arrayTextureParameters(gl);
      gl.bindTexture(gl.TEXTURE_2D_ARRAY, null);
      levelTextures[level] = texture;
      levelLayers[level] = layers;
    },
    imageLayers: (level) => levelLayers[level],
    uploadImage(level, layer, image) {
      const texture = levelTextures[level];
      if (texture === null || layer < 0 || layer >= levelLayers[level]) return;
      const [w, h] = sizeOfLevel(level);
      let source: TexImageSource = image;
      if (image.width !== w || image.height !== h) {
        // not the size asked for (a tiny original the converter would not blow up): drawn to size
        if (resizeCanvas === null) resizeCanvas = document.createElement("canvas");
        resizeCanvas.width = w;
        resizeCanvas.height = h;
        const ctx = resizeCanvas.getContext("2d");
        if (!ctx) return;
        ctx.drawImage(image, 0, 0, w, h);
        source = resizeCanvas;
      }
      gl.bindTexture(gl.TEXTURE_2D_ARRAY, texture);
      gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
      gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
      gl.texSubImage3D(gl.TEXTURE_2D_ARRAY, 0, 0, 0, layer, w, h, 1, gl.RGBA, gl.UNSIGNED_BYTE, source);
      gl.bindTexture(gl.TEXTURE_2D_ARRAY, null);
    },
    setCardImage(index, kind, level, layer, fromLevel, fromLayer, arrivalMs) {
      if (index < 0 || index >= count) return;
      const at = index * 3;
      texWords[at] = ((layer >= 0 && level >= 0 ? layer & 0xffff : 0) | ((level >= 0 ? level + 1 : 0) << 16) | ((fromLevel >= 0 && fromLayer >= 0 ? fromLevel + 1 : 0) << 20) | ((kind & 3) << 24)) >>> 0;
      texWords[at + 1] = fromLayer >= 0 ? fromLayer & 0xffff : 0;
      texWords[at + 2] = Math.max(0, Math.floor(arrivalMs)) >>> 0;
      texDirty.add(index);
      if (kind === CardKind.Image && level >= 0) fadeUntil = Math.max(fadeUntil, arrivalMs + imageFadeMs + 20);
      dirty = true;
      schedule();
    },
    setTiles(tiles) {
      tileOrder.fill(-1);
      tileCards.fill(-1);
      tileRects.fill(0);
      tileTimes.fill(0);
      for (let k = 0; k < tiles.length && k < tileSlots; k++) {
        const t = tiles[k];
        if (t.slot < 0 || t.slot >= tileSlots || tileTextures[t.slot] === null) continue;
        tileOrder[k] = t.slot;
        tileCards[k] = t.card;
        for (let c = 0; c < 4; c++) tileRects[k * 4 + c] = t.rect[c];
        tileTimes[k] = t.arrivalMs;
        fadeUntil = Math.max(fadeUntil, t.arrivalMs + imageFadeMs + 20);
      }
      dirty = true;
      schedule();
    },
    uploadTile(slot, image) {
      if (slot < 0 || slot >= tileSlots) return;
      let texture = tileTextures[slot];
      if (texture === null) {
        texture = gl.createTexture()!;
        tileTextures[slot] = texture;
      }
      gl.bindTexture(gl.TEXTURE_2D, texture);
      gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
      gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, image);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      gl.bindTexture(gl.TEXTURE_2D, null);
    },
    freeTiles() {
      for (let i = 0; i < tileSlots; i++) {
        if (tileTextures[i] !== null) gl.deleteTexture(tileTextures[i]);
        tileTextures[i] = null;
      }
      tileOrder.fill(-1);
      tileCards.fill(-1);
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
      gl.deleteBuffer(texBuffer);
      gl.deleteBuffer(shapeBuffer);
      gl.deleteVertexArray(vao);
      gl.deleteTexture(palette);
      gl.deleteTexture(emptyLevel);
      if (shapeAtlas !== null) gl.deleteTexture(shapeAtlas);
      gl.deleteTexture(emptyTile);
      for (const t of levelTextures) if (t !== null) gl.deleteTexture(t);
      for (const t of tileTextures) if (t !== null) gl.deleteTexture(t);
      gl.deleteTexture(pickTexture);
      gl.deleteFramebuffer(pickFramebuffer);
      gl.deleteProgram(prog);
    },
  };
  return field;
}

function arrayTextureParameters(gl: WebGL2RenderingContext) {
  gl.texParameteri(gl.TEXTURE_2D_ARRAY, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D_ARRAY, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D_ARRAY, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D_ARRAY, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
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
