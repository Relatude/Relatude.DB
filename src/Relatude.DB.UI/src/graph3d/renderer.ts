import type { Mat4, Vec3 } from "./math";

/**
 * Draws the 3D graph with plain WebGL 2: spheres for nodes, screen-width ribbons for the lines and
 * cones for the arrowheads, all instanced, so a few hundred of each cost a handful of draw calls.
 * A frame is gathered between begin() and end(): the caller adds what it wants drawn, in world
 * units, and end() uploads and draws it. Colours are 0..1 floats; the far end fades into the fog
 * colour so depth can be read at a glance.
 */

export type RGB = [number, number, number];

export interface FrameSetup {
  view: Mat4;
  proj: Mat4;
  eye: Vec3;
  width: number; // in device pixels
  height: number;
  /** what the far end fades toward, and what the frame is cleared to */
  fog: RGB;
  /** where the fade starts and where it is complete, as distances from the eye */
  fogNear: number;
  fogFar: number;
  /** the near and far planes the projection was built with */
  near: number;
  far: number;
  /**
   * Drawn after the frame is cleared and before anything of the graph: the sky, the range and the
   * aeroplane of fun mode. It shares the depth buffer, so the graph is occluded by a mountain in
   * front of it, and the state it leaves behind is put back before the graph is drawn.
   */
  background?: () => void;
}

export type Dash = 0 | 1 | 2; // solid, dashed (6 on 4 off), dotted (2 on 3 off)

export interface Renderer {
  /** the context the graph draws on, so fun mode's scenery can share it */
  gl: WebGL2RenderingContext;
  resize(width: number, height: number): void;
  begin(setup: FrameSetup): void;
  /** A lit sphere; rim adds a glow of that colour along the silhouette, strength 0..1. */
  sphere(center: Vec3, r: number, color: RGB, alpha: number, rim?: RGB, rimStrength?: number): void;
  /** A translucent shell, drawn after everything opaque. */
  halo(center: Vec3, r: number, color: RGB, alpha: number): void;
  /** An axis-aligned box, lit and fogged like the spheres. A number is a cube of that side. */
  box(center: Vec3, size: Vec3 | number, color: RGB, alpha: number, rim?: RGB, rimStrength?: number): void;
  /** A line from a to b, its width in device pixels, dashed in world units. */
  line(a: Vec3, b: Vec3, color: RGB, widthPx: number, dash: Dash, alpha: number): void;
  /** A cone with its tip at apex pointing along dir, size = its length in world units. */
  cone(apex: Vec3, dir: Vec3, size: number, color: RGB, alpha: number): void;
  end(): void;
  destroy(): void;
}

export function createRenderer(canvas: HTMLCanvasElement): Renderer | null {
  const context = canvas.getContext("webgl2", { antialias: true, alpha: false, premultipliedAlpha: false, powerPreference: "high-performance" });
  if (!context) return null;
  // a const of the plain type, so the closures below see it as never null
  const gl: WebGL2RenderingContext = context;

  const sphereMesh = buildSphere(gl, 28, 18);
  const boxMesh = buildBox(gl);
  const coneMesh = buildCone(gl, 16);
  const sphereProg = program(gl, sphereVert, sphereFrag);
  const lineProg = program(gl, lineVert, lineFrag);
  const coneProg = program(gl, coneVert, coneFrag);

  // instance streams: interleaved floats, grown as needed, uploaded once per frame
  const spheres = new Stream(12); // x y z r | cr cg cb a | rr rg rb rs
  const halos = new Stream(12);
  const boxes = new Stream(16); // x y z _ | cr cg cb a | rr rg rb rs | sx sy sz _
  const lines = new Stream(12); // ax ay az _ | bx by bz _ | cr cg cb a  + style in the pad slots: [3]=width [7]=dash
  const cones = new Stream(12); // ax ay az size | dx dy dz _ | cr cg cb a

  const sphereVao = instancedVao(gl, sphereProg, sphereMesh, spheres, [
    ["aInst0", 0],
    ["aInst1", 4],
    ["aInst2", 8],
  ]);
  const haloVao = instancedVao(gl, sphereProg, sphereMesh, halos, [
    ["aInst0", 0],
    ["aInst1", 4],
    ["aInst2", 8],
  ]);
  // a box needs a size per axis rather than one radius, so it gets its own vertex shader; the
  // lighting and the fog are the sphere's, so the two read as the same material
  const boxProg = program(gl, boxVert, sphereFrag);
  const boxVao = instancedVao(gl, boxProg, boxMesh, boxes, [
    ["aInst0", 0],
    ["aInst1", 4],
    ["aInst2", 8],
    ["aInst3", 12],
  ]);
  const coneVao = instancedVao(gl, coneProg, coneMesh, cones, [
    ["aInst0", 0],
    ["aInst1", 4],
    ["aInst2", 8],
  ]);
  // a ribbon is two triangles whose corners say where along the line and which side they sit
  const quad = gl.createBuffer()!;
  gl.bindBuffer(gl.ARRAY_BUFFER, quad);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([0, -1, 1, -1, 0, 1, 1, 1]), gl.STATIC_DRAW);
  const lineVao = gl.createVertexArray()!;
  gl.bindVertexArray(lineVao);
  gl.bindBuffer(gl.ARRAY_BUFFER, quad);
  const cornerLoc = gl.getAttribLocation(lineProg, "aCorner");
  gl.enableVertexAttribArray(cornerLoc);
  gl.vertexAttribPointer(cornerLoc, 2, gl.FLOAT, false, 0, 0);
  gl.bindBuffer(gl.ARRAY_BUFFER, lines.buffer(gl));
  for (const [name, offset] of [
    ["aA", 0],
    ["aB", 4],
    ["aColor", 8],
  ] as const) {
    const loc = gl.getAttribLocation(lineProg, name);
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 4, gl.FLOAT, false, 12 * 4, offset * 4);
    gl.vertexAttribDivisor(loc, 1);
  }
  gl.bindVertexArray(null);

  const u = (p: WebGLProgram, name: string) => gl.getUniformLocation(p, name);
  let setup: FrameSetup | null = null;
  let viewProj: Float32Array = new Float32Array(16);
  const lightDir = normalize3([0.45, 0.8, 0.5]);

  function draw() {
    if (!setup) return;
    const s = setup;
    gl.viewport(0, 0, s.width, s.height);
    gl.clearColor(s.fog[0], s.fog[1], s.fog[2], 1);
    gl.enable(gl.DEPTH_TEST);
    gl.depthMask(true);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    // whatever stands behind the graph draws first and leaves the state as it likes
    s.background?.();
    gl.enable(gl.DEPTH_TEST);
    gl.depthMask(true);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
    gl.enable(gl.CULL_FACE);
    gl.cullFace(gl.BACK);

    // opaque first, writing depth: spheres and arrowheads
    gl.useProgram(sphereProg);
    gl.uniformMatrix4fv(u(sphereProg, "uViewProj"), false, viewProj);
    gl.uniform3fv(u(sphereProg, "uEye"), s.eye);
    gl.uniform3fv(u(sphereProg, "uLight"), lightDir);
    gl.uniform3fv(u(sphereProg, "uFog"), s.fog);
    gl.uniform2f(u(sphereProg, "uFogRange"), s.fogNear, s.fogFar);
    if (spheres.count > 0) {
      spheres.upload(gl);
      gl.bindVertexArray(sphereVao);
      gl.drawElementsInstanced(gl.TRIANGLES, sphereMesh.indexCount, gl.UNSIGNED_SHORT, 0, spheres.count);
    }
    if (boxes.count > 0) {
      gl.useProgram(boxProg);
      gl.uniformMatrix4fv(u(boxProg, "uViewProj"), false, viewProj);
      gl.uniform3fv(u(boxProg, "uEye"), s.eye);
      gl.uniform3fv(u(boxProg, "uLight"), lightDir);
      gl.uniform3fv(u(boxProg, "uFog"), s.fog);
      gl.uniform2f(u(boxProg, "uFogRange"), s.fogNear, s.fogFar);
      boxes.upload(gl);
      gl.bindVertexArray(boxVao);
      gl.drawElementsInstanced(gl.TRIANGLES, boxMesh.indexCount, gl.UNSIGNED_SHORT, 0, boxes.count);
      gl.useProgram(sphereProg);
    }
    if (cones.count > 0) {
      gl.useProgram(coneProg);
      gl.uniformMatrix4fv(u(coneProg, "uViewProj"), false, viewProj);
      gl.uniform3fv(u(coneProg, "uEye"), s.eye);
      gl.uniform3fv(u(coneProg, "uLight"), lightDir);
      gl.uniform3fv(u(coneProg, "uFog"), s.fog);
      gl.uniform2f(u(coneProg, "uFogRange"), s.fogNear, s.fogFar);
      cones.upload(gl);
      gl.bindVertexArray(coneVao);
      gl.drawElementsInstanced(gl.TRIANGLES, coneMesh.indexCount, gl.UNSIGNED_SHORT, 0, cones.count);
    }
    // then what is see-through, tested against depth but not writing it
    gl.depthMask(false);
    gl.disable(gl.CULL_FACE);
    if (lines.count > 0) {
      gl.useProgram(lineProg);
      gl.uniformMatrix4fv(u(lineProg, "uView"), false, s.view);
      gl.uniformMatrix4fv(u(lineProg, "uProj"), false, s.proj);
      gl.uniform2f(u(lineProg, "uViewport"), s.width, s.height);
      gl.uniform3fv(u(lineProg, "uEye"), s.eye);
      gl.uniform3fv(u(lineProg, "uFog"), s.fog);
      gl.uniform2f(u(lineProg, "uFogRange"), s.fogNear, s.fogFar);
      lines.upload(gl);
      gl.bindVertexArray(lineVao);
      gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, lines.count);
    }
    if (halos.count > 0) {
      gl.enable(gl.CULL_FACE);
      gl.useProgram(sphereProg);
      gl.uniformMatrix4fv(u(sphereProg, "uViewProj"), false, viewProj);
      gl.uniform3fv(u(sphereProg, "uEye"), s.eye);
      gl.uniform3fv(u(sphereProg, "uLight"), lightDir);
      gl.uniform3fv(u(sphereProg, "uFog"), s.fog);
      gl.uniform2f(u(sphereProg, "uFogRange"), s.fogNear, s.fogFar);
      halos.upload(gl);
      gl.bindVertexArray(haloVao);
      gl.drawElementsInstanced(gl.TRIANGLES, sphereMesh.indexCount, gl.UNSIGNED_SHORT, 0, halos.count);
    }
    gl.bindVertexArray(null);
    gl.depthMask(true);
  }

  return {
    gl,
    resize(width, height) {
      if (canvas.width !== width || canvas.height !== height) {
        canvas.width = width;
        canvas.height = height;
      }
    },
    begin(s) {
      setup = s;
      viewProj = mul(s.proj, s.view);
      spheres.reset();
      halos.reset();
      boxes.reset();
      lines.reset();
      cones.reset();
    },
    sphere(c, r, color, alpha, rim = [1, 1, 1], rimStrength = 0) {
      spheres.push(c[0], c[1], c[2], r, color[0], color[1], color[2], alpha, rim[0], rim[1], rim[2], rimStrength);
    },
    box(c, size, color, alpha, rim = [1, 1, 1], rimStrength = 0) {
      const sx = typeof size === "number" ? size : size[0];
      const sy = typeof size === "number" ? size : size[1];
      const sz = typeof size === "number" ? size : size[2];
      boxes.push(c[0], c[1], c[2], 0, color[0], color[1], color[2], alpha, rim[0], rim[1], rim[2], rimStrength, sx, sy, sz, 0);
    },
    halo(c, r, color, alpha) {
      // a rim-only shell: the body colour is the halo colour at low alpha, the silhouette glows
      halos.push(c[0], c[1], c[2], r, color[0], color[1], color[2], alpha, color[0], color[1], color[2], 0.9);
    },
    line(a, b, color, widthPx, dash, alpha) {
      lines.push(a[0], a[1], a[2], widthPx, b[0], b[1], b[2], dash, color[0], color[1], color[2], alpha);
    },
    cone(apex, dir, size, color, alpha) {
      cones.push(apex[0], apex[1], apex[2], size, dir[0], dir[1], dir[2], 0, color[0], color[1], color[2], alpha);
    },
    end() {
      draw();
    },
    destroy() {
      // the resources go, the context stays: the same canvas may be drawn on again by the next
      // renderer (a hot reload re-mounts on the element it has), and a lost context is never given back
      for (const p of [sphereProg, boxProg, lineProg, coneProg]) gl.deleteProgram(p);
      for (const v of [sphereVao, haloVao, boxVao, coneVao, lineVao]) gl.deleteVertexArray(v);
      for (const m of [sphereMesh, boxMesh, coneMesh]) {
        gl.deleteBuffer(m.vertices);
        gl.deleteBuffer(m.indices);
      }
      gl.deleteBuffer(quad);
      boxes.release(gl);
      for (const st of [spheres, halos, lines, cones]) st.release(gl);
      setup = null;
    },
  };
}

// ---- streams of instances ----

class Stream {
  data: Float32Array;
  count = 0;
  private glBuffer: WebGLBuffer | null = null;
  private capacity: number;
  constructor(readonly stride: number) {
    this.capacity = 256;
    this.data = new Float32Array(this.capacity * stride);
  }
  reset() {
    this.count = 0;
  }
  push(...values: number[]) {
    if (this.count >= this.capacity) {
      this.capacity *= 2;
      const next = new Float32Array(this.capacity * this.stride);
      next.set(this.data);
      this.data = next;
    }
    this.data.set(values, this.count * this.stride);
    this.count++;
  }
  buffer(gl: WebGL2RenderingContext) {
    if (!this.glBuffer) this.glBuffer = gl.createBuffer();
    return this.glBuffer!;
  }
  upload(gl: WebGL2RenderingContext) {
    gl.bindBuffer(gl.ARRAY_BUFFER, this.buffer(gl));
    gl.bufferData(gl.ARRAY_BUFFER, this.data.subarray(0, this.count * this.stride), gl.DYNAMIC_DRAW);
  }
  release(gl: WebGL2RenderingContext) {
    if (this.glBuffer) gl.deleteBuffer(this.glBuffer);
    this.glBuffer = null;
  }
}

// ---- meshes ----

interface Mesh {
  vertices: WebGLBuffer; // position xyz + normal xyz
  indices: WebGLBuffer;
  indexCount: number;
}

function buildSphere(gl: WebGL2RenderingContext, segments: number, rings: number): Mesh {
  const verts: number[] = [];
  const idx: number[] = [];
  for (let y = 0; y <= rings; y++) {
    const v = y / rings;
    const phi = v * Math.PI;
    for (let x = 0; x <= segments; x++) {
      const theta = (x / segments) * Math.PI * 2;
      const nx = Math.sin(phi) * Math.cos(theta);
      const ny = Math.cos(phi);
      const nz = Math.sin(phi) * Math.sin(theta);
      verts.push(nx, ny, nz, nx, ny, nz);
    }
  }
  for (let y = 0; y < rings; y++) {
    for (let x = 0; x < segments; x++) {
      const a = y * (segments + 1) + x;
      const b = a + segments + 1;
      idx.push(a, b, a + 1, b, b + 1, a + 1);
    }
  }
  return mesh(gl, verts, idx);
}

/**
 * A unit cube about the origin with its edges taken off.
 *
 * Six faces inset by the bevel, twelve narrow quads along the edges and eight triangles at the
 * corners, each with its own flat normal. A true cube meets the light along a hard line and its
 * edges read as a black crease or vanish entirely against a neighbour; a chamfer catches the light
 * along every edge instead, which is what makes a stack of them legible as separate solids. The
 * chamfer is a fraction of the mesh, so it scales with whatever the instance is scaled by.
 */
function buildBox(gl: WebGL2RenderingContext, bevel = 0.022): Mesh {
  const verts: number[] = [];
  const idx: number[] = [];
  const h = 0.5;
  const c = h - bevel * h; // how far the flat of a face reaches before the chamfer starts
  const axis = (i: number, v: number): [number, number, number] => (i === 0 ? [v, 0, 0] : i === 1 ? [0, v, 0] : [0, 0, v]);
  const add = (a: number[], b: number[]) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];

  /**
   * One flat polygon. The winding is not derived case by case: the polygon's own normal is compared
   * with the direction it ought to face and the order is reversed when they disagree, which is far
   * harder to get wrong than twelve hand-worked edge cases.
   */
  const face = (points: number[][], outward: number[]) => {
    const e1 = [points[1][0] - points[0][0], points[1][1] - points[0][1], points[1][2] - points[0][2]];
    const e2 = [points[2][0] - points[0][0], points[2][1] - points[0][1], points[2][2] - points[0][2]];
    const cx = e1[1] * e2[2] - e1[2] * e2[1];
    const cy = e1[2] * e2[0] - e1[0] * e2[2];
    const cz = e1[0] * e2[1] - e1[1] * e2[0];
    const p = cx * outward[0] + cy * outward[1] + cz * outward[2] < 0 ? [...points].reverse() : points;
    const n = normalize3(outward);
    const base = verts.length / 6;
    for (const q of p) verts.push(q[0], q[1], q[2], n[0], n[1], n[2]);
    for (let i = 2; i < p.length; i++) idx.push(base, base + i - 1, base + i);
  };

  for (let a = 0; a < 3; a++) {
    const b = (a + 1) % 3;
    const d = (a + 2) % 3;
    for (const sa of [1, -1]) {
      // the flat of the face, inset all round by the chamfer
      face(
        [
          add(add(axis(a, sa * h), axis(b, -c)), axis(d, -c)),
          add(add(axis(a, sa * h), axis(b, c)), axis(d, -c)),
          add(add(axis(a, sa * h), axis(b, c)), axis(d, c)),
          add(add(axis(a, sa * h), axis(b, -c)), axis(d, c)),
        ],
        axis(a, sa),
      );
      // the chamfer along each of its edges, shared with the neighbouring face
      for (const sb of [1, -1]) {
        face(
          [
            add(add(axis(a, sa * h), axis(b, sb * c)), axis(d, -c)),
            add(add(axis(a, sa * c), axis(b, sb * h)), axis(d, -c)),
            add(add(axis(a, sa * c), axis(b, sb * h)), axis(d, c)),
            add(add(axis(a, sa * h), axis(b, sb * c)), axis(d, c)),
          ],
          add(axis(a, sa), axis(b, sb)),
        );
      }
    }
  }
  // and the eight corners, where three chamfers meet
  for (const sx of [1, -1]) {
    for (const sy of [1, -1]) {
      for (const sz of [1, -1]) {
        face(
          [
            [sx * h, sy * c, sz * c],
            [sx * c, sy * h, sz * c],
            [sx * c, sy * c, sz * h],
          ],
          [sx, sy, sz],
        );
      }
    }
  }
  return mesh(gl, verts, idx);
}

/** Tip at the origin, base ring at z = -1, base radius 0.42: a slim arrowhead. */
function buildCone(gl: WebGL2RenderingContext, segments: number): Mesh {
  const verts: number[] = [];
  const idx: number[] = [];
  const r = 0.42;
  // the side: the tip is repeated per segment so each face keeps its own normal
  for (let i = 0; i < segments; i++) {
    const t0 = (i / segments) * Math.PI * 2;
    const t1 = ((i + 1) / segments) * Math.PI * 2;
    const p0 = [Math.cos(t0) * r, Math.sin(t0) * r, -1];
    const p1 = [Math.cos(t1) * r, Math.sin(t1) * r, -1];
    const tm = (t0 + t1) / 2;
    const n = normalize3([Math.cos(tm), Math.sin(tm), r]);
    const base = verts.length / 6;
    verts.push(0, 0, 0, ...n, ...p0, ...n, ...p1, ...n);
    idx.push(base, base + 1, base + 2);
  }
  // the base cap
  const capCenter = verts.length / 6;
  verts.push(0, 0, -1, 0, 0, -1);
  for (let i = 0; i < segments; i++) {
    const t = (i / segments) * Math.PI * 2;
    verts.push(Math.cos(t) * r, Math.sin(t) * r, -1, 0, 0, -1);
  }
  for (let i = 0; i < segments; i++) idx.push(capCenter, capCenter + 1 + ((i + 1) % segments), capCenter + 1 + i);
  return mesh(gl, verts, idx);
}

function mesh(gl: WebGL2RenderingContext, verts: number[], idx: number[]): Mesh {
  const vertices = gl.createBuffer()!;
  gl.bindBuffer(gl.ARRAY_BUFFER, vertices);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(verts), gl.STATIC_DRAW);
  const indices = gl.createBuffer()!;
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, indices);
  gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new Uint16Array(idx), gl.STATIC_DRAW);
  return { vertices, indices, indexCount: idx.length };
}

function instancedVao(gl: WebGL2RenderingContext, prog: WebGLProgram, m: Mesh, stream: Stream, attrs: [string, number][]) {
  const vao = gl.createVertexArray()!;
  gl.bindVertexArray(vao);
  gl.bindBuffer(gl.ARRAY_BUFFER, m.vertices);
  const pos = gl.getAttribLocation(prog, "aPos");
  gl.enableVertexAttribArray(pos);
  gl.vertexAttribPointer(pos, 3, gl.FLOAT, false, 24, 0);
  const nrm = gl.getAttribLocation(prog, "aNormal");
  gl.enableVertexAttribArray(nrm);
  gl.vertexAttribPointer(nrm, 3, gl.FLOAT, false, 24, 12);
  gl.bindBuffer(gl.ARRAY_BUFFER, stream.buffer(gl));
  for (const [name, offset] of attrs) {
    const loc = gl.getAttribLocation(prog, name);
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 4, gl.FLOAT, false, stream.stride * 4, offset * 4);
    gl.vertexAttribDivisor(loc, 1);
  }
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, m.indices);
  gl.bindVertexArray(null);
  return vao;
}

// ---- shaders ----

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

const fogSnippet = `
  float fogAmount(vec3 world) {
    float d = distance(world, uEye);
    return smoothstep(uFogRange.x, uFogRange.y, d);
  }`;

const sphereVert = `#version 300 es
  in vec3 aPos; in vec3 aNormal;
  in vec4 aInst0; in vec4 aInst1; in vec4 aInst2;
  uniform mat4 uViewProj;
  out vec3 vN; out vec3 vW; out vec4 vColor; out vec4 vRim;
  void main() {
    vec3 w = aInst0.xyz + aPos * aInst0.w;
    gl_Position = uViewProj * vec4(w, 1.0);
    vN = aNormal; vW = w; vColor = aInst1; vRim = aInst2;
  }`;

const boxVert = `#version 300 es
  in vec3 aPos; in vec3 aNormal;
  in vec4 aInst0; in vec4 aInst1; in vec4 aInst2; in vec4 aInst3;
  uniform mat4 uViewProj;
  out vec3 vN; out vec3 vW; out vec4 vColor; out vec4 vRim;
  void main() {
    vec3 w = aInst0.xyz + aPos * aInst3.xyz;
    gl_Position = uViewProj * vec4(w, 1.0);
    // the mesh is axis aligned and only ever scaled, so its normals need no fixing up
    vN = aNormal; vW = w; vColor = aInst1; vRim = aInst2;
  }`;

const sphereFrag = `#version 300 es
  precision highp float;
  in vec3 vN; in vec3 vW; in vec4 vColor; in vec4 vRim;
  uniform vec3 uEye; uniform vec3 uLight; uniform vec3 uFog; uniform vec2 uFogRange;
  out vec4 outColor;
  ${fogSnippet}
  void main() {
    vec3 n = normalize(vN);
    vec3 v = normalize(uEye - vW);
    float diff = max(dot(n, uLight), 0.0);
    float head = max(dot(n, v), 0.0);
    vec3 h = normalize(uLight + v);
    float spec = pow(max(dot(n, h), 0.0), 40.0) * 0.35;
    vec3 body = vColor.rgb * (0.42 + 0.46 * diff + 0.22 * head);
    vec3 c = body + spec;
    float rim = pow(1.0 - head, 2.2) * vRim.a;
    c = mix(c, vRim.rgb, rim);
    float a = vColor.a + rim * 0.6 * vRim.a;
    c = mix(c, uFog, fogAmount(vW) * 0.85);
    outColor = vec4(c, clamp(a, 0.0, 1.0));
  }`;

const coneVert = `#version 300 es
  in vec3 aPos; in vec3 aNormal;
  in vec4 aInst0; in vec4 aInst1; in vec4 aInst2;
  uniform mat4 uViewProj;
  out vec3 vN; out vec3 vW; out vec4 vColor;
  void main() {
    vec3 w = normalize(aInst1.xyz);
    vec3 helper = abs(w.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 u = normalize(cross(w, helper));
    vec3 v = cross(w, u);
    mat3 basis = mat3(u, v, w);
    vec3 pos = aInst0.xyz + basis * (aPos * aInst0.w);
    gl_Position = uViewProj * vec4(pos, 1.0);
    vN = basis * aNormal; vW = pos; vColor = aInst2;
  }`;

const coneFrag = `#version 300 es
  precision highp float;
  in vec3 vN; in vec3 vW; in vec4 vColor;
  uniform vec3 uEye; uniform vec3 uLight; uniform vec3 uFog; uniform vec2 uFogRange;
  out vec4 outColor;
  ${fogSnippet}
  void main() {
    vec3 n = normalize(vN);
    float diff = max(dot(n, uLight), 0.0);
    vec3 v = normalize(uEye - vW);
    float head = max(dot(n, v), 0.0);
    vec3 c = vColor.rgb * (0.5 + 0.35 * diff + 0.2 * head);
    c = mix(c, uFog, fogAmount(vW) * 0.85);
    outColor = vec4(c, vColor.a);
  }`;

// The ribbon: both ends go to view space, the segment is clipped against the near plane, both ends are
// projected, and each corner is pushed sideways in screen pixels. Varyings interpolate with the true w
// of each end, so the distance along the line, which sets the dashes, comes out right in perspective.
const lineVert = `#version 300 es
  in vec2 aCorner;
  in vec4 aA; in vec4 aB; in vec4 aColor;
  uniform mat4 uView; uniform mat4 uProj; uniform vec2 uViewport;
  out float vS; out float vSide; out vec4 vColor; out float vDash; out vec3 vW; out float vWidth;
  void main() {
    vec4 a = uView * vec4(aA.xyz, 1.0);
    vec4 b = uView * vec4(aB.xyz, 1.0);
    float len = distance(aA.xyz, aB.xyz);
    float ta = 0.0, tb = 1.0;
    float nz = -1.05;
    if (a.z > nz && b.z > nz) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); vS = 0.0; vSide = 0.0; vColor = vec4(0.0); vDash = 0.0; vW = aA.xyz; vWidth = 1.0; return; }
    if (a.z > nz) { float t = (nz - a.z) / (b.z - a.z); a = mix(a, b, t); ta = t; }
    else if (b.z > nz) { float t = (nz - a.z) / (b.z - a.z); b = mix(a, b, t); tb = t; }
    vec4 ca = uProj * a;
    vec4 cb = uProj * b;
    vec2 halfVp = uViewport * 0.5;
    vec2 sa = ca.xy / ca.w * halfVp;
    vec2 sb = cb.xy / cb.w * halfVp;
    vec2 d = sb - sa;
    float dl = length(d);
    vec2 dir = dl > 1e-4 ? d / dl : vec2(1.0, 0.0);
    vec2 nrm = vec2(-dir.y, dir.x);
    // one extra pixel each side gives the edge something to fade over
    float w = aA.w + 1.5;
    vec4 c = mix(ca, cb, aCorner.x);
    vec2 s = mix(sa, sb, aCorner.x) + nrm * aCorner.y * w * 0.5;
    gl_Position = vec4(s / halfVp * c.w, c.z, c.w);
    float t = mix(ta, tb, aCorner.x);
    vS = t * len;
    vSide = aCorner.y;
    vColor = aColor;
    vDash = aB.w;
    vW = mix(aA.xyz, aB.xyz, t);
    vWidth = w;
  }`;

const lineFrag = `#version 300 es
  precision highp float;
  in float vS; in float vSide; in vec4 vColor; in float vDash; in vec3 vW; in float vWidth;
  uniform vec3 uEye; uniform vec3 uFog; uniform vec2 uFogRange;
  out vec4 outColor;
  ${fogSnippet}
  void main() {
    if (vDash > 0.5 && vDash < 1.5 && mod(vS, 10.0) > 6.0) discard;
    if (vDash > 1.5 && mod(vS, 5.0) > 2.2) discard;
    // soft edges: the last 1.5 px of the half width fade out
    float edge = 1.5 / (vWidth * 0.5);
    float a = vColor.a * (1.0 - smoothstep(1.0 - edge, 1.0, abs(vSide)));
    vec3 c = mix(vColor.rgb, uFog, fogAmount(vW) * 0.85);
    outColor = vec4(c, a);
  }`;

// ---- small helpers ----

function normalize3(v: number[]): Vec3 {
  const l = Math.hypot(v[0], v[1], v[2]) || 1;
  return [v[0] / l, v[1] / l, v[2] / l];
}

function mul(a: Mat4, b: Mat4): Float32Array {
  const out = new Float32Array(16);
  for (let c = 0; c < 4; c++) {
    for (let r = 0; r < 4; r++) {
      out[c * 4 + r] = a[r] * b[c * 4] + a[4 + r] * b[c * 4 + 1] + a[8 + r] * b[c * 4 + 2] + a[12 + r] * b[c * 4 + 3];
    }
  }
  return out;
}
