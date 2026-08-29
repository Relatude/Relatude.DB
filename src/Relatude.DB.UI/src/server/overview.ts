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
