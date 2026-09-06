import type { EditorContext } from "./DatamodelEditors";
import { relationMeta } from "./DatamodelIcons";
import type { NodeTypeJson, PropertyJson } from "../server/datamodel";

/**
 * The model as a graph to be unfolded, shared by the flat Graph view and the 3D view: which types
 * and lines there are, which of them are on screen given what has been unfolded, and what is
 * remembered about it per database. The two views draw the same picture in different spaces, so
 * the picture itself is computed once, here.
 */

/** What is drawn: a type, or one property of a type shown as a leaf beside it. */
export type GraphNode =
  | { id: string; kind: "type"; type: NodeTypeJson; root: boolean; r: number; hidden: number; open: boolean }
  | { id: string; kind: "property"; property: PropertyJson; ownerId: string; r: number };

/** The kinds of line, each of which can be switched off - and with it the types it would lead to. */
export type EdgeKind = "inherits" | "relation" | "reference" | "embeds" | "property";

export type GraphLink =
  | { id: string; kind: "inherits"; from: string; to: string }
  | { id: string; kind: "relation"; from: string; to: string; label: string; relationId: string; directed: boolean }
  | { id: string; kind: "reference"; from: string; to: string; label: string; propertyId: string }
  | { id: string; kind: "embeds"; from: string; to: string; label: string; propertyId: string }
  | { id: string; kind: "property"; from: string; to: string; propertyId: string };

export const edgeKinds: { kind: EdgeKind; label: string; hint: string }[] = [
  { kind: "inherits", label: "inherits", hint: "Inheritance: a type's parents and the types under it" },
  { kind: "relation", label: "relations", hint: "Relations between types" },
  { kind: "reference", label: "references", hint: "Reference properties and the types they point at" },
  { kind: "embeds", label: "embedded", hint: "Embedded inner node types" },
  { kind: "property", label: "properties", hint: "A type's own properties, as leaves" },
];

export interface World {
  eligible: Set<string>;
  types: NodeTypeJson[];
  links: GraphLink[];
  neighbors: Map<string, Set<string>>;
  leaves: Map<string, PropertyJson[]>;
}

export const leafId = (propertyId: string) => "p:" + propertyId;

// the same keys for both views: what is unfolded in one is unfolded in the other
export const rootKey = (storeId: string) => "dmGraphRoot:" + storeId;
export const expandedKey = (storeId: string) => "dmGraphExpanded:" + storeId;
export const edgesKey = (storeId: string) => "dmGraphEdges:" + storeId;

/** The whole model as a graph, independent of what is unfolded. */
export function buildWorld(ctx: EditorContext, visibleTypes: Set<string>, edges: Set<EdgeKind>): World {
  const baseId = ctx.baseTypeId;
  const eligible = new Set<string>();
  for (const t of Object.values(ctx.model.NodeTypes)) if (t.Id === baseId || visibleTypes.has(t.Id)) eligible.add(t.Id);
  const types = [...eligible].map((id) => ctx.model.NodeTypes[id]).filter((t): t is NodeTypeJson => !!t);
  const all: GraphLink[] = [];
  const seen = new Set<string>();
  const push = (l: GraphLink) => {
    if (seen.has(l.id)) return;
    seen.add(l.id);
    all.push(l);
  };
  for (const t of types) {
    if (t.Id === baseId) continue;
    const parents = (t.Parents ?? []).filter((p) => eligible.has(p));
    // a type with no shown parent hangs off the base, which is where it stands in the store
    if (parents.length === 0) push({ id: t.Id + ">" + baseId, kind: "inherits", from: t.Id, to: baseId });
    for (const p of parents) push({ id: t.Id + ">" + p, kind: "inherits", from: t.Id, to: p });
    for (const p of Object.values(t.Properties)) {
      if (p.Internal) continue;
      if ((p.PropertyType === "Reference" || p.PropertyType === "References") && p.NodeTypes) {
        for (const target of p.NodeTypes) if (eligible.has(target)) push({ id: p.Id + ">" + target, kind: "reference", from: t.Id, to: target, label: p.CodeName, propertyId: p.Id });
      }
      if (p.PropertyType === "Embedded" && p.InnerNodeTypes) {
        for (const target of p.InnerNodeTypes) if (eligible.has(target)) push({ id: p.Id + ">" + target, kind: "embeds", from: t.Id, to: target, label: p.CodeName, propertyId: p.Id });
      }
    }
  }
  for (const r of Object.values(ctx.model.Relations)) {
    const meta = relationMeta[r.RelationType];
    let n = 0;
    for (const s of r.SourceTypes) {
      for (const t of r.TargetTypes) {
        if (!eligible.has(s) || !eligible.has(t) || n++ > 12) continue;
        push({ id: r.Id + ":" + s + ">" + t, kind: "relation", from: s, to: t, label: r.CodeName, relationId: r.Id, directed: meta?.directed ?? true });
      }
    }
  }
  // only the kinds switched on connect anything: a kind switched off neither draws nor unfolds
  const links = all.filter((l) => edges.has(l.kind));
  const neighbors = new Map<string, Set<string>>();
  const add = (a: string, b: string) => {
    if (!neighbors.has(a)) neighbors.set(a, new Set());
    neighbors.get(a)!.add(b);
  };
  for (const l of links) {
    if (l.from === l.to) continue;
    add(l.from, l.to);
    add(l.to, l.from);
  }
  // the properties a type shows as leaves: those not already drawn as a line to another type
  const leaves = new Map<string, PropertyJson[]>();
  if (edges.has("property")) {
    for (const t of types) {
      leaves.set(
        t.Id,
        Object.values(t.Properties).filter((p) => !p.Internal && !p.RelationId && p.PropertyType !== "Reference" && p.PropertyType !== "References" && p.PropertyType !== "Embedded" && p.PropertyType !== "Relation"),
      );
    }
  }
  return { eligible, types, links, neighbors, leaves };
}

/**
 * What is on screen right now: the start type, every type reachable from it through unfolded types,
 * the lines between them, and the leaves of the unfolded types.
 */
export function unfold(world: World, ctx: EditorContext, expanded: Set<string>, rootId: string | null): { nodes: GraphNode[]; links: GraphLink[] } {
  const nodes: GraphNode[] = [];
  const links: GraphLink[] = [];
  if (rootId === null) return { nodes, links };
  const shown = new Set<string>([rootId]);
  const open = new Set([...expanded].filter((id) => world.eligible.has(id)));
  // an unfolded type shows its neighbours; a type only reachable through a folded one is not there,
  // so the unfolded set is walked from the start type
  const reach = new Set<string>([rootId]);
  const queue = [rootId];
  while (queue.length > 0) {
    const id = queue.shift()!;
    if (!open.has(id)) continue;
    for (const n of world.neighbors.get(id) ?? []) {
      if (reach.has(n)) continue;
      reach.add(n);
      queue.push(n);
    }
  }
  for (const id of reach) shown.add(id);
  const openShown = new Set([...open].filter((id) => shown.has(id)));
  for (const id of shown) {
    const type = ctx.model.NodeTypes[id];
    if (!type) continue;
    const hiddenTypes = [...(world.neighbors.get(id) ?? [])].filter((n) => !shown.has(n)).length;
    const hiddenLeaves = openShown.has(id) ? 0 : (world.leaves.get(id)?.length ?? 0);
    const count = ctx.typeCounts[id] ?? 0;
    const r = Math.min(30, 15 + Math.log10(count + 1) * 4 + Math.min(4, Object.keys(type.Properties).length / 6));
    nodes.push({ id, kind: "type", type, root: id === rootId, r, hidden: hiddenTypes + hiddenLeaves, open: openShown.has(id) });
  }
  links.push(...world.links.filter((l) => shown.has(l.from) && shown.has(l.to)));
  for (const id of openShown) {
    for (const p of world.leaves.get(id) ?? []) {
      nodes.push({ id: leafId(p.Id), kind: "property", property: p, ownerId: id, r: 4.5 });
      links.push({ id: "stem:" + p.Id, kind: "property", from: id, to: leafId(p.Id), propertyId: p.Id });
    }
  }
  return { nodes, links };
}

// ---- what is remembered ----

export function remember(key: string, value: unknown) {
  try {
    if (value === null || value === undefined) localStorage.removeItem(key);
    else localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // storage unavailable: the choice is not remembered
  }
}

export function recall(key: string): unknown {
  try {
    return JSON.parse(localStorage.getItem(key) ?? "null");
  } catch {
    return null;
  }
}

export function readRoot(storeId: string): string | null {
  const v = recall(rootKey(storeId));
  return typeof v === "string" ? v : null;
}

export function readExpanded(storeId: string): Set<string> {
  const v = recall(expandedKey(storeId));
  return new Set(Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : []);
}

export function readEdges(storeId: string): Set<EdgeKind> {
  const v = recall(edgesKey(storeId));
  const all = edgeKinds.map((e) => e.kind);
  if (!Array.isArray(v)) return new Set(all);
  return new Set(v.filter((x): x is EdgeKind => typeof x === "string" && (all as string[]).includes(x)));
}
