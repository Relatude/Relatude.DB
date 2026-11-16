import { useEffect, useMemo, useState } from "react";
import { IconFileZip, IconLock } from "@tabler/icons-react";
import { showError } from "../dialogs";
import { downloadOverridesExport, fetchOverridesExport, type ExportEntry, type ExportList } from "../server/overridesExport";
import { count, DataDialog, DataValue, type DataDialogEntry } from "./DataDialog";
import { DiffView } from "./VersionCompare";

const changeWords: Record<ExportEntry["change"], string | null> = {
  changed: null,
  added: "added here",
  removed: "removed here",
  same: "says the same",
  file: "file",
};

/**
 * Everything this installation changed in the admin UI - settings, custom settings, datamodel overrides,
 * log and GraphQL definitions, runtime types - and a zip of the application's own files with the entries
 * chosen moved in, to put into the project by hand. Nothing on the server changes. What configuration
 * decides is listed but never exported; secrets start unchosen.
 */
export function OverridesExportDialog({ onClose }: { onClose: () => void }) {
  const [list, setList] = useState<ExportList | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    fetchOverridesExport()
      .then(setList)
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, []);

  const byId = useMemo(() => new Map((list?.groups ?? []).flatMap((g) => g.entries.map((e) => [e.id, e] as const))), [list]);
  const entries: DataDialogEntry[] | null = list
    ? list.groups.flatMap((group) =>
        group.entries.map((entry) => ({
          key: entry.id,
          group: group.title,
          label: entry.label,
          badges: (
            <>
              {changeWords[entry.change] && <span className="setting-badge overrides">{changeWords[entry.change]}</span>}
              {entry.secret && <span className="setting-badge faint">secret</span>}
            </>
          ),
          values: <Change entry={entry} />,
          note: (
            <>
              {entry.where && <div className="overrides-entry-where">{entry.where}</div>}
              <div className="muted">
                into <code>{entry.file}</code>
              </div>
              {entry.blocked && (
                <span>
                  <IconLock size={12} stroke={1.8} /> {entry.blocked}
                </span>
              )}
            </>
          ),
          selectable: !entry.blocked,
        })),
      )
    : null;

  async function download(ids: string[]): Promise<void> {
    if (ids.length === 0) return;
    setBusy(true);
    try {
      await downloadOverridesExport(ids);
      const secrets = ids.filter((id) => byId.get(id)?.secret).length;
      setMessage(
        `${count(ids.length, "change")} downloaded. Unzip over the project, review the diff and deploy.` +
          (secrets > 0 ? ` ${count(secrets, "of them is a secret", "of them are secrets")}: keep ${secrets === 1 ? "it" : "them"} out of source control.` : ""),
      );
    } catch (e) {
      await showError("Could not download the changes", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <DataDialog
      title="Changes made on this server"
      titleLead="Download changes"
      titleIcon={<IconFileZip size={17} stroke={1.8} className="share-button-icon" />}
      intro={
        list && (
          <>
            What was changed in the admin UI on this installation, and kept apart from the application's own files. Download the files of the
            application as they would be with the changes chosen moved in - each at its path in the project, with <code>relatude-changes.md</code> saying
            what is in them - then unzip them over the project, review the diff and deploy. Nothing here is changed; once the files are deployed,
            the server takes out of its overrides what they say the same as.
          </>
        )
      }
      warnings={list?.problems ?? []}
      entries={entries}
      error={error}
      loadingLabel="Reading what was changed here…"
      emptyText={<>Nothing was changed in the admin UI on this installation: everything comes from the application's own files.</>}
      message={message}
      busy={busy}
      initiallySelected={(key) => !byId.get(key)?.secret}
      download={{ label: (n) => "Download" + (n > 0 ? " " + n : ""), title: "A zip of the application's files with the changes chosen moved in", onRun: download }}
      onClose={onClose}
    />
  );
}

// short values side by side; a text of several lines as a diff, shown when asked for
function Change({ entry }: { entry: ExportEntry }) {
  const [open, setOpen] = useState(false);
  const before = entry.before ?? "";
  const after = entry.after ?? "";
  const long = before.includes("\n") || after.includes("\n") || before.length + after.length > 120;
  if (!long) {
    return (
      <>
        {entry.change !== "file" && <DataValue side="settings">{entry.before ?? "not set"}</DataValue>}
        <DataValue side="data">{entry.after ?? "not set"}</DataValue>
      </>
    );
  }
  return (
    <div className="overrides-export-change">
      <button
        type="button"
        className="link-button"
        onClick={(e) => {
          e.preventDefault();
          setOpen(!open);
        }}
      >
        {open ? "Hide the change" : "Show the change"}
      </button>
      {open && <DiffView a={before} b={after} granularity="lines" split={false} />}
    </div>
  );
}
