import { send } from "./channel";

/** How the field is rendered, decided on the server from the setting's CLR type. */
export type SettingEditor = "text" | "number" | "integer" | "toggle" | "choice";

/** When a change starts to matter. Shown on the field so a save is never mistaken for an effect. */
export type SettingApplies = "live" | "reopen" | "restart";

export interface SettingChoice {
  value: string;
  label: string;
  hint?: string | null;
}

/** Hides a field until a sibling holds one of these values. Paths are full, already prefixed. */
export interface SettingVisibility {
  path: string;
  values: string[];
}

export interface SettingView {
  path: string;
  label: string;
  /** The inline explanation of what this setting does. */
  help: string;
  unit?: string | null;
  placeholder?: string | null;
  /** Names a runtime list in `pickers` to choose from instead of typing a value. */
  picker?: string | null;
  /** Set on the fields of a list element whose relevance depends on another field. */
  visibleWhen?: SettingVisibility | null;
  secret: boolean;
  readOnly: boolean;
  applies: SettingApplies;
  editor: SettingEditor;
  /** The value may be cleared; a required setting falls back to its zero value instead. */
  optional: boolean;
  choices?: SettingChoice[] | null;
  /** null for secrets, which are never sent back to the browser. */
  value: unknown;
  default: unknown;
  hasValue: boolean;
  isDefault: boolean;
  /** Decided by the configuration section, so it cannot be edited here. */
  overridden: boolean;
  configuredValue: unknown;
}

/** One element of an editable collection: a storage provider, a file store. */
export interface SettingListItem {
  id: string;
  /** the element's own fields, on paths that address it by id */
  settings: SettingView[];
  /** what points at this element, in plain words */
  usedBy: string[];
  removable: boolean;
  /** the uses that stand in the way of removing it */
  blocking: string[];
  /** what removing it costs beyond the settings file, when that is not visible from here */
  removeWarning?: string | null;
}

export interface SettingList {
  path: string;
  /** what one element is called: "storage provider" */
  itemName: string;
  /** the field naming an element in its header */
  labelField: string;
  emptyHelp: string;
  /** configuration supplied part of this list, so it cannot be edited here */
  locked: boolean;
  items: SettingListItem[];
}

export interface SettingGroup {
  id: string;
  title: string;
  help?: string | null;
  settings: SettingView[];
  /** set when the group edits a collection rather than a fixed set of settings */
  list?: SettingList | null;
}

/** A top level entry in the settings navigation. `icon` is a name the UI maps to a glyph. */
export interface SettingSection {
  id: string;
  title: string;
  icon: string;
  groups: SettingGroup[];
}

export interface SettingsPage {
  scope: "server" | "database";
  storeId?: string;
  title: string;
  /** database scope only */
  state?: string;
  isOpen?: boolean;
  settingsFile: string;
  /** the configuration section that may override these settings, when one is configured */
  configSection?: string | null;
  sections: SettingSection[];
  pickers: Record<string, SettingChoice[] | undefined>;
}

export interface SettingsSaveResult {
  changed: string[];
  rejected: { path: string; reason: string }[];
  reopened: boolean;
  settings: SettingsPage;
}

export type SettingValues = Record<string, unknown>;

export function fetchServerSettings(): Promise<SettingsPage> {
  return send<SettingsPage>("settings-server-get");
}

export function saveServerSettings(values: SettingValues): Promise<SettingsSaveResult> {
  return send<SettingsSaveResult>("settings-server-save", { values });
}

export function fetchDatabaseSettings(storeId: string): Promise<SettingsPage> {
  return send<SettingsPage>("settings-db-get", { storeId });
}

export function saveDatabaseSettings(storeId: string, values: SettingValues, reopen: boolean): Promise<SettingsSaveResult> {
  return send<SettingsSaveResult>("settings-db-save", { storeId, values, reopen });
}

/** Adding and removing write straight through: a collection's shape is not something to stage. */
export interface ListChangeResult {
  added?: string;
  removed?: string;
  rejected?: { path: string; reason: string }[];
  settings: SettingsPage;
}

export function addListItem(storeId: string, path: string, values?: SettingValues): Promise<ListChangeResult> {
  return send<ListChangeResult>("settings-db-list-add", { storeId, path, values: values ?? null });
}

export function removeListItem(storeId: string, path: string, id: string): Promise<ListChangeResult> {
  return send<ListChangeResult>("settings-db-list-remove", { storeId, path, id });
}
