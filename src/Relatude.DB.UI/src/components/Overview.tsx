import { useCallback, useEffect, useState } from "react";
import { subscribe, subscribeResync } from "../server/channel";
import { collectGarbage, fetchServerOverview, softRestart, stopHost, type ProcessActionResult, type ServerOverview } from "../server/overview";
import { formatBytes, formatCount, formatDuration, formatTime } from "../format";

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
    const timer = window.setInterval(load, 10000); // uptime and memory drift, refresh at a slow pace
    const unsubscribeResync = subscribeResync(load);
    const unsubscribeContainers = subscribe("containers", load); // any database change refreshes the page
    return () => {
      clearInterval(timer);
      unsubscribeResync();
      unsubscribeContainers();
    };
  }, [load]);
  if (error) return <div className="placeholder">{error}</div>;
  if (!data) return null;
  const open = data.containers.filter((c) => c.state === "Open").length;
  const errors = data.containers.filter((c) => c.state === "Error").length;
  const totalNodes = data.containers.reduce((sum, c) => sum + (c.nodeCount ?? 0), 0);
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
  return (
    <div className="overview">
      <div className="overview-columns">
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
        <section className="panel actions-panel">
          <h3>Process actions</h3>
          <ProcessAction
            label="Garbage collection"
            hint="deep, blocking, compacting collection"
            confirm={false}
            disabled={false}
            run={collectGarbage}
            onDone={load}
          />
          <ProcessAction
            label="Soft restart"
            hint="re-reads settings, closes and reopens every database"
            disabled={!data.restart.canSoftRestart}
            run={softRestart}
          />
          <ProcessAction label="Stop application" hint="stops the host process" danger disabled={!data.restart.canStopHost} run={stopHost} />
        </section>
      </div>
      <section className="panel">
        <h3>
          Databases{" "}
          <span className="panel-sub">
            {data.containers.length} · {open} open
            {errors > 0 ? ` · ${errors} ${errors === 1 ? "error" : "errors"}` : ""}
          </span>
        </h3>
        <div className="db-table">
          <div className="db-table-row db-table-head">
            <span />
            <span>Name</span>
            <span>State</span>
            <span className="num">Nodes</span>
            <span>Provider</span>
          </div>
          {data.containers.map((c) => (
            <div key={c.id} className="db-table-row">
              <span className={"state-dot " + c.state.toLowerCase()} />
              <span>
                {c.name} <span className="row-id">{c.id}</span>
              </span>
              <span>{c.state}</span>
              <span className="num">{c.nodeCount != null ? formatCount(c.nodeCount) : "—"}</span>
              <span>{c.provider ?? "—"}</span>
            </div>
          ))}
          <div className="db-table-row db-table-total">
            <span />
            <span>Total</span>
            <span />
            <span className="num">{formatCount(totalNodes)}</span>
            <span />
          </div>
        </div>
      </section>
      <div className="overview-columns">
        <section className="panel">
          <h3>Server log</h3>
          <div className="log-list">
            {data.serverLog.length === 0 && <div className="muted">Empty.</div>}
            {data.serverLog.map((e, i) => (
              <div key={i} className="log-row">
                <span className="log-time">{formatTime(e.timeUtc)}</span>
                <span>{e.message}</span>
              </div>
            ))}
          </div>
        </section>
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
      </div>
    </div>
  );
}

// destructive actions use a two-step confirmation: first click arms the button, second runs it
function ProcessAction({
  label,
  hint,
  danger,
  disabled,
  confirm = true,
  run,
  onDone,
}: {
  label: string;
  hint: string;
  danger?: boolean;
  disabled: boolean;
  confirm?: boolean;
  run: () => Promise<ProcessActionResult>;
  onDone?: () => void;
}) {
  const [armed, setArmed] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  useEffect(() => {
    if (!armed) return;
    const t = setTimeout(() => setArmed(false), 4000);
    return () => clearTimeout(t);
  }, [armed]);
  async function click() {
    if (confirm && !armed) {
      setArmed(true);
      return;
    }
    setArmed(false);
    try {
      const result = await run();
      setMessage(result.message);
      onDone?.();
    } catch (e) {
      setMessage(e instanceof Error ? e.message : String(e));
    }
  }
  return (
    <div className="process-action">
      <button className={"action-button" + (danger ? " danger" : "") + (armed ? " armed" : "")} onClick={click} disabled={disabled}>
        {armed ? "Click again to confirm" : label}
      </button>
      <span className="muted">{disabled ? "not available on this server" : (message ?? hint)}</span>
    </div>
  );
}
