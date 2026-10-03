import { useEffect, useMemo, useRef, useState } from "react";
import { IconSearch, IconX } from "@tabler/icons-react";
import { VisualPivotView, emptyVisual } from "../components/VisualPivotView";
import type { PivotBase } from "../components/PivotView";
import type { Facet, FacetSelection, FacetValue } from "../server/query";
import { useLiveResult } from "../server/hooks";
import { formatCount } from "../format";
import type { VisualDefinition } from "../queryTabs";
import type { FacetModel, FacetSearchRequest, FacetSource } from "./facetSource";

// The facet search of a GraphQL endpoint, on the endpoint's own url: a rail of facets over the exposed types and the
// admin UI's visual pivot beside it, in its simple form - nothing to open, select or edit, only the picture, which
// starts as a slowly turning 3D bar chart of the type and answers every facet with its cards flying in and out.
// The server keeps it to what the endpoint exposes (NodeServer/GraphQL/GraphQLFacetSearch.cs).

/** how long the search box waits for the typing to stop before the picture is asked again: every answer moves every card */
const textSettleMs = 280;

function keyOf(v: { value: string | null; value2: string | null }): string {
  return (v.value ?? "\u0000") + "|" + (v.value2 ?? "");
}

/**
 * How the picture of a type starts: rows into the distance by a property with a handful of values, bars across by
 * another, and the cards coloured by a third - so the first thing seen is a 3D bar chart on two axes, turning
 * slowly. Chosen from the facet rail's own answer, which says how many values each property has: a property
 * with hundreds of values makes a picture of needles, and one with a single value makes nothing at all.
 */
function startingDefinition(facets: Facet[]): VisualDefinition {
  const taken = new Set<string>();
  const pick = (bands: [number, number][], prefer?: (f: Facet) => boolean): string | null => {
    for (const [low, high] of bands) {
      const inBand = facets.filter((f) => !taken.has(f.propertyId) && f.totalValues >= low && f.totalValues <= high);
      const found = (prefer ? inBand.find(prefer) : undefined) ?? inBand[0];
      if (found) {
        taken.add(found.propertyId);
        return found.propertyId;
      }
    }
    return null;
  };
  // rows read best as a few named groups (values rather than ranges), bars can take a dozen or two
  const rows = pick([[3, 6], [2, 2], [7, 10], [11, 24]], (f) => !f.isRange);
  const bars = pick([[3, 12], [13, 24], [2, 2]]);
  const colour = pick([[2, 10], [11, 40]]) ?? bars ?? rows;
  // every hue rather than the app's own blues, and the cards in their colours: a node without a picture would
  // otherwise wear the grey placeholder, and a page of them reads as a field of grey (the camera turns them on)
  return { ...emptyVisual, colorProperty: colour, barProperty: bars, depthGroupProperty: rows, spin: true, solidPictures: false, palette: "spectrum" };
}

export function FacetPage({ source }: { source: FacetSource }) {
  const [model, setModel] = useState<FacetModel | null>(null);
  const [modelError, setModelError] = useState<string | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    setModel(null);
    setModelError(null);
    source.model(controller.signal).then(
      (m) => !controller.signal.aborted && setModel(m),
      (e) => !controller.signal.aborted && setModelError(e instanceof Error ? e.message : String(e)),
    );
    return () => controller.abort();
  }, [source]);

  const [typeId, setTypeId] = useState<string | null>(null);
  const type = model ? (model.types.find((t) => t.id === typeId) ?? model.types[0] ?? null) : null;
  const [typed, setTyped] = useState("");
  const [text, setText] = useState("");
  useEffect(() => {
    const timer = window.setTimeout(() => setText(typed.trim()), textSettleMs);
    return () => window.clearTimeout(timer);
  }, [typed]);
  const [selections, setSelections] = useState<FacetSelection[]>([]);
  const [expanded, setExpanded] = useState<string[]>([]);

  const request = useMemo<FacetSearchRequest | null>(
    () => (type ? { typeId: type.id, text, selections, expanded } : null),
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the type is its id
    [type?.id, text, selections, expanded],
  );
  const { result, error } = useLiveResult(request, source.search);
  // an answer for the type that was on screen before is not this type's rail
  const current = result && type && result.typeId === type.id ? result : null;

  // What the picture of each type is showing, and whether it is shown flat. The first answer for a type decides how
  // its picture starts; after that it is whatever the builder line has been set to.
  const [definitions, setDefinitions] = useState<Record<string, VisualDefinition>>({});
  const [flat, setFlat] = useState<Record<string, boolean>>({});
  useEffect(() => {
    if (!current || definitions[current.typeId]) return;
    setDefinitions((d) => ({ ...d, [current.typeId]: startingDefinition(current.facets) }));
  }, [current, definitions]);
  const definition = type ? (definitions[type.id] ?? null) : null;
  const isFlat = type ? flat[type.id] === true : false;
  // flat is the same picture with its depth taken away, kept so that 3D comes back to what it was
  const shown = definition && isFlat ? { ...definition, depthGroupProperty: null, depthProperty: null } : definition;
  const solid = !!shown && (!!shown.depthGroupProperty || !!shown.depthProperty);
  // what 3D would lay the rows by when the picture has none of its own yet
  const rowsFor3d = definition?.depthGroupProperty ?? (current ? startingDefinition(current.facets).depthGroupProperty : null);

  function changeDefinition(next: VisualDefinition) {
    if (!type || !definition) return;
    if (isFlat && (next.depthGroupProperty || next.depthProperty)) {
      // a depth chosen on the flat picture is a way into 3D
      setFlat((f) => ({ ...f, [type.id]: false }));
      setDefinitions((d) => ({ ...d, [type.id]: next }));
      return;
    }
    setDefinitions((d) => ({ ...d, [type.id]: isFlat ? { ...next, depthGroupProperty: definition.depthGroupProperty, depthProperty: definition.depthProperty } : next }));
  }

  function setDimensions(three: boolean) {
    if (!type || !definition) return;
    setFlat((f) => ({ ...f, [type.id]: !three }));
    if (three && !definition.depthGroupProperty && !definition.depthProperty && rowsFor3d) {
      setDefinitions((d) => ({ ...d, [type.id]: { ...definition, depthGroupProperty: rowsFor3d } }));
    }
  }

  function chooseType(id: string) {
    setTypeId(id);
    // another type has other properties: a selection or an expanded facet of the last one means nothing here
    setSelections([]);
    setExpanded([]);
  }

  function toggleFacet(facet: Facet, value: FacetValue) {
    const now = selections.find((s) => s.propertyId === facet.propertyId)?.values ?? [];
    const key = keyOf(value);
    const values = now.some((v) => keyOf(v) === key) ? now.filter((v) => keyOf(v) !== key) : [...now, { value: value.value, value2: value.value2 }];
    setSelections([...selections.filter((s) => s.propertyId !== facet.propertyId), ...(values.length > 0 ? [{ propertyId: facet.propertyId, values }] : [])]);
  }
  const selectedCount = selections.reduce((n, s) => n + s.values.length, 0);

  // what the picture is a picture of: the search as the rail has it
  const base = useMemo<PivotBase | null>(
    () => (type ? { storeId: "", typeId: type.id, text, semanticRatio: null, minimumSimilarity: null, match: "wildcard", anyWord: false, selections } : null),
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the type is its id
    [type?.id, text, selections],
  );

  // The whole row fills the screen, the rail with it, so a picture filling the screen can still be narrowed down.
  // The browser owns the state - Escape changes it without asking - so the button follows the document.
  const body = useRef<HTMLDivElement>(null);
  const [fullscreen, setFullscreen] = useState(false);
  useEffect(() => {
    const sync = () => setFullscreen(document.fullscreenElement !== null && document.fullscreenElement === body.current);
    document.addEventListener("fullscreenchange", sync);
    sync();
    return () => document.removeEventListener("fullscreenchange", sync);
  }, []);
  function toggleFullscreen() {
    if (document.fullscreenElement) void document.exitFullscreen().catch(() => {});
    else void body.current?.requestFullscreen?.().catch(() => {});
  }

  if (modelError) return <div className="fp-message">{modelError}</div>;
  if (!model) return <div className="fp-message muted">Loading…</div>;
  if (!type) return <div className="fp-message">This endpoint exposes no node types to search.</div>;

  const dimensions = (
    <div className="module-switch compact fp-dimensions" role="group" aria-label="Flat or solid picture">
      <button className={solid ? "" : "active"} title="The flat picture: a grid of cards, or bars of them" onClick={() => setDimensions(false)}>
        2D
      </button>
      <button
        className={solid ? "active" : ""}
        disabled={!solid && !rowsFor3d}
        title={rowsFor3d || solid ? "The picture in three dimensions: rows into the distance by a third property — drag to turn it, the wheel to come closer" : "Nothing here has few enough values to lay rows by"}
        onClick={() => setDimensions(true)}
      >
        3D
      </button>
    </div>
  );

  return (
    <div className="query-body fp" ref={body}>
      <aside className="query-facets panel fp-rail">
        {model.types.length > 1 && (
          <select className="select fp-type" value={type.id} title="The type searched" onChange={(e) => chooseType(e.target.value)}>
            {model.types.map((t) => (
              <option key={t.id} value={t.id}>
                {t.name} · {formatCount(t.count)}
              </option>
            ))}
          </select>
        )}
        <div className="fp-search">
          <IconSearch size={14} stroke={1.8} />
          <input className="text-input" value={typed} placeholder={"Search " + type.name} spellCheck={false} onChange={(e) => setTyped(e.target.value)} />
          {typed && (
            <button className="icon-button small" title="Clear the search" onClick={() => setTyped("")}>
              <IconX size={14} stroke={1.8} />
            </button>
          )}
        </div>
        <div className="fp-count">
          {current ? (
            <>
              <strong>{formatCount(current.total)}</strong>{" "}
              <span className="muted">
                {current.total === 1 ? "node" : "nodes"}
                {current.total !== current.sourceCount ? ` of ${formatCount(current.sourceCount)}` : ""} · {current.durationMs.toFixed(1)} ms
              </span>
            </>
          ) : (
            <span className="muted">Searching…</span>
          )}
        </div>
        {error && <div className="query-error">{error}</div>}
        <div className="query-facets-head">
          <span>Facets</span>
          {selectedCount > 0 && (
            <button className="link-button" onClick={() => setSelections([])}>
              clear {selectedCount}
            </button>
          )}
        </div>
        {current?.facets.length === 0 && <div className="query-empty muted">Nothing in this result can be faceted.</div>}
        {current?.facets.map((facet) => (
          <section className="query-facet" key={facet.propertyId}>
            <h4 title={facet.codeName + " · " + facet.valueType}>{facet.displayName}</h4>
            {facet.values.map((v) => (
              <button className={"query-facet-value" + (v.selected ? " selected" : "")} key={keyOf(v)} onClick={() => toggleFacet(facet, v)}>
                <span className="query-facet-check" aria-hidden />
                <span className="query-facet-label" title={v.display}>
                  {v.display}
                </span>
                <span className="query-facet-count">{formatCount(v.count)}</span>
              </button>
            ))}
            {facet.truncated && !expanded.includes(facet.propertyId) && (
              <button className="link-button" onClick={() => setExpanded([...expanded, facet.propertyId])}>
                show all {formatCount(facet.totalValues)}
              </button>
            )}
          </section>
        ))}
      </aside>
      <div className="query-results panel fp-picture">
        {base && shown ? (
          <VisualPivotView key={type.id} base={base} definition={shown} onChange={changeDefinition} fullscreen={fullscreen} onToggleFullscreen={toggleFullscreen} head={dimensions} source={source} simple />
        ) : (
          <div className="fp-message muted">{error ?? "Loading the picture…"}</div>
        )}
      </div>
    </div>
  );
}
