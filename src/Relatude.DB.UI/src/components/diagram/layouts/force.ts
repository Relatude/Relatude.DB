/**
 * Fruchterman-Reingold: every pair of boxes pushes apart like two charges, every line between two of
 * them pulls like a spring, and the whole thing sits in a frame that keeps it together. The step a
 * box may take in one round cools toward nothing, so it starts by finding its part of the picture and
 * ends by settling into it. What it is good for is a model nothing inherits: clusters fall out of the
 * relations, and a type joined to nothing ends up at the edge where it is easy to spot.
 *
 * The forces work on points, so the whole relaxation is done in a space squeezed sideways by however
 * much wider a box is than it is tall - there the boxes are square and one distance means the same
 * thing either way - and the picture is stretched back out at the end. Whatever overlap is left after
 * that is pushed apart the short way.
 *
 * There is no randomness in it: the same model always relaxes into the same picture, or opening one
 * box would deal the whole diagram again.
 */

import { averageSize, gaps, normalize, pairs, separate, spiral, type LayoutBox, type LayoutEdge } from "./graph";

/** How much of the frame a box may cross in the first round. */
const startTemperature = 0.1;

export function fruchtermanReingold(boxes: LayoutBox[], edges: LayoutEdge[], spacing: number) {
  const n = boxes.length;
  if (n < 2) return;
  const links = pairs(boxes, edges);
  const size = averageSize(boxes);
  const aspect = size.w / size.h;
  const gap = gaps(spacing);
  const cell = size.h + gap.box;
  // the frame: room for every box and the space between them the springs settle at, and no more, or
  // what nothing is joined to ends up pinned out at the corners with the picture lost in the middle
  const frame = Math.sqrt(n) * cell * 1.25;
  const k = frame / Math.sqrt(n);
  spiral(boxes, frame / 2.5);
  const dispX = new Float64Array(n);
  const dispY = new Float64Array(n);
  const rounds = n > 160 ? 260 : 460;
  let temperature = frame * startTemperature;
  for (let round = 0; round < rounds; round++) {
    dispX.fill(0);
    dispY.fill(0);
    for (let i = 0; i < n; i++) {
      for (let j = i + 1; j < n; j++) {
        let ddx = boxes[i].x - boxes[j].x;
        let ddy = boxes[i].y - boxes[j].y;
        let d = Math.hypot(ddx, ddy);
        if (d < 0.01) {
          // one on top of the other, and the push has no direction: give it the same one every time
          // this pair meets, so the arrangement stays repeatable
          ddx = ((i * 7 + j * 13) % 11) / 11 - 0.5;
          ddy = ((i * 11 + j * 3) % 11) / 11 - 0.5;
          d = Math.hypot(ddx, ddy) || 0.01;
        }
        const f = (k * k) / (d * d);
        dispX[i] += ddx * f;
        dispY[i] += ddy * f;
        dispX[j] -= ddx * f;
        dispY[j] -= ddy * f;
      }
    }
    for (const [i, j] of links) {
      const ddx = boxes[i].x - boxes[j].x;
      const ddy = boxes[i].y - boxes[j].y;
      const d = Math.hypot(ddx, ddy);
      if (d < 0.01) continue;
      const f = d / k;
      dispX[i] -= ddx * f;
      dispY[i] -= ddy * f;
      dispX[j] += ddx * f;
      dispY[j] += ddy * f;
    }
    for (let i = 0; i < n; i++) {
      const d = Math.hypot(dispX[i], dispY[i]);
      if (d > 0.001) {
        const step = Math.min(d, temperature) / d;
        boxes[i].x += dispX[i] * step;
        boxes[i].y += dispY[i] * step;
      }
      const half = frame / 2;
      boxes[i].x = Math.min(half, Math.max(-half, boxes[i].x));
      boxes[i].y = Math.min(half, Math.max(-half, boxes[i].y));
    }
    temperature = frame * startTemperature * (1 - (round + 1) / rounds);
  }
  // the relaxation moved centres about; the boxes hang off them by half their size
  for (const b of boxes) {
    b.x = b.x * aspect - b.w / 2;
    b.y -= b.h / 2;
  }
  separate(boxes, gap.pad);
  normalize(boxes);
}
