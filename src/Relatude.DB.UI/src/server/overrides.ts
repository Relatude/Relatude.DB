// What a node type or property ends up with once overrides and inheritance have had their say, worked
// out in the page from the model being edited, so a form can say where a value comes from while it is
// being changed. It mirrors the server: Datamodel.ApplyOverrides (the database's overrides go into the
// types as if the sources said so), Datamodel.resolvePropertySettingsPerType and inheritedSwitches (the
// most specific type that sets a value wins; base types that disagree fall back to the declaration, or
// the database default), and keepRequests (a property is indexed, or a facet, when any type that has it asks). The server is the authority - validation reports what it makes of a draft -
// so a difference here only misleads a label, never what is written.

import type { ModelJson, NodeTypeJson, NodeTypeOverrideJson, OverridesJson, OverrideScope, PropertyJson, PropertyOverrideJson } from "./datamodel";

/** Where a value a form shows comes from. */
export type OriginKind =
  /** the database's overrides, on this type */
  | "override"
  /** this type's own definition: the source, or [PropertyOverride] on it for an inherited property */
  | "own"
  /** a base type: typeId names it, and whether it is that type's override or its definition */
  | "inherited"
  /** nothing sets it: the property's declaration, or for a node type switch the database default */
  | "default";

export interface Resolved {
  value: unknown;
  kind: OriginKind;
  /** for inherited: the type the value comes from */
  typeId?: string;
  /** for inherited: the base type's value is itself one of the database's overrides */
  fromOverride?: boolean;
  /** base types that disagree, when they do */
  conflict?: string[];
}

const isSet = (v: unknown) => v !== undefined && v !== null;

/** The type and every type it inherits from, the built-in base included. */
export function lineage(model: ModelJson, typeId: string): Set<string> {
  const out = new Set<string>();
  const walk = (id: string, depth: number) => {
    if (depth > 60 || out.has(id)) return;
    out.add(id);
    for (const p of model.NodeTypes[id]?.Parents ?? []) walk(p, depth + 1);
  };
  walk(typeId, 0);
  return out;
}

/** The type that declares a property. */
export function declaringType(model: ModelJson, propertyId: string): NodeTypeJson | undefined {
  return Object.values(model.NodeTypes).find((t) => !!t.Properties[propertyId]);
}

export function typeOverride(model: ModelJson, typeId: string): NodeTypeOverrideJson | undefined {
  return model.Overrides?.NodeTypes?.[typeId];
}
export function propertyOverride(model: ModelJson, typeId: string, propertyId: string): PropertyOverrideJson | undefined {
  return model.Overrides?.NodeTypes?.[typeId]?.Properties?.[propertyId];
}

// candidates some other candidate inherits from are overridden by that one
function mostSpecific(model: ModelJson, ids: string[]): string[] {
  const lines = new Map(ids.map((id) => [id, lineage(model, id)]));
  return ids.filter((c) => !ids.some((o) => o !== c && lines.get(o)!.has(c)));
}
const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);

/** A node type's own value of a setting: the database's override, else what its source says. */
export function ownTypeSetting(model: ModelJson, typeId: string, path: string): { value: unknown; overridden: boolean } {
  const o = typeOverride(model, typeId)?.[path];
  if (isSet(o)) return { value: o, overridden: true };
  return { value: (model.NodeTypes[typeId] as Record<string, unknown> | undefined)?.[path], overridden: false };
}

/** What a node type ends up with for a setting the catalog marks as overridable. */
export function resolveTypeSetting(model: ModelJson, typeId: string, path: string, scope: OverrideScope): Resolved {
  const own = ownTypeSetting(model, typeId, path);
  if (isSet(own.value) || scope !== "inherited") return { value: own.value, kind: own.overridden ? "override" : isSet(own.value) ? "own" : "default" };
  return typeSettingFromBases(model, typeId, path);
}
/** What a type would take from its base types for an inherited setting if it set none itself. */
export function typeSettingFromBases(model: ModelJson, typeId: string, path: string): Resolved {
  const candidates = [...lineage(model, typeId)].filter((id) => id !== typeId && isSet(ownTypeSetting(model, id, path).value));
  const best = mostSpecific(model, candidates);
  const values = best.map((id) => ownTypeSetting(model, id, path).value).filter((v, i, all) => all.findIndex((x) => same(x, v)) === i);
  if (values.length === 1) return { value: values[0], kind: "inherited", typeId: best[0], fromOverride: ownTypeSetting(model, best[0], path).overridden };
  return { value: undefined, kind: "default", conflict: values.length > 1 ? best : undefined };
}

/**
 * What a type's view of a property sets for a field, at one type of its lineage: the database's
 * override, else the type's own definition - the property itself on the declaring type, the type's
 * PropertyOverrides on any other.
 */
function atType(model: ModelJson, typeId: string, property: PropertyJson, declaringId: string, path: string): { value: unknown; overridden: boolean } {
  const o = propertyOverride(model, typeId, property.Id)?.[path];
  if (isSet(o)) return { value: o, overridden: true };
  if (typeId === declaringId) return { value: (property as Record<string, unknown>)[path], overridden: false };
  return { value: model.NodeTypes[typeId]?.PropertyOverrides?.[property.Id]?.[path], overridden: false };
}

/**
 * What a property is for a field when seen from viewTypeId (the declaring type, or one inheriting the
 * property). A whole property field is the declaring type's; an inherited one is resolved like the
 * server does: the most specific type in viewTypeId's lineage that sets it, the declaration when two
 * that are not related disagree.
 */
export function resolvePropertySetting(model: ModelJson, viewTypeId: string, property: PropertyJson, declaringId: string, path: string, scope: OverrideScope | null): Resolved {
  const declared = atType(model, declaringId, property, declaringId, path);
  if (scope !== "inherited") {
    if (viewTypeId === declaringId) return { value: declared.value, kind: declared.overridden ? "override" : "own" };
    return { value: declared.value, kind: "inherited", typeId: declaringId, fromOverride: declared.overridden };
  }
  const line = lineage(model, viewTypeId);
  const candidates = [...line].filter((id) => line.has(id) && lineage(model, id).has(declaringId) && isSet(atType(model, id, property, declaringId, path).value));
  const best = mostSpecific(model, candidates);
  const values = best.map((id) => atType(model, id, property, declaringId, path).value).filter((v, i, all) => all.findIndex((x) => same(x, v)) === i);
  if (values.length === 1) {
    const from = best[0];
    const at = atType(model, from, property, declaringId, path);
    if (from === viewTypeId) return { value: values[0], kind: at.overridden ? "override" : "own" };
    return { value: values[0], kind: "inherited", typeId: from, fromOverride: at.overridden };
  }
  return { value: declared.value, kind: "default", typeId: declaringId, conflict: values.length > 1 ? best : undefined };
}

/**
 * What a field resolves to for viewTypeId when that type's override, and unless keepOwn its own
 * definition too, are left out: what removing them goes back to. An override is removed on top of the
 * type's definition, so what removing it goes back to keeps what the definition says.
 */
export function inheritedPropertySetting(model: ModelJson, viewTypeId: string, property: PropertyJson, declaringId: string, path: string, keepOwn = false): unknown {
  const copy = JSON.parse(JSON.stringify(model)) as ModelJson;
  const t = copy.NodeTypes[viewTypeId];
  if (!keepOwn && t?.PropertyOverrides?.[property.Id]) delete t.PropertyOverrides[property.Id][path];
  const o = copy.Overrides?.NodeTypes?.[viewTypeId]?.Properties?.[property.Id];
  if (o) delete o[path];
  return resolvePropertySetting(copy, viewTypeId, property, declaringId, path, "inherited").value;
}

// ---- a setting any type that has the property can ask for (anyType: the value index) ----

/** What one type itself says for a field of a property: its override, else its own definition. */
export function ownPropertySetting(model: ModelJson, typeId: string, property: PropertyJson, declaringId: string, path: string): { value: unknown; overridden: boolean } {
  return atType(model, typeId, property, declaringId, path);
}

/**
 * The types asking for an anyType field of a property - with asks, the value that asks (true for
 * Indexed, false for NotFacet) - the declaring type first, when its declaration or the database's
 * override of it says so. The property has it when any type does: there is one value index and one set
 * of facet counts, shared by every type that has the property. Mirrors Datamodel.keepRequests.
 */
export function requestsOf(model: ModelJson, property: PropertyJson, declaringId: string, path: string, asks = true): string[] {
  const out: string[] = [];
  // a declaration that leaves the field out says false
  if ((atType(model, declaringId, property, declaringId, path).value ?? false) === asks) out.push(declaringId);
  for (const [id, t] of Object.entries(model.NodeTypes)) {
    if (id === declaringId) continue;
    // cheap checks first: most types say nothing about most properties
    if (!isSet(t.PropertyOverrides?.[property.Id]?.[path]) && !isSet(propertyOverride(model, id, property.Id)?.[path])) continue;
    if (atType(model, id, property, declaringId, path).value === asks && lineage(model, id).has(declaringId)) out.push(id);
  }
  return out;
}

/** Whether a property has a value index as the store will see it: declared or overridden, unique values, or asked for by a type that has it. */
export function isIndexed(model: ModelJson, property: PropertyJson): boolean {
  if (property.UniqueValues) return true;
  const declaringId = property.NodeType && model.NodeTypes[property.NodeType]?.Properties[property.Id] ? property.NodeType : declaringType(model, property.Id)?.Id;
  if (!declaringId) return !!property.Indexed;
  return requestsOf(model, property, declaringId, "Indexed", true).length > 0;
}

// ---- writing ----

/**
 * Sets (or with undefined removes) one override, keeping the names next to the ids current and dropping
 * what is left of an override that no longer sets anything.
 */
export function setOverride(model: ModelJson, typeId: string, propertyId: string | null, path: string, value: unknown) {
  const type = model.NodeTypes[typeId];
  if (!model.Overrides) model.Overrides = { NodeTypes: {} };
  const types = model.Overrides.NodeTypes;
  const t = (types[typeId] ??= {});
  if (type) t.Name = type.Namespace ? `${type.Namespace}.${type.CodeName}` : type.CodeName;
  if (propertyId === null) {
    if (value === undefined) delete t[path];
    else t[path] = value;
  } else {
    const props = (t.Properties ??= {});
    const p = (props[propertyId] ??= {});
    const declaring = declaringType(model, propertyId);
    const property = declaring?.Properties[propertyId];
    if (property) p.Name = property.CodeName;
    if (value === undefined) delete p[path];
    else p[path] = value;
    if (Object.keys(p).every((k) => k === "Name")) delete props[propertyId];
    if (Object.keys(props).length === 0) delete t.Properties;
  }
  if (Object.keys(t).every((k) => k === "Name")) delete types[typeId];
  // deleted rather than nulled: a model without overrides then reads exactly as it did before any were set
  if (Object.keys(types).length === 0) delete model.Overrides;
}

/** Sets (or removes) an attribute a type gives a property it inherits, in its own definition. */
export function setOwnPropertyOverride(model: ModelJson, typeId: string, propertyId: string, path: string, value: unknown) {
  const t = model.NodeTypes[typeId];
  if (!t) return;
  const all = (t.PropertyOverrides ??= {});
  const o = (all[propertyId] ??= {});
  const property = declaringType(model, propertyId)?.Properties[propertyId];
  if (property) o.Name = property.CodeName;
  if (value === undefined) delete o[path];
  else o[path] = value;
  if (Object.keys(o).every((k) => k === "Name")) delete all[propertyId];
  if (Object.keys(all).length === 0) delete t.PropertyOverrides;
}

// ---- listing ----

export interface OverrideEntry {
  typeId: string;
  /** null: a setting of the type itself */
  propertyId: string | null;
  path: string;
  value: unknown;
}

/** Every override the model carries, in a stable order. */
export function listOverrides(model: ModelJson): OverrideEntry[] {
  const out: OverrideEntry[] = [];
  for (const [typeId, t] of Object.entries(model.Overrides?.NodeTypes ?? {})) {
    for (const [path, value] of Object.entries(t)) if (path !== "Name" && path !== "Properties" && isSet(value)) out.push({ typeId, propertyId: null, path, value });
    for (const [propertyId, p] of Object.entries(t.Properties ?? {})) {
      for (const [path, value] of Object.entries(p)) if (path !== "Name" && isSet(value)) out.push({ typeId, propertyId, path, value });
    }
  }
  return out;
}

/** Whether the database overrides anything on the type or on a property seen from it. */
export function hasOverrides(model: ModelJson, typeId: string): boolean {
  return listOverrides(model).some((e) => e.typeId === typeId);
}
/** Whether a property has overrides seen from the type: the database's, or the type's own [PropertyOverride]. */
export function propertyHasOverrides(model: ModelJson, typeId: string, propertyId: string): { database: boolean; own: boolean } {
  const db = propertyOverride(model, typeId, propertyId);
  const own = model.NodeTypes[typeId]?.PropertyOverrides?.[propertyId];
  const any = (o: PropertyOverrideJson | undefined) => !!o && Object.entries(o).some(([k, v]) => k !== "Name" && isSet(v));
  return { database: any(db), own: any(own) };
}

// ---- the two files: the shared one, and this installation's ----
//
// The model carries the overrides in force: the shared file's (relatude.settings, deployed to every
// installation) with this installation's merged over them. Activating writes what differs from the shared
// file into this installation's, and null there for a shared value the draft no longer has. So which file an
// override is in follows from comparing with the shared file, which the page gets on its own.

/** Where the database keeps its overrides, for the forms to say which file a value is in. */
export interface OverridesPlaces {
  /** what the shared file overrides */
  shared: OverridesJson | null;
  /** the shared file, relative to the application's folder */
  sharedLocation: string;
  /** this installation's file */
  location: string;
}

/** What the shared file says for an attribute, or undefined when it says nothing. */
export function sharedOverride(shared: OverridesJson | null | undefined, typeId: string, propertyId: string | null, path: string): unknown {
  const t = shared?.NodeTypes?.[typeId];
  const v = propertyId === null ? t?.[path] : t?.Properties?.[propertyId]?.[path];
  return isSet(v) ? v : undefined;
}

export type OverrideLayer = "shared" | "installation";
/** The file an override in force is in: the shared one when it says the same, else this installation's. */
export function layerOf(shared: OverridesJson | null | undefined, typeId: string, propertyId: string | null, path: string, value: unknown): OverrideLayer {
  const s = sharedOverride(shared, typeId, propertyId, path);
  return s !== undefined && same(s, value) ? "shared" : "installation";
}

/** The shared overrides the model does not have: taken away on this installation, where the source's value applies. */
export function listResets(model: ModelJson, shared: OverridesJson | null | undefined): OverrideEntry[] {
  const out: OverrideEntry[] = [];
  for (const [typeId, t] of Object.entries(shared?.NodeTypes ?? {})) {
    const inForce = model.Overrides?.NodeTypes?.[typeId];
    for (const [path, value] of Object.entries(t)) {
      if (path === "Name" || path === "Properties" || !isSet(value)) continue;
      if (!isSet(inForce?.[path])) out.push({ typeId, propertyId: null, path, value });
    }
    for (const [propertyId, p] of Object.entries(t.Properties ?? {})) {
      for (const [path, value] of Object.entries(p)) {
        if (path === "Name" || !isSet(value)) continue;
        if (!isSet(inForce?.Properties?.[propertyId]?.[path])) out.push({ typeId, propertyId, path, value });
      }
    }
  }
  return out;
}
