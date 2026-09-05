import { formatCount } from "../format";

// The basic chart of the query page's summaries: horizontal bars, one band per group, one bar per
// series inside it. Plain HTML rather than SVG - a label that truncates, a bar that is a click
// target and a tooltip that is the browser's own all come for free that way, and nothing here needs
// a curve. Bars run horizontally because group labels are words (a product name, a month) and
// words read best on a line of their own.
//
// Colour tells series apart and nothing else: a single series is one colour throughout, and a bar's
// length is never repeated as a shade. Series beyond the eight the palette has are the caller's to
// fold or refuse - a ninth colour would not be distinguishable from one already on screen.

export interface BarSeries {
  name: string;
  /** One per category; null is no value (no bar, not a zero). */
  values: (number | null)[];
}

/** The most series the palette can tell apart; callers cap or refuse beyond it. */
export const maxSeries = 8;

/** Numbers as the chart writes them: whole ones as counts, others with a little precision. */
export function formatMeasure(value: number): string {
  if (Number.isInteger(value)) return formatCount(value);
  return value.toLocaleString(undefined, { maximumFractionDigits: Math.abs(value) < 10 ? 2 : 1 });
}

/** Clean axis steps: 1, 2, 5 × a power of ten, about five of them across the range. */
function ticks(lo: number, hi: number): number[] {
  const range = hi - lo || 1;
  const rough = range / 5;
  const power = Math.pow(10, Math.floor(Math.log10(rough)));
  const step = [1, 2, 5, 10].map((m) => m * power).find((s) => s >= rough) ?? power * 10;
  const out: number[] = [];
  for (let t = Math.ceil(lo / step) * step; t <= hi + step / 1000; t += step) out.push(Math.abs(t) < step / 1000 ? 0 : t);
  return out;
}

export function BarChart({
  categories,
  series,
  format = formatMeasure,
  onBarClick,
  barTitle,
}: {
  categories: string[];
  series: BarSeries[];
  format?: (value: number) => string;
  onBarClick?: (category: number, series: number) => void;
  /** The tooltip of one bar; the default names the category, the series and the value. */
  barTitle?: (category: number, series: number, value: number) => string;
}) {
  const values = series.flatMap((s) => s.values).filter((v): v is number => v !== null && Number.isFinite(v));
  if (categories.length === 0 || values.length === 0) return <div className="query-empty">Nothing to draw.</div>;
  // the axis always includes zero: a bar grows from a baseline, and a range that starts at the
  // smallest value would draw the smallest group as nothing at all
  const lo = Math.min(0, ...values);
  const hi = Math.max(0, ...values);
  const axis = ticks(lo, hi);
  const span = (Math.max(hi, axis[axis.length - 1] ?? hi) - Math.min(lo, axis[0] ?? lo)) || 1;
  const start = Math.min(lo, axis[0] ?? lo);
  const pct = (v: number) => ((v - start) / span) * 100;
  const single = series.length === 1;
  const thickness = single ? 18 : series.length <= 3 ? 12 : 8;
  return (
    <div className="bar-chart" style={{ "--bar-thickness": thickness + "px" } as React.CSSProperties}>
      {!single && (
        <div className="bar-legend">
          {series.map((s, i) => (
            <span key={i} className="bar-legend-item" title={s.name}>
              <span className="bar-swatch" style={{ background: `var(--viz-${(i % maxSeries) + 1})` }} />
              {s.name}
            </span>
          ))}
        </div>
      )}
      <div className="bar-axis">
        <span className="bar-label" />
        <div className="bar-area">
          {axis.map((t) => (
            <span key={t} className="bar-tick" style={{ left: pct(t) + "%" }}>
              {format(t)}
            </span>
          ))}
        </div>
      </div>
      <div className="bar-rows">
        {categories.map((label, c) => (
          <div className="bar-row" key={c}>
            <span className="bar-label" title={label}>
              {label}
            </span>
            <div className="bar-area">
              {axis.map((t) => (
                <span key={t} className={"bar-grid" + (t === 0 ? " zero" : "")} style={{ left: pct(t) + "%" }} />
              ))}
              {series.map((s, i) => {
                const v = s.values[c];
                if (v === null || v === undefined || !Number.isFinite(v)) return <span key={i} className="bar-slot" />;
                const left = pct(Math.min(0, v));
                const width = Math.abs(pct(v) - pct(0));
                const title = barTitle ? barTitle(c, i, v) : (single ? label : label + " · " + s.name) + ": " + format(v);
                return (
                  <span key={i} className="bar-slot">
                    <button
                      className={"bar" + (v < 0 ? " negative" : "")}
                      style={{ left: left + "%", width: width + "%", background: `var(--viz-${(i % maxSeries) + 1})` }}
                      title={title}
                      onClick={onBarClick ? () => onBarClick(c, i) : undefined}
                      tabIndex={onBarClick ? 0 : -1}
                    />
                    {single && (
                      // one series: the value at the tip is the label the legend would otherwise be
                      <span className={"bar-value" + (v < 0 ? " negative" : "")} style={v < 0 ? { right: 100 - left + "%" } : { left: left + width + "%" }}>
                        {format(v)}
                      </span>
                    )}
                  </span>
                );
              })}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
