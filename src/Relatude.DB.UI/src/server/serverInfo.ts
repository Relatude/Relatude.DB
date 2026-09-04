import { send } from "./channel";

export interface DatabaseInfo {
  id: string;
  name: string;
  state: string; // Closed | Open | Opening | Closing | Error | Disposed
  nodeCount?: number | null;
  /** file conversions still owed: running plus queued. Rides the container broadcast, so the nav
      badge stays live without the Conversions page having to be open. */
  conversionCount?: number;
  /** background tasks still owed: pending plus running, across both queues. Rides the same broadcast. */
  taskCount?: number;
  /**
   * The open revert window, if one is - begun here, from code or from the CLI alike. Rides the
   * broadcast so every page can say so the moment it happens (see server/revert.ts).
   */
  revertWindow?: { timestamp: string; begunUtc: string } | null;
}

export interface ServerInfo {
  version: string;
  upTimeMs: number;
  containers: DatabaseInfo[];
}

export function fetchServerInfo(): Promise<ServerInfo> {
  return send<ServerInfo>("server-info");
}

/**
 * Who is looking at the admin UI. Two ways in, and they are not the same thing: a token is a session
 * that can be ended, while the localhost bypass (NoLoginRequiredForLocalhost) is not one - there is
 * nothing to log out of, so the UI must not offer it.
 */
export interface WhoAmI {
  userName: string | null;
  viaLocalhost: boolean;
  canLogOut: boolean;
  machine: string;
}

export function fetchWhoAmI(): Promise<WhoAmI> {
  return send<WhoAmI>("whoami");
}
