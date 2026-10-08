import { useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type KeyboardEvent, type PointerEvent, type ReactNode } from "react";
import { IconMinus, IconPlus, IconSquareHalf, IconZoomReset, IconZoomScan } from "@tabler/icons-react";

/** Where the before picture lies on the after one, in the after's pixels: how far in from each edge (less than 0 is beyond it). */
export interface ImageInset {
  top: number;
  right: number;
  bottom: number;
  left: number;
}

const zoomSteps = [0.05, 0.1, 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4, 6, 8, 12, 16];
const minZoom = zoomSteps[0];
/** the most the picture is drawn at, on its longest side, however far in it is zoomed */
const maxFramePx = 32_000;
/** the room kept around a fitted picture */
const stagePad = 12;
/** from this zoom on, pixels are drawn as squares, so detail an upscale added or lost can be told apart */
const pixelatedFrom = 2;

/**
 * A picture to look at closely: fitted to the stage at first, zoomed with the buttons, the wheel (or a pinch) and a
 * double click, and moved by dragging when it is larger than the stage. With a `before`, a vertical split lies over
 * the picture: the before to the left of it, the after - `src` - to the right, and dragging moves it left and right.
 * The before is drawn in the after's box (or, with `beforeInset`, in the part of it it became), keeping its own
 * shape, so an upscale or a cutout lines up with what it was made from. `children` are drawn on the picture, placed
 * in percent of its size, and zoom with it. `lead` goes first on the bar, before the viewer's own buttons: a switch
 * between pictures, say, so the stage keeps one bar. `above` goes between the bar and the picture.
 */
export function ImageViewer({
  src,
  alt,
  before,
  beforeInset,
  lead,
  above,
  children,
}: {
  src: string;
  alt: string;
  before?: string | null;
  beforeInset?: ImageInset | null;
  /** first on the bar, before the viewer's own buttons */
  lead?: ReactNode;
  /** between the bar and the picture: what the picture is, said before it is looked at */
  above?: ReactNode;
  /** drawn on the picture once its size is known, which they are given in its own pixels */
  children?: (size: { w: number; h: number }) => ReactNode;
}) {
  const stage = useRef<HTMLDivElement>(null);
  const frame = useRef<HTMLDivElement>(null);
  // the size is kept with the picture it was read from, since a picture already at hand loads before any effect runs
  const [loaded, setLoaded] = useState<{ src: string; w: number; h: number } | null>(null);
  const natural = loaded?.src === src ? loaded : null;
  const [room, setRoom] = useState({ w: 0, h: 0 });
  const [zoom, setZoom] = useState<number | "fit">("fit");
  const [split, setSplit] = useState(50);
  const [compare, setCompare] = useState(true);
  const [drag, setDrag] = useState<"split" | "pan" | null>(null);
  // the point of the picture that stays under the pointer (or the stage's middle) while the zoom changes
  const anchor = useRef<{ x: number; y: number; at: { x: number; y: number } } | null>(null);
  // the zoom asked for but not yet drawn, so wheel turns quicker than the renders add up instead of starting over
  const asked = useRef<number | null>(null);
  const pan = useRef<{ x: number; y: number; left: number; top: number } | null>(null);

  // a new picture is seen whole again, split in the middle
  const [shown, setShown] = useState(src);
  if (shown !== src) {
    setShown(src);
    setZoom("fit");
    setSplit(50);
  }

  useEffect(() => {
    const el = stage.current;
    if (!el) return;
    const observer = new ResizeObserver(() => setRoom({ w: el.clientWidth, h: el.clientHeight }));
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  const maxZoom = natural ? Math.min(zoomSteps[zoomSteps.length - 1], maxFramePx / Math.max(natural.w, natural.h)) : 1;
  const fit = natural && room.w > 0 ? Math.max(minZoom, Math.min(1, (room.w - 2 * stagePad) / natural.w, (room.h - 2 * stagePad) / natural.h)) : 1;
  const scale = zoom === "fit" ? fit : zoom;
  const width = natural ? Math.round(natural.w * scale) : 0;
  const height = natural ? Math.round(natural.h * scale) : 0;
  const overflows = width + 2 * stagePad > room.w + 1 || height + 2 * stagePad > room.h + 1;
  const comparing = !!before && compare;

  /** Zooms to `next`, keeping the picture's point under `at` (a point in the window; the stage's middle when left out) where it is. */
  function zoomTo(next: number | "fit", at?: { x: number; y: number }) {
    const s = stage.current;
    const f = frame.current;
    if (!s || !f || !natural) return;
    const target = next === "fit" ? fit : Math.min(maxZoom, Math.max(minZoom, next));
    const box = s.getBoundingClientRect();
    const point = at ?? { x: box.left + box.width / 2, y: box.top + box.height / 2 };
    const fr = f.getBoundingClientRect();
    anchor.current = { x: (point.x - fr.left) / scale, y: (point.y - fr.top) / scale, at: point };
    asked.current = target;
    setZoom(next === "fit" || Math.abs(target - fit) < 1e-6 ? "fit" : target);
  }

  useLayoutEffect(() => {
    const a = anchor.current;
    const s = stage.current;
    const f = frame.current;
    anchor.current = null;
    asked.current = null;
    if (a && s && f) {
      const fr = f.getBoundingClientRect();
      s.scrollLeft += fr.left + a.x * scale - a.at.x;
      s.scrollTop += fr.top + a.y * scale - a.at.y;
    }
    placeGrip();
  });

  /** Keeps the split's grip in the middle of what is seen of the picture, however far it is zoomed in and moved. */
  function placeGrip() {
    const s = stage.current;
    const f = frame.current;
    if (!s || !f) return;
    const sr = s.getBoundingClientRect();
    const fr = f.getBoundingClientRect();
    const middle = (Math.max(sr.top, fr.top) + Math.min(sr.bottom, fr.bottom)) / 2 - fr.top;
    f.style.setProperty("--iv-grip-y", Math.round(middle) + "px");
  }

  // the wheel, and a pinch on a touchpad (which arrives as Ctrl + wheel), zooms about the pointer; a larger picture is
  // moved by dragging it. It is listened to natively, since React's wheel listeners are passive and cannot prevent the scroll
  useEffect(() => {
    const el = stage.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      // a notch is some 100 pixels in Chromium but 3 lines in Firefox; a page is the stage's height
      const dy = e.deltaY * (e.deltaMode === WheelEvent.DOM_DELTA_LINE ? 33 : e.deltaMode === WheelEvent.DOM_DELTA_PAGE ? el.clientHeight : 1);
      zoomTo((asked.current ?? scale) * Math.exp(-dy * 0.0015), { x: e.clientX, y: e.clientY });
    };
    el.addEventListener("wheel", onWheel, { passive: false });
    return () => el.removeEventListener("wheel", onWheel);
  });

  function splitAt(clientX: number) {
    const fr = frame.current?.getBoundingClientRect();
    if (!fr || fr.width === 0) return;
    setSplit(Math.min(100, Math.max(0, ((clientX - fr.left) / fr.width) * 100)));
  }

  function onPointerDown(e: PointerEvent<HTMLDivElement>) {
    if (e.button !== 0 || !natural) return;
    const onHandle = (e.target as HTMLElement).closest(".iv-split");
    // on the split, or anywhere on a picture that fits, a drag moves the split; on a larger picture it moves the picture
    if (comparing && (onHandle || !overflows)) {
      setDrag("split");
      splitAt(e.clientX);
    } else if (overflows) {
      const s = stage.current!;
      pan.current = { x: e.clientX, y: e.clientY, left: s.scrollLeft, top: s.scrollTop };
      setDrag("pan");
    } else return;
    e.currentTarget.setPointerCapture(e.pointerId);
    e.preventDefault();
  }

  function onPointerMove(e: PointerEvent<HTMLDivElement>) {
    if (drag === "split") splitAt(e.clientX);
    else if (drag === "pan" && pan.current) {
      const s = stage.current!;
      s.scrollLeft = pan.current.left - (e.clientX - pan.current.x);
      s.scrollTop = pan.current.top - (e.clientY - pan.current.y);
    }
  }

  function onPointerUp() {
    setDrag(null);
    pan.current = null;
  }

  function onSplitKey(e: KeyboardEvent<HTMLDivElement>) {
    const step = e.shiftKey ? 10 : 2;
    if (e.key === "ArrowLeft") setSplit((v) => Math.max(0, v - step));
    else if (e.key === "ArrowRight") setSplit((v) => Math.min(100, v + step));
    else if (e.key === "Home") setSplit(0);
    else if (e.key === "End") setSplit(100);
    else return;
    e.preventDefault();
  }

  const nextIn = zoomSteps.find((s) => s > scale * 1.001);
  const nextOut = [...zoomSteps].reverse().find((s) => s < scale / 1.001);
  const percent = (part: number, whole: number) => (100 * part) / whole + "%";
  const beforeBox: CSSProperties =
    beforeInset && natural
      ? { top: percent(beforeInset.top, natural.h), right: percent(beforeInset.right, natural.w), bottom: percent(beforeInset.bottom, natural.h), left: percent(beforeInset.left, natural.w) }
      : { inset: 0 };

  return (
    <div className="iv">
      <div className="iv-bar">
        {lead}
        {before && (
          <button
            className={"icon-button labelled iv-toggle" + (compare ? " active" : "")}
            aria-pressed={compare}
            title={compare ? "Show only the answer" : "Lay the image it was made from over the answer, split down the middle"}
            onClick={() => setCompare(!compare)}
          >
            <IconSquareHalf size={14} stroke={1.8} /> Before / after
          </button>
        )}
        <span className="iv-spacer" />
        <button className="icon-button" title="Zoom out" disabled={!natural || nextOut === undefined} onClick={() => nextOut !== undefined && zoomTo(nextOut)}>
          <IconMinus size={14} stroke={1.8} />
        </button>
        <button className="iv-zoom" title="The zoom; click to fit. The wheel or a double click on the picture zooms, and a picture larger than the stage is moved by dragging it." disabled={!natural} onClick={() => zoomTo("fit")}>
          {Math.round(scale * 100)} %
        </button>
        <button className="icon-button" title="Zoom in" disabled={!natural || nextIn === undefined || scale >= maxZoom} onClick={() => nextIn !== undefined && zoomTo(nextIn)}>
          <IconPlus size={14} stroke={1.8} />
        </button>
        <button className={"icon-button" + (zoom === "fit" ? " active" : "")} title="Fit the picture to the stage" disabled={!natural} onClick={() => zoomTo("fit")}>
          <IconZoomScan size={14} stroke={1.8} />
        </button>
        <button className={"icon-button" + (zoom === 1 || (zoom === "fit" && fit === 1) ? " active" : "")} title="Actual size: one pixel of the picture to one on the screen" disabled={!natural} onClick={() => zoomTo(1)}>
          <IconZoomReset size={14} stroke={1.8} />
        </button>
      </div>
      {above}
      <div className="iv-view">
        <div
          ref={stage}
          className={"iv-stage" + (overflows ? " overflows" : "") + (drag ? " dragging-" + drag : "") + (comparing && !overflows ? " splits" : "")}
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onPointerCancel={onPointerUp}
          onScroll={placeGrip}
          onDoubleClick={(e) => zoomTo(zoom === "fit" && fit < 1 ? 1 : zoom === "fit" ? 2 : "fit", { x: e.clientX, y: e.clientY })}
        >
          <div ref={frame} className={"iv-frame" + (natural ? "" : " loading") + (scale >= pixelatedFrom ? " pixelated" : "")} style={natural ? { width, height } : undefined}>
            <img
              src={src}
              alt={alt}
              draggable={false}
              onLoad={(e) => setLoaded({ src, w: e.currentTarget.naturalWidth, h: e.currentTarget.naturalHeight })}
            />
            {comparing && natural && (
              <div className="iv-before" style={{ clipPath: `inset(0 ${100 - split}% 0 0)` }}>
                <div className="iv-before-box" style={beforeBox}>
                  <img src={before!} alt="What it was made from" draggable={false} />
                </div>
              </div>
            )}
            {natural && children?.(natural)}
            {comparing && natural && (
              <div
                className="iv-split"
                style={{ left: split + "%" }}
                role="slider"
                tabIndex={0}
                aria-label="Where the before ends and the after begins"
                aria-valuemin={0}
                aria-valuemax={100}
                aria-valuenow={Math.round(split)}
                onKeyDown={onSplitKey}
              >
                <span className="iv-split-grip" />
              </div>
            )}
          </div>
        </div>
        {comparing && natural && (
          <>
            {split > 8 && <span className="iv-label before">Before</span>}
            {split < 92 && <span className="iv-label after">After</span>}
          </>
        )}
      </div>
    </div>
  );
}
