import { useEffect, useMemo, useRef, useState, type ComponentType, type ReactNode } from "react";
import {
  IconArrowBackUp,
  IconBrush,
  IconCircleCheck,
  IconDownload,
  IconEraser,
  IconExternalLink,
  IconFileText,
  IconInfoCircle,
  IconLanguage,
  IconMessage,
  IconPhoto,
  IconRefresh,
  IconSearch,
  IconSend,
  IconSparkles,
  IconTrash,
  IconUpload,
  IconWand,
  IconX,
} from "@tabler/icons-react";
import { fetchAiModels } from "../server/settings";
import { Combo, type PickerLoader } from "./Combo";
import {
  fetchFileToTextFormats,
  fetchImagingOperations,
  fetchTranslationLanguages,
  licenseAccount,
  licenseCarriesAccount,
  licenseCarriesAi,
  licenseCarriesFileToText,
  licenseCarriesImaging,
  licenseCarriesSms,
  licenseCarriesTranslation,
  licenseMayUseAnySmsSender,
  licenseSmsSenders,
  runDetectionTest,
  runFileToTextTest,
  runImagingTest,
  runTranslationTest,
  sendTestSms,
  serviceAccounts,
  testAiCompletion,
  testAiEmbedding,
  type AiCompletionResult,
  type AiEmbeddingResult,
  type DetectionTestResult,
  type FileToTextResult,
  type TranslationLanguage,
  type TranslationTestResult,
  type TranslationTestText,
  type ImagingAnswerResult,
  type ImagingBoolResult,
  type ImagingImageResult,
  type ImagingLeftAsIsResult,
  type ImagingMetaResult,
  type LicenseStatus,
  type SmsReceipt,
} from "../server/license";
import { formatBytes } from "../format";
import { Loading } from "./Loading";
import { CopyText } from "./CopyText";
import { ImageViewer, type ImageInset } from "./ImageViewer";
import { LogoMarkIcon } from "./Logo";
import { Account, Field, headline, portalLinks, useLicenseStatus } from "./LicenseSection";

/**
 * The views of the Test services module, one per Relatude service. Each is a section id of its own
 * (navigation.ts, under "services-tests"), so the global search finds "Imaging test" and opens the
 * page on it, and the switch at the top is nothing more than those ids - picking one there is the
 * same operation as picking it in the rail, as in Storage.
 */
export type ServiceTestView = "services-sms" | "services-ai" | "services-imaging" | "services-filetotext" | "services-translation";

interface ServiceTest {
  id: ServiceTestView;
  /** on the switch */
  label: string;
  /** over the view, beside its icon */
  title: string;
  /** what the view is about, after the title */
  sub: string;
  icon: ComponentType<{ size?: number; stroke?: number }>;
  /** the credit accounts the view's calls are charged to, and what each pays for */
  accounts: { key: string; pays: string }[];
}

const serviceTests: ServiceTest[] = [
  {
    id: "services-sms",
    label: "SMS",
    title: "Test SMS",
    sub: "a real message through the Relatude SMS service, charged to the license",
    icon: IconMessage,
    accounts: [{ key: serviceAccounts.sms, pays: "messages" }],
  },
  {
    id: "services-ai",
    label: "AI",
    title: "Test AI",
    sub: "real embeddings and completions from the Relatude AI service, charged to the license",
    icon: IconSparkles,
    accounts: [
      { key: serviceAccounts.aiEmbeddings, pays: "embeddings" },
      { key: serviceAccounts.aiCompletion, pays: "completions" },
    ],
  },
  {
    id: "services-imaging",
    label: "Imaging",
    title: "Test Imaging",
    sub: "real calls to the Relatude Imaging service, charged to the license",
    icon: IconPhoto,
    accounts: [{ key: serviceAccounts.imaging, pays: "operations" }],
  },
  {
    id: "services-filetotext",
    label: "File to text",
    title: "Test file to text",
    sub: "real files read by the Relatude FileToText service, charged to the license",
    icon: IconFileText,
    accounts: [{ key: serviceAccounts.fileToText, pays: "files" }],
  },
  {
    id: "services-translation",
    label: "Translation",
    title: "Test translation",
    sub: "real texts translated by the Relatude Translation service, charged to the license",
    icon: IconLanguage,
    accounts: [{ key: serviceAccounts.translation, pays: "translations" }],
  },
];

export const isServiceTestView = (id: string): id is ServiceTestView => serviceTests.some((t) => t.id === id);

/**
 * Test services: a real call to each Relatude service, so whoever set up the license can see it work
 * before code depends on it - one view per service, under a switch at the top.
 *
 * Every test is shown whatever the license carries. One the license cannot pay for is shown as it
 * is but faded and closed to use, with why on its first line: the service would refuse the call, so
 * the test does not make it. That first line is the same panel in every test (ServiceBar): what the
 * calls are charged to, and what the service offers. Every test calls the service a database here is
 * set up with, or the hosted one.
 *
 * A view stays mounted once it has been opened, and is only hidden while another is shown: what was
 * typed, a receipt, an image answer that was paid for, are all still there on coming back to it.
 */
export function ServiceTestsSection({
  view,
  onSelectView,
  onAccount,
  onChanged,
}: {
  view: ServiceTestView;
  onSelectView: (view: ServiceTestView) => void;
  /** to the account page, where a license is put right */
  onAccount: () => void;
  onChanged?: (status: LicenseStatus) => void;
}) {
  const { status, error, busy, load } = useLicenseStatus(onChanged);
  const [opened, setOpened] = useState<ServiceTestView[]>([view]);
  useEffect(() => setOpened((o) => (o.includes(view) ? o : [...o, view])), [view]);
  // the view asked for is shown on this very render, not one later
  const mounted = opened.includes(view) ? opened : [...opened, view];

  function body(test: ServiceTest, status: LicenseStatus) {
    const credit = <ServiceCredit status={status} accounts={test.accounts} onAccount={onAccount} />;
    switch (test.id) {
      case "services-sms":
        return <SmsTest credit={credit} allowed={licenseCarriesSms(status)} senders={licenseSmsSenders(status)} anySender={licenseMayUseAnySmsSender(status)} />;
      case "services-ai":
        return (
          <AiTests credit={credit} configuredUrl={status.aiServiceUrl ?? ""} embeddings={licenseCarriesAi(status, "embeddings")} completions={licenseCarriesAi(status, "completions")} />
        );
      case "services-imaging":
        return <ImagingTest credit={credit} configuredUrl={status.imagingServiceUrl ?? ""} allowed={licenseCarriesImaging(status)} />;
      case "services-filetotext":
        return <FileToTextTest credit={credit} configuredUrl={status.fileToTextServiceUrl ?? ""} allowed={licenseCarriesFileToText(status)} />;
      case "services-translation":
        return <TranslationTest credit={credit} configuredUrl={status.translationServiceUrl ?? ""} allowed={licenseCarriesTranslation(status)} />;
    }
  }

  return (
    <div className="license-page service-tests">
      <div className="module-switch" role="tablist">
        {serviceTests.map((test) => {
          // a service the license cannot pay for is still there to look at, only quieter
          const unpaid = status !== null && !test.accounts.some((a) => licenseCarriesAccount(status, a.key));
          return (
            <button
              key={test.id}
              role="tab"
              aria-selected={view === test.id}
              className={(view === test.id ? "active" : "") + (unpaid ? " unpaid" : "")}
              title={unpaid ? test.title + " · the license cannot pay for these calls" : test.title}
              onClick={() => onSelectView(test.id)}
            >
              <test.icon size={15} stroke={1.8} />
              {test.label}
            </button>
          );
        })}
      </div>
      {!status ? (
        error ? (
          <div className="placeholder">{error}</div>
        ) : (
          <Loading label="Checking the license…" />
        )
      ) : (
        serviceTests
          .filter((test) => mounted.includes(test.id))
          .map((test) => (
            <div key={test.id} className="license-view" role="tabpanel" hidden={test.id !== view}>
              {/* Storage's group heading, so the modules read alike: an icon in this module's
                  colour, the name beside it, what it is about, and the refresh at the end of the line */}
              <div className="storage-group-head">
                <test.icon size={18} stroke={1.7} />
                <h2>{test.title}</h2>
                <span className="muted">{test.sub}</span>
                <button className="icon-button storage-refresh" onClick={() => void load()} disabled={busy} title="Ask the license server again">
                  <IconRefresh size={14} stroke={1.8} />
                </button>
              </div>
              {/* the last answer stays on screen when asking again fails, with why under the heading */}
              {error && <div className="license-error">{error}</div>}
              {body(test, status)}
            </div>
          ))
      )}
    </div>
  );
}

/**
 * The first panel of every test, the same in each: what its calls are charged to and what the
 * service offers. The rows are a label and its value, the labels in one column, like a small table.
 */
function ServiceBar({ credit, children }: { credit: ReactNode; children?: ReactNode }) {
  return (
    <section className="panel license-service-bar">
      {credit}
      {children}
    </section>
  );
}

/**
 * The license's credit account each call of a test is charged to, with what is left of the month -
 * or why there is none, in which case the service would refuse the call and the test is closed. A
 * license that is not there, not valid or not active pays for nothing, and the account page is where
 * that is put right; an account the license lacks is added on the license's page in the portal.
 */
function ServiceCredit({ status, accounts, onAccount }: { status: LicenseStatus; accounts: { key: string; pays: string }[]; onAccount: () => void }) {
  const license = status.state === "valid" ? status.license : null;
  if (!license || !license.active) {
    const why = !license
      ? status.state === "missing"
        ? "This installation has no license, so the services refuse calls from it."
        : headline(status) + ", so the services refuse calls from this installation."
      : `The license is ${license.expired ? "expired" : "disabled"}, so the services refuse its calls.`;
    return (
      <div className="license-status license-status-info license-service-wide">
        <span className="license-status-icon">
          <IconInfoCircle size={20} stroke={1.8} />
        </span>
        <div className="license-status-text">
          <strong>{why}</strong>
          <span className="license-muted">A free license lets this installation use them. The tests are shown, but closed until there is one.</span>
        </div>
        <div className="license-actions">
          <button className="action-button" onClick={onAccount}>
            <LogoMarkIcon size={14} stroke={1.8} />
            Account
          </button>
        </div>
      </div>
    );
  }
  const { licensePage } = portalLinks(status);
  return (
    <>
      <span className="license-service-label">Charged to</span>
      <div className="license-credit-accounts">
        {accounts.map(({ key, pays }) => {
          const account = licenseAccount(status, key);
          return account ? (
            <Account key={key} account={account} />
          ) : (
            <div key={key} className="license-credit-missing">
              <span>
                <IconInfoCircle size={14} stroke={1.8} /> No “{key}” credit account
              </span>
              <span className="license-muted">
                The license cannot pay for {pays}, so that test is closed.{" "}
                <a href={licensePage} target="_blank" rel="noreferrer">
                  Add it in the portal
                  <IconExternalLink size={12} stroke={1.8} />
                </a>
              </span>
            </div>
          );
        })}
      </div>
    </>
  );
}

/** What the service says it offers, as a row of the service bar: what a call costs, and how large a file may be. */
function ServiceOfferRow({ children }: { children: ReactNode }) {
  return (
    <>
      <span className="license-service-label">Service</span>
      <div className="license-service-offer">{children}</div>
    </>
  );
}

/** A test's own panels: shown whatever the license carries, but faded and closed to use when the license cannot pay for the calls. */
function TestBody({ allowed, className, children }: { allowed: boolean; className?: string; children: ReactNode }) {
  return (
    <div className={"license-test-body" + (className ? " " + className : "") + (allowed ? "" : " off")} inert={!allowed} aria-disabled={!allowed || undefined}>
      {children}
    </div>
  );
}

/**
 * A real message through the Relatude SMS service, so whoever set up the license can see it arrive
 * before any code depends on it. Only sent when the license has the "sms" credit account; it is
 * charged like any other message, which is why the receipt says what it cost and what is left. The
 * sender is the service's own or one Relatude has approved for the license, and the server checks it
 * with the license server again before anything is sent.
 */
function SmsTest({ credit, allowed, senders, anySender }: { credit: ReactNode; allowed: boolean; senders: string[]; anySender: boolean }) {
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
    <>
      <ServiceBar credit={credit} />
      <TestBody allowed={allowed}>
        <section className="panel license-sms">
          <h3>Message</h3>
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
              <button className="action-button primary" onClick={sendIt} disabled={sending || !allowed || !to.trim() || !message.trim()}>
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
        </section>
      </TestBody>
    </>
  );
}

/**
 * Real calls to the Relatude AI service, an embedding and a completion side by side, so whoever set
 * up the license can see both work before a database depends on them. Each is closed unless the
 * license has the credit account that kind of call is charged to, "ai_embeddings" or "ai_completion";
 * every call is charged like any other, which is why each result says what it cost and what is left.
 * The model lists are asked of the service each time one is opened - that costs nothing - and empty
 * is the service's default.
 */
function AiTests({ credit, configuredUrl, embeddings, completions }: { credit: ReactNode; configuredUrl: string; embeddings: boolean; completions: boolean }) {
  const url = configuredUrl.trim();
  const models = (kind: "embeddings" | "completions"): PickerLoader => () =>
    fetchAiModels("RelatudeServices", url).then((m) => {
      if (m.error) throw new Error(m.error);
      return m[kind];
    });
  return (
    <>
      <ServiceBar credit={credit} />
      <div className="license-ai-tests">
        <TestBody allowed={embeddings}>
          <AiEmbeddingTest serviceUrl={url} loadModels={models("embeddings")} allowed={embeddings} />
        </TestBody>
        <TestBody allowed={completions}>
          <AiCompletionTest serviceUrl={url} loadModels={models("completions")} allowed={completions} />
        </TestBody>
      </div>
    </>
  );
}

function AiEmbeddingTest({ serviceUrl, loadModels, allowed }: { serviceUrl: string; loadModels: PickerLoader; allowed: boolean }) {
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
    <section className="panel license-ai-test">
      <h3>Embedding</h3>
      <Field label="Model" locked={false}>
        <Combo label="Embedding model" placeholder="the service's default" options={[]} load={loadModels} value={model} disabled={busy} onChange={(v) => setModel(String(v ?? ""))} />
      </Field>
      <Field label="Text" hint={text.length + " characters"} locked={false}>
        <textarea className="text-input license-sms-message" rows={3} value={text} onChange={(e) => setText(e.target.value)} />
      </Field>
      <div className="license-save">
        <button className="action-button primary" onClick={run} disabled={busy || !allowed || !text.trim()}>
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
    </section>
  );
}

function AiCompletionTest({ serviceUrl, loadModels, allowed }: { serviceUrl: string; loadModels: PickerLoader; allowed: boolean }) {
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
    <section className="panel license-ai-test">
      <h3>Completion</h3>
      <Field label="Model" locked={false}>
        <Combo label="Completion model" placeholder="the service's default" options={[]} load={loadModels} value={model} disabled={busy} onChange={(v) => setModel(String(v ?? ""))} />
      </Field>
      <Field label="Prompt" hint={prompt.length + " characters"} locked={false}>
        <textarea className="text-input license-sms-message" rows={3} value={prompt} onChange={(e) => setPrompt(e.target.value)} />
      </Field>
      <div className="license-save">
        <button className="action-button primary" onClick={run} disabled={busy || !allowed || !prompt.trim()}>
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
    </section>
  );
}

/**
 * What a service offers, asked once the test is mounted - which is when its view is first opened, so
 * when somebody looks. Asking costs nothing and needs no license. Null until there is an answer for
 * this very address.
 */
function useServiceInfo<T>(url: string, load: (url: string) => Promise<T>): { info: T | null; error: string | null } | null {
  const [state, setState] = useState<{ url: string; info: T | null; error: string | null } | null>(null);
  useEffect(() => {
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
  }, [url, load]);
  return state?.url === url ? state : null;
}

/**
 * An object url for a blob while it is shown, let go when the blob changes or the component goes.
 * Only ever the url of the blob asked for: on the render where the blob changes, before the new url
 * is made, it is null rather than the old one - which is let go by then, and whose blob may be gone
 * (an image taken out leaves no image[0] for the old url to be shown with).
 */
function useObjectUrl(blob: Blob | null): string | null {
  const [made, setMade] = useState<{ blob: Blob; url: string } | null>(null);
  useEffect(() => {
    if (!blob) {
      setMade(null);
      return;
    }
    const url = URL.createObjectURL(blob);
    setMade({ blob, url });
    return () => URL.revokeObjectURL(url);
  }, [blob]);
  return made && made.blob === blob ? made.url : null;
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
 * The mask painted onto the image itself rather than made elsewhere and chosen as a file, on the
 * test's stage, where the picture is as large as it can be shown. The paint lies on a canvas of the
 * image's own size, however small the picture shows, so what is handed on is the mask the service
 * asks for: as large as the image, white where it was painted and black where it was not - or null
 * while nothing is. A mask made elsewhere can be loaded and painted on from there, and Undo steps
 * back a stroke at a time. `lead` goes first on the bar of tools, as on the stage's other views.
 */
function MaskEditor({
  lead,
  image,
  mask,
  disabled,
  onChange,
}: {
  lead?: ReactNode;
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
    <div className="license-mask">
      <div className="license-mask-tools" role="toolbar" aria-label="Mask">
        {lead}
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
      {error ? (
        <span className="license-stage-caption license-offer-bad">{error}</span>
      ) : (
        <span className="license-stage-caption">
          {mask && size
            ? `Sent as a ${size.w} × ${size.h} mask: white where painted, black elsewhere.`
            : "Paint over the image where the change goes. What is not painted stays as it is."}
        </span>
      )}
      <div className="license-mask-view">
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
      </div>
    </div>
  );
}

/** What the service says about itself: what it offers, what a call costs, and how large a file may be. */
function ServiceOffer({ answer, children }: { answer: { error: string | null } | null; children?: ReactNode }) {
  if (!answer) return <p className="license-muted license-offer">Asking the service what it offers…</p>;
  if (answer.error) return <p className="license-offer license-offer-bad">The service did not answer: {answer.error}</p>;
  return <div className="license-muted license-offer">{children}</div>;
}

const marginSides = ["top", "right", "bottom", "left"] as const;
const imageTypes = "image/png,image/jpeg,image/webp";

/** The groups the Imaging test shows its operations in, in this order. */
type ImagingGroup = "Make" | "Edit" | "Reframe" | "Understand";
const imagingGroups: ImagingGroup[] = ["Make", "Edit", "Reframe", "Understand"];

/** The operations of the Imaging service as the test offers them: the group each is shown in, what it does, and the fields it takes, named as the service names them. */
interface ImagingOp {
  key: string;
  label: string;
  group: ImagingGroup;
  /** what the operation does, beside the operations */
  about: string;
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
    group: "Make",
    about: "Draws a new image from a description, in the look of any inspiration images.",
    image: false,
    extra: "inspiration",
    text: { field: "description", label: "Description", required: true, placeholder: "A lighthouse on a rocky coast at dusk, oil painting" },
    size: true,
    transparent: true,
  },
  {
    key: "manipulate-image",
    label: "Manipulate",
    group: "Edit",
    about: "Changes the image as the instruction says, or only where the mask is painted.",
    image: true,
    mask: "optional",
    extra: "references",
    text: { field: "instruction", label: "Instruction", required: true, placeholder: "Make it a winter scene" },
  },
  {
    key: "remove-object",
    label: "Remove object",
    group: "Edit",
    about: "Removes what the mask covers and fills in the space.",
    image: true,
    mask: "required",
  },
  {
    key: "remove-background",
    label: "Remove background",
    group: "Edit",
    about: "Keeps the subject and makes the background transparent.",
    image: true,
  },
  {
    key: "upscale",
    label: "Upscale",
    group: "Edit",
    about: "Makes the image 2 or 4 times larger, adding detail rather than blur.",
    image: true,
    factor: true,
  },
  {
    key: "expand-image",
    label: "Expand",
    group: "Reframe",
    about: "Grows the canvas by the margins and fills the new space.",
    image: true,
    margins: true,
    text: { field: "hint", label: "Hint", required: false, placeholder: "What the new space should show" },
  },
  {
    key: "shrink-image",
    label: "Shrink",
    group: "Reframe",
    about: "Shrinks the canvas by the margins, moving the subject rather than cutting it.",
    image: true,
    margins: true,
  },
  {
    key: "rotate-if-needed",
    label: "Rotate if needed",
    group: "Reframe",
    about: "Turns the image the right way up, if it is not already.",
    image: true,
  },
  {
    key: "image-to-meta",
    label: "Describe",
    group: "Understand",
    about: "A title, description and keywords, what is in the image and where, and its focus point.",
    image: true,
    language: true,
  },
  {
    key: "ask-about-image",
    label: "Ask",
    group: "Understand",
    about: "Answers a question about the image in words.",
    image: true,
    text: { field: "question", label: "Question", required: true, placeholder: "What is the person in the picture holding?" },
  },
  {
    key: "ask-about-image-bool",
    label: "Ask yes / no",
    group: "Understand",
    about: "Answers a question about the image with yes or no, and how sure it is.",
    image: true,
    text: { field: "question", label: "Question", required: true, placeholder: "Is there a dog in the picture?" },
  },
];

/** What the stage shows: the image as it is sent, the mask painted on it, or the answer. */
type ImagingStage = "image" | "mask" | "answer";

/** What an operation answered, with what was sent for it. */
interface ImagingResult {
  op: ImagingOp;
  answer: ImagingImageResult | ImagingMetaResult | ImagingLeftAsIsResult | ImagingAnswerResult | ImagingBoolResult;
  input: File | null;
  inset: ImageInset | null;
  question: string;
}

/**
 * Real calls to the Relatude Imaging service, one operation at a time, so whoever set up the license
 * can see each one work before code depends on it. Only made when the license has the "ai_image"
 * credit account every operation is charged to, which is why each answer says what it cost. The same
 * call made again is answered from what the service kept, at its price for that, unless "New answer"
 * asks for another. An image answer can be taken as the image of the next call - upscale a cutout,
 * say - which the service then knows by its name and does not need sent.
 *
 * Laid out as panels on a grid, so their edges line up: the operations across the top, with what
 * the chosen one does beside them; then a column of what is sent (Input) and what came back
 * (Answer), and beside it one stage, as large as the page allows, for whatever there is to look at -
 * the image chosen, the mask painted on it, and the answer laid over the image it was made from. The
 * two columns start alike, a heading row and a label row, so the stage and the first field's box
 * begin on the same line; and they end on the same line, the last panel on the left stretching to
 * the stage's foot.
 */
function ImagingTest({ credit, configuredUrl, allowed }: { credit: ReactNode; configuredUrl: string; allowed: boolean }) {
  const url = configuredUrl.trim();
  const offer = useServiceInfo(url, fetchImagingOperations);
  const [opKey, setOpKey] = useState("remove-background");
  const op = imagingOps.find((o) => o.key === opKey)!;
  const [image, setImage] = useState<File[]>([]);
  const imageUrl = useObjectUrl(image[0] ?? null);
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
  const [result, setResult] = useState<ImagingResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [stage, setStage] = useState<ImagingStage>("image");
  const answerUrl = useObjectUrl(result?.answer.kind === "image" ? result.answer.png : null);
  const inputUrl = useObjectUrl(result?.input ?? null);

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
    setStage("answer");
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

  function chooseImage(files: File[], forOp: ImagingOp = op) {
    setImage(files);
    setImageTurn((t) => t + 1);
    setMask(null);
    // an operation that takes a mask is painted for next; any other shows the image as it is sent
    if (files.length > 0) setStage(forOp.mask ? "mask" : "image");
  }

  function chooseOp(next: ImagingOp) {
    setOpKey(next.key);
    if (next.mask && image.length > 0) setStage("mask");
  }

  /** The answer becomes the image of the next call: the service holds it already, so it is named, not sent. */
  function takeAsImage(answer: ImagingImageResult) {
    const next = op.image ? op : imagingOps.find((o) => o.key === "upscale")!;
    chooseImage([new File([answer.png], (result?.op.key ?? "answer") + ".png", { type: "image/png" })], next);
    if (next !== op) chooseOp(next);
  }

  // what there is to look at, in the order it comes about; the stage shows the one picked, or the last there is
  const stages: { id: ImagingStage; label: string; title: string }[] = [];
  if (op.image && imageUrl) stages.push({ id: "image", label: "Image", title: "The image as it is sent" });
  if (op.mask && image.length > 0) stages.push({ id: "mask", label: "Mask", title: "Paint where the change goes" });
  if (result || busy) stages.push({ id: "answer", label: "Answer", title: busy ? "The call being made" : "What the service answered" });
  const shown = stages.some((s) => s.id === stage) ? stage : (stages[stages.length - 1]?.id ?? null);
  const lead =
    stages.length > 1 ? (
      <div className="module-switch compact license-stage-switch" role="tablist" aria-label="What the stage shows">
        {stages.map((s) => (
          <button key={s.id} role="tab" aria-selected={shown === s.id} className={shown === s.id ? "active" : ""} title={s.title} onClick={() => setStage(s.id)}>
            {s.label}
          </button>
        ))}
      </div>
    ) : (
      <span className="license-stage-name">{stages[0]?.label ?? "Answer"}</span>
    );
  const unoffered = available(op.key) === false;

  return (
    <>
      <ServiceBar credit={credit}>
        <ServiceOfferRow>
          <ServiceOffer answer={offer}>
            {offer?.info && (
              <>
                {credits(offer.info.creditsPerOperation)} a call, {credits(offer.info.creditsPerCachedOperation)} for an answer made before · images up to{" "}
                {formatBytes(offer.info.maxFileBytes)}
              </>
            )}
          </ServiceOffer>
        </ServiceOfferRow>
      </ServiceBar>
      <TestBody allowed={allowed} className="license-imaging">
        {/* No heading of its own: the groups' names say what the buttons are, and the panel is kept as
            low as it can be, since everything under it wants the height */}
        <section className="panel license-ops-panel">
          <div className="license-ops-body">
            <div className="license-ops" role="group" aria-label="Operation">
              {imagingGroups.map((group) => (
                <div key={group} className="license-op-group" role="group" aria-label={group}>
                  <span className="license-op-group-name">{group}</span>
                  <div className="license-op-buttons">
                    {imagingOps
                      .filter((o) => o.group === group)
                      .map((o) => {
                        const offered = available(o.key);
                        return (
                          <button
                            key={o.key}
                            aria-pressed={o.key === op.key}
                            className={"license-op" + (o.key === op.key ? " active" : "") + (offered === false ? " unavailable" : "")}
                            title={o.about + (offered === false ? " Not offered by this service." : "")}
                            onClick={() => chooseOp(o)}
                            disabled={busy}
                          >
                            {o.label}
                          </button>
                        );
                      })}
                  </div>
                </div>
              ))}
            </div>
            <div className="license-op-about">
              <strong>{op.label}</strong>
              <p>{op.about}</p>
              {unoffered && <p className="license-offer-bad">This service does not offer it: a call would be refused, and cost nothing.</p>}
            </div>
          </div>
        </section>
        <div className="license-bench">
          <div className="license-bench-side">
            <section className="panel">
              <h3 className="license-bench-head">Input</h3>
              <div className="license-bench-fields">
                {op.image && <FilePick label="Image" hint="PNG, JPEG or WebP" accept={imageTypes} files={image} disabled={busy} onChange={(files) => chooseImage(files)} />}
                {op.mask && (
                  <div className="license-field">
                    <span className="license-field-label">{op.mask === "required" ? "Mask" : "Mask (optional)"}</span>
                    {image.length === 0 ? (
                      <span className="license-muted license-field-hint">Choose the image first, then paint on it where the change goes.</span>
                    ) : (
                      <div className="license-mask-summary">
                        <span className={mask ? "" : "license-muted"}>
                          {mask ? "Painted, and sent as a mask as large as the image." : op.mask === "required" ? "Not painted yet: paint over what should go." : "None: the whole image may change."}
                        </span>
                        <button type="button" className="action-button" onClick={() => setStage("mask")} disabled={busy || shown === "mask"} title="Paint the mask on the image, on the stage">
                          <IconBrush size={14} stroke={1.8} />
                          Paint
                        </button>
                      </div>
                    )}
                  </div>
                )}
                {op.text && (
                  <Field label={op.text.label + (op.text.required ? "" : " (optional)")} locked={false}>
                    <textarea
                      className="text-input license-sms-message"
                      rows={op.text.field === "description" || op.text.field === "instruction" ? 3 : 2}
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
                        <span>Transparent</span>
                      </label>
                    )}
                  </div>
                )}
                {op.factor && (
                  <Field label="Factor" locked={false}>
                    <select className="select" value={factor} disabled={busy} onChange={(e) => setFactor(e.target.value)}>
                      <option value="2">2 × larger</option>
                      <option value="4">4 × larger</option>
                    </select>
                  </Field>
                )}
                {op.margins && (
                  <div className="license-field">
                    <span className="license-field-label">{op.key === "expand-image" ? "Grow by, in pixels" : "Cut in by, in pixels"}</span>
                    <div className="license-margins">
                      {marginSides.map((side) => (
                        <label key={side} className="license-margin">
                          <span className="license-muted">{side[0].toUpperCase() + side.slice(1)}</span>
                          <input
                            className="text-input"
                            inputMode="numeric"
                            value={margins[side]}
                            placeholder="0"
                            disabled={busy}
                            onChange={(e) => setMargins({ ...margins, [side]: e.target.value })}
                          />
                        </label>
                      ))}
                    </div>
                  </div>
                )}
                {op.language && (
                  <Field label="Language" hint="A code such as en or nb, for the title, description and keywords." locked={false}>
                    <input className="text-input" value={language} spellCheck={false} disabled={busy} onChange={(e) => setLanguage(e.target.value)} />
                  </Field>
                )}
              </div>
              <div className="license-save license-bench-run">
                <button className="action-button primary" onClick={run} disabled={busy || !allowed || !ready}>
                  <IconWand size={15} stroke={1.8} />
                  {busy ? "Working…" : op.label}
                </button>
                <label className="license-toggle" title="Make the call again, and pay for it again, rather than take the answer kept for the same call">
                  <input type="checkbox" checked={fresh} disabled={busy} onChange={(e) => setFresh(e.target.checked)} />
                  <span>New answer</span>
                </label>
              </div>
            </section>
            {(busy || error || result) && (
              <section className="panel license-answer-panel">
                <h3 className="license-bench-head">
                  Answer
                  {result && (
                    <span className="panel-sub">
                      {credits(result.answer.credits)} · {result.answer.creditsLeft.toLocaleString()} left{result.answer.cached ? " · kept from before" : ""}
                    </span>
                  )}
                </h3>
                {busy ? (
                  <div className="license-answer-busy" role="status">
                    <div className="progress-bar indeterminate">
                      <div className="progress-fill" />
                    </div>
                    <span className="license-muted">The service is working on it…</span>
                  </div>
                ) : error ? (
                  <div className="license-error">{error}</div>
                ) : (
                  result && <ImagingAnswerDetails result={result} answerUrl={answerUrl} onUse={takeAsImage} />
                )}
              </section>
            )}
          </div>
          <section className="panel license-bench-main">
            {shown === null && (
              <StageEmpty lead={lead} caption={op.image ? "Nothing chosen yet" : "Nothing made yet"}>
                {op.image ? "Choose an image: it shows here, and the answer after it." : "The new image shows here."}
              </StageEmpty>
            )}
            {op.image && imageUrl && image.length > 0 && (
              <div className="license-stage-pane" hidden={shown !== "image"}>
                <ImageViewer
                  src={imageUrl}
                  alt="The image chosen"
                  lead={lead}
                  above={
                    <span className="license-stage-caption">
                      {image[0].name} · {formatBytes(image[0].size)} · sent as it is
                    </span>
                  }
                />
              </div>
            )}
            {/* kept while another view is shown, so the strokes can still be undone on coming back */}
            {op.mask && image.length > 0 && (
              <div className="license-stage-pane" hidden={shown !== "mask"}>
                <MaskEditor key={imageTurn} lead={lead} image={image[0]} mask={mask} disabled={busy} onChange={setMask} />
              </div>
            )}
            {(result || busy) && (
              <div className="license-stage-pane" hidden={shown !== "answer"}>
                {result ? (
                  <ImagingAnswerStage result={result} lead={lead} answerUrl={answerUrl} inputUrl={inputUrl} />
                ) : (
                  <StageEmpty lead={lead} caption="Waiting for the service" busy>
                    The service is working on it…
                  </StageEmpty>
                )}
              </div>
            )}
          </section>
        </div>
      </TestBody>
    </>
  );
}

/** The stage with nothing on it yet, the size it will be: what will show there, or that the service is at work. */
function StageEmpty({ lead, caption, busy, children }: { lead?: ReactNode; caption: string; busy?: boolean; children?: ReactNode }) {
  return (
    <div className="iv">
      <div className="iv-bar">{lead}</div>
      <span className="license-stage-caption">{caption}</span>
      <div className="iv-view">
        <div className="license-imaging-empty license-muted" role={busy ? "status" : undefined}>
          {busy && (
            <div className="progress-bar indeterminate">
              <div className="progress-fill" />
            </div>
          )}
          {children && <span>{children}</span>}
        </div>
      </div>
    </div>
  );
}

/**
 * An answer on the stage: an image laid over the image it was made from, with a split to drag
 * between them (but not a turn: a quarter of one does not even have its shape); otherwise the image
 * the answer is about, with what image-to-meta found drawn on it.
 */
function ImagingAnswerStage({ result, lead, answerUrl, inputUrl }: { result: ImagingResult; lead: ReactNode; answerUrl: string | null; inputUrl: string | null }) {
  const a = result.answer;
  const caption = (text: string) => (
    <span className="license-stage-caption" title={text}>
      {text}
    </span>
  );
  if (a.kind === "image") {
    if (!answerUrl) return <StageEmpty lead={lead} caption="" />;
    const before = a.rotation === undefined ? inputUrl : null;
    return (
      <ImageViewer
        src={answerUrl}
        alt={"The answer to " + result.op.key}
        before={before}
        beforeInset={result.inset}
        lead={lead}
        above={caption(`${result.op.label}: ${a.width} × ${a.height} PNG` + (before ? " · drag the split to compare it with the image sent" : ""))}
      />
    );
  }
  if (!inputUrl) return <StageEmpty lead={lead} caption="" />;
  if (a.kind === "meta") {
    const percent = (part: number, whole: number) => (whole > 0 ? (100 * part) / whole : 0) + "%";
    return (
      <ImageViewer src={inputUrl} alt="The image described" lead={lead} above={caption("What was found, drawn on the image sent: where each thing is, and the point it is about")}>
        {(natural) => (
          <>
            {a.objects.map((o, i) => (
              <span
                key={i}
                className="license-imaging-box"
                style={{ left: percent(o.x, natural.w), top: percent(o.y, natural.h), width: percent(o.width, natural.w), height: percent(o.height, natural.h) }}
                title={`${o.type}${o.name ? ": " + o.name : ""} (${Math.round(o.confidence * 100)} %)`}
              >
                <span>{o.name || o.type}</span>
              </span>
            ))}
            {a.focus && <span className="license-imaging-focus" style={{ left: percent(a.focus.x, natural.w), top: percent(a.focus.y, natural.h) }} title="The point the image is about" />}
          </>
        )}
      </ImageViewer>
    );
  }
  return <ImageViewer src={inputUrl} alt="The image sent" lead={lead} above={caption(a.kind === "left-as-is" ? "The image, left as it is" : "The image asked about")} />;
}

/** What an answer says, in the Answer panel under what was sent: the image's size and what to do with it, or the words. */
function ImagingAnswerDetails({ result, answerUrl, onUse }: { result: ImagingResult; answerUrl: string | null; onUse: (answer: ImagingImageResult) => void }) {
  const a = result.answer;
  switch (a.kind) {
    case "image":
      return (
        <div className="license-answer">
          <strong>
            {a.rotation !== undefined && `Turned ${a.rotation}° clockwise · `}
            {a.width} × {a.height} PNG · {formatBytes(a.png.size)}
          </strong>
          <code className="license-ai-vector" title="The image's name at the service: a later call naming it sends nothing">
            sha256 {a.sha256}
          </code>
          <div className="license-answer-actions">
            {answerUrl && (
              <a className="action-button" href={answerUrl} target="_blank" rel="noreferrer" title="Open the answer at full size in a new tab" aria-label="Open">
                <IconExternalLink size={14} stroke={1.8} />
              </a>
            )}
            {answerUrl && (
              <a className="action-button" href={answerUrl} download={result.op.key + ".png"} title="Download the answer" aria-label="Download">
                <IconDownload size={14} stroke={1.8} />
              </a>
            )}
            <button className="action-button" onClick={() => onUse(a)} title="Take this answer as the image of the next call">
              <IconArrowBackUp size={14} stroke={1.8} />
              Use as image
            </button>
          </div>
        </div>
      );
    case "left-as-is":
      return (
        <div className="license-answer">
          <strong>Left as it is · not turned</strong>
          <span className="license-muted">It stands the right way up already, or which way is up could not be told.</span>
        </div>
      );
    case "bool":
      return (
        <div className="license-answer">
          {result.question && <span className="license-muted">{result.question}</span>}
          <strong className="license-answer-big" title="How sure the answer is, from 0 (a guess: the image says nothing either way) to 100 (it plainly shows it)">
            {a.answer ? "Yes" : "No"} · {a.certainty} % sure
          </strong>
        </div>
      );
    case "answer":
      return (
        <div className="license-answer">
          {result.question && <span className="license-muted">{result.question}</span>}
          <span className="license-imaging-answer-text">{a.answer}</span>
        </div>
      );
    default:
      return (
        <div className="license-answer">
          <strong>{a.title || "No title"}</strong>
          {a.description && <span>{a.description}</span>}
          {a.keywords.length > 0 && (
            <div className="license-chips">
              {a.keywords.map((k) => (
                <span key={k} className="license-chip">
                  {k}
                </span>
              ))}
            </div>
          )}
          <span className="license-muted">
            {a.language ? a.language + " · " : ""}
            {a.objects.length} {a.objects.length === 1 ? "thing" : "things"} found{a.focus ? " · a focus point" : ""}
          </span>
        </div>
      );
  }
}

/** the most of a text put on the page; the whole of it is in the download */
const shownTextChars = 200_000;

/**
 * A real file read through the Relatude FileToText service, so whoever set up the license can see what
 * the service makes of the files the database holds before code depends on it. Only read when the
 * license has the "filetotext" credit account every file is charged to. The kinds of file the service
 * reads are listed in the service bar, license or not; a file it cannot read is refused for nothing.
 *
 * Laid out as the Imaging test is: what is sent in a column on the left, and what came back - the
 * text, page by page - in the panel beside it, the two starting and ending on the same lines.
 */
function FileToTextTest({ credit, configuredUrl, allowed }: { credit: ReactNode; configuredUrl: string; allowed: boolean }) {
  const url = configuredUrl.trim();
  const offer = useServiceInfo(url, fetchFileToTextFormats);
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
    <>
      <ServiceBar credit={credit}>
        <ServiceOfferRow>
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
        </ServiceOfferRow>
      </ServiceBar>
      <TestBody allowed={allowed} className="license-bench">
        <div className="license-bench-side">
          <section className="panel">
            <h3 className="license-bench-head">Input</h3>
            <div className="license-bench-fields">
              <FilePick
                label="File"
                hint="Judged by what it holds, not by its name. A video's sound is sent, taken by the FFmpeg plugin here."
                files={file}
                disabled={busy}
                onChange={setFile}
              />
              <Field label="Languages" hint="Codes such as nb, en, most likely first. They help OCR and speech to text." locked={false}>
                <input className="text-input" value={languages} placeholder="nb, en" spellCheck={false} disabled={busy} onChange={(e) => setLanguages(e.target.value)} />
              </Field>
            </div>
            <div className="license-save license-bench-run">
              <button className="action-button primary" onClick={run} disabled={busy || !allowed || file.length === 0}>
                <IconFileText size={15} stroke={1.8} />
                {busy ? "Reading…" : "Read"}
              </button>
              <label className="license-toggle" title="Read the file again, and pay for it again, rather than take the text kept from before">
                <input type="checkbox" checked={fresh} disabled={busy} onChange={(e) => setFresh(e.target.checked)} />
                <span>Read anew</span>
              </label>
            </div>
          </section>
        </div>
        <section className="panel license-bench-main">
          <FileTextView result={result} busy={busy} error={error} />
        </section>
      </TestBody>
    </>
  );
}

/** A recording's length as m:ss, or h:mm:ss from an hour. */
function playTime(seconds: number) {
  const s = Math.round(seconds);
  const mmss = `${Math.floor((s % 3600) / 60)}:${String(s % 60).padStart(2, "0")}`;
  return s >= 3600 ? `${Math.floor(s / 3600)}:${mmss.padStart(5, "0")}` : mmss;
}

/**
 * The text the service read, in the panel beside the file: a heading row with copy and download, one
 * line of what it read the file as and what it cost, and the text page by page in a box that scrolls.
 * Before the first read, the box says what will show there; while reading, that it is under way.
 * A transcript can be shown without its timestamps, as an index takes it, and downloaded as WebVTT subtitles.
 */
function FileTextView({ result, busy, error }: { result: FileToTextResult | null; busy: boolean; error: string | null }) {
  const [timestamps, setTimestamps] = useState(true);
  const text = result ? (result.timed && !timestamps ? (result.textWithoutTimestamps ?? result.text) : result.text) : "";
  const pages = useMemo(() => (result ? text.slice(0, shownTextChars).split("\f") : []), [result, text]);
  const download = useObjectUrl(useMemo(() => (result ? new Blob([text], { type: "text/plain;charset=utf-8" }) : null), [result, text]));
  const subtitles = useObjectUrl(useMemo(() => (result?.webVtt ? new Blob([result.webVtt], { type: "text/vtt;charset=utf-8" }) : null), [result]));
  const name = (result?.fileName || "file").replace(/\.[^.]*$/, "");
  const caption = result
    ? [
        "Read as " + result.format + (result.fileName ? " · " + result.fileName : ""),
        result.duration != null ? `plays ${playTime(result.duration)}` : null,
        result.timed ? "a line for each stretch of speech" : null,
        result.pages != null ? `${result.pages} ${result.pages === 1 ? "page" : "pages"}` : null,
        `${result.characters.toLocaleString()} characters`,
        result.ocr ? "partly read by OCR" : null,
        result.truncated ? "cut: the file holds more" : null,
        result.title ? `“${result.title}”` + (result.author ? " by " + result.author : "") : result.author ? "by " + result.author : null,
        result.language ? "in " + result.language : null,
        `${credits(result.credits)} · ${result.creditsLeft.toLocaleString()} left` + (result.cached ? " · the text kept from before" : ""),
      ]
        .filter(Boolean)
        .join(" · ")
    : busy
      ? "Waiting for the service"
      : "Nothing read yet";
  return (
    <div className="license-text-view">
      <div className="iv-bar">
        <span className="license-stage-name">Text</span>
        <span className="iv-spacer" />
        {result?.timed && (
          <label className="license-toggle" title="The time each line is spoken; an index takes the text without it">
            <input type="checkbox" checked={timestamps} onChange={(e) => setTimestamps(e.target.checked)} />
            <span>Timestamps</span>
          </label>
        )}
        {result && <CopyText text={text} title="Copy the text" small />}
        {download && (
          <a className="icon-button labelled" href={download} download={name + ".txt"} title="Download the whole text">
            <IconDownload size={14} stroke={1.8} />
            Download
          </a>
        )}
        {subtitles && (
          <a className="icon-button labelled" href={subtitles} download={name + ".vtt"} title="Download the transcript as WebVTT subtitles, for a video player">
            <IconDownload size={14} stroke={1.8} />
            Subtitles
          </a>
        )}
      </div>
      <span className="license-stage-caption" title={caption}>
        {caption}
      </span>
      <div className={"license-text-result" + (result && !error ? "" : " empty")}>
        {busy ? (
          <div className="license-answer-busy" role="status">
            <div className="progress-bar indeterminate">
              <div className="progress-fill" />
            </div>
            <span className="license-muted">The service is reading it…</span>
          </div>
        ) : error ? (
          <div className="license-error">{error}</div>
        ) : !result ? (
          <span className="license-muted">Choose a file and read it: its text shows here, page by page.</span>
        ) : (
          <>
            {result.text.length === 0 && <p className="license-muted">{result.timed ? "Nothing is said in it." : "The file holds no text."}</p>}
            {pages.map((page, i) => (
              <div key={i} className="license-text-page">
                {pages.length > 1 && <span className="license-text-page-no">Page {i + 1}</span>}
                <pre>{page}</pre>
              </div>
            ))}
            {text.length > shownTextChars && (
              <p className="license-muted">
                The first {shownTextChars.toLocaleString()} characters of {text.length.toLocaleString()} are shown; the download has them all.
              </p>
            )}
          </>
        )}
      </div>
    </div>
  );
}

/**
 * A line's own languages, written before its text the way the service's own test page has them:
 * "[sv] Hej" is in Swedish, "[>de] Hello" goes to German, "[sv>de] Hej" both. The codes are BCP 47
 * tags as the service takes them: nb, en-GB, zh-Hans.
 */
const ownLanguages = /^\s*\[\s*([A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)?\s*(?:>\s*([A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*))?\s*\]\s?/;

/** The texts to send, one a line, each with the languages it names of its own. Empty lines are left out. */
function translationTexts(input: string): TranslationTestText[] {
  return input
    .split(/\r?\n/)
    .filter((line) => line.trim())
    .map((line) => {
      const own = ownLanguages.exec(line);
      if (!own || (!own[1] && !own[2])) return { text: line };
      return { text: line.slice(own[0].length), from: own[1] ?? null, to: own[2] ?? null };
    })
    .filter((t) => t.text.trim());
}

const sampleTexts = ["Good morning, and welcome.", "[sv] Hej och välkommen till oss.", "[>de] Free delivery on every order."].join("\n");

/** What came back, with the texts it came back for: the panel shows each text beside its answer. */
type TranslationRun =
  | { kind: "translate"; texts: TranslationTestText[]; result: TranslationTestResult }
  | { kind: "detect"; texts: TranslationTestText[]; result: DetectionTestResult };

/**
 * Real texts translated through the Relatude Translation service, so whoever set up the license can see
 * what it makes of them before code depends on it. Only sent when the license has the "translation"
 * credit account every call is charged to. The languages it translates, and what a call costs, are in
 * the service bar, license or not; they are also what the two language fields offer.
 *
 * Laid out as the File to text test is: the texts and their languages in a column on the left, and
 * what came back - each text beside its translation, or its language - in the panel beside it.
 */
function TranslationTest({ credit, configuredUrl, allowed }: { credit: ReactNode; configuredUrl: string; allowed: boolean }) {
  const url = configuredUrl.trim();
  const offer = useServiceInfo(url, fetchTranslationLanguages);
  const [input, setInput] = useState(sampleTexts);
  const [to, setTo] = useState("nb");
  const [from, setFrom] = useState("");
  const [html, setHtml] = useState(false);
  const [fresh, setFresh] = useState(false);
  const [busy, setBusy] = useState<"translate" | "detect" | null>(null);
  const [run, setRun] = useState<TranslationRun | null>(null);
  const [error, setError] = useState<string | null>(null);
  const texts = useMemo(() => translationTexts(input), [input]);
  const languages = offer?.info?.languages ?? [];
  const names = useMemo(() => new Map(languages.map((l) => [l.code.toLowerCase(), l])), [languages]);
  const choices = useMemo(() => languages.map((l) => ({ value: l.code, label: `${l.code} · ${l.name}`, hint: l.nativeName !== l.name ? l.nativeName : null })), [languages]);
  // every text needs a language to go to: the call's, or one of its own
  const toMissing = !to.trim() && texts.some((t) => !t.to);
  const characters = texts.reduce((n, t) => n + t.text.trim().length, 0);

  async function go(kind: "translate" | "detect") {
    setBusy(kind);
    setError(null);
    setRun(null);
    const sent = texts;
    try {
      if (kind === "translate") {
        const result = await runTranslationTest({ serviceUrl: url, texts: sent, to: to.trim(), from: from.trim(), format: html ? "html" : "text", fresh });
        setRun({ kind, texts: sent, result });
      } else {
        const result = await runDetectionTest({ serviceUrl: url, texts: sent.map((t) => t.text), fresh });
        setRun({ kind, texts: sent, result });
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(null);
    }
  }

  return (
    <>
      <ServiceBar credit={credit}>
        <ServiceOfferRow>
          <ServiceOffer answer={offer}>
            {offer?.info && (
              <>
                <span>
                  {offer.info.charsPerCredit.toLocaleString()} characters a credit,{" "}
                  {offer.info.cachedCharsPerCredit > 0 ? offer.info.cachedCharsPerCredit.toLocaleString() + " translated before" : "those translated before free"} · up to{" "}
                  {offer.info.maxTexts.toLocaleString()} texts and {offer.info.maxTotalChars.toLocaleString()} characters a call · translates {languages.length}{" "}
                  languages:
                </span>
                <span className="license-formats">
                  {languages.map((l) => (
                    <span key={l.code} className="license-format" title={l.name + (l.nativeName !== l.name ? " · " + l.nativeName : "")}>
                      {l.code}
                    </span>
                  ))}
                </span>
              </>
            )}
          </ServiceOffer>
        </ServiceOfferRow>
      </ServiceBar>
      <TestBody allowed={allowed} className="license-bench">
        <div className="license-bench-side">
          <section className="panel">
            <h3 className="license-bench-head">Input</h3>
            <div className="license-bench-fields">
              <Field
                label="Texts"
                hint={`One text a line, ${texts.length} ${texts.length === 1 ? "text" : "texts"} of ${characters.toLocaleString()} characters. A line may name its own languages first: [sv] Hej, [>de] Hello, [sv>de] Hej.`}
                locked={false}
              >
                <textarea className="text-input license-translation-input" rows={8} value={input} spellCheck={false} disabled={busy !== null} onChange={(e) => setInput(e.target.value)} />
              </Field>
              <div className="license-translation-languages">
                <Field label="To" locked={false}>
                  <Combo label="To" placeholder="each line's own" options={choices} value={to} disabled={busy !== null} onChange={(v) => setTo(String(v ?? ""))} />
                </Field>
                <Field label="From" locked={false}>
                  <Combo label="From" placeholder="found for each text" options={choices} value={from} disabled={busy !== null} onChange={(v) => setFrom(String(v ?? ""))} />
                </Field>
              </div>
              <div className="license-translation-toggles">
                <label className="license-toggle" title="Keep the markup, and translate only its text">
                  <input type="checkbox" checked={html} disabled={busy !== null} onChange={(e) => setHtml(e.target.checked)} />
                  <span>HTML</span>
                </label>
                <label className="license-toggle" title="Translate every text again, and pay for it again, rather than take what was translated before">
                  <input type="checkbox" checked={fresh} disabled={busy !== null} onChange={(e) => setFresh(e.target.checked)} />
                  <span>Translate anew</span>
                </label>
              </div>
            </div>
            <div className="license-save license-bench-run">
              <button
                className="action-button primary"
                onClick={() => void go("translate")}
                disabled={busy !== null || !allowed || texts.length === 0 || toMissing}
                title={toMissing ? "Choose a language to translate to, or name one on every line" : undefined}
              >
                <IconLanguage size={15} stroke={1.8} />
                {busy === "translate" ? "Translating…" : "Translate"}
              </button>
              <button className="action-button" onClick={() => void go("detect")} disabled={busy !== null || !allowed || texts.length === 0} title="Find the language of each text; priced as a translation">
                <IconSearch size={15} stroke={1.8} />
                {busy === "detect" ? "Finding…" : "Find languages"}
              </button>
            </div>
          </section>
        </div>
        <section className="panel license-bench-main">
          <TranslationView run={run} busy={busy} error={error} names={names} />
        </section>
      </TestBody>
    </>
  );
}

/** A language code as a chip, its name in English and in itself on hover; a dash for none. */
function LanguageChip({ code, names }: { code: string | null | undefined; names: Map<string, TranslationLanguage> }) {
  const language = code ? names.get(code.toLowerCase()) : undefined;
  return (
    <span className="license-format license-lang" title={language ? language.name + (language.nativeName !== language.name ? " · " + language.nativeName : "") : code ? undefined : "no language"}>
      {code || "–"}
    </span>
  );
}

function percent(score: number | null | undefined) {
  return score == null ? "" : Math.round(score * 100) + "%";
}

/**
 * What came back, in the panel beside the texts: a heading row with copy, one line of what the call
 * cost, and each text beside its translation - or its language - in a box that scrolls. A language
 * found rather than named says how sure the service is; a translation made before says so.
 */
function TranslationView({
  run,
  busy,
  error,
  names,
}: {
  run: TranslationRun | null;
  busy: "translate" | "detect" | null;
  error: string | null;
  names: Map<string, TranslationLanguage>;
}) {
  const result = run?.result;
  const count = run?.texts.length ?? 0;
  const caption = result
    ? [
        `${count} ${count === 1 ? "text" : "texts"}`,
        `${result.characters.toLocaleString()} characters sent`,
        result.cachedCharacters > 0 ? `${result.cachedCharacters.toLocaleString()} from before` : null,
        `${credits(result.credits)} · ${result.creditsLeft.toLocaleString()} left`,
      ]
        .filter(Boolean)
        .join(" · ")
    : busy
      ? "Waiting for the service"
      : "Nothing sent yet";
  const copy = run?.kind === "translate" ? run.result.translations.map((t) => t.text).join("\n") : "";
  return (
    <div className="license-text-view">
      <div className="iv-bar">
        <span className="license-stage-name">{run?.kind === "detect" ? "Languages" : "Translations"}</span>
        <span className="iv-spacer" />
        {copy && <CopyText text={copy} title="Copy the translations, one a line" small />}
      </div>
      <span className="license-stage-caption" title={caption}>
        {caption}
      </span>
      <div className={"license-text-result" + (run && !error && !busy ? "" : " empty")}>
        {busy ? (
          <div className="license-answer-busy" role="status">
            <div className="progress-bar indeterminate">
              <div className="progress-fill" />
            </div>
            <span className="license-muted">{busy === "translate" ? "The service is translating them…" : "The service is finding their languages…"}</span>
          </div>
        ) : error ? (
          <div className="license-error">{error}</div>
        ) : !run ? (
          <span className="license-muted">Write a few texts and translate them: each shows here beside its translation.</span>
        ) : run.kind === "translate" ? (
          <div className="license-translations">
            {run.result.translations.map((t, i) => (
              <div key={i} className="license-translation">
                <div className="license-translation-side">
                  <span className="license-translation-langs">
                    <LanguageChip code={t.from} names={names} />
                    {t.detected && <span className="license-muted">found{t.score != null ? ", " + percent(t.score) : ""}</span>}
                  </span>
                  <span className="license-translation-text license-muted">{run.texts[i]?.text}</span>
                </div>
                <div className="license-translation-side">
                  <span className="license-translation-langs">
                    <LanguageChip code={t.to} names={names} />
                    {t.cached && <span className="license-muted">translated before</span>}
                  </span>
                  <span className="license-translation-text" dir={names.get(t.to.toLowerCase())?.direction === "rtl" ? "rtl" : undefined}>
                    {t.text}
                  </span>
                </div>
              </div>
            ))}
          </div>
        ) : (
          <div className="license-translations">
            {run.result.detections.map((d, i) => (
              <div key={i} className="license-translation">
                <div className="license-translation-side">
                  <span className="license-translation-text license-muted">{run.texts[i]?.text}</span>
                </div>
                <div className="license-translation-side">
                  <span className="license-translation-langs">
                    <LanguageChip code={d.language} names={names} />
                    <span className="license-muted">
                      {d.language ? percent(d.score) + " sure" : "no language to place"}
                      {d.language && !d.translatable ? " · not translated from" : ""}
                      {d.cached ? " · found before" : ""}
                    </span>
                  </span>
                  {d.alternatives.length > 0 && (
                    <span className="license-translation-alternatives license-muted">
                      or{" "}
                      {d.alternatives.map((a, j) => (
                        <span key={a.language}>
                          {j > 0 && ", "}
                          <LanguageChip code={a.language} names={names} /> {percent(a.score)}
                        </span>
                      ))}
                    </span>
                  )}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
