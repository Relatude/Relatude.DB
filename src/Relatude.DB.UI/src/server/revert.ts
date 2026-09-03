// The revert window of one database (server side: NodeServer/UI/UIRevert.cs).
//
// Begin marks the current position in the transaction log; everything written after it can then
// be thrown away with a rollback - the log is cut back to the mark and the database reloads - or
// kept with a commit. While a window is open the database suspends what would persist state past
// the mark (index durability, state snapshots, log rewrites), which is what keeps a rollback cheap.
//
// Log timestamps travel as strings: they are UTC ticks, past what a javascript number holds
// exactly, and nothing here does arithmetic on them.

import { send } from "./channel";

export interface RevertWindowInfo {
  timestamp: string;
  /** The rollback target as a point in time: the last transaction that survives a rollback. */
  timestampUtc: string;
  begunUtc: string;
  logPosition: number;
}

export interface RevertStatus {
  storeId: string;
  active: boolean;
  window: RevertWindowInfo | null;
  headTimestamp: string;
  /** When the newest transaction in the log was written, or null for an empty log. */
  headUtc: string | null;
  /** Whether anything has been written since the window began: the head moved past the mark. */
  changedSinceBegin: boolean;
}

/** What a rollback deleted, or (a preview) would delete. */
export interface RevertResult {
  dryRun: boolean;
  afterUtc: string | null;
  lastUtc: string | null;
  transactionsDeleted: number;
  actionsDeleted: number;
  bytesTruncated: number;
  /** The state snapshot was newer than the mark, so state and every index were rebuilt from the log. */
  stateAndIndexesReset: boolean;
  /** Index engines that had persisted past the mark and were reset to be rebuilt. */
  enginesReset: string[];
  durationMs: number;
}

export function fetchRevertStatus(storeId: string): Promise<RevertStatus> {
  return send<RevertStatus>("revert-status", { storeId });
}

/** Begins a window. With `saveStateFirst` the state snapshot is written first, so a rollback reloads from it rather than replaying the log. */
export function beginRevertWindow(storeId: string, saveStateFirst: boolean): Promise<RevertStatus> {
  return send<RevertStatus>("revert-begin", { storeId, saveStateFirst });
}

/** Ends the window keeping every change made inside it. */
export function commitRevertWindow(storeId: string): Promise<RevertStatus> {
  return send<RevertStatus>("revert-commit", { storeId });
}

/** What a rollback would delete right now. Scans the log tail; changes nothing. */
export function previewRollback(storeId: string): Promise<RevertResult> {
  return send<RevertResult>("revert-preview", { storeId });
}

/** Ends the window deleting every change made inside it. The database reloads before this returns. */
export function rollbackRevertWindow(storeId: string): Promise<{ result: RevertResult; status: RevertStatus }> {
  return send<{ result: RevertResult; status: RevertStatus }>("revert-rollback", { storeId });
}
