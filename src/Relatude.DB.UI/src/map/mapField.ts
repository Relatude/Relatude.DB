/**
 * The map, drawn with WebGL2: the flat world and the globe, from the same buffers and the same
 * shaders.
 *
 * The one thing that differs between the two pictures is where a place lands on the screen, and
 * that is a dozen lines of the vertex shader (`placeOf` below). Everything else - the coastlines,
 * the grid of latitude and longitude, a shading of the countries, and the nodes themselves - is one
 * set of vertex buffers holding LONGITUDE AND LATITUDE and nothing else. Changing projection, or
 * going from the flat map to the globe, is a uniform: no geometry is rebuilt, nothing is projected
 * on the processor, and the marks of a million nodes do not move through javascript to get there.
 *
 * Four things are drawn, in this order:
 *
 *   the ground   a grid of quads over the whole world, which is a ball in the globe and a sheet on
 *                the flat map. It carries the shading of the countries when there is one, as a
 *                raster of country NUMBERS sampled by longitude and latitude and looked up in a
 *                palette, so one texture serves both pictures and no filter can ever blend two
 *                countries into a third. Skipped on the flat map with nothing to shade.
 *   the lines    coastlines, borders and the graticule, as instanced quads in screen space rather
 *                than GL lines: a GL line is one pixel wide and nothing else, and a map's lines
 *                have to hold their weight against the marks lying on top of them. Two sets of
 *                them, coarse and fine, and the zoom picks (see coarseTolerance).
 *   the marks    a dot or a pin per node, as point sprites.
 *   the heat     the same nodes counted into a small texture, blurred, and read through a colour
 *                ramp - a picture of the crowd rather than of its members (see the note at drawHeat).
 *
 * On the globe, anything on the far side is thrown outside the clip volume by a facing test rather
 * than hidden with the depth buffer, which is why the depth test is on for the ground alone.
 *
 * What is NOT here: the cluster bubbles and their counts. There are a few hundred of those and each
 * carries a number written in words - which is the one thing a canvas does better than a shader -
 * so they are drawn in 2d over the top (see clusters.ts).
 */

import { multiply, normalize, perspective, raySphere, scale as scaled, view as viewMatrix, type Mat4, type Vec3 } from "../graph3d/math";
import { mercatorLimit, naturalWidth, projectionIndex, type Projection, type View } from "./projection";
import { world } from "./worldMap";

export interface MapTheme {
  /** the page behind it all: what the canvas is cleared with */
  clear: RGB;
  /** the ball, and the sheet the flat map's countries are shaded on */
  ground: RGB;
  /** coastlines and borders */
  line: RGB;
  /** the lines of latitude and longitude */
  grid: RGB;
  /** the halo round the globe's edge, which is what makes a flat disc read as a ball */
  glow: RGB;
  /** what a pin is outlined in, so one pin on top of another is still two pins */
  outline: RGB;
}

/** 0..255 per channel, as the rest of the app keeps colours. */
export type RGB = [number, number, number];

/** Where the camera is on the globe: the place it looks straight down at, and how close it has come. */
export interface GlobeCamera {
  lat: number;
  lon: number;
  /** 1 is the whole globe in view; larger closes in. */
  zoom: number;
}

/** How the nodes are drawn. Clusters and shaded countries draw no marks at all. */
export type MapMarks = "dots" | "pins" | "heat" | "none";

/** Everything one frame needs to know. */
export interface MapScene {
  globe: boolean;
  projection: Projection;
  /** the flat map's window on the world */
  view: View;
  camera: GlobeCamera;
  marks: MapMarks;
  /** how large one mark is, in css pixels */
  size: number;
  /** and how solid, so a crowd of them reads as a crowd */
  alpha: number;
  /** how many marks to draw at most; the rest of the nodes are still there, just not drawn */
  limit: number;
  /** how far a node's heat spreads, in css pixels */
  radius: number;
  /** the lines of latitude and longitude */
  graticule: boolean;
  /** whether the ground carries the picture given to setSurface */
  surface: boolean;
}

export interface MapField {
  resize(cssWidth: number, cssHeight: number, dpr: number): void;
  setTheme(theme: MapTheme): void;
  /** The nodes, in degrees; the same arrays the view keeps, uploaded as they are. */
  setPoints(lon: Float32Array, lat: Float32Array, count: number): void;
  /** rgb bytes per colour group, and the group of each node; a null assignment paints them all with the first colour. */
  setColors(palette: Uint8Array, assignment: Uint16Array | null): void;
  /** The colours a heat field is read through: rgba bytes, low end first. */
  setRamp(ramp: Uint8Array): void;
  /**
   * The world as country numbers, one byte a cell, equirectangular (see map/countries.ts). Uploaded
   * once - it never changes - and painted with the palette below, so recolouring costs a kilobyte.
   */
  setSurface(index: Uint8Array, width: number, height: number): void;
  /** What each country number is painted with: 256 rgba entries, 0 being the sea and painted with nothing. */
  setSurfaceColors(colors: Uint8Array): void;
  draw(scene: MapScene): void;
  /**
   * Where a place lands on the canvas, in css pixels, written into `out`; the answer is whether it
   * is drawn at all. Made once per frame and called once per node, so it writes into the caller's
   * array rather than handing back a new one a million times.
   */
  project(scene: MapScene): (lon: number, lat: number, out: Float32Array) => boolean;
  /** The place under a point of the canvas, as [lat, lon], or null where there is no world there. */
  placeAt(scene: MapScene, px: number, py: number): [number, number] | null;
  /** How many degrees a pixel covers around a place: what a pick radius is converted with. */
  degreesPerPixel(scene: MapScene, lat: number): number;
  destroy(): void;
}

// ---- the globe's camera ----

const fov = (38 * Math.PI) / 180;
/** How far away the whole ball just fits in the view. */
const fitDistance = 1.08 / Math.sin(fov / 2);
export const maxGlobeZoom = 200;

/**
 * How far the camera stands from the middle of the ball. Zooming closes the gap to the SURFACE
 * rather than to the middle, so doubling the zoom halves how much of the ground is in view all the
 * way in - which is what a map is expected to do, and what a distance divided by the zoom stops
 * doing the moment it would put the camera inside the world.
 */
function distanceOf(camera: GlobeCamera): number {
  return 1 + (fitDistance - 1) / Math.max(1, Math.min(maxGlobeZoom, camera.zoom));
}

/** A place, as a point on the unit sphere: x east at the prime meridian, y north, z out through (0°, 0°). */
function unitVector(lat: number, lon: number): Vec3 {
  const φ = (lat * Math.PI) / 180;
  const λ = (lon * Math.PI) / 180;
  const c = Math.cos(φ);
  return [c * Math.sin(λ), Math.sin(φ), c * Math.cos(λ)];
}

/** The place a unit vector stands for, as [lat, lon] in degrees. */
function placeOf(x: number, y: number, z: number): [number, number] {
  return [(Math.asin(Math.max(-1, Math.min(1, y))) * 180) / Math.PI, (Math.atan2(x, z) * 180) / Math.PI];
}

// ---- how much geometry the world is ----

/**
 * How long a piece of line may be before it is broken up, in degrees. A step between two places is
 * a straight line only on an equirectangular map: on the globe it has to hug the ball, and on the
 * others it bends. Broken up once, here, into pieces short enough for every projection, rather than
 * rebuilt whenever the projection changes.
 */
const maxSegmentDegrees = 2;
/** How fine the ground is: a grid this many quads across and half that down. */
const groundColumns = 256;
const groundRows = 128;
/** How far above the surface the lines and the marks stand on the globe, so the ball does not swallow them. */
const lineLift = 1.0015;
const markLift = 1.003;
/**
 * How far a point of an outline may be moved to leave it out, in degrees, when the world is drawn
 * coarsely. The map holds every point the source has, which is what a country looks like at a
 * city's zoom and far more than a world map can show: at that size the extra points are a tenth of
 * a pixel apart, and all they do is fill the coasts with speckle. So a second, simplified set is
 * built once at load, and whichever suits the zoom is drawn (see detailBelow).
 */
const coarseTolerance = 0.2;
/** Below this many degrees to the pixel the coarse outlines would start to show, so the fine ones are drawn. */
const detailBelow = 0.1;
/** The heat field is counted at this fraction of the canvas: fine enough to blur smoothly, small enough to blur cheaply. */
const heatShrink = 4;
/** and blurred with at most this many taps a side, whatever the spread asks for */
const maxBlurTaps = 28;

export function createMapField(canvas: HTMLCanvasElement): MapField | null {
  const context = canvas.getContext("webgl2", { antialias: true, alpha: false, premultipliedAlpha: false, powerPreference: "high-performance" });
  if (!context) return null;
  const gl: WebGL2RenderingContext = context;
  // rendering a heat field needs somewhere to add up the nodes that is not eight bits deep; without
  // it the count saturates in the crowded places, which are the ones the picture is about
  const float = gl.getExtension("EXT_color_buffer_float") !== null;

  let width = 1;
  let height = 1;
  let dpr = 1;
  let theme: MapTheme = { clear: [255, 255, 255], ground: [246, 246, 246], line: [90, 90, 90], grid: [200, 200, 200], glow: [120, 150, 200], outline: [255, 255, 255] };
  let pointCount = 0;
  let groupCount = 1;
  let hasSurface = false;

  const ground = buildGround(gl);
  const outlines = buildOutlines(gl, 0);
  const coarse = buildOutlines(gl, coarseTolerance);
  const graticule = buildGraticule(gl);

  const groundProgram = program(gl, groundVert, groundFrag);
  const lineProgram = program(gl, lineVert, lineFrag);
  const markProgram = program(gl, markVert, markFrag);
  const heatProgram = program(gl, markVert, heatFrag);
  const blurProgram = program(gl, screenVert, blurFrag);
  const reduceProgram = program(gl, screenVert, reduceFrag);
  const rampProgram = program(gl, screenVert, rampFrag);

  const groundVao = gl.createVertexArray()!;
  gl.bindVertexArray(groundVao);
  gl.bindBuffer(gl.ARRAY_BUFFER, ground.places);
  attribute(gl, groundProgram, "aPlace", 2);
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, ground.indices);
  gl.bindVertexArray(null);

  // the corners of one line's quad, shared by every segment ever drawn
  const corners = buffer(gl, gl.ARRAY_BUFFER, new Float32Array([0, -1, 1, -1, 0, 1, 1, 1]));
  const outlineVao = lineVao(gl, lineProgram, corners, outlines.segments);
  const coarseVao = lineVao(gl, lineProgram, corners, coarse.segments);
  const graticuleVao = lineVao(gl, lineProgram, corners, graticule.segments);

  const placeBuffer = gl.createBuffer()!;
  const groupBuffer = gl.createBuffer()!;
  const markVao = gl.createVertexArray()!;
  for (const p of [markProgram, heatProgram]) {
    gl.bindVertexArray(markVao);
    gl.bindBuffer(gl.ARRAY_BUFFER, placeBuffer);
    attribute(gl, p, "aPlace", 2);
    gl.bindBuffer(gl.ARRAY_BUFFER, groupBuffer);
    const at = gl.getAttribLocation(p, "aGroup");
    if (at >= 0) {
      gl.enableVertexAttribArray(at);
      gl.vertexAttribIPointer(at, 1, gl.UNSIGNED_SHORT, 0, 0);
    }
  }
  gl.bindVertexArray(null);
  /** the fullscreen passes take no attributes at all and build their triangle from the vertex id */
  const screenVao = gl.createVertexArray()!;

  const palette = texture(gl, gl.NEAREST);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([128, 128, 128, 255]));
  // the world as country numbers, and what each number is painted with; both read without filtering,
  // since a number halfway between two countries is a third country and not a blend of two colours
  const surface = texture(gl, gl.NEAREST, gl.REPEAT);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.R8, 1, 1, 0, gl.RED, gl.UNSIGNED_BYTE, new Uint8Array([0]));
  const surfaceColors = texture(gl, gl.NEAREST);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([0, 0, 0, 0]));
  const ramp = texture(gl, gl.LINEAR);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([0, 0, 0, 0]));

  // the heat field's own targets, sized with the canvas: where the nodes are counted, somewhere to
  // blur through, and the two steps that find the largest count without reading anything back
  const density = target(gl);
  const scratch = target(gl);
  const tiles = target(gl);
  const peak = target(gl);
  let heatWidth = 0;
  let heatHeight = 0;

  function cameraOf(camera: GlobeCamera): { eye: Vec3; viewProj: Mat4 } {
    const distance = distanceOf(camera);
    const eye = scaled(unitVector(camera.lat, camera.lon), distance);
    const forward = normalize(scaled(eye, -1));
    // the world's own up, made square to the heading; the pitch is clamped by the view before it
    // ever reaches here, so the two are never parallel
    const right = normalize(cross(forward, [0, 1, 0]));
    const up = cross(right, forward);
    // the camera closes in on the SURFACE rather than on the middle of the ball, so the near plane
    // is a fraction of how far it still is from the ground under it
    const near = Math.max(0.002, (distance - 1) * 0.35);
    const projection = perspective(fov, Math.max(0.001, width / height), near, distance + 1.2);
    return { eye, viewProj: multiply(projection, viewMatrix(eye, forward, up)) };
  }

  /** The uniforms every program shares: which picture this is, and how to get onto the screen. */
  function place(p: WebGLProgram, scene: MapScene) {
    const device: [number, number] = [Math.max(1, canvas.width), Math.max(1, canvas.height)];
    gl.uniform2fv(gl.getUniformLocation(p, "uSize"), device);
    gl.uniform1i(gl.getUniformLocation(p, "uGlobe"), scene.globe ? 1 : 0);
    gl.uniform1i(gl.getUniformLocation(p, "uProjection"), projectionIndex(scene.projection));
    gl.uniform3f(gl.getUniformLocation(p, "uFlat"), scene.view.cx, scene.view.cy, scene.view.scale * dpr);
    const { eye, viewProj } = cameraOf(scene.camera);
    gl.uniformMatrix4fv(gl.getUniformLocation(p, "uViewProj"), false, viewProj);
    gl.uniform3fv(gl.getUniformLocation(p, "uEye"), eye);
  }

  function drawLines(scene: MapScene, vao: WebGLVertexArrayObject, count: number, color: RGB, widthPx: number) {
    if (count === 0) return;
    gl.useProgram(lineProgram);
    place(lineProgram, scene);
    gl.uniform1f(gl.getUniformLocation(lineProgram, "uLift"), lineLift);
    gl.uniform1f(gl.getUniformLocation(lineProgram, "uWidth"), Math.max(1, widthPx * dpr));
    gl.uniform3fv(gl.getUniformLocation(lineProgram, "uColor"), floats(color));
    gl.bindVertexArray(vao);
    gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, count);
  }

  /**
   * The heat field.
   *
   * A node's heat spreads over tens of pixels, so drawing each one as a soft blob would be tens of
   * thousands of fragments a node - at a million nodes, a picture no machine finishes. Instead each
   * node adds ONE to a small texture (a quarter of the canvas across), and that texture is blurred:
   * the spread costs the same whether there are a thousand nodes or a million, and the counting
   * costs one fragment each.
   *
   * The colours are then a scale of the LARGEST count on screen, which is found by two passes that
   * take maxima - a tile per pixel, then the lot into one - rather than by reading the texture back,
   * which would stall the frame on the processor.
   */
  function drawHeat(scene: MapScene) {
    if (pointCount === 0 || heatWidth === 0) return;
    // the radius asked for, in cells of the field, taken as how far the heat REACHES rather than
    // as its standard deviation - a gaussian is still faintly there at three times the latter, and
    // a heat map that washes the whole world in its lowest colour says nothing
    const reach = Math.max(1, (scene.radius * dpr) / heatShrink);
    const sigma = Math.max(0.5, reach / 3);
    gl.viewport(0, 0, heatWidth, heatHeight);
    gl.bindFramebuffer(gl.FRAMEBUFFER, density.frame);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    gl.enable(gl.BLEND);
    gl.blendEquation(gl.FUNC_ADD);
    gl.blendFunc(gl.ONE, gl.ONE); // every node adds itself to whatever is already there
    gl.useProgram(heatProgram);
    place(heatProgram, scene);
    gl.uniform1f(gl.getUniformLocation(heatProgram, "uLift"), markLift);
    gl.uniform1f(gl.getUniformLocation(heatProgram, "uPointSize"), 1);
    // the field is drawn at a fraction of the canvas, so the places have to land there too
    gl.uniform1f(gl.getUniformLocation(heatProgram, "uScale"), 1 / heatShrink);
    gl.uniform2f(gl.getUniformLocation(heatProgram, "uTarget"), heatWidth, heatHeight);
    // a node drawn instead of several stands for all of them; without whole floats to add up in,
    // the counts run 0..1 and are scaled back by the same amount they saturate at
    gl.uniform1f(gl.getUniformLocation(heatProgram, "uWeight"), Math.max(1, Math.ceil(pointCount / Math.max(1, scene.limit))) / (float ? 1 : 255));
    gl.bindVertexArray(markVao);
    gl.drawArrays(gl.POINTS, 0, Math.min(pointCount, Math.max(1, scene.limit)));

    gl.disable(gl.BLEND);
    gl.useProgram(blurProgram);
    gl.bindVertexArray(screenVao);
    gl.uniform1i(gl.getUniformLocation(blurProgram, "uSource"), 0);
    gl.activeTexture(gl.TEXTURE0);
    for (const [step, from, to] of [
      [[1, 0], density, scratch],
      [[0, 1], scratch, density],
    ] as [number[], typeof density, typeof density][]) {
      gl.bindFramebuffer(gl.FRAMEBUFFER, to.frame);
      gl.bindTexture(gl.TEXTURE_2D, from.texture);
      gl.uniform2f(gl.getUniformLocation(blurProgram, "uStep"), step[0] / heatWidth, step[1] / heatHeight);
      gl.uniform1i(gl.getUniformLocation(blurProgram, "uTaps"), Math.min(maxBlurTaps, Math.ceil(reach)));
      gl.uniform1f(gl.getUniformLocation(blurProgram, "uSigma"), sigma);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
    }

    // the largest count on screen, in two steps: a tile of the field into each of 32 x 32 pixels,
    // then all of those into one
    gl.useProgram(reduceProgram);
    gl.uniform1i(gl.getUniformLocation(reduceProgram, "uSource"), 0);
    gl.bindFramebuffer(gl.FRAMEBUFFER, tiles.frame);
    gl.viewport(0, 0, 32, 32);
    gl.bindTexture(gl.TEXTURE_2D, density.texture);
    gl.uniform2i(gl.getUniformLocation(reduceProgram, "uTile"), Math.ceil(heatWidth / 32), Math.ceil(heatHeight / 32));
    gl.uniform2i(gl.getUniformLocation(reduceProgram, "uFrom"), heatWidth, heatHeight);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    gl.bindFramebuffer(gl.FRAMEBUFFER, peak.frame);
    gl.viewport(0, 0, 1, 1);
    gl.bindTexture(gl.TEXTURE_2D, tiles.texture);
    gl.uniform2i(gl.getUniformLocation(reduceProgram, "uTile"), 32, 32);
    gl.uniform2i(gl.getUniformLocation(reduceProgram, "uFrom"), 32, 32);
    gl.drawArrays(gl.TRIANGLES, 0, 3);

    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.viewport(0, 0, canvas.width, canvas.height);
    gl.enable(gl.BLEND);
    gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
    gl.useProgram(rampProgram);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, density.texture);
    gl.uniform1i(gl.getUniformLocation(rampProgram, "uSource"), 0);
    gl.activeTexture(gl.TEXTURE1);
    gl.bindTexture(gl.TEXTURE_2D, peak.texture);
    gl.uniform1i(gl.getUniformLocation(rampProgram, "uPeak"), 1);
    gl.activeTexture(gl.TEXTURE2);
    gl.bindTexture(gl.TEXTURE_2D, ramp);
    gl.uniform1i(gl.getUniformLocation(rampProgram, "uRamp"), 2);
    gl.uniform2f(gl.getUniformLocation(rampProgram, "uSourceSize"), heatWidth, heatHeight);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
  }

  /** How many degrees a pixel covers around a place: what a pick radius, and the level of detail, are decided with. */
  function degreesPerPixel(scene: MapScene, lat: number): number {
    if (scene.globe) {
      // how much of the ball the view covers where it meets the surface, spread over its pixels
      const halfWorld = Math.tan(fov / 2) * (distanceOf(scene.camera) - 1);
      return ((halfWorld * 2) / height) * (180 / Math.PI);
    }
    // what a degree of longitude comes to in world units where the pointer is, and so in pixels
    const [x0] = scene.projection.project(0, lat);
    const [x1] = scene.projection.project(0.1, lat);
    return 0.1 / Math.max(1e-9, Math.abs(x1 - x0) * scene.view.scale);
  }

  function toScreen(viewProj: Mat4, eye: Vec3, x: number, y: number, z: number, out: Float32Array): boolean {
    // on the near side when the surface at that point turns towards the camera; tested first,
    // because half the nodes fail it and the projection below is the expensive half
    if (x * (eye[0] - x) + y * (eye[1] - y) + z * (eye[2] - z) <= 0) return false;
    const cx = viewProj[0] * x + viewProj[4] * y + viewProj[8] * z + viewProj[12];
    const cy = viewProj[1] * x + viewProj[5] * y + viewProj[9] * z + viewProj[13];
    const cw = viewProj[3] * x + viewProj[7] * y + viewProj[11] * z + viewProj[15];
    const w = cw === 0 ? 1e-6 : cw;
    out[0] = (cx / w) * 0.5 * width + width / 2;
    out[1] = height / 2 - (cy / w) * 0.5 * height;
    return true;
  }

  return {
    resize(cssWidth, cssHeight, ratio) {
      width = Math.max(1, cssWidth);
      height = Math.max(1, cssHeight);
      dpr = ratio;
      const w = Math.round(width * dpr);
      const h = Math.round(height * dpr);
      if (canvas.width === w && canvas.height === h) return;
      canvas.width = w;
      canvas.height = h;
      heatWidth = Math.max(1, Math.ceil(w / heatShrink));
      heatHeight = Math.max(1, Math.ceil(h / heatShrink));
      for (const t of [density, scratch]) resizeTarget(gl, t, heatWidth, heatHeight, float);
      resizeTarget(gl, tiles, 32, 32, float);
      resizeTarget(gl, peak, 1, 1, float);
    },
    setTheme(next) {
      theme = next;
    },
    setPoints(lon, lat, count) {
      pointCount = count;
      const places = new Float32Array(count * 2);
      for (let i = 0; i < count; i++) {
        places[i * 2] = lon[i];
        places[i * 2 + 1] = lat[i];
      }
      gl.bindBuffer(gl.ARRAY_BUFFER, placeBuffer);
      gl.bufferData(gl.ARRAY_BUFFER, places, gl.STATIC_DRAW);
    },
    setColors(colors, assignment) {
      groupCount = Math.max(1, colors.length / 3);
      const rgba = new Uint8Array(groupCount * 4);
      for (let i = 0; i < groupCount; i++) {
        rgba[i * 4] = colors[i * 3];
        rgba[i * 4 + 1] = colors[i * 3 + 1];
        rgba[i * 4 + 2] = colors[i * 3 + 2];
        rgba[i * 4 + 3] = 255;
      }
      gl.bindTexture(gl.TEXTURE_2D, palette);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, groupCount, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, rgba);
      const groups = assignment ?? new Uint16Array(pointCount); // all of them in group 0
      gl.bindBuffer(gl.ARRAY_BUFFER, groupBuffer);
      gl.bufferData(gl.ARRAY_BUFFER, groups.subarray(0, pointCount), gl.STATIC_DRAW);
    },
    setRamp(colors) {
      gl.bindTexture(gl.TEXTURE_2D, ramp);
      // a ramp arrives empty until the page has read its own colours out of the stylesheet
      const steps = colors.length >= 4 ? colors : new Uint8Array(4);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, steps.length / 4, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, steps);
    },
    setSurface(index, w, h) {
      hasSurface = true;
      gl.bindTexture(gl.TEXTURE_2D, surface);
      gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1); // one byte a cell, so rows are not padded to four
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.R8, w, h, 0, gl.RED, gl.UNSIGNED_BYTE, index);
      gl.pixelStorei(gl.UNPACK_ALIGNMENT, 4);
    },
    setSurfaceColors(colors) {
      gl.bindTexture(gl.TEXTURE_2D, surfaceColors);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, Math.max(1, colors.length / 4), 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, colors);
    },
    draw(scene) {
      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
      gl.viewport(0, 0, canvas.width, canvas.height);
      const clear = floats(theme.clear);
      gl.clearColor(clear[0], clear[1], clear[2], 1);
      gl.clearDepth(1);
      gl.disable(gl.BLEND);
      gl.enable(gl.DEPTH_TEST);
      gl.depthFunc(gl.LEQUAL);
      gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);

      // the ground. The flat map has none of its own - the panel behind it is its ground - so it is
      // drawn only when there is something to paint on it
      const painting = scene.surface && hasSurface;
      if (scene.globe || painting) {
        gl.enable(gl.BLEND);
        gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
        gl.useProgram(groundProgram);
        place(groundProgram, scene);
        gl.uniform3fv(gl.getUniformLocation(groundProgram, "uGround"), floats(theme.ground));
        gl.uniform3fv(gl.getUniformLocation(groundProgram, "uGlow"), floats(theme.glow));
        gl.uniform1i(gl.getUniformLocation(groundProgram, "uPainted"), painting ? 1 : 0);
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, surface);
        gl.uniform1i(gl.getUniformLocation(groundProgram, "uSurface"), 0);
        gl.activeTexture(gl.TEXTURE1);
        gl.bindTexture(gl.TEXTURE_2D, surfaceColors);
        gl.uniform1i(gl.getUniformLocation(groundProgram, "uSurfaceColors"), 1);
        gl.bindVertexArray(groundVao);
        gl.drawElements(gl.TRIANGLES, ground.count, gl.UNSIGNED_INT, 0);
      }

      // everything from here lies on top of the ground rather than in it
      gl.disable(gl.DEPTH_TEST);
      gl.enable(gl.BLEND);
      gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
      if (scene.graticule) drawLines(scene, graticuleVao, graticule.count, theme.grid, 1);
      // every point of the coastline, or as many of them as this zoom can show apart
      const fine = degreesPerPixel(scene, scene.globe ? scene.camera.lat : 0) < detailBelow;
      drawLines(scene, fine ? outlineVao : coarseVao, fine ? outlines.count : coarse.count, theme.line, 1.25);

      if (scene.marks === "heat") {
        drawHeat(scene);
      } else if (scene.marks !== "none" && pointCount > 0) {
        gl.useProgram(markProgram);
        place(markProgram, scene);
        gl.uniform1f(gl.getUniformLocation(markProgram, "uLift"), markLift);
        // a pin's sprite has to hold the whole teardrop, which is about one and a half heads tall
        gl.uniform1f(gl.getUniformLocation(markProgram, "uPointSize"), Math.max(1, scene.size * dpr) * (scene.marks === "pins" ? 1.7 : 1));
        gl.uniform1f(gl.getUniformLocation(markProgram, "uScale"), 1);
        gl.uniform2f(gl.getUniformLocation(markProgram, "uTarget"), canvas.width, canvas.height);
        gl.uniform1f(gl.getUniformLocation(markProgram, "uAlpha"), scene.alpha);
        gl.uniform1f(gl.getUniformLocation(markProgram, "uGroups"), groupCount);
        gl.uniform1i(gl.getUniformLocation(markProgram, "uPin"), scene.marks === "pins" ? 1 : 0);
        gl.uniform3fv(gl.getUniformLocation(markProgram, "uOutline"), floats(theme.outline));
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, palette);
        gl.uniform1i(gl.getUniformLocation(markProgram, "uPalette"), 0);
        gl.bindVertexArray(markVao);
        gl.drawArrays(gl.POINTS, 0, Math.min(pointCount, Math.max(1, scene.limit)));
      }
      gl.bindVertexArray(null);
    },
    project(scene) {
      if (!scene.globe) {
        const { view, projection } = scene;
        return (lon, lat, out) => {
          const [x, y] = projection.project(lon, lat);
          out[0] = width / 2 + (x - view.cx) * view.scale;
          out[1] = height / 2 + (y - view.cy) * view.scale;
          return true;
        };
      }
      const { eye, viewProj } = cameraOf(scene.camera);
      return (lon, lat, out) => {
        const p = unitVector(lat, lon);
        return toScreen(viewProj, eye, p[0], p[1], p[2], out);
      };
    },
    placeAt(scene, px, py) {
      if (!scene.globe) {
        const { view, projection } = scene;
        const at = projection.invert(view.cx + (px - width / 2) / view.scale, view.cy + (py - height / 2) / view.scale);
        return at === null ? null : [at[1], at[0]];
      }
      const { eye, viewProj } = cameraOf(scene.camera);
      // a ray through the pixel: undo the projection at two depths and take the direction between
      const inverse = invert(viewProj);
      if (inverse === null) return null;
      const ndcX = (px / width) * 2 - 1;
      const ndcY = 1 - (py / height) * 2;
      const near = unproject(inverse, ndcX, ndcY, -1);
      const far = unproject(inverse, ndcX, ndcY, 1);
      const dir = normalize([far[0] - near[0], far[1] - near[1], far[2] - near[2]]);
      const t = raySphere(eye, dir, [0, 0, 0], 1);
      if (t === null) return null;
      return placeOf(eye[0] + dir[0] * t, eye[1] + dir[1] * t, eye[2] + dir[2] * t);
    },
    degreesPerPixel,
    destroy() {
      for (const p of [groundProgram, lineProgram, markProgram, heatProgram, blurProgram, reduceProgram, rampProgram]) gl.deleteProgram(p);
      for (const b of [ground.places, ground.indices, outlines.segments, coarse.segments, graticule.segments, corners, placeBuffer, groupBuffer]) gl.deleteBuffer(b);
      for (const t of [palette, surface, surfaceColors, ramp]) gl.deleteTexture(t);
      for (const t of [density, scratch, tiles, peak]) {
        gl.deleteFramebuffer(t.frame);
        if (t.texture) gl.deleteTexture(t.texture);
      }
      for (const v of [groundVao, outlineVao, coarseVao, graticuleVao, markVao, screenVao]) gl.deleteVertexArray(v);
    },
  };
}

const cross = (a: Vec3, b: Vec3): Vec3 => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const floats = (rgb: RGB): [number, number, number] => [rgb[0] / 255, rgb[1] / 255, rgb[2] / 255];

/** A point of clip space back into the world; the caller has already inverted the matrix. */
function unproject(inverse: Mat4, x: number, y: number, z: number): Vec3 {
  const w = inverse[3] * x + inverse[7] * y + inverse[11] * z + inverse[15] || 1e-6;
  return [
    (inverse[0] * x + inverse[4] * y + inverse[8] * z + inverse[12]) / w,
    (inverse[1] * x + inverse[5] * y + inverse[9] * z + inverse[13]) / w,
    (inverse[2] * x + inverse[6] * y + inverse[10] * z + inverse[14]) / w,
  ];
}

/** A 4x4 inverse, column major; null when the matrix is singular, which a camera's never is. */
function invert(m: Mat4): Mat4 | null {
  const out = new Float32Array(16);
  const a00 = m[0], a01 = m[1], a02 = m[2], a03 = m[3];
  const a10 = m[4], a11 = m[5], a12 = m[6], a13 = m[7];
  const a20 = m[8], a21 = m[9], a22 = m[10], a23 = m[11];
  const a30 = m[12], a31 = m[13], a32 = m[14], a33 = m[15];
  const b00 = a00 * a11 - a01 * a10;
  const b01 = a00 * a12 - a02 * a10;
  const b02 = a00 * a13 - a03 * a10;
  const b03 = a01 * a12 - a02 * a11;
  const b04 = a01 * a13 - a03 * a11;
  const b05 = a02 * a13 - a03 * a12;
  const b06 = a20 * a31 - a21 * a30;
  const b07 = a20 * a32 - a22 * a30;
  const b08 = a20 * a33 - a23 * a30;
  const b09 = a21 * a32 - a22 * a31;
  const b10 = a21 * a33 - a23 * a31;
  const b11 = a22 * a33 - a23 * a32;
  const det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
  if (!det) return null;
  const d = 1 / det;
  out[0] = (a11 * b11 - a12 * b10 + a13 * b09) * d;
  out[1] = (a02 * b10 - a01 * b11 - a03 * b09) * d;
  out[2] = (a31 * b05 - a32 * b04 + a33 * b03) * d;
  out[3] = (a22 * b04 - a21 * b05 - a23 * b03) * d;
  out[4] = (a12 * b08 - a10 * b11 - a13 * b07) * d;
  out[5] = (a00 * b11 - a02 * b08 + a03 * b07) * d;
  out[6] = (a32 * b02 - a30 * b05 - a33 * b01) * d;
  out[7] = (a20 * b05 - a22 * b02 + a23 * b01) * d;
  out[8] = (a10 * b10 - a11 * b08 + a13 * b06) * d;
  out[9] = (a01 * b08 - a00 * b10 - a03 * b06) * d;
  out[10] = (a30 * b04 - a31 * b02 + a33 * b00) * d;
  out[11] = (a21 * b02 - a20 * b04 - a23 * b00) * d;
  out[12] = (a11 * b07 - a10 * b09 - a12 * b06) * d;
  out[13] = (a00 * b09 - a01 * b07 + a02 * b06) * d;
  out[14] = (a31 * b01 - a30 * b03 - a32 * b00) * d;
  out[15] = (a20 * b03 - a21 * b01 + a22 * b00) * d;
  return out;
}

// ---- the geometry, all of it in degrees ----

/**
 * The ground: a grid of quads over the whole world, in longitude and latitude. Wrapped round a ball
 * or laid out flat by the shader, so one mesh is both the globe and the flat map's sheet.
 */
function buildGround(gl: WebGL2RenderingContext) {
  const places = new Float32Array((groundColumns + 1) * (groundRows + 1) * 2);
  let at = 0;
  for (let row = 0; row <= groundRows; row++) {
    const lat = 90 - (row * 180) / groundRows;
    for (let col = 0; col <= groundColumns; col++) {
      places[at++] = -180 + (col * 360) / groundColumns;
      places[at++] = lat;
    }
  }
  const indices = new Uint32Array(groundColumns * groundRows * 6);
  let k = 0;
  for (let row = 0; row < groundRows; row++) {
    for (let col = 0; col < groundColumns; col++) {
      const a = row * (groundColumns + 1) + col;
      const b = a + groundColumns + 1;
      indices[k++] = a;
      indices[k++] = b;
      indices[k++] = a + 1;
      indices[k++] = a + 1;
      indices[k++] = b;
      indices[k++] = b + 1;
    }
  }
  return { places: buffer(gl, gl.ARRAY_BUFFER, places), indices: buffer(gl, gl.ELEMENT_ARRAY_BUFFER, indices), count: indices.length };
}

/** One line segment per instance: where it starts and where it ends, in degrees. */
function segmentBuffer(gl: WebGL2RenderingContext, segments: number[]) {
  const data = new Float32Array(segments);
  return { segments: buffer(gl, gl.ARRAY_BUFFER, data), count: data.length / 4 };
}

/** A run of places, broken into pieces short enough to follow any of the projections. */
function addRun(out: number[], lon0: number, lat0: number, lon1: number, lat1: number) {
  if (Math.abs(lon1 - lon0) > 180) return; // a step across the seam is a wrap, not a line
  const steps = Math.min(64, Math.max(1, Math.ceil(Math.max(Math.abs(lon1 - lon0), Math.abs(lat1 - lat0)) / maxSegmentDegrees)));
  for (let s = 0; s < steps; s++) {
    const t0 = s / steps;
    const t1 = (s + 1) / steps;
    out.push(lon0 + (lon1 - lon0) * t0, lat0 + (lat1 - lat0) * t0, lon0 + (lon1 - lon0) * t1, lat0 + (lat1 - lat0) * t1);
  }
}

/**
 * Every coastline and border of the world, once each: the arcs are shared, so a border is one line.
 * With a tolerance, the same world with the points no zoom could tell apart left out - each ARC
 * simplified on its own and its ends kept, so two countries sharing a border still share it.
 */
function buildOutlines(gl: WebGL2RenderingContext, tolerance: number) {
  const segments: number[] = [];
  for (const whole of world().arcs) {
    const arc = tolerance > 0 ? simplify(whole, tolerance) : whole;
    for (let i = 1; i < arc.length / 2; i++) addRun(segments, arc[(i - 1) * 2], arc[(i - 1) * 2 + 1], arc[i * 2], arc[i * 2 + 1]);
  }
  return segmentBuffer(gl, segments);
}

/**
 * Douglas-Peucker: the run of points that stays within `tolerance` degrees of the original, keeping
 * the two ends. Written with a stack rather than recursively, since an arc can be thousands of
 * points long and the whole world goes through here at load.
 */
function simplify(points: Float32Array, tolerance: number): Float32Array {
  const count = points.length / 2;
  if (count < 3) return points;
  const keep = new Uint8Array(count);
  keep[0] = keep[count - 1] = 1;
  const stack: number[] = [0, count - 1];
  const limit = tolerance * tolerance;
  while (stack.length > 0) {
    const last = stack.pop()!;
    const first = stack.pop()!;
    if (last - first < 2) continue;
    const ax = points[first * 2];
    const ay = points[first * 2 + 1];
    const bx = points[last * 2];
    const by = points[last * 2 + 1];
    const dx = bx - ax;
    const dy = by - ay;
    const length = dx * dx + dy * dy;
    let worst = 0;
    let at = -1;
    for (let i = first + 1; i < last; i++) {
      const px = points[i * 2] - ax;
      const py = points[i * 2 + 1] - ay;
      // the square of the distance to the line, or to the point when the two ends are the same
      const t = length > 0 ? Math.max(0, Math.min(1, (px * dx + py * dy) / length)) : 0;
      const ox = px - dx * t;
      const oy = py - dy * t;
      const off = ox * ox + oy * oy;
      if (off > worst) {
        worst = off;
        at = i;
      }
    }
    if (at < 0 || worst <= limit) continue;
    keep[at] = 1;
    stack.push(first, at, at, last);
  }
  let kept = 0;
  for (let i = 0; i < count; i++) if (keep[i]) kept++;
  const out = new Float32Array(kept * 2);
  let k = 0;
  for (let i = 0; i < count; i++) {
    if (!keep[i]) continue;
    out[k++] = points[i * 2];
    out[k++] = points[i * 2 + 1];
  }
  return out;
}

/** The lines of latitude and longitude, every 30 degrees. */
function buildGraticule(gl: WebGL2RenderingContext, every = 30) {
  const segments: number[] = [];
  for (let lon = -180; lon <= 180; lon += every) {
    for (let lat = -90; lat < 90; lat += maxSegmentDegrees) addRun(segments, lon, lat, lon, Math.min(90, lat + maxSegmentDegrees));
  }
  for (let lat = -60; lat <= 60; lat += every) {
    for (let lon = -180; lon < 180; lon += maxSegmentDegrees) addRun(segments, lon, lat, Math.min(180, lon + maxSegmentDegrees), lat);
  }
  return segmentBuffer(gl, segments);
}

// ---- the plumbing ----

function buffer(gl: WebGL2RenderingContext, target: number, data: BufferSource): WebGLBuffer {
  const b = gl.createBuffer()!;
  gl.bindBuffer(target, b);
  gl.bufferData(target, data, gl.STATIC_DRAW);
  return b;
}

function attribute(gl: WebGL2RenderingContext, prog: WebGLProgram, name: string, size: number, divisor = 0) {
  const at = gl.getAttribLocation(prog, name);
  if (at < 0) return;
  gl.enableVertexAttribArray(at);
  gl.vertexAttribPointer(at, size, gl.FLOAT, false, 0, 0);
  if (divisor) gl.vertexAttribDivisor(at, divisor);
}

function lineVao(gl: WebGL2RenderingContext, prog: WebGLProgram, corners: WebGLBuffer, segments: WebGLBuffer) {
  const vao = gl.createVertexArray()!;
  gl.bindVertexArray(vao);
  gl.bindBuffer(gl.ARRAY_BUFFER, corners);
  attribute(gl, prog, "aCorner", 2);
  gl.bindBuffer(gl.ARRAY_BUFFER, segments);
  attribute(gl, prog, "aSegment", 4, 1);
  gl.bindVertexArray(null);
  return vao;
}

function texture(gl: WebGL2RenderingContext, filter: number, wrap: number = gl.CLAMP_TO_EDGE): WebGLTexture {
  const t = gl.createTexture()!;
  gl.bindTexture(gl.TEXTURE_2D, t);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, filter);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, filter);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, wrap);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  return t;
}

interface Target {
  frame: WebGLFramebuffer;
  texture: WebGLTexture | null;
}

const target = (gl: WebGL2RenderingContext): Target => ({ frame: gl.createFramebuffer()!, texture: null });

/**
 * A render target for the heat field, of the given size.
 *
 * Whole floats where the machine will render to them, and eight bits per channel where it will
 * not. Eight bits saturate at 255 nodes to a cell and the places worth looking at hold far more,
 * so the fallback flattens the densest spots - better than nothing, and rare: every current
 * browser has the extension.
 *
 * Never filtered. A blur tap lands exactly on a texel by construction, so there is nothing for a
 * filter to do there, and the one place that wants smoothing - reading the field back out over the
 * canvas - mixes its four texels itself (see rampFrag). Which is also what lets this be a whole
 * float rather than a half: a half has eleven bits of mantissa, so adding one to four thousand
 * changes nothing, and a crowded cell would stop counting long before it stopped filling up.
 */
function resizeTarget(gl: WebGL2RenderingContext, t: Target, width: number, height: number, float: boolean) {
  if (t.texture) gl.deleteTexture(t.texture);
  t.texture = texture(gl, gl.NEAREST);
  gl.texImage2D(gl.TEXTURE_2D, 0, float ? gl.R32F : gl.RGBA8, width, height, 0, float ? gl.RED : gl.RGBA, float ? gl.FLOAT : gl.UNSIGNED_BYTE, null);
  gl.bindFramebuffer(gl.FRAMEBUFFER, t.frame);
  gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, t.texture, 0);
  gl.bindFramebuffer(gl.FRAMEBUFFER, null);
}

function program(gl: WebGL2RenderingContext, vs: string, fs: string): WebGLProgram {
  const compile = (type: number, src: string) => {
    const sh = gl.createShader(type)!;
    gl.shaderSource(sh, src);
    gl.compileShader(sh);
    if (!gl.getShaderParameter(sh, gl.COMPILE_STATUS)) throw new Error("shader: " + gl.getShaderInfoLog(sh));
    return sh;
  };
  const p = gl.createProgram()!;
  gl.attachShader(p, compile(gl.VERTEX_SHADER, vs));
  gl.attachShader(p, compile(gl.FRAGMENT_SHADER, fs));
  gl.linkProgram(p);
  if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error("program: " + gl.getProgramInfoLog(p));
  return p;
}

// ---- the shaders ----

/**
 * Where a place goes. The forward projections here are the same arithmetic as map/projection.ts,
 * which is the version the processor uses for picking and for gathering clusters; the two have to
 * agree, so a change to one belongs in the other.
 */
const placeGlsl = `
  const float PI = 3.141592653589793;
  uniform bool uGlobe;
  uniform int uProjection;
  uniform vec3 uFlat;      // the flat map's window: middle x, middle y, device pixels per world unit
  uniform vec2 uSize;      // the canvas, in device pixels
  uniform mat4 uViewProj;
  uniform vec3 uEye;

  vec2 projectFlat(vec2 place) {
    if (uProjection == 0) return vec2(place.x / 180.0, -place.y / 180.0);
    if (uProjection == 1) {
      float phi = clamp(place.y, -${mercatorLimit.toFixed(7)}, ${mercatorLimit.toFixed(7)}) * PI / 180.0;
      return vec2(place.x / 180.0, -log(tan(PI * 0.25 + phi * 0.5)) / PI);
    }
    float phi = place.y * PI / 180.0;
    float p2 = phi * phi;
    float p4 = p2 * p2;
    float nx = 0.8707 - 0.131979 * p2 + p4 * (-0.013791 + p4 * (0.003971 * p2 - 0.001529 * p4));
    float ny = phi * (1.007226 + p2 * (0.015085 + p4 * (-0.044475 + 0.028874 * p2 - 0.005916 * p4)));
    return vec2(place.x * PI / 180.0 * nx, -ny) / ${naturalWidth.toFixed(9)};
  }

  vec3 unitOf(vec2 place) {
    float phi = place.y * PI / 180.0;
    float lam = place.x * PI / 180.0;
    float c = cos(phi);
    return vec3(c * sin(lam), sin(phi), c * cos(lam));
  }

  // device pixels, and whether the place is drawn at all - on the globe, half of them are not
  bool placeOf(vec2 place, float lift, out vec2 px) {
    if (uGlobe) {
      vec3 p = unitOf(place) * lift;
      if (dot(normalize(p), normalize(uEye - p)) <= 0.0) return false;
      vec4 clip = uViewProj * vec4(p, 1.0);
      if (clip.w <= 0.0) return false;
      px = (clip.xy / clip.w * 0.5 + 0.5) * uSize;
      px.y = uSize.y - px.y;
      return true;
    }
    px = uSize * 0.5 + (projectFlat(place) - uFlat.xy) * uFlat.z;
    return true;
  }

  vec4 toClip(vec2 px, vec2 size) {
    return vec4(px.x / size.x * 2.0 - 1.0, 1.0 - px.y / size.y * 2.0, 0.0, 1.0);
  }`;

/**
 * The ground. Through the real projection rather than through screen pixels, so that what is drawn
 * on it - the shading of the countries - stays where it should inside each quad of the grid.
 */
const groundVert = `#version 300 es
  in vec2 aPlace;
  out vec2 vPlace;
  out vec3 vWorld;
${placeGlsl}
  void main() {
    vPlace = aPlace;
    if (uGlobe) {
      vWorld = unitOf(aPlace);
      gl_Position = uViewProj * vec4(vWorld, 1.0);
    } else {
      vWorld = vec3(0.0);
      vec2 px;
      placeOf(aPlace, 1.0, px);
      gl_Position = toClip(px, uSize);
    }
  }`;

/**
 * The ball is lit from over the camera's shoulder rather than from a sun somewhere, so no part of
 * the world is ever in the dark - this is a chart, not a planetarium - with the light falling off
 * towards the edge just enough to read as a curve, and a glow at the very rim. Its colour is very
 * nearly the page's own, so what stands out on it is the data and not the planet.
 *
 * Flat, there is no curve to read and no rim: the sheet carries the painting and nothing else.
 */
const groundFrag = `#version 300 es
  precision highp float;
  in vec2 vPlace;
  in vec3 vWorld;
  uniform bool uGlobe;
  uniform vec3 uEye;
  uniform vec3 uGround;
  uniform vec3 uGlow;
  uniform bool uPainted;
  uniform sampler2D uSurface;
  uniform sampler2D uSurfaceColors;
  out vec4 outColor;
  void main() {
    // the raster holds a country's NUMBER; the palette holds what that number is painted with
    float country = uPainted ? texture(uSurface, vec2(0.5 + vPlace.x / 360.0, 0.5 - vPlace.y / 180.0)).r : 0.0;
    vec4 painted = uPainted ? texelFetch(uSurfaceColors, ivec2(int(country * 255.0 + 0.5), 0), 0) : vec4(0.0);
    if (!uGlobe) {
      if (painted.a <= 0.002) discard;
      outColor = painted;
      return;
    }
    vec3 n = normalize(vWorld);
    float facing = max(0.0, dot(n, normalize(uEye - vWorld)));
    vec3 base = mix(uGround, painted.rgb, painted.a);
    float shade = 0.82 + 0.18 * pow(facing, 0.7);
    float rim = pow(1.0 - facing, 3.0);
    outColor = vec4(base * shade + uGlow * rim * 0.30, 1.0);
  }`;

/**
 * A line as a quad in screen space. GL's own lines are one pixel wide and that is all they will
 * ever be, which is too faint for a coastline once a screen has two device pixels to the point -
 * so each segment becomes a rectangle of the width asked for, extended half a width past each end
 * so that consecutive pieces meet without a notch, and feathered at the edges for smoothness.
 *
 * A segment with either end on the far side of the globe is collapsed to nothing. The pieces are
 * two degrees at most, so what that loses is a stub at the very edge of the ball.
 */
const lineVert = `#version 300 es
  in vec2 aCorner;    // along the segment (0..1), across it (-1 or 1)
  in vec4 aSegment;   // lon0, lat0, lon1, lat1
  uniform float uLift;
  uniform float uWidth;
  out float vAcross;
${placeGlsl}
  void main() {
    vAcross = 0.0;
    vec2 a, b;
    if (!placeOf(aSegment.xy, uLift, a) || !placeOf(aSegment.zw, uLift, b)) {
      gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
      return;
    }
    vec2 along = b - a;
    float len = length(along);
    vec2 dir = len > 1e-5 ? along / len : vec2(1.0, 0.0);
    vec2 across = vec2(-dir.y, dir.x);
    float reach = uWidth * 0.5 + 0.75;
    vAcross = aCorner.y * reach;
    vec2 px = mix(a, b, aCorner.x) + dir * (aCorner.x * 2.0 - 1.0) * reach + across * vAcross;
    gl_Position = toClip(px, uSize);
  }`;

const lineFrag = `#version 300 es
  precision highp float;
  in float vAcross;
  uniform vec3 uColor;
  uniform float uWidth;
  out vec4 outColor;
  void main() {
    float edge = uWidth * 0.5;
    float alpha = clamp(edge + 0.4 - abs(vAcross), 0.0, 1.0);
    if (alpha <= 0.002) discard;
    outColor = vec4(uColor, alpha);
  }`;

/**
 * One node, as a point sprite standing on its place. `uScale` and `uTarget` let the same shader
 * draw into the small texture the heat field is counted in as well as onto the canvas.
 */
const markVert = `#version 300 es
  in vec2 aPlace;
  in uint aGroup;
  uniform float uLift;
  uniform float uPointSize;
  uniform float uScale;
  uniform vec2 uTarget;
  uniform bool uPin;
  flat out uint vGroup;
${placeGlsl}
  void main() {
    vGroup = aGroup;
    gl_PointSize = 0.0;
    vec2 px;
    if (!placeOf(aPlace, uLift, px)) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
    px *= uScale;
    // a pin is lifted by half its own height, so its tip rather than its middle is on the place
    if (uPin) px.y -= uPointSize * 0.5;
    gl_Position = toClip(px, uTarget);
    gl_PointSize = uPointSize;
  }`;

/**
 * A dot is a soft disc and a pin is a teardrop, both drawn twice over: once at their own size for
 * the colour and once a little larger for an outline, so one mark on top of another is still two
 * marks rather than a blot.
 */
const markFrag = `#version 300 es
  precision highp float;
  flat in uint vGroup;
  uniform sampler2D uPalette;
  uniform float uGroups;
  uniform float uAlpha;
  uniform bool uPin;
  uniform vec3 uOutline;
  out vec4 outColor;

  // how much of this fragment is inside the mark, grown outward by 'grow' of the sprite's width
  float cover(vec2 d, bool pin, float grow) {
    if (!pin) return 1.0 - smoothstep(0.36 + grow - 0.02, 0.36 + grow + 0.02, length(d));
    float head = 1.0 - smoothstep(0.20 + grow - 0.02, 0.20 + grow + 0.02, length(vec2(d.x, d.y + 0.16)));
    float tail = (1.0 - smoothstep(0.0, 0.03, abs(d.x) - (0.19 + grow) * (0.5 - d.y))) * step(-0.16, d.y) * step(d.y, 0.46 + grow);
    return max(head, tail);
  }

  void main() {
    vec2 d = gl_PointCoord - vec2(0.5);
    float fill = cover(d, uPin, 0.0);
    float edge = cover(d, uPin, uPin ? 0.045 : 0.06);
    if (edge <= 0.01) discard;
    vec3 c = texture(uPalette, vec2((float(vGroup) + 0.5) / uGroups, 0.5)).rgb;
    outColor = vec4(mix(uOutline, c, fill), edge * uAlpha);
  }`;

/** The same nodes, counted rather than drawn: one added per node per cell of the density field. */
const heatFrag = `#version 300 es
  precision highp float;
  uniform float uWeight;
  out vec4 outColor;
  void main() { outColor = vec4(uWeight, 0.0, 0.0, 1.0); }`;

/** A triangle over the whole target, built from the vertex id: the fullscreen passes take no attributes. */
const screenVert = `#version 300 es
  out vec2 vUv;
  void main() {
    vec2 p = vec2(gl_VertexID == 1 ? 3.0 : -1.0, gl_VertexID == 2 ? 3.0 : -1.0);
    vUv = p * 0.5 + 0.5;
    gl_Position = vec4(p, 0.0, 1.0);
  }`;

/** One axis of a gaussian blur, as many taps as the spread asks for. */
const blurFrag = `#version 300 es
  precision highp float;
  in vec2 vUv;
  uniform sampler2D uSource;
  uniform vec2 uStep;
  uniform int uTaps;
  uniform float uSigma;
  out vec4 outColor;
  void main() {
    float sum = texture(uSource, vUv).r;
    float weight = 1.0;
    for (int i = 1; i <= uTaps; i++) {
      float w = exp(-float(i * i) / (2.0 * uSigma * uSigma));
      sum += (texture(uSource, vUv + uStep * float(i)).r + texture(uSource, vUv - uStep * float(i)).r) * w;
      weight += 2.0 * w;
    }
    outColor = vec4(sum / weight, 0.0, 0.0, 1.0);
  }`;

/** The largest value in each tile of the source: run twice, this is the largest anywhere. */
const reduceFrag = `#version 300 es
  precision highp float;
  in vec2 vUv;
  uniform sampler2D uSource;
  uniform ivec2 uTile;
  uniform ivec2 uFrom;
  out vec4 outColor;
  void main() {
    ivec2 at = ivec2(gl_FragCoord.xy) * uTile;
    float peak = 0.0;
    for (int y = 0; y < uTile.y; y++) {
      for (int x = 0; x < uTile.x; x++) {
        ivec2 p = at + ivec2(x, y);
        if (p.x < uFrom.x && p.y < uFrom.y) peak = max(peak, texelFetch(uSource, p, 0).r);
      }
    }
    outColor = vec4(peak, 0.0, 0.0, 1.0);
  }`;

/**
 * The density field through the colour ramp. Scaled by the largest count on screen and read through
 * a square root, so a map with one enormous city on it still shows the villages.
 */
const rampFrag = `#version 300 es
  precision highp float;
  in vec2 vUv;
  uniform sampler2D uSource;
  uniform sampler2D uPeak;
  uniform sampler2D uRamp;
  uniform vec2 uSourceSize;
  out vec4 outColor;

  float at(ivec2 p) {
    return texelFetch(uSource, clamp(p, ivec2(0), ivec2(uSourceSize) - 1), 0).r;
  }

  void main() {
    // the field is a quarter of the canvas across, so it is mixed between its texels on the way
    // out; done here rather than by the sampler, since the texture it comes from is not filterable
    vec2 t = vUv * uSourceSize - 0.5;
    ivec2 b = ivec2(floor(t));
    vec2 f = fract(t);
    float value = mix(mix(at(b), at(b + ivec2(1, 0)), f.x), mix(at(b + ivec2(0, 1)), at(b + ivec2(1, 1)), f.x), f.y);
    float peak = texelFetch(uPeak, ivec2(0, 0), 0).r;
    if (peak <= 0.0 || value <= 0.0) discard;
    vec4 c = texture(uRamp, vec2(sqrt(clamp(value / peak, 0.0, 1.0)), 0.5));
    if (c.a <= 0.002) discard;
    outColor = c;
  }`;
