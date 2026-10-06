/**
 * Where a value comes from, said the same way on every page that shows one - settings, the datamodel
 * editor's overrides, custom logs, GraphQL endpoints:
 *
 * - DEFAULT: nothing sets it, so the built-in default applies.
 * - SHARED: the relatude.settings folder - part of the application, in source control and deployed to
 *   every installation. (The "settings" kind in code.)
 * - THIS SERVER: the data folder (relatude.data) - this installation's: what the admin UI saved here. It wins
 *   over SHARED, and is moved into SHARED with the page's Move to shared button (ShareButton). (The "data" kind in code.)
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
    // prefixed: a bare "settings" class is the settings page's own root, whose height and flex the tag took on
    <span className={"source-tag source-" + kind + (small ? " small" : "") + (removes ? " removes" : "")} title={title}>
      {label ?? labels[kind]}
    </span>
  );
}

/** THIS SERVER taking a SHARED value or definition away on this installation. */
export function RemovedTag({ title, small }: { title: string; small?: boolean }) {
  return <SourceTag kind="data" removes small={small} label="this server · removed" title={title} />;
}
