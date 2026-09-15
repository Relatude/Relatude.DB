import { useCallback, useState } from "react";
import { IconDeviceFloppy, IconEraser, IconRecycle, IconRotate } from "@tabler/icons-react";
import { showError } from "../dialogs";
import { clearCaches } from "../server/dashboard";
import { collectGarbage } from "../server/overview";
import { applyMemoryBudget, fetchMemory, type MemoryBudget, type MemoryReport } from "../server/memory";
import { saveDatabaseSettings } from "../server/settings";
import { useLive } from "../live";
import { usePanelMaximized } from "../panelMaximized";
import { formatBytes, formatCount } from "../format";

const mb = 1024 * 1024;
const gb = 1024 * mb;

/**
 * What the dashboard reads about the caches themselves, as opposed to their budgets: how often each
 * one answered, how full it is, and how many times it filled up and was cut back. Cumulative since
 * the caches were last cleared, and taken with the full picture, so it is up to a minute old.
 */
export interface CacheStats {
  nodeCacheSizePercentage: number;
  nodeCacheHits: number;
  nodeCacheMisses: number;
  nodeCacheOverflows: number;
  setCacheSizePercentage: number;
  setCacheHits: number;
  setCacheMisses: number;
  setCacheOverflows: number;
  aggregateCacheCount: number;
  aggregateCacheHits: number;
  aggregateCacheMisses: number;
  /** unset when the server is older than this page, which a dashboard must survive rather than blank */
  aggregateCacheOverflows?: number;
}

/** What the two caches hold this second, from the live sample rather than the full picture. */
export interface CacheCounts {
  nodeCacheCount?: number;
  nodeCacheSize?: number;
  setCacheCount?: number;
  setCacheSize?: number;
}

/** One figure in the detail of a row: the label, the value, and the whole of it for the title. */
interface Detail {
  k: string;
  v: string;
  title?: string;
  /** two columns rather than one: a settings path is a sentence, not a number */
  wide?: boolean;
}

/**
 * Every memory budget of one database on one line: the guid and node maps, the caches and each index
 * engine, each with what it is allowed and what it is holding right now.
 *
 * One control carries both. The fill is the usage and the thumb is the bound, drawn on the same
 * scale, so the gap between them is the headroom - a fill short of the thumb is memory given away,
 * a fill at the thumb is a cache being thrown away and rebuilt. The exact figures are beside it and
 * everything else - what the budget is for, whether a change takes hold now or at the next open, how
 * often the cache is being hit - is in the tooltip, so the panel stays a list of bars.
 *
 * Maximized, the panel has the page and there is nothing left to save room for: every row opens into
 * what its tooltip says plus every figure behind it - saved against running, headroom, floor, and for
 * the caches what they hold and how often they answered - and the aggregate cache, which has no
 * budget and therefore no bar, joins the list at the bottom.
 *
 * Letting a slider go applies the bound at once wherever the part can be re-sized while it runs.
 * Saving is separate and writes the same values into the settings file, so trying a budget costs a
 * drag and keeping one costs a click.
 */
export function MemoryPanel({
  storeId,
  cache,
  counts,
  onChanged,
}: {
  storeId: string;
  cache?: CacheStats;
  counts?: CacheCounts;
  onChanged?: () => void;
}) {
  const [report, setReport] = useState<MemoryReport | null>(null);
  // where a slider is being dragged to, until it lands: live samples must not fight the pointer
  const [drafts, setDrafts] = useState<Record<string, number>>({});
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  // the panel is the page: the rows have room for everything behind them, so they show it
  const detailed = usePanelMaximized();

  const apply = useCallback((data: MemoryReport) => setReport(data), []);
  useLive<MemoryReport>("memory", { storeId }, apply);

  const budgets = report?.budgets ?? [];
  const valueOf = (b: MemoryBudget) => drafts[b.key] ?? b.limitBytes;
  const changed = budgets.filter((b) => b.settingPath && valueOf(b) !== b.settingBytes);

  // Applied where the gesture ends, never while it moves, and the slider is never disabled for it:
  // a control that goes dead under the pointer loses the drag. A second gesture may land while the
  // first is still answering, so the draft is only dropped when the answer is about the value the
  // slider still holds.
  async function commit(b: MemoryBudget) {
    const bytes = drafts[b.key];
    if (bytes == null || bytes === b.limitBytes || !b.adjustable) return;
    try {
      const result = await applyMemoryBudget(storeId, b.kind, b.engineId, bytes);
      setReport(result.report);
      setDrafts((d) => {
        if (d[b.key] !== bytes) return d; // moved again since: its own commit will follow
        const next = { ...d };
        delete next[b.key];
        return next;
      });
      setMessage(result.applied ? `${b.label} now runs on ${formatBytes(bytes)}.` : `${b.label} could not be changed while the database is open.`);
    } catch (e) {
      showError("Could not change the budget", e instanceof Error ? e.message : String(e));
    }
  }

  async function onSave() {
    const values: Record<string, number> = {};
    for (const b of changed) values[b.settingPath!] = b.settingUnit === "GB" ? valueOf(b) / gb : Math.round(valueOf(b) / mb);
    setBusy("save");
    try {
      const result = await saveDatabaseSettings(storeId, values, false);
      if (result.rejected && result.rejected.length > 0) {
        await showError("Not saved", "The settings file did not take these values.", result.rejected.map((r) => `${r.path} — ${r.reason}`));
      } else {
        setMessage("Saved: these are the budgets the database opens with from now on.");
      }
      setDrafts({});
      setReport(await fetchMemory(storeId));
    } catch (e) {
      showError("Could not save", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(null);
    }
  }

  // back to what the settings file says, applied at once where that is possible
  async function onReset() {
    setBusy("reset");
    try {
      for (const b of changed) {
        if (b.adjustable && b.settingBytes !== b.limitBytes) await applyMemoryBudget(storeId, b.kind, b.engineId, b.settingBytes);
      }
      setDrafts({});
      setReport(await fetchMemory(storeId));
      setMessage("Back to the saved budgets.");
    } catch (e) {
      showError("Could not reset", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(null);
    }
  }

  async function onClearCaches() {
    setBusy("clear");
    try {
      const result = await clearCaches(storeId);
      setMessage(
        `Cleared ${formatCount(result.entriesCleared)} entr${result.entriesCleared === 1 ? "y" : "ies"}` +
          `${result.freedBytes > 0 ? `, freeing ${formatBytes(result.freedBytes)}` : ""}. The indexes warm again in the background.`,
      );
      setReport(await fetchMemory(storeId));
      onChanged?.();
    } catch (e) {
      showError("Could not clear the caches", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(null);
    }
  }

  // the process, not this database: the collection is deep, blocking and compacting, and there is
  // one heap behind every database on this server
  async function onCollect() {
    setBusy("collect");
    try {
      setMessage((await collectGarbage()).message);
      setReport(await fetchMemory(storeId));
    } catch (e) {
      showError("Could not collect", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(null);
    }
  }

  /** The hit counts of one row, where it is a cache the store keeps count of: node and result sets. */
  function hitCounts(b: MemoryBudget): { hits: number; misses: number; overflows: number; entries: number | null } | null {
    if (cache == null) return null;
    if (b.kind === "NodeCache") {
      return { hits: cache.nodeCacheHits, misses: cache.nodeCacheMisses, overflows: cache.nodeCacheOverflows, entries: counts?.nodeCacheCount ?? null };
    }
    if (b.kind === "SetCache") {
      return { hits: cache.setCacheHits, misses: cache.setCacheMisses, overflows: cache.setCacheOverflows, entries: counts?.setCacheCount ?? null };
    }
    return null;
  }

  function tooltip(b: MemoryBudget): string {
    const lines = [b.help];
    if (b.settingPath == null) lines.push("Everything is resident, so there is no budget to set: what it costs is the heap.");
    else lines.push(b.adjustable ? "A change here applies at once." : "A change here applies the next time the database opens.");
    if (b.settingPath != null && b.settingBytes !== b.limitBytes) {
      lines.push(`Saved as ${formatBytes(b.settingBytes)}; the running database is on ${formatBytes(b.limitBytes)}.`);
    }
    if (b.floorBytes > 0 && valueOf(b) < b.floorBytes) lines.push(`It holds ${formatBytes(b.floorBytes)} it cannot give back, which is over this budget.`);
    const hit = hitCounts(b);
    if (hit != null && hit.hits + hit.misses > 0) {
      lines.push(`${formatCount(hit.hits)} hits · ${formatCount(hit.misses)} misses · ${Math.round((hit.hits / (hit.hits + hit.misses)) * 100)}% answered from memory`);
    }
    return lines.join("\n");
  }

  /**
   * Everything behind one row, written out. The bar says holding against budget and the tooltip says
   * the rest; this is the same material with nothing left out, which is what the room is for.
   */
  function details(b: MemoryBudget): Detail[] {
    const list: Detail[] = [];
    const value = valueOf(b);
    const used = b.usedBytes;
    list.push({ k: "Holding", v: used == null ? "not counted" : formatBytes(used) });
    if (b.settingPath == null) {
      list.push({ k: "Budget", v: "none - resident" });
    } else {
      list.push({ k: "Budget", v: formatBytes(value) });
      list.push({
        k: "Saved",
        v: b.settingBytes === b.limitBytes ? formatBytes(b.settingBytes) : `${formatBytes(b.settingBytes)} (running on ${formatBytes(b.limitBytes)})`,
        title: `The settings file says ${formatBytes(b.settingBytes)}, which is what the database opens with.`,
      });
      if (used != null) {
        const room = value - used;
        list.push({
          k: room >= 0 ? "Headroom" : "Over budget",
          v: formatBytes(Math.abs(room)),
          title: room >= 0 ? "Memory it is allowed and not using." : "It is holding more than the budget: what is over it is being evicted, or cannot be given back.",
        });
        if (value > 0) list.push({ k: "Fill", v: Math.round((used / value) * 100) + "%" });
      }
      if (b.floorBytes > 0) {
        list.push({ k: "Never released", v: formatBytes(b.floorBytes), title: "Held whatever the budget says: a resident graph the index cannot give back." });
      }
    }
    const hit = hitCounts(b);
    if (hit != null) {
      if (hit.entries != null) list.push({ k: "Entries", v: formatCount(hit.entries), title: "What it holds this second." });
      list.push({ k: "Hits", v: formatCount(hit.hits), title: "Answered from memory since the caches were last cleared." });
      list.push({ k: "Misses", v: formatCount(hit.misses), title: "Had to be read or computed since the caches were last cleared." });
      list.push({
        k: "Answered",
        v: hit.hits + hit.misses === 0 ? "—" : Math.round((hit.hits / (hit.hits + hit.misses)) * 100) + "%",
        title: "The share of lookups the cache answered. A low share on a large cache is memory better spent elsewhere.",
      });
      list.push({ k: "Trims", v: formatCount(hit.overflows), title: "Times it filled up and was cut back to half. Many of these means the budget is too small for what is being read." });
    }
    list.push({ k: "Engine", v: b.engine });
    if (b.settingPath != null) {
      list.push({ k: "Takes effect", v: b.adjustable ? "at once" : "next open", title: b.adjustable ? "A change here applies to the running database." : "The running database keeps its budget until it is opened again." });
      list.push({ k: "Setting", v: b.settingPath, title: b.settingPath + (b.settingUnit ? " (" + b.settingUnit + ")" : ""), wide: true });
    }
    return list;
  }

  /**
   * The aggregate cache: counts, sums and facet totals a query already worked out. It has no budget
   * to drag - it is a fixed number of entries, and the bytes behind them are the sets it points at,
   * which the set cache is already accounting for - so it is only here where there is room for it.
   */
  function aggregateDetails(c: CacheStats): Detail[] {
    const lookups = c.aggregateCacheHits + c.aggregateCacheMisses;
    return [
      { k: "Entries", v: formatCount(c.aggregateCacheCount) },
      { k: "Hits", v: formatCount(c.aggregateCacheHits), title: "Answered from memory since the caches were last cleared." },
      { k: "Misses", v: formatCount(c.aggregateCacheMisses), title: "Had to be counted again since the caches were last cleared." },
      { k: "Answered", v: lookups === 0 ? "—" : Math.round((c.aggregateCacheHits / lookups) * 100) + "%" },
      { k: "Trims", v: c.aggregateCacheOverflows == null ? "—" : formatCount(c.aggregateCacheOverflows), title: "Times it filled up and was cut back to half." },
      { k: "Budget", v: "a fixed number of entries" },
    ];
  }

  const totals = report == null ? "" : `${formatBytes(report.managedBytes)} heap · ${formatBytes(report.processBytes)} resident`;
  return (
    <section className="panel panel-fill">
      <h3>
        Memory <span className="panel-sub">{totals || "what each part may keep, and what it holds"}</span>
      </h3>
      <div className={"fill-body mem-list" + (detailed ? " detailed" : "")}>
        {budgets.map((b) => (
          <BudgetRow
            key={b.key}
            budget={b}
            value={valueOf(b)}
            title={tooltip(b)}
            details={detailed ? details(b) : null}
            onDrag={(bytes) => setDrafts((d) => ({ ...d, [b.key]: bytes }))}
            onCommit={() => void commit(b)}
          />
        ))}
        {detailed && report?.open && cache != null && (
          // a cache with no bar of its own: the row is its name, what it holds, and the figures
          <div className="mem-row detailed" title={"Counts, sums and facet totals a query already worked out.\nNo budget to set: a fixed number of entries."}>
            <span className="mem-name">
              <span className="mem-label">Aggregate cache</span>
              <span className="mem-engine">Sets</span>
            </span>
            <span className="mem-track mem-resident muted">no budget</span>
            <span className="mem-figures">{formatCount(cache.aggregateCacheCount)} entries</span>
            <DetailBlock help="Counts, sums and facet totals a query already worked out, so a repeated count or a facet drilled into does not walk the sets again." details={aggregateDetails(cache)} />
          </div>
        )}
        {report != null && !report.open && <div className="muted">The database is {report.state.toLowerCase()}, so it is holding nothing.</div>}
      </div>
      <div className="dash-cache-actions">
        {changed.length > 0 && (
          <>
            <button className="action-button" onClick={() => void onSave()} disabled={busy !== null} title="Writes these budgets to the settings file, so they survive a restart">
              <IconDeviceFloppy size={14} stroke={1.8} /> {busy === "save" ? "Saving…" : `Save ${changed.length}`}
            </button>
            <button className="action-button" onClick={() => void onReset()} disabled={busy !== null} title="Back to the budgets in the settings file">
              <IconRotate size={14} stroke={1.8} /> {busy === "reset" ? "Resetting…" : "Back to saved"}
            </button>
          </>
        )}
        <button className="action-button" onClick={() => void onClearCaches()} disabled={busy !== null} title="Empties the caches of this database, so what fills again is what is really being used">
          <IconEraser size={14} stroke={1.8} /> {busy === "clear" ? "Clearing…" : "Clear caches"}
        </button>
        <button className="action-button" onClick={() => void onCollect()} disabled={busy !== null} title="The deepest collection the runtime allows - blocking, compacting, repeated - on the whole server process">
          <IconRecycle size={14} stroke={1.8} /> {busy === "collect" ? "Collecting…" : "Collect garbage"}
        </button>
      </div>
      <div className="muted dash-cache-note">
        {message ?? (detailed ? "drag to change a budget now, save to keep it; the hit counts are since the caches were last cleared" : "drag to change a budget now, save to keep it; hover a row for what it is for")}
      </div>
    </section>
  );
}

function BudgetRow({
  budget,
  value,
  title,
  details,
  onDrag,
  onCommit,
}: {
  budget: MemoryBudget;
  value: number;
  title: string;
  /** Written out under the bar when the panel has the page; null when it does not. */
  details: Detail[] | null;
  onDrag: (bytes: number) => void;
  onCommit: () => void;
}) {
  const b = budget;
  // the track runs to four times the saved budget, so the thumb sits a quarter along and the usage
  // beside it has room to be seen; it only ever grows, or the thumb would jump mid-drag
  const max = Math.max(b.suggestedMaxBytes, value, b.usedBytes ?? 0);
  const step = Math.max(16 * mb, Math.round(max / 128 / (16 * mb)) * 16 * mb);
  const used = b.usedBytes;
  const pending = b.settingPath != null && b.settingBytes !== b.limitBytes;
  return (
    // the tooltip stays on the detailed row too: it is the same material, and a pointer that has
    // learned to rest on a row should not find it gone
    <div className={"mem-row" + (details ? " detailed" : "")} title={title}>
      <span className="mem-name">
        <span className="mem-label">{b.label}</span>
        <span className="mem-engine">{b.engine}</span>
      </span>
      {b.settingPath == null ? (
        <span className="mem-track mem-resident muted">resident</span>
      ) : (
        <span className="mem-track">
          <span className="mem-fill">
            <span style={{ width: Math.min(100, ((used ?? 0) / max) * 100) + "%" }} />
          </span>
          <input
            type="range"
            min={0}
            max={max}
            step={step}
            value={value}
            aria-label={b.label + " budget"}
            onChange={(e) => onDrag(Number(e.target.value))}
            // where the gesture ends, and nowhere else: committing on blur too would race the click
            // that caused the blur when that click is one of the buttons below
            onPointerUp={onCommit}
            onKeyUp={onCommit}
          />
        </span>
      )}
      <span className={"mem-figures" + (pending ? " pending" : "")}>
        {used == null ? <span className="muted">—</span> : formatBytes(used)}
        {b.settingPath != null && <span className="muted"> / {formatBytes(value)}</span>}
      </span>
      {details != null && <DetailBlock help={b.help} details={details} />}
    </div>
  );
}

/** What a row is for, and every figure behind it. Sits under the bar, across the whole row. */
function DetailBlock({ help, details }: { help: string; details: Detail[] }) {
  return (
    <div className="mem-detail">
      <div className="mem-help">{help}</div>
      <div className="mem-facts">
        {details.map((d) => (
          <div className={"fact" + (d.wide ? " wide" : "")} key={d.k}>
            <div className="fact-k">{d.k}</div>
            <div className="fact-v" title={d.title ?? d.v}>
              {d.v}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
