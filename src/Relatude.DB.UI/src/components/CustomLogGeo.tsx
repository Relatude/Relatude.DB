import { useCallback, useLayoutEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { GeoMap, boundsOf, emptyMap, type MapNoun } from "./GeoMap";
import { ShareBars, StatTiles, type Tile } from "./LogCharts";
import { bytesOf } from "../server/query";
import { useMaximized } from "../maximize";
import type { SeriesData, SeriesSummary } from "../server/logs";
import type { MapDefinition } from "../queryTabs";
import type { MapPoints, MapProperty } from "../map/points";
import { formatCount } from "../format";
import { formatArea, formatDistance } from "../customLogRange";

/**
 * The pieces the Logs page draws a column of positions with: its heatmap on a map, the centres of
 * its intervals on a map, and the numbers that go with them. The map is the query section's own
 * (GeoMap), handed points made here from what the statistics sent - a heatmap's cells, each counting
 * for the positions it holds, or one centre per interval, counting for its entries.
 */

export const entries: MapNoun = { one: "entry", many: "entries" };

/** What the server sends a coordinate as: ten million to the degree. */
const coordinateScale = 1e7;

/** A heatmap's cells as points for the map: each at the middle of its cell, weighing what it holds. */
export function heatmapPoints(summary: SeriesSummary | null | undefined): { points: MapPoints; heights: Float32Array } | null {
  const cells = summary?.cells;
  if (!cells || cells.count === 0) return null;
  const centres = bytesOf(cells.centres);
  const packed = new Int32Array(centres.buffer, centres.byteOffset, cells.count * 2);
  const countBytes = bytesOf(cells.counts);
  const counts = new Float64Array(countBytes.buffer.slice(countBytes.byteOffset, countBytes.byteOffset + cells.count * 8));
  const levels = bytesOf(cells.levels);
  const lat = new Float32Array(cells.count);
  const lon = new Float32Array(cells.count);
  const weight = new Float32Array(cells.count);
  const heights = new Float32Array(cells.count);
  const ids = new Int32Array(cells.count);
  for (let i = 0; i < cells.count; i++) {
    lat[i] = packed[i * 2] / coordinateScale;
    lon[i] = packed[i * 2 + 1] / coordinateScale;
    weight[i] = counts[i];
    // a cell is 180/2^level degrees tall, which is the same number of metres at every latitude
    heights[i] = (Math.PI * 6371000) / Math.pow(2, levels[i]);
    ids[i] = i;
  }
  return { points: { count: cells.count, total: cells.count, read: cells.count, ids, lat, lon, weight, byProperty: new Map() }, heights };
}

/** The centre of every interval of a centre-and-spread series as a point, weighing its entries. */
export function centrePoints(data: SeriesData | null): MapPoints | null {
  if (!data) return null;
  const kept = data.points.map((p, i) => ({ p, i })).filter(({ p }) => p.hasValue && p.latitude != null && p.longitude != null);
  if (kept.length === 0) return null;
  const n = kept.length;
  const lat = new Float32Array(n);
  const lon = new Float32Array(n);
  const weight = new Float32Array(n);
  const ids = new Int32Array(n);
  kept.forEach(({ p, i }, k) => {
    lat[k] = p.latitude!;
    lon[k] = p.longitude!;
    weight[k] = p.count ?? 1;
    ids[k] = i; // the interval's place among the series' points
  });
  return { count: n, total: n, read: n, ids, lat, lon, weight, byProperty: new Map() };
}

/**
 * A map definition kept for one place on the page (a log's heatmap, say) in the browser, so the
 * picture chosen there is the one it opens with next time; starting from what that place wants.
 */
export function useStoredMap(key: string, start: Partial<MapDefinition>): [MapDefinition, (d: MapDefinition) => void] {
  const [definition, setDefinition] = useState<MapDefinition>(() => {
    try {
      const saved = localStorage.getItem(key);
      if (saved) return { ...emptyMap, ...start, ...(JSON.parse(saved) as Partial<MapDefinition>) };
    } catch {
      // private windows and cleared site data: the default below
    }
    return { ...emptyMap, ...start };
  });
  const change = useCallback(
    (next: MapDefinition) => {
      setDefinition(next);
      try {
        localStorage.setItem(key, JSON.stringify(next));
      } catch {
        // nothing depends on it holding
      }
    },
    [key],
  );
  return [definition, change];
}

/** What a map in a MapBox is handed to offer filling the window, and to say that it does. */
export interface MapBoxControl {
  maximized: boolean;
  onToggleMaximized: () => void;
}

/**
 * A map inside a panel: the map fills a box of its own height, the way it fills the query section,
 * and can be made to fill the browser window instead (Escape, or its button again, puts it back).
 *
 * The map is always drawn into ONE element made for the box, and it is that element that moves -
 * into the box on the page, or out to the top of the document. Rendering the map somewhere else
 * would build a new one: a new WebGL context, and the map back where it opened rather than where it
 * was turned to. It cannot just be made `position: fixed` where it is, either: the logs section is a
 * size container, and a container is what a fixed box inside it is placed against, not the window.
 * The box keeps its height on the page meanwhile, so nothing below it moves.
 */
export function MapBox({ height, children }: { height: number; children: (control: MapBoxControl) => React.ReactNode }) {
  const [maximized, onToggleMaximized] = useMaximized();
  const place = useRef<HTMLDivElement>(null);
  const [host] = useState(() => {
    const el = document.createElement("div");
    el.className = "clog-map";
    return el;
  });
  // the map is mounted once its element is in the page, so it measures and reads its colours there
  const [placed, setPlaced] = useState(false);
  useLayoutEffect(() => {
    const spot = place.current;
    if (!spot) return;
    if (maximized) {
      // the section's tone is set on the section, and the top of the document is outside it
      const cs = getComputedStyle(spot);
      for (const name of toneProperties) host.style.setProperty(name, cs.getPropertyValue(name));
      host.classList.add("maximized");
      document.body.appendChild(host);
    } else {
      for (const name of toneProperties) host.style.removeProperty(name);
      host.classList.remove("maximized");
      spot.appendChild(host);
    }
    setPlaced(true);
    return () => host.remove();
  }, [maximized, host]);
  return (
    <div className="clog-map-place" ref={place} style={{ height }}>
      {placed && createPortal(children({ maximized, onToggleMaximized }), host)}
    </div>
  );
}

const toneProperties = ["--section-tone", "--section-tone-soft"];

/** The heatmap of a log's positions over the range: the cells on a map, the densest of them, and how little ground holds most of them. */
export function HeatmapView({ logKey, data, height }: { logKey: string; data: SeriesData; height: number }) {
  const [definition, setDefinition] = useStoredMap("customLogs:heatmap:" + logKey + ":" + (data.property ?? ""), { marks: "heat", legend: false, radius: 18 });
  const made = useMemo(() => heatmapPoints(data.summary), [data.summary]);
  const fitTo = useMemo(() => boundsOf(made?.points ?? null), [made]);
  const describe = useCallback((id: number) => (made ? "a cell " + formatDistance(made.heights[id]) + " tall" : ""), [made]);
  const s = data.summary;
  return (
    <>
      <MapBox height={height}>
        {(control) => (
          <GeoMap
            points={made?.points ?? null}
            colorData={null}
            definition={definition}
            onChange={setDefinition}
            noun={entries}
            describe={describe}
            notes={null}
            emptyText="No position was counted in the range."
            fitTo={fitTo}
            {...control}
          />
        )}
      </MapBox>
      {s && s.total != null && s.total > 0 && (
        <div className="clog-geo-foot">
          <StatTiles
            tiles={[
              { label: "Positions", value: formatCount(s.total) },
              { label: "Half of them within", value: formatArea(s.halfWithinSquareMeters ?? 0), hint: "the densest cells holding half of the positions" },
              { label: "Nine in ten within", value: formatArea(s.nineTenthsWithinSquareMeters ?? 0) },
              { label: "Cells", value: formatCount(s.cells?.count ?? 0), hint: "merged where there is little in them" },
            ]}
          />
          {s.hotspots && s.hotspots.length > 0 && (
            <ShareBars
              items={s.hotspots.slice(0, 6).map((h) => ({
                label: `${h.latitude.toFixed(4)}, ${h.longitude.toFixed(4)} · ${formatDistance(h.heightMeters)} cell · ${formatCount(Math.round(h.perSquareKilometre))}/km²`,
                count: h.count,
                hint: "One of the densest cells: positions per square kilometre",
              }))}
              total={s.total}
              format={(n) => formatCount(n)}
            />
          )}
        </div>
      )}
    </>
  );
}

/** The centres of the intervals on a map, each weighing its entries: where the activity went, interval by interval. */
export function CentresView({ logKey, data, height, label }: { logKey: string; data: SeriesData; height: number; label: (index: number) => string }) {
  const [definition, setDefinition] = useStoredMap("customLogs:centres:" + logKey + ":" + (data.property ?? ""), { marks: "dots", legend: false, size: 7 });
  const points = useMemo(() => centrePoints(data), [data]);
  const fitTo = useMemo(() => boundsOf(points, 1), [points]);
  const describe = useCallback(
    (id: number) => {
      const p = data.points[id];
      return p ? label(id) + " · spread " + formatDistance(p.value ?? 0) : "";
    },
    [data, label],
  );
  return (
    <MapBox height={height}>
      {(control) => (
        <GeoMap
          points={points}
          colorData={null as MapProperty | null}
          definition={definition}
          onChange={setDefinition}
          noun={entries}
          describe={describe}
          notes={null}
          emptyText="No interval of the range has a position."
          fitTo={fitTo}
          {...control}
        />
      )}
    </MapBox>
  );
}

/** The tiles a centre-and-spread series adds to the top of the graphs page. */
export function spreadTiles(label: string, s: SeriesSummary): Tile[] {
  const tiles: Tile[] = [];
  if (s.latitude != null && s.longitude != null) {
    tiles.push({ label: label + " · centre", value: `${s.latitude.toFixed(4)}, ${s.longitude.toFixed(4)}`, hint: "the spherical mean of every position in the range" });
  }
  if (s.standardDistance != null) {
    tiles.push({
      label: label + " · spread",
      value: formatDistance(s.standardDistance),
      hint: s.major != null && s.minor != null ? `one standard deviation: ${formatDistance(s.major)} along, ${formatDistance(s.minor)} across` : undefined,
    });
  }
  return tiles;
}

/** Which way a spread runs, in words: "north-east", from a bearing of 0 to 180 degrees. */
export function bearingText(degrees: number | null | undefined): string {
  if (degrees == null) return "no direction";
  // an axis rather than a direction: north-east is the line from the north-east to the south-west
  const names = ["north-south", "north-north-east", "north-east", "east-north-east", "east-west", "east-south-east", "south-east", "south-south-east"];
  return names[Math.round(((degrees % 180) + 180) % 180 / 22.5) % 8];
}
