import { useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { IconAlertTriangle, IconBook, IconCheck, IconChevronDown, IconChevronRight, IconDatabase, IconInfoCircle, IconLoader2, IconSearch, IconX } from "@tabler/icons-react";
import { DialogTools } from "./DialogTools";
import { findTemplateFor, patternGroups, patternTemplates, type CaseChange, type PatternTemplate, type PatternVariant } from "../patternTemplates";
import { allowsEmptyValue, explainPattern, ignoresCase, withCaseIgnored, withEmptyValueAllowed } from "../explainPattern";
import { testPattern, type PatternTest, type StoredValuesCheck } from "../server/datamodel";

export interface SiblingField {
  value: unknown;
  onChange: (value: unknown) => void;
  disabled: boolean;
}

export type SiblingAccess = (path: string) => SiblingField | null;

interface PatternChoice {
  pattern: string;
  message: string;
  trim: boolean;
  caseChange: CaseChange;
}

const caseChangeLabels: Record<CaseChange, string> = {
  NoTransform: "Keep as typed",
  ToLowerCase: "Make lower case",
  ToUpperCase: "MAKE UPPER CASE",
};

const guide: { title: string; items: { code: string; meaning: string }[] }[] = [
  {
    title: "Where",
    items: [
      { code: "^", meaning: "The start of the value. Put it first." },
      { code: "$", meaning: "The end of the value. Put it last. With both, the whole value has to fit the pattern, not just a piece of it." },
    ],
  },
  {
    title: "One character",
    items: [
      { code: "\\d", meaning: "A digit 0-9" },
      { code: "\\p{L}", meaning: "A letter in any language, also æ ø å é ü" },
      { code: "[a-z]", meaning: "One lower case letter a to z (English letters only)" },
      { code: "[A-Z]", meaning: "One upper case letter A to Z" },
      { code: "[abc]", meaning: "One of the characters listed: a, b or c" },
      { code: "[^abc]", meaning: "Any one character except those listed" },
      { code: "\\s", meaning: "A space, tab or line break" },
      { code: "\\w", meaning: "A letter, digit or underscore" },
      { code: ".", meaning: "Any character at all (except a line break)" },
    ],
  },
  {
    title: "How many",
    items: [
      { code: "?", meaning: "The thing before it is optional" },
      { code: "+", meaning: "One or more of the thing before it" },
      { code: "*", meaning: "Any number of the thing before it, also none" },
      { code: "{4}", meaning: "Exactly 4 of the thing before it" },
      { code: "{2,5}", meaning: "Between 2 and 5 of the thing before it" },
      { code: "{2,}", meaning: "2 or more of the thing before it" },
    ],
  },
  {
    title: "Groups and choices",
    items: [
      { code: "(...)", meaning: "Treats what is inside as one unit, so ? + * {n} apply to all of it" },
      { code: "a|b", meaning: "Either a or b. Inside a group: (Mr|Mrs|Ms)" },
    ],
  },
  {
    title: "Special characters",
    items: [
      { code: "\\.", meaning: "A real dot. Put \\ before . + * ? ( ) [ ] { } ^ $ | \\ to mean the character itself" },
      { code: "\\-", meaning: "A real dash inside [ ], like [a-z\\-]" },
      { code: "(?i)", meaning: "At the very start: upper and lower case count as the same" },
    ],
  },
];

function useEscape(onClose: () => void) {
  const latest = useRef(onClose);
  latest.current = onClose;
  useEffect(() => {
    const close = (e: KeyboardEvent) => {
      if (e.key === "Escape") latest.current();
    };
    window.addEventListener("keydown", close);
    return () => window.removeEventListener("keydown", close);
  }, []);
}

function asCaseChange(value: unknown): CaseChange {
  return value === "ToLowerCase" || value === "ToUpperCase" ? value : "NoTransform";
}

function asText(value: unknown): string {
  return typeof value === "string" ? value : "";
}

function stripCaseAndEmpty(pattern: string): string {
  return withEmptyValueAllowed(withCaseIgnored(pattern, false), false);
}

export function PatternField({ value, onChange, disabled, siblings, storeId, propertyId, typeId, propertyName }: { value: unknown; onChange: (value: unknown) => void; disabled: boolean; siblings: SiblingAccess; storeId: string; propertyId?: string; typeId?: string; propertyName: string }) {
  const [open, setOpen] = useState(false);
  const pattern = asText(value);
  const known = pattern ? findTemplateFor(stripCaseAndEmpty(pattern)) : null;

  function apply(choice: PatternChoice) {
    onChange(choice.pattern === "" ? null : choice.pattern);
    siblings("RegularExpressionMessage")?.onChange(choice.message.trim() === "" ? undefined : choice.message.trim());
    const trim = siblings("Trim");
    if (trim && (trim.value === true) !== choice.trim && !trim.disabled) trim.onChange(choice.trim);
    const caseChange = siblings("CaseTransform");
    if (caseChange && asCaseChange(caseChange.value) !== choice.caseChange && !caseChange.disabled) caseChange.onChange(choice.caseChange);
    setOpen(false);
  }

  return (
    <div className="pattern-field">
      <div className="pattern-field-row">
        <input className="text-input pattern-input" type="text" value={pattern} disabled={disabled} spellCheck={false} placeholder="No pattern: any value is accepted" onChange={(e) => onChange(e.target.value === "" ? null : e.target.value)} />
        <button className="action-button pattern-open" onClick={() => setOpen(true)}>
          {disabled ? "Explain" : pattern ? "Edit and test" : "Choose a pattern"}
        </button>
      </div>
      {known && (
        <div className="pattern-known">
          {known.template.name}
          {known.template.variants.length > 1 ? " - " + known.variant.label : ""}
        </div>
      )}
      {open &&
        createPortal(
          <PatternDialog
            initial={{
              pattern,
              message: asText(siblings("RegularExpressionMessage")?.value),
              trim: siblings("Trim")?.value === true,
              caseChange: asCaseChange(siblings("CaseTransform")?.value),
            }}
            legalCharacters={asText(siblings("LegalCharacters")?.value) || null}
            readOnly={disabled}
            storeId={storeId}
            propertyId={propertyId}
            typeId={typeId}
            propertyName={propertyName}
            onApply={apply}
            onClose={() => setOpen(false)}
          />,
          document.body,
        )}
    </div>
  );
}

function PatternDialog({ initial, legalCharacters, readOnly, storeId, propertyId, typeId, propertyName, onApply, onClose }: { initial: PatternChoice; legalCharacters: string | null; readOnly: boolean; storeId: string; propertyId?: string; typeId?: string; propertyName: string; onApply: (choice: PatternChoice) => void; onClose: () => void }) {
  useEscape(onClose);
  const startingTemplate = initial.pattern ? findTemplateFor(stripCaseAndEmpty(initial.pattern)) : null;
  const [pattern, setPattern] = useState(initial.pattern);
  const [message, setMessage] = useState(initial.message);
  const [trim, setTrim] = useState(initial.trim);
  const [caseChange, setCaseChange] = useState<CaseChange>(initial.caseChange);
  const [templateId, setTemplateId] = useState<string | null>(startingTemplate?.template.id ?? null);
  const [variantIndex, setVariantIndex] = useState(startingTemplate ? startingTemplate.template.variants.indexOf(startingTemplate.variant) : 0);
  const [samples, setSamples] = useState(() => (startingTemplate ? examplesOf(startingTemplate.variant).join("\n") : ""));
  const [expected, setExpected] = useState<Map<string, boolean>>(() => (startingTemplate ? expectations(startingTemplate.variant) : new Map()));
  const [query, setQuery] = useState("");
  const [test, setTest] = useState<PatternTest | null>(null);
  const [testing, setTesting] = useState(false);
  const [testFailure, setTestFailure] = useState<string | null>(null);
  const [stored, setStored] = useState<StoredValuesCheck | null>(null);
  const [storedState, setStoredState] = useState<"idle" | "checking" | "failed" | "unavailable">("idle");
  const [guideOpen, setGuideOpen] = useState(!initial.pattern);
  const patternRef = useRef<HTMLInputElement>(null);
  const requestNumber = useRef(0);

  const template = patternTemplates.find((t) => t.id === templateId) ?? null;
  const parts = useMemo(() => explainPattern(pattern), [pattern]);
  const sampleValues = useMemo(() => samples.split(/\r?\n/), [samples]);
  const emptyAllowed = allowsEmptyValue(pattern);
  const caseIgnored = ignoresCase(pattern);

  useEffect(() => {
    const number = ++requestNumber.current;
    setTesting(true);
    const timer = window.setTimeout(() => {
      testPattern({ storeId, pattern, values: sampleValues.filter((v, i) => v !== "" || i < sampleValues.length - 1), trim, caseTransform: caseChange, legalCharacters })
        .then((result) => {
          if (number !== requestNumber.current) return;
          setTest(result);
          setTestFailure(null);
        })
        .catch((e) => {
          if (number === requestNumber.current) setTestFailure(e instanceof Error ? e.message : String(e));
        })
        .finally(() => {
          if (number === requestNumber.current) setTesting(false);
        });
    }, 250);
    return () => window.clearTimeout(timer);
  }, [storeId, pattern, sampleValues, trim, caseChange, legalCharacters]);

  useEffect(() => {
    setStored(null);
    setStoredState("idle");
  }, [pattern, trim, caseChange]);

  const filteredTemplates = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return patternTemplates;
    return patternTemplates.filter((t) => t.name.toLowerCase().includes(q) || t.description.toLowerCase().includes(q) || t.group.toLowerCase().includes(q) || t.variants.some((v) => v.label.toLowerCase().includes(q)));
  }, [query]);

  function chooseTemplate(t: PatternTemplate, index: number) {
    const variant = t.variants[index] ?? t.variants[0];
    setTemplateId(t.id);
    setVariantIndex(t.variants.indexOf(variant));
    setPattern(withCaseIgnored(withEmptyValueAllowed(variant.pattern, emptyAllowed), caseIgnored));
    setMessage(variant.message ?? t.message);
    if (t.trim !== undefined) setTrim(t.trim);
    setCaseChange(variant.caseChange ?? t.caseChange ?? "NoTransform");
    setSamples(examplesOf(variant).join("\n"));
    setExpected(expectations(variant));
  }

  function insert(code: string) {
    const input = patternRef.current;
    const at = input?.selectionStart ?? pattern.length;
    const to = input?.selectionEnd ?? pattern.length;
    const next = pattern.slice(0, at) + code + pattern.slice(to);
    setPattern(next);
    requestAnimationFrame(() => {
      input?.focus();
      input?.setSelectionRange(at + code.length, at + code.length);
    });
  }

  async function checkStored() {
    if (!propertyId || !typeId) return;
    setStoredState("checking");
    try {
      const result = await testPattern({ storeId, pattern, values: [], trim, caseTransform: caseChange, legalCharacters, propertyId, typeId });
      if (!result.stored) {
        setStoredState("unavailable");
        return;
      }
      setStored(result.stored);
      setStoredState("idle");
    } catch {
      setStoredState("failed");
    }
  }

  const results = test && !test.error ? test.results : [];
  const failing = results.filter((r) => !r.matches).length;
  const surprises = results.filter((r) => expected.has(r.value) && expected.get(r.value) !== r.matches).length;
  const changed = pattern !== initial.pattern || message !== initial.message || trim !== initial.trim || caseChange !== initial.caseChange;

  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="dialog pattern-dialog" role="dialog" aria-label={`Pattern for ${propertyName}`}>
        <h3>
          Pattern for {propertyName}
          <DialogTools onClose={onClose} closeTitle={readOnly ? "Close" : "Cancel"} />
        </h3>
        <div className="dialog-body pattern-intro">
          A pattern describes what a valid value looks like - for example &quot;four digits&quot; for a Norwegian post code. Values that do not fit are refused when they are saved, and the message below is shown instead. Start from a ready-made pattern and adjust it, then try it on example values.
        </div>
        <div className="pattern-columns">
          <div className="pattern-templates">
            <div className="pattern-search">
              <IconSearch size={14} stroke={2} />
              <input value={query} placeholder="Find a pattern: email, phone, post code…" spellCheck={false} onChange={(e) => setQuery(e.target.value)} />
              {query && (
                <button className="icon-button" title="Clear" onClick={() => setQuery("")}>
                  <IconX size={13} stroke={2} />
                </button>
              )}
            </div>
            <div className="pattern-template-list">
              {patternGroups.map((group) => {
                const inGroup = filteredTemplates.filter((t) => t.group === group);
                if (inGroup.length === 0) return null;
                return (
                  <div key={group} className="pattern-template-group">
                    <div className="pattern-template-group-name">{group}</div>
                    {inGroup.map((t) => (
                      <button key={t.id} className={"pattern-template" + (t.id === templateId ? " active" : "")} onClick={() => chooseTemplate(t, t.id === templateId ? variantIndex : 0)}>
                        <span className="pattern-template-name">
                          {t.name}
                          {t.variants.length > 1 && <span className="pattern-template-count">{t.variants.length} variants</span>}
                        </span>
                        <span className="pattern-template-description">{t.description}</span>
                      </button>
                    ))}
                  </div>
                );
              })}
              {filteredTemplates.length === 0 && <div className="muted pattern-empty">No ready-made pattern matches “{query.trim()}”. Write your own in the pattern box - the guide below it explains the pieces.</div>}
            </div>
          </div>
          <div className="pattern-work">
            {template && (
              <section className="pattern-section pattern-chosen">
                <div className="pattern-chosen-name">{template.name}</div>
                <div className="pattern-help">{template.description}</div>
                {template.variants.length > 1 && (
                  <label className="pattern-variant">
                    <span>{template.variantLabel ?? "Variant"}</span>
                    <select className="select" value={variantIndex} onChange={(e) => chooseTemplate(template, Number(e.target.value))}>
                      {template.variants.map((v, i) => (
                        <option key={v.label} value={i}>
                          {v.label}
                        </option>
                      ))}
                    </select>
                  </label>
                )}
              </section>
            )}

            <section className="pattern-section">
              <label className="pattern-label" htmlFor="pattern-input">
                Pattern
              </label>
              <input
                id="pattern-input"
                ref={patternRef}
                className={"text-input pattern-input" + (test?.error ? " invalid" : "")}
                value={pattern}
                spellCheck={false}
                autoFocus={!!initial.pattern}
                placeholder="Pick a pattern on the left, or write one, like ^\d{4}$"
                onChange={(e) => setPattern(e.target.value)}
              />
              {test?.error && (
                <div className="pattern-error">
                  <IconAlertTriangle size={14} stroke={2} /> {test.error}
                </div>
              )}
              <div className="pattern-options">
                <label className="dm-check" title="Wraps the pattern so an empty value passes too">
                  <input type="checkbox" checked={emptyAllowed} disabled={!pattern} onChange={(e) => setPattern(withEmptyValueAllowed(pattern, e.target.checked))} />
                  <span>The value may be empty</span>
                </label>
                <label className="dm-check" title="Adds (?i) at the start of the pattern">
                  <input type="checkbox" checked={caseIgnored} disabled={!pattern} onChange={(e) => setPattern(withCaseIgnored(pattern, e.target.checked))} />
                  <span>Upper and lower case count as the same</span>
                </label>
              </div>
              {pattern && !emptyAllowed && <div className="pattern-help">An empty value does not fit this pattern, so the property has to be filled in. Tick &quot;The value may be empty&quot; if it is optional.</div>}
            </section>

            <section className="pattern-section">
              <div className="pattern-label">What the pattern means</div>
              {pattern === "" ? (
                <div className="pattern-help">No pattern: every value is accepted.</div>
              ) : (
                <div className="pattern-explain">
                  {parts.map((part, i) => (
                    <div key={i} className="pattern-explain-row" style={{ paddingLeft: part.depth * 18 }}>
                      <code>{part.code}</code>
                      <span>{part.meaning}</span>
                    </div>
                  ))}
                </div>
              )}
            </section>

            <section className="pattern-section">
              <div className="pattern-label">
                Try it
                {testing && sampleValues.some((v) => v !== "") && <IconLoader2 size={13} stroke={2} className="spinning" />}
                {test && !test.error && sampleValues.some((v) => v !== "") && (
                  <span className={"badge " + (failing === 0 ? "dm-badge-ok" : "dm-badge-warn")}>
                    {failing === 0 ? "all fit" : `${failing} refused`}
                  </span>
                )}
                {surprises > 0 && <span className="badge dm-badge-warn">{surprises} not as the example expects</span>}
              </div>
              <div className="pattern-help">Type example values, one per line. Each is cleaned the way it will be when saved (see below), then checked.</div>
              <div className="pattern-try">
                <textarea className="text-input pattern-samples" rows={Math.max(4, Math.min(10, sampleValues.length + 1))} value={samples} spellCheck={false} placeholder={"91234567\n+47 912 34 567"} onChange={(e) => setSamples(e.target.value)} />
                <div className="pattern-results">
                  {testFailure && <div className="pattern-error">The values could not be tested: {testFailure}</div>}
                  {results.map((r, i) => (
                    <div key={i} className={"pattern-result " + (r.matches ? "ok" : "refused")}>
                      {r.matches ? <IconCheck size={14} stroke={2.4} /> : <IconX size={14} stroke={2.4} />}
                      <div className="pattern-result-text">
                        <code>{r.value === "" ? "(empty)" : r.value}</code>
                        {r.changed && <span className="pattern-result-stored">saved as <code>{r.stored === "" ? "(empty)" : r.stored}</code></span>}
                        {!r.matches && r.reason && <span className="pattern-result-reason">{r.reason}</span>}
                        {expected.has(r.value) && expected.get(r.value) !== r.matches && <span className="pattern-result-surprise">the example expects this to be {expected.get(r.value) ? "accepted" : "refused"}</span>}
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            </section>

            <section className="pattern-section">
              <div className="pattern-label">Before checking</div>
              <div className="pattern-help">Applied to every value when it is saved, before the pattern is checked. Trimming forgives a space typed by mistake; changing the case makes &quot;Ole@Example.com&quot; and &quot;ole@example.com&quot; the same value.</div>
              <div className="pattern-options">
                <label className="dm-check">
                  <input type="checkbox" checked={trim} onChange={(e) => setTrim(e.target.checked)} />
                  <span>Remove spaces at the start and end</span>
                </label>
                <select className="select" value={caseChange} onChange={(e) => setCaseChange(e.target.value as CaseChange)} aria-label="Change case">
                  {(Object.keys(caseChangeLabels) as CaseChange[]).map((c) => (
                    <option key={c} value={c}>
                      {caseChangeLabels[c]}
                    </option>
                  ))}
                </select>
              </div>
              {legalCharacters && (
                <div className="pattern-help">
                  <IconInfoCircle size={13} stroke={2} /> The property also only allows these characters: <code>{legalCharacters}</code>
                </div>
              )}
            </section>

            <section className="pattern-section">
              <label className="pattern-label" htmlFor="pattern-message">
                Message when a value does not fit
              </label>
              <input id="pattern-message" className="text-input" value={message} placeholder="Enter a valid post code, like 0150" onChange={(e) => setMessage(e.target.value)} />
              <div className="pattern-help">
                Shown as: <em>{propertyName} &quot;abc&quot; is not valid: {message.trim() || "…"}</em>. Without a message the pattern itself is shown, which few people can read.
              </div>
            </section>

            {propertyId && typeId && (
              <section className="pattern-section">
                <div className="pattern-label">Values already stored</div>
                <div className="pattern-help">Values already saved are not checked again, but a node whose value does not fit cannot be saved until the value is fixed.</div>
                <div className="pattern-stored">
                  <button className="action-button" onClick={checkStored} disabled={storedState === "checking" || !!test?.error}>
                    {storedState === "checking" ? <IconLoader2 size={14} stroke={2} className="spinning" /> : <IconDatabase size={14} stroke={2} />} Check the stored values
                  </button>
                  {storedState === "unavailable" && <span className="muted">The database is closed, or there is nothing to check.</span>}
                  {storedState === "failed" && <span className="pattern-error">The stored values could not be checked.</span>}
                  {stored && <StoredSummary stored={stored} />}
                </div>
              </section>
            )}

            <section className="pattern-section">
              <button className="link-button pattern-guide-toggle" onClick={() => setGuideOpen((o) => !o)}>
                {guideOpen ? <IconChevronDown size={13} stroke={2} /> : <IconChevronRight size={13} stroke={2} />}
                <IconBook size={14} stroke={2} /> Pattern guide - the building blocks
              </button>
              {guideOpen && (
                <div className="pattern-guide">
                  <div className="pattern-help">Click a building block to put it into the pattern where the cursor is.</div>
                  {guide.map((section) => (
                    <div key={section.title} className="pattern-guide-section">
                      <div className="pattern-guide-title">{section.title}</div>
                      {section.items.map((item) => (
                        <button key={item.code} className="pattern-guide-item" onClick={() => insert(item.code === "(...)" ? "()" : item.code === "a|b" ? "|" : item.code)} disabled={readOnly}>
                          <code>{item.code}</code>
                          <span>{item.meaning}</span>
                        </button>
                      ))}
                    </div>
                  ))}
                  <div className="pattern-help">
                    Example: <code>^\d{"{"}3{"}"} ?\d{"{"}2{"}"}$</code> is a Swedish post code - three digits, an optional space, two digits, and nothing before or after.
                  </div>
                </div>
              )}
            </section>
          </div>
        </div>
        <div className="dialog-row pattern-footer">
          {!readOnly && initial.pattern && (
            <button className="action-button danger" onClick={() => onApply({ pattern: "", message: "", trim, caseChange })}>
              Remove the pattern
            </button>
          )}
          <div className="header-spacer" />
          <button className="action-button" onClick={onClose}>
            {readOnly ? "Close" : "Cancel"}
          </button>
          {!readOnly && (
            <button className="action-button primary" disabled={!changed || !!test?.error} onClick={() => onApply({ pattern, message, trim, caseChange })} title={test?.error ? "Fix the pattern first" : undefined}>
              Use this pattern
            </button>
          )}
        </div>
      </div>
    </div>
  );
}

function StoredSummary({ stored }: { stored: StoredValuesCheck }) {
  const partly = stored.checked < stored.total ? ` (the first ${stored.checked.toLocaleString()} of ${stored.total.toLocaleString()} nodes)` : "";
  if (stored.checked === 0) return <span className="muted">There are no nodes to check yet.</span>;
  if (stored.failing === 0)
    return (
      <span className="pattern-stored-ok">
        <IconCheck size={14} stroke={2.4} /> All {stored.checked.toLocaleString()} stored values fit{partly}.
      </span>
    );
  return (
    <div className="pattern-stored-failing">
      <div>
        <IconAlertTriangle size={14} stroke={2} /> {stored.failing.toLocaleString()} of {stored.checked.toLocaleString()} stored values do not fit{partly}, for example:
      </div>
      <ul>
        {stored.examples.map((e, i) => (
          <li key={i}>
            <code>{e.value === "" ? "(empty)" : e.value}</code> {e.reason && <span className="muted">- {e.reason}</span>}
          </li>
        ))}
      </ul>
    </div>
  );
}

function examplesOf(variant: PatternVariant): string[] {
  return [...variant.matches, ...variant.doesNotMatch];
}

function expectations(variant: PatternVariant): Map<string, boolean> {
  const map = new Map<string, boolean>();
  for (const v of variant.matches) map.set(v, true);
  for (const v of variant.doesNotMatch) map.set(v, false);
  return map;
}
