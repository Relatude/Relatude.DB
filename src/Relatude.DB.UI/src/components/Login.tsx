import { useEffect, useState, type FormEvent, type MouseEvent } from "react";
import { IconCloudLock, IconMoon, IconSun } from "@tabler/icons-react";
import { AnimatedLogo } from "./AnimatedLogo";
import { licenseLoginOptions, licenseLoginStartUrl, login, masterLoginOptions, type MasterLoginOptions } from "../server/auth";
import type { Theme } from "../theme";

interface LoginProps {
  onLoggedIn: () => void;
  theme: Theme;
  onToggleTheme: () => void;
}

// The license sign-in comes back to the UI root with ?login-error=… when it fails on the way
// (the license server unreachable, access refused, the browser not the one that left). Read once
// and taken out of the url, so a reload does not show a stale error.
function takeLoginError(): string | null {
  const url = new URL(window.location.href);
  const error = url.searchParams.get("login-error");
  if (error === null) return null;
  url.searchParams.delete("login-error");
  window.history.replaceState(null, "", url.pathname + (url.search || "") + url.hash);
  return error;
}

// What the page says when there is no way in at all from here.
function noWayIn(reason: MasterLoginOptions["reason"]): string {
  return reason === "remote"
    ? "The master account can only be used on the server itself, and sign-in with Relatude Services is not set up, so logging in from here is not possible."
    : "No master user is configured on this server, so logging in is not possible.";
}

// How long the screen takes to fade out before the browser leaves for Relatude Services.
const leaveMs = 320;

/**
 * Where "Sign in with Relatude Services" goes, with this screen's colours on it: the server passes
 * them on to the landing page (LicenseLogin.cs), which - when it can send the user straight back -
 * shows nothing but a progress line in them, so the round trip reads as one screen. Read at the
 * moment of the click, so a theme switched a second ago is the one sent.
 */
function servicesStartUrl(): string {
  const style = getComputedStyle(document.documentElement);
  const params = new URLSearchParams();
  for (const [name, token] of [
    ["bg", "--bg"], // the page itself
    ["track", "--border"], // the progress line's track
    ["bar", "--text-muted"], // what moves along it
  ]) {
    const value = style.getPropertyValue(token).trim();
    if (/^#[0-9a-f]{3,8}$/i.test(value)) params.set(name, value);
  }
  const query = params.toString();
  return query ? `${licenseLoginStartUrl}?${query}` : licenseLoginStartUrl;
}

export function Login({ onLoggedIn, theme, onToggleTheme }: LoginProps) {
  const [userName, setUserName] = useState("");
  const [password, setPassword] = useState("");
  const [remember, setRemember] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(takeLoginError);
  // The two ways in, as the server answers for this browser: null until it has. Nothing but the
  // logo shows before both are known, so a form the server would refuse never flashes up first.
  const [master, setMaster] = useState<MasterLoginOptions | null>(null);
  const [licenseLogin, setLicenseLogin] = useState<boolean | null>(null);
  // on the way out to Relatude Services: the screen fades before the page goes
  const [leaving, setLeaving] = useState(false);
  useEffect(() => {
    masterLoginOptions()
      .then(setMaster)
      // server unreachable: offer the form, and the login attempt itself will surface the error
      .catch(() => setMaster({ available: true, reason: null }));
    licenseLoginOptions()
      .then((o) => setLicenseLogin(o.available))
      .catch(() => setLicenseLogin(false)); // an older server without the endpoint: no button
  }, []);
  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      if (await login(userName, password, remember)) {
        onLoggedIn();
      } else {
        setError("Wrong username or password.");
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }
  function goToServices(e: MouseEvent<HTMLAnchorElement>) {
    // a new tab or window is the browser's business: the link goes there as it is
    if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
    e.preventDefault();
    if (leaving) return;
    const url = servicesStartUrl();
    const still = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;
    setLeaving(true);
    window.setTimeout(() => window.location.assign(url), still ? 0 : leaveMs);
  }
  // No animated background here any more. Backdrop.tsx is still in the tree and still works;
  // rendering <Backdrop /> as the first child below brings it back.
  return (
    <div className={"login" + (leaving ? " leaving" : "")}>
      <button
        type="button"
        className="icon-button login-theme"
        onClick={onToggleTheme}
        title={theme === "dark" ? "Switch to light theme" : "Switch to dark theme"}
      >
        {theme === "dark" ? <IconSun size={18} stroke={1.8} /> : <IconMoon size={18} stroke={1.8} />}
      </button>
      <form className="login-card" onSubmit={submit}>
        <div className="login-logo">
          <AnimatedLogo height="72px" color="var(--text)" />
        </div>
        {error && <div className="login-error">{error}</div>}
        {master && licenseLogin !== null && (
          <>
            {licenseLogin && (
              <>
                {/* a link, not a button: the server answers with a redirect to Relatude Services */}
                <a className="login-button login-services" href={licenseLoginStartUrl} onClick={goToServices}>
                  <IconCloudLock size={17} stroke={1.7} />
                  Sign in with Relatude Services
                </a>
                {master.available && <div className="login-divider">or with the master account</div>}
              </>
            )}
            {/* the master form only where the server would take it: with no user or password set,
                or from outside the server while master login is not allowed remotely, it could
                only ever answer "wrong username or password" */}
            {master.available ? (
              <>
                <label className="login-field">
                  Username
                  <input autoFocus={!licenseLogin} autoComplete="username" value={userName} onChange={(e) => setUserName(e.target.value)} />
                </label>
                <label className="login-field">
                  Password
                  <input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
                </label>
                <div className="login-actions">
                  <label className="login-remember">
                    <input type="checkbox" checked={remember} onChange={(e) => setRemember(e.target.checked)} />
                    Remember me
                  </label>
                  <button className="login-button" disabled={busy}>
                    {busy ? "Signing in…" : "Sign in"}
                  </button>
                </div>
              </>
            ) : (
              !licenseLogin && <div className="login-error">{noWayIn(master.reason)}</div>
            )}
          </>
        )}
      </form>
    </div>
  );
}
