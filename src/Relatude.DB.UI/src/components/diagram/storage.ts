/**
 * What the reader has arranged by hand, remembered per database. Dragged boxes and lines pushed
 * sideways belong to the arrangement they were dragged in - a box moved in the layers means nothing
 * on the grid - so each layout keeps its own, and the choice of layout is kept once.
 */

import { defaultSpacing, isLayoutMode, spacings, type EdgeKind, type LayoutMode } from "./layouts";
import { edgeKinds } from "./model";
import type { Pt } from "./router";

const positionsKey = (storeId: string, mode: LayoutMode) => "dmDiagram:" + storeId + ":" + mode;
const linesKey = (storeId: string, mode: LayoutMode) => "dmDiagramLines:" + storeId + ":" + mode;
const layoutKey = (storeId: string) => "dmDiagramLayout:" + storeId;
// how much room the boxes are given is a reading of the whole model rather than of one arrangement,
// so every layout of a database shares it
const spacingKey = (storeId: string) => "dmDiagramSpacing:" + storeId;
const panKey = "dmDiagramPan";
// which kinds of line are drawn: kept as the ones switched off, so all of them on needs no entry
const kindsKey = (storeId: string) => "dmDiagramKinds:" + storeId;

function read<T>(key: string): T | null {
  try {
    const raw = localStorage.getItem(key);
    return raw ? (JSON.parse(raw) as T) : null;
  } catch {
    return null;
  }
}

/** Storage may be unavailable, and what was arranged then simply is not remembered. */
function write(key: string, value: unknown, empty: boolean) {
  try {
    if (empty) localStorage.removeItem(key);
    else localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // nothing to do about it
  }
}

export type Positions = Record<string, { x: number; y: number }>;
export type Lines = Record<string, Pt[]>;

export const readPositions = (storeId: string, mode: LayoutMode): Positions => read<Positions>(positionsKey(storeId, mode)) ?? {};
export const writePositions = (storeId: string, mode: LayoutMode, positions: Positions) => write(positionsKey(storeId, mode), positions, Object.keys(positions).length === 0);
export const readLines = (storeId: string, mode: LayoutMode): Lines => read<Lines>(linesKey(storeId, mode)) ?? {};
export const writeLines = (storeId: string, mode: LayoutMode, lines: Lines) => write(linesKey(storeId, mode), lines, Object.keys(lines).length === 0);

export function readLayout(storeId: string): LayoutMode {
  const saved = localStorage.getItem(layoutKey(storeId));
  return isLayoutMode(saved) ? saved : "sugiyama";
}

export function readSpacing(storeId: string): number {
  // nothing remembered reads as null, and Number(null) is a perfectly good 0: ask first
  const saved = localStorage.getItem(spacingKey(storeId));
  const step = saved === null ? NaN : Number(saved);
  return Number.isInteger(step) && step >= 0 && step < spacings.length ? step : defaultSpacing;
}

export function writeSpacing(storeId: string, step: number) {
  try {
    localStorage.setItem(spacingKey(storeId), String(step));
  } catch {
    // the choice then simply is not remembered
  }
}

/**
 * Whether dragging the background pans the drawing rather than picking boxes out of it. Which of the
 * two the hand does is a habit of the reader rather than a fact about a model, so it is kept once and
 * shared by every database. What is picked is not kept at all.
 */
export const readPanMode = () => localStorage.getItem(panKey) === "true";

export function writePanMode(on: boolean) {
  try {
    localStorage.setItem(panKey, String(on));
  } catch {
    // the choice then simply is not remembered
  }
}

export function readKinds(storeId: string): Set<EdgeKind> {
  const off = read<EdgeKind[]>(kindsKey(storeId)) ?? [];
  return new Set(edgeKinds.map((k) => k.id).filter((id) => !off.includes(id)));
}

export function writeKinds(storeId: string, kinds: Set<EdgeKind>) {
  const off = edgeKinds.map((k) => k.id).filter((id) => !kinds.has(id));
  write(kindsKey(storeId), off, off.length === 0);
}

export function writeLayout(storeId: string, mode: LayoutMode) {
  try {
    localStorage.setItem(layoutKey(storeId), mode);
  } catch {
    // the choice then simply is not remembered
  }
}
