/**
 * Right-angled line routing for the datamodel diagram: every line is made of horizontal and vertical
 * pieces only, bends as often as it has to, and never runs through a box.
 *
 * Where a line leaves and enters its boxes is decided first (route.ts), the way between the two is
 * the cheapest walk over a sparse grid of lanes between the boxes (grid.ts), what is drawn from that
 * is in draw.ts, and what a hand does to a line afterwards in edit.ts.
 */

export { borderPoint, routeMargin, simplify, type PreferredAxis, type Pt, type Route, type RouteRect } from "./geometry";
export { routeAll } from "./route";
export { blend, cornerRadius, crossingGaps, labelPlace, orthoPath, plainPath, type Gap } from "./draw";
export { moveSegment, routeTouches, validManual } from "./edit";
