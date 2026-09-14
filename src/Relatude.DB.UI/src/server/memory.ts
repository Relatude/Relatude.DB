// What a database is allowed to keep in memory, and what it holds right now (see UIMemory.cs).
// Every budget is a bound on one structure, not an allocation, so a budget is only readable next to
// the usage under it - which is why the two always travel together here.

import { send } from "./channel";

export type MemoryBudgetKind = "NodeCache" | "SetCache" | "ValueIndex" | "TextIndex" | "VectorIndex" | "StateStore";

export interface MemoryBudget {
  /** one budget of one engine; the slider is keyed on it */
  key: string;
  kind: MemoryBudgetKind;
  engineId: string;
  label: string;
  /** what is behind it: an engine type name, or "Memory" for something that is simply resident */
  engine: string;
  /** the bound in force right now, which a slider may already have changed */
  limitBytes: number;
  /** what it holds, or null when the component keeps no account of it */
  usedBytes: number | null;
  /** what it keeps whatever the budget says (a resident vector graph); 0 when everything is evictable */
  floorBytes: number;
  /** true when a new budget takes effect without reopening the database */
  adjustable: boolean;
  /** the setting behind it, or null for a component with no budget to set */
  settingPath: string | null;
  settingUnit: "GB" | "MB" | null;
  /** what the settings file says, which is what the database opens with */
  settingBytes: number;
  suggestedMaxBytes: number;
  help: string;
}

export interface MemoryReport {
  open: boolean;
  state: string;
  managedBytes: number;
  processBytes: number;
  budgets: MemoryBudget[];
}

export interface ApplyMemoryResult {
  applied: boolean;
  report: MemoryReport;
}

export function fetchMemory(storeId: string): Promise<MemoryReport> {
  return send<MemoryReport>("memory", { storeId });
}

export function applyMemoryBudget(storeId: string, kind: MemoryBudgetKind, engineId: string, bytes: number): Promise<ApplyMemoryResult> {
  return send<ApplyMemoryResult>("memory-apply", { storeId, kind, engineId, bytes });
}
