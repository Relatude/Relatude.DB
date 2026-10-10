import { useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { IconChevronDown } from "@tabler/icons-react";
import type { SettingChoice } from "../server/settings";

/** A list fetched when a combo opens rather than known up front, such as the models an AI service publishes. */
export type PickerLoader = () => Promise<SettingChoice[]>;

function asText(value: unknown): string {
  return value === null || value === undefined ? "" : String(value);
}

/**
 * A text field that knows the values it usually holds: typing works exactly as it did before, and
 * the arrow opens the known ones. It is not a drop-down with an "other..." entry, because the
 * setting genuinely is free text - the list is a shortcut and a spelling reference, so nothing here
 * ever refuses a value or rewrites one.
 *
 * Typing narrows the list to what matches, by value or label, and a value that matches nothing simply leaves it empty
 * rather than closing the list on a keystroke; the arrow always shows everything.
 *
 * With `load` the list is not known up front: it is fetched each time the list opens, and what was
 * fetched last is shown while the next answer is on its way. The list then opens even with nothing
 * in it, to say it is loading, that the service offers nothing, or why it could not be asked.
 */
export function Combo({
  label,
  placeholder,
  options,
  load,
  value,
  disabled,
  onChange,
}: {
  /** what the field is, for the arrow's title and screen readers */
  label: string;
  placeholder?: string | null;
  options: SettingChoice[];
  load?: PickerLoader;
  value: unknown;
  disabled: boolean;
  onChange: (value: unknown) => void;
}) {
  const [open, setOpen] = useState(false);
  // set while typing, so the list narrows to what is being typed but reopens whole from the arrow
  const [filtering, setFiltering] = useState(false);
  const [active, setActive] = useState(-1);
  const [fetched, setFetched] = useState<SettingChoice[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  // only the answer to the latest opening counts; an earlier one arriving late is dropped
  const request = useRef(0);
  const input = useRef<HTMLInputElement>(null);
  const current = asText(value);
  const all = load ? (fetched ?? []) : options;
  // what is typed is matched against the label too, so a language can be found by its name as well as its code
  const typed = current.toLowerCase();
  const matches =
    filtering && current ? all.filter((o) => o.value.toLowerCase().includes(typed) || o.label.toLowerCase().includes(typed)) : all;

  // a list with nothing in it is not shown at all, so "open" on its own is not the state anything
  // else should key off: a typed value matching no suggestion must still open the whole list. A
  // fetched list is the exception, since it has something to say even when it is empty.
  const visible = open && (matches.length > 0 || load !== undefined);
  const fetchList = () => {
    if (!load) return;
    const n = ++request.current;
    setLoading(true);
    setLoadError(null);
    load()
      .then((list) => {
        if (n !== request.current) return;
        setFetched(list);
        setLoading(false);
      })
      .catch((e: unknown) => {
        if (n !== request.current) return;
        setLoadError(e instanceof Error ? e.message : String(e));
        setLoading(false);
      });
  };
  const show = (filtered: boolean) => {
    // asked once per opening, not on every keystroke typed while it is open
    if (!open) fetchList();
    setFiltering(filtered);
    setActive(-1);
    setOpen(true);
    input.current?.focus(); // opening from the arrow still leaves the caret where typing works
  };
  const pick = (choice: string) => {
    onChange(choice);
    setOpen(false);
    input.current?.focus();
  };

  function onKeyDown(e: ReactKeyboardEvent) {
    if (e.key === "Escape") {
      setOpen(false);
      return;
    }
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      if (!visible) return show(false);
      const step = e.key === "ArrowDown" ? 1 : -1;
      setActive((i) => (i < 0 ? (step > 0 ? 0 : matches.length - 1) : (i + step + matches.length) % matches.length));
      return;
    }
    if (e.key === "Enter" && visible && active >= 0 && active < matches.length) {
      e.preventDefault();
      pick(matches[active].value);
    }
  }

  return (
    <div
      className="setting-combo"
      // closing on blur rather than behind a backdrop: a click straight into the next field should
      // land there, not be spent dismissing this list
      onBlur={(e) => {
        if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setOpen(false);
      }}
    >
      <input
        ref={input}
        className="text-input"
        value={current}
        placeholder={placeholder ?? ""}
        disabled={disabled}
        spellCheck={false}
        autoComplete="off"
        role="combobox"
        aria-expanded={visible}
        onChange={(e) => {
          onChange(e.target.value);
          show(true);
        }}
        onKeyDown={onKeyDown}
      />
      <button
        type="button"
        className="setting-combo-toggle"
        tabIndex={-1}
        disabled={disabled}
        title={"Known values for " + label}
        aria-label={"Known values for " + label}
        onClick={() => (visible ? setOpen(false) : show(false))}
      >
        <IconChevronDown size={14} />
      </button>
      {visible && (
        <div className="setting-combo-list">
          {load && loading && <div className="setting-combo-status">Loading the list…</div>}
          {load && !loading && loadError && <div className="setting-combo-status error">Could not load the list: {loadError}</div>}
          {load && !loading && !loadError && fetched?.length === 0 && <div className="setting-combo-status">The service offers none.</div>}
          {load && !loading && !loadError && all.length > 0 && matches.length === 0 && (
            <div className="setting-combo-status">None match; the value is saved as typed.</div>
          )}
          {matches.map((o, i) => (
            <button
              type="button"
              key={o.value}
              className={
                "setting-combo-option" +
                (i === active ? " active" : "") +
                (o.value.toLowerCase() === current.toLowerCase() ? " current" : "")
              }
              onMouseEnter={() => setActive(i)}
              onClick={() => pick(o.value)}
            >
              <span>{o.label}</span>
              {o.hint && <span className="hint">{o.hint}</span>}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
