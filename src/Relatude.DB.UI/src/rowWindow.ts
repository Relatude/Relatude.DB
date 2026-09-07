import { useCallback, useState } from "react";

/** How many rows are built, the scroll handler that asks for more, and a way to demand a row. */
export interface RowWindow {
  /** how many of the page's rows to render right now */
  count: number;
  /** put on the scrolling element: nearing its end builds the next chunk */
  onScroll: (e: React.UIEvent<HTMLElement>) => void;
  /** build at least this many rows - the keyboard walking past the ones that are built */
  ensure: (rows: number) => void;
}

/** how close to the end of the scroller the next chunk is built, in pixels */
const growAhead = 600;

/**
 * The rows of one page, handed out a chunk at a time.
 *
 * A page size is a query, not a rendering budget. Asking for a hundred thousand rows is about the
 * whole set - scrolling it, copying it, exporting it - and building a hundred thousand table rows
 * to say so is enough dom to wedge the tab for minutes: the browser stops answering, and since the
 * page size is remembered per tab, it stops answering again next time it is opened.
 *
 * So the answer arrives whole and is rendered in chunks: the first are built at once, the next
 * whenever the scroller comes near the end of them or the cursor walks past them. Nothing is
 * measured and no row is thrown away once built, which keeps the scrollbar honest as far as it has
 * been read and leaves find-in-page working over everything that has been seen.
 */
export function useRowWindow(rows: readonly unknown[], chunk = 400): RowWindow {
  const total = rows.length;
  const [count, setCount] = useState(chunk);
  // A new page is different rows: they start from the first chunk again. Derived during the render
  // that brings them rather than in an effect afterwards, so the new page is never briefly rendered
  // at the length the old one had grown to.
  const [seen, setSeen] = useState(rows);
  if (seen !== rows) {
    setSeen(rows);
    setCount(chunk);
  }

  const ensure = useCallback(
    (wanted: number) => setCount((c) => (wanted <= c ? c : Math.max(wanted, c + chunk))),
    [chunk],
  );

  const onScroll = useCallback(
    (e: React.UIEvent<HTMLElement>) => {
      const el = e.currentTarget;
      if (el.scrollTop + el.clientHeight >= el.scrollHeight - growAhead) setCount((c) => c + chunk);
    },
    [chunk],
  );

  return { count: Math.min(count, total), onScroll, ensure };
}
