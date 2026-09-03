import { useEffect, useMemo, useState } from "react";
import { IconArrowNarrowDown, IconArrowNarrowUp, IconChevronLeft, IconChevronRight, IconDownload, IconX } from "@tabler/icons-react";
import {
  fetchPivotModel,
  runGroupBy,
  type FacetSelection,
  type GroupByRequest,
  type GroupByResult,
  type GroupByRow,
  type PivotFunction,
  type PivotLevelSpec,
  type PivotMeasureSpec,
  type PivotModel,
  type PivotProperty,
} from "../server/query";
import { useLiveResult } from "../server/hooks";
import { formatCount } from "../format";
import { dateModes, functions, propertiesFor, type PivotBase } from "./PivotView";

const pageSize = 200;

/** How a key property can be bucketed: one group per value always; dates by calendar too; numbers and dates by range. */
function keyModesOf(property: PivotProperty | undefined): { value: string; label: string }[] {
  const values = { value: "values", label: "values" };
  if (!property) return [values];
  if (property.isDate) return [values, ...dateModes.map((m) => ({ value: m, label: "by " + m })), { value: "ranges", label: "ranges" }];
  if (property.numeric || property.type === "TimeSpan") return [values, { value: "ranges", label: "ranges" }];
  return [values];
}

/**
 * The group-by view of the query page: the current search summarized as one row per group - SQL's
 * GROUP BY with a count and aggregates per row, the flat cousin of the pivot. The builder is the
 * whole definition: what to group by (one or more properties, each by value, calendar interval or
 * range) and what to compute; every change runs at once. Column headers sort by count or by a
 * measure; a click on a row hands its groups back to the page as a facet selection.
 */
export function GroupByView({ base, showQuery, onDrill }: { base: PivotBase; showQuery: boolean; onDrill: (selections: FacetSelection[]) => void }) {
  const [model, setModel] = useState<PivotModel | null>(null);
  const [modelError, setModelError] = useState<string | null>(null);
  const [keys, setKeys] = useState<PivotLevelSpec[]>([]);
  const [measures, setMeasures] = useState<PivotMeasureSpec[]>([]);
  const [includeMissing, setIncludeMissing] = useState(true);
  const [sort, setSort] = useState<{ by: string; descending: boolean } | null>(null);
  const [page, setPage] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setModel(null);
    setModelError(null);
    fetchPivotModel(base.storeId, base.typeId)
      .then((m) => {
        if (cancelled) return;
        setModel(m);
        // a first key, so the view opens with rows rather than an empty table
        const first = m.properties.find((p) => p.groupable && !p.isDate) ?? m.properties.find((p) => p.groupable);
        if (first) setKeys([{ propertyId: first.id, mode: keyModesOf(first)[0].value }]);
      })
      .catch((e) => !cancelled && setModelError(e instanceof Error ? e.message : String(e)));
    return () => {
      cancelled = true;
    };
  }, [base.storeId, base.typeId]);

  const request = useMemo<GroupByRequest | null>(
    () =>
      model === null
        ? null
        : {
            storeId: base.storeId,
            typeId: base.typeId,
            text: base.text,
            semanticRatio: base.semanticRatio,
            minimumSimilarity: base.minimumSimilarity,
            selections: base.selections,
            keys: keys.filter((k) => k.propertyId !== ""),
            measures: measures.filter((m) => m.propertyId !== null),
            includeMissing,
            sortBy: sort?.by ?? null,
            descending: sort?.descending ?? true,
            page,
            pageSize,
          },
    [model, base, keys, measures, includeMissing, sort, page],
  );
  const { result, loading, error } = useLiveResult(request, runGroupBy);

  // any change to the definition starts over at the first page
  function edit(apply: () => void) {
    setPage(0);
    apply();
  }
  // a header clicked: sort by it, largest first; again flips the direction; a third click is back to the natural order
  function sortBy(name: string) {
    edit(() => {
      if (sort?.by !== name) setSort({ by: name, descending: true });
      else if (sort.descending) setSort({ by: name, descending: false });
      else setSort(null);
    });
  }
  function drill(row: GroupByRow) {
    if (!result) return;
    const selections: FacetSelection[] = result.keys.map((k, i) => ({ propertyId: k.propertyId, values: [{ value: row.values[i], value2: row.values2[i] }] }));
    onDrill(selections);
  }

  if (modelError) return <div className="query-error">{modelError}</div>;
  if (!model) return null;
  const groupable = model.properties.filter((p) => p.groupable);
  const lastPage = result ? Math.max(0, Math.ceil(result.totalRows / pageSize) - 1) : 0;
  const sortIcon = (name: string) =>
    sort?.by === name ? (sort.descending ? <IconArrowNarrowDown size={14} stroke={2} /> : <IconArrowNarrowUp size={14} stroke={2} />) : null;
  return (
    <div className="pivot">
      <div className="pivot-builder">
        <div className="pivot-builder-row">
          <span className="pivot-builder-label">Group by</span>
          {keys.map((key, i) => {
            const property = groupable.find((p) => p.id === key.propertyId);
            const modes = keyModesOf(property);
            return (
              <span className="pivot-chip" key={i}>
                <select
                  className="select"
                  value={key.propertyId}
                  title={property ? property.type + (property.declaredBy ? " · from " + property.declaredBy : "") : "Group by this property"}
                  onChange={(e) => {
                    const next = groupable.find((p) => p.id === e.target.value);
                    edit(() => setKeys(keys.map((x, j) => (j === i ? { propertyId: e.target.value, mode: keyModesOf(next)[0].value } : x))));
                  }}
                >
                  {!property && <option value="">choose…</option>}
                  {groupable.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.name}
                    </option>
                  ))}
                </select>
                {modes.length > 1 && (
                  <select
                    className="select"
                    value={key.mode}
                    title="How the values are grouped: one group per value, a calendar interval, or ranges"
                    onChange={(e) => edit(() => setKeys(keys.map((x, j) => (j === i ? { ...x, mode: e.target.value } : x))))}
                  >
                    {modes.map((m) => (
                      <option key={m.value} value={m.value}>
                        {m.label}
                      </option>
                    ))}
                  </select>
                )}
                <button className="icon-button" title="Remove this key" onClick={() => edit(() => setKeys(keys.filter((_, j) => j !== i)))}>
                  <IconX size={13} stroke={1.8} />
                </button>
              </span>
            );
          })}
          <button className="link-button" disabled={groupable.length === 0} onClick={() => edit(() => setKeys([...keys, { propertyId: groupable[0]?.id ?? "", mode: keyModesOf(groupable[0])[0].value }]))}>
            + key
          </button>
          <div className="pivot-options">
            <label className="settings-check" title="A (none) row for the nodes without a value for a key - the way SQL groups nulls">
              <input type="checkbox" checked={includeMissing} onChange={(e) => edit(() => setIncludeMissing(e.target.checked))} />
              missing
            </label>
          </div>
        </div>
        <div className="pivot-builder-row">
          <span className="pivot-builder-label">Aggregates</span>
          <span className="pivot-chip" title="Every row has the number of nodes in its group">
            <span className="muted">Count</span>
          </span>
          {measures.map((m, i) => {
            const candidates = propertiesFor(m.function, model.properties);
            return (
              <span className="pivot-chip" key={i}>
                <select
                  className="select"
                  value={m.function}
                  title="What to compute per group"
                  onChange={(e) => {
                    const fn = e.target.value as PivotFunction;
                    const ok = propertiesFor(fn, model.properties);
                    edit(() => setMeasures(measures.map((x, j) => (j === i ? { function: fn, propertyId: ok.some((p) => p.id === x.propertyId) ? x.propertyId : (ok[0]?.id ?? null) } : x))));
                  }}
                >
                  {functions
                    .filter((f) => f.value !== "Count")
                    .map((f) => (
                      <option key={f.value} value={f.value}>
                        {f.label}
                      </option>
                    ))}
                </select>
                <select
                  className="select"
                  value={m.propertyId ?? ""}
                  title={m.function === "CountDistinct" ? "Distinct values of this property" : "A numeric property"}
                  onChange={(e) => edit(() => setMeasures(measures.map((x, j) => (j === i ? { ...x, propertyId: e.target.value || null } : x))))}
                >
                  {candidates.length === 0 && <option value="">(no property fits)</option>}
                  {m.propertyId === null && candidates.length > 0 && <option value="">choose…</option>}
                  {candidates.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.name}
                    </option>
                  ))}
                </select>
                <button className="icon-button" title="Remove this aggregate" onClick={() => edit(() => setMeasures(measures.filter((_, j) => j !== i)))}>
                  <IconX size={13} stroke={1.8} />
                </button>
              </span>
            );
          })}
          <button
            className="link-button"
            onClick={() => {
              const numeric = model.properties.find((p) => p.numeric);
              edit(() => setMeasures([...measures, { function: numeric ? "Sum" : "CountDistinct", propertyId: numeric?.id ?? model.properties.find((p) => p.aggregatable)?.id ?? null }]));
            }}
          >
            + aggregate
          </button>
        </div>
      </div>

      {showQuery && result && result.query && <div className="query-string">{result.query}</div>}
      {error && <div className="query-error">{error}</div>}

      {result && (
        <div className="pivot-head">
          <span>
            <strong>{formatCount(result.sourceCount)}</strong> nodes · {formatCount(result.totalRows)} {result.totalRows === 1 ? "group" : "groups"} · {result.durationMs.toFixed(1)} ms
          </span>
          <div className="query-spacer" />
          <button className="icon-button" title="Download these rows as csv" disabled={result.rows.length === 0} onClick={() => downloadCsv(result)}>
            <IconDownload size={16} stroke={1.8} />
          </button>
          {result.totalRows > pageSize && (
            <div className="query-paging">
              <button className="icon-button" disabled={page === 0} title="Previous groups" onClick={() => setPage(page - 1)}>
                <IconChevronLeft size={15} stroke={1.8} />
              </button>
              <span className="muted">
                {page * pageSize + 1}–{Math.min((page + 1) * pageSize, result.totalRows)}
              </span>
              <button className="icon-button" disabled={page >= lastPage} title="Next groups" onClick={() => setPage(page + 1)}>
                <IconChevronRight size={15} stroke={1.8} />
              </button>
            </div>
          )}
        </div>
      )}
      <div className={"query-table-wrap" + (loading ? " loading" : "")}>
        {result && result.keys.length > 0 && (
          <table className="query-table pivot-table">
            <thead>
              <tr>
                {result.keys.map((k) => (
                  <th key={k.propertyId + k.interval} title={k.valueType + (k.interval !== "None" ? " by " + k.interval.toLowerCase() : k.isRange ? " ranges" : "")}>
                    {k.codeName}
                  </th>
                ))}
                <th className={"pivot-group sortable" + (sort?.by === "Count" ? " sorted" : "")} title="Nodes in the group — click to sort" onClick={() => sortBy("Count")}>
                  Count {sortIcon("Count")}
                </th>
                {result.measures.map((m) => (
                  <th
                    key={m.name}
                    className={"pivot-group sortable" + (sort?.by === m.name ? " sorted" : "")}
                    title={m.function + (m.propertyName ? " of " + m.propertyName : "") + " — click to sort"}
                    onClick={() => sortBy(m.name)}
                  >
                    {m.name} {sortIcon(m.name)}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {result.rows.map((row, r) => (
                <tr key={r} onClick={() => drill(row)} title={formatCount(row.count) + " nodes — show them"}>
                  {row.labels.map((label, i) => (
                    <td key={i} className={"pivot-label pivot-cell" + (row.isMissing && row.values[i] === null ? " pivot-other" : "")}>
                      {label}
                    </td>
                  ))}
                  <td className="pivot-num pivot-cell">{formatCount(row.count)}</td>
                  {result.measures.map((m, i) => (
                    <td key={m.name} className="pivot-num pivot-cell">
                      {formatValue(row.measures[i], m.function)}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}

function formatValue(value: number | null, fn: PivotFunction): string {
  if (value === null) return "–";
  if (fn === "Count" || fn === "CountDistinct" || Number.isInteger(value)) return formatCount(value);
  return value.toLocaleString(undefined, { maximumFractionDigits: 2 });
}

/** The page as csv: the key labels, the count and one column per aggregate. */
function downloadCsv(result: GroupByResult) {
  const header = [...result.keys.map((k) => k.codeName), "Count", ...result.measures.map((m) => m.name)];
  const lines = [header, ...result.rows.map((row) => [...row.labels, String(row.count), ...row.measures.map((v) => (v === null ? "" : String(v)))])];
  const quote = (s: string) => '"' + s.replace(/"/g, '""') + '"';
  const csv = "﻿" + lines.map((line) => line.map(quote).join(",")).join("\r\n");
  const url = URL.createObjectURL(new Blob([csv], { type: "text/csv;charset=utf-8" }));
  try {
    const link = document.createElement("a");
    link.href = url;
    link.download = result.typeName + "-groups.csv";
    link.click();
  } finally {
    URL.revokeObjectURL(url);
  }
}
