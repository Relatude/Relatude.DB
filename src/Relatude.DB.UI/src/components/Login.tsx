import { useEffect, useState, type FormEvent } from "react";
import { IconCloudLock, IconMoon, IconSun } from "@tabler/icons-react";
import { AnimatedLogo } from "./AnimatedLogo";
import { beginLicenseLogin, licenseLoginOptions, login, masterLoginOptions, type MasterLoginOptions } from "../server/auth";
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
 * This screen's colours, sent along when "Sign in with Relatude Services" starts: the server passes
 * them on to the landing page (LicenseLogin.cs), which - when it can send the user straight back -
 * shows nothing but a progress line in them, so the round trip reads as one screen. Read at the
 * moment of the click, so a theme switched a second ago is the one sent.
 */
function screenColours(): Record<string, string> {
  const style = getComputedStyle(document.documentElement);
  const colours: Record<string, string> = {};
  for (const [name, token] of [
    ["bg", "--bg"], // the page itself
    ["track", "--border"], // the progress line's track
    ["bar", "--text-muted"], // what moves along it
  ]) {
    const value = style.getPropertyValue(token).trim();
    if (/^#[0-9a-f]{3,8}$/i.test(value)) colours[name] = value;
  }
  return colours;
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
  // The screen starts fading as the server is asked, so the wait for its answer is part of the fade
  // rather than added to it; an answer that is a refusal brings the screen back with the reason.
  async function goToServices() {
    if (leaving) return;
    setError(null);
    setLeaving(true);
    const started = Date.now();
    const still = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;
    try {
      const begun = await beginLicenseLogin(screenColours());
      const loginUrl = begun.loginUrl;
      if (!loginUrl) {
        setLeaving(false);
        setError(begun.error ?? "Sign-in with Relatude Services could not start.");
        return;
      }
      const left = still ? 0 : Math.max(0, leaveMs - (Date.now() - started));
      window.setTimeout(() => window.location.assign(loginUrl), left);
    } catch (err) {
      setLeaving(false);
      setError(err instanceof Error ? err.message : String(err));
    }
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
                {/* a button, not a link: the page has to tell the server the address it is open on first */}
                <button type="button" className="login-button login-services" onClick={goToServices} disabled={leaving}>
                  <IconCloudLock size={17} stroke={1.7} />
                  Sign in with Relatude Services
                </button>
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
