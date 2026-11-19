import type { ReactNode } from "react";
import { SourceTag } from "./SourceTag";

// The helpers the changes dialog (ChangesDialog) and the pages share for what THIS SERVER holds: one side of a
// value, a count in words and the deployed-copy warning.
// The move-to-shared dialogs that were here, one per page, are the changes dialog now.

/** One side of an entry's values: what SHARED, or THIS SERVER, has. */
export function DataValue({ side, children }: { side: "settings" | "data"; children: ReactNode }) {
  return (
    <span className="overrides-value">
      <SourceTag kind={side} small title={side === "settings" ? "What SHARED has: relatude.settings, part of the application" : "What THIS SERVER has: this installation's data folder"} />{" "}
      <code>{children}</code>
    </span>
  );
}

export function count(n: number, one: string, many?: string): string {
  return `${n} ${n === 1 ? one : (many ?? one + "s")}`;
}

/** The warning the changes dialog gives outside the Development environment. */
export function deployedCopyWarning(environment: string, file: string, download: boolean): string {
  return `This server runs in the ${environment} environment. Here ${file} is most likely the deployed copy of the application, which the next deployment replaces, taking whatever was moved into it along.`
    + (download ? " Download the file with the selection moved in instead, and put it into source control." : " Make the same change in source control instead.");
}
