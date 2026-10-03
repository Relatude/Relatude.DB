import { send } from "./channel";

/** How the field is rendered, decided on the server from the setting's CLR type. */
export type SettingEditor = "text" | "number" | "integer" | "toggle" | "choice" | "color";

/** When a change starts to matter. Shown on the field so a save is never mistaken for an effect. */
export type SettingApplies = "live" | "reopen" | "restart";

export interface SettingChoice {
  value: string;
  label: string;
  hint?: string | null;
}

/**
 * A condition on a sibling field, holding while the sibling has one of these values - compared
 * without regard to case or surrounding spaces, "" standing for no value. Paths are full, already
 * prefixed.
 */
export interface SettingVisibility {
  path: string;
  values: string[];
  /** a further condition that must hold as well */
  and?: SettingVisibility | null;
}

/** Somewhere to read more about a setting, for a choice that needs more than its help text gives. */
export interface SettingLink {
  url: string;
  text: string;
}

export interface SettingView {
  path: string;
  label: string;
  /** The inline explanation of what this setting does. */
  help: string;
  /** A page worth reading before choosing, shown under the help text. */
  link?: SettingLink | null;
  unit?: string | null;
  placeholder?: string | null;
  /** Names a runtime list in `pickers` to choose from instead of typing a value. */
  picker?: string | null;
  /** Offers a button that fills the field with a freshly made value; "guid" is the one kind so far. */
  generate?: string | null;
  /** Shows the field only while this holds, for one whose relevance depends on another field. */
  visibleWhen?: SettingVisibility | null;
  /** Hides the field while this holds: a service URL the built-in provider does not need. */
  hiddenWhen?: SettingVisibility | null;
  /**
   * Only with `picker`: the list applies only while this holds, and the field is plain text
   * otherwise - the models the Relatude AI service publishes mean nothing to any other provider.
   */
  pickerWhen?: SettingVisibility | null;
  secret: boolean;
  readOnly: boolean;
  applies: SettingApplies;
  editor: SettingEditor;
  /** The value may be cleared; a required setting falls back to its zero value instead. */
  optional: boolean;
  choices?: SettingChoice[] | null;
  /**
   * The choices are suggestions, not the legal values: the field stays free text with the list one
   * click away. Set from `Suggestions` on the setting's catalog entry.
   */
  allowCustom: boolean;
  /** null for secrets, which are never sent back to the browser. */
  value: unknown;
  default: unknown;
  hasValue: boolean;
  isDefault: boolean;
  /** Decided by the configuration section, so it cannot be edited here. */
  overridden: boolean;
  configuredValue: unknown;
  /** Set by the application's code (OnServerSettingsInit and its kind) at every start: locked too. */
  codeSet: boolean;
  /** Saved in relatude.db.overrides.json - changed here - rather than coming from relatude.db.json. */
  inOverrides: boolean;
  /** What relatude.db.json gives the setting, when `inOverrides`; null for secrets. */
  fileValue: unknown;
  fileHasValue: boolean;
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
  /** added here: relatude.db.overrides.json holds it, relatude.db.json does not */
  addedHere: boolean;
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
  /** elements removed here that relatude.db.json still has, by name */
  removedHere: string[];
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
  /**
   * where the changes made here are saved; null when they go straight into relatude.db.json. `error`
   * says the file could not be read at start: nothing in it is in force and saves are refused.
   */
  overrides?: { file: string; count: number; error?: string | null } | null;
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

/**
 * The models the configured AI service publishes, for the two model fields in the AI group. Asked
 * for on its own rather than with the page: it follows the provider type as it is edited rather
 * than as it was saved, and a service that cannot be reached must not hold up the settings page.
 *
 * A provider type that publishes no list answers with two empty lists rather than an error, which
 * is what leaves those fields as the plain text boxes a vendor's own model name needs. `error` is
 * set when the service was asked and could not answer; the fields still work.
 */
export interface AiModelChoices {
  embeddings: SettingChoice[];
  completions: SettingChoice[];
  error?: string | null;
}

export function fetchAiModels(typeName: string, serviceUrl: string): Promise<AiModelChoices> {
  return send<AiModelChoices>("settings-ai-models", { typeName, serviceUrl });
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

// ---- relatude.db.overrides.json ----

/** One thing relatude.db.overrides.json changes: a setting, or a list element added or removed here. */
export interface OverrideEntry {
  path: string;
  kind: "value" | "added" | "removed";
  label: string;
  /** the section and group the setting is shown under */
  where?: string | null;
  secret: boolean;
  /** the value saved here; null for secrets and for elements */
  value: unknown;
  hasValue: boolean;
  /** what relatude.db.json has instead */
  fileValue: unknown;
  fileHasValue: boolean;
  summary?: string | null;
  canMove: boolean;
  /** why it cannot be moved: configuration decides it */
  moveBlocked?: string | null;
  canDiscard: boolean;
  discardBlocked?: string | null;
}

export interface OverrideGroup {
  scope: "server" | "database";
  storeId?: string | null;
  title: string;
  entries: OverrideEntry[];
}

export interface OverridesView {
  /** false when the server writes the admin UI's changes straight into relatude.db.json */
  enabled: boolean;
  file: string | null;
  /** the file could not be read when the server started: nothing in it is in force, nothing can be saved */
  error?: string | null;
  settingsFile: string;
  configSection?: string | null;
  count: number;
  groups: OverrideGroup[];
}

export function fetchOverrides(): Promise<OverridesView> {
  return send<OverridesView>("settings-overrides-get");
}

export interface MoveOverridesResult {
  moved: string[];
  overrides: OverridesView;
}

export interface DiscardOverridesResult {
  discarded: string[];
  rejected: { path: string; reason: string }[];
  overrides: OverridesView;
}

/** Moves entries into relatude.db.json; what the configuration section decides stays where it is. */
export function moveOverrides(paths: string[]): Promise<MoveOverridesResult> {
  return send<MoveOverridesResult>("settings-overrides-move", { paths });
}

/** Drops entries, putting back what relatude.db.json says. */
export function discardOverrides(paths: string[]): Promise<DiscardOverridesResult> {
  return send<DiscardOverridesResult>("settings-overrides-discard", { paths });
}
