/**
 * Drag to select: a rectangle drawn round the nodes on screen, in every view that has nodes on it -
 * the list and the table of hits, the cards of the visual pivot (flat or solid) and the points of
 * the map. It is a mode, switched on in the result's head, because in the pictures a plain drag
 * already means something (pan, or turn the solids); with it on, the left button draws the
 * rectangle and the other buttons move the picture. A press that goes nowhere is still a click,
 * and does what a click always did.
 *
 * What the rectangle takes is whatever it touches, however little: a row with one line inside it,
 * a card with one pixel showing. On its own the rectangle becomes the selection - an empty one
 * clears it - and with shift or ctrl held it is added, where a node caught that was already
 * selected is let go of again (applyMarquee in selection.ts). What is here is the part every view
 * shares: the geometry, the box drawn over the view with its count, the gesture over a scrolling
 * list.
 */
import { useEffect, useRef, useState, type ReactNode, type RefObject } from "react";
import { formatCount } from "./format";

/** A rectangle with x0 <= x1 and y0 <= y1, in whatever coordinates the caller works in. */
export interface Rect {
  x0: number;
  y0: number;
  x1: number;
  y1: number;
}

export function rectOf(ax: number, ay: number, bx: number, by: number): Rect {
  return { x0: Math.min(ax, bx), y0: Math.min(ay, by), x1: Math.max(ax, bx), y1: Math.max(ay, by) };
}

/** How far the pointer moves before a press is a rectangle rather than a click, in css pixels. */
export const marqueeThreshold = 4;

/*
 * Nothing here asks before a rectangle is let go of, whatever it caught. It used to, past a hundred
 * thousand, back when selecting meant looking every node up by guid and reading a sample of them
 * into the form. It does not mean that any more: a selection is the internal ids the view already
 * has, and a selection of several nodes opens on a summary that reads nothing at all. So there is
 * nothing to warn about at this point - selecting two million cards is a rectangle and no round
 * trips - and the question belongs where the work is, which is Edit combined in the form (see
 * askAboveNodes in NodeEditor.tsx); Delete asks there too.
 */

/** Whether the keys held mean "add to what is selected" rather than "replace it". */
export function keepOf(e: { shiftKey: boolean; ctrlKey: boolean; metaKey: boolean }): boolean {
  return e.shiftKey || e.ctrlKey || e.metaKey;
}

/** Escape lets go of a rectangle being drawn; what comes back removes the listener. */
export function untilEscape(cancel: () => void): () => void {
  const onKey = (e: KeyboardEvent) => {
    if (e.key !== "Escape") return;
    e.stopPropagation();
    cancel();
  };
  window.addEventListener("keydown", onKey, true);
  return () => window.removeEventListener("keydown", onKey, true);
}

/** The rectangle as it is drawn over a view, with how many nodes it holds right now. */
export function MarqueeBox({ rect, count }: { rect: Rect; count: number }) {
  return (
    <div className="marquee-box" style={{ left: rect.x0, top: rect.y0, width: Math.max(0, rect.x1 - rect.x0), height: Math.max(0, rect.y1 - rect.y0) }}>
      <span className="marquee-count">
        {formatCount(count)} {count === 1 ? "node" : "nodes"}
      </span>
    </div>
  );
}

// ---- the lists ----

/**
 * The rows of a list or a table - the elements carrying a data-node-id - that lie within a rectangle
 * of the screen (client coordinates), in the order they are on screen. The rows stand one under
 * another, so the first one reaching down into the rectangle is found by halving and the rest are
 * read off from there: a page of a hundred thousand rows is answered in the few it holds.
 */
export function rowsInRect(host: HTMLElement, rect: Rect): HTMLElement[] {
  const rows = host.querySelectorAll<HTMLElement>("[data-node-id]");
  const n = rows.length;
  if (n === 0) return [];
  let lo = 0;
  let hi = n;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (rows[mid].getBoundingClientRect().bottom <= rect.y0) lo = mid + 1;
    else hi = mid;
  }
  const out: HTMLElement[] = [];
  for (let i = lo; i < n; i++) {
    const b = rows[i].getBoundingClientRect();
    if (b.top >= rect.y1) break;
    if (b.right > rect.x0 && b.left < rect.x1) out.push(rows[i]);
  }
  return out;
}

/** A rectangle being drawn over a list. */
interface ListGesture {
  /** the element that scrolls: the rectangle is drawn over its rows */
  scroller: HTMLElement;
  /** the corner the rectangle grows from, in the scroller's content coordinates: it keeps its place in the rows as they scroll */
  ax: number;
  ay: number;
  /** where the pointer is, on screen */
  x: number;
  y: number;
  keep: boolean;
  moved: boolean;
  /** the rows marked as inside right now */
  marked: Set<HTMLElement>;
  /** the frame the list is being scrolled on, while the pointer is held past its edge */
  raf: number;
  stop: () => void;
}

/** how far the list scrolls per frame with the pointer held past its edge: a share of the overshoot, within limits */
const scrollShare = 0.2;
const minScroll = 2;
const maxScroll = 40;

/**
 * Drag to select over the hits, as a list or as a table (see QuerySection). Put `onPointerDown` and
 * `onClickCapture` on the results panel and render `box` inside it: a press on a row or between the
 * rows that then moves becomes a rectangle, and the rows it touches are marked as it grows. Held
 * past the edge of the list the pointer scrolls it, and the rectangle grows with the rows it
 * reaches - its first corner keeps its place among the rows, not on the screen. Letting go hands
 * the rows' node ids to `onSelect`; a press that never moved is left alone and is the click it
 * always was, while the click the browser makes of a drag is swallowed on its way to the row.
 */
export function useListMarquee({ enabled, host, onSelect }: { enabled: boolean; host: RefObject<HTMLElement | null>; onSelect: (ids: string[], keep: boolean) => void }): {
  onPointerDown: (e: React.PointerEvent) => void;
  onClickCapture: (e: React.MouseEvent) => void;
  box: ReactNode;
} {
  const [shown, setShown] = useState<{ rect: Rect; count: number } | null>(null);
  const gesture = useRef<ListGesture | null>(null);
  /** the click that follows a rectangle is not a click on the row it happens to end over */
  const swallow = useRef(false);
  const enabledRef = useRef(enabled);
  enabledRef.current = enabled;
  const onSelectRef = useRef(onSelect);
  onSelectRef.current = onSelect;

  // the gesture ends with the mode being switched off under it, and with the component
  useEffect(() => {
    if (!enabled) finish(false);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- reads the latest through refs
  }, [enabled]);
  useEffect(
    () => () => finish(false),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [],
  );

  function visibleBox(s: HTMLElement): Rect {
    const b = s.getBoundingClientRect();
    return { x0: b.left, y0: b.top, x1: b.left + s.clientWidth, y1: b.top + s.clientHeight };
  }

  /** the rectangle on screen right now: its first corner has scrolled with the rows */
  function rectNow(g: ListGesture): Rect {
    return rectOf(g.ax - g.scroller.scrollLeft, g.ay - g.scroller.scrollTop, g.x, g.y);
  }

  /** how far past the list's edge the pointer is held, or null while it is inside */
  function overshoot(g: ListGesture, v: Rect): [number, number] | null {
    const dx = g.x < v.x0 ? g.x - v.x0 : g.x > v.x1 ? g.x - v.x1 : 0;
    const dy = g.y < v.y0 ? g.y - v.y0 : g.y > v.y1 ? g.y - v.y1 : 0;
    return dx === 0 && dy === 0 ? null : [dx, dy];
  }

  function update(g: ListGesture) {
    const rect = rectNow(g);
    const rows = rowsInRect(g.scroller, rect);
    // marked straight on the elements rather than through React: a render of a thousand rows on
    // every move of the pointer is not what the rows are for, and nothing else writes this class
    const now = new Set(rows);
    for (const el of g.marked) if (!now.has(el)) el.classList.remove("marquee-hit");
    for (const el of rows) if (!g.marked.has(el)) el.classList.add("marquee-hit");
    g.marked = now;
    const v = visibleBox(g.scroller);
    const h = host.current?.getBoundingClientRect();
    // drawn in the panel's coordinates and clipped to the list's own window, so it never lies over the head
    if (h) setShown({ rect: { x0: Math.max(rect.x0, v.x0) - h.left, y0: Math.max(rect.y0, v.y0) - h.top, x1: Math.min(rect.x1, v.x1) - h.left, y1: Math.min(rect.y1, v.y1) - h.top }, count: rows.length });
    if (g.raf === 0 && overshoot(g, v) !== null) g.raf = requestAnimationFrame(tick);
  }

  /** the pointer held past the edge: the list scrolls toward it, faster the further past it is */
  function tick() {
    const g = gesture.current;
    if (!g) return;
    g.raf = 0;
    const over = overshoot(g, visibleBox(g.scroller));
    if (over === null) return;
    const step = (d: number) => (d === 0 ? 0 : Math.sign(d) * Math.min(maxScroll, Math.max(minScroll, Math.abs(d) * scrollShare)));
    g.scroller.scrollLeft += step(over[0]);
    g.scroller.scrollTop += step(over[1]);
    update(g); // which asks for the next frame while the pointer is still outside
  }

  function onMove(ev: PointerEvent, pointerId: number) {
    const g = gesture.current;
    if (!g) return;
    g.x = ev.clientX;
    g.y = ev.clientY;
    if (!g.moved) {
      const s = g.scroller;
      if (Math.hypot(ev.clientX - (g.ax - s.scrollLeft), ev.clientY - (g.ay - s.scrollTop)) < marqueeThreshold) return;
      g.moved = true;
      // a rectangle it is: from here the pointer is the list's wherever it goes, and the keys too
      try {
        s.setPointerCapture(pointerId);
      } catch {
        // the pointer is gone; the window still hears whatever is left of it
      }
      s.focus({ preventScroll: true });
    }
    update(g);
  }

  function finish(commit: boolean) {
    const g = gesture.current;
    if (!g) return;
    gesture.current = null;
    g.stop();
    if (g.raf !== 0) cancelAnimationFrame(g.raf);
    for (const el of g.marked) el.classList.remove("marquee-hit");
    setShown(null);
    if (!g.moved) return; // a click, which goes on to the row as it always did
    // the click the browser makes of this press and release is not a click on a row; it is dispatched
    // right after the release, so the flag is dropped again once that has had its chance
    swallow.current = true;
    window.setTimeout(() => (swallow.current = false), 0);
    if (!commit) return;
    const ids: string[] = [];
    for (const el of rowsInRect(g.scroller, rectNow(g))) {
      const id = el.dataset.nodeId;
      if (id) ids.push(id);
    }
    onSelectRef.current(ids, g.keep);
  }

  function onPointerDown(e: React.PointerEvent) {
    if (!enabledRef.current || e.button !== 0 || gesture.current !== null) return;
    const target = e.target as Element;
    // a press on a control is the control's - a row is a button too, and is not one
    if (target.closest("input, select, textarea, a, .icon-button, .link-button")) return;
    const scroller = target.closest<HTMLElement>(".query-hits, .query-table-wrap");
    if (!scroller) return;
    const v = visibleBox(scroller);
    if (e.clientX > v.x1 || e.clientY > v.y1) return; // a press on the scrollbar is a scroll
    const g: ListGesture = {
      scroller,
      ax: e.clientX + scroller.scrollLeft,
      ay: e.clientY + scroller.scrollTop,
      x: e.clientX,
      y: e.clientY,
      keep: keepOf(e),
      moved: false,
      marked: new Set(),
      raf: 0,
      stop: () => {},
    };
    const pointerId = e.pointerId;
    const move = (ev: PointerEvent) => onMove(ev, pointerId);
    // a cancelled pointer ends the rectangle where the last move left it rather than dropping it:
    // the end point is the gesture's own, never the event's (a cancel arrives at 0,0)
    const up = () => finish(true);
    const escape = untilEscape(() => finish(false));
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
    window.addEventListener("pointercancel", up);
    g.stop = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
      window.removeEventListener("pointercancel", up);
      escape();
    };
    gesture.current = g;
  }

  function onClickCapture(e: React.MouseEvent) {
    if (!swallow.current) return;
    swallow.current = false;
    e.stopPropagation();
    e.preventDefault();
  }

  return { onPointerDown, onClickCapture, box: shown ? <MarqueeBox rect={shown.rect} count={shown.count} /> : null };
}
