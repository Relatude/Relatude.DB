/**
 * The weight a map has. A hand that flicks it and lets go leaves it moving, and it slows to a stop
 * on its own; a wheel that is spun keeps zooming for a moment after the fingers stop. The same
 * feeling the visual pivot's camera has (graph3d/camera.ts), and for the same reason: a picture
 * that stops dead the instant it is released feels like a scrollbar rather than a thing.
 *
 * Everything here is in POINTER PIXELS a second, whichever picture is on screen. The flat map turns
 * pixels into world units and the globe turns them into degrees, and both already do exactly that
 * for a drag - so a glide is a drag that nobody is holding any more, and neither of them needs to
 * know that it is one.
 */

/** How fast a motion bleeds away, in e-folds a second: after half a second about a tenth is left. */
const glideDamping = 4.5;
/** Zooming settles faster. It travels further per unit of velocity, and an overshoot is worse there. */
const zoomDamping = 6.5;
/** Below this a motion is over: a fraction of a pixel and a fraction of a percent a second. */
const stillPixels = 6;
const stillZoom = 0.02;
/** How far back a release looks for the speed of the hand, in milliseconds. */
const sampleWindow = 90;
/** and how fast it may say the hand was going, so a stuttering pointer cannot launch the map */
const maxGlide = 4500;

interface Sample {
  x: number;
  y: number;
  at: number;
}

export interface MotionStep {
  /** pointer pixels to move by this frame */
  dx: number;
  dy: number;
  /** and what to multiply the zoom by */
  zoom: number;
}

export class Momentum {
  private vx = 0;
  private vy = 0;
  private vZoom = 0;
  private samples: Sample[] = [];
  /** Where a zoom is closing in on, in css pixels; null zooms on the middle of the canvas. */
  anchor: [number, number] | null = null;

  /** A pointer moved while it was down. Kept only long enough to tell how fast it was going. */
  track(x: number, y: number, at = performance.now()): void {
    this.samples.push({ x, y, at });
    while (this.samples.length > 2 && at - this.samples[0].at > sampleWindow) this.samples.shift();
  }

  /** The hand let go: whatever speed it had is the map's now. */
  release(at = performance.now()): void {
    const last = this.samples[this.samples.length - 1];
    const first = this.samples[0];
    this.samples = [];
    if (!last || !first || last === first) return;
    // a hand that stopped before letting go was not throwing anything
    const idle = at - last.at;
    const span = (last.at - first.at) / 1000;
    if (idle > sampleWindow || span <= 0) return;
    this.vx = clamp((last.x - first.x) / span, maxGlide);
    this.vy = clamp((last.y - first.y) / span, maxGlide);
  }

  /** A wheel was turned: `amount` is in e-folds of zoom, and the map goes on zooming for a moment. */
  push(amount: number, anchor: [number, number] | null): void {
    this.anchor = anchor;
    // added rather than replaced, so a fast scroll builds up the way a fast scroll should
    this.vZoom = clamp(this.vZoom + amount * zoomDamping, 40);
  }

  /** Nothing is moving of its own accord any more. */
  stop(): void {
    this.vx = 0;
    this.vy = 0;
    this.vZoom = 0;
    this.samples = [];
  }

  get moving(): boolean {
    return Math.abs(this.vx) > stillPixels || Math.abs(this.vy) > stillPixels || Math.abs(this.vZoom) > stillZoom;
  }

  /**
   * How far the map travels in the next `dt` seconds, or null when it has come to rest. The
   * distance is the integral of a velocity that decays rather than the velocity times the frame,
   * so a slow frame and two fast ones move it the same distance.
   */
  step(dt: number): MotionStep | null {
    if (!this.moving) {
      this.vx = this.vy = this.vZoom = 0;
      return null;
    }
    const glide = Math.exp(-glideDamping * Math.min(0.1, dt));
    const zoom = Math.exp(-zoomDamping * Math.min(0.1, dt));
    const dx = (this.vx * (1 - glide)) / glideDamping;
    const dy = (this.vy * (1 - glide)) / glideDamping;
    const dz = (this.vZoom * (1 - zoom)) / zoomDamping;
    this.vx *= glide;
    this.vy *= glide;
    this.vZoom *= zoom;
    return { dx, dy, zoom: Math.exp(dz) };
  }
}

const clamp = (v: number, limit: number) => Math.max(-limit, Math.min(limit, v));
