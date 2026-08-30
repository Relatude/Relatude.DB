import { IconChevronLeft, IconChevronRight } from "@tabler/icons-react";
import { sections, type Section } from "../navigation";
import type { DatabaseInfo } from "../server/serverInfo";

interface SidebarProps {
  collapsed: boolean;
  onToggleCollapsed: () => void;
  databases: DatabaseInfo[];
  activeDb: DatabaseInfo | null;
  activeSectionId: string;
  onSelectSection: (id: string) => void;
}

export function Sidebar({ collapsed, onToggleCollapsed, databases, activeDb, activeSectionId, onSelectSection }: SidebarProps) {
  const errors = databases.filter((db) => db.state === "Error").length;
  const conversions = activeDb?.conversionCount ?? 0;
  // live badges: how many databases failed, and what the conversion queue still owes
  const badgeFor = (s: Section) => {
    if (s.id === "server-databases" && errors > 0) return { text: errors === 1 ? "1 error" : `${errors} errors`, danger: true };
    if (s.id === "conversions" && conversions > 0) return { text: String(conversions), danger: false };
    return null;
  };
  return (
    <div className="sidebar-wrap">
      <aside className={"sidebar" + (collapsed ? " collapsed" : "")}>
        <NavGroup
          label={activeDb ? `Database — ${activeDb.name}` : "Database"}
          shortLabel="DB"
          items={sections.filter((s) => s.scope === "database")}
          badgeFor={badgeFor}
          activeSectionId={activeSectionId}
          onSelectSection={onSelectSection}
        />
        <NavGroup
          label="Server"
          shortLabel="SRV"
          items={sections.filter((s) => s.scope === "server")}
          badgeFor={badgeFor}
          activeSectionId={activeSectionId}
          onSelectSection={onSelectSection}
        />
        <div className="sidebar-footer">
          {databases.length > 0 &&
            `${databases.length} ${databases.length === 1 ? "database" : "databases"}${errors > 0 ? ` · ${errors} needs attention` : ""}`}
        </div>
      </aside>
      <button className="nav-toggle" onClick={onToggleCollapsed} title={collapsed ? "Expand menu" : "Collapse menu"}>
        {collapsed ? <IconChevronRight size={14} stroke={2} /> : <IconChevronLeft size={14} stroke={2} />}
      </button>
    </div>
  );
}

interface NavGroupProps {
  label: string;
  shortLabel: string;
  items: Section[];
  badgeFor: (s: Section) => { text: string; danger: boolean } | null;
  activeSectionId: string;
  onSelectSection: (id: string) => void;
}

function NavGroup({ label, shortLabel, items, badgeFor, activeSectionId, onSelectSection }: NavGroupProps) {
  return (
    <div className="nav-group">
      <div className="nav-group-label">
        <span className="full">{label}</span>
        <span className="short">{shortLabel}</span>
      </div>
      {items.map((section) => {
        const badge = badgeFor(section);
        return (
          <button
            key={section.id}
            className={"nav-item" + (section.id === activeSectionId ? " active" : "")}
            onClick={() => onSelectSection(section.id)}
            title={section.label}
          >
            <section.icon size={16} stroke={1.8} />
            <span className="label">{section.label}</span>
            {badge && <span className={"badge" + (badge.danger ? " danger" : "")}>{badge.text}</span>}
          </button>
        );
      })}
    </div>
  );
}
