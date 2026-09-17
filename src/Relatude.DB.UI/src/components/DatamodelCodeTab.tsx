import { useEffect, useMemo, useRef, useState } from "react";
import { IconCheck, IconCopy, IconDownload, IconLoader2 } from "@tabler/icons-react";
import { highlight } from "../code/highlight";
import type { Language } from "../code/language";
import { fetchModelCode, type CodeLanguage, type CodeScope, type ModelJson } from "../server/datamodel";

// The "As code" tab of the data model editor's forms: the thing the form edits, written out as model
// code to copy into a code editor. The server generates it (the same generator that writes model
// files at an activation), from the model the form is editing rather than from the active one, so a
// change shows up here before it is saved - which is the point: the tab is how a model drawn in the
// editor gets into a project that keeps its model in code.
//
// Only C# for now. The language picker is here so a second one is a list entry and a server case,
// and so the tab says what it is generating without anyone having to guess.

const languages: { value: CodeLanguage; label: string; syntax: Language; extension: string }[] = [
  { value: "csharp", label: "C#", syntax: "csharp", extension: ".cs" },
];

/** How long a change to the model waits before the code is asked for again: a form is typed into. */
const settleMs = 400;

export function CodeTab({ storeId, model, scope, id, typeId, name }: { storeId: string; model: ModelJson; scope: CodeScope; id: string; typeId?: string; name: string }) {
  const [language, setLanguage] = useState<CodeLanguage>("csharp");
  const [attributes, setAttributes] = useState(true);
  const [code, setCode] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(true);
  const [copied, setCopied] = useState(false);
  const copiedTimer = useRef(0);
  const syntax = languages.find((l) => l.value === language) ?? languages[0];

  useEffect(() => () => window.clearTimeout(copiedTimer.current), []);

  useEffect(() => {
    let dropped = false;
    setBusy(true);
    const timer = window.setTimeout(() => {
      fetchModelCode(storeId, model, scope, id, typeId, language, attributes).then(
        (r) => {
          if (dropped) return;
          setCode(r.content);
          setError(null);
          setBusy(false);
        },
        (e) => {
          if (dropped) return;
          // the model is edited as it is typed, so a half finished one reaching the generator is
          // ordinary: the message says what is wrong and the next keystroke asks again
          setError(e instanceof Error ? e.message : String(e));
          setBusy(false);
        },
      );
    }, settleMs);
    return () => {
      dropped = true;
      window.clearTimeout(timer);
    };
  }, [storeId, model, scope, id, typeId, language, attributes]);

  const html = useMemo(() => (code ? highlight(code, syntax.syntax) : ""), [code, syntax.syntax]);

  async function copy() {
    if (!code) return;
    try {
      await navigator.clipboard.writeText(code);
      setCopied(true);
      window.clearTimeout(copiedTimer.current);
      copiedTimer.current = window.setTimeout(() => setCopied(false), 1500);
    } catch (e) {
      setError("The code could not be copied: " + (e instanceof Error ? e.message : String(e)));
    }
  }

  function download() {
    if (!code) return;
    const url = URL.createObjectURL(new Blob([code], { type: "text/plain" }));
    const a = document.createElement("a");
    a.href = url;
    a.download = fileName(name) + syntax.extension;
    a.click();
    URL.revokeObjectURL(url);
  }

  return (
    <div className="dm-code-tab">
      <div className="dm-code-bar">
        <select className="dm-code-language" value={language} onChange={(e) => setLanguage(e.target.value as CodeLanguage)} title="The language the model is written out in">
          {languages.map((l) => (
            <option key={l.value} value={l.value}>
              {l.label}
            </option>
          ))}
        </select>
        <label className="dm-code-toggle" title="Leave out the attributes that carry the ids and the index settings. Without them the code is easier to read, and a model built from it is not the same model.">
          <input type="checkbox" checked={attributes} onChange={(e) => setAttributes(e.target.checked)} /> Attributes
        </label>
        <span className="dm-code-spacer" />
        {busy && <IconLoader2 size={15} className="spin" />}
        <button className={"icon-button" + (copied ? " copied" : "")} title={copied ? "Copied" : "Copy the code"} disabled={!code} onClick={copy}>
          {copied ? <IconCheck size={16} stroke={2} /> : <IconCopy size={16} stroke={1.8} />}
        </button>
        <button className="icon-button" title="Save the code as a file" disabled={!code} onClick={download}>
          <IconDownload size={16} stroke={1.8} />
        </button>
      </div>
      {error && <div className="dm-note dm-code-error">{error}</div>}
      {code !== null && code.length === 0 && !error && <div className="muted dm-empty">Nothing to generate: this is empty.</div>}
      {code && <pre className="dm-code" dangerouslySetInnerHTML={{ __html: html }} />}
    </div>
  );
}

/** A file name from what the form is showing: the code name, without what a file name cannot hold. */
function fileName(name: string): string {
  const clean = name.replace(/[^A-Za-z0-9_.-]+/g, "");
  return clean.length > 0 ? clean : "model";
}
