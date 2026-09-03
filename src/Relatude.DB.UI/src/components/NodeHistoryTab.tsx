import { useEffect, useState } from "react";
import { IconChevronDown, IconChevronRight, IconGitCompare, IconRefresh } from "@tabler/icons-react";
import { fetchNodeVersions, type NodeHistory, type VersionRow } from "../server/query";
import { formatCount, formatTime } from "../format";
import { DiffView, VersionCompare } from "./VersionCompare";

// a change longer than this is shown as a word diff rather than as old → new: two long texts side
// by side say that something changed, a diff says what
const longChange = 80;

const firstPage = 50;

/**
 * The older versions of a node, newest first, as the transaction log has them. Every write of a
 * node appends the whole node to the log together with the position of its previous version, so
 * this is a walk along a chain rather than a search - one read per version, nothing cached. It
 * reaches back to the last log rewrite, or further when the database keeps a secondary log.
 *
 * Each row says what that write changed, compared with the version before it; the current version
 * heads the list and is compared with the newest older one. Relations are not part of node data and
 * do not appear. The oldest reachable row has nothing older to compare with, so it lists its values
 * and no changes - it is the oldest we can see, not necessarily the first there was.
 */
export function NodeHistoryTab({ storeId, nodeId }: { storeId: string; nodeId: string }) {
  const [history, setHistory] = useState<NodeHistory | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [max, setMax] = useState(firstPage);
  const [loading, setLoading] = useState(false);
  const [open, setOpen] = useState<Set<string>>(new Set());
  const [reloads, setReloads] = useState(0);
  // the compare dialog: which two rows it opened on (older side, newer side), or null when closed
  const [compare, setCompare] = useState<{ from: number; to: number } | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    fetchNodeVersions(storeId, nodeId, max)
      .then((h) => {
        if (cancelled) return;
        setHistory(h);
        setError(null);
      })
      .catch((e) => !cancelled && setError(e instanceof Error ? e.message : String(e)))
      .finally(() => !cancelled && setLoading(false));
    return () => {
      cancelled = true;
    };
  }, [storeId, nodeId, max, reloads]);

  function toggle(key: string) {
    setOpen((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  if (error) return <div className="placeholder">{error}</div>;
  if (!history) return null;

  return (
    <div className="node-history">
      <div className="history-intro">
        <span>
          {history.count === 0
            ? "No older version is reachable."
            : `${formatCount(history.count)} older ${history.count === 1 ? "version" : "versions"}${history.mayHaveMore ? " shown" : ""}, read from the transaction log.`}
        </span>
        {history.mayHaveMore && (
          <button className="link-button" onClick={() => setMax(max * 4)} disabled={loading}>
            show more
          </button>
        )}
        {history.count > 0 && (
          <button className="link-button" onClick={() => setCompare({ from: 1, to: 0 })} title="Pick any two versions and see every difference between them">
            <IconGitCompare size={13} stroke={1.8} /> compare versions
          </button>
        )}
        <div className="query-spacer" />
        <button className="icon-button" title="Read the history again" onClick={() => setReloads((n) => n + 1)} disabled={loading}>
          <IconRefresh size={14} stroke={1.8} />
        </button>
      </div>
      {history.count === 0 && (
        <div className="muted history-note">
          History follows the chain of writes in the log file: it reaches back to the last log rewrite (further with a secondary log), and only over
          transactions written in the current log format. A node written once has no older version.
        </div>
      )}
      {history.rows.map((row, i) => {
        const key = row.timestamp ?? "current";
        const expanded = open.has(key);
        return (
          <Row
            key={key}
            row={row}
            expanded={expanded}
            onToggle={() => toggle(key)}
            oldest={i === history.rows.length - 1 && i > 0}
            // an older version is compared with the current one; the current one with the version before it
            onCompare={history.rows.length > 1 ? () => setCompare(i === 0 ? { from: 1, to: 0 } : { from: i, to: 0 }) : null}
          />
        );
      })}
      {compare && <VersionCompare rows={history.rows} initialFrom={compare.from} initialTo={compare.to} onClose={() => setCompare(null)} />}
    </div>
  );
}

function Row({
  row,
  expanded,
  onToggle,
  oldest,
  onCompare,
}: {
  row: VersionRow;
  expanded: boolean;
  onToggle: () => void;
  oldest: boolean;
  onCompare: (() => void) | null;
}) {
  const changes = row.changes ?? [];
  return (
    <div className={"history-row" + (row.current ? " current" : "")}>
      <div className="history-head-wrap">
      <button className="history-head" onClick={onToggle} title={expanded ? "Hide the values of this version" : "Show every value of this version"}>
        {expanded ? <IconChevronDown size={14} stroke={2} /> : <IconChevronRight size={14} stroke={2} />}
        {row.current ? (
          // no time on the current row: a property write does not move the node's own change
          // stamp, and its log time is not what the history reads - the head says when it changed
          <span className="history-time">Current version</span>
        ) : (
          <span className="history-time" title="When the transaction was written to the log">
            {formatTime(row.utc)}
          </span>
        )}
        {!row.current && <span className="setting-badge faint">{row.source ?? "log"}</span>}
        <span className="muted">{row.typeName}</span>
        <span className="muted history-summary">
          {row.changes === null
            ? oldest
              ? "oldest reachable version"
              : "only version"
            : changes.length === 0
              ? "no change in the stored values"
              : `${formatCount(changes.length)} ${changes.length === 1 ? "change" : "changes"}${row.current ? " since the previous version" : ""}`}
        </span>
      </button>
      {onCompare && (
        <button
          className="icon-button history-compare"
          title={row.current ? "Compare with the version before it, with a text diff" : "Compare with the current version, with a text diff"}
          onClick={onCompare}
        >
          <IconGitCompare size={15} stroke={1.8} />
        </button>
      )}
      </div>
      {changes.length > 0 && (
        <div className="history-changes">
          {changes.map((c) =>
            c.from.length + c.to.length > longChange ? (
              <div className="history-change long" key={c.name}>
                <em>{c.name}</em>
                <DiffView a={c.from} b={c.to} granularity="words" split={false} />
              </div>
            ) : (
              <div className="history-change" key={c.name}>
                <em>{c.name}</em>
                <span className="history-from">{c.from || "—"}</span>
                <span className="history-arrow">→</span>
                <span className="history-to">{c.to || "—"}</span>
              </div>
            ),
          )}
        </div>
      )}
      {expanded && (
        <div className="history-values">
          {row.values.map((v) => (
            <div className="history-value" key={v.name} title={v.type ?? undefined}>
              <em>{v.name}</em>
              <span>{v.value || "—"}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
