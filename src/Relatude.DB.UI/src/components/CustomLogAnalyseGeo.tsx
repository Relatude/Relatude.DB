import { useCallback, useMemo } from "react";
import { IconChartHistogram, IconMap, IconRoute, IconTarget } from "@tabler/icons-react";
import { GeoMap, boundsOf } from "./GeoMap";
import { Histogram, ShareBars, StatTiles, type Tile } from "./LogCharts";
import { MapBox, bearingText, entries, useStoredMap } from "./CustomLogGeo";
import { bytesOf } from "../server/query";
import type { AnalyseResult, CustomLogSummary, GeoAnalysis } from "../server/customLogs";
import type { MapGroup, MapPoints, MapProperty } from "../map/points";
import { paletteSlot } from "../visual/palette";
import { formatCount, formatDateTime } from "../format";
import { formatArea, formatDistance, formatMs, formatSpeed } from "../customLogRange";

/**
 * A column of positions, analysed from its entries (UIGeoAnalysis on the server): the tiles that
 * sum it up, the positions on a map, how far they lie from the middle of them, where they crowd,
 * and - when a column says what moved - the tracks of each thing that did.
 */
export function GeoAnalysisView({
  log,
  property,
  result,
  trackBy,
  onShowMatching,
}: {
  log: CustomLogSummary;
  property: string;
  result: AnalyseResult;
  trackBy: string | null;
  onShowMatching: (search: string) => void;
}) {
  const geo = result.geo!;
  const column = log.columns.find((c) => c.key === property);
  // coloured by the values of another column - a vehicle, an event - which tells apart best in every hue
  const [definition, setDefinition] = useStoredMap("customLogs:analyseMap:" + log.key + ":" + property, { marks: "dots", legend: true, size: 4, palette: "vivid" });
  const decoded = useMemo(() => decodePoints(geo), [geo]);
  const fitTo = useMemo(() => boundsOf(decoded?.points ?? null), [decoded]);
  const first = Date.parse(geo.points.firstUtc);
  const describe = useCallback(
    (id: number) => {
      if (!decoded) return "";
      const at = new Date(first + decoded.times[id] * 1000).toISOString();
      return formatDateTime(at);
    },
    [decoded, first],
  );
  const tiles = geoTiles(geo);
  const d = geo.distances;
  const name = column?.name ?? property;

  return (
    <>
      <StatTiles tiles={tiles} />
      <section className="panel">
        <h3>
          <IconMap size={15} stroke={1.8} /> Where {name} was{" "}
          <span className="panel-sub">
            {formatCount(geo.points.count)} {geo.points.count === 1 ? "position" : "positions"}
            {geo.points.count < geo.points.total ? ` of ${formatCount(geo.points.total)} - every ${Math.round(geo.points.total / geo.points.count)}th, evenly` : ""}
            {geo.points.colour ? ` · coloured by ${log.columns.find((c) => c.key === geo.points.colour!.column)?.name ?? geo.points.colour.column}` : ""}
          </span>
        </h3>
        <MapBox height={460}>
          {(control) => (
            <GeoMap
              points={decoded?.points ?? null}
              colorData={decoded?.colour ?? null}
              definition={definition}
              onChange={setDefinition}
              noun={entries}
              describe={describe}
              notes={null}
              emptyText="No entry of the range has a position."
              fitTo={fitTo}
              {...control}
            />
          )}
        </MapBox>
      </section>

      <div className="clog-analyse-grid">
        {d && (
          <section className="panel">
            <h3>
              <IconChartHistogram size={15} stroke={1.8} /> How far from the middle{" "}
              <span className="panel-sub">
                from the median centre, {d.from.latitude.toFixed(4)}, {d.from.longitude.toFixed(4)}
              </span>
            </h3>
            <Histogram
              bins={d.histogram}
              format={formatDistance}
              height={220}
              marks={[50, 90]
                .map((rank) => ({ rank, value: d.percentiles.find((x) => x.p === rank)?.value }))
                .filter((m): m is { rank: number; value: number } => m.value != null)
                .map((m) => ({ value: m.value, label: m.rank === 50 ? "half" : "nine in ten" }))}
            />
            <div className="clog-percentiles">
              {d.percentiles.map((p) => (
                <div key={p.p} className="clog-percentile">
                  <span className="muted">{p.p}%</span>
                  <span className="clog-percentile-bar">
                    <span style={{ width: (d.max > 0 ? (p.value / d.max) * 100 : 100) + "%" }} />
                  </span>
                  <span className="clog-percentile-value">{formatDistance(p.value)}</span>
                </div>
              ))}
            </div>
            <div className="muted clog-analyse-foot">
              Each line is the share of the positions within that distance of the median centre.{" "}
              {d.beyond > 0 ? `${formatCount(d.beyond)} further out than the bars reach, the farthest ${formatDistance(d.max)} away. ` : ""}
              The median centre is the place with the least total distance to every position: a few far-off ones do not pull it away the way they pull the average.
            </div>
          </section>
        )}
        {geo.clusters && (
          <section className="panel">
            <h3>
              <IconTarget size={15} stroke={1.8} /> Where they crowd{" "}
              <span className="panel-sub">
                {geo.clusters.items.length === 0
                  ? "nowhere in particular"
                  : `${formatCount(geo.clusters.inClusters)} of ${formatCount(geo.count)} in ${geo.clusters.items.length === 1 ? "one cluster" : geo.clusters.items.length + " clusters"}`}{" "}
                · click one to list its entries
              </span>
            </h3>
            <ShareBars
              items={geo.clusters.items.map((c) => ({
                label: `${c.latitude.toFixed(4)}, ${c.longitude.toFixed(4)} · within ${formatDistance(c.radius)}`,
                count: c.count,
                hint: "List the entries within twice the spread of this cluster's middle",
                onClick: () => onShowMatching(`${property}:${c.latitude.toFixed(5)},${c.longitude.toFixed(5)}~${Math.max(10, Math.round(c.radius * 2))}m`),
              }))}
              total={geo.count}
              format={(n) => formatCount(n)}
              empty="The positions are spread out: no cells stand out from the rest."
            />
            <div className="muted clog-analyse-foot">
              A cluster is where cells of about {formatDistance(geo.clusters.cellMeters)} hold at least twice what an occupied cell holds on average, joined where they touch.
            </div>
          </section>
        )}
      </div>

      {geo.tracks && trackBy && <TracksPanel tracks={geo.tracks} columnName={log.columns.find((c) => c.key === trackBy)?.name ?? trackBy} onShowMatching={onShowMatching} trackBy={trackBy} />}
    </>
  );
}

function geoTiles(geo: GeoAnalysis): Tile[] {
  const s = geo.spread;
  const tiles: Tile[] = [];
  if (geo.median) tiles.push({ label: "Median centre", value: `${geo.median.latitude.toFixed(4)}, ${geo.median.longitude.toFixed(4)}`, hint: "the place with the least total distance to every position" });
  if (s.latitude != null && s.longitude != null) tiles.push({ label: "Mean centre", value: `${s.latitude.toFixed(4)}, ${s.longitude.toFixed(4)}`, hint: "the spherical mean: what the statistics keep" });
  if (s.standardDistance != null) tiles.push({ label: "Spread", value: formatDistance(s.standardDistance), hint: "the root mean square distance from the mean centre" });
  if (s.major != null && s.minor != null)
    tiles.push({ label: "Ellipse", value: `${formatDistance(s.major)} × ${formatDistance(s.minor)}`, hint: `one standard deviation along and across the spread, which runs ${bearingText(s.bearing)}` });
  const half = geo.distances?.percentiles.find((p) => p.p === 50)?.value;
  const most = geo.distances?.percentiles.find((p) => p.p === 90)?.value;
  if (half != null) tiles.push({ label: "Half within", value: formatDistance(half), hint: "of the median centre" });
  if (most != null) tiles.push({ label: "Nine in ten within", value: formatDistance(most) });
  if (s.south != null && s.north != null && s.west != null && s.east != null) {
    const midLat = ((s.south + s.north) / 2) * (Math.PI / 180);
    const spanLon = s.east >= s.west ? s.east - s.west : s.east + 360 - s.west;
    const tall = ((s.north - s.south) * Math.PI * 6371000) / 180;
    const wide = ((spanLon * Math.PI * 6371000) / 180) * Math.cos(midLat);
    tiles.push({ label: "Box round them", value: `${formatDistance(wide)} × ${formatDistance(tall)}`, hint: `about ${formatArea(wide * tall)}${s.east < s.west ? ", across the date line" : ""}` });
  }
  return tiles;
}

function TracksPanel({ tracks, columnName, trackBy, onShowMatching }: { tracks: NonNullable<GeoAnalysis["tracks"]>; columnName: string; trackBy: string; onShowMatching: (search: string) => void }) {
  const tiles: Tile[] = [
    { label: "Tracks", value: formatCount(tracks.tracks), hint: tracks.withoutName > 0 ? `${formatCount(tracks.withoutName)} positions had no ${columnName}` : undefined },
    { label: "Distance covered", value: formatDistance(tracks.totalDistance), hint: "every step of every track, jumps left out" },
    { label: "Time moving", value: formatMs(tracks.totalMoving * 1000) },
    { label: "Stops", value: formatCount(tracks.stops), hint: "within 100 m of one place for five minutes or more" },
    { label: "Jumps", value: formatCount(tracks.jumps), tone: tracks.jumps > 0 ? "warn" : undefined, hint: "steps faster than 300 m/s, or two places at one moment: a bad fix, left out of the distance" },
  ];
  const median = tracks.distancePercentiles.find((p) => p.p === 50)?.value;
  if (median != null) tiles.push({ label: "Median track", value: formatDistance(median) });
  return (
    <section className="panel">
      <h3>
        <IconRoute size={15} stroke={1.8} /> Tracks by {columnName}{" "}
        <span className="panel-sub">each {columnName.toLowerCase()}'s positions in time order · click one to list its entries</span>
      </h3>
      <StatTiles tiles={tiles} />
      <div className="clog-tracks">
        <div className="clog-track clog-track-head">
          <span>{columnName}</span>
          <span>Positions</span>
          <span>Distance</span>
          <span>Duration</span>
          <span>Moving</span>
          <span>Average speed</span>
          <span>Top speed</span>
          <span>Stops</span>
          <span>Jumps</span>
        </div>
        {tracks.top.map((t) => (
          <button key={t.name} className="clog-track" onClick={() => onShowMatching(`${trackBy}:${/\s/.test(t.name) ? `"${t.name}"` : t.name}`)} title={`${formatDateTime(t.firstUtc)} — ${formatDateTime(t.lastUtc)}`}>
            <span className="clog-track-name">{t.name}</span>
            <span>{formatCount(t.points)}</span>
            <span>{formatDistance(t.distance)}</span>
            <span>{formatMs(t.duration * 1000)}</span>
            <span>{formatMs(t.moving * 1000)}</span>
            <span>{t.averageSpeed > 0 ? formatSpeed(t.averageSpeed) : "—"}</span>
            <span>{t.topSpeed > 0 ? formatSpeed(t.topSpeed) : "—"}</span>
            <span>{t.stops > 0 ? `${t.stops} · ${formatMs(t.stopped * 1000)}` : "—"}</span>
            <span className={t.jumps > 0 ? "logs-warn" : ""}>{t.jumps || "—"}</span>
          </button>
        ))}
      </div>
      {tracks.tracks > tracks.top.length && <div className="muted clog-analyse-foot">The {tracks.top.length} that went furthest of {formatCount(tracks.tracks)}.</div>}
    </section>
  );
}

/** The positions of an analysis as points for the map, with their times and - when asked - their colour groups. */
function decodePoints(geo: GeoAnalysis): { points: MapPoints; times: Int32Array; colour: MapProperty | null } | null {
  const n = geo.points.count;
  if (n === 0) return null;
  const bytes = bytesOf(geo.points.coordinates);
  const packed = new Int32Array(bytes.buffer, bytes.byteOffset, n * 2);
  const timeBytes = bytesOf(geo.points.times);
  const times = new Int32Array(timeBytes.buffer, timeBytes.byteOffset, n);
  const lat = new Float32Array(n);
  const lon = new Float32Array(n);
  const ids = new Int32Array(n);
  for (let i = 0; i < n; i++) {
    lat[i] = packed[i * 2] / 1e7;
    lon[i] = packed[i * 2 + 1] / 1e7;
    ids[i] = i;
  }
  let colour: MapProperty | null = null;
  const c = geo.points.colour;
  if (c) {
    const assignmentBytes = bytesOf(c.assignment);
    const assignment = new Uint16Array(assignmentBytes.buffer.slice(assignmentBytes.byteOffset, assignmentBytes.byteOffset + n * 2));
    const taken = new Set<number>();
    const groups: MapGroup[] = c.groups.map((g) => ({
      label: g.label,
      value: g.kind === "value" ? g.label : null,
      value2: null,
      count: g.count,
      kind: g.kind,
      // the same colour a value has everywhere else on the page: slotted by its text
      ordinal: g.kind === "value" ? paletteSlot(g.label + "|", taken, 512) : -1,
    }));
    colour = { propertyId: c.column, name: c.column, groups, assignment };
  }
  return { points: { count: n, total: geo.points.total, read: geo.points.total, ids, lat, lon, byProperty: new Map() }, times, colour };
}
