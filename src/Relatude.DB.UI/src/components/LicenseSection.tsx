import { useCallback, useEffect, useState, type ReactNode } from "react";
import {
  IconAlertTriangle,
  IconCircleCheck,
  IconCloud,
  IconExternalLink,
  IconInfoCircle,
  IconPlugConnected,
  IconPlugConnectedX,
  IconMessage,
  IconRefresh,
  IconSend,
  IconSparkles,
} from "@tabler/icons-react";
import { showConfirm } from "../dialogs";
import { masterLoginOptions } from "../server/auth";
import { fetchAiModels } from "../server/settings";
import { Combo, type PickerLoader } from "./Combo";
import { fetchWhoAmI } from "../server/serverInfo";
import {
  cancelPairing,
  fetchLicenseStatus,
  licenseCarriesAi,
  licenseCarriesSms,
  licenseMayUseAnySmsSender,
  licenseSmsSenders,
  lookUpApiKey,
  pollPairing,
  saveLicenseSettings,
  sendTestSms,
  startPairing,
  testAiCompletion,
  testAiEmbedding,
  type AiCompletionResult,
  type AiEmbeddingResult,
  type SmsReceipt,
  type LicenseAccount,
  type LicenseStatus,
  type PairingHandle,
} from "../server/license";
import { formatTime } from "../format";
import { Loading } from "./Loading";
import { FoldHead } from "./LogsSection";

/**
 * The Services module: the Relatude Services account this installation runs under - whether it has
 * one, what it carries, and the key that decides both.
 *
 * One column of full-width panels under one heading, written the way Storage writes its group
 * headings: where the installation stands and the key that puts it there share the first panel,
 * which has no heading of its own - the group's name is its name. What the license carries follows,
 * and "do I need one?" - the answer is no - closes the page.
 *
 * The keys are ordinary server settings and are saved through the settings command, so a key that
 * configuration decides is locked here exactly as it is on the settings page. Only the API key is
 * entered: the license key is looked up from it and saved beside it, and is not shown here.
 */
export function LicenseSection({ onChanged }: { onChanged?: (status: LicenseStatus) => void }) {
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

  if (error) return <div className="placeholder">{error}</div>;
  if (!status) return <Loading label="Checking the license…" />;

  return (
    <div className="license-page">
      {/* Storage's group heading, so the two modules read alike: an icon in this module's colour, the
          name beside it, what it is about, and the refresh at the end of the line */}
      <div className="storage-group-head">
        <IconCloud size={18} stroke={1.7} />
        <h2>Relatude Services Account</h2>
        <span className="muted">the license this installation runs under, and the API key that connects it</span>
        <button className="icon-button storage-refresh" onClick={() => void load()} disabled={busy} title="Ask the license server again">
          <IconRefresh size={14} stroke={1.8} />
        </button>
      </div>
      <LicensePanel status={status} onReload={load} />
      {status.state === "valid" && status.license && <EntitlementsPanel status={status} />}
      {licenseCarriesSms(status) && <SmsTestPanel senders={licenseSmsSenders(status)} anySender={licenseMayUseAnySmsSender(status)} />}
      {(licenseCarriesAi(status, "embeddings") || licenseCarriesAi(status, "completions")) && (
        <AiTestPanel
          configuredUrl={status.aiServiceUrl ?? ""}
          embeddings={licenseCarriesAi(status, "embeddings")}
          completions={licenseCarriesAi(status, "completions")}
        />
      )}
      <WhatALicenseIs />
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

/**
 * The same whatever the state. Someone who has just found a section called "Relatude Services" in a database
 * they are running wants to know whether something is wrong, and the answer is no.
 */
function WhatALicenseIs() {
  return (
    <section className="panel license-intro">
      <h3>
        <IconInfoCircle size={15} stroke={1.8} /> Do I need one?
      </h3>
      <ul>
        <li>
          <strong>No.</strong> The database runs fully without a license; nothing expires or is held back.
        </li>
        <li>
          <strong>It is free</strong>, and lets this installation use <strong>Relatude Services</strong> for AI and text messages instead of your own vendor keys.
        </li>
        <li>It also lets people you choose sign in with a Relatude Cloud account instead of the master password.</li>
      </ul>
    </section>
  );
}

/**
 * Where the installation stands and the key that puts it there, in one panel: the answer in one
 * line with the actions that state calls for, then the API key - the only key anybody enters - and
 * the switch that decides whether it signs people in, under one Save.
 *
 * A pasted key is looked up before anything is saved, so a key the server does not take is refused
 * with its reason rather than saved. The license key comes back with the answer and is saved beside
 * it, but is not shown: the settings page has it, and nobody needs it to set anything up. The saved
 * API key is told apart by its first five characters, which is all of it the server ever sends back.
 */
function LicensePanel({ status, onReload }: { status: LicenseStatus; onReload: () => Promise<unknown> }) {
  const { server, licensePage } = portalLinks(status);
  const tone = toneOf(status);
  const pair = usePairing(onReload, status.pairing);
  const [apiKey, setApiKey] = useState("");
  const [serverUrl, setServerUrl] = useState(status.servicesServerUrl);
  const [signIn, setSignIn] = useState(status.signInEnabled);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  // the page is reloaded after every save, so the fields follow what the server now holds - and a
  // refusal from before is no longer about what is on screen
  useEffect(() => {
    setApiKey("");
    setServerUrl(status.servicesServerUrl);
    setSignIn(status.signInEnabled);
    setSaveError(null);
  }, [status]);

  const locked = (path: string) => status.locked.includes(path);
  const apiKeyLocked = locked("ApiKey");
  const pasted = apiKey.trim();
  // only a debug build of the server offers the address, and only then will it accept it
  const serverChanged = status.showLicenseServer && !locked("ServicesServerUrl") && serverUrl.trim() !== status.servicesServerUrl;
  const signInChanged = !locked("AllowLicenseeAdminLogin") && signIn !== status.signInEnabled;
  const changed = (pasted.length > 0 && !apiKeyLocked) || serverChanged || signInChanged;

  async function save() {
    if (saving || !changed) return;
    setSaving(true);
    setSaveError(null);
    try {
      // the address first: a pasted key is checked with the license server it is going to be used with
      if (serverChanged) await saveLicenseSettings({ ServicesServerUrl: serverUrl.trim() });
      const values: Record<string, unknown> = {};
      if (pasted.length > 0 && !apiKeyLocked) {
        const found = await lookUpApiKey(pasted);
        values.ApiKey = found.apiKey;
        // saved beside it, so the settings file says which license this is. When configuration
        // decides the license key instead, the API key's license is the one used all the same.
        if (!locked("LicenseKey")) values.LicenseKey = found.licenseKey;
      }
      if (signInChanged) values.AllowLicenseeAdminLogin = signIn;
      if (Object.keys(values).length > 0) await saveLicenseSettings(values);
      await onReload();
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }

  /**
   * Takes the installation out from under the license: the API key goes, the license key saved
   * beside it too, and sign-in with Relatude Services is turned off - which ends every session opened
   * that way, the one doing this included. The dialog says so, and when there is no master login to
   * come back in with it will not go ahead until that has been read and ticked.
   */
  async function remove() {
    const turnsOffSignIn = status.signInEnabled && !locked("AllowLicenseeAdminLogin");
    const who = turnsOffSignIn ? await fetchWhoAmI().catch(() => null) : null;
    const signsOutThisUser = who?.via === "license";
    const lockedOut = signsOutThisUser && !(await masterLoginOptions().catch(() => null))?.available;
    const body = [
      `The API key is removed from this installation's settings, so it no longer runs under ${status.license ? `"${status.license.name}"` : "the license"}.`,
      turnsOffSignIn ? "Sign-in with Relatude Services is turned off too, which ends every session opened with it." : "",
      signsOutThisUser
        ? lockedOut
          ? "That includes yours, and there is no master login you can use from here: you will be locked out of this admin UI."
          : "That includes yours: you will be signed out, and can sign in again with the master login."
        : "",
      "The license itself is not deleted - paste an API key again to put it back.",
    ]
      .filter((sentence) => sentence.length > 0)
      .join(" ");
    const { ok } = await showConfirm("Remove the license", body, {
      confirmLabel: "Remove license",
      danger: true,
      option: lockedOut ? { label: "I understand that I will be locked out", required: true } : undefined,
    });
    if (!ok) return;
    setSaving(true);
    setSaveError(null);
    try {
      const values: Record<string, unknown> = { ApiKey: "" };
      if (!locked("LicenseKey")) values.LicenseKey = "";
      if (turnsOffSignIn) values.AllowLicenseeAdminLogin = false;
      await saveLicenseSettings(values);
      await onReload();
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className="panel license-main">
      <div className={"license-status license-status-" + tone}>
        <span className="license-status-icon">
          {tone === "ok" ? <IconCircleCheck size={20} stroke={1.8} /> : tone === "bad" ? <IconAlertTriangle size={20} stroke={1.8} /> : <IconInfoCircle size={20} stroke={1.8} />}
        </span>
        <div className="license-status-text">
          <strong>{headline(status)}</strong>
          {status.reason && <span className="license-muted">{status.reason}</span>}
        </div>
        <div className="license-actions">
          {status.state === "missing" ? (
            // No copying: the portal is opened on this pairing, and whatever license is picked there
            // comes back here by itself. The plain link is kept beside it for anyone who would rather
            // do it by hand, or whose browser would not open the tab.
            <>
              <button className="action-button primary" onClick={pair.start} disabled={pair.starting || pair.pairing !== null}>
                <IconPlugConnected size={15} stroke={1.8} />
                {pair.starting ? "Starting…" : pair.pairing ? "Waiting…" : "Create a license"}
              </button>
              <a className="action-button" href={server} target="_blank" rel="noreferrer">
                Open portal
                <IconExternalLink size={13} stroke={1.8} />
              </a>
            </>
          ) : (
            // invalid, malformed or unreachable is fixed at the other end too - on the license's own
            // page when the key is good enough to name one
            <a className="action-button" href={licensePage} target="_blank" rel="noreferrer">
              {status.state === "valid" ? "Open in portal" : "Edit in portal"}
              <IconExternalLink size={13} stroke={1.8} />
            </a>
          )}
          {status.hasApiKey && (
            // Storage's toolbar rule: the label in the text colour, red on the icon only - it destroys
            // something - and the dialog behind it says how much
            <button
              className="action-button"
              onClick={remove}
              disabled={saving || apiKeyLocked}
              title={apiKeyLocked ? "The API key is set by configuration, so it is removed there." : "Take this installation out from under the license"}
            >
              <IconPlugConnectedX size={15} stroke={1.8} className="tone-danger" />
              Remove license
            </button>
          )}
        </div>
      </div>

      {pair.pairing ? (
        <Waiting pairing={pair.pairing} saving={pair.saving} onStop={pair.stop} />
      ) : (
        pair.error && <div className="license-error">{pair.error}</div>
      )}

      <p className="license-keys-lead">
        Copy an <strong>API key</strong> from{" "}
        <a href={licensePage} target="_blank" rel="noreferrer">
          the license's page in Relatude Services
          <IconExternalLink size={12} stroke={1.8} />
        </a>{" "}
        and paste it here. It is the only key this installation needs.
      </p>
      <div className="license-keys-body">
        <div className="license-keys-column">
          <Field
            label="API key"
            locked={apiKeyLocked}
            extra={
              status.apiKeyStart && (
                <span className="license-key-start" title={`The saved API key starts with ${status.apiKeyStart}`}>
                  {status.apiKeyStart}…
                </span>
              )
            }
            hint={apiKeyLocked ? undefined : "A secret: once saved, only its first five characters are shown. On a production server, keep it in configuration or user secrets instead."}
          >
            <input
              className="text-input"
              type="password"
              autoComplete="new-password"
              spellCheck={false}
              value={apiKey}
              placeholder={apiKeyLocked ? "Set by configuration" : status.hasApiKey ? "Paste another one to replace it" : "Paste the API key"}
              disabled={apiKeyLocked || saving}
              onChange={(e) => {
                setApiKey(e.target.value);
                setSaveError(null);
              }}
              onKeyDown={(e) => {
                if (e.key === "Enter") void save();
              }}
            />
          </Field>
        </div>
        <div className="license-keys-column">
          <Field
            label="Cloud sign-in"
            hint={'Adds "Sign in with Relatude Services" to the login page; the portal decides who gets in.'}
            locked={locked("AllowLicenseeAdminLogin")}
          >
            <span className="license-toggle">
              <input type="checkbox" checked={signIn} disabled={locked("AllowLicenseeAdminLogin") || saving} onChange={(e) => setSignIn(e.target.checked)} />
              <span>{signIn ? "On" : "Off"}</span>
            </span>
          </Field>
          {status.showLicenseServer && (
            <Field label="License server" hint="Only for self-hosted or test servers. Offered by debug builds only." locked={locked("ServicesServerUrl")}>
              <input
                className="text-input"
                value={serverUrl}
                spellCheck={false}
                disabled={locked("ServicesServerUrl") || saving}
                onChange={(e) => setServerUrl(e.target.value)}
              />
            </Field>
          )}
        </div>
      </div>
      <div className="license-keys-footer">
        <button className="action-button primary" onClick={save} disabled={saving || !changed}>
          {saving ? (pasted.length > 0 ? "Checking the key…" : "Saving…") : "Save"}
        </button>
        {status.locked.length > 0 && (
          <span className="license-muted">
            {status.locked.length === 1 ? "1 field is" : `${status.locked.length} fields are`} set by configuration.
          </span>
        )}
      </div>
      {saveError && <div className="license-error">{saveError}</div>}
    </section>
  );
}

/**
 * What the license turns out to carry. Only shown when the license server answered for it. The SMS
 * senders are shown when the license can send at all - it has the "sms" account and is active - or
 * has some: they are asked for on the license's page in the portal and approved by Relatude, so that
 * is where the link goes. Not for a license with the "smsanysender" feature, which may name any sender.
 */
function EntitlementsPanel({ status }: { status: LicenseStatus }) {
  const license = status.license!;
  const { smsSendersPage } = portalLinks(status);
  const senders = license.smsSenders ?? [];
  const showSenders = !licenseMayUseAnySmsSender(status) && (senders.length > 0 || licenseCarriesSms(status));
  return (
    <section className="panel license-entitlements">
      <h3>
        {license.name}
        <span className="panel-sub">
          {" · "}
          {license.disabled ? "Disabled" : license.expired ? "Expired" : "Active"}
          {" · "}
          {license.expiresUtc ? "expires " + formatTime(license.expiresUtc) : "never expires"}
        </span>
      </h3>
      <div className="license-columns">
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
            <table className="license-table">
              <tbody>
                {license.limits.map((l) => (
                  <tr key={l.key}>
                    <td>{l.name}</td>
                    <td className="num">{l.unlimited ? "Unlimited" : l.maxValue.toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>

        <div>
          <h4 className="license-sub">Monthly credits</h4>
          {license.accounts.length === 0 ? (
            <p className="license-muted">None, so the Relatude services will refuse calls.</p>
          ) : (
            <div className="license-accounts">
              {license.accounts.map((a) => (
                <Account key={a.key} account={a} />
              ))}
            </div>
          )}
        </div>

        {showSenders && (
          <div>
            <h4 className="license-sub">SMS senders</h4>
            {senders.length === 0 ? (
              <p className="license-muted">None — messages go as the service's own sender.</p>
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

/** the fold's own duration in app.css (.fold), after which an open panel stops clipping its content */
const foldMs = 180;

/**
 * A test panel, folded to its heading until somebody wants it: the tests send real messages and
 * make real calls that are charged to the license, so they start out of the way, and the heading
 * still says what is in there.
 *
 * The content stays mounted while folded - what was typed and the last receipt are still there when
 * the panel is opened again - and `inert` keeps the folded fields out of the tab order. It is
 * clipped only while it moves: once open and still, a model list may reach past the panel's edge.
 */
function TestPanel({ className, icon, title, sub, label, children }: { className: string; icon: ReactNode; title: string; sub: string; label: string; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  const [settled, setSettled] = useState(false);
  useEffect(() => {
    setSettled(false);
    if (!open) return;
    const id = window.setTimeout(() => setSettled(true), foldMs);
    return () => window.clearTimeout(id);
  }, [open]);
  return (
    <section className={"panel " + className + (open ? "" : " folded")}>
      <h3 className="with-fold">
        <FoldHead open={open} label={label} onToggle={() => setOpen(!open)}>
          {icon} {title}
          <span className="panel-sub"> · {sub}</span>
        </FoldHead>
      </h3>
      <div className={"fold" + (open ? " open" : "") + (open && settled ? " settled" : "")} inert={!open}>
        <div className="fold-inner">{children}</div>
      </div>
    </section>
  );
}

/**
 * A real message through the Relatude SMS service, so whoever set up the license can see it arrive
 * before any code depends on it. Only offered when the license has the "sms" credit account; it is
 * charged like any other message, which is why the receipt says what it cost and what is left. The
 * sender is the service's own or one Relatude has approved for the license, and the server checks it
 * with the license server again before anything is sent.
 */
function SmsTestPanel({ senders, anySender }: { senders: string[]; anySender: boolean }) {
  const [from, setFrom] = useState("");
  // a sender that is no longer approved after a reload falls back to the service's own; with the
  // any-sender feature whatever is typed goes, and the license server judges it
  const sender = anySender ? from.trim() : senders.includes(from) ? from : "";
  const [to, setTo] = useState("");
  const [message, setMessage] = useState("Test message from Relatude.DB");
  const [sending, setSending] = useState(false);
  const [receipt, setReceipt] = useState<SmsReceipt | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function sendIt() {
    setSending(true);
    setError(null);
    setReceipt(null);
    try {
      setReceipt(await sendTestSms({ from: sender, to: to.trim(), message }));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSending(false);
    }
  }

  return (
    <TestPanel
      className="license-sms"
      icon={<IconMessage size={15} stroke={1.8} />}
      title="Test SMS"
      sub="sends a real message, charged to the license"
      label="the SMS test"
    >
      <div className="license-sms-fields">
        {anySender ? (
          <Field label="From" hint="Any name of at most 11 letters and digits, or a number with its country code. Empty: the service's own sender." locked={false}>
            <input
              className="text-input"
              value={from}
              placeholder="The service's own sender"
              maxLength={20}
              spellCheck={false}
              disabled={sending}
              onChange={(e) => setFrom(e.currentTarget.value)}
            />
          </Field>
        ) : (
          <Field
            label="From"
            hint={senders.length === 0 ? "Senders are requested on the license's page in Relatude Services and approved by Relatude." : undefined}
            locked={false}
          >
            <select className="select" value={sender} disabled={sending} onChange={(e) => setFrom(e.currentTarget.value)}>
              <option value="">The service's own sender</option>
              {senders.map((s) => (
                <option key={s} value={s}>
                  {s}
                </option>
              ))}
            </select>
          </Field>
        )}
        <Field label="To" locked={false}>
          <input className="text-input" type="tel" value={to} placeholder="+47 900 00 000" spellCheck={false} onChange={(e) => setTo(e.target.value)} />
        </Field>
        <Field label="Message" hint={message.length + " characters"} locked={false}>
          <textarea className="text-input license-sms-message" rows={2} value={message} onChange={(e) => setMessage(e.target.value)} />
        </Field>
        <div className="license-save">
          <button className="action-button primary" onClick={sendIt} disabled={sending || !to.trim() || !message.trim()}>
            <IconSend size={15} stroke={1.8} />
            {sending ? "Sending…" : "Send"}
          </button>
        </div>
      </div>
      {error && <div className="license-error">{error}</div>}
      {receipt && (
        <div className="license-status license-status-ok license-sms-receipt">
          <span className="license-status-icon">
            <IconCircleCheck size={20} stroke={1.8} />
          </span>
          <div className="license-status-text">
            <strong>Sent to {receipt.to}</strong>
            <span className="license-muted">
              {receipt.parts} {receipt.parts === 1 ? "part" : "parts"} · {receipt.credits} {receipt.credits === 1 ? "credit" : "credits"} ·{" "}
              {receipt.creditsLeft.toLocaleString()} left{receipt.messageId && " · id " + receipt.messageId}
            </span>
          </div>
        </div>
      )}
    </TestPanel>
  );
}

/**
 * Real calls to the Relatude AI service, an embedding and a completion, so whoever set up the license
 * can see both work before a database depends on them. Each test is offered when the license has the
 * credit account that kind of call is charged to, "ai_embeddings" or "ai_completion"; every call is
 * charged like any other, which is why each result says what it cost and what is left. The address starts as the one a database here uses, when one names its own, and empty is
 * the hosted service. The model lists are asked of that address each time one is opened; empty is the
 * service's default.
 */
function AiTestPanel({ configuredUrl, embeddings, completions }: { configuredUrl: string; embeddings: boolean; completions: boolean }) {
  const [serviceUrl, setServiceUrl] = useState(configuredUrl);
  const url = serviceUrl.trim();
  const models = (kind: "embeddings" | "completions"): PickerLoader => () =>
    fetchAiModels("RelatudeServices", url).then((m) => {
      if (m.error) throw new Error(m.error);
      return m[kind];
    });
  return (
    <TestPanel
      className="license-ai"
      icon={<IconSparkles size={15} stroke={1.8} />}
      title="Test AI"
      sub="real calls to the AI service, charged to the license"
      label="the AI tests"
    >
      <div className="license-ai-url">
        <Field label="Service URL" hint={configuredUrl ? "the address a database here uses" : undefined} locked={false}>
          <input className="text-input" value={serviceUrl} placeholder="https://ai.relatude.com (the hosted service)" spellCheck={false} onChange={(e) => setServiceUrl(e.target.value)} />
        </Field>
      </div>
      <div className="license-ai-tests">
        {embeddings && <AiEmbeddingTest serviceUrl={url} loadModels={models("embeddings")} />}
        {completions && <AiCompletionTest serviceUrl={url} loadModels={models("completions")} />}
      </div>
    </TestPanel>
  );
}

function AiEmbeddingTest({ serviceUrl, loadModels }: { serviceUrl: string; loadModels: PickerLoader }) {
  const [model, setModel] = useState("");
  const [text, setText] = useState("Relatude.DB is a graph database for .NET.");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<AiEmbeddingResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function run() {
    setBusy(true);
    setError(null);
    setResult(null);
    try {
      setResult(await testAiEmbedding({ serviceUrl, model: model.trim(), text }));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="license-ai-test">
      <h4>Embedding</h4>
      <Field label="Model" locked={false}>
        <Combo label="Embedding model" placeholder="the service's default" options={[]} load={loadModels} value={model} disabled={busy} onChange={(v) => setModel(String(v ?? ""))} />
      </Field>
      <Field label="Text" hint={text.length + " characters"} locked={false}>
        <textarea className="text-input license-sms-message" rows={3} value={text} onChange={(e) => setText(e.target.value)} />
      </Field>
      <div className="license-save">
        <button className="action-button primary" onClick={run} disabled={busy || !text.trim()}>
          <IconSparkles size={15} stroke={1.8} />
          {busy ? "Embedding…" : "Embed"}
        </button>
      </div>
      {error && <div className="license-error">{error}</div>}
      {result && (
        <div className="license-status license-status-ok license-ai-result">
          <span className="license-status-icon">
            <IconCircleCheck size={20} stroke={1.8} />
          </span>
          <div className="license-status-text">
            <strong>
              {result.dimensions.toLocaleString()} dimensions from {result.model || "the default model"}
            </strong>
            <span className="license-muted">
              {result.credits} {result.credits === 1 ? "credit" : "credits"} · {result.creditsLeft.toLocaleString()} left · length {result.norm.toFixed(3)}
            </span>
            <code className="license-ai-vector">
              [{result.preview.map((v) => v.toFixed(4)).join(", ")}
              {result.dimensions > result.preview.length ? ", …" : ""}]
            </code>
          </div>
        </div>
      )}
    </div>
  );
}

function AiCompletionTest({ serviceUrl, loadModels }: { serviceUrl: string; loadModels: PickerLoader }) {
  const [model, setModel] = useState("");
  const [prompt, setPrompt] = useState("Say hello to Relatude.DB in one short sentence.");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<AiCompletionResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function run() {
    setBusy(true);
    setError(null);
    setResult(null);
    try {
      setResult(await testAiCompletion({ serviceUrl, model: model.trim(), text: prompt }));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="license-ai-test">
      <h4>Completion</h4>
      <Field label="Model" locked={false}>
        <Combo label="Completion model" placeholder="the service's default" options={[]} load={loadModels} value={model} disabled={busy} onChange={(v) => setModel(String(v ?? ""))} />
      </Field>
      <Field label="Prompt" hint={prompt.length + " characters"} locked={false}>
        <textarea className="text-input license-sms-message" rows={3} value={prompt} onChange={(e) => setPrompt(e.target.value)} />
      </Field>
      <div className="license-save">
        <button className="action-button primary" onClick={run} disabled={busy || !prompt.trim()}>
          <IconSend size={15} stroke={1.8} />
          {busy ? "Asking…" : "Ask"}
        </button>
      </div>
      {error && <div className="license-error">{error}</div>}
      {result && (
        <div className="license-status license-status-ok license-ai-result">
          <span className="license-status-icon">
            <IconCircleCheck size={20} stroke={1.8} />
          </span>
          <div className="license-status-text">
            <strong>Answered by {result.model || "the default model"}</strong>
            <span className="license-muted">
              {result.credits} {result.credits === 1 ? "credit" : "credits"} · {result.creditsLeft.toLocaleString()} left
            </span>
            <p className="license-ai-answer">{result.text}</p>
          </div>
        </div>
      )}
    </div>
  );
}

/** One credit account: what is left of the month, and the rate limits on top of it. */
function Account({ account }: { account: LicenseAccount }) {
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

/** A labelled field; `extra` goes on the label's line, after the name. */
function Field({
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
        {locked && <span className="license-lock">from configuration</span>}
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
function portalLinks(status: LicenseStatus): { server: string; licensePage: string; smsSendersPage: string } {
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

function headline(status: LicenseStatus): string {
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
