import { useEffect, useRef, useState } from "react";
import type { ReactNode } from "react";

/** how long the old text takes to go; the new one takes a little longer, starting a little later */
const fadeMs = 600;

/**
 * Text that crossfades when what it says changes kind: the old words fade out where they are while
 * the new ones fade in over them, instead of one replacing the other between two frames.
 *
 * `fadeKey` says when that is. It is not the text itself, because a lot of status text changes
 * without changing what it is about - a node count going up, an open counting down its time left -
 * and a crossfade on every one of those would be a blur. Only a new key fades; the same key updates
 * in place. The first render is simply there.
 *
 * The two copies sit in one grid cell, so the box is as wide as the wider of them for the moment
 * both are there, and each copy is cut short with an ellipsis like the line it replaces.
 */
export function FadeText({ fadeKey, children, className }: { fadeKey: string; children: ReactNode; className?: string }) {
  const [current, setCurrent] = useState(fadeKey);
  const [leaving, setLeaving] = useState<{ key: string; content: ReactNode } | null>(null);
  // what was on screen at the last render, which is what fades out when the key changes
  const shown = useRef<ReactNode>(children);

  if (current !== fadeKey) {
    // the documented way to adjust state to a changed prop: during the render, before anything is drawn
    setCurrent(fadeKey);
    setLeaving({ key: current, content: shown.current });
  }

  useEffect(() => {
    shown.current = children;
  });

  useEffect(() => {
    if (!leaving) return;
    const timer = setTimeout(() => setLeaving(null), fadeMs);
    return () => clearTimeout(timer);
  }, [leaving]);

  return (
    <span className={"fade-text" + (className ? " " + className : "")}>
      {leaving && (
        <span key={"out:" + leaving.key} className="fade-text-out" aria-hidden="true">
          {leaving.content}
        </span>
      )}
      <span key={"in:" + current} className={leaving ? "fade-text-in" : undefined}>
        {children}
      </span>
    </span>
  );
}
