import type { ComponentType } from "react";
import { IconArrowsExchange, IconBolt, IconDatabaseSearch, IconFileText, IconGauge, IconServerCog, IconStack2, IconSubtask } from "@tabler/icons-react";

export type LogIconType = ComponentType<{ size?: number; stroke?: number; className?: string }>;

/**
 * The picture one of the database's own logs is known by - in the Activity page's tabs and table,
 * and in the Logs page when it shows these logs beside the ones defined there. The keys are the
 * server's (StoreLogger), and a log it grows that is not on this list gets the generic picture -
 * naming every log here would make adding one on the server a change in two places, which is the
 * one thing those pages are built not to be.
 */
const logIcons: Record<string, LogIconType> = {
  system: IconServerCog,
  query: IconDatabaseSearch,
  transaction: IconArrowsExchange,
  action: IconBolt,
  task: IconSubtask,
  taskbatch: IconStack2,
  metrics: IconGauge,
};

export const logIcon = (key: string): LogIconType => logIcons[key] ?? IconFileText;
