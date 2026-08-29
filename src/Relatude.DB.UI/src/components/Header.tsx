import { useState } from "react";
import { IconChevronDown, IconDotsVertical, IconLogout, IconMoon, IconSun } from "@tabler/icons-react";
import { sections } from "../navigation";
import type { DatabaseInfo } from "../server/serverInfo";
import type { Theme } from "../theme";
import { Logo, LogoMark } from "./Logo";

interface HeaderProps {
  databases: DatabaseInfo[];
  activeDb: DatabaseInfo | null;
  onSelectDb: (id: string) => void;
  activeSectionId: string;
  theme: Theme;
  onToggleTheme: () => void;
  onLogout: () => void;
  navCollapsed: boolean;
  onToggleNav: () => void;
}

export function Header(p: HeaderProps) {
  const section = sections.find((s) => s.id === p.activeSectionId);
  const isServerScope = section?.scope === "server";
  return (
    <header className="header">
      {/* sized like the rail (and animated with it) so its right border and the rail border form one line */}
      <button
        className={"header-brand" + (p.navCollapsed ? " collapsed" : "")}
        onClick={p.onToggleNav}
        title={p.navCollapsed ? "Expand menu" : "Collapse menu"}
      >
        <span className="logo-full">
          <Logo height={40} />
        </span>
        <span className="logo-mark">
          <LogoMark height={13} />
        </span>
      </button>
      <div className="header-title">
        <div className="page-kicker">{isServerScope ? "Server" : (p.activeDb?.name ?? "Database")}</div>
        <h2>{section?.label}</h2>
      </div>
      <div className="header-spacer" />
      <button className="icon-button" onClick={p.onToggleTheme} title={p.theme === "dark" ? "Switch to light theme" : "Switch to dark theme"}>
        {p.theme === "dark" ? <IconSun size={18} stroke={1.8} /> : <IconMoon size={18} stroke={1.8} />}
      </button>
      <DbSwitcher databases={p.databases} activeDb={p.activeDb} onSelectDb={p.onSelectDb} />
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
