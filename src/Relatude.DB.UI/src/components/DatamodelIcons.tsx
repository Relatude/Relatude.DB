import type { ComponentType } from "react";
import {
  IconAbc,
  IconArrowsExchange,
  IconAssembly,
  IconBinary,
  IconBraces,
  IconBrandCSharp,
  IconCalendar,
  IconClock,
  IconCode,
  IconCube,
  IconCubeUnfolded,
  IconDecimal,
  IconFile,
  IconFingerprint,
  IconHash,
  IconHexagon,
  IconLetterI,
  IconLink,
  IconList,
  IconMapPin,
  IconPhoto,
  IconRelationManyToMany,
  IconRelationOneToMany,
  IconRelationOneToOne,
  IconStack2,
  IconTags,
  IconToggleLeft,
  IconVector,
} from "@tabler/icons-react";
import type { ModelKind, RelationKind, SourceFileFormat, SourceType } from "../server/datamodel";

type Icon = ComponentType<{ size?: number; stroke?: number; color?: string; className?: string }>;

/**
 * The visual vocabulary of the data model pages. Shape says what kind of thing something is: a
 * type's kind (class, interface, record, struct), a property's value type, a relation's cardinality,
 * a source's kind. Color says where it comes from - one color per source (see sourceColors) - so the
 * two dimensions never compete for the same channel.
 */

/** Type kinds and their colors, stable across the lists, the tree and the diagram. */
export const kindMeta: Record<ModelKind, { icon: Icon; color: string; label: string }> = {
  Class: { icon: IconCube, color: "#2f7fd6", label: "class" },
  Interface: { icon: IconLetterI, color: "#b84cc0", label: "interface" },
  Record: { icon: IconCubeUnfolded, color: "#3ca35a", label: "record" },
  Struct: { icon: IconHexagon, color: "#d97b2b", label: "struct" },
};

export function KindIcon({ kind, size = 16 }: { kind: ModelKind; size?: number }) {
  const meta = kindMeta[kind] ?? kindMeta.Class;
  const Cmp = meta.icon;
  return <Cmp size={size} stroke={1.9} color={meta.color} />;
}

/** Embedded (inner node) properties, shared by the property icon and the diagram's embed edges. */
export const embeddedColor = "#b84cc0";

/** Property value types grouped into a handful of families, each with a shape and a hue. */
const propertyFamilies: Record<string, { icon: Icon; color: string }> = {
  text: { icon: IconAbc, color: "#2f7fd6" },
  number: { icon: IconHash, color: "#d97b2b" },
  decimal: { icon: IconDecimal, color: "#d97b2b" },
  bool: { icon: IconToggleLeft, color: "#3ca35a" },
  date: { icon: IconCalendar, color: "#c9a227" },
  time: { icon: IconClock, color: "#c9a227" },
  guid: { icon: IconFingerprint, color: "#8a8781" },
  geo: { icon: IconMapPin, color: "#2ba8a0" },
  bytes: { icon: IconBinary, color: "#8a8781" },
  file: { icon: IconPhoto, color: "#e06aa2" },
  list: { icon: IconList, color: "#7a6ff0" },
  vector: { icon: IconVector, color: "#7a6ff0" },
  embedded: { icon: IconStack2, color: embeddedColor },
  reference: { icon: IconLink, color: "#d64f5f" },
  relation: { icon: IconArrowsExchange, color: "#d64f5f" },
  tags: { icon: IconTags, color: "#7a6ff0" },
};

const propertyFamilyOf: Record<string, keyof typeof propertyFamilies> = {
  String: "text",
  Integer: "number",
  Long: "number",
  Double: "decimal",
  Float: "decimal",
  Decimal: "decimal",
  Boolean: "bool",
  DateTime: "date",
  DateTimeOffset: "date",
  TimeSpan: "time",
  Guid: "guid",
  GeoCoordinate: "geo",
  ByteArray: "bytes",
  File: "file",
  StringArray: "tags",
  GuidArray: "list",
  FloatArray: "vector",
  EnumArray: "tags",
  Embedded: "embedded",
  Reference: "reference",
  References: "reference",
  Relation: "relation",
};

export function propertyColor(propertyType: string): string {
  return propertyFamilies[propertyFamilyOf[propertyType] ?? "guid"].color;
}

export function PropertyIcon({ propertyType, size = 14 }: { propertyType: string; size?: number }) {
  const family = propertyFamilies[propertyFamilyOf[propertyType] ?? "guid"];
  const Cmp = family.icon;
  return <Cmp size={size} stroke={1.9} color={family.color} />;
}

/** Relation cardinalities. OneOne and ManyMany are symmetric, so the icon shows no arrowhead bias. */
export const relationMeta: Record<RelationKind, { icon: Icon; label: string; short: string; directed: boolean }> = {
  OneOne: { icon: IconRelationOneToOne, label: "One to one, symmetric", short: "1 — 1", directed: false },
  OneToOne: { icon: IconRelationOneToOne, label: "One to one, directed", short: "1 → 1", directed: true },
  OneToMany: { icon: IconRelationOneToMany, label: "One to many", short: "1 → n", directed: true },
  ManyMany: { icon: IconRelationManyToMany, label: "Many to many, symmetric", short: "n — n", directed: false },
  ManyToMany: { icon: IconRelationManyToMany, label: "Many to many, directed", short: "n → n", directed: true },
};

export const relationColor = "#d64f5f";

export function RelationIcon({ kind, size = 16 }: { kind: RelationKind; size?: number }) {
  const meta = relationMeta[kind] ?? relationMeta.ManyToMany;
  const Cmp = meta.icon;
  return <Cmp size={size} stroke={1.9} color={relationColor} />;
}

/** Source kinds. Color comes from the source's own swatch; the shape tells the kind apart. */
export const sourceMeta: Record<SourceType, { icon: Icon; label: string }> = {
  TypeReference: { icon: IconAssembly, label: "Compiled types" },
  TextFiles: { icon: IconBraces, label: "Text files" },
  Code: { icon: IconCode, label: "Application code" },
};
/** Text files split further by what they hold; the other kinds are one shape each. */
export function sourceKindMeta(type: SourceType, fileFormat?: SourceFileFormat | null): { icon: Icon; label: string } {
  if (type === "TextFiles") return fileFormat === "CSharpCode" ? { icon: IconBrandCSharp, label: "C# files" } : { icon: IconBraces, label: "JSON files" };
  return sourceMeta[type] ?? sourceMeta.Code;
}

export function SourceIcon({ type, fileFormat, color, size = 16 }: { type: SourceType; fileFormat?: SourceFileFormat | null; color?: string; size?: number }) {
  const Cmp = sourceKindMeta(type, fileFormat).icon;
  return <Cmp size={size} stroke={1.9} color={color} />;
}

export function FileIcon({ size = 14 }: { size?: number }) {
  return <IconFile size={size} stroke={1.9} />;
}

/** A small filled circle in a source's color; the badge every list row carries. */
export function SourceDot({ color, title }: { color: string; title?: string }) {
  return <span className="dm-dot" style={{ background: color }} title={title} />;
}
