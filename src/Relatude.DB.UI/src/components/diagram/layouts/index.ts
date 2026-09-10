import { IconAffiliate, IconGridDots, IconHierarchy2, IconTopologyStar3 } from "@tabler/icons-react";
import type { PreferredAxis } from "../router";
import { fruchtermanReingold } from "./force";
import type { LayoutBox, LayoutEdge } from "./graph";
import { orthogonalLayout } from "./orthogonal";
import { stressLayout } from "./stress";
import { sugiyama } from "./sugiyama";

export type { EdgeKind, LayoutBox, LayoutEdge } from "./graph";

/**
 * The four ways the boxes can be arranged. Each is a published method rather than a house rule, and
 * each answers a different question about a model: what inherits and holds what, which types cluster
 * together, how the whole thing looks laid out on a grid, and how far apart any two types really are.
 * All four write x and y straight onto the boxes and nothing else in the diagram knows which one ran;
 * the lines are then routed round them the same way whichever it was, and anything can be dragged
 * afterwards.
 */
export type LayoutMode = "sugiyama" | "force" | "orthogonal" | "stress";

/**
 * How much room the arrangements leave around the boxes: the steps the two spacing buttons walk
 * through, 1 being what each method is tuned at. Compact fits a big model on a screen; spaced out
 * gives the lines between the boxes room to be told apart. Below about 0.5 the lines start going
 * round pairs of boxes rather than between them - there is no lane left - which is part of what the
 * tightest steps look like.
 */
export const spacings = [0.35, 0.45, 0.6, 0.8, 1, 1.3, 1.7, 2.2, 3];
export const defaultSpacing = spacings.indexOf(1);

export interface LayoutInfo {
  id: LayoutMode;
  label: string;
  hint: string;
  icon: typeof IconHierarchy2;
  /** Which way the router prefers to leave a box when the other one is beside and above it. */
  axis: PreferredAxis;
  run: (boxes: LayoutBox[], edges: LayoutEdge[], spacing: number) => void;
}

export const layouts: LayoutInfo[] = [
  {
    id: "sugiyama",
    label: "Layered",
    hint: "Sugiyama: layers down the model's own direction, parents above children and sources above targets, ordered to cross as little as it can",
    icon: IconHierarchy2,
    axis: "v",
    run: sugiyama,
  },
  {
    id: "force",
    label: "Force",
    hint: "Fruchterman-Reingold: boxes push apart, lines pull together, cooling into a rest - clusters fall out of the relations",
    icon: IconAffiliate,
    axis: "auto",
    run: fruchtermanReingold,
  },
  {
    id: "orthogonal",
    label: "Orthogonal",
    hint: "Topology-Shape-Metrics: boxes on a grid, each beside or above a neighbour, so most lines are one straight run",
    icon: IconGridDots,
    axis: "auto",
    run: orthogonalLayout,
  },
  {
    id: "stress",
    label: "Stress",
    hint: "Stress majorization with PRISM: distance on screen follows distance through the model, then the boxes are made room for",
    icon: IconTopologyStar3,
    axis: "auto",
    run: stressLayout,
  },
];

export function layoutInfo(mode: LayoutMode): LayoutInfo {
  return layouts.find((l) => l.id === mode) ?? layouts[0];
}

export function isLayoutMode(value: string | null): value is LayoutMode {
  return layouts.some((l) => l.id === value);
}
