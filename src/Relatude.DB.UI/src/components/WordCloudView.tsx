import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { IconCloud, IconDownload, IconTable } from "@tabler/icons-react";
import { fetchPivotModel, runCloud, type CloudRequest, type CloudResult, type CloudWord, type PivotModel } from "../server/query";
import { useLiveResult } from "../server/hooks";
import { formatCount, formatQuery } from "../format";
import type { PivotBase } from "./PivotView";
import type { CloudDefinition } from "../queryTabs";
import { buildRamp, palettes, parseCssColor, paletteSlot, buildPalette, type RGB } from "../visual/palette";
import { layoutCloud, type CloudLayout } from "../cloud/layout";
import { downloadSvg } from "../svgExport";
import { CopyButton } from "./CopyButton";

/** A cloud before anyone has touched it: the commonest words of the first text property there is. */
export const emptyCloud: CloudDefinition = {
  property: null,
  weigh: "documents",
  maxWords: 120,
  minDocuments: 1,
  minWordLength: 0,
  ignore: [],
  palette: palettes[0].id,
  colorBy: "weight",
  shape: "cloud",
  contrast: 5,
  upright: false,
};

const weighings: { id: CloudDefinition["weigh"]; label: string; hint: string }[] = [
  { id: "documents", label: "Nodes", hint: "How many nodes of the result hold the word — the plainest reading, and the one that matches what clicking it finds" },
  { id: "occurrences", label: "Uses", hint: "How many times the word appears in all, counting repeats within a node" },
  { id: "distinctive", label: "Distinctive", hint: "How much the word belongs to THIS result rather than to the type as a whole — the words everything is written with shrink away" },
];

/** The smallest and largest a word is drawn, against the contrast slider. */
const smallestFont = 11;
const largestFont = (contrast: number) => 20 + contrast * 12;
const rampSteps = 24;

interface Theme {
  panel: RGB;
  accent: RGB;
}

function readTheme(el: HTMLElement): Theme {
  const cs = getComputedStyle(el);
  const v = (name: string, fallback: string) => parseCssColor(cs.getPropertyValue(name).trim() || fallback);
  return { panel: v("--stage", "#ffffff"), accent: v("--accent", "#0960b2") };
}

const css = (c: RGB) => "rgb(" + c[0] + "," + c[1] + "," + c[2] + ")";

/**
 * What a word weighs, by the reading the definition asks for.
 *
 * "Distinctive" is the textbook inverse document frequency: how many nodes here hold the word,
 * against how rare the word is in the database as a whole. Without it a cloud of anything written
 * in one language is a cloud of that language - the same handful of workaday words on top of every
 * query - because a count alone says nothing about what makes this result different from the rest.
 *
 * The log is taken of N/df rather than of 1 + N/df, which matters more than it looks: with the 1 a
 * word held by every single node still scores log 2 per node and wins on sheer count, which is
 * exactly the boilerplate this is meant to push down. Without it that word scores nothing and drops
 * out, and what floats up is what this result has and the rest of the database does not.
 */
function weightOf(word: CloudWord, weigh: CloudDefinition["weigh"], indexDocuments: number): number {
  if (weigh === "occurrences") return word.occurrences;
  if (weigh === "distinctive") {
    const inIndex = Math.max(1, word.documentsInIndex);
    return word.documents * Math.max(0, Math.log(Math.max(1, indexDocuments) / inIndex));
  }
  return word.documents;
}

/**
 * The word cloud view of the query page: the words the result set is written with, sized by how
 * much of it they account for.
 *
 * The words are read out of the property's word index rather than out of the nodes, so no node is
 * touched however large the result - and every word here is one a search would find, which is what
 * makes clicking one to narrow the query mean something. Not every text index can be read that way;
 * the page only offers this view for the properties the server says can (PivotProperty.words).
 */
export function WordCloudView({
  base,
  definition,
  onChange,
  refreshToken,
  showQuery,
  onWord,
  head,
}: {
  base: PivotBase;
  /** The definition as the page keeps it - null until this view has opened once for the type. */
  definition: CloudDefinition | null;
  onChange: (definition: CloudDefinition) => void;
  /** Changes when the page is asked to run again with nothing else changed. */
  refreshToken: number;
  showQuery: boolean;
  /** A word was clicked: search the result for it. */
  onWord: (word: string) => void;
  /** What the result's own head would say, when the page has folded that head away. */
  head?: React.ReactNode;
}) {
  const [model, setModel] = useState<PivotModel | null>(null);
  const [modelError, setModelError] = useState<string | null>(null);
  const def = definition ?? emptyCloud;
  const set = (patch: Partial<CloudDefinition>) => onChange({ ...def, ...patch });

  useEffect(() => {
    let cancelled = false;
    setModel(null);
    setModelError(null);
    fetchPivotModel(base.storeId, base.typeId)
      .then((m) => !cancelled && setModel(m))
      .catch((e) => !cancelled && setModelError(e instanceof Error ? e.message : String(e)));
    return () => {
      cancelled = true;
    };
  }, [base.storeId, base.typeId]);

  const texts = useMemo(() => model?.properties.filter((p) => p.words) ?? [], [model]);

  // the first time the view opens for a type it reads the first countable text property it finds,
  // so there is a cloud before anyone chooses; the choice is then the query's own
  useEffect(() => {
    if (model && definition === null) onChange({ ...emptyCloud, property: texts[0]?.id ?? null });
  }, [model, definition, texts, onChange]);

  const property = texts.some((p) => p.id === def.property) ? def.property : (texts[0]?.id ?? null);

  const request = useMemo<CloudRequest | null>(() => {
    if (model === null || definition === null || property === null) return null;
    return {
      storeId: base.storeId,
      typeId: base.typeId,
      text: base.text,
      semanticRatio: base.semanticRatio,
      minimumSimilarity: base.minimumSimilarity,
      selections: base.selections,
      propertyId: property,
      maxWords: def.maxWords,
      minDocuments: def.minDocuments,
      minWordLength: def.minWordLength,
      ignore: def.ignore,
    };
    // the token is not part of the request; a new object is how the runner is told to run again
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [model, definition === null, base, property, def.maxWords, def.minDocuments, def.minWordLength, def.ignore, refreshToken]);
  const { result, loading, error } = useLiveResult(request, runCloud);

  // ---- drawing it ----

  const stageRef = useRef<HTMLDivElement>(null);
  const svgRef = useRef<SVGSVGElement>(null);
  const [size, setSize] = useState({ width: 0, height: 0 });
  const [theme, setTheme] = useState<Theme | null>(null);
  const [hovered, setHovered] = useState<string | null>(null);

  useLayoutEffect(() => {
    const el = stageRef.current;
    if (!el) return;
    const measure = () => setSize((was) => (was.width === el.clientWidth && was.height === el.clientHeight ? was : { width: el.clientWidth, height: el.clientHeight }));
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  // the page's colours, read from the stylesheet so the cloud follows the theme - including the
  // switch, which changes an attribute on the document rather than anything this view can see
  useLayoutEffect(() => {
    const el = stageRef.current;
    if (!el) return;
    const read = () => setTheme(readTheme(el));
    read();
    const observer = new MutationObserver(read);
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    return () => observer.disconnect();
  }, [size.width]);

  const weighted = useMemo(() => {
    if (!result) return [];
    return result.words
      .map((w) => ({ word: w, weight: weightOf(w, def.weigh, result.indexDocuments) }))
      .filter((w) => w.weight > 0)
      .sort((a, b) => b.weight - a.weight || a.word.word.localeCompare(b.word.word));
  }, [result, def.weigh]);

  const layout = useMemo<CloudLayout | null>(() => {
    if (weighted.length === 0 || size.width < 40 || size.height < 40 || def.shape === "bars") return null;
    const canvas = document.createElement("canvas");
    const ctx = canvas.getContext("2d");
    if (!ctx) return null;
    // the same family the page is set in, so what is measured is what gets drawn
    const family = getComputedStyle(document.body).fontFamily || "sans-serif";
    const measure = (text: string, fontSize: number) => {
      ctx.font = "600 " + fontSize + "px " + family;
      const m = ctx.measureText(text);
      return { width: m.width, height: fontSize * 1.02 };
    };
    return layoutCloud(
      weighted.map((w) => ({ text: w.word.word, weight: w.weight })),
      {
        width: size.width,
        height: size.height,
        minFontSize: smallestFont,
        maxFontSize: largestFont(def.contrast ?? 5),
        upright: def.upright === true,
        measure,
      },
    );
  }, [weighted, size.width, size.height, def.contrast, def.upright, def.shape]);

  const colors = useMemo(() => {
    if (theme === null) return null;
    return {
      ramp: buildRamp(rampSteps, theme.panel, theme.accent, def.palette),
      wheel: buildPalette(512, theme.panel, theme.accent, def.palette),
    };
  }, [theme, def.palette]);

  function colorFor(text: string, weight: number, max: number): string {
    if (colors === null) return "currentColor";
    if (def.colorBy === "word") return css(colors.wheel[paletteSlot(text, new Set(), colors.wheel.length)].rgb);
    // by weight, along the ramp: heavier words darker, by square root so the tail is still coloured
    const t = max <= 0 ? 1 : Math.sqrt(weight / max);
    return css(colors.ramp[Math.min(rampSteps - 1, Math.max(0, Math.round(t * (rampSteps - 1))))].rgb);
  }

  if (modelError) return <div className="query-error">{modelError}</div>;
  // Deliberately no early return while the model loads: the stage below has to be in the document
  // from the first render, because that is when its ResizeObserver attaches. Rendering nothing
  // until the model arrives would leave the observer looking at an element that never existed, and
  // the layout would never learn how big it is.

  const maxWeight = weighted.length > 0 ? weighted[0].weight : 0;
  const bars = def.shape === "bars";
  const countLabel = (w: CloudWord) =>
    def.weigh === "occurrences" ? formatCount(w.occurrences) + " uses" : formatCount(w.documents) + (w.documents === 1 ? " node" : " nodes");
  const title = (w: CloudWord) =>
    w.word +
    " — " +
    formatCount(w.documents) +
    (w.documents === 1 ? " node of this result" : " nodes of this result") +
    ", " +
    formatCount(w.occurrences) +
    " in all; " +
    formatCount(w.documentsInIndex) +
    " in the whole type. Click to search for it.";

  return (
    <div className="cloud">
      <div className="pivot-builder">
        <div className="pivot-builder-row">
          <span className="pivot-builder-label">Words of</span>
          <span className="pivot-chip">
            <select
              className="select"
              value={property ?? ""}
              title="The text property the words are read from"
              disabled={texts.length === 0}
              onChange={(e) => set({ property: e.target.value })}
            >
              {texts.length === 0 && <option value="">(no countable text)</option>}
              {texts.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          </span>
          <span className="pivot-chip">
            <span className="muted">sized by</span>
            <select className="select" value={def.weigh} title={weighings.find((w) => w.id === def.weigh)?.hint} onChange={(e) => set({ weigh: e.target.value as CloudDefinition["weigh"] })}>
              {weighings.map((w) => (
                <option key={w.id} value={w.id} title={w.hint}>
                  {w.label}
                </option>
              ))}
            </select>
          </span>
          <div className="pivot-options">
            <label title="How many words the cloud draws at most">
              words
              <input
                className="text-input pivot-number"
                type="number"
                min={5}
                max={500}
                step={5}
                value={def.maxWords}
                onChange={(e) => set({ maxWords: Math.max(5, Math.min(500, Number(e.target.value) || 5)) })}
              />
            </label>
            <label title="A word held by fewer nodes than this is left out">
              in ≥
              <input
                className="text-input pivot-number"
                type="number"
                min={1}
                step={1}
                value={def.minDocuments}
                onChange={(e) => set({ minDocuments: Math.max(1, Number(e.target.value) || 1) })}
              />
            </label>
            <label title="A word shorter than this is left out. The index has its own minimum already; this only raises it.">
              length ≥
              <input
                className="text-input pivot-number"
                type="number"
                min={0}
                max={30}
                step={1}
                value={def.minWordLength}
                onChange={(e) => set({ minWordLength: Math.max(0, Math.min(30, Number(e.target.value) || 0)) })}
              />
            </label>
          </div>
        </div>
        <div className="pivot-builder-row">
          <span className="pivot-builder-label">Ignore</span>
          <input
            className="text-input cloud-ignore"
            type="text"
            placeholder="words to leave out, separated by spaces"
            title="Words to leave out whatever their count — markup and boilerplate the index holds too. Compared as the index holds them: lowercase."
            defaultValue={def.ignore.join(" ")}
            key={(definition === null ? "none" : "set") + ":" + base.typeId}
            onBlur={(e) => {
              const next = e.target.value.split(/[\s,]+/).map((w) => w.trim().toLowerCase()).filter((w) => w.length > 0);
              if (next.join(" ") !== def.ignore.join(" ")) set({ ignore: next });
            }}
            onKeyDown={(e) => {
              if (e.key === "Enter") (e.target as HTMLInputElement).blur();
            }}
          />
          <div className="pivot-options">
            <select className="select compact" value={def.palette ?? palettes[0].id} title="The colours the words are drawn in" onChange={(e) => set({ palette: e.target.value })}>
              {palettes.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
            <select className="select compact" value={def.colorBy ?? "weight"} title="What decides a word's colour" onChange={(e) => set({ colorBy: e.target.value as "weight" | "word" })}>
              <option value="weight">by weight</option>
              <option value="word">by word</option>
            </select>
            <label title="How far the largest word outgrows the smallest">
              contrast
              <input type="range" min={1} max={10} step={1} value={def.contrast ?? 5} onChange={(e) => set({ contrast: Number(e.target.value) })} />
            </label>
            <label className="settings-check" title="Keep every word level, rather than turning some of them on their side">
              <input type="checkbox" checked={def.upright === true} onChange={(e) => set({ upright: e.target.checked })} />
              upright
            </label>
          </div>
        </div>
      </div>

      {showQuery && result && result.query && <div className="query-string">{formatQuery(result.query)}</div>}
      {error && <div className="query-error">{error}</div>}
      {model !== null && texts.length === 0 && !error && (
        <div className="query-empty">
          No text to count here. A word cloud reads the words out of a property's word index, which needs a string property indexed by words — and a text index that can list what it holds, which the
          memory and native ones can and Lucene and SQLite cannot.
        </div>
      )}

      {result && (
        <div className="pivot-head">
          {head}
          <div className="query-spacer" />
          <span className="muted">
            {formatCount(result.words.length)} of {formatCount(result.distinctWords)} words
            {result.truncated && " · partial: the count reached its budget before the end of the index"}
            {layout && layout.dropped > 0 && " · " + formatCount(layout.dropped) + " did not fit"}
          </span>
          <div className="query-view" role="tablist">
            <button className={bars ? "" : "active"} title="The words as a cloud" onClick={() => set({ shape: "cloud" })}>
              <IconCloud size={14} stroke={1.8} />
              Cloud
            </button>
            <button className={bars ? "active" : ""} title="The words as a list with their counts" onClick={() => set({ shape: "bars" })}>
              <IconTable size={14} stroke={1.8} />
              List
            </button>
          </div>
          <CopyButton
            title="Copy these words to the clipboard"
            disabled={result.words.length === 0}
            table={() => ({
              header: ["Word", "Nodes", "Uses", "Nodes in type"],
              rows: result.words.map((w) => [w.word, String(w.documents), String(w.occurrences), String(w.documentsInIndex)]),
            })}
          />
          <button
            className="icon-button"
            title="Download the cloud as an svg"
            disabled={bars || !layout || layout.words.length === 0}
            onClick={() => svgRef.current && downloadSvg(svgRef.current, "word-cloud", "Word cloud of " + result.typeName + "." + result.propertyName)}
          >
            <IconDownload size={16} stroke={1.8} />
          </button>
        </div>
      )}

      <div className={"cloud-stage" + (loading ? " loading" : "")} ref={stageRef}>
        {bars && result && (
          <div className="cloud-list">
            {weighted.map(({ word, weight }) => (
              <button key={word.word} className="cloud-bar" title={title(word)} onClick={() => onWord(word.word)}>
                <span className="cloud-bar-fill" style={{ width: (maxWeight <= 0 ? 0 : (weight / maxWeight) * 100) + "%", background: colorFor(word.word, weight, maxWeight) }} />
                <span className="cloud-bar-word">{word.word}</span>
                <span className="cloud-bar-count muted">{countLabel(word)}</span>
              </button>
            ))}
          </div>
        )}
        {!bars && layout && layout.words.length > 0 && (
          <svg
            ref={svgRef}
            className="cloud-svg"
            width={size.width}
            height={size.height}
            viewBox={layout.box.x + " " + layout.box.y + " " + layout.box.width + " " + layout.box.height}
            preserveAspectRatio="xMidYMid meet"
            role="img"
          >
            {layout.words.map((w) => (
              <text
                key={w.text}
                x={w.x}
                y={w.y}
                fontSize={w.fontSize}
                fontWeight={600}
                textAnchor="middle"
                dominantBaseline="central"
                fill={colorFor(w.text, w.weight, maxWeight)}
                opacity={hovered === null || hovered === w.text ? 1 : 0.35}
                transform={w.rotated ? "rotate(-90 " + w.x + " " + w.y + ")" : undefined}
                style={{ cursor: "pointer" }}
                onMouseEnter={() => setHovered(w.text)}
                onMouseLeave={() => setHovered(null)}
                onClick={() => onWord(w.text)}
              >
                <title>{title(weighted.find((x) => x.word.word === w.text)?.word ?? { word: w.text, documents: 0, occurrences: 0, documentsInIndex: 0 })}</title>
                {w.text}
              </text>
            ))}
          </svg>
        )}
        {!bars && result && weighted.length === 0 && !loading && <div className="query-empty">No words: nothing in this result has any text in that property.</div>}
      </div>
    </div>
  );
}

/** What the head says while the cloud is the view: how many nodes the words were counted over. */
export function cloudSummary(result: CloudResult): string {
  return formatCount(result.total) + (result.total === 1 ? " node" : " nodes") + " · " + Math.round(result.durationMs) + " ms";
}
