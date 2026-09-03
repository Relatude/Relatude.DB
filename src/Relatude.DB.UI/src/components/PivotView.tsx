import { useEffect, useMemo, useState } from "react";
import { IconArrowNarrowDown, IconArrowNarrowUp, IconChevronLeft, IconChevronRight, IconDownload, IconX } from "@tabler/icons-react";
import {
  fetchPivotModel,
  runPivot,
  type FacetSelection,
  type PivotAxis,
  type PivotAxisOptions,
  type PivotCell,
  type PivotFunction,
  type PivotGroup,
  type PivotLevelSpec,
  type PivotMeasureSpec,
  type PivotModel,
  type PivotProperty,
  type PivotRequest,
  type PivotResult,
} from "../server/query";
import { useLiveResult } from "../server/hooks";
import { formatCount } from "../format";

/** What the pivot summarizes: the search as the rest of the page has it. */
export interface PivotBase {
  storeId: string;
  typeId: string;
  text: string;
  semanticRatio: number | null;
  minimumSimilarity: number | null;
  selections: FacetSelection[];
}

const rowPageSize = 200;
export const functions: { value: PivotFunction; label: string }[] = [
  { value: "Count", label: "Count" },
  { value: "CountDistinct", label: "Distinct" },
  { value: "Sum", label: "Sum" },
  { value: "Average", label: "Average" },
  { value: "Min", label: "Min" },
  { value: "Max", label: "Max" },
];
export const dateModes = ["year", "quarter", "month", "week", "day", "hour"];
const noOptions: PivotAxisOptions = { maxGroups: 0, sortByMeasure: null, descending: true, otherGroup: false, includeMissing: false };

/** The modes a property can be bucketed by: dates by calendar, numbers by range, everything else by value. */
function modesOf(property: PivotProperty | undefined): { value: string; label: string }[] {
  if (!property) return [{ value: "auto", label: "auto" }];
  if (property.isDate) {
    return [
      { value: "auto", label: "auto" },
      { value: "values", label: "values" },
      { value: "ranges", label: "ranges" },
      ...dateModes.map((m) => ({ value: m, label: "by " + m })),
    ];
  }
  if (property.numeric || property.type === "TimeSpan") {
    return [
      { value: "auto", label: "auto" },
      { value: "values", label: "values" },
      { value: "ranges", label: "ranges" },
    ];
  }
  return [{ value: "values", label: "values" }];
}

/** Which properties a measure function accepts. */
export function propertiesFor(fn: PivotFunction, properties: PivotProperty[]): PivotProperty[] {
  if (fn === "Count") return [];
  if (fn === "CountDistinct") return properties.filter((p) => p.aggregatable);
  return properties.filter((p) => p.numeric);
}

/**
 * The names the server gives the measures, computed the same way here so the sort option can name
 * one before the result is back: "Count", or "<Property>.<Function>", numbered when repeated.
 */
function measureNames(measures: PivotMeasureSpec[], properties: PivotProperty[]): string[] {
  const taken = new Set<string>();
  return measures.map((m) => {
    const property = properties.find((p) => p.id === m.propertyId);
    const base = m.function === "Count" ? "Count" : property ? property.name + "." + m.function : "";
    if (!base) return "";
    let name = base;
    for (let i = 2; taken.has(name.toLowerCase()); i++) name = `${base} (${i})`;
    taken.add(name.toLowerCase());
    return name;
  });
}

/**
 * The pivot view of the query page: the current search summarized as a table of groups and measures,
 * like a spreadsheet pivot table. The builder above the table is the whole definition - what to
 * group the rows and columns by, and what to compute per cell - and every change runs at once, the
 * way the rest of the page does. A click on a cell hands the groups behind it back to the page as a
 * facet selection, which is how a number becomes the nodes it counts.
 */
export function PivotView({ base, showQuery, onDrill }: { base: PivotBase; showQuery: boolean; onDrill: (selections: FacetSelection[]) => void }) {
  const [model, setModel] = useState<PivotModel | null>(null);
  const [modelError, setModelError] = useState<string | null>(null);
  const [rows, setRows] = useState<PivotLevelSpec[]>([]);
  const [columns, setColumns] = useState<PivotLevelSpec[]>([]);
  const [measures, setMeasures] = useState<PivotMeasureSpec[]>([{ function: "Count", propertyId: null }]);
  const [rowOptions, setRowOptions] = useState<PivotAxisOptions>(noOptions);
  const [columnOptions, setColumnOptions] = useState<PivotAxisOptions>(noOptions);
  const [subTotals, setSubTotals] = useState(false);
  const [rowPage, setRowPage] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setModel(null);
    setModelError(null);
    fetchPivotModel(base.storeId, base.typeId)
      .then((m) => {
        if (cancelled) return;
        setModel(m);
        // a first row grouping, so the view opens as a table rather than a single number
        const first = m.properties.find((p) => p.groupable && !p.isDate) ?? m.properties.find((p) => p.groupable);
        if (first) setRows([{ propertyId: first.id, mode: modesOf(first)[0].value }]);
      })
      .catch((e) => !cancelled && setModelError(e instanceof Error ? e.message : String(e)));
    return () => {
      cancelled = true;
    };
  }, [base.storeId, base.typeId]);

  const names = useMemo(() => (model ? measureNames(measures, model.properties) : []), [measures, model]);

  const request = useMemo<PivotRequest | null>(
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
            rows,
            columns,
            measures: measures.filter((m) => m.function === "Count" || m.propertyId !== null),
            rowOptions,
            columnOptions,
            subTotals,
            rowPage,
            rowPageSize,
          },
    [model, base, rows, columns, measures, rowOptions, columnOptions, subTotals, rowPage],
  );
  const { result, loading, error } = useLiveResult(request, runPivot);

  // any change to the definition starts the row axis over at its first page
  function edit(apply: () => void) {
    setRowPage(0);
    apply();
  }

  if (modelError) return <div className="query-error">{modelError}</div>;
  if (!model) return null;
  const groupable = model.properties.filter((p) => p.groupable);
  const lastPage = result ? Math.max(0, Math.ceil(result.rows.totalGroupCount / rowPageSize) - 1) : 0;
  return (
    <div className="pivot">
      <div className="pivot-builder">
        <AxisEditor
          title="Rows"
          levels={rows}
          properties={groupable}
          options={rowOptions}
          measureNames={names}
          onChange={(levels) => edit(() => setRows(levels))}
          onOptions={(o) => edit(() => setRowOptions(o))}
        />
        <AxisEditor
          title="Columns"
          levels={columns}
          properties={groupable}
          options={columnOptions}
          measureNames={names}
          onChange={(levels) => edit(() => setColumns(levels))}
          onOptions={(o) => edit(() => setColumnOptions(o))}
        />
        <div className="pivot-builder-row">
          <span className="pivot-builder-label">Measures</span>
          {measures.map((m, i) => {
            const candidates = propertiesFor(m.function, model.properties);
            return (
              <span className="pivot-chip" key={i}>
                <select
                  className="select"
                  value={m.function}
                  title="What to compute per cell"
                  onChange={(e) => {
                    const fn = e.target.value as PivotFunction;
                    const ok = propertiesFor(fn, model.properties);
                    edit(() =>
                      setMeasures(measures.map((x, j) => (j === i ? { function: fn, propertyId: fn === "Count" ? null : ok.some((p) => p.id === x.propertyId) ? x.propertyId : (ok[0]?.id ?? null) } : x))),
                    );
                  }}
                >
                  {functions.map((f) => (
                    <option key={f.value} value={f.value}>
                      {f.label}
                    </option>
                  ))}
                </select>
                {m.function !== "Count" && (
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
                )}
                <button className="icon-button" title="Remove this measure" onClick={() => edit(() => setMeasures(measures.filter((_, j) => j !== i)))}>
                  <IconX size={13} stroke={1.8} />
                </button>
              </span>
            );
          })}
          <button className="link-button" onClick={() => edit(() => setMeasures([...measures, { function: "Count", propertyId: null }]))}>
            + measure
          </button>
          <div className="pivot-options">
            <label className="settings-check" title="A total for every group above the leaf level, on an axis with several levels">
              <input type="checkbox" checked={subTotals} onChange={(e) => edit(() => setSubTotals(e.target.checked))} />
              sub-totals
            </label>
          </div>
        </div>
      </div>

      {showQuery && result && <div className="query-string">{result.query}</div>}
      {error && <div className="query-error">{error}</div>}

      {result && (
        <div className="pivot-head">
          <span>
            <strong>{formatCount(result.sourceCount)}</strong> nodes · {formatCount(result.rows.totalGroupCount)} {result.rows.totalGroupCount === 1 ? "row" : "rows"} ×{" "}
            {formatCount(result.columns.groups.length)} {result.columns.groups.length === 1 ? "column" : "columns"} · {result.durationMs.toFixed(1)} ms
          </span>
          {result.capped && <span className="query-filters">cut short: too many cells — group by fewer values</span>}
          <div className="query-spacer" />
          <button className="icon-button" title="Download this table as csv" disabled={result.cells.length === 0} onClick={() => downloadCsv(result)}>
            <IconDownload size={16} stroke={1.8} />
          </button>
          {result.rows.totalGroupCount > rowPageSize && (
            <div className="query-paging">
              <button className="icon-button" disabled={rowPage === 0} title="Previous rows" onClick={() => setRowPage(rowPage - 1)}>
                <IconChevronLeft size={15} stroke={1.8} />
              </button>
              <span className="muted">
                rows {rowPage * rowPageSize + 1}–{Math.min((rowPage + 1) * rowPageSize, result.rows.totalGroupCount)}
              </span>
              <button className="icon-button" disabled={rowPage >= lastPage} title="Next rows" onClick={() => setRowPage(rowPage + 1)}>
                <IconChevronRight size={15} stroke={1.8} />
              </button>
            </div>
          )}
        </div>
      )}
      <div className={"query-table-wrap" + (loading ? " loading" : "")}>{result && <PivotTable result={result} onDrill={onDrill} />}</div>
    </div>
  );
}

/** One axis of the builder: its levels, and the options that apply to all of them. */
function AxisEditor({
  title,
  levels,
  properties,
  options,
  measureNames,
  onChange,
  onOptions,
}: {
  title: string;
  levels: PivotLevelSpec[];
  properties: PivotProperty[];
  options: PivotAxisOptions;
  measureNames: string[];
  onChange: (levels: PivotLevelSpec[]) => void;
  onOptions: (options: PivotAxisOptions) => void;
}) {
  const sortChoices = ["Count", ...measureNames.filter((n) => n && n.toLowerCase() !== "count")];
  return (
    <div className="pivot-builder-row">
      <span className="pivot-builder-label">{title}</span>
      {levels.map((level, i) => {
        const property = properties.find((p) => p.id === level.propertyId);
        const modes = modesOf(property);
        return (
          <span className="pivot-chip" key={i}>
            <select
              className="select"
              value={level.propertyId}
              title={property ? property.type + (property.declaredBy ? " · from " + property.declaredBy : "") : "Group by this property"}
              onChange={(e) => {
                const next = properties.find((p) => p.id === e.target.value);
                onChange(levels.map((x, j) => (j === i ? { propertyId: e.target.value, mode: modesOf(next)[0].value } : x)));
              }}
            >
              {!property && <option value="">choose…</option>}
              {properties.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
            {modes.length > 1 && (
              <select
                className="select"
                value={level.mode}
                title="How the values are bucketed: one group per value, ranges, or a calendar interval"
                onChange={(e) => onChange(levels.map((x, j) => (j === i ? { ...x, mode: e.target.value } : x)))}
              >
                {modes.map((m) => (
                  <option key={m.value} value={m.value}>
                    {m.label}
                  </option>
                ))}
              </select>
            )}
            <button className="icon-button" title={"Remove this " + title.toLowerCase().replace(/s$/, "") + " grouping"} onClick={() => onChange(levels.filter((_, j) => j !== i))}>
              <IconX size={13} stroke={1.8} />
            </button>
          </span>
        );
      })}
      <button className="link-button" disabled={properties.length === 0} onClick={() => onChange([...levels, { propertyId: properties[0]?.id ?? "", mode: modesOf(properties[0])[0].value }])}>
        + {title.toLowerCase().replace(/s$/, "")}
      </button>
      {levels.length > 0 && (
        <div className="pivot-options">
          <label title="Keep only the first N groups of each level, after sorting; 0 shows them all">
            top
            <input
              className="text-input pivot-number"
              type="number"
              min={0}
              value={options.maxGroups}
              onChange={(e) => onOptions({ ...options, maxGroups: Math.max(0, Number(e.target.value) || 0) })}
            />
          </label>
          <label title="The order of the groups: their natural order, or by a measure">
            sort
            <select className="select" value={options.sortByMeasure ?? ""} onChange={(e) => onOptions({ ...options, sortByMeasure: e.target.value || null })}>
              <option value="">natural</option>
              {sortChoices.map((n) => (
                <option key={n} value={n}>
                  {n}
                </option>
              ))}
            </select>
          </label>
          {options.sortByMeasure && (
            <button className="icon-button" title={options.descending ? "Largest first — click for smallest first" : "Smallest first — click for largest first"} onClick={() => onOptions({ ...options, descending: !options.descending })}>
              {options.descending ? <IconArrowNarrowDown size={14} stroke={2} /> : <IconArrowNarrowUp size={14} stroke={2} />}
            </button>
          )}
          <label className="settings-check" title="Collect the groups trimmed by the limit into one (other) group">
            <input type="checkbox" checked={options.otherGroup} onChange={(e) => onOptions({ ...options, otherGroup: e.target.checked })} />
            other
          </label>
          <label className="settings-check" title="A (none) group for the nodes without a value">
            <input type="checkbox" checked={options.includeMissing} onChange={(e) => onOptions({ ...options, includeMissing: e.target.checked })} />
            missing
          </label>
        </div>
      )}
    </div>
  );
}

/** The facet selection a group stands for: one bucket per level. An "(other)" group has none. */
function selectionsOf(axis: PivotAxis, group: PivotGroup): FacetSelection[] | null {
  if (group.isOther) return null;
  const out: FacetSelection[] = [];
  for (let i = 0; i < group.depth; i++) {
    out.push({ propertyId: axis.levels[i].propertyId, values: [{ value: group.values[i], value2: group.values2[i] }] });
  }
  return out;
}

function formatValue(value: number | null, fn: PivotFunction): string {
  if (value === null) return "–";
  if (fn === "Count" || fn === "CountDistinct" || Number.isInteger(value)) return formatCount(value);
  return value.toLocaleString(undefined, { maximumFractionDigits: 2 });
}

/**
 * The table itself. Row levels are the leading columns (a label is left blank when it repeats the
 * row above at that level and every level before it, so a nested axis reads as an outline); the
 * column groups follow, each holding one column per measure; the row totals close the row. With
 * sub-totals on, a total line follows the last row of every group above the leaf level.
 */
function PivotTable({ result, onDrill }: { result: PivotResult; onDrill: (selections: FacetSelection[]) => void }) {
  const { rows, columns, measures } = result;
  const cellIndex = useMemo(() => {
    const index = new Map<string, PivotCell>();
    for (const c of result.cells) index.set(c.row + ":" + c.column, c);
    return index;
  }, [result]);
  const subTotalIndex = useMemo(() => {
    const index = new Map<string, (typeof result.rowSubTotals)[number]>();
    for (const s of result.rowSubTotals) index.set(s.group.labels.join(""), s);
    return index;
  }, [result]);
  const rowLevels = Math.max(1, rows.levels.length);
  const hasColumns = columns.levels.length > 0;
  const hasRows = rows.levels.length > 0;
  const measureCount = Math.max(1, measures.length);

  function drill(row: PivotGroup | null, column: PivotGroup | null) {
    const a = row ? selectionsOf(rows, row) : [];
    const b = column ? selectionsOf(columns, column) : [];
    if (a === null || b === null) return;
    onDrill([...a, ...b]);
  }
  function cells(cell: PivotCell | null | undefined, key: string, className: string, onClick?: () => void, title?: string) {
    if (measures.length === 0) {
      return (
        <td key={key} className={className + " pivot-num" + (cell ? " pivot-cell" : " pivot-empty")} onClick={cell ? onClick : undefined} title={cell ? title : undefined}>
          {cell ? formatCount(cell.count) : "–"}
        </td>
      );
    }
    return measures.map((m, i) => (
      <td key={key + ":" + i} className={className + " pivot-num" + (cell ? " pivot-cell" : " pivot-empty")} onClick={cell ? onClick : undefined} title={cell ? title : undefined}>
        {cell ? formatValue(cell.values[i], m.function) : "–"}
      </td>
    ));
  }
  const drillTitle = "Show these nodes";

  // the sub-total lines that follow row r: the groups (deepest first) whose last row it is
  function subTotalsAfter(r: number): { key: string; labels: string[]; sub: (typeof result.rowSubTotals)[number] }[] {
    if (result.rowSubTotals.length === 0) return [];
    const group = rows.groups[r];
    const next = rows.groups[r + 1];
    const out: { key: string; labels: string[]; sub: (typeof result.rowSubTotals)[number] }[] = [];
    for (let d = rows.levels.length - 2; d >= 0; d--) {
      const prefix = group.labels.slice(0, d + 1);
      const same = next !== undefined && prefix.every((l, i) => next.labels[i] === l);
      if (same) break; // this prefix continues, and so do every shorter one
      const sub = subTotalIndex.get(prefix.join(""));
      if (sub) out.push({ key: r + ":" + d, labels: prefix, sub });
    }
    return out;
  }

  return (
    <table className="query-table pivot-table">
      <thead>
        <tr>
          {hasRows ? rows.levels.map((l) => <th key={l.propertyId + l.codeName} title={l.valueType}>{l.codeName}</th>) : <th />}
          {hasColumns
            ? columns.groups.map((g, c) => (
                <th key={c} className="pivot-group" colSpan={measureCount} title={formatCount(g.count) + " nodes"}>
                  {g.labels.join(" / ")}
                </th>
              ))
            : measures.length === 0
              ? <th className="pivot-group">Count</th>
              : measures.map((m) => (
                  <th key={m.name} className="pivot-group" title={m.function + (m.propertyName ? " of " + m.propertyName : "")}>
                    {m.name}
                  </th>
                ))}
          {hasColumns && (
            <th className="pivot-group pivot-total-col" colSpan={measureCount}>
              Total
            </th>
          )}
        </tr>
        {hasColumns && measures.length > 1 && (
          <tr>
            {Array.from({ length: rowLevels }, (_, i) => (
              <th key={i} />
            ))}
            {columns.groups.map((_, c) => measures.map((m) => (
              <th key={c + ":" + m.name} className="pivot-measure" title={m.function + (m.propertyName ? " of " + m.propertyName : "")}>
                {m.name}
              </th>
            )))}
            {measures.map((m) => (
              <th key={"t:" + m.name} className="pivot-measure pivot-total-col">
                {m.name}
              </th>
            ))}
          </tr>
        )}
      </thead>
      <tbody>
        {rows.groups.map((g, r) => {
          const previous = rows.groups[r - 1];
          return [
            <tr key={r}>
              {hasRows ? (
                g.labels.map((label, i) => {
                  // a label repeats when this row and the one above agree at this level and every level before it
                  const repeats = previous !== undefined && !g.isOther && !previous.isOther && g.labels.slice(0, i + 1).every((l, k) => previous.labels[k] === l);
                  return (
                    <td key={i} className={"pivot-label pivot-cell" + (g.isOther ? " pivot-other" : "")} title={repeats ? undefined : formatCount(g.count) + " nodes — " + drillTitle} onClick={() => drill(g, null)}>
                      {repeats ? "" : label}
                    </td>
                  );
                })
              ) : (
                <td className="pivot-label">All</td>
              )}
              {columns.groups.map((cg, c) => cells(cellIndex.get(r + ":" + c), r + ":" + c, "", () => drill(g, cg), drillTitle))}
              {hasColumns && cells(result.rowTotals[r], r + ":total", "pivot-total-col", () => drill(g, null), drillTitle)}
            </tr>,
            ...subTotalsAfter(r).map(({ key, labels, sub }) => (
              <tr key={"sub:" + key} className="pivot-subtotal">
                {Array.from({ length: rowLevels }, (_, i) => (
                  <td key={i} className="pivot-label">
                    {i < labels.length - 1 ? "" : i === labels.length - 1 ? labels[i] + " total" : ""}
                  </td>
                ))}
                {columns.groups.map((cg, c) => cells(sub.cells[c], key + ":" + c, "", () => drill(sub.group, cg), drillTitle))}
                {hasColumns && cells(sub.total, key + ":total", "pivot-total-col", () => drill(sub.group, null), drillTitle)}
              </tr>
            )),
          ];
        })}
        {hasRows && (
          <tr className="pivot-total">
            {Array.from({ length: rowLevels }, (_, i) => (
              <td key={i} className="pivot-label">
                {i === 0 ? "Total" : ""}
              </td>
            ))}
            {columns.groups.map((cg, c) => cells(result.columnTotals[c], "total:" + c, "", () => drill(null, cg), drillTitle))}
            {hasColumns && cells(result.grandTotal, "total:total", "pivot-total-col", () => drill(null, null), drillTitle)}
          </tr>
        )}
      </tbody>
    </table>
  );
}

/** The table as csv: the row labels, then one column per column group and measure, then the row totals. */
function downloadCsv(result: PivotResult) {
  const { rows, columns, measures } = result;
  const hasColumns = columns.levels.length > 0;
  const measureNamesOrCount = measures.length === 0 ? ["Count"] : measures.map((m) => m.name);
  const header = [
    ...(rows.levels.length > 0 ? rows.levels.map((l) => l.codeName) : ["Group"]),
    ...columns.groups.flatMap((g) => measureNamesOrCount.map((m) => (hasColumns ? g.labels.join(" / ") + " · " + m : m))),
    ...(hasColumns ? measureNamesOrCount.map((m) => "Total · " + m) : []),
  ];
  const index = new Map<string, PivotCell>();
  for (const c of result.cells) index.set(c.row + ":" + c.column, c);
  const valuesOf = (cell: PivotCell | null | undefined) =>
    measures.length === 0 ? [cell ? String(cell.count) : ""] : measures.map((_, i) => (cell && cell.values[i] !== null ? String(cell.values[i]) : ""));
  const lines = [header];
  rows.groups.forEach((g, r) => {
    lines.push([
      ...(rows.levels.length > 0 ? g.labels : ["All"]),
      ...columns.groups.flatMap((_, c) => valuesOf(index.get(r + ":" + c))),
      ...(hasColumns ? valuesOf(result.rowTotals[r]) : []),
    ]);
  });
  if (rows.levels.length > 0) {
    lines.push([
      "Total",
      ...Array.from({ length: rows.levels.length - 1 }, () => ""),
      ...columns.groups.flatMap((_, c) => valuesOf(result.columnTotals[c])),
      ...(hasColumns ? valuesOf(result.grandTotal) : []),
    ]);
  }
  const quote = (s: string) => '"' + s.replace(/"/g, '""') + '"';
  const csv = "﻿" + lines.map((line) => line.map(quote).join(",")).join("\r\n");
  const url = URL.createObjectURL(new Blob([csv], { type: "text/csv;charset=utf-8" }));
  try {
    const link = document.createElement("a");
    link.href = url;
    link.download = result.typeName + "-pivot.csv";
    link.click();
  } finally {
    URL.revokeObjectURL(url);
  }
}
