import { useCallback, useEffect, useState } from "react";
import { IconArrowBackUp, IconCheck, IconHistory } from "@tabler/icons-react";
import { runWithProgress, showConfirm, showError, showInfo } from "../dialogs";
import {
  beginRevertWindow,
  commitRevertWindow,
  fetchRevertStatus,
  previewRollback,
  rollbackRevertWindow,
  type RevertResult,
  type RevertStatus,
} from "../server/revert";
import type { DatabaseInfo } from "../server/serverInfo";
import { notifyResync } from "../server/channel";
import { usePoll } from "../refresh";
import { formatBytes, formatCount, formatDuration, formatTime } from "../format";

/**
 * The revert window of the active database, in the top bar.
 *
 * It belongs beside the database it applies to rather than on any one page: a window is a mode the
 * whole database is in, and every page is written under it. Closed, it is one quiet icon - there is
 * nothing to say about a database that is simply saving what it is told. Open, it is a coloured
 * pill counting up, because every change being made is provisional until someone comes back to it,
 * and that has to be visible without reading anything. The details and the two ways out are one
 * click away in the panel, not spread across the bar.
 *
 * The container broadcast says whether a window is open, so one begun from code, the CLI or another
 * browser shows up here within a second without this component asking; the details come from a
 * status call, refreshed on the global cadence only while there is a window to follow.
 */
export function RevertControl({ db }: { db: DatabaseInfo }) {
  const [status, setStatus] = useState<RevertStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const [open, setOpen] = useState(false);
  const isActive = !!db.revertWindow;

  const load = useCallback(async () => {
    try {
      setStatus(await fetchRevertStatus(db.id));
    } catch {
      // a closed or restarting database has no window to report; the control keeps what it has
    }
  }, [db.id]);

  // the broadcast is the trigger, the status call the detail: a change in one refetches the other
  useEffect(() => {
    load();
  }, [load, isActive, db.revertWindow?.timestamp]);
  usePoll(load, { enabled: isActive || status?.active === true });

  // the broadcast is the fresher of the two while they disagree (a window just begun, or just ended)
  const active = isActive && status?.active === true ? status : null;

  async function begin() {
    setOpen(false);
    const { ok, option } = await showConfirm(
      "Begin a revert window?",
      "The current position in the transaction log becomes a rollback target. Every change made after it can be discarded as one - or kept. " +
        "While the window is open the database keeps writing its log as usual but suspends index durability, state snapshots and log rewrites, " +
        "which is what makes a rollback cheap. Closing the database ends the window and keeps the changes.",
      { confirmLabel: "Begin revert window", option: { label: "Write a state snapshot first, so a rollback reloads from it instead of replaying the log", checked: true } },
    );
    if (!ok) return;
    setBusy(true);
    try {
      const result = await runWithProgress("Beginning revert window", async (ctl) => {
        ctl.set({ label: option ? "Writing the state snapshot…" : "Marking the log position…" });
        return await beginRevertWindow(db.id, option);
      });
      if (result) setStatus(result);
    } catch (e) {
      await showError("Could not begin a revert window", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  async function commit() {
    setOpen(false);
    const since = status?.window ? ` since ${formatTime(status.window.begunUtc)}` : "";
    const { ok } = await showConfirm(
      "Keep the changes and end the window?",
      `Every change made${since} stays in the database and becomes permanent: the indexes are made durable again and the window closes. Nothing is deleted.`,
      { confirmLabel: "Commit — keep changes" },
    );
    if (!ok) return;
    setBusy(true);
    try {
      setStatus(await commitRevertWindow(db.id));
    } catch (e) {
      await showError("Could not commit the revert window", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  async function rollback() {
    setOpen(false);
    setBusy(true);
    try {
      // the confirmation names what is about to be deleted, so it is measured first
      const preview = await runWithProgress("Measuring what a rollback would delete", async (ctl) => {
        ctl.set({ label: "Scanning the transaction log…" });
        return await previewRollback(db.id);
      });
      if (!preview) return;
      const target = status?.window ? formatTime(status.window.timestampUtc) : "the window's start";
      const nothing = preview.transactionsDeleted === 0;
      const { ok } = await showConfirm(
        nothing ? "Nothing to roll back" : "Roll back and delete the changes?",
        nothing
          ? `No transaction has been written since the window began. Rolling back only ends the window; the database is already in the state it had at ${target}.`
          : `${describeDeletion(preview)} — everything written since ${target} — will be permanently deleted from the transaction log, ` +
            `as if it never happened. The database reloads at the window's start` +
            (preview.stateAndIndexesReset ? " and rebuilds its state and indexes from the log" : "") +
            (preview.enginesReset.length > 0 ? `; the ${preview.enginesReset.join(", ")} index engine${preview.enginesReset.length === 1 ? " is" : "s are"} rebuilt` : "") +
            `. Files uploaded by the deleted transactions are not removed. This cannot be undone.`,
        { confirmLabel: nothing ? "End the window" : "Roll back — delete changes", danger: !nothing },
      );
      if (!ok) return;
      const outcome = await runWithProgress("Rolling back", async (ctl) => {
        ctl.set({ label: nothing ? "Ending the window…" : "Truncating the log and reloading the database…" });
        return await rollbackRevertWindow(db.id);
      });
      if (!outcome) return;
      setStatus(outcome.status);
      // the page under the bar shows a database that no longer exists in that form
      if (!nothing) notifyResync();
      if (!nothing) {
        const took = outcome.result.durationMs < 1000 ? `${Math.round(outcome.result.durationMs)} ms` : formatDuration(outcome.result.durationMs);
        await showInfo("Rolled back", `${describeDeletion(outcome.result)} deleted in ${took}.`, [
          outcome.result.lastUtc ? `The database is back at ${formatTime(outcome.result.lastUtc)}.` : "",
          outcome.result.stateAndIndexesReset ? "State and indexes were rebuilt from the log." : "The indexes reopened at the window's start without a rebuild.",
          outcome.result.enginesReset.length > 0 ? `Rebuilt engines: ${outcome.result.enginesReset.join(", ")}.` : "",
        ].filter(Boolean));
      }
    } catch (e) {
      await showError("Could not roll back", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  const begun = active?.window?.begunUtc ?? db.revertWindow?.begunUtc ?? null;
  return (
    <div className="revert-control">
      <button
        className={"revert-pill" + (isActive ? " active" : "")}
        onClick={() => setOpen(!open)}
        title={isActive ? "A revert window is open — commit or roll back" : "No revert window: every change is permanent as it is made"}
      >
        {isActive ? <span className="revert-pulse" aria-hidden /> : <IconHistory size={16} stroke={1.8} />}
        {isActive && <span className="revert-pill-text">{begun ? <Elapsed from={begun} /> : "open"}</span>}
      </button>
      {open && (
        <>
          <div className="db-menu-backdrop" onClick={() => setOpen(false)} />
          <div className="revert-panel">
            {isActive ? (
              <>
                <div className="revert-panel-head">
                  <span className="revert-pulse" aria-hidden />
                  Revert window open
                </div>
                <div className="revert-panel-facts">
                  {begun && (
                    <div>
                      <span className="fact-k">Begun</span>
                      <span>
                        {formatTime(begun)} · <Elapsed from={begun} />
                      </span>
                    </div>
                  )}
                  {active?.window && (
                    <div title="The last transaction that survives a rollback">
                      <span className="fact-k">Rollback to</span>
                      <span>{formatTime(active.window.timestampUtc)}</span>
                    </div>
                  )}
                  {active && (
                    <div>
                      <span className="fact-k">Since then</span>
                      <span className={active.changedSinceBegin ? "revert-changed" : "revert-unchanged"}>
                        {active.changedSinceBegin ? `changes, last at ${active.headUtc ? formatTime(active.headUtc) : "—"}` : "no changes yet"}
                      </span>
                    </div>
                  )}
                </div>
                <p className="revert-panel-note">
                  Everything written since the window began can be kept or discarded as one. Until then the database suspends index durability, state snapshots
                  and log rewrites.
                </p>
                <div className="revert-panel-actions">
                  <button className="action-button" onClick={commit} disabled={busy} title="End the window and keep every change made inside it">
                    <IconCheck size={14} stroke={2} /> Commit · keep
                  </button>
                  <button className="action-button danger" onClick={rollback} disabled={busy} title="End the window and permanently delete every change made inside it">
                    <IconArrowBackUp size={14} stroke={1.8} /> Roll back
                  </button>
                </div>
              </>
            ) : (
              <>
                <div className="revert-panel-head">
                  <IconHistory size={15} stroke={1.8} /> No revert window
                </div>
                <p className="revert-panel-note">
                  Every change is permanent as it is made. Begin a window to mark the current position in the transaction log, and everything written after it can
                  be rolled back or committed as one.
                </p>
                <div className="revert-panel-actions">
                  <button className="action-button" onClick={begin} disabled={busy}>
                    <IconHistory size={14} stroke={1.8} /> Begin revert window
                  </button>
                </div>
              </>
            )}
          </div>
        </>
      )}
    </div>
  );
}

function describeDeletion(r: RevertResult): string {
  return (
    `${formatCount(r.transactionsDeleted)} transaction${r.transactionsDeleted === 1 ? "" : "s"} with ` +
    `${formatCount(r.actionsDeleted)} action${r.actionsDeleted === 1 ? "" : "s"} (${formatBytes(r.bytesTruncated)} of log)`
  );
}

/** How long ago, ticking once a second - so an open window is never mistaken for a fresh one. */
function Elapsed({ from }: { from: string }) {
  const [, tick] = useState(0);
  useEffect(() => {
    const timer = window.setInterval(() => tick((n) => n + 1), 1000);
    return () => window.clearInterval(timer);
  }, []);
  const ms = Math.max(0, Date.now() - new Date(from).getTime());
  return <span>{ms < 60000 ? `${Math.floor(ms / 1000)}s` : formatDuration(ms)}</span>;
}
