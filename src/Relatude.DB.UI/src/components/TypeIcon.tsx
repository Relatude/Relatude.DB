import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { KindIcon } from "./DatamodelIcons";
import type { ModelKind, ModelJson } from "../server/datamodel";
import { resolveTypeSetting } from "../server/overrides";
import { peekSvg, svgDataUrl, svgOf, usesCurrentColor } from "../server/icons";

/**
 * A type's icon (NodeTypeModel.Icon) as it is shown: a name of the icon library, or SVG source. Drawn as an
 * image, never put into the page, so SVG somebody wrote cannot run anything here. A monochrome icon (one drawn
 * in currentColor) is a mask over the text colour, so it takes the colour of where it sits as the library's
 * own icons do; a coloured one is the picture itself. While its set is fetched it holds its place, and an icon
 * that cannot be drawn shows the fallback.
 */
export function TypeIcon({ icon, size = 18, className, fallback }: { icon: string | null | undefined; size?: number; className?: string; fallback?: ReactNode }) {
  const svg = useIconSvg(icon);
  if (svg === null && fallback !== undefined) return <>{fallback}</>;
  if (!svg) return <span className={"type-icon " + (className ?? "")} style={{ width: size, height: size }} />;
  const url = svgDataUrl(svg);
  if (usesCurrentColor(svg)) {
    return <span className={"type-icon is-mono " + (className ?? "")} style={{ width: size, height: size, WebkitMaskImage: `url("${url}")`, maskImage: `url("${url}")` }} />;
  }
  return <img className={"type-icon " + (className ?? "")} src={url} width={size} height={size} alt="" draggable={false} />;
}

/** The SVG of an icon: undefined while its set is fetched, null for none or one that cannot be drawn. */
export function useIconSvg(icon: string | null | undefined): string | null | undefined {
  const [svg, setSvg] = useState<string | null | undefined>(() => (icon ? peekSvg(icon) : null));
  useEffect(() => {
    let live = true;
    const known = icon ? peekSvg(icon) : null;
    setSvg(known);
    if (icon && known === undefined) void svgOf(icon).then((s) => live && setSvg(s));
    return () => {
      live = false;
    };
  }, [icon]);
  return svg;
}

/**
 * A type's icon inside an SVG drawing - the diagram's boxes, the graph's circles - where it sits on the type's
 * colour: a monochrome icon is drawn in the colour given (white on the colour), a coloured one on a white disc
 * of its own, so its colours do not fight the one under it. As an image, so nothing in the SVG runs. Nothing is
 * drawn while the set is fetched or for an icon that cannot be drawn: callers draw the kind instead.
 */
export function SvgTypeIcon({ svg, x, y, size, color = "#fff" }: { svg: string; x: number; y: number; size: number; color?: string }) {
  const mono = usesCurrentColor(svg);
  const url = svgDataUrl(mono ? svg.replace(/currentColor/gi, color) : svg);
  if (mono) return <image href={url} x={x} y={y} width={size} height={size} />;
  const pad = size * 0.18;
  return (
    <g className="svg-type-icon">
      <circle cx={x + size / 2} cy={y + size / 2} r={size / 2 + pad} fill="#fff" />
      <image href={url} x={x} y={y} width={size} height={size} />
    </g>
  );
}

/** How the pages draw a type they know by id or name: its icon (its own or inherited) and its kind. */
export interface TypeLook {
  icon: string | null;
  kind: ModelKind | string;
}
const TypeLooks = createContext<{ byId: Map<string, TypeLook>; byName: Map<string, TypeLook> } | null>(null);

/** Tells the glyphs below which icon each type has: a page that lists nodes has the types they are of. */
export function TypeLooksProvider({ types, children }: { types: { id: string; name: string; kind: ModelKind | string; icon?: string | null }[]; children: ReactNode }) {
  const value = useMemo(() => {
    const byId = new Map<string, TypeLook>();
    const byName = new Map<string, TypeLook>();
    for (const t of types) {
      const look = { icon: t.icon ?? null, kind: t.kind };
      byId.set(t.id, look);
      if (!byName.has(t.name)) byName.set(t.name, look);
    }
    return { byId, byName };
  }, [types]);
  return <TypeLooks.Provider value={value}>{children}</TypeLooks.Provider>;
}

/** A node's type as a glyph, beside its name or its type's: found by id, else by name, among the types the page has; nothing for a type it does not know. */
export function NodeTypeGlyph({ typeId, typeName, size = 14 }: { typeId?: string | null; typeName?: string | null; size?: number }) {
  const looks = useContext(TypeLooks);
  const look = (typeId ? looks?.byId.get(typeId) : undefined) ?? (typeName ? looks?.byName.get(typeName) : undefined);
  if (!look) return null;
  return <TypeGlyph icon={look.icon} kind={look.kind} size={size} className="node-type-glyph" />;
}

/**
 * A node type as the pages show it: the icon it is shown with when it has one, its own or the one it inherits,
 * else the shape of its kind (class, interface, record, struct), as before types had icons.
 */
export function TypeGlyph({ icon, kind, size = 16, className }: { icon: string | null | undefined; kind: ModelKind | string; size?: number; className?: string }) {
  const shape = (
    <span className={"kind-glyph " + (className ?? "")}>
      <KindIcon kind={(kind as ModelKind) ?? "Class"} size={size} />
    </span>
  );
  if (!icon) return shape;
  return <TypeIcon icon={icon} size={size} className={className} fallback={shape} />;
}

/** The icon a type of the model being edited is shown with: its own, overrides included, else the one it inherits; null for none. */
export function modelIcon(model: ModelJson, typeId: string): string | null {
  const icon = resolveTypeSetting(model, typeId, "Icon", "inherited").value;
  return typeof icon === "string" && icon.trim() ? icon : null;
}
