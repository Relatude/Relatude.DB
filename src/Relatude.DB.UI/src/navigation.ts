import type { ComponentType } from "react";
import {
  IconLayoutDashboard,
  IconSchema,
  IconDatabaseSearch,
  IconFolders,
  IconArchive,
  IconFileText,
  IconChecklist,
  IconApi,
  IconSettings,
  IconGauge,
  IconDatabase,
  IconAlertTriangle,
  IconAdjustments,
  IconLock,
  IconTransform,
} from "@tabler/icons-react";

export type SectionScope = "database" | "server";

export interface Section {
  id: string;
  label: string;
  scope: SectionScope;
  icon: ComponentType<{ size?: number; stroke?: number }>;
  /**
   * Renders the settings page of its scope, opened at this settings section. For entries that are a
   * name for part of the settings rather than a page of their own - Access is the security settings,
   * and building a second screen for them would only be a second place to keep them.
   */
  settingsSection?: string;
  /** Left out of the rail for now: the page is not ready to be shown. The entry stays so it keeps its id and place. */
  hidden?: boolean;
  /**
   * One view of another section's page rather than a page of its own. It keeps its id, label and
   * icon - so the global search finds it and opens it - but the rail shows only the parent, which
   * switches to this view when it is picked.
   */
  parentId?: string;
}

export const sections: Section[] = [
  { id: "dashboard", label: "Dashboard", scope: "database", icon: IconLayoutDashboard },
  { id: "datamodel", label: "Data model", scope: "database", icon: IconSchema },
  { id: "query", label: "Query & Edit", scope: "database", icon: IconDatabaseSearch },
  // one page in three views (FilesStorageSection): everything about what is on disk
  { id: "files", label: "Files & storage", scope: "database", icon: IconFolders },
  { id: "storage", label: "Storage", scope: "database", icon: IconArchive, parentId: "files" },
  { id: "conversions", label: "Conversions", scope: "database", icon: IconTransform, parentId: "files" },
  { id: "logs", label: "Logs", scope: "database", icon: IconFileText },
  { id: "tasks", label: "Tasks", scope: "database", icon: IconChecklist },
  { id: "api", label: "API", scope: "database", icon: IconApi },
  { id: "db-settings", label: "Settings", scope: "database", icon: IconSettings },
  { id: "server-overview", label: "Overview", scope: "server", icon: IconGauge },
  { id: "server-databases", label: "Databases", scope: "server", icon: IconDatabase },
  { id: "server-events", label: "Events & exceptions", scope: "server", icon: IconAlertTriangle, hidden: true },
  { id: "server-settings", label: "Settings", scope: "server", icon: IconAdjustments },
  { id: "server-access", label: "Access", scope: "server", icon: IconLock, settingsSection: "security", hidden: true },
];
