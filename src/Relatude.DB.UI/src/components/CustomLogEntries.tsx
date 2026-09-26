import { useCallback, useEffect, useRef, useState, type MouseEvent as ReactMouseEvent } from "react";
import {
  IconArrowAutofitWidth,
  IconChevronLeft,
  IconChevronRight,
  IconClock,
  IconColumns,
  IconCopy,
  IconDownload,
  IconFilter,
  IconSearch,
  IconSortAscending,
  IconSortDescending,
} from "@tabler/icons-react";
import { DialogTools } from "./DialogTools";
import { FilterCell, SearchBox } from "./LogsSection";
import { matchesTerm, type LogColumn, type LogEntry, type LogPage } from "../server/logs";
import { dataTypeLabel, downloadCustomLog, type CustomLogSummary, type ExportFormat } from "../server/customLogs";
import type { DatabaseInfo } from "../server/serverInfo";
import { useLive } from "../live";
import { showError } from "../dialogs";
import { formatBytes, formatCount, formatDateTime, formatTime } from "../format";
import { autoInterval, formatLogValue, rangeBounds, rangeLabel, rangePayload, type LogRange } from "../customLogRange";

/**
 * The entries of a custom log over the range the page is looking at.
 *
 * Two ways of narrowing them, as on the system logs: the search box is read on the server over the
 * whole range (and costs the range, so it is asked for with a button), the fields under the headings
 * filter what the browser holds - a window of the newest few thousand - as they are typed into.
 *
 * On top of that page: the order can be turned around, columns hidden and dragged wider (kept per
 * log), an entry opened to see all of it and to narrow the table by one of its values, and the
 * entries on screen - or the whole log - saved as a file.
 */

const pageSize = 100;
const filterWindow = 5000;
const timeKey = "*time";
const minColumnWidth = 60;

function readStore<T>(key: string, fallback: T): T {
  try {
    const saved = localStorage.getItem(key);
    return saved ? (JSON.parse(saved) as T) : fallback;
  } catch {
    return fallback;
  }
}
function writeStore(key: string, value: unknown) {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // nothing depends on it holding
  }
}

interface AppliedSearch {
  text: string;
  caseSensitive: boolean;
}
const noSearch: AppliedSearch = { text: "", caseSensitive: false };

export function CustomLogEntries({
  db,
  log,
  range,
  onRange,
  live,
  tick,
  handedSearch,
  onHandedSearchTaken,
  onStartRecording,
}: {
  db: DatabaseInfo;
  log: CustomLogSummary;
  range: LogRange;
  onRange: (range: LogRange) => void;
  live: boolean;
  tick: number;
  handedSearch: string | null;
  onHandedSearchTaken: () => void;
  onStartRecording: () => void;
}) {
  const [page, setPage] = useState<LogPage | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [skip, setSkip] = useState(0);
  const [newestFirst, setNewestFirst] = useState(true);
  const [filters, setFilters] = useState<Record<string, string>>({});
  const [searchText, setSearchText] = useState(handedSearch ?? "");
  const [matchCase, setMatchCase] = useState(false);
  const [applied, setApplied] = useState<AppliedSearch>(handedSearch ? { text: handedSearch, caseSensitive: false } : noSearch);
  const [loading, setLoading] = useState(true);
  const [widths, setWidths] = useState<Record<string, number>>(() => readStore("customLogs:columns:" + log.key, {}));
  const [hiddenColumns, setHiddenColumns] = useState<string[]>(() => readStore("customLogs:hiddenColumns:" + log.key, []));
  const [dragging, setDragging] = useState<string | null>(null);
  const [opened, setOpened] = useState<LogEntry | null>(null);
  const [columnsOpen, setColumnsOpen] = useState(false);
  const [downloadOpen, setDownloadOpen] = useState(false);
  // what arrived since the page before: marked for a moment, so a live table says what is new
  const [freshAfter, setFreshAfter] = useState<string | null>(null);
  const previousNewest = useRef<string | null>(null);

  // a search handed over from another view (a value clicked in the analysis) is run straight away
  useEffect(() => {
    if (handedSearch == null) return;
    setSearchText(handedSearch);
    setApplied({ text: handedSearch, caseSensitive: false });
    onHandedSearchTaken();
  }, [handedSearch, onHandedSearchTaken]);

  const columns = log.columns.filter((c) => !hiddenColumns.includes(c.key));
  const needles = Object.entries(filters)
    .map(([key, text]) => ({ key, column: log.columns.find((c) => c.key === key) ?? null, needle: text.trim().toLowerCase() }))
    .filter((f) => f.needle.length > 0);
  const filtering = needles.length > 0;
  const searching = applied.text.length > 0;
  const unsearched = searchText.trim() !== applied.text || matchCase !== applied.caseSensitive;
  const filterKey = needles.map((f) => f.key + "=" + f.needle).join("\n");
  const rangeKey = JSON.stringify(range);
  useEffect(() => setSkip(0), [rangeKey, filterKey, applied, newestFirst]);

  const take = filtering ? filterWindow : pageSize;
  const windowSkip = filtering ? 0 : skip;
  const apply = useCallback(
    (p: LogPage) => {
      const newest = p.entries.length > 0 ? (newestFirst ? p.entries[0].timestampUtc : p.entries[p.entries.length - 1].timestampUtc) : null;
      // only a page that follows the one before it has anything new in it: the same question, asked again
      setFreshAfter(previousNewest.current);
      previousNewest.current = newest;
      setPage(p);
      setError(null);
      setLoading(false);
    },
    [newestFirst],
  );
  const fail = useCallback((message: string) => {
    setPage(null);
    setError(message);
    setLoading(false);
  }, []);
  useEffect(() => {
    // a different question: nothing on the next page is new, it is only different
    previousNewest.current = null;
    setFreshAfter(null);
    setLoading(true);
  }, [rangeKey, windowSkip, take, applied, newestFirst, log.key]);
  useLive<LogPage>(
    "custom-logs-extract",
    {
      storeId: db.id,
      logKey: log.key,
      ...rangePayload(range),
      skip: windowSkip,
      take,
      search: searching ? applied.text : null,
      caseSensitive: applied.caseSensitive,
      newestFirst,
    },
    apply,
    // a live search would read the whole range again on every sample: the refresh button repeats it
    { once: !live || searching, restartOn: tick, minMs: autoInterval(range) === "Second" ? 1000 : 3000, onError: fail },
  );

  function runSearch(text = searchText, caseSensitive = matchCase) {
    setApplied({ text: text.trim(), caseSensitive });
  }
  function clearSearch() {
    setSearchText("");
    if (searching) setApplied(noSearch);
  }
  function toggleCase(on: boolean) {
    setMatchCase(on);
    if (searching) runSearch(searchText, on);
  }
  function setFilter(key: string, text: string) {
    setFilters((current) => ({ ...current, [key]: text }));
  }
  function toggleColumn(key: string) {
    setHiddenColumns((current) => {
      const next = current.includes(key) ? current.filter((k) => k !== key) : [...current, key];
      writeStore("customLogs:hiddenColumns:" + log.key, next);
      return next;
    });
  }

  const entries = page?.entries ?? [];
  const matches = filtering ? entries.filter((e) => needles.every((f) => haystack(e, f.key, f.column).some((text) => matchesTerm(text, f.needle)))) : entries;
  const rows = filtering ? matches.slice(skip, skip + pageSize) : matches;
  const total = filtering ? matches.length : (page?.total ?? 0);
  const beyondWindow = filtering && (page?.total ?? 0) > entries.length;

  const track = (key: string, fallback: string) => (widths[key] ? widths[key] + "px" : fallback);
  const gridTemplate = [track(timeKey, "170px"), ...columns.map((c) => track(c.key, c.dataType === "String" ? "minmax(0, 2fr)" : "minmax(0, 1fr)"))].join(" ");
  const minRowWidth = 20 + (widths[timeKey] ?? 170) + columns.reduce((sum, c) => sum + (widths[c.key] ?? 110), 0) + (columns.length + 1) * 10;
  const rowStyle = { gridTemplateColumns: gridTemplate, minWidth: minRowWidth };

  function startColumnResize(e: ReactMouseEvent, key: string) {
    e.preventDefault();
    e.stopPropagation();
    const cell = (e.currentTarget as HTMLElement).parentElement;
    const startWidth = widths[key] ?? Math.round(cell?.getBoundingClientRect().width ?? 120);
    const startX = e.clientX;
    setDragging(key);
    document.body.style.cursor = "col-resize";
    const move = (ev: MouseEvent) => {
      const next = Math.max(minColumnWidth, startWidth + ev.clientX - startX);
      setWidths((prev) => (prev[key] === next ? prev : { ...prev, [key]: next }));
    };
    const up = () => {
      window.removeEventListener("mousemove", move);
      window.removeEventListener("mouseup", up);
      document.body.style.cursor = "";
      setDragging(null);
      setWidths((current) => {
        writeStore("customLogs:columns:" + log.key, current);
        return current;
      });
    };
    window.addEventListener("mousemove", move);
    window.addEventListener("mouseup", up);
  }
  function resetColumn(key: string) {
    setWidths((prev) => {
      if (!(key in prev)) return prev;
      const next = { ...prev };
      delete next[key];
      writeStore("customLogs:columns:" + log.key, next);
      return next;
    });
  }

  const status = statusLine(range, total, skip, rows.length, filtering, entries.length, searching);
  return (
    <div className="clog-entries">
      <SearchBox
        value={searchText}
        onChange={setSearchText}
        onSearch={() => runSearch()}
        onClear={clearSearch}
        caseSensitive={matchCase}
        onCaseSensitive={toggleCase}
        unsearched={unsearched}
        busy={loading && searching}
        rangeLabel={rangeLabel(range).replace(/^the last /, "")}
        rangePhrase={rangeLabel(range)}
      />
      <div className="logs-toolbar">
        <span className="clog-entries-status">{status}</span>
        {filtering && (
          <button className="link-button" onClick={() => setFilters({})} title="Empty every filter field">
            <IconFilter size={14} stroke={1.8} /> Clear filters
          </button>
        )}
        <span className="logs-spacer" />
        <button className="action-button" onClick={() => setNewestFirst(!newestFirst)} title={newestFirst ? "Newest entries first" : "Oldest entries first"}>
          {newestFirst ? <IconSortDescending size={15} stroke={1.8} /> : <IconSortAscending size={15} stroke={1.8} />} {newestFirst ? "Newest first" : "Oldest first"}
        </button>
        <span className="clog-popover-anchor">
          <button className={"action-button" + (hiddenColumns.length > 0 ? " armed" : "")} onClick={() => setColumnsOpen(!columnsOpen)} title="Which columns the table shows">
            <IconColumns size={15} stroke={1.8} /> Columns{hiddenColumns.length > 0 ? ` (${log.columns.length - hiddenColumns.filter((h) => log.columns.some((c) => c.key === h)).length}/${log.columns.length})` : ""}
          </button>
          {columnsOpen && (
            <div className="clog-popover" onMouseLeave={() => setColumnsOpen(false)}>
              {log.columns.map((c) => (
                <label key={c.key} className="clog-popover-item">
                  <input type="checkbox" checked={!hiddenColumns.includes(c.key)} onChange={() => toggleColumn(c.key)} />
                  {c.name} <span className="muted">{c.key}</span>
                </label>
              ))}
            </div>
          )}
        </span>
        {Object.keys(widths).length > 0 && (
          <button
            className="action-button"
            onClick={() => {
              setWidths({});
              writeStore("customLogs:columns:" + log.key, {});
            }}
            title="Give every column back to the row"
          >
            <IconArrowAutofitWidth size={15} stroke={1.8} /> Reset widths
          </button>
        )}
        <button className="action-button" onClick={() => setDownloadOpen(true)} disabled={!log.firstRecordUtc} title={log.firstRecordUtc ? "Save the entries as a file" : "Nothing recorded to download"}>
          <IconDownload size={15} stroke={1.8} /> Download
        </button>
      </div>

      {!log.enabledLog && (
        <div className="logs-note">
          Entries are not being recorded{log.enabledStatistics ? " - only counted into the statistics" : ""}.
          <button className="link-button" onClick={onStartRecording}>
            Start recording
          </button>
        </div>
      )}
      {error && <div className="logs-note">{error}</div>}
      {beyondWindow && (
        <div className="logs-note">
          The filters look at the {newestFirst ? "newest" : "oldest"} {formatCount(entries.length)} of the {formatCount(page?.total ?? 0)} entries{" "}
          {searching ? "the search matched" : "in the range"}. The search box reads every one of them.
        </div>
      )}

      <section className="panel clog-entries-panel">
        <div className={"log-table" + (dragging ? " resizing" : "")}>
          <div className="log-table-row log-table-head with-filter-row" style={rowStyle}>
            {[{ key: timeKey, name: "Time" }, ...columns].map((c) => (
              <span key={c.key} className="log-head-cell" title={c.key === timeKey ? "When the entry was recorded" : `${c.name} · ${c.key}`}>
                <span className="log-head-label">{c.name}</span>
                <span
                  className={"log-col-grip" + (dragging === c.key ? " active" : "") + (widths[c.key] ? " sized" : "")}
                  onMouseDown={(e) => startColumnResize(e, c.key)}
                  onDoubleClick={() => resetColumn(c.key)}
                  title={widths[c.key] ? "Drag to resize, double-click to fit it to the row again" : "Drag to resize this column"}
                />
              </span>
            ))}
          </div>
          <div className="log-table-row log-table-filter" style={rowStyle}>
            <FilterCell columnKey={timeKey} label="Time" value={filters[timeKey] ?? ""} onChange={setFilter} />
            {columns.map((c) => (
              <FilterCell key={c.key} columnKey={c.key} label={c.name} value={filters[c.key] ?? ""} onChange={setFilter} />
            ))}
          </div>
          {rows.map((entry, i) => (
            <div
              key={entry.timestampUtc + ":" + i}
              className={"log-table-row clickable" + (freshAfter && live && entry.timestampUtc > freshAfter ? " fresh" : "")}
              style={rowStyle}
              onClick={() => setOpened(entry)}
              title="Show the whole entry"
            >
              <span className="log-time" title={entry.timestampUtc}>
                {formatTime(entry.timestampUtc)}
              </span>
              {columns.map((c) => {
                const text = formatLogValue(entry.values[c.key], c.dataType);
                return (
                  <span key={c.key} className={"log-cell" + (c.dataType === "String" ? "" : " num-cell")} title={text}>
                    {text}
                  </span>
                );
              })}
            </div>
          ))}
          {rows.length === 0 && !loading && (
            <div className="log-table-empty">
              {filtering ? "Nothing matches the filters." : searching ? `Nothing in ${rangeLabel(range)} matches the search.` : `Nothing recorded in ${rangeLabel(range)}.`}
            </div>
          )}
        </div>
        {total > pageSize && (
          <div className="logs-paging">
            <button className="action-button" disabled={skip === 0} onClick={() => setSkip(Math.max(0, skip - pageSize))}>
              <IconChevronLeft size={15} stroke={1.8} /> {newestFirst ? "Newer" : "Older"}
            </button>
            <button className="action-button" disabled={skip + pageSize >= total} onClick={() => setSkip(skip + pageSize)}>
              {newestFirst ? "Older" : "Newer"} <IconChevronRight size={15} stroke={1.8} />
            </button>
            <span className="muted">
              page {formatCount(Math.floor(skip / pageSize) + 1)} of {formatCount(Math.ceil(total / pageSize))}
            </span>
          </div>
        )}
      </section>

      {opened && (
        <EntryDialog
          log={log}
          entry={opened}
          onClose={() => setOpened(null)}
          onFilter={(key, text) => {
            setFilter(key, text);
            setOpened(null);
          }}
          onSearch={(text) => {
            setSearchText(text);
            runSearch(text, false);
            setOpened(null);
          }}
          onAround={() => {
            const t = Date.parse(opened.timestampUtc);
            onRange({
              kind: "between",
              fromUtc: new Date(t - 60_000).toISOString(),
              toUtc: new Date(t + 60_001).toISOString(),
              label: `a minute either side of ${formatTime(opened.timestampUtc)}`,
            });
            setOpened(null);
          }}
        />
      )}
      {downloadOpen && (
        <DownloadDialog
          db={db}
          log={log}
          range={range}
          inRange={searching || !filtering ? (page?.total ?? null) : null}
          search={applied}
          onClose={() => setDownloadOpen(false)}
        />
      )}
    </div>
  );
}

/** What a filter looks in, in one cell: the text shown, and the value it came from. */
function haystack(entry: LogEntry, key: string, column: LogColumn | null): string[] {
  if (key === timeKey) return [formatTime(entry.timestampUtc).toLowerCase(), entry.timestampUtc.toLowerCase()];
  const value = entry.values[key];
  return [formatLogValue(value, column?.dataType ?? "String").toLowerCase(), value == null ? "" : String(value).toLowerCase()];
}

function statusLine(range: LogRange, total: number, skip: number, shown: number, filtering: boolean, searched: number, searching: boolean): string {
  const where = rangeLabel(range);
  if (filtering) {
    if (total === 0) return `nothing matches in the ${formatCount(searched)} ${searched === 1 ? "entry" : "entries"} looked at`;
    return `${formatCount(skip + 1)}–${formatCount(skip + shown)} of ${formatCount(total)} matching, of ${formatCount(searched)} looked at`;
  }
  if (searching) {
    if (total === 0) return `nothing in ${where} matches the search`;
    return `${formatCount(skip + 1)}–${formatCount(skip + shown)} of ${formatCount(total)} matching in ${where}`;
  }
  if (total === 0) return `nothing recorded in ${where}`;
  return `${formatCount(skip + 1)}–${formatCount(skip + shown)} of ${formatCount(total)} in ${where}`;
}

/**
 * One entry, all of it: every column with its value as shown and as it was stored, the values it
 * carries for columns the log no longer declares, and the ways to narrow the table by what is in it.
 */
function EntryDialog({
  log,
  entry,
  onClose,
  onFilter,
  onSearch,
  onAround,
}: {
  log: CustomLogSummary;
  entry: LogEntry;
  onClose: () => void;
  onFilter: (key: string, text: string) => void;
  onSearch: (text: string) => void;
  onAround: () => void;
}) {
  const [copied, setCopied] = useState(false);
  const extra = Object.keys(entry.values).filter((k) => !log.columns.some((c) => c.key === k));
  async function copy() {
    try {
      await navigator.clipboard.writeText(JSON.stringify({ "@timestamp": entry.timestampUtc, ...entry.values }, null, 2));
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      showError("Could not copy", "The browser did not allow the page to write to the clipboard.");
    }
  }
  const searchFor = (key: string, value: unknown) => {
    const text = String(value);
    return `${key}:${/\s/.test(text) ? `"${text}"` : text}`;
  };
  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="dialog dialog-wide clog-entry-dialog">
        <h3>
          {log.name || log.key} · {formatDateTime(entry.timestampUtc)}
          <DialogTools onClose={onClose} />
        </h3>
        <div className="clog-entry-time muted">{entry.timestampUtc} (UTC)</div>
        <div className="clog-entry-values">
          {log.columns.map((c) => {
            const value = entry.values[c.key];
            const shown = formatLogValue(value, c.dataType);
            const raw = value == null ? "" : typeof value === "object" ? JSON.stringify(value) : String(value);
            return (
              <div key={c.key} className="clog-entry-row">
                <span className="clog-entry-name" title={`${c.key} · ${dataTypeLabel[c.dataType]}`}>
                  {c.name}
                  <span className="muted"> {c.key}</span>
                </span>
                <span className={"clog-entry-value" + (value == null ? " muted" : "")}>
                  {shown}
                  {/* the stored value beside the shown one where formatting changes what it says: a
                      moment in UTC, a duration as .NET wrote it - not a number with a separator added */}
                  {raw && raw !== shown && c.dataType !== "Integer" && c.dataType !== "Double" && <span className="muted clog-entry-raw"> {raw}</span>}
                </span>
                {value != null && (
                  <span className="clog-entry-actions">
                    <button className="icon-button" title="Filter the table by this value" onClick={() => onFilter(c.key, raw.length > 80 ? raw.slice(0, 80) : raw)}>
                      <IconFilter size={14} stroke={1.8} />
                    </button>
                    <button className="icon-button" title="Search the whole range for this value in this column" onClick={() => onSearch(searchFor(c.key, raw))}>
                      <IconSearch size={14} stroke={1.8} />
                    </button>
                  </span>
                )}
              </div>
            );
          })}
          {extra.length > 0 && (
            <>
              <div className="clog-entry-extra muted">Values for columns the log no longer declares</div>
              {extra.map((k) => (
                <div key={k} className="clog-entry-row">
                  <span className="clog-entry-name">{k}</span>
                  <span className="clog-entry-value">{formatLogValue(entry.values[k], "String")}</span>
                  <span />
                </div>
              ))}
            </>
          )}
        </div>
        <div className="dialog-row">
          <button className="action-button" onClick={copy}>
            <IconCopy size={15} stroke={1.8} /> {copied ? "Copied" : "Copy as json"}
          </button>
          <button className="action-button" onClick={onAround} title="Show what else was recorded around this moment">
            <IconClock size={15} stroke={1.8} /> Entries around it
          </button>
          <span className="header-spacer" />
          <button className="action-button" onClick={onClose}>
            Close
          </button>
        </div>
      </div>
    </div>
  );
}

const formats: { id: ExportFormat; label: string; hint: string }[] = [
  { id: "tsv", label: "Tab separated", hint: "opens in a spreadsheet as it is" },
  { id: "csv", label: "Comma separated", hint: "quoted where a value needs it; line breaks kept" },
  { id: "jsonl", label: "Json lines", hint: "one json object per entry, values as their types" },
];

/** A download, asked for by format and by how much: the range on screen, or all the log has kept. */
function DownloadDialog({
  db,
  log,
  range,
  inRange,
  search,
  onClose,
}: {
  db: DatabaseInfo;
  log: CustomLogSummary;
  range: LogRange;
  inRange: number | null;
  search: AppliedSearch;
  onClose: () => void;
}) {
  const [format, setFormat] = useState<ExportFormat>(() => readStore("customLogs:downloadFormat", "tsv"));
  const [whole, setWhole] = useState(false);
  const [busy, setBusy] = useState(false);
  async function download() {
    setBusy(true);
    writeStore("customLogs:downloadFormat", format);
    try {
      const bounds = whole ? { fromUtc: null, toUtc: null } : rangeBounds(range);
      await downloadCustomLog(db.id, log.key, format, bounds.fromUtc, bounds.toUtc, search.text || null, search.caseSensitive);
      onClose();
    } catch (e) {
      showError("Could not download the log", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }
  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="dialog clog-download-dialog">
        <h3>
          <IconDownload size={16} stroke={2} /> Download {log.name || log.key}
          <DialogTools onClose={onClose} closeTitle="Cancel" />
        </h3>
        <div className="dialog-label">Format</div>
        <div className="dialog-choices">
          {formats.map((f) => (
            <label key={f.id} className={"dialog-choice clog-choice" + (format === f.id ? " chosen" : "")}>
              <span>
                <input type="radio" name="format" checked={format === f.id} onChange={() => setFormat(f.id)} /> {f.label} <span className="muted">.{f.id}</span>
              </span>
              <span className="muted">{f.hint}</span>
            </label>
          ))}
        </div>
        <div className="dialog-label">How much</div>
        <div className="dialog-choices">
          <label className={"dialog-choice clog-choice" + (!whole ? " chosen" : "")}>
            <span>
              <input type="radio" name="scope" checked={!whole} onChange={() => setWhole(false)} /> {rangeLabel(range)}
            </span>
            <span className="muted">
              the range the page is showing{inRange != null ? ` · ${formatCount(inRange)} ${inRange === 1 ? "entry" : "entries"}` : ""}
            </span>
          </label>
          <label className={"dialog-choice clog-choice" + (whole ? " chosen" : "")}>
            <span>
              <input type="radio" name="scope" checked={whole} onChange={() => setWhole(true)} /> The whole log
            </span>
            <span className="muted">
              {log.firstRecordUtc && log.lastRecordUtc ? `${formatDateTime(log.firstRecordUtc)} – ${formatDateTime(log.lastRecordUtc)} · ${formatBytes(log.logBytes)} on disk` : "everything it has kept"}
            </span>
          </label>
        </div>
        {search.text && <div className="dialog-body clog-download-search">Only the entries matching “{search.text}”, as in the table.</div>}
        <div className="dialog-row">
          <span className="header-spacer" />
          <button className="action-button primary" onClick={download} disabled={busy}>
            <IconDownload size={15} stroke={1.8} /> {busy ? "Writing…" : "Download"}
          </button>
          <button className="action-button" onClick={onClose}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}
