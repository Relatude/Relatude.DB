import { useCallback, useEffect, useRef, useState } from "react";
import { IconAlertTriangle, IconChevronDown, IconChevronRight, IconCopy, IconFileExport, IconFolder, IconFolderOpen } from "@tabler/icons-react";
import { DialogTools } from "./DialogTools";
import { fetchFolder, nameProblem, type FolderListing, type IoInfo } from "../server/files";

/** What the dialog was answered with. */
export interface MoveChoice {
  toIoId: string;
  targetPath: string; // '/'-separated, "" for the storage root; folders that are not there yet are made
  overwrite: boolean; // replace a file the target already has, rather than leave this one where it is
  keepOriginals: boolean; // a copy rather than a move
}

interface Props {
  mode: "move" | "copy"; // what the button that opened the dialog asked for; the dialog can switch it
  source: IoInfo;
  targets: IoInfo[]; // every other storage of the database, never empty
  what: string; // "3 files and 2 folders"
  fromPath: string; // the open folder, which the target folder starts out as
  touchesPrimary: boolean; // some of it is the database's own data
  show: (path: string) => string; // a path as the Files view shows it (friendly names)
  onCancel: () => void;
  onMove: (choice: MoveChoice) => void;
}

const targetKey = "filesMoveTarget"; // the storage moved into last, offered first next time

/**
 * Where the selection of the Files view goes - moved or copied: which storage, which folder in it,
 * and what happens to a file that is there already. Move and Copy each have a button in the toolbar,
 * and the switch at the top of the dialog changes between them without starting over. The folder starts out as the one open here, so a move from one
 * storage to another keeps the layout it had, and can be typed - a folder not there yet is made - or
 * picked from the target's own tree below the field.
 *
 * The dialog is its own confirmation, the way the time travel dialog is: it says what will happen,
 * and the button is what does it. Only a move out of the database's own data folders is asked about
 * once more, after this (see confirmPrimaryMove in FilesSection).
 */
export function MoveFilesDialog(p: Props) {
  const [toIoId, setToIoId] = useState(() => {
    let remembered: string | null = null;
    try {
      remembered = localStorage.getItem(targetKey);
    } catch {
      // storage blocked: the first one it is
    }
    return p.targets.find((t) => t.id === remembered)?.id ?? p.targets[0].id;
  });
  const target = p.targets.find((t) => t.id === toIoId) ?? p.targets[0];
  const [folder, setFolder] = useState(p.fromPath);
  const [overwrite, setOverwrite] = useState(false);
  const [keep, setKeep] = useState(p.mode === "copy");
  const plain = target.kind === "projectRoot";
  const targetPath = normalizeFolder(folder);
  const problem = folderProblem(folder, plain);
  const verb = keep ? "Copy" : "Move";

  function submit() {
    if (problem) return;
    try {
      localStorage.setItem(targetKey, target.id);
    } catch {
      // only a convenience
    }
    p.onMove({ toIoId: target.id, targetPath, overwrite, keepOriginals: keep });
  }

  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && p.onCancel()}>
      <div
        className="dialog move-dialog"
        role="dialog"
        aria-label={`${verb} ${p.what}`}
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            e.preventDefault();
            p.onCancel();
          }
        }}
      >
        <h3>
          {verb} {p.what} to another storage
          <DialogTools onClose={p.onCancel} closeTitle="Cancel" />
        </h3>
        <div className="module-switch compact move-mode" role="tablist" aria-label="Move or copy">
          <button className={keep ? "" : "active"} role="tab" aria-selected={!keep} onClick={() => setKeep(false)} title="Take them out of this storage">
            <IconFileExport size={14} stroke={1.8} /> Move
          </button>
          <button className={keep ? "active" : ""} role="tab" aria-selected={keep} onClick={() => setKeep(true)} title="Leave them here as they are and put a copy there">
            <IconCopy size={14} stroke={1.8} /> Copy
          </button>
        </div>
        <div className="dialog-body">
          {keep
            ? `Copies ${p.what} from ${p.source.name}; nothing here is changed.`
            : `Moves ${p.what} out of ${p.source.name}. Each file is copied, checked and only then deleted here, so it is always in one place or the other; a file that is in use stays where it is.`}{" "}
          Folders arrive with everything in them.
        </div>
        <label className="dialog-field">
          <span className="muted">To storage</span>
          <select className="select" value={target.id} onChange={(e) => setToIoId(e.target.value)}>
            {p.targets.map((candidate) => (
              <option key={candidate.id} value={candidate.id}>
                {candidate.kind === "projectRoot" ? candidate.name : `${candidate.name} (${candidate.type})`}
              </option>
            ))}
          </select>
        </label>
        <label className="dialog-field move-folder-field">
          <span className="muted">Into folder</span>
          <input
            className="text-input"
            value={folder}
            placeholder="The storage root"
            onChange={(e) => setFolder(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") {
                e.preventDefault();
                submit();
              }
            }}
            spellCheck={false}
            autoComplete="off"
          />
          {problem ? <span className="dialog-error">{problem}</span> : <span className="muted">Pick a folder below, or type one - folders that are not there yet are made.</span>}
        </label>
        <TargetFolders key={target.id} io={target} value={targetPath} onPick={setFolder} show={p.show} />
        <div className="move-options">
          <span className="muted">When the file is already there</span>
          <label className="login-remember">
            <input type="radio" name="move-existing" checked={!overwrite} onChange={() => setOverwrite(false)} />
            Leave it as it is, and skip this one
          </label>
          <label className="login-remember">
            <input type="radio" name="move-existing" checked={overwrite} onChange={() => setOverwrite(true)} />
            Replace it with this one
          </label>
        </div>
        {p.touchesPrimary && !keep && (
          <div className="files-notice">
            <IconAlertTriangle size={15} stroke={1.8} />
            <span>
              <b>Some of this is the database's own data.</b> The database will miss what is moved until it is moved back.
            </span>
          </div>
        )}
        <div className="dialog-row">
          <div className="header-spacer" />
          <button className="action-button dialog-confirm" disabled={problem !== null} onClick={submit}>
            {verb}
          </button>
          <button className="action-button" onClick={p.onCancel}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}

// the folder as typed, as the server takes it: '/' between the names, no empty ones, either slash
function normalizeFolder(text: string): string {
  return text
    .split(/[/\\]/)
    .map((segment) => segment.trim())
    .filter((segment) => segment.length > 0)
    .join("/");
}

function folderProblem(text: string, plain: boolean): string | null {
  for (const segment of normalizeFolder(text).split("/")) {
    if (segment === "") continue; // the storage root
    const problem = nameProblem(segment, plain);
    if (problem) return `"${segment}": ${problem}`;
  }
  return null;
}

/**
 * The target storage's folders, to pick the one to move into. Loaded a level at a time as they are
 * opened, and opened to begin with down to the folder in the field, so the folder the move starts out
 * going to is on screen with its neighbours. The field stays the answer: clicking a folder here puts
 * its path there.
 */
function TargetFolders({ io, value, onPick, show }: { io: IoInfo; value: string; onPick: (path: string) => void; show: (path: string) => string }) {
  const [listings, setListings] = useState<Record<string, FolderListing>>({});
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set(opened(value)));
  const alive = useRef(true);
  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
    };
  }, []);
  const load = useCallback(
    (path: string) => {
      fetchFolder(io.id, path)
        .then((listing) => {
          if (alive.current) setListings((prev) => ({ ...prev, [path]: listing }));
        })
        .catch(() => {}); // a folder that cannot be listed just shows nothing below it
    },
    [io.id],
  );
  // the way down to the folder the field starts with; the component is keyed by the storage, so this
  // runs once per storage picked
  useEffect(() => {
    for (const path of opened(value)) load(path);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [load]);

  function toggle(path: string) {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(path)) {
        next.delete(path);
      } else {
        next.add(path);
        if (!listings[path]) load(path);
      }
      return next;
    });
  }

  function row(path: string, name: string, depth: number, hasSubFolders: boolean) {
    const open = expanded.has(path);
    const children = listings[path]?.subFolders ?? [];
    return (
      <div key={path}>
        <div className={"tree-row" + (value === path ? " active" : "")} style={{ paddingLeft: 4 + depth * 14 }}>
          <button className="tree-chevron" onClick={() => toggle(path)} style={{ visibility: hasSubFolders ? "visible" : "hidden" }} tabIndex={-1}>
            {open ? <IconChevronDown size={13} stroke={2} /> : <IconChevronRight size={13} stroke={2} />}
          </button>
          <button className="tree-label" onClick={() => onPick(path)} onDoubleClick={() => toggle(path)} title={path || name}>
            {value === path ? <IconFolderOpen size={15} stroke={1.7} /> : <IconFolder size={15} stroke={1.7} />}
            <span>{path === "" ? name : show(name)}</span>
          </button>
        </div>
        {open && children.map((sub) => row(path === "" ? sub.name : `${path}/${sub.name}`, sub.name, depth + 1, sub.hasSubFolders))}
      </div>
    );
  }

  return <div className="move-tree">{row("", io.kind === "projectRoot" ? "[Server root]" : "Storage root", 0, true)}</div>;
}

// "", "a", "a/b" for "a/b/c": the folders to open for c to show - and the root at least, so its
// folders are there to pick from when the move goes to the root
function opened(path: string): string[] {
  const segments = path === "" ? [] : path.split("/");
  return ["", ...segments.slice(0, -1).map((_, i) => segments.slice(0, i + 1).join("/"))];
}
