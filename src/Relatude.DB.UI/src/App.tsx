import { useEffect, useState } from "react";
import { ApiSection } from "./components/ApiSection";
import { DashboardSection } from "./components/DashboardSection";
import { DatabasesSection } from "./components/DatabasesSection";
import { DatamodelSection } from "./components/DatamodelSection";
import { DialogHost } from "./components/DialogHost";
import { FilesStorageSection, type FilesStorageView } from "./components/FilesStorageSection";
import { Header } from "./components/Header";
import { LicenseSection } from "./components/LicenseSection";
import { Loading } from "./components/Loading";
import { Login } from "./components/Login";
import { LogsSection } from "./components/LogsSection";
import { CustomLogsSection } from "./components/CustomLogsSection";
import { Overview } from "./components/Overview";
import { QuerySection } from "./components/QuerySection";
import { isServiceTestView, ServiceTestsSection } from "./components/ServiceTestsSection";
import { SettingsSection } from "./components/SettingsSection";
import { Sidebar } from "./components/Sidebar";
import { TasksSection } from "./components/TasksSection";
import { sections } from "./navigation";
import { peekDatamodelTarget, peekQueryTarget, peekSearchTarget, peekSettingsTarget, useNavigationRequest } from "./navigate";
import { isLoggedIn, logout } from "./server/auth";
import { disconnect, subscribe, subscribeResync, subscribeUnauthorized } from "./server/channel";
import { fetchLicenseStatus, type LicenseStatus } from "./server/license";
import { fetchServerInfo, type DatabaseInfo, type ServerInfo } from "./server/serverInfo";
import { applyTheme, getInitialTheme } from "./theme";

// on localhost the server usually skips authentication (NoLoginRequiredForLocalhost);
// ?login forces the login screen so it can be seen and styled during development
const forceLogin = new URLSearchParams(window.location.search).has("login");

type AuthState = "checking" | "login" | "ready";

/** The section ids the files page owns: it is one page in three views (FilesStorageSection). */
const isFilesStorage = (id: string): id is FilesStorageView => id === "files" || id === "storage" || id === "conversions";

// The view of a module that was open last, for the modules with views (Section.parentId). The rail's
// entry opens the module on it, so someone who works in the file browser comes back to the file
// browser, and someone trying out a service to that service's test; the switch on the page and the
// global search still open the view they name.
const rememberedViewKeys: Record<string, string> = { storage: "storageView", "services-tests": "servicesTestView" };
// A module that is not a view itself opens on one of its views: Test services is its four tests, so
// its entry - in the rail or the global search - lands on the one open last, or else on this one.
const landingViews: Record<string, string> = { "services-tests": "services-sms" };
const moduleOf = (id: string) => sections.find((s) => s.id === id)?.parentId ?? id;
function rememberedView(moduleId: string): string {
  const key = rememberedViewKeys[moduleId];
  try {
    const view = key ? localStorage.getItem(key) : null;
    if (view && moduleOf(view) === moduleId) return view;
  } catch {
    // storage blocked: the module's own landing view
  }
  return landingViews[moduleId] ?? moduleId;
}

export function App() {
  const [theme, setTheme] = useState(getInitialTheme);
  const [auth, setAuth] = useState<AuthState>("checking");
  const [serverInfo, setServerInfo] = useState<ServerInfo | null>(null);
  // why the first server-info never came, so a database page can say so rather than wait forever
  const [serverInfoError, setServerInfoError] = useState<string | null>(null);
  const [activeDbId, setActiveDbId] = useState<string | null>(null);
  const [activeSectionId, setActiveSectionId] = useState("dashboard");
  const [navOpen, setNavOpen] = useState(true);
  // The rail marks the Relatude Services entry when there is no license or the keys are refused, so the one
  // place that says so is not a page nobody has opened. Asked once: it changes when someone changes
  // it, and the page hands the fresh answer back rather than making the rail poll for it.
  const [license, setLicense] = useState<LicenseStatus | null>(null);
  useEffect(() => applyTheme(theme), [theme]);
  useEffect(() => {
    const key = rememberedViewKeys[moduleOf(activeSectionId)];
    if (!key) return;
    try {
      localStorage.setItem(key, activeSectionId);
    } catch {
      // storage blocked: the module opens on its landing view next time
    }
  }, [activeSectionId]);
  // a click on the rail's entry for a module with views opens the module where it was left
  const selectFromRail = (id: string) => setActiveSectionId(rememberedView(id));
  // anything else that names a section (the global search) opens the view it names, or - for a
  // module that is not a view itself - where the module was left
  const openSection = (id: string) => setActiveSectionId(landingViews[id] ? rememberedView(id) : id);
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
        setServerInfoError(null);
        setAuth("login");
      }),
    [],
  );
  useEffect(() => {
    if (auth !== "ready") return;
    let cancelled = false;
    fetchLicenseStatus()
      .then((s) => !cancelled && setLicense(s))
      .catch(() => {}); // the rail simply carries no mark when the question cannot be answered
    return () => {
      cancelled = true;
    };
  }, [auth]);
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
          setServerInfoError(null);
          applyContainers(info.containers);
        })
        // a 401 is handled by subscribeUnauthorized; anything else is shown where the database page
        // would be, until the next resync brings an answer
        .catch((e) => !cancelled && setServerInfoError(e instanceof Error ? e.message : String(e)));
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
  // a page asking for the model editor - a form linking to a property's definition, say - or for a
  // query on a type switches the section here; the page itself opens what was asked for once its
  // model is loaded
  const navigation = useNavigationRequest();
  useEffect(() => {
    if (peekDatamodelTarget()) setActiveSectionId("datamodel");
    else if (peekQueryTarget()) setActiveSectionId("query");
    else if (peekSearchTarget()) {
      // a search carried from the global box into a module's own: the page takes the words itself
      const search = peekSearchTarget()!;
      setActiveSectionId(search.section === "settings" ? "db-settings" : search.section);
    } else {
      // a setting can be one of another database's, so the database moves with the page; the
      // settings page itself takes the target and scrolls to the group once it has loaded
      const settings = peekSettingsTarget();
      if (!settings) return;
      if (settings.scope === "database") {
        if (settings.storeId) setActiveDbId(settings.storeId);
        setActiveSectionId("db-settings");
      } else {
        setActiveSectionId("server-settings");
      }
    }
  }, [navigation]);
  async function handleLogout() {
    try {
      await logout();
    } finally {
      disconnect();
      setServerInfo(null);
      setServerInfoError(null);
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
        onSelectSection={openSection}
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
          license={license}
          activeSectionId={activeSectionId}
          onSelectSection={selectFromRail}
          onLogout={handleLogout}
        />
        {/* the page takes the tone of the entry that opened it, so a module carries a trace of its
            menu colour; see --section-tone in app.css for where it is allowed to show */}
        <main className={"content" + (section.tone ? " nav-tone-" + section.tone : "") + (inRevert ? " in-revert" : "")}>
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
          ) : activeSectionId === "server-license" ? (
            // the page hands its answer back, so the mark in the rail follows a key being fixed here
            <LicenseSection onChanged={setLicense} />
          ) : isServiceTestView(activeSectionId) ? (
            // the tests of each service, as views of one page whose switch picks the section, exactly
            // as the rail does; a test the license cannot pay for points to the account page
            <ServiceTestsSection
              view={activeSectionId}
              onSelectView={setActiveSectionId}
              onAccount={() => setActiveSectionId("server-license")}
              onChanged={setLicense}
            />
          ) : section.scope === "server" && (activeSectionId === "server-settings" || section.settingsSection) ? (
            // an entry that names part of the settings renders the settings page opened there
            <SettingsSection key={activeSectionId} focusSection={section.settingsSection} />
          ) : activeSectionId === "db-settings" && activeDb ? (
            <SettingsSection key={activeDb.id} storeId={activeDb.id} />
          ) : activeSectionId === "datamodel" && activeDb ? (
            <DatamodelSection key={activeDb.id} db={activeDb} />
          ) : activeSectionId === "logs" && activeDb ? (
            <LogsSection key={activeDb.id} db={activeDb} />
          ) : activeSectionId === "custom-logs" && activeDb ? (
            <CustomLogsSection key={activeDb.id} db={activeDb} />
          ) : activeSectionId === "query" && activeDb ? (
            <QuerySection key={activeDb.id} db={activeDb} />
          ) : isFilesStorage(activeSectionId) && activeDb ? (
            // three views of one page; the switch on it picks the section, exactly as the rail does
            <FilesStorageSection db={activeDb} view={activeSectionId} onSelectView={setActiveSectionId} />
          ) : activeSectionId === "tasks" && activeDb ? (
            <TasksSection key={activeDb.id} db={activeDb} />
          ) : activeSectionId === "api" && activeDb ? (
            <ApiSection key={activeDb.id} db={activeDb} />
          ) : section.scope === "database" && !activeDb ? (
            // a database page has no database until the server has said which there are
            serverInfo !== null ? (
              <div className="placeholder">There is no database on this server yet — create one under Databases.</div>
            ) : serverInfoError ? (
              <div className="placeholder">Could not load the databases: {serverInfoError}</div>
            ) : (
              <Loading label="Loading the databases…" />
            )
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
