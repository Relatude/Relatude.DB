import { useCallback, useEffect, useMemo, useState } from "react";
import { IconAlertTriangle, IconCalendarPlus, IconPencil, IconPlayerPlay, IconPlayerStop, IconPlus, IconSettings, IconTrash } from "@tabler/icons-react";
import { useLive } from "../live";
import { showConfirm, showError, showInfo } from "../dialogs";
import { openInSettings } from "../navigate";
import {
  addSchedule,
  cancelRun,
  fetchScheduler,
  previewSchedule,
  removeSchedule,
  runJob,
  saveSchedule,
  type JobView,
  type RunStatus,
  type ScheduleUnit,
  type SchedulePreview,
  type ScheduleValues,
  type ScheduleView,
  type SchedulerInfo,
  type JobRun,
} from "../server/scheduler";
import type { DatabaseInfo } from "../server/serverInfo";
import { formatCount, formatDateTime, formatTime } from "../format";
import { Combo } from "./Combo";
import { DialogTools } from "./DialogTools";
import { Loading } from "./Loading";
import { SourceTag } from "./SourceTag";

/**
 * The scheduler: which job runs when, what jobs there are to run, and how the runs went.
 *
 * A schedule is a setting of the database (LocalSettings.Schedules). This page edits the list with an
 * editor of its own - a timetable reads better as "every day at 02:00, next on Monday" than as a
 * dozen fields - but saves it exactly as the settings page would: on THIS SERVER, in the overrides
 * file, with Move to shared at the top right of the module to take it into SHARED. A list decided by
 * appsettings or the application's code is shown and locked.
 *
 * Times of day are on the schedule's own clock - the server's time zone unless the schedule names
 * one - so the next run is shown on that clock too. The runs below are in this browser's time, like
 * everything else that happened on the server.
 */
export function SchedulerView({ db, version, onSaved }: { db: DatabaseInfo; version: number; onSaved: () => void }) {
  const [info, setInfo] = useState<SchedulerInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [editing, setEditing] = useState<EditorTarget | null>(null);
  const [running, setRunning] = useState<JobView | null>(null);
  const now = useNow(15000);

  const apply = useCallback((data: SchedulerInfo) => {
    setInfo(data);
    setError(null);
  }, []);
  // restarted when something was saved - here or in the Move to shared dialog - so the page shows it at once
  useLive<SchedulerInfo>("scheduler", { storeId: db.id }, apply, { onError: setError, restartOn: version });

  const load = useCallback(async () => {
    try {
      apply(await fetchScheduler(db.id));
    } catch (e) {
      setError(message(e));
    }
  }, [db.id, apply]);

  /** Runs one change, then refreshes: every one of them changes what the page should show. */
  async function act(what: string, action: () => Promise<unknown>, saved = false): Promise<void> {
    setBusy(true);
    try {
      await action();
      if (saved) onSaved();
      await load();
    } catch (e) {
      await showError("Could not " + what, message(e));
    } finally {
      setBusy(false);
    }
  }

  if (error && !info) return <div className="placeholder">{error}</div>;
  if (!info) return <Loading label="Loading the scheduler…" />;

  const locked = info.locked !== null;
  const lockedTitle =
    info.locked === "code"
      ? "The application's code sets the schedules at every start (OnStoreSettingsInit), so they cannot be changed here."
      : `The ${info.configSection ?? "RelatudeDB"} configuration section - appsettings.json, an environment variable or user secrets - sets the schedules, so they cannot be changed here.`;
  const zone = info.serverTimeZone;
  const jobsByName = new Map(info.jobs.map((t) => [t.name.toLowerCase(), t]));

  async function toggle(s: ScheduleView, enabled: boolean): Promise<void> {
    await act(enabled ? "turn the schedule on" : "turn the schedule off", () => saveSchedule(db.id, s.id, { enabled }), true);
  }

  async function runNow(s: ScheduleView): Promise<void> {
    const job = s.job ? jobsByName.get(s.job.toLowerCase()) : undefined;
    const confirmed = await showConfirm(
      `Run “${scheduleName(s)}” now?`,
      `${job?.description ?? "Runs the job " + (s.job ?? "")}${s.argument ? ` With the argument “${s.argument}”.` : ""} It goes through the task queue like a scheduled run, and the schedule's own times stay as they are.`,
      { confirmLabel: "Run now" },
    );
    if (!confirmed.ok) return;
    await act("start the run", () => runJob(db.id, { scheduleId: s.id }));
  }

  async function remove(s: ScheduleView): Promise<void> {
    const shared = !s.addedHere;
    const confirmed = await showConfirm(
      `Remove the schedule “${scheduleName(s)}”?`,
      `${s.summary}, ${s.job ?? "no job"} stops running. ` +
        (shared
          ? `It is in SHARED, ${info?.settingsFile ?? "relatude.db.json"}: removing it here is saved on THIS SERVER, and Move to shared takes the removal there - or takes it back.`
          : "Its runs stay in the list below."),
      { confirmLabel: "Remove", danger: true },
    );
    if (!confirmed.ok) return;
    await act("remove the schedule", () => removeSchedule(db.id, s.id), true);
  }

  async function stop(run: JobRun): Promise<void> {
    await act("stop the run", async () => {
      const result = await cancelRun(db.id, run.runId);
      if (!result.cancelled) await showInfo("Nothing to stop", `The run of ${run.jobName} has already finished.`);
    });
  }

  const subtitle =
    `${info.schedules.length === 0 ? "nothing scheduled" : `${formatCount(info.schedules.length)} ${info.schedules.length === 1 ? "schedule" : "schedules"}`}` +
    ` · on the server's clock, ${zone.name} (${utcOffset(zone.offsetMinutes)}), unless a schedule names a time zone`;

  return (
    <div className="scheduler">
      {!info.open && (
        <div className="scheduler-notice">
          <IconAlertTriangle size={15} stroke={1.8} />
          <span>The database is {info.state.toLowerCase()}. Its schedules are listed and can be changed; nothing runs, and its jobs are not known, until it opens.</span>
        </div>
      )}
      {info.open && !info.autoDequeue && (
        <div className="scheduler-notice">
          <IconAlertTriangle size={15} stroke={1.8} />
          <span>
            Run queued tasks is off for this database, so the schedules queue their runs and nothing runs them.{" "}
            <button className="link-button" onClick={() => openInSettings({ scope: "database", storeId: db.id, sectionId: "maintenance", groupId: "tasks" })}>
              <IconSettings size={13} stroke={1.8} /> Background tasks
            </button>
          </span>
        </div>
      )}
      {locked && (
        <div className="scheduler-notice locked">
          <SourceTag kind={info.locked === "code" ? "code" : "appsettings"} title={lockedTitle} />
          <span>{lockedTitle}</span>
        </div>
      )}

      <section className="panel">
        <h3 className="panel-head-row">
          Schedules <span className="panel-sub">{subtitle}</span>
          <span className="header-spacer" />
          <button
            className="action-button"
            disabled={busy || locked}
            title={locked ? lockedTitle : "Run a job at set intervals"}
            onClick={() => setEditing({ schedule: null, preset: null })}
          >
            <IconPlus size={15} stroke={1.8} /> Add schedule
          </button>
        </h3>
        <div className="sched-table">
          <div className="sched-row sched-head-row">
            <span />
            <span>Schedule</span>
            <span>Next run</span>
            <span>Last run</span>
            <span />
          </div>
          {info.schedules.length === 0 && (
            <div className="tasks-empty">
              Nothing is scheduled. Add a schedule to run one of the jobs below at set intervals - a nightly backup, a weekly log truncation, or a job
              of the application's own.
            </div>
          )}
          {info.schedules.map((s) => (
            <div key={s.id} className={"sched-row" + (s.enabled ? "" : " off")}>
              <span>
                <input
                  type="checkbox"
                  className="sched-toggle"
                  checked={s.enabled}
                  disabled={busy || locked}
                  title={locked ? lockedTitle : s.enabled ? "On - click to keep the schedule without running it" : "Off - click to turn it on; it then waits for its next time"}
                  onChange={(e) => void toggle(s, e.target.checked)}
                />
              </span>
              <span className="sched-what">
                <span className="sched-name">
                  <span className="sched-name-text">{scheduleName(s)}</span>
                  {s.name && s.job && <span className="sched-job-tag">{s.job}</span>}
                  {!locked && <ScheduleSource schedule={s} info={info} />}
                </span>
                <span className="sched-summary">
                  {s.summary}
                  {s.argument && <span className="sched-argument"> · argument {s.argument}</span>}
                  {!s.runMissed && <span className="muted"> · skips missed runs</span>}
                </span>
                {s.problem && (
                  <span className="sched-problem">
                    <IconAlertTriangle size={13} stroke={1.8} /> {s.problem}
                  </span>
                )}
              </span>
              <span className="sched-next">
                {!s.enabled ? (
                  <span className="muted">off</span>
                ) : s.nextUtc ? (
                  <>
                    <span className="sched-next-at" title={formatDateTime(s.nextUtc) + " in this browser's time"}>
                      {s.nextAt ?? formatTime(s.nextUtc)}
                    </span>
                    <span className="muted">{untilText(s.nextUtc, now)}</span>
                  </>
                ) : (
                  <span className="muted">—</span>
                )}
              </span>
              <span className="sched-last">{s.lastRun ? <RunSummary run={s.lastRun} now={now} /> : <span className="muted">not yet</span>}</span>
              <span className="tasks-actions">
                <button
                  className="icon-button"
                  disabled={busy || !info.open || !s.job || s.active}
                  title={!info.open ? "Open the database to run a job" : s.active ? "A run of it is in the queue or running" : "Run it now, with its argument"}
                  onClick={() => void runNow(s)}
                >
                  <IconPlayerPlay size={15} stroke={1.8} />
                </button>
                <button className="icon-button" disabled={busy || locked} title={locked ? lockedTitle : "Change the schedule"} onClick={() => setEditing({ schedule: s, preset: null })}>
                  <IconPencil size={15} stroke={1.8} />
                </button>
                <button className="icon-button danger" disabled={busy || locked} title={locked ? lockedTitle : "Remove the schedule"} onClick={() => void remove(s)}>
                  <IconTrash size={15} stroke={1.8} />
                </button>
              </span>
            </div>
          ))}
        </div>
        {info.removedHere.length > 0 && (
          <div className="setting-items-removed">
            Removed on THIS SERVER, still in SHARED ({info.settingsFile}): {info.removedHere.join(", ")}. Move to shared at the top right takes the removal there, or
            takes it back.
          </div>
        )}
      </section>

      <section className="panel">
        <h3>
          Jobs <span className="panel-sub">what this database can run by name - its own maintenance, and the jobs the application registers</span>
        </h3>
        {!info.open ? (
          <div className="tasks-empty">Open the database to see its jobs. The application registers its own as the database opens.</div>
        ) : (
          <div className="sched-jobs">
            {info.jobs.map((j) => (
              <div key={j.name} className="sched-job">
                <span className="sched-job-head">
                  <span className="sched-job-name">{j.name}</span>
                  <span className={"sched-origin" + (j.builtIn ? "" : " app")}>{j.builtIn ? "built in" : "application"}</span>
                </span>
                <span className="sched-job-desc">
                  {j.description ?? <span className="muted">No description.</span>}
                  {j.argumentHelp && <span className="sched-job-arg">Argument: {j.argumentHelp}</span>}
                </span>
                <span className="sched-job-usage">
                  {j.schedules === 0 ? <span className="muted">not scheduled</span> : `${formatCount(j.schedules)} ${j.schedules === 1 ? "schedule" : "schedules"}`}
                  {j.active > 0 && <span className="tasks-type-running"> · {formatCount(j.active)} running</span>}
                  {j.lastRun && <RunSummary run={j.lastRun} now={now} compact />}
                </span>
                <span className="tasks-actions">
                  <button className="icon-button" disabled={busy} title="Run it now" onClick={() => setRunning(j)}>
                    <IconPlayerPlay size={15} stroke={1.8} />
                  </button>
                  <button
                    className="icon-button"
                    disabled={busy || locked}
                    title={locked ? lockedTitle : "Schedule it"}
                    onClick={() => setEditing({ schedule: null, preset: { job: j.name } })}
                  >
                    <IconCalendarPlus size={15} stroke={1.8} />
                  </button>
                </span>
              </div>
            ))}
            {info.jobs.length === 0 && <div className="tasks-empty">No job is registered.</div>}
          </div>
        )}
      </section>

      <section className="panel">
        <h3>
          Recent runs <span className="panel-sub">newest first, kept across restarts - a run also shows on the Queue while it waits or runs</span>
        </h3>
        <div className="sched-runs">
          {info.runs.length === 0 && <div className="tasks-empty">Nothing has run yet.</div>}
          {info.runs.map((r) => (
            <div key={r.runId} className="sched-run">
              <span className={"sched-status " + r.status.toLowerCase()}>{statusLabel(r)}</span>
              <span className="sched-run-what">
                <span className="sched-run-job">{r.jobName}</span>
                <span className="muted">{runOrigin(r)}</span>
              </span>
              <span className="tasks-time" title={runTimes(r)}>
                {formatTime(r.startedUtc ?? r.queuedUtc)}
              </span>
              <span className="tasks-time">{r.durationMs != null ? formatTook(r.durationMs) : r.status === "Running" ? untilNowText(r.startedUtc, now) : ""}</span>
              <span className={"sched-run-message" + (r.error ? " error" : "")} title={r.error ?? r.message ?? undefined}>
                {r.error ?? r.message ?? ""}
              </span>
              <span className="tasks-actions">
                {(r.status === "Running" || r.status === "Queued") && (
                  <button
                    className="icon-button"
                    disabled={busy}
                    title={r.status === "Running" ? "Ask it to stop - a job stops when it next looks" : "Take it out of the queue"}
                    onClick={() => void stop(r)}
                  >
                    <IconPlayerStop size={15} stroke={1.8} />
                  </button>
                )}
              </span>
            </div>
          ))}
        </div>
      </section>

      {editing && (
        <ScheduleEditor
          db={db}
          info={info}
          target={editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            onSaved();
            void load();
          }}
        />
      )}
      {running && (
        <RunJobDialog
          job={running}
          onClose={() => setRunning(null)}
          onRun={async (argument) => {
            setRunning(null);
            await act("start the run", () => runJob(db.id, { job: running.name, argument }));
          }}
        />
      )}
    </div>
  );
}

// ---- where a schedule comes from ----

function ScheduleSource({ schedule, info }: { schedule: ScheduleView; info: SchedulerInfo }) {
  const data = info.dataFile ?? "the overrides file";
  if (schedule.addedHere) {
    return (
      <SourceTag
        kind="data"
        small
        title={`Added on THIS SERVER, ${data}; SHARED, ${info.settingsFile}, does not have it. Move to shared at the top right takes it there.`}
      />
    );
  }
  if (schedule.changedHere.length > 0) {
    return (
      <SourceTag
        kind="data"
        small
        label="this server · changed"
        title={`In SHARED, ${info.settingsFile}, with ${schedule.changedHere.join(", ")} changed on THIS SERVER, ${data}. Move to shared at the top right takes the change there.`}
      />
    );
  }
  return (
    <SourceTag
      kind="settings"
      small
      title={`From SHARED, ${info.settingsFile} - part of the application, deployed to every installation. A change made here is saved on THIS SERVER.`}
    />
  );
}

// ---- a run, in a line ----

function RunSummary({ run, now, compact }: { run: JobRun; now: number; compact?: boolean }) {
  const when = run.finishedUtc ?? run.startedUtc ?? run.queuedUtc;
  const text = run.error ?? run.message;
  return (
    <span className={"sched-run-summary" + (compact ? " compact" : "")} title={runTimes(run) + (text ? "\n" + text : "")}>
      <span className={"sched-status " + run.status.toLowerCase()}>{statusLabel(run)}</span>
      <span className="muted">
        {run.status === "Running" ? "since " + formatTime(run.startedUtc ?? run.queuedUtc) : agoText(when, now)}
        {run.durationMs != null && ` · ${formatTook(run.durationMs)}`}
      </span>
      {!compact && text && <span className={"sched-run-text" + (run.error ? " error" : "")}>{text}</span>}
    </span>
  );
}

const statusLabels: Record<RunStatus, string> = {
  Queued: "queued",
  Running: "running",
  Succeeded: "done",
  Failed: "failed",
  Cancelled: "stopped",
  Skipped: "skipped",
  Interrupted: "interrupted",
};

function statusLabel(run: JobRun): string {
  const label = statusLabels[run.status] ?? run.status.toLowerCase();
  return run.status === "Skipped" && run.times > 1 ? `${label} ×${formatCount(run.times)}` : label;
}

/** Who started it, and with what: "by hand · count", "Nightly backup · missed run". A schedule without a name of its own is called after its job, which the line above already says. */
function runOrigin(run: JobRun): string {
  const named = run.scheduleName && run.scheduleName !== run.jobName ? run.scheduleName : null;
  const parts = [run.trigger === "Manual" ? "by hand" : (named ?? "on schedule")];
  if (run.trigger === "Manual" && named) parts.push(named);
  if (run.trigger === "Missed") parts.push("missed run");
  if (run.argument) parts.push(run.argument);
  return parts.join(" · ");
}

function runTimes(run: JobRun): string {
  const lines: string[] = [];
  if (run.dueUtc) lines.push("Due " + formatDateTime(run.dueUtc));
  lines.push((run.status === "Skipped" ? "Skipped " : "Queued ") + formatDateTime(run.queuedUtc));
  if (run.startedUtc) lines.push("Started " + formatDateTime(run.startedUtc));
  if (run.finishedUtc && run.status !== "Skipped") lines.push("Finished " + formatDateTime(run.finishedUtc));
  return lines.join("\n");
}

// ---- running a job by hand ----

function RunJobDialog({ job, onClose, onRun }: { job: JobView; onClose: () => void; onRun: (argument: string | null) => void }) {
  const [argument, setArgument] = useState("");
  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div
        className="dialog"
        role="dialog"
        aria-label={"Run " + job.name}
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            e.preventDefault();
            onClose();
          }
        }}
      >
        <h3>
          Run {job.name} now?
          <DialogTools onClose={onClose} closeTitle="Cancel" />
        </h3>
        <div className="dialog-body">
          {job.description ?? "A job the application registered."} It goes through the task queue, and waits there while a run of it is still going on.
        </div>
        {job.argumentHelp && (
          <label className="sched-field">
            <span className="sched-field-label">Argument</span>
            <input
              className="text-input"
              autoFocus
              value={argument}
              placeholder="none"
              onChange={(e) => setArgument(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && onRun(argument.trim() || null)}
            />
            <span className="sched-field-help">{job.argumentHelp}</span>
          </label>
        )}
        <div className="dialog-row">
          <div className="header-spacer" />
          <button className="action-button primary" autoFocus={!job.argumentHelp} onClick={() => onRun(argument.trim() || null)}>
            <IconPlayerPlay size={14} stroke={1.8} /> Run now
          </button>
          <button className="action-button" onClick={onClose}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}

// ---- the schedule editor ----

interface EditorTarget {
  /** the schedule to change; null adds one */
  schedule: ScheduleView | null;
  /** what a new one starts with, beyond the defaults */
  preset: { job: string } | null;
}

interface Form {
  job: string;
  name: string;
  argument: string;
  enabled: boolean;
  every: string;
  unit: ScheduleUnit;
  at: string;
  until: string;
  days: Set<number>;
  /** a value the chips cannot show - written by hand in the settings file - kept as it is until a chip is clicked */
  rawDays: string | null;
  dayOfMonth: string;
  timeZone: string;
  runMissed: boolean;
}

const units: { id: ScheduleUnit; label: string; one: string }[] = [
  { id: "Seconds", label: "seconds", one: "second" },
  { id: "Minutes", label: "minutes", one: "minute" },
  { id: "Hours", label: "hours", one: "hour" },
  { id: "Days", label: "days", one: "day" },
  { id: "Weeks", label: "weeks", one: "week" },
  { id: "Months", label: "months", one: "month" },
];
const subDay = (unit: ScheduleUnit) => unit === "Seconds" || unit === "Minutes" || unit === "Hours";

function formOf(target: EditorTarget): Form {
  const s = target.schedule;
  const parsed = parseDays(s?.days ?? null);
  return {
    job: s?.job ?? target.preset?.job ?? "",
    name: s?.name ?? "",
    argument: s?.argument ?? "",
    enabled: s?.enabled ?? true,
    every: String(s?.every ?? 1),
    unit: s?.unit ?? "Days",
    at: s?.at ?? (s ? "" : "02:00"),
    until: s?.until ?? "",
    days: parsed ?? new Set(),
    rawDays: parsed === null ? (s?.days ?? null) : null,
    dayOfMonth: s?.dayOfMonth != null ? String(s.dayOfMonth) : "1",
    timeZone: s?.timeZone ?? "",
    runMissed: s?.runMissed ?? true,
  };
}

/** What is saved: the fields the unit uses, the rest emptied, so a schedule never keeps a value that means nothing for it. */
function valuesOf(f: Form): ScheduleValues {
  const every = Math.floor(Number(f.every));
  return {
    job: f.job.trim() || null,
    name: f.name.trim() || null,
    argument: f.argument.trim() || null,
    enabled: f.enabled,
    every: Number.isFinite(every) && every >= 1 ? every : 1,
    unit: f.unit,
    at: f.at || null,
    until: subDay(f.unit) ? f.until || null : null,
    days: f.unit === "Months" ? null : (f.rawDays ?? formatDays(f.days)) || null,
    dayOfMonth: f.unit === "Months" ? Math.min(31, Math.max(1, Math.floor(Number(f.dayOfMonth)) || 1)) : null,
    timeZone: f.timeZone.trim() || null,
    runMissed: f.runMissed,
  };
}

function ScheduleEditor({ db, info, target, onClose, onSaved }: { db: DatabaseInfo; info: SchedulerInfo; target: EditorTarget; onClose: () => void; onSaved: () => void }) {
  const [form, setForm] = useState<Form>(() => formOf(target));
  // an added schedule whose save was partly refused exists from then on: saving again changes it
  const [id, setId] = useState<string | null>(target.schedule?.id ?? null);
  const [preview, setPreview] = useState<SchedulePreview | null>(null);
  const [saving, setSaving] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const values = useMemo(() => valuesOf(form), [form]);
  const job = info.jobs.find((j) => j.name.toLowerCase() === form.job.trim().toLowerCase()) ?? null;
  const zones = useMemo(timeZoneNames, []);

  // when it would run, as the server works it out, a moment after the last keystroke
  useEffect(() => {
    let cancelled = false;
    const timer = window.setTimeout(() => {
      previewSchedule(db.id, values)
        .then((p) => !cancelled && setPreview(p))
        .catch((e) => !cancelled && setPreview({ summary: "", problem: message(e), zone: null, upcoming: [] }));
    }, 250);
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [db.id, values]);

  const set = <K extends keyof Form>(key: K, value: Form[K]) => setForm((f) => ({ ...f, [key]: value }));
  const toggleDay = (day: number) =>
    setForm((f) => {
      const days = new Set(f.days);
      if (days.has(day)) days.delete(day);
      else days.add(day);
      return { ...f, days, rawDays: null };
    });

  async function save(): Promise<void> {
    if (!values.job) {
      setFormError("Choose the job to run.");
      return;
    }
    setSaving(true);
    setFormError(null);
    try {
      const result = id ? await saveSchedule(db.id, id, values) : await addSchedule(db.id, values);
      if (result.rejected.length > 0) {
        setId(result.id);
        setFormError(result.rejected.map((r) => `${fieldOf(r.path)}: ${r.reason}`).join(" "));
        return;
      }
      onSaved();
    } catch (e) {
      setFormError(message(e));
    } finally {
      setSaving(false);
    }
  }

  const unit = units.find((u) => u.id === form.unit) ?? units[3];
  const everyNumber = Number(form.every);
  const jobChoices = info.jobs.map((j) => ({ value: j.name, label: j.name, hint: j.builtIn ? "built in" : "application" }));

  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div
        className="dialog sched-editor"
        role="dialog"
        aria-label={target.schedule ? "Change the schedule" : "Add a schedule"}
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            e.preventDefault();
            onClose();
          }
        }}
      >
        <h3>
          {target.schedule ? `Change “${scheduleName(target.schedule)}”` : "Add a schedule"}
          <DialogTools onClose={onClose} closeTitle="Cancel" />
        </h3>

        <div className="sched-form">
          <label className="sched-field">
            <span className="sched-field-label">Job</span>
            <Combo label="Job" placeholder={info.open ? "pick a job" : "the job's name"} options={jobChoices} value={form.job} disabled={saving} onChange={(v) => set("job", String(v ?? ""))} />
            <span className="sched-field-help">
              {job?.description ??
                (info.open
                  ? form.job.trim()
                    ? `No job named “${form.job.trim()}” is registered on this database. The schedule is saved, and skips its runs until one is.`
                    : "One of the database's jobs - its own, or one the application registers."
                  : "The database is closed, so the jobs it has are not known; a name typed here is checked when it opens.")}
            </span>
          </label>

          {(job?.argumentHelp || form.argument) && (
            <label className="sched-field">
              <span className="sched-field-label">Argument</span>
              <input className="text-input" value={form.argument} placeholder="none" disabled={saving} onChange={(e) => set("argument", e.target.value)} />
              <span className="sched-field-help">{job?.argumentHelp ?? "Handed to the job when it runs."}</span>
            </label>
          )}

          <div className="sched-field">
            <span className="sched-field-label">Repeat</span>
            <span className="sched-inline">
              every
              <input
                className="text-input sched-every"
                type="number"
                min={1}
                value={form.every}
                disabled={saving}
                onChange={(e) => set("every", e.target.value)}
              />
              <select className="select" value={form.unit} disabled={saving} onChange={(e) => set("unit", e.target.value as ScheduleUnit)}>
                {units.map((u) => (
                  <option key={u.id} value={u.id}>
                    {everyNumber === 1 ? u.one : u.label}
                  </option>
                ))}
              </select>
            </span>
            <span className="sched-field-help">
              {subDay(form.unit)
                ? `Repeats through the day from the first time below, every ${everyNumber === 1 ? unit.one : (Number.isFinite(everyNumber) ? everyNumber : 1) + " " + unit.label}.`
                : form.unit === "Months"
                  ? "Once a month, or every few months, on the day and at the time below."
                  : "Once, at the time below. Every other day or week is counted from a fixed point in the calendar."}
            </span>
          </div>

          {form.unit === "Months" && (
            <label className="sched-field">
              <span className="sched-field-label">On day</span>
              <input
                className="text-input sched-every"
                type="number"
                min={1}
                max={31}
                value={form.dayOfMonth}
                disabled={saving}
                onChange={(e) => set("dayOfMonth", e.target.value)}
              />
              <span className="sched-field-help">1 to 31. A month without that day runs on its last day, so 31 is the last day of every month.</span>
            </label>
          )}

          <div className="sched-field">
            <span className="sched-field-label">{subDay(form.unit) ? "From" : "At"}</span>
            <span className="sched-inline">
              <input className="text-input sched-time" type="time" value={form.at} disabled={saving} onChange={(e) => set("at", e.target.value)} />
              {subDay(form.unit) && (
                <>
                  until
                  <input className="text-input sched-time" type="time" value={form.until} disabled={saving} onChange={(e) => set("until", e.target.value)} />
                </>
              )}
            </span>
            <span className="sched-field-help">
              {subDay(form.unit) ? "The first run of the day, and the last time a run may start. Empty is midnight to midnight." : "The time of day. Empty is midnight."}
            </span>
          </div>

          {form.unit !== "Months" && (
            <div className="sched-field">
              <span className="sched-field-label">On</span>
              <span className="sched-days" role="group" aria-label="Days of the week">
                {weekDays.map((d, i) => (
                  <button key={d} type="button" className={"sched-day" + (form.days.has(i) ? " on" : "")} aria-pressed={form.days.has(i)} disabled={saving} onClick={() => toggleDay(i)}>
                    {d}
                  </button>
                ))}
              </span>
              <span className="sched-field-help">
                {form.rawDays !== null
                  ? `The settings say “${form.rawDays}”, which is not a list of days. Pick the days to replace it.`
                  : form.days.size === 0
                    ? form.unit === "Weeks"
                      ? "None picked: Mondays."
                      : "None picked: every day."
                    : formatDays(form.days)}
              </span>
            </div>
          )}

          <label className="sched-field">
            <span className="sched-field-label">Time zone</span>
            <input
              className="text-input"
              list="sched-zones"
              value={form.timeZone}
              placeholder={`${info.serverTimeZone.name} - the server's`}
              disabled={saving}
              onChange={(e) => set("timeZone", e.target.value)}
            />
            <datalist id="sched-zones">
              {zones.map((z) => (
                <option key={z} value={z} />
              ))}
            </datalist>
            <span className="sched-field-help">An IANA id, like Europe/Oslo or UTC. A run at 02:00 stays at 02:00 across summer time.</span>
          </label>

          <label className="sched-field">
            <span className="sched-field-label">Name</span>
            <input className="text-input" value={form.name} placeholder={form.job.trim() || "the job's name"} disabled={saving} onChange={(e) => set("name", e.target.value)} />
            <span className="sched-field-help">Worth giving when one job has two schedules, like a daily and a weekly backup.</span>
          </label>

          <div className="sched-field">
            <span className="sched-field-label" />
            <span className="sched-checks">
              <label className="sched-check">
                <input type="checkbox" checked={form.runMissed} disabled={saving} onChange={(e) => set("runMissed", e.target.checked)} />
                Run a missed run once the database is open again
              </label>
              <label className="sched-check">
                <input type="checkbox" checked={form.enabled} disabled={saving} onChange={(e) => set("enabled", e.target.checked)} />
                Enabled
              </label>
            </span>
          </div>
        </div>

        <div className={"sched-preview" + (preview?.problem ? " problem" : "")}>
          {preview === null ? (
            <span className="muted">Working out when it runs…</span>
          ) : preview.problem ? (
            <>
              <IconAlertTriangle size={14} stroke={1.8} /> {preview.problem}
            </>
          ) : (
            <>
              <div className="sched-preview-summary">{preview.summary}</div>
              <div className="sched-preview-times">
                <span className="muted">Next{preview.zone ? ` (${preview.zone})` : ""}:</span> {preview.upcoming.map((u) => u.at ?? formatDateTime(u.utc)).join(" · ")}
              </div>
            </>
          )}
        </div>

        {formError && <div className="login-error">{formError}</div>}

        <div className="dialog-row">
          <span className="muted sched-saves-to" title={info.dataFile ?? undefined}>
            Saved on THIS SERVER
          </span>
          <button className="action-button primary" disabled={saving} onClick={() => void save()}>
            {target.schedule || id ? "Save" : "Add schedule"}
          </button>
          <button className="action-button" onClick={onClose}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}

// ---- days of the week, the way the server reads and writes them (ScheduleTimes.ParseDays/FormatDays) ----

const weekDays = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
const weekNames = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];

/** The days a value names, Monday = 0; null for a value that is not a list of days. */
function parseDays(text: string | null): Set<number> | null {
  const days = new Set<number>();
  if (!text || !text.trim()) return days;
  let normal = text.split(String.fromCharCode(0x2013)).join("-");
  while (normal.includes(" -")) normal = normal.split(" -").join("-");
  while (normal.includes("- ")) normal = normal.split("- ").join("-");
  const dayOf = (word: string) => {
    const w = word.trim().toLowerCase();
    if (w.length < 2) return -1;
    const hits = weekNames.map((n, i) => (n.startsWith(w) ? i : -1)).filter((i) => i >= 0);
    return hits.length === 1 ? hits[0] : -1;
  };
  for (const token of normal.split(/[,; ]+/).filter(Boolean)) {
    const dash = token.indexOf("-");
    if (dash > 0 && dash < token.length - 1) {
      const from = dayOf(token.slice(0, dash));
      const to = dayOf(token.slice(dash + 1));
      if (from < 0 || to < 0) return null;
      for (let i = from; ; i = (i + 1) % 7) {
        days.add(i);
        if (i === to) break;
      }
    } else {
      const day = dayOf(token);
      if (day < 0) return null;
      days.add(day);
    }
  }
  return days;
}

/** Monday first, three days or more in a row as a range; "" for none or all, which both mean every day. */
function formatDays(days: Set<number>): string {
  if (days.size === 0 || days.size === 7) return "";
  const parts: string[] = [];
  for (let i = 0; i < 7; i++) {
    if (!days.has(i)) continue;
    const start = i;
    while (i + 1 < 7 && days.has(i + 1)) i++;
    if (i - start >= 2) parts.push(weekDays[start] + "-" + weekDays[i]);
    else for (let k = start; k <= i; k++) parts.push(weekDays[k]);
  }
  return parts.join(",");
}

// ---- pieces ----

function scheduleName(s: ScheduleView): string {
  return s.name?.trim() || s.job?.trim() || "a schedule without a job";
}

function fieldOf(path: string): string {
  const close = path.lastIndexOf("].");
  return close >= 0 ? path.slice(close + 2) : path;
}

function message(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

function timeZoneNames(): string[] {
  try {
    return ["UTC", ...Intl.supportedValuesOf("timeZone").filter((z) => z !== "UTC")];
  } catch {
    return ["UTC"];
  }
}

function utcOffset(minutes: number): string {
  const sign = minutes < 0 ? "-" : "+";
  const abs = Math.abs(minutes);
  return `UTC${sign}${String(Math.floor(abs / 60)).padStart(2, "0")}:${String(abs % 60).padStart(2, "0")}`;
}

/** A clock for the "in 3 h" and "2 min ago" texts, which go stale while the samples stay the same. */
function useNow(everyMs: number): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), everyMs);
    return () => window.clearInterval(timer);
  }, [everyMs]);
  return now;
}

function span(ms: number): string {
  const s = ms / 1000;
  if (s < 60) return `${Math.max(1, Math.round(s))} s`;
  const m = s / 60;
  if (m < 60) return `${Math.round(m)} min`;
  const h = Math.floor(m / 60);
  if (h < 24) {
    const rest = Math.round(m - h * 60);
    return rest > 0 && rest < 60 ? `${h} h ${rest} min` : `${h} h`;
  }
  const d = Math.round(h / 24);
  return d === 1 ? "1 day" : `${d} days`;
}

function untilText(iso: string, now: number): string {
  const ms = new Date(iso).getTime() - now;
  return ms <= 1000 ? "due now" : "in " + span(ms);
}

function agoText(iso: string, now: number): string {
  const ms = now - new Date(iso).getTime();
  return ms < 5000 ? "just now" : span(ms) + " ago";
}

function untilNowText(iso: string | null, now: number): string {
  return iso ? span(Math.max(0, now - new Date(iso).getTime())) + " so far" : "";
}

/** A run takes milliseconds or hours, and a clock format renders the first as 00:00:00. */
function formatTook(ms: number): string {
  if (ms < 1000) return Math.round(ms) + " ms";
  if (ms < 60000) return (ms / 1000).toFixed(1) + " s";
  return span(ms);
}
