// The icon library the server carries (IconLibrary in NodeServer/Icons), for the icon a node type is shown
// with (NodeTypeModel.Icon): open icon sets in Iconify's JSON format. A type's icon is a name, "set:name"
// ("lucide:house"), or the source of an SVG image. The picker downloads whole sets - the first time one is
// needed, and the browser keeps them under a url carrying their version - and draws them itself, with the
// same rules the server's IconLibrary.ToSvg uses, so an icon looks the same here and wherever it is shown.

import { adminBase } from "./base";

export interface IconSetInfo {
  prefix: string;
  name: string;
  /** the icons carry colours of their own; otherwise they are drawn in the colour of the text around them */
  colour: boolean;
  total: number;
  author: string | null;
  authorUrl: string | null;
  license: string | null;
  licenseUrl: string | null;
  samples: string[];
  version: string;
  package: string | null;
}

interface Box {
  left?: number;
  top?: number;
  width?: number;
  height?: number;
}

export interface IconSetJson extends Box {
  prefix: string;
  icons: Record<string, Box & { body: string; hidden?: boolean }>;
  aliases?: Record<string, Box & { parent: string; rotate?: number; hFlip?: boolean; vFlip?: boolean; hidden?: boolean }>;
  categories?: Record<string, string[]>;
}

let catalog: Promise<IconSetInfo[]> | null = null;
const sets = new Map<string, Promise<IconSetJson>>();

/** The sets of the library, monochrome ones first. */
export function iconSets(): Promise<IconSetInfo[]> {
  catalog ??= fetch(adminBase + "/ui/icons", { credentials: "same-origin" }).then(async (r) => {
    if (!r.ok) throw new Error("The server did not list its icons (" + r.status + ").");
    return (await r.json()) as IconSetInfo[];
  });
  catalog.catch(() => (catalog = null)); // tried again next time
  return catalog;
}

/** A set's icons, downloaded once. */
export function iconSet(prefix: string): Promise<IconSetJson> {
  let set = sets.get(prefix);
  if (!set) {
    set = iconSets().then(async (all) => {
      const info = all.find((s) => s.prefix === prefix);
      if (!info) throw new Error("The icon library has no set called " + prefix + ".");
      const r = await fetch(adminBase + "/ui/icons/" + encodeURIComponent(prefix) + "?v=" + info.version, { credentials: "same-origin" });
      if (!r.ok) throw new Error("The icons of " + info.name + " could not be read (" + r.status + ").");
      return (await r.json()) as IconSetJson;
    });
    sets.set(prefix, set);
    set.catch(() => sets.delete(prefix));
  }
  return set;
}

/** True for a value that is the source of an SVG image rather than a name of the library. */
export function isSvgSource(icon: string): boolean {
  const t = icon.trimStart().toLowerCase();
  return t.startsWith("<svg") || t.startsWith("<?xml");
}

/** "lucide:house" -> ["lucide", "house"]; null for a value that is not a name. */
export function splitIconName(icon: string): [string, string] | null {
  const colon = icon.indexOf(":");
  if (colon < 1 || colon === icon.length - 1 || isSvgSource(icon)) return null;
  return [icon.slice(0, colon), icon.slice(colon + 1)];
}

const n = (v: number) => String(Math.round(v * 1000) / 1000);

/** An icon of a set as SVG, or null for a name the set does not have: the icon's box as the viewBox, an alias's turns and flips around its middle. */
export function iconSvg(set: IconSetJson, name: string): string | null {
  const box: Box = {};
  let rotate = 0;
  let hFlip = false;
  let vFlip = false;
  for (let depth = 0; depth < 8; depth++) {
    const icon = set.icons[name];
    if (icon) {
      const l = box.left ?? icon.left ?? set.left ?? 0;
      const t = box.top ?? icon.top ?? set.top ?? 0;
      const w = box.width ?? icon.width ?? set.width ?? 16;
      const h = box.height ?? icon.height ?? set.height ?? 16;
      const turns: string[] = [];
      if (rotate % 4) turns.push(`rotate(${n((rotate % 4) * 90)} ${n(l + w / 2)} ${n(t + h / 2)})`);
      if (hFlip) turns.push(`translate(${n(2 * l + w)} 0) scale(-1 1)`);
      if (vFlip) turns.push(`translate(0 ${n(2 * t + h)}) scale(1 -1)`);
      const body = turns.length ? `<g transform="${turns.join(" ")}">${icon.body}</g>` : icon.body;
      return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${n(l)} ${n(t)} ${n(w)} ${n(h)}" width="${n(w)}" height="${n(h)}">${body}</svg>`;
    }
    const alias = set.aliases?.[name];
    if (!alias) return null;
    box.left ??= alias.left;
    box.top ??= alias.top;
    box.width ??= alias.width;
    box.height ??= alias.height;
    rotate += alias.rotate ?? 0;
    hFlip = hFlip !== !!alias.hFlip;
    vFlip = vFlip !== !!alias.vFlip;
    name = alias.parent;
  }
  return null;
}

const drawn = new Map<string, string | null>();

/** The SVG of an icon drawn before, so a list shown again draws it at once: undefined while it has not been. */
export function peekSvg(icon: string): string | null | undefined {
  return drawn.get(icon);
}

/** The SVG of a type's icon: a name of the library as its SVG, SVG source as written; null for an unknown name or a value that fails checkSvg. */
export async function svgOf(icon: string): Promise<string | null> {
  const known = drawn.get(icon);
  if (known !== undefined) return known;
  let svg: string | null = null;
  if (isSvgSource(icon)) {
    svg = checkSvg(icon) ? null : icon.trim();
  } else {
    const parts = splitIconName(icon.trim());
    if (!parts) svg = null;
    else {
      try {
        svg = iconSvg(await iconSet(parts[0]), parts[1]);
      } catch {
        return null; // the set could not be fetched: tried again next time
      }
    }
  }
  drawn.set(icon, svg);
  return svg;
}

/**
 * What is wrong with SVG source as an icon, or null when nothing is: the server's rules (IconLibrary.IsValid),
 * so the editor says so before an activation does. It has to parse, have svg as its root and hold no script.
 */
export function checkSvg(source: string): string | null {
  if (source.length > 100_000) return "An icon may be at most 100,000 characters of SVG.";
  const doc = new DOMParser().parseFromString(source.trim(), "image/svg+xml");
  if (doc.querySelector("parsererror")) return "The SVG does not parse.";
  const root = doc.documentElement;
  if (root.localName !== "svg") return "The SVG's root element is <" + root.localName + ">, not <svg>.";
  for (const el of [root, ...Array.from(root.querySelectorAll("*"))]) {
    if (el.localName === "script" || el.localName === "foreignObject") return "The SVG holds a <" + el.localName + "> element. An icon may not hold scripts or other documents.";
    for (const a of Array.from(el.attributes)) {
      if (/^on/i.test(a.localName)) return "The SVG has an event attribute, " + a.localName + ". An icon may not hold scripts.";
      if (/^\s*javascript:/i.test(a.value)) return "The SVG links to javascript:. An icon may not hold scripts.";
    }
  }
  return null;
}

/** Whether an SVG is drawn in the colour of the text around it (a monochrome icon) rather than in colours of its own. */
export function usesCurrentColor(svg: string): boolean {
  return /currentColor/i.test(svg);
}

/** An SVG as a url an <img> or a CSS mask can show: the safe way to show SVG someone wrote, since nothing in it runs. */
export function svgDataUrl(svg: string): string {
  return "data:image/svg+xml;charset=utf-8," + encodeURIComponent(svg);
}
