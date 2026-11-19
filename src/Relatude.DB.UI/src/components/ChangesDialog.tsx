import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type ComponentType, type CSSProperties, type KeyboardEvent, type PointerEvent } from "react";
import {
  IconAdjustments,
  IconAdjustmentsHorizontal,
  IconAlertTriangle,
  IconApi,
  IconArrowBackUp,
  IconChevronRight,
  IconChevronsDown,
  IconChevronsUp,
  IconDatabase,
  IconDownload,
  IconFileAnalytics,
  IconFileCode,
  IconFileImport,
  IconFolder,
  IconLock,
  IconSchema,
  IconServer,
  IconSettings,
} from "@tabler/icons-react";
import { showConfirm, showError } from "../dialogs";
import {
  closeChanges,
  datamodelEditorOf,
  notifyChangesMade,
  useChangesDialog,
  useDatamodelEditors,
  type ChangesDialogState,
} from "../changes";
import {
  datamodelPathOf,
  discardEntries,
  downloadOverridesExport,
  fetchOverridesExport,
  moveEntries,
  type ChangeOutcome,
  type ExportEntry,
  type ExportGroup,
  type ExportKind,
  type ExportList,
} from "../server/overridesExport";
import { count, DataValue, deployedCopyWarning } from "./DataDialog";
import { DialogTools } from "./DialogTools";
import { Loading } from "./Loading";
import { ShareDialogTitle } from "./ShareButton";
import { RemovedTag } from "./SourceTag";
import { DiffView } from "./VersionCompare";

/** Drawn once, in App: the changes dialog whenever a page has opened it (openChanges in changes.ts). */
export function ChangesHost() {
  const request = useChangesDialog();
  // keyed: every opening starts over, with what that page is about
  return request ? <ChangesDialog key={request.id} request={request} /> : null;
}

type Icon = ComponentType<{ size?: number; stroke?: number; className?: string }>;

const kindIcons: Record<ExportKind, Icon> = {
  settings: IconSettings,
  custom: IconAdjustmentsHorizontal,
  datamodel: IconSchema,
  logs: IconFileAnalytics,
  graphql: IconApi,
  types: IconFileCode,
};

/** What can be done with an entry here, and why not where it cannot. */
interface Can {
  download: boolean;
  move: boolean;
  discard: boolean;
  why: { download?: string | null; move?: string | null; discard?: string | null };
}

interface Leaf {
  kind: "leaf";
  key: string;
  level: number;
  entry: ExportEntry;
  group: ExportGroup;
  can: Can;
  /** the label as the tree shows it: a datamodel override under its type without the type's name */
  label: string;
}

interface Branch {
  kind: "branch";
  key: string;
  level: number;
  label: string;
  icon: Icon;
  title?: string;
  children: Branch[];
  leaves: Leaf[];
  /** every entry below it, in the order they are listed */
  all: Leaf[];
  /** said once on the branch, for what every entry below it shares: "Not moved into SHARED: ..." */
  notes: string[];
}

type Row = Branch | Leaf;

const sizeKey = "relatude.changesDialog.size";
const minSize = { w: 560, h: 380 };
// a branch with no more entries than this below it opens whole at first; a larger list opens to its kinds
const openAllUpTo = 40;

/**
 * Everything THIS SERVER holds that SHARED does not, as one tree: the server and each database, under
 * them each kind (settings, a custom set, datamodel overrides, logs, GraphQL endpoints, runtime types),
 * under those a settings section or a type where the entries have one, and the entries. What is chosen
 * can be downloaded - a zip of the application's files with it moved in, nothing here changed - moved
 * into SHARED, or discarded, each with the command of the entry's kind and the questions that kind's
 * page always asked. The dialog can be made larger by any edge or corner, and keeps its size.
 */
function ChangesDialog({ request }: { request: ChangesDialogState }) {
  const [list, setList] = useState<ExportList | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [open, setOpen] = useState<Set<string>>(new Set());
  const [focusKey, setFocusKey] = useState<string | null>(null);
  const editorsVersion = useDatamodelEditors();
  const treeRef = useRef<HTMLDivElement>(null);
  const rowRefs = useRef(new Map<string, HTMLDivElement>());
  const started = useRef(false);
  // a row the keyboard moved to, to focus once it is drawn; a branch to scroll to the top of the list
  const focusPending = useRef(false);
  const scrollTo = useRef<string | null>(null);

  const load = useCallback(() => {
    return fetchOverridesExport()
      .then((l) => {
        setList(l);
        setError(null);
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, []);
  useEffect(() => {
    void load();
  }, [load]);

  // eslint-disable-next-line react-hooks/exhaustive-deps
  const tree = useMemo(() => (list ? buildTree(list) : []), [list, editorsVersion]);
  const leaves = useMemo(() => tree.flatMap((b) => b.all), [tree]);
  const byId = useMemo(() => new Map(leaves.map((l) => [l.entry.id, l])), [leaves]);
  const pickable = (l: Leaf) => l.can.download || l.can.move || l.can.discard;

  // the first time the list is there: what is open, chosen and in sight comes from the page that opened it
  useEffect(() => {
    if (!list || started.current) return;
    started.current = true;
    const focused = request.focus ? leaves.filter((l) => request.focus!(l.entry, l.group)) : [];
    const nextOpen = new Set<string>();
    if (focused.length > 0) {
      // the page's own branch, down to its entries; everything else closed
      for (const l of focused) for (const key of ancestorsOf(l.key)) nextOpen.add(key);
      const first = firstBranchHolding(tree, focused[0].key);
      scrollTo.current = first;
      setFocusKey(first);
    } else {
      const small = leaves.length <= openAllUpTo;
      walk(tree, (b) => {
        if (small || b.level <= 2) nextOpen.add(b.key);
      });
      setFocusKey(tree[0]?.key ?? null);
      if (request.focus && request.focusTitle && leaves.length > 0) setMessage(`Nothing of ${request.focusTitle} is held on THIS SERVER: it all comes from SHARED.`);
    }
    setOpen(nextOpen);
    if (request.intent === "download") setSelected(new Set(leaves.filter((l) => l.can.download && !l.entry.secret).map((l) => l.entry.id)));
    focusPending.current = true;
  }, [list, leaves, tree, request]);

  // what is no longer listed can no longer be chosen
  useEffect(() => {
    setSelected((prev) => {
      const still = Array.from(prev).filter((id) => byId.has(id) && pickable(byId.get(id)!));
      return still.length === prev.size ? prev : new Set(still);
    });
  }, [byId]);

  const rows = useMemo(() => visibleRows(tree, open), [tree, open]);

  // a row that went - moved, discarded, closed away under its branch - hands the keyboard to the nearest one shown
  useEffect(() => {
    // none yet: the first look at the list sets it, in the same pass
    if (!focusKey || rows.length === 0 || rows.some((r) => r.key === focusKey)) return;
    let key: string | null = focusKey;
    while (key && !rows.some((r) => r.key === key)) key = parentKeyOf(key);
    if (document.activeElement === document.body) focusPending.current = true;
    setFocusKey(key ?? rows[0].key);
  }, [rows, focusKey]);

  // keyboard focus follows focusKey when the keyboard moved it; the branch a page opened it on goes to the top
  useLayoutEffect(() => {
    if (scrollTo.current && treeRef.current) {
      const row = rowRefs.current.get(scrollTo.current);
      if (row) {
        // just under its database, whose row stays at the top of the list as it scrolls
        const parent = parentKeyOf(scrollTo.current);
        const above = parent ? (rowRefs.current.get(parent)?.offsetHeight ?? 0) : 0;
        treeRef.current.scrollTop = Math.max(0, row.offsetTop - above);
        scrollTo.current = null;
      }
    }
    if (focusPending.current && focusKey) {
      const row = rowRefs.current.get(focusKey);
      if (row) {
        row.focus({ preventScroll: true });
        row.scrollIntoView({ block: "nearest" });
        focusPending.current = false;
      }
    }
  });

  const chosen = leaves.filter((l) => selected.has(l.entry.id) && pickable(l));
  const allPickable = leaves.filter(pickable);
  const allChosen = allPickable.length > 0 && allPickable.every((l) => selected.has(l.entry.id));

  function choose(ids: string[], on: boolean): void {
    setSelected((prev) => {
      const next = new Set(prev);
      for (const id of ids) {
        if (on) next.add(id);
        else next.delete(id);
      }
      return next;
    });
  }
  function toggleOpen(key: string, to?: boolean): void {
    setOpen((prev) => {
      const next = new Set(prev);
      if (to ?? !next.has(key)) next.add(key);
      else next.delete(key);
      return next;
    });
  }
  const stateOf = (b: Branch): "all" | "some" | "none" => {
    const ids = b.all.filter(pickable).map((l) => l.entry.id);
    const n = ids.filter((id) => selected.has(id)).length;
    return n === 0 ? "none" : n === ids.length ? "all" : "some";
  };
  const toggleRow = (row: Row) => {
    if (busy) return;
    if (row.kind === "leaf") {
      if (pickable(row)) choose([row.entry.id], !selected.has(row.entry.id));
    } else {
      const ids = row.all.filter(pickable).map((l) => l.entry.id);
      if (ids.length > 0) choose(ids, stateOf(row) !== "all");
    }
  };

  // ---- the tree's keys, as a tree view has them ----

  function onTreeKey(e: KeyboardEvent<HTMLDivElement>): void {
    const target = e.target as HTMLElement;
    // keys in a button or field inside an entry (Show the change) are theirs
    if (target.getAttribute("role") !== "treeitem") return;
    const index = rows.findIndex((r) => r.key === focusKey);
    if (index < 0) return;
    const row = rows[index];
    const go = (i: number) => {
      const next = rows[Math.max(0, Math.min(rows.length - 1, i))];
      if (next) {
        focusPending.current = true;
        setFocusKey(next.key);
      }
    };
    switch (e.key) {
      case "ArrowDown":
        go(index + 1);
        break;
      case "ArrowUp":
        go(index - 1);
        break;
      case "Home":
        go(0);
        break;
      case "End":
        go(rows.length - 1);
        break;
      case "ArrowRight":
        if (row.kind === "branch") {
          if (!open.has(row.key)) toggleOpen(row.key, true);
          else go(index + 1);
        }
        break;
      case "ArrowLeft":
        if (row.kind === "branch" && open.has(row.key)) toggleOpen(row.key, false);
        else {
          const parent = parentKeyOf(row.key);
          if (parent) {
            focusPending.current = true;
            setFocusKey(parent);
          }
        }
        break;
      case "Enter":
        if (row.kind === "branch") toggleOpen(row.key);
        else toggleRow(row);
        break;
      case " ":
        toggleRow(row);
        break;
      case "*": {
        // every branch beside this one, opened
        const parent = parentKeyOf(row.key);
        setOpen((prev) => new Set([...prev, ...rows.filter((r) => r.kind === "branch" && parentKeyOf(r.key) === parent).map((r) => r.key)]));
        break;
      }
      default:
        return;
    }
    e.preventDefault();
  }

  // ---- the actions ----

  const forDownload = chosen.filter((l) => l.can.download);
  const forMove = chosen.filter((l) => l.can.move);
  const forDiscard = chosen.filter((l) => l.can.discard);

  async function settle(done: string, problems: string[]): Promise<void> {
    notifyChangesMade();
    request.onChanged?.();
    await load();
    setMessage(done);
    if (problems.length > 0) await showError("Not everything was done", done, problems);
  }

  async function download(): Promise<void> {
    if (forDownload.length === 0) return;
    setBusy(true);
    try {
      await downloadOverridesExport(forDownload.map((l) => l.entry.id));
      const secrets = forDownload.filter((l) => l.entry.secret).length;
      setMessage(
        `${count(forDownload.length, "change")} downloaded. Unzip over the project, review the diff and deploy.` +
          (secrets > 0 ? ` ${count(secrets, "of them is a secret", "of them are secrets")}: keep ${secrets === 1 ? "it" : "them"} out of source control.` : ""),
      );
    } catch (e) {
      await showError("Could not download the changes", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  async function move(): Promise<void> {
    if (!list || forMove.length === 0) return;
    const parts = partsOf(forMove);
    const secrets = forMove.filter((l) => l.entry.secret).length;
    const rewrites = parts.some((p) => p.group.kind === "settings" || p.group.kind === "custom" || p.group.kind === "datamodel");
    const definitions = parts.some((p) => p.group.kind === "logs" || p.group.kind === "graphql");
    const { ok } = await showConfirm(
      `Move ${count(forMove.length, "change")} into SHARED?`,
      [
        `${forMove.length === 1 ? "It is" : "They are"} written into SHARED - part of the application, which every installation has - and taken off THIS SERVER. Nothing that is running changes: the same values then come from SHARED, so no database is reopened.`,
        rewrites ? "A settings file or datamodel.json moved into is rewritten as a whole, so comments in it are not kept." : "",
        definitions ? "A log or endpoint taken away here has its SHARED file deleted." : "",
        secrets > 0
          ? `${count(secrets, "of them is a secret", "of them are secrets")}, which SHARED then holds in plain text. Keep that file out of source control, or leave secrets to the configuration section.`
          : "",
        list.development ? `Commit what was moved to take it to the other installations.` : deployedCopyWarning(list.environment, "relatude.settings", true),
      ]
        .filter(Boolean)
        .join(" "),
      { confirmLabel: "Move", danger: !list.development, details: parts.map((p) => `${p.what}: ${count(p.leaves.length, p.noun)} into ${p.group.into ?? "SHARED"}`) },
    );
    if (!ok) return;
    await run(parts, "moved into SHARED", (p) => moveEntries(p.group, p.leaves.map((l) => l.entry.id)), "Could not move");
  }

  async function discard(): Promise<void> {
    if (!list || forDiscard.length === 0) return;
    const parts = partsOf(forDiscard);
    const { ok } = await showConfirm(
      `Discard ${count(forDiscard.length, "change")} on THIS SERVER?`,
      "Each goes back to what SHARED says, and what was saved here is lost.",
      { confirmLabel: "Discard", danger: true, details: parts.map(discardSaid) },
    );
    if (!ok) return;
    await run(
      parts,
      "discarded",
      async (p) => {
        if (p.group.kind !== "datamodel") return discardEntries(p.group, p.leaves.map((l) => l.entry.id));
        const editor = datamodelEditorOf(p.group.storeId);
        if (!editor) throw new Error("The data model editor of " + (p.group.database ?? "the database") + " is no longer open.");
        return { done: await editor.discard(p.leaves.map((l) => datamodelPathOf(l.entry.id))), rejected: [] };
      },
      "Could not discard",
    );
  }

  async function run(parts: Part[], said: string, act: (p: Part) => Promise<ChangeOutcome>, failed: string): Promise<void> {
    setBusy(true);
    let done = 0;
    const problems: string[] = [];
    const inDraft = parts.some((p) => p.group.kind === "datamodel") && said === "discarded";
    try {
      for (const p of parts) {
        try {
          const outcome = await act(p);
          done += outcome.done;
          for (const r of outcome.rejected) problems.push(`${p.what}: ${byId.get(r.id)?.label ?? r.id}: ${r.reason}`);
        } catch (e) {
          problems.push(`${failed} ${p.what}: ${e instanceof Error ? e.message : String(e)}`);
        }
      }
      if (done > 0) setSelected((prev) => new Set(Array.from(prev).filter((id) => !parts.some((p) => p.leaves.some((l) => l.entry.id === id)))));
      await settle(
        `${count(done, "change")} ${said}.` + (inDraft && done > 0 ? " Datamodel overrides were discarded in the draft: activate it to apply the change." : ""),
        problems,
      );
    } finally {
      setBusy(false);
    }
  }

  // ---- resizing ----

  const [size, setSize] = useState<{ w: number; h: number } | null>(readSize);
  const sizeRef = useRef(size);
  sizeRef.current = size;
  const dialogRef = useRef<HTMLDivElement>(null);
  const drag = useRef<{ edge: string; x: number; y: number; w: number; h: number } | null>(null);
  const grip = (edge: string) => ({
    className: "changes-grip " + edge,
    onPointerDown: (e: PointerEvent<HTMLDivElement>) => {
      if (e.button !== 0 || !dialogRef.current) return;
      const r = dialogRef.current.getBoundingClientRect();
      drag.current = { edge, x: e.clientX, y: e.clientY, w: r.width, h: r.height };
      e.currentTarget.setPointerCapture(e.pointerId);
      e.preventDefault();
    },
    onPointerMove: (e: PointerEvent<HTMLDivElement>) => {
      const d = drag.current;
      if (!d) return;
      // the dialog stays in the middle, so an edge moves half of what the size does: twice the pointer's way
      const dx = (e.clientX - d.x) * 2;
      const dy = (e.clientY - d.y) * 2;
      setSize(
        clampSize({
          w: d.w + (d.edge.includes("e") ? dx : d.edge.includes("w") ? -dx : 0),
          h: d.h + (d.edge.includes("s") ? dy : d.edge.includes("n") ? -dy : 0),
        }),
      );
    },
    onPointerUp: () => {
      if (!drag.current) return;
      drag.current = null;
      saveSize(sizeRef.current);
    },
    onLostPointerCapture: () => {
      if (drag.current) saveSize(sizeRef.current);
      drag.current = null;
    },
    onDoubleClick: () => {
      setSize(null);
      saveSize(null);
    },
    title: "Drag to make the dialog larger or smaller; double-click for its usual size",
  });
  // a window made smaller keeps the dialog on it
  useEffect(() => {
    const fit = () => setSize((s) => (s ? clampSize(s) : s));
    window.addEventListener("resize", fit);
    return () => window.removeEventListener("resize", fit);
  }, []);

  const close = () => {
    if (!busy) closeChanges();
  };
  const editorWarnings = list
    ? Array.from(new Set(list.groups.filter((g) => g.kind === "datamodel").map((g) => datamodelEditorOf(g.storeId)?.warning).filter((w): w is string => !!w)))
    : [];
  const warnings = [...(list?.problems ?? []), ...(list && !list.development ? [deployedCopyWarning(list.environment, "relatude.settings", true)] : []), ...editorWarnings];
  const primary = request.intent === "download" ? "download" : "move";

  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && close()}>
      <div
        ref={dialogRef}
        className={"dialog changes-dialog" + (size ? " sized" : "")}
        style={size ? { width: size.w, height: size.h } : undefined}
        role="dialog"
        aria-modal="true"
        aria-label="Changes on this server"
        onKeyDown={(e) => {
          if (e.key === "Escape" && !busy) {
            e.preventDefault();
            close();
          }
        }}
      >
        <h3>
          <ShareDialogTitle what={request.focusTitle ?? list?.server ?? "this server"} lead="Changes on this server" icon={<IconFileImport size={17} stroke={1.8} className="share-button-icon" />}>
            <DialogTools onClose={close} />
          </ShareDialogTitle>
        </h3>
        {error && <div className="dialog-body dialog-title-error">{error}</div>}
        {!error && !list && <Loading label="Reading what was changed here…" />}
        {list && (
          <>
            <div className="changes-top">
              <div className="dialog-body overrides-intro">
                What was changed in the admin UI on this installation is kept on THIS SERVER, apart from SHARED - <code>relatude.settings</code> and{" "}
                <code>relatude.db.json</code>, part of the application, in source control and deployed with it. Choose entries, then <b>move</b> them into SHARED to make
                them part of the application, <b>download</b> the application's files with them moved in to put into the project by hand (nothing here changes), or{" "}
                <b>discard</b> them to go back to what SHARED says.
              </div>
              {warnings.map((w, i) => (
                <div className="dm-notice warn" key={i}>
                  <IconAlertTriangle size={15} stroke={1.8} />
                  <span>{w}</span>
                </div>
              ))}
            </div>
            {leaves.length === 0 ? (
              <div className="overrides-empty">Nothing was changed in the admin UI on this installation: everything comes from the application's own files.</div>
            ) : (
              <>
                <div className="overrides-toolbar changes-toolbar">
                  <label className="settings-check">
                    <input type="checkbox" checked={allChosen} disabled={busy || allPickable.length === 0} onChange={(e) => choose(allPickable.map((l) => l.entry.id), e.target.checked)} />
                    Select all
                  </label>
                  <span className="muted">
                    {chosen.length} of {leaves.length} chosen
                  </span>
                  <span className="header-spacer" />
                  <button type="button" className="changes-tool" onClick={() => setOpen(new Set(allBranches(tree)))} title="Open every branch">
                    <IconChevronsDown size={14} stroke={1.8} /> Expand all
                  </button>
                  <button
                    type="button"
                    className="changes-tool"
                    onClick={() => setOpen(new Set())}
                    title="Close every branch"
                  >
                    <IconChevronsUp size={14} stroke={1.8} /> Collapse all
                  </button>
                </div>
                <div className="changes-tree" role="tree" aria-multiselectable="true" aria-label="What THIS SERVER holds" ref={treeRef} onKeyDown={onTreeKey}>
                  {rows.map((row) =>
                    row.kind === "branch" ? (
                      <BranchRow
                        key={row.key}
                        branch={row}
                        isOpen={open.has(row.key)}
                        state={stateOf(row)}
                        chosenCount={row.all.filter((l) => selected.has(l.entry.id) && pickable(l)).length}
                        focused={row.key === focusKey}
                        busy={busy}
                        refOf={(el) => setRowRef(rowRefs.current, row.key, el)}
                        onFocus={() => setFocusKey(row.key)}
                        onToggleOpen={() => toggleOpen(row.key)}
                        onToggle={() => toggleRow(row)}
                      />
                    ) : (
                      <LeafRow
                        key={row.key}
                        leaf={row}
                        checked={selected.has(row.entry.id)}
                        focused={row.key === focusKey}
                        busy={busy}
                        refOf={(el) => setRowRef(rowRefs.current, row.key, el)}
                        onFocus={() => setFocusKey(row.key)}
                        onToggle={() => toggleRow(row)}
                      />
                    ),
                  )}
                </div>
              </>
            )}
          </>
        )}
        {/* its own line: the buttons leave no room beside them for a sentence naming a file */}
        {message && <div className="dialog-body muted changes-message">{message}</div>}
        <div className="dialog-row">
          <div className="header-spacer" />
          <button className="action-button danger" disabled={busy || !list || forDiscard.length === 0} title="Back to what SHARED says, on THIS SERVER" onClick={() => void discard()}>
            <IconArrowBackUp size={14} stroke={1.8} /> Discard{forDiscard.length > 0 ? " " + forDiscard.length : ""}
          </button>
          <button
            className={"action-button" + (primary === "download" ? " dialog-confirm primary" : "")}
            disabled={busy || !list || forDownload.length === 0}
            title="A zip of the application's files with the changes chosen moved in, to put into the project by hand. Nothing here changes."
            onClick={() => void download()}
          >
            <IconDownload size={14} stroke={1.8} /> Download{forDownload.length > 0 ? " " + forDownload.length : ""}
          </button>
          <button
            className={"action-button" + (primary === "move" ? " dialog-confirm primary" : "")}
            disabled={busy || !list || forMove.length === 0}
            title="Into SHARED, part of the application; taken off THIS SERVER"
            onClick={() => void move()}
          >
            <IconFileImport size={14} stroke={1.8} /> Move{forMove.length > 0 ? " " + forMove.length : ""} into shared
          </button>
          <button className="action-button" onClick={close} disabled={busy}>
            Close
          </button>
        </div>
        {["n", "s", "e", "w", "ne", "nw", "se", "sw"].map((edge) => (
          <div key={edge} {...grip(edge)} aria-hidden="true" />
        ))}
      </div>
    </div>
  );
}

// ---- rows ----

function BranchRow({
  branch,
  isOpen,
  state,
  chosenCount,
  focused,
  busy,
  refOf,
  onFocus,
  onToggleOpen,
  onToggle,
}: {
  branch: Branch;
  isOpen: boolean;
  state: "all" | "some" | "none";
  chosenCount: number;
  focused: boolean;
  busy: boolean;
  refOf: (el: HTMLDivElement | null) => void;
  onFocus: () => void;
  onToggleOpen: () => void;
  onToggle: () => void;
}) {
  const box = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (box.current) box.current.indeterminate = state === "some";
  }, [state]);
  const Icon = branch.icon;
  const total = branch.all.length;
  return (
    <div
      ref={refOf}
      role="treeitem"
      aria-level={branch.level}
      aria-expanded={isOpen}
      aria-checked={state === "all" ? true : state === "some" ? "mixed" : false}
      tabIndex={focused ? 0 : -1}
      className={"changes-row branch level-" + branch.level + (isOpen ? " open" : "")}
      style={{ "--depth": branch.level - 1 } as CSSProperties}
      title={branch.title}
      onFocus={(e) => e.target === e.currentTarget && onFocus()}
      onClick={(e) => {
        if ((e.target as HTMLElement).closest("input")) return;
        onFocus();
        onToggleOpen();
      }}
    >
      <span className="changes-chevron" aria-hidden="true">
        <IconChevronRight size={14} stroke={2} />
      </span>
      <input
        ref={box}
        type="checkbox"
        tabIndex={-1}
        checked={state === "all"}
        disabled={busy || branch.all.every((l) => !(l.can.download || l.can.move || l.can.discard))}
        onChange={onToggle}
        aria-label={"Choose everything in " + branch.label}
      />
      <span className="changes-branch-text">
        <span className="changes-branch-label">
          <Icon size={branch.level === 1 ? 16 : 15} stroke={1.8} className="changes-icon" />
          <span className="changes-name">{branch.label}</span>
          <span className="changes-count">{chosenCount > 0 ? `${chosenCount} of ${total} chosen` : total}</span>
        </span>
        {isOpen &&
          branch.notes.map((n, i) => (
            <span className="changes-note" key={i}>
              {n}
            </span>
          ))}
      </span>
    </div>
  );
}

const changeWords: Record<ExportEntry["change"], string | null> = {
  changed: null,
  added: "added here",
  removed: null,
  same: "same as shared",
  file: "file",
};

function LeafRow({
  leaf,
  checked,
  focused,
  busy,
  refOf,
  onFocus,
  onToggle,
}: {
  leaf: Leaf;
  checked: boolean;
  focused: boolean;
  busy: boolean;
  refOf: (el: HTMLDivElement | null) => void;
  onFocus: () => void;
  onToggle: () => void;
}) {
  const { entry, can } = leaf;
  const pickable = can.download || can.move || can.discard;
  const blocked = Object.entries(can.why).filter((x): x is [string, string] => !!x[1]);
  return (
    <div
      ref={refOf}
      role="treeitem"
      aria-level={leaf.level}
      aria-checked={checked}
      aria-disabled={!pickable || undefined}
      tabIndex={focused ? 0 : -1}
      className={"changes-row leaf" + (pickable ? "" : " blocked") + (checked ? " chosen" : "")}
      style={{ "--depth": leaf.level - 1 } as CSSProperties}
      onFocus={(e) => e.target === e.currentTarget && onFocus()}
      onClick={(e) => {
        // what is in the entry - Show the change, the diff - is not a click on the entry
        if ((e.target as HTMLElement).closest("button, a, input, .changes-detail")) return;
        onFocus();
        onToggle();
      }}
    >
      <span className="changes-chevron empty" aria-hidden="true" />
      <input type="checkbox" tabIndex={-1} checked={pickable && checked} disabled={busy || !pickable} onChange={onToggle} aria-label={"Choose " + leaf.label} />
      <div className="overrides-entry-text">
        <div className="overrides-entry-label">
          <span>{leaf.label}</span>
          {changeWords[entry.change] && <span className={"setting-badge " + (entry.change === "same" ? "faint" : "overrides")}>{changeWords[entry.change]}</span>}
          {entry.change === "removed" && <RemovedTag small title="THIS SERVER takes this away on this installation" />}
          {entry.secret && <span className="setting-badge faint">secret</span>}
        </div>
        {entry.where && entry.where !== entry.section && <div className="overrides-entry-where">{entry.where}</div>}
        <div className="overrides-entry-values changes-detail">
          <Change entry={entry} />
        </div>
        <div className="overrides-entry-note">
          <span className="muted">
            into <code>{entry.file}</code>
          </span>
        </div>
        {!pickable && blocked.length > 0 && (
          <div className="overrides-entry-note">
            <IconLock size={12} stroke={1.8} /> {blocked[0][1]}
          </div>
        )}
        {pickable &&
          blocked.map(([action, why]) => (
            <div className="overrides-entry-note" key={action}>
              <span>
                Not {action === "download" ? "downloaded" : action === "move" ? "moved" : "discarded"}: {why}
              </span>
            </div>
          ))}
      </div>
    </div>
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
        <DataValue side="data">{entry.change === "removed" && entry.after == null ? "taken away" : (entry.after ?? "not set")}</DataValue>
      </>
    );
  }
  return (
    <div className="overrides-export-change">
      <button type="button" className="link-button" onClick={() => setOpen(!open)} aria-expanded={open}>
        {open ? "Hide the change" : "Show the change"}
      </button>
      {open && <DiffView a={before} b={after} granularity="lines" split={false} />}
    </div>
  );
}

// ---- the tree ----

/** Keys are paths: "s:<store>" / "a:<kind>:<set>" / "c:<section>" / "e:<id>", so a key says its parents. */
const join = (parent: string, part: string) => parent + "/" + part.replace(/\//g, "∕");
function parentKeyOf(key: string): string | null {
  const at = key.lastIndexOf("/");
  return at < 0 ? null : key.slice(0, at);
}
function ancestorsOf(key: string): string[] {
  const result: string[] = [];
  for (let k = parentKeyOf(key); k; k = parentKeyOf(k)) result.push(k);
  return result;
}

function buildTree(list: ExportList): Branch[] {
  const scopes = new Map<string, Branch>();
  const branch = (key: string, level: number, label: string, icon: Icon, title?: string): Branch => ({
    kind: "branch",
    key,
    level,
    label,
    icon,
    title,
    children: [],
    leaves: [],
    all: [],
    notes: [],
  });
  for (const group of list.groups) {
    const scopeKey = "s:" + (group.storeId ?? "server");
    let scope = scopes.get(scopeKey);
    if (!scope) {
      scope = group.storeId
        ? branch(scopeKey, 1, group.database ?? group.storeId, IconDatabase, "What THIS SERVER holds for the database " + (group.database ?? group.storeId))
        : branch(scopeKey, 1, "Server", IconServer, "What THIS SERVER holds for the server itself");
      scopes.set(scopeKey, scope);
    }
    const areaKey = join(scopeKey, "a:" + group.kind + ":" + (group.key ?? ""));
    let area = scope.children.find((b) => b.key === areaKey);
    if (!area) {
      const icon = group.kind === "settings" && !group.storeId ? IconAdjustments : kindIcons[group.kind];
      area = branch(areaKey, 2, group.area, icon, group.from ? `Kept on THIS SERVER in ${group.from}` + (group.into ? `; moved into ${group.into}` : "") : undefined);
      scope.children.push(area);
    }
    const editor = group.kind === "datamodel" ? datamodelEditorOf(group.storeId) : null;
    for (const entry of group.entries) {
      let holder = area;
      if (entry.section) {
        const sectionKey = join(areaKey, "c:" + entry.section);
        holder = area.children.find((b) => b.key === sectionKey) ?? area.children[area.children.push(branch(sectionKey, 3, entry.section, IconFolder)) - 1];
      }
      const discardWhy =
        entry.discardBlocked ??
        (group.kind === "datamodel" && !editor
          ? `discarding changes the draft of the model, which the Datamodel page of ${group.database ?? "the database"} holds: open this dialog from its Move to shared button there.`
          : null);
      holder.leaves.push({
        kind: "leaf",
        key: join(holder.key, "e:" + entry.id),
        level: holder.level + 1,
        entry,
        group,
        label: labelOf(entry, group),
        can: {
          download: !entry.blocked,
          move: !entry.moveBlocked && group.kind !== "types",
          discard: !discardWhy && group.kind !== "types",
          why: { download: entry.blocked, move: entry.moveBlocked, discard: discardWhy },
        },
      });
    }
  }
  const server = scopes.get("s:server");
  const databases = Array.from(scopes.values())
    .filter((s) => s !== server)
    .sort((a, b) => a.label.localeCompare(b.label));
  const tree = [...(server ? [server] : []), ...databases];
  const fill = (b: Branch): Leaf[] => {
    b.all = [...b.children.flatMap(fill), ...b.leaves];
    return b.all;
  };
  tree.forEach(fill);
  // a reason every entry of a kind shares is said once, on the kind, rather than on each entry
  for (const scope of tree) {
    for (const area of scope.children) {
      for (const action of ["download", "move", "discard"] as const) {
        const reasons = new Set(area.all.map((l) => l.can.why[action] ?? null));
        const [only] = reasons;
        if (reasons.size === 1 && only) {
          area.notes.push(`Not ${action === "download" ? "downloaded" : action === "move" ? "moved into SHARED" : "discarded"}: ${only}`);
          for (const l of area.all) l.can.why = { ...l.can.why, [action]: null };
        }
      }
    }
  }
  return tree;
}

// a datamodel override under its type's branch, without the type's name in front
function labelOf(entry: ExportEntry, group: ExportGroup): string {
  if (group.kind !== "datamodel" || !entry.section) return entry.label;
  const short = entry.section.slice(entry.section.lastIndexOf(".") + 1);
  if (entry.label.startsWith(short + ".")) return entry.label.slice(short.length + 1);
  if (entry.label.startsWith(short + ": ")) return entry.label.slice(short.length + 2);
  return entry.label;
}

function visibleRows(tree: Branch[], open: Set<string>): Row[] {
  const rows: Row[] = [];
  const add = (b: Branch) => {
    rows.push(b);
    if (!open.has(b.key)) return;
    b.children.forEach(add);
    rows.push(...b.leaves);
  };
  tree.forEach(add);
  return rows;
}

function walk(tree: Branch[], visit: (b: Branch) => void): void {
  for (const b of tree) {
    visit(b);
    walk(b.children, visit);
  }
}
function allBranches(tree: Branch[]): string[] {
  const keys: string[] = [];
  walk(tree, (b) => keys.push(b.key));
  return keys;
}
// the kind an entry is listed under: what a page's button opens the dialog on
function firstBranchHolding(tree: Branch[], leafKey: string): string {
  const ancestors = ancestorsOf(leafKey);
  // [section?, area, scope]: the area is the second from the top
  const area = ancestors[ancestors.length - 2] ?? ancestors[0];
  return area ?? tree[0]?.key ?? leafKey;
}

function setRowRef(map: Map<string, HTMLDivElement>, key: string, el: HTMLDivElement | null): void {
  if (el) map.set(key, el);
  else map.delete(key);
}

// ---- the actions' parts ----

/** The entries of one group, done with one command. */
interface Part {
  group: ExportGroup;
  leaves: Leaf[];
  /** "Settings of Main", for the questions and what went wrong */
  what: string;
  noun: string;
}

function partsOf(leaves: Leaf[]): Part[] {
  const parts = new Map<ExportGroup, Leaf[]>();
  for (const l of leaves) parts.set(l.group, [...(parts.get(l.group) ?? []), l]);
  return Array.from(parts.entries()).map(([group, items]) => ({
    group,
    leaves: items,
    what: group.area + (group.database ? " of " + group.database : group.kind === "settings" ? " of the server" : ""),
    noun: nouns[group.kind],
  }));
}

const nouns: Record<ExportKind, string> = {
  settings: "setting",
  custom: "setting",
  datamodel: "override",
  logs: "log definition",
  graphql: "endpoint definition",
  types: "runtime type",
};

// what discarding does, kind by kind: what the dialogs of each page said
function discardSaid(p: Part): string {
  const n = count(p.leaves.length, p.noun);
  const added = p.leaves.filter((l) => l.entry.change === "added").length;
  const removed = p.leaves.filter((l) => l.entry.change === "removed").length;
  switch (p.group.kind) {
    case "settings":
    case "custom":
      return `${p.what}: ${n} back to what ${p.group.into ?? "SHARED"} says. A setting that only applies when a database opens takes effect at its next open.`;
    case "datamodel":
      return `${p.what}: ${n} back to the SHARED override, or to what the source says where SHARED has none. This changes the draft: activate it to apply the change.`;
    case "logs":
      return (
        `${p.what}: ${n} back to SHARED; entries are moved as a change of definition would move them.` +
        (added > 0 ? ` ${count(added, "log is", "logs are")} only on THIS SERVER and ${added === 1 ? "goes" : "go"}: what ${added === 1 ? "it" : "they"} recorded is kept.` : "") +
        (removed > 0 ? ` ${count(removed, "log")} taken away here ${removed === 1 ? "comes" : "come"} back.` : "")
      );
    case "graphql":
      return (
        `${p.what}: ${n} back to SHARED.` +
        (added > 0 ? ` ${count(added, "endpoint is", "endpoints are")} only on THIS SERVER and ${added === 1 ? "is" : "are"} no longer served.` : "") +
        (removed > 0 ? ` ${count(removed, "endpoint")} taken away here ${removed === 1 ? "is" : "are"} served again.` : "")
      );
    case "types":
      return `${p.what}: ${n}`;
  }
}

// ---- the size, kept for the next time ----

function readSize(): { w: number; h: number } | null {
  try {
    const saved = JSON.parse(localStorage.getItem(sizeKey) ?? "null");
    return saved && typeof saved.w === "number" && typeof saved.h === "number" ? clampSize(saved) : null;
  } catch {
    return null;
  }
}
function saveSize(size: { w: number; h: number } | null): void {
  try {
    if (size) localStorage.setItem(sizeKey, JSON.stringify({ w: Math.round(size.w), h: Math.round(size.h) }));
    else localStorage.removeItem(sizeKey);
  } catch {
    // the size is a convenience: a browser that keeps nothing still has the dialog
  }
}
function clampSize(size: { w: number; h: number }): { w: number; h: number } {
  const maxW = Math.max(minSize.w, window.innerWidth - 24);
  const maxH = Math.max(minSize.h, window.innerHeight - 24);
  return { w: Math.min(maxW, Math.max(minSize.w, size.w)), h: Math.min(maxH, Math.max(minSize.h, size.h)) };
}

