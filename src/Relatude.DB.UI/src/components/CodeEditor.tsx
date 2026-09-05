import { useEffect, useMemo, useRef, type KeyboardEvent, type RefObject } from "react";
import { escapeHtml, highlight } from "../code/highlight";
import type { Language } from "../code/language";
import type { LintIssue } from "../code/lint";

// A small code editor with no dependencies: a transparent textarea over a <pre> holding the
// syntax coloured copy of the same text, in the same font and metrics, so the caret and the
// selection belong to the textarea while the colours come from the pre. A gutter to the left
// carries the line numbers and marks the lines the linter has something to say about. Tab
// indents, Enter keeps the indentation, Ctrl+S saves.

const highlightLimit = 400_000; // above this the colouring is skipped: the tokenizer is not free
const indentUnit = "  ";

export interface CodeEditorApi {
  goToLine(line: number): void;
}

export function CodeEditor(p: {
  value: string;
  onChange: (value: string) => void;
  language: Language;
  issues: LintIssue[];
  readOnly?: boolean;
  onSave?: () => void;
  apiRef?: RefObject<CodeEditorApi | null>;
}) {
  const scroller = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLTextAreaElement>(null);
  // a trailing newline is added so a file ending in one still shows its last, empty line: a <pre>
  // swallows a final newline, a textarea does not
  const html = useMemo(() => (p.value.length > highlightLimit ? escapeHtml(p.value) : highlight(p.value, p.language)) + "\n", [p.value, p.language]);
  const lineCount = useMemo(() => countLines(p.value), [p.value]);
  const issueByLine = useMemo(() => {
    const map = new Map<number, string>();
    for (const issue of p.issues) map.set(issue.line, map.has(issue.line) ? map.get(issue.line) + "\n" + issue.message : issue.message);
    return map;
  }, [p.issues]);

  useEffect(() => {
    if (!p.apiRef) return;
    p.apiRef.current = {
      goToLine(line) {
        const textarea = input.current;
        const box = scroller.current;
        if (!textarea || !box) return;
        const offset = offsetOfLine(textarea.value, line);
        textarea.focus();
        textarea.setSelectionRange(offset, offset);
        const lineHeight = parseFloat(getComputedStyle(textarea).lineHeight) || 20;
        box.scrollTop = Math.max(0, (line - 1) * lineHeight - box.clientHeight / 3);
      },
    };
    return () => {
      if (p.apiRef) p.apiRef.current = null;
    };
  }, [p.apiRef]);

  // replaces the selection as a user edit would, so undo keeps working
  function insert(text: string) {
    const textarea = input.current;
    if (!textarea) return;
    textarea.focus();
    let done = false;
    try {
      done = document.execCommand("insertText", false, text);
    } catch {
      done = false;
    }
    if (!done) {
      textarea.setRangeText(text, textarea.selectionStart, textarea.selectionEnd, "end");
      p.onChange(textarea.value);
    }
  }

  function onKeyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s") {
      e.preventDefault();
      p.onSave?.();
      return;
    }
    if (p.readOnly) return;
    const textarea = e.currentTarget;
    if (e.key === "Tab" && !e.shiftKey) {
      e.preventDefault();
      insert(indentUnit);
      return;
    }
    if (e.key === "Enter" && !e.shiftKey && !e.ctrlKey && !e.metaKey && !e.altKey) {
      const start = textarea.selectionStart;
      const lineStart = textarea.value.lastIndexOf("\n", start - 1) + 1;
      const indentation = /^[ \t]*/.exec(textarea.value.slice(lineStart, start))?.[0] ?? "";
      const before = textarea.value[start - 1];
      const after = textarea.value[start];
      const opensBlock = before === "{" || before === "[" || before === "(";
      const closesBlock = (before === "{" && after === "}") || (before === "[" && after === "]") || (before === "(" && after === ")");
      e.preventDefault();
      if (closesBlock) {
        // between a pair of brackets: the closing one moves to its own line at the outer depth
        insert("\n" + indentation + indentUnit + "\n" + indentation);
        const caret = start + 1 + indentation.length + indentUnit.length;
        textarea.setSelectionRange(caret, caret);
      } else {
        insert("\n" + indentation + (opensBlock ? indentUnit : ""));
      }
    }
  }

  const lines: number[] = [];
  for (let i = 1; i <= lineCount; i++) lines.push(i);

  return (
    <div className="code-editor" ref={scroller}>
      <div className="code-gutter" aria-hidden>
        {lines.map((line) => {
          const message = issueByLine.get(line);
          return (
            <div key={line} className={"code-ln" + (message ? " has-issue" : "")} title={message}>
              {line}
            </div>
          );
        })}
      </div>
      <div className="code-area">
        <pre className="code-pre" aria-hidden dangerouslySetInnerHTML={{ __html: html }} />
        <textarea
          ref={input}
          className="code-input"
          value={p.value}
          onChange={(e) => p.onChange(e.target.value)}
          onKeyDown={onKeyDown}
          readOnly={p.readOnly}
          spellCheck={false}
          wrap="off"
          autoCapitalize="off"
          autoCorrect="off"
          autoComplete="off"
        />
      </div>
    </div>
  );
}

function countLines(text: string): number {
  let count = 1;
  for (let i = 0; i < text.length; i++) if (text.charCodeAt(i) === 10) count++;
  return count;
}

function offsetOfLine(text: string, line: number): number {
  let offset = 0;
  for (let current = 1; current < line; current++) {
    const next = text.indexOf("\n", offset);
    if (next < 0) return text.length;
    offset = next + 1;
  }
  return offset;
}
