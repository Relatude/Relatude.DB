import { useCallback, useEffect, useState } from "react";
import { PanelGrid, type PanelRow } from "./PanelGrid";
import { showConfirm } from "../dialogs";
import { subscribe, subscribeResync } from "../server/channel";
import { collectGarbage, fetchServerLive, fetchServerOverview, softRestart, stopHost, type ProcessActionResult, type ServerOverview } from "../server/overview";
import { ProcessChart, currentCpu, formatPercent, useProcessSamples, type ProcessSample } from "./ProcessChart";
import { usePoll } from "../refresh";
import { formatBytes, formatDuration, formatTime } from "../format";

export function Overview() {
  const [data, setData] = useState<ServerOverview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => {
    fetchServerOverview()
      .then((d) => {
        setData(d);
        setError(null);
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, []);
  useEffect(() => {
    load();
    const unsubscribeResync = subscribeResync(load);
    const unsubscribeContainers = subscribe("containers", load); // any database change refreshes the page
    return () => {
      unsubscribeResync();
      unsubscribeContainers();
    };
  }, [load]);
  // uptime and memory drift rather than change: never worth asking more often than this, however
  // fast the refresh rate is set
  usePoll(load, { minMs: 10000 });
  // the process itself moves faster than the facts: sampled at the refresh rate for the graph
  const samples = useProcessSamples(readProcess);
  if (error) return <div className="placeholder">{error}</div>;
  if (!data) return null;
  const open = data.containers.filter((c) => c.state === "Open").length;
  const facts = [
    { k: "Server uptime", v: formatDuration(data.upTimeMs) },
    { k: "Host", v: `${data.machine} · ${data.processorCount} cores` },
    { k: "OS", v: data.os },
    { k: "Runtime", v: data.runtime },
    { k: "Relatude.DB", v: `v${data.version}` },
    { k: "Process memory", v: `${formatBytes(data.processMemoryBytes)} · ${formatBytes(data.managedMemoryBytes)} managed` },
    { k: "Databases", v: `${data.containers.length} · ${open} open` },
    { k: "Admin path", v: data.adminPath },
    { k: "Settings file", v: data.settingsFile },
    { k: "Default database", v: data.defaultDatabase ?? "—" },
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
    </section>
  );

  const actionsPanel = (
    <section className="panel actions-panel">
      <h3>Process actions</h3>
      <ProcessAction label="Garbage collection" hint="deep, blocking, compacting collection" disabled={false} run={collectGarbage} onDone={load} />
      <ProcessAction
        label="Soft restart"
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

  // what the server wrote, shown the way it wrote it: fixed pitch, one line an entry, newest last
  const logPanel = (
    <section className="panel panel-fill">
      <h3>
        Server log <span className="panel-sub">what the host has done since it started</span>
      </h3>
      <div className="term fill-body">
        {data.serverLog.length === 0 && <div className="term-empty">Empty.</div>}
        {data.serverLog.map((e, i) => (
          <div key={i} className="term-line">
            <span className="term-time">{formatTime(e.timeUtc)}</span>
            <span className="term-tag" />
            <span className="term-text">{e.message}</span>
          </div>
        ))}
        {data.serverLog.length > 0 && <div className="term-idle">_</div>}
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
  const processPanel = (
    <section className="panel panel-fill">
      <h3>
        Process{" "}
        <span className="panel-sub">
          {last
            ? `${formatBytes(last.managedMemory)} in the managed heap · ${formatBytes(last.processMemory)} resident${cpu === null ? "" : ` · ${formatPercent(cpu)} cpu of ${last.processorCount} cores`}`
            : "sampling…"}
        </span>
      </h3>
      <ProcessChart samples={samples} />
      <div className="logs-chart-foot">
        <span className="muted">the whole server process: one heap and one cpu budget serve every database on it</span>
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
async function readProcess(): Promise<ProcessSample> {
  const live = await fetchServerLive();
  return {
    at: new Date(live.sampledUtc).getTime(),
    iso: live.sampledUtc,
    managedMemory: live.managedMemory ?? 0,
    processMemory: live.processMemory ?? 0,
    processorTimeMs: live.processorTimeMs ?? 0,
    processorCount: live.processorCount ?? 1,
  };
}

function ProcessAction({
  label,
  hint,
  danger,
  disabled,
  confirm,
  run,
  onDone,
}: {
  label: string;
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
        {label}
      </button>
      <span className="muted">{disabled ? "not available on this server" : (message ?? hint)}</span>
    </div>
  );
}
