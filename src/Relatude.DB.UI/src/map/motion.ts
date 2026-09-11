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
/**
 * How fast the zoom catches up with where it has been asked to go, in e-folds a second. A wheel
 * arrives in notches - one lump of scroll per click of the finger - and applying a notch the
 * instant it lands is what makes a zoom feel like a staircase. So a notch is not applied at all:
 * it is ADDED TO A DEBT, and every frame pays off a share of whatever is owed. Fast enough that
 * the map is where you asked inside a quarter of a second, smooth enough that the steps are gone.
 */
const zoomFollow = 13;
/** Below this a motion is over: a fraction of a pixel, and a zoom nobody could see. */
const stillPixels = 6;
const stillZoom = 0.0008;
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
  /** e-folds of zoom asked for and not yet applied */
  private owed = 0;
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

  /** A wheel was turned: `amount` is in e-folds of zoom, owed to the map and paid off over the next moment. */
  push(amount: number, anchor: [number, number] | null): void {
    this.anchor = anchor;
    // added rather than replaced, so spinning the wheel fast goes further than turning it once
    this.owed = clamp(this.owed + amount, 6);
  }

  /** Nothing is moving of its own accord any more. */
  stop(): void {
    this.vx = 0;
    this.vy = 0;
    this.owed = 0;
    this.samples = [];
  }

  get moving(): boolean {
    return Math.abs(this.vx) > stillPixels || Math.abs(this.vy) > stillPixels || Math.abs(this.owed) > stillZoom;
  }

  /**
   * How far the map travels in the next `dt` seconds, or null when it has come to rest. The
   * distance is the integral of a velocity that decays rather than the velocity times the frame,
   * so a slow frame and two fast ones move it the same distance.
   */
  step(dt: number): MotionStep | null {
    if (!this.moving) {
      this.vx = this.vy = this.owed = 0;
      return null;
    }
    const step = Math.min(0.1, dt);
    const glide = Math.exp(-glideDamping * step);
    const dx = (this.vx * (1 - glide)) / glideDamping;
    const dy = (this.vy * (1 - glide)) / glideDamping;
    this.vx *= glide;
    this.vy *= glide;
    // whatever share of the debt this frame is long enough to cover
    const paid = this.owed * (1 - Math.exp(-zoomFollow * step));
    this.owed -= paid;
    return { dx, dy, zoom: Math.exp(paid) };
  }
}

const clamp = (v: number, limit: number) => Math.max(-limit, Math.min(limit, v));
