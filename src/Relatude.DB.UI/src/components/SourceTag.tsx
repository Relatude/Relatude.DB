/**
 * Where a value comes from, said the same way on every page that shows one - settings, the datamodel
 * editor's overrides, custom logs, GraphQL endpoints:
 *
 * - DEFAULT: nothing sets it, so the built-in default applies.
 * - SHARED: the relatude.settings folder - part of the application, in source control and deployed to
 *   every installation. (The "settings" kind in code.)
 * - THIS SERVER: the data folder (relatude.data) - this installation's: what the admin UI saved here. It wins
 *   over SHARED, and is moved into SHARED from the page's THIS SERVER dialog. (The "data" kind in code.)
 * - APPSETTINGS: appsettings.json and the rest of the configuration; wins over both files, never written here.
 * - CODE: the application's code, at every start; never written to a file.
 *
 * The tooltip is the page's to give, since only it knows the file names: it says where the value is, and
 * where a change made here is written.
 */
export type SourceKind = "default" | "settings" | "data" | "appsettings" | "code";

const labels: Record<SourceKind, string> = { default: "default", settings: "shared", data: "this server", appsettings: "appsettings", code: "code" };

export function SourceTag({ kind, title, label, small, removes }: { kind: SourceKind; title: string; label?: string; small?: boolean; removes?: boolean }) {
  return (
    <span className={"source-tag " + kind + (small ? " small" : "") + (removes ? " removes" : "")} title={title}>
      {label ?? labels[kind]}
    </span>
  );
}

/** THIS SERVER taking a SHARED value or definition away on this installation. */
export function RemovedTag({ title, small }: { title: string; small?: boolean }) {
  return <SourceTag kind="data" removes small={small} label="this server · removed" title={title} />;
}

/** The THIS SERVER count on a page's toolbar: what this installation holds that SHARED does not, and the way to the dialog that moves it. */
export function DataButton({ count, error, title, onClick, disabled }: { count: number; error?: string | null; title: string; onClick: () => void; disabled?: boolean }) {
  return (
    <button className="icon-button labelled data-button" title={error ? title + " " + error : title} onClick={onClick} disabled={disabled}>
      <span className={"source-tag data" + (error ? " error" : "")}>{labels.data}</span>
      {error ? "not read" : count > 0 ? count : "none"}
    </button>
  );
}
