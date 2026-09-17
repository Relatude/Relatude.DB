import { useEffect, useRef, useState } from "react";
import type { SearchMatch } from "../server/query";

/**
 * The two things about a free text search that are not the text: how each word is matched, and
 * whether a hit has to hold all of the words or any one of them.
 *
 * It lives inside the search box as a single small button showing where both stand - "ab* all" -
 * because the toolbar it sits in is already full and these are settings somebody changes rarely and
 * wants to SEE always: a search running as fuzzy, or as any-word, explains a result that otherwise
 * looks wrong, and a setting nobody can see is one nobody will think to look at. The choices
 * themselves are in a popover, where there is room to say what each one does.
 */
const matches: { id: SearchMatch; label: string; chip: string; hint: string }[] = [
  { id: "wildcard", label: "Wildcard", chip: "ab*", hint: "Every word is a prefix: “cor” finds “cork”, and a half typed word already finds something." },
  { id: "fuzzy", label: "Fuzzy", chip: "ab~", hint: "A word may be misspelled: “kork” finds “cork”. Slower — the index looks up the near misses too." },
  { id: "exact", label: "Exact", chip: "ab", hint: "The word has to be in the text as written: “cor” finds “cor”, never “cork”." },
];

export function SearchOptions({
  match,
  anyWord,
  onChange,
}: {
  match: SearchMatch;
  anyWord: boolean;
  onChange: (change: { match?: SearchMatch; anyWord?: boolean }) => void;
}) {
  const [open, setOpen] = useState(false);
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const away = (e: MouseEvent) => {
      if (!box.current?.contains(e.target as Node)) setOpen(false);
    };
    const key = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    window.addEventListener("mousedown", away);
    window.addEventListener("keydown", key);
    return () => {
      window.removeEventListener("mousedown", away);
      window.removeEventListener("keydown", key);
    };
  }, [open]);

  const current = matches.find((m) => m.id === match) ?? matches[0];
  // a search running on anything but the plain prefix-and-all-words is marked in the section's
  // colour as well as said in words: it explains a result that otherwise looks wrong, and a setting
  // nobody notices is one nobody will think to look at (the semantic button is lit for the same reason)
  const set = match !== "wildcard" || anyWord;
  return (
    <div className="search-options" ref={box}>
      <button
        className={"search-options-button" + (set ? " set" : "") + (open ? " active" : "")}
        aria-expanded={open}
        title={`${current.label} match, ${anyWord ? "any word" : "all words"} — click to change`}
        onClick={() => setOpen((o) => !o)}
      >
        <span className="so-chip">{current.chip}</span>
        <span className="so-words">{anyWord ? "any" : "all"}</span>
      </button>
      {open && (
        <div className="search-options-menu">
          <div className="so-row">
            <span className="so-label">Match</span>
            <div className="module-switch compact">
              {matches.map((m) => (
                <button key={m.id} className={m.id === match ? "active" : ""} title={m.hint} onClick={() => onChange({ match: m.id })}>
                  {m.label}
                </button>
              ))}
            </div>
          </div>
          <div className="so-hint">{current.hint}</div>
          <div className="so-row">
            <span className="so-label">Words</span>
            <div className="module-switch compact">
              <button className={anyWord ? "" : "active"} onClick={() => onChange({ anyWord: false })}>
                All
              </button>
              <button className={anyWord ? "active" : ""} onClick={() => onChange({ anyWord: true })}>
                Any
              </button>
            </div>
          </div>
          <div className="so-hint">
            {anyWord ? "A node holding any one of the words is a hit." : "Every word has to be there, wherever in the node it is."}
          </div>
          <div className="so-note">Typing “word*” or “word~” yourself still means just that, whatever is set here.</div>
        </div>
      )}
    </div>
  );
}
