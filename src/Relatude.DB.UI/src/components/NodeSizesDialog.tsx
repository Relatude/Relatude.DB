import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { IconChartHistogram, IconExternalLink, IconRefresh, IconSearch, IconX } from "@tabler/icons-react";
import { DialogTools } from "./DialogTools";
import { KindIcon } from "./DatamodelIcons";
import { Loading } from "./Loading";
import { CopyButton } from "./CopyButton";
import { CopyText } from "./CopyText";
import {
  fetchLargestNodes,
  fetchNodeAnatomy,
  fetchNodeSizes,
  fetchTypeAnatomy,
  type AnatomyPart,
  type LargestList,
  type NodeAnatomyView,
  type NodeSizeType,
  type NodeSizesOverview,
  type SizeStats,
  type TypeAnatomyView,
} from "../server/nodeSizes";
import type { DatabaseInfo } from "../server/serverInfo";
import { openInDatamodel, openInQuery } from "../navigate";
import { formatBytes, formatCount } from "../format";

/**
 * How big the nodes of a database are, and which ones are the biggest.
 *
 * Every node has a segment in the log file - the position and length of its current bytes - which
 * the store keeps to read the node back. Those lengths are the binary size of every node, so the
 * dialog's pictures cost the server a walk of the segment map and not a single read:
 *
 *   - the spread of sizes, on a scale of doublings, as nodes or as the bytes they take. The two
 *     answer different questions - where most nodes are, and where most of the storage is - and a
 *     few huge nodes usually make them disagree;
 *   - where in the log file the current nodes sit. What is not current node data is history (the
 *     older versions of nodes, deleted ones), relations and the log's own framing - the part a
 *     truncation would drop;
 *   - the types, with their totals and spread, which is also the table view of both pictures.
 *
 * Only the lists read nodes: the largest of the selection (to name them), one node taken apart
 * property by property, and a sample of a type's nodes to say which of its properties the bytes are
 * in. Grouping by type stacks the pictures by the types with the most bytes; picking a type, a
 * size bar or a node narrows everything below it.
 */
export function NodeSizesDialog({ db, onClose }: { db: DatabaseInfo; onClose: () => void }) {
  const [overview, setOverview] = useState<NodeSizesOverview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [round, setRound] = useState(0); // "measure again"
  const [groupBy, setGroupBy] = useState<GroupBy>(() => (readStored(groupByKey) === "none" ? "none" : "type"));
  const [measure, setMeasure] = useState<Measure>(() => (readStored(measureKey) === "bytes" ? "bytes" : "nodes"));
  const [typeId, setTypeId] = useState<string | null>(null);
  // the size bar picked, as the bytes it covers: a measurement taken again may draw other bars
  const [range, setRange] = useState<{ min: number; max: number } | null>(null);
  const [take, setTake] = useState(listStep);
  const [list, setList] = useState<LargestList | null>(null);
  const [listError, setListError] = useState<string | null>(null);
  const [listLoading, setListLoading] = useState(false);
  const [nodeId, setNodeId] = useState<number | null>(null);
  const [node, setNode] = useState<NodeAnatomyView | null>(null);
  const [typeView, setTypeView] = useState<TypeAnatomyView | null>(null);
  const [examineError, setExamineError] = useState<string | null>(null);
  const [typeSort, setTypeSort] = useState<TypeSort>("bytes");
  const dialogRef = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  // focus in the dialog, so Escape closes it without a click first
  useEffect(() => dialogRef.current?.focus(), []);

  useEffect(() => writeStored(groupByKey, groupBy), [groupBy]);
  useEffect(() => writeStored(measureKey, measure), [measure]);

  useEffect(() => {
    let live = true;
    setError(null);
    fetchNodeSizes(db.id)
      .then((o) => {
        if (!live) return;
        setOverview(o);
        // a type picked in a measurement that no longer has it is let go of
        setTypeId((t) => (t && o.types.some((x) => x.id === t) ? t : null));
      })
      .catch((e) => live && setError(e instanceof Error ? e.message : String(e)));
    return () => {
      live = false;
    };
  }, [db.id, round]);

  // the bar the range is, while the measurement still has one exactly like it
  const bucket = useMemo(() => {
    if (!overview || !range) return null;
    const i = overview.edges.indexOf(range.min);
    return i >= 0 && overview.edges[i + 1] === range.max ? i : null;
  }, [overview, range]);
  useEffect(() => {
    if (!overview) return;
    let live = true;
    setListLoading(true);
    setListError(null);
    fetchLargestNodes(db.id, { typeId, minBytes: range?.min ?? null, maxBytes: range?.max ?? null, take })
      .then((l) => live && setList(l))
      .catch((e) => live && setListError(e instanceof Error ? e.message : String(e)))
      .finally(() => live && setListLoading(false));
    return () => {
      live = false;
    };
  }, [db.id, overview, typeId, range?.min, range?.max, take]);

  useEffect(() => {
    setNode(null);
    setExamineError(null);
    if (nodeId == null) return;
    let live = true;
    fetchNodeAnatomy(db.id, nodeId)
      .then((n) => live && setNode(n))
      .catch((e) => live && setExamineError(e instanceof Error ? e.message : String(e)));
    return () => {
      live = false;
    };
  }, [db.id, nodeId, round]);

  useEffect(() => {
    setTypeView(null);
    setExamineError(null);
    if (!typeId) return;
    let live = true;
    fetchTypeAnatomy(db.id, typeId)
      .then((t) => live && setTypeView(t))
      .catch((e) => live && setExamineError(e instanceof Error ? e.message : String(e)));
    return () => {
      live = false;
    };
  }, [db.id, typeId, round]);

  // Colour follows the type, never what is on screen: the types with the most bytes take the
  // palette in order and keep it whatever is picked, so a type is the same colour in both pictures,
  // the table and the legend, and narrowing to one does not repaint it.
  const colorOf = useMemo(() => {
    const map = new Map<string, string>();
    overview?.types.forEach((t, i) => map.set(t.id, i < namedSeries ? `var(--viz-${i + 1})` : otherColor));
    return (id: string) => map.get(id) ?? otherColor;
  }, [overview]);

  const selectedType = overview?.types.find((t) => t.id === typeId) ?? null;

  // the series both pictures stack: the named types and the rest folded, or one series in all
  const series = useMemo((): Series[] => {
    if (!overview) return [];
    const sizes = (t: NodeSizeType) => (measure === "nodes" ? t.sizeCounts : t.sizeBytes);
    if (selectedType) return [{ key: selectedType.id, label: selectedType.name, color: colorOf(selectedType.id), sizes: sizes(selectedType), position: selectedType.position }];
    if (groupBy === "none") {
      return [{ key: "all", label: "All nodes", color: "var(--viz-1)", sizes: sumOf(overview.types.map(sizes)), position: sumOf(overview.types.map((t) => t.position)) }];
    }
    const named = overview.types.slice(0, namedSeries);
    const rest = overview.types.slice(namedSeries);
    const out: Series[] = named.map((t) => ({ key: t.id, label: t.name, color: colorOf(t.id), sizes: sizes(t), position: t.position }));
    if (rest.length > 0) {
      out.push({
        key: "other",
        label: `${rest.length} other type${rest.length === 1 ? "" : "s"}`,
        color: otherColor,
        sizes: sumOf(rest.map(sizes)),
        position: sumOf(rest.map((t) => t.position)),
      });
    }
    return out;
  }, [overview, selectedType, groupBy, measure, colorOf]);

  const stats: SizeStats | null = selectedType ? selectedType.stats : (overview?.stats ?? null);
  const shownBytes = selectedType ? selectedType.bytes : (overview?.totalBytes ?? 0);
  const shownCount = selectedType ? selectedType.count : (overview?.measured ?? 0);

  // a selection that narrows the list starts it from the top again
  function pickType(id: string | null) {
    setTypeId((t) => (t === id ? null : id));
    setNodeId(null);
    setTake(listStep);
    listRef.current?.scrollTo(0, 0);
  }

  function pickBucket(b: number | null) {
    const picked = b != null && overview ? { min: overview.edges[b], max: overview.edges[b + 1] } : null;
    setRange((x) => (picked && x && x.min === picked.min && x.max === picked.max ? null : picked));
    setNodeId(null);
    setTake(listStep);
    listRef.current?.scrollTo(0, 0);
  }

  function openNode(n: { typeId: string; nodeId: string }) {
    onClose();
    openInQuery({ typeId: n.typeId, nodeId: n.nodeId });
  }

  function openProperty(part: AnatomyPart) {
    if (!part.declaringTypeId) return;
    onClose();
    openInDatamodel({ typeId: part.declaringTypeId, propertyId: part.propertyId });
  }

  const sortedTypes = useMemo(() => {
    const all = [...(overview?.types ?? [])];
    const by: Record<TypeSort, (t: NodeSizeType) => number> = {
      bytes: (t) => t.bytes,
      count: (t) => t.count,
      median: (t) => t.stats.median,
      p90: (t) => t.stats.p90,
      max: (t) => t.stats.max,
    };
    return all.sort((a, b) => by[typeSort](b) - by[typeSort](a) || a.name.localeCompare(b.name));
  }, [overview, typeSort]);

  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div
        ref={dialogRef}
        className="dialog node-sizes-dialog"
        role="dialog"
        aria-label="Node sizes"
        tabIndex={-1}
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            e.preventDefault();
            onClose();
          }
        }}
      >
        <h3>
          <IconChartHistogram size={16} stroke={1.9} /> Node sizes — {db.name}
          <DialogTools onClose={onClose} />
        </h3>
        {error && <div className="dialog-error">{error}</div>}
        {!overview && !error && <Loading label="Measuring every node from its segment in the log file…" />}
        {overview && (
          <div className="ns-body">
            <div className="ns-facts">
              <Fact label={selectedType ? `${selectedType.name} nodes` : "Nodes measured"} value={formatCount(shownCount)} />
              <Fact label="Node data" value={formatBytes(shownBytes)} title={`${formatCount(shownBytes)} bytes`} />
              {selectedType ? (
                <Fact label="Of all node data" value={percent(shownBytes, overview.totalBytes)} />
              ) : (
                <Fact
                  label="Of the log file"
                  value={percent(overview.totalBytes, overview.logFileSize)}
                  title={`${overview.logFileKey}: ${formatBytes(overview.logFileSize)}. The rest is earlier versions of nodes, deleted nodes, relations and the log's own framing.`}
                />
              )}
              <Fact label="Mean" value={formatBytes(stats?.mean ?? 0)} />
              <Fact label="Median" value={formatBytes(stats?.median ?? 0)} />
              <Fact label="90th percentile" value={formatBytes(stats?.p90 ?? 0)} />
              <Fact label="99th percentile" value={formatBytes(stats?.p99 ?? 0)} />
              <Fact label="Largest" value={formatBytes(stats?.max ?? 0)} title={`${formatCount(stats?.max ?? 0)} bytes`} />
            </div>
            <div className="ns-controls">
              <span className="ns-control-label">Group by</span>
              <div className="module-switch compact">
                <button className={groupBy === "none" ? "active" : ""} onClick={() => setGroupBy("none")}>
                  Nothing
                </button>
                <button className={groupBy === "type" ? "active" : ""} onClick={() => setGroupBy("type")}>
                  Node type
                </button>
              </div>
              <span className="ns-control-label">Count</span>
              <div className="module-switch compact">
                <button className={measure === "nodes" ? "active" : ""} onClick={() => setMeasure("nodes")} title="How many nodes have a size in each bar">
                  Nodes
                </button>
                <button className={measure === "bytes" ? "active" : ""} onClick={() => setMeasure("bytes")} title="How many bytes the nodes of each bar take together">
                  Bytes
                </button>
              </div>
              <span className="ns-control-label">Type</span>
              <select className="select compact ns-type-select" value={typeId ?? ""} onChange={(e) => pickType(e.target.value || null)}>
                <option value="">All types ({formatCount(overview.types.length)})</option>
                {overview.types.map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.name} — {formatCount(t.count)} nodes, {formatBytes(t.bytes)}
                  </option>
                ))}
              </select>
              {range && (
                <span className="ns-chip" title="The size bar picked in the chart">
                  {sizeLabel(range.min)} – {sizeLabel(range.max)}
                  <button className="icon-button" onClick={() => pickBucket(null)} title="Show every size again" aria-label="Clear size">
                    <IconX size={12} stroke={2} />
                  </button>
                </span>
              )}
              <span className="header-spacer" />
              <span className="muted ns-elapsed">
                {overview.notYetWritten > 0 && `${formatCount(overview.notYetWritten)} not yet written to the log, so not measured · `}
                measured in {formatCount(overview.elapsedMs)} ms
              </span>
              <button className="icon-button" onClick={() => setRound((r) => r + 1)} title="Measure again">
                <IconRefresh size={15} stroke={1.8} />
              </button>
            </div>
            {overview.measured === 0 ? (
              <div className="muted ns-empty">No node has been written to the log yet, so there is nothing to measure.</div>
            ) : (
              <>
                <div className="ns-row">
                  <section className="ns-panel ns-histogram">
                    <div className="ns-panel-head">
                      <span className="ns-panel-title">Size distribution</span>
                      <span className="muted">
                        {measure === "nodes" ? "nodes" : "bytes"} per size, on a scale of doublings · click a bar to list its nodes
                      </span>
                    </div>
                    {series.length > 1 && <Legend series={series} onPick={(key) => key !== "other" && pickType(key)} />}
                    <SizeHistogram
                      edges={overview.edges}
                      series={series}
                      measure={measure}
                      selected={bucket}
                      onSelect={pickBucket}
                    />
                  </section>
                  <section className="ns-panel ns-types">
                    <div className="ns-panel-head">
                      <span className="ns-panel-title">By node type</span>
                      <span className="muted">click a type to narrow everything to it</span>
                      <span className="header-spacer" />
                      <CopyButton title="Copy the table" table={() => typeTable(sortedTypes)} />
                    </div>
                    <div className="ns-table ns-type-table">
                      <div className="ns-type-row ns-table-head">
                        <span>Type</span>
                        <SortHead label="Nodes" sort="count" current={typeSort} onSort={setTypeSort} />
                        <SortHead label="Total" sort="bytes" current={typeSort} onSort={setTypeSort} />
                        <span />
                        <SortHead label="Median" sort="median" current={typeSort} onSort={setTypeSort} />
                        <SortHead label="90 %" sort="p90" current={typeSort} onSort={setTypeSort} />
                        <SortHead label="Largest" sort="max" current={typeSort} onSort={setTypeSort} />
                      </div>
                      {sortedTypes.map((t) => (
                        <button
                          key={t.id}
                          className={"ns-type-row ns-row-button" + (t.id === typeId ? " selected" : "")}
                          onClick={() => pickType(t.id)}
                          title={`${t.full}\n${formatCount(t.count)} nodes, ${formatCount(t.bytes)} bytes`}
                        >
                          <span className="ns-type-name">
                            <span className="ns-swatch" style={{ background: colorOf(t.id) }} />
                            <KindIcon kind={t.kind} size={13} />
                            <span className="ns-ellipsis">{t.name}</span>
                          </span>
                          <span className="num">{formatCount(t.count)}</span>
                          <span className="num">{formatBytes(t.bytes)}</span>
                          <span className="ns-share" title={percent(t.bytes, overview.totalBytes) + " of all node data"}>
                            <span className="scan-bar">
                              <span className="scan-bar-fill" style={{ width: barWidth(t.bytes, overview.types[0]?.bytes ?? 1), background: colorOf(t.id) }} />
                            </span>
                          </span>
                          <span className="num">{formatBytes(t.stats.median)}</span>
                          <span className="num">{formatBytes(t.stats.p90)}</span>
                          <span className="num">{formatBytes(t.stats.max)}</span>
                        </button>
                      ))}
                    </div>
                  </section>
                </div>
                <section className="ns-panel ns-position">
                  <div className="ns-panel-head">
                    <span className="ns-panel-title">Where in the log file</span>
                    <span className="muted">
                      {selectedType
                        ? `how much of each stretch of ${overview.logFileKey} (${formatBytes(overview.logFileSize)}) is the current bytes of ${selectedType.name} nodes`
                        : `how much of each stretch of ${overview.logFileKey} (${formatBytes(overview.logFileSize)}) is current node data; the rest is earlier versions, deleted nodes, relations and framing - what a truncation drops`}
                    </span>
                  </div>
                  <PositionStrip overview={overview} series={series} what={selectedType ? `${selectedType.name} nodes` : "current node data"} />
                </section>
                <div className="ns-row ns-row-bottom">
                  <section className="ns-panel ns-largest">
                    <div className="ns-panel-head">
                      <span className="ns-panel-title">Largest nodes</span>
                      <span className="muted">
                        {list
                          ? `${formatCount(list.matching)} node${list.matching === 1 ? "" : "s"}${selectedType ? " of " + selectedType.name : ""}${range ? ` of ${sizeLabel(range.min)} – ${sizeLabel(range.max)}` : ""}, ${formatBytes(list.matchingBytes)} together`
                          : ""}
                      </span>
                      <span className="header-spacer" />
                      {listLoading && <span className="muted">loading…</span>}
                      <CopyButton title="Copy the list" disabled={!list} table={() => nodeTable(list)} />
                    </div>
                    {listError && <div className="dialog-error">{listError}</div>}
                    <div className="ns-table ns-node-table" ref={listRef}>
                      <div className="ns-node-row ns-table-head">
                        <span className="num">#</span>
                        <span>Node</span>
                        <span>Type</span>
                        <span className="num">Size</span>
                        <span />
                        <span className="num" title="Where the node's bytes start in the log file">At</span>
                      </div>
                      {(list?.nodes ?? []).map((n, i) => (
                        <button
                          key={n.id}
                          className={"ns-node-row ns-row-button" + (n.id === nodeId ? " selected" : "")}
                          onClick={() => setNodeId((x) => (x === n.id ? null : n.id))}
                          title={`${n.name ?? n.nodeId}\n#${n.id} · ${n.nodeId}\n${formatCount(n.size)} bytes at byte ${formatCount(n.position)} of the log file`}
                        >
                          <span className="num muted">{i + 1}</span>
                          <span className="ns-ellipsis">{n.name ?? <span className="muted ns-guid">{n.nodeId}</span>}</span>
                          <span className="ns-type-name">
                            <span className="ns-swatch" style={{ background: colorOf(n.typeId) }} />
                            <span className="ns-ellipsis">{n.typeName}</span>
                          </span>
                          <span className="num">{formatBytes(n.size)}</span>
                          <span className="scan-bar">
                            <span className="scan-bar-fill" style={{ width: barWidth(n.size, list?.nodes[0]?.size ?? 1), background: colorOf(n.typeId) }} />
                          </span>
                          <span className="num muted">{formatBytes(n.position)}</span>
                        </button>
                      ))}
                      {list && list.nodes.length === 0 && <div className="muted ns-empty">No node matches.</div>}
                    </div>
                    {list && list.nodes.length < Math.min(list.matching, maxListed) && (
                      <div className="ns-more">
                        <button className="action-button" disabled={listLoading} onClick={() => setTake((t) => Math.min(maxListed, t + listStep))}>
                          Show {formatCount(Math.min(listStep, Math.min(list.matching, maxListed) - list.nodes.length))} more
                        </button>
                      </div>
                    )}
                  </section>
                  <section className="ns-panel ns-examine">
                    {examineError && <div className="dialog-error">{examineError}</div>}
                    {nodeId != null ? (
                      node ? (
                        <NodeView node={node} color={colorOf(node.typeId)} onOpen={() => openNode(node)} onOpenProperty={openProperty} onClose={() => setNodeId(null)} />
                      ) : (
                        !examineError && <Loading label="Reading the node…" />
                      )
                    ) : typeId ? (
                      typeView ? (
                        <TypeView view={typeView} color={colorOf(typeView.typeId)} onOpenProperty={openProperty} />
                      ) : (
                        !examineError && <Loading label="Reading a sample of the type's nodes…" />
                      )
                    ) : (
                      <div className="ns-examine-hint muted">
                        <IconSearch size={22} stroke={1.5} />
                        Pick a node in the list to take it apart property by property, or a type to see which of its properties its bytes are in.
                      </div>
                    )}
                  </section>
                </div>
              </>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

type GroupBy = "none" | "type";
type Measure = "nodes" | "bytes";
type TypeSort = "bytes" | "count" | "median" | "p90" | "max";

interface Series {
  key: string;
  label: string;
  color: string;
  /** per size bar, in the measure shown */
  sizes: number[];
  /** bytes per stretch of the log file */
  position: number[];
}

// the palette has eight steps; seven types are named and the eighth colour is the fold of the rest,
// in a grey that belongs to no type
const namedSeries = 7;
const otherColor = "var(--ns-other)";
const listStep = 100;
const maxListed = 1000;
const groupByKey = "nodeSizesGroupBy";
const measureKey = "nodeSizesMeasure";

function readStored(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}
function writeStored(key: string, value: string) {
  try {
    localStorage.setItem(key, value);
  } catch {
    // a private window: the choice lasts as long as the dialog
  }
}

function sumOf(arrays: number[][]): number[] {
  const length = arrays.reduce((n, a) => Math.max(n, a.length), 0);
  const out = new Array<number>(length).fill(0);
  for (const a of arrays) for (let i = 0; i < a.length; i++) out[i] += a[i];
  return out;
}

function percent(part: number, whole: number): string {
  if (whole <= 0) return "—";
  const p = (part / whole) * 100;
  // nearly all is not all: 99.8 stays 99.8 rather than rounding up to a whole that is not there
  if (p >= 99.5 && p < 100) return p.toFixed(1) + "%";
  return p >= 10 ? Math.round(p) + "%" : p >= 0.1 ? p.toFixed(1) + "%" : p > 0 ? "<0.1%" : "0%";
}

function barWidth(value: number, top: number): string {
  return Math.max(1, Math.round((value / Math.max(1, top)) * 100)) + "%";
}

/** A size as an axis says it: "512 B", "4 KB", "1.5 MB" - no ".0" on a whole number. */
export function sizeLabel(bytes: number): string {
  return formatBytes(bytes).replace(/\.0 /, " ");
}

/** A count on an axis: 950, 12K, 1.2M. */
function compactCount(n: number): string {
  if (n < 1000) return String(Math.round(n));
  if (n < 1_000_000) return (n / 1000).toFixed(n < 10_000 ? 1 : 0).replace(/\.0$/, "") + "K";
  return (n / 1_000_000).toFixed(n < 10_000_000 ? 1 : 0).replace(/\.0$/, "") + "M";
}

function Fact({ label, value, title }: { label: string; value: string; title?: string }) {
  return (
    <div className="fact" title={title}>
      <div className="fact-k">{label}</div>
      <div className="fact-v">{value}</div>
    </div>
  );
}

function SortHead({ label, sort, current, onSort }: { label: string; sort: TypeSort; current: TypeSort; onSort: (s: TypeSort) => void }) {
  return (
    <button className={"num ns-sort" + (current === sort ? " active" : "")} onClick={() => onSort(sort)} title={`Largest ${label.toLowerCase()} first`}>
      {label}
    </button>
  );
}

function Legend({ series, onPick }: { series: Series[]; onPick: (key: string) => void }) {
  return (
    <div className="ns-legend">
      {series.map((s) => (
        <button key={s.key} className="ns-legend-item" onClick={() => onPick(s.key)} title={s.key === "other" ? s.label : `Narrow to ${s.label}`} disabled={s.key === "other"}>
          <span className="ns-swatch" style={{ background: s.color }} />
          {s.label}
        </button>
      ))}
    </div>
  );
}

function typeTable(types: NodeSizeType[]) {
  return {
    header: ["Type", "Nodes", "Bytes", "Mean", "Median", "90th percentile", "99th percentile", "Largest"],
    rows: types.map((t) => [
      t.full,
      String(t.count),
      String(t.bytes),
      String(Math.round(t.stats.mean)),
      String(t.stats.median),
      String(t.stats.p90),
      String(t.stats.p99),
      String(t.stats.max),
    ]),
  };
}

function nodeTable(list: LargestList | null) {
  return {
    header: ["Id", "Guid", "Name", "Type", "Bytes", "Position"],
    rows: (list?.nodes ?? []).map((n) => [String(n.id), n.nodeId, n.name ?? "", n.typeName, String(n.size), String(n.position)]),
  };
}

// ---- the pictures ----

/** The width an element is given, kept current: the charts draw in real pixels so text is never stretched. */
function useWidth<T extends HTMLElement>(): [React.RefObject<T | null>, number] {
  const ref = useRef<T>(null);
  const [width, setWidth] = useState(0);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    setWidth(el.clientWidth);
    const observer = new ResizeObserver(() => setWidth(el.clientWidth));
    observer.observe(el);
    return () => observer.disconnect();
  }, []);
  return [ref, width];
}

/** Clean axis steps: 1, 2, 5 × a power of ten, about `count` of them up to the top. */
function ticks(top: number, count: number): number[] {
  if (top <= 0) return [0];
  const rough = top / count;
  const power = Math.pow(10, Math.floor(Math.log10(rough)));
  const step = [1, 2, 5, 10].map((m) => m * power).find((s) => s >= rough) ?? power * 10;
  const out: number[] = [];
  for (let t = 0; t <= top + step / 1000; t += step) out.push(t);
  if (out[out.length - 1] < top) out.push(out[out.length - 1] + step);
  return out;
}
const byteUnits = ["B", "KB", "MB", "GB", "TB"];
/**
 * A byte axis in one unit, the one the top of the axis is in: 0, 0.5 MB, 1 MB, 1.5 MB rather than
 * 500 KB, 1000 KB, 1.5 MB - steps of 1, 2 or 5 in that unit, so every label is a round number of it.
 */
function byteAxis(top: number, count: number): { ticks: number[]; label: (bytes: number) => string } {
  const k = top >= 1 ? Math.min(byteUnits.length - 1, Math.floor(Math.log(top) / Math.log(1024))) : 0;
  const unit = Math.pow(1024, k);
  return {
    ticks: ticks(top / unit, count).map((t) => t * unit),
    label: (bytes) => (bytes === 0 ? "0" : `${+(bytes / unit).toFixed(1)} ${byteUnits[k]}`),
  };
}

/** A column with its data-end rounded and its foot square, as a path. */
function topRounded(x: number, y: number, w: number, h: number, r: number): string {
  const rr = Math.max(0, Math.min(r, w / 2, h));
  return `M${x},${y + h}V${y + rr}Q${x},${y} ${x + rr},${y}H${x + w - rr}Q${x + w},${y} ${x + w},${y + rr}V${y + h}Z`;
}

interface Tip {
  x: number;
  y: number;
  title: string;
  lines: { color: string; label: string; value: string }[];
  foot?: string;
}

function Tooltip({ tip, width }: { tip: Tip | null; width: number }) {
  if (!tip) return null;
  // kept inside the chart: past the middle it opens to the left of the pointer
  const left = tip.x > width / 2 ? undefined : tip.x + 14;
  const right = tip.x > width / 2 ? width - tip.x + 14 : undefined;
  return (
    <div className="ns-tip" style={{ left, right, top: Math.max(0, tip.y - 10) }}>
      <div className="ns-tip-title">{tip.title}</div>
      {tip.lines.map((l, i) => (
        <div key={i} className="ns-tip-line">
          <span className="ns-swatch" style={{ background: l.color }} />
          <span className="ns-ellipsis">{l.label}</span>
          <span className="num">{l.value}</span>
        </div>
      ))}
      {tip.foot && <div className="ns-tip-foot">{tip.foot}</div>}
    </div>
  );
}

const histogramHeight = 230;

function SizeHistogram({
  edges,
  series,
  measure,
  selected,
  onSelect,
}: {
  edges: number[];
  series: Series[];
  measure: Measure;
  selected: number | null;
  onSelect: (bucket: number) => void;
}) {
  const [ref, width] = useWidth<HTMLDivElement>();
  const [hover, setHover] = useState<{ bucket: number; x: number; y: number } | null>(null);
  const buckets = edges.length - 1;
  const totals = useMemo(() => {
    const out = new Array<number>(buckets).fill(0);
    for (const s of series) for (let i = 0; i < buckets; i++) out[i] += s.sizes[i] ?? 0;
    return out;
  }, [series, buckets]);
  const peak = Math.max(...totals, 1);
  const { ticks: axis, label: format } = measure === "nodes" ? { ticks: ticks(peak, 4), label: compactCount } : byteAxis(peak, 4);
  const top = axis[axis.length - 1] || 1;
  const pad = { left: 52, right: 8, top: 8, bottom: 26 };
  const plotW = Math.max(10, width - pad.left - pad.right);
  const plotH = histogramHeight - pad.top - pad.bottom;
  const colW = plotW / buckets;
  const gap = Math.min(2, colW * 0.25);
  const yOf = (v: number) => pad.top + plotH - (v / top) * plotH;
  // every edge is labelled where there is room, else every second, fourth...: the steps per doubling
  // are a power of two, so a thinned out axis still lands on the doublings
  let every = 1;
  while (colW * every < 46) every *= 2;

  function tipFor(b: number, x: number, y: number): Tip {
    const nodes = measure === "nodes";
    const lines = series
      .map((s) => ({ color: s.color, label: s.label, raw: s.sizes[b] ?? 0 }))
      .filter((l) => l.raw > 0)
      .map((l) => ({ color: l.color, label: l.label, value: nodes ? formatCount(l.raw) : formatBytes(l.raw) }));
    const total = totals[b];
    return {
      x,
      y,
      title: `${sizeLabel(edges[b])} – ${sizeLabel(edges[b + 1])}`,
      lines: series.length > 1 ? lines : [],
      foot: (nodes ? `${formatCount(total)} node${total === 1 ? "" : "s"}` : `${formatBytes(total)} of node data`) + (total > 0 ? " · click to list them" : ""),
    };
  }

  return (
    <div className="ns-chart" ref={ref} onMouseLeave={() => setHover(null)}>
      {width > 0 && (
        <svg width={width} height={histogramHeight} role="img" aria-label="Node size distribution">
          {axis.map((t) => (
            <g key={t}>
              <line className="ns-grid" x1={pad.left} x2={pad.left + plotW} y1={yOf(t)} y2={yOf(t)} />
              <text className="ns-axis" x={pad.left - 6} y={yOf(t)} dy="0.32em" textAnchor="end">
                {format(t)}
              </text>
            </g>
          ))}
          {selected != null && (
            <rect className="ns-selected-band" x={pad.left + selected * colW} y={pad.top} width={colW} height={plotH} />
          )}
          {totals.map((total, b) => {
            if (total <= 0) return null;
            const x = pad.left + b * colW + gap / 2;
            const w = Math.max(1, colW - gap);
            const dim = selected != null && selected !== b;
            let base = 0;
            const visible = series.filter((s) => (s.sizes[b] ?? 0) > 0);
            return (
              <g key={b} className={dim ? "ns-dim" : undefined}>
                {visible.map((s, i) => {
                  const v = s.sizes[b] ?? 0;
                  const y0 = yOf(base);
                  base += v;
                  const y1 = yOf(base);
                  // the gap between stacked segments is surface, taken off the top of the lower one
                  const h = Math.max(0, y0 - y1 - (i > 0 ? gap : 0));
                  const last = i === visible.length - 1;
                  return last ? (
                    <path key={s.key} d={topRounded(x, y1, w, Math.max(h, 1), 4)} fill={s.color} />
                  ) : (
                    <rect key={s.key} x={x} y={y1} width={w} height={Math.max(h, 0.5)} fill={s.color} />
                  );
                })}
              </g>
            );
          })}
          <line className="ns-baseline" x1={pad.left} x2={pad.left + plotW} y1={pad.top + plotH} y2={pad.top + plotH} />
          {edges.map((e, i) =>
            i % every === 0 ? (
              <text key={i} className="ns-axis" x={pad.left + i * colW} y={histogramHeight - 8} textAnchor={i === 0 ? "start" : i === buckets ? "end" : "middle"}>
                {sizeLabel(e)}
              </text>
            ) : null,
          )}
          {/* the hit targets: the whole height of every column, so a short bar is as easy to point at as a tall one */}
          {totals.map((total, b) => (
            <rect
              key={b}
              className="ns-hit"
              x={pad.left + b * colW}
              y={pad.top}
              width={colW}
              height={plotH}
              style={{ cursor: total > 0 ? "pointer" : "default" }}
              onMouseMove={(e) => {
                const box = ref.current!.getBoundingClientRect();
                setHover({ bucket: b, x: e.clientX - box.left, y: e.clientY - box.top });
              }}
              onClick={() => total > 0 && onSelect(b)}
            />
          ))}
        </svg>
      )}
      <Tooltip tip={hover ? tipFor(hover.bucket, hover.x, hover.y) : null} width={width} />
    </div>
  );
}

const stripHeight = 120;

/**
 * The log file from its first byte to its last, cut into stretches; each column is how much of its
 * stretch is the current bytes of nodes. A log that has only ever been appended to fills up towards
 * the end and thins out towards the start, where what nodes once were has since been written again.
 */
function PositionStrip({ overview, series, what }: { overview: NodeSizesOverview; series: Series[]; what: string }) {
  const [ref, width] = useWidth<HTMLDivElement>();
  const [hover, setHover] = useState<{ bin: number; x: number; y: number } | null>(null);
  const { bins, binSize, logFileSize } = overview;
  // the last stretch ends with the file, so it is measured against its own length
  const lengthOf = (b: number) => Math.max(1, Math.min(binSize, logFileSize - b * binSize));
  const used = Math.min(bins, Math.ceil(logFileSize / binSize));
  const pad = { left: 52, right: 8, top: 8, bottom: 22 };
  const plotW = Math.max(10, width - pad.left - pad.right);
  const plotH = stripHeight - pad.top - pad.bottom;
  const colW = plotW / Math.max(1, used);
  const gap = Math.min(2, colW * 0.25);
  const yOf = (share: number) => pad.top + plotH - share * plotH;
  const marks = [0, 0.25, 0.5, 0.75, 1];

  function tipFor(b: number, x: number, y: number): Tip {
    const length = lengthOf(b);
    const total = series.reduce((n, s) => n + (s.position[b] ?? 0), 0);
    return {
      x,
      y,
      title: `${formatBytes(b * binSize)} – ${formatBytes(b * binSize + length)} of the file`,
      lines:
        series.length > 1
          ? series.filter((s) => (s.position[b] ?? 0) > 0).map((s) => ({ color: s.color, label: s.label, value: formatBytes(s.position[b]) }))
          : [],
      foot: `${percent(total, length)} ${what} (${formatBytes(total)})`,
    };
  }

  return (
    <div className="ns-chart" ref={ref} onMouseLeave={() => setHover(null)}>
      {width > 0 && (
        <svg width={width} height={stripHeight} role="img" aria-label="Current node data along the log file">
          {[0, 0.5, 1].map((t) => (
            <g key={t}>
              <line className="ns-grid" x1={pad.left} x2={pad.left + plotW} y1={yOf(t)} y2={yOf(t)} />
              <text className="ns-axis" x={pad.left - 6} y={yOf(t)} dy="0.32em" textAnchor="end">
                {Math.round(t * 100)}%
              </text>
            </g>
          ))}
          {Array.from({ length: used }, (_, b) => {
            const length = lengthOf(b);
            const x = pad.left + b * colW + gap / 2;
            const w = Math.max(0.5, colW - gap);
            let base = 0;
            const visible = series.filter((s) => (s.position[b] ?? 0) > 0);
            return (
              <g key={b}>
                {visible.map((s, i) => {
                  const share = Math.min(1, (s.position[b] ?? 0) / length);
                  const y0 = yOf(base);
                  base = Math.min(1, base + share);
                  const y1 = yOf(base);
                  const h = Math.max(0.5, y0 - y1 - (i > 0 ? gap : 0));
                  return i === visible.length - 1 ? (
                    <path key={s.key} d={topRounded(x, y1, w, h, 2)} fill={s.color} />
                  ) : (
                    <rect key={s.key} x={x} y={y1} width={w} height={h} fill={s.color} />
                  );
                })}
              </g>
            );
          })}
          <line className="ns-baseline" x1={pad.left} x2={pad.left + plotW} y1={pad.top + plotH} y2={pad.top + plotH} />
          {marks.map((m) => (
            <text key={m} className="ns-axis" x={pad.left + m * plotW} y={stripHeight - 6} textAnchor={m === 0 ? "start" : m === 1 ? "end" : "middle"}>
              {m === 0 ? "start" : m === 1 ? formatBytes(logFileSize) : formatBytes(logFileSize * m)}
            </text>
          ))}
          <rect
            className="ns-hit"
            x={pad.left}
            y={pad.top}
            width={plotW}
            height={plotH}
            onMouseMove={(e) => {
              const box = ref.current!.getBoundingClientRect();
              const bin = Math.min(used - 1, Math.max(0, Math.floor((e.clientX - box.left - pad.left) / colW)));
              setHover({ bin, x: e.clientX - box.left, y: e.clientY - box.top });
            }}
          />
          {hover && <rect className="ns-hover-band" x={pad.left + hover.bin * colW} y={pad.top} width={colW} height={plotH} />}
        </svg>
      )}
      <Tooltip tip={hover ? tipFor(hover.bin, hover.x, hover.y) : null} width={width} />
    </div>
  );
}

// ---- taking a node, or a type, apart ----

function NodeView({
  node,
  color,
  onOpen,
  onOpenProperty,
  onClose,
}: {
  node: NodeAnatomyView;
  color: string;
  onOpen: () => void;
  onOpenProperty: (part: AnatomyPart) => void;
  onClose: () => void;
}) {
  return (
    <div className="ns-examine-body">
      <div className="ns-panel-head">
        <span className="ns-swatch" style={{ background: color }} />
        <span className="ns-panel-title ns-ellipsis" title={node.name ?? node.nodeId}>
          {node.name ?? node.nodeId}
        </span>
        <span className="header-spacer" />
        <button className="action-button" onClick={onOpen} title="Open the node in Query">
          <IconExternalLink size={14} stroke={1.8} /> Open in Query
        </button>
        <button className="icon-button" onClick={onClose} title="Back" aria-label="Back">
          <IconX size={15} stroke={2} />
        </button>
      </div>
      <div className="ns-examine-meta muted">
        <span>{node.typeName}</span>
        <span>#{node.id}</span>
        <span className="ns-guid">
          {node.nodeId} <CopyText text={node.nodeId} title="Copy the guid" small />
        </span>
        <span>
          {formatBytes(node.size)} ({formatCount(node.size)} bytes) at {formatBytes(node.position)}
        </span>
        {node.revisions > 0 && <span>{formatCount(node.revisions)} revisions stored together</span>}
        {node.version !== "NodeData" && node.version !== "RevisionContainer" && <span>stored in the old {node.version} format</span>}
      </div>
      <Parts parts={node.parts} total={node.size} perNode={1} onOpenProperty={onOpenProperty} />
    </div>
  );
}

function TypeView({ view, color, onOpenProperty }: { view: TypeAnatomyView; color: string; onOpenProperty: (part: AnatomyPart) => void }) {
  const all = view.sampled === view.nodes;
  return (
    <div className="ns-examine-body">
      <div className="ns-panel-head">
        <span className="ns-swatch" style={{ background: color }} />
        <span className="ns-panel-title">Where the bytes of {view.typeName} go</span>
      </div>
      <div className="ns-examine-meta muted">
        <span>
          {all
            ? `all ${formatCount(view.nodes)} nodes read`
            : `a sample of ${formatCount(view.sampled)} of ${formatCount(view.nodes)} nodes read, spread over the type`}
          , {formatBytes(view.sampledBytes)}
        </span>
        <span>sizes are the mean per node; pick a node in the list for one node's own</span>
      </div>
      {view.sampled === 0 ? (
        <div className="muted ns-empty">No node of this type could be read.</div>
      ) : (
        <Parts parts={view.parts} total={view.sampledBytes} perNode={view.sampled} onOpenProperty={onOpenProperty} />
      )}
    </div>
  );
}

/**
 * The parts of a node, largest first, each with its share of the whole as a bar. For a type the
 * bytes are the sample's sum, shown as the mean per node, with how many of the nodes have the part.
 */
function Parts({ parts, total, perNode, onOpenProperty }: { parts: AnatomyPart[]; total: number; perNode: number; onOpenProperty: (part: AnatomyPart) => void }) {
  const top = parts[0]?.bytes ?? 1;
  const isType = perNode > 1;
  return (
    <div className="ns-table ns-part-table">
      <div className="ns-part-row ns-table-head">
        <span>Part</span>
        <span className="num">{isType ? "Mean" : "Bytes"}</span>
        <span className="num">Share</span>
        <span />
        <span>{isType ? "In" : "Value"}</span>
      </div>
      {parts.map((p) => (
        <div key={p.kind + p.propertyId} className={"ns-part-row" + (p.kind === "Property" ? "" : " ns-part-system")}>
          <span className="ns-part-name">
            {p.kind === "Property" && p.declaringTypeId ? (
              <button className="ns-part-link ns-ellipsis" onClick={() => onOpenProperty(p)} title="Open the property in the data model">
                {p.label}
              </button>
            ) : (
              <span className="ns-ellipsis">{p.label}</span>
            )}
            {p.propertyType && <span className="badge">{p.propertyType}</span>}
            {p.orphan && (
              <span className="badge ns-orphan" title="Still stored in the node and read past on every load; it goes the next time the node is saved">
                {p.orphan}
              </span>
            )}
          </span>
          <span className="num" title={`${formatCount(Math.round(p.bytes / perNode))} bytes${p.valueBytes !== p.bytes ? `, ${formatCount(Math.round(p.valueBytes / perNode))} of them the value` : ""}`}>
            {formatBytes(p.bytes / perNode)}
          </span>
          <span className="num">{percent(p.bytes, total)}</span>
          <span className="scan-bar">
            <span className="scan-bar-fill" style={{ width: barWidth(p.bytes, top) }} />
          </span>
          <span className="ns-ellipsis muted" title={isType ? undefined : (p.preview ?? undefined)}>
            {isType
              ? `${percent(p.occurrences, perNode)} of nodes`
              : p.kind === "Property"
                ? (p.preview ?? "")
                : p.occurrences > 1
                  ? `${formatCount(p.occurrences)} revisions`
                  : ""}
          </span>
        </div>
      ))}
    </div>
  );
}
