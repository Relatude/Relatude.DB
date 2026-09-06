import { useEffect, useRef, useState } from "react";
import { IconCheck, IconCopy } from "@tabler/icons-react";
import { copyTable, type TableData } from "../clipboard";
import { showError } from "../dialogs";

/**
 * The button that puts a table on the clipboard. The table is asked for at the click, not before,
 * so a page that re-renders a hundred times a minute does not build it a hundred times; the tick
 * that follows says it happened, since nothing else on screen changes.
 */
export function CopyButton({ title, disabled, table }: { title: string; disabled?: boolean; table: () => TableData }) {
  const [copied, setCopied] = useState(false);
  const timer = useRef(0);
  useEffect(() => () => window.clearTimeout(timer.current), []);
  async function copy() {
    try {
      await copyTable(table());
      setCopied(true);
      window.clearTimeout(timer.current);
      timer.current = window.setTimeout(() => setCopied(false), 1500);
    } catch (e) {
      await showError("Could not copy", e instanceof Error ? e.message : String(e));
    }
  }
  return (
    <button className={"icon-button" + (copied ? " copied" : "")} title={copied ? "Copied" : title} disabled={disabled} onClick={copy}>
      {copied ? <IconCheck size={16} stroke={2} /> : <IconCopy size={16} stroke={1.8} />}
    </button>
  );
}
