import { useEffect, useMemo, useRef, useState } from "react";
import {
  IconAlertTriangle,
  IconArrowBackUp,
  IconArrowDown,
  IconArrowUp,
  IconBraces,
  IconCode,
  IconCopy,
  IconDeviceFloppy,
  IconDownload,
  IconForms,
  IconInfoCircle,
  IconLock,
  IconPlus,
  IconTrash,
} from "@tabler/icons-react";
import { CodeEditor } from "./CodeEditor";
import { DialogTools } from "./DialogTools";
import { Switch } from "./LogsSection";
import {
  columnKeyProblem,
  dataTypeLabel,
  dataTypes,
  daysOfWeek,
  definitionFromFileJson,
  definitionToFileJson,
  fetchDefinition,
  fileIntervals,
  keyFromName,
  keyProblem,
  planDefinition,
  recordingCode,
  retentionOf,
  saveDefinition,
  saveText,
  statisticApplies,
  statisticInfos,
  type ColumnDefinition,
  type CustomLogsInfo,
  type CustomLogSummary,
  type DefinitionPlan,
  type LogDefinition,
  type StatisticsType,
} from "../server/customLogs";
import type { LogDataType } from "../server/logs";
import type { DatabaseInfo } from "../server/serverInfo";
import { showError } from "../dialogs";
import { lint, stripJsonComments } from "../code/lint";
import { formatCount } from "../format";

/**
 * A log's definition, edited: its name, its columns with their types and statistics, how it keeps
 * its files. The same page defines a new log and changes one that exists.
 *
 * Three ways to look at the one definition: the form, the json its settings file holds (edited here
 * as text too, for pasting in a definition from elsewhere), and the application code that records
 * into it. The draft is held by the section, not here, so looking at the graphs in between loses
 * nothing.
 *
 * A change to a log that has recorded something is asked about before it is made. The server says
 * what the change does - entries moved to another file layout, statistics dropped or started over,
 * entries past a tighter limit deleted - and the confirmation lists exactly that, with the offer to
 * rebuild new statistics from the entries already there.
 *
 * Recording and statistics of an existing log are switched at the top of its page, where they take
 * effect at once; saving a definition leaves them as they are.
 */

type Mode = "form" | "json" | "code";

// what the server is asked about the draft, at most this often while it is being typed into
const planDelayMs = 400;

export function CustomLogEditor({
  db,
  info,
  log,
  draft,
  onDraft,
  onDiscard,
  onSaved,
  onDuplicate,
}: {
  db: DatabaseInfo;
  info: CustomLogsInfo;
  /** The log being edited; null for a new one. */
  log: CustomLogSummary | null;
  draft: LogDefinition | null;
  onDraft: (draft: LogDefinition | null) => void;
  onDiscard: () => void;
  onSaved: (key: string) => void;
  onDuplicate?: (draft: LogDefinition) => void;
}) {
  const isNew = log === null;
  const [saved, setSaved] = useState<LogDefinition | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [mode, setMode] = useState<Mode>("form");
  const [jsonText, setJsonText] = useState("");
  const [jsonError, setJsonError] = useState<string | null>(null);
  const [plan, setPlan] = useState<DefinitionPlan | null>(null);
  const [saving, setSaving] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<{ plan: DefinitionPlan; resolve: (choice: { ok: boolean; rebuild: boolean }) => void } | null>(null);
  // a new log's key follows its name until it is typed into
  const [keyTouched, setKeyTouched] = useState(() => !!draft?.key);

  // what the definition is on disk. Asked again whenever the page's summary of the log says it
  // changed - switched at the top of the page, or saved in another window
  const signature = log
    ? JSON.stringify([log.name, log.description, log.fileInterval, log.maxAgeInDays, log.maxSizeInMb, log.resolutionRowStats, log.firstDayOfWeek, log.enabledLog, log.enabledStatistics, log.enabledText, log.compressed, log.series, log.columns])
    : "";
  useEffect(() => {
    if (!log) return;
    let cancelled = false;
    fetchDefinition(db.id, log.key)
      .then((d) => {
        if (cancelled) return;
        setSaved(d.settings);
        setLoadError(null);
      })
      .catch((e) => !cancelled && setLoadError(e instanceof Error ? e.message : String(e)));
    return () => {
      cancelled = true;
    };
  }, [db.id, log?.key, signature]);

  const current = draft ?? saved;
  // every edit is made to the newest draft, not to the one this render closed over: two edits in one
  // tick (a script, a fast double action) would otherwise both start from the same draft and the
  // second would undo the first
  const latest = useRef<LogDefinition | null>(null);
  latest.current = current;
  function commit(next: LogDefinition) {
    latest.current = next;
    onDraft(next);
  }
  // the switches of an existing log are not the editor's: they are saved as they are now
  const toSave = useMemo(
    () => (current && saved && !isNew ? { ...current, enableLog: saved.enableLog, enableStatistics: saved.enableStatistics } : current),
    [current, saved, isNew],
  );
  const dirty = isNew || (!!draft && !!saved && definitionToFileJson({ ...draft, enableLog: saved.enableLog, enableStatistics: saved.enableStatistics }) !== definitionToFileJson(saved));

  // the server's word on the draft: whether it can be saved at all, and for an existing log what
  // saving it would do. Asked a moment after the typing stops, and forgotten if the draft moved on
  const planRequest = useRef(0);
  useEffect(() => {
    if (!toSave || (!isNew && !dirty)) {
      setPlan(null);
      return;
    }
    const id = ++planRequest.current;
    const timer = window.setTimeout(() => {
      planDefinition(db.id, toSave, isNew)
        .then((p) => id === planRequest.current && setPlan(p))
        .catch((e) => id === planRequest.current && setPlan({ valid: false, error: e instanceof Error ? e.message : String(e), changed: false, notes: [] }));
    }, planDelayMs);
    return () => window.clearTimeout(timer);
  }, [db.id, toSave, isNew, dirty]);

  function update(change: Partial<LogDefinition>) {
    const base = latest.current;
    if (!base) return;
    const next = { ...base, ...change };
    if (isNew && change.name !== undefined && !keyTouched) next.key = keyFromName(change.name);
    commit(next);
    setNote(null);
  }
  function updateColumn(index: number, change: Partial<ColumnDefinition>) {
    const base = latest.current;
    if (!base) return;
    const properties = base.properties.map((c, i) => {
      if (i !== index) return c;
      const next = { ...c, ...change };
      // a statistic the new type cannot keep goes with the type change, rather than failing the save
      if (change.dataType) next.statistics = next.statistics.filter((s) => statisticApplies(s.statisticsType, change.dataType!));
      return next;
    });
    update({ properties });
  }
  function moveColumn(index: number, by: number) {
    const base = latest.current;
    if (!base) return;
    const properties = [...base.properties];
    const target = index + by;
    if (target < 0 || target >= properties.length) return;
    [properties[index], properties[target]] = [properties[target], properties[index]];
    update({ properties });
  }
  function addColumn() {
    const base = latest.current;
    if (!base) return;
    let n = base.properties.length + 1;
    while (base.properties.some((c) => c.key.toLowerCase() === `column${n}`)) n++;
    update({ properties: [...base.properties, { key: `column${n}`, name: "", dataType: "String", statistics: [] }] });
  }
  function removeColumn(index: number) {
    const base = latest.current;
    if (!base) return;
    update({ properties: base.properties.filter((_, i) => i !== index) });
  }
  function toggleStatistic(index: number, type: StatisticsType) {
    const base = latest.current;
    if (!base) return;
    const column = base.properties[index];
    const has = column.statistics.some((s) => s.statisticsType === type);
    // a statistic switched on is kept at the log's level of detail, like every other one of it
    updateColumn(index, {
      statistics: has ? column.statistics.filter((s) => s.statisticsType !== type) : [...column.statistics, { statisticsType: type, resolution: base.resolutionRowStats }],
    });
  }
  /**
   * The level of statistical detail: how far back every statistic of the log reaches. The file keeps
   * a level per statistic (and one for the entry count), and a file written by hand may give them
   * different ones; the form offers the one level, and setting it gives every statistic that level.
   */
  function setLevel(level: number) {
    const base = latest.current;
    if (!base) return;
    const value = Math.max(1, Math.min(1000, Math.round(level) || 1));
    update({
      resolutionRowStats: value,
      properties: base.properties.map((c) => ({ ...c, statistics: c.statistics.map((s) => ({ ...s, resolution: value })) })),
    });
  }

  function switchMode(next: Mode) {
    if (next === "json" && current) {
      setJsonText(definitionToFileJson(current));
      setJsonError(null);
    }
    setMode(next);
  }
  function editJson(text: string) {
    setJsonText(text);
    try {
      const parsed = definitionFromFileJson(stripJsonComments(text));
      setJsonError(null);
      commit(parsed);
      if (isNew) setKeyTouched(true);
    } catch (e) {
      setJsonError(e instanceof Error ? e.message : String(e));
    }
  }

  /** The confirmation of a change: what it does, and the offer to rebuild new statistics. */
  function confirmChanges(p: DefinitionPlan): Promise<{ ok: boolean; rebuild: boolean }> {
    return new Promise((resolve) => setConfirming({ plan: p, resolve }));
  }

  async function save() {
    if (!toSave) return;
    setSaving(true);
    try {
      const p = await planDefinition(db.id, toSave, isNew);
      setPlan(p);
      if (!p.valid) {
        await showError(isNew ? "The log cannot be created yet" : "The definition cannot be saved yet", p.error ?? "The settings are not valid.");
        return;
      }
      if (!isNew && !p.changed) {
        onDraft(null);
        return;
      }
      let rebuild = false;
      if (!isNew && p.notes.length > 0) {
        const choice = await confirmChanges(p);
        if (!choice.ok) return;
        rebuild = choice.rebuild;
      }
      const result = await saveDefinition(db.id, toSave, isNew, rebuild);
      const parts: string[] = [];
      if (result.entriesMoved > 0) parts.push(`${formatCount(result.entriesMoved)} entries moved into the new files`);
      if (result.statisticsRebuilt) parts.push("statistics rebuilt from the entries");
      setNote(result.created ? "Created." : parts.length > 0 ? `Saved: ${parts.join(", ")}.` : "Saved.");
      onSaved(result.key);
    } catch (e) {
      await showError(isNew ? "Could not create the log" : "Could not save the definition", e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }

  if (loadError) return <div className="logs-note">{loadError}</div>;
  if (!current) return null;
  const taken = info.logs.map((l) => l.key);
  const keyIssue = isNew ? keyProblem(current.key, taken, info.reservedKeys) : null;
  const columnIssues = current.properties.map((c, i) =>
    columnKeyProblem(
      c.key,
      current.properties.filter((_, j) => j !== i).map((o) => o.key),
    ),
  );
  const localProblem = keyIssue ?? columnIssues.find((x) => x) ?? null;
  const serverProblem = plan && !plan.valid ? plan.error : null;
  const problem = localProblem ?? serverProblem;

  return (
    <div className="clog-editor">
      <div className="clog-editor-bar">
        <div className="module-switch compact">
          <button className={mode === "form" ? "active" : ""} onClick={() => switchMode("form")}>
            <IconForms size={14} stroke={1.8} /> Form
          </button>
          <button className={mode === "json" ? "active" : ""} onClick={() => switchMode("json")}>
            <IconBraces size={14} stroke={1.8} /> Json
          </button>
          <button className={mode === "code" ? "active" : ""} onClick={() => switchMode("code")}>
            <IconCode size={14} stroke={1.8} /> Recording code
          </button>
        </div>
        <span className={"clog-editor-status" + (problem ? " bad" : "")}>
          {problem ? (
            <>
              <IconAlertTriangle size={14} stroke={1.8} /> {problem}
            </>
          ) : note ? (
            note
          ) : isNew ? (
            "Not created yet"
          ) : dirty ? (
            plan?.notes.length ? `Unsaved changes · ${plan.notes.length} ${plan.notes.length === 1 ? "thing" : "things"} to know before saving` : "Unsaved changes"
          ) : (
            "Saved"
          )}
        </span>
        <button className="action-button" onClick={() => saveText(definitionToFileJson(current), `log.${current.key || "new"}.settings.json`, "application/json")} title="Save the definition as a settings file, to import in another database">
          <IconDownload size={15} stroke={1.8} /> Json
        </button>
        {!isNew && onDuplicate && (
          <button
            className="action-button"
            onClick={() => onDuplicate({ ...current, key: `${current.key}-copy`.slice(0, 64), name: current.name ? `${current.name} (copy)` : "" })}
            title="A new log with this definition and another key"
          >
            <IconCopy size={15} stroke={1.8} /> Duplicate
          </button>
        )}
        {isNew ? (
          <button className="action-button" onClick={onDiscard}>
            <IconTrash size={15} stroke={1.8} /> Discard
          </button>
        ) : (
          <button className="action-button" onClick={() => onDraft(null)} disabled={!dirty} title="Throw the changes away and go back to what is saved">
            <IconArrowBackUp size={15} stroke={1.8} /> Revert
          </button>
        )}
        <button className="action-button primary" onClick={save} disabled={saving || (!isNew && !dirty) || !!localProblem || (mode === "json" && !!jsonError)}>
          <IconDeviceFloppy size={15} stroke={1.8} /> {saving ? "Saving…" : isNew ? "Create log" : "Save"}
        </button>
      </div>

      {mode === "json" ? (
        <section className="panel">
          <h3>
            <code className="clog-h3-file">log.{current.key || "…"}.settings.json</code>{" "}
            <span className="panel-sub">the file in the database's log folder: edit it here, or paste one in</span>
          </h3>
          {jsonError && <div className="logs-note clog-bad-note">{jsonError}</div>}
          <div className="clog-json-editor">
            <CodeEditor value={jsonText} onChange={editJson} language="json" issues={lint(jsonText, "json")} onSave={save} />
          </div>
        </section>
      ) : mode === "code" ? (
        <section className="panel">
          <h3>
            Recording into {current.name || current.key || "the log"} <span className="panel-sub">C#, with the store the application already has</span>
          </h3>
          <CodeEditor value={recordingCode(current)} onChange={() => {}} language="csharp" issues={[]} readOnly />
          <div className="clog-code-note muted">
            <IconInfoCircle size={14} stroke={1.8} /> A value is converted to its column's type: a long into a whole number, a decimal into a decimal number, a
            DateTimeOffset into a UTC moment. One that does not convert, and a null, is left out of the entry rather than recorded as zero. Values for keys the log
            does not declare are kept in the entry as text.
          </div>
        </section>
      ) : (
        <DefinitionForm
          current={current}
          isNew={isNew}
          keyIssue={keyIssue}
          keyTouched={keyTouched}
          onKeyTouched={() => setKeyTouched(true)}
          columnIssues={columnIssues}
          update={update}
          updateColumn={updateColumn}
          moveColumn={moveColumn}
          addColumn={addColumn}
          removeColumn={removeColumn}
          toggleStatistic={toggleStatistic}
          setLevel={setLevel}
          plan={!isNew && dirty ? plan : null}
        />
      )}

      {confirming && (
        <ChangesDialog
          name={current.name || current.key}
          plan={confirming.plan}
          onAnswer={(choice) => {
            confirming.resolve(choice);
            setConfirming(null);
          }}
        />
      )}
    </div>
  );
}

function DefinitionForm({
  current,
  isNew,
  keyIssue,
  keyTouched,
  onKeyTouched,
  columnIssues,
  update,
  updateColumn,
  moveColumn,
  addColumn,
  removeColumn,
  toggleStatistic,
  setLevel,
  plan,
}: {
  current: LogDefinition;
  isNew: boolean;
  keyIssue: string | null;
  keyTouched: boolean;
  onKeyTouched: () => void;
  columnIssues: (string | null)[];
  update: (change: Partial<LogDefinition>) => void;
  updateColumn: (index: number, change: Partial<ColumnDefinition>) => void;
  moveColumn: (index: number, by: number) => void;
  addColumn: () => void;
  removeColumn: (index: number) => void;
  toggleStatistic: (index: number, type: StatisticsType) => void;
  setLevel: (level: number) => void;
  plan: DefinitionPlan | null;
}) {
  const level = current.resolutionRowStats;
  const kept = retentionOf(level);
  // levels a file written by hand gave single statistics: the form's one level replaces them when set
  const otherLevels = [...new Set(current.properties.flatMap((c) => c.statistics.map((s) => s.resolution)).filter((r) => r !== level))].sort((a, b) => a - b);
  return (
    <div className="clog-form">
      {plan && plan.valid && plan.notes.length > 0 && (
        <div className="clog-plan">
          <div className="clog-plan-head">
            <IconInfoCircle size={15} stroke={1.8} /> Saving this changes what the log has recorded:
          </div>
          <ul>
            {plan.notes.map((n) => (
              <li key={n}>{n}</li>
            ))}
          </ul>
        </div>
      )}

      <section className="panel">
        <h3>The log</h3>
        <div className="clog-fields">
          <label className="clog-field">
            <span className="clog-field-label">Name</span>
            <input className="text-input" value={current.name} placeholder="Web requests, Orders, Imports…" onChange={(e) => update({ name: e.currentTarget.value })} />
          </label>
          <label className="clog-field">
            <span className="clog-field-label">Key</span>
            {isNew ? (
              <>
                <input
                  className={"text-input mono" + (keyIssue ? " invalid" : "")}
                  value={current.key}
                  placeholder="requests"
                  onChange={(e) => {
                    onKeyTouched();
                    update({ key: e.currentTarget.value });
                  }}
                />
                <span className={"clog-field-hint" + (keyIssue ? " bad" : "")}>
                  {keyIssue ?? (keyTouched ? "What the application records by, and what the files are named after. It cannot be changed later." : "Made from the name until it is typed into.")}
                </span>
              </>
            ) : (
              <>
                <span className="clog-key-fixed">
                  <IconLock size={13} stroke={1.8} /> <code>{current.key}</code>
                </span>
                <span className="clog-field-hint">The key names the log's files and is what the application records by, so it stays. Duplicate the log for another one.</span>
              </>
            )}
          </label>
          <label className="clog-field wide">
            <span className="clog-field-label">Description</span>
            <textarea
              className="text-input"
              rows={2}
              value={current.description}
              placeholder="What the log is for, and who records into it"
              onChange={(e) => update({ description: e.currentTarget.value })}
            />
          </label>
        </div>
      </section>

      <section className="panel">
        <h3>
          Columns <span className="panel-sub">what an entry holds, beside the time it was recorded at, and what is counted about each</span>
        </h3>
        {current.properties.length > 0 && (
          <div className="clog-columns">
            <div className="clog-column-row clog-column-head">
              <span />
              <span>Key</span>
              <span>Name</span>
              <span>Type</span>
              <span className="clog-column-head-stats">Statistics</span>
              <span className="clog-column-remove" />
            </div>
            {current.properties.map((c, i) => (
              <ColumnRow
                key={i}
                column={c}
                index={i}
                count={current.properties.length}
                issue={columnIssues[i]}
                updateColumn={updateColumn}
                moveColumn={moveColumn}
                removeColumn={removeColumn}
                toggleStatistic={toggleStatistic}
              />
            ))}
          </div>
        )}
        {current.properties.length === 0 && <div className="logs-note">No columns yet. A log without columns still counts its entries, but records nothing in them.</div>}
        <div className="clog-columns-foot">
          <button className="action-button" onClick={addColumn}>
            <IconPlus size={15} stroke={1.8} /> Add column
          </button>
        </div>
      </section>

      <section className="panel">
        <h3>Recording</h3>
        <div className="clog-switches">
          {isNew ? (
            <>
              <Switch label="Record entries" checked={current.enableLog} onChange={(v) => update({ enableLog: v })} title="Write every entry to disk" />
              <Switch label="Keep statistics" checked={current.enableStatistics} onChange={(v) => update({ enableStatistics: v })} title="Aggregate the entries into statistics for the graphs" />
            </>
          ) : (
            <span className="muted clog-switch-note">Recording entries and keeping statistics are switched at the top of the page, and take effect at once.</span>
          )}
          <Switch
            label="Also write a text copy"
            checked={current.enableLogTextFormat}
            onChange={(v) => update({ enableLogTextFormat: v })}
            title="A tab separated .txt file beside every entries file, readable without the database"
          />
          <Switch label="Compress" checked={current.compressed} onChange={(v) => update({ compressed: v })} title="Smaller files, a little more work to write and read them" />
        </div>
      </section>

      <section className="panel">
        <h3>
          Files and limits <span className="panel-sub">how the entries are kept on disk, and for how long</span>
        </h3>
        <div className="clog-fields">
          <div className="clog-field">
            <span className="clog-field-label">One file per</span>
            <div className="module-switch compact">
              {fileIntervals.map((f) => (
                <button key={f} className={current.fileInterval === f ? "active" : ""} onClick={() => update({ fileInterval: f })}>
                  {f.toLowerCase()}
                </button>
              ))}
            </div>
            <span className="clog-field-hint">
              A file is what the limits delete, whole. Small files trim closely; large ones are fewer. Changing it moves what is recorded into files of the new size.
            </span>
          </div>
          <label className="clog-field">
            <span className="clog-field-label">Keep entries for</span>
            <span className="clog-number">
              <input
                className="text-input number"
                type="number"
                min={0}
                value={current.maxAgeOfLogFilesInDays}
                onChange={(e) => update({ maxAgeOfLogFilesInDays: Math.max(0, Math.round(Number(e.currentTarget.value) || 0)) })}
              />
              days
            </span>
            <span className="clog-field-hint">{current.maxAgeOfLogFilesInDays > 0 ? "Older files are deleted, about once a minute." : "0: no age limit."}</span>
          </label>
          <label className="clog-field">
            <span className="clog-field-label">At most</span>
            <span className="clog-number">
              <input
                className="text-input number"
                type="number"
                min={0}
                value={current.maxTotalSizeOfLogFilesInMb}
                onChange={(e) => update({ maxTotalSizeOfLogFilesInMb: Math.max(0, Math.round(Number(e.currentTarget.value) || 0)) })}
              />
              MB
            </span>
            <span className="clog-field-hint">{current.maxTotalSizeOfLogFilesInMb > 0 ? "The oldest files go first when the entries grow past it." : "0: no size limit."}</span>
          </label>
          <label className="clog-field">
            <span className="clog-field-label">Weeks start on</span>
            <select className="select" value={current.firstDayOfWeek} onChange={(e) => update({ firstDayOfWeek: e.currentTarget.value as LogDefinition["firstDayOfWeek"] })}>
              {daysOfWeek.map((d) => (
                <option key={d} value={d}>
                  {d}
                </option>
              ))}
            </select>
            <span className="clog-field-hint">What a week is, for statistics kept per week.</span>
          </label>
        </div>
        {/* on a line of its own: it is about every statistic of the log, not about its files */}
        <div className="clog-level">
          <label className="clog-field clog-level-control">
            <span className="clog-field-label">Level of statistical detail</span>
            <input className="text-input number" type="number" min={1} max={1000} value={level} onChange={(e) => setLevel(Number(e.currentTarget.value))} />
          </label>
          <div className="clog-level-text">
            <p>
              A statistic is kept per second, minute, hour, day, week and month, and the level says how far back: level 1 keeps 60 seconds, 60 minutes, 48
              hours, 60 days, 52 weeks and 60 months, and a higher level that many times as much. More detail costs more memory and disk.
            </p>
            <p className="muted">
              At level {level}: per second for {kept[0].span}, per minute for {kept[1].span}, per hour for {kept[2].span}, per day for {kept[3].span}, per week
              for {kept[4].span} and per month for {kept[5].span}.
            </p>
            {otherLevels.length > 0 && (
              <p className="clog-field-hint">
                Some statistics of this log are kept at {otherLevels.length === 1 ? "level" : "levels"} {otherLevels.join(", ")}. Setting the level here gives
                all of them this one.
              </p>
            )}
          </div>
        </div>
      </section>
    </div>
  );
}

function ColumnRow({
  column,
  index,
  count,
  issue,
  updateColumn,
  moveColumn,
  removeColumn,
  toggleStatistic,
}: {
  column: ColumnDefinition;
  index: number;
  count: number;
  issue: string | null;
  updateColumn: (index: number, change: Partial<ColumnDefinition>) => void;
  moveColumn: (index: number, by: number) => void;
  removeColumn: (index: number) => void;
  toggleStatistic: (index: number, type: StatisticsType) => void;
}) {
  const shadowsTime = ["time", "timestamp"].includes(column.key.trim().toLowerCase());
  return (
    <div className="clog-column-row">
      <span className="clog-column-move">
        <button className="icon-button" disabled={index === 0} onClick={() => moveColumn(index, -1)} title="Move up">
          <IconArrowUp size={14} stroke={1.8} />
        </button>
        <button className="icon-button" disabled={index === count - 1} onClick={() => moveColumn(index, 1)} title="Move down">
          <IconArrowDown size={14} stroke={1.8} />
        </button>
      </span>
      <span className="clog-column-cell">
        <input className={"text-input mono" + (issue ? " invalid" : "")} value={column.key} onChange={(e) => updateColumn(index, { key: e.currentTarget.value })} />
        {issue && <span className="clog-field-hint bad">{issue}</span>}
        {!issue && shadowsTime && <span className="clog-field-hint">A search naming "{column.key}" finds this column rather than the time.</span>}
      </span>
      <span className="clog-column-cell">
        <input className="text-input" value={column.name} placeholder={column.key} onChange={(e) => updateColumn(index, { name: e.currentTarget.value })} />
      </span>
      <span className="clog-column-cell">
        <select className="select" value={column.dataType} onChange={(e) => updateColumn(index, { dataType: e.currentTarget.value as LogDataType })}>
          {dataTypes.map((t) => (
            <option key={t} value={t}>
              {dataTypeLabel[t]}
            </option>
          ))}
        </select>
      </span>
      <span className="clog-stat-chips">
        {statisticInfos
          .filter((s) => statisticApplies(s.type, column.dataType))
          .map((s) => {
            const on = column.statistics.some((x) => x.statisticsType === s.type);
            return (
              <button key={s.type} className={"clog-stat-chip" + (on ? " on" : "")} title={s.help} aria-pressed={on} onClick={() => toggleStatistic(index, s.type)}>
                {s.label}
              </button>
            );
          })}
      </span>
      <span className="clog-column-remove">
        <button className="icon-button" onClick={() => removeColumn(index)} title="Remove the column">
          <IconTrash size={14} stroke={1.8} />
        </button>
      </span>
    </div>
  );
}

/**
 * The confirmation in front of a change: every consequence the server named, and - where statistics
 * were added or start over while the log already has entries - the offer to rebuild them from those
 * entries, so the new graphs cover what was recorded before the change too.
 */
function ChangesDialog({ name, plan, onAnswer }: { name: string; plan: DefinitionPlan; onAnswer: (choice: { ok: boolean; rebuild: boolean }) => void }) {
  const [rebuild, setRebuild] = useState(true);
  const heavy = plan.movesEntries || plan.discardsStatistics;
  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onAnswer({ ok: false, rebuild: false })}>
      <div className="dialog dialog-wide clog-changes-dialog">
        <h3>
          <IconDeviceFloppy size={16} stroke={2} /> Save the changes to {name}
          <DialogTools onClose={() => onAnswer({ ok: false, rebuild: false })} closeTitle="Cancel" />
        </h3>
        <div className="dialog-body">What the change does to what the log has recorded:</div>
        <ul className="clog-changes">
          {plan.notes.map((n) => (
            <li key={n}>{n}</li>
          ))}
        </ul>
        {plan.canRebuildStatistics && (
          <label className="dialog-option clog-rebuild-option">
            <input type="checkbox" checked={rebuild} onChange={(e) => setRebuild(e.currentTarget.checked)} /> Rebuild the statistics from the entries already recorded
            <span className="muted"> - reads every entry once</span>
          </label>
        )}
        {heavy && <div className="dialog-body muted">This can take a while on a large log. The page waits until it is done.</div>}
        <div className="dialog-row">
          <span className="header-spacer" />
          <button className="action-button primary" onClick={() => onAnswer({ ok: true, rebuild: plan.canRebuildStatistics ? rebuild : false })}>
            Save changes
          </button>
          <button className="action-button" onClick={() => onAnswer({ ok: false, rebuild: false })}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}
