import { useEffect, useMemo, useState } from "react";
import { IconAlertTriangle, IconArrowRight, IconLoader2, IconSparkles } from "@tabler/icons-react";
import { showInfo } from "../dialogs";
import { fetchIconsAi, suggestIcons, typeList, type IconSuggestionType, type IconSuggestions, type IconsAiStatus, type ModelJson, type NodeTypeJson } from "../server/datamodel";
import { iconSets, type IconSetInfo } from "../server/icons";
import { ownTypeSetting, setOverride } from "../server/overrides";
import type { EditorContext } from "./DatamodelEditors";
import { SourceDot } from "./DatamodelIcons";
import { DialogTools } from "./DialogTools";
import { IconPicker } from "./IconPicker";
import { TypeGlyph, TypeIcon, modelIcon } from "./TypeIcon";

type Scope = "missing" | "own" | "all";

interface Row {
  typeId: string;
  choices: string[];
  icon: string | null;
  take: boolean;
}

const setKey = "dmIconAssignSet";
const scopeKey = "dmIconAssignScope";

const hasText = (v: unknown) => typeof v === "string" && v.trim().length > 0;

function inScope(model: ModelJson, type: NodeTypeJson, scope: Scope): boolean {
  if (scope === "all") return true;
  if (scope === "own") return !hasText(ownTypeSetting(model, type.Id, "Icon").value);
  return !modelIcon(model, type.Id);
}

function describe(model: ModelJson, type: NodeTypeJson, baseTypeId: string): IconSuggestionType {
  return {
    id: type.Id,
    name: type.CodeName,
    namespace: type.Namespace ?? null,
    kind: type.ModelType,
    bases: (type.Parents ?? []).filter((p) => p !== baseTypeId).map((p) => model.NodeTypes[p]?.CodeName ?? "").filter(Boolean),
    properties: Object.values(type.Properties).map((p) => p.CodeName),
  };
}

const plural = (n: number, word: string) => `${n.toLocaleString()} ${word}${n === 1 ? "" : "s"}`;

export function IconAssignDialog({ ctx, hiddenSources, onClose }: { ctx: EditorContext; hiddenSources: Set<string>; onClose: () => void }) {
  const [catalog, setCatalog] = useState<IconSetInfo[] | null>(null);
  const [ai, setAi] = useState<IconsAiStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [prefix, setPrefix] = useState(() => localStorage.getItem(setKey) ?? "lucide");
  const [scope, setScope] = useState<Scope>(() => (localStorage.getItem(scopeKey) as Scope | null) ?? "missing");
  const [sources, setSources] = useState<Set<string>>(() => new Set(ctx.model.Sources.filter((s) => !hiddenSources.has(s.Id)).map((s) => s.Id)));
  const [running, setRunning] = useState<AbortController | null>(null);
  const [answer, setAnswer] = useState<IconSuggestions | null>(null);
  const [rows, setRows] = useState<Row[]>([]);
  const [picking, setPicking] = useState<string | null>(null);

  useEffect(() => {
    iconSets().then(setCatalog, (e: Error) => setError(e.message));
    fetchIconsAi().then(setAi, (e: Error) => setError(e.message));
  }, []);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape" && !running) onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [running, onClose]);

  const types = useMemo(() => typeList(ctx.model, ctx.baseTypeId), [ctx.model, ctx.baseTypeId]);
  const scopeCounts = useMemo(() => {
    const picked = types.filter((t) => sources.has(t.DatamodelSourceId));
    return {
      missing: picked.filter((t) => inScope(ctx.model, t, "missing")).length,
      own: picked.filter((t) => inScope(ctx.model, t, "own")).length,
      all: picked.length,
    };
  }, [types, sources, ctx.model]);
  const sourceRows = useMemo(
    () =>
      ctx.model.Sources.map((s) => ({
        source: s,
        total: types.filter((t) => t.DatamodelSourceId === s.Id).length,
        inScope: types.filter((t) => t.DatamodelSourceId === s.Id && inScope(ctx.model, t, scope)).length,
        overrides: !ctx.writableSource(s.Id),
      })).filter((r) => r.total > 0),
    [ctx, types, scope],
  );
  const chosen = useMemo(() => types.filter((t) => sources.has(t.DatamodelSourceId) && inScope(ctx.model, t, scope)), [types, sources, scope, ctx.model]);
  const set = catalog?.find((s) => s.prefix === prefix);
  const calls = ai ? Math.ceil(chosen.length / ai.typesPerCall) : 0;
  const tooMany = !!ai && chosen.length > ai.maxTypes;

  function pickSet(p: string) {
    setPrefix(p);
    localStorage.setItem(setKey, p);
  }
  function pickScope(s: Scope) {
    setScope(s);
    localStorage.setItem(scopeKey, s);
  }
  function toggleSource(id: string) {
    setSources((was) => {
      const next = new Set(was);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  async function suggest() {
    const abort = new AbortController();
    setRunning(abort);
    setError(null);
    try {
      const result = await suggestIcons(ctx.storeId, prefix, chosen.map((t) => describe(ctx.model, t, ctx.baseTypeId)), abort.signal);
      setAnswer(result);
      setRows(
        result.types.map((s) => {
          const choices = s.icon ? [s.icon, ...s.alternatives] : [];
          return { typeId: s.typeId, choices, icon: s.icon, take: !!s.icon };
        }),
      );
    } catch (e) {
      if (!abort.signal.aborted) setError(e instanceof Error ? e.message : String(e));
    } finally {
      setRunning(null);
    }
  }

  function change(typeId: string, update: Partial<Row>) {
    setRows((all) => all.map((r) => (r.typeId === typeId ? { ...r, ...update } : r)));
  }

  const taken = rows.filter((r) => r.take && r.icon);
  const asOverrides = taken.filter((r) => {
    const t = ctx.model.NodeTypes[r.typeId];
    return !!t && (ownTypeSetting(ctx.model, t.Id, "Icon").overridden || !ctx.writableSource(t.DatamodelSourceId));
  }).length;

  async function apply() {
    ctx.update((m) => {
      for (const r of taken) {
        const type = m.NodeTypes[r.typeId];
        if (!type) continue;
        if (ownTypeSetting(m, type.Id, "Icon").overridden || !ctx.writableSource(type.DatamodelSourceId)) setOverride(m, type.Id, null, "Icon", r.icon === type.Icon ? undefined : r.icon);
        else type.Icon = r.icon;
      }
    });
    onClose();
    await showInfo(
      "Icons set in the draft",
      `${plural(taken.length, "type")} got ${taken.length === 1 ? "its icon" : "their icons"} in the draft.` +
        (asOverrides > 0 ? ` ${asOverrides} of them ${asOverrides === 1 ? "is in a source" : "are in sources"} that cannot be written from here, so ${asOverrides === 1 ? "its icon is an override" : "their icons are overrides"} on this server.` : "") +
        " Save or activate the draft to keep them.",
    );
  }

  const chip = (s: IconSetInfo) => (
    <button key={s.prefix} type="button" className={"chip" + (prefix === s.prefix ? " on" : "")} onClick={() => pickSet(s.prefix)} title={`${s.total.toLocaleString()} icons by ${s.author ?? "?"}, ${s.license ?? "?"} licence`}>
      {s.samples[0] && <TypeIcon icon={s.prefix + ":" + s.samples[0]} size={14} />}
      {s.name}
    </button>
  );

  const pickingRow = picking ? rows.find((r) => r.typeId === picking) : null;

  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && !running && onClose()}>
      <div className="dialog icon-assign" role="dialog" aria-label="Assign icons with AI">
        <h3>
          <IconSparkles size={16} stroke={1.9} /> Assign icons with AI
          <DialogTools onClose={onClose} />
        </h3>
        {!answer ? (
          <>
            <div className="dialog-body muted">
              The AI picks an icon from the set you choose for every type, judging by its name, namespace, base types and properties. Many types go in one request, so a model needs few calls. The icons go into the draft, where you see them before anything is saved.
            </div>
            <div className="icon-assign-section">
              <div className="icon-assign-label">Icon set</div>
              <div className="icon-picker-sets">
                <span className="icon-picker-group muted">Monochrome</span>
                {catalog?.filter((s) => !s.colour).map(chip)}
                <span className="icon-picker-group muted">Colour</span>
                {catalog?.filter((s) => s.colour).map(chip)}
              </div>
              {set && (
                <div className="icon-assign-samples">
                  {set.samples.map((name) => (
                    <TypeIcon key={name} icon={set.prefix + ":" + name} size={22} />
                  ))}
                  <span className="muted">
                    {set.total.toLocaleString()} icons by {set.author ?? "?"}, {set.licenseUrl ? <a href={set.licenseUrl} target="_blank" rel="noreferrer">{set.license}</a> : set.license}
                  </span>
                </div>
              )}
            </div>
            <div className="icon-assign-section">
              <div className="icon-assign-label">Types</div>
              {(
                [
                  ["missing", "Types without an icon", "neither their own nor one they inherit"],
                  ["own", "Types without an icon of their own", "those that only inherit one too"],
                  ["all", "All types", "replacing the icons they have"],
                ] as [Scope, string, string][]
              ).map(([value, label, hint]) => (
                <label key={value} className="icon-assign-option">
                  <input type="radio" name="icon-assign-scope" checked={scope === value} onChange={() => pickScope(value)} />
                  <span>
                    {label} <span className="muted">- {hint}</span>
                  </span>
                  <span className="badge">{scopeCounts[value]}</span>
                </label>
              ))}
            </div>
            <div className="icon-assign-section">
              <div className="icon-assign-label">
                Sources
                <button type="button" className="link-button" onClick={() => setSources(new Set(sourceRows.map((r) => r.source.Id)))}>
                  all
                </button>
                <button type="button" className="link-button" onClick={() => setSources(new Set())}>
                  none
                </button>
              </div>
              <div className="icon-assign-sources">
                {sourceRows.map((r) => (
                  <label key={r.source.Id} className="icon-assign-option">
                    <input type="checkbox" checked={sources.has(r.source.Id)} onChange={() => toggleSource(r.source.Id)} />
                    <SourceDot color={ctx.colors.get(r.source.Id) ?? "#888"} />
                    <span>
                      {r.source.Name || r.source.Id}
                      {r.overrides && <span className="muted" title={(ctx.readOnlyReason(r.source.Id) ?? "The source cannot be written from here.") + " Its icons are saved as overrides on this server."}> - as overrides</span>}
                    </span>
                    <span className="badge" title={`${r.inScope} of the source's ${r.total} types`}>
                      {r.inScope}
                    </span>
                  </label>
                ))}
              </div>
            </div>
            {ai && !ai.available && (
              <div className="icon-assign-warning">
                <IconAlertTriangle size={15} stroke={2} />
                <span>
                  The AI is not available: {ai.reason} Icons are picked by the Relatude AI service and paid for with the license's <code>ai_completion</code> credits.
                </span>
              </div>
            )}
            {error && <div className="dialog-body dialog-title-error">{error}</div>}
            <div className="icon-picker-foot">
              {running ? (
                <span className="icon-assign-running">
                  <IconLoader2 size={16} stroke={1.8} className="spin" /> Asking the AI for icons for {plural(chosen.length, "type")}…
                </span>
              ) : (
                <span className="muted">
                  {chosen.length === 0
                    ? "No type is chosen."
                    : tooMany
                      ? `At most ${ai!.maxTypes.toLocaleString()} types at a time: leave some sources out.`
                      : `${plural(chosen.length, "type")}${ai ? " in " + plural(calls, "AI call") : ""}.`}
                </span>
              )}
              <span className="icon-picker-actions">
                {running ? (
                  <button type="button" className="action-button dm-button" onClick={() => running.abort()}>
                    Stop
                  </button>
                ) : (
                  <button type="button" className="action-button dm-button" onClick={onClose}>
                    Cancel
                  </button>
                )}
                <button type="button" className="action-button dm-button primary" disabled={!!running || !ai?.available || chosen.length === 0 || tooMany || !set} onClick={suggest}>
                  <IconSparkles size={15} stroke={2} /> Suggest icons
                </button>
              </span>
            </div>
          </>
        ) : (
          <>
            <div className="dialog-body muted">
              {rows.filter((r) => r.choices.length > 0).length} of {plural(rows.length, "type")} got an icon from {set?.name ?? prefix}, in {plural(answer.calls, "AI call")} costing {plural(answer.credits, "credit")}
              {answer.creditsLeft >= 0 ? ` (${answer.creditsLeft.toLocaleString()} left)` : ""}. Click another suggestion to take it instead, or choose any icon.
            </div>
            {answer.problem && <div className="dialog-body dialog-title-error">Some of the types were not answered: {answer.problem}</div>}
            <div className="icon-assign-list">
              {rows.map((r) => {
                const type = ctx.model.NodeTypes[r.typeId];
                if (!type) return null;
                const current = modelIcon(ctx.model, type.Id);
                return (
                  <div key={r.typeId} className={"icon-assign-row" + (r.take && r.icon ? "" : " is-off")}>
                    <input type="checkbox" checked={r.take && !!r.icon} disabled={!r.icon} onChange={(e) => change(r.typeId, { take: e.target.checked })} aria-label={"Use the icon for " + type.CodeName} />
                    <span className="icon-assign-type">
                      <TypeGlyph icon={current} kind={type.ModelType} size={16} />
                      <span className="icon-assign-name">
                        {type.CodeName}
                        {type.Namespace && <span className="muted"> {type.Namespace}</span>}
                      </span>
                    </span>
                    <IconArrowRight size={14} stroke={1.8} className="muted" />
                    <span className="icon-assign-choices">
                      {r.icon ? (
                        <button type="button" className="icon-tile is-chosen" title={r.icon} onClick={() => setPicking(r.typeId)}>
                          <TypeIcon icon={r.icon} size={24} />
                        </button>
                      ) : (
                        <span className="muted icon-assign-none">no match</span>
                      )}
                      {r.choices
                        .filter((c) => c !== r.icon)
                        .map((c) => (
                          <button key={c} type="button" className="icon-tile" title={c} onClick={() => change(r.typeId, { icon: c, take: true })}>
                            <TypeIcon icon={c} size={18} />
                          </button>
                        ))}
                    </span>
                    <button type="button" className="action-button dm-button" onClick={() => setPicking(r.typeId)}>
                      Choose…
                    </button>
                  </div>
                );
              })}
            </div>
            <div className="icon-picker-foot">
              <span className="muted">
                {plural(taken.length, "icon")} to set{asOverrides > 0 ? `, ${asOverrides} as overrides` : ""}.
              </span>
              <span className="icon-picker-actions">
                <button type="button" className="action-button dm-button" onClick={() => setAnswer(null)}>
                  Back
                </button>
                <button type="button" className="action-button dm-button" onClick={onClose}>
                  Cancel
                </button>
                <button type="button" className="action-button dm-button primary" disabled={taken.length === 0} onClick={apply}>
                  Set {plural(taken.length, "icon")} in the draft
                </button>
              </span>
            </div>
          </>
        )}
      </div>
      {pickingRow && (
        <IconPicker
          value={pickingRow.icon}
          onClose={() => setPicking(null)}
          onChoose={(v) => {
            change(pickingRow.typeId, { icon: v, take: !!v, choices: v && !pickingRow.choices.includes(v) ? [...pickingRow.choices, v] : pickingRow.choices });
            setPicking(null);
          }}
        />
      )}
    </div>
  );
}
