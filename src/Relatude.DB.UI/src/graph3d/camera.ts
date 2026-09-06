import { add, cross, normalize, perspective, scale, sub, view, type Mat4, type Vec3 } from "./math";

/**
 * A camera that flies: a position, a heading (yaw and pitch, no roll) and a focus distance along the
 * heading, which together name the point it orbits and pans around. Every motion is a velocity that
 * keeps going after the hand lets go and bleeds away, so an orbit, a pan, a zoom or a flight has
 * some weight to it. While a channel is held by a pointer the pointer drives it directly and its
 * velocity is only measured, ready for the release.
 */

export type Channel = "orbit" | "look" | "pan";

export interface Pose {
  pos: Vec3;
  yaw: number;
  pitch: number;
  dist: number;
}

const worldUp: Vec3 = [0, 1, 0];
const maxPitch = 1.5;
const minDist = 12;
const damping = 4.5; // per second: after half a second roughly a tenth is left
const flyDamping = 5;
const dollyDamping = 6;

export class FlyCamera {
  pos: Vec3 = [0, 0, 500];
  yaw = 0;
  pitch = 0;
  dist = 500;
  readonly fov = (50 * Math.PI) / 180;
  readonly near = 1;
  readonly far = 40000;

  private vYaw = 0;
  private vPitch = 0;
  private vLookYaw = 0;
  private vLookPitch = 0;
  private vPanX = 0;
  private vPanY = 0;
  private vDolly = 0;
  private dollyRay: Vec3 = [0, 0, -1];
  private vFly: Vec3 = [0, 0, 0];
  private held = new Set<Channel>();
  private lastSample = new Map<Channel, number>();
  private tween: { from: Pose; to: Pose; start: number; ms: number } | null = null;
  autoOrbit = false;

  // ---- frame ----

  forward(): Vec3 {
    const cp = Math.cos(this.pitch);
    return [Math.sin(this.yaw) * cp, Math.sin(this.pitch), -Math.cos(this.yaw) * cp];
  }
  right(): Vec3 {
    return normalize(cross(this.forward(), worldUp));
  }
  up(): Vec3 {
    return cross(this.right(), this.forward());
  }
  target(): Vec3 {
    return add(this.pos, scale(this.forward(), this.dist));
  }
  viewMatrix(): Mat4 {
    return view(this.pos, this.forward(), this.up());
  }
  projMatrix(aspect: number): Mat4 {
    return perspective(this.fov, aspect, this.near, this.far);
  }
  /** The ray through a point of the viewport, in world space and unit length. */
  ray(ndcX: number, ndcY: number, aspect: number): Vec3 {
    const t = Math.tan(this.fov / 2);
    const f = this.forward();
    const r = this.right();
    const u = this.up();
    return normalize(add(f, add(scale(r, ndcX * t * aspect), scale(u, ndcY * t))));
  }
  /** World units per pixel at the focus distance, for a viewport this many pixels high. */
  unitsPerPixel(viewportHeight: number) {
    return (2 * this.dist * Math.tan(this.fov / 2)) / Math.max(1, viewportHeight);
  }
  pose(): Pose {
    return { pos: [...this.pos], yaw: this.yaw, pitch: this.pitch, dist: this.dist };
  }
  setPose(p: Pose) {
    this.pos = [...p.pos];
    this.yaw = p.yaw;
    this.pitch = Math.max(-maxPitch, Math.min(maxPitch, p.pitch));
    this.dist = Math.max(minDist, p.dist);
  }
  /** Placed so the target is in the middle at the given distance, heading kept. */
  poseLookingAt(target: Vec3, dist: number, yaw = this.yaw, pitch = this.pitch): Pose {
    const cp = Math.cos(pitch);
    const f: Vec3 = [Math.sin(yaw) * cp, Math.sin(pitch), -Math.cos(yaw) * cp];
    return { pos: sub(target, scale(f, dist)), yaw, pitch, dist };
  }

  // ---- what the hand does, applied at once and remembered as a velocity ----

  hold(channel: Channel) {
    this.tween = null;
    this.held.add(channel);
    this.lastSample.set(channel, performance.now());
    if (channel === "orbit") this.vYaw = this.vPitch = 0;
    if (channel === "look") this.vLookYaw = this.vLookPitch = 0;
    if (channel === "pan") this.vPanX = this.vPanY = 0;
  }
  release(channel: Channel) {
    this.held.delete(channel);
    // a hand that had come to rest before letting go does not fling the view
    if (performance.now() - (this.lastSample.get(channel) ?? 0) > 80) {
      if (channel === "orbit") this.vYaw = this.vPitch = 0;
      if (channel === "look") this.vLookYaw = this.vLookPitch = 0;
      if (channel === "pan") this.vPanX = this.vPanY = 0;
    }
  }
  /** Rotates around the focus point by the given angles, measuring the speed for the release. */
  orbit(dYaw: number, dPitch: number, dt: number) {
    this.applyOrbit(dYaw, dPitch);
    this.vYaw = sample(this.vYaw, dYaw / dt);
    this.vPitch = sample(this.vPitch, dPitch / dt);
    this.lastSample.set("orbit", performance.now());
  }
  /** Turns the camera where it stands; the focus point swings with the heading. */
  look(dYaw: number, dPitch: number, dt: number) {
    this.applyLook(dYaw, dPitch);
    this.vLookYaw = sample(this.vLookYaw, dYaw / dt);
    this.vLookPitch = sample(this.vLookPitch, dPitch / dt);
    this.lastSample.set("look", performance.now());
  }
  /** Slides sideways and up, in world units. */
  pan(dx: number, dy: number, dt: number) {
    this.applyPan(dx, dy);
    this.vPanX = sample(this.vPanX, dx / dt);
    this.vPanY = sample(this.vPanY, dy / dt);
    this.lastSample.set("pan", performance.now());
  }
  /** A wheel tick: gathers speed toward (or away from) what is under the cursor. */
  dolly(amount: number, ray: Vec3) {
    this.tween = null;
    this.dollyRay = ray;
    this.vDolly = Math.max(-6, Math.min(6, this.vDolly + amount));
  }
  /** Acceleration in the camera's own frame (right, up, forward), from the keys. */
  thrust(x: number, y: number, z: number, dt: number) {
    this.tween = null;
    const s = Math.max(this.dist, 120);
    const a = 5 * s * dt;
    this.vFly = [this.vFly[0] + x * a, this.vFly[1] + y * a, this.vFly[2] + z * a];
    const cap = 3 * s;
    const l = Math.hypot(...this.vFly);
    if (l > cap) this.vFly = scale(this.vFly, cap / l);
  }
  stop() {
    this.vYaw = this.vPitch = this.vLookYaw = this.vLookPitch = this.vPanX = this.vPanY = this.vDolly = 0;
    this.vFly = [0, 0, 0];
    this.tween = null;
  }
  animateTo(to: Pose, ms = 520) {
    this.stop();
    this.tween = { from: this.pose(), to, start: performance.now(), ms };
  }

  // ---- time passing ----

  /** Advances every motion by dt seconds and says whether anything still moves. */
  step(dt: number, now: number): boolean {
    let moving = false;
    const t = this.tween;
    if (t) {
      const p = Math.min(1, (now - t.start) / t.ms);
      const e = 1 - Math.pow(1 - p, 3);
      // the shorter way round for the heading
      let dy = t.to.yaw - t.from.yaw;
      dy = Math.atan2(Math.sin(dy), Math.cos(dy));
      this.setPose({
        pos: [t.from.pos[0] + (t.to.pos[0] - t.from.pos[0]) * e, t.from.pos[1] + (t.to.pos[1] - t.from.pos[1]) * e, t.from.pos[2] + (t.to.pos[2] - t.from.pos[2]) * e],
        yaw: t.from.yaw + dy * e,
        pitch: t.from.pitch + (t.to.pitch - t.from.pitch) * e,
        dist: t.from.dist + (t.to.dist - t.from.dist) * e,
      });
      if (p >= 1) this.tween = null;
      else moving = true;
    }
    const decay = Math.exp(-damping * dt);
    // a held channel is driven by the hand; its speed only decays if the hand has stopped moving,
    // so that a pause before letting go does not throw the view
    const stale = (c: Channel) => now - (this.lastSample.get(c) ?? 0) > 60;
    if (this.held.has("orbit")) {
      if (stale("orbit")) {
        this.vYaw *= 0.5;
        this.vPitch *= 0.5;
      }
    } else if (Math.abs(this.vYaw) + Math.abs(this.vPitch) > 1e-4) {
      this.applyOrbit(this.vYaw * dt, this.vPitch * dt);
      this.vYaw *= decay;
      this.vPitch *= decay;
      moving = true;
    }
    if (this.held.has("look")) {
      if (stale("look")) {
        this.vLookYaw *= 0.5;
        this.vLookPitch *= 0.5;
      }
    } else if (Math.abs(this.vLookYaw) + Math.abs(this.vLookPitch) > 1e-4) {
      this.applyLook(this.vLookYaw * dt, this.vLookPitch * dt);
      this.vLookYaw *= decay;
      this.vLookPitch *= decay;
      moving = true;
    }
    if (this.held.has("pan")) {
      if (stale("pan")) {
        this.vPanX *= 0.5;
        this.vPanY *= 0.5;
      }
    } else if (Math.abs(this.vPanX) + Math.abs(this.vPanY) > 0.01 * this.dist) {
      this.applyPan(this.vPanX * dt, this.vPanY * dt);
      this.vPanX *= decay;
      this.vPanY *= decay;
      moving = true;
    }
    if (Math.abs(this.vDolly) > 1e-3) {
      // a fraction of the focus distance per second along the ray; once the focus is as near as it
      // gets the camera flies on through
      const amount = Math.max(-0.5, Math.min(0.5, this.vDolly * dt));
      const move = this.dist * amount;
      this.pos = add(this.pos, scale(this.dollyRay, move));
      this.dist = Math.max(minDist, this.dist - move);
      this.vDolly *= Math.exp(-dollyDamping * dt);
      moving = true;
    }
    const fl = Math.hypot(...this.vFly);
    if (fl > 0.5) {
      const r = this.right();
      const u = this.up();
      const f = this.forward();
      const d = add(add(scale(r, this.vFly[0] * dt), scale(u, this.vFly[1] * dt)), scale(f, this.vFly[2] * dt));
      this.pos = add(this.pos, d);
      const k = Math.exp(-flyDamping * dt);
      this.vFly = scale(this.vFly, k);
      moving = true;
    }
    if (this.autoOrbit && this.held.size === 0 && !this.tween) {
      this.applyOrbit(0.12 * dt, 0);
      moving = true;
    }
    return moving;
  }

  private applyOrbit(dYaw: number, dPitch: number) {
    const target = this.target();
    this.yaw += dYaw;
    this.pitch = Math.max(-maxPitch, Math.min(maxPitch, this.pitch + dPitch));
    this.pos = sub(target, scale(this.forward(), this.dist));
  }
  private applyLook(dYaw: number, dPitch: number) {
    this.yaw += dYaw;
    this.pitch = Math.max(-maxPitch, Math.min(maxPitch, this.pitch + dPitch));
  }
  private applyPan(dx: number, dy: number) {
    this.pos = add(this.pos, add(scale(this.right(), dx), scale(this.up(), dy)));
  }
}

/** A speed reading smoothed against the last, so one jittery pointer event does not set the fling. */
function sample(prev: number, next: number) {
  if (!isFinite(next)) return prev;
  return prev * 0.4 + next * 0.6;
}
