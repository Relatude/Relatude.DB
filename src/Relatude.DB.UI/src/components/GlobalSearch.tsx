import { useEffect, useMemo, useRef, useState, type ComponentType } from "react";
import { IconArrowRight, IconCornerDownLeft, IconDatabaseSearch, IconSearch, IconSettings, IconX } from "@tabler/icons-react";
import { sections } from "../navigation";
import { openInDatamodel, openInQuery, openInSettings, openSearch, type SearchTarget } from "../navigate";
import { useLiveResult } from "../server/hooks";
import { globalSearch, minSearchLength, type SearchResults } from "../server/search";
import type { DatabaseInfo } from "../server/serverInfo";
import { KindIcon, PropertyIcon } from "./DatamodelIcons";
import type { ModelKind } from "../server/datamodel";

/** One row of the panel: what it looks like, and what opening it does. */
interface Hit {
  key: string;
  group: string;
  icon: React.ReactNode;
  label: string;
  /** the muted line under the label: where the thing is, or what it is */
  hint: string;
  open: () => void;
}

/**
 * The search box of the top bar: one word, and everything the admin UI can open that mentions it.
 *
 * It stands where the page title used to, because a title says what you are already looking at and
 * this is how you get somewhere else. Four kinds of thing answer, and each is looked for where it
 * lives: the pages by their own names, which are in the client (navigation.ts); the data model and
 * the settings catalog on the server, which holds both; and the nodes through the database's text
 * index. The first group is matched as it is typed and needs no round trip at all, so the panel is
 * never empty while the rest arrive.
 *
 * Every row opens something, and opening is a hand-off through navigate.ts rather than a route: the
 * shell switches section and the page that owns the target picks it up. Nothing here knows what a
 * settings page or a model editor is made of.
 */
export function GlobalSearch({
  activeDb,
  onSelectSection,
}: {
  activeDb: DatabaseInfo | null;
  onSelectSection: (id: string) => void;
}) {
  const [text, setText] = useState("");
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const box = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLInputElement>(null);

  const trimmed = text.trim();
  // one request per distinct search, and none at all until there is enough to look for; useLiveResult
  // keeps exactly one in flight, so typing costs the round trips that fit in the time
  const request = useMemo(
    () => (trimmed.length >= minSearchLength ? { storeId: activeDb?.id ?? null, text: trimmed } : null),
    [activeDb?.id, trimmed],
  );
  const { result, loading } = useLiveResult(request, (r) => globalSearch(r.storeId, r.text));
  // the answer to an older keystroke is still on screen while the next one runs; showing it against
  // the text it answered would be wrong, so it is only used once the two agree
  const answer: SearchResults | null = result && result.text === trimmed ? result : null;

  const hits = useMemo(
    () => build(trimmed, answer, activeDb, onSelectSection),
    [trimmed, answer, activeDb, onSelectSection],
  );

  // the highlight starts at the top of every new set of results
  useEffect(() => setActive(0), [trimmed, answer]);

  // ctrl/cmd+k from anywhere, which is where everyone's fingers already go
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        input.current?.focus();
        input.current?.select();
        setOpen(true);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  // a click outside closes the panel; the box itself keeps it open, so clicking into the text does
  // not throw away the results being read
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (!box.current?.contains(e.target as Node)) setOpen(false);
    };
    window.addEventListener("mousedown", onDown);
    return () => window.removeEventListener("mousedown", onDown);
  }, [open]);

  function run(hit: Hit) {
    hit.open();
    setOpen(false);
    setText(""); // the box is a way to get somewhere, not a filter left switched on
    input.current?.blur();
  }

  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key === "Escape") {
      if (text) setText("");
      else setOpen(false);
      return;
    }
    if (e.key === "ArrowDown" || (e.key === "Tab" && !e.shiftKey && open && hits.length > 0)) {
      e.preventDefault();
      setOpen(true);
      setActive((i) => Math.min(hits.length - 1, i + 1));
    } else if (e.key === "ArrowUp" || (e.key === "Tab" && e.shiftKey && open)) {
      e.preventDefault();
      setActive((i) => Math.max(0, i - 1));
    } else if (e.key === "Enter") {
      const hit = hits[active];
      if (hit) {
        e.preventDefault();
        run(hit);
      }
    }
  }

  // one heading per group, in the order the groups are built
  const groups: { name: string; hits: Hit[] }[] = [];
  for (const hit of hits) {
    const last = groups[groups.length - 1];
    if (last?.name === hit.group) last.hits.push(hit);
    else groups.push({ name: hit.group, hits: [hit] });
  }

  /**
   * The page that owns a search of its own for this kind of thing. The box here shows a handful and
   * stops; the module's own search is where the same words can be paged, sorted, filtered and kept -
   * so every group that has one offers to hand the words over rather than pretending to be it.
   */
  const handOver = (group: string): SearchTarget["section"] | null =>
    group === "Data model" ? "datamodel" : group === "Settings" ? "settings" : group === "Nodes" ? "query" : null;
  const handOverLabel: Record<SearchTarget["section"], string> = {
    datamodel: "Search the data model for this",
    settings: "Search the settings for this",
    query: "Search the nodes for this in Query & Edit",
  };

  const showPanel = open && trimmed.length > 0;
  return (
    <div className="global-search" ref={box}>
      <div className={"global-search-box" + (open ? " open" : "")}>
        <IconSearch size={17} stroke={1.8} />
        <input
          ref={input}
          className="text-input"
          value={text}
          spellCheck={false}
          placeholder="Search types, properties, settings and nodes…"
          onChange={(e) => {
            setText(e.target.value);
            setOpen(true);
          }}
          onFocus={() => setOpen(true)}
          onKeyDown={onKeyDown}
        />
        {text ? (
          <button className="icon-button" title="Clear" onClick={() => { setText(""); input.current?.focus(); }}>
            <IconX size={15} stroke={2} />
          </button>
        ) : (
          <span className="global-search-key" aria-hidden>
            ctrl K
          </span>
        )}
      </div>
      {showPanel && (
        <div className="global-search-panel">
          {groups.map((group) => (
            <section key={group.name}>
              <h4>
                {group.name}
                {handOver(group.name) && (
                  <button
                    className="global-search-more"
                    title={handOverLabel[handOver(group.name)!]}
                    onClick={() => {
                      openSearch({ section: handOver(group.name)!, text: trimmed });
                      setOpen(false);
                      setText("");
                      input.current?.blur();
                    }}
                  >
                    search all <IconArrowRight size={12} stroke={2} />
                  </button>
                )}
              </h4>
              {group.hits.map((hit) => (
                <button
                  key={hit.key}
                  className={"global-search-hit" + (hits[active]?.key === hit.key ? " active" : "")}
                  onMouseEnter={() => setActive(hits.indexOf(hit))}
                  onClick={() => run(hit)}
                >
                  <span className="global-search-icon">{hit.icon}</span>
                  <span className="global-search-text">
                    <span className="global-search-label">{hit.label}</span>
                    <span className="global-search-hint">{hit.hint}</span>
                  </span>
                  <IconCornerDownLeft className="global-search-enter" size={14} stroke={1.8} />
                </button>
              ))}
            </section>
          ))}
          {hits.length === 0 && (
            <div className="global-search-empty">
              {trimmed.length < minSearchLength
                ? `Type at least ${minSearchLength} letters.`
                : loading
                  ? "Searching…"
                  : `Nothing matches “${trimmed}”.`}
            </div>
          )}
          {hits.length > 0 && loading && <div className="global-search-empty">Searching the database…</div>}
        </div>
      )}
    </div>
  );
}

/**
 * The rows, in the order they are offered: the pages first because they are matched here and are
 * certain, then the model, then the settings, then the database's own content, which is the longest
 * list and the one that changes under you.
 */
function build(
  text: string,
  answer: SearchResults | null,
  activeDb: DatabaseInfo | null,
  onSelectSection: (id: string) => void,
): Hit[] {
  const hits: Hit[] = [];
  const lower = text.toLowerCase();
  if (lower.length === 0) return hits;

  for (const section of sections) {
    if (section.hidden) continue;
    if (section.scope === "database" && !activeDb) continue;
    if (!section.label.toLowerCase().includes(lower)) continue;
    const Icon: ComponentType<{ size?: number; stroke?: number }> = section.icon;
    hits.push({
      key: "page/" + section.id,
      group: "Pages",
      icon: <Icon size={16} stroke={1.8} />,
      label: section.label,
      hint: section.scope === "server" ? "Server" : (activeDb?.name ?? "Database"),
      open: () => onSelectSection(section.id),
    });
  }

  for (const type of answer?.types ?? []) {
    hits.push({
      key: "type/" + type.id,
      group: "Data model",
      icon: <KindIcon kind={(type.kind as ModelKind) ?? "Class"} size={16} />,
      label: type.name,
      hint: (type.isBase ? "every node type" : type.fullName) + " · open in the data model",
      open: () => openInDatamodel({ typeId: type.id }),
    });
  }
  for (const property of answer?.properties ?? []) {
    hits.push({
      key: "property/" + property.typeId + "/" + property.id,
      group: "Data model",
      icon: <PropertyIcon propertyType={property.kind.startsWith("Relation") ? "Relation" : property.kind} size={16} />,
      label: property.typeName + "." + property.name,
      hint: property.kind,
      open: () => openInDatamodel({ typeId: property.typeId, propertyId: property.id }),
    });
  }

  (answer?.settings ?? []).forEach((setting, i) => {
    hits.push({
      // Its place in the answer, not its path: a path is relative to the list it belongs to, so the
      // same one - "Name" - is a setting in several groups, and even a group can hold two of them.
      key: "setting/" + i + "/" + setting.scope + "/" + setting.path,
      group: "Settings",
      icon: <IconSettings size={16} stroke={1.8} />,
      label: setting.label,
      hint: (setting.scope === "server" ? "Server" : (activeDb?.name ?? "Database")) + " · " + setting.sectionTitle + " · " + setting.groupTitle,
      open: () =>
        openInSettings({
          scope: setting.scope,
          storeId: setting.storeId,
          sectionId: setting.sectionId,
          groupId: setting.groupId,
          path: setting.path,
        }),
    });
  });

  for (const node of answer?.nodes ?? []) {
    hits.push({
      key: "node/" + node.id,
      group: "Nodes",
      icon: <IconDatabaseSearch size={16} stroke={1.8} />,
      label: node.name,
      hint: node.typeName + " · open in query & edit",
      open: () => openInQuery({ typeId: node.typeId, nodeId: node.id }),
    });
  }
  return hits;
}
