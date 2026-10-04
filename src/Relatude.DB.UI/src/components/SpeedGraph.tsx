import { useRef, useState, type PointerEvent } from "react";
import type { ProgressSpeed } from "../dialogs";
import { formatBytes } from "../format";

// viewBox units: the svg is stretched to the dialog's width, and the lines keep their own width by
// not scaling their stroke
const width = 400;
const height = 40;

/**
 * The speed of a transfer, under its progress bar: the rate now as a figure, and the run so far as a
 * small area chart whose x axis is the transfer itself - the same width as the bar above it, filling
 * in as the bar fills, the way a file copy in the operating system draws it. The shape says what one
 * number cannot: whether it is steady, climbing, or stalling on some of the files. Pointing at the
 * chart reads out the part under the pointer.
 */
export function SpeedGraph({ speed }: { speed: ProgressSpeed }) {
  const [hover, setHover] = useState<number | null>(null); // the column under the pointer
  const svg = useRef<SVGSVGElement>(null);
  const { rates, reached } = speed;
  const columns = rates.length;
  const peak = rates.reduce<number>((max, rate) => Math.max(max, rate ?? 0), 0);
  const top = Math.max(peak, speed.now, 1) * 1.15; // headroom, so the peak is not flattened against the edge
  const x = (fraction: number) => Math.max(0, Math.min(1, fraction)) * width;
  const y = (rate: number) => height - 1 - (rate / top) * (height - 2);
  // a point at the middle of every column reached, the first one carried back to the left edge and
  // the last one on to where the transfer is now
  const points: [number, number][] = [];
  rates.forEach((rate, i) => {
    if (rate !== null) points.push([x(Math.min((i + 0.5) / columns, reached)), y(rate)]);
  });
  if (points.length > 0) {
    points.unshift([0, points[0][1]]);
    points.push([x(reached), points[points.length - 1][1]]);
  }
  const line = points.map(([px, py], i) => `${i === 0 ? "M" : "L"}${px.toFixed(1)},${py.toFixed(1)}`).join("");
  const area = points.length > 0 ? `${line}L${points[points.length - 1][0].toFixed(1)},${height}L0,${height}Z` : "";
  const shown = hover !== null ? rates[hover] : null;

  function onPointerMove(e: PointerEvent<SVGSVGElement>) {
    const box = svg.current?.getBoundingClientRect();
    if (!box || box.width === 0) return;
    const fraction = (e.clientX - box.left) / box.width;
    const column = Math.floor(fraction * columns);
    setHover(fraction >= 0 && column < columns && rates[column] !== null ? column : null);
  }

  return (
    <div className="speed">
      <div className="speed-figures">
        <span className="speed-now">{formatBytes(shown ?? speed.now)}/s</span>
        <span className="muted">
          {hover !== null && shown !== null
            ? `from ${Math.round((hover / columns) * 100)} to ${Math.round(((hover + 1) / columns) * 100)} %`
            : `now · average ${formatBytes(speed.average)}/s` + (peak > 0 ? ` · peak ${formatBytes(peak)}/s` : "")}
        </span>
      </div>
      <svg
        ref={svg}
        className="speed-graph"
        viewBox={`0 0 ${width} ${height}`}
        preserveAspectRatio="none"
        role="img"
        aria-label={`Transfer speed: now ${formatBytes(speed.now)}/s, average ${formatBytes(speed.average)}/s, peak ${formatBytes(peak)}/s`}
        onPointerMove={onPointerMove}
        onPointerLeave={() => setHover(null)}
      >
        <line className="speed-base" x1={0} x2={width} y1={height - 0.5} y2={height - 0.5} vectorEffect="non-scaling-stroke" />
        {area && <path className="speed-area" d={area} />}
        {line && <path className="speed-line" d={line} vectorEffect="non-scaling-stroke" />}
        {hover !== null && shown !== null && (
          <line className="speed-cursor" x1={x((hover + 0.5) / columns)} x2={x((hover + 0.5) / columns)} y1={0} y2={height} vectorEffect="non-scaling-stroke" />
        )}
      </svg>
    </div>
  );
}
