import { useEffect, useRef, useState } from "react";
import type { EditorContext, Selection } from "./DatamodelEditors";
import { DatamodelGraph } from "./DatamodelGraph";
import { DatamodelGraph3D } from "./DatamodelGraph3D";
import { modeKey, namesKey, readMode, readNames, remember, type GraphMode } from "./datamodelGraphModel";

interface Props {
  ctx: EditorContext;
  visibleTypes: Set<string>;
  selection: Selection | null;
  query: string;
  storeId: string;
}

/**
 * What the two graphs share and neither owns: which of them is up, whether names are written, and
 * the browser's fullscreen. Held here rather than in either graph, so switching from flat to space
 * while filling the screen keeps the screen filled, and a name switched off in one is off in the other.
 */
export interface GraphShell {
  mode: GraphMode;
  setMode: (mode: GraphMode) => void;
  names: boolean;
  toggleNames: () => void;
  fullscreen: boolean;
  /** Enters or leaves the browser's fullscreen; settles once the browser has answered either way. */
  toggleFullscreen: () => Promise<void>;
  /** Steps out of fullscreen if the graph is filling the screen; nothing otherwise. */
  exitFullscreen: () => void;
}

/**
 * The Graph view: one panel with two ways of drawing the same picture, flat on the page or in space
 * with a camera flying through it. The start type, what is unfolded and which lines are shown are
 * kept by the graphs themselves in shared storage, so the picture carries over between the two.
 */
export function DatamodelGraphView(props: Props) {
  const [mode, setMode] = useState<GraphMode>(readMode);
  const [names, setNames] = useState<boolean>(readNames);
  const [fullscreen, setFullscreen] = useState(false);
  const viewRef = useRef<HTMLDivElement>(null);

  useEffect(() => remember(modeKey, mode), [mode]);
  useEffect(() => remember(namesKey, names), [names]);

  // the browser owns the fullscreen state - Escape and F11 change it without asking - so the button
  // follows the document rather than the other way round
  useEffect(() => {
    // both sides are null before the first paint, which is not the same as being fullscreen
    const sync = () => setFullscreen(document.fullscreenElement !== null && document.fullscreenElement === viewRef.current);
    document.addEventListener("fullscreenchange", sync);
    sync();
    return () => document.removeEventListener("fullscreenchange", sync);
  }, []);

  /**
   * Hands the whole view to the browser's fullscreen, toolbar and all, so the controls stay within
   * reach. The drawing follows by itself: both graphs measure their own stage when it resizes.
   */
  function toggleFullscreen(): Promise<void> {
    const el = viewRef.current;
    if (!el) return Promise.resolve();
    if (document.fullscreenElement === el) return document.exitFullscreen().catch(() => {});
    // refused (a permissions policy, or no gesture behind the call): the view stays where it is
    return el.requestFullscreen?.().catch(() => {}) ?? Promise.resolve();
  }
  function exitFullscreen() {
    if (document.fullscreenElement === viewRef.current) void document.exitFullscreen().catch(() => {});
  }

  const shell: GraphShell = { mode, setMode, names, toggleNames: () => setNames((v) => !v), fullscreen, toggleFullscreen, exitFullscreen };
  // one div for both modes on purpose: it is the element the browser is showing fullscreen
  return (
    <div className="dm-diagram dm-graph" ref={viewRef}>
      {mode === "3d" ? <DatamodelGraph3D {...props} shell={shell} /> : <DatamodelGraph {...props} shell={shell} />}
    </div>
  );
}
