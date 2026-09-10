import { indexMarks, kindMeta, propertyColor } from "../DatamodelIcons";
import { headerHeight, rowHeight, type Box } from "./model";

interface Props {
  box: Box;
  color: string;
  selected: boolean;
  selectedProperty: string | null;
  dim: boolean;
  open: boolean;
  onPointerDown: (e: React.PointerEvent) => void;
  onSelectProperty: (propertyId: string) => void;
  onToggle: () => void;
}

/**
 * One type as a box: the header in its source's colour with the kind, an "inner" badge when the type
 * only exists embedded and the name; then a row per property, with its type's colour, the marks for
 * how it is indexed, and its property type. A row selects the property; the box selects the type.
 */
export function DiagramNode({ box, color, selected, selectedProperty, dim, open, onPointerDown, onSelectProperty, onToggle }: Props) {
  const kind = kindMeta[box.type.ModelType] ?? kindMeta.Class;
  // the name takes what the kind and the badge leave, so it never runs into them
  const kindWidth = 12 + kind.label.length * 6.6;
  const badgeX = kindWidth + 8;
  const badgeWidth = box.type.IsInnerNode ? 36 : 0;
  const titleChars = Math.max(6, Math.floor((box.w - 10 - badgeX - badgeWidth - 8) / 7.2));
  const footer = box.more > 0 || open;
  return (
    <g transform={`translate(${box.x} ${box.y})`} className={"dm-node" + (box.type.IsInnerNode ? " inner" : "") + (box.ghost ? " ghost" : "") + (selected ? " selected" : "") + (dim ? " dim" : "")} onPointerDown={onPointerDown}>
      <rect width={box.w} height={box.h} rx={8} className="dm-node-body" />
      <path d={`M0 8 a8 8 0 0 1 8 -8 h${box.w - 16} a8 8 0 0 1 8 8 v${headerHeight - 8} h-${box.w} z`} fill={color} className="dm-node-head" />
      <text x={12} y={headerHeight / 2 + 4.5} className="dm-node-kind" fill="#fff" opacity={0.85}>
        {kind.label}
      </text>
      {box.type.IsInnerNode && (
        <g className="dm-node-badge">
          <title>Only exists embedded inside another node</title>
          <rect x={badgeX} y={6} width={badgeWidth} height={headerHeight - 12} rx={5} />
          <text x={badgeX + badgeWidth / 2} y={headerHeight / 2 + 3.5} textAnchor="middle">
            inner
          </text>
        </g>
      )}
      <text x={box.w - 10} y={headerHeight / 2 + 4.5} className="dm-node-title" textAnchor="end" fill="#fff">
        {box.type.CodeName.length > titleChars ? box.type.CodeName.slice(0, titleChars - 1) + "…" : box.type.CodeName}
      </text>
      {box.rows.map((r, i) => {
        const top = headerHeight + 4 + i * rowHeight;
        const mid = top + rowHeight / 2;
        // the index marks sit between the name and the type, and the name gives up the room
        const marksWidth = r.marks.length * 11;
        const nameRoom = Math.max(6, Math.floor((box.w - 34 - marksWidth - 62) / 6.3));
        return (
          <g key={r.id} className={"dm-node-row" + (selectedProperty === r.id ? " selected" : "")} onPointerDown={(e) => e.stopPropagation()} onClick={() => onSelectProperty(r.id)}>
            <rect x={4} y={top} width={box.w - 8} height={rowHeight} rx={3} className="dm-node-row-bg" />
            <circle cx={14} cy={mid} r={3.5} fill={propertyColor(r.propertyType)} />
            <text x={24} y={mid + 4} className="dm-node-prop">
              {r.name.length > nameRoom ? r.name.slice(0, nameRoom - 1) + "…" : r.name}
            </text>
            {r.marks.map((key, m) => {
              const mark = indexMarks.find((x) => x.key === key)!;
              return (
                <g key={key} transform={`translate(${26 + Math.min(r.name.length, nameRoom) * 6.3 + m * 11} ${mid - 5.5})`} className="dm-node-mark">
                  <title>{mark.title}</title>
                  <mark.icon size={11} stroke={2.4} color={mark.color} />
                </g>
              );
            })}
            <text x={box.w - 10} y={mid + 4} className="dm-node-proptype" textAnchor="end">
              {r.propertyType}
            </text>
          </g>
        );
      })}
      {footer && (
        <g className="dm-node-row dm-node-more" onPointerDown={(e) => e.stopPropagation()} onClick={onToggle}>
          <rect x={4} y={headerHeight + 4 + box.rows.length * rowHeight} width={box.w - 8} height={rowHeight} rx={3} className="dm-node-row-bg" />
          <text x={24} y={headerHeight + 4 + box.rows.length * rowHeight + rowHeight / 2 + 4} className="dm-node-proptype">
            {box.more > 0 ? `+${box.more} more — click to show` : "show fewer"}
          </text>
        </g>
      )}
      {box.rows.length === 0 && box.more === 0 && (
        <text x={24} y={headerHeight + 4 + rowHeight / 2 + 4} className="dm-node-proptype">
          no properties
        </text>
      )}
    </g>
  );
}
