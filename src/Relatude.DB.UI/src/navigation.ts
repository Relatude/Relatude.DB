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
  IconCertificate,
} from "@tabler/icons-react";

export type SectionScope = "database" | "server";

/**
 * The colour a rail entry's icon is drawn in. A name rather than a value: the two themes need
 * different shades of every one of these, and only the stylesheet can hold both (see `--nav-*`
 * and the `.nav-tone-*` classes in app.css). "plain" is the page's own text colour, which reads as
 * white in the dark theme and as near-black in the light one - the same "no colour of its own".
 */
export type SectionTone = "plain" | "purple" | "blue-light" | "blue" | "blue-dark" | "red" | "orange" | "green" | "yellow" | "pink" | "gray";

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
  /** What colour its icon is in the rail; see SectionTone. Entries with no tone stay uncoloured. */
  tone?: SectionTone;
}

export const sections: Section[] = [
  { id: "dashboard", label: "Dashboard", scope: "database", icon: IconLayoutDashboard, tone: "plain" },
  { id: "datamodel", label: "Models", scope: "database", icon: IconSchema, tone: "purple" },
  { id: "query", label: "Query", scope: "database", icon: IconDatabaseSearch, tone: "blue-light" },
  // one page in three views (FilesStorageSection): everything about what is on disk. The entry is
  // the storage view; the other two are its views and are reached from the switch on the page
  { id: "storage", label: "Storage", scope: "database", icon: IconArchive, tone: "orange" },
  { id: "files", label: "Files", scope: "database", icon: IconFolders, parentId: "storage", tone: "orange" },
  { id: "conversions", label: "Conversions", scope: "database", icon: IconTransform, parentId: "storage", tone: "orange" },
  { id: "logs", label: "Logs", scope: "database", icon: IconFileText, tone: "green" },
  { id: "tasks", label: "Tasks", scope: "database", icon: IconChecklist, tone: "yellow" },
  { id: "api", label: "API", scope: "database", icon: IconApi, tone: "pink" },
  { id: "db-settings", label: "Settings", scope: "database", icon: IconSettings, tone: "blue-dark" },
  { id: "server-overview", label: "Overview", scope: "server", icon: IconGauge, tone: "plain" },
  { id: "server-databases", label: "Databases", scope: "server", icon: IconDatabase, tone: "blue" },
  { id: "server-events", label: "Events & exceptions", scope: "server", icon: IconAlertTriangle, hidden: true },
  // its own page rather than part of the settings: the keys are a small part of it, and most of the
  // page is explaining what a license is for to someone who has just found out it exists
  { id: "server-license", label: "License", scope: "server", icon: IconCertificate, tone: "green" },
  { id: "server-settings", label: "Server settings", scope: "server", icon: IconAdjustments, tone: "red" },
  { id: "server-access", label: "Access", scope: "server", icon: IconLock, settingsSection: "security", hidden: true },
];
