import { useEffect, useMemo, useState, type FormEvent } from "react";
import { IconKey, IconMoon, IconSun } from "@tabler/icons-react";
import { DialogHost } from "../components/DialogHost";
import { GraphQLExplorer, type ExplorerSource } from "../components/GraphQLExplorer";
import { LogoMark } from "../components/Logo";
import type { ExplorerData } from "../server/graphql";
import { applyTheme, getInitialTheme, type Theme } from "../theme";
import "./explorerPage.css";

// The explorer on an endpoint's own url, for anyone with a browser: the same explorer as in the admin UI, reading
// the schema from "?explorer-data" and posting queries to the url like any client. The server writes the
// endpoint's name, url and what it asks of a client into the page (GraphQL/Endpoints/ExplorerPage.cs). An endpoint
// with an API key gets the key typed in here; it is kept for the browser tab only.

interface PageConfig {
  name: string;
  description?: string | null;
  url: string;
  apiKey: boolean;
  introspection: boolean;
  mutations: boolean;
}

function readConfig(): PageConfig {
  const element = document.getElementById("relatude-explorer-config");
  if (element?.textContent) return JSON.parse(element.textContent) as PageConfig;
  // the dev server serves the page without the server's settings: ?endpoint= names the url
  const url = new URLSearchParams(location.search).get("endpoint") ?? "/graphql";
  return { name: "GraphQL", url, apiKey: false, introspection: true, mutations: false };
}

function readKey(storageKey: string): string {
  try {
    return sessionStorage.getItem(storageKey) ?? "";
  } catch {
    return "";
  }
}

export function ExplorerPage() {
  const config = useMemo(readConfig, []);
  const keyStorage = "gqlExplorer.key." + config.url;
  const [key, setKey] = useState(() => readKey(keyStorage));
  const [keyState, setKeyState] = useState<"none" | "rejected" | "ok">(() => (config.apiKey && !readKey(keyStorage) ? "none" : "ok"));
  const [theme, setTheme] = useState<Theme>(getInitialTheme);
  useEffect(() => applyTheme(theme), [theme]);

  function acceptKey(value: string) {
    try {
      sessionStorage.setItem(keyStorage, value);
    } catch {
      // the key then lasts as long as the page
    }
    setKey(value);
    setKeyState("ok");
  }

  const source = useMemo<ExplorerSource>(() => {
    const headers: Record<string, string> = { "Content-Type": "application/json" };
    if (key) headers["X-Api-Key"] = key;
    return {
      storageKey: "page:" + config.url,
      version: key,
      async load(signal) {
        const response = await fetch(config.url + "?explorer-data", { headers, signal });
        if (response.status === 401) {
          setKeyState(key ? "rejected" : "none");
          throw new Error("The endpoint needs an API key.");
        }
        const body = (await response.json().catch(() => null)) as { errors?: { message: string }[] } | null;
        if (!response.ok) throw new Error(body?.errors?.[0]?.message ?? `${response.status} ${response.statusText}`);
        return body as unknown as ExplorerData;
      },
      async execute(request) {
        const response = await fetch(config.url, { method: "POST", headers, body: JSON.stringify(request) });
        if (response.status === 401) setKeyState("rejected");
        const text = await response.text();
        try {
          return JSON.parse(text);
        } catch {
          throw new Error(`${response.status} ${response.statusText}: ${text.slice(0, 300)}`);
        }
      },
      endpoint: location.origin + config.url,
      apiKey: config.apiKey,
      introspection: config.introspection,
      audience: "public",
    };
  }, [key, config]);

  return (
    <div className="gxp">
      <header className="gxp-head">
        <span className="gxp-mark" title="Relatude.DB">
          <LogoMark height={14} />
        </span>
        <span className="gxp-name">{config.name}</span>
        <span className="gxp-url">{config.url}</span>
        {config.description && <span className="gxp-description">{config.description}</span>}
        <span className="gxp-spacer" />
        {config.apiKey && keyState === "ok" && (
          <button className="gxp-key-set" title="Change the API key" onClick={() => setKeyState("none")}>
            <IconKey size={14} stroke={1.8} /> API key
          </button>
        )}
        {config.introspection && (
          <a className="gxp-link" href={config.url + "?sdl"} target="_blank" rel="noreferrer" title="The schema as SDL text">
            SDL
          </a>
        )}
        <button className="icon-button small" title={theme === "dark" ? "Light theme" : "Dark theme"} onClick={() => setTheme(theme === "dark" ? "light" : "dark")}>
          {theme === "dark" ? <IconSun size={16} stroke={1.8} /> : <IconMoon size={16} stroke={1.8} />}
        </button>
      </header>
      <main className="content nav-tone-pink gxp-body">
        {config.apiKey && keyState !== "ok" ? (
          <KeyPrompt rejected={keyState === "rejected"} current={key} onKey={acceptKey} cancel={key ? () => setKeyState("ok") : undefined} />
        ) : (
          <GraphQLExplorer key={key} source={source} fullWindow />
        )}
      </main>
      <DialogHost />
    </div>
  );
}

function KeyPrompt({ rejected, current, onKey, cancel }: { rejected: boolean; current: string; onKey: (key: string) => void; cancel?: () => void }) {
  const [value, setValue] = useState(rejected ? "" : current);
  function submit(e: FormEvent) {
    e.preventDefault();
    if (value.trim()) onKey(value.trim());
  }
  return (
    <form className="gxp-key" onSubmit={submit}>
      <div className="gxp-key-title">
        <IconKey size={18} stroke={1.8} /> This endpoint needs an API key
      </div>
      <p className="muted">
        {rejected ? "That key was not accepted. " : ""}The key is sent with every request in the X-Api-Key header, and kept in this browser tab until it is closed.
      </p>
      <input className="text-input" type="password" autoFocus autoComplete="off" placeholder="API key" value={value} onChange={(e) => setValue(e.target.value)} />
      <div className="gxp-key-actions">
        <button type="submit" className="action-button primary" disabled={!value.trim()}>
          Use the key
        </button>
        {cancel && (
          <button type="button" className="action-button" onClick={cancel}>
            Cancel
          </button>
        )}
      </div>
    </form>
  );
}
