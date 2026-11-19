import { useCallback, useEffect, useMemo, useState } from "react";
import { IconChevronLeft, IconChevronRight, IconExternalLink, IconHistory, IconReload, IconSettings, IconX } from "@tabler/icons-react";
import { Chart } from "./Chart";
import { Loading } from "./Loading";
import { TypePicker } from "./TypePicker";
import { formatBytes, formatCount, formatTime } from "../format";
import { openInDatamodel, openInQuery, openInSettings } from "../navigate";
import { fetchQueryModel, type QueryModel } from "../server/query";
import type { DatabaseInfo } from "../server/serverInfo";
import type { IntervalType, SeriesPoint } from "../server/logs";
import {
  fetchHistoryActivity,
  fetchHistoryChanges,
  fetchHistoryInfo,
  fetchHistoryTypes,
  type ChangeKind,
  type HistoryActivity,
  type HistoryChange,
  type HistoryInfo,
  type HistoryLink,
  type HistoryPage,
  type HistoryType,
} from "../server/history";

/**
 * The change history of a database, under Activity: who changed which node when, newest first, with a chart of how
 * much changed over the period. Read from the history index the database keeps of its log (Settings › History), a
 * few seconds behind the writes. A row opens the node on the query page, whose form has its own history. The second
 * view says which types are in history and why; their switch is the data model editor's.
 */
export function HistoryTab({ db }: { db: DatabaseInfo }) {
  const [info, setInfo] = useState<HistoryInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [view, setView] = useState<"changes" | "types">("changes");
  const load = useCallback(async () => {
    try {
      setInfo(await fetchHistoryInfo(db.id));
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, [db.id]);
  useEffect(() => {
    load();
  }, [load]);
  // while it builds, how far it has got comes back every few seconds
  const building = info?.status?.building ?? false;
  useEffect(() => {
    if (!building) return;
    const timer = window.setInterval(load, 3000);
    return () => window.clearInterval(timer);
  }, [building, load]);

  if (error) return <div className="placeholder">{error}</div>;
  if (!info) return <Loading label="Loading the history…" />;
  if (!info.open) return <div className="placeholder">The database must be open to read its history.</div>;
  if (!info.settings.enabled) {
    return (
      <div className="history-off panel">
        <IconHistory size={28} stroke={1.5} />
        <h3>Change history is off</h3>
        <p className="muted">
          Turned on, the database reads its log files in the background into a time index of who changed what and when, and follows the log a
          few seconds behind from then on. It puts nothing on the write path. It reaches as far back as the log files kept, or the secondary log
          when that is on.
        </p>
        <button
          className="action-button"
          onClick={() => openInSettings({ scope: "database", storeId: db.id, sectionId: "maintenance", groupId: "history", path: "LocalSettings.History.Enabled" })}
        >
          <IconSettings size={14} stroke={1.8} /> Turn it on in Settings › History
        </button>
      </div>
    );
  }
  const status = info.status!;
  return (
    <div className="history">
      <div className="history-head">
        <div className="history-reach">
          <IconHistory size={16} stroke={1.8} />
          {status.reachesBackToUtc ? <>Reaches back to {formatTime(status.reachesBackToUtc)}</> : <>Nothing recorded yet</>}
          <span className="muted">
            · from the {status.source === "secondary" ? "secondary log" : "log files kept"}
            {status.building ? ` · building, ${status.percent}%` : " · up to date"}
            {" · "}
            {formatCount(status.changes)} change{status.changes === 1 ? "" : "s"}, {formatCount(status.links)} link{status.links === 1 ? "" : "s"}, {formatBytes(status.indexBytes)}
          </span>
          {status.problem && <span className="history-problem">{status.problem}</span>}
        </div>
        <div className="history-views">
          <button className={"history-view" + (view === "changes" ? " active" : "")} onClick={() => setView("changes")}>
            Changes
          </button>
          <button className={"history-view" + (view === "types" ? " active" : "")} onClick={() => setView("types")}>
            Types in history
          </button>
        </div>
      </div>
      {view === "changes" ? <ChangesView db={db} building={status.building} /> : <TypesView db={db} typesByDefault={info.settings.typesByDefault} />}
    </div>
  );
}

// ---- the changes ----

const periods = [
  { id: "24h", label: "24 hours", ms: 86_400_000, interval: "Hour" as const },
  { id: "7d", label: "7 days", ms: 7 * 86_400_000, interval: "Day" as const },
  { id: "30d", label: "30 days", ms: 30 * 86_400_000, interval: "Day" as const },
  { id: "1y", label: "1 year", ms: 365 * 86_400_000, interval: "Month" as const },
];

/** What a change is called here: the weightiest of its kinds. */
const kindWords: [ChangeKind, string][] = [
  ["NodeRemoved", "Deleted"],
  ["Removed", "Version removed"],
  ["MarkedDeleted", "Marked deleted"],
  ["Unmarked", "Unmarked"],
  ["NodeCreated", "Created"],
  ["Created", "Version created"],
  ["Published", "Published"],
  ["Unpublished", "Unpublished"],
  ["LanguageMoved", "Language moved"],
  ["Updated", "Updated"],
  ["MetaChanged", "Meta changed"],
];
function kindOf(kinds: ChangeKind[]): string {
  for (const [kind, word] of kindWords) if (kinds.includes(kind)) return word;
  return "Changed";
}
function kindTone(kinds: ChangeKind[]): string {
  if (kinds.includes("NodeRemoved") || kinds.includes("Removed") || kinds.includes("MarkedDeleted")) return " is-removed";
  if (kinds.includes("Created") || kinds.includes("NodeCreated") || kinds.includes("Unmarked")) return " is-created";
  if (kinds.includes("Published")) return " is-published";
  return "";
}
const kindFilters: { id: string; label: string; kinds: ChangeKind[] }[] = [
  { id: "", label: "Any kind", kinds: [] },
  { id: "created", label: "Created", kinds: ["Created"] },
  { id: "updated", label: "Updated", kinds: ["Updated"] },
  { id: "published", label: "Published or unpublished", kinds: ["Published", "Unpublished"] },
  { id: "deleted", label: "Deleted or marked deleted", kinds: ["Removed", "MarkedDeleted"] },
  { id: "links", label: "Links", kinds: ["Linked", "Unlinked"] },
];

type Entry = { kind: "change"; change: HistoryChange } | { kind: "link"; link: HistoryLink };
/** The page's changes and links in one list, newest first, a save's changes before its links. */
function entriesOf(page: HistoryPage): Entry[] {
  const all: Entry[] = [...page.changes.map((change) => ({ kind: "change" as const, change })), ...page.links.map((link) => ({ kind: "link" as const, link }))];
  const at = (e: Entry) => (e.kind === "change" ? e.change.timestamp : e.link.timestamp);
  return all.sort((a, b) => at(b) - at(a) || (a.kind === b.kind ? 0 : a.kind === "change" ? -1 : 1));
}

function ChangesView({ db, building }: { db: DatabaseInfo; building: boolean }) {
  const [periodId, setPeriodId] = useState("7d");
  const [typeId, setTypeId] = useState<string | null>(null);
  const [writer, setWriter] = useState<string>("");
  const [kindId, setKindId] = useState("");
  const [nodeId, setNodeId] = useState("");
  const [includeSystem, setIncludeSystem] = useState(false);
  const [model, setModel] = useState<QueryModel | null>(null);
  const [activity, setActivity] = useState<HistoryActivity | null>(null);
  const [writers, setWriters] = useState<HistoryActivity | null>(null);
  const [page, setPage] = useState<HistoryPage | null>(null);
  const [cursors, setCursors] = useState<(string | null)[]>([null]); // the cursor each page was asked with
  const [error, setError] = useState<string | null>(null);
  const [day, setDay] = useState<number | null>(null); // a bar clicked narrows the list to its interval
  const [reload, setReload] = useState(0);
  useEffect(() => {
    fetchQueryModel(db.id).then(setModel, () => setModel(null));
  }, [db.id]);
  const period = periods.find((p) => p.id === periodId) ?? periods[1];
  const range = useMemo(() => {
    const to = new Date();
    return { fromUtc: new Date(to.getTime() - period.ms).toISOString(), toUtc: new Date(to.getTime() + 60_000).toISOString() };
    // the period moves on with each reload
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [period, reload]);
  const kinds = kindFilters.find((k) => k.id === kindId)?.kinds ?? [];
  const types = typeId ? [typeId] : null;
  const writerIds = writer ? [writer] : null;
  // the chart: by kind, over the period
  useEffect(() => {
    const ctl = new AbortController();
    fetchHistoryActivity(db.id, { ...range, interval: period.interval, by: "Kind", types, writers: writerIds, kinds, includeSystem }, ctl.signal)
      .then(setActivity)
      .catch((e) => !ctl.signal.aborted && setError(e instanceof Error ? e.message : String(e)));
    return () => ctl.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [db.id, range, period.interval, typeId, writer, kindId, includeSystem]);
  // who changed anything in the period: the choices of the who filter
  useEffect(() => {
    const ctl = new AbortController();
    fetchHistoryActivity(db.id, { ...range, interval: period.interval, by: "Writer", types, includeSystem }, ctl.signal).then(setWriters, () => {});
    return () => ctl.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [db.id, range, typeId, includeSystem]);
  // what the list shows: the period, or the bar clicked
  const listRange = useMemo(() => {
    if (day === null || !activity) return range;
    const start = activity.buckets[day]?.startUtc;
    const next = activity.buckets[day + 1]?.startUtc ?? range.toUtc;
    return start ? { fromUtc: start, toUtc: next } : range;
  }, [day, activity, range]);
  useEffect(() => {
    setCursors([null]);
  }, [listRange, typeId, writer, kindId, nodeId, includeSystem]);
  const cursor = cursors[cursors.length - 1];
  useEffect(() => {
    const ctl = new AbortController();
    setPage(null);
    fetchHistoryChanges(
      db.id,
      { ...listRange, types, writers: writerIds, kinds, node: /^[0-9a-f-]{36}$/i.test(nodeId.trim()) ? nodeId.trim() : null, cursor, take: 50, collapse: true, includeSystem },
      ctl.signal,
    )
      .then((p) => {
        setPage(p);
        setError(null);
      })
      .catch((e) => !ctl.signal.aborted && setError(e instanceof Error ? e.message : String(e)));
    return () => ctl.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [db.id, listRange, cursor, typeId, writer, kindId, nodeId, includeSystem]);
  const points: SeriesPoint[] = (activity?.buckets ?? []).map((b) => ({
    fromUtc: b.startUtc,
    hasValue: b.counts.changes + b.counts.links > 0,
    value: b.counts.changes,
    values: b.groups ?? {},
  }));
  const groups = (activity?.groups ?? []).map((g) => g.key);
  const entries = page ? entriesOf(page) : [];
  return (
    <div className="history-changes">
      <div className="history-toolbar">
        <select className="select" value={periodId} onChange={(e) => (setPeriodId(e.currentTarget.value), setDay(null))} title="Period">
          {periods.map((p) => (
            <option key={p.id} value={p.id}>
              Last {p.label}
            </option>
          ))}
        </select>
        {model ? (
          <span className="history-type">
            <TypePicker types={model.types} sources={model.sources ?? []} value={typeId} onChange={setTypeId} />
            {typeId && (
              <button className="icon-button" title="Any type" onClick={() => setTypeId(null)}>
                <IconX size={14} />
              </button>
            )}
          </span>
        ) : null}
        <select className="select" value={writer} onChange={(e) => setWriter(e.currentTarget.value)} title="Who">
          <option value="">Anyone</option>
          {(writers?.groups ?? [])
            .filter((g) => g.key !== "other")
            .map((g) => (
              <option key={g.key} value={g.key}>
                {g.label} ({formatCount(g.changes)})
              </option>
            ))}
        </select>
        <select className="select" value={kindId} onChange={(e) => setKindId(e.currentTarget.value)} title="Kind">
          {kindFilters.map((k) => (
            <option key={k.id} value={k.id}>
              {k.label}
            </option>
          ))}
        </select>
        <input className="input history-node" placeholder="Node id" value={nodeId} onChange={(e) => setNodeId(e.currentTarget.value)} />
        <label className="history-system" title="Writes the database makes for itself: text indexing, file analysis, measuring">
          <input type="checkbox" checked={includeSystem} onChange={(e) => setIncludeSystem(e.currentTarget.checked)} /> System writes
        </label>
        <button className="icon-button at-end" title="Read again" onClick={() => setReload((r) => r + 1)}>
          <IconReload size={16} />
        </button>
      </div>
      {error && <div className="history-problem">{error}</div>}
      <div className="history-totals">
        {activity && (
          <>
            <Total label="Changes" value={activity.totals.changes} />
            <Total label="Saves" value={activity.totals.saves} />
            <Total label="People" value={activity.totals.people} />
            <Total label="Nodes changed" value={activity.totals.nodes} />
            <Total label="Links" value={activity.totals.links} />
          </>
        )}
      </div>
      <div className="history-chart panel">
        {activity ? (
          <Chart
            kind="groups"
            points={points}
            groups={groups}
            interval={activity.interval as IntervalType}
            format={(v) => formatCount(v)}
            integer
            bars
            height={180}
            onPick={(i) => setDay(day === i ? null : i)}
          />
        ) : (
          <Loading label="Counting…" />
        )}
      </div>
      {day !== null && activity?.buckets[day] && (
        <div className="history-narrowed">
          Showing {formatTime(activity.buckets[day].startUtc)} alone
          <button className="link-button" onClick={() => setDay(null)}>
            Show the whole period
          </button>
        </div>
      )}
      <div className="log-table history-table">
        <div className="log-table-row log-table-head history-row">
          <span>When</span>
          <span>Kind</span>
          <span>Node</span>
          <span>Type</span>
          <span>Version</span>
          <span>Who</span>
        </div>
        {!page ? (
          <Loading label="Reading the history…" />
        ) : entries.length === 0 ? (
          <div className="log-table-empty">{building ? "Nothing in this period yet: the history is still being built." : "No changes in this period."}</div>
        ) : (
          entries.map((e, i) => (e.kind === "change" ? <ChangeRow key={"c" + i} change={e.change} /> : <LinkRow key={"l" + i} link={e.link} />))
        )}
      </div>
      <div className="history-pager">
        <button className="action-button" disabled={cursors.length <= 1} onClick={() => setCursors((c) => c.slice(0, -1))}>
          <IconChevronLeft size={14} /> Newer
        </button>
        <button className="action-button" disabled={!page?.next} onClick={() => page?.next && setCursors((c) => [...c, page.next])}>
          Older <IconChevronRight size={14} />
        </button>
      </div>
    </div>
  );
}

function Total({ label, value }: { label: string; value: number }) {
  return (
    <div className="fact">
      <div className="fact-k">{label}</div>
      <div className="fact-v">{formatCount(value)}</div>
    </div>
  );
}

function ChangeRow({ change }: { change: HistoryChange }) {
  const version = [change.typeAfter ?? change.typeBefore, change.cultureName].filter(Boolean).join(", ");
  const changed = change.changedProperties?.map((p) => p.name ?? p.id).join(", ");
  const others = change.others > 0 ? `and ${formatCount(change.others)} more: ` + (change.typeCounts ?? []).map((t) => `${t.typeName} ${formatCount(t.count)}`).join(", ") : null;
  return (
    <div
      className="log-table-row history-row clickable"
      title={change.versionKept ? "Open the node on the query page" : "Open the node on the query page (this version's log file is gone)"}
      onClick={() => openInQuery({ typeId: change.typeId, nodeId: change.nodeId })}
    >
      <span className="log-time">{formatTime(change.utc)}</span>
      <span className={"history-kind" + kindTone(change.kinds)}>
        {kindOf(change.kinds)}
        {change.others > 0 ? ` ×${formatCount(change.others + 1)}` : ""}
      </span>
      <span className="history-node-cell">
        <span className="history-name">{change.name || <span className="muted">{change.nodeId.slice(0, 8)}…</span>}</span>
        {(others || changed) && <span className="muted history-sub">{others ?? changed}</span>}
      </span>
      <span>{change.typeName}</span>
      <span className="muted">{version || "—"}</span>
      <span>{change.writerName ?? (change.writer ? change.writer.slice(0, 8) + "…" : <span className="muted">not recorded</span>)}</span>
    </div>
  );
}

function LinkRow({ link }: { link: HistoryLink }) {
  return (
    <div className="log-table-row history-row">
      <span className="log-time">{formatTime(link.utc)}</span>
      <span className={"history-kind" + (link.added ? " is-created" : " is-removed")}>{link.added ? "Linked" : "Unlinked"}</span>
      <span className="history-node-cell">
        <span className="history-name">
          {link.sourceName ?? link.source.slice(0, 8) + "…"} → {link.targetName ?? link.target.slice(0, 8) + "…"}
        </span>
        <span className="muted history-sub">{link.relationName}</span>
      </span>
      <span>
        {link.sourceTypeName} → {link.targetTypeName}
      </span>
      <span className="muted">—</span>
      <span>{link.writerName ?? (link.writer ? link.writer.slice(0, 8) + "…" : <span className="muted">not recorded</span>)}</span>
    </div>
  );
}

// ---- the types ----

function TypesView({ db, typesByDefault }: { db: DatabaseInfo; typesByDefault: boolean }) {
  const [types, setTypes] = useState<HistoryType[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState("");
  useEffect(() => {
    fetchHistoryTypes(db.id).then(
      (r) => setTypes(r.types),
      (e) => setError(e instanceof Error ? e.message : String(e)),
    );
  }, [db.id]);
  if (error) return <div className="placeholder">{error}</div>;
  if (!types) return <Loading label="Reading the types…" />;
  const shown = types.filter((t) => !filter || t.fullName.toLowerCase().includes(filter.toLowerCase()));
  const why = (t: HistoryType) =>
    t.from === "override" ? "overridden here" : t.from === "code" ? "set in code" : t.from === "inherited" ? `from ${t.fromType}` : "the default";
  return (
    <div className="history-types">
      <div className="history-toolbar">
        <input className="input" placeholder="Find a type" value={filter} onChange={(e) => setFilter(e.currentTarget.value)} />
        <span className="muted">
          Types that say nothing are {typesByDefault ? "in" : "out"} (Settings › History). A type is put in or out with its Change history switch in the data model editor; one
          put in shows its earlier changes once the history index is rebuilt (Storage).
        </span>
      </div>
      <div className="log-table">
        <div className="log-table-row log-table-head history-type-row">
          <span>Type</span>
          <span>In history</span>
          <span>Why</span>
          <span />
        </div>
        {shown.map((t) => (
          <div key={t.id} className="log-table-row history-type-row">
            <span title={t.fullName}>
              {t.name}
              {t.isAbstract && <span className="muted"> (abstract)</span>}
            </span>
            <span className={"history-kind" + (t.inHistory ? " is-created" : " is-removed")}>{t.inHistory ? "In" : "Out"}</span>
            <span className="muted">{why(t)}</span>
            <button className="link-button" title="Open the type in the data model editor, where its Change history switch is" onClick={() => openInDatamodel({ typeId: t.id })}>
              <IconExternalLink size={13} /> Change
            </button>
          </div>
        ))}
      </div>
    </div>
  );
}
