/**
 * What a page shows while its first answer from the server is on the way: a bar that runs and a
 * line saying what is being fetched, in the room the page will take.
 *
 * It holds itself back for a moment before fading in (see `.page-loading` in app.css), so a page
 * that answers at once goes straight from nothing to itself instead of flashing this on the way.
 */
export function Loading({ label = "Loading…" }: { label?: string }) {
  return (
    <div className="page-loading" role="status" aria-live="polite">
      <div className="progress-bar indeterminate">
        <div className="progress-fill" />
      </div>
      <span>{label}</span>
    </div>
  );
}
