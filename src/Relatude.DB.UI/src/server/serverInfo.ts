import { send } from "./channel";

export interface DatabaseInfo {
  id: string;
  name: string;
  state: string; // Closed | Open | Opening | Closing | Error | Disposed
  nodeCount?: number | null;
  /** file conversions still owed: running plus queued. Rides the container broadcast, so the nav
      badge stays live without the Conversions page having to be open. */
  conversionCount?: number;
}

export interface ServerInfo {
  version: string;
  upTimeMs: number;
  containers: DatabaseInfo[];
}

export function fetchServerInfo(): Promise<ServerInfo> {
  return send<ServerInfo>("server-info");
}
