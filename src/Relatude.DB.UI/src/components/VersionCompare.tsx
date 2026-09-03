import { useEffect, useMemo, useState } from "react";
import { IconArrowsExchange, IconGitCompare, IconX } from "@tabler/icons-react";
import { diffStats, diffText, toHunks, type DiffRun, type Granularity } from "../diff";
import type { VersionRow } from "../server/query";
import { formatCount, formatTime } from "../format";

/**
 * Two versions of a node side by side, property by property.
 *
 * The history tab says what each write changed against the version before it; this dialog answers
 * the other question - what is different between any two versions, however many writes apart - and
 * shows *how* a text differs rather than only that it does: a word, character or line diff of the
 * value, inline or in two columns. Everything here is computed from the values the history already
 * carries, so picking other versions costs nothing.
 *
 * A property counts as changed when its display text differs; that is the same text the history
 * rows show, so the two agree.
 */
export function VersionCompare({
  rows,
  initialFrom,
  initialTo,
  onClose,
}: {
  rows: VersionRow[];
  /** Index into rows of the older side. */
  initialFrom: number;
  /** Index into rows of the newer side. */
  initialTo: number;
  onClose: () => void;
}) {
  const [from, setFrom] = useState(initialFrom);
  const [to, setTo] = useState(initialTo);
  const [granularity, setGranularity] = useState<Granularity>("words");
  const [split, setSplit] = useState(false);
  const [onlyChanged, setOnlyChanged] = useState(true);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const a = rows[from];
  const b = rows[to];

  // every property either side has, in the newer side's order, then whatever only the older had
  const entries = useMemo(() => {
    const byName = new Map<string, { name: string; type: string | null; a: string; b: string }>();
    for (const v of b.values) byName.set(v.name, { name: v.name, type: v.type, a: "", b: v.value });
    for (const v of a.values) {
      const e = byName.get(v.name);
      if (e) e.a = v.value;
      else byName.set(v.name, { name: v.name, type: v.type, a: v.value, b: "" });
    }
    return [...byName.values()].map((e) => ({ ...e, changed: e.a !== e.b }));
  }, [a, b]);
  const changedCount = entries.filter((e) => e.changed).length;
  const shown = onlyChanged ? entries.filter((e) => e.changed) : entries;

  return (
    <div className="dialog-backdrop" onClick={onClose}>
      <div className="dialog dialog-wide" role="dialog" aria-label="Compare versions" onClick={(e) => e.stopPropagation()}>
        <h3>
          <IconGitCompare size={16} stroke={1.8} />
          Compare versions
          <span className="header-spacer" />
          <button className="icon-button" title="Close" onClick={onClose}>
            <IconX size={16} stroke={1.8} />
          </button>
        </h3>
        <div className="compare-pick">
          <label>
            From
            <select className="select" value={from} onChange={(e) => setFrom(Number(e.target.value))}>
              {rows.map((row, i) => (
                <option key={i} value={i}>
                  {labelOf(row)}
                </option>
              ))}
            </select>
          </label>
          <button
            className="icon-button"
            title="Swap the two sides"
            onClick={() => {
              setFrom(to);
              setTo(from);
            }}
          >
            <IconArrowsExchange size={15} stroke={1.8} />
          </button>
          <label>
            To
            <select className="select" value={to} onChange={(e) => setTo(Number(e.target.value))}>
              {rows.map((row, i) => (
                <option key={i} value={i}>
                  {labelOf(row)}
                </option>
              ))}
            </select>
          </label>
          <span className="muted">
            {from === to
              ? "the same version on both sides"
              : changedCount === 0
                ? "no stored value differs"
                : `${formatCount(changedCount)} of ${formatCount(entries.length)} ${entries.length === 1 ? "value differs" : "values differ"}`}
          </span>
        </div>
        <div className="compare-toolbar">
          <span className="compare-group" title="What a difference is measured in">
            {(["words", "chars", "lines"] as Granularity[]).map((g) => (
              <button key={g} className={"logs-chip" + (granularity === g ? " active" : "")} onClick={() => setGranularity(g)}>
                {g === "words" ? "Words" : g === "chars" ? "Characters" : "Lines"}
              </button>
            ))}
          </span>
          <span className="compare-group" title="How a difference is laid out">
            <button className={"logs-chip" + (!split ? " active" : "")} onClick={() => setSplit(false)}>
              Inline
            </button>
            <button className={"logs-chip" + (split ? " active" : "")} onClick={() => setSplit(true)}>
              Side by side
            </button>
          </span>
          <label className="setting-toggle">
            <input type="checkbox" checked={onlyChanged} onChange={(e) => setOnlyChanged(e.target.checked)} />
            <span>Only what changed</span>
          </label>
        </div>
        <div className="compare-body">
          {shown.length === 0 && (
            <div className="compare-empty">
              {entries.length === 0 ? "Neither version stores any value." : "These two versions store the same values."}
            </div>
          )}
          {shown.map((e) => (
            <section className="compare-prop" key={e.name}>
              <header>
                <strong>{e.name}</strong>
                {e.type && <span className="setting-badge faint">{e.type}</span>}
                {!e.changed && <span className="muted">unchanged</span>}
                {e.changed && <Stats a={e.a} b={e.b} granularity={granularity} />}
              </header>
              {e.changed ? (
                <DiffView a={e.a} b={e.b} granularity={granularity} split={split} />
              ) : (
                <div className="compare-same">{e.a || "—"}</div>
              )}
            </section>
          ))}
        </div>
        <div className="dialog-row">
          <span className="muted dialog-meta">
            {labelOf(a)} → {labelOf(b)}
          </span>
          <div className="header-spacer" />
          <button className="action-button" onClick={onClose}>
            Close
          </button>
        </div>
      </div>
    </div>
  );
}

function labelOf(row: VersionRow): string {
  return row.current ? "Current version" : `${formatTime(row.utc)} · ${row.source ?? "log"}`;
}

function Stats({ a, b, granularity }: { a: string; b: string; granularity: Granularity }) {
  const { inserted, deleted } = useMemo(() => diffStats(diffText(a, b, granularity)), [a, b, granularity]);
  return (
    <span className="diff-stats" title="Characters added and removed">
      {inserted > 0 && <span className="plus">+{formatCount(inserted)}</span>}
      {deleted > 0 && <span className="minus">−{formatCount(deleted)}</span>}
    </span>
  );
}

/**
 * The difference between two texts, painted. Inline is one flow with the removed runs struck
 * through and the added ones marked; side by side keeps the old text left and the new text right,
 * aligned hunk by hunk so the eye can jump across.
 */
export function DiffView({ a, b, granularity, split }: { a: string; b: string; granularity: Granularity; split: boolean }) {
  const runs = useMemo(() => diffText(a, b, granularity), [a, b, granularity]);
  if (!split) {
    return (
      <pre className="diff diff-inline">
        {runs.map((run, i) => (
          <Run key={i} run={run} />
        ))}
        {runs.length === 0 && <span className="muted">—</span>}
      </pre>
    );
  }
  const hunks = toHunks(runs);
  return (
    <div className="diff-split">
      <div className="diff-side-label">old</div>
      <div className="diff-side-label">new</div>
      {hunks.map((h, i) => (
        <div className="diff-hunk" key={i} style={{ display: "contents" }}>
          <pre className={"diff diff-cell" + (h.equal ? "" : " changed")}>
            {h.equal ? h.old : <span className="delete">{h.old}</span>}
            {!h.equal && h.old === "" && <span className="diff-blank" />}
          </pre>
          <pre className={"diff diff-cell" + (h.equal ? "" : " changed")}>
            {h.equal ? h.new : <span className="insert">{h.new}</span>}
            {!h.equal && h.new === "" && <span className="diff-blank" />}
          </pre>
        </div>
      ))}
    </div>
  );
}

function Run({ run }: { run: DiffRun }) {
  if (run.kind === "equal") return <>{run.text}</>;
  return <span className={run.kind}>{run.text}</span>;
}
