import { useEffect, useMemo, useState } from "react";
import { IconAlertTriangle, IconArrowsMaximize, IconDatabase, IconFileSearch, IconHistory, IconRefresh } from "@tabler/icons-react";
import { DialogTools } from "./DialogTools";
import { LogTimelineView, formatSpan, type TimelineView } from "./LogTimelineView";
import {
  fetchLogFiles,
  fetchTimeTravelInfo,
  scanLogFile,
  scanLogFileClosed,
  timeTravel,
  type LogFileInfo,
  type LogFileSource,
  type LogTimeline,
  type TimeTravelInfo,
  type TimeTravelResult,
} from "../server/storage";
import type { DatabaseInfo } from "../server/serverInfo";
import { runWithProgress, showConfirm, showInfo } from "../dialogs";
import { formatBytes, formatCount, formatDateTime } from "../format";

/**
 * Picking the file to go back in, and the moment to go back to.
 *
 * Going back in time is a copy of a database log file that stops at a moment, put in place as the
 * database. Two things have to be chosen for that, and this dialog is both of them:
 *
 *   - which log file. The one the database is running on is the usual answer and is selected to
 *     begin with, but every log file the server can see is offered: the ones the database has moved
 *     on from (a time travel leaves one behind every time), and every backup, in each storage the
 *     database has. Going back into a backup is one operation here rather than a restore followed by
 *     a second trip;
 *   - which moment. A bare datetime box is a poor place to decide that, so the moments the file
 *     itself knows are listed beside it, and the file can be scanned and drawn as a timeline of
 *     everything ever written to it, to pick from by pointing at it. The scan reads the whole file,
 *     so it is offered rather than done: the moments below the box are enough on their own, and a
 *     reader who knows when the mistake was made does not need a picture of it.
 *
 * The dialog is its own confirmation: its body says what happens and the button is the one that does
 * it. A second "are you sure" on top of a dialog somebody opened, picked a file in and typed a time
 * into reads as a stutter, and there is nothing it could add - nothing here deletes anything.
 */
export function TimeTravelDialog({
  db,
  initialFile,
  onCancel,
  onDone,
}: {
  db: DatabaseInfo;
  /** The file the dialog was opened from, on the Files page; the database's own file otherwise. */
  initialFile?: LogFileSource | null;
  onCancel: () => void;
  onDone: (result: TimeTravelResult) => void;
}) {
  const [info, setInfo] = useState<TimeTravelInfo | null>(null);
  const [files, setFiles] = useState<LogFileInfo[] | null>(null);
  const [selected, setSelected] = useState<string | null>(initialFile ? sourceKey(initialFile) : null);
  // What has been scanned, per file, so going back to one already looked at costs nothing. `shown`
  // is the picture on screen and `whole` the last scan that covered the file: a scan of one stretch
  // replaces the picture but says nothing about where the file begins and ends, and the moments
  // beside the field are about the file.
  const [scans, setScans] = useState<Record<string, { shown: LogTimeline; whole: LogTimeline | null }>>({});
  const [view, setView] = useState<TimelineView | null>(null);
  const [value, setValue] = useState("");
  const [logScale, setLogScale] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const file = files?.find((f) => sourceKey(f) === selected) ?? null;
  const source: LogFileSource | null = file ? { ioId: file.ioId, key: file.key } : null;
  const scan = selected ? (scans[selected] ?? null) : null;
  const timeline = scan?.shown ?? null;

  // What the selected file is known to begin and end at. A scan of the whole file answers both;
  // without one the header gives the beginning, and only the database can say where its own open log
  // file stands.
  const firstUtc = scan?.whole?.firstUtc ?? (file?.isCurrent ? (file.firstChangeUtc ?? info?.firstChangeUtc ?? null) : (file?.firstChangeUtc ?? null));
  const lastUtc = scan?.whole?.lastUtc ?? (file?.isCurrent ? (info?.lastChangeUtc ?? null) : null);

  async function load(keepSelection: boolean) {
    try {
      const [nextInfo, nextFiles] = await Promise.all([fetchTimeTravelInfo(db.id), fetchLogFiles(db.id, initialFile)]);
      setInfo(nextInfo);
      setFiles(nextFiles);
      setError(null);
      // the file the dialog was opened on, else the database's own; on a refresh the one already
      // selected, unless it has gone (deleted elsewhere, or a backup that has just been rolled)
      const keep = keepSelection && nextFiles.some((f) => sourceKey(f) === selected);
      if (!keep) {
        const wanted = initialFile ? sourceKey(initialFile) : null;
        const pick = nextFiles.find((f) => sourceKey(f) === wanted) ?? nextFiles.find((f) => f.isCurrent) ?? nextFiles[0];
        setSelected(pick ? sourceKey(pick) : null);
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  useEffect(() => {
    void load(false);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [db.id]);

  // The moment starts at the end of the file that is selected: going back starts from where the
  // file is now, and every moment picked afterwards takes something away. Where the end is not known
  // - an unscanned file - the moment it was written is the stand-in, since it is after everything in
  // the file; starting at the first transaction instead would open on "leave out all of it", which
  // is not where anybody means to start.
  //
  // Switching files moves it, because the moment belongs to the file it is read against. Not to what
  // a scan later finds out about the same file, though: by then the reader may have typed a moment
  // of their own, and a picture they asked for must not take it away from them.
  useEffect(() => {
    const end = lastUtc ?? file?.backupUtc ?? file?.modifiedUtc ?? firstUtc;
    setValue(end ? toLocalInput(end) : toLocalInput(new Date().toISOString()));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selected, files]);

  // a fresh scan is shown whole; the view is what zooming moves, not the scan
  useEffect(() => {
    setView(timeline ? wholeView(timeline) : null);
  }, [timeline]);

  const when = value ? new Date(value) : null;
  const valid = when !== null && !Number.isNaN(when.getTime()) && file !== null && file.error === null;
  const pickedMs = when && !Number.isNaN(when.getTime()) ? when.getTime() : null;
  const groups = useMemo(() => groupByStorage(files ?? []), [files]);
  // counted off the scan that covered the file: a scan of one stretch knows nothing about what the
  // rest of the file holds, so it cannot say how much a moment would keep
  const split = useMemo(() => (scan?.whole && pickedMs != null ? splitAt(scan.whole, pickedMs) : null), [scan?.whole, pickedMs]);
  const zoomed = timeline != null && view != null && !sameView(view, wholeView(timeline));

  const moments: { label: string; utc: string | null; hint: string }[] = [
    { label: "First transaction", utc: firstUtc, hint: "everything the file holds" },
    {
      label: "Last transaction",
      utc: lastUtc,
      hint: lastUtc
        ? file?.isCurrent
          ? "where the database is now - nothing would be left out"
          : "the end of this file - all of it would be copied"
        : "only a scan of the file can say where it ends",
    },
    {
      label: "Last revert window",
      utc: info?.revertWindowUtc ?? null,
      hint: info?.revertWindowUtc
        ? (info.revertWindowActive ? "still open, begun " : "begun ") + formatDateTime(info.revertWindowBegunUtc ?? info.revertWindowUtc)
        : "no revert window has been begun on this database",
    },
  ];

  /**
   * Reads the file and draws it. `range` is a stretch of a picture already drawn, which is scanned
   * again to see it more closely - the positions of its slices are transaction boundaries, so only
   * that much of the file is read.
   *
   * The file a running database holds is the one case that needs the database out of the way: it
   * keeps its log file to itself, so the scan closes it and opens it again afterwards. That is asked
   * first, since it takes the database away from whatever is using it.
   */
  async function runScan(range?: { fromPosition: number; toPosition: number }) {
    if (!file || !source) return;
    if (file.inUse) {
      const choice = await showConfirm(
        `Close ${db.name} to read its file?`,
        `The database is open and holds ${file.name} to itself, so nothing else can read it. It is closed for the length of the`
          + " scan and opened again afterwards, and the scan only reads: nothing is written and nothing is changed.",
        { confirmLabel: "Close and scan" },
      );
      if (!choice.ok) return;
    }
    const target = source;
    const result = await runWithProgress(`Scan ${file.name}`, (ctl) =>
      file.inUse ? scanLogFileClosed(ctl, db.id, target, range) : scanLogFile(ctl, db.id, target, range),
    );
    if (!result) return; // cancelled or failed; the progress dialog has said which
    setScans((prev) => {
      const key = sourceKey(target);
      return { ...prev, [key]: { shown: result, whole: range ? (prev[key]?.whole ?? null) : result } };
    });
  }

  /** The stretch being looked at, as the positions of the slices it covers; see LogFileScan.Timeline. */
  function rangeOfView(): { fromPosition: number; toPosition: number } | undefined {
    if (!timeline || !view) return undefined;
    const inView = timeline.slices.filter((s) => s.lastMs >= view.fromMs && s.firstMs <= view.toMs);
    if (inView.length === 0) return undefined;
    return { fromPosition: inView[0].startPosition, toPosition: inView[inView.length - 1].endPosition };
  }

  /**
   * The database as the file was at the moment picked. The copy is written beside the database file
   * in use and the database opens on it; the file copied from is only read, so this is reversible
   * from the Files page whichever file it was.
   */
  async function onGoBack() {
    if (!valid || !when || !source) return;
    const result = await runWithProgress(`Go back in time — ${db.name}`, (ctl) => timeTravel(ctl, db.id, when, source));
    if (!result) return; // cancelled or failed; the dialog stays, so it can be tried again
    onDone(result);
    // what the operation was for, said as the one number that answers it: where the database ends now
    await showInfo(
      "The database went back in time",
      result.lastChangeUtc
        ? `The last transaction is now ${formatDateTime(result.lastChangeUtc)}.`
        : "The copy holds no transactions at all: the database is empty.",
      [
        `New database file: ${result.newKey} (${formatBytes(result.bytesKept)}), copied from ${result.sourceKey}`,
        `Kept ${formatCount(result.transactionsKept)} transaction${result.transactionsKept === 1 ? "" : "s"}`,
        `Left out ${formatCount(result.transactionsDropped)} transaction${result.transactionsDropped === 1 ? "" : "s"}`
          + ` with ${formatCount(result.actionsDropped)} action${result.actionsDropped === 1 ? "" : "s"} (${formatBytes(result.bytesDropped)})`
          + (result.droppedFromUtc ? `, up to ${formatDateTime(result.droppedFromUtc)}` : ""),
        `The database file that was in place is kept as ${result.previousKey}`,
      ],
    );
  }

  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onCancel()}>
      <div
        className="dialog time-travel-dialog"
        role="dialog"
        aria-label="Go back in time"
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            e.preventDefault();
            onCancel();
          }
        }}
      >
        <h3 className="dialog-title-error">
          <IconAlertTriangle size={16} stroke={2} /> Go back in time — {db.name}
          <DialogTools onClose={onCancel} closeTitle="Cancel" />
        </h3>
        <div className="dialog-body">
          The file below is copied up to the moment you pick and {db.name} is reopened on the copy. Everything written after that moment is left
          out of the copy. Nothing is deleted: the file is only read, {info?.currentKey ?? "the database file"}
          {info ? ` (${formatBytes(info.size)})` : ""} is kept beside the new one, and the Files page can make either the database again.
        </div>
        {error && <div className="login-error">{error}</div>}
        <div className="tt-body">
          <div className="tt-files">
            <div className="tt-files-head">
              <span className="muted">Database files</span>
              <button className="icon-button" title="Look for database files again" onClick={() => void load(true)}>
                <IconRefresh size={14} stroke={1.8} />
              </button>
            </div>
            <div className="tt-files-list">
              {files === null && <div className="muted tt-note">Looking for database files…</div>}
              {files !== null && files.length === 0 && <div className="muted tt-note">No database file was found in any storage.</div>}
              {groups.map((group) => (
                <div key={group.ioId} className="tt-files-group">
                  {groups.length > 1 && <div className="tt-files-group-head muted">{group.ioName}</div>}
                  {group.files.map((f) => (
                    <button
                      key={sourceKey(f)}
                      className={"tt-file" + (sourceKey(f) === selected ? " selected" : "") + (f.error ? " unreadable" : "")}
                      onClick={() => setSelected(sourceKey(f))}
                      title={f.key + (f.error ? " — " + f.error : "")}
                    >
                      <span className="tt-file-row">
                        <span className="tt-file-name">{f.name}</span>
                        <span className="tt-file-size">{formatBytes(f.size)}</span>
                      </span>
                      <span className="tt-file-row muted">
                        <span>
                          {f.folder || "storage root"}
                          {f.isCurrent && <span className="tt-badge current">current</span>}
                          {f.inUse && <span className="tt-badge">in use</span>}
                          {f.keepForever && <span className="tt-badge">kept</span>}
                        </span>
                        <span className="tt-file-time">
                          {f.error
                            ? "unreadable"
                            : f.backupUtc
                              ? formatDateTime(f.backupUtc)
                              : f.firstChangeUtc
                                ? "from " + formatDateTime(f.firstChangeUtc)
                                : f.inUse
                                  ? "held by the database"
                                  : "empty"}
                        </span>
                      </span>
                    </button>
                  ))}
                </div>
              ))}
            </div>
          </div>
          <div className="tt-main">
            <div className="tt-toolbar">
              <IconDatabase size={14} stroke={1.8} className="tone-data" />
              <span className="tt-toolbar-name">{file ? file.key : "No file selected"}</span>
              <span className="muted">
                {timeline
                  ? `${formatCount(timeline.transactions)} transaction${timeline.transactions === 1 ? "" : "s"}`
                    + `, ${formatCount(timeline.actions)} action${timeline.actions === 1 ? "" : "s"}`
                    + ` · ${formatSpan(timeline.sliceMs)} per column`
                  : "not scanned"}
              </span>
              <div className="header-spacer" />
              {timeline && (
                <label className="tt-switch" title="Write bursts are orders of magnitude apart; a log axis keeps the quiet stretches visible">
                  <input type="checkbox" checked={logScale} onChange={(e) => setLogScale(e.target.checked)} />
                  Log scale
                </label>
              )}
              {zoomed && (
                <button className="action-button" onClick={() => timeline && setView(wholeView(timeline))} title="Show the whole of what was scanned">
                  <IconArrowsMaximize size={14} stroke={1.8} /> Zoom out
                </button>
              )}
              {zoomed && (
                <button
                  className="action-button"
                  onClick={() => void runScan(rangeOfView())}
                  title="Read this stretch of the file again, so the picture of it is as fine as the file allows"
                >
                  <IconFileSearch size={14} stroke={1.8} /> Scan this range
                </button>
              )}
              <button className="action-button" disabled={!file || file.error !== null} onClick={() => void runScan()}>
                <IconFileSearch size={14} stroke={1.8} /> {timeline ? (scan?.whole === timeline ? "Scan again" : "Scan whole file") : "Scan file"}
              </button>
            </div>
            {timeline && view ? (
              <LogTimelineView
                timeline={timeline}
                view={view}
                onView={setView}
                pickedMs={pickedMs}
                onPick={(ms) => setValue(toLocalInput(new Date(ms).toISOString()))}
                logScale={logScale}
              />
            ) : (
              <div className="tt-plot-empty">
                <IconFileSearch size={22} stroke={1.4} />
                <div>{file?.error ?? "Scan the file to see everything ever written to it, and pick a moment by pointing at it."}</div>
                <div className="muted">
                  {file?.error
                    ? "This file cannot be read, so there is nothing to go back into."
                    : "Reading the whole file takes as long as it takes; it is not needed to pick a moment below."}
                </div>
              </div>
            )}
            <div className="tt-pick">
              <label className="dialog-field tt-field">
                <span className="muted">Go back to</span>
                {/* to the millisecond, so a moment picked off the timeline lands where it was
                    pointed at rather than at the second it rounds to */}
                <input
                  className="text-input"
                  type="datetime-local"
                  step={0.001}
                  autoFocus
                  value={value}
                  onChange={(e) => setValue(e.target.value)}
                />
              </label>
              <div className="tt-moments">
                {moments.map((moment) => (
                  <button
                    key={moment.label}
                    className="dialog-choice tt-moment"
                    disabled={!moment.utc}
                    onClick={() => moment.utc && setValue(toLocalInput(moment.utc))}
                    title={moment.hint}
                  >
                    <span className="tt-moment-label">{moment.label}</span>
                    <span className="tt-moment-time">{moment.utc ? formatDateTime(moment.utc) : "—"}</span>
                  </button>
                ))}
              </div>
            </div>
            {split && (
              <div className="muted tt-summary">
                About {formatCount(split.kept)} transaction{split.kept === 1 ? "" : "s"} would be kept and {formatCount(split.dropped)} left out
                {split.uncertain > 0 && `, with ${formatCount(split.uncertain)} in the column the moment falls inside`}. The copy is cut at the
                last transaction at or before the moment, so the exact numbers are the ones reported when it is done.
              </div>
            )}
          </div>
        </div>
        <div className="dialog-row">
          {info && !info.open && <span className="muted">The database is closed.</span>}
          <div className="header-spacer" />
          <button className="action-button dialog-confirm danger" disabled={!valid} onClick={() => void onGoBack()}>
            <IconHistory size={14} stroke={1.8} /> Go back
          </button>
          <button className="action-button" onClick={onCancel}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}

/** A file's identity across the two storages it could be named in: the provider and the key in it. */
function sourceKey(source: LogFileSource): string {
  return source.ioId + "|" + source.key;
}

interface StorageGroup {
  ioId: string;
  ioName: string;
  files: LogFileInfo[];
}

/** The files as the list draws them: one block per storage, in the order the server listed them. */
function groupByStorage(files: LogFileInfo[]): StorageGroup[] {
  const groups: StorageGroup[] = [];
  for (const file of files) {
    const last = groups[groups.length - 1];
    if (last && last.ioId === file.ioId) last.files.push(file);
    else groups.push({ ioId: file.ioId, ioName: file.ioName, files: [file] });
  }
  return groups;
}

function wholeView(timeline: LogTimeline): TimelineView {
  const slices = timeline.slices;
  if (slices.length === 0) return { fromMs: 0, toMs: 1 };
  const fromMs = slices[0].fromMs;
  const toMs = Math.max(slices[slices.length - 1].toMs, fromMs + 1);
  return { fromMs, toMs };
}

const sameView = (a: TimelineView, b: TimelineView) => a.fromMs === b.fromMs && a.toMs === b.toMs;

/**
 * What a moment would keep and leave out, counted off the slices. The slice the moment falls inside
 * holds transactions on both sides of it and the scan did not record where, so it is counted apart
 * rather than guessed at - the numbers that matter are the ones the copy reports when it is made.
 */
function splitAt(timeline: LogTimeline, ms: number): { kept: number; dropped: number; uncertain: number } {
  let kept = 0;
  let dropped = 0;
  let uncertain = 0;
  for (const slice of timeline.slices) {
    if (slice.lastMs <= ms) kept += slice.transactions;
    else if (slice.firstMs > ms) dropped += slice.transactions;
    else uncertain += slice.transactions;
  }
  return { kept, dropped, uncertain };
}

/**
 * A UTC instant as a datetime-local value: local time, to the millisecond, without a zone.
 *
 * To the millisecond and not to the second, because a moment is picked by pointing at the timeline
 * as often as by typing, and a picture of five seconds of writes rounded to whole seconds would put
 * the line a fifth of the plot away from where the reader pointed.
 *
 * Rounded up rather than truncated, for the same reason a moment is picked at all: everything filled
 * in here - the first transaction, the last one, the end of a column - is a moment the reader wants
 * inside the copy, and the log is written in ticks of a ten-millionth of a second, so a moment
 * truncated to the millisecond falls just before the transaction it was taken from.
 */
export function toLocalInput(utc: string): string {
  const date = new Date(utc);
  if (Number.isNaN(date.getTime())) return "";
  if (/\.\d{3}\d*[1-9]/.test(utc)) date.setTime(date.getTime() + 1); // a Date holds three digits; .NET writes seven
  const pad = (n: number, width = 2) => n.toString().padStart(width, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`
    + `:${pad(date.getSeconds())}.${pad(date.getMilliseconds(), 3)}`;
}
