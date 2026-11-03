import { useCallback, useEffect, useState } from "react";
import type { ComponentType } from "react";
import { IconAlertTriangle, IconEye, IconPlayerStop, IconRecycle, IconRefresh, IconTrash } from "@tabler/icons-react";
import { PanelGrid, type PanelRow } from "./PanelGrid";
import { showConfirm, showError } from "../dialogs";
import {
  clearCriticalErrorLog,
  collectGarbage,
  fetchServerOverview,
  readCriticalErrorLog,
  softRestart,
  stopHost,
  type CriticalErrorLog,
  type CriticalErrorLogContent,
  type DriveInfo,
  type ProcessActionResult,
  type ServerLive,
  type ServerOverview,
} from "../server/overview";
import { subscribe, subscribeResync } from "../server/channel";
import { ProcessChart, currentCpu, currentDisk, currentIo, formatBytesPerSecond, formatPercent, hasIo, useProcessSamples, type ProcessSample } from "./ProcessChart";
import { useLive } from "../live";
import { formatBytes, formatCount, formatDuration, formatTime } from "../format";
import { Loading } from "./Loading";
import { DialogTools } from "./DialogTools";
import { CopyText } from "./CopyText";

export function Overview() {
  const [data, setData] = useState<ServerOverview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [shownLog, setShownLog] = useState<CriticalErrorLog | null>(null);
  const apply = useCallback((d: ServerOverview) => {
    setData(d);
    setError(null);
  }, []);
  // uptime and disk sizes drift rather than change: never worth sampling more often than this,
  // however fast the refresh rate is set
  useLive<ServerOverview>("server-overview", null, apply, { minMs: 10000, onError: setError });
  const load = useCallback(() => {
    fetchServerOverview()
      .then(apply)
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, [apply]);
  useEffect(() => {
    // the two things that change the page without waiting for the next sample: a database opening
    // or closing anywhere, and a resync after the stream came back
    const unsubscribeResync = subscribeResync(load);
    const unsubscribeContainers = subscribe("containers", load);
    return () => {
      unsubscribeResync();
      unsubscribeContainers();
    };
  }, [load]);
  // the process itself moves faster than the facts: read on the refresh cadence for the graph
  const samples = useProcessSamples("server-live", null, readProcess);
  if (error) return <div className="placeholder">{error}</div>;
  if (!data) return <Loading label="Loading the server overview…" />;
  const open = data.containers.filter((c) => c.state === "Open").length;
  const criticalLogs = data.criticalErrorLogs ?? [];
  // The machine, then the runtime hosting it, then the process, then where it keeps things: read
  // down the list and it runs from what could not change without moving the server to what a
  // setting decided. A fact the server reported nothing for is left out rather than shown as a dash.
  const facts: { k: string; v: string }[] = [
    ...(data.serverName ? [{ k: "Server", v: data.serverName }] : []),
    { k: "Host", v: `${data.machine} · ${data.processorCount} cores · ${data.osArchitecture}` },
    { k: "OS", v: data.os },
    { k: "Runtime", v: `${data.runtime} · ${data.serverGC ? "server GC" : "workstation GC"}` },
    { k: "Relatude.DB", v: `v${data.version}` },
    ...(data.environment ? [{ k: "Environment", v: data.environment }] : []),
    { k: "Process", v: `${data.processName} · pid ${data.processId} · ${data.processArchitecture}` },
    { k: "Server uptime", v: formatDuration(data.upTimeMs) + (data.processStartedUtc ? ` · started ${formatTime(data.processStartedUtc)}` : "") },
    { k: "Server time", v: `${serverClock(data.serverTimeUtc, data.utcOffsetMinutes)} · ${data.timeZone} (${utcOffset(data.utcOffsetMinutes)})` },
    {
      k: "Process memory",
      v:
        `${formatBytes(data.processMemoryBytes)} resident · ${formatBytes(data.managedMemoryBytes)} managed`
        + (data.memoryLimitBytes > 0 ? ` · ${formatBytes(data.memoryLimitBytes)} available to the runtime` : ""),
    },
    ...(data.threadCount ? [{ k: "Threads", v: formatCount(data.threadCount) }] : []),
    { k: "Databases", v: `${data.containers.length} · ${open} open` },
    { k: "Default database", v: data.defaultDatabase ?? "—" },
    { k: "Admin path", v: data.adminPath },
    { k: "Working folder", v: data.workingFolder },
  ];
  const hostPanel = (
    <section className="panel">
      <h3>Host</h3>
      <div className="facts-grid">
        {facts.map((f) => (
          <div key={f.k} className="fact">
            <div className="fact-k">{f.k}</div>
            <div className="fact-v" title={f.v}>
              {f.v}
            </div>
          </div>
        ))}
        {/* the drives after all of that, a square apiece like any other fact, with a bar: "410 GB
            free" says nothing until it is put beside how big the disk is */}
        {data.disks.map((d) => (
          <Drive key={d.name} drive={d} />
        ))}
      </div>
    </section>
  );

  const actionsPanel = (
    <section className="panel actions-panel">
      <h3>Process actions</h3>
      <ProcessAction
        label="Garbage collection"
        icon={IconRecycle}
        tone="ok"
        hint="the deepest collection the runtime allows, on the whole process"
        disabled={false}
        run={collectGarbage}
        onDone={load}
      />
      <ProcessAction
        label="Soft restart"
        icon={IconRefresh}
        tone="data"
        hint="re-reads settings, closes and reopens every database"
        major
        disabled={!data.restart.canSoftRestart}
        confirm={{
          title: "Soft restart?",
          body:
            "The settings file is read again and every database is closed and reopened. Requests are drained first, so nothing in flight is lost, but no database can be reached until it has reopened - replaying a large log is not quick.",
          confirmLabel: "Restart",
        }}
        run={softRestart}
      />
      <ProcessAction
        label="Stop application"
        icon={IconPlayerStop}
        hint="stops the host process"
        danger
        major
        disabled={!data.restart.canStopHost}
        confirm={{
          title: "Stop the application?",
          body:
            "The host process stops. Every database is closed and flushed first, so nothing is lost, but the server - this admin UI included - stays down until something starts it again.",
          confirmLabel: "Stop",
        }}
        run={stopHost}
      />
    </section>
  );

  // what the server wrote, shown the way it wrote it: fixed pitch, one line an entry, newest first
  const logPanel = (
    <section className="panel panel-fill">
      <h3>
        Server log <span className="panel-sub">what the host has done since it started</span>
      </h3>
      <div className="term fill-body">
        {data.serverLog.length === 0 && <div className="term-empty">Empty.</div>}
        {data.serverLog.length > 0 && <div className="term-idle term-idle-top">_</div>}
        {[...data.serverLog].reverse().map((e, i) => (
          <div key={i} className="term-line">
            <span className="term-time">{formatTime(e.timeUtc)}</span>
            <span className="term-tag" />
            <span className="term-text">{e.message}</span>
          </div>
        ))}
      </div>
    </section>
  );

  const exceptionsPanel = (
    <section className="panel">
      <h3>Startup exceptions</h3>
      {data.startupExceptions.length === 0 && <div className="muted">None.</div>}
      {data.startupExceptions.map((e, i) => (
        <div key={i} className="startup-exception">
          <div className="startup-exception-head">
            {e.container}
            {e.timeUtc ? <span className="log-time"> {formatTime(e.timeUtc)}</span> : null}
          </div>
          <div>{e.message}</div>
        </div>
      ))}
      <div className="critical-log">
        <div className="critical-log-title">Critical error log</div>
        {criticalLogs.length === 0 && <div className="muted">No critical errors logged.</div>}
        {criticalLogs.map((log) => (
          <CriticalErrorLogRow key={log.storeId} log={log} onShow={() => setShownLog(log)} onCleared={load} />
        ))}
      </div>
    </section>
  );

  // the rows of the resizable grid (see PanelGrid.tsx). Both have two panels, so the column line
  // runs the whole way down and the divider between them carries a corner handle
  const last = samples[samples.length - 1];
  const cpu = currentCpu(samples);
  const disk = currentDisk(samples);
  const io = currentIo(samples);
  const ioShown = hasIo(samples);
  const processPanel = (
    <section className="panel panel-fill">
      <h3>
        Process{" "}
        <span className="panel-sub">
          {last
            ? `${formatBytes(last.managedMemory)} in the managed heap · ${formatBytes(last.processMemory)} resident`
              + (cpu === null ? "" : ` · ${formatPercent(cpu)} cpu of ${last.processorCount} cores`)
              + (disk === null ? "" : ` · ${formatBytes(disk.free)} free on disk`)
              + (io === null ? "" : ` · ${formatBytesPerSecond(io.read.value ?? 0)} read · ${formatBytesPerSecond(io.written.value ?? 0)} written`)
            : "sampling…"}
        </span>
      </h3>
      <ProcessChart samples={samples} showDisk />
      <div className="logs-chart-foot">
        <span className="muted">
          <span className="chart-key chart-key-first">Memory</span> · <span className="chart-key chart-key-second">CPU</span>
          {disk && (
            <>
              {" · "}
              <span className="chart-key chart-key-third">Disk used</span>
            </>
          )}
          {ioShown && (
            <>
              {" · "}
              <span className="chart-key chart-key-read">Read</span> · <span className="chart-key chart-key-write">Written</span>
            </>
          )}{" "}
          — the whole server process: one heap, one cpu budget and one disk serve every database on it{ioShown ? "; the traffic is every storage provider summed" : ""}
        </span>
      </div>
    </section>
  );

  const rows: PanelRow[] = [
    { id: "process", height: 260, cells: [processPanel] },
    { id: "host", cells: [hostPanel, actionsPanel] },
    { id: "log", cells: [logPanel, exceptionsPanel] },
  ];

  return (
    <div className="overview">
      <PanelGrid id="overview" rows={rows} />
      {shownLog && (
        <CriticalErrorLogDialog
          log={shownLog}
          onClose={() => setShownLog(null)}
          onCleared={() => {
            setShownLog(null);
            load();
          }}
        />
      )}
    </div>
  );
}

function criticalErrorCount(log: CriticalErrorLog): string {
  if (log.entries === 0) return "the log is not empty";
  const errors = log.entries === 1 ? "critical error" : "critical errors";
  return log.complete ? `${formatCount(log.entries)} ${errors} on record` : `more than ${formatCount(log.entries)} ${errors} on record`;
}

function criticalErrorWhere(log: CriticalErrorLog): string {
  return `${formatBytes(log.sizeBytes)} in ${log.fileKey}${log.storage ? ` on ${log.storage}` : ""}`;
}

async function confirmAndClear(log: CriticalErrorLog): Promise<boolean> {
  const { ok } = await showConfirm(
    "Clear the critical error log?",
    `${log.fileKey} of ${log.databases.join(", ")} is deleted: ${criticalErrorCount(log)}, ${formatBytes(log.sizeBytes)}. What it recorded cannot be read again afterwards.`,
    { confirmLabel: "Clear", danger: true },
  );
  if (!ok) return false;
  try {
    await clearCriticalErrorLog(log.storeId);
    return true;
  } catch (e) {
    await showError("Could not clear the critical error log", e instanceof Error ? e.message : String(e));
    return false;
  }
}

function CriticalErrorLogRow({ log, onShow, onCleared }: { log: CriticalErrorLog; onShow: () => void; onCleared: () => void }) {
  const [busy, setBusy] = useState(false);
  async function clear() {
    setBusy(true);
    try {
      if (await confirmAndClear(log)) onCleared();
    } finally {
      setBusy(false);
    }
  }
  return (
    <div className="critical-log-row">
      <IconAlertTriangle size={16} stroke={1.8} className="critical-log-icon" />
      <div className="critical-log-text">
        <div className="critical-log-what">
          {log.databases.join(", ")}: {criticalErrorCount(log)}
        </div>
        <div className="muted">
          {log.latestUtc ? `latest ${formatTime(log.latestUtc)} · ` : ""}
          {criticalErrorWhere(log)}
        </div>
      </div>
      <div className="critical-log-actions">
        <button className="action-button" onClick={onShow} disabled={busy}>
          <IconEye size={14} stroke={1.8} /> Show
        </button>
        <button className="action-button danger" onClick={clear} disabled={busy}>
          <IconTrash size={14} stroke={1.8} /> Clear
        </button>
      </div>
    </div>
  );
}

function CriticalErrorLogDialog({ log, onClose, onCleared }: { log: CriticalErrorLog; onClose: () => void; onCleared: () => void }) {
  const [content, setContent] = useState<CriticalErrorLogContent | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    readCriticalErrorLog(log.storeId)
      .then(setContent)
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, [log.storeId]);
  const shown = content?.log ?? log;
  async function clear() {
    setBusy(true);
    try {
      if (await confirmAndClear(shown)) onCleared();
    } finally {
      setBusy(false);
    }
  }
  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div
        className="dialog critical-log-dialog"
        role="dialog"
        aria-label="Critical error log"
        tabIndex={-1}
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            e.preventDefault();
            onClose();
          }
        }}
      >
        <h3>
          <IconAlertTriangle size={16} stroke={1.9} className="critical-log-icon" /> Critical error log — {shown.databases.join(", ")}
          <DialogTools onClose={onClose} />
        </h3>
        <div className="dialog-body muted">
          {criticalErrorCount(shown)}
          {shown.latestUtc ? `, the latest ${formatTime(shown.latestUtc)}` : ""} · {criticalErrorWhere(shown)}
          {content && content.shownBytes < shown.sizeBytes ? ` · showing the last ${formatBytes(content.shownBytes)}` : ""}
        </div>
        {error && <div className="dialog-error">{error}</div>}
        {!content && !error && <Loading label="Reading the critical error log…" />}
        {content && <pre className="term critical-log-content">{content.text.trim() || "Empty."}</pre>}
        <div className="dialog-row">
          {content && <CopyText text={content.text} title="Copy the log" />}
          <div className="header-spacer" />
          <button className="action-button danger" onClick={clear} disabled={busy || !content || shown.sizeBytes === 0}>
            Clear log
          </button>
          <button className="action-button" onClick={onClose}>
            Close
          </button>
        </div>
      </div>
    </div>
  );
}

// one reading of the process, in the shape the chart samples
function readProcess(live: ServerLive): ProcessSample {
  return {
    at: new Date(live.sampledUtc).getTime(),
    iso: live.sampledUtc,
    managedMemory: live.managedMemory ?? 0,
    processMemory: live.processMemory ?? 0,
    processorTimeMs: live.processorTimeMs ?? 0,
    processorCount: live.processorCount ?? 1,
    diskTotalBytes: live.diskTotalBytes ?? 0,
    diskFreeBytes: live.diskFreeBytes ?? 0,
    io: live.io,
  };
}

/**
 * The clock the SERVER reads, not the one the browser does. The two are usually the same machine and
 * then this changes nothing - but on a server in another zone, formatting its instant locally and
 * labelling it with the server's zone would print a time that is neither. The instant is shifted by
 * the server's offset and then formatted as UTC, which is its wall clock in the reader's format.
 */
function serverClock(utcIso: string, offsetMinutes: number): string {
  const shifted = new Date(new Date(utcIso).getTime() + offsetMinutes * 60000);
  return shifted.toLocaleString(undefined, { timeZone: "UTC" });
}

/** "UTC+02:00" - the offset the server's own clock runs at, beside the time it reads. */
function utcOffset(minutes: number): string {
  const sign = minutes < 0 ? "-" : "+";
  const abs = Math.abs(minutes);
  return `UTC${sign}${String(Math.floor(abs / 60)).padStart(2, "0")}:${String(abs % 60).padStart(2, "0")}`;
}

/**
 * One drive, in a square of the facts grid like the facts before it: which drive, what is left of
 * it, and a bar of how full it is. What of the server is on it, and the exact numbers, are in the
 * tooltip. The bar is a quiet grey while there is room and only takes a colour when the disk is
 * filling (amber) or nearly gone (red) - a database whose disk is nearly full is about to stop being
 * a database, and that is worth seeing before it happens rather than in the log after.
 */
function Drive({ drive }: { drive: DriveInfo }) {
  const used = Math.max(0, drive.totalBytes - drive.freeBytes);
  const percent = drive.totalBytes > 0 ? (used / drive.totalBytes) * 100 : 0;
  const tone = percent >= 95 ? " bad" : percent >= 85 ? " warn" : "";
  const details =
    `${drive.name}${drive.label ? " " + drive.label : ""}${drive.format ? " · " + drive.format : ""}\n` +
    `${formatBytes(used)} of ${formatBytes(drive.totalBytes)} used · ${formatPercent(percent)}\n` +
    `on it: ${drive.uses.join(", ")}`;
  return (
    <div className="fact host-disk" title={details}>
      <div className="fact-k">
        Disk {drive.name}
        {drive.label && <span className="host-disk-label"> {drive.label}</span>}
      </div>
      <div className="fact-v">
        {formatBytes(drive.freeBytes)} free of {formatBytes(drive.totalBytes)}
      </div>
      <div className="host-disk-bar">
        <div className={"host-disk-fill" + tone} style={{ width: Math.max(1, Math.min(100, percent)) + "%" }} />
      </div>
    </div>
  );
}

function ProcessAction({
  label,
  icon: Icon,
  tone,
  hint,
  danger,
  major,
  disabled,
  confirm,
  run,
  onDone,
}: {
  label: string;
  /** what the action does, in a picture; a danger one takes its colour from the button instead */
  icon: ComponentType<{ size?: number; stroke?: number; className?: string }>;
  /** colours the icon the way the storage page does: the tone of the thing acted on */
  tone?: "ok" | "data" | "accent";
  hint: string;
  danger?: boolean;
  /** one of the two that take the server down, for a moment or for good: a bigger button, found at once */
  major?: boolean;
  disabled: boolean;
  confirm?: { title: string; body: string; confirmLabel: string };
  run: () => Promise<ProcessActionResult>;
  onDone?: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  async function click() {
    if (confirm) {
      const { ok } = await showConfirm(confirm.title, confirm.body, { confirmLabel: confirm.confirmLabel, danger: danger ?? true });
      if (!ok) return;
    }
    setBusy(true);
    try {
      const result = await run();
      setMessage(result.message);
      onDone?.();
    } catch (e) {
      setMessage(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }
  return (
    <div className={"process-action" + (major ? " major" : "")}>
      <button className={"action-button" + (danger ? " danger" : "")} onClick={click} disabled={disabled || busy}>
        <Icon size={major ? 17 : 14} stroke={1.8} className={tone ? "tone-" + tone : undefined} /> {label}
      </button>
      <span className="muted">{disabled ? "not available on this server" : (message ?? hint)}</span>
    </div>
  );
}
