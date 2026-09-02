import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type ComponentType,
  type KeyboardEvent as ReactKeyboardEvent,
} from "react";
import {
  IconArchive,
  IconArrowBackUp,
  IconChevronDown,
  IconChevronRight,
  IconDatabase,
  IconFileText,
  IconFolders,
  IconGauge,
  IconLock,
  IconPlus,
  IconRefresh,
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
import { showConfirm, showError } from "../dialogs";
import {
  addListItem,
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
  type SettingsPage,
} from "../server/settings";

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
 * secrets - decides it, in which case editing here is pointless and the field is locked.
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
            const settings = group.settings.filter((s) => keep(s, context));
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
  }, [page, needle, onlyChanged, edits]);

  // the highlight in the contents follows the pane: the active group is the last one whose heading
  // has passed the top of the pane
  const syncActive = useCallback(() => {
    const container = pane.current;
    if (!container) return;
    const top = container.getBoundingClientRect().top + 12;
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

  function scrollToGroup(key: string): void {
    const container = pane.current;
    const element = groupElements.current.get(key);
    if (!container || !element) return;
    container.scrollTop += element.getBoundingClientRect().top - container.getBoundingClientRect().top - 12;
    setActiveGroup(key);
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
      setMessage(describeSave(result.changed.length, result.reopened, result.changed.map((p) => byPath.get(p)).filter(Boolean) as SettingView[]));
    } catch (e) {
      await showError("Could not save", e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
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
  if (!page) return null;

  const editsBySection = new Map<string, number>();
  for (const section of page.sections) {
    const count = section.groups.flatMap((g) => g.settings).filter((s) => edits[s.path] !== undefined).length;
    if (count > 0) editsBySection.set(section.id, count);
  }

  return (
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
        <span className="header-spacer" />
        <span className="muted settings-source" title={page.configSection ? `Any of these can be overridden from the ${page.configSection} configuration section` : undefined}>
          {page.settingsFile}
          {page.configSection ? ` · ${page.configSection} section` : ""}
        </span>
        <button className="icon-button" title="Reload" onClick={load} disabled={editedPaths.length > 0}>
          <IconRefresh size={16} stroke={1.8} />
        </button>
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
                        pickers={page.pickers}
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
                          pickers={page.pickers}
                          edit={edits[setting.path]}
                          edited={edits[setting.path] !== undefined}
                          showComment={showComments}
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
      {list.items.map((item) => {
        const isCollapsed = collapsed[item.id] ?? false;
        return (
          <div className="setting-item" key={item.id}>
            <div className="setting-item-head">
              <button className="setting-item-toggle" onClick={() => setCollapsed({ ...collapsed, [item.id]: !isCollapsed })}>
                {isCollapsed ? <IconChevronRight size={15} stroke={1.8} /> : <IconChevronDown size={15} stroke={1.8} />}
                <span className="setting-item-name">{itemLabel(list, item, pickers, valueOf)}</span>
              </button>
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
                    />
                  ))}
              </div>
            )}
          </div>
        );
      })}
      <button className="action-button" disabled={busy || list.locked} onClick={onAdd} title={list.locked ? "This list comes from configuration" : undefined}>
        <IconPlus size={15} stroke={1.8} />
        Add {list.itemName}
      </button>
    </div>
  );
}

// a field that only applies to some kinds of element is hidden for the rest, so an Azure container
// never sits under a local folder
function visible(setting: SettingView, valueOf: (path: string) => unknown): boolean {
  if (!setting.visibleWhen) return true;
  const current = valueOf(setting.visibleWhen.path);
  return setting.visibleWhen.values.some((v) => String(current ?? "").toLowerCase() === v.toLowerCase());
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
  onChange,
  onRevert,
}: {
  setting: SettingView;
  pickers: Record<string, SettingChoice[] | undefined>;
  edit: unknown;
  edited: boolean;
  showComment: boolean;
  onChange: (value: unknown) => void;
  onRevert: () => void;
}) {
  const value = edited ? edit : setting.value;
  const locked = setting.overridden || setting.readOnly;
  // emptying a secret field is the only way to remove a stored secret, so say so before it is saved
  const clearsSecret = setting.secret && edited && (edit === "" || edit === null);
  return (
    <div className={"setting" + (edited ? " edited" : "") + (locked ? " locked" : "")}>
      <div className="setting-text">
        <div className="setting-label">
          <span>{setting.label}</span>
          <Badges setting={setting} edited={edited} clearsSecret={clearsSecret} />
        </div>
        {showComment && setting.help && <div className="setting-help">{setting.help}</div>}
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
      </div>
      <div className="setting-control">
        <Editor setting={setting} value={value} pickers={pickers} disabled={locked} onChange={onChange} />
        {setting.unit && <span className="setting-unit">{setting.unit}</span>}
        {/* a random value is made here and only filled in: it is saved with the rest, and undone like any edit */}
        {!locked && setting.generate === "guid" && (
          <button className="icon-button" title="Generate a new random value" onClick={() => onChange(newGuid())}>
            <IconWand size={15} stroke={1.8} />
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
      {setting.readOnly && !setting.overridden && <span className="setting-badge">read only</span>}
      {/* "from configuration" already says the value is not this server's own, so default-vs-custom would only add noise */}
      {!setting.readOnly && !setting.overridden && (setting.isDefault ? <span className="setting-badge faint">default</span> : <span className="setting-badge custom">custom</span>)}
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
}: {
  setting: SettingView;
  value: unknown;
  pickers: Record<string, SettingChoice[] | undefined>;
  disabled: boolean;
  onChange: (value: unknown) => void;
}) {
  const listId = useRef("dl-" + setting.path.replace(/\W/g, "-")).current;
  if (setting.editor === "toggle") {
    return (
      <label className="setting-toggle">
        <input type="checkbox" checked={value === true} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
        <span>{value === true ? "On" : "Off"}</span>
      </label>
    );
  }
  const options = setting.choices ?? (setting.picker ? pickers[setting.picker] : undefined);
  // suggestions rather than choices: the known values are one click away, but the field is still
  // free text, so a value the server has never heard of can be typed in
  if (options && setting.allowCustom) {
    return <Combo setting={setting} options={options} value={value} disabled={disabled} onChange={onChange} />;
  }
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

/**
 * A text field that knows the values it usually holds: typing works exactly as it did before, and
 * the arrow opens the known ones. It is not a drop-down with an "other..." entry, because the
 * setting genuinely is free text - the list is a shortcut and a spelling reference, so nothing here
 * ever refuses a value or rewrites one.
 *
 * Typing narrows the list to what matches, and a value that matches nothing simply leaves it empty
 * rather than closing the list on a keystroke; the arrow always shows everything.
 */
function Combo({
  setting,
  options,
  value,
  disabled,
  onChange,
}: {
  setting: SettingView;
  options: SettingChoice[];
  value: unknown;
  disabled: boolean;
  onChange: (value: unknown) => void;
}) {
  const [open, setOpen] = useState(false);
  // set while typing, so the list narrows to what is being typed but reopens whole from the arrow
  const [filtering, setFiltering] = useState(false);
  const [active, setActive] = useState(-1);
  const input = useRef<HTMLInputElement>(null);
  const current = asText(value);
  const matches =
    filtering && current ? options.filter((o) => o.value.toLowerCase().includes(current.toLowerCase())) : options;

  // a list with nothing in it is not shown at all, so "open" on its own is not the state anything
  // else should key off: a typed value matching no suggestion must still open the whole list
  const visible = open && matches.length > 0;
  const show = (filtered: boolean) => {
    setFiltering(filtered);
    setActive(-1);
    setOpen(true);
    input.current?.focus(); // opening from the arrow still leaves the caret where typing works
  };
  const pick = (choice: string) => {
    onChange(choice);
    setOpen(false);
    input.current?.focus();
  };

  function onKeyDown(e: ReactKeyboardEvent) {
    if (e.key === "Escape") {
      setOpen(false);
      return;
    }
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      if (!visible) return show(false);
      const step = e.key === "ArrowDown" ? 1 : -1;
      setActive((i) => (i < 0 ? (step > 0 ? 0 : matches.length - 1) : (i + step + matches.length) % matches.length));
      return;
    }
    if (e.key === "Enter" && visible && active >= 0 && active < matches.length) {
      e.preventDefault();
      pick(matches[active].value);
    }
  }

  return (
    <div
      className="setting-combo"
      // closing on blur rather than behind a backdrop: a click straight into the next field should
      // land there, not be spent dismissing this list
      onBlur={(e) => {
        if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setOpen(false);
      }}
    >
      <input
        ref={input}
        className="text-input"
        value={current}
        placeholder={setting.placeholder ?? ""}
        disabled={disabled}
        spellCheck={false}
        autoComplete="off"
        role="combobox"
        aria-expanded={visible}
        onChange={(e) => {
          onChange(e.target.value);
          show(true);
        }}
        onKeyDown={onKeyDown}
      />
      <button
        type="button"
        className="setting-combo-toggle"
        tabIndex={-1}
        disabled={disabled}
        title={"Known values for " + setting.label}
        aria-label={"Known values for " + setting.label}
        onClick={() => (visible ? setOpen(false) : show(false))}
      >
        <IconChevronDown size={14} />
      </button>
      {visible && (
        <div className="setting-combo-list">
          {matches.map((o, i) => (
            <button
              type="button"
              key={o.value}
              className={
                "setting-combo-option" +
                (i === active ? " active" : "") +
                (o.value.toLowerCase() === current.toLowerCase() ? " current" : "")
              }
              onMouseEnter={() => setActive(i)}
              onClick={() => pick(o.value)}
            >
              <span>{o.label}</span>
              {o.hint && <span className="hint">{o.hint}</span>}
            </button>
          ))}
        </div>
      )}
    </div>
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

function describeSave(changed: number, reopened: boolean, settings: SettingView[]): string {
  if (changed === 0) return "Nothing changed.";
  const saved = `${changed} ${changed === 1 ? "setting" : "settings"} saved.`;
  if (reopened) return saved + " The database was closed and reopened.";
  if (settings.some((s) => s.applies === "restart")) return saved + " Some of them only take effect after the host restarts.";
  if (settings.some((s) => s.applies === "reopen")) return saved + " Some of them only take effect when the database is next opened.";
  return saved;
}
