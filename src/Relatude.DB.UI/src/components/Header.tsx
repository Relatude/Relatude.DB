import { useState } from "react";
import {
  IconChevronDown,
  IconDotsVertical,
  IconLayoutSidebarLeftCollapse,
  IconLayoutSidebarLeftExpand,
  IconLogout,
  IconMoon,
  IconSearch,
  IconSun,
} from "@tabler/icons-react";
import { sections } from "../navigation";
import type { DatabaseInfo } from "../server/serverInfo";
import type { Theme } from "../theme";
import { useConnectionState } from "../server/hooks";
import { Logo } from "./Logo";

interface HeaderProps {
  databases: DatabaseInfo[];
  activeDb: DatabaseInfo | null;
  onSelectDb: (id: string) => void;
  activeSectionId: string;
  theme: Theme;
  onToggleTheme: () => void;
  navOpen: boolean;
  onToggleNav: () => void;
  onLogout: () => void;
}

export function Header(p: HeaderProps) {
  const connection = useConnectionState();
  const section = sections.find((s) => s.id === p.activeSectionId);
  const isServerScope = section?.scope === "server";
  return (
    <header className="header">
      <div className="logo" title="Relatude.DB">
        <Logo height={40} />
      </div>
      <button className="icon-button" onClick={p.onToggleNav} title={p.navOpen ? "Collapse menu" : "Expand menu"}>
        {p.navOpen ? <IconLayoutSidebarLeftCollapse size={18} stroke={1.8} /> : <IconLayoutSidebarLeftExpand size={18} stroke={1.8} />}
      </button>
      <div className="header-divider" />
      <DbSwitcher databases={p.databases} activeDb={p.activeDb} onSelectDb={p.onSelectDb} />
      <nav className="breadcrumb" aria-label="Breadcrumb">
        <span className="crumb">{isServerScope ? "Server" : "Database"}</span>
        {!isServerScope && p.activeDb && (
          <>
            <span className="crumb">/</span>
            <span className="crumb">{p.activeDb.name}</span>
          </>
        )}
        <span className="crumb">/</span>
        <span className="current">{section?.label}</span>
      </nav>
      <div className="header-spacer" />
      <div className="search-box">
        <IconSearch size={15} stroke={1.8} />
        <span>Search / jump to</span>
        <kbd>Ctrl K</kbd>
      </div>
      <span className={"stream-dot" + (connection === "open" ? " open" : "")} title={"stream: " + connection} />
      <button className="icon-button" onClick={p.onToggleTheme} title={p.theme === "dark" ? "Switch to light theme" : "Switch to dark theme"}>
        {p.theme === "dark" ? <IconSun size={18} stroke={1.8} /> : <IconMoon size={18} stroke={1.8} />}
      </button>
      <MoreMenu onLogout={p.onLogout} />
    </header>
  );
}

function describeDb(db: DatabaseInfo): string {
  return db.state + (db.nodeCount != null ? ` · ${db.nodeCount.toLocaleString("en-US")} nodes` : "");
}

function DbSwitcher({
  databases,
  activeDb,
  onSelectDb,
}: {
  databases: DatabaseInfo[];
  activeDb: DatabaseInfo | null;
  onSelectDb: (id: string) => void;
}) {
  const [open, setOpen] = useState(false);
  return (
    <div className="db-switcher">
      <button className="db-switcher-button" onClick={() => setOpen(!open)} disabled={databases.length === 0}>
        <span className={"state-dot " + (activeDb?.state ?? "closed").toLowerCase()} />
        <span>
          <div className="db-name">{activeDb?.name ?? "No databases"}</div>
          <div className="db-state">{activeDb ? describeDb(activeDb) : ""}</div>
        </span>
        <span className="chevron">
          <IconChevronDown size={16} stroke={1.8} />
        </span>
      </button>
      {open && (
        <>
          <div className="db-menu-backdrop" onClick={() => setOpen(false)} />
          <div className="db-menu">
            {databases.map((db) => (
              <button
                key={db.id}
                className="db-menu-item"
                onClick={() => {
                  onSelectDb(db.id);
                  setOpen(false);
                }}
              >
                <span className={"state-dot " + db.state.toLowerCase()} />
                <span className="db-name">{db.name}</span>
                <span className="db-meta">{describeDb(db)}</span>
              </button>
            ))}
          </div>
        </>
      )}
    </div>
  );
}

function MoreMenu({ onLogout }: { onLogout: () => void }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="more-menu">
      <button className="icon-button" title="More" onClick={() => setOpen(!open)}>
        <IconDotsVertical size={18} stroke={1.8} />
      </button>
      {open && (
        <>
          <div className="db-menu-backdrop" onClick={() => setOpen(false)} />
          <div className="db-menu more-menu-list">
            <button
              className="db-menu-item"
              onClick={() => {
                setOpen(false);
                onLogout();
              }}
            >
              <IconLogout size={16} stroke={1.8} />
              Log out
            </button>
          </div>
        </>
      )}
    </div>
  );
}
