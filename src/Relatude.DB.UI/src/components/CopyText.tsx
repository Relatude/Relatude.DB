import { useEffect, useRef, useState } from "react";
import { IconCheck, IconCopy } from "@tabler/icons-react";
import { showError } from "../dialogs";

/** A button that puts a text on the clipboard; the tick that follows says it happened. */
export function CopyText({ text, title = "Copy", small }: { text: string | (() => string); title?: string; small?: boolean }) {
  const [copied, setCopied] = useState(false);
  const timer = useRef(0);
  useEffect(() => () => window.clearTimeout(timer.current), []);
  const empty = typeof text === "string" && !text;
  async function copy() {
    try {
      await navigator.clipboard.writeText(typeof text === "string" ? text : text());
      setCopied(true);
      window.clearTimeout(timer.current);
      timer.current = window.setTimeout(() => setCopied(false), 1500);
    } catch (e) {
      await showError("Could not copy", e instanceof Error ? e.message : String(e));
    }
  }
  return (
    <button className={"icon-button" + (small ? " small" : "") + (copied ? " copied" : "")} title={copied ? "Copied" : title} disabled={empty} onClick={copy}>
      {copied ? <IconCheck size={small ? 15 : 16} stroke={1.8} /> : <IconCopy size={small ? 15 : 16} stroke={1.8} />}
    </button>
  );
}
