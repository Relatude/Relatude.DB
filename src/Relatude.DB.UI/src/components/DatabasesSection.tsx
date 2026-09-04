import { useCallback, useEffect, useRef, useState } from "react";
import { IconCheck, IconDatabasePlus, IconPlayerPlayFilled, IconPlayerStopFilled, IconPlus, IconRefresh, IconStar, IconStarFilled } from "@tabler/icons-react";
import { PanelGrid, type PanelRow } from "./PanelGrid";
import { showConfirm, showError, showInfo } from "../dialogs";
import { subscribe, subscribeResync } from "../server/channel";
import { createDatabase, fetchDatabases, setDefaultDatabase, type DatabaseList, type DatabaseRow } from "../server/databases";
import { closeStore, openStore } from "../server/storage";
import { usePoll } from "../refresh";
import { formatCount } from "../format";

/**
 * Every database on this server, and the things that are decided about a database rather than inside
 * one: whether it runs, which one applications get when they name none, and adding another.
 *
 * This list used to be a panel on the server overview. It moved here because all three of those are
 * actions, and an action wants room - a row that can carry a labelled button, a default marker and
 * what the database is made of, rather than a table cell with an icon in it.
 */
export function DatabasesSection({ onSelectDb }: { onSelectDb?: (id: string) => void }) {
  const [data, setData] = useState<DatabaseList | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => {
    fetchDatabases()
      .then((d) => {
        setData(d);
        setError(null);
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, []);
  useEffect(() => {
    load();
    const unsubscribeResync = subscribeResync(load);
    // the container watch broadcasts every state change, so a database opening elsewhere lands here
    const unsubscribeContainers = subscribe("containers", load);
    return () => {
      unsubscribeResync();
      unsubscribeContainers();
    };
  }, [load]);
  usePoll(load, { minMs: 5000 });

  if (error) return <div className="placeholder">{error}</div>;
  if (!data) return null;

  const open = data.databases.filter((d) => d.state === "Open").length;

  const listPanel = (
    <section className="panel">
      <h3>
        Databases{" "}
        <span className="panel-sub">
          {data.databases.length} · {open} open
        </span>
        <button className="icon-button storage-refresh" title="Refresh" onClick={load}>
          <IconRefresh size={14} stroke={1.8} />
        </button>
      </h3>
      <div className="db-list">
        {data.databases.map((db) => (
          <DatabaseCard
            key={db.id}
            db={db}
            defaultLocked={data.defaultLocked}
            configSection={data.configSection ?? null}
            onDone={load}
            onOpenSection={onSelectDb}
            onApply={setData}
          />
        ))}
        {data.databases.length === 0 && <div className="muted">No databases on this server yet.</div>}
      </div>
    </section>
  );

  const rows: PanelRow[] = [
    { id: "list", cells: [listPanel] },
    { id: "new", cells: [<NewDatabase key="new" onCreated={setData} />] },
  ];

  return (
    <div className="databases">
      <PanelGrid id="databases" rows={rows} />
    </div>
  );
}

/**
 * One database. The two things that change what the server does - running or not, default or not -
 * are the two buttons on the right, said in words: an icon alone never says which way it is about to
 * go, and closing a database takes an application offline.
 */
function DatabaseCard({
  db,
  defaultLocked,
  configSection,
  onDone,
  onOpenSection,
  onApply,
}: {
  db: DatabaseRow;
  defaultLocked: boolean;
  configSection: string | null;
  onDone: () => void;
  onOpenSection?: (id: string) => void;
  onApply: (list: DatabaseList) => void;
}) {
  const [busy, setBusy] = useState<"toggle" | "default" | null>(null);
  const settling = db.state === "Opening" || db.state === "Closing";
  const isOpen = db.state === "Open";

  /**
   * Opening replays the transaction log and rebuilds the indexes; closing takes the database away
   * from the application until it is opened again. Only closing asks first - the disruptive
   * direction is the one worth a dialog.
   */
  async function toggle() {
    if (isOpen) {
      const confirmed = await showConfirm(
        `Close ${db.name}?`,
        "The application cannot read or write this database until it is opened again. Anything not yet flushed is written out first, so nothing is lost.",
        { confirmLabel: "Close database", danger: true },
      );
      if (!confirmed.ok) return;
    }
    setBusy("toggle");
    try {
      await (isOpen ? closeStore(db.id) : openStore(db.id));
    } catch (e) {
      await showError(isOpen ? "Could not close the database" : "Could not open the database", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(null);
      onDone(); // a failed open leaves the database in Error, which the row should show
    }
  }

  async function makeDefault() {
    setBusy("default");
    try {
      onApply(await setDefaultDatabase(db.id));
    } catch (e) {
      await showError("Could not change the default database", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className={"db-card" + (db.isDefault ? " is-default" : "")}>
      <div className="db-card-main">
        <div className="db-card-head">
          <span className={"state-dot " + db.state.toLowerCase()} />
          <button className="db-card-name" onClick={() => onOpenSection?.(db.id)} title="Open this database in the pages on the left">
            {db.name}
          </button>
          {db.isDefault && (
            <span className="badge db-default-badge" title="What an application gets when it asks for no database in particular">
              default
            </span>
          )}
          <span className="db-card-state">{db.state}</span>
        </div>
        <div className="db-card-facts">
          <span title="Where the files of this database live">{db.storage}</span>
          <span>{isOpen ? `${formatCount(db.nodeCount ?? 0)} nodes · ${formatCount(db.relationCount ?? 0)} relations` : "not counted while closed"}</span>
          <span>
            {db.modelSources === 0 ? "no model sources" : `${db.modelSources} model source${db.modelSources === 1 ? "" : "s"}`}
            {db.autoOpen ? " · opens with the server" : " · opened by hand"}
          </span>
        </div>
        {db.startupError && <div className="db-card-error">{db.startupError}</div>}
      </div>
      <div className="db-card-actions">
        <button
          className={"big-button" + (isOpen ? " danger" : " go")}
          onClick={toggle}
          disabled={busy !== null || settling}
          title={isOpen ? "Close this database; the application can no longer reach it" : "Open this database: replays the transaction log and rebuilds the indexes"}
        >
          {isOpen ? <IconPlayerStopFilled size={16} stroke={1.8} /> : <IconPlayerPlayFilled size={16} stroke={1.8} />}
          <span>{busy === "toggle" ? (isOpen ? "Closing…" : "Opening…") : settling ? db.state + "…" : isOpen ? "Stop" : "Start"}</span>
        </button>
        <button
          className={"big-button quiet" + (db.isDefault ? " on" : "")}
          onClick={makeDefault}
          disabled={db.isDefault || defaultLocked || busy !== null}
          title={
            db.isDefault
              ? "This is the default database"
              : defaultLocked
                ? `The default database is set by configuration${configSection ? " (" + configSection + ")" : ""} and cannot be changed here`
                : "Make this the database applications get when they ask for none in particular"
          }
        >
          {db.isDefault ? <IconStarFilled size={16} stroke={1.8} /> : <IconStar size={16} stroke={1.8} />}
          <span>{db.isDefault ? "Default" : busy === "default" ? "Setting…" : "Make default"}</span>
        </button>
      </div>
    </div>
  );
}

/**
 * Adding a database. What it makes is deliberately empty: its own folder under the data folder, the
 * native engines, and no datamodel sources - the model is the Data model section's business, and one
 * guessed here would put types in a database nobody asked to have them in. It is created closed,
 * because opening writes the first files into a real folder.
 */
function NewDatabase({ onCreated }: { onCreated: (list: DatabaseList) => void }) {
  const [name, setName] = useState("");
  const [autoOpen, setAutoOpen] = useState(true);
  const [busy, setBusy] = useState(false);
  const field = useRef<HTMLInputElement>(null);

  async function create() {
    const trimmed = name.trim();
    if (trimmed.length === 0 || busy) return;
    setBusy(true);
    try {
      const result = await createDatabase(trimmed, autoOpen);
      onCreated(result.list);
      setName("");
      await showInfo(
        `"${trimmed}" created`,
        `Its files will live in ${result.folder}. It has no datamodel sources yet - add them under Data model, then start it.`,
      );
    } catch (e) {
      await showError("Could not create the database", e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
      field.current?.focus();
    }
  }

  return (
    <section className="panel">
      <h3>
        New database <span className="panel-sub">an empty one, in a folder of its own</span>
      </h3>
      <div className="db-new">
        <input
          ref={field}
          className="text-input"
          value={name}
          placeholder="Name"
          disabled={busy}
          onChange={(e) => setName(e.target.value)}
          onKeyDown={(e) => e.key === "Enter" && create()}
        />
        <label className="db-new-option" title="Whether the server opens this database by itself when it starts">
          <input type="checkbox" checked={autoOpen} disabled={busy} onChange={(e) => setAutoOpen(e.target.checked)} />
          <span>Open with the server</span>
        </label>
        <button className="big-button" onClick={create} disabled={busy || name.trim().length === 0}>
          {busy ? <IconCheck size={16} stroke={1.8} /> : <IconDatabasePlus size={16} stroke={1.8} />}
          <span>{busy ? "Creating…" : "Create database"}</span>
        </button>
      </div>
      <div className="muted db-new-note">
        <IconPlus size={13} stroke={1.8} /> The settings file gains an entry with its own storage provider. It starts closed and with no node types: choose
        its datamodel sources under Data model, then press Start.
      </div>
    </section>
  );
}
