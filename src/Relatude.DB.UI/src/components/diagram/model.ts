/**
 * The model as boxes and lines, before anything is arranged or drawn. A box is a type with the first
 * of its properties; a line is inheritance, a relation, a reference property or an embedded inner
 * node. Nothing here knows where anything goes.
 */

import type { NodeTypeJson } from "../../server/datamodel";
import type { EdgeKind } from "./layouts";
import type { EditorContext } from "../DatamodelEditors";
import { indexMarks, relationMeta } from "../DatamodelIcons";

export const nodeWidth = 210;
export const headerHeight = 28;
export const rowHeight = 17;
/** Properties a box shows before it has to be opened. */
const maxRows = 9;
/** Lines drawn for one relation: a relation over many types either way would be a thicket. */
const maxRelationLines = 12;

export interface BoxRow {
  id: string;
  name: string;
  propertyType: string;
  marks: string[];
}

export interface Box {
  id: string;
  type: NodeTypeJson;
  x: number;
  y: number;
  w: number;
  h: number;
  rows: BoxRow[];
  more: number;
  ghost: boolean;
}

/** The four kinds of line, as the legend lists them - which is also where they are switched on and off. */
export const edgeKinds: { id: EdgeKind; label: string; hint: string }[] = [
  { id: "inherits", label: "inherits", hint: "the dashed line from a type to the one it inherits" },
  { id: "relation", label: "relation", hint: "the line for a relation between two types" },
  { id: "reference", label: "reference", hint: "the dotted line from a reference property to the type it points at" },
  { id: "embeds", label: "embedded", hint: "the line from a type to an inner type it embeds" },
];

export type Edge =
  | { kind: "inherits"; from: string; to: string; id: string }
  | { kind: "relation"; from: string; to: string; id: string; label: string; directed: boolean; symmetric: boolean }
  | { kind: "reference"; from: string; to: string; id: string; label: string; propertyId: string }
  | { kind: "embeds"; from: string; to: string; id: string; label: string; propertyId: string };

export function buildDiagram(ctx: EditorContext, visibleTypes: Set<string>, ghostTypes: Set<string>, openBoxes: Set<string>, kinds: Set<EdgeKind>): { boxes: Box[]; edges: Edge[] } {
  const shown = new Set([...visibleTypes, ...ghostTypes]);
  const types = Object.values(ctx.model.NodeTypes).filter((t) => t.Id !== ctx.baseTypeId && shown.has(t.Id));
  const boxes: Box[] = types.map((t) => {
    const props = Object.values(t.Properties).filter((p) => !p.Internal);
    const limit = openBoxes.has(t.Id) ? props.length : maxRows;
    const rows = props.slice(0, limit).map((p) => ({
      id: p.Id,
      name: p.CodeName,
      propertyType: p.PropertyType,
      marks: indexMarks.filter((m) => (m.key === "indexed" ? p.Indexed : m.key === "wordIndex" ? p.IndexedByWords : p.IndexedBySemantic)).map((m) => m.key),
    }));
    const more = props.length - rows.length;
    return {
      id: t.Id,
      type: t,
      x: 0,
      y: 0,
      w: nodeWidth,
      h: headerHeight + Math.max(1, rows.length + (more > 0 || openBoxes.has(t.Id) ? 1 : 0)) * rowHeight + 8,
      rows,
      more,
      ghost: ghostTypes.has(t.Id) && !visibleTypes.has(t.Id),
    };
  });
  const drawn = new Set(boxes.map((b) => b.id));
  const edges: Edge[] = [];
  for (const b of boxes) {
    for (const p of b.type.Parents ?? []) if (drawn.has(p)) edges.push({ kind: "inherits", from: b.id, to: p, id: b.id + ">" + p });
    for (const p of Object.values(b.type.Properties)) {
      if ((p.PropertyType === "Reference" || p.PropertyType === "References") && p.NodeTypes) {
        for (const target of p.NodeTypes) if (drawn.has(target)) edges.push({ kind: "reference", from: b.id, to: target, id: p.Id + ">" + target, label: p.CodeName, propertyId: p.Id });
      }
      if (p.PropertyType === "Embedded" && p.InnerNodeTypes) {
        for (const target of p.InnerNodeTypes) if (drawn.has(target)) edges.push({ kind: "embeds", from: b.id, to: target, id: p.Id + ">" + target, label: p.CodeName, propertyId: p.Id });
      }
    }
  }
  for (const r of Object.values(ctx.model.Relations)) {
    const meta = relationMeta[r.RelationType];
    let n = 0;
    for (const s of r.SourceTypes) {
      for (const t of r.TargetTypes) {
        if (!drawn.has(s) || !drawn.has(t) || n++ > maxRelationLines) continue;
        edges.push({ kind: "relation", from: s, to: t, id: r.Id + ":" + s + ">" + t, label: r.CodeName, directed: meta?.directed ?? true, symmetric: !(meta?.directed ?? true) });
      }
    }
  }
  // a kind switched off is left out here rather than hidden later, so the arrangement is of what is
  // drawn: with everything but inheritance off, the layered arrangement is the inheritance tree
  return { boxes, edges: edges.filter((e) => kinds.has(e.kind)) };
}
