/**
 * The world as a ball: a WebGL2 globe with the coastlines and borders drawn on it, and the nodes of
 * the result standing on the places they belong to.
 *
 * It is a second renderer beside the flat map rather than a mode of it - the same arrangement the
 * visual pivot uses for its flat and solid pictures - because almost nothing is shared between
 * drawing on a plane and drawing on a sphere, and keeping them apart means the flat map goes on
 * costing exactly what it did.
 *
 * What the GPU draws here is the sphere, the outlines and the marks that there can be a million of.
 * What there are only a few hundred of - the cluster bubbles and their counts - is drawn over the
 * top in ordinary 2d by the view, which also uses `project` below to place them; a globe is only
 * worth a shader for the things that scale.
 */

import { multiply, normalize, perspective, raySphere, scale, transform, view as viewMatrix, type Mat4, type Vec3 } from "../graph3d/math";
import { world } from "./worldMap";

/** Where the camera is: the place it looks straight down at, and how close it has come. */
export interface GlobeCamera {
  lat: number;
  lon: number;
  /** 1 is the whole globe in view; larger closes in. */
  zoom: number;
}

export interface GlobeTheme {
  /** behind the globe */
  clear: [number, number, number];
  /** the ball itself */
  ground: [number, number, number];
  /** the coastlines and borders */
  line: [number, number, number];
  /** the faint halo round the edge, which is what makes a flat disc read as a ball */
  glow: [number, number, number];
}

/** How the nodes are drawn on the globe. The other three ways are drawn over it in 2d. */
export type GlobeMarks = "dots" | "pins" | "none";

export interface Globe {
  resize(cssWidth: number, cssHeight: number, dpr: number): void;
  setTheme(theme: GlobeTheme): void;
  /** The nodes, as unit vectors (see `unitVectors`); the same array the view projects with. */
  setPoints(unit: Float32Array, count: number): void;
  /** rgb bytes per colour group, and the group of each node; a null assignment paints them all with the first colour. */
  setColors(palette: Uint8Array, assignment: Uint16Array | null): void;
  /** An equirectangular picture wrapped round the ball - the countries shaded by their counts - or null for the plain ground. */
  setSurface(surface: ImageData | null): void;
  /** `limit` caps how many of the nodes are drawn as marks - what keeps pins to the number the flat map draws. */
  draw(camera: GlobeCamera, marks: GlobeMarks, size: number, alpha: number, limit: number): void;
  /**
   * Where a place lands on the canvas, in css pixels, written into `out`; the answer is whether it
   * is on the near side of the ball. A function made once per frame and called once per node, so it
   * writes into the caller's array rather than handing back a new one a million times.
   */
  project(camera: GlobeCamera): (lat: number, lon: number, out: Float32Array) => boolean;
  /** And the same for a node already turned into a unit vector, which is how the view keeps them. */
  projectUnit(camera: GlobeCamera): (x: number, y: number, z: number, out: Float32Array) => boolean;
  /** The place under a point of the canvas, in degrees, or null where the pointer is off the ball. */
  placeAt(camera: GlobeCamera, px: number, py: number): [number, number] | null;
  /** How many degrees a pixel covers at the middle of the view: what a pick radius is converted with. */
  degreesPerPixel(camera: GlobeCamera): number;
  destroy(): void;
}

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
/** How far above the surface the lines and the marks stand, so they are not swallowed by it. */
const lineLift = 1.0015;
const markLift = 1.003;
/** How long a piece of outline may be before it is broken up to follow the curve of the ball, in degrees. */
const maxLineDegrees = 2.5;

/** A place, as a point on the unit sphere: x east at the prime meridian, y north, z out through (0°, 0°). */
export function unitVector(lat: number, lon: number): Vec3 {
  const φ = (lat * Math.PI) / 180;
  const λ = (lon * Math.PI) / 180;
  const c = Math.cos(φ);
  return [c * Math.sin(λ), Math.sin(φ), c * Math.cos(λ)];
}

/** Every node as a unit vector, interleaved x, y, z: computed once per result and used by the globe and the view alike. */
export function unitVectors(lat: Float32Array, lon: Float32Array, count: number): Float32Array {
  const out = new Float32Array(count * 3);
  const rad = Math.PI / 180;
  for (let i = 0; i < count; i++) {
    const φ = lat[i] * rad;
    const λ = lon[i] * rad;
    const c = Math.cos(φ);
    out[i * 3] = c * Math.sin(λ);
    out[i * 3 + 1] = Math.sin(φ);
    out[i * 3 + 2] = c * Math.cos(λ);
  }
  return out;
}

/** The place a unit vector stands for, in degrees. */
export function placeOf(x: number, y: number, z: number): [number, number] {
  const lat = (Math.asin(Math.max(-1, Math.min(1, y))) * 180) / Math.PI;
  const lon = (Math.atan2(x, z) * 180) / Math.PI;
  return [lat, lon];
}

export function createGlobe(canvas: HTMLCanvasElement): Globe | null {
  const context = canvas.getContext("webgl2", { antialias: true, alpha: false, premultipliedAlpha: false, powerPreference: "high-performance" });
  if (!context) return null;
  const gl: WebGL2RenderingContext = context;

  let width = 1;
  let height = 1;
  let dpr = 1;
  let theme: GlobeTheme = { clear: [1, 1, 1], ground: [0.93, 0.94, 0.96], line: [0.6, 0.6, 0.62], glow: [0.5, 0.6, 0.8] };
  let pointCount = 0;
  let groupCount = 1;
  let hasSurface = false;

  const sphere = buildSphere(gl, 128, 64);
  const outline = buildOutline(gl);

  const sphereProgram = program(gl, sphereVert, sphereFrag);
  const lineProgram = program(gl, lineVert, lineFrag);
  const pointProgram = program(gl, pointVert, pointFrag);

  const sphereVao = gl.createVertexArray()!;
  gl.bindVertexArray(sphereVao);
  gl.bindBuffer(gl.ARRAY_BUFFER, sphere.positions);
  bindAttribute(gl, sphereProgram, "aPos", 3);
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, sphere.indices);
  gl.bindVertexArray(null);

  const lineVao = gl.createVertexArray()!;
  gl.bindVertexArray(lineVao);
  gl.bindBuffer(gl.ARRAY_BUFFER, outline.positions);
  bindAttribute(gl, lineProgram, "aPos", 3);
  gl.bindVertexArray(null);

  const pointBuffer = gl.createBuffer()!;
  const groupBuffer = gl.createBuffer()!;
  const pointVao = gl.createVertexArray()!;
  gl.bindVertexArray(pointVao);
  gl.bindBuffer(gl.ARRAY_BUFFER, pointBuffer);
  bindAttribute(gl, pointProgram, "aPos", 3);
  gl.bindBuffer(gl.ARRAY_BUFFER, groupBuffer);
  const groupAt = gl.getAttribLocation(pointProgram, "aGroup");
  if (groupAt >= 0) {
    gl.enableVertexAttribArray(groupAt);
    gl.vertexAttribIPointer(groupAt, 1, gl.UNSIGNED_SHORT, 0, 0);
  }
  gl.bindVertexArray(null);

  // the colours, as a row of texels read by group index; nearest, so no two colours are ever mixed
  const paletteTexture = gl.createTexture()!;
  gl.bindTexture(gl.TEXTURE_2D, paletteTexture);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([128, 128, 128, 255]));

  // and the picture wrapped round the ball, when there is one
  const surfaceTexture = gl.createTexture()!;
  gl.bindTexture(gl.TEXTURE_2D, surfaceTexture);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([0, 0, 0, 0]));

  function cameraOf(camera: GlobeCamera): { eye: Vec3; viewProj: Mat4 } {
    const distance = distanceOf(camera);
    const eye = scale(unitVector(camera.lat, camera.lon), distance);
    const forward = normalize(scale(eye, -1));
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

  function project(camera: GlobeCamera) {
    const { eye, viewProj } = cameraOf(camera);
    return (lat: number, lon: number, out: Float32Array) => {
      const p = unitVector(lat, lon);
      return toScreen(viewProj, eye, p[0], p[1], p[2], out);
    };
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
      canvas.width = Math.round(width * dpr);
      canvas.height = Math.round(height * dpr);
    },
    setTheme(next) {
      theme = next;
    },
    setPoints(unit, count) {
      pointCount = count;
      gl.bindBuffer(gl.ARRAY_BUFFER, pointBuffer);
      gl.bufferData(gl.ARRAY_BUFFER, unit.subarray(0, count * 3), gl.STATIC_DRAW);
    },
    setColors(palette, assignment) {
      groupCount = Math.max(1, palette.length / 3);
      const rgba = new Uint8Array(groupCount * 4);
      for (let i = 0; i < groupCount; i++) {
        rgba[i * 4] = palette[i * 3];
        rgba[i * 4 + 1] = palette[i * 3 + 1];
        rgba[i * 4 + 2] = palette[i * 3 + 2];
        rgba[i * 4 + 3] = 255;
      }
      gl.bindTexture(gl.TEXTURE_2D, paletteTexture);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, groupCount, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, rgba);
      const groups = assignment ?? new Uint16Array(pointCount); // all of them in group 0
      gl.bindBuffer(gl.ARRAY_BUFFER, groupBuffer);
      gl.bufferData(gl.ARRAY_BUFFER, groups.subarray(0, pointCount), gl.STATIC_DRAW);
    },
    setSurface(surface) {
      hasSurface = surface !== null;
      gl.bindTexture(gl.TEXTURE_2D, surfaceTexture);
      if (surface === null) gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([0, 0, 0, 0]));
      else gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, surface);
    },
    draw(camera, marks, size, alpha, limit) {
      const { eye, viewProj } = cameraOf(camera);
      gl.viewport(0, 0, canvas.width, canvas.height);
      gl.clearColor(theme.clear[0], theme.clear[1], theme.clear[2], 1);
      gl.clearDepth(1);
      gl.enable(gl.DEPTH_TEST);
      gl.depthFunc(gl.LEQUAL);
      gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
      gl.disable(gl.BLEND);

      gl.useProgram(sphereProgram);
      gl.uniformMatrix4fv(gl.getUniformLocation(sphereProgram, "uViewProj"), false, viewProj);
      gl.uniform3fv(gl.getUniformLocation(sphereProgram, "uEye"), eye);
      gl.uniform3fv(gl.getUniformLocation(sphereProgram, "uGround"), theme.ground);
      gl.uniform3fv(gl.getUniformLocation(sphereProgram, "uGlow"), theme.glow);
      gl.uniform1i(gl.getUniformLocation(sphereProgram, "uHasSurface"), hasSurface ? 1 : 0);
      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, surfaceTexture);
      gl.uniform1i(gl.getUniformLocation(sphereProgram, "uSurface"), 0);
      gl.bindVertexArray(sphereVao);
      gl.drawElements(gl.TRIANGLES, sphere.count, gl.UNSIGNED_INT, 0);

      gl.enable(gl.BLEND);
      gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
      gl.useProgram(lineProgram);
      gl.uniformMatrix4fv(gl.getUniformLocation(lineProgram, "uViewProj"), false, viewProj);
      gl.uniform3fv(gl.getUniformLocation(lineProgram, "uColor"), theme.line);
      gl.bindVertexArray(lineVao);
      gl.drawArrays(gl.LINES, 0, outline.count);

      const drawing = Math.max(0, Math.min(pointCount, limit));
      if (marks !== "none" && drawing > 0) {
        gl.useProgram(pointProgram);
        gl.uniformMatrix4fv(gl.getUniformLocation(pointProgram, "uViewProj"), false, viewProj);
        gl.uniform3fv(gl.getUniformLocation(pointProgram, "uEye"), eye);
        // a pin's sprite has to hold the whole teardrop, which is about one and a half heads tall
        gl.uniform1f(gl.getUniformLocation(pointProgram, "uSize"), Math.max(1, size * dpr) * (marks === "pins" ? 1.7 : 1));
        gl.uniform1f(gl.getUniformLocation(pointProgram, "uAlpha"), alpha);
        gl.uniform1f(gl.getUniformLocation(pointProgram, "uGroups"), groupCount);
        gl.uniform1i(gl.getUniformLocation(pointProgram, "uPin"), marks === "pins" ? 1 : 0);
        gl.uniform1f(gl.getUniformLocation(pointProgram, "uHeight"), canvas.height);
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, paletteTexture);
        gl.uniform1i(gl.getUniformLocation(pointProgram, "uPalette"), 0);
        // a pin stands on its place and reaches up out of the ball, so it must not be cut away by
        // the surface it is standing on; a dot lies flat on it and is
        gl.depthMask(false);
        gl.bindVertexArray(pointVao);
        gl.drawArrays(gl.POINTS, 0, drawing);
        gl.depthMask(true);
      }
      gl.bindVertexArray(null);
    },
    project,
    projectUnit(camera) {
      const { eye, viewProj } = cameraOf(camera);
      return (x, y, z, out) => toScreen(viewProj, eye, x, y, z, out);
    },
    placeAt(camera, px, py) {
      const { eye, viewProj } = cameraOf(camera);
      // a ray through the pixel: undo the projection at two depths and take the direction between
      const ndcX = (px / width) * 2 - 1;
      const ndcY = 1 - (py / height) * 2;
      const inverse = invert(viewProj);
      if (inverse === null) return null;
      const near = unproject(inverse, ndcX, ndcY, -1);
      const far = unproject(inverse, ndcX, ndcY, 1);
      const dir = normalize([far[0] - near[0], far[1] - near[1], far[2] - near[2]]);
      const t = raySphere(eye, dir, [0, 0, 0], 1);
      if (t === null) return null;
      const hit: Vec3 = [eye[0] + dir[0] * t, eye[1] + dir[1] * t, eye[2] + dir[2] * t];
      return placeOf(hit[0], hit[1], hit[2]);
    },
    degreesPerPixel(camera) {
      // how much of the ball the view covers where it meets the surface, spread over its pixels
      const halfWorld = Math.tan(fov / 2) * (distanceOf(camera) - 1);
      return ((halfWorld * 2) / height) * (180 / Math.PI);
    },
    destroy() {
      gl.deleteProgram(sphereProgram);
      gl.deleteProgram(lineProgram);
      gl.deleteProgram(pointProgram);
      gl.deleteBuffer(pointBuffer);
      gl.deleteBuffer(groupBuffer);
      gl.deleteBuffer(sphere.positions);
      gl.deleteBuffer(sphere.indices);
      gl.deleteBuffer(outline.positions);
      gl.deleteTexture(paletteTexture);
      gl.deleteTexture(surfaceTexture);
      gl.deleteVertexArray(sphereVao);
      gl.deleteVertexArray(lineVao);
      gl.deleteVertexArray(pointVao);
    },
  };
}

const cross = (a: Vec3, b: Vec3): Vec3 => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];

/** A point of clip space back into the world; the caller has already inverted the matrix. */
function unproject(inverse: Mat4, x: number, y: number, z: number): Vec3 {
  const p = transform(inverse, [x, y, z]);
  const w = p[3] === 0 ? 1e-6 : p[3];
  return [p[0] / w, p[1] / w, p[2] / w];
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

// ---- the meshes ----

/** A sphere of unit radius as a grid of quads; 128 x 64 is smooth to the eye at any zoom a map reaches. */
function buildSphere(gl: WebGL2RenderingContext, columns: number, rows: number) {
  const positions = new Float32Array((columns + 1) * (rows + 1) * 3);
  let at = 0;
  for (let row = 0; row <= rows; row++) {
    const lat = 90 - (row * 180) / rows;
    for (let col = 0; col <= columns; col++) {
      const lon = -180 + (col * 360) / columns;
      const p = unitVector(lat, lon);
      positions[at++] = p[0];
      positions[at++] = p[1];
      positions[at++] = p[2];
    }
  }
  const indices = new Uint32Array(columns * rows * 6);
  let k = 0;
  for (let row = 0; row < rows; row++) {
    for (let col = 0; col < columns; col++) {
      const a = row * (columns + 1) + col;
      const b = a + columns + 1;
      indices[k++] = a;
      indices[k++] = b;
      indices[k++] = a + 1;
      indices[k++] = a + 1;
      indices[k++] = b;
      indices[k++] = b + 1;
    }
  }
  return { positions: buffer(gl, gl.ARRAY_BUFFER, positions), indices: buffer(gl, gl.ELEMENT_ARRAY_BUFFER, indices), count: indices.length };
}

/**
 * Every arc of the world as line segments on the sphere, a pair of points each. A long straight
 * step in longitude and latitude is not a straight line on a ball, so the long ones are broken up
 * until each piece is short enough to hug the surface.
 */
function buildOutline(gl: WebGL2RenderingContext) {
  const points: number[] = [];
  const push = (lat: number, lon: number) => {
    const p = unitVector(lat, lon);
    points.push(p[0] * lineLift, p[1] * lineLift, p[2] * lineLift);
  };
  for (const arc of world().arcs) {
    const count = arc.length / 2;
    for (let i = 1; i < count; i++) {
      const lon0 = arc[(i - 1) * 2];
      const lat0 = arc[(i - 1) * 2 + 1];
      const lon1 = arc[i * 2];
      const lat1 = arc[i * 2 + 1];
      if (Math.abs(lon1 - lon0) > 180) continue; // a step across the seam: not a line, a wrap
      const steps = Math.min(48, Math.max(1, Math.ceil(Math.max(Math.abs(lon1 - lon0), Math.abs(lat1 - lat0)) / maxLineDegrees)));
      for (let s = 0; s < steps; s++) {
        const t0 = s / steps;
        const t1 = (s + 1) / steps;
        push(lat0 + (lat1 - lat0) * t0, lon0 + (lon1 - lon0) * t0);
        push(lat0 + (lat1 - lat0) * t1, lon0 + (lon1 - lon0) * t1);
      }
    }
  }
  const positions = new Float32Array(points);
  return { positions: buffer(gl, gl.ARRAY_BUFFER, positions), count: positions.length / 3 };
}

function buffer(gl: WebGL2RenderingContext, target: number, data: BufferSource): WebGLBuffer {
  const b = gl.createBuffer()!;
  gl.bindBuffer(target, b);
  gl.bufferData(target, data, gl.STATIC_DRAW);
  return b;
}

function bindAttribute(gl: WebGL2RenderingContext, prog: WebGLProgram, name: string, size: number) {
  const at = gl.getAttribLocation(prog, name);
  if (at < 0) return;
  gl.enableVertexAttribArray(at);
  gl.vertexAttribPointer(at, size, gl.FLOAT, false, 0, 0);
}

// ---- the shaders ----

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

const sphereVert = `#version 300 es
  in vec3 aPos;
  uniform mat4 uViewProj;
  out vec3 vPos;
  void main() {
    vPos = aPos;
    gl_Position = uViewProj * vec4(aPos, 1.0);
  }`;

/**
 * The ball. Lit from over the camera's shoulder rather than from a sun somewhere, so no part of the
 * world is ever in the dark - this is a chart, not a planetarium - with the light falling off
 * towards the edge just enough to read as a curve, and a glow at the very rim.
 */
const sphereFrag = `#version 300 es
  precision highp float;
  in vec3 vPos;
  uniform vec3 uEye;
  uniform vec3 uGround;
  uniform vec3 uGlow;
  uniform bool uHasSurface;
  uniform sampler2D uSurface;
  out vec4 outColor;
  const float PI = 3.14159265359;
  void main() {
    vec3 n = normalize(vPos);
    vec3 toEye = normalize(uEye - vPos);
    float facing = max(0.0, dot(n, toEye));
    vec3 base = uGround;
    if (uHasSurface) {
      vec2 uv = vec2(0.5 + atan(n.x, n.z) / (2.0 * PI), 0.5 - asin(clamp(n.y, -1.0, 1.0)) / PI);
      vec4 painted = texture(uSurface, uv);
      base = mix(base, painted.rgb, painted.a);
    }
    float shade = 0.72 + 0.28 * pow(facing, 0.7);
    float rim = pow(1.0 - facing, 3.0);
    outColor = vec4(base * shade + uGlow * rim * 0.35, 1.0);
  }`;

const lineVert = `#version 300 es
  in vec3 aPos;
  uniform mat4 uViewProj;
  void main() {
    gl_Position = uViewProj * vec4(aPos, 1.0);
  }`;

const lineFrag = `#version 300 es
  precision highp float;
  uniform vec3 uColor;
  out vec4 outColor;
  void main() { outColor = vec4(uColor, 0.9); }`;

/**
 * One node, as a point sprite standing on the surface. A node on the far side of the ball is thrown
 * outside the clip volume rather than drawn and hidden, which costs one dot product and saves the
 * fill; the depth test then takes care of the ones near the edge.
 *
 * A pin is lifted by half its own height so that its tip, rather than its middle, is on the place -
 * in clip space, so it stays put whatever the perspective is doing at that part of the ball.
 */
const pointVert = `#version 300 es
  in vec3 aPos;
  in uint aGroup;
  uniform mat4 uViewProj;
  uniform vec3 uEye;
  uniform float uSize;
  uniform float uGroups;
  uniform float uHeight;
  uniform bool uPin;
  flat out uint vGroup;
  void main() {
    vec3 p = aPos * ${markLift.toFixed(4)};
    if (dot(normalize(p), normalize(uEye - p)) <= 0.0) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
    vGroup = aGroup;
    gl_Position = uViewProj * vec4(p, 1.0);
    gl_PointSize = uSize;
    if (uPin) gl_Position.y += (uSize / uHeight) * gl_Position.w;
  }`;

/** A dot is a soft disc; a pin is a disc with a tail, which is the same shape a pin on the flat map has. */
const pointFrag = `#version 300 es
  precision highp float;
  flat in uint vGroup;
  uniform sampler2D uPalette;
  uniform float uGroups;
  uniform float uAlpha;
  uniform bool uPin;
  out vec4 outColor;
  void main() {
    vec2 d = gl_PointCoord - vec2(0.5);
    float mask;
    if (uPin) {
      // the head, and a wedge narrowing to the bottom middle where the place is
      float head = 1.0 - smoothstep(0.20, 0.24, length(vec2(d.x, d.y + 0.16)));
      float tail = (1.0 - smoothstep(0.0, 0.02, abs(d.x) - 0.19 * (0.5 - d.y))) * step(-0.16, d.y) * step(d.y, 0.48);
      mask = max(head, tail);
    } else {
      mask = 1.0 - smoothstep(0.36, 0.5, length(d));
    }
    if (mask <= 0.01) discard;
    vec4 c = texture(uPalette, vec2((float(vGroup) + 0.5) / uGroups, 0.5));
    outColor = vec4(c.rgb, mask * uAlpha);
  }`;
