import { IconChevronLeft, IconChevronRight } from "@tabler/icons-react";
import { sections, type Section } from "../navigation";
import type { DatabaseInfo } from "../server/serverInfo";

interface SidebarProps {
  collapsed: boolean;
  onToggleCollapsed: () => void;
  databases: DatabaseInfo[];
  activeDbName: string | null;
  activeSectionId: string;
  onSelectSection: (id: string) => void;
}

export function Sidebar({ collapsed, onToggleCollapsed, databases, activeDbName, activeSectionId, onSelectSection }: SidebarProps) {
  const errors = databases.filter((db) => db.state === "Error").length;
  // the one live badge so far: the server "Databases" section shows how many databases failed
  const badgeFor = (s: Section) =>
    s.id === "server-databases" && errors > 0 ? { text: errors === 1 ? "1 error" : `${errors} errors`, danger: true } : null;
  return (
    <div className="sidebar-wrap">
      <aside className={"sidebar" + (collapsed ? " collapsed" : "")}>
        <NavGroup
          label={activeDbName ? `Database — ${activeDbName}` : "Database"}
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
