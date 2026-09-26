import { useCallback, useEffect, useState } from "react";
import {
  IconAlertTriangle,
  IconChartHistogram,
  IconCopy,
  IconDeviceFloppy,
  IconDownload,
  IconEraser,
  IconFlask,
  IconPlayerRecord,
  IconSparkles,
  IconTrash,
} from "@tabler/icons-react";
import {
  addSampleEntries,
  clearCustomLog,
  dataTypeLabel,
  deleteCustomLog,
  fetchCustomLogFiles,
  fetchDefinition,
  flushCustomLogs,
  rawFileUrl,
  rebuildCustomStatistics,
  recordCustomEntry,
  saveText,
  type CustomLogFile,
  type CustomLogSummary,
  type LogDefinition,
} from "../server/customLogs";
import { deleteFiles } from "../server/files";
import type { DatabaseInfo } from "../server/serverInfo";
import { showChoice, showConfirm, showError, showInfo, showPrompt } from "../dialogs";
import { formatBytes, formatCount, formatDateTime, formatTime } from "../format";
import { fromLocalInput, toLocalInput } from "../customLogRange";
import type { LogView } from "./CustomLogsSection";

/**
 * What a custom log has on disk, and everything that is done to it rather than read from it:
 * recording a test entry (or a few hundred made-up ones, to see the graphs before the application
 * records anything), its files with a download each, cleaning up by age, and deleting the log.
 *
 * Every deletion asks first and says what goes and what stays. None of them touches another log.
 */

const kindLabels: Record<CustomLogFile["kind"], string> = {
  entries: "Entries",
  text: "Text copy",
  statistics: "Statistics",
  "statistics-backup": "Statistics backup",
  settings: "Definition",
  "left-over": "Left over",
};

const ages = [
  { label: "1 day", days: 1 },
  { label: "7 days", days: 7 },
  { label: "30 days", days: 30 },
  { label: "90 days", days: 90 },
  { label: "1 year", days: 365 },
];

const sampleCounts = [100, 500, 2000, 10000, 100000, 1000000];

/**
 * About how much a made-up entry takes on disk, from the columns it fills: a record's own framing,
 * and per value its key, a type byte and the value. Measured against a written log it lands within a
 * few percent (a six column request log: 107 estimated, 105 written), which is what a warning about a
 * size limit needs.
 */
function bytesPerEntry(columns: { key: string; dataType: string }[]): number {
  let bytes = 24;
  for (const c of columns) {
    const value = c.dataType === "String" ? 9 : c.dataType === "Integer" ? 4 : c.dataType === "Bytes" ? 40 : 8;
    bytes += c.key.length + 2 + value;
  }
  return bytes;
}
const sampleSpans = [
  { label: "the last hour", ms: 3_600_000 },
  { label: "the last 24 hours", ms: 86_400_000 },
  { label: "the last 7 days", ms: 7 * 86_400_000 },
  { label: "the last 30 days", ms: 30 * 86_400_000 },
];

export function CustomLogData({
  db,
  log,
  tick,
  onChanged,
  onDeleted,
  onDuplicate,
  onView,
}: {
  db: DatabaseInfo;
  log: CustomLogSummary;
  tick: number;
  onChanged: () => void;
  onDeleted: () => void;
  onDuplicate: (draft: LogDefinition) => void;
  onView: (view: LogView) => void;
}) {
  const [files, setFiles] = useState<CustomLogFile[] | null>(null);
  const [ioId, setIoId] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const loadFiles = useCallback(async () => {
    try {
      const r = await fetchCustomLogFiles(db.id, log.key);
      setFiles(r.files);
      setIoId(r.ioId);
    } catch (e) {
      setFiles([]);
      showError("Could not list the files", e instanceof Error ? e.message : String(e));
    }
  }, [db.id, log.key]);
  useEffect(() => {
    loadFiles();
  }, [loadFiles, tick, log.logBytes, log.statisticsBytes]);

  async function run(what: string, action: () => Promise<unknown>) {
    setBusy(what);
    try {
      await action();
      onChanged();
      await loadFiles();
    } catch (e) {
      showError("Could not " + what, e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(null);
    }
  }

  async function deleteOlder() {
    const choices = [...ages.map((a) => ({ label: `Older than ${a.label}`, hint: `files wholly recorded before ${formatDateTime(new Date(Date.now() - a.days * 86_400_000).toISOString())}` })), { label: "Older than a date…", hint: "pick the moment" }];
    const picked = await showChoice("Delete older entries", `Entries go by whole files: ${log.name || log.key} keeps one per ${log.fileInterval.toLowerCase()}, and a file is deleted once all of it is older than the moment.`, choices);
    if (picked === null) return;
    let before: string | null;
    if (picked < ages.length) before = new Date(Date.now() - ages[picked].days * 86_400_000).toISOString();
    else {
      const read = (text: string) => fromLocalInput(text.trim().replace(" ", "T"));
      const typed = await showPrompt("Delete older entries", "The moment to delete the entries before, in local time.", {
        label: "Before (yyyy-MM-dd HH:mm)",
        initial: toLocalInput(new Date(Date.now() - 30 * 86_400_000).toISOString()).replace("T", " "),
        confirmLabel: "Next",
        validate: (text) => (read(text) ? null : "Not a date and time: write it as 2026-09-01 12:00"),
      });
      if (!typed) return;
      before = read(typed);
      if (!before) return;
    }
    const { ok } = await showConfirm("Delete older entries", `Delete the entries of ${log.name || log.key} recorded before ${formatDateTime(before)}? The statistics are kept. This cannot be undone.`, {
      confirmLabel: "Delete",
      danger: true,
    });
    if (!ok) return;
    await run("delete the entries", () => clearCustomLog(db.id, log.key, { entries: true, statistics: false, olderThanUtc: before }));
  }

  async function clear(entries: boolean, statistics: boolean) {
    const what = entries && statistics ? "every entry and all statistics" : entries ? "every recorded entry" : "the statistics";
    const kept = entries && statistics ? "The definition stays, and the log goes on recording." : entries ? "The statistics are kept, so the graphs still show what was recorded." : "The entries are kept, and the statistics can be rebuilt from them.";
    const { ok } = await showConfirm(`Clear ${log.name || log.key}`, `Delete ${what} of this log? ${kept} This cannot be undone.`, { confirmLabel: "Delete", danger: true });
    if (!ok) return;
    await run("clear the log", () => clearCustomLog(db.id, log.key, { entries, statistics }));
  }

  async function deleteLog() {
    const picked = await showChoice(`Delete ${log.name || log.key}`, "What should go with it?", [
      {
        label: "The definition only",
        hint: `the entries and statistics stay on disk (${formatBytes(log.totalBytes)}), and a log created again with the key "${log.key}" picks them up`,
      },
      { label: "The definition and everything it recorded", hint: `${formatBytes(log.totalBytes)} of entries and statistics are deleted` },
    ]);
    if (picked === null) return;
    const everything = picked === 1;
    const { ok } = await showConfirm(
      `Delete ${log.name || log.key}`,
      everything
        ? `Delete the log, its entries and its statistics? The application's records into "${log.key}" are ignored from then on. This cannot be undone.`
        : `Delete the definition of the log? The application's records into "${log.key}" are ignored from then on.`,
      { confirmLabel: "Delete the log", danger: true },
    );
    if (!ok) return;
    setBusy("delete the log");
    try {
      await deleteCustomLog(db.id, log.key, everything);
      onDeleted();
    } catch (e) {
      showError("Could not delete the log", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(null);
    }
  }

  async function deleteLeftOvers(leftOver: CustomLogFile[]) {
    if (!ioId) return;
    const { ok } = await showConfirm(
      "Delete the left-over files",
      `Delete ${leftOver.length} ${leftOver.length === 1 ? "file" : "files"} (${formatBytes(leftOver.reduce((s, f) => s + f.size, 0))}) the log wrote in a file layout it no longer uses? It cannot read them.`,
      { confirmLabel: "Delete", danger: true },
    );
    if (!ok) return;
    await run("delete the files", () => deleteFiles(ioId, leftOver.map((f) => f.fileKey)));
  }

  async function duplicate() {
    try {
      const d = await fetchDefinition(db.id, log.key);
      onDuplicate({ ...d.settings, key: `${log.key}-copy`.slice(0, 64), name: log.name ? `${log.name} (copy)` : "" });
    } catch (e) {
      showError("Could not read the definition", e instanceof Error ? e.message : String(e));
    }
  }
  async function downloadDefinition() {
    try {
      const d = await fetchDefinition(db.id, log.key);
      saveText(d.json, `log.${log.key}.settings.json`, "application/json");
    } catch (e) {
      showError("Could not read the definition", e instanceof Error ? e.message : String(e));
    }
  }

  const textBytes = (files ?? []).filter((f) => f.kind === "text").reduce((s, f) => s + f.size, 0);
  const leftOver = (files ?? []).filter((f) => f.kind === "left-over");
  const entryFiles = (files ?? []).filter((f) => f.kind === "entries").length;
  return (
    <div className="clog-data">
      <RecordPanel db={db} log={log} onRecorded={onChanged} onView={onView} />

      <section className="panel">
        <h3>
          Clean up <span className="panel-sub">what the log has recorded; the definition stays</span>
        </h3>
        <div className="clog-actions">
          <button className="action-button" onClick={deleteOlder} disabled={!!busy || log.logBytes === 0}>
            <IconTrash size={15} stroke={1.8} /> Delete older entries…
          </button>
          <button className="action-button" onClick={() => clear(true, false)} disabled={!!busy || log.logBytes === 0}>
            <IconTrash size={15} stroke={1.8} /> Delete every entry
          </button>
          <button className="action-button" onClick={() => clear(false, true)} disabled={!!busy || log.statisticsBytes === 0}>
            <IconEraser size={15} stroke={1.8} /> Delete the statistics
          </button>
          <button
            className="action-button"
            onClick={() => run("rebuild the statistics", () => rebuildCustomStatistics(db.id, log.key).then(() => showInfo("Statistics rebuilt", "The statistics were aggregated again from every entry.")))}
            disabled={!!busy || !log.enabledStatistics || log.logBytes === 0}
            title={log.enabledStatistics ? "Aggregate the statistics again from every recorded entry" : "Turn statistics on first"}
          >
            <IconChartHistogram size={15} stroke={1.8} /> Rebuild the statistics
          </button>
          <button className="action-button" onClick={() => run("write to disk", () => flushCustomLogs(db.id))} disabled={!!busy} title="Write what is buffered to disk now, rather than within half a minute">
            <IconDeviceFloppy size={15} stroke={1.8} /> Write to disk now
          </button>
        </div>
        {busy && <div className="muted clog-busy">Working: {busy}…</div>}
      </section>

      <section className="panel">
        <h3>
          The definition <code className="clog-h3-file">log/log.{log.key}.settings.json</code>
        </h3>
        <div className="clog-actions">
          <button className="action-button" onClick={() => onView("definition")}>
            Edit the definition
          </button>
          <button className="action-button" onClick={downloadDefinition}>
            <IconDownload size={15} stroke={1.8} /> Download json
          </button>
          <button className="action-button" onClick={duplicate}>
            <IconCopy size={15} stroke={1.8} /> Duplicate as a new log
          </button>
          <span className="logs-spacer" />
          <button className="action-button danger" onClick={deleteLog} disabled={!!busy}>
            <IconTrash size={15} stroke={1.8} /> Delete the log…
          </button>
        </div>
      </section>

      <section className="panel">
        <h3>
          On disk <span className="panel-sub">{formatBytes(log.totalBytes + textBytes)} in the log folder</span>
        </h3>
        <div className="clog-facts">
          <Fact label="Entries" value={log.logBytes > 0 ? `${formatBytes(log.logBytes)} in ${formatCount(entryFiles)} ${entryFiles === 1 ? "file" : "files"}` : "none"} />
          <Fact label="Statistics" value={log.statisticsBytes > 0 ? formatBytes(log.statisticsBytes) : "none saved yet"} />
          {log.enabledText && <Fact label="Text copies" value={formatBytes(textBytes)} />}
          <Fact label="First entry" value={log.firstRecordUtc ? formatDateTime(log.firstRecordUtc) : "—"} />
          <Fact label="Last entry" value={log.lastRecordUtc ? formatDateTime(log.lastRecordUtc) : "—"} />
          <Fact label="Files" value={`one per ${log.fileInterval.toLowerCase()}${log.compressed ? ", compressed" : ""}`} />
          <Fact
            label="Limits"
            value={`${log.maxAgeInDays > 0 ? `${log.maxAgeInDays} days` : "no age limit"} · ${log.maxSizeInMb > 0 ? `${log.maxSizeInMb} MB` : "no size limit"}`}
          />
        </div>
        {leftOver.length > 0 && (
          <div className="logs-note clog-bad-note">
            <IconAlertTriangle size={14} stroke={1.8} /> {leftOver.length} {leftOver.length === 1 ? "file was" : "files were"} written in a file layout the log no longer
            uses - left behind by a change that did not finish. The log cannot read {leftOver.length === 1 ? "it" : "them"}.
            <button className="link-button" onClick={() => deleteLeftOvers(leftOver)} disabled={!ioId}>
              Delete {leftOver.length === 1 ? "it" : "them"}
            </button>
          </div>
        )}
        {files && files.length > 0 && (
          <div className="log-table clog-files">
            <div className="log-table-row log-table-head clog-file-row">
              <span>File</span>
              <span>Holds</span>
              <span className="num">Size</span>
              <span />
            </div>
            {files.map((f) => (
              <div key={f.fileKey} className={"log-table-row clog-file-row" + (f.kind === "left-over" ? " left-over" : "")}>
                <span className="log-cell mono" title={f.fileKey}>
                  {f.fileKey.split("/").pop()}
                </span>
                <span className="log-cell muted">{kindLabels[f.kind]}</span>
                <span className="num">{formatBytes(f.size)}</span>
                <span>
                  {ioId && (
                    <a className="icon-button" href={rawFileUrl(db.id, ioId, f.fileKey)} download title="Download the file as it is on disk">
                      <IconDownload size={14} stroke={1.8} />
                    </a>
                  )}
                </span>
              </div>
            ))}
          </div>
        )}
        <div className="logs-chart-foot">
          <span className="muted">
            The entries files are the log's own format; the text copies and the Download on the Entries page are the ones to open elsewhere.
          </span>
        </div>
      </section>
    </div>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="clog-fact">
      <span className="clog-fact-label">{label}</span>
      <span className="clog-fact-value">{value}</span>
    </div>
  );
}

/**
 * Writing into the log from the page: one entry typed in, column by column, or a few hundred made-up
 * ones spread over a stretch of time - to try a definition out before the application records into
 * it, and to see what its graphs will look like. The values go through the same conversion as the
 * application's, so what does not fit a column is left out the same way.
 */
function RecordPanel({ db, log, onRecorded, onView }: { db: DatabaseInfo; log: CustomLogSummary; onRecorded: () => void; onView: (view: LogView) => void }) {
  const [values, setValues] = useState<Record<string, string>>({});
  const [when, setWhen] = useState("");
  const [recorded, setRecorded] = useState<string | null>(null);
  const [count, setCount] = useState(500);
  const [spanIndex, setSpanIndex] = useState(1);
  const [working, setWorking] = useState(false);
  // what is being written right now, said beside the buttons: a hundred thousand entries take a while
  const [adding, setAdding] = useState<number | null>(null);
  const off = !log.enabledLog && !log.enabledStatistics;

  async function record() {
    setWorking(true);
    try {
      const payload: Record<string, unknown> = {};
      for (const c of log.columns) {
        const text = values[c.key]?.trim() ?? "";
        if (!text) continue;
        payload[c.key] = c.dataType === "DateTime" ? (fromLocalInput(text) ?? text) : c.dataType === "Integer" || c.dataType === "Double" ? (isFinite(Number(text)) ? Number(text) : text) : text;
      }
      const result = await recordCustomEntry(db.id, log.key, payload, when ? fromLocalInput(when) : null);
      setRecorded(result.timestampUtc);
      onRecorded();
    } catch (e) {
      showError("Could not record the entry", e instanceof Error ? e.message : String(e));
    } finally {
      setWorking(false);
    }
  }

  async function sample() {
    const span = sampleSpans[spanIndex];
    // a million entries are a hundred megabytes and more: past a log's size limit the oldest files go
    // at the next clean-up, and some of what was just added with them, which is better said up front
    const estimate = count * bytesPerEntry(log.columns);
    const limit = log.maxSizeInMb * 1024 * 1024;
    const size =
      count >= 10000
        ? ` That is about ${formatBytes(estimate)} on disk${log.compressed ? " before compression" : ""}` +
          (log.maxSizeInMb > 0 && !log.compressed && log.logBytes + estimate > limit
            ? `, past the log's limit of ${formatCount(log.maxSizeInMb)} MB: the oldest files are deleted at the next clean-up, within a minute, and some of these entries with them.`
            : ".")
        : "";
    const slow =
      count >= 1000000
        ? " Writing them and rebuilding the statistics takes ten seconds or more."
        : count >= 100000
          ? " Writing them and rebuilding the statistics takes a few seconds."
          : "";
    const { ok } = await showConfirm(
      "Add made-up entries",
      `Add ${formatCount(count)} made-up entries to ${log.name || log.key}, spread over ${span.label}?${size}${slow} They mix with any real ones, and go only with the entries of their time - by deleting older entries, or all of them.`,
      { confirmLabel: "Add them" },
    );
    if (!ok) return;
    setWorking(true);
    setAdding(count);
    try {
      const result = await addSampleEntries(db.id, log.key, count, span.ms);
      onRecorded();
      showInfo(
        "Entries added",
        `${formatCount(result.recorded)} made-up entries were recorded${result.written ? "" : " into the statistics only, as entries are not being recorded"}${result.rebuilt ? ", and the statistics rebuilt so each one counts where it belongs" : ""}.`,
      );
    } catch (e) {
      showError("Could not add the entries", e instanceof Error ? e.message : String(e));
    } finally {
      setWorking(false);
      setAdding(null);
    }
  }

  return (
    <section className="panel">
      <h3>
        <IconFlask size={15} stroke={1.8} /> Try it out <span className="panel-sub">record into the log from here, before the application does</span>
      </h3>
      {off && <div className="logs-note">Recording and statistics are both off, so nothing written here would be kept. Turn one of them on at the top of the page.</div>}
      <div className="clog-record">
        <div className="clog-record-fields">
          {log.columns.map((c) => (
            <label key={c.key} className="clog-field">
              <span className="clog-field-label">
                {c.name} <span className="muted">{dataTypeLabel[c.dataType].toLowerCase()}</span>
              </span>
              <input
                className="text-input"
                type={c.dataType === "Integer" || c.dataType === "Double" ? "number" : c.dataType === "DateTime" ? "datetime-local" : "text"}
                step={c.dataType === "Double" ? "any" : undefined}
                placeholder={c.dataType === "TimeSpan" ? "milliseconds, or 00:01:30" : c.dataType === "Bytes" ? "text, stored as UTF-8" : ""}
                value={values[c.key] ?? ""}
                onChange={(e) => setValues({ ...values, [c.key]: e.currentTarget.value })}
              />
            </label>
          ))}
          <label className="clog-field">
            <span className="clog-field-label">
              Time <span className="muted">empty is now</span>
            </span>
            <input className="text-input" type="datetime-local" value={when} onChange={(e) => setWhen(e.currentTarget.value)} />
          </label>
        </div>
        <div className="clog-actions">
          <button className="action-button primary" onClick={record} disabled={working || off}>
            <IconPlayerRecord size={15} stroke={1.8} /> Record the entry
          </button>
          {recorded && (
            <span className="muted">
              Recorded at {formatTime(recorded)}.{" "}
              <button className="link-button" onClick={() => onView("entries")}>
                See it among the entries
              </button>
            </span>
          )}
        </div>
      </div>
      <div className="clog-sample">
        <span className="clog-sample-text">
          <IconSparkles size={15} stroke={1.8} /> Or fill the log with
        </span>
        <select className="select" value={count} onChange={(e) => setCount(Number(e.currentTarget.value))}>
          {sampleCounts.map((n) => (
            <option key={n} value={n}>
              {formatCount(n)} made-up entries
            </option>
          ))}
        </select>
        <span className="muted">over</span>
        <select className="select" value={spanIndex} onChange={(e) => setSpanIndex(Number(e.currentTarget.value))}>
          {sampleSpans.map((s, i) => (
            <option key={s.label} value={i}>
              {s.label}
            </option>
          ))}
        </select>
        <button className="action-button" onClick={sample} disabled={working || off || log.columns.length === 0}>
          Add them
        </button>
        {adding != null && (
          <span className="clog-sample-working">
            <span className="logs-search-pending" /> Adding {formatCount(adding)} entries{log.enabledStatistics ? " and rebuilding the statistics" : ""}…
          </span>
        )}
        <span className="muted clog-sample-note">plausible values for every column, busier by day than by night</span>
      </div>
    </section>
  );
}

