import { useEffect, useState } from "react";
import { IconGripVertical } from "@tabler/icons-react";

/** A place in one of the chip lists of a builder: the list, and the index in it. */
export interface ChipSlot {
  list: string;
  index: number;
}

/**
 * Drag and drop between the chip rows of a builder - the pivot's rows, columns and measures, the
 * group-by keys and aggregates - for changing the order of a row, and for carrying a grouping from
 * one axis to the other.
 *
 * A chip is dragged by its grip rather than by its body: a chip is mostly selects, and a select
 * inside a draggable element cannot be worked with the mouse. What is dropped is an insertion
 * point - before or after the chip under the pointer, by which half of it the pointer is in, or at
 * the end of a row when the pointer is over the row's own space - and it shows as a line there for
 * as long as the drag lasts. The browser's own drag and drop does the ghost and the cursor, which is
 * also why a drop that would leave the lists as they are is not marked: there is nothing to show.
 */
export function useChipDrag(canDrop: (fromList: string, toList: string) => boolean, onMove: (from: ChipSlot, to: ChipSlot) => void) {
  const [source, setSource] = useState<ChipSlot | null>(null);
  const [target, setTarget] = useState<ChipSlot | null>(null);

  function clear() {
    setSource(null);
    setTarget(null);
  }

  // whether putting the dragged chip at `slot` changes anything: before or after itself does not
  function moves(slot: ChipSlot): boolean {
    return source !== null && !(slot.list === source.list && (slot.index === source.index || slot.index === source.index + 1));
  }

  function over(e: React.DragEvent, slot: ChipSlot) {
    if (!source || !canDrop(source.list, slot.list)) return;
    e.preventDefault(); // the default is to refuse the drop
    e.stopPropagation(); // an accepted drag does not reach the window listener that clears the marker
    e.dataTransfer.dropEffect = "move";
    if (target?.list !== slot.list || target.index !== slot.index) setTarget(slot);
  }

  // the marker has to go when the pointer leaves the places that would take the chip - a row of
  // another kind, the table below, the rest of the page. A drag over any of those reaches the window,
  // because only the handlers that accept the drag stop it on the way.
  useEffect(() => {
    if (source === null) return;
    const outside = () => setTarget((t) => (t === null ? t : null));
    window.addEventListener("dragover", outside);
    return () => window.removeEventListener("dragover", outside);
  }, [source]);

  function drop(e: React.DragEvent, slot: ChipSlot) {
    if (!source || !canDrop(source.list, slot.list)) return;
    e.preventDefault();
    const from = source;
    clear();
    if (moves(slot)) onMove(from, slot);
  }

  // the insertion point a pointer over a chip means: before it in its left half, after it in the right
  function slotAt(e: React.DragEvent<HTMLElement>, slot: ChipSlot): ChipSlot {
    const r = e.currentTarget.getBoundingClientRect();
    return { list: slot.list, index: slot.index + (e.clientX > r.left + r.width / 2 ? 1 : 0) };
  }

  return {
    /** Something is being dragged. */
    dragging: source !== null,

    /** The props of the grip that starts a drag of the chip at this slot. */
    grip(slot: ChipSlot) {
      return {
        draggable: true,
        onDragStart(e: React.DragEvent<HTMLElement>) {
          e.dataTransfer.effectAllowed = "move";
          e.dataTransfer.setData("text/plain", slot.list + ":" + slot.index); // Firefox starts no drag without data
          // the ghost is the whole chip, held where it was picked up, rather than the grip alone
          const chip = e.currentTarget.closest<HTMLElement>(".pivot-chip");
          if (chip) {
            const r = chip.getBoundingClientRect();
            e.dataTransfer.setDragImage(chip, e.clientX - r.left, e.clientY - r.top);
          }
          setSource(slot);
        },
        onDragEnd: clear,
      };
    },

    /** The props and the marker of the chip at this slot, in a list of `length`. */
    chip(slot: ChipSlot, length: number) {
      const isSource = source?.list === slot.list && source.index === slot.index;
      // the marker is drawn on a chip: a drop at the end of a list marks its last chip's far side
      let marker = "";
      if (target?.list === slot.list && moves(target)) {
        if (target.index === slot.index) marker = " drop-before";
        else if (target.index === length && slot.index === length - 1) marker = " drop-after";
      }
      return {
        className: (isSource ? " dragging" : "") + marker,
        onDragOver(e: React.DragEvent<HTMLElement>) {
          // stopped in over(): the row behind the chip would otherwise make this a drop at its end
          over(e, slotAt(e, slot));
        },
        onDrop(e: React.DragEvent<HTMLElement>) {
          if (!source || !canDrop(source.list, slot.list)) return;
          e.stopPropagation();
          drop(e, slotAt(e, slot));
        },
      };
    },

    /** The props of a whole row: a drop on its own space goes at the end of the list of `length`. */
    row(list: string, length: number) {
      const ready = source !== null && canDrop(source.list, list);
      return {
        className: ready ? " drop-ready" + (target?.list === list ? " drop-over" : "") : "",
        onDragOver: (e: React.DragEvent<HTMLElement>) => over(e, { list, index: length }),
        onDrop: (e: React.DragEvent<HTMLElement>) => drop(e, { list, index: length }),
      };
    },
  };
}

export type ChipDrag = ReturnType<typeof useChipDrag>;

/** The lists after the chip at `from` is put at `to`, or null when that would leave them as they are. */
export function moveChip<T>(lists: Record<string, T[]>, from: ChipSlot, to: ChipSlot): Record<string, T[]> | null {
  const item = lists[from.list]?.[from.index];
  if (item === undefined) return null;
  const rest = lists[from.list].filter((_, j) => j !== from.index);
  if (from.list === to.list) {
    const at = to.index > from.index ? to.index - 1 : to.index;
    if (at === from.index) return null;
    rest.splice(at, 0, item);
    return { ...lists, [from.list]: rest };
  }
  const into = [...(lists[to.list] ?? [])];
  into.splice(Math.min(to.index, into.length), 0, item);
  return { ...lists, [from.list]: rest, [to.list]: into };
}

/** The handle a chip is dragged by. */
export function ChipGrip({ title, ...props }: { title: string } & React.HTMLAttributes<HTMLSpanElement>) {
  return (
    <span className="pivot-grip" title={title} {...props}>
      <IconGripVertical size={12} stroke={1.8} />
    </span>
  );
}
