import { Fragment, useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import {
  IconAlertTriangle,
  IconArrowBackUp,
  IconBrush,
  IconCircleCheck,
  IconCloud,
  IconDownload,
  IconEraser,
  IconExternalLink,
  IconFileText,
  IconFingerprint,
  IconInfoCircle,
  IconPhoto,
  IconPlugConnected,
  IconPlugConnectedX,
  IconMessage,
  IconRefresh,
  IconSend,
  IconSparkles,
  IconTrash,
  IconUpload,
  IconWand,
  IconX,
} from "@tabler/icons-react";
import { showConfirm } from "../dialogs";
import { masterLoginOptions } from "../server/auth";
import { fetchAiModels } from "../server/settings";
import { Combo, type PickerLoader } from "./Combo";
import { fetchWhoAmI } from "../server/serverInfo";
import {
  cancelPairing,
  fetchFileToTextFormats,
  fetchImagingOperations,
  fetchLicenseStatus,
  licenseCarriesAi,
  licenseCarriesFileToText,
  licenseCarriesImaging,
  licenseCarriesSms,
  licenseMayUseAnySmsSender,
  licenseSmsSenders,
  lookUpApiKey,
  pollPairing,
  runFileToTextTest,
  runImagingTest,
  saveLicenseSettings,
  sendTestSms,
  startPairing,
  testAiCompletion,
  testAiEmbedding,
  type AiCompletionResult,
  type AiEmbeddingResult,
  type FileToTextResult,
  type ImagingAnswerResult,
  type ImagingBoolResult,
  type ImagingImageResult,
  type ImagingLeftAsIsResult,
  type ImagingMetaResult,
  type SmsReceipt,
  type InstallationInfo,
  type LicenseAccount,
  type LicenseStatus,
  type PairingHandle,
} from "../server/license";
import { formatBytes, formatTime } from "../format";
import { Loading } from "./Loading";
import { FoldHead } from "./LogsSection";
import { CopyText } from "./CopyText";
import { DialogTools } from "./DialogTools";
import { ImageViewer, type ImageInset } from "./ImageViewer";

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
      {licenseCarriesImaging(status) && <ImagingTestPanel configuredUrl={status.imagingServiceUrl ?? ""} />}
      {licenseCarriesFileToText(status) && <FileToTextTestPanel configuredUrl={status.fileToTextServiceUrl ?? ""} />}
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
          <strong>It is free</strong>, and lets this installation use <strong>Relatude Services</strong> for AI, images, the text of files and text messages instead of your own vendor keys.
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
              title={apiKeyLocked ? "The API key is set in appsettings, so it is removed there." : "Take this installation out from under the license"}
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
              <>
                {/* what the key is called in Relatude Services, when the license server says - and the key has a name there */}
                {status.license?.apiKeyName && (
                  <span className="license-key-name" title="What this API key is called on the license's page in Relatude Services">
                    {status.license.apiKeyName}
                  </span>
                )}
                {status.apiKeyStart && (
                  <span className="license-key-start" title={`The saved API key starts with ${status.apiKeyStart}`}>
                    {status.apiKeyStart}…
                  </span>
                )}
              </>
            }
            hint={apiKeyLocked ? undefined : "A secret: once saved, only its first five characters are shown. On a production server, keep it in configuration or user secrets instead."}
          >
            <input
              className="text-input"
              type="password"
              autoComplete="new-password"
              spellCheck={false}
              value={apiKey}
              placeholder={apiKeyLocked ? "Set in appsettings" : status.hasApiKey ? "Paste another one to replace it" : "Paste the API key"}
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
          {status.installation && <InstallationKey installation={status.installation} />}
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
            {status.locked.length === 1 ? "1 field is" : `${status.locked.length} fields are`} set in appsettings.
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
function TestPanel({
  className,
  icon,
  title,
  sub,
  label,
  onOpen,
  children,
}: {
  className: string;
  icon: ReactNode;
  title: string;
  sub: string;
  label: string;
  /** told when the panel is opened, for a test that asks its service something only once somebody looks */
  onOpen?: () => void;
  children: ReactNode;
}) {
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
        <FoldHead
          open={open}
          label={label}
          onToggle={() => {
            if (!open) onOpen?.();
            setOpen(!open);
          }}
        >
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

/**
 * What a service offers, asked of the address in the panel once the panel is open and the address
 * has stopped changing for a moment. Asking costs nothing and needs no license. Null until there is
 * an answer for this very address.
 */
function useServiceInfo<T>(enabled: boolean, url: string, load: (url: string) => Promise<T>): { info: T | null; error: string | null } | null {
  const [state, setState] = useState<{ url: string; info: T | null; error: string | null } | null>(null);
  useEffect(() => {
    if (!enabled) return;
    let stale = false;
    const timer = window.setTimeout(() => {
      load(url)
        .then((info) => !stale && setState({ url, info, error: null }))
        .catch((e) => !stale && setState({ url, info: null, error: e instanceof Error ? e.message : String(e) }));
    }, 400);
    return () => {
      stale = true;
      window.clearTimeout(timer);
    };
  }, [enabled, url, load]);
  return state?.url === url ? state : null;
}

/** An object url for a blob while it is shown, let go when the blob changes or the component goes. */
function useObjectUrl(blob: Blob | null): string | null {
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    if (!blob) {
      setUrl(null);
      return;
    }
    const made = URL.createObjectURL(blob);
    setUrl(made);
    return () => URL.revokeObjectURL(made);
  }, [blob]);
  return url;
}

function credits(n: number): string {
  return `${n.toLocaleString()} ${n === 1 ? "credit" : "credits"}`;
}

/**
 * A file or files to send, chosen with the button or dropped on the field. Each chosen file is a row
 * with its name and size, a thumbnail when it is a picture, and a way to take it out again.
 */
function FilePick({
  label,
  hint,
  accept,
  files,
  multiple,
  disabled,
  onChange,
}: {
  label: string;
  hint?: string;
  accept?: string;
  files: File[];
  multiple?: boolean;
  disabled?: boolean;
  onChange: (files: File[]) => void;
}) {
  const input = useRef<HTMLInputElement>(null);
  const [over, setOver] = useState(false);
  const take = (chosen: File[]) => {
    if (chosen.length > 0) onChange(multiple ? [...files, ...chosen] : chosen.slice(0, 1));
  };
  return (
    <div className="license-field">
      <span className="license-field-label">{label}</span>
      <div
        className={"license-pick" + (over ? " over" : "")}
        onDragOver={(e) => {
          if (disabled || !e.dataTransfer.types.includes("Files")) return;
          e.preventDefault();
          setOver(true);
        }}
        onDragLeave={() => setOver(false)}
        onDrop={(e) => {
          if (disabled) return;
          e.preventDefault();
          setOver(false);
          take(Array.from(e.dataTransfer.files));
        }}
      >
        {files.map((file, i) => (
          <PickedFile key={i + ":" + file.name + ":" + file.size} file={file} disabled={disabled} onRemove={() => onChange(files.filter((_, j) => j !== i))} />
        ))}
        {(multiple || files.length === 0) && (
          <div className="license-pick-choose">
            <button type="button" className="action-button" onClick={() => input.current?.click()} disabled={disabled}>
              <IconUpload size={14} stroke={1.8} />
              {files.length === 0 ? "Choose…" : "Add…"}
            </button>
            <span className="license-muted">or drop {multiple ? "files" : "one"} here</span>
          </div>
        )}
        <input
          ref={input}
          type="file"
          hidden
          accept={accept}
          multiple={multiple}
          onChange={(e) => {
            const chosen = Array.from(e.currentTarget.files ?? []);
            // or choosing the same file twice in a row is not a change
            e.currentTarget.value = "";
            take(chosen);
          }}
        />
      </div>
      {hint && <span className="license-muted license-field-hint">{hint}</span>}
    </div>
  );
}

function PickedFile({ file, disabled, onRemove }: { file: File; disabled?: boolean; onRemove: () => void }) {
  const picture = file.type.startsWith("image/");
  const src = useObjectUrl(picture ? file : null);
  return (
    <div className="license-picked">
      {picture ? (
        src && <img className="license-picked-thumb" src={src} alt="" />
      ) : (
        <span className="license-picked-thumb">
          <IconFileText size={16} stroke={1.6} />
        </span>
      )}
      <span className="license-picked-name" title={file.name}>
        {file.name}
      </span>
      <span className="license-muted">{formatBytes(file.size)}</span>
      <button type="button" className="icon-button" onClick={onRemove} disabled={disabled} title={"Take " + file.name + " out"}>
        <IconX size={13} stroke={1.8} />
      </button>
    </div>
  );
}

/** How much the mask editor's undo may hold: a large image keeps fewer steps. */
const maskUndoBytes = 120_000_000;

function paintContext(canvas: HTMLCanvasElement) {
  return canvas.getContext("2d", { willReadFrequently: true })!;
}

/** The paint as the mask the service takes: as large as the canvas, white where painted and black elsewhere. Null when nothing is painted. */
function paintToMask(canvas: HTMLCanvasElement): Promise<Blob | null> {
  const paint = paintContext(canvas).getImageData(0, 0, canvas.width, canvas.height);
  const p = paint.data;
  let any = false;
  for (let i = 0; i < p.length; i += 4) {
    const a = p[i + 3];
    if (a) any = true;
    p[i] = p[i + 1] = p[i + 2] = a;
    p[i + 3] = 255;
  }
  if (!any) return Promise.resolve(null);
  const out = document.createElement("canvas");
  out.width = canvas.width;
  out.height = canvas.height;
  out.getContext("2d")!.putImageData(paint, 0, 0);
  return new Promise((resolve) => out.toBlob(resolve, "image/png"));
}

/** A mask file as paint: the lighter a pixel, the more it is painted, stretched to the canvas when its size differs. */
async function drawMaskFile(canvas: HTMLCanvasElement, file: Blob) {
  const bitmap = await createImageBitmap(file);
  const g = paintContext(canvas);
  g.globalCompositeOperation = "source-over";
  g.clearRect(0, 0, canvas.width, canvas.height);
  g.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
  bitmap.close();
  const paint = g.getImageData(0, 0, canvas.width, canvas.height);
  const p = paint.data;
  for (let i = 0; i < p.length; i += 4) {
    // a transparent pixel is black: nothing changes there
    p[i + 3] = Math.round(((p[i] * 299 + p[i + 1] * 587 + p[i + 2] * 114) / 1000) * (p[i + 3] / 255));
    p[i] = 255;
    p[i + 1] = 0;
    p[i + 2] = 0;
  }
  g.putImageData(paint, 0, 0);
}

/**
 * The mask painted onto the image itself rather than made elsewhere and chosen as a file. The paint
 * lies on a canvas of the image's own size, however small the picture shows here, so what is handed
 * on is the mask the service asks for: as large as the image, white where it was painted and black
 * where it was not - or null while nothing is. A mask made elsewhere can be loaded and painted on
 * from there, and Undo steps back a stroke at a time.
 */
function MaskEditor({
  label,
  image,
  mask,
  disabled,
  onChange,
}: {
  label: string;
  image: File;
  mask: File | null;
  disabled?: boolean;
  onChange: (mask: File | null) => void;
}) {
  const src = useObjectUrl(image);
  const canvas = useRef<HTMLCanvasElement>(null);
  const ring = useRef<HTMLSpanElement>(null);
  const loadInput = useRef<HTMLInputElement>(null);
  const [size, setSize] = useState<{ w: number; h: number } | null>(null);
  const [erasing, setErasing] = useState(false);
  const [brush, setBrush] = useState(28);
  const [undoDepth, setUndoDepth] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const undo = useRef<ImageData[]>([]);
  const stroke = useRef<{ id: number; x: number; y: number } | null>(null);
  // the mask the editor opened with - painted before, under another operation - drawn once the canvas has the image's size
  const initial = useRef(mask);
  // a mask made before a later one, or after the editor closed, is not handed on
  const made = useRef(0);
  useEffect(
    () => () => {
      made.current++;
    },
    [],
  );

  async function handOn() {
    const turn = ++made.current;
    const blob = await paintToMask(canvas.current!);
    if (turn === made.current) onChange(blob && new File([blob], "mask.png", { type: "image/png" }));
  }

  function keep() {
    const c = canvas.current!;
    const steps = Math.max(1, Math.floor(maskUndoBytes / (c.width * c.height * 4)));
    undo.current.push(paintContext(c).getImageData(0, 0, c.width, c.height));
    if (undo.current.length > steps) undo.current.splice(0, undo.current.length - steps);
    setUndoDepth(undo.current.length);
  }

  function stepBack() {
    const last = undo.current.pop();
    setUndoDepth(undo.current.length);
    if (!last) return;
    paintContext(canvas.current!).putImageData(last, 0, 0);
    void handOn();
  }

  function clear() {
    keep();
    const c = canvas.current!;
    paintContext(c).clearRect(0, 0, c.width, c.height);
    made.current++;
    onChange(null);
  }

  async function load(file: File) {
    setError(null);
    keep();
    try {
      await drawMaskFile(canvas.current!, file);
      void handOn();
    } catch {
      stepBack();
      setError(file.name + " could not be read as an image.");
    }
  }

  /** Where the pointer is on the canvas, in the image's own pixels, and how many of those one pixel on screen is. */
  function at(e: React.PointerEvent<HTMLCanvasElement>) {
    const c = e.currentTarget;
    const box = c.getBoundingClientRect();
    const scale = c.width / box.width;
    return { x: (e.clientX - box.left) * scale, y: (e.clientY - box.top) * (c.height / box.height), scale };
  }

  function dab(from: { x: number; y: number }, to: { x: number; y: number }, scale: number) {
    const g = paintContext(canvas.current!);
    const width = brush * scale;
    g.globalCompositeOperation = erasing ? "destination-out" : "source-over";
    g.fillStyle = "#f00";
    g.strokeStyle = "#f00";
    g.lineWidth = width;
    g.lineCap = "round";
    g.lineJoin = "round";
    g.beginPath();
    if (from.x === to.x && from.y === to.y) {
      g.arc(to.x, to.y, width / 2, 0, Math.PI * 2);
      g.fill();
    } else {
      g.moveTo(from.x, from.y);
      g.lineTo(to.x, to.y);
      g.stroke();
    }
  }

  /** The brush's outline where it would paint: at the pointer, or in the middle while its size is set. */
  function showRing(x: number, y: number) {
    const r = ring.current;
    if (!r) return;
    r.style.display = "block";
    r.style.left = x + "px";
    r.style.top = y + "px";
  }

  function hideRing() {
    if (ring.current) ring.current.style.display = "none";
  }

  function end(e: React.PointerEvent<HTMLCanvasElement>) {
    if (stroke.current?.id !== e.pointerId) return;
    stroke.current = null;
    void handOn();
  }

  const ready = size !== null && !disabled;
  return (
    <div className="license-field">
      <span className="license-field-label">{label}</span>
      <div className="license-mask-tools" role="toolbar" aria-label="Mask">
        <button
          type="button"
          className={"icon-button labelled" + (erasing ? "" : " active")}
          aria-pressed={!erasing}
          onClick={() => setErasing(false)}
          disabled={!ready}
          title="Paint where the change goes"
        >
          <IconBrush size={15} stroke={1.8} />
          Paint
        </button>
        <button
          type="button"
          className={"icon-button labelled" + (erasing ? " active" : "")}
          aria-pressed={erasing}
          onClick={() => setErasing(true)}
          disabled={!ready}
          title="Rub out paint where the image should stay as it is"
        >
          <IconEraser size={15} stroke={1.8} />
          Erase
        </button>
        <label className="license-mask-size" title="The brush, as wide as it shows on the picture">
          <input
            type="range"
            min={4}
            max={120}
            value={brush}
            disabled={!ready}
            aria-label="Brush size"
            onChange={(e) => {
              setBrush(Number(e.target.value));
              const c = canvas.current;
              if (c) showRing(c.clientWidth / 2, c.clientHeight / 2);
            }}
            onPointerUp={hideRing}
            onBlur={hideRing}
          />
          <span>{brush} px</span>
        </label>
        <span className="license-mask-actions">
          <button type="button" className="icon-button" onClick={stepBack} disabled={!ready || undoDepth === 0} title="Undo the last stroke">
            <IconArrowBackUp size={15} stroke={1.8} />
          </button>
          <button type="button" className="icon-button" onClick={() => loadInput.current?.click()} disabled={!ready} title="Load a mask made elsewhere: white where the change goes">
            <IconUpload size={15} stroke={1.8} />
          </button>
          <button type="button" className="icon-button danger" onClick={clear} disabled={!ready || !mask} title="Clear the mask">
            <IconTrash size={15} stroke={1.8} />
          </button>
        </span>
        <input
          ref={loadInput}
          type="file"
          hidden
          accept={imageTypes}
          onChange={(e) => {
            const chosen = e.currentTarget.files?.[0];
            e.currentTarget.value = "";
            if (chosen) void load(chosen);
          }}
        />
      </div>
      <div className={"license-imaging-picture license-mask-stage" + (ready ? "" : " idle")}>
        {src && (
          <img
            src={src}
            alt="The image the mask is painted on"
            draggable={false}
            onLoad={(e) => {
              const c = canvas.current!;
              c.width = e.currentTarget.naturalWidth;
              c.height = e.currentTarget.naturalHeight;
              undo.current = [];
              setUndoDepth(0);
              setSize({ w: c.width, h: c.height });
              if (initial.current) void drawMaskFile(c, initial.current).catch(() => undefined);
            }}
          />
        )}
        <canvas
          ref={canvas}
          className="license-mask-paint"
          onPointerDown={(e) => {
            if (!ready || e.button !== 0) return;
            e.preventDefault();
            e.currentTarget.setPointerCapture(e.pointerId);
            keep();
            const p = at(e);
            dab(p, p, p.scale);
            stroke.current = { id: e.pointerId, x: p.x, y: p.y };
          }}
          onPointerMove={(e) => {
            if (!ready) return;
            showRing(e.nativeEvent.offsetX, e.nativeEvent.offsetY);
            const s = stroke.current;
            if (!s || s.id !== e.pointerId) return;
            const p = at(e);
            dab(s, p, p.scale);
            s.x = p.x;
            s.y = p.y;
          }}
          onPointerUp={end}
          onPointerCancel={end}
          onPointerLeave={hideRing}
        />
        <span ref={ring} className={"license-mask-ring" + (erasing ? " erasing" : "")} style={{ width: brush, height: brush }} />
      </div>
      {error ? (
        <span className="license-field-hint license-offer-bad">{error}</span>
      ) : (
        <span className="license-muted license-field-hint">
          {mask && size
            ? `Sent as a ${size.w} × ${size.h} mask: white where painted, black elsewhere.`
            : "Paint over the image where the change goes. What is not painted stays as it is."}
        </span>
      )}
    </div>
  );
}

/** What the service says about itself, under the address: what it offers, what a call costs, and how large a file may be. */
function ServiceOffer({ answer, children }: { answer: { error: string | null } | null; children?: ReactNode }) {
  if (!answer) return <p className="license-muted license-offer">Asking the service what it offers…</p>;
  if (answer.error) return <p className="license-offer license-offer-bad">The service did not answer: {answer.error}</p>;
  return <div className="license-muted license-offer">{children}</div>;
}

/** The operations of the Imaging service as the test offers them, and the fields each takes, named as the service names them. */
interface ImagingOp {
  key: string;
  label: string;
  image: boolean;
  mask?: "optional" | "required";
  extra?: "inspiration" | "references";
  text?: { field: "description" | "instruction" | "hint" | "question"; label: string; required: boolean; placeholder: string };
  size?: boolean;
  transparent?: boolean;
  factor?: boolean;
  margins?: boolean;
  language?: boolean;
}

const imagingOps: ImagingOp[] = [
  {
    key: "create-image",
    label: "Create",
    image: false,
    extra: "inspiration",
    text: { field: "description", label: "Description", required: true, placeholder: "A lighthouse on a rocky coast at dusk, oil painting" },
    size: true,
    transparent: true,
  },
  {
    key: "manipulate-image",
    label: "Manipulate",
    image: true,
    mask: "optional",
    extra: "references",
    text: { field: "instruction", label: "Instruction", required: true, placeholder: "Make it a winter scene" },
  },
  { key: "remove-background", label: "Remove background", image: true },
  { key: "upscale", label: "Upscale", image: true, factor: true },
  { key: "remove-object", label: "Remove object", image: true, mask: "required" },
  {
    key: "expand-image",
    label: "Expand",
    image: true,
    margins: true,
    text: { field: "hint", label: "Hint", required: false, placeholder: "What the new space should show" },
  },
  { key: "shrink-image", label: "Shrink", image: true, margins: true },
  { key: "rotate-if-needed", label: "Rotate if needed", image: true },
  { key: "image-to-meta", label: "Describe", image: true, language: true },
  {
    key: "ask-about-image",
    label: "Ask",
    image: true,
    text: { field: "question", label: "Question", required: true, placeholder: "What is the person in the picture holding?" },
  },
  {
    key: "ask-about-image-bool",
    label: "Ask yes / no",
    image: true,
    text: { field: "question", label: "Question", required: true, placeholder: "Is there a dog in the picture?" },
  },
];

const marginSides = ["top", "right", "bottom", "left"] as const;
const imageTypes = "image/png,image/jpeg,image/webp";

/**
 * Real calls to the Relatude Imaging service, one operation at a time, so whoever set up the license
 * can see each one work before code depends on it. Only offered when the license has the "ai_image"
 * credit account every operation is charged to, which is why each answer says what it cost. The same
 * call made again is answered from what the service kept, at its price for that, unless "New answer"
 * asks for another. An image answer can be taken as the image of the next call - upscale a cutout,
 * say - which the service then knows by its name and does not need sent.
 */
function ImagingTestPanel({ configuredUrl }: { configuredUrl: string }) {
  const [serviceUrl, setServiceUrl] = useState(configuredUrl);
  const url = serviceUrl.trim();
  const [opened, setOpened] = useState(false);
  const offer = useServiceInfo(opened, url, fetchImagingOperations);
  const [opKey, setOpKey] = useState("remove-background");
  const op = imagingOps.find((o) => o.key === opKey)!;
  const [image, setImage] = useState<File[]>([]);
  // a new image starts a new mask, in a new editor, even when it is the same file chosen again
  const [imageTurn, setImageTurn] = useState(0);
  const [mask, setMask] = useState<File | null>(null);
  const [extra, setExtra] = useState<File[]>([]);
  const [texts, setTexts] = useState({ description: "", instruction: "", hint: "", question: "" });
  const [size, setSize] = useState({ width: "", height: "" });
  const [transparent, setTransparent] = useState(false);
  const [factor, setFactor] = useState("2");
  const [margins, setMargins] = useState({ top: "", right: "", bottom: "", left: "" });
  const [language, setLanguage] = useState("en");
  const [fresh, setFresh] = useState(false);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{
    op: ImagingOp;
    answer: ImagingImageResult | ImagingMetaResult | ImagingLeftAsIsResult | ImagingAnswerResult | ImagingBoolResult;
    input: File | null;
    inset: ImageInset | null;
    question: string;
  } | null>(null);
  const [error, setError] = useState<string | null>(null);

  const available = (key: string) => offer?.info?.operations.find((o) => o.key === key)?.available;
  const marginTotal = marginSides.reduce((sum, side) => sum + (Number(margins[side]) || 0), 0);
  const ready =
    (!op.image || image.length > 0) &&
    (op.mask !== "required" || mask !== null) &&
    (!op.text?.required || texts[op.text.field].trim().length > 0) &&
    (!op.size || !size.width.trim() === !size.height.trim()) &&
    (!op.margins || marginTotal > 0);

  async function run() {
    setBusy(true);
    setError(null);
    setResult(null);
    try {
      const form = new FormData();
      form.set("serviceUrl", url);
      form.set("operation", op.key);
      if (fresh) form.set("fresh", "true");
      if (op.image) form.set("image", image[0]);
      if (op.mask && mask) form.set("mask", mask);
      if (op.extra) for (const file of extra) form.append(op.extra, file);
      if (op.text && texts[op.text.field].trim()) form.set(op.text.field, texts[op.text.field].trim());
      if (op.size && size.width.trim()) {
        form.set("width", size.width.trim());
        form.set("height", size.height.trim());
      }
      if (op.transparent && transparent) form.set("transparent", "true");
      if (op.factor) form.set("factor", factor);
      if (op.margins) for (const side of marginSides) if (Number(margins[side]) > 0) form.set(side, String(Number(margins[side])));
      if (op.language && language.trim()) form.set("language", language.trim());
      // where the image sent lies on an expanded or shrunk answer: in by the margins, or out by them
      const sign = op.key === "expand-image" ? 1 : op.key === "shrink-image" ? -1 : 0;
      const inset = sign === 0 ? null : { top: sign * (Number(margins.top) || 0), right: sign * (Number(margins.right) || 0), bottom: sign * (Number(margins.bottom) || 0), left: sign * (Number(margins.left) || 0) };
      setResult({ op, answer: await runImagingTest(form), input: op.image ? image[0] : null, inset, question: texts.question.trim() });
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  function chooseImage(files: File[]) {
    setImage(files);
    setImageTurn((t) => t + 1);
    setMask(null);
  }

  /** The answer becomes the image of the next call: the service holds it already, so it is named, not sent. */
  function takeAsImage(answer: ImagingImageResult) {
    chooseImage([new File([answer.png], (result?.op.key ?? "answer") + ".png", { type: "image/png" })]);
    if (!op.image) setOpKey("upscale");
  }

  return (
    <TestPanel
      className="license-imaging"
      icon={<IconPhoto size={15} stroke={1.8} />}
      title="Test Imaging"
      sub="real calls to the imaging service, charged to the license"
      label="the imaging test"
      onOpen={() => setOpened(true)}
    >
      <div className="license-ai-url">
        <Field label="Service URL" hint={configuredUrl ? "the address a database here uses" : undefined} locked={false}>
          <input className="text-input" value={serviceUrl} placeholder="https://imaging.services.relatude.com (the hosted service)" spellCheck={false} onChange={(e) => setServiceUrl(e.target.value)} />
        </Field>
        <ServiceOffer answer={offer}>
          {offer?.info && (
            <>
              {credits(offer.info.creditsPerOperation)} a call, {credits(offer.info.creditsPerCachedOperation)} for an answer made before · images up to{" "}
              {formatBytes(offer.info.maxFileBytes)}
            </>
          )}
        </ServiceOffer>
      </div>
      <div className="license-ops" role="tablist" aria-label="Operation">
        {imagingOps.map((o) => {
          const offered = available(o.key);
          return (
            <button
              key={o.key}
              role="tab"
              aria-selected={o.key === op.key}
              className={"license-op" + (o.key === op.key ? " active" : "") + (offered === false ? " unavailable" : "")}
              title={o.key + (offered === false ? " - not offered by this service" : "")}
              onClick={() => {
                setOpKey(o.key);
                setError(null);
              }}
              disabled={busy}
            >
              {o.label}
            </button>
          );
        })}
      </div>
      <div className="license-imaging-body">
        <div className="license-imaging-fields">
          {available(op.key) === false && <p className="license-offer license-offer-bad">This service does not offer {op.key}: a call would be refused, and cost nothing.</p>}
          {op.image && <FilePick label="Image" hint="PNG, JPEG or WebP" accept={imageTypes} files={image} disabled={busy} onChange={chooseImage} />}
          {op.mask &&
            (image.length > 0 ? (
              <MaskEditor
                key={imageTurn}
                label={op.mask === "required" ? "Mask" : "Mask (optional)"}
                image={image[0]}
                mask={mask}
                disabled={busy}
                onChange={setMask}
              />
            ) : (
              <div className="license-field">
                <span className="license-field-label">{op.mask === "required" ? "Mask" : "Mask (optional)"}</span>
                <span className="license-muted license-field-hint">Choose the image first, then paint on it where the change goes.</span>
              </div>
            ))}
          {op.text && (
            <Field label={op.text.label + (op.text.required ? "" : " (optional)")} locked={false}>
              <textarea
                className="text-input license-sms-message"
                rows={2}
                value={texts[op.text.field]}
                placeholder={op.text.placeholder}
                disabled={busy}
                onChange={(e) => {
                  const field = op.text!.field;
                  const value = e.target.value;
                  setTexts((t) => ({ ...t, [field]: value }));
                }}
              />
            </Field>
          )}
          {op.extra && (
            <FilePick
              label={op.extra === "inspiration" ? "Inspiration (optional)" : "References (optional)"}
              hint={op.extra === "inspiration" ? "Images whose look the new one should take after." : "Other images the change may draw on. Not every provider can combine images."}
              accept={imageTypes}
              files={extra}
              multiple
              disabled={busy}
              onChange={setExtra}
            />
          )}
          {op.size && (
            <div className="license-imaging-row">
              <Field label="Width" locked={false}>
                <input className="text-input" inputMode="numeric" value={size.width} placeholder="any" disabled={busy} onChange={(e) => setSize({ ...size, width: e.target.value })} />
              </Field>
              <Field label="Height" locked={false}>
                <input className="text-input" inputMode="numeric" value={size.height} placeholder="any" disabled={busy} onChange={(e) => setSize({ ...size, height: e.target.value })} />
              </Field>
              {op.transparent && (
                <label className="license-toggle license-imaging-check">
                  <input type="checkbox" checked={transparent} disabled={busy} onChange={(e) => setTransparent(e.target.checked)} />
                  <span>Transparent background</span>
                </label>
              )}
            </div>
          )}
          {op.factor && (
            <Field label="Factor" locked={false}>
              <select className="select license-imaging-narrow" value={factor} disabled={busy} onChange={(e) => setFactor(e.target.value)}>
                <option value="2">2 × larger</option>
                <option value="4">4 × larger</option>
              </select>
            </Field>
          )}
          {op.margins && (
            <div className="license-imaging-row">
              {marginSides.map((side) => (
                <Field key={side} label={side[0].toUpperCase() + side.slice(1)} locked={false}>
                  <input
                    className="text-input"
                    inputMode="numeric"
                    value={margins[side]}
                    placeholder="0"
                    disabled={busy}
                    onChange={(e) => setMargins({ ...margins, [side]: e.target.value })}
                  />
                </Field>
              ))}
            </div>
          )}
          {op.language && (
            <Field label="Language" hint="A code such as en or nb, for the title, description and keywords." locked={false}>
              <input className="text-input license-imaging-narrow" value={language} spellCheck={false} disabled={busy} onChange={(e) => setLanguage(e.target.value)} />
            </Field>
          )}
          <div className="license-save">
            <button className="action-button primary" onClick={run} disabled={busy || !ready}>
              <IconWand size={15} stroke={1.8} />
              {busy ? "Working…" : op.label}
            </button>
            <label className="license-toggle" title="Make the call again, and pay for it again, rather than take the answer kept for the same call">
              <input type="checkbox" checked={fresh} disabled={busy} onChange={(e) => setFresh(e.target.checked)} />
              <span>New answer</span>
            </label>
          </div>
          {error && <div className="license-error">{error}</div>}
        </div>
        <div className="license-imaging-answer">
          {result ? (
            result.answer.kind === "image" ? (
              <ImagingImageAnswer
                opKey={result.op.key}
                answer={result.answer}
                // a turn is no change to lay over the image: a quarter of one does not even have its shape
                input={result.answer.rotation === undefined ? result.input : null}
                inset={result.inset}
                onUse={takeAsImage}
              />
            ) : result.answer.kind === "left-as-is" ? (
              <ImagingLeftAsIsAnswer answer={result.answer} input={result.input} />
            ) : result.answer.kind === "answer" || result.answer.kind === "bool" ? (
              <ImagingQuestionAnswer answer={result.answer} question={result.question} input={result.input} />
            ) : (
              <ImagingMetaAnswer meta={result.answer} input={result.input} />
            )
          ) : (
            <div className="license-imaging-empty license-muted">{busy ? "The service is working on it…" : "The answer shows here."}</div>
          )}
        </div>
      </div>
    </TestPanel>
  );
}

/** An image answer, laid over the image it was made from (when there was one) with a split to drag between them. */
function ImagingImageAnswer({
  opKey,
  answer,
  input,
  inset,
  onUse,
}: {
  opKey: string;
  answer: ImagingImageResult;
  input: File | null;
  inset: ImageInset | null;
  onUse: (answer: ImagingImageResult) => void;
}) {
  const src = useObjectUrl(answer.png);
  const before = useObjectUrl(input);
  return (
    <div className="license-imaging-result">
      {src && <ImageViewer src={src} alt={"The answer to " + opKey} before={before} beforeInset={inset} />}
      <div className="license-status license-status-ok">
        <span className="license-status-icon">
          <IconCircleCheck size={20} stroke={1.8} />
        </span>
        <div className="license-status-text">
          <strong>
            {answer.rotation !== undefined && `Turned ${answer.rotation}° clockwise · `}
            {answer.width} × {answer.height} PNG
          </strong>
          <span className="license-muted">
            {credits(answer.credits)} · {answer.creditsLeft.toLocaleString()} left · {formatBytes(answer.png.size)}
            {answer.cached ? " · the answer kept from before" : ""}
          </span>
          <code className="license-ai-vector" title="The image's name at the service: a later call naming it sends nothing">
            sha256 {answer.sha256}
          </code>
        </div>
        <div className="license-actions">
          {src && (
            <a className="action-button" href={src} target="_blank" rel="noreferrer" title="Open the answer at full size in a new tab">
              <IconExternalLink size={14} stroke={1.8} />
              Open
            </a>
          )}
          {src && (
            <a className="action-button" href={src} download={opKey + ".png"}>
              <IconDownload size={14} stroke={1.8} />
              Download
            </a>
          )}
          <button className="action-button" onClick={() => onUse(answer)} title="Take this answer as the image of the next call">
            <IconArrowBackUp size={14} stroke={1.8} />
            Use as image
          </button>
        </div>
      </div>
    </div>
  );
}

/** The answer to a question about the image, in words or as yes or no with how sure, under the question and beside the image. */
function ImagingQuestionAnswer({ answer, question, input }: { answer: ImagingAnswerResult | ImagingBoolResult; question: string; input: File | null }) {
  const src = useObjectUrl(input);
  return (
    <div className="license-imaging-result">
      {src && <ImageViewer src={src} alt="The image asked about" />}
      <div className="license-status license-status-ok">
        <span className="license-status-icon">
          <IconCircleCheck size={20} stroke={1.8} />
        </span>
        <div className="license-status-text">
          {question && <span className="license-muted">{question}</span>}
          {answer.kind === "bool" ? (
            <strong title="How sure the answer is, from 0 (a guess: the image says nothing either way) to 100 (it plainly shows it)">
              {answer.answer ? "Yes" : "No"} · {answer.certainty} % sure
            </strong>
          ) : (
            <span className="license-imaging-answer-text">{answer.answer}</span>
          )}
          <span className="license-muted">
            {credits(answer.credits)} · {answer.creditsLeft.toLocaleString()} left
            {answer.cached ? " · the answer kept from before" : ""}
          </span>
        </div>
      </div>
    </div>
  );
}

/** rotate-if-needed leaving the image as it is, with the image it looked at. */
function ImagingLeftAsIsAnswer({ answer, input }: { answer: ImagingLeftAsIsResult; input: File | null }) {
  const src = useObjectUrl(input);
  return (
    <div className="license-imaging-result">
      {src && <ImageViewer src={src} alt="The image, left as it is" />}
      <div className="license-status license-status-ok">
        <span className="license-status-icon">
          <IconCircleCheck size={20} stroke={1.8} />
        </span>
        <div className="license-status-text">
          <strong>Left as it is · not turned</strong>
          <span>It stands the right way up already, or which way is up could not be told.</span>
          <span className="license-muted">
            {credits(answer.credits)} · {answer.creditsLeft.toLocaleString()} left
            {answer.cached ? " · the answer kept from before" : ""}
          </span>
        </div>
      </div>
    </div>
  );
}

/** What image-to-meta said, with the image beside it and the focus point and the boxes of what is in it drawn on top. */
function ImagingMetaAnswer({ meta, input }: { meta: ImagingMetaResult; input: File | null }) {
  const src = useObjectUrl(input);
  const percent = (part: number, whole: number) => (whole > 0 ? (100 * part) / whole : 0) + "%";
  return (
    <div className="license-imaging-result">
      {src && (
        <ImageViewer src={src} alt="The image described">
          {(natural) => (
            <>
              {meta.objects.map((o, i) => (
                <span
                  key={i}
                  className="license-imaging-box"
                  style={{ left: percent(o.x, natural.w), top: percent(o.y, natural.h), width: percent(o.width, natural.w), height: percent(o.height, natural.h) }}
                  title={`${o.type}${o.name ? ": " + o.name : ""} (${Math.round(o.confidence * 100)} %)`}
                >
                  <span>{o.name || o.type}</span>
                </span>
              ))}
              {meta.focus && (
                <span className="license-imaging-focus" style={{ left: percent(meta.focus.x, natural.w), top: percent(meta.focus.y, natural.h) }} title="The point the image is about" />
              )}
            </>
          )}
        </ImageViewer>
      )}
      <div className="license-status license-status-ok">
        <span className="license-status-icon">
          <IconCircleCheck size={20} stroke={1.8} />
        </span>
        <div className="license-status-text">
          <strong>{meta.title || "No title"}</strong>
          {meta.description && <span>{meta.description}</span>}
          {meta.keywords.length > 0 && (
            <div className="license-chips license-imaging-keywords">
              {meta.keywords.map((k) => (
                <span key={k} className="license-chip">
                  {k}
                </span>
              ))}
            </div>
          )}
          <span className="license-muted">
            {meta.language ? meta.language + " · " : ""}
            {meta.objects.length} {meta.objects.length === 1 ? "thing" : "things"} found{meta.focus ? " · a focus point" : ""} · {credits(meta.credits)} ·{" "}
            {meta.creditsLeft.toLocaleString()} left{meta.cached ? " · the answer kept from before" : ""}
          </span>
        </div>
      </div>
    </div>
  );
}

/** the most of a text put on the page; the whole of it is in the download */
const shownTextChars = 200_000;

/**
 * A real file read through the Relatude FileToText service, so whoever set up the license can see what
 * the service makes of the files the database holds before code depends on it. Only offered when the
 * license has the "filetotext" credit account every file is charged to. The kinds of file the service
 * reads are listed once the panel is open; a file it cannot read is refused for nothing.
 */
function FileToTextTestPanel({ configuredUrl }: { configuredUrl: string }) {
  const [serviceUrl, setServiceUrl] = useState(configuredUrl);
  const url = serviceUrl.trim();
  const [opened, setOpened] = useState(false);
  const offer = useServiceInfo(opened, url, fetchFileToTextFormats);
  const [file, setFile] = useState<File[]>([]);
  const [languages, setLanguages] = useState("");
  const [fresh, setFresh] = useState(false);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<FileToTextResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const readable = offer?.info?.formats.filter((f) => f.available) ?? [];

  async function run() {
    setBusy(true);
    setError(null);
    setResult(null);
    try {
      const form = new FormData();
      form.set("serviceUrl", url);
      form.set("file", file[0]);
      if (languages.trim()) form.set("languages", languages.trim());
      if (fresh) form.set("fresh", "true");
      setResult(await runFileToTextTest(form));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <TestPanel
      className="license-filetotext"
      icon={<IconFileText size={15} stroke={1.8} />}
      title="Test file to text"
      sub="reads a real file, charged to the license"
      label="the file to text test"
      onOpen={() => setOpened(true)}
    >
      <div className="license-ai-url">
        <Field label="Service URL" hint={configuredUrl ? "the address a database here uses" : undefined} locked={false}>
          <input className="text-input" value={serviceUrl} placeholder="https://filetotext.services.relatude.com (the hosted service)" spellCheck={false} onChange={(e) => setServiceUrl(e.target.value)} />
        </Field>
        <ServiceOffer answer={offer}>
          {offer?.info && (
            <>
              <span>
                {credits(offer.info.creditsPerOperation)} a file, {credits(offer.info.creditsPerCachedOperation)} for one read before · files up to{" "}
                {formatBytes(offer.info.maxFileBytes)} · reads {readable.length} kinds of file:
              </span>
              <span className="license-formats">
                {readable.map((f) => (
                  <span key={f.key} className="license-format" title={`${f.name} (${f.group})`}>
                    {f.key}
                  </span>
                ))}
              </span>
            </>
          )}
        </ServiceOffer>
      </div>
      <div className="license-filetotext-fields">
        <FilePick label="File" hint="Judged by what it holds, not by its name." files={file} disabled={busy} onChange={setFile} />
        <Field label="Languages" hint="Codes such as nb, en, most likely first. They help OCR." locked={false}>
          <input className="text-input" value={languages} placeholder="nb, en" spellCheck={false} disabled={busy} onChange={(e) => setLanguages(e.target.value)} />
        </Field>
        <div className="license-save">
          <button className="action-button primary" onClick={run} disabled={busy || file.length === 0}>
            <IconFileText size={15} stroke={1.8} />
            {busy ? "Reading…" : "Read"}
          </button>
          <label className="license-toggle" title="Read the file again, and pay for it again, rather than take the text kept from before">
            <input type="checkbox" checked={fresh} disabled={busy} onChange={(e) => setFresh(e.target.checked)} />
            <span>Read anew</span>
          </label>
        </div>
      </div>
      {error && <div className="license-error">{error}</div>}
      {result && <FileTextAnswer result={result} />}
    </TestPanel>
  );
}

function FileTextAnswer({ result }: { result: FileToTextResult }) {
  const pages = useMemo(() => result.text.slice(0, shownTextChars).split("\f"), [result]);
  const download = useObjectUrl(useMemo(() => new Blob([result.text], { type: "text/plain;charset=utf-8" }), [result]));
  const facts = [
    result.pages != null ? `${result.pages} ${result.pages === 1 ? "page" : "pages"}` : null,
    `${result.characters.toLocaleString()} characters`,
    result.ocr ? "partly read by OCR" : null,
    result.truncated ? "cut: the file holds more" : null,
  ].filter(Boolean);
  const about = [result.title && `“${result.title}”`, result.author && "by " + result.author, result.language && "in " + result.language].filter(Boolean);
  const name = (result.fileName || "file").replace(/\.[^.]*$/, "");
  return (
    <div className="license-filetotext-result">
      <div className="license-status license-status-ok">
        <span className="license-status-icon">
          <IconCircleCheck size={20} stroke={1.8} />
        </span>
        <div className="license-status-text">
          <strong>
            Read as {result.format}
            {result.fileName ? " · " + result.fileName : ""}
          </strong>
          <span className="license-muted">
            {facts.join(" · ")} · {credits(result.credits)} · {result.creditsLeft.toLocaleString()} left{result.cached ? " · the text kept from before" : ""}
          </span>
          {about.length > 0 && <span className="license-muted">{about.join(" ")}</span>}
        </div>
        <div className="license-actions">
          <CopyText text={result.text} title="Copy the text" small />
          {download && (
            <a className="action-button" href={download} download={name + ".txt"}>
              <IconDownload size={14} stroke={1.8} />
              Download
            </a>
          )}
        </div>
      </div>
      <div className="license-text-result">
        {result.text.length === 0 && <p className="license-muted">The file holds no text.</p>}
        {pages.map((page, i) => (
          <div key={i} className="license-text-page">
            {pages.length > 1 && <span className="license-text-page-no">Page {i + 1}</span>}
            <pre>{page}</pre>
          </div>
        ))}
        {result.text.length > shownTextChars && (
          <p className="license-muted">The first {shownTextChars.toLocaleString()} characters of {result.text.length.toLocaleString()} are shown; the download has them all.</p>
        )}
      </div>
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
