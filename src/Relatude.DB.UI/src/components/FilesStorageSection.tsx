import { IconArchive, IconFolders, IconTransform } from "@tabler/icons-react";
import type { ComponentType } from "react";
import { ConversionsSection } from "./ConversionsSection";
import { FilesSection } from "./FilesSection";
import { StorageSection } from "./StorageSection";
import type { DatabaseInfo } from "../server/serverInfo";

/**
 * The three pages about what is on disk, under one entry in the rail.
 *
 * They were three sections and they are one question asked three ways: the files themselves, the
 * database file and the copies of it, and the queue that turns one file into another. Someone
 * looking at a picture that has not appeared yet goes Files → Conversions and back, and someone
 * clearing space goes Storage → Files; as separate rail entries that was a trip through the menu
 * each time.
 *
 * Each view keeps its own section id, so the global search still finds "Files" or "Conversions" and
 * opens the page on it, and the switch is nothing more than those ids: picking one here is the same
 * operation as picking it in the rail. The rail's own entry is the "storage" id, so that is what
 * opens when someone clicks Storage in the menu.
 */
export type FilesStorageView = "files" | "storage" | "conversions";

const views: { id: FilesStorageView; label: string; icon: ComponentType<{ size?: number; stroke?: number }>; hint: string }[] = [
  { id: "files", label: "Files", icon: IconFolders, hint: "browse the file storages of this database" },
  { id: "storage", label: "Storage", icon: IconArchive, hint: "backups, the database file, and the file storage as a whole" },
  { id: "conversions", label: "Conversions", icon: IconTransform, hint: "the queue that resizes images, converts media and extracts text" },
];

export function FilesStorageSection({
  db,
  view,
  onSelectView,
}: {
  db: DatabaseInfo;
  view: FilesStorageView;
  onSelectView: (view: FilesStorageView) => void;
}) {
  const conversions = db.conversionCount ?? 0;
  return (
    // only the file browser sizes itself against the window; the other two are ordinary pages that
    // scroll, and a filling box around them would cut them off at the fold
    <div className={"files-storage" + (view === "files" ? " fill" : "")}>
      <div className="module-switch" role="tablist">
        {views.map((v) => (
          <button
            key={v.id}
            role="tab"
            aria-selected={view === v.id}
            className={view === v.id ? "active" : ""}
            title={v.hint}
            onClick={() => onSelectView(v.id)}
          >
            <v.icon size={15} stroke={1.8} />
            {v.label}
            {/* what the conversion queue still owes, on the switch rather than only in the rail:
                the reason to look at that view is usually that the number is not zero */}
            {v.id === "conversions" && conversions > 0 && <span className="badge">{conversions}</span>}
          </button>
        ))}
      </div>
      {view === "files" ? (
        <FilesSection key={db.id} db={db} />
      ) : view === "storage" ? (
        <StorageSection key={db.id} db={db} />
      ) : (
        <ConversionsSection key={db.id} db={db} />
      )}
    </div>
  );
}
