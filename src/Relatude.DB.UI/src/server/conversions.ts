import { send } from "./channel";

/** Mirrors ConversionStatus: what one conversion is doing right now. */
export type ConversionStatus = "Queued" | "Running" | "Completed" | "Failed" | "Canceled";

export interface FileConversion {
  id: string;
  fileName: string;
  /** source and target formats, e.g. "Png" to "Webp" */
  from: string;
  to: string;
  fromType: string;
  toType: string;
  /** the property the file belongs to, when the datamodel still has it */
  property?: string | null;
  status: ConversionStatus;
  progressPercentage: number;
  created: string;
  started?: string | null;
  ended?: string | null;
  processedMs?: number | null;
  description?: string | null;
}

export interface ConversionsInfo {
  /** conversions only exist while the database is open */
  open: boolean;
  running: number;
  queued: number;
  completed: number;
  failed: number;
  canceled: number;
  /** what is running and queued, plus a short tail of ones that have finished */
  current: FileConversion[];
}

export function fetchConversions(storeId: string): Promise<ConversionsInfo> {
  return send<ConversionsInfo>("conversions", { storeId });
}

/**
 * Stops one conversion. `permanently` also records the failure against the file, so the next request
 * for it does not start the work over again.
 */
export function cancelConversion(storeId: string, id: string, permanently: boolean): Promise<{ cancelled: boolean }> {
  return send<{ cancelled: boolean }>("conversion-cancel", { storeId, id, permanently });
}
