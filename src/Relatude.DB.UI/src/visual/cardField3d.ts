import {
  CardKind,
  bornScale,
  cardFill,
  detailCssPx,
  ease,
  fadeSeconds,
  imageFadeMs,
  imageLevels,
  imageShare,
  moveDuration,
  moveStagger,
  overshoot,
  pulseFadeDepth,
  pulseSeconds,
  type Camera,
  type CardFieldCommon,
  type FieldSurface,
  type FieldTheme,
  type PulseFade,
  type RGBf,
} from "./cardField";
import type { Bounds } from "./layouts";
import { shapeCount, shapeField, shapeFieldSize, shapeVariants } from "./shapes";
import { FlyCamera, type Channel, type Pose } from "../graph3d/camera";
import { add, cross, distance, dot, multiply, normalize, perspective, rayPlane, scale, sub, view, type Mat4, type Vec3 } from "../graph3d/math";

/**
 * Draws the cards of the visual pivot as solids: the same picture as the flat field (cardField.ts),
 * given a thickness by a third property and seen through a camera that orbits.
 *
 * This is a second renderer rather than a mode of the first, and deliberately so: the flat picture
 * has to stay able to hold a million cards at two triangles each, and every uniform, attribute and
 * branch added to that shader is paid for a million times a frame whether anyone asked for depth or
 * not. The two share what is genuinely shared - the timing of a move, the picture levels
 * (cardMedia.ts drives either through FieldSurface), the silhouettes (shapes.ts) - and nothing else.
 * Only one of them exists at a time: choosing a depth property tears down the flat field and builds
 * this one, and clearing it does the reverse.
 *
 * What a card is here: a box standing on the plane of the layout, one unit of the grid wide, as deep
 * as its group of the depth property says, extruded toward where the camera starts. The layout is
 * the flat one unchanged (visual/layouts.ts), so a grid is a grid and bars are bars; depth is a
 * channel laid over it, the way colour and shape are, which is also why switching to 3D does not
 * move a single card.
 *
 * How it is drawn:
 *
 *  - Three quads an instance, not six. A closed box shows at most three of its faces, and which
 *    three follows from the sign of (eye - centre) per axis; the ones that come out facing away are
 *    dropped by the backface culling, which leaves exactly the faces that can be seen. Front faces
 *    of a convex solid never overlap on the screen, so nothing is shaded twice, and a card costs
 *    four to six triangles rather than twelve.
 *  - Two programs, chosen once per frame rather than per fragment. The plain one shades the
 *    rasterized face: its own normal, turned toward the edge over the last few percent of the face
 *    so the box catches a highlight along every edge (a chamfer that is shaded rather than built -
 *    at the size a card is on screen the difference is the silhouette of a bevel one pixel wide).
 *    It writes no depth of its own, so the hardware can still throw away hidden fragments early,
 *    which is what keeps a large set drawable at all. The other program raymarches the true solid:
 *    the silhouette's distance field extruded and filleted, the normal from its gradient, the depth
 *    written per pixel. That one is only reached when the cards are large enough on screen for a
 *    shape or a bevel to be seen at all - which, the zoom being what it is, is also when there are
 *    few enough of them to afford it.
 *  - The material is the one the 3D datamodel graph uses: a tight highlight and a broad sheen over
 *    an ambient floor set by the theme, so a card reads as something moulded and glossy.
 *
 * Everything else - a move, a fade-in, a pulse, the picture words, the palette, picking by drawing
 * the ids into one pixel - works exactly as it does in the flat field, and for the same reasons. The
 * one difference is that the picture is opaque: a solid needs the depth buffer, and blending against
 * it in an arbitrary order does not work, so a newborn card grows into place without fading and a
 * pulse takes the cards it addresses toward the colour of the page rather than into it. The edges
 * are left to the multisampling.
 */

/**
 * How much the picture is allowed to cost. Lowered by the guard below when frames run long, and by
 * the view when a set is large enough that the first frame would be a risk.
 *
 *  - Solid: the raymarched solid where the cards are big enough to show it - true extruded shapes
 *    and a filleted, glossy edge.
 *  - Boxes: plain boxes, shaded bevel, everything else the same.
 *  - Flat: no bevel either; a box lit by its faces.
 */
export const DetailLevel = { Flat: 0, Boxes: 1, Solid: 2 } as const;
export type DetailLevel = (typeof DetailLevel)[keyof typeof DetailLevel];

export interface CardField3D extends CardFieldCommon, FieldSurface {
  /**
   * How thick every card is, in units of the grid pitch (so 1 is as deep as a card is wide); null
   * for one thickness for all of them. The cards grow or shrink to it over `seconds`.
   */
  setDepths(depths: Float32Array | null, seconds?: number): void;
  /**
   * Which row along the depth axis every card stands on, in cells of the grid; null for one plane.
   * The cards travel there on the timeline of the move that follows, so a change of the depth
   * grouping is told as setRows and then moveTo. `from` says where they leave from: left out, it is
   * where they are now (the same cards, moved); null puts them there without travelling (a picture
   * that is starting); an array is the rows of a set of cards that is partly new, the carried-over
   * ones handed the row they were on.
   */
  setRows(rows: Float32Array | null, from?: Float32Array | null): void;
  /** Where every card stands along the depth axis right now, mid-move or not; a fresh array. */
  rows(): Float32Array;
  /**
   * The lines on the floor of the picture: pairs of points, three world coordinates each, drawn
   * under the cards. An empty array draws none.
   */
  setFloorLines(points: Float32Array): void;
  /**
   * A button has gone down at this point of the canvas. What is under it becomes what the drag works
   * about: the point the picture turns around, and the depth a slide is measured at.
   */
  hold(channel: Channel, cssX: number, cssY: number): void;
  release(channel: Channel): void;
  /** Turns the picture about the point taken hold of; the deltas are pointer pixels. */
  orbit(dxPx: number, dyPx: number): void;
  /** Turns the camera where it stands. */
  look(dxPx: number, dyPx: number): void;
  /** Slides the picture, so that the point taken hold of follows the pointer. */
  panBy(dxPx: number, dyPx: number): void;
  /**
   * The same slide, taken slowly and without a hold: what a canvas that has changed shape under the
   * picture asks for, so the view is nudged along rather than fitted afresh.
   */
  driftBy(dxPx: number, dyPx: number, seconds: number): void;
  /** Where the camera stands, in world units: what the picture is being looked at from. */
  eye(): Vec3;
  /** A wheel step: closer to, or further from, whatever is under the pointer, which stays put. */
  zoomAt(amount: number, cssX: number, cssY: number): void;
  /** Brings the camera to a halt: what a menu or a modal does to a drag in progress. */
  stop(): void;
  /** How the picture is being drawn and how long a frame is taking, for the note over the canvas. */
  detail(): { level: DetailLevel; ceiling: DetailLevel; frameMs: number };
  /** The most detail the picture may be drawn with. */
  setDetailCeiling(level: DetailLevel): void;
  /** Where the camera is, so the view can put it back after a rebuild. */
  pose(): { yaw: number; pitch: number };
  setHeading(yaw: number, pitch: number): void;
}

/**
 * The thickness a card is given when nothing has said otherwise: as deep as it is wide, so a card
 * with no thickness property is a cube rather than a card standing on edge.
 */
export const defaultDepth = cardFill;
/** the fillet on a raymarched solid, as a fraction of the card's width */
const roundShare = 0.045;
/** the shaded bevel on a plain box, as a fraction of the card's width */
const bevelShare = 0.07;
/** how long a change of thickness takes, in seconds */
const depthSeconds = 1.1;
/** how far above 2.5 device pixels a card has to be before the solid is drawn: the same measure the flat field brings a silhouette in on */
const shapeZoomLow = 2.5;
const shapeZoomHigh = 7;
/** a plain box is raymarched as a filleted solid once it is this many device pixels across, where a straight corner starts to show */
const roundZoom = 40;
/**
 * Three lamps, the way a thing on a table is lit for a photograph of it.
 *
 * The key is well off to one side and above; the fill comes from the opposite side and below, dim
 * and a touch cool against the key's warmth, which is what makes the two sides of a block read as
 * two different faces rather than as one face and its shadow. The third comes from behind and above
 * the far shoulder and is only let through where the face is turning away from the eye, so it draws
 * a bright edge along the silhouette of every solid instead of flooding the faces the other two
 * already light. Between them there is no direction a face can be turned that has nothing on it.
 */
const lightDir = normalize([0.62, 0.66, 0.43]);
const keyLight: RGBf = [1.38, 1.32, 1.22];
const fillLight: RGBf = [0.46, 0.51, 0.62];
const rimDir = normalize([-0.55, 0.42, -0.72]);
const rimLight: RGBf = [0.3, 0.34, 0.4];
/** the most layers a picture level is ever given (see cardField.ts, which bounds it the same way) */
const maxLayersWanted = 2048;
/**
 * How fast the camera closes on where the wheel is taking it, as a share of what is left per second,
 * and how long a wheel gesture is held to be still going after its last notch.
 */
const zoomFollowRate = 13;
const gestureGapMs = 400;
/** the guard: a frame this long, this many times running, takes a level of detail away */
const slowFrameMs = 45;
const slowFramesBeforeDrop = 30;
const fastFrameMs = 22;
const fastFramesBeforeRaise = 240;

const vertexSource = `#version 300 es
precision highp float;
precision highp int;
layout(location = 0) in vec3 aVert;   // which of the three faces, and the corner of it: axis, u, v
layout(location = 1) in vec2 aFrom;
layout(location = 2) in vec2 aTo;
layout(location = 3) in vec2 aTiming; // when this card sets off, and whether it is arriving (1) or leaving (2)
layout(location = 4) in uint aGroup;
layout(location = 5) in uvec3 aTex;   // the picture words, see setCardImage
layout(location = 6) in uint aShape;  // which silhouette this card is cut out to, see shapes.ts
layout(location = 7) in vec2 aDepth;  // the thickness it had, and the one it is going to
layout(location = 8) in vec2 aRow;    // the row it stood on, and the one it is going to
uniform mat4 uViewProj;
uniform vec3 uEye;        // the camera, and everything else, measured from uOrigin: at a deep zoom
uniform vec3 uOrigin;     // the difference of two large coordinates is not a small one in float32
uniform float uTime;      // seconds since the move began
uniform float uDuration;
uniform float uDepthT;    // how far through a change of thickness, 0..1
uniform float uFill;
uniform float uFade;
uniform float uZoom;      // device pixels per world unit at the point in focus
uniform float uPxScale;   // half the canvas in device pixels over the tangent of half the field of view
uniform float uDetailPx;
uniform sampler2D uPalette;
uniform int uHover;
uniform int uSelected;
uniform vec3 uInk;
uniform int uPick;
uniform int uPulseGroup;
uniform int uPulseShape;
uniform float uPulseT;
uniform int uShaped;
uniform vec2 uShapeTurn[${shapeVariants.length}];
uniform float uShapeInvScale[${shapeVariants.length}];
out vec2 vUv;
out vec3 vWorld;
out vec4 vColor;
flat out vec3 vCenter;
flat out float vPxPerWorld;
flat out float vSide;
flat out float vThick;
flat out int vFace;
flat out float vSign;
flat out uvec3 vTex;
flat out float vDetail;
flat out float vFadeOut;  // how far into the page a pulse has taken this card, 0 at rest
flat out int vFlags;
flat out vec4 vShape;     // the turn (cos, sin), one over the scale, and the layer of the field; w < 0 is a plain card
flat out float vShapeMix;
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
float smoother(float t) {
  t = clamp(t, 0.0, 1.0);
  return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
}
float pulse(float t) {
  if (t <= 0.0 || t >= 1.0) return 0.0;
  float s = sin(6.2831853 * t);
  return s * s;
}
void main() {
  float t = (uTime - aTiming.x) / uDuration;
  bool leaving = aTiming.y > 1.5;
  bool arriving = !leaving && aTiming.y > 0.5;
  // A card that is only moving travels on the eased curve, which starts from rest and settles. One
  // that is leaving FALLS: the same curve squared, which is a constant acceleration - what a dropped
  // thing does - and it never settles, because there is nothing left of it by the time it would.
  float dropped = clamp(t, 0.0, 1.0);
  float travelled = leaving ? dropped * dropped : ease(t);
  vec2 flat2 = mix(aFrom, aTo, travelled);
  float row = mix(aRow.x, aRow.y, travelled);
  float fill = mix(1.0, uFill, smoothstep(2.5, 7.0, uZoom));
  // A card coming down out of the sky grows and colours as it descends, and is itself by the time it
  // lands; one that has been dropped shrinks and pales as it falls, and is nothing before it is far
  // enough away to be missed. Paling is mixing toward the colour of the page rather than any kind of
  // transparency: a picture of solids is opaque, and a card blended over its neighbours in whatever
  // order they happen to be drawn in is not a fade, it is a fault.
  float born = 1.0;
  float paled = 0.0;
  float over = clamp(t, 0.0, 1.0);
  float settling = over * over * (3.0 - 2.0 * over);
  if (leaving) {
    born = 1.0 - settling;
    paled = settling;
  } else if (arriving) {
    born = BORN + (1.0 - BORN) * settling;
    paled = 1.0 - settling;
  }
  float side = fill * born;
  float thick = max(0.02, mix(aDepth.x, aDepth.y, smoother(uDepthT))) * born;
  // The card's box: a cell of the grid wide, standing on the plane of its own row. y grows downward
  // in a layout and upward in the world, so the picture keeps the way up it had when it was flat; z
  // is 0 for the row nearest the viewer and negative behind it, and a card is extruded toward them.
  vec3 centre = vec3(flat2.x + 0.5, -(flat2.y + 0.5), row + thick * 0.5) - uOrigin;
  vec3 half3 = vec3(side * 0.5, side * 0.5, thick * 0.5);
  // Which three faces can be seen, and the two world directions that span each of them. The axes
  // are taken in cyclic order so that the cross product of the two spanning ones is the outward
  // normal, and swapped when the face is the far one, which keeps every triangle wound the same way
  // round: the faces that came out facing away are then dropped by the culling rather than here.
  int axis = int(aVert.x + 0.5);
  vec3 ea = axis == 0 ? vec3(1.0, 0.0, 0.0) : axis == 1 ? vec3(0.0, 1.0, 0.0) : vec3(0.0, 0.0, 1.0);
  vec3 eb = axis == 0 ? vec3(0.0, 1.0, 0.0) : axis == 1 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
  vec3 ec = axis == 0 ? vec3(0.0, 0.0, 1.0) : axis == 1 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
  float s = dot(uEye, ea) >= dot(centre, ea) ? 1.0 : -1.0;
  float hb = dot(half3, eb);
  float hc = dot(half3, ec);
  vec3 spanU = s > 0.0 ? eb : ec;
  vec3 spanV = s > 0.0 ? ec : eb;
  float hu = s > 0.0 ? hb : hc;
  float hv = s > 0.0 ? hc : hb;
  vec3 world = centre + ea * (s * dot(half3, ea)) + spanU * ((2.0 * aVert.y - 1.0) * hu) + spanV * ((2.0 * aVert.z - 1.0) * hv);
  gl_Position = uViewProj * vec4(world, 1.0);
  vWorld = world;
  vCenter = centre;
  vSide = side;
  vThick = thick;
  vFace = axis;
  vSign = s;
  // the picture is laid on the face the same way up whichever face it is, and mirrored on the far
  // faces so that a card read from behind is not read backwards (see faceBasis in the fragment)
  float fb = s > 0.0 ? aVert.y : aVert.z;
  float fc = s > 0.0 ? aVert.z : aVert.y;
  vec2 uv = axis == 0 ? vec2(fc, 1.0 - fb) : axis == 1 ? vec2(fc, 1.0 - fb) : vec2(fb, 1.0 - fc);
  if (s < 0.0) uv.x = 1.0 - uv.x;
  vUv = uv;
  vPxPerWorld = uPxScale / max(length(uEye - world), 1e-4);
  vDetail = smoothstep(uDetailPx * 0.9, uDetailPx * 1.1, fill * uZoom);
  vTex = aTex;
  int slot = int(aShape);
  bool pulsed = int(aGroup) == uPulseGroup || slot == uPulseShape;
  // one measure of how far into the page a card is, whether that is a pulse or a card on its way in
  // or out; the fragment shader mixes the lot toward the page at the end
  vFadeOut = max(paled, pulsed ? PULSE_FADE * pulse(uPulseT) : 0.0);
  if (uShaped == 1 && slot > 0) {
    int variant = clamp(slot >> 8, 0, VARIANTS - 1);
    vShape = vec4(uShapeTurn[variant], uShapeInvScale[variant], float(slot & 255));
    vShapeMix = smoothstep(${shapeZoomLow.toFixed(1)}, ${shapeZoomHigh.toFixed(1)}, uZoom);
  } else {
    vShape = vec4(1.0, 0.0, 1.0, -1.0);
    vShapeMix = 0.0;
  }
  int id = gl_InstanceID;
  vFlags = id == uSelected ? 1 : 0;
  if (uPick == 1) {
    vColor = vec4(float(id & 255) / 255.0, float((id >> 8) & 255) / 255.0, float((id >> 16) & 255) / 255.0, 1.0);
    return;
  }
  vec4 c = texelFetch(uPalette, ivec2(int(aGroup), 0), 0);
  if (id == uHover) c.rgb = mix(c.rgb, uInk, 0.28);
  vColor = c;
}`;

/** Everything both fragment shaders share: the card's own face, and the material it is lit with. */
const fragmentCommon = `
const float SHARE = ${imageShare.toFixed(4)};
const float FADE_MS = ${imageFadeMs.toFixed(1)};
const float ROUND = ${roundShare.toFixed(4)};
const float BEVEL = ${bevelShare.toFixed(4)};
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
/** the placeholder a node with no picture shows, drawn the way the flat field draws it */
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
/**
 * What one face of a card shows: its own colour, with the picture over the upper part once the card
 * is wide enough to be read. how much of it is picture comes back in shown, which is what the
 * highlight is damped by so a photograph is not washed out by the gloss over it.
 */
vec3 cardFace(vec3 c, vec2 uv0, out float shown) {
  shown = 0.0;
  if (vDetail <= 0.001 || uv0.y >= SHARE) return c;
  vec2 uv = vec2(uv0.x, uv0.y / SHARE);
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
        pic = mix(sampleLevel(levelB, vec3(uv, float(vTex.y & 0xFFFFu))), a, fade);
        show = 1.0;
      } else {
        pic = a;
        show = fade;
      }
    }
  } else if (kind == 2) {
    pic = placeholder(c, uv, 1.5 / max(1.0, vPxPerWorld * vSide * SHARE));
    show = 1.0;
  }
  shown = vDetail * show;
  return mix(c, pic, shown);
}
/** How far past the terminator the key still reaches: a lamp with a size to it, not a point. */
#define KEYWRAP 0.18
/** Where the light stops adding brightness and starts bending over, and what it never quite reaches. */
#define KNEE 0.85
/**
 * The top of the range, rolled over rather than cut off. Three lamps and a sheen put more light on
 * the face turned into the key than a screen can show, and a channel held at white there is a face
 * with no shading left on it: the whole of it comes out one flat colour however it is curved. Bent
 * over instead, it goes on separating - a fillet still catches more light than the face behind it.
 *
 * Each channel is bent on its own, which is what a bright surface does in a photograph: the channel
 * that is already high gives way first and the colour walks toward white as it brightens. Scaling
 * the three together would hold the hue, but it would also mean that a saturated colour stopped
 * taking light the moment its strongest channel arrived, and a blue block lit from three sides would
 * read as flat as a printed one. Nothing under the knee is touched, which is the body of the picture.
 */
float roll(float x) {
  return x <= KNEE ? x : KNEE + (1.0 - KNEE) * (1.0 - exp(-(x - KNEE) / (1.0 - KNEE)));
}
vec3 shoulder(vec3 x) {
  return vec3(roll(x.r), roll(x.g), roll(x.b));
}
/**
 * The material. A field of boxes lives or dies by how differently its three visible faces are lit,
 * so the light is made to do as much of the work as it can:
 *
 *  - A key light well off to one side, and a hemisphere over it - the ambient is not a flat floor
 *    but brighter overhead than underfoot, which is what lifts the top of every block away from its
 *    sides without a second light being aimed at it. The key is wrapped a little past the
 *    terminator, so a fillet turning out of the light dims into it rather than falling off an edge.
 *  - A fill from the other side, cool against the warm key, so the face turned away from the key is
 *    darker but nowhere near a hole. Two lights from opposite sides is what stops a box reading as
 *    two tones; it gives every face its own.
 *  - A rim from behind the far shoulder, let through only where the face is turning away from the
 *    eye. On a solid that is the last sliver before the silhouette, so every card is drawn round
 *    with a line of light and reads as separate from whatever is standing behind it.
 *  - A tight highlight and a broad sheen, both stronger than a matte surface would have, so an edge
 *    or a fillet catches the light as something moulded rather than printed. Damped over a picture,
 *    which has no business being washed out by the gloss on top of it.
 */
vec3 shade(vec3 c, vec3 n, vec3 world, float pictured) {
  vec3 v = normalize(uEye - world);
  float head = max(dot(n, v), 0.0);
  float diff = max((dot(n, uLight) + KEYWRAP) / (1.0 + KEYWRAP), 0.0);
  float back = max(dot(n, -uLight), 0.0);
  // cubed, so this lamp is an edge and not a third flood: it is all but out by the time a face has
  // turned far enough toward the eye to be read as a face
  float rim = max(dot(n, uRim), 0.0) * pow(1.0 - head, 3.0);
  vec3 h = normalize(uLight + v);
  float ndh = max(dot(n, h), 0.0);
  float spec = (pow(ndh, 64.0) * 0.78 + pow(ndh, 10.0) * 0.12) * mix(1.0, 0.4, pictured);
  // overhead against underfoot: the ambient a face sees depends on which way it is turned
  float sky = 0.5 + 0.5 * n.y;
  vec3 ambient = c * uAmbient * mix(0.58, 1.3, sky * sky);
  vec3 key = c * uKey * (0.82 * diff + 0.06 * head);
  vec3 opposite = c * uFillLight * back;
  vec3 edge = c * uRimLight * rim;
  return shoulder(ambient + key + opposite + edge + spec * uShine);
}
/** The face's normal and the world directions its two picture axes run in (see the vertex shader). */
void faceBasis(out vec3 n, out vec3 ux, out vec3 uy) {
  if (vFace == 0) {
    n = vec3(vSign, 0.0, 0.0);
    ux = vec3(0.0, 0.0, 1.0);
    uy = vec3(0.0, -1.0, 0.0);
  } else if (vFace == 1) {
    n = vec3(0.0, vSign, 0.0);
    ux = vec3(1.0, 0.0, 0.0);
    uy = vec3(0.0, 0.0, -1.0);
  } else {
    n = vec3(0.0, 0.0, vSign);
    ux = vec3(1.0, 0.0, 0.0);
    uy = vec3(0.0, -1.0, 0.0);
  }
  if (vSign < 0.0) ux = -ux;
}
/** How wide the face is in world units along each of its two picture axes. */
vec2 faceSize() {
  return vFace == 0 ? vec2(vThick, vSide) : vFace == 1 ? vec2(vSide, vThick) : vec2(vSide, vSide);
}
`;

/** The uniforms both fragment shaders declare. */
const fragmentUniforms = `
uniform highp sampler2DArray uShapeAtlas;
uniform mediump sampler2DArray uLevel0;
uniform mediump sampler2DArray uLevel1;
uniform mediump sampler2DArray uLevel2;
uniform mediump sampler2DArray uLevel3;
uniform mediump sampler2DArray uLevel4;
uniform mediump sampler2DArray uLevel5;
uniform mat4 uViewProj;
uniform vec3 uEye;
uniform vec3 uLight;
uniform vec3 uInk;
uniform vec3 uClear;
uniform vec3 uOutline;
uniform float uAmbient;
uniform vec3 uKey;
uniform vec3 uFillLight;
uniform vec3 uRim;
uniform vec3 uRimLight;
uniform float uShine;
uniform float uNowMs;
uniform int uPick;
uniform int uBevel;
`;

const fragmentIn = `
in vec2 vUv;
in vec3 vWorld;
in vec4 vColor;
flat in vec3 vCenter;
flat in float vPxPerWorld;
flat in float vSide;
flat in float vThick;
flat in int vFace;
flat in float vSign;
flat in uvec3 vTex;
flat in float vDetail;
flat in float vFadeOut;
flat in int vFlags;
flat in vec4 vShape;
flat in float vShapeMix;
out vec4 outColor;
`;

/**
 * The plain program: the rasterized face, shaded by its own normal, with the normal turned toward
 * the edge over the last few percent of the face. Nothing is marched and no depth is written, so
 * the hardware keeps its early rejection and a large set stays drawable.
 */
const plainFragmentSource = `#version 300 es
precision highp float;
precision highp int;
${fragmentUniforms}
${fragmentIn}
${fragmentCommon}
void main() {
  if (uPick == 1) {
    outColor = vColor;
    return;
  }
  vec3 n, ux, uy;
  faceBasis(n, ux, uy);
  vec2 size = faceSize();
  // how far this pixel is from the edge of the face, in world units, and which way that edge lies
  float dx = min(vUv.x, 1.0 - vUv.x) * size.x;
  float dy = min(vUv.y, 1.0 - vUv.y) * size.y;
  float edge = min(dx, dy);
  float bevel = BEVEL * min(vSide, vThick);
  // a bevel narrower than a pixel is not a bevel, it is a shimmer along the edge
  if (uBevel == 1 && bevel * vPxPerWorld > 1.2) {
    float t = 1.0 - clamp(edge / bevel, 0.0, 1.0);
    vec3 outward = dx < dy ? ux * (vUv.x < 0.5 ? -1.0 : 1.0) : uy * (vUv.y < 0.5 ? -1.0 : 1.0);
    n = normalize(mix(n, outward, 0.72 * t * t));
  }
  float pictured;
  vec3 c = cardFace(vColor.rgb, vUv, pictured);
  vec3 lit = shade(c, n, vWorld, pictured);
  // the card the form has open wears a ring of the accent colour round every face it shows
  if (vFlags == 1) {
    float ring = 1.0 - smoothstep(2.0, 3.5, edge * vPxPerWorld);
    lit = mix(lit, uOutline, ring);
  }
  outColor = vec4(mix(lit, uClear, vFadeOut), 1.0);
}`;

/**
 * The solid program: the card's silhouette, extruded and filleted, found by sphere tracing from
 * where the ray enters the box. The silhouette is the same distance field the flat field cuts its
 * cards out with (shapes.ts), so a heart is a heart in either picture; here it is a solid heart with
 * an edge that catches the light. The depth is written per pixel, which is what lets one solid pass
 * through another correctly.
 */
const solidFragmentSource = `#version 300 es
precision highp float;
precision highp int;
${fragmentUniforms}
${fragmentIn}
${fragmentCommon}
const int STEPS = 48;
/** the silhouette, in units of the card's width, about its middle */
float sdShape(vec2 p) {
  vec2 c = p * vShape.z;
  vec2 uv = vec2(c.x * vShape.x + c.y * vShape.y, c.y * vShape.x - c.x * vShape.y) + 0.5;
  float ds = texture(uShapeAtlas, vec3(uv, vShape.w)).r;
  // past the shape's own box nothing was measured; what is there is the distance to the box
  vec2 e = max(abs(uv - 0.5) - 0.5, 0.0);
  return max(ds, length(e)) / vShape.z;
}
/** the plain card: a square with the corners the flat field rounds them by */
float sdSquare(vec2 p) {
  vec2 q = abs(p) - vec2(0.42);
  return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - 0.08;
}
float sdCross(vec2 p) {
  if (vShape.w < 0.0) return sdSquare(p);
  // the shape grows out of the square as the cards get room for it, the way it does when flat: a
  // blend of two distances is not one, but it is no steeper than either, which is all tracing needs
  return vShapeMix >= 0.999 ? sdShape(p) : mix(sdSquare(p), sdShape(p), vShapeMix);
}
float sdSolid(vec3 q, float hz, float r) {
  vec2 w = vec2(sdCross(q.xy) + r, abs(q.z) - hz + r);
  return min(max(w.x, w.y), 0.0) + length(max(w, 0.0)) - r;
}
void main() {
  // the ray, and the card's own box, in units of the card's width about its middle
  vec3 ro = (uEye - vCenter) / vSide;
  vec3 rd = normalize(vWorld - uEye);
  float hz = 0.5 * vThick / vSide;
  float r = ROUND * vPxPerWorld * vSide > 0.8 ? min(ROUND, hz * 0.9) : 0.0;
  vec3 bmax = vec3(0.5, 0.5, hz);
  // where the ray is in the box: a ray running along a face gives a huge t either way, which the
  // min and max below take care of without a branch
  vec3 safe = mix(rd, vec3(1e-6), step(abs(rd), vec3(1e-6)));
  vec3 ta = (-bmax - ro) / safe;
  vec3 tb = (bmax - ro) / safe;
  vec3 tn = min(ta, tb);
  vec3 tf = max(ta, tb);
  float t = max(max(tn.x, tn.y), max(tn.z, 0.0));
  float tExit = min(min(tf.x, tf.y), tf.z);
  if (tExit <= t) discard;
  float pixel = 1.0 / max(1.0, vPxPerWorld * vSide);
  float eps = max(0.0006, 0.35 * pixel);
  float d = 1.0;
  float nearest = 1e9;  // how close the ray came to the surface, and where
  float nearestT = t;
  bool hit = false;
  for (int i = 0; i < STEPS; i++) {
    d = sdSolid(ro + rd * t, hz, r);
    if (d < nearest) {
      nearest = d;
      nearestT = t;
    }
    if (d < eps) {
      hit = true;
      break;
    }
    t += max(d, eps * 0.5);
    if (t > tExit) break;
  }
  // A ray that only grazed the outline still covers part of its pixel, and how much is what the
  // distance it passed at says. That fraction is written as the alpha and turned into coverage by
  // the multisampling (SAMPLE_ALPHA_TO_COVERAGE), which is what gives a raymarched outline an edge
  // as clean as a rasterized one: a solid that is cut out by discarding has no geometry along its
  // edge for the multisampling to measure, and would come out as a staircase however many samples
  // there were. Coverage rather than blending, so the depth buffer still sorts the solids.
  float alpha = 1.0;
  if (!hit) {
    alpha = 1.0 - smoothstep(0.0, pixel, nearest);
    if (alpha <= 0.004) discard;
    t = nearestT;
    d = nearest;
  }
  vec3 q = ro + rd * t;
  vec3 world = vCenter + q * vSide;
  vec4 clip = uViewProj * vec4(world, 1.0);
  gl_FragDepth = 0.5 + 0.5 * clip.z / clip.w;
  if (uPick == 1) {
    outColor = vColor;
    return;
  }
  // the surface's own direction, from the gradient of the same distance
  float e = max(eps, 0.0015);
  vec3 n = normalize(vec3(sdSolid(q + vec3(e, 0.0, 0.0), hz, r) - d, sdSolid(q + vec3(0.0, e, 0.0), hz, r) - d, sdSolid(q + vec3(0.0, 0.0, e), hz, r) - d));
  // the picture is on the two flat ends; the wall between them is the card's colour, which is what
  // the thickness of a solid should read as
  float pictured = 0.0;
  vec3 c = vColor.rgb;
  if (abs(q.z) > hz - max(r, e) * 1.05) {
    vec2 uv = vec2(q.z > 0.0 ? q.x + 0.5 : 0.5 - q.x, 0.5 - q.y);
    c = cardFace(c, uv, pictured);
  }
  vec3 lit = shade(c, n, world, pictured);
  if (vFlags == 1) {
    float ring = 1.0 - smoothstep(2.0, 3.5, (hz - abs(q.z)) * vSide * vPxPerWorld);
    lit = mix(lit, uOutline, ring * step(hz - max(r, e) * 1.05, abs(q.z)));
  }
  outColor = vec4(mix(lit, uClear, vFadeOut), alpha);
}`;

/**
 * The lines on the floor: a straight run under each bar and each row, out past the picture to where
 * its name is written. They are drawn a hair below the cards, so a line is hidden under the block it
 * belongs to and shows in the empty floor between the blocks and beyond them - which is what makes
 * a name at the end of one read as belonging to what the line comes from, rather than floating
 * somewhere near it.
 */
const lineVertexSource = `#version 300 es
precision highp float;
layout(location = 0) in vec3 aPoint;
uniform mat4 uViewProj;
uniform vec3 uOrigin;
void main() {
  gl_Position = uViewProj * vec4(aPoint - uOrigin, 1.0);
}`;

const lineFragmentSource = `#version 300 es
precision highp float;
uniform vec4 uColor;
out vec4 outColor;
void main() {
  outColor = uColor;
}`;

type Uniforms = Record<string, WebGLUniformLocation | null>;

export function createCardField3D(canvas: HTMLCanvasElement): CardField3D | null {
  const context = canvas.getContext("webgl2", { antialias: true, alpha: false, depth: true, powerPreference: "high-performance" });
  if (!context) return null;
  const gl: WebGL2RenderingContext = context;
  const maxLayers = Math.max(1, Math.min(maxLayersWanted, gl.getParameter(gl.MAX_ARRAY_TEXTURE_LAYERS) as number));

  const plain = program(gl, vertexSource, plainFragmentSource);
  const solid = program(gl, vertexSource, solidFragmentSource);
  const lineProgram = program(gl, lineVertexSource, lineFragmentSource);
  const lineViewProj = gl.getUniformLocation(lineProgram, "uViewProj");
  const lineOrigin = gl.getUniformLocation(lineProgram, "uOrigin");
  const lineColor = gl.getUniformLocation(lineProgram, "uColor");
  const lineBuffer = gl.createBuffer()!;
  const lineVao = gl.createVertexArray()!;
  gl.bindVertexArray(lineVao);
  gl.bindBuffer(gl.ARRAY_BUFFER, lineBuffer);
  gl.enableVertexAttribArray(0);
  gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 0, 0);
  gl.bindVertexArray(null);
  let linePoints = 0;
  const names = [
    "uViewProj",
    "uEye",
    "uOrigin",
    "uTime",
    "uDuration",
    "uDepthT",
    "uFill",
    "uFade",
    "uZoom",
    "uPxScale",
    "uDetailPx",
    "uPalette",
    "uHover",
    "uSelected",
    "uInk",
    "uClear",
    "uOutline",
    "uLight",
    "uAmbient",
    "uKey",
    "uFillLight",
    "uRim",
    "uRimLight",
    "uShine",
    "uNowMs",
    "uPick",
    "uBevel",
    "uPulseGroup",
    "uPulseShape",
    "uPulseT",
    "uShaped",
    "uShapeAtlas",
    ...imageLevels.map((_, i) => "uLevel" + i),
  ];
  const uniforms = new Map<WebGLProgram, Uniforms>();
  for (const p of [plain, solid]) {
    const table: Uniforms = {};
    for (const name of names) table[name] = gl.getUniformLocation(p, name);
    uniforms.set(p, table);
    gl.useProgram(p);
    gl.uniform2fv(gl.getUniformLocation(p, "uShapeTurn"), shapeVariants.flatMap((v) => [Math.cos(v.angle), Math.sin(v.angle)]));
    gl.uniform1fv(gl.getUniformLocation(p, "uShapeInvScale"), shapeVariants.map((v) => 1 / v.scale));
  }

  // The mesh: three quads, one per axis, which the vertex shader puts on whichever side of the box
  // faces the camera. Twelve vertices and six triangles is the whole of a card's geometry.
  const verts: number[] = [];
  for (let axis = 0; axis < 3; axis++) {
    for (const [u, v] of [
      [0, 0],
      [1, 0],
      [1, 1],
      [0, 1],
    ] as const) {
      verts.push(axis, u, v);
    }
  }
  const indices: number[] = [];
  for (let axis = 0; axis < 3; axis++) {
    const b = axis * 4;
    indices.push(b, b + 1, b + 2, b, b + 2, b + 3);
  }
  const meshBuffer = gl.createBuffer()!;
  gl.bindBuffer(gl.ARRAY_BUFFER, meshBuffer);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(verts), gl.STATIC_DRAW);
  const indexBuffer = gl.createBuffer()!;
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, indexBuffer);
  gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new Uint16Array(indices), gl.STATIC_DRAW);

  const fromBuffer = gl.createBuffer()!;
  const toBuffer = gl.createBuffer()!;
  const timingBuffer = gl.createBuffer()!;
  const groupBuffer = gl.createBuffer()!;
  const texBuffer = gl.createBuffer()!;
  const shapeBuffer = gl.createBuffer()!;
  const depthBuffer = gl.createBuffer()!;
  const rowBuffer = gl.createBuffer()!;
  const vao = gl.createVertexArray()!;
  gl.bindVertexArray(vao);
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, indexBuffer);
  gl.bindBuffer(gl.ARRAY_BUFFER, meshBuffer);
  gl.enableVertexAttribArray(0);
  gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 0, 0);
  for (const [location, buffer, size] of [
    [1, fromBuffer, 2],
    [2, toBuffer, 2],
    [3, timingBuffer, 2],
    [7, depthBuffer, 2],
    [8, rowBuffer, 2],
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

  const palette = gl.createTexture()!;
  gl.bindTexture(gl.TEXTURE_2D, palette);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([128, 128, 128, 255]));

  const emptyLevel = gl.createTexture()!;
  gl.bindTexture(gl.TEXTURE_2D_ARRAY, emptyLevel);
  gl.texStorage3D(gl.TEXTURE_2D_ARRAY, 1, gl.RGBA8, 1, 1, 1);
  gl.texSubImage3D(gl.TEXTURE_2D_ARRAY, 0, 0, 0, 0, 1, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([128, 128, 128, 255]));
  arrayTextureParameters(gl);
  const levelTextures: (WebGLTexture | null)[] = imageLevels.map(() => null);
  const levelLayers: number[] = imageLevels.map(() => 0);
  let resizeCanvas: HTMLCanvasElement | null = null;

  // the silhouettes, a layer per shape, filled in the first time a shape is actually used
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

  // one pixel to pick into, with a depth of its own so the nearest solid wins
  const pickTexture = gl.createTexture()!;
  gl.bindTexture(gl.TEXTURE_2D, pickTexture);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
  const pickDepth = gl.createRenderbuffer()!;
  gl.bindRenderbuffer(gl.RENDERBUFFER, pickDepth);
  gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT24, 1, 1);
  const pickFramebuffer = gl.createFramebuffer()!;
  gl.bindFramebuffer(gl.FRAMEBUFFER, pickFramebuffer);
  gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, pickTexture, 0);
  gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, pickDepth);
  gl.bindFramebuffer(gl.FRAMEBUFFER, null);
  const pickPixel = new Uint8Array(4);

  // ---- state ----
  let count = 0;
  let from: Float32Array = new Float32Array(0);
  let to: Float32Array = new Float32Array(0);
  let timing: Float32Array = new Float32Array(0);
  let depths: Float32Array = new Float32Array(0);
  let depthFrom: Float32Array = new Float32Array(0);
  let depthStart = 0;
  let depthDuration = depthSeconds;
  let maxDepth = defaultDepth;
  // the row every card stood on and the one it is going to, in cells; both zero when the picture
  // lies in one plane, which is what a card field without a depth grouping is
  let rowFrom: Float32Array = new Float32Array(0);
  let rowTo: Float32Array = new Float32Array(0);
  let rowSpan: [number, number] = [0, 0];
  let texWords: Uint32Array = new Uint32Array(0);
  const texDirty = new Set<number>();
  let fadeUntil = 0;
  let moveStart = 0;
  let duration = 1;
  let maxDelay = 0;
  let cardsMoving = false;
  let hover = -1;
  let selected = -1;
  let pulsedGroup = -1;
  let pulsedShape = -1;
  let pulseStart = 0;
  let shaped = false;
  let theme: FieldTheme = { clear: [1, 1, 1], outline: [0.04, 0.38, 0.7], ink: [0.1, 0.1, 0.1] };
  let ambient = 0.5;
  let width = 1;
  let height = 1;
  let dpr = 1;
  let bounds: Bounds = { x0: 0, y0: 0, x1: 1, y1: 1 };
  let frameCallback: (() => void) | null = null;
  let raf = 0;
  let lastFrame = 0;
  let frameNow = 0;
  let dirty = true;
  let destroyed = false;
  let frameMs = 16;
  let slowRun = 0;
  let fastRun = 0;
  let ceiling: DetailLevel = DetailLevel.Solid;
  let detailLevel: DetailLevel = DetailLevel.Solid;
  // the matrix of the frame just drawn, for worldToCss and for the depth written per pixel
  let viewProj: Mat4 = new Float32Array(16);
  let origin: Vec3 = [0, 0, 0];

  /**
   * Where the wheel is taking the camera, or null when it is not taking it anywhere. A notch moves
   * this and `stepZoom` walks the camera toward it, so a spin of the wheel goes on closing in on one
   * point rather than each notch starting an animation of its own. Let go of as soon as anything
   * else takes hold of the camera.
   */
  let zoomTo: Pose | null = null;
  /**
   * What the drag in progress works about: the point of the picture that was under the pointer when
   * the button went down. The picture turns about it and stays put under the pointer while it does,
   * and a slide is measured at its depth so that it follows the pointer exactly. Null between drags.
   */
  let held: Vec3 | null = null;
  /** the last point worked out under the pointer, so a spin of the wheel does not ask again per notch */
  let asked: { x: number; y: number; at: number; point: Vec3 } | null = null;

  const cam = new FlyCamera();
  cam.minDist = 0.04;
  cam.yaw = 0.34;
  cam.pitch = -0.26;

  function schedule() {
    if (raf === 0 && !destroyed) raf = requestAnimationFrame(frame);
  }

  function elapsed(now: number): number {
    return (now - moveStart) / 1000;
  }

  function pulsing(): boolean {
    return pulsedGroup >= 0 || pulsedShape >= 0;
  }

  function pulseAmountAt(now: number): number {
    const t = (now - pulseStart) / 1000 / pulseSeconds;
    if (t <= 0 || t >= 1) return 0;
    const s = Math.sin(2 * Math.PI * t);
    return pulseFadeDepth * s * s;
  }

  function depthProgress(now: number): number {
    return depthDuration <= 0 ? 1 : Math.min(1, Math.max(0, (now - depthStart) / 1000 / depthDuration));
  }

  function fading(now: number): boolean {
    return now < fadeUntil;
  }

  /** how far the whole picture reaches from its middle: what the near and far planes are set by */
  function sceneRadius(): number {
    const bx = (bounds.x1 - bounds.x0) / 2 + 1;
    const by = (bounds.y1 - bounds.y0) / 2 + 1;
    const bz = (rowSpan[1] - rowSpan[0]) / 2 + maxDepth;
    return Math.hypot(bx, by, bz) + 1;
  }

  /** device pixels per world unit at the point the camera is focused on: the picture's own zoom */
  function zoomDev(): number {
    return height / 2 / Math.max(1e-6, cam.dist * Math.tan(cam.fov / 2));
  }

  /** Whether the solid is worth drawing at this zoom: the cards are big enough to show a shape or an edge. */
  function solidWanted(): boolean {
    if (detailLevel < DetailLevel.Solid) return false;
    const z = zoomDev();
    return (shaped && z >= shapeZoomLow) || z >= roundZoom;
  }

  function currentProgram(): WebGLProgram {
    return solidWanted() ? solid : plain;
  }

  function matrices() {
    const aspect = width / Math.max(1, height);
    const radius = sceneRadius();
    // near and far follow the camera, so a card filled with a picture and a whole million of them
    // both get a depth buffer worth having
    cam.near = Math.max(0.004, Math.min(cam.dist * 0.02, radius * 0.01));
    cam.far = Math.max(cam.near * 4096, cam.dist + radius * 6);
    origin = [Math.round(cam.pos[0]), Math.round(cam.pos[1]), Math.round(cam.pos[2])];
    const eye: Vec3 = [cam.pos[0] - origin[0], cam.pos[1] - origin[1], cam.pos[2] - origin[2]];
    viewProj = multiply(perspective(cam.fov, aspect, cam.near, cam.far), view(eye, cam.forward(), cam.up()));
    return { eye };
  }

  function setUniforms(prog: WebGLProgram, now: number, pickMode: boolean, eye: Vec3) {
    const u = uniforms.get(prog)!;
    gl.useProgram(prog);
    gl.uniformMatrix4fv(u.uViewProj, false, viewProj);
    gl.uniform3fv(u.uEye, eye);
    gl.uniform3fv(u.uOrigin, origin);
    gl.uniform1f(u.uTime, cardsMoving ? elapsed(now) : 1e6);
    gl.uniform1f(u.uDuration, duration);
    gl.uniform1f(u.uDepthT, depthProgress(now));
    gl.uniform1f(u.uFill, cardFill);
    gl.uniform1f(u.uFade, fadeSeconds);
    gl.uniform1f(u.uZoom, zoomDev());
    gl.uniform1f(u.uPxScale, height / 2 / Math.tan(cam.fov / 2));
    gl.uniform1f(u.uDetailPx, detailCssPx * dpr);
    gl.uniform1i(u.uHover, hover);
    gl.uniform1i(u.uSelected, selected);
    gl.uniform1i(u.uPulseGroup, pulsedGroup);
    gl.uniform1i(u.uPulseShape, pulsedShape);
    gl.uniform1f(u.uPulseT, pulsing() ? (now - pulseStart) / 1000 / pulseSeconds : 1);
    gl.uniform1i(u.uShaped, shaped ? 1 : 0);
    gl.uniform1i(u.uBevel, detailLevel >= DetailLevel.Boxes ? 1 : 0);
    gl.uniform3fv(u.uInk, theme.ink);
    gl.uniform3fv(u.uClear, theme.clear);
    gl.uniform3fv(u.uOutline, theme.outline);
    gl.uniform3fv(u.uLight, lightDir);
    gl.uniform1f(u.uAmbient, ambient);
    gl.uniform3fv(u.uKey, keyLight);
    gl.uniform3fv(u.uFillLight, fillLight);
    gl.uniform3fv(u.uRim, rimDir);
    gl.uniform3fv(u.uRimLight, rimLight);
    gl.uniform1f(u.uShine, detailLevel >= DetailLevel.Boxes ? 1 : 0.5);
    gl.uniform1f(u.uNowMs, now);
    gl.uniform1i(u.uPick, pickMode ? 1 : 0);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, palette);
    gl.uniform1i(u.uPalette, 0);
    for (let i = 0; i < imageLevels.length; i++) {
      gl.activeTexture(gl.TEXTURE1 + i);
      gl.bindTexture(gl.TEXTURE_2D_ARRAY, levelTextures[i] ?? emptyLevel);
      gl.uniform1i(u["uLevel" + i], 1 + i);
    }
    const shapeUnit = 1 + imageLevels.length;
    gl.activeTexture(gl.TEXTURE0 + shapeUnit);
    gl.bindTexture(gl.TEXTURE_2D_ARRAY, shapeAtlas ?? emptyLevel);
    gl.uniform1i(u.uShapeAtlas, shapeUnit);
    gl.activeTexture(gl.TEXTURE0);
  }

  function flushTexWords() {
    if (texDirty.size === 0) return;
    gl.bindBuffer(gl.ARRAY_BUFFER, texBuffer);
    for (const i of texDirty) {
      if (i < count) gl.bufferSubData(gl.ARRAY_BUFFER, i * 12, texWords, i * 3, 3);
    }
    texDirty.clear();
  }

  function draw() {
    if (count === 0) return;
    gl.bindVertexArray(vao);
    gl.drawElementsInstanced(gl.TRIANGLES, 18, gl.UNSIGNED_SHORT, 0, count);
    gl.bindVertexArray(null);
  }

  /** The floor, over the cards' own pass: depth-tested, so a line goes behind whatever stands on it. */
  function drawFloorLines() {
    if (linePoints === 0) return;
    gl.useProgram(lineProgram);
    gl.uniformMatrix4fv(lineViewProj, false, viewProj);
    gl.uniform3fv(lineOrigin, origin);
    // the page's own ink, well faded: a rule on the floor, not a line drawn over the picture
    gl.uniform4f(lineColor, theme.ink[0], theme.ink[1], theme.ink[2], 0.34);
    gl.disable(gl.CULL_FACE);
    gl.disable(gl.SAMPLE_ALPHA_TO_COVERAGE);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
    gl.bindVertexArray(lineVao);
    gl.drawArrays(gl.LINES, 0, linePoints);
    gl.bindVertexArray(null);
    gl.disable(gl.BLEND);
  }

  /**
   * Solids are opaque and the depth buffer sorts them; nothing here is ever blended. The outline of
   * a raymarched solid is the one thing without geometry of its own, so its edge pixels say how much
   * of themselves they cover and the multisampling turns that into coverage - which the pick pass,
   * drawing into a single pixel that has no samples to spread, must not do.
   */
  function drawingState(coverage: boolean) {
    gl.disable(gl.BLEND);
    gl.enable(gl.DEPTH_TEST);
    gl.depthFunc(gl.LEQUAL);
    gl.enable(gl.CULL_FACE);
    gl.cullFace(gl.BACK);
    gl.frontFace(gl.CCW);
    if (coverage) gl.enable(gl.SAMPLE_ALPHA_TO_COVERAGE);
    else gl.disable(gl.SAMPLE_ALPHA_TO_COVERAGE);
  }

  /**
   * The guard. A frame is timed by how long it took the last one to come back, which under a
   * continuous animation is what the picture actually costs; when it runs long for a while a level
   * of detail is given up, and when it has been comfortable for a good while it is taken back. The
   * point is that a set nobody measured beforehand cannot bring the tab to a halt: it gets slower
   * for half a second and then simpler.
   */
  function measure(intervalMs: number) {
    frameMs = frameMs * 0.88 + Math.min(2000, intervalMs) * 0.12;
    if (frameMs > slowFrameMs) {
      fastRun = 0;
      if (++slowRun >= slowFramesBeforeDrop && detailLevel > DetailLevel.Flat) {
        detailLevel = (detailLevel - 1) as DetailLevel;
        slowRun = 0;
        frameMs = 16;
      }
    } else if (frameMs < fastFrameMs) {
      slowRun = 0;
      if (++fastRun >= fastFramesBeforeRaise && detailLevel < ceiling) {
        detailLevel = (detailLevel + 1) as DetailLevel;
        fastRun = 0;
      }
    } else {
      slowRun = 0;
      fastRun = 0;
    }
  }

  function frame(now: number) {
    raf = 0;
    frameNow = now;
    if (destroyed) return;
    const dt = lastFrame === 0 ? 1 / 60 : Math.min(0.1, (now - lastFrame) / 1000);
    const cameraMoving = cam.moving() || zoomTo !== null;
    if (cam.moving()) cam.step(dt, now);
    stepZoom(dt);
    const depthMoving = depthProgress(now) < 1;
    if (!dirty && !cardsMoving && !depthMoving && !pulsing() && !cameraMoving && !fading(now) && texDirty.size === 0) {
      lastFrame = 0;
      return;
    }
    if (lastFrame !== 0) measure(now - lastFrame);
    lastFrame = now;
    if (cardsMoving && elapsed(now) > duration + maxDelay) cardsMoving = false;
    if (pulsing() && (now - pulseStart) / 1000 >= pulseSeconds) {
      pulsedGroup = -1;
      pulsedShape = -1;
    }

    flushTexWords();
    const { eye } = matrices();
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.viewport(0, 0, width, height);
    gl.clearColor(theme.clear[0], theme.clear[1], theme.clear[2], 1);
    gl.clearDepth(1);
    const prog = currentProgram();
    drawingState(prog === solid);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    setUniforms(prog, now, false, eye);
    draw();
    drawFloorLines();
    dirty = false;
    frameCallback?.();
    if (cardsMoving || depthProgress(now) < 1 || pulsing() || cam.moving() || zoomTo !== null || fading(now) || texDirty.size > 0) schedule();
    else lastFrame = 0;
  }

  function upload(buffer: WebGLBuffer, data: ArrayBufferView) {
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.bufferData(gl.ARRAY_BUFFER, data, gl.DYNAMIC_DRAW);
  }

  function planTiming(stagger: number, fresh: Uint8Array | null) {
    if (timing.length !== count * 2) timing = new Float32Array(count * 2);
    let h = 0x9e3779b9;
    for (let i = 0; i < count; i++) {
      h = (h ^ (h << 13)) >>> 0;
      h = (h ^ (h >>> 17)) >>> 0;
      h = (h ^ (h << 5)) >>> 0;
      const jitter = (h & 0xffff) / 0xffff;
      timing[i * 2] = stagger * (0.72 * (i / Math.max(1, count - 1)) + 0.28 * jitter);
      // 0 settled, 1 arriving, 2 leaving - handed over as it is, since a leaver is drawn differently
      timing[i * 2 + 1] = fresh === null ? 0 : fresh[i];
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

  /** Every card's thickness right now, mid-change or not: what a new change leaves from. */
  function currentDepths(): Float32Array {
    const p = depthProgress(performance.now());
    if (p >= 1) return depths.slice(0, count);
    const t = p * p * p * (p * (p * 6 - 15) + 10);
    const out = new Float32Array(count);
    for (let i = 0; i < count; i++) out[i] = depthFrom[i] + (depths[i] - depthFrom[i]) * t;
    return out;
  }

  /** Where every card stands along the depth axis right now, mid-move or not: what a change leaves from. */
  function currentRows(): Float32Array {
    if (!cardsMoving) return rowTo.slice(0, count);
    const time = elapsed(performance.now());
    const out = new Float32Array(count);
    for (let i = 0; i < count; i++) {
      const s = ease((time - timing[i * 2]) / duration);
      out[i] = rowFrom[i] + (rowTo[i] - rowFrom[i]) * s;
    }
    return out;
  }

  function uploadRows() {
    const pairs = new Float32Array(count * 2);
    let low = 0;
    let high = 0;
    for (let i = 0; i < count; i++) {
      pairs[i * 2] = rowFrom[i];
      pairs[i * 2 + 1] = rowTo[i];
      const z = rowTo[i];
      if (z < low) low = z;
      if (z > high) high = z;
    }
    upload(rowBuffer, pairs);
    rowSpan = [low, high];
  }

  function uploadDepths() {
    const pairs = new Float32Array(count * 2);
    for (let i = 0; i < count; i++) {
      pairs[i * 2] = depthFrom[i];
      pairs[i * 2 + 1] = depths[i];
    }
    upload(depthBuffer, pairs);
    let top = 0;
    for (let i = 0; i < count; i++) {
      if (depths[i] > top) top = depths[i];
      if (depthFrom[i] > top) top = depthFrom[i];
    }
    maxDepth = Math.max(0.05, top);
  }

  function sizeOfLevel(level: number): [number, number] {
    const w = imageLevels[level];
    return [w, Math.round(w * imageShare)];
  }

  function pickAt(cssX: number, cssY: number): number {
    if (count === 0) return -1;
    const px = Math.floor(cssX * dpr);
    const py = Math.floor(cssY * dpr);
    if (px < 0 || py < 0 || px >= width || py >= height) return -1;
    flushTexWords();
    const { eye } = matrices();
    gl.bindFramebuffer(gl.FRAMEBUFFER, pickFramebuffer);
    // the whole canvas is drawn, shifted so that the pixel under the pointer is the one pixel the
    // framebuffer has; everything else is rasterized away
    gl.viewport(-px, -(height - 1 - py), width, height);
    drawingState(false);
    gl.clearColor(1, 1, 1, 1);
    gl.clearDepth(1);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    setUniforms(currentProgram(), performance.now(), true, eye);
    draw();
    gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pickPixel);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    const id = pickPixel[0] | (pickPixel[1] << 8) | (pickPixel[2] << 16);
    return id === 0xffffff || id >= count ? -1 : id;
  }

  /** Where one card stands right now, mid-move or not; O(1), unlike asking for all of them. */
  function cardCentre(i: number): Vec3 {
    const time = elapsed(performance.now());
    const travelled = cardsMoving ? ease((time - timing[i * 2]) / duration) : 1;
    const x = from[i * 2] + (to[i * 2] - from[i * 2]) * travelled;
    const y = from[i * 2 + 1] + (to[i * 2 + 1] - from[i * 2 + 1]) * travelled;
    const z = i < rowTo.length ? rowFrom[i] + (rowTo[i] - rowFrom[i]) * travelled : 0;
    const thick = i < depths.length ? depths[i] : defaultDepth;
    return [x + 0.5, -(y + 0.5), z + thick * 0.5];
  }

  /** The middle of the whole picture: what the camera falls back to when the pointer is over nothing. */
  function sceneCentre(): Vec3 {
    return [(bounds.x0 + bounds.x1) / 2, -(bounds.y0 + bounds.y1) / 2, (rowSpan[0] + rowSpan[1] + maxDepth) / 2];
  }

  /**
   * The point of the picture under a pixel of the canvas: what a drag or a wheel step works about.
   *
   * This is the whole of what makes the camera answerable. A view that turns about a focus distance
   * of its own turns about nothing in particular - the further that distance is from what is being
   * looked at, the more the picture swings when the hand asks it to turn - and a slide measured at
   * that distance runs faster or slower than the hand. So the card under the pointer is asked for,
   * by the same one-pixel pass a click uses; whatever comes back is a real point of the picture, so
   * turning holds it still under the pointer and sliding moves it exactly as far as the hand does.
   *
   * Over empty space it is the plane through the middle of the picture facing the camera - never the
   * floor, however tempting: a floor met almost edge on is met hundreds of units away, and turning
   * about a point out there swings the camera right across the picture for a short drag. The middle
   * of the picture is always about as far off as what is being looked at, which is the point.
   */
  function pointUnder(cssX: number, cssY: number): Vec3 {
    const i = pickAt(cssX, cssY);
    if (i >= 0) return cardCentre(i);
    const cssW = width / dpr;
    const cssH = height / dpr;
    const dir = cam.ray((2 * cssX) / Math.max(1, cssW) - 1, 1 - (2 * cssY) / Math.max(1, cssH), cssW / Math.max(1, cssH));
    const centre = sceneCentre();
    return rayPlane(cam.pos, dir, centre, scale(cam.forward(), -1)) ?? centre;
  }

  /**
   * The same, asked for once per gesture rather than once per notch of the wheel.
   *
   * This matters more than it looks. Asking costs a whole extra pass over the cards and a read back
   * from the card the driver has not finished drawing yet, which at three quarters of a million of
   * them is about what a frame costs - so asking per notch halves the frame rate for as long as the
   * wheel is turning, and that is felt as a stutter. Worse, the answer would be a DIFFERENT point
   * each time, because by then the picture has moved: the zoom would keep changing its mind about
   * what it was closing in on. The window slides from the last USE, so a wheel spun without moving
   * the pointer asks once and keeps the answer for the whole spin.
   */
  function pointUnderCached(cssX: number, cssY: number): Vec3 {
    const now = performance.now();
    if (asked !== null && now - asked.at < gestureGapMs && Math.abs(asked.x - cssX) < 5 && Math.abs(asked.y - cssY) < 5) {
      asked.at = now;
      return asked.point;
    }
    const point = pointUnder(cssX, cssY);
    asked = { x: cssX, y: cssY, at: now, point };
    return point;
  }

  /**
   * The camera closing on where the wheel is taking it. A notch does not start an animation of its
   * own: it moves the target, and this walks the camera toward it by the same fraction of what is
   * left every second, which is the one way of following a target that does not care how often or
   * how unevenly the target moves. A tween per notch, restarted each time, gives a spin of the wheel
   * a stop-start crawl - an ease-out begun again and again never gets past its own slow beginning.
   *
   * The distance closes in log space, so a step feels the same size wherever the camera is; the
   * position closes straight, which stays exactly on the line to the anchor because the target is
   * on that line to begin with.
   */
  function stepZoom(dt: number) {
    const goal = zoomTo;
    if (goal === null) return;
    const k = 1 - Math.exp(-zoomFollowRate * dt);
    cam.pos = [cam.pos[0] + (goal.pos[0] - cam.pos[0]) * k, cam.pos[1] + (goal.pos[1] - cam.pos[1]) * k, cam.pos[2] + (goal.pos[2] - cam.pos[2]) * k];
    cam.dist = Math.exp(Math.log(cam.dist) + (Math.log(goal.dist) - Math.log(cam.dist)) * k);
    // near enough to be there: arriving exactly is what lets the frames stop
    if (distance(cam.pos, goal.pos) < Math.max(1e-4, goal.dist * 1e-4) && Math.abs(Math.log(cam.dist / goal.dist)) < 1e-4) {
      cam.pos = [...goal.pos];
      cam.dist = goal.dist;
      zoomTo = null;
    }
  }

  /** A vector turned about a unit axis (Rodrigues). */
  function turnAbout(v: Vec3, axis: Vec3, angle: number): Vec3 {
    const c = Math.cos(angle);
    const s = Math.sin(angle);
    const k = dot(axis, v) * (1 - c);
    const x = cross(axis, v);
    return [v[0] * c + x[0] * s + axis[0] * k, v[1] * c + x[1] * s + axis[1] * k, v[2] * c + x[2] * s + axis[2] * k];
  }

  /** How far the heading may tip, given the camera's own limits. */
  function clampPitch(pitch: number): number {
    return Math.max(cam.pitchLimit[0], Math.min(cam.pitchLimit[1], pitch));
  }





  /**
   * How far a pointer pixel turns the view, sideways and up. Each is measured against the canvas
   * across that way, so a drag from one edge to the other is about the same turn whatever shape the
   * canvas is - the panel here is often much wider than it is tall, and one rate for both would spin
   * the picture right round for a short drag upward.
   */
  function turnRates(): [number, number] {
    const turn = Math.PI * 1.4;
    return [turn / Math.max(1, width / dpr), turn / Math.max(1, height / dpr)];
  }

  /** The camera as the pictures read it: where it looks on the plane of the layout, and its zoom there. */
  function flatCamera(): Camera {
    const t = cam.target();
    return { x: t[0], y: -t[1], z: t[2], zoom: zoomDev() / dpr };
  }

  const field: CardField3D = {
    setCards(n, start, targets, fresh = null) {
      count = n;
      to = targets;
      from = start ?? targets;
      cardsMoving = n > 0 && (start !== null || fresh !== null);
      moveStart = performance.now();
      duration = moveDuration;
      planTiming(cardsMoving ? moveStagger : 0, fresh);
      upload(fromBuffer, from);
      upload(toBuffer, to);
      const groups = new Uint16Array(n);
      upload(groupBuffer, groups);
      upload(shapeBuffer, groups);
      shaped = false;
      depths = new Float32Array(n).fill(defaultDepth);
      depthFrom = depths.slice();
      depthStart = 0;
      depthDuration = 0;
      uploadDepths();
      rowFrom = new Float32Array(n);
      rowTo = new Float32Array(n);
      uploadRows();
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
      planTiming(stagger, null);
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
    setDepths(next, seconds = depthSeconds) {
      if (count === 0) return;
      const wanted = new Float32Array(count);
      if (next === null) wanted.fill(defaultDepth);
      else for (let i = 0; i < count; i++) wanted[i] = Math.max(0.02, next[i] ?? defaultDepth);
      let same = depths.length === count;
      if (same) for (let i = 0; i < count; i++) if (depths[i] !== wanted[i]) { same = false; break; }
      if (same && depthProgress(performance.now()) >= 1) return;
      depthFrom = seconds > 0 ? currentDepths() : wanted.slice();
      if (depthFrom.length !== count) depthFrom = wanted.slice();
      depths = wanted;
      depthStart = performance.now();
      depthDuration = seconds;
      uploadDepths();
      dirty = true;
      schedule();
    },
    setRows(next, from) {
      if (count === 0) return;
      const wanted = new Float32Array(count);
      if (next !== null) for (let i = 0; i < count; i++) wanted[i] = next[i] ?? 0;
      const leaving = from === undefined ? currentRows() : from;
      rowFrom = leaving !== null && leaving.length === count ? Float32Array.from(leaving) : wanted.slice();
      rowTo = wanted;
      uploadRows();
      dirty = true;
      schedule();
    },
    rows: currentRows,
    setFloorLines(points) {
      linePoints = Math.floor(points.length / 3);
      gl.bindBuffer(gl.ARRAY_BUFFER, lineBuffer);
      gl.bufferData(gl.ARRAY_BUFFER, points, gl.DYNAMIC_DRAW);
      dirty = true;
      schedule();
    },
    setGroups(assignment, colors) {
      upload(groupBuffer, assignment);
      gl.bindTexture(gl.TEXTURE_2D, palette);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, Math.max(1, colors.length / 4), 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, colors);
      dirty = true;
      schedule();
    },
    setShapes(assignment) {
      if (assignment === null) {
        shaped = false;
      } else {
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
      // the floor under the shading: high on a light page, where a body in shadow would otherwise
      // be a hole in the picture, and low on a dark one, where the light does the shaping
      const luminance = 0.2126 * t.clear[0] + 0.7152 * t.clear[1] + 0.0722 * t.clear[2];
      // The fill and the rim keep a body in shadow legible on their own, so the floor is not what
      // has to do it - but it is what decides how much of the picture sits in the well-lit middle,
      // and the shoulder above means raising it no longer costs the bright faces their colour.
      ambient = luminance > 0.5 ? 0.62 : 0.44;
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
    pulseFade(): PulseFade | null {
      if (!pulsing()) return null;
      return { group: pulsedGroup, shape: pulsedShape, amount: pulseAmountAt(frameNow) };
    },
    pick: pickAt,
    fit(next, paddingPx, seconds) {
      bounds = next;
      const cssW = width / dpr;
      const cssH = height / dpr;
      // the rows reach from z0 back to z1, and the thickest card standing on a row reaches forward
      // of it, so the picture is that span grown by one card's depth
      const back = next.z0 ?? 0;
      const front = (next.z1 ?? 0) + maxDepth;
      const centre: Vec3 = [(next.x0 + next.x1) / 2, -(next.y0 + next.y1) / 2, (back + front) / 2];
      const hx = (next.x1 - next.x0) / 2 + cardFill / 2;
      const hy = (next.y1 - next.y0) / 2 + cardFill / 2;
      const hz = Math.max(0.05, (front - back) / 2);
      const t = Math.tan(cam.fov / 2);
      const aspect = cssW / Math.max(1, cssH);
      const roomW = Math.max(0.15, 1 - (2 * paddingPx) / Math.max(1, cssW));
      const roomH = Math.max(0.15, 1 - (2 * paddingPx) / Math.max(1, cssH));
      // The nearest the camera can stand and still have every corner of the picture on the screen.
      // A corner sits at its own depth, so what it takes up is its offset across the view over its
      // own distance rather than over the middle's: measuring the corners one by one is what keeps a
      // picture seen at an angle from being fitted as though its near edge were as far off as its far
      // one, which leaves it a third of the room it has.
      const right = cam.right();
      const up = cam.up();
      const forward = cam.forward();
      let dist = cam.minDist;
      for (const sx of [-1, 1]) {
        for (const sy of [-1, 1]) {
          for (const sz of [-1, 1]) {
            const p: Vec3 = [sx * hx, sy * hy, sz * hz];
            const along = p[0] * forward[0] + p[1] * forward[1] + p[2] * forward[2];
            const across = Math.abs(p[0] * up[0] + p[1] * up[1] + p[2] * up[2]);
            const sideways = Math.abs(p[0] * right[0] + p[1] * right[1] + p[2] * right[2]);
            dist = Math.max(dist, across / (t * roomH) - along, sideways / (t * aspect * roomW) - along);
          }
        }
      }
      const pose = cam.poseLookingAt(centre, dist);
      zoomTo = null;
      if (seconds > 0) {
        cam.animateTo(pose, seconds * 1000);
      } else {
        cam.stop();
        cam.setPose(pose);
      }
      dirty = true;
      schedule();
    },
    hold(channel, cssX, cssY) {
      zoomTo = null;
      // the camera has no momentum of its own here: it goes where the hand puts it and stops when
      // the hand stops. A view with weight is right for flying through a graph and wrong for reading
      // a chart, where a short drag that carries on turning for another second is just a view lost.
      cam.stop();
      held = channel === "look" ? null : pointUnder(cssX, cssY);
      asked = null;
      schedule();
    },
    release() {
      held = null;
      schedule();
    },
    /**
     * The picture turns about the point taken hold of, and that point stays exactly where it is on
     * the screen while it does: the camera and its heading are turned together about it, which is a
     * rigid turn of the whole frame, and a rigid turn leaves the centre of itself alone.
     */
    orbit(dxPx, dyPx) {
      const [rx, ry] = turnRates();
      const pivot = held ?? cam.target();
      const dYaw = dxPx * rx;
      const pitch = clampPitch(cam.pitch - dyPx * ry);
      const dPitch = pitch - cam.pitch;
      const yaw = cam.yaw + dYaw;
      // a turn about the world's up axis raises the yaw, which is the other way round from the
      // right-handed turn of the same axis; the tip is about the camera's own right, after the yaw
      let arm = turnAbout(sub(cam.pos, pivot), [0, 1, 0], -dYaw);
      arm = turnAbout(arm, [Math.cos(yaw), 0, Math.sin(yaw)], dPitch);
      cam.yaw = yaw;
      cam.pitch = pitch;
      cam.pos = add(pivot, arm);
      // the focus distance follows what is being turned about, so a slide or a wheel step after the
      // turn is measured against the same thing
      cam.dist = Math.max(cam.minDist, distance(cam.pos, pivot));
      dirty = true;
      schedule();
    },
    look(dxPx, dyPx) {
      const [rx, ry] = turnRates();
      cam.yaw += dxPx * rx * 0.7;
      cam.pitch = clampPitch(cam.pitch - dyPx * ry * 0.7);
      dirty = true;
      schedule();
    },
    /**
     * The picture slides with the hand: the camera moves across its own view by exactly what a pixel
     * is worth at the depth of the point taken hold of, so that point stays under the pointer
     * however near or far the picture is.
     */
    panBy(dxPx, dyPx) {
      const pivot = held ?? cam.target();
      const reach = Math.max(cam.minDist, distance(pivot, cam.pos));
      const perPixel = (2 * reach * Math.tan(cam.fov / 2)) / Math.max(1, height / dpr);
      cam.pos = add(cam.pos, add(scale(cam.right(), -dxPx * perPixel), scale(cam.up(), dyPx * perPixel)));
      dirty = true;
      schedule();
    },
    driftBy(dxPx, dyPx, seconds) {
      const pivot = cam.target();
      const reach = Math.max(cam.minDist, distance(pivot, cam.pos));
      const perPixel = (2 * reach * Math.tan(cam.fov / 2)) / Math.max(1, height / dpr);
      const move = add(scale(cam.right(), -dxPx * perPixel), scale(cam.up(), dyPx * perPixel));
      zoomTo = null;
      cam.animateTo({ pos: add(cam.pos, move), yaw: cam.yaw, pitch: cam.pitch, dist: cam.dist }, seconds * 1000);
      dirty = true;
      schedule();
    },
    eye: () => [...cam.pos],
    /**
     * The wheel closes in on the point of the plane under the pointer, which stays under it, rather
     * than flying along the line of sight the way the 3D graph's wheel does. A graph is a cloud and
     * flying through it is how it is read; a picture of cards is a plane, and flying through a plane
     * leaves the picture behind and the camera looking at nothing.
     *
     * A step is taken from where the wheel was already heading rather than from where the camera has
     * got to, so spinning it keeps closing in instead of chasing its own easing.
     */
    zoomAt(amount, cssX, cssY) {
      const base = zoomTo ?? cam.pose();
      // what is under the pointer, whatever it is: the card, the floor, or the middle of the picture
      const anchor = pointUnderCached(cssX, cssY);
      const reach = Math.max(cam.minDist, distance(anchor, base.pos));
      const far = Math.max(cam.minDist * 4, sceneRadius() * 30);
      // a fifth of the way in per notch, geometric, so a step is the same size to the eye however
      // near or far the picture is - and small enough that a spin lands where it was aimed
      const wanted = Math.max(cam.minDist, Math.min(far, reach * Math.exp(-amount * 0.2)));
      const k = wanted / reach;
      // the target is on the line from the anchor through where the camera is heading, so the follow
      // below can walk straight toward it without leaving that line
      zoomTo = {
        pos: [anchor[0] + (base.pos[0] - anchor[0]) * k, anchor[1] + (base.pos[1] - anchor[1]) * k, anchor[2] + (base.pos[2] - anchor[2]) * k],
        yaw: base.yaw,
        pitch: base.pitch,
        dist: Math.max(cam.minDist, base.dist * k),
      };
      dirty = true;
      schedule();
    },
    stop() {
      zoomTo = null;
      held = null;
      cam.stop();
      schedule();
    },
    detail: () => ({ level: detailLevel, ceiling, frameMs }),
    setDetailCeiling(next) {
      ceiling = next;
      if (detailLevel > next) detailLevel = next;
      fastRun = 0;
      dirty = true;
      schedule();
    },
    pose: () => ({ yaw: cam.yaw, pitch: cam.pitch }),
    setHeading(yaw, pitch) {
      zoomTo = null;
      const target = cam.target();
      cam.setPose(cam.poseLookingAt(target, cam.dist, yaw, pitch));
      dirty = true;
      schedule();
    },
    worldToCss(x, y, z = 0) {
      // the point of the picture, through the matrix of the frame just drawn
      const wx = x - origin[0];
      const wy = -y - origin[1];
      const wz = z - origin[2];
      const cx = viewProj[0] * wx + viewProj[4] * wy + viewProj[8] * wz + viewProj[12];
      const cy = viewProj[1] * wx + viewProj[5] * wy + viewProj[9] * wz + viewProj[13];
      const cw = viewProj[3] * wx + viewProj[7] * wy + viewProj[11] * wz + viewProj[15];
      // at or behind the camera there is no answer: dividing anyway would put the point on the
      // canvas mirrored through the middle, which is worse than saying nothing
      if (cw <= 1e-6) return [NaN, NaN];
      return [((cx / cw + 1) / 2) * (width / dpr), ((1 - cy / cw) / 2) * (height / dpr)];
    },
    camera: flatCamera,
    cameraTarget: flatCamera,
    size: () => ({ width: width / dpr, height: height / dpr, dpr }),
    moving: () => cardsMoving || depthProgress(performance.now()) < 1 || pulsing() || cam.moving() || zoomTo !== null || fading(performance.now()),
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
    // Tiles are the flat picture's way of going on past the largest level: the part of one card's
    // picture in view, cut out at the width of the canvas. A face of a solid seen at an angle has no
    // such part, so the solid stops at the largest level and cardMedia is told not to ask.
    setTiles() {},
    uploadTile() {},
    freeTiles() {},
    destroy() {
      destroyed = true;
      if (raf !== 0) cancelAnimationFrame(raf);
      raf = 0;
      gl.deleteBuffer(meshBuffer);
      gl.deleteBuffer(indexBuffer);
      gl.deleteBuffer(fromBuffer);
      gl.deleteBuffer(toBuffer);
      gl.deleteBuffer(timingBuffer);
      gl.deleteBuffer(groupBuffer);
      gl.deleteBuffer(texBuffer);
      gl.deleteBuffer(shapeBuffer);
      gl.deleteBuffer(depthBuffer);
      gl.deleteBuffer(rowBuffer);
      gl.deleteBuffer(lineBuffer);
      gl.deleteVertexArray(vao);
      gl.deleteVertexArray(lineVao);
      gl.deleteTexture(palette);
      gl.deleteTexture(emptyLevel);
      if (shapeAtlas !== null) gl.deleteTexture(shapeAtlas);
      for (const t of levelTextures) if (t !== null) gl.deleteTexture(t);
      gl.deleteTexture(pickTexture);
      gl.deleteRenderbuffer(pickDepth);
      gl.deleteFramebuffer(pickFramebuffer);
      gl.deleteProgram(plain);
      gl.deleteProgram(solid);
      gl.deleteProgram(lineProgram);
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
