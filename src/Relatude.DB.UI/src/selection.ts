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
export function applySelect<T>(current: readonly T[], id: T, mode: SelectMode, run?: { order: readonly T[]; anchor: T | null; keep: boolean }): T[] {
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

/**
 * The selection after a rectangle drawn round `ids` (see marquee.tsx). On its own the rectangle IS
 * the selection, and an empty one clears it. With `keep` (shift or ctrl held) it is added: the nodes
 * not yet selected go in, and the ones already selected come out again - so a node caught in two
 * rectangles is deselected by the second, the way a ctrl-click on it would. What stays keeps its
 * order, and what arrives comes after it in the order the view found it.
 */
export function applyMarquee<T>(current: readonly T[], ids: readonly T[], keep: boolean): T[] {
  if (!keep) return [...ids];
  const caught = new Set(ids);
  const had = new Set(current);
  return [...current.filter((id) => !caught.has(id)), ...ids.filter((id) => !had.has(id))];
}

/**
 * What the query page has selected.
 *
 * Nodes are addressed by the INTERNAL id here - the int the store knows them by, which is what a hit
 * carries, what a card is drawn with and what a rectangle over a picture picks out. A selection of a
 * million nodes is then four bytes each rather than a guid string of forty, and nothing has to be
 * resolved to make one: the guids are fetched for the handful a form actually reads.
 *
 * Two other shapes exist because two other things can be selected: a whole QUERY (the page's Select
 * all, which never names a node at all), and a GUID, which is how another page hands a node over -
 * the global search, the dashboard - having no internal id to give.
 */
export type PageSelection =
  | { kind: "ints"; ids: number[] }
  | { kind: "guids"; ids: string[] }
  | { kind: "query"; count: number };

export const noSelection: PageSelection = { kind: "ints", ids: [] };

/** How many nodes are selected: what a query said it matched, or how many ids there are. */
export function selectionCount(selection: PageSelection): number {
  return selection.kind === "query" ? selection.count : selection.ids.length;
}

/** The internal ids selected, for the pictures to mark; nothing for the other two shapes. */
export function selectedInts(selection: PageSelection): readonly number[] {
  return selection.kind === "ints" ? selection.ids : [];
}
