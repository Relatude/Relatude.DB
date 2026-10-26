import { useEffect, useMemo, useRef, useState } from "react";
import { IconCode, IconLoader2, IconPhotoOff, IconSearch, IconX } from "@tabler/icons-react";
import { DialogTools } from "./DialogTools";
import type { EditorContext } from "./DatamodelEditors";
import { TypeIcon, modelIcon } from "./TypeIcon";
import { checkSvg, iconSet, iconSets, iconSvg, isSvgSource, splitIconName, usesCurrentColor, type IconSetInfo, type IconSetJson } from "../server/icons";

/** The icon a type is shown with in the datamodel's lists: its own, overrides included, else the one it inherits; nothing when it has none. */
export function ShownIcon({ ctx, typeId, size = 15 }: { ctx: EditorContext; typeId: string; size?: number }) {
  const icon = modelIcon(ctx.model, typeId);
  if (!icon) return null;
  return <TypeIcon icon={icon} size={size} className="dm-type-icon" />;
}

/**
 * The Icon field of a node type: what it is shown with, the button to the picker, and what it shows while it
 * has none of its own - the icon of the closest type it inherits from, which the field's chips name too.
 */
export function IconField({ value, inherited, disabled, onChange }: { value: string | null; inherited: string | null; disabled: boolean; onChange: (value: string | null) => void }) {
  const [picking, setPicking] = useState(false);
  const shown = value || inherited;
  const label = !value ? (inherited ? "Inherited" : "None") : isSvgSource(value) ? "Own SVG" : value;
  return (
    <span className={"icon-field" + (disabled ? " off" : "")}>
      <button type="button" className={"icon-field-current" + (value ? "" : " is-inherited")} disabled={disabled} onClick={() => setPicking(true)} title={disabled ? undefined : "Choose an icon"}>
        {shown ? <TypeIcon icon={shown} size={22} /> : <IconPhotoOff size={18} stroke={1.6} className="muted" />}
      </button>
      <span className={"icon-field-name" + (value ? "" : " muted")}>{label}</span>
      {!disabled && (
        <button type="button" className="action-button dm-button" onClick={() => setPicking(true)}>
          Choose…
        </button>
      )}
      {!disabled && value && (
        <button type="button" className="icon-button" title={inherited ? "Remove the icon: the type shows the one it inherits" : "Remove the icon"} onClick={() => onChange(null)}>
          <IconX size={15} stroke={1.9} />
        </button>
      )}
      {picking && (
        <IconPicker
          value={value}
          onClose={() => setPicking(false)}
          onChoose={(v) => {
            setPicking(false);
            onChange(v);
          }}
        />
      )}
    </span>
  );
}

interface Hit {
  prefix: string;
  name: string;
  svg: string;
}

const page = 360;

/** How well an icon's name matches the search: lower is better, null for no match. Every word must be in the name. */
function rank(name: string, words: string[], whole: string): number | null {
  if (!words.every((w) => name.includes(w))) return null;
  if (name === whole) return 0;
  if (name.startsWith(whole)) return 1;
  if (name.split("-").some((part) => words.some((w) => part === w))) return 2;
  return 3;
}

/** The icons of a set the search finds: by name, and by the other names (aliases) an icon goes by, best matches first. */
function search(set: IconSetJson, query: string, category: string | null): Hit[] {
  const whole = query.trim().toLowerCase().replace(/\s+/g, "-");
  const words = query.toLowerCase().split(/[\s,-]+/).filter(Boolean);
  const inCategory = category ? new Set(set.categories?.[category] ?? []) : null;
  const best = new Map<string, number>();
  const consider = (name: string, score: number | null) => {
    if (score === null) return;
    const icon = set.icons[name];
    if (!icon || icon.hidden || (inCategory && !inCategory.has(name))) return;
    const was = best.get(name);
    if (was === undefined || score < was) best.set(name, score);
  };
  for (const name in set.icons) consider(name, words.length ? rank(name, words, whole) : 3);
  if (words.length && set.aliases) {
    for (const alias in set.aliases) {
      const score = rank(alias, words, whole);
      if (score === null) continue;
      let parent = set.aliases[alias].parent;
      for (let depth = 0; depth < 8 && !set.icons[parent] && set.aliases[parent]; depth++) parent = set.aliases[parent].parent;
      consider(parent, score + 0.5);
    }
  }
  const names = [...best.keys()];
  if (words.length) names.sort((a, b) => best.get(a)! - best.get(b)! || a.length - b.length || a.localeCompare(b));
  return names.map((name) => ({ prefix: set.prefix, name, svg: iconSvg(set, name) ?? "" }));
}

/**
 * Picks a node type's icon. The library's sets - monochrome ones, drawn in the colour of the text around them,
 * and coloured ones - are searched by name, one set at a time or all at once, and some have categories to
 * browse; or the source of an SVG image of one's own goes in on the second tab, checked as it is typed by the
 * rules the server activates by. A double click on an icon takes it.
 */
export function IconPicker({ value, onClose, onChoose }: { value: string | null; onClose: () => void; onChoose: (value: string | null) => void }) {
  const named = value ? splitIconName(value) : null;
  const [tab, setTab] = useState<"library" | "svg">(value && isSvgSource(value) ? "svg" : "library");
  const [catalog, setCatalog] = useState<IconSetInfo[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [prefix, setPrefix] = useState<string>(named?.[0] ?? "lucide");
  const [query, setQuery] = useState("");
  const [category, setCategory] = useState<string | null>(null);
  const [loaded, setLoaded] = useState<Record<string, IconSetJson>>({});
  const [limit, setLimit] = useState(page);
  const [chosen, setChosen] = useState<string | null>(named ? value : null);
  const [source, setSource] = useState(value && isSvgSource(value) ? value : "");
  const gridRef = useRef<HTMLDivElement>(null);
  const moreRef = useRef<HTMLDivElement>(null);
  const searchRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    iconSets().then(setCatalog, (e: Error) => setError(e.message));
  }, []);

  // the sets in view, downloaded as they are needed: all of them for a search of every set
  const wanted = useMemo(() => (prefix === "*" ? (query.trim() ? (catalog ?? []).map((s) => s.prefix) : []) : [prefix]), [prefix, query, catalog]);
  useEffect(() => {
    let live = true;
    for (const p of wanted) {
      if (loaded[p]) continue;
      iconSet(p).then(
        (set) => live && setLoaded((all) => ({ ...all, [p]: set })),
        (e: Error) => live && setError(e.message),
      );
    }
    return () => {
      live = false;
    };
  }, [wanted, loaded]);

  const busy = wanted.some((p) => !loaded[p]);
  const hits = useMemo(() => {
    if (busy) return [];
    return wanted.flatMap((p) => search(loaded[p], query, prefix === "*" ? null : category));
  }, [busy, wanted, loaded, query, category, prefix]);

  // a new search or set starts at the top
  useEffect(() => {
    setLimit(page);
    gridRef.current?.scrollTo({ top: 0 });
  }, [prefix, query, category]);

  // more icons as the list is scrolled to its end
  useEffect(() => {
    const more = moreRef.current;
    if (!more) return;
    const observer = new IntersectionObserver(([e]) => e.isIntersecting && setLimit((l) => l + page), { root: gridRef.current, rootMargin: "300px" });
    observer.observe(more);
    return () => observer.disconnect();
  }, [hits]);

  // the icon the type has is in view when the picker opens on it
  const scrolledTo = useRef(false);
  useEffect(() => {
    if (scrolledTo.current || !named || busy) return;
    const index = hits.findIndex((h) => h.prefix === named[0] && h.name === named[1]);
    if (index < 0) return;
    scrolledTo.current = true;
    if (index >= limit) setLimit(index + page);
    requestAnimationFrame(() => gridRef.current?.querySelector(".icon-tile.is-chosen")?.scrollIntoView({ block: "center" }));
  }, [hits, busy, named, limit]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== "Escape") return;
      e.stopPropagation();
      onClose();
    };
    document.addEventListener("keydown", onKey, true);
    return () => document.removeEventListener("keydown", onKey, true);
  }, [onClose]);

  const set = catalog?.find((s) => s.prefix === prefix);
  const categories = prefix !== "*" ? Object.keys(loaded[prefix]?.categories ?? {}) : [];
  const chosenParts = chosen ? splitIconName(chosen) : null;
  const chosenSet = chosenParts ? catalog?.find((s) => s.prefix === chosenParts[0]) : null;
  const svgProblem = source.trim() ? checkSvg(source) : null;
  const total = catalog?.reduce((sum, s) => sum + s.total, 0) ?? 0;

  const chip = (s: IconSetInfo) => (
    <button key={s.prefix} type="button" className={"chip" + (prefix === s.prefix ? " on" : "")} onClick={() => { setPrefix(s.prefix); setCategory(null); }} title={`${s.total.toLocaleString()} icons by ${s.author ?? "?"}, ${s.license ?? "?"} licence`}>
      {s.name}
    </button>
  );

  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="dialog icon-picker" role="dialog" aria-label="Choose an icon">
        <h3>
          Choose an icon
          <span className="icon-picker-tabs">
            <button type="button" className={"chip" + (tab === "library" ? " on" : "")} onClick={() => setTab("library")}>
              <IconSearch size={13} stroke={2} /> Library
            </button>
            <button type="button" className={"chip" + (tab === "svg" ? " on" : "")} onClick={() => setTab("svg")}>
              <IconCode size={13} stroke={2} /> SVG code
            </button>
          </span>
          <DialogTools onClose={onClose} />
        </h3>
        {error && <div className="dialog-body dialog-title-error">{error}</div>}
        {tab === "library" ? (
          <>
            <div className="icon-picker-search">
              <IconSearch size={15} stroke={1.9} className="muted" />
              <input
                ref={searchRef}
                autoFocus
                className="text-input"
                type="search"
                value={query}
                placeholder={prefix === "*" ? `Search all ${total.toLocaleString()} icons…` : `Search ${set ? set.total.toLocaleString() + " icons of " + set.name : "the icons"}…`}
                onChange={(e) => setQuery(e.target.value)}
              />
              {categories.length > 0 && (
                <select className="select" value={category ?? ""} onChange={(e) => setCategory(e.target.value || null)}>
                  <option value="">All categories</option>
                  {categories.map((c) => (
                    <option key={c} value={c}>
                      {c}
                    </option>
                  ))}
                </select>
              )}
            </div>
            <div className="icon-picker-sets">
              <button type="button" className={"chip" + (prefix === "*" ? " on" : "")} onClick={() => { setPrefix("*"); setCategory(null); searchRef.current?.focus(); }}>
                All sets
              </button>
              <span className="icon-picker-group muted">Monochrome</span>
              {catalog?.filter((s) => !s.colour).map(chip)}
              <span className="icon-picker-group muted">Colour</span>
              {catalog?.filter((s) => s.colour).map(chip)}
            </div>
            <div className="icon-picker-grid" ref={gridRef}>
              {busy ? (
                <div className="icon-picker-note muted">
                  <IconLoader2 size={18} stroke={1.8} className="spin" /> Fetching the icons…
                </div>
              ) : prefix === "*" && !query.trim() ? (
                <div className="icon-picker-note muted">Type what you are looking for to search every set at once.</div>
              ) : hits.length === 0 ? (
                <div className="icon-picker-note muted">No icon is called anything like “{query}”{prefix === "*" ? "" : " in " + (set?.name ?? prefix) + ". Try all sets"}.</div>
              ) : (
                <>
                  {hits.slice(0, limit).map((h) => {
                    const id = h.prefix + ":" + h.name;
                    return (
                      <button
                        key={id}
                        type="button"
                        className={"icon-tile" + (chosen === id ? " is-chosen" : "")}
                        title={id}
                        onClick={() => setChosen(id)}
                        onDoubleClick={() => onChoose(id)}
                        // the library's own icons, drawn in place so the monochrome ones take the text colour
                        dangerouslySetInnerHTML={{ __html: h.svg }}
                      />
                    );
                  })}
                  {hits.length > limit && <div ref={moreRef} className="icon-picker-more" />}
                </>
              )}
            </div>
            <div className="icon-picker-foot">
              {chosen ? (
                <span className="icon-picker-chosen">
                  <span className="icon-picker-preview">
                    <TypeIcon icon={chosen} size={32} />
                    <TypeIcon icon={chosen} size={16} />
                  </span>
                  <span>
                    <strong>{chosen}</strong>
                    {chosenSet && (
                      <span className="muted">
                        {" "}
                        · {chosenSet.name}, {chosenSet.licenseUrl ? <a href={chosenSet.licenseUrl} target="_blank" rel="noreferrer">{chosenSet.license}</a> : chosenSet.license}
                      </span>
                    )}
                  </span>
                </span>
              ) : (
                <span className="muted">{hits.length > 0 && !busy ? `${hits.length.toLocaleString()} icons. Click one to see it, double-click to take it.` : ""}</span>
              )}
              <span className="icon-picker-actions">
                {value && (
                  <button type="button" className="action-button dm-button" onClick={() => onChoose(null)}>
                    Remove icon
                  </button>
                )}
                <button type="button" className="action-button dm-button" onClick={onClose}>
                  Cancel
                </button>
                <button type="button" className="action-button dm-button primary" disabled={!chosen} onClick={() => chosen && onChoose(chosen)}>
                  Use icon
                </button>
              </span>
            </div>
          </>
        ) : (
          <>
            <div className="dialog-body muted">
              The source of an SVG image, from <code>&lt;svg</code> to <code>&lt;/svg&gt;</code>. Draw it in <code>currentColor</code> and it takes the colour of the text around it, as the monochrome icons do; with colours of its own it is shown as drawn. It may not hold scripts.
            </div>
            <textarea className="text-input icon-picker-source" spellCheck={false} value={source} placeholder={'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">…</svg>'} onChange={(e) => setSource(e.target.value)} />
            <div className="icon-picker-foot">
              {source.trim() && !svgProblem ? (
                <span className="icon-picker-chosen">
                  <span className="icon-picker-preview">
                    <TypeIcon icon={source} size={48} />
                    <TypeIcon icon={source} size={24} />
                    <TypeIcon icon={source} size={16} />
                  </span>
                  <span className="muted">{usesCurrentColor(source) ? "Monochrome: drawn in the colour of the text around it." : "In its own colours."}</span>
                </span>
              ) : (
                <span className={svgProblem ? "dialog-title-error" : "muted"}>{svgProblem ?? "Paste or write the SVG above."}</span>
              )}
              <span className="icon-picker-actions">
                {value && (
                  <button type="button" className="action-button dm-button" onClick={() => onChoose(null)}>
                    Remove icon
                  </button>
                )}
                <button type="button" className="action-button dm-button" onClick={onClose}>
                  Cancel
                </button>
                <button type="button" className="action-button dm-button primary" disabled={!source.trim() || !!svgProblem} onClick={() => onChoose(source.trim())}>
                  Use this SVG
                </button>
              </span>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
