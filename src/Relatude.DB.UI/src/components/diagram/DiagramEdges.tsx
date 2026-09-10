import { embeddedColor, relationColor } from "../DatamodelIcons";
import type { Selection } from "../DatamodelEditors";
import { blend, borderPoint, cornerRadius, labelPlace, orthoPath, plainPath, type Gap, type Pt, type Route } from "./router";
import type { Box, Edge } from "./model";

/** How much of a horizontal line is left out either side of a line crossing it. */
export const gapRadius = 3.5;
/** Together with the corner rounding, how close to a bend a crossing may still get a gap. */
export const gapClearance = gapRadius + cornerRadius + 1;

interface Props {
  edges: Edge[];
  boxById: Map<string, Box>;
  routes: Map<string, Route> | null;
  /** Where lines cross, once they are where they belong; nothing crosses cleanly while they travel. */
  gaps: Map<string, Gap[]> | null;
  /** Where each line set out from, while a rearrangement is on its way. */
  from: Map<string, Pt[]> | null;
  /** How far that rearrangement has got, 1 when there is none. */
  t: number;
  selectedType: string | null;
  selectedRelation: string | null;
  query: string;
  select: (selection: Selection) => void;
  onSegment: (e: React.PointerEvent, edge: Edge, route: Route, seg: number, select?: () => void) => void;
  record: (id: string, points: Pt[]) => void;
}

/** The arrowheads and the diamond of containment; the shapes are shared by every line of a kind. */
export function EdgeMarkers() {
  return (
    <defs>
      <marker id="dm-arrow-inherit" viewBox="0 0 12 12" refX="11" refY="6" markerWidth="12" markerHeight="12" orient="auto-start-reverse">
        <path d="M1 1 L11 6 L1 11 z" className="dm-marker-inherit" />
      </marker>
      <marker id="dm-arrow-relation" viewBox="0 0 12 12" refX="11" refY="6" markerWidth="9" markerHeight="9" orient="auto-start-reverse">
        <path d="M1 1 L11 6 L1 11 z" className="dm-marker-relation" />
      </marker>
      <marker id="dm-dot-relation" viewBox="0 0 12 12" refX="6" refY="6" markerWidth="7" markerHeight="7">
        <circle cx="6" cy="6" r="4" className="dm-marker-relation" />
      </marker>
      <marker id="dm-arrow-reference" viewBox="0 0 12 12" refX="11" refY="6" markerWidth="9" markerHeight="9" orient="auto-start-reverse">
        <path d="M1 1 L11 6 L1 11 z" className="dm-marker-reference" />
      </marker>
      <marker id="dm-arrow-embed" viewBox="0 0 12 12" refX="11" refY="6" markerWidth="9" markerHeight="9" orient="auto-start-reverse">
        <path d="M1 1 L11 6 L1 11 z" className="dm-marker-embed" />
      </marker>
      {/* the filled diamond sits at the owner end, as containment does in UML; the shape is symmetric so orient does not matter */}
      <marker id="dm-diamond-embed" viewBox="0 0 14 12" refX="0" refY="6" markerWidth="10" markerHeight="9" orient="auto">
        <path d="M0 6 L7 2 L14 6 L7 10 z" className="dm-marker-embed" />
      </marker>
      <style>{`.dm-marker-relation{fill:${relationColor}}.dm-marker-embed{fill:${embeddedColor}}`}</style>
    </defs>
  );
}

/**
 * Every line: inheritance (dashed, hollow arrow at the parent), a relation (solid, in the relation
 * colour, labelled, arrowhead when directed), a reference (dotted) and an embedded inner node (in the
 * embedded colour, with the filled diamond of containment at the type that owns the property). A
 * relation line selects its relation, a reference or embed line its property, and every piece of a
 * line is a handle to push it sideways.
 */
export function DiagramEdges({ edges, boxById, routes, gaps, from, t, selectedType, selectedRelation, query, select, onSegment, record }: Props) {
  const q = query.trim().toLowerCase();
  return (
    <>
      {edges.map((e) => {
        const a = boxById.get(e.from);
        const b = boxById.get(e.to);
        if (!a || !b) return null;
        const selfLoop = a.id === b.id;
        const highlighted = (selectedType !== null && (e.from === selectedType || e.to === selectedType)) || (e.kind === "relation" && selectedRelation !== null && e.id.startsWith(selectedRelation + ":"));
        const dim = (a.ghost && b.ghost) || (q.length > 0 && !(a.type.CodeName.toLowerCase().includes(q) || b.type.CodeName.toLowerCase().includes(q)));
        const route = routes?.get(e.id) ?? null;
        const start = t < 1 ? from?.get(e.id) : undefined;
        let pts: Pt[];
        let d: string;
        let label: { x: number; y: number; anchor: "middle" | "start" };
        let handles: Route | null = null;
        if (route && !start) {
          pts = route.points;
          d = route.orthogonal ? orthoPath(pts, gaps?.get(e.id), gapRadius) : plainPath(pts);
          label = labelPlace(pts);
          if (route.orthogonal) handles = route;
        } else {
          const ac = { x: a.x + a.w / 2, y: a.y + a.h / 2 };
          const bc = { x: b.x + b.w / 2, y: b.y + b.h / 2 };
          const p1 = selfLoop ? { x: a.x + a.w, y: a.y + 20 } : borderPoint(a, bc);
          const p2 = selfLoop ? { x: a.x + a.w, y: a.y + a.h - 20 } : borderPoint(b, ac);
          if (start) {
            pts = blend(start, route ? route.points : [p1, p2], t);
            d = plainPath(pts);
            const m = pts[pts.length >> 1];
            label = { x: m.x, y: m.y - 4, anchor: "middle" };
          } else {
            pts = [p1, p2];
            d = selfLoop ? `M${p1.x} ${p1.y} C ${p1.x + 60} ${p1.y - 10}, ${p2.x + 60} ${p2.y + 10}, ${p2.x} ${p2.y}` : `M${p1.x} ${p1.y} L${p2.x} ${p2.y}`;
            label = { x: (p1.x + p2.x) / 2 + (selfLoop ? 45 : 0), y: (p1.y + p2.y) / 2 - 4, anchor: "middle" };
          }
        }
        record(e.id, pts);
        const marker =
          e.kind === "inherits" ? "url(#dm-arrow-inherit)" : e.kind === "reference" ? "url(#dm-arrow-reference)" : e.kind === "embeds" ? "url(#dm-arrow-embed)" : e.directed ? "url(#dm-arrow-relation)" : "url(#dm-dot-relation)";
        const markerStart = e.kind === "embeds" ? "url(#dm-diamond-embed)" : e.kind === "relation" && e.symmetric ? "url(#dm-dot-relation)" : undefined;
        const choose =
          e.kind === "relation"
            ? () => select({ kind: "relation", id: e.id.split(":")[0] })
            : e.kind === "reference" || e.kind === "embeds"
              ? () => select({ kind: "property", id: e.propertyId, typeId: e.from })
              : undefined;
        return (
          <g
            key={e.id}
            className={"dm-edge " + e.kind + (highlighted ? " highlighted" : "") + (dim ? " dim" : "")}
            // the pan on the svg captures the pointer, and a captured pointer sends the click to the
            // svg rather than here, so a clickable edge has to keep the pan from starting
            onPointerDown={choose && ((ev) => ev.stopPropagation())}
            onClick={choose}
          >
            {handles && <title>{handles.manual ? "Placed by hand. Drag a piece to move it; click the line twice to have it routed again" : "Drag a piece of the line to move it sideways"}</title>}
            <path d={handles ? plainPath(pts) : d} className="dm-edge-hit" />
            <path d={d} className="dm-edge-line" markerEnd={marker} markerStart={markerStart} />
            {e.kind !== "inherits" && (
              <text x={label.x} y={label.y} className="dm-edge-label" textAnchor={label.anchor}>
                {e.label}
              </text>
            )}
            {handles?.points.slice(0, -1).map((p, i) => {
              const next = handles!.points[i + 1];
              const horizontal = Math.abs(p.y - next.y) < 0.01;
              return <path key={i} d={`M${p.x} ${p.y} L${next.x} ${next.y}`} className={"dm-edge-seg " + (horizontal ? "h" : "v")} onPointerDown={(ev) => onSegment(ev, e, handles!, i, choose)} />;
            })}
          </g>
        );
      })}
    </>
  );
}
