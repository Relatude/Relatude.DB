import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type ComponentType,
} from "react";
import {
  IconArchive,
  IconArrowBackUp,
  IconChevronDown,
  IconChevronRight,
  IconCode,
  IconDatabase,
  IconExternalLink,
  IconFileDiff,
  IconFileText,
  IconFolders,
  IconGauge,
  IconLock,
  IconMessage,
  IconPlus,
  IconRefresh,
  IconReload,
  IconRestore,
  IconSchema,
  IconSearch,
  IconServer,
  IconSettings,
  IconShieldLock,
  IconSparkles,
  IconStethoscope,
  IconWand,
  IconTrash,
  IconX,
} from "@tabler/icons-react";
import { ColorField } from "./ColorField";
import { Combo, type PickerLoader } from "./Combo";
import { sourceColor } from "../server/datamodel";
import { showConfirm, showError } from "../dialogs";
import { peekSearchTarget, peekSettingsTarget, takeSearchTarget, takeSettingsTarget, useNavigationRequest } from "../navigate";
import { closeStore, openStore } from "../server/storage";
import {
  addListItem,
  fetchAiModels,
  fetchDatabaseSettings,
  fetchServerSettings,
  removeListItem,
  saveDatabaseSettings,
  saveServerSettings,
  type SettingChoice,
  type SettingList,
  type SettingListItem,
  type SettingValues,
  type SettingView,
  type SettingVisibility,
  type SettingsPage,
} from "../server/settings";
import { Loading } from "./Loading";
import { OverridesDialog } from "./OverridesDialog";

// Where a programmatic scroll leaves a group's heading, and the line at which the contents counts a
// heading as passed. The line sits a hair below the landing point, so a group that was scrolled to
// exactly counts as the one being read rather than falling a fraction of a pixel short of its own
// highlight.
const scrollOffset = 12;
const spyLine = scrollOffset + 2;

/**
 * A runtime list fetched when its drop-down is opened rather than with the page: the models the
 * configured AI service publishes. The page supplies one per field it applies to, and none for a
 * field whose `pickerWhen` does not hold, which leaves that field the plain text box a vendor's own
 * model name needs. A context rather than a prop, so the fields deep in a list or a row reach it
 * without every component between them carrying it.
 */
const LazyPickers = createContext<(setting: SettingView) => PickerLoader | undefined>(() => undefined);

/**
 * The settings pages, server scope and database scope alike. The server sends the whole page -
 * sections, groups, fields, editors, choices, defaults - so this file renders settings without
 * knowing what any individual one is; adding a setting is a backend change only.
 *
 * The layout follows the shape the settings themselves have: a table of contents on the left for
 * the sections and their groups, one scrolling pane on the right, and a search that narrows both at
 * once. Scrolling the pane moves the highlight in the contents, so the two never disagree.
 *
 * Edits are kept locally until saved, and only the changed paths are posted, so two people editing
 * different settings do not overwrite each other. Three states are marked on every field, since a
 * value alone does not tell you what to do with it: whether it still holds its default, whether it
 * has an unsaved edit, and whether configuration - appsettings.json, environment variables, user
 * secrets - decides it, in which case editing here is pointless and the field is locked. The same
 * lock applies to a setting the application's own code sets at every start.
 *
 * A save goes to relatude.db.overrides.json, not relatude.db.json: the server keeps what is changed
 * here apart from the application's own file and merges it over that file at every start. A field
 * saved there says so, with the value relatude.db.json has and a button that puts it back; the
 * Overrides button in the toolbar lists everything in the file and moves it into relatude.db.json.
 */
export function SettingsSection({
  storeId,
  focusSection,
}: {
  /** absent for the server scope */
  storeId?: string;
  /** opens the page at this section, for nav entries that name part of the settings (Access) */
  focusSection?: string;
}) {
  const [page, setPage] = useState<SettingsPage | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [edits, setEdits] = useState<SettingValues>({});
  const [message, setMessage] = useState<string | null>(null);
  const [filter, setFilter] = useState("");
  const [onlyChanged, setOnlyChanged] = useState(false);
  const [showComments, setShowComments] = useState(readShowComments);
  const [reopen, setReopen] = useState(false);
  const [saving, setSaving] = useState(false);
  const [restarting, setRestarting] = useState(false);
  const [showOverrides, setShowOverrides] = useState(false);
  const [activeGroup, setActiveGroup] = useState<string | null>(null);
  const pane = useRef<HTMLDivElement>(null);
  const groupElements = useRef(new Map<string, HTMLElement>());

  const load = useCallback(() => {
    const request = storeId ? fetchDatabaseSettings(storeId) : fetchServerSettings();
    request
      .then((p) => {
        setPage(p);
        setError(null);
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, [storeId]);
  useEffect(() => {
    setEdits({});
    setMessage(null);
    setFilter("");
    load();
  }, [load]);

  const all = useMemo(
    () =>
      (page?.sections ?? [])
        .flatMap((s) => s.groups)
        .flatMap((g) => [...g.settings, ...(g.list?.items ?? []).flatMap((i) => i.settings)]),
    [page],
  );
  const byPath = useMemo(() => new Map(all.map((s) => [s.path, s])), [all]);

  // The two model fields in the AI group offer what the configured AI service publishes, asked for
  // each time one of their drop-downs opens, so the list is the service's current one. They follow
  // the provider type and service url as they are edited rather than as they were saved: choosing
  // the Relatude service turns both into drop-downs without saving first. For any other provider
  // pickerWhen does not hold and they stay plain text boxes.
  const currentValue = useCallback((path: string) => edits[path] ?? byPath.get(path)?.value, [edits, byPath]);
  const aiProviderType = asText(currentValue("AISettings.TypeName"));
  const aiServiceUrl = asText(currentValue("AISettings.ServiceUrl"));
  const lazyPicker = useCallback(
    (setting: SettingView): PickerLoader | undefined => {
      if (!setting.picker || !setting.pickerWhen || !holds(setting.pickerWhen, currentValue)) return undefined;
      const kind = setting.picker === "aiEmbeddingModels" ? "embeddings" : setting.picker === "aiCompletionModels" ? "completions" : undefined;
      if (!kind) return undefined;
      return () =>
        fetchAiModels(aiProviderType, aiServiceUrl).then((models) => {
          // the fields work without the list; the failure is said in the list rather than instead of it
          if (models.error) throw new Error(models.error);
          return models[kind];
        });
    },
    [currentValue, aiProviderType, aiServiceUrl],
  );

  const pickers = useMemo(() => page?.pickers ?? {}, [page]);

  const editedPaths = Object.keys(edits);
  // a number field left blank has no value to post, and a required one would silently become zero
  const invalid = editedPaths.filter((path) => {
    const setting = byPath.get(path);
    if (!setting || (setting.editor !== "number" && setting.editor !== "integer")) return false;
    const value = edits[path];
    if (value === null || value === "") return !setting.optional;
    return Number.isNaN(Number(value));
  });
  const needsReopen = editedPaths.some((path) => byPath.get(path)?.applies === "reopen");

  const needle = filter.trim().toLowerCase();
  const sections = useMemo(() => {
    if (!page) return [];
    // a group setting shown or hidden by another one follows that one as edited, not as saved, so
    // choosing a provider type shows its fields at once; a list element's fields are sorted out by
    // the list editor, which knows their siblings
    const valueOf = (path: string): unknown => (edits[path] !== undefined ? edits[path] : byPath.get(path)?.value);
    const keep = (s: SettingView, context: string) => {
      // a read-only setting has no default to differ from, so it is not "changed" either
      if (onlyChanged && (s.readOnly || s.isDefault) && edits[s.path] === undefined) return false;
      if (!needle) return true;
      return (s.label + " " + s.help + " " + s.path + " " + context).toLowerCase().includes(needle);
    };
    return page.sections
      .map((section) => ({
        ...section,
        groups: section.groups
          .map((group) => {
            const context = group.title + " " + section.title;
            const settings = group.settings.filter((s) => visible(s, valueOf) && keep(s, context));
            const list = group.list
              ? {
                  ...group.list,
                  items: group.list.items
                    .map((item) => ({ ...item, settings: item.settings.filter((s) => keep(s, context)) }))
                    .filter((item) => item.settings.length > 0),
                }
              : null;
            // a list whose own name matches is kept whole, empty included, so it can still be added to
            const named = needle.length > 0 && context.toLowerCase().includes(needle);
            const keepEmptyList = group.list != null && !onlyChanged && (needle.length === 0 || named);
            return { ...group, settings, list: keepEmptyList ? group.list : list };
          })
          .filter((group) => group.settings.length > 0 || (group.list?.items.length ?? 0) > 0 || (group.list != null && !onlyChanged && !needle)),
      }))
      .filter((section) => section.groups.length > 0);
  }, [page, byPath, needle, onlyChanged, edits]);

  // the highlight in the contents follows the pane: the active group is the last one whose heading
  // has passed the top of the pane
  const syncActive = useCallback(() => {
    const container = pane.current;
    if (!container) return;
    const top = container.getBoundingClientRect().top + spyLine;
    let active: string | null = null;
    for (const [key, element] of groupElements.current) {
      if (element.getBoundingClientRect().top <= top) active = key;
    }
    // before the first heading has scrolled past, the first group is the one being read
    setActiveGroup(active ?? groupElements.current.keys().next().value ?? null);
  }, []);
  useLayoutEffect(syncActive, [syncActive, sections]);

  // a nav entry that names one section opens there, once the page it is in has arrived
  const focused = useRef(false);
  useEffect(() => {
    focused.current = false;
  }, [focusSection, storeId]);
  useLayoutEffect(() => {
    if (!focusSection || focused.current || sections.length === 0) return;
    const section = sections.find((s) => s.id === focusSection);
    if (!section) return;
    focused.current = true;
    scrollToGroup(section.id + "/" + section.groups[0].id);
  });

  // Someone asked for one particular setting - from the global search - and the shell has switched
  // to this page. The group holding it is scrolled to and the setting itself is marked for a moment:
  // a settings page is a long scroll of fields, and landing near the right one is not the same as
  // being shown it. Taken once, on the render after the page arrives, so a remount does not repeat it.
  const [marked, setMarked] = useState<string | null>(null);
  const navigation = useNavigationRequest();
  // words handed over from the global search box: they go in the page's own search, which narrows
  // every section at once rather than offering the handful the box had room for
  useEffect(() => {
    if (peekSearchTarget()?.section !== "settings") return;
    setFilter(takeSearchTarget()!.text);
    setOnlyChanged(false);
  }, [navigation]);
  useEffect(() => {
    if (!page) return;
    const target = peekSettingsTarget();
    if (!target) return;
    // the other scope's page is a different mount of this component; it takes its own target
    if ((target.scope === "database") !== (storeId != null)) return;
    takeSettingsTarget();
    setFilter("");
    setOnlyChanged(false);
    setMarked(target.path ?? null);
    // after the render that clearing the filter causes, so the group is in the dom to scroll to
    requestAnimationFrame(() => scrollToGroup(target.sectionId + "/" + target.groupId));

    // eslint-disable-next-line react-hooks/exhaustive-deps -- the request is taken, not watched
  }, [navigation, page]);
  useEffect(() => {
    if (!marked) return;
    const timer = setTimeout(() => setMarked(null), 2600);
    return () => clearTimeout(timer);
  }, [marked]);

  function scrollToGroup(key: string): void {
    const container = pane.current;
    const element = groupElements.current.get(key);
    if (!container || !element) return;
    container.scrollTop += element.getBoundingClientRect().top - container.getBoundingClientRect().top - scrollOffset;
    setActiveGroup(key);
    // The scroll event this causes runs the scroll-spy, which can land mid-render - the group refs
    // are re-attached on every render - and leave the contents highlighting a group nobody is
    // looking at. One more pass once everything has settled, so the highlight matches the pane.
    requestAnimationFrame(syncActive);
  }

  function revert(path: string): void {
    setEdits((prev) => {
      const next = { ...prev };
      delete next[path];
      return next;
    });
  }

  function setValue(path: string, value: unknown): void {
    setMessage(null);
    setEdits((prev) => {
      const next = { ...prev };
      const setting = byPath.get(path);
      // typing the stored value back is not an edit - secrets excepted, since their stored value is unknown here
      if (setting && !setting.secret && sameValue(value, setting.value)) delete next[path];
      else next[path] = value;
      return next;
    });
  }

  async function save(): Promise<void> {
    if (!page || editedPaths.length === 0 || invalid.length > 0) return;
    setSaving(true);
    try {
      const result = storeId ? await saveDatabaseSettings(storeId, edits, reopen && needsReopen) : await saveServerSettings(edits);
      setPage(result.settings);
      setEdits({});
      setReopen(false);
      if (result.rejected.length > 0) {
        await showError(
          "Some settings were not saved",
          `${result.changed.length} saved, ${result.rejected.length} refused.`,
          result.rejected.map((r) => `${byPath.get(r.path)?.label ?? r.path}: ${r.reason}`),
        );
      }
      setMessage(
        describeSave(result.changed.length, result.reopened, result.changed.map((p) => byPath.get(p)).filter(Boolean) as SettingView[], result.settings.overrides?.file ?? null),
      );
    } catch (e) {
      await showError("Could not save", e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }

  /**
   * Closes the database and opens it again. Reading the settings file is part of opening one, so
   * this is how a setting marked "needs reopen" takes effect without saving anything - the one the
   * savebar's tick does for settings being saved here, and the only way to apply a change made to
   * relatude.db.json outside this page. It takes the database away for the duration, so it is asked
   * about first, and the page is read back afterwards: what "in force" means has just changed.
   */
  async function restart(): Promise<void> {
    if (!storeId) return;
    const choice = await showConfirm(
      "Restart the database?",
      "It is closed and opened again, which is what makes the settings and the model sources be read afresh."
        + " Every index is flushed and the log is replayed - which takes a while on a large database - and nothing is served from it in between.",
      { confirmLabel: "Restart" },
    );
    if (!choice.ok) return;
    setRestarting(true);
    try {
      await closeStore(storeId);
      await openStore(storeId);
      setMessage("The database was closed and opened again.");
      load();
    } catch (e) {
      await showError("Could not restart the database", e instanceof Error ? e.message : String(e));
      load(); // the state it ended up in is worth showing, whichever half failed
    } finally {
      setRestarting(false);
    }
  }

  // adding and removing write straight through, so the page comes back fresh; pending field edits
  // are kept, except any that belonged to an element that has just gone
  async function changeList(run: () => Promise<{ settings: SettingsPage }>, removedPrefix?: string): Promise<void> {
    try {
      const result = await run();
      setPage(result.settings);
      setMessage(null);
      if (removedPrefix) {
        setEdits((prev) => Object.fromEntries(Object.entries(prev).filter(([path]) => !path.startsWith(removedPrefix))));
      }
    } catch (e) {
      await showError("Could not change the list", e instanceof Error ? e.message : String(e));
    }
  }

  if (error) return <div className="placeholder">{error}</div>;
  if (!page) return <Loading label="Loading the settings…" />;

  const editsBySection = new Map<string, number>();
  for (const section of page.sections) {
    const count = section.groups.flatMap((g) => g.settings).filter((s) => edits[s.path] !== undefined).length;
    if (count > 0) editsBySection.set(section.id, count);
  }

  return (
    <LazyPickers.Provider value={lazyPicker}>
    <div className="settings">
      <div className="settings-toolbar">
        <div className="settings-search">
          <IconSearch size={15} stroke={1.8} />
          <input
            className="text-input"
            placeholder="Search settings"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            spellCheck={false}
          />
          {filter && (
            <button className="icon-button" title="Clear" onClick={() => setFilter("")}>
              <IconX size={15} stroke={1.8} />
            </button>
          )}
        </div>
        <label className="settings-check">
          <input type="checkbox" checked={onlyChanged} onChange={(e) => setOnlyChanged(e.target.checked)} />
          Only settings that differ from their default
        </label>
        <label className="settings-check" title="The line under each setting saying what it does. Turning it off fits far more settings on the screen.">
          <input
            type="checkbox"
            checked={showComments}
            onChange={(e) => {
              setShowComments(e.target.checked);
              writeShowComments(e.target.checked);
            }}
          />
          Show comments
        </label>
        <div className="settings-toolbar-end">
        <span className="muted settings-source" title={page.configSection ? `Any of these can be overridden from the ${page.configSection} configuration section` : undefined}>
          {page.settingsFile}
          {page.configSection ? ` · ${page.configSection} section` : ""}
        </span>
        {page.overrides && (
          <button
            className="icon-button labelled"
            title={
              page.overrides.error
                ? `The overrides file could not be read: ${page.overrides.error}`
                : `Saving here writes to ${page.overrides.file}, merged over ${page.settingsFile} at every start. List what it holds, and move it into ${page.settingsFile}.`
            }
            onClick={() => setShowOverrides(true)}
          >
            <IconFileDiff size={15} stroke={1.8} className={page.overrides.error ? "tone-danger" : "tone-override"} />
            Overrides{page.overrides.error ? " · not read" : page.overrides.count > 0 ? ` · ${page.overrides.count}` : ""}
          </button>
        )}
        {page.scope === "database" && (
          <button
            className="icon-button labelled"
            title={
              !page.isOpen
                ? "The database is closed, so there is nothing to restart"
                : editedPaths.length > 0
                  ? "Save or discard the changes first — a restart does not apply them"
                  : "Close the database and open it again, so the settings are read afresh"
            }
            onClick={restart}
            disabled={restarting || !page.isOpen || editedPaths.length > 0}
          >
            <IconReload size={15} stroke={1.8} className="tone-data" />
            {restarting ? "Restarting…" : "Restart"}
          </button>
        )}
        <button className="icon-button" title="Reload" onClick={load} disabled={editedPaths.length > 0}>
          <IconRefresh size={16} stroke={1.8} />
        </button>
        </div>
      </div>
      {message && <div className="settings-message">{message}</div>}
      <div className="settings-body">
        <nav className="settings-toc">
          {sections.map((section) => {
            const Icon = sectionIcon(section.icon);
            const firstKey = section.id + "/" + section.groups[0].id;
            const inSection = activeGroup?.startsWith(section.id + "/") ?? false;
            return (
              <div className="toc-section" key={section.id}>
                <button className={"toc-row toc-head" + (inSection ? " active" : "")} onClick={() => scrollToGroup(firstKey)}>
                  <Icon size={16} stroke={1.8} />
                  <span className="toc-label">{section.title}</span>
                  {editsBySection.has(section.id) && <span className="toc-dot" title="unsaved changes in this section" />}
                </button>
                {section.groups.map((group) => {
                  const key = section.id + "/" + group.id;
                  return (
                    <button key={key} className={"toc-row toc-child" + (activeGroup === key ? " active" : "")} onClick={() => scrollToGroup(key)}>
                      <span className="toc-label">{group.title}</span>
                    </button>
                  );
                })}
              </div>
            );
          })}
        </nav>
        <div className="settings-pane" ref={pane} onScroll={syncActive}>
          {sections.length === 0 && <div className="placeholder">No setting matches “{filter}”.</div>}
          {sections.map((section) => {
            const Icon = sectionIcon(section.icon);
            return (
              <div className="settings-section" key={section.id}>
                <h2 className="settings-section-head">
                  <Icon size={18} stroke={1.7} />
                  {section.title}
                </h2>
                {section.groups.map((group) => (
                  <section
                    className="panel"
                    key={group.id}
                    ref={(element) => {
                      const key = section.id + "/" + group.id;
                      if (element) groupElements.current.set(key, element);
                      else groupElements.current.delete(key);
                    }}
                  >
                    <h3>{group.title}</h3>
                    {showComments && group.help && <p className="settings-group-help">{group.help}</p>}
                    {group.list && storeId && (
                      <ListEditor
                        list={group.list}
                        pickers={pickers}
                        edits={edits}
                        onChange={setValue}
                        onRevert={revert}
                        showComments={showComments}
                        onAdd={() => changeList(() => addListItem(storeId, group.list!.path))}
                        onRemove={(id) => changeList(() => removeListItem(storeId, group.list!.path, id), group.list!.path + "[" + id + "].")}
                      />
                    )}
                    <div className="settings-list">
                      {group.settings.map((setting) => (
                        <SettingRow
                          key={setting.path}
                          setting={setting}
                          pickers={pickers}
                          edit={edits[setting.path]}
                          edited={edits[setting.path] !== undefined}
                          showComment={showComments}
                          marked={setting.path === marked}
                          onChange={(value) => setValue(setting.path, value)}
                          onRevert={() => revert(setting.path)}
                        />
                      ))}
                    </div>
                  </section>
                ))}
              </div>
            );
          })}
        </div>
      </div>
      {showOverrides && (
        <OverridesDialog
          onClose={() => setShowOverrides(false)}
          // moving changes nothing that is running, but discarding does, and either changes the marks
          onChanged={load}
        />
      )}
      {editedPaths.length > 0 && (
        <div className="settings-savebar">
          <span>
            {editedPaths.length} unsaved {editedPaths.length === 1 ? "change" : "changes"}
            {invalid.length > 0 && <span className="settings-invalid"> · {invalid.length} needs a value</span>}
          </span>
          {page.scope === "database" && needsReopen && page.isOpen && (
            <label className="settings-check">
              <input type="checkbox" checked={reopen} onChange={(e) => setReopen(e.target.checked)} />
              Close and reopen the database so the changes take effect now
            </label>
          )}
          <span className="header-spacer" />
          <button className="action-button" onClick={() => setEdits({})} disabled={saving}>
            Discard
          </button>
          <button className="action-button primary" onClick={save} disabled={saving || invalid.length > 0}>
            {saving ? "Saving…" : "Save"}
          </button>
        </div>
      )}
    </div>
    </LazyPickers.Provider>
  );
}

// the catalog names what a section is about; picking the glyph stays a UI decision, and an
// unrecognized name still gets an icon rather than a hole in the column
const sectionIcons: Record<string, ComponentType<{ size?: number; stroke?: number }>> = {
  server: IconServer,
  security: IconShieldLock,
  database: IconDatabase,
  model: IconSchema,
  storage: IconFolders,
  content: IconFileText,
  performance: IconGauge,
  search: IconSparkles,
  messaging: IconMessage,
  maintenance: IconArchive,
  diagnostics: IconStethoscope,
};

function sectionIcon(name: string): ComponentType<{ size?: number; stroke?: number }> {
  return sectionIcons[name] ?? IconSettings;
}

/**
 * A collection of settings objects - the storage providers, the file stores - as a stack of cards.
 * An element's fields are ordinary settings on paths that address it by id, so everything below the
 * card header is the same rendering as anywhere else on the page, unsaved marks and all.
 *
 * Adding and removing are not staged the way field edits are: the shape of the collection is written
 * through immediately, because a half-added element is not a thing the settings file should hold.
 */
// A reading preference rather than a setting of the server: it belongs to whoever is looking at the
// page, so it is kept in the browser and never travels with relatude.db.json. Comments are on unless
// they have been turned off - someone who has never seen this page needs them most.
const showCommentsKey = "settingsShowComments";

function readShowComments(): boolean {
  try {
    return localStorage.getItem(showCommentsKey) !== "false";
  } catch {
    return true; // storage unavailable
  }
}

function writeShowComments(value: boolean): void {
  try {
    localStorage.setItem(showCommentsKey, String(value));
  } catch {
    // storage unavailable, the choice just won't outlive the tab
  }
}

function ListEditor({
  list,
  pickers,
  edits,
  showComments,
  onChange,
  onRevert,
  onAdd,
  onRemove,
}: {
  list: SettingList;
  pickers: Record<string, SettingChoice[] | undefined>;
  edits: SettingValues;
  showComments: boolean;
  onChange: (path: string, value: unknown) => void;
  onRevert: (path: string) => void;
  onAdd: () => void;
  onRemove: (id: string) => void;
}) {
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({});
  const [busy, setBusy] = useState(false);

  const valueOf = (path: string): unknown => {
    if (edits[path] !== undefined) return edits[path];
    for (const item of list.items) {
      const setting = item.settings.find((s) => s.path === path);
      if (setting) return setting.value;
    }
    return undefined;
  };

  async function confirmRemove(item: SettingListItem): Promise<void> {
    const label = itemLabel(list, item, pickers, valueOf);
    const result = await showConfirm(
      `Remove ${label}?`,
      [`This removes the ${list.itemName} from the settings file. Nothing already written to it is deleted.`, item.removeWarning ?? ""]
        .filter(Boolean)
        .join(" "),
      { confirmLabel: "Remove", danger: true },
    );
    if (!result.ok) return;
    setBusy(true);
    try {
      onRemove(item.id);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="setting-items">
      {list.items.length === 0 && <div className="setting-items-empty">{list.emptyHelp}</div>}
      {list.items.map((item, itemIndex) => {
        const isCollapsed = collapsed[item.id] ?? false;
        return (
          <div className="setting-item" key={item.id}>
            <div className="setting-item-head">
              <button className="setting-item-toggle" onClick={() => setCollapsed({ ...collapsed, [item.id]: !isCollapsed })}>
                {isCollapsed ? <IconChevronRight size={15} stroke={1.8} /> : <IconChevronDown size={15} stroke={1.8} />}
                <span className="setting-item-name">{itemLabel(list, item, pickers, valueOf)}</span>
              </button>
              {item.addedHere && (
                <span className="setting-badge overrides" title="Added here: relatude.db.overrides.json holds it, relatude.db.json does not">
                  added here
                </span>
              )}
              {item.usedBy.length > 0 && <span className="setting-item-usage">used as {item.usedBy.join(", ")}</span>}
              <span className="header-spacer" />
              <button
                className="icon-button danger"
                disabled={busy || !item.removable}
                title={
                  list.locked
                    ? "This list comes from configuration"
                    : item.removable
                      ? `Remove this ${list.itemName}`
                      : `Still used as ${item.blocking.join(", ")}`
                }
                onClick={() => confirmRemove(item)}
              >
                <IconTrash size={15} stroke={1.8} />
              </button>
            </div>
            {!isCollapsed && (
              <div className="settings-list">
                {item.settings
                  .filter((setting) => visible(setting, valueOf))
                  .map((setting, index) => (
                    <SettingRow
                      key={setting.path + "#" + index}
                      setting={setting}
                      pickers={pickers}
                      edit={edits[setting.path]}
                      edited={edits[setting.path] !== undefined}
                      showComment={showComments}
                      onChange={(value) => onChange(setting.path, value)}
                      onRevert={() => onRevert(setting.path)}
                      // an element that names no colour is marked with the palette's, by its place in
                      // the list - the same rule the pages showing it follow (see sourceColors)
                      fallbackColor={sourceColor(itemIndex)}
                    />
                  ))}
              </div>
            )}
          </div>
        );
      })}
      {list.removedHere.length > 0 && (
        <div className="setting-items-removed">
          Removed here, still in relatude.db.json: {list.removedHere.join(", ")}. The Overrides button moves the removal there, or takes it back.
        </div>
      )}
      <button className="action-button" disabled={busy || list.locked} onClick={onAdd} title={list.locked ? "This list comes from configuration" : undefined}>
        <IconPlus size={15} stroke={1.8} />
        Add {list.itemName}
      </button>
    </div>
  );
}

// a field that only applies to some kinds of element is hidden for the rest, so an Azure container
// never sits under a local folder, and the built-in SMS service shows no URL or key it does not use
function visible(setting: SettingView, valueOf: (path: string) => unknown): boolean {
  return holds(setting.visibleWhen, valueOf) && !(setting.hiddenWhen && holds(setting.hiddenWhen, valueOf));
}
function holds(rule: SettingVisibility | null | undefined, valueOf: (path: string) => unknown): boolean {
  if (!rule) return true;
  const current = String(valueOf(rule.path) ?? "").trim().toLowerCase();
  return rule.values.some((v) => current === v.trim().toLowerCase()) && holds(rule.and, valueOf);
}

// the card header follows the field the catalog nominated, live, so renaming a provider renames its
// card as you type; a picker field shows the option's label rather than its id
function itemLabel(list: SettingList, item: SettingListItem, pickers: Record<string, SettingChoice[] | undefined>, valueOf: (path: string) => unknown): string {
  const field = item.settings.find((s) => s.path.endsWith("]." + list.labelField));
  const raw = field ? asText(valueOf(field.path)) : "";
  if (field?.picker) {
    const option = (pickers[field.picker] ?? []).find((o) => o.value.toLowerCase() === raw.toLowerCase());
    if (option) return option.label;
  }
  return raw || `${list.itemName} ${item.id.slice(0, 8)}`;
}

function SettingRow({
  setting,
  pickers,
  edit,
  edited,
  showComment,
  marked,
  onChange,
  onRevert,
  fallbackColor,
}: {
  setting: SettingView;
  pickers: Record<string, SettingChoice[] | undefined>;
  edit: unknown;
  edited: boolean;
  showComment: boolean;
  /** someone was sent to this setting from somewhere else: it says so for a moment */
  marked?: boolean;
  onChange: (value: unknown) => void;
  onRevert: () => void;
  /** for a colour field: what the value in force is while nothing is set */
  fallbackColor?: string;
}) {
  const value = edited ? edit : setting.value;
  const locked = setting.overridden || setting.codeSet || setting.readOnly;
  // the value relatude.db.json has can be put back like any edit - not for a secret, whose value the
  // page never sees; the Overrides list discards those
  const canRestoreFile = setting.inOverrides && !setting.secret && !locked && !edited;
  // emptying a secret field is the only way to remove a stored secret, so say so before it is saved
  const clearsSecret = setting.secret && edited && (edit === "" || edit === null);
  return (
    <div className={"setting" + (edited ? " edited" : "") + (locked ? " locked" : "") + (marked ? " marked" : "")}>
      <div className="setting-text">
        <div className="setting-label">
          <span>{setting.label}</span>
          <Badges setting={setting} edited={edited} clearsSecret={clearsSecret} />
        </div>
        {showComment && setting.help && <div className="setting-help">{setting.help}</div>}
        {/* the link stays whether or not the help text is showing: it is the answer to the question
            the field asks, not a footnote to the sentence above it */}
        {setting.link && (
          <a className="setting-link" href={setting.link.url} target="_blank" rel="noreferrer" title={setting.link.url + " — opens in a new tab"}>
            {setting.link.text}
            <IconExternalLink size={12} stroke={1.8} />
          </a>
        )}
        {setting.overridden && (
          <div className="setting-override">
            <IconLock size={13} stroke={1.8} />
            <span>
              Set by configuration
              {setting.configuredValue !== null && setting.configuredValue !== undefined ? (
                <>
                  {" to "}
                  <code>{display(setting.configuredValue)}</code>
                </>
              ) : (
                ""
              )}
              . The value below is what is running; editing it here would be undone at the next start.
            </span>
          </div>
        )}
        {setting.codeSet && (
          <div className="setting-override">
            <IconCode size={13} stroke={1.8} />
            <span>
              Set by the application's code at every start (OnServerSettingsInit, OnContainerSettingsInit or OnStoreSettingsInit). The value below is
              what is running; editing it here would be undone at the next start, so it is never saved to the settings files.
            </span>
          </div>
        )}
      </div>
      <div className="setting-control">
        <Editor setting={setting} value={value} pickers={pickers} disabled={locked} onChange={onChange} fallbackColor={fallbackColor} />
        {setting.unit && <span className="setting-unit">{setting.unit}</span>}
        {/* a random value is made here and only filled in: it is saved with the rest, and undone like any edit */}
        {!locked && setting.generate === "guid" && (
          <button className="icon-button" title="Generate a new random value" onClick={() => onChange(newGuid())}>
            <IconWand size={15} stroke={1.8} />
          </button>
        )}
        {canRestoreFile && (
          <button
            className="icon-button"
            title={`Use the value relatude.db.json has: ${setting.fileHasValue ? display(setting.fileValue) : "not set"}. Saving then removes it from the overrides file.`}
            onClick={() => onChange(setting.fileValue ?? "")}
          >
            <IconRestore size={15} stroke={1.8} className="tone-override" />
          </button>
        )}
        {edited ? (
          <button className="icon-button" title="Undo this change" onClick={onRevert}>
            <IconArrowBackUp size={15} stroke={1.8} />
          </button>
        ) : (
          !locked &&
          !setting.isDefault && (
            <button className="icon-button" title="Reset to the default value" onClick={() => onChange(setting.default ?? "")}>
              <IconRefresh size={15} stroke={1.8} />
            </button>
          )
        )}
      </div>
    </div>
  );
}

function Badges({ setting, edited, clearsSecret }: { setting: SettingView; edited: boolean; clearsSecret: boolean }) {
  return (
    <>
      {edited && <span className="setting-badge unsaved">unsaved</span>}
      {setting.overridden && <span className="setting-badge config">from configuration</span>}
      {setting.codeSet && <span className="setting-badge config">from code</span>}
      {setting.readOnly && !setting.overridden && <span className="setting-badge">read only</span>}
      {/* "from configuration" already says the value is not this server's own, so default-vs-custom would only add noise */}
      {!setting.readOnly && !setting.overridden && !setting.codeSet && (setting.isDefault ? <span className="setting-badge faint">default</span> : <span className="setting-badge custom">custom</span>)}
      {setting.inOverrides && (
        <span
          className="setting-badge overrides"
          title={`Saved in relatude.db.overrides.json, merged over relatude.db.json at every start. relatude.db.json has: ${
            setting.secret ? (setting.fileHasValue ? "a secret" : "nothing") : setting.fileHasValue ? display(setting.fileValue) : "not set"
          }`}
        >
          in overrides
        </span>
      )}
      {/* what it takes to apply is only worth saying for a setting that can actually be changed here */}
      {!setting.readOnly && setting.applies === "reopen" && <span className="setting-badge applies">needs reopen</span>}
      {!setting.readOnly && setting.applies === "restart" && <span className="setting-badge applies">needs restart</span>}
      {clearsSecret && <span className="setting-badge config">will be cleared</span>}
      {setting.secret && !clearsSecret && <span className="setting-badge faint">{setting.hasValue ? "secret set" : "not set"}</span>}
    </>
  );
}

function Editor({
  setting,
  value,
  pickers,
  disabled,
  onChange,
  fallbackColor,
}: {
  setting: SettingView;
  value: unknown;
  pickers: Record<string, SettingChoice[] | undefined>;
  disabled: boolean;
  onChange: (value: unknown) => void;
  fallbackColor?: string;
}) {
  const listId = useRef("dl-" + setting.path.replace(/\W/g, "-")).current;
  const load = useContext(LazyPickers)(setting);
  if (setting.editor === "color") {
    return <ColorField value={asText(value) || null} fallback={fallbackColor} disabled={disabled} onChange={(v) => onChange(v ?? "")} />;
  }
  if (setting.editor === "toggle") {
    return (
      <label className="setting-toggle">
        <input type="checkbox" checked={value === true} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
        <span>{value === true ? "On" : "Off"}</span>
      </label>
    );
  }
  // a list fetched when the drop-down opens: the field is a combo from the start, since what it
  // will offer is not known until it is asked
  if (load && setting.allowCustom) {
    return <Combo label={setting.label} placeholder={setting.placeholder} options={[]} load={load} value={value} disabled={disabled} onChange={onChange} />;
  }
  const listed = setting.choices ?? (setting.picker ? pickers[setting.picker] : undefined);
  // Suggestions rather than choices: the known values are one click away, but the field is still
  // free text, so a value the server has never heard of can be typed in. A runtime list that came
  // back empty - an AI provider that publishes no model names - is no list at all, and falls
  // through to the plain text field the value needs anyway rather than to an empty combo box.
  if (listed && setting.allowCustom) {
    if (listed.length > 0) {
      return <Combo label={setting.label} placeholder={setting.placeholder} options={listed} value={value} disabled={disabled} onChange={onChange} />;
    }
  }
  // a closed list: the value has to be one of these, so an empty one still shows as such
  const options = setting.allowCustom ? undefined : listed;
  // a long list (cultures) is a type-ahead field, a short one a plain drop-down
  if (options && options.length > 40) {
    return (
      <>
        <input
          className="text-input"
          list={listId}
          value={asText(value)}
          placeholder={setting.placeholder ?? ""}
          disabled={disabled}
          spellCheck={false}
          onChange={(e) => onChange(e.target.value)}
        />
        <datalist id={listId}>
          {options.map((o) => (
            <option key={o.value} value={o.value} label={o.hint ?? o.label} />
          ))}
        </datalist>
      </>
    );
  }
  if (options) {
    const current = asText(value);
    const known = options.some((o) => o.value.toLowerCase() === current.toLowerCase());
    return (
      <select className="select" value={known ? current : ""} disabled={disabled} onChange={(e) => onChange(e.target.value)}>
        {(setting.optional || !known) && <option value="">{known ? (setting.placeholder ?? "— none —") : current || (setting.placeholder ?? "— none —")}</option>}
        {options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
            {o.hint && o.hint !== o.label ? ` — ${o.hint}` : ""}
          </option>
        ))}
      </select>
    );
  }
  if (setting.secret) {
    return (
      <input
        className="text-input"
        type="password"
        autoComplete="new-password"
        value={asText(value)}
        placeholder={setting.hasValue ? "•••••••• (unchanged)" : (setting.placeholder ?? "not set")}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value)}
      />
    );
  }
  if (setting.editor === "number" || setting.editor === "integer") {
    return (
      <input
        className="text-input number"
        type="number"
        step={setting.editor === "integer" ? 1 : "any"}
        value={asText(value)}
        placeholder={setting.placeholder ?? ""}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value)}
      />
    );
  }
  return (
    <input
      className="text-input"
      value={asText(value)}
      placeholder={setting.placeholder ?? ""}
      disabled={disabled}
      spellCheck={false}
      onChange={(e) => onChange(e.target.value)}
    />
  );
}

// crypto.randomUUID needs a secure context; a plain http admin host on the LAN is not one, so fall
// back to the same shape built from getRandomValues, which is available everywhere
function newGuid(): string {
  if (typeof crypto.randomUUID === "function") return crypto.randomUUID();
  const b = crypto.getRandomValues(new Uint8Array(16));
  b[6] = (b[6] & 0x0f) | 0x40;
  b[8] = (b[8] & 0x3f) | 0x80;
  const hex = Array.from(b, (x) => x.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

function asText(value: unknown): string {
  return value === null || value === undefined ? "" : String(value);
}

function display(value: unknown): string {
  return typeof value === "string" ? value : JSON.stringify(value);
}

// the posted value is a string for every text and number field, so compare loosely: "70" typed
// back into a field holding 70 is not a change
function sameValue(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (a === null || a === undefined || a === "") return b === null || b === undefined || b === "";
  if (b === null || b === undefined) return false;
  return String(a) === String(b);
}

function describeSave(changed: number, reopened: boolean, settings: SettingView[], overridesFile: string | null): string {
  if (changed === 0) return "Nothing changed.";
  const saved = `${changed} ${changed === 1 ? "setting" : "settings"} saved${overridesFile ? ` to ${overridesFile}` : ""}.`;
  if (reopened) return saved + " The database was closed and reopened.";
  if (settings.some((s) => s.applies === "restart")) return saved + " Some of them only take effect after the host restarts.";
  if (settings.some((s) => s.applies === "reopen")) return saved + " Some of them only take effect when the database is next opened.";
  return saved;
}
