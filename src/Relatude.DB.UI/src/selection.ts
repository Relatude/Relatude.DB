/**
 * How a click changes which nodes are selected, the same way in every view that has nodes to click:
 * a plain click selects that node alone, ctrl (cmd on a mac) toggles it in and out of the selection,
 * and shift extends the selection - by the run of rows from the last plain click in a list, and by
 * the one node in a picture, where the cards have no run to take.
 */
export type SelectMode = "only" | "toggle" | "extend";

export function selectModeOf(e: { ctrlKey: boolean; metaKey: boolean; shiftKey: boolean }): SelectMode {
  if (e.shiftKey) return "extend";
  if (e.ctrlKey || e.metaKey) return "toggle";
  return "only";
}

/**
 * The selection after a click on `id`. `run` is what a shift-click takes: the ids in the order they
 * are on screen and the anchor the run starts from; `keep` (ctrl held as well) adds the run to what
 * was selected rather than replacing it. Without a run, or with an anchor that is no longer on
 * screen, shift simply adds the one node.
 */
export function applySelect(current: readonly string[], id: string, mode: SelectMode, run?: { order: readonly string[]; anchor: string | null; keep: boolean }): string[] {
  if (mode === "only") return [id];
  if (mode === "toggle") return current.includes(id) ? current.filter((x) => x !== id) : [...current, id];
  if (run && run.anchor !== null) {
    const from = run.order.indexOf(run.anchor);
    const to = run.order.indexOf(id);
    if (from >= 0 && to >= 0) {
      const taken = run.order.slice(Math.min(from, to), Math.max(from, to) + 1);
      if (!run.keep) return taken;
      const seen = new Set(current);
      return [...current, ...taken.filter((x) => !seen.has(x))];
    }
  }
  return current.includes(id) ? [...current] : [...current, id];
}
