import { useEffect, useMemo, useRef, useState, type ChangeEvent, type KeyboardEvent, type RefObject } from "react";
import { createPortal } from "react-dom";
import { escapeHtml, highlight } from "../code/highlight";
import type { Language } from "../code/language";
import type { LintIssue } from "../code/lint";

// A small code editor with no dependencies: a transparent textarea over a <pre> holding the
// syntax coloured copy of the same text, in the same font and metrics, so the caret and the
// selection belong to the textarea while the colours come from the pre. A gutter to the left
// carries the line numbers and marks the lines the linter has something to say about. Tab
// indents, Enter keeps the indentation, Ctrl+S saves. Given a completion source, it offers what
// can be typed at the caret in a list under it (Ctrl+Space, or while typing a name).

const highlightLimit = 400_000; // above this the colouring is skipped: the tokenizer is not free
const indentUnit = "  ";

export interface CodeEditorApi {
  goToLine(line: number): void;
  /** puts the caret at an offset (and scrolls to it) */
  setCursor(offset: number): void;
  /** opens the completion list at the caret */
  openCompletion(): void;
  focus(): void;
}

export interface EditorCompletion {
  label: string;
  kind: string;
  detail?: string;
  doc?: string | null;
  /** the text put in; "|" marks where the caret goes */
  insert?: string;
  /** opens the list again after inserting */
  reopen?: boolean;
}

export interface EditorCompletions {
  from: number;
  to: number;
  items: EditorCompletion[];
}

interface Popup extends EditorCompletions {
  index: number;
  x: number;
  y: number;
  above: boolean;
}

export function CodeEditor(p: {
  value: string;
  onChange: (value: string) => void;
  language: Language;
  issues: LintIssue[];
  readOnly?: boolean;
  onSave?: () => void;
  /** Ctrl+Enter */
  onRun?: () => void;
  /** Shift+Alt+F */
  onFormat?: () => void;
  /** the caret's offset, whenever it moves */
  onCursor?: (offset: number) => void;
  complete?: (text: string, offset: number) => EditorCompletions | null;
  apiRef?: RefObject<CodeEditorApi | null>;
}) {
  const scroller = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLTextAreaElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const [popup, setPopup] = useState<Popup | null>(null);
  const popupRef = useRef<Popup | null>(null);
  popupRef.current = popup;
  const completeRef = useRef(p.complete);
  completeRef.current = p.complete;
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
      setCursor(offset) {
        const textarea = input.current;
        const box = scroller.current;
        if (!textarea || !box) return;
        textarea.focus();
        textarea.setSelectionRange(offset, offset);
        const lineHeight = parseFloat(getComputedStyle(textarea).lineHeight) || 20;
        const line = countLines(textarea.value.slice(0, offset));
        if ((line - 1) * lineHeight < box.scrollTop || line * lineHeight > box.scrollTop + box.clientHeight) box.scrollTop = Math.max(0, (line - 1) * lineHeight - box.clientHeight / 3);
        reportCursor();
      },
      openCompletion() {
        input.current?.focus();
        openCompletion();
      },
      focus() {
        input.current?.focus();
      },
    };
    return () => {
      if (p.apiRef) p.apiRef.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [p.apiRef]);

  // the list follows the caret: it goes when the text scrolls or the window changes
  useEffect(() => {
    if (!popup) return;
    const close = () => setPopup(null);
    const box = scroller.current;
    box?.addEventListener("scroll", close);
    window.addEventListener("resize", close);
    return () => {
      box?.removeEventListener("scroll", close);
      window.removeEventListener("resize", close);
    };
  }, [popup]);

  // keeps the chosen entry in view
  useEffect(() => {
    if (!popup) return;
    const row = listRef.current?.children[popup.index] as HTMLElement | undefined;
    row?.scrollIntoView({ block: "nearest" });
  }, [popup]);

  function reportCursor() {
    const textarea = input.current;
    if (textarea && p.onCursor) p.onCursor(textarea.selectionStart);
  }

  function openCompletion(onlyWhenTyping = false) {
    const textarea = input.current;
    const source = completeRef.current;
    if (!textarea || !source || p.readOnly) return;
    const offset = textarea.selectionStart;
    if (textarea.selectionEnd !== offset) return setPopup(null);
    const result = source(textarea.value, offset);
    if (!result || result.items.length === 0) return setPopup(null);
    if (onlyWhenTyping && result.from === offset) return setPopup(null);
    const place = caretPlace(textarea, result.from);
    const previous = popupRef.current;
    // keep the chosen entry while the list narrows, if it is still there
    const keep = previous ? result.items.findIndex((i) => i.label === previous.items[previous.index]?.label) : -1;
    setPopup({ ...result, index: keep >= 0 ? keep : 0, ...place });
  }

  function accept(item: EditorCompletion) {
    const textarea = input.current;
    const current = popupRef.current;
    if (!textarea || !current) return;
    setPopup(null);
    const lineStart = textarea.value.lastIndexOf("\n", current.from - 1) + 1;
    const indentation = /^[ \t]*/.exec(textarea.value.slice(lineStart, current.from))?.[0] ?? "";
    const raw = (item.insert ?? item.label).replace(/\n/g, "\n" + indentation);
    const caret = raw.indexOf("|");
    const text = caret >= 0 ? raw.slice(0, caret) + raw.slice(caret + 1) : raw;
    textarea.focus();
    textarea.setSelectionRange(current.from, current.to);
    insert(text);
    const at = current.from + (caret >= 0 ? caret : text.length);
    textarea.setSelectionRange(at, at);
    reportCursor();
    if (item.reopen) window.setTimeout(() => openCompletion(), 0);
  }

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
    const open = popupRef.current;
    if (open) {
      const n = open.items.length;
      if (e.key === "ArrowDown" || e.key === "ArrowUp" || e.key === "PageDown" || e.key === "PageUp") {
        e.preventDefault();
        const step = e.key === "ArrowDown" ? 1 : e.key === "ArrowUp" ? -1 : e.key === "PageDown" ? 8 : -8;
        const index = Math.abs(step) === 1 ? (open.index + step + n) % n : Math.max(0, Math.min(n - 1, open.index + step));
        setPopup({ ...open, index });
        return;
      }
      if (e.key === "Enter" || e.key === "Tab") {
        e.preventDefault();
        accept(open.items[open.index]);
        return;
      }
      if (e.key === "Escape") {
        e.preventDefault();
        setPopup(null);
        return;
      }
    }
    if ((e.ctrlKey || e.metaKey) && e.key === " ") {
      e.preventDefault();
      openCompletion();
      return;
    }
    if ((e.ctrlKey || e.metaKey) && e.key === "Enter" && p.onRun) {
      e.preventDefault();
      setPopup(null);
      p.onRun();
      return;
    }
    if (e.shiftKey && e.altKey && e.key.toLowerCase() === "f" && p.onFormat) {
      e.preventDefault();
      p.onFormat();
      return;
    }
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

  function onInput(e: ChangeEvent<HTMLTextAreaElement>) {
    p.onChange(e.target.value);
    if (!completeRef.current) return;
    const native = e.nativeEvent as InputEvent;
    const typed = native.inputType === "insertText" && typeof native.data === "string" && /^[A-Za-z0-9_$@]$/.test(native.data);
    if (popupRef.current || typed) {
      // the list is worked out from the text as it is now, after this keystroke
      window.setTimeout(() => openCompletion(!popupRef.current), 0);
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
          onChange={onInput}
          onKeyDown={onKeyDown}
          onKeyUp={(e) => {
            if (e.key.startsWith("Arrow") || e.key === "Home" || e.key === "End") {
              reportCursor();
              if (popupRef.current && (e.key === "ArrowLeft" || e.key === "ArrowRight" || e.key === "Home" || e.key === "End")) openCompletion();
            }
          }}
          onClick={() => {
            reportCursor();
            setPopup(null);
          }}
          onSelect={reportCursor}
          onBlur={() => setPopup(null)}
          readOnly={p.readOnly}
          spellCheck={false}
          wrap="off"
          autoCapitalize="off"
          autoCorrect="off"
          autoComplete="off"
        />
      </div>
      {popup &&
        createPortal(
          <div
            className={"code-complete" + (popup.above ? " above" : "")}
            style={popup.above ? { left: popup.x, bottom: popup.y } : { left: popup.x, top: popup.y }}
            onMouseDown={(e) => e.preventDefault() /* the textarea keeps the focus */}
          >
            <div className="code-complete-list" ref={listRef} role="listbox">
              {popup.items.map((item, i) => (
                <div
                  key={item.kind + item.label + i}
                  role="option"
                  aria-selected={i === popup.index}
                  className={"code-complete-item" + (i === popup.index ? " active" : "")}
                  onMouseEnter={() => setPopup({ ...popup, index: i })}
                  onClick={() => accept(item)}
                >
                  <span className={"code-complete-kind kind-" + item.kind}>{kindLetter(item.kind)}</span>
                  <span className="code-complete-label">{item.label}</span>
                  {item.detail && <span className="code-complete-detail">{item.detail}</span>}
                </div>
              ))}
            </div>
            {popup.items[popup.index]?.doc && <div className="code-complete-doc">{popup.items[popup.index].doc}</div>}
          </div>,
          document.body,
        )}
    </div>
  );
}

function kindLetter(kind: string): string {
  switch (kind) {
    case "field":
      return "f";
    case "argument":
      return "a";
    case "type":
      return "T";
    case "enum":
      return "e";
    case "variable":
      return "$";
    case "fragment":
      return "…";
    case "directive":
      return "@";
    case "keyword":
      return "k";
    default:
      return "v";
  }
}

const listHeight = 300;

/** Where the list goes: under the character at the offset, or above it when there is no room below. */
function caretPlace(textarea: HTMLTextAreaElement, offset: number): { x: number; y: number; above: boolean } {
  const style = getComputedStyle(textarea);
  const lineHeight = parseFloat(style.lineHeight) || 20;
  const padLeft = parseFloat(style.paddingLeft) || 0;
  const padTop = parseFloat(style.paddingTop) || 0;
  const canvas = document.createElement("canvas").getContext("2d");
  let charWidth = 7.5;
  if (canvas) {
    canvas.font = `${style.fontStyle} ${style.fontWeight} ${style.fontSize} ${style.fontFamily}`;
    charWidth = canvas.measureText("0000000000").width / 10;
  }
  const text = textarea.value;
  const lineStart = text.lastIndexOf("\n", offset - 1) + 1;
  const line = countLines(text.slice(0, offset)) - 1;
  let column = 0;
  for (let i = lineStart; i < offset; i++) column += text[i] === "\t" ? 2 - (column % 2) : 1;
  const rect = textarea.getBoundingClientRect();
  const x = Math.min(rect.left + padLeft + column * charWidth, window.innerWidth - 340);
  const below = rect.top + padTop + (line + 1) * lineHeight + 2;
  if (below + listHeight > window.innerHeight && rect.top + padTop + line * lineHeight > listHeight) {
    return { x, y: window.innerHeight - (rect.top + padTop + line * lineHeight - 2), above: true };
  }
  return { x, y: below, above: false };
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
