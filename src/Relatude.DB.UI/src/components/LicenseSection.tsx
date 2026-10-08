import { Fragment, useCallback, useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import {
  IconExternalLink,
  IconFingerprint,
  IconKey,
  IconPlugConnected,
  IconPlugConnectedX,
  IconRefresh,
} from "@tabler/icons-react";
import { showConfirm } from "../dialogs";
import { masterLoginOptions } from "../server/auth";
import { fetchWhoAmI } from "../server/serverInfo";
import {
  cancelPairing,
  fetchLicenseStatus,
  licenseCarriesSms,
  licenseMayUseAnySmsSender,
  lookUpApiKey,
  pollPairing,
  saveLicenseSettings,
  startPairing,
  type InstallationInfo,
  type LicenseAccount,
  type LicenseStatus,
  type PairingHandle,
} from "../server/license";
import { formatTime } from "../format";
import { Loading } from "./Loading";
import { CopyText } from "./CopyText";
import { DialogTools } from "./DialogTools";
import { Logo, LogoMarkIcon } from "./Logo";

/**
 * Where this installation stands with the license server, asked on mount and again by `load`. The
 * answer is handed to `onChanged` as well, so the mark in the rail follows a key being fixed. Shared
 * by the account page and Test services, which both start from it.
 */
export function useLicenseStatus(onChanged?: (status: LicenseStatus) => void) {
  const [status, setStatus] = useState<LicenseStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    setBusy(true);
    return fetchLicenseStatus()
      .then((s) => {
        setStatus(s);
        setError(null);
        onChanged?.(s);
        return s;
      })
      .catch((e) => {
        setError(e instanceof Error ? e.message : String(e));
        return null;
      })
      .finally(() => setBusy(false));
  }, [onChanged]);

  useEffect(() => {
    void load();
  }, [load]);

  return { status, error, busy, load };
}

/**
 * The account page of Relatude Services: the license this installation runs under, the API key that
 * connects it, and what the license carries. The key is set in a dialog; the license key is looked up
 * from it and never shown. A key set in configuration is locked here, as on the settings page.
 */
export function LicenseSection({ onChanged }: { onChanged?: (status: LicenseStatus) => void }) {
  const { status, error, busy, load } = useLicenseStatus(onChanged);

  if (!status) return error ? <div className="placeholder">{error}</div> : <Loading label="Checking the license…" />;

  return (
    <div className="license-page account-page">
      <AccountHero status={status} busy={busy} onReload={load} />
      {error && <div className="license-error">{error}</div>}
      {status.state === "valid" && status.license && <EntitlementsPanel status={status} />}
    </div>
  );
}

/** The state of the account in a word, for the pill in the hero. */
function stateLabel(status: LicenseStatus): string {
  switch (status.state) {
    case "valid":
      return status.license?.active ? "Licensed" : status.license?.expired ? "Expired" : "Disabled";
    case "invalid":
      return "Key refused";
    case "malformed":
      return "Malformed key";
    case "unreachable":
      return "Unreachable";
    default:
      return "No license";
  }
}

/**
 * Who signing in with Relatude Services being turned off would throw out: the person doing it, when
 * they came in that way - and whether there is a master login to come back in with.
 */
async function signInLoss(): Promise<{ signsOutThisUser: boolean; lockedOut: boolean }> {
  const who = await fetchWhoAmI().catch(() => null);
  const signsOutThisUser = who?.via === "license";
  const lockedOut = signsOutThisUser && !(await masterLoginOptions().catch(() => null))?.available;
  return { signsOutThisUser, lockedOut };
}

/**
 * The account at a glance: the Relatude wordmark, where the installation stands, the license's name,
 * and the few settings that decide it - the API key (changed in a dialog), cloud sign-in, and the
 * installation key. With no license, the way to get one, and that none is needed.
 */
function AccountHero({ status, busy, onReload }: { status: LicenseStatus; busy: boolean; onReload: () => Promise<unknown> }) {
  const { server, licensePage } = portalLinks(status);
  const tone = toneOf(status);
  const pair = usePairing(onReload, status.pairing);
  const [keyDialog, setKeyDialog] = useState(false);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const locked = (path: string) => status.locked.includes(path);
  const apiKeyLocked = locked("ApiKey");
  const signInLocked = locked("AllowLicenseeAdminLogin");
  const license = status.state === "valid" ? status.license : null;

  async function save(values: Record<string, unknown>) {
    setSaving(true);
    setSaveError(null);
    try {
      await saveLicenseSettings(values);
      await onReload();
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }

  async function toggleSignIn(on: boolean) {
    if (!on) {
      const { signsOutThisUser, lockedOut } = await signInLoss();
      if (signsOutThisUser) {
        const { ok } = await showConfirm(
          "Turn off cloud sign-in",
          lockedOut
            ? "You are signed in with Relatude Services, and there is no master login you can use from here: you will be locked out of this admin UI."
            : "You are signed in with Relatude Services: you will be signed out, and can sign in again with the master login.",
          { confirmLabel: "Turn off", danger: true, option: lockedOut ? { label: "I understand that I will be locked out", required: true } : undefined },
        );
        if (!ok) return;
      }
    }
    await save({ AllowLicenseeAdminLogin: on });
  }

  /** Takes the installation out from under the license: the keys go, and sign-in with Relatude Services is turned off. */
  async function remove() {
    const turnsOffSignIn = status.signInEnabled && !signInLocked;
    const { signsOutThisUser, lockedOut } = turnsOffSignIn ? await signInLoss() : { signsOutThisUser: false, lockedOut: false };
    const body = [
      `The API key is removed, so this installation no longer runs under ${status.license ? `"${status.license.name}"` : "the license"}.`,
      turnsOffSignIn ? "Cloud sign-in is turned off too, which ends every session opened with it." : "",
      signsOutThisUser ? (lockedOut ? "That includes yours, and there is no master login you can use from here: you will be locked out." : "That includes yours.") : "",
      "The license itself is kept - add the API key again to put it back.",
    ]
      .filter((sentence) => sentence.length > 0)
      .join(" ");
    const { ok } = await showConfirm("Remove the license", body, {
      confirmLabel: "Remove license",
      danger: true,
      option: lockedOut ? { label: "I understand that I will be locked out", required: true } : undefined,
    });
    if (!ok) return;
    const values: Record<string, unknown> = { ApiKey: "" };
    if (!locked("LicenseKey")) values.LicenseKey = "";
    if (turnsOffSignIn) values.AllowLicenseeAdminLogin = false;
    await save(values);
  }

  return (
    <section className={"panel account-hero account-tone-" + tone}>
      <LogoMarkIcon className="account-watermark" size={260} stroke={1.2} />
      <div className="account-brand">
        <Logo height={30} />
        <span className="account-brand-name">Services account</span>
        <span className="header-spacer" />
        <span className="account-pill">
          <span className="account-pill-dot" />
          {stateLabel(status)}
        </span>
        <button className="icon-button" onClick={() => void onReload()} disabled={busy} title="Ask the license server again">
          <IconRefresh size={15} stroke={1.8} />
        </button>
      </div>

      <div className="account-title">
        <h2>{license ? license.name : status.state === "missing" ? "No license" : headline(status)}</h2>
        <p>
          {license
            ? [license.disabled ? "Disabled" : license.expired ? "Expired" : "Active", license.expiresUtc ? "expires " + formatTime(license.expiresUtc) : "never expires"].join(" · ")
            : status.state === "missing"
              ? "None is needed: the database runs fully without one. A free license adds AI, imaging, file to text and SMS, and sign-in with a Relatude account."
              : status.reason}
        </p>
      </div>

      <div className="account-actions">
        {status.state === "missing" ? (
          <>
            <button className="action-button primary" onClick={pair.start} disabled={pair.starting || pair.pairing !== null}>
              <IconPlugConnected size={15} stroke={1.8} />
              {pair.starting ? "Starting…" : pair.pairing ? "Waiting…" : "Create a free license"}
            </button>
            <button className="action-button" onClick={() => setKeyDialog(true)} disabled={apiKeyLocked}>
              <IconKey size={15} stroke={1.8} />I have an API key
            </button>
            <a className="action-button" href={server} target="_blank" rel="noreferrer">
              Open portal
              <IconExternalLink size={13} stroke={1.8} />
            </a>
          </>
        ) : (
          <a className="action-button" href={licensePage} target="_blank" rel="noreferrer">
            {status.state === "valid" ? "Open in portal" : "Edit in portal"}
            <IconExternalLink size={13} stroke={1.8} />
          </a>
        )}
      </div>
      {pair.pairing ? <Waiting pairing={pair.pairing} saving={pair.saving} onStop={pair.stop} /> : pair.error && <div className="license-error">{pair.error}</div>}

      <div className="account-rows">
        <span className="account-label">API key</span>
        <span className="account-value">
          {status.hasApiKey ? (
            <>
              {status.license?.apiKeyName && <span className="license-key-name">{status.license.apiKeyName}</span>}
              {status.apiKeyStart && <span className="license-key-start">{status.apiKeyStart}…</span>}
            </>
          ) : (
            <span className="license-muted">None</span>
          )}
          {apiKeyLocked && <span className="license-lock">from appsettings</span>}
        </span>
        <span className="account-row-actions">
          <button className="action-button" onClick={() => setKeyDialog(true)} disabled={apiKeyLocked || saving}>
            <IconKey size={14} stroke={1.8} />
            {status.hasApiKey ? "Change" : "Add"}
          </button>
          {status.hasApiKey && (
            <button className="action-button" onClick={remove} disabled={apiKeyLocked || saving} title="Take this installation out from under the license">
              <IconPlugConnectedX size={14} stroke={1.8} className="tone-danger" />
              Remove
            </button>
          )}
        </span>

        <span className="account-label">Cloud sign-in</span>
        <span className="account-value">
          <label className="license-toggle">
            <input type="checkbox" checked={status.signInEnabled} disabled={signInLocked || saving} onChange={(e) => void toggleSignIn(e.target.checked)} />
            <span>{status.signInEnabled ? "On" : "Off"}</span>
          </label>
          <span className="license-muted">Sign in to this admin UI with a Relatude account</span>
          {signInLocked && <span className="license-lock">from appsettings</span>}
        </span>
        <span />

        {status.installation && (
          <>
            <span className="account-label">Installation</span>
            <span className="account-value">
              <InstallationKey installation={status.installation} />
            </span>
            <span />
          </>
        )}

        {status.showLicenseServer && (
          <>
            <span className="account-label">License server</span>
            <span className="account-value license-muted">{status.servicesServerUrl}</span>
            <span />
          </>
        )}
      </div>
      {saveError && <div className="license-error">{saveError}</div>}
      {keyDialog && <ApiKeyDialog status={status} onClose={() => setKeyDialog(false)} onSaved={onReload} />}
    </section>
  );
}

/**
 * Adds or replaces the API key. A pasted key is looked up before anything is saved, so one the
 * license server does not take is refused with its reason. Debug builds may point at another license
 * server here as well; the key is then checked with that one.
 */
function ApiKeyDialog({ status, onClose, onSaved }: { status: LicenseStatus; onClose: () => void; onSaved: () => Promise<unknown> }) {
  const { licensePage } = portalLinks(status);
  const [apiKey, setApiKey] = useState("");
  const [serverUrl, setServerUrl] = useState(status.servicesServerUrl);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const pasted = apiKey.trim();
  const serverChanged = status.showLicenseServer && !status.locked.includes("ServicesServerUrl") && serverUrl.trim() !== status.servicesServerUrl;

  async function save() {
    if (saving || (!pasted && !serverChanged)) return;
    setSaving(true);
    setError(null);
    try {
      if (serverChanged) await saveLicenseSettings({ ServicesServerUrl: serverUrl.trim() });
      if (pasted) {
        const found = await lookUpApiKey(pasted);
        const values: Record<string, unknown> = { ApiKey: found.apiKey };
        if (!status.locked.includes("LicenseKey")) values.LicenseKey = found.licenseKey;
        await saveLicenseSettings(values);
      }
      await onSaved();
      onClose();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }

  return createPortal(
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && !saving && onClose()}>
      <div
        className="dialog account-key-dialog"
        role="dialog"
        aria-modal="true"
        aria-label="API key"
        onKeyDown={(e) => {
          if (e.key === "Escape" && !saving) {
            e.preventDefault();
            onClose();
          }
        }}
      >
        <h3>
          <IconKey size={16} stroke={1.8} /> {status.hasApiKey ? "Change API key" : "Add API key"}
          <DialogTools onClose={onClose} />
        </h3>
        <div className="dialog-body">
          <p className="account-key-lead">
            Copy an API key from{" "}
            <a href={licensePage} target="_blank" rel="noreferrer">
              the license's page in Relatude Services
              <IconExternalLink size={12} stroke={1.8} />
            </a>{" "}
            and paste it here.
          </p>
          <input
            className="text-input secret-input"
            autoFocus
            autoComplete="off"
            spellCheck={false}
            data-1p-ignore
            data-lpignore="true"
            data-bwignore
            value={apiKey}
            placeholder={status.hasApiKey ? "Paste the new API key" : "Paste the API key"}
            disabled={saving}
            onChange={(e) => {
              setApiKey(e.target.value);
              setError(null);
            }}
            onKeyDown={(e) => {
              if (e.key === "Enter") void save();
            }}
          />
          <span className="license-muted account-key-hint">Only its first five characters are shown once saved. On a production server, keep it in configuration instead.</span>
          {status.showLicenseServer && (
            <Field label="License server" hint="Debug builds only." locked={status.locked.includes("ServicesServerUrl")}>
              <input className="text-input" value={serverUrl} spellCheck={false} disabled={saving || status.locked.includes("ServicesServerUrl")} onChange={(e) => setServerUrl(e.target.value)} />
            </Field>
          )}
          {error && <div className="license-error">{error}</div>}
        </div>
        <div className="dialog-row">
          <div className="header-spacer" />
          <button className="action-button primary" onClick={save} disabled={saving || (!pasted && !serverChanged)}>
            {saving ? (pasted ? "Checking the key…" : "Saving…") : "Save"}
          </button>
          <button className="action-button" onClick={onClose} disabled={saving}>
            Cancel
          </button>
        </div>
      </div>
    </div>,
    document.body,
  );
}

/**
 * What the license carries: its monthly credits first, a card each, then its features, limits and -
 * when it can send text messages and may not name any sender - the senders Relatude has approved.
 */
function EntitlementsPanel({ status }: { status: LicenseStatus }) {
  const license = status.license!;
  const { smsSendersPage } = portalLinks(status);
  const senders = license.smsSenders ?? [];
  const showSenders = !licenseMayUseAnySmsSender(status) && (senders.length > 0 || licenseCarriesSms(status));
  return (
    <section className="panel account-carries">
      <h3>Monthly credits</h3>
      {license.accounts.length === 0 ? (
        <p className="license-muted">None, so the Relatude services refuse calls.</p>
      ) : (
        <div className="account-credits">
          {license.accounts.map((a) => (
            <CreditCard key={a.key} account={a} />
          ))}
        </div>
      )}
      <div className="account-extras">
        <div>
          <h4 className="license-sub">Features</h4>
          {license.features.length === 0 ? (
            <p className="license-muted">None</p>
          ) : (
            <div className="license-chips">
              {license.features.map((f) => (
                <span key={f.key} className="license-chip">
                  {f.name}
                </span>
              ))}
            </div>
          )}
        </div>
        <div>
          <h4 className="license-sub">Limits</h4>
          {license.limits.length === 0 ? (
            <p className="license-muted">None</p>
          ) : (
            <dl className="account-limits">
              {license.limits.map((l) => (
                <Fragment key={l.key}>
                  <dt>{l.name}</dt>
                  <dd>{l.unlimited ? "Unlimited" : l.maxValue.toLocaleString()}</dd>
                </Fragment>
              ))}
            </dl>
          )}
        </div>
        {showSenders && (
          <div>
            <h4 className="license-sub">SMS senders</h4>
            {senders.length === 0 ? (
              <p className="license-muted">None: messages go as the service's own sender.</p>
            ) : (
              <div className="license-chips">
                {senders.map((s) => (
                  <span key={s} className="license-chip">
                    {s}
                  </span>
                ))}
              </div>
            )}
            <a className="license-request" href={smsSendersPage} target="_blank" rel="noreferrer">
              Request a sender
              <IconExternalLink size={12} stroke={1.8} />
            </a>
          </div>
        )}
      </div>
    </section>
  );
}

/** One credit account as a card: what is left of the month, how much has gone, and the rate limits. */
function CreditCard({ account }: { account: LicenseAccount }) {
  const used = Math.min(account.usedThisMonth, account.monthlyLimit);
  const share = account.monthlyLimit > 0 ? Math.round((used / account.monthlyLimit) * 100) : 0;
  const rates = [
    { label: "/min", window: account.minute },
    { label: "/hour", window: account.hour },
    { label: "/day", window: account.day },
  ].filter((r) => r.window.limit > 0);
  return (
    <div className={"account-credit" + (share >= 90 ? " low" : "")}>
      <span className="account-credit-name">{account.name}</span>
      <span className="account-credit-left">
        <strong>{account.balanceLeft.toLocaleString()}</strong>
        <span className="license-muted"> of {account.monthlyLimit.toLocaleString()} left</span>
      </span>
      <div className="license-bar" title={`${used.toLocaleString()} used this month`}>
        <span className={"license-bar-fill" + (share >= 90 ? " full" : "")} style={{ width: share + "%" }} />
      </div>
      <span className="license-muted account-credit-rates">{rates.length > 0 ? rates.map((r) => r.window.limit.toLocaleString() + r.label).join(" · ") : "no rate limit"}</span>
    </div>
  );
}

/**
 * Getting a license without anybody copying a key.
 *
 * The button opens the license server in a tab and then waits: the server here holds a secret that
 * collects the answer, and this polls until somebody has picked a license over there. The keys come
 * back and are saved exactly as the Save button saves them, so nothing about a configuration
 * override or the settings file behaves differently because they arrived this way.
 *
 * The tab is opened from the click itself rather than after the pairing is made, because a browser
 * that did not see a click blocks the window - so the tab is opened first and pointed at the claim
 * url once there is one.
 */
function usePairing(onPaired: () => Promise<unknown>, pending: PairingHandle | null) {
  const [pairing, setPairing] = useState<PairingHandle | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [starting, setStarting] = useState(false);
  const [saving, setSaving] = useState(false);

  // One the server is still waiting on, from before this page was loaded: someone went to the
  // portal in another tab, or reloaded while waiting. Taking it up is the whole reason the server
  // holds it rather than this page.
  useEffect(() => {
    if (pending) setPairing((current) => current ?? pending);
  }, [pending]);

  const stop = useCallback(() => {
    setPairing((current) => {
      if (current) void cancelPairing(current.pairingId).catch(() => {});
      return null;
    });
  }, []);

  async function start() {
    setStarting(true);
    setError(null);
    // opened on the click, so the browser does not treat it as a pop-up
    const tab = window.open("", "_blank");
    try {
      const handle = await startPairing();
      setPairing(handle);
      if (tab) tab.location.href = handle.claimUrl;
      else window.open(handle.claimUrl, "_blank", "noreferrer");
    } catch (e) {
      tab?.close();
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setStarting(false);
    }
  }

  useEffect(() => {
    if (!pairing) return;
    let stopped = false;
    let timer = 0;
    const tick = async () => {
      if (stopped) return;
      try {
        const answer = await pollPairing(pairing.pairingId);
        if (stopped) return;
        if (answer.status === "ready" && answer.licenseKey && answer.apiKey) {
          // the pairing is spent at both ends once it has answered, so whatever the save does, the
          // waiting is over - a save that fails says why rather than polling a pairing that is gone
          setSaving(true);
          try {
            await saveLicenseSettings({ LicenseKey: answer.licenseKey, ApiKey: answer.apiKey });
            await onPaired();
          } catch (e) {
            setError(e instanceof Error ? e.message : String(e));
          } finally {
            setSaving(false);
            setPairing(null);
          }
          return;
        }
        if (answer.status === "expired") {
          setError(answer.reason ?? "The pairing expired before it was answered. Try again.");
          setPairing(null);
          return;
        }
        // "unreachable" is not the end of it: the license server may simply be busy, and the pairing
        // is still waiting over there
      } catch (e) {
        if (stopped) return;
        setError(e instanceof Error ? e.message : String(e));
      }
      if (!stopped) timer = window.setTimeout(tick, Math.max(1, pairing.pollSeconds) * 1000);
    };
    timer = window.setTimeout(tick, Math.max(1, pairing.pollSeconds) * 1000);
    return () => {
      stopped = true;
      window.clearTimeout(timer);
    };
  }, [pairing, onPaired]);

  return { pairing, error, starting, saving, start, stop };
}

/** One credit account: what is left of the month, and the rate limits on top of it. */
export function Account({ account }: { account: LicenseAccount }) {
  const used = Math.min(account.usedThisMonth, account.monthlyLimit);
  const share = account.monthlyLimit > 0 ? Math.round((used / account.monthlyLimit) * 100) : 0;
  const rates = [
    { label: "/min", window: account.minute },
    { label: "/hour", window: account.hour },
    { label: "/day", window: account.day },
  ].filter((r) => r.window.limit > 0);
  return (
    <div className="license-account">
      <div className="license-account-head">
        <strong>{account.name}</strong>
        <span className="license-muted">
          {account.balanceLeft.toLocaleString()} / {account.monthlyLimit.toLocaleString()} left
        </span>
      </div>
      <div className="license-bar" title={`${used.toLocaleString()} used of ${account.monthlyLimit.toLocaleString()}`}>
        <span className={"license-bar-fill" + (share >= 90 ? " full" : "")} style={{ width: share + "%" }} />
      </div>
      {rates.length > 0 && (
        <div className="license-muted license-rates">
          {rates.map((r) => (
            <span key={r.label}>
              {r.window.limit.toLocaleString()}
              {r.label}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

/** While the portal is open in the other tab: what is being waited for, and how to stop waiting. */
function Waiting({ pairing, saving, onStop }: { pairing: PairingHandle; saving: boolean; onStop: () => void }) {
  return (
    <div className="license-waiting">
      <span className="license-spinner" aria-hidden="true" />
      <div className="license-status-text">
        <strong>{saving ? "Saving the keys…" : "Pick a license in the portal tab"}</strong>
        <span className="license-muted">
          The keys arrive here by themselves. Expires {formatTime(pairing.expiresUtc)} ·{" "}
          <a href={pairing.claimUrl} target="_blank" rel="noreferrer">
            reopen the tab
          </a>
        </span>
      </div>
      <button className="action-button" onClick={onStop} disabled={saving}>
        Stop waiting
      </button>
    </div>
  );
}

/**
 * The key Relatude Services knows this installation by, to look it up there. It stays off the page - a
 * screen shared or photographed does not give it away - and so does everything said about it: the page
 * has only a quiet text link, and the dialog it opens has the key and what it is made of.
 */
function InstallationKey({ installation }: { installation: InstallationInfo }) {
  const [shown, setShown] = useState(false);
  return (
    <>
      <button className="license-reveal-link" onClick={() => setShown(true)} title="Show the key Relatude Services knows this installation by">
        Installation key
      </button>
      {shown && <InstallationKeyDialog installation={installation} onClose={() => setShown(false)} />}
    </>
  );
}

/**
 * The installation key itself: read only, with a copy button. The parts break at their colons, and the
 * line under it says what each one is, so two installations that look alike in the portal can be told
 * apart by the part they share.
 */
function InstallationKeyDialog({ installation, onClose }: { installation: InstallationInfo; onClose: () => void }) {
  const parts = installation.key.split(":");
  const closeButton = useRef<HTMLButtonElement>(null);
  useEffect(() => closeButton.current?.focus(), []);
  return createPortal(
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div
        className="dialog installation-key-dialog"
        role="dialog"
        aria-modal="true"
        aria-label="Installation key"
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            e.preventDefault();
            onClose();
          }
        }}
      >
        <h3>
          <IconFingerprint size={16} stroke={1.8} /> Installation key
          <DialogTools onClose={onClose} />
        </h3>
        <div className="dialog-body license-installation">
          <code className="license-installation-key">
            {parts.map((part, i) => (
              <Fragment key={i}>
                {i > 0 && (
                  <>
                    :<wbr />
                  </>
                )}
                {part}
              </Fragment>
            ))}
          </code>
          <CopyText text={installation.key} title="Copy the installation key" small />
        </div>
        <div className="dialog-body license-muted">
          What Relatude Services knows this installation by: the server id from relatude.db.json, the host ({installation.host})
          {installation.dataId ? ` and an id kept in ${installation.dataIdPlace}.` : "."}
        </div>
        {installation.dataIdProblem && (
          <div className="dialog-body license-installation-problem">
            No id could be kept in {installation.dataIdPlace}, so the key goes without one: {installation.dataIdProblem}
          </div>
        )}
        <div className="dialog-row">
          <div className="header-spacer" />
          <button ref={closeButton} className="action-button" onClick={onClose}>
            Close
          </button>
        </div>
      </div>
    </div>,
    document.body,
  );
}

/** A labelled field; `extra` goes on the label's line, after the name. */
export function Field({
  label,
  hint,
  locked,
  extra,
  children,
}: {
  label: string;
  hint?: string;
  locked: boolean;
  extra?: React.ReactNode;
  children: React.ReactNode;
}) {
  return (
    <label className="license-field">
      <span className="license-field-label">
        {label}
        {extra}
        {locked && <span className="license-lock">from appsettings</span>}
      </span>
      {children}
      {hint && <span className="license-muted license-field-hint">{hint}</span>}
    </label>
  );
}

/**
 * The portal, the license's own page in it, and the page of the license where its SMS senders are
 * asked for. A key that is not a guid names no license, so the portal's front page is the best there
 * is then.
 */
export function portalLinks(status: LicenseStatus): { server: string; licensePage: string; smsSendersPage: string } {
  const server = status.servicesServerUrl.replace(/\/$/, "");
  const key = status.licenseKey?.trim() ?? "";
  const licensePage = /^[0-9a-f]{8}-?([0-9a-f]{4}-?){3}[0-9a-f]{12}$/i.test(key) ? `${server}/licenses/${encodeURIComponent(key)}` : server;
  return { server, licensePage, smsSendersPage: licensePage === server ? server : licensePage + "/sms-senders" };
}

function toneOf(status: LicenseStatus): "ok" | "bad" | "info" {
  if (status.state === "valid") return status.license?.active ? "ok" : "bad";
  if (status.state === "invalid" || status.state === "malformed") return "bad";
  return "info"; // missing and unreachable are both "nothing is wrong here yet"
}

export function headline(status: LicenseStatus): string {
  switch (status.state) {
    case "valid":
      return status.license?.active
        ? `Licensed — ${status.license.name}`
        : `"${status.license?.name}" is ${status.license?.expired ? "expired" : "disabled"}`;
    case "invalid":
      return "The license server rejects these keys";
    case "malformed":
      return "The keys are malformed";
    case "unreachable":
      return "License server unreachable";
    default:
      return "No license (none needed)";
  }
}
