import { useEffect, useState } from "react";
import { ConversionsSection } from "./components/ConversionsSection";
import { DashboardSection } from "./components/DashboardSection";
import { DatabasesSection } from "./components/DatabasesSection";
import { DatamodelSection } from "./components/DatamodelSection";
import { DialogHost } from "./components/DialogHost";
import { FilesSection } from "./components/FilesSection";
import { Header } from "./components/Header";
import { Login } from "./components/Login";
import { LogsSection } from "./components/LogsSection";
import { Overview } from "./components/Overview";
import { QuerySection } from "./components/QuerySection";
import { SettingsSection } from "./components/SettingsSection";
import { Sidebar } from "./components/Sidebar";
import { StorageSection } from "./components/StorageSection";
import { TasksSection } from "./components/TasksSection";
import { sections } from "./navigation";
import { isLoggedIn, logout } from "./server/auth";
import { disconnect, subscribe, subscribeResync, subscribeUnauthorized } from "./server/channel";
import { fetchServerInfo, type DatabaseInfo, type ServerInfo } from "./server/serverInfo";
import { applyTheme, getInitialTheme } from "./theme";

// on localhost the server usually skips authentication (NoLoginRequiredForLocalhost);
// ?login forces the login screen so it can be seen and styled during development
const forceLogin = new URLSearchParams(window.location.search).has("login");

type AuthState = "checking" | "login" | "ready";

export function App() {
  const [theme, setTheme] = useState(getInitialTheme);
  const [auth, setAuth] = useState<AuthState>("checking");
  const [serverInfo, setServerInfo] = useState<ServerInfo | null>(null);
  const [activeDbId, setActiveDbId] = useState<string | null>(null);
  const [activeSectionId, setActiveSectionId] = useState("dashboard");
  const [navOpen, setNavOpen] = useState(true);
  useEffect(() => applyTheme(theme), [theme]);
  useEffect(() => {
    if (forceLogin) {
      setAuth("login");
      return;
    }
    isLoggedIn()
      .then((loggedIn) => setAuth(loggedIn ? "ready" : "login"))
      .catch(() => setAuth("login"));
  }, []);
  useEffect(
    () =>
      // a 401 from the channel means the session expired: back to the login screen
      subscribeUnauthorized(() => {
        disconnect();
        setAuth("login");
      }),
    [],
  );
  function applyContainers(containers: DatabaseInfo[]) {
    setActiveDbId((prev) => (prev && containers.some((c) => c.id === prev) ? prev : (containers[0]?.id ?? null)));
  }
  useEffect(() => {
    if (auth !== "ready") return;
    let cancelled = false;
    const load = () =>
      fetchServerInfo()
        .then((info) => {
          if (cancelled) return;
          setServerInfo(info);
          applyContainers(info.containers);
        })
        .catch(() => {}); // a 401 is handled by subscribeUnauthorized; other errors leave the shell empty
    load();
    // after a stream reconnect (e.g. a server restart) events were missed: fetch a fresh snapshot
    const unsubscribeResync = subscribeResync(load);
    return () => {
      cancelled = true;
      unsubscribeResync();
    };
  }, [auth]);
  useEffect(() => {
    if (auth !== "ready") return;
    // the server broadcasts the container list whenever it changes (state, node count, name)
    return subscribe<DatabaseInfo[]>("containers", (containers) => {
      setServerInfo((prev) => ({ version: prev?.version ?? "", upTimeMs: prev?.upTimeMs ?? 0, containers }));
      applyContainers(containers);
    });
  }, [auth]);
  async function handleLogout() {
    try {
      await logout();
    } finally {
      disconnect();
      setServerInfo(null);
      setAuth("login");
    }
  }
  if (auth === "checking") return null;
  if (auth === "login") {
    return <Login onLoggedIn={() => setAuth("ready")} theme={theme} onToggleTheme={() => setTheme(theme === "dark" ? "light" : "dark")} />;
  }
  const databases = serverInfo?.containers ?? [];
  const activeDb = databases.find((db) => db.id === activeDbId) ?? null;
  const section = sections.find((s) => s.id === activeSectionId)!;
  // the revert window belongs to the database, not to a page: it is controlled from the top bar and
  // frames every page of the database while it is open
  const inRevert = section.scope === "database" && activeDb !== null && activeDb.state === "Open" && !!activeDb.revertWindow;
  return (
    <div className="shell">
      <Header
        databases={databases}
        activeDb={activeDb}
        onSelectDb={setActiveDbId}
        activeSectionId={activeSectionId}
        theme={theme}
        onToggleTheme={() => setTheme(theme === "dark" ? "light" : "dark")}
        navCollapsed={!navOpen}
        onToggleNav={() => setNavOpen(!navOpen)}
      />
      <div className="shell-body">
        <Sidebar
          collapsed={!navOpen}
          onToggleCollapsed={() => setNavOpen(!navOpen)}
          databases={databases}
          activeDb={activeDb}
          activeSectionId={activeSectionId}
          onSelectSection={setActiveSectionId}
          onLogout={handleLogout}
        />
        <main className={"content" + (inRevert ? " in-revert" : "")}>
          <div className="content-body">
          {activeSectionId === "dashboard" && activeDb ? (
            <DashboardSection key={activeDb.id} db={activeDb} />
          ) : activeSectionId === "server-databases" ? (
            // picking a database here switches the pages on the left to it, which is what someone
            // who just started one is about to want
            <DatabasesSection
              onSelectDb={(id) => {
                setActiveDbId(id);
                setActiveSectionId("dashboard");
              }}
            />
          ) : activeSectionId === "server-overview" ? (
            <Overview />
          ) : section.scope === "server" && (activeSectionId === "server-settings" || section.settingsSection) ? (
            // an entry that names part of the settings renders the settings page opened there
            <SettingsSection key={activeSectionId} focusSection={section.settingsSection} />
          ) : activeSectionId === "db-settings" && activeDb ? (
            <SettingsSection key={activeDb.id} storeId={activeDb.id} />
          ) : activeSectionId === "datamodel" && activeDb ? (
            <DatamodelSection key={activeDb.id} db={activeDb} />
          ) : activeSectionId === "logs" && activeDb ? (
            <LogsSection key={activeDb.id} db={activeDb} />
          ) : activeSectionId === "conversions" && activeDb ? (
            <ConversionsSection key={activeDb.id} db={activeDb} />
          ) : activeSectionId === "query" && activeDb ? (
            <QuerySection key={activeDb.id} db={activeDb} />
          ) : activeSectionId === "files" && activeDb ? (
            <FilesSection key={activeDb.id} db={activeDb} />
          ) : activeSectionId === "storage" && activeDb ? (
            <StorageSection key={activeDb.id} db={activeDb} />
          ) : activeSectionId === "tasks" && activeDb ? (
            <TasksSection key={activeDb.id} db={activeDb} />
          ) : (
            <div className="placeholder">
              <span>{section.label} — not implemented yet</span>
            </div>
          )}
          </div>
        </main>
      </div>
      <DialogHost />
    </div>
  );
}
