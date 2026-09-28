import { useCallback, useEffect, useMemo, useState } from "react";
import type { PivotBase } from "./PivotView";
import { GeoMap, colouredMarks, emptyMap } from "./GeoMap";
import { fetchCards, fetchPivotModel, runMap, type MapRequest, type PivotModel, type PivotProperty } from "../server/query";
import type { MarqueeMode, SelectMode } from "../selection";
import { useLiveResult } from "../server/hooks";
import { formatQuery } from "../format";
import type { MapDefinition } from "../queryTabs";
import { decodeMap } from "../map/points";

export { emptyMap } from "./GeoMap";

const modeOptions = [
  { value: "auto", label: "auto" },
  { value: "values", label: "values" },
  { value: "ranges", label: "ranges" },
];

/**
 * The map of the query section: every node of the result standing on the place one of its
 * properties says it is (see GeoMap for the map itself).
 *
 * What this adds to the map is the query: which property places the nodes and which one colours
 * them, both offered from the type's model; the result asked of the server, which sends a position
 * per node read straight from the geo index - no node is read, whatever the size of the result - and,
 * for the colouring, the same groups the visual pivot uses; a node's name in its tooltip; and a click
 * or a rectangle opening the nodes in the form beside the map. What is counted follows the search
 * and the facet selection like everything else on this page.
 */
export function MapView({
  base,
  definition,
  onChange,
  refreshToken,
  showQuery,
  onOpen,
  onSelectMany,
  marquee,
  selected,
  allSelected,
  fullscreen,
  onToggleFullscreen,
  maximized,
  onToggleMaximized,
  head,
}: {
  base: PivotBase;
  /** The definition as the page keeps it - null until this view has opened once for the type. */
  definition: MapDefinition | null;
  onChange: (definition: MapDefinition) => void;
  /** Changes when the page is asked to run again with nothing else changed. */
  refreshToken: number;
  showQuery: boolean;
  /** A node was clicked: it goes to the form beside the map, by the internal id the point carries. */
  onOpen: (nodeId: number, mode: SelectMode) => void;
  /** A rectangle was drawn round some points: these nodes become the selection, or are added to or taken out of it (see applyMarquee). */
  onSelectMany: (ids: number[], mode: MarqueeMode) => void;
  /** Drag to select is on: the left button draws a rectangle round points rather than moving the map. */
  marquee: boolean;
  /** The nodes the form has open, by internal id: the map marks their points. */
  selected: readonly number[];
  /** The selection is the whole result set (the page's Select all): every point is marked, no guid needed. */
  allSelected: boolean;
  fullscreen: boolean;
  onToggleFullscreen: () => void;
  /** the row fills the browser window rather than the screen (see useMaximized) */
  maximized: boolean;
  onToggleMaximized: () => void;
  /** What the result's own head would say, when the page has folded that head away (see the visual pivot). */
  head?: React.ReactNode;
}) {
  const [model, setModel] = useState<PivotModel | null>(null);
  const [modelError, setModelError] = useState<string | null>(null);
  const def = definition ?? emptyMap;

  useEffect(() => {
    let cancelled = false;
    setModel(null);
    setModelError(null);
    fetchPivotModel(base.storeId, base.typeId, base.propertyScope)
      .then((m) => !cancelled && setModel(m))
      .catch((e) => !cancelled && setModelError(e instanceof Error ? e.message : String(e)));
    return () => {
      cancelled = true;
    };
  }, [base.storeId, base.typeId, base.propertyScope]);

  const positions = useMemo(() => model?.properties.filter((p) => p.geo) ?? [], [model]);
  const groupable = useMemo(() => model?.properties.filter((p) => p.groupable) ?? [], [model]);

  // the first time the view opens for a type it places the nodes by the first position property it
  // finds, so there is a map before anyone chooses; the choice is then the query's own
  useEffect(() => {
    if (model && definition === null) onChange({ ...emptyMap, property: positions[0]?.id ?? null });
  }, [model, definition, positions, onChange]);

  const property = positions.some((p) => p.id === def.property) ? def.property : (positions[0]?.id ?? null);
  const colorProperty = groupable.some((p) => p.id === def.colorProperty) ? def.colorProperty : null;
  const colorInfo = groupable.find((p) => p.id === colorProperty);
  const coloured = colouredMarks(def);

  const request = useMemo<MapRequest | null>(() => {
    if (model === null || definition === null || property === null) return null;
    return {
      storeId: base.storeId,
      typeId: base.typeId,
      text: base.text,
      match: base.match,
      anyWord: base.anyWord,
      semanticRatio: base.semanticRatio,
      minimumSimilarity: base.minimumSimilarity,
      selections: base.selections,
      propertyId: property,
      // the colouring is only asked for when something is drawn in it; a heat field has no use for it
      properties: coloured && colorProperty !== null ? [{ propertyId: colorProperty, mode: def.colorMode }] : [],
    };
    // the token is not part of the request; a new object is how the runner is told to run again
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [model, definition === null, base, property, coloured, colorProperty, def.colorMode, refreshToken]);
  const { result, loading, error } = useLiveResult(request, runMap);
  const points = useMemo(() => (result ? decodeMap(result) : null), [result]);
  const colorData = colorProperty === null ? null : (points?.byProperty.get(colorProperty) ?? null);

  // a node's name, for the first line of its tooltip, asked for once and kept by the map
  const storeId = base.storeId;
  const describe = useCallback((id: number) => fetchCards(storeId, [id]).then((r) => r.cards[0]?.name ?? ""), [storeId]);

  const propertySelect = (value: string | null, none: string, title: string, options: PivotProperty[], onPick: (id: string | null) => void) => (
    <select className="select" value={value ?? ""} title={title} onChange={(e) => onPick(e.target.value || null)}>
      <option value="">{none}</option>
      {options.map((p) => (
        <option key={p.id} value={p.id}>
          {p.name}
          {p.declaredBy ? " (" + p.declaredBy + ")" : ""}
        </option>
      ))}
    </select>
  );

  if (modelError) return <div className="query-error">{modelError}</div>;
  if (model !== null && positions.length === 0) {
    return (
      <div className="query-empty">
        <p>Nothing on this type says where a node is, so there is nothing to put on a map.</p>
        <p className="muted">A map places nodes by a property holding a position — a GeoCoordinate. Give the type one, index it, and it will be offered here.</p>
      </div>
    );
  }

  return (
    <GeoMap
      points={points}
      colorData={colorData}
      definition={def}
      onChange={onChange}
      loading={loading}
      error={error}
      leading={
        <>
          <span className="pivot-builder-label">Place by</span>
          <span className="pivot-chip">{propertySelect(property, "(none)", "The property holding the position each node is drawn at", positions, (id) => onChange({ ...def, property: id }))}</span>
        </>
      }
      colourControl={
        <>
          <span className="pivot-builder-label visual-label-2">Colour by</span>
          <span className="pivot-chip">
            {propertySelect(colorProperty, "(one colour)", "The property whose values colour the marks", groupable, (id) => onChange({ ...def, colorProperty: id }))}
            {colorInfo && hasModes(colorInfo) && (
              <select className="select" value={def.colorMode} title="How the values are grouped: one group per value, or ranges of them" onChange={(e) => onChange({ ...def, colorMode: e.target.value })}>
                {modeOptions.map((m) => (
                  <option key={m.value} value={m.value}>
                    {m.label}
                  </option>
                ))}
              </select>
            )}
          </span>
        </>
      }
      above={showQuery && result ? <div className="query-string">{formatQuery(result.query)}</div> : null}
      describe={describe}
      onOpen={onOpen}
      onSelectMany={onSelectMany}
      marquee={marquee}
      selected={selected}
      allSelected={allSelected}
      fullscreen={fullscreen}
      onToggleFullscreen={onToggleFullscreen}
      maximized={maximized}
      onToggleMaximized={onToggleMaximized}
      head={head}
    />
  );
}

/** Numbers, dates and durations can be grouped by ranges as well as by value. */
function hasModes(property: PivotProperty): boolean {
  return property.numeric || property.isDate || property.type === "TimeSpan";
}
