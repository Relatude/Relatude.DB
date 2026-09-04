import { useState } from "react";
import { IconChevronDown, IconMoon, IconPlayerPause, IconRefresh, IconSun } from "@tabler/icons-react";
import { sections } from "../navigation";
import { describeInterval, refreshSteps, setRefreshInterval, useRefreshInterval } from "../refresh";
import type { DatabaseInfo } from "../server/serverInfo";
import type { Theme } from "../theme";
import { Logo, LogoMark } from "./Logo";
import { RevertControl } from "./RevertControl";

interface HeaderProps {
  databases: DatabaseInfo[];
  activeDb: DatabaseInfo | null;
  onSelectDb: (id: string) => void;
  activeSectionId: string;
  theme: Theme;
  onToggleTheme: () => void;
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
      {/* the revert window is a mode the whole database is in, so it sits with the database rather
          than on the page that happens to be open */}
      {p.activeDb?.state === "Open" && <RevertControl key={p.activeDb.id} db={p.activeDb} />}
      <RefreshRate />
      <button className="icon-button" onClick={p.onToggleTheme} title={p.theme === "dark" ? "Switch to light theme" : "Switch to dark theme"}>
        {p.theme === "dark" ? <IconSun size={18} stroke={1.8} /> : <IconMoon size={18} stroke={1.8} />}
      </button>
      <DbSwitcher databases={p.databases} activeDb={p.activeDb} onSelectDb={p.onSelectDb} />
    </header>
  );
}

/**
 * How often the whole UI refreshes itself. It sits in the top bar because it is not a property of any
 * one page: every page that follows something live (the dashboard counters, the conversion queue, the
 * task queues, the system trace, a log that is being watched) polls on this cadence, and the reason to
 * turn it down - a busy database, a remote connection, a laptop on battery - is never about one page
 * either. All the way to the left is off, which leaves the pages exactly as they are until something
 * is refreshed by hand.
 *
 * One icon in the bar, the slider in the panel behind it: the cadence is set once and then read at a
 * glance, so the bar carries the state (spinning arrows or a pause mark, and the interval next to it)
 * while the control itself stays out of the way.
 */
function RefreshRate() {
  const interval = useRefreshInterval();
  const [open, setOpen] = useState(false);
  const index = Math.max(0, refreshSteps.indexOf(interval as (typeof refreshSteps)[number]));
  const paused = interval === 0;
  return (
    <div className="refresh-rate">
      <button
        className={"icon-button refresh-rate-button" + (paused ? " paused" : "") + (open ? " active" : "")}
        onClick={() => setOpen(!open)}
        title={paused ? "Live updates are off" : `Pages refresh every ${describeInterval(interval)}`}
        aria-label="Refresh rate"
      >
        {paused ? <IconPlayerPause size={18} stroke={1.8} /> : <IconRefresh size={18} stroke={1.8} />}
        <span className="refresh-rate-badge">{describeInterval(interval)}</span>
      </button>
      {open && (
        <>
          <div className="db-menu-backdrop" onClick={() => setOpen(false)} />
          <div className="db-menu refresh-rate-panel">
            <div className="refresh-rate-head">
              <span>Refresh rate</span>
              <span className="refresh-rate-value">{describeInterval(interval)}</span>
            </div>
            <input
              type="range"
              min={0}
              max={refreshSteps.length - 1}
              step={1}
              value={index}
              aria-label="Refresh rate"
              onChange={(e) => setRefreshInterval(refreshSteps[Number(e.target.value)])}
            />
            <div className="refresh-rate-scale">
              <span>Off</span>
              <span>Fastest</span>
            </div>
            <div className="muted refresh-rate-note">
              {paused
                ? "Nothing polls the server; the refresh buttons on each page still work."
                : `Every page that follows something live asks again every ${describeInterval(interval)}. Pages that ask for something expensive keep a slower floor of their own.`}
            </div>
          </div>
        </>
      )}
    </div>
  );
}

function describeDb(db: DatabaseInfo): string {
  return db.state + (db.nodeCount != null ? ` · ${db.nodeCount.toLocaleString("en-US")} nodes` : "");
}

/** The one-line state of a database, with the open revert window marked in the window's own colour. */
function DbState({ db }: { db: DatabaseInfo }) {
  return (
    <>
      {describeDb(db)}
      {db.revertWindow && <span className="db-revert">revert window</span>}
    </>
  );
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
          <div className="db-state">{activeDb ? <DbState db={activeDb} /> : ""}</div>
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
                <span className="db-meta">
                  <DbState db={db} />
                </span>
              </button>
            ))}
          </div>
        </>
      )}
    </div>
  );
}
