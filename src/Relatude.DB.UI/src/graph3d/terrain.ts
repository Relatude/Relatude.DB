/**
 * The mountains, and the ground the aeroplane can hit.
 *
 * The height field is the one from db.relatude.com: a ridged multifractal - octaves of value noise,
 * each folded about its middle to make a crease, each octave weighted by the one above it so ridges
 * gather into massifs - under a broad mask that decides whether a stretch of the range comes out as
 * sparse spires over open ground or as a solid wall. The front page stops at five octaves; here
 * there is a sixth, and the mesh is finer than the page's, so a ridge carries crumble down its
 * flanks rather than being one clean facet from the snow to the valley.
 *
 * Everything in this file is in terrain units. The scene draws it scaled up (see terrainScale in
 * funMode) so the ranges tower over the graph rather than sitting under it as hills.
 *
 * The whole field is rebuilt on the cpu whenever the patch moves, which is every couple of seconds
 * of flying, so the cost per vertex is the thing that decides how big and how detailed the country
 * can be. Two things pay for the extra octave and the finer mesh: every octave's period is a power
 * of two, so wrapping the lattice is a bitwise AND rather than two modulos, and the octaves live in
 * flat typed arrays walked by index rather than as objects walked by iterator. Together they run
 * about three and a half times faster than the straightforward version, which is where the room for
 * a bigger landscape came from. KEEP THE PERIODS POWERS OF TWO: 1280 * f must be one, or the mask
 * below silently wraps the noise at the wrong place.
 */

const CELL = 2.6; // terrain units per mesh quad - the facet size
const PERIOD = 1280; // the noise repeats over this, so a mesh can be moved without a seam

let seed = 0;

/** A fresh range. The hash is seeded rather than the coordinates, so the lattice stays periodic. */
export function reseedTerrain(s?: number) {
  seed = s === undefined ? (Math.random() * 0xffffffff) >>> 0 : s >>> 0;
}
reseedTerrain(0x51a7c3);

function h2(x: number, y: number) {
  let h = Math.imul(x, 374761393) + Math.imul(y, 668265263) + seed;
  h = Math.imul(h ^ (h >>> 13), 1274126177);
  return ((h ^ (h >>> 16)) >>> 0) / 4294967296;
}

/**
 * Value noise on an integer lattice. lin = 1 is linear and creased, lin = 0 is smooth and rounded.
 * `mask` is the lattice period minus one; see the note above on why it is an AND.
 */
function vnoise(x: number, z: number, f: number, lin: number, mask: number) {
  const px = x * f;
  const pz = z * f;
  const xi = Math.floor(px);
  const zi = Math.floor(pz);
  const fx = px - xi;
  const fz = pz - zi;
  const ux = fx + (fx * fx * (3 - 2 * fx) - fx) * (1 - lin);
  const uz = fz + (fz * fz * (3 - 2 * fz) - fz) * (1 - lin);
  // AND wraps negatives the way the modulo did: in two's complement -1 & 7 is 7
  const x0 = xi & mask;
  const x1 = (xi + 1) & mask;
  const z0 = zi & mask;
  const z1 = (zi + 1) & mask;
  const a = h2(x0, z0);
  const b = h2(x1, z0);
  const c = h2(x0, z1);
  const d = h2(x1, z1);
  const t = a + (b - a) * ux;
  const u = c + (d - c) * ux;
  return t + (u - t) * uz;
}

// The octaves, flat: frequency, amplitude, how creased, and the lattice mask (1280 * f - 1). The
// last one is at the mesh's own resolution - a wavelength of two quads - and nothing finer is worth
// having: the facets could not carry it, and the ground the aeroplane collides with would stop
// agreeing with the ground it can see.
const OCT_F = new Float64Array([1 / 160, 1 / 80, 1 / 40, 1 / 20, 1 / 10, 1 / 5]);
const OCT_A = new Float64Array([1.0, 0.52, 0.27, 0.14, 0.07, 0.035]);
const OCT_LIN = new Float64Array([1.0, 1.0, 0.88, 0.66, 0.45, 0.3]);
const OCT_MASK = new Int32Array(OCT_F.length);
for (let i = 0; i < OCT_F.length; i++) OCT_MASK[i] = PERIOD * OCT_F[i] - 1;
let OCT_NORM = 0;
for (let i = 0; i < OCT_A.length; i++) OCT_NORM += OCT_A[i];
/** the broad mask's own lattice: 1280 / 320 */
const MASK_PERIOD = 3;

/** The height of the ground at a point, in terrain units. */
export function heightAt(x: number, z: number): number {
  let sum = 0;
  let prev = 1;
  for (let i = 0; i < OCT_F.length; i++) {
    const n = 1 - Math.abs(vnoise(x, z, OCT_F[i], OCT_LIN[i], OCT_MASK[i]) * 2 - 1); // ridge
    sum += OCT_A[i] * n * (0.55 + 0.45 * prev); // multifractal
    prev = n;
  }
  // the broad mask varies the character of the range rather than its presence
  const raw = vnoise(x, z, 1 / 320, 0.25, MASK_PERIOD);
  const t = Math.min(1, Math.max(0, (raw - 0.24) / 0.58));
  const m = t * t * (3 - 2 * t);
  return Math.pow(sum / OCT_NORM, 2.8 - 1.65 * m) * 130 - 12;
}

/** The ground and its normal at a point; the normal comes from a small central difference. */
export function groundAt(x: number, z: number): { y: number; nx: number; ny: number; nz: number } {
  const e = CELL * 0.5;
  const y = heightAt(x, z);
  const hx = heightAt(x + e, z) - heightAt(x - e, z);
  const hz = heightAt(x, z + e) - heightAt(x, z - e);
  // n = normalize(-dh/dx, 1, -dh/dz) with the differences taken over 2e
  let nx = -hx / (2 * e);
  let nz = -hz / (2 * e);
  const l = Math.hypot(nx, 1, nz);
  nx /= l;
  nz /= l;
  return { y, nx, ny: 1 / l, nz };
}

/** The highest ground within a small ring, so a flight line can clear the ridge it is crossing. */
export function clearanceAt(x: number, z: number, r = 14): number {
  let m = -1e9;
  for (const [dx, dz] of [
    [0, 0],
    [r, 0],
    [-r, 0],
    [0, r],
    [0, -r],
    [r * 0.7, r * 0.7],
    [-r * 0.7, r * 0.7],
    [r * 0.7, -r * 0.7],
    [-r * 0.7, -r * 0.7],
  ]) {
    m = Math.max(m, heightAt(x + dx, z + dz));
  }
  return m;
}

export interface TerrainMesh {
  /** vertex positions, xyz per vertex, in terrain units */
  positions: Float32Array;
  indices: Uint32Array;
  /** how many quads across and along */
  nx: number;
  nz: number;
  cell: number;
  /** the middle of the patch, snapped to whole cells */
  originX: number;
  originZ: number;
}

/**
 * A square patch of ground centred on a point. The mesh has no normals: the shader takes each
 * facet's normal from the screen-space derivative of the world position, which is exact for a flat
 * triangle and is what gives the range its faceted look with no split vertices.
 */
export function buildTerrainMesh(nx: number, nz: number, centreX: number, centreZ: number): TerrainMesh {
  const vx = nx + 1;
  const vz = nz + 1;
  const originX = snap(centreX);
  const originZ = snap(centreZ);
  const positions = new Float32Array(vx * vz * 3);
  fillPositions(positions, nx, nz, originX, originZ);
  const indices = new Uint32Array(nx * nz * 6);
  for (let j = 0, k = 0; j < nz; j++) {
    for (let i = 0; i < nx; i++) {
      const a = j * vx + i;
      const b = a + 1;
      const c = a + vx;
      const d = c + 1;
      // alternating diagonals, so the facets read as a range rather than as a weave
      if ((i + j) & 1) {
        indices[k++] = a;
        indices[k++] = c;
        indices[k++] = b;
        indices[k++] = b;
        indices[k++] = c;
        indices[k++] = d;
      } else {
        indices[k++] = a;
        indices[k++] = c;
        indices[k++] = d;
        indices[k++] = a;
        indices[k++] = d;
        indices[k++] = b;
      }
    }
  }
  return { positions, indices, nx, nz, cell: CELL, originX, originZ };
}

/**
 * The patch is only ever moved by an EVEN number of cells.
 *
 * Which way a quad is split into triangles alternates with `(i + j) & 1`, and that parity comes from
 * the vertex index rather than from where the ground actually is. Move the patch by one cell and
 * every quad in the range flips its diagonal at once - the same hillside, but every facet's normal
 * changed, which as the aeroplane flies along reads as the whole landscape flickering. Snapping to
 * two cells keeps the pattern nailed to the world, and the seam never moves relative to the rock.
 */
const snap = (v: number) => Math.round(v / (CELL * 2)) * CELL * 2;

/** Moves a patch to a new middle, rewriting its heights in place. */
export function recentreTerrainMesh(mesh: TerrainMesh, centreX: number, centreZ: number): boolean {
  const originX = snap(centreX);
  const originZ = snap(centreZ);
  if (originX === mesh.originX && originZ === mesh.originZ) return false;
  mesh.originX = originX;
  mesh.originZ = originZ;
  fillPositions(mesh.positions, mesh.nx, mesh.nz, originX, originZ);
  return true;
}

function fillPositions(out: Float32Array, nx: number, nz: number, originX: number, originZ: number) {
  const vx = nx + 1;
  const vz = nz + 1;
  for (let j = 0, p = 0; j < vz; j++) {
    const z = originZ + (j - nz / 2) * CELL;
    for (let i = 0; i < vx; i++, p += 3) {
      const x = originX + (i - nx / 2) * CELL;
      out[p] = x;
      out[p + 1] = heightAt(x, z);
      out[p + 2] = z;
    }
  }
}

export const terrainCell = CELL;
