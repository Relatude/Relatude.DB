import { useCallback, useEffect, useRef, useState } from "react";
import { IconArrowRight, IconBan, IconX } from "@tabler/icons-react";
import { showConfirm, showError } from "../dialogs";
import { cancelConversion, fetchConversions, type ConversionsInfo, type FileConversion } from "../server/conversions";
import type { DatabaseInfo } from "../server/serverInfo";
import { usePoll } from "../refresh";
import { formatCount, formatTime } from "../format";

// long enough to read as "that one is done and gone" rather than a flicker; must match conv-leave
const leaveMs = 420;

/** A conversion as the list holds it: on its way out until the animation has finished. */
type Row = FileConversion & { leaving?: boolean };

/**
 * What the file conversion queue is doing: image resizing, format conversion and text extraction, all
 * of which happen off the request that asked for them. The list holds what is running and queued plus
 * a short tail of ones that have finished, which is the point - a conversion that failed is usually
 * what you came to look at, and it is gone from "running" by the time you get here.
 */
export function ConversionsSection({ db }: { db: DatabaseInfo }) {
  const [data, setData] = useState<ConversionsInfo | null>(null);
  const [rows, setRows] = useState<Row[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [cancelling, setCancelling] = useState<Record<string, boolean>>({});
  // the list the component shows, which is the server's plus whatever is still animating out
  const shown = useRef<Row[]>([]);
  const leaveTimers = useRef<number[]>([]);

  /**
   * Folds a poll into the visible list. A conversion the server has stopped reporting is kept for one
   * animation rather than vanishing between two frames - finishing is the moment worth seeing, and it
   * is exactly the moment the row would otherwise disappear without a trace. Rows that are still
   * there keep their place, so nothing reshuffles under the pointer while someone is reaching for a
   * cancel button.
   */
  const applyRows = useCallback((incoming: FileConversion[]) => {
    const byId = new Map(incoming.map((c) => [c.id, c]));
    const next: Row[] = [];
    const leaving: string[] = [];
    for (const row of shown.current) {
      const fresh = byId.get(row.id);
      if (fresh) {
        next.push(fresh);
        byId.delete(row.id);
      } else if (row.leaving) {
        next.push(row); // already on its way out, its timer is running
      } else {
        next.push({ ...row, leaving: true });
        leaving.push(row.id);
      }
    }
    for (const fresh of byId.values()) next.push(fresh);
    shown.current = next;
    setRows(next);
    if (leaving.length === 0) return;
    const timeout = window.setTimeout(() => {
      shown.current = shown.current.filter((row) => !leaving.includes(row.id));
      setRows(shown.current);
    }, leaveMs);
    leaveTimers.current.push(timeout);
  }, []);

  const load = useCallback(async (): Promise<ConversionsInfo | null> => {
    try {
      const info = await fetchConversions(db.id);
      setData(info);
      applyRows(info.current);
      setError(null);
      return info;
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      return null;
    }
  }, [db.id, applyRows]);

  useEffect(() => {
    load();
    return () => {
      for (const id of leaveTimers.current) clearTimeout(id);
      leaveTimers.current = [];
    };
  }, [load]);
  usePoll(load);

  async function cancel(conversion: FileConversion, permanently: boolean): Promise<void> {
    if (permanently) {
      const confirmed = await showConfirm(
        `Stop converting ${conversion.fileName} for good?`,
        "The failure is recorded against the file, so nothing will try this conversion again until the file itself changes. Cancelling without this only stops the current attempt.",
        { confirmLabel: "Cancel permanently", danger: true },
      );
      if (!confirmed.ok) return;
    }
    setCancelling((prev) => ({ ...prev, [conversion.id]: true }));
    try {
      await cancelConversion(db.id, conversion.id, permanently);
      await load();
    } catch (e) {
      await showError("Could not cancel", e instanceof Error ? e.message : String(e));
    } finally {
      setCancelling((prev) => {
        const next = { ...prev };
        delete next[conversion.id];
        return next;
      });
    }
  }

  if (error) return <div className="placeholder">{error}</div>;
  if (!data) return null;
  if (!data.open) return <div className="placeholder">Open the database to see its conversions.</div>;

  return (
    <div className="conversions">
      <div className="conv-stats">
        <Stat label="Running" value={data.running} tone="running" />
        <Stat label="Queued" value={data.queued} tone="queued" />
        <Stat label="Completed" value={data.completed} tone="done" />
        <Stat label="Failed or cancelled" value={data.failed + data.canceled} tone={data.failed + data.canceled > 0 ? "bad" : undefined} />
      </div>
      <section className="panel">
        <h3>
          Conversions <span className="panel-sub">running, queued, and recently finished</span>
        </h3>
        <div className="conv-table">
          <div className="conv-row conv-head">
            <span>File</span>
            <span>Format</span>
            <span>Property</span>
            <span>Status</span>
            <span>Progress</span>
            <span className="num">Time</span>
            <span />
          </div>
          {rows.length === 0 && <div className="conv-empty">Nothing has been converted recently.</div>}
          {rows.map((c) => {
            const active = !c.leaving && (c.status === "Queued" || c.status === "Running");
            const busy = cancelling[c.id] === true;
            return (
              <div className={"conv-row" + (c.leaving ? " leaving" : "")} key={c.id}>
                <span className="conv-file" title={c.fileName}>
                  {c.fileName || "—"}
                  {c.description && <span className="conv-desc">{c.description}</span>}
                </span>
                <span className="conv-format">
                  <span className="conv-chip">{c.from}</span>
                  <IconArrowRight size={12} stroke={2} />
                  <span className="conv-chip">{c.to}</span>
                </span>
                <span className="conv-property" title={c.property ?? undefined}>
                  {c.property ?? "—"}
                </span>
                <span className={"conv-status " + c.status.toLowerCase()}>{c.status}</span>
                <span>{c.status === "Running" ? <Progress percent={c.progressPercentage} /> : <span className="muted">—</span>}</span>
                <span className="num" title={c.started ? "Started " + formatTime(c.started) : "Created " + formatTime(c.created)}>
                  {c.processedMs != null ? formatCount(Math.round(c.processedMs)) + " ms" : "—"}
                </span>
                <span className="conv-actions">
                  {active && (
                    <>
                      <button className="icon-button" disabled={busy} title="Cancel this attempt" onClick={() => cancel(c, false)}>
                        <IconX size={15} stroke={1.8} />
                      </button>
                      <button className="icon-button danger" disabled={busy} title="Cancel and do not try again" onClick={() => cancel(c, true)}>
                        <IconBan size={15} stroke={1.8} />
                      </button>
                    </>
                  )}
                </span>
              </div>
            );
          })}
        </div>
      </section>
    </div>
  );
}

function Stat({ label, value, tone }: { label: string; value: number; tone?: string }) {
  return (
    <div className={"conv-stat" + (tone ? " " + tone : "")}>
      <div className="conv-stat-value">{formatCount(value)}</div>
      <div className="conv-stat-label">{label}</div>
    </div>
  );
}

function Progress({ percent }: { percent: number }) {
  const pct = Math.max(0, Math.min(100, percent || 0));
  return (
    <span className="conv-progress">
      <span className="conv-progress-bar">
        <span className="conv-progress-fill" style={{ width: pct + "%" }} />
      </span>
      <span className="conv-progress-text">{pct}%</span>
    </span>
  );
}
