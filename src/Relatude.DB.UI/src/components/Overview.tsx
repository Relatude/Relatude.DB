import { useCallback, useEffect, useState } from "react";
import type { ComponentType } from "react";
import { IconPlayerStop, IconRecycle, IconRefresh } from "@tabler/icons-react";
import { PanelGrid, type PanelRow } from "./PanelGrid";
import { showConfirm } from "../dialogs";
import {
  collectGarbage,
  fetchServerOverview,
  softRestart,
  stopHost,
  type DriveInfo,
  type ProcessActionResult,
  type ServerLive,
  type ServerOverview,
} from "../server/overview";
import { subscribe, subscribeResync } from "../server/channel";
import { ProcessChart, currentCpu, currentDisk, formatPercent, useProcessSamples, type ProcessSample } from "./ProcessChart";
import { useLive } from "../live";
import { formatBytes, formatCount, formatDuration, formatTime } from "../format";

export function Overview() {
  const [data, setData] = useState<ServerOverview | null>(null);
  const [error, setError] = useState<string | null>(null);
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
  if (!data) return null;
  const open = data.containers.filter((c) => c.state === "Open").length;
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
    { k: "Settings file", v: data.settingsFile },
    { k: "Working folder", v: data.workingFolder },
    ...(data.tempFolder ? [{ k: "Temporary files", v: data.tempFolder }] : []),
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
      </div>
      {/* the drives under all of that: a bar apiece, because "410 GB free" says nothing until it is
          put beside how big the disk is */}
      {data.disks.length > 0 && (
        <div className="host-disks">
          {data.disks.map((d) => (
            <Drive key={d.name} drive={d} />
          ))}
        </div>
      )}
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
    </section>
  );

  // the rows of the resizable grid (see PanelGrid.tsx). Both have two panels, so the column line
  // runs the whole way down and the divider between them carries a corner handle
  const last = samples[samples.length - 1];
  const cpu = currentCpu(samples);
  const disk = currentDisk(samples);
  const processPanel = (
    <section className="panel panel-fill">
      <h3>
        Process{" "}
        <span className="panel-sub">
          {last
            ? `${formatBytes(last.managedMemory)} in the managed heap · ${formatBytes(last.processMemory)} resident`
              + (cpu === null ? "" : ` · ${formatPercent(cpu)} cpu of ${last.processorCount} cores`)
              + (disk === null ? "" : ` · ${formatBytes(disk.free)} free on disk`)
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
          )}{" "}
          — the whole server process: one heap, one cpu budget and one disk serve every database on it
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
 * One drive, as a bar: how much of it is gone, what is left, and what of the server put it on the
 * list. It turns amber and then red as it fills - a database whose disk is nearly full is about to
 * stop being a database, and that is worth seeing before it happens rather than in the log after.
 */
function Drive({ drive }: { drive: DriveInfo }) {
  const used = Math.max(0, drive.totalBytes - drive.freeBytes);
  const percent = drive.totalBytes > 0 ? (used / drive.totalBytes) * 100 : 0;
  const tone = percent >= 95 ? " bad" : percent >= 85 ? " warn" : "";
  return (
    <div className="host-disk">
      <div className="host-disk-head">
        <span className="host-disk-name" title={drive.name + (drive.format ? " · " + drive.format : "")}>
          {drive.name}
          {drive.label && <span className="muted"> {drive.label}</span>}
        </span>
        <span className="muted host-disk-uses" title={"what of the server is on this drive: " + drive.uses.join(", ")}>
          {drive.uses.join(", ")}
        </span>
        <span className="host-disk-free">{formatBytes(drive.freeBytes)} free</span>
      </div>
      <div className="host-disk-bar" title={`${formatBytes(used)} of ${formatBytes(drive.totalBytes)} used`}>
        <div className={"host-disk-fill" + tone} style={{ width: Math.max(1, Math.min(100, percent)) + "%" }} />
      </div>
      <div className="host-disk-foot muted">
        {formatBytes(used)} of {formatBytes(drive.totalBytes)} · {formatPercent(percent)} used
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
    <div className="process-action">
      <button className={"action-button" + (danger ? " danger" : "")} onClick={click} disabled={disabled || busy}>
        <Icon size={14} stroke={1.8} className={tone ? "tone-" + tone : undefined} /> {label}
      </button>
      <span className="muted">{disabled ? "not available on this server" : (message ?? hint)}</span>
    </div>
  );
}
