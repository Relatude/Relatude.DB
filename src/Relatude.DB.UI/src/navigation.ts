import type { ComponentType } from "react";
import {
  IconLayoutDashboard,
  IconSchema,
  IconDatabaseSearch,
  IconFolders,
  IconArchive,
  IconFileText,
  IconLanguage,
  IconFileAnalytics,
  IconChecklist,
  IconCalendarRepeat,
  IconApi,
  IconSettings,
  IconGauge,
  IconDatabase,
  IconAlertTriangle,
  IconAdjustments,
  IconLock,
  IconTransform,
  IconMessage,
  IconSparkles,
  IconPhoto,
  IconFlask,
  IconWorldSearch,
} from "@tabler/icons-react";
import { LogoMarkIcon } from "./components/Logo";
import { customSettingsIcon } from "./components/customSettingsIcons";
import type { CustomSettingsTab } from "./server/serverInfo";

/** Which group of the rail an entry is in: the database's pages, the server's, or Relatude Services'. */
export type SectionScope = "database" | "server" | "services";

/**
 * The colour a rail entry's icon is drawn in. A name rather than a value: the two themes need
 * different shades of every one of these, and only the stylesheet can hold both (see `--nav-*`
 * and the `.nav-tone-*` classes in app.css). "plain" is the page's own text colour, which reads as
 * white in the dark theme and as near-black in the light one - the same "no colour of its own".
 */
export type SectionTone =
  | "plain"
  | "purple"
  | "blue-light"
  | "blue"
  | "blue-dark"
  | "brown"
  | "orange"
  | "green-dark"
  | "green"
  | "green-light"
  | "yellow"
  | "pink"
  | "gray";

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
  /** The tab of a set of settings the application registers, by its file key: see setCustomSettingsSections. */
  customKey?: string;
}

export const sections: Section[] = [
  { id: "dashboard", label: "Dashboard", scope: "database", icon: IconLayoutDashboard, tone: "plain" },
  { id: "datamodel", label: "Datamodel", scope: "database", icon: IconSchema, tone: "purple" },
  { id: "query", label: "Query", scope: "database", icon: IconDatabaseSearch, tone: "blue-light" },
  // What the database records about itself. It and the logs defined for it (Logs, below) are two
  // shades of one green, so they read as related wherever they stand. The ids stay what they were
  // when these were "System logs" and "Custom logs", so every hand-off still finds them
  { id: "logs", label: "Activity", scope: "database", icon: IconFileText, tone: "green-dark" },
  // one page in three views (FilesStorageSection): everything about what is on disk. The entry is
  // the storage view; the other two are its views and are reached from the switch on the page
  { id: "storage", label: "Storage", scope: "database", icon: IconArchive, tone: "orange" },
  { id: "files", label: "Files", scope: "database", icon: IconFolders, parentId: "storage", tone: "orange" },
  { id: "conversions", label: "Conversions", scope: "database", icon: IconTransform, parentId: "storage", tone: "orange" },
  { id: "custom-logs", label: "Logs", scope: "database", icon: IconFileAnalytics, tone: "green-light" },
  // one page in two views (TasksModule): the queue, which is the entry, and the scheduler that feeds it
  { id: "tasks", label: "Tasks", scope: "database", icon: IconChecklist, tone: "yellow" },
  { id: "scheduler", label: "Scheduler", scope: "database", icon: IconCalendarRepeat, parentId: "tasks", tone: "yellow" },
  { id: "api", label: "API", scope: "database", icon: IconApi, tone: "pink" },
  { id: "db-settings", label: "Settings", scope: "database", icon: IconSettings, tone: "blue-dark" },
  { id: "server-overview", label: "Overview", scope: "server", icon: IconGauge, tone: "plain" },
  { id: "server-databases", label: "Databases", scope: "server", icon: IconDatabase, tone: "blue" },
  { id: "server-events", label: "Events & exceptions", scope: "server", icon: IconAlertTriangle, hidden: true },
  { id: "server-settings", label: "Server settings", scope: "server", icon: IconAdjustments, tone: "brown" },
  { id: "server-access", label: "Access", scope: "server", icon: IconLock, settingsSection: "security", hidden: true },
  // Relatude Services, a group of its own: the account this installation runs under (LicenseSection,
  // its own page rather than part of the settings - most of it explains what a license is for to
  // someone who has just found out it exists), and a test of each service. The id is older than the
  // group, from when the account page was a server page called License.
  { id: "server-license", label: "Account", scope: "services", icon: LogoMarkIcon, tone: "plain" },
  // one page in six views (ServiceTestsSection), one per service. The entry is not a view itself: it
  // opens the test that was open last, or the first (App's landing views)
  { id: "services-tests", label: "Test services", scope: "services", icon: IconFlask, tone: "plain" },
  { id: "services-sms", label: "SMS test", scope: "services", icon: IconMessage, parentId: "services-tests", tone: "plain" },
  { id: "services-ai", label: "AI test", scope: "services", icon: IconSparkles, parentId: "services-tests", tone: "plain" },
  { id: "services-imaging", label: "Imaging test", scope: "services", icon: IconPhoto, parentId: "services-tests", tone: "plain" },
  { id: "services-filetotext", label: "File to text test", scope: "services", icon: IconFileText, parentId: "services-tests", tone: "plain" },
  { id: "services-translation", label: "Translation test", scope: "services", icon: IconLanguage, parentId: "services-tests", tone: "plain" },
  { id: "services-iplookup", label: "IP lookup test", scope: "services", icon: IconWorldSearch, parentId: "services-tests", tone: "plain" },
];

/** The id of the rail entry of a set of custom settings. */
export const customSettingsSectionId = (key: string): string => "custom-settings:" + key;

/**
 * Puts the tabs of the settings the application registers (ICustomSettingsDefinition on the server) into
 * the rail: a set of the server's right after Server settings, a set per database right after the
 * database's Settings, in the order the application registered them. The list is known once the server
 * has said what it has, so the entries are added to `sections` then - and replaced, not added again, when
 * it says so again after a restart. Everything that reads `sections` finds them: the rail, the header,
 * the global search.
 */
export function setCustomSettingsSections(tabs: CustomSettingsTab[]): void {
  for (let i = sections.length - 1; i >= 0; i--) if (sections[i].customKey) sections.splice(i, 1);
  for (const scope of ["database", "server"] as const) {
    const after = sections.findIndex((s) => s.id === (scope === "database" ? "db-settings" : "server-settings"));
    const entries: Section[] = tabs
      .filter((t) => t.scope === scope)
      .map((t) => ({
        id: customSettingsSectionId(t.key),
        label: t.title,
        scope,
        icon: customSettingsIcon(t.icon),
        customKey: t.key,
        // the colour of the settings they sit beside, so they read as more of them
        tone: scope === "database" ? "blue-dark" : "brown",
      }));
    sections.splice(after + 1, 0, ...entries);
  }
}
