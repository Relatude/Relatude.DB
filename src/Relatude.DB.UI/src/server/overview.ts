import { send } from "./channel";

export interface OverviewContainer {
  id: string;
  name: string;
  state: string;
  nodeCount?: number | null;
  provider?: string | null;
}

/** One drive the server has folders on, and what puts it there. */
export interface DriveInfo {
  name: string;
  label?: string | null;
  format?: string | null;
  totalBytes: number;
  freeBytes: number;
  /** what of the server lives on it: "website", "temp", or a database name */
  uses: string[];
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
  /** the process itself: what it is, since when, and how the runtime is set up */
  processId: number;
  processName: string;
  processStartedUtc?: string | null;
  threadCount?: number | null;
  processArchitecture: string;
  osArchitecture: string;
  serverGC: boolean;
  gcMode: string;
  /** what the runtime believes it may grow to; a container limit shows up here and nowhere else */
  memoryLimitBytes: number;
  environment?: string | null;
  workingFolder: string;
  tempFolder?: string | null;
  utcOffsetMinutes: number;
  timeZone: string;
  serverTimeUtc: string;
  disks: DriveInfo[];
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
  /** the drive the databases are written to; 0 when the server has no local folder to report on */
  diskTotalBytes: number;
  diskFreeBytes: number;
  diskName?: string | null;
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
