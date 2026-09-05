import { readColor, sourcePalette } from "../server/datamodel";

/**
 * Picks a colour, or leaves it to whatever chose one before there was anything to set: the palette,
 * the theme, the position in a list. That "unset" state is the point of the control - a plain colour
 * input has no way to say "none", so it would quietly turn every automatic colour into a fixed one
 * the moment anyone opened it.
 *
 * Three ways in, because they suit different intentions: the palette for "one of the usual ones",
 * the well for "this exact colour", the text field for a value that is pasted or typed (a hex code
 * or a CSS colour name). Clearing the text field is what puts it back to automatic.
 */
export function ColorField({
  value,
  fallback,
  disabled,
  onChange,
}: {
  value: string | null | undefined;
  /** the colour in force while nothing is set, shown so the swatch is never blank */
  fallback?: string;
  disabled?: boolean;
  onChange: (value: string | null) => void;
}) {
  const set = readColor(value);
  const shown = set ?? fallback ?? "#8a8781";
  // the native well only understands #rrggbb: a short hex is widened, and a colour name has none to
  // give, so it opens on the colour in force rather than on black
  const well = hex6(set) ?? hex6(fallback) ?? "#8a8781";
  return (
    <span className={"color-field" + (disabled ? " off" : "")}>
      <span className="color-current" style={{ background: shown }} title={set ? shown : "Automatic: " + shown}>
        <input type="color" value={well} disabled={disabled} onChange={(e) => onChange(e.target.value)} aria-label="Pick a colour" />
        {!set && <span className="color-auto-mark" />}
      </span>
      <input
        className="text-input color-text"
        type="text"
        value={value ?? ""}
        placeholder="automatic"
        spellCheck={false}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value.trim() === "" ? null : e.target.value)}
      />
      <span className="color-swatches">
        {sourcePalette.map((c) => (
          <button
            key={c}
            type="button"
            className={"color-swatch" + (set === c ? " on" : "")}
            style={{ background: c }}
            title={c}
            disabled={disabled}
            onClick={() => onChange(c)}
          />
        ))}
        <button
          type="button"
          className={"color-swatch auto" + (set ? "" : " on")}
          title="Automatic"
          disabled={disabled}
          onClick={() => onChange(null)}
        />
      </span>
    </span>
  );
}

/** A colour as #rrggbb, for the native colour input; null for anything that is not a hex colour. */
function hex6(value: string | null | undefined): string | null {
  const c = readColor(value);
  if (c == null || !c.startsWith("#")) return null;
  if (c.length === 7) return c;
  return "#" + c[1] + c[1] + c[2] + c[2] + c[3] + c[3];
}
