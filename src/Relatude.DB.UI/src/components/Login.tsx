import { useEffect, useState, type FormEvent } from "react";
import { IconMoon, IconSun } from "@tabler/icons-react";
import { AnimatedLogo } from "./AnimatedLogo";
import { haveUsers, licenseLoginOptions, licenseLoginStartUrl, login } from "../server/auth";
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

export function Login({ onLoggedIn, theme, onToggleTheme }: LoginProps) {
  const [userName, setUserName] = useState("");
  const [password, setPassword] = useState("");
  const [remember, setRemember] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(takeLoginError);
  const [hasUsers, setHasUsers] = useState(true);
  const [licenseLogin, setLicenseLogin] = useState(false);
  useEffect(() => {
    haveUsers()
      .then(setHasUsers)
      .catch(() => {}); // server unreachable: the login attempt itself will surface the error
    licenseLoginOptions()
      .then((o) => setLicenseLogin(o.available))
      .catch(() => {}); // an older server without the endpoint: no button
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
  // No animated background here any more. Backdrop.tsx is still in the tree and still works;
  // rendering <Backdrop /> as the first child below brings it back.
  return (
    <div className="login">
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
        {licenseLogin && (
          <>
            {/* a link, not a button: the server answers with a redirect to the license server */}
            <a className="login-submit login-license" href={licenseLoginStartUrl}>
              Sign in with Relatude.License
            </a>
            <div className="login-divider">or with the master account</div>
          </>
        )}
        <label className="login-field">
          Username
          <input autoFocus={!licenseLogin} autoComplete="username" value={userName} onChange={(e) => setUserName(e.target.value)} />
        </label>
        <label className="login-field">
          Password
          <input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
        </label>
        <label className="login-remember">
          <input type="checkbox" checked={remember} onChange={(e) => setRemember(e.target.checked)} />
          Remember me
        </label>
        {!hasUsers && (
          <div className="login-error">
            {licenseLogin
              ? "No master user is configured on this server; sign in with Relatude.License above."
              : "No master user is configured on this server, so logging in is not possible."}
          </div>
        )}
        <button className="login-submit" disabled={busy || !hasUsers}>
          {busy ? "Signing in…" : "Sign in"}
        </button>
      </form>
    </div>
  );
}
