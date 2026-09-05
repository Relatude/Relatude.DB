import { send } from "./channel";

export interface OverviewContainer {
  id: string;
  name: string;
  state: string;
  nodeCount?: number | null;
  provider?: string | null;
}

export interface ServerOverview {
  serverName?: string | null;
  version: string;
  upTimeMs: number;
  machine: string;
  os: string;
  runtime: string;
  processorCount: number;
  processMemoryBytes: number;
  managedMemoryBytes: number;
  adminPath: string;
  settingsFile: string;
  defaultDatabase?: string | null;
  restart: { canSoftRestart: boolean; canStopHost: boolean };
  containers: OverviewContainer[];
  serverLog: { timeUtc: string; message: string }[];
  startupExceptions: { container: string; message: string; timeUtc?: string | null }[];
}

export function fetchServerOverview(): Promise<ServerOverview> {
  return send<ServerOverview>("server-overview");
}

/** One reading of the process: what the overview's memory and cpu graph is drawn from. */
export interface ServerLive {
  sampledUtc: string;
  managedMemory: number;
  processMemory: number;
  /** cumulative ms of cpu time, all cores together */
  processorTimeMs: number;
  processorCount: number;
}

export function fetchServerLive(): Promise<ServerLive> {
  return send<ServerLive>("server-live");
}

export interface ProcessActionResult {
  started: boolean;
  message: string;
}

export function collectGarbage(): Promise<ProcessActionResult> {
  return send<ProcessActionResult>("collect-garbage");
}

export function softRestart(): Promise<ProcessActionResult> {
  return send<ProcessActionResult>("soft-restart");
}

export function stopHost(): Promise<ProcessActionResult> {
  return send<ProcessActionResult>("stop-host");
}
