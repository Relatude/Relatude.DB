// The landing page of one database. Two calls, because they cost very different amounts (see
// UIDashboard.cs): the full picture once, and a cheap sample every couple of seconds that the page
// turns into rates.

import { send } from "./channel";
import type { ModelKind, SourceType } from "./datamodel";

/**
 * One node type and how much of the database is it, counted both ways: `count` is the nodes whose
 * own type this is, `countAll` those plus every node of a type below it. A parent and its children
 * both count the same nodes in `countAll`, so the two are never mixed in one picture - that is what
 * the content panel's "include inherited" switch chooses between.
 */
export interface TypeCount {
  id: string;
  name: string;
  full: string;
  count: number;
  countAll: number;
  kind: ModelKind;
  isInterface: boolean;
  sourceId: string;
  /** the types this one is directly under, base type excluded; empty means it is outermost */
  parents: string[];
}

export interface FileSizes {
  database: number;
  state: number;
  logs: number;
  backups: number;
  secondary: number;
}

export interface DashboardInfo {
  open: boolean;
  state: string;
  name: string;
  storeId: string;
  startupError: { timeUtc: string | null; message: string } | null;
  files: FileSizes;
  engines: { textIndex: string; valueIndex: string; queue: string; semanticIndex?: string | null };
  uptimeMs?: number;
  startUpMs?: number;
  openedUtc?: string | null;
  firstChangeUtc?: string | null;
  lastChangeUtc?: string | null;
  datamodel?: { nodeTypes: number; properties: number; relations: number; indexes: number };
  types?: TypeCount[];
  /** the model sources, in load order: their position decides their colour (see sourceColors) */
  sources?: { id: string; name: string; type: SourceType }[];
  maintenance?: {
    actionsNotInState: number;
    truncatableActions: number;
    indexesOutOfSync: number;
    runningRewrite: string | null;
  };
  cache?: {
    nodeCacheSizePercentage: number;
    nodeCacheHits: number;
    nodeCacheMisses: number;
    nodeCacheOverflows: number;
    setCacheSizePercentage: number;
    setCacheHits: number;
    setCacheMisses: number;
    setCacheOverflows: number;
    aggregateCacheCount: number;
    aggregateCacheHits: number;
    aggregateCacheMisses: number;
  };
  ai?: { provider: string | null; embeddingModel: string | null } | null;
  relationTypes?: number;
}

export interface DashboardLive {
  open: boolean;
  state: string;
  /** When the counters were read, which is what the rates are measured against. */
  sampledUtc: string;
  nodeCount?: number;
  relationCount?: number;
  /** Cumulative since the caches were last cleared: a drop means a reset, not negative traffic. */
  queries?: number;
  transactions?: number;
  actions?: number;
  nodeReads?: number;
  /**
   * The server process, not this database: one heap serves every database on it. A level rather
   * than a counter, so the graph plots the samples themselves instead of their difference.
   */
  managedMemory?: number;
  processMemory?: number;
  nodeCacheCount?: number;
  nodeCacheSize?: number;
  setCacheCount?: number;
  setCacheSize?: number;
  tasksQueued?: number;
  /** Set only while the database is opening: how far the log replay has come. */
  opening?: { progressPercentage: number; timeRemainingMs: number; timeElapsedMs: number } | null;
  conversions?: { running: number; queued: number; failed: number };
  activities?: { category: string; description: string | null; percentageProgress: number | null }[];
}

export function fetchDashboard(storeId: string): Promise<DashboardInfo> {
  return send<DashboardInfo>("dashboard", { storeId });
}

export function fetchDashboardLive(storeId: string): Promise<DashboardLive> {
  return send<DashboardLive>("dashboard-live", { storeId });
}

export interface ClearCacheResult {
  /** What the node and result set caches held when they were emptied. */
  entriesCleared: number;
  freedBytes: number;
  managedBytes: number;
  elapsedMs: number;
}

/**
 * Empties this database's node, result set and index caches, and collects what they held. The
 * counters above start over, so the rate graph shows a gap rather than a drop, and the indexes
 * warm again in the background.
 */
export function clearCaches(storeId: string): Promise<ClearCacheResult> {
  return send<ClearCacheResult>("dashboard-clear-cache", { storeId });
}
