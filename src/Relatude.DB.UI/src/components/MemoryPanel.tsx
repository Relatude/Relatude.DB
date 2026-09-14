import { useCallback, useState } from "react";
import { IconDeviceFloppy, IconEraser, IconRecycle, IconRotate } from "@tabler/icons-react";
import { showError } from "../dialogs";
import { clearCaches } from "../server/dashboard";
import { collectGarbage } from "../server/overview";
import { applyMemoryBudget, fetchMemory, type MemoryBudget, type MemoryReport } from "../server/memory";
import { saveDatabaseSettings } from "../server/settings";
import { useLive } from "../live";
import { formatBytes, formatCount } from "../format";

const mb = 1024 * 1024;
const gb = 1024 * mb;

/** The hit counts of the two caches, from the dashboard's own reading: they belong in the tooltips. */
export interface CacheStats {
  nodeCacheHits: number;
  nodeCacheMisses: number;
  setCacheHits: number;
  setCacheMisses: number;
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
 * Letting a slider go applies the bound at once wherever the part can be re-sized while it runs.
 * Saving is separate and writes the same values into the settings file, so trying a budget costs a
 * drag and keeping one costs a click.
 */
export function MemoryPanel({ storeId, cache, onChanged }: { storeId: string; cache?: CacheStats; onChanged?: () => void }) {
  const [report, setReport] = useState<MemoryReport | null>(null);
  // where a slider is being dragged to, until it lands: live samples must not fight the pointer
  const [drafts, setDrafts] = useState<Record<string, number>>({});
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

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

  function tooltip(b: MemoryBudget): string {
    const lines = [b.help];
    if (b.settingPath == null) lines.push("Everything is resident, so there is no budget to set: what it costs is the heap.");
    else lines.push(b.adjustable ? "A change here applies at once." : "A change here applies the next time the database opens.");
    if (b.settingPath != null && b.settingBytes !== b.limitBytes) {
      lines.push(`Saved as ${formatBytes(b.settingBytes)}; the running database is on ${formatBytes(b.limitBytes)}.`);
    }
    if (b.floorBytes > 0 && valueOf(b) < b.floorBytes) lines.push(`It holds ${formatBytes(b.floorBytes)} it cannot give back, which is over this budget.`);
    const hits = b.kind === "NodeCache" ? cache?.nodeCacheHits : b.kind === "SetCache" ? cache?.setCacheHits : undefined;
    const misses = b.kind === "NodeCache" ? cache?.nodeCacheMisses : b.kind === "SetCache" ? cache?.setCacheMisses : undefined;
    if (hits != null && misses != null && hits + misses > 0) {
      lines.push(`${formatCount(hits)} hits · ${formatCount(misses)} misses · ${Math.round((hits / (hits + misses)) * 100)}% answered from memory`);
    }
    return lines.join("\n");
  }

  const totals = report == null ? "" : `${formatBytes(report.managedBytes)} heap · ${formatBytes(report.processBytes)} resident`;
  return (
    <section className="panel panel-fill">
      <h3>
        Memory <span className="panel-sub">{totals || "what each part may keep, and what it holds"}</span>
      </h3>
      <div className="fill-body mem-list">
        {budgets.map((b) => (
          <BudgetRow key={b.key} budget={b} value={valueOf(b)} title={tooltip(b)} onDrag={(bytes) => setDrafts((d) => ({ ...d, [b.key]: bytes }))} onCommit={() => void commit(b)} />
        ))}
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
        {message ?? "drag to change a budget now, save to keep it; hover a row for what it is for"}
      </div>
    </section>
  );
}

function BudgetRow({
  budget,
  value,
  title,
  onDrag,
  onCommit,
}: {
  budget: MemoryBudget;
  value: number;
  title: string;
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
    <div className="mem-row" title={title}>
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
    </div>
  );
}
