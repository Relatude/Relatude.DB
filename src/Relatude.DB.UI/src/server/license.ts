import { adminBase } from "./base";
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

/**
 * A feature the license carries. The key is what code matches on, trimmed and ignoring case; the
 * name is only for people to read.
 */
export interface LicenseFeature {
  key: string;
  name: string;
}

/** A numeric cap on the license: matched on its key, with the name only for display. */
export interface LicenseLimit {
  key: string;
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

/** A monthly credit account and its rate limits: matched on its key ("sms", say), with the name only for display. */
export interface LicenseAccount {
  key: string;
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
  features: LicenseFeature[];
  limits: LicenseLimit[];
  accounts: LicenseAccount[];
  messageToAllEditors: string;
  messageToAllVisitors: string;
  stopEdit: boolean;
  stopVisit: boolean;
  /**
   * The senders Relatude has approved for the license's text messages, as the license server writes
   * them: names of up to eleven letters and digits, and phone numbers with their country code. A
   * message goes as one of these or as the SMS service's own sender. Missing from an older license
   * server, which means none.
   */
  smsSenders?: string[] | null;
  /** what the API key the license was asked for with is called; null for a key without a name, missing from an older license server */
  apiKeyName?: string | null;
  /** neither disabled nor expired */
  active: boolean;
}

/**
 * The key Relatude Services knows this installation by - the heartbeat, the sign-in and a pairing all
 * send it - and what it is made of: "server id:host id:data id".
 */
export interface InstallationInfo {
  key: string;
  /** the Id in relatude.db.json, which travels with the file */
  serverId: string;
  /** the fingerprint of the host */
  hostId: string;
  /** what the host id is calculated from: the Azure App Service app and slot, or "this machine" */
  host: string;
  /** the random id kept with the default database, which tells apart applications that share the other two; null when it could not be kept */
  dataId: string | null;
  /** where the data id is kept */
  dataIdPlace: string;
  /** why there is no data id */
  dataIdProblem: string | null;
}

export interface LicenseStatus {
  state: LicenseState;
  /** why, for every state but "valid" */
  reason: string | null;
  /** always sent, since the portal links are built from it, but only shown when showLicenseServer is */
  servicesServerUrl: string;
  /** only a debug build of the server shows and edits the license server address */
  showLicenseServer: boolean;
  /** whether the settings hold a license key; it need not be set, since it is found from the API key */
  hasLicenseKey: boolean;
  hasApiKey: boolean;
  /** the first five characters of the saved API key, to tell which one it is; the rest is never sent back */
  apiKeyStart: string | null;
  /**
   * The license key this installation goes by: the license server's answer for the API key when it
   * gave one, otherwise what the settings say. Not shown on this page - the settings page has it -
   * but it names the license's own page in the portal.
   */
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
  /** What Relatude Services knows this installation by, so it can be found there. Missing from an older server. */
  installation?: InstallationInfo | null;
  /**
   * The Relatude AI service address a database here is set up to use, which the AI test starts
   * from; null when none names one, and the hosted service is meant.
   */
  aiServiceUrl: string | null;
  /** The Relatude Imaging service address a database here is set up to use, which the imaging test starts from; null for the hosted service. */
  imagingServiceUrl?: string | null;
  /** The Relatude FileToText service address a database here is set up to use, which the file-to-text test starts from; null for the hosted service. */
  fileToTextServiceUrl?: string | null;
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
 *
 * A value the server turns down comes back in the result rather than as an error, so it is made one
 * here: this page saves a handful of values at once and has nowhere else to say that one did not take.
 */
export async function saveLicenseSettings(values: Record<string, unknown>): Promise<void> {
  const result = await saveServerSettings(values);
  if (result.rejected.length > 0) throw new Error(result.rejected.map((r) => r.reason).join(" "));
}

/** The license a pasted API key belongs to, as the license server says. */
export interface ApiKeyLookup {
  /** the key as the settings hold it: trimmed, lower case, with the dashes */
  apiKey: string;
  licenseKey: string;
  name: string;
}

/**
 * Asks the license server about an API key before it is saved. It is the only key anybody pastes:
 * the answer names the license, and so gives the license key to save beside it. A key the server
 * does not take, or a server that cannot be reached, is an error with the reason, and nothing is saved.
 */
export function lookUpApiKey(apiKey: string): Promise<ApiKeyLookup> {
  return send<ApiKeyLookup>("license-look-up", { apiKey });
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

/** Whether a key from the license server is the one code refers to: trimmed and ignoring case, as the server matches it. */
function isKey(key: string | null | undefined, expected: string): boolean {
  return (key ?? "").trim().toLowerCase() === expected;
}

/**
 * The credit accounts the Relatude services charge their calls to. The account is what licenses a
 * service's calls - no feature is needed - and each key is the one the service is set up with.
 */
export const serviceAccounts = {
  sms: "sms",
  aiEmbeddings: "ai_embeddings",
  aiCompletion: "ai_completion",
  imaging: "ai_image",
  fileToText: "filetotext",
} as const;

/** The license's credit account with this key, active or not. Null when the license has no such account, or there is no license. */
export function licenseAccount(status: LicenseStatus, key: string): LicenseAccount | null {
  return status.license?.accounts.find((a) => isKey(a.key, key)) ?? null;
}

/** Whether a call charged to this credit account can be paid for: the license is valid and active, and has the account. */
export function licenseCarriesAccount(status: LicenseStatus, key: string): boolean {
  return status.state === "valid" && !!status.license?.active && licenseAccount(status, key) !== null;
}

/** Whether the license has the "sms" credit account, which the Relatude SMS service charges every message to; no feature is needed. */
export function licenseCarriesSms(status: LicenseStatus): boolean {
  return licenseCarriesAccount(status, serviceAccounts.sms);
}

/**
 * The senders a message may go as besides the SMS service's own: those Relatude has approved for the
 * license, while it is valid and active. Empty otherwise, and then only the service's own is left.
 */
export function licenseSmsSenders(status: LicenseStatus): string[] {
  return status.state === "valid" && status.license?.active ? (status.license.smsSenders ?? []) : [];
}

/**
 * Whether the license has the "smsanysender" feature, which lets its messages name any sender with no
 * approved senders at all: the license server allows any sender a phone can show, so the approved
 * ones are beside the point and are not shown.
 */
export function licenseMayUseAnySmsSender(status: LicenseStatus): boolean {
  return status.state === "valid" && !!status.license?.features.some((f) => isKey(f.key, "smsanysender"));
}

/**
 * Sends one real message through the hosted Relatude SMS service with this installation's API key,
 * charged to the license. No database's SMS settings are involved. An empty sender is the service's
 * own; any other is checked with the license server first, and refused with its reason when it is
 * not approved for the license.
 */
export function sendTestSms(values: { from: string; to: string; message: string }): Promise<SmsReceipt> {
  return send<SmsReceipt>("license-sms-test", values);
}

/**
 * Whether the license has the credit account the Relatude AI service charges one kind of call to:
 * "ai_embeddings" for embeddings, "ai_completion" for completions. The account is what licenses the
 * call; no feature is needed, as with SMS.
 */
export function licenseCarriesAi(status: LicenseStatus, kind: "embeddings" | "completions"): boolean {
  return licenseCarriesAccount(status, kind === "embeddings" ? serviceAccounts.aiEmbeddings : serviceAccounts.aiCompletion);
}

/** One text embedded by the AI service: the model, the vector's length, its first values and length (norm), and what it cost. */
export interface AiEmbeddingResult {
  model: string;
  dimensions: number;
  preview: number[];
  norm: number;
  credits: number;
  creditsLeft: number;
}

/** One prompt answered by the AI service, and what it cost for the prompt and the answer together. */
export interface AiCompletionResult {
  model: string;
  text: string;
  credits: number;
  creditsLeft: number;
}

/**
 * Embeds one text through the Relatude AI service with this installation's API key, charged to the
 * license. An empty model is the service's default, an empty url the hosted service.
 */
export function testAiEmbedding(values: { serviceUrl: string; model: string; text: string }): Promise<AiEmbeddingResult> {
  return send<AiEmbeddingResult>("license-ai-embed-test", values);
}

/** Answers one prompt through the Relatude AI service, charged to the license like the embedding test. */
export function testAiCompletion(values: { serviceUrl: string; model: string; text: string }): Promise<AiCompletionResult> {
  return send<AiCompletionResult>("license-ai-complete-test", values);
}

/** Whether the license has the "ai_image" credit account the Relatude Imaging service charges every operation to; no feature is needed. */
export function licenseCarriesImaging(status: LicenseStatus): boolean {
  return licenseCarriesAccount(status, serviceAccounts.imaging);
}

/** Whether the license has the "filetotext" credit account the Relatude FileToText service charges every file to; no feature is needed. */
export function licenseCarriesFileToText(status: LicenseStatus): boolean {
  return licenseCarriesAccount(status, serviceAccounts.fileToText);
}

/** One operation of the Imaging service: its key, which is also its route, a name for people, and whether it can be called there. */
export interface ImagingOperationInfo {
  key: string;
  name: string;
  available: boolean;
}

/** What the Imaging service offers and what it costs. Asking costs nothing and needs no license. */
export interface ImagingOperations {
  operations: ImagingOperationInfo[];
  creditsPerOperation: number;
  creditsPerCachedOperation: number;
  partBytes: number;
  maxFileBytes: number;
}

/** The Imaging service's operations, asked of the address given; empty is the hosted service. */
export function fetchImagingOperations(serviceUrl: string): Promise<ImagingOperations> {
  return send<ImagingOperations>("license-imaging-operations", { serviceUrl });
}

/** An image an operation made: the PNG, its size, its SHA-256 at the service, and what it cost. For rotate-if-needed, how far it was turned clockwise. */
export interface ImagingImageResult {
  kind: "image";
  png: Blob;
  width: number;
  height: number;
  sha256: string;
  credits: number;
  creditsLeft: number;
  cached: boolean;
  rotation?: number;
}

/** ask-about-image's answer, in words, in the language the question was asked in. */
export interface ImagingAnswerResult {
  kind: "answer";
  answer: string;
  credits: number;
  creditsLeft: number;
  cached: boolean;
}

/** ask-about-image-bool's answer: true for yes, and how sure it is from 0 (a guess) to 100 (plainly shown). */
export interface ImagingBoolResult {
  kind: "bool";
  answer: boolean;
  certainty: number;
  credits: number;
  creditsLeft: number;
  cached: boolean;
}

/** rotate-if-needed leaving the image as it is: upright already, or with no telling which way is up. */
export interface ImagingLeftAsIsResult {
  kind: "left-as-is";
  rotation: 0;
  credits: number;
  creditsLeft: number;
  cached: boolean;
}

/** Something in an image and the box around it, in pixels of the image as shown. */
export interface ImagingObject {
  type: string;
  name: string | null;
  confidence: number;
  x: number;
  y: number;
  width: number;
  height: number;
}

/** What image-to-meta says an image shows. Focus and objects are optional: not every provider finds them. */
export interface ImagingMetaResult {
  kind: "meta";
  title: string;
  description: string;
  keywords: string[];
  language: string | null;
  focus: { x: number; y: number } | null;
  objects: ImagingObject[];
  credits: number;
  creditsLeft: number;
  cached: boolean;
}

/** A form posted to one of the two test routes, the answer's error turned into an exception in the service's own words. */
async function postTest(route: string, form: FormData, signal?: AbortSignal): Promise<Response> {
  const response = await fetch(`${adminBase}/ui/${route}`, { method: "POST", body: form, signal });
  if (!response.ok) {
    let message = `The test failed (HTTP ${response.status}).`;
    try {
      const body = await response.json();
      if (typeof body?.error === "string") message = body.error;
    } catch {
      // not json, keep the default message
    }
    throw new Error(message);
  }
  return response;
}

function intHeader(response: Response, name: string): number {
  const value = Number(response.headers.get(name));
  return Number.isFinite(value) ? value : 0;
}

/**
 * Runs one operation of the Imaging service with this installation's API key, charged to the license.
 * The form names the operation and carries the service's own fields, the images as files. An image
 * comes back as the PNG with what it cost in headers; image-to-meta and the questions as JSON, and
 * rotate-if-needed leaving the image as it is as JSON with a rotation of 0.
 */
export async function runImagingTest(
  form: FormData,
  signal?: AbortSignal,
): Promise<ImagingImageResult | ImagingMetaResult | ImagingLeftAsIsResult | ImagingAnswerResult | ImagingBoolResult> {
  const response = await postTest("imaging-test", form, signal);
  if ((response.headers.get("content-type") ?? "").startsWith("image/")) {
    const rotation = response.headers.get("X-Rotation");
    return {
      kind: "image",
      png: await response.blob(),
      width: intHeader(response, "X-Width"),
      height: intHeader(response, "X-Height"),
      sha256: response.headers.get("X-Sha256") ?? "",
      credits: intHeader(response, "X-Credits"),
      creditsLeft: intHeader(response, "X-Credits-Left"),
      cached: response.headers.get("X-Cache") === "hit",
      ...(rotation === null ? {} : { rotation: Number(rotation) }),
    };
  }
  const answer = await response.json();
  if (typeof answer?.rotation === "number") return { ...(answer as Omit<ImagingLeftAsIsResult, "kind">), rotation: 0, kind: "left-as-is" };
  if (typeof answer?.certainty === "number") return { ...(answer as Omit<ImagingBoolResult, "kind">), kind: "bool" };
  if (typeof answer?.answer === "string") return { ...(answer as Omit<ImagingAnswerResult, "kind">), kind: "answer" };
  const meta = answer as Omit<ImagingMetaResult, "kind">;
  return { ...meta, keywords: meta.keywords ?? [], objects: meta.objects ?? [], kind: "meta" };
}

/** One kind of file the FileToText service knows, and whether it reads it there. */
export interface FileToTextFormat {
  key: string;
  name: string;
  group: string;
  mediaType: string;
  extensions: string[];
  available: boolean;
}

/** What the FileToText service reads and what a file costs. Asking costs nothing and needs no license. */
export interface FileToTextFormats {
  formats: FileToTextFormat[];
  creditsPerOperation: number;
  creditsPerCachedOperation: number;
  partBytes: number;
  maxFileBytes: number;
}

/** The FileToText service's formats, asked of the address given; empty is the hosted service. */
export function fetchFileToTextFormats(serviceUrl: string): Promise<FileToTextFormats> {
  return send<FileToTextFormats>("license-filetotext-formats", { serviceUrl });
}

/** The text of a file as the FileToText service read it: pages separated by form feeds, and what the file says of itself. */
export interface FileToTextResult {
  text: string;
  format: string;
  fileName: string | null;
  characters: number;
  pages: number | null;
  truncated: boolean;
  ocr: boolean;
  title: string | null;
  author: string | null;
  language: string | null;
  credits: number;
  creditsLeft: number;
  cached: boolean;
}

/** Reads one file through the FileToText service with this installation's API key, charged to the license. */
export async function runFileToTextTest(form: FormData, signal?: AbortSignal): Promise<FileToTextResult> {
  return (await (await postTest("filetotext-test", form, signal)).json()) as FileToTextResult;
}

/** Whether the rail should draw attention to the Relatude Services entry, and in how many words. */
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
