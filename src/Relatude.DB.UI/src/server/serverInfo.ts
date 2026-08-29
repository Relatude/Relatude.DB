import { send } from "./channel";

export interface DatabaseInfo {
  id: string;
  name: string;
  state: string; // Closed | Open | Opening | Closing | Error | Disposed
  nodeCount?: number | null;
}

export interface ServerInfo {
  version: string;
  upTimeMs: number;
  containers: DatabaseInfo[];
}

export function fetchServerInfo(): Promise<ServerInfo> {
  return send<ServerInfo>("server-info");
}
