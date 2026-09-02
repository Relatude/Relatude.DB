// The landing page of one database. Two calls, because they cost very different amounts (see
// UIDashboard.cs): the full picture once, and a cheap sample every couple of seconds that the page
// turns into rates.

import { send } from "./channel";

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
  types?: { name: string; full: string; count: number }[];
  otherTypes?: number;
  otherTypeNodes?: number;
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
