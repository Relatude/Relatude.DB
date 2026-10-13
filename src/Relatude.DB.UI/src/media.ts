import { useEffect, useSyncExternalStore } from "react";

/**
 * When the UI is laid out for a phone: the rail leaves the page and becomes a drawer behind the menu
 * button, the search box folds into its icon, the pages stack their columns and scroll as one. A
 * narrow window, or a touch screen too short for a page to fill - a phone turned on its side, which
 * is wide enough for the desktop layout and leaves it a strip a few rows high. The css says the same
 * in every `@media` rule for the phone layout; keep them together.
 */
export const phoneQuery = "(max-width: 760px), (max-height: 500px) and (pointer: coarse)";

/** Below this the rail starts collapsed to its icons: the page needs the width more than the labels. */
export const narrowQuery = "(max-width: 1024px)";

export function matchesMedia(query: string): boolean {
  return typeof window !== "undefined" && !!window.matchMedia?.(query).matches;
}

/** Whether a media query matches now, re-rendering when that changes (a rotated phone, a resized window). */
export function useMediaQuery(query: string): boolean {
  return useSyncExternalStore(
    (listener) => {
      const list = window.matchMedia?.(query);
      list?.addEventListener("change", listener);
      return () => list?.removeEventListener("change", listener);
    },
    () => matchesMedia(query),
  );
}

/** True while the UI is laid out for a phone (see phoneQuery). */
export const usePhone = () => useMediaQuery(phoneQuery);

/**
 * On a phone a switch of views is wider than the screen and scrolls sideways (app.css), so a page that
 * opens on its fourth view - remembered from last time, or asked for by the search - would open with
 * the lit button out of sight past the edge. Each switch is scrolled to its lit button once, when it
 * first appears; after that it is the reader's to scroll. Pages render their switches whenever their
 * data arrives, so the whole page is watched rather than one render of the shell.
 */
export function useActiveSwitchesInView(enabled: boolean): void {
  useEffect(() => {
    if (!enabled) return;
    const done = new WeakSet<Element>();
    let frame = 0;
    const run = () => {
      frame = 0;
      for (const strip of document.querySelectorAll(".module-switch, .query-view")) {
        if (done.has(strip)) continue;
        const active = strip.querySelector<HTMLElement>(".active");
        if (!active) continue;
        done.add(strip);
        if (strip.scrollWidth <= strip.clientWidth) continue;
        strip.scrollLeft = active.offsetLeft - (strip.clientWidth - active.offsetWidth) / 2;
      }
    };
    const observer = new MutationObserver(() => {
      if (!frame) frame = requestAnimationFrame(run);
    });
    observer.observe(document.body, { childList: true, subtree: true });
    run();
    return () => {
      observer.disconnect();
      cancelAnimationFrame(frame);
    };
  }, [enabled]);
}
