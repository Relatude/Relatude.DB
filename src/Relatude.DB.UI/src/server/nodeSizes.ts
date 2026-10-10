import { send } from "./channel";
import type { ModelKind } from "./datamodel";

// How big the nodes of a database are where they are stored. Every node has a segment in the log
// file - where its bytes start and how many there are - so the sizes come from the segment map and
// no node is read for the overview; only the lists and the breakdowns read nodes.

/** Sizes of a set of nodes, in bytes. The percentiles are sizes some node actually has. */
export interface SizeStats {
  min: number;
  max: number;
  mean: number;
  median: number;
  p90: number;
  p99: number;
}

export interface NodeSizeType {
  id: string;
  name: string;
  full: string;
  kind: ModelKind;
  count: number;
  bytes: number;
  stats: SizeStats;
  /** nodes per size bar, one per gap between two of the overview's edges */
  sizeCounts: number[];
  /** bytes per size bar */
  sizeBytes: number[];
  /** bytes of this type's nodes in each stretch of the log file */
  position: number[];
}

export interface NodeSizesOverview {
  logFileKey: string;
  logFileSize: number;
  /** nodes with a position in the log - every node but the ones still waiting to be written */
  measured: number;
  notYetWritten: number;
  totalBytes: number;
  stats: SizeStats;
  /** the size bars' edges in bytes, ascending: bar i holds sizes from edges[i] up to edges[i + 1] */
  edges: number[];
  /** the length of one stretch of the log file, and how many there are */
  binSize: number;
  bins: number;
  /** largest share of the bytes first */
  types: NodeSizeType[];
  elapsedMs: number;
}

export interface LargestNode {
  id: number;
  nodeId: string;
  typeId: string;
  typeName: string;
  size: number;
  position: number;
  /** null when the node has no name of its own, or was too far down a long list to be read */
  name: string | null;
}

export interface LargestList {
  /** every node the selection holds, of which the largest are listed */
  matching: number;
  matchingBytes: number;
  nodes: LargestNode[];
}

export type AnatomyPartKind = "Header" | "DisplayName" | "Address" | "Meta" | "Property";

export interface AnatomyPart {
  kind: AnatomyPartKind;
  propertyId: string;
  /** where the property is declared, to open it in the model editor */
  declaringTypeId: string | null;
  label: string;
  propertyType: string | null;
  /** everything the part takes, framing included; for a type, summed over the sample */
  bytes: number;
  valueBytes: number;
  /** for a node, the revisions carrying it; for a type, the sampled nodes that have it */
  occurrences: number;
  /** a stored value the node's type no longer has: "not on this type" or "not in the model" */
  orphan: string | null;
  preview: string | null;
  /** for a type: the share of the sampled nodes that have it */
  share: number;
}

export interface NodeAnatomyView {
  id: number;
  nodeId: string;
  typeId: string;
  typeName: string;
  name: string | null;
  size: number;
  position: number;
  version: string;
  revisions: number;
  parts: AnatomyPart[];
}

export interface TypeAnatomyView {
  typeId: string;
  typeName: string;
  nodes: number;
  sampled: number;
  sampledBytes: number;
  parts: AnatomyPart[];
}

export function fetchNodeSizes(storeId: string): Promise<NodeSizesOverview> {
  return send<NodeSizesOverview>("node-sizes", { storeId });
}

/** The largest nodes of a type and a size range, both optional; maxBytes is exclusive. */
export function fetchLargestNodes(
  storeId: string,
  filter: { typeId?: string | null; minBytes?: number | null; maxBytes?: number | null; take?: number },
): Promise<LargestList> {
  return send<LargestList>("node-sizes-list", { storeId, ...filter });
}

export function fetchNodeAnatomy(storeId: string, id: number): Promise<NodeAnatomyView> {
  return send<NodeAnatomyView>("node-sizes-node", { storeId, id });
}

export function fetchTypeAnatomy(storeId: string, typeId: string, sample?: number): Promise<TypeAnatomyView> {
  return send<TypeAnatomyView>("node-sizes-type", { storeId, typeId, sample });
}
