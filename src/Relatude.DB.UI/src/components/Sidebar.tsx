import { useEffect, useState } from "react";
import { IconChevronLeft, IconChevronRight, IconDeviceDesktop, IconLogout, IconUser } from "@tabler/icons-react";
import { sections, type Section } from "../navigation";
import { fetchWhoAmI, type DatabaseInfo, type WhoAmI } from "../server/serverInfo";

interface SidebarProps {
  collapsed: boolean;
  onToggleCollapsed: () => void;
  databases: DatabaseInfo[];
  activeDb: DatabaseInfo | null;
  activeSectionId: string;
  onSelectSection: (id: string) => void;
  onLogout: () => void;
}

export function Sidebar({ collapsed, onToggleCollapsed, databases, activeDb, activeSectionId, onSelectSection, onLogout }: SidebarProps) {
  const errors = databases.filter((db) => db.state === "Error").length;
  const conversions = activeDb?.conversionCount ?? 0;
  const tasks = activeDb?.taskCount ?? 0;
  // live badges: how many databases failed, and what the two background queues still owe
  const badgeFor = (s: Section) => {
    if (s.id === "server-databases" && errors > 0) return { text: errors === 1 ? "1 error" : `${errors} errors`, danger: true };
    if (s.id === "conversions" && conversions > 0) return { text: String(conversions), danger: false };
    if (s.id === "tasks" && tasks > 0) return { text: tasks > 9999 ? Math.round(tasks / 1000) + "k" : String(tasks), danger: false };
    return null;
  };
  return (
    <div className="sidebar-wrap">
      <aside className={"sidebar" + (collapsed ? " collapsed" : "")}>
        <NavGroup
          label={activeDb ? `Database — ${activeDb.name}` : "Database"}
          shortLabel="DB"
          items={sections.filter((s) => s.scope === "database" && !s.hidden)}
          badgeFor={badgeFor}
          activeSectionId={activeSectionId}
          onSelectSection={onSelectSection}
        />
        <NavGroup
          label="Server"
          shortLabel="SRV"
          items={sections.filter((s) => s.scope === "server" && !s.hidden)}
          badgeFor={badgeFor}
          activeSectionId={activeSectionId}
          onSelectSection={onSelectSection}
        />
        <SignedIn collapsed={collapsed} onLogout={onLogout} />
      </aside>
      <button className="nav-toggle" onClick={onToggleCollapsed} title={collapsed ? "Expand menu" : "Collapse menu"}>
        {collapsed ? <IconChevronRight size={14} stroke={2} /> : <IconChevronLeft size={14} stroke={2} />}
      </button>
    </div>
  );
}

/**
 * Who is signed in, at the foot of the rail, and the way out.
 *
 * Two ways into this UI and they are not the same thing. A token is a session, and ending it is what
 * the Log out button does. The localhost bypass (NoLoginRequiredForLocalhost) is not a session at
 * all: nothing was signed in, so there is nothing to sign out of, and a button there would delete a
 * cookie nobody is reading and leave the next request just as welcome. So it is not offered - what
 * is shown instead is why no login was asked for.
 */
function SignedIn({ collapsed, onLogout }: { collapsed: boolean; onLogout: () => void }) {
  const [who, setWho] = useState<WhoAmI | null>(null);
  useEffect(() => {
    // asked once: who is looking does not change under a session, and the 401 handler takes the
    // page back to the login screen when it ends
    fetchWhoAmI()
      .then(setWho)
      .catch(() => {});
  }, []);
  if (!who) return <div className="sidebar-footer" />;
  const local = who.userName == null && who.viaLocalhost;
  const name = who.userName ?? (local ? "Local access" : "Not signed in");
  const hint = local ? `no login required on ${who.machine}` : who.userName ? "signed in" : "";
  return (
    <div className="sidebar-footer" title={collapsed ? name + (hint ? " \u2014 " + hint : "") : undefined}>
      <span className="sidebar-user-icon">{local ? <IconDeviceDesktop size={16} stroke={1.7} /> : <IconUser size={16} stroke={1.7} />}</span>
      <span className="sidebar-user">
        <span className="sidebar-user-name">{name}</span>
        {hint && <span className="sidebar-user-hint">{hint}</span>}
      </span>
      {/* shown either way, so the way out is always where it is expected - but only live when there
          is a session to end. Under the bypass it would send this browser to a login screen it
          cannot pass, which is worse than a button that says why it is off */}
      <button
        className="icon-button sidebar-logout"
        onClick={onLogout}
        disabled={!who.canLogOut}
        title={who.canLogOut ? "Log out" : `No login was required from ${who.machine}, so there is no session to end`}
      >
        <IconLogout size={16} stroke={1.8} />
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
