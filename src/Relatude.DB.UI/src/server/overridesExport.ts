import { adminBase } from "./base";
import { send } from "./channel";
import { discardCustomLogData, moveCustomLogData, saveBlob } from "./customLogs";
import { moveOverridesToSettings, type OverridePath } from "./datamodel";
import { discardEndpointData, moveEndpointData } from "./graphql";
import { customOverridesApi, serverOverridesApi } from "./settings";

/**
 * One thing this installation changed in the admin UI, as the changes dialog lists it: what the download
 * puts into the application's files, and what can be moved into SHARED or discarded on THIS SERVER.
 */
export interface ExportEntry {
  /** what the download is asked for, and what the entry's own commands are told, taken apart by kind (see entryParts) */
  id: string;
  label: string;
  where?: string | null;
  /** what the tree lists it under within its group: a settings section, a type, a source */
  section?: string | null;
  /** the application's file it goes into, relative to the application's folder */
  file: string;
  change: "changed" | "added" | "removed" | "same" | "file";
  /** what the application's file has, and would have, as text; secrets blanked out */
  before?: string | null;
  after?: string | null;
  secret: boolean;
  /** why it cannot be exported */
  blocked?: string | null;
  /** why it cannot be moved into SHARED */
  moveBlocked?: string | null;
  /** why it cannot be discarded on THIS SERVER */
  discardBlocked?: string | null;
}

export type ExportKind = "settings" | "custom" | "datamodel" | "logs" | "graphql" | "types";

export interface ExportGroup {
  kind: ExportKind;
  title: string;
  /** the kind as the tree names it: "Settings", a custom set's title, "Logs"... */
  area: string;
  storeId?: string | null;
  /** the database's name; null for the server's own */
  database?: string | null;
  /** a custom set's file key */
  key?: string | null;
  /** where THIS SERVER keeps them, and where a move writes them */
  from?: string | null;
  into?: string | null;
  entries: ExportEntry[];
}

export interface ExportList {
  server: string;
  /** relatude.settings is most likely the one in source control; elsewhere it is the deployed copy */
  development: boolean;
  environment: string;
  /** the configuration section whose settings are never moved or exported */
  configSection?: string | null;
  groups: ExportGroup[];
  /** what could not be listed, by what it is */
  problems: string[];
}

export function fetchOverridesExport(): Promise<ExportList> {
  return send<ExportList>("overrides-export-list");
}

/** The parts of an entry's id after its kind, the last one whole: a settings path may hold anything. */
function entryParts(id: string, count: number): string[] {
  const parts = id.split("|");
  return [...parts.slice(1, count), parts.slice(count).join("|")];
}

/** What a datamodel entry's id says: the store, then the attribute's path. */
export function datamodelPathOf(id: string): OverridePath {
  const [, typeId, propertyId, attribute] = entryParts(id, 4);
  return { typeId, propertyId: propertyId === "-" ? null : propertyId, attribute };
}

/** What moving or discarding entries of one group did: how many, and those it left as they were, with why. */
export interface ChangeOutcome {
  done: number;
  rejected: { id: string; reason: string }[];
}

/**
 * Moves entries of one group into SHARED with the command of their kind - the same command the page of the
 * kind had its own dialog for. Runtime types are never moved: they are files of their own.
 */
export async function moveEntries(group: ExportGroup, ids: string[]): Promise<ChangeOutcome> {
  const storeId = group.storeId ?? "";
  switch (group.kind) {
    case "settings": {
      const result = await serverOverridesApi.move(ids.map((id) => entryParts(id, 1)[0]));
      return { done: result.moved.length, rejected: [] };
    }
    case "custom": {
      const result = await customOverridesApi(group.key ?? "", group.storeId ?? null).move(ids.map((id) => entryParts(id, 3)[2]));
      return { done: result.moved.length, rejected: [] };
    }
    case "datamodel":
      return { done: (await moveOverridesToSettings(storeId, ids.map(datamodelPathOf))).moved, rejected: [] };
    case "logs":
      return { done: (await moveCustomLogData(storeId, ids.map((id) => entryParts(id, 2)[1]))).moved, rejected: [] };
    case "graphql":
      return { done: (await moveEndpointData(storeId, ids.map((id) => entryParts(id, 2)[1]))).moved, rejected: [] };
    case "types":
      throw new Error("Runtime types are files of their own: download them instead.");
  }
}

/**
 * Discards entries of one group on THIS SERVER, back to what SHARED says, with the command of their kind.
 * Datamodel overrides are discarded in the draft of the model, which only the data model editor holds: the
 * dialog asks the editor (see changes.ts), never this.
 */
export async function discardEntries(group: ExportGroup, ids: string[]): Promise<ChangeOutcome> {
  const storeId = group.storeId ?? "";
  const rejectedOf = (result: { rejected: { path: string; reason: string }[] }, pathOf: (id: string) => string) =>
    result.rejected.map((r) => ({ id: ids.find((id) => pathOf(id) === r.path) ?? r.path, reason: r.reason }));
  switch (group.kind) {
    case "settings": {
      const pathOf = (id: string) => entryParts(id, 1)[0];
      const result = await serverOverridesApi.discard(ids.map(pathOf));
      return { done: result.discarded.length, rejected: rejectedOf(result, pathOf) };
    }
    case "custom": {
      const pathOf = (id: string) => entryParts(id, 3)[2];
      const result = await customOverridesApi(group.key ?? "", group.storeId ?? null).discard(ids.map(pathOf));
      return { done: result.discarded.length, rejected: rejectedOf(result, pathOf) };
    }
    case "logs":
      return { done: (await discardCustomLogData(storeId, ids.map((id) => entryParts(id, 2)[1]))).discarded.length, rejected: [] };
    case "graphql":
      return { done: (await discardEndpointData(storeId, ids.map((id) => entryParts(id, 2)[1]))).discarded, rejected: [] };
    case "datamodel":
      throw new Error("Datamodel overrides are discarded in the data model editor, as it changes the draft.");
    case "types":
      throw new Error("Runtime types are changed and deleted in the data model editor.");
  }
}

/** The zip of the application's files with the chosen entries moved in, saved by the browser. */
export async function downloadOverridesExport(ids: string[]): Promise<void> {
  const response = await fetch(`${adminBase}/ui/overrides-export`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ ids }),
  });
  if (!response.ok) {
    let message = `The download failed (HTTP ${response.status}).`;
    try {
      const body = await response.json();
      if (typeof body?.error === "string") message = body.error;
    } catch {
      // not json, keep the default message
    }
    throw new Error(message);
  }
  const match = response.headers.get("content-disposition")?.match(/filename="([^"]+)"/);
  saveBlob(await response.blob(), match ? match[1] : "relatude-changes.zip");
}
