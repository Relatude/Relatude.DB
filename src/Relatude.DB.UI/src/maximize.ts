import { useCallback, useEffect, useState } from "react";

/**
 * A view filling the browser window - not the screen, which is what fullscreen does: whether it
 * does, and the switch. Escape puts it back, unless the Escape is somebody else's: a dialog open
 * over the view (a preview opened from the form beside a map), a field being typed in, or a handler
 * that has used it already - a rectangle being drawn cancels on Escape and stops it there.
 *
 * Who it belongs to is decided in the capture phase, before anything has handled it: a dialog that
 * closes on Escape is gone by the time the key has bubbled back up, and would no longer say so.
 */
export function useMaximized(): [boolean, () => void, (on: boolean) => void] {
  const [maximized, setMaximized] = useState(false);
  useEffect(() => {
    if (!maximized) return;
    let ours = false;
    const look = (e: KeyboardEvent) => {
      ours = e.key === "Escape" && !belongsElsewhere(e);
    };
    const act = (e: KeyboardEvent) => {
      if (ours && !e.defaultPrevented) setMaximized(false);
      ours = false;
    };
    window.addEventListener("keydown", look, true);
    window.addEventListener("keydown", act);
    return () => {
      window.removeEventListener("keydown", look, true);
      window.removeEventListener("keydown", act);
    };
  }, [maximized]);
  const toggle = useCallback(() => setMaximized((m) => !m), []);
  return [maximized, toggle, setMaximized];
}

function belongsElsewhere(e: KeyboardEvent): boolean {
  const target = e.target instanceof HTMLElement ? e.target : null;
  if (target && (target.isContentEditable || target.closest("input, textarea, select") !== null)) return true;
  return document.querySelector(".dialog-backdrop") !== null;
}
