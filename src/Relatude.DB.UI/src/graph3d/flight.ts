/**
 * The aeroplane: the flight model from the glider, with an engine bolted to the nose.
 *
 * Nothing here is a flight-path animation. Forces and moments are worked out from the airflow over
 * the wing each step and integrated, so the aeroplane does what the numbers say - it stalls when the
 * wing runs out of angle, drops the low wing and autorotates when it does, gets heavy on the elevator
 * at speed, and floats in ground effect. The coefficients are the glider's, unchanged; what is new is
 * a propeller that pulls hardest when slow and loses its bite as the airspeed comes up, which is what
 * gives the aeroplane a top speed rather than a fuel gauge.
 *
 * Units are metres, seconds, radians and newtons. Body axes are x right, y up, z forward.
 */

export type V3 = [number, number, number];
export type Quat = [number, number, number, number];

const RHO = 1.225; // kg/m^3
const G = 9.81;

export const AC = {
  m: 340, // kg, airframe and pilot
  S: 10.5,
  b: 15.0,
  c: 0.7, // wing area, span, mean chord
  // Long thin wings make roll the cheap axis and yaw the expensive one, which is why it rolls
  // willingly and why the nose keeps swinging after the rudder is centred.
  Ix: 1900, // pitch, about the span axis
  Iy: 3000, // yaw, about the vertical
  Iz: 1500, // roll, about the fuselage

  CL0: 0.15,
  CLa: 5.2, // lift curve slope, per radian
  aStall: 0.26,
  aFade: 0.13, // where the wing lets go, and how abruptly
  CD0: 0.0188,
  K: 0.026, // parasite drag, and induced drag factor
  CYb: 0.95, // side force from sideslip

  Cm0: 0.14,
  Cma: -1.3, // pitch trim and static stability
  Cmq: -26,
  Cmde: 0.3, // pitch damping, elevator power
  Clb: -0.1,
  Clp: -0.6,
  Clr: 0.09,
  Clda: 0.19,
  spinP: 1.15, // how far roll damping reverses in a stall
  dropBank: 0.038, // the low wing lets go first
  dropSeed: 0.01, // and one of them goes even from level
  Cnb: 0.075,
  Cnr: -0.2,
  Cndr: 0.085,
  Cnda: -0.03,

  RUD_MAX: 0.55,

  // The engine. 1600 N standing still is about half the weight, so it climbs willingly, and the
  // bite term is the propeller running out of air to push as the speed comes up - which is what
  // settles the top speed at about 200 km/h, comfortably short of the speed that folds the wings.
  thrust: 1600,
  propBite: 78, // m/s at which the propeller has nothing left
  vA: 45, // manoeuvring speed, m/s
  gBreak: 9.0,
  gBreakNeg: -6.0,
  vne: 69, // never-exceed; past this for long enough it comes apart
};

// Best glide and the trim point fall out of the numbers above rather than being typed in.
const CL_BEST = Math.sqrt(AC.CD0 / AC.K);
export const V_BEST = Math.sqrt((2 * AC.m * G) / (RHO * AC.S * CL_BEST));
const CL_MAX = AC.CL0 + AC.CLa * AC.aStall;
export const V_STALL = Math.sqrt((2 * AC.m * G) / (RHO * AC.S * CL_MAX));
const A_TRIM = AC.Cm0 / -AC.Cma;
const CL_TRIM = AC.CL0 + AC.CLa * A_TRIM;
export const V_TRIM = Math.sqrt((2 * AC.m * G) / (RHO * AC.S * CL_TRIM));

export interface Controls {
  ail: number;
  elev: number;
  rud: number;
  throttle: number;
}

export interface Aircraft {
  p: V3; // position, metres
  v: V3; // velocity, metres/second
  q: Quat; // attitude
  w: V3; // angular velocity, body axes, rad/s
  alpha: number;
  beta: number;
  V: number;
  CL: number;
  LD: number;
  stall: number;
  gz: number;
  agl: number;
  onGround: boolean;
  touching: boolean;
  overG: number;
  crashed: boolean;
  dropSide: number;
  /** body axes in world space, refreshed every step */
  right: V3;
  up: V3;
  fwd: V3;
}

/** The ground under a point, in metres, with its normal. */
export type GroundSampler = (x: number, z: number) => { y: number; nx: number; ny: number; nz: number };

export function newAircraft(): Aircraft {
  return {
    p: [0, 0, 0],
    v: [0, 0, 0],
    q: [0, 0, 0, 1],
    w: [0, 0, 0],
    alpha: 0,
    beta: 0,
    V: 0,
    CL: 0,
    LD: 0,
    stall: 0,
    gz: 1,
    agl: 0,
    onGround: false,
    touching: false,
    overG: 0,
    crashed: false,
    dropSide: Math.random() < 0.5 ? 1 : -1,
    right: [1, 0, 0],
    up: [0, 1, 0],
    fwd: [0, 0, 1],
  };
}

export function newControls(): Controls {
  return { ail: 0, elev: 0, rud: 0, throttle: 0.65 };
}

/** Sets the aeroplane flying straight and level on a heading, at its trim speed. */
export function placeAircraft(ac: Aircraft, p: V3, heading: number, speed = V_TRIM) {
  ac.p = [...p];
  qEuler(ac.q, heading, 0, 0);
  qRot(ac.fwd, ac.q, EZ);
  ac.v = [ac.fwd[0] * speed, ac.fwd[1] * speed, ac.fwd[2] * speed];
  ac.w = [0, 0, 0];
  ac.overG = 0;
  ac.crashed = false;
  ac.dropSide = Math.random() < 0.5 ? 1 : -1;
}

// ---- contact points, body frame, metres ----
// `fatal` is the speed above which arriving on that point ends the flight. The wheel and the tail
// skid can take anything; a wingtip or the nose arriving fast is a crash.
const CONTACT: { p: V3; gear: number; fatal: number }[] = [
  { p: [0.0, -0.74, 0.25], gear: 1, fatal: 1e9 }, // the wheel
  { p: [0.0, -0.24, -3.85], gear: 1, fatal: 1e9 }, // tail skid
  { p: [0.0, -0.12, 3.34], gear: 0, fatal: 24 }, // nose
  { p: [-7.5, 0.6, -0.05], gear: 0, fatal: 24 }, // wingtips
  { p: [7.5, 0.6, -0.05], gear: 0, fatal: 24 },
];
const KS = 70000;
const CS = 9000; // undercarriage spring and damper

const EX: V3 = [1, 0, 0];
const EY: V3 = [0, 1, 0];
const EZ: V3 = [0, 0, 1];
const clamp = (v: number, a: number, b: number) => (v < a ? a : v > b ? b : v);
const smoothstep = (e0: number, e1: number, x: number) => {
  const t = clamp((x - e0) / (e1 - e0), 0, 1);
  return t * t * (3 - 2 * t);
};

const tmp: V3 = [0, 0, 0];
const tmp2: V3 = [0, 0, 0];
const iq: Quat = [0, 0, 0, 1];
const wind: V3 = [0, 0, 0];

/**
 * The gains of the hand on the stick: the only numbers in this file that are not aerodynamics.
 *
 * `beta` is zero on purpose. An earlier version had the hand hold the nose into the airflow with a
 * gain twelve times the airframe's own weathercock stiffness, and because a yaw moment is taken over
 * the span while a pitching one is taken over the chord, that came out twenty times heavier than it
 * looked and tumbled the aeroplane inside a second. Straightening the sideslip is the rudder's job
 * and the fin already does it; all that is added here is a little more damping.
 */
const ASSIST = { theta: -1.0, q: -12.0, bank: -0.3, p: -0.55, beta: 0, r: -0.25 };

/** A steady breeze, plus the lift where it meets a mountainside. */
export interface Air {
  speed: number;
  dirX: number;
  dirZ: number;
}

/** One step of the flight. Returns nothing; everything lands in `ac`. */
export function stepFlight(ac: Aircraft, ctl: Controls, dt: number, t: number, ground: GroundSampler, air: Air) {
  qRot(ac.right, ac.q, EX);
  qRot(ac.up, ac.q, EY);
  qRot(ac.fwd, ac.q, EZ);
  const bR = ac.right;
  const bU = ac.up;
  const bF = ac.fwd;

  const g = ground(ac.p[0], ac.p[2]);
  ac.agl = ac.p[1] - g.y;

  // the air: a gusting breeze, lifted where it runs up a slope and sinking over the back of it
  const gust = 1 + 0.16 * Math.sin(t * 0.37) + 0.1 * Math.sin(t * 1.13 + 2.0);
  wind[0] = air.speed * air.dirX * gust;
  wind[2] = air.speed * air.dirZ * gust;
  const ny = Math.max(g.ny, 0.15);
  const sx = -g.nx / ny;
  const sz = -g.nz / ny;
  wind[1] = clamp((wind[0] * sx + wind[2] * sz) * 0.5, -2.0, 3.5) * Math.exp(-Math.max(ac.agl, 0) / 150);

  let fx = 0;
  let fy = -G * AC.m; // weight, always
  let fz = 0;
  let Tx = 0;
  let Ty = 0;
  let Tz = 0;

  // ---- aerodynamics ----
  const vrx = ac.v[0] - wind[0];
  const vry = ac.v[1] - wind[1];
  const vrz = ac.v[2] - wind[2];
  const V = Math.hypot(vrx, vry, vrz);
  ac.V = V;

  if (V > 0.6) {
    const ivx = vrx / V;
    const ivy = vry / V;
    const ivz = vrz / V;
    const vbx = vrx * bR[0] + vry * bR[1] + vrz * bR[2];
    const vby = vrx * bU[0] + vry * bU[1] + vrz * bU[2];
    const vbz = vrx * bF[0] + vry * bF[1] + vrz * bF[2];

    const alpha = Math.atan2(-vby, vbz); // angle of attack
    const beta = Math.asin(clamp(vbx / V, -1, 1)); // sideslip
    ac.alpha = alpha;
    ac.beta = beta;

    // A lift curve with a real break in it: linear below the stalling angle, and past it the flow
    // separates and all that is left is the bluff-body term - far less lift, far more drag.
    const sep = smoothstep(AC.aStall, AC.aStall + AC.aFade, Math.abs(alpha));
    const CLlin = AC.CL0 + AC.CLa * alpha;
    const CLsep = 1.05 * Math.sin(2 * alpha);
    const CL = CLlin * (1 - sep) + CLsep * sep;
    ac.stall = sep;

    // Ground effect: within about a span of the ground the downwash is blocked and induced drag
    // falls away. This is the float at the end of a good approach.
    const ge = clamp(1 - ac.agl / (AC.b * 0.95), 0, 1);
    const Keff = AC.K * (1 - 0.4 * ge * ge);

    const CD = AC.CD0 + Keff * CL * CL + 1.35 * sep * Math.abs(Math.sin(alpha)) + 0.3 * beta * beta;

    const qbar = 0.5 * RHO * V * V;
    const L = qbar * AC.S * CL;
    const D = qbar * AC.S * CD;
    const Yf = qbar * AC.S * (-AC.CYb * beta);
    ac.CL = CL;
    ac.LD = CL / Math.max(CD, 1e-4);
    ac.gz = L / (AC.m * G);

    // lift acts perpendicular to the airflow, in the plane of symmetry: (v x up) x v
    const sx2 = ivy * bU[2] - ivz * bU[1];
    const sy2 = ivz * bU[0] - ivx * bU[2];
    const sz2 = ivx * bU[1] - ivy * bU[0];
    let lx = sy2 * ivz - sz2 * ivy;
    let ly = sz2 * ivx - sx2 * ivz;
    let lz = sx2 * ivy - sy2 * ivx;
    const ll = Math.hypot(lx, ly, lz);
    if (ll > 1e-5) {
      lx /= ll;
      ly /= ll;
      lz /= ll;
      fx += lx * L;
      fy += ly * L;
      fz += lz * L;
    }
    fx -= ivx * D;
    fy -= ivy * D;
    fz -= ivz * D;
    fx += bR[0] * Yf;
    fy += bR[1] * Yf;
    fz += bR[2] * Yf;

    // rates in the conventional senses, taken from the body-axis rates
    const pRoll = -ac.w[2];
    const qPitch = -ac.w[0];
    const rYaw = ac.w[1];
    const k2V = 1 / (2 * Math.max(V, 8));
    const qh = qPitch * AC.c * k2V;
    const ph = pRoll * AC.b * k2V;
    const rh = rYaw * AC.b * k2V;

    // Stick force goes as q times deflection, and there is only so hard a pilot can pull. Past the
    // manoeuvring speed the deflection that can actually be held falls off as 1/V^2, which is what
    // stops a fast dive plus a bootful of elevator from folding the wings.
    const auth = Math.min(1, (AC.vA / Math.max(V, 12)) * (AC.vA / Math.max(V, 12)));
    const elev = clamp(ctl.elev * auth, -1, 1);
    const Cm = AC.Cm0 + AC.Cma * alpha + AC.Cmq * qh + AC.Cmde * elev;
    let Cm2 = Cm;
    // Past the stall the roll damping changes sign: a wing that starts down meets the air at a
    // larger angle than the rising one, deeper into the stall, making less lift still. That is
    // autorotation, and it is why a stalled aeroplane departs rather than sinking straight ahead.
    const Clp = AC.Clp * (1 - AC.spinP * sep);
    const drop = sep * (AC.dropBank * -bR[1] + AC.dropSeed * ac.dropSide);
    const Cl = AC.Clb * beta + Clp * ph + AC.Clr * rh + drop + AC.Clda * ctl.ail * (1 - 0.55 * sep);
    let Cl2 = Cl;
    let Cn = AC.Cnb * beta + AC.Cnr * rh + AC.Cndr * ctl.rud + AC.Cnda * ctl.ail;

    // ---- what a hand on the stick would be doing ----
    // Left alone, a stable aeroplane does not fly straight: it trims to one speed and then hunts
    // about it in a phugoid that takes a minute to die, and the propeller's torque rolls it left
    // the whole time. A pilot corrects that without thinking, and this is that hand. It holds the
    // NOSE where it is - not the flight path: chasing the flight path with the elevator alone is
    // the classic way to fly onto the back of the power curve, pitching up to stop a descent until
    // the speed has gone and the wing lets go, which is exactly what it did when tried that way.
    // Holding the attitude has no such trap: the nose stays on the horizon and the throttle decides
    // whether that means climbing, cruising or coming down.
    //
    // Whenever the stick is centred the airframe's own trim bias is cancelled too, so "centred"
    // really means neutral rather than "wherever this aeroplane happens to trim". It all lets go
    // the moment the stick moves or the wing stalls, so a stall still drops a wing and departs.
    const settled = 1 - sep;
    const hands = (input: number) => settled * Math.max(0, 1 - Math.abs(input) * 3);
    const theta = Math.asin(clamp(bF[1], -1, 1)); // where the nose is pointing, against the horizon
    // Added to what the airframe is already doing, never replacing it. An earlier version cancelled
    // the trim terms so that "centred" would mean perfectly neutral - and cancelling Cma*alpha threw
    // away the aeroplane's pitch stiffness with it, leaving nothing to stop the angle of attack
    // running away: it tumbled within two seconds. So the static stability stays exactly as it is,
    // and this only adds heavy pitch damping to kill the phugoid and a light spring that keeps the
    // nose near the horizon. Both are restoring terms, so they can only ever settle it.
    Cm2 = Cm + hands(ctl.elev) * (ASSIST.theta * theta + ASSIST.q * qh);
    const bank = Math.atan2(-bR[1], Math.max(1e-4, bU[1]));
    // upside down there is no "level" worth returning to, so the wings are only picked up the
    // right way up; inverted the aeroplane stays where it is put
    const upright = Math.max(0, bU[1]);
    Cl2 = Cl + hands(ctl.ail) * upright * (ASSIST.bank * clamp(bank, -1.2, 1.2) + ASSIST.p * ph);
    Cn += hands(ctl.rud) * (ASSIST.beta * beta + ASSIST.r * rh);

    Tx += -(qbar * AC.S * AC.c * Cm2);
    Ty += qbar * AC.S * AC.b * Cn;
    Tz += -(qbar * AC.S * AC.b * Cl2);
  }

  // ---- the engine ----
  // A propeller is not a rocket: it pulls hardest standing still and loses its grip on the air as
  // the aeroplane catches up with the slipstream, which is the top speed all by itself.
  const bite = clamp(1 - V / AC.propBite, 0.06, 1);
  const T = AC.thrust * clamp(ctl.throttle, 0, 1) * bite;
  fx += bF[0] * T;
  fy += bF[1] * T;
  fz += bF[2] * T;
  // and it twists the airframe the other way - kept light, so it is felt on a hard climb rather
  // than being something to hold off all the way round the graph
  Tz += -0.045 * T;

  // ---- the ground ----
  let anyGear = false;
  let anyTouch = false;
  for (const c of CONTACT) {
    qRot(tmp, ac.q, c.p);
    const wx = ac.p[0] + tmp[0];
    const wy = ac.p[1] + tmp[1];
    const wz = ac.p[2] + tmp[2];
    const gp = ground(wx, wz);
    const pen = gp.y - wy;
    if (pen <= 0) continue;

    // velocity of this point = v + omega x r, omega taken to world axes
    qRot(tmp2, ac.q, ac.w);
    const vpx = ac.v[0] + tmp2[1] * tmp[2] - tmp2[2] * tmp[1];
    const vpy = ac.v[1] + tmp2[2] * tmp[0] - tmp2[0] * tmp[2];
    const vpz = ac.v[2] + tmp2[0] * tmp[1] - tmp2[1] * tmp[0];

    const nx = gp.nx;
    const ny2 = gp.ny;
    const nz = gp.nz;
    const vn = vpx * nx + vpy * ny2 + vpz * nz;
    const fn = Math.max(0, KS * Math.min(pen, 1.2) - CS * Math.min(vn, 0));

    anyTouch = true;
    if (!c.gear) {
      if (ac.V > c.fatal || pen > 1.1) ac.crashed = true;
    } else anyGear = true;

    // friction: rolling resistance along the track, a firm grip sideways so the wheel steers
    const tx = vpx - nx * vn;
    const ty = vpy - ny2 * vn;
    const tz = vpz - nz * vn;
    const bfn = bF[0] * nx + bF[1] * ny2 + bF[2] * nz;
    let dx = bF[0] - nx * bfn;
    let dy = bF[1] - ny2 * bfn;
    let dz = bF[2] - nz * bfn;
    const dl = Math.hypot(dx, dy, dz) || 1;
    dx /= dl;
    dy /= dl;
    dz /= dl;
    const vf = tx * dx + ty * dy + tz * dz;
    const lx2 = tx - dx * vf;
    const ly2 = ty - dy * vf;
    const lz2 = tz - dz * vf;
    const lvl = Math.hypot(lx2, ly2, lz2);
    const muR = c.gear ? 0.28 : 0.5;
    const ff = -muR * fn * Math.tanh(vf / 0.6);
    const fl = lvl > 1e-4 ? (-0.9 * fn * Math.tanh(lvl / 0.5)) / lvl : 0;

    const Fx = nx * fn + dx * ff + lx2 * fl;
    const Fy = ny2 * fn + dy * ff + ly2 * fl;
    const Fz = nz * fn + dz * ff + lz2 * fl;
    fx += Fx;
    fy += Fy;
    fz += Fz;

    // r x F, taken back into the body frame
    const cx = tmp[1] * Fz - tmp[2] * Fy;
    const cy = tmp[2] * Fx - tmp[0] * Fz;
    const cz = tmp[0] * Fy - tmp[1] * Fx;
    iq[0] = -ac.q[0];
    iq[1] = -ac.q[1];
    iq[2] = -ac.q[2];
    iq[3] = ac.q[3];
    qRot(tmp2, iq, [cx, cy, cz]);
    Tx += tmp2[0];
    Ty += tmp2[1];
    Tz += tmp2[2];
  }
  ac.onGround = anyGear;
  ac.touching = anyTouch;

  // ---- integrate ----
  ac.v[0] += (fx / AC.m) * dt;
  ac.v[1] += (fy / AC.m) * dt;
  ac.v[2] += (fz / AC.m) * dt;
  ac.p[0] += ac.v[0] * dt;
  ac.p[1] += ac.v[1] * dt;
  ac.p[2] += ac.v[2] * dt;

  // Euler's equations. The inertia tensor makes the axes talk to each other, which is why a
  // bootful of rudder at a high roll rate does not stay tidy.
  const w0 = ac.w[0];
  const w1 = ac.w[1];
  const w2 = ac.w[2];
  const hx = AC.Ix * w0;
  const hy = AC.Iy * w1;
  const hz = AC.Iz * w2;
  ac.w[0] += ((Tx - (w1 * hz - w2 * hy)) / AC.Ix) * dt;
  ac.w[1] += ((Ty - (w2 * hx - w0 * hz)) / AC.Iy) * dt;
  ac.w[2] += ((Tz - (w0 * hy - w1 * hx)) / AC.Iz) * dt;

  const dq = qMul([0, 0, 0, 0], ac.q, [ac.w[0] * 0.5 * dt, ac.w[1] * 0.5 * dt, ac.w[2] * 0.5 * dt, 0]);
  ac.q[0] += dq[0];
  ac.q[1] += dq[1];
  ac.q[2] += dq[2];
  ac.q[3] += dq[3];
  qNorm(ac.q);

  // wings are not infinitely strong: spars survive a brief snatch, it is holding it that folds them
  if (ac.gz > AC.gBreak || ac.gz < AC.gBreakNeg || V > AC.vne * 1.16) ac.overG += dt;
  else ac.overG = Math.max(0, ac.overG - dt * 2);
  if (ac.overG > 1.1) ac.crashed = true;

  // A spin driven into the ground can put the integrator somewhere it cannot come back from, and a
  // single NaN then spreads through position, camera and instruments and wedges the view showing
  // nothing. Whatever led here, the aeroplane is wrecked: say so, and let the usual relaunch clear it.
  if (!isFinite(ac.p[0] + ac.p[1] + ac.p[2] + ac.v[0] + ac.v[1] + ac.v[2] + ac.q[0] + ac.q[1] + ac.q[2] + ac.q[3] + ac.w[0] + ac.w[1] + ac.w[2])) {
    ac.p = [0, 0, 0];
    ac.v = [0, 0, 0];
    ac.q = [0, 0, 0, 1];
    ac.w = [0, 0, 0];
    ac.V = 0;
    ac.agl = 0;
    ac.crashed = true;
  }
}

/** A surface that moves at a finite rate: rate is full travel per second. */
export function toward(cur: number, want: number, rate: number, dt: number) {
  const d = want - cur;
  const m = rate * dt;
  return Math.abs(d) <= m ? want : cur + Math.sign(d) * m;
}

// ---- quaternions ----

export function qMul(o: Quat, a: Quat, b: Quat): Quat {
  const ax = a[0], ay = a[1], az = a[2], aw = a[3];
  const bx = b[0], by = b[1], bz = b[2], bw = b[3];
  o[0] = aw * bx + ax * bw + ay * bz - az * by;
  o[1] = aw * by - ax * bz + ay * bw + az * bx;
  o[2] = aw * bz + ax * by - ay * bx + az * bw;
  o[3] = aw * bw - ax * bx - ay * by - az * bz;
  return o;
}

export function qRot(o: V3, q: Quat, v: V3): V3 {
  const x = q[0], y = q[1], z = q[2], w = q[3];
  const tx = 2 * (y * v[2] - z * v[1]);
  const ty = 2 * (z * v[0] - x * v[2]);
  const tz = 2 * (x * v[1] - y * v[0]);
  o[0] = v[0] + w * tx + y * tz - z * ty;
  o[1] = v[1] + w * ty + z * tx - x * tz;
  o[2] = v[2] + w * tz + x * ty - y * tx;
  return o;
}

export function qNorm(q: Quat) {
  const l = Math.hypot(q[0], q[1], q[2], q[3]) || 1;
  q[0] /= l;
  q[1] /= l;
  q[2] /= l;
  q[3] /= l;
}

/** Heading, then pitch, then roll - the order an attitude is usually read out in. */
export function qEuler(o: Quat, hdg: number, pitch: number, roll: number): Quat {
  const ch = Math.cos(hdg * 0.5), sh = Math.sin(hdg * 0.5);
  const cp = Math.cos(pitch * 0.5), sp = Math.sin(pitch * 0.5);
  const cr = Math.cos(roll * 0.5), sr = Math.sin(roll * 0.5);
  // yaw about y, pitch about x, roll about z
  const qy: Quat = [0, sh, 0, ch];
  const qx: Quat = [sp, 0, 0, cp];
  const qz: Quat = [0, 0, sr, cr];
  qMul(o, qy, qx);
  qMul(o, o, qz);
  return o;
}

/** The 4x4 the plane mesh is drawn with: rotation from the attitude, translation, uniform scale. */
export function bodyMatrix(out: Float32Array, ac: Aircraft, tx: number, ty: number, tz: number, s: number): Float32Array {
  const r = ac.right;
  const u = ac.up;
  const f = ac.fwd;
  out[0] = r[0] * s;
  out[1] = r[1] * s;
  out[2] = r[2] * s;
  out[3] = 0;
  out[4] = u[0] * s;
  out[5] = u[1] * s;
  out[6] = u[2] * s;
  out[7] = 0;
  out[8] = f[0] * s;
  out[9] = f[1] * s;
  out[10] = f[2] * s;
  out[11] = 0;
  out[12] = tx;
  out[13] = ty;
  out[14] = tz;
  out[15] = 1;
  return out;
}
