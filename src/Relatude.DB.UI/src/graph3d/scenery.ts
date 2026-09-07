import { buildTerrainMesh, recentreTerrainMesh, type TerrainMesh } from "./terrain";
import type { Mat4, Vec3 } from "./math";
import type { RGB } from "./renderer";

/**
 * The landscape behind the graph: the sky, the range, and the motes in the air.
 *
 * The sky and the terrain are the ones from db.relatude.com, kept close to the original so the
 * country under the graph is the same country as the front page - flat facets whose normals come
 * from the screen-space derivative of the world position, a shade ramp from deep to hot, a lattice
 * on the low flats, and haze that pools in the valleys. What is new is the palette: rather than the
 * page's two fixed themes, every colour is derived from the admin UI's own tokens, so the range is
 * painted in whatever theme the user has chosen and fades into the same ground the panels sit on.
 *
 * The terrain is modelled in terrain units and scaled up on the way to the screen, so every constant
 * tuned in the shader keeps its meaning while the ranges still tower over a graph that is only a few
 * hundred units across.
 */

export interface ScenePalette {
  deep: RGB;
  mid: RGB;
  lit: RGB;
  hot: RGB;
  fog: RGB;
  sun: RGB;
  top: RGB;
  /** the starfield only belongs on a dark sky */
  star: number;
}

/**
 * The scene's colours, read off the admin UI's theme tokens. On a dark theme the rock rises out of
 * the page's own black toward the text colour, so peaks catch the light; on a light one the ramp is
 * inverted and the ridges read as silhouettes against a bright sky, which is the only way a range
 * shows up on white at all. Nothing here takes the accent: the weather is colourless, and a tinted
 * atmosphere reads as a filter over the picture rather than as air.
 */
export function scenePalette(panel: RGB, bg: RGB, text: RGB, dark: boolean): ScenePalette {
  const mix = (a: RGB, b: RGB, t: number): RGB => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];
  const scale = (a: RGB, s: number): RGB => [a[0] * s, a[1] * s, a[2] * s];
  if (dark) {
    return {
      deep: scale(panel, 0.34),
      mid: mix(panel, text, 0.14),
      lit: mix(panel, text, 0.58),
      hot: mix(panel, text, 0.93),
      fog: panel,
      // The haze is colourless. It used to be the page's accent, which put a blue wash over the
      // whole range: the accent belongs to the interface, not to the weather.
      sun: mix(panel, [1, 1, 1], 0.3),
      top: scale(bg, 0.5),
      star: 1,
    };
  }
  return {
    deep: mix(panel, text, 0.74),
    mid: mix(panel, text, 0.5),
    lit: mix(panel, text, 0.23),
    hot: mix(panel, text, 0.08),
    fog: panel,
    // barely there on a white sky, which is what a colourless haze on a bright day amounts to
    sun: mix(panel, text, 0.05),
    top: mix(bg, text, 0.2),
    star: 0,
  };
}

export interface Scenery {
  /** Fills the frame with sky. No depth: it is the ground everything else is drawn onto. */
  sky(viewProj: Mat4, eye: Vec3, light: Vec3, pal: ScenePalette, time: number): void;
  /** The range, centred on a point in terrain units and drawn at the scene's scale. */
  terrain(viewProj: Mat4, eye: Vec3, light: Vec3, pal: ScenePalette, centre: [number, number], scale: number, offsetY: number): void;
  /** Motes in the air, wrapped in a box that travels with the camera so there are always some about. */
  /** Motes in the air, part of the landscape: there is nothing to see them against without it. */
  dust(viewProj: Mat4, eye: Vec3, pal: ScenePalette, dark: boolean, time: number): void;
  destroy(): void;
}

export function createScenery(gl: WebGL2RenderingContext): Scenery {
  const skyProg = program(gl, SKY_VS, SKY_FS);
  const terProg = program(gl, TER_VS, TER_FS);
  const dstProg = program(gl, DUST_VS, DUST_FS);

  // The motes. Each is a fixed point inside a box that travels with the camera, so however far the
  // camera goes there is always the same handful of specks drifting past it. Size and phase are
  // baked in, so nothing has to be uploaded again once they are made.
  const dustCount = 2600;
  const dustData = new Float32Array(dustCount * 4);
  for (let i = 0; i < dustCount; i++) {
    dustData[i * 4] = Math.random();
    dustData[i * 4 + 1] = Math.random();
    dustData[i * 4 + 2] = Math.random();
    dustData[i * 4 + 3] = Math.random();
  }
  const dstVao = gl.createVertexArray()!;
  const dstBuf = gl.createBuffer()!;
  gl.bindVertexArray(dstVao);
  gl.bindBuffer(gl.ARRAY_BUFFER, dstBuf);
  gl.bufferData(gl.ARRAY_BUFFER, dustData, gl.STATIC_DRAW);
  const dstLoc = gl.getAttribLocation(dstProg, "aSeed");
  gl.enableVertexAttribArray(dstLoc);
  gl.vertexAttribPointer(dstLoc, 4, gl.FLOAT, false, 0, 0);
  gl.bindVertexArray(null);

  // One patch of ground, moved with the camera rather than rebuilt around it. 320 quads of 2.6
  // terrain units reach 832 units in every direction, which at the scene's scale is eleven
  // kilometres of country - and the whole field is still cheaper to rebuild than the smaller,
  // coarser one was before the noise was tightened up (see terrain.ts).
  const mesh: TerrainMesh = buildTerrainMesh(320, 320, 0, 0);
  const terVao = gl.createVertexArray()!;
  const terPos = gl.createBuffer()!;
  const terIdx = gl.createBuffer()!;
  gl.bindVertexArray(terVao);
  gl.bindBuffer(gl.ARRAY_BUFFER, terPos);
  gl.bufferData(gl.ARRAY_BUFFER, mesh.positions, gl.DYNAMIC_DRAW);
  const terLoc = gl.getAttribLocation(terProg, "aPos");
  gl.enableVertexAttribArray(terLoc);
  gl.vertexAttribPointer(terLoc, 3, gl.FLOAT, false, 0, 0);
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, terIdx);
  gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, mesh.indices, gl.STATIC_DRAW);
  gl.bindVertexArray(null);

  const u = (p: WebGLProgram, n: string) => gl.getUniformLocation(p, n);
  const pal3 = (p: WebGLProgram, pal: ScenePalette) => {
    gl.uniform3fv(u(p, "C_DEEP"), pal.deep);
    gl.uniform3fv(u(p, "C_MID"), pal.mid);
    gl.uniform3fv(u(p, "C_LIT"), pal.lit);
    gl.uniform3fv(u(p, "C_HOT"), pal.hot);
    gl.uniform3fv(u(p, "C_FOG"), pal.fog);
    gl.uniform3fv(u(p, "C_SUN"), pal.sun);
  };

  return {
    sky(viewProj, eye, light, pal, time) {
      gl.useProgram(skyProg);
      gl.disable(gl.DEPTH_TEST);
      gl.depthMask(false);
      gl.disable(gl.BLEND);
      gl.disable(gl.CULL_FACE); // the one triangle is not wound for any particular face
      gl.uniformMatrix4fv(u(skyProg, "uInvVP"), false, invert(viewProj));
      gl.uniform3fv(u(skyProg, "uCam"), eye);
      gl.uniform3fv(u(skyProg, "uLight"), light);
      gl.uniform1f(u(skyProg, "uTime"), time);
      gl.uniform1f(u(skyProg, "uHaze"), HAZE);
      gl.uniform1f(u(skyProg, "C_STAR"), pal.star);
      gl.uniform3fv(u(skyProg, "C_FOG"), pal.fog);
      gl.uniform3fv(u(skyProg, "C_TOP"), pal.top);
      gl.uniform3fv(u(skyProg, "C_SUN"), pal.sun);
      gl.uniform3fv(u(skyProg, "C_HOT"), pal.hot);
      gl.bindVertexArray(null);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
      gl.enable(gl.DEPTH_TEST);
      gl.depthMask(true);
      gl.enable(gl.BLEND);
    },
    terrain(viewProj, eye, light, pal, centre, scale, offsetY) {
      if (recentreTerrainMesh(mesh, centre[0], centre[1])) {
        gl.bindBuffer(gl.ARRAY_BUFFER, terPos);
        gl.bufferSubData(gl.ARRAY_BUFFER, 0, mesh.positions);
      }
      gl.useProgram(terProg);
      gl.enable(gl.DEPTH_TEST);
      gl.depthMask(true);
      gl.disable(gl.CULL_FACE);
      gl.disable(gl.BLEND); // the ground is solid
      gl.uniformMatrix4fv(u(terProg, "uVP"), false, viewProj);
      // the camera is handed over in terrain units, so every distance the shader is tuned for
      // (the haze, the lattice fade, the snow line) keeps the meaning it has on the front page
      gl.uniform3f(u(terProg, "uCam"), eye[0] / scale, (eye[1] - offsetY) / scale, eye[2] / scale);
      gl.uniform3fv(u(terProg, "uLight"), light);
      gl.uniform1f(u(terProg, "uScale"), scale);
      gl.uniform1f(u(terProg, "uOffsetY"), offsetY);
      gl.uniform1f(u(terProg, "uHaze"), HAZE);
      pal3(terProg, pal);
      gl.bindVertexArray(terVao);
      gl.drawElements(gl.TRIANGLES, mesh.indices.length, gl.UNSIGNED_INT, 0);
      gl.bindVertexArray(null);
    },
    dust(viewProj, eye, pal, dark, time) {
      gl.useProgram(dstProg);
      gl.enable(gl.DEPTH_TEST);
      gl.depthMask(false); // motes do not hide one another, and nothing hides behind a mote
      gl.disable(gl.CULL_FACE);
      gl.enable(gl.BLEND);
      // adding light to a near-white sky renders nothing, so on a light theme they darken instead
      if (dark) gl.blendFunc(gl.SRC_ALPHA, gl.ONE);
      else gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
      gl.uniformMatrix4fv(u(dstProg, "uVP"), false, viewProj);
      gl.uniform3fv(u(dstProg, "uCam"), eye);
      gl.uniform1f(u(dstProg, "uTime"), time);
      gl.uniform1f(u(dstProg, "uSpan"), dustSpan);
      gl.uniform3fv(u(dstProg, "uTint"), dark ? pal.hot : pal.deep);
      gl.bindVertexArray(dstVao);
      gl.drawArrays(gl.POINTS, 0, dustCount);
      gl.bindVertexArray(null);
      gl.depthMask(true);
      gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
    },
    destroy() {
      for (const p of [skyProg, terProg, dstProg]) gl.deleteProgram(p);
      for (const v of [terVao, dstVao]) gl.deleteVertexArray(v);
      for (const b of [terPos, terIdx, dstBuf]) gl.deleteBuffer(b);
    },
  };
}

// ---- shaders ----

function compile(gl: WebGL2RenderingContext, type: number, src: string) {
  const sh = gl.createShader(type)!;
  gl.shaderSource(sh, src);
  gl.compileShader(sh);
  if (!gl.getShaderParameter(sh, gl.COMPILE_STATUS)) throw new Error("shader: " + gl.getShaderInfoLog(sh));
  return sh;
}

function program(gl: WebGL2RenderingContext, vs: string, fs: string) {
  const p = gl.createProgram()!;
  gl.attachShader(p, compile(gl, gl.VERTEX_SHADER, vs));
  gl.attachShader(p, compile(gl, gl.FRAGMENT_SHADER, fs));
  gl.linkProgram(p);
  if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error("program: " + gl.getProgramInfoLog(p));
  return p;
}

/**
 * How thick the air is. The range runs for kilometres while the graph it stands around is a few
 * hundred units across, so without a heavy atmosphere every ridge from here to the horizon comes out
 * at the same strength and the whole picture goes flat.
 */
/**
 * How thick the air is, for the sky's haze band and the range's own fog alike - they share it so a
 * distant ridge dissolves into exactly the sky behind it, with no seam at the horizon.
 *
 * It is a viewing distance in disguise. The front page's value (3.2) is for standing IN the range,
 * a few dozen terrain units from the rock; here the graph is the subject and the range is behind it,
 * seen from hundreds of units away, and at 3.2 every ridge fogs to the panel colour and the
 * landscape is simply not there. Tuned so the ground under the graph is lightly veiled and the far
 * edge of the mesh is nearly gone.
 */
const HAZE = 0.6;

const SKY_VS = `#version 300 es
out vec2 vNdc;
void main(){
  vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2)) * 2.0 - 1.0;
  vNdc = p;
  gl_Position = vec4(p, 1.0, 1.0);
}`;

const SKY_FS = `#version 300 es
precision highp float;
in vec2 vNdc;
uniform mat4 uInvVP;
uniform vec3 uCam, uLight;
uniform float uTime, C_STAR;
uniform vec3 C_FOG, C_TOP, C_SUN, C_HOT;
out vec4 frag;
uniform float uHaze;
float hash13(vec3 p){
  p = fract(p * 0.1031);
  p += dot(p, p.yzx + 33.33);
  return fract((p.x + p.y) * p.z);
}
void main(){
  vec4 w = uInvVP * vec4(vNdc, 1.0, 1.0);
  vec3 dir = normalize(w.xyz / w.w - uCam);
  float up = clamp(dir.y, -1.0, 1.0);
  // the thick air lifts the haze band well up the sky, so the ridges dissolve into the same
  // ground the terrain fades to rather than standing against a clear zenith
  vec3 col = mix(C_FOG, C_TOP, pow(clamp(up, 0.0, 1.0), 0.55 * uHaze));
  col = mix(col, C_FOG * 0.82, smoothstep(0.0, -0.25, up));
  // the low sun haze; the terrain fog uses the same term, so distant ridges
  // dissolve into the sky with no visible seam
  float sun = pow(max(dot(dir, normalize(vec3(uLight.x, 0.0, uLight.z))), 0.0), 3.0);
  col += C_SUN * sun * exp(-abs(up) * 4.5) * 0.85;
  col += C_SUN * pow(max(dot(dir, uLight), 0.0), 34.0) * 0.55;
  vec3 g = dir * 190.0;
  vec3 id = floor(g), f = fract(g);
  float r = hash13(id);
  if (r > 0.9825){
    vec3 c = vec3(hash13(id + 11.0), hash13(id + 23.0), hash13(id + 37.0));
    float s = smoothstep(0.30, 0.0, length(f - c));
    float tw = 0.55 + 0.45 * sin(uTime * 0.8 + r * 340.0);
    col += C_HOT * s * tw * 0.55 * C_STAR * smoothstep(0.02, 0.42, up);
  }
  frag = vec4(col, 1.0);
}`;

// vW stays in terrain units; only the position handed to the rasteriser is scaled, so the shader's
// constants keep the meaning they were tuned with
const TER_VS = `#version 300 es
in vec3 aPos;
uniform mat4 uVP;
uniform float uScale, uOffsetY;
out vec3 vW;
void main(){
  vW = aPos;
  gl_Position = uVP * vec4(aPos.x * uScale, aPos.y * uScale + uOffsetY, aPos.z * uScale, 1.0);
}`;

const TER_FS = `#version 300 es
precision highp float;
in vec3 vW;
uniform vec3 uCam, uLight;
uniform vec3 C_DEEP, C_MID, C_LIT, C_HOT, C_FOG, C_SUN;
out vec4 frag;
uniform float uHaze;
void main(){
  // Exact per-facet normal: vW is planar across each triangle, so its screen-space
  // derivative IS that triangle's normal. Flat shading, no normal attribute.
  vec3 n = normalize(cross(dFdx(vW), dFdy(vW)));
  if (n.y < 0.0) n = -n;
  vec3 toCam = uCam - vW;
  float dist = length(toCam);
  vec3 V = toCam / dist;
  float lam  = max(dot(n, uLight), 0.0);
  float skyf = 0.5 + 0.5 * n.y;
  float rim  = pow(1.0 - max(dot(n, V), 0.0), 3.0);
  float peak = smoothstep(38.0, 118.0, vW.y);
  float shade = 0.060 + 0.78 * lam + 0.23 * skyf * skyf + 0.30 * rim + 0.40 * peak * (0.25 + 0.75 * lam);
  vec3 col = mix(C_DEEP, C_MID, clamp(shade * 1.55, 0.0, 1.0));
  col = mix(col, C_LIT, clamp((shade - 0.42) * 1.35, 0.0, 1.0));
  col = mix(col, C_HOT, clamp((shade - 0.98) * 1.10, 0.0, 1.0));
  // a lattice on the low flats, like a substrate the range has grown out of
  vec2 gp = vW.xz * 0.08333;
  vec2 gw = fwidth(gp);
  vec2 gg = abs(fract(gp - 0.5) - 0.5) / max(gw, vec2(1e-5));
  float grid = 1.0 - min(min(gg.x, gg.y), 1.0);
  grid *= smoothstep(3.0, -10.0, vW.y) * pow(max(n.y, 0.0), 5.0);
  grid *= 1.0 - smoothstep(45.0, 195.0, dist);
  col += C_LIT * grid * 0.18;
  // atmosphere, thicker down low, so the valleys pool with haze
  float low = exp(-max(vW.y + 8.0, 0.0) * 0.048);
  float fd  = dist * (0.0050 + 0.0062 * low) * uHaze;
  float fog = 1.0 - exp(-fd * fd);
  float sun = pow(max(dot(-V, normalize(vec3(uLight.x, 0.0, uLight.z))), 0.0), 3.0);
  vec3 fogC = C_FOG * (0.92 + 0.42 * low) + C_SUN * sun * 0.65;
  frag = vec4(mix(col, fogC, fog), 1.0);
}`;

// The side of the box the motes wrap inside. Deliberately tighter than the fog: it packs them into
// the near volume where a speck is still big enough to see, instead of scattering most of them out
// where they would fog down to nothing.
const dustSpan = 260;

const DUST_VS = `#version 300 es
in vec4 aSeed;
uniform mat4 uVP;
uniform vec3 uCam;
uniform float uTime, uSpan;
out float vFade;
out float vPhase;
void main(){
  // a fixed point in a box that follows the camera: take the camera's cell and add the mote's own
  // offset, so a mote leaving one side reappears on the other with nothing to animate
  vec3 drift = vec3(0.0, -uTime * 0.9, uTime * 0.35) * (0.4 + aSeed.w);
  vec3 p = fract(aSeed.xyz + drift / uSpan) * uSpan;
  vec3 base = floor(uCam / uSpan) * uSpan - uSpan * 0.5;
  vec3 w = base + p;
  // and if it lands behind the camera's own cell, push it a box forward
  vec3 d = w - uCam;
  w -= uSpan * step(vec3(uSpan * 0.5), d);
  w += uSpan * step(d, vec3(-uSpan * 0.5));
  vec4 clip = uVP * vec4(w, 1.0);
  float dist = length(w - uCam);
  gl_Position = clip;
  gl_PointSize = clamp((1.6 + aSeed.w * 2.6) * 90.0 / max(dist, 1.0), 1.0, 7.0);
  // fade in from the near clip and out into the haze, so none of them pop
  vFade = smoothstep(4.0, 26.0, dist) * (1.0 - smoothstep(uSpan * 0.35, uSpan * 0.55, dist));
  vPhase = aSeed.w;
}`;

const DUST_FS = `#version 300 es
precision highp float;
in float vFade;
in float vPhase;
uniform vec3 uTint;
uniform float uTime;
out vec4 frag;
void main(){
  vec2 d = gl_PointCoord - 0.5;
  float r = length(d) * 2.0;
  if (r > 1.0) discard;
  float soft = 1.0 - r * r;
  float tw = 0.65 + 0.35 * sin(uTime * 1.7 + vPhase * 31.0);
  frag = vec4(uTint, soft * vFade * tw * 0.5);
}`;

// ---- a 4x4 inverse, for turning the sky's clip corners back into rays ----

function invert(m: Mat4): Float32Array {
  const o = new Float32Array(16);
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
  let det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
  if (!det) return o;
  det = 1 / det;
  o[0] = (a11 * b11 - a12 * b10 + a13 * b09) * det;
  o[1] = (a02 * b10 - a01 * b11 - a03 * b09) * det;
  o[2] = (a31 * b05 - a32 * b04 + a33 * b03) * det;
  o[3] = (a22 * b04 - a21 * b05 - a23 * b03) * det;
  o[4] = (a12 * b08 - a10 * b11 - a13 * b07) * det;
  o[5] = (a00 * b11 - a02 * b08 + a03 * b07) * det;
  o[6] = (a32 * b02 - a30 * b05 - a33 * b01) * det;
  o[7] = (a20 * b05 - a22 * b02 + a23 * b01) * det;
  o[8] = (a10 * b10 - a11 * b08 + a13 * b06) * det;
  o[9] = (a01 * b08 - a00 * b10 - a03 * b06) * det;
  o[10] = (a30 * b04 - a31 * b02 + a33 * b00) * det;
  o[11] = (a21 * b02 - a20 * b04 - a23 * b00) * det;
  o[12] = (a11 * b07 - a10 * b09 - a12 * b06) * det;
  o[13] = (a00 * b09 - a01 * b07 + a02 * b06) * det;
  o[14] = (a31 * b01 - a30 * b03 - a32 * b00) * det;
  o[15] = (a20 * b03 - a21 * b01 + a22 * b00) * det;
  return o;
}
