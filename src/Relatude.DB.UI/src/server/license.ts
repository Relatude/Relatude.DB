import { send } from "./channel";
import { saveServerSettings } from "./settings";

/**
 * Where this installation stands with Relatude.License.
 *
 * "unreachable" is kept apart from "invalid" deliberately: a license server that did not answer
 * says nothing about the license, and a page that called a customer's license bad because their
 * network was down would be its own kind of wrong.
 */
export type LicenseState = "missing" | "malformed" | "unreachable" | "invalid" | "valid";

export interface LicenseLimit {
  name: string;
  maxValue: number;
  unlimited: boolean;
}

/** Credits allowed and spent in one clock window. A limit of zero means there is no rate limit. */
export interface RateWindow {
  limit: number;
  used: number;
  left: number | null;
}

export interface LicenseAccount {
  name: string;
  monthlyLimit: number;
  usedThisMonth: number;
  balanceLeft: number;
  minute: RateWindow;
  hour: RateWindow;
  day: RateWindow;
}

export interface LicenseInfo {
  id: string;
  name: string;
  disabled: boolean;
  expired: boolean;
  expiresUtc: string | null;
  features: string[];
  limits: LicenseLimit[];
  accounts: LicenseAccount[];
  messageToAllEditors: string;
  messageToAllVisitors: string;
  stopEdit: boolean;
  stopVisit: boolean;
  /** neither disabled nor expired */
  active: boolean;
}

export interface LicenseStatus {
  state: LicenseState;
  /** why, for every state but "valid" */
  reason: string | null;
  /** always sent, since the portal links are built from it, but only shown when showLicenseServer is */
  servicesServerUrl: string;
  /** only a debug build of the server shows and edits the license server address */
  showLicenseServer: boolean;
  hasLicenseKey: boolean;
  hasApiKey: boolean;
  /** the license key itself, which is not a secret; the API key is never sent back */
  licenseKey: string | null;
  signInEnabled: boolean;
  lastContactUtc: string | null;
  license: LicenseInfo | null;
  /**
   * A pairing the server is still waiting on. It lives on the server rather than in this page, so
   * going off to the portal in another tab and coming back - or simply reloading - picks up where it
   * left off instead of starting a second one.
   */
  pairing: PairingHandle | null;
  /** settings paths decided by configuration, which cannot be edited from this page */
  locked: string[];
}

export function fetchLicenseStatus(): Promise<LicenseStatus> {
  return send<LicenseStatus>("license-status");
}

/**
 * The license settings are ordinary server settings, so they are saved through the settings command
 * rather than a second path of their own: that is what keeps a configuration override, a secret that
 * is written but never read back, and the settings file itself behaving the same either way.
 */
export function saveLicenseSettings(values: Record<string, unknown>): Promise<unknown> {
  return saveServerSettings(values);
}

/**
 * Getting a license without copying a key by hand.
 *
 * The server asks the license server for a pairing and keeps the secret that collects it; this page
 * gets the url to open and then asks every couple of seconds until somebody has answered it there.
 * The keys come back here and are saved the way every other setting is saved.
 */
export interface PairingHandle {
  pairingId: string;
  claimUrl: string;
  expiresUtc: string;
  /** how long the license server asks us to wait between polls */
  pollSeconds: number;
}

export interface PairingAnswer {
  status: "pending" | "ready" | "expired" | "unreachable";
  licenseKey: string | null;
  apiKey: string | null;
  reason: string | null;
}

export function startPairing(): Promise<PairingHandle> {
  return send<PairingHandle>("license-pair-start");
}

export function pollPairing(pairingId: string): Promise<PairingAnswer> {
  return send<PairingAnswer>("license-pair-poll", { pairingId });
}

export function cancelPairing(pairingId: string): Promise<unknown> {
  return send("license-pair-cancel", { pairingId });
}

/** What the SMS service said about a message it accepted. */
export interface SmsReceipt {
  messageId: string;
  /** the number actually used, after the service's default country code */
  to: string;
  parts: number;
  credits: number;
  creditsLeft: number;
  reference: string | null;
}

/** Whether the license carries the SMS feature, which is what the Relatude SMS service checks. */
export function licenseCarriesSms(status: LicenseStatus): boolean {
  return status.state === "valid" && !!status.license?.active && status.license.features.some((f) => f.trim().toLowerCase() === "sms");
}

/**
 * Sends one real message through the hosted Relatude SMS service with this installation's API key,
 * charged to the license. No database's SMS settings are involved.
 */
export function sendTestSms(values: { from: string; to: string; message: string }): Promise<SmsReceipt> {
  return send<SmsReceipt>("license-sms-test", values);
}

/** Whether the rail should draw attention to the License entry, and in how many words. */
export function licenseAttention(status: LicenseStatus | null): { text: string; danger: boolean } | null {
  if (!status) return null;
  if (status.state === "missing") return { text: "none", danger: false };
  if (status.state === "malformed" || status.state === "invalid") return { text: "invalid", danger: true };
  // a license the server answered for, but which it will not honour
  if (status.state === "valid" && status.license && !status.license.active) {
    return { text: status.license.expired ? "expired" : "disabled", danger: true };
  }
  return null;
}
