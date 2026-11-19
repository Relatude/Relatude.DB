import { send } from "./channel";

// The change history of a database: who changed what and when, read from the log by the history reader into a
// time index (see LogHistory in Relatude.DB.DataStoreLocal). The commands are UIHistory's.

export interface HistorySourceStatus {
  key: string;
  length: number;
  readTo: number;
  changes: number;
  links: number;
  firstUtc: string | null;
  lastUtc: string | null;
}

export interface HistoryStatus {
  supported: boolean;
  enabled: boolean;
  source: string | null;
  reachesBackToUtc: string | null;
  latestUtc: string | null;
  changes: number;
  links: number;
  indexBytes: number;
  building: boolean;
  percent: number;
  bytesBehind: number;
  lastRunUtc: string | null;
  problem: string | null;
  sources: HistorySourceStatus[];
}

export interface HistoryInfo {
  open: boolean;
  settings: { enabled: boolean; typesByDefault: boolean; buildSpeed: string; secondaryLog: boolean };
  status: HistoryStatus | null;
}

/** What a change did to one version; the engine's words, which the page puts into its own. */
export type ChangeKind =
  | "Created"
  | "Updated"
  | "Removed"
  | "Published"
  | "Unpublished"
  | "LanguageMoved"
  | "MarkedDeleted"
  | "Unmarked"
  | "MetaChanged"
  | "NodeCreated"
  | "NodeRemoved"
  | "System"
  | "Linked"
  | "Unlinked";

export interface HistoryChange {
  timestamp: number;
  utc: string;
  nodeId: string;
  typeId: string;
  typeName: string;
  revisionId: string;
  kinds: ChangeKind[];
  writer: string | null;
  writerName: string | null;
  culture: string | null;
  cultureName: string | null;
  collection: string | null;
  collectionName: string | null;
  typeAfter: string | null;
  typeBefore: string | null;
  inTransaction: number;
  name: string | null;
  versionKept: boolean;
  versions: number;
  others: number;
  changedProperties: { id: string; name: string | null }[] | null;
  typeCounts: { typeId: string; typeName: string; count: number }[] | null;
}

export interface HistoryLink {
  timestamp: number;
  utc: string;
  relationId: string;
  relationName: string;
  source: string;
  sourceName: string | null;
  sourceTypeName: string;
  target: string;
  targetName: string | null;
  targetTypeName: string;
  added: boolean;
  writer: string | null;
  writerName: string | null;
  inTransaction: number;
}

export interface HistoryPage {
  changes: HistoryChange[];
  links: HistoryLink[];
  next: string | null;
}

export interface HistoryQuery {
  fromUtc?: string | null;
  toUtc?: string | null;
  types?: string[] | null;
  writers?: string[] | null;
  kinds?: ChangeKind[] | null;
  node?: string | null;
  cursor?: string | null;
  take?: number;
  collapse?: boolean;
  includeSystem?: boolean;
  transaction?: number | null;
}

export interface HistoryCounts {
  changes: number;
  saves: number;
  people: number;
  nodes: number;
  links: number;
}

export interface HistoryActivity {
  interval: "Hour" | "Day" | "Week" | "Month";
  fromUtc: string;
  toUtc: string;
  by: string;
  totals: HistoryCounts;
  groups: { key: string; label: string; changes: number }[];
  buckets: { startUtc: string; counts: HistoryCounts; groups: Record<string, number> | null }[];
}

export interface HistoryType {
  id: string;
  name: string;
  fullName: string;
  own: boolean | null;
  inHistory: boolean;
  from: "code" | "override" | "inherited" | "default";
  fromType: string | null;
  isAbstract: boolean;
  parents: string[];
}

export function fetchHistoryInfo(storeId: string): Promise<HistoryInfo> {
  return send<HistoryInfo>("history-status", { storeId });
}

export function fetchHistoryChanges(storeId: string, query: HistoryQuery, signal?: AbortSignal): Promise<HistoryPage> {
  return send<HistoryPage>("history-changes", { storeId, ...query }, signal);
}

export function fetchHistoryActivity(
  storeId: string,
  query: { fromUtc: string; toUtc: string; interval: string; by: string; types?: string[] | null; writers?: string[] | null; kinds?: ChangeKind[] | null; includeSystem?: boolean },
  signal?: AbortSignal,
): Promise<HistoryActivity> {
  return send<HistoryActivity>("history-activity", { storeId, ...query }, signal);
}

export function fetchHistoryTypes(storeId: string): Promise<{ typesByDefault: boolean; types: HistoryType[] }> {
  return send("history-types", { storeId });
}

export function rebuildHistory(storeId: string): Promise<{ started: boolean }> {
  return send("history-rebuild", { storeId });
}
