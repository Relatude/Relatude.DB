import { useCallback, useEffect, useState } from "react";
import { IconPlayerPlayFilled, IconPlayerStopFilled } from "@tabler/icons-react";
import { showConfirm, showError } from "../dialogs";
import { subscribe, subscribeResync } from "../server/channel";
import { collectGarbage, fetchServerOverview, softRestart, stopHost, type OverviewContainer, type ProcessActionResult, type ServerOverview } from "../server/overview";
import { closeStore, openStore } from "../server/storage";
import { usePoll } from "../refresh";
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
            <span />
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
              <StoreToggle db={c} onDone={load} />
            </div>
          ))}
          <div className="db-table-row db-table-total">
            <span />
            <span>Total</span>
            <span />
            <span className="num">{formatCount(totalNodes)}</span>
            <span />
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

/**
 * Opens or closes one database from the list. Closing is the disruptive direction - the application
 * cannot reach the database until it is opened again - so only that side asks first.
 *
 * Both commands run to completion on the server before answering, and replaying a large log is not
 * quick, so the button holds a busy state for as long as that takes. The row itself does not depend
 * on the answer: the container watch broadcasts every state change, so Opening and Open arrive on
 * the stream whether or not this request has come back yet.
 */
function StoreToggle({ db, onDone }: { db: OverviewContainer; onDone: () => void }) {
  const [busy, setBusy] = useState(false);
  const settling = db.state === "Opening" || db.state === "Closing";
  const isOpen = db.state === "Open";
  async function click() {
    if (isOpen) {
      const confirmed = await showConfirm(
        `Close ${db.name}?`,
        "The application cannot read or write this database until it is opened again. Anything not yet flushed is written out first, so nothing is lost.",
        { confirmLabel: "Close", danger: true },
      );
      if (!confirmed.ok) return;
    }
    setBusy(true);
    try {
      await (isOpen ? closeStore(db.id) : openStore(db.id));
      onDone();
    } catch (e) {
      await showError(isOpen ? "Could not close the database" : "Could not open the database", e instanceof Error ? e.message : String(e));
      onDone(); // a failed open leaves the database in Error, which the row should show
    } finally {
      setBusy(false);
    }
  }
  return (
    <button
      className={"icon-button" + (isOpen ? " danger" : "")}
      disabled={busy || settling}
      title={settling ? db.state : isOpen ? "Close this database" : "Open this database"}
      onClick={click}
    >
      {isOpen ? <IconPlayerStopFilled size={13} /> : <IconPlayerPlayFilled size={13} />}
    </button>
  );
}

// An action on the process itself. The disruptive ones ask first, in a confirm dialog that spells
// out what the action does to a running server; the harmless ones (a collection) just run.
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
