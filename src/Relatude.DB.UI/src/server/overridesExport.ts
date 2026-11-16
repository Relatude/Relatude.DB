import { adminBase } from "./base";
import { send } from "./channel";
import { saveBlob } from "./customLogs";

/** One thing this installation changed in the admin UI, as the overrides download lists it. */
export interface ExportEntry {
  /** what the download is asked for */
  id: string;
  label: string;
  where?: string | null;
  /** the application's file it goes into, relative to the application's folder */
  file: string;
  change: "changed" | "added" | "removed" | "same" | "file";
  /** what the application's file has, and would have, as text; secrets blanked out */
  before?: string | null;
  after?: string | null;
  secret: boolean;
  /** why it cannot be exported */
  blocked?: string | null;
}

export interface ExportGroup {
  kind: "settings" | "custom" | "datamodel" | "logs" | "graphql" | "types";
  title: string;
  storeId?: string | null;
  entries: ExportEntry[];
}

export interface ExportList {
  server: string;
  groups: ExportGroup[];
  /** what could not be listed, by what it is */
  problems: string[];
}

export function fetchOverridesExport(): Promise<ExportList> {
  return send<ExportList>("overrides-export-list");
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
