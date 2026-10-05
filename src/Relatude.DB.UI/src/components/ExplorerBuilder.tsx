import { useEffect, useRef, useState, type ReactNode } from "react";
import { IconBook2, IconChevronRight, IconVariable, IconVariableOff, IconX } from "@tabler/icons-react";
import { argumentValue, selectionAt, updateValue, valueAt, type BuildPath, type ValuePath } from "../graphql/build";
import { printValue, type OperationNode, type OperationType, type ValueNode } from "../graphql/language";
import { isList, isNonNull, isRequired, listItem, namedType, stripNonNull, type ArgInfo, type FieldInfo, type Schema } from "../graphql/schema";

// The Build panel: what the endpoint offers as a tree to tick. A ticked field shows the fields of what it
// returns, and its arguments - each with an input that fits its type - once its "(…)" is clicked (or at
// once when one of them is required). Every change is made to the query text (see graphql/build.ts), so
// the tree and the editor never disagree. A branch can also be opened with its chevron just to look
// inside, without selecting anything; that never shows the arguments.

export interface BuilderActions {
  toggle(kind: OperationType, path: BuildPath, on: boolean): void;
  setArgument(kind: OperationType, path: BuildPath, name: string, value: ValueNode | null): void;
  toVariable(kind: OperationType, path: BuildPath, argName: string, valuePath: ValuePath, typeRef: string): void;
  toLiteral(kind: OperationType, path: BuildPath, argName: string, valuePath: ValuePath): void;
  setVariable(name: string, value: unknown): void;
  openDocs(typeName: string, fieldName?: string): void;
}

interface Ctx {
  schema: Schema;
  kind: OperationType;
  op: OperationNode | undefined;
  samples: Record<string, { id: string; name: string | null }>;
  variables: Record<string, unknown>;
  editable: boolean;
  actions: BuilderActions;
  /** whether a branch is open: what its chevron was last set to, else open while it is selected */
  isOpen(path: BuildPath, included: boolean): boolean;
  /** opens or closes a branch; null lets it follow its selection again */
  setOpen(path: BuildPath, open: boolean | null): void;
  /** whether the arguments of a selected field are shown: only after its "(…)" asked for them */
  isArgsOpen(path: BuildPath): boolean;
  /** shows or hides the arguments of a field; hiding also forgets it for the fields below */
  setArgsOpen(path: BuildPath, open: boolean): void;
}

// branch indent per level, and the caret's box: big enough to hit without aiming
const indent = 16;
const caretSize = 20;
// how long a branch takes to open or close; explorer.css animates .gx-reveal for as long
const revealMs = 160;

export function ExplorerBuilder({
  schema,
  queryOp,
  mutationOp,
  samples,
  variables,
  error,
  actions,
  onReset,
}: {
  schema: Schema;
  queryOp: OperationNode | undefined;
  mutationOp: OperationNode | undefined;
  samples: Record<string, { id: string; name: string | null }>;
  variables: Record<string, unknown>;
  /** a syntax error in the query: the tree cannot follow the text until it is fixed */
  error: string | null;
  actions: BuilderActions;
  onReset: () => void;
}) {
  const [filter, setFilter] = useState("");
  // branches opened or closed by their chevron, by operation kind and path; one left out follows its selection
  const [opened, setOpened] = useState<Record<string, boolean>>({});
  // fields whose arguments are shown, by the same key
  const [argsOpened, setArgsOpened] = useState<ReadonlySet<string>>(() => new Set());
  const editable = !error;
  const branches = (kind: OperationType) => {
    const key = (path: BuildPath) => kind + " " + path.join(".");
    return {
      isOpen: (path: BuildPath, included: boolean) => opened[key(path)] ?? included,
      setOpen: (path: BuildPath, open: boolean | null) =>
        setOpened((o) => {
          const next = { ...o };
          if (open === null) delete next[key(path)];
          else next[key(path)] = open;
          return next;
        }),
      isArgsOpen: (path: BuildPath) => argsOpened.has(key(path)),
      setArgsOpen: (path: BuildPath, open: boolean) =>
        setArgsOpened((o) => {
          const k = key(path);
          if (open) return o.has(k) ? o : new Set(o).add(k);
          const next = new Set([...o].filter((x) => x !== k && !x.startsWith(k + ".")));
          return next.size === o.size ? o : next;
        }),
    };
  };
  const query: Ctx = { schema, kind: "query", op: queryOp, samples, variables, editable, actions, ...branches("query") };
  const mutation: Ctx = { schema, kind: "mutation", op: mutationOp, samples, variables, editable, actions, ...branches("mutation") };
  const match = (f: FieldInfo) => !filter || f.name.toLowerCase().includes(filter.toLowerCase()) || namedType(f.type).toLowerCase().includes(filter.toLowerCase());
  const roots = schema.fields(schema.queryType).filter((f) => !f.name.startsWith("__"));
  const mutations = schema.mutationType ? schema.fields(schema.mutationType) : [];
  const lists = roots.filter((f) => f.kind === "list" || f.kind === "view");
  const singles = roots.filter((f) => f.kind !== "list" && f.kind !== "view");
  return (
    <div className="gx-builder">
      <input className="text-input gx-filter" placeholder="Find a root field or type" value={filter} onChange={(e) => setFilter(e.target.value)} />
      {error && (
        <div className="gx-builder-error">
          The query does not parse ({error}), so the tree cannot follow it.{" "}
          <button className="link-button" onClick={onReset}>
            Start over
          </button>
        </div>
      )}
      <Section title="Lists" hint="filter, search, order and page" fields={lists.filter(match)} ctx={query} />
      <Section title="By id" hint="one node" fields={singles.filter(match)} ctx={query} />
      {schema.mutationType && <Section title="Mutations" hint="change data" fields={mutations.filter(match)} ctx={mutation} />}
      <p className="gx-builder-foot muted">Ticking rewrites the query in the standard layout; comments in it go.</p>
    </div>
  );
}

function Section({ title, hint, fields, ctx }: { title: string; hint: string; fields: FieldInfo[]; ctx: Ctx }) {
  if (fields.length === 0) return null;
  return (
    <div className="gx-section">
      <div className="gx-section-head">
        {title} <span className="muted">{hint}</span>
      </div>
      {fields.map((f) => (
        <FieldRow key={f.name} ctx={ctx} path={[f.name]} field={f} depth={0} />
      ))}
    </div>
  );
}

function kindBadge(f: FieldInfo): string | null {
  switch (f.kind) {
    case "relation":
      return f.many ? "relation · many" : "relation";
    case "reference":
      return f.many ? "references" : "reference";
    case "view":
      return "view";
    case "create":
    case "update":
    case "delete":
      return f.kind;
    case "file":
      return "file";
    case "geo":
      return "position";
    default:
      return null;
  }
}

/** The chevron that opens a branch to look inside it, selected or not. */
function Caret({ open, label, onToggle }: { open: boolean; label: string; onToggle: () => void }) {
  return (
    <button
      type="button"
      className={"gx-caret" + (open ? " open" : "")}
      onClick={onToggle}
      aria-expanded={open}
      aria-label={(open ? "Close " : "Open ") + label}
      title={open ? "Close" : "Open to see what is inside"}
    >
      <IconChevronRight size={16} stroke={2.2} />
    </button>
  );
}

/**
 * Ticks or unticks a row; ticking opens it, whatever its chevron was last left at, and shows its
 * arguments when asked to. Unticking hides them again.
 */
function select(ctx: Ctx, path: BuildPath, included: boolean, showArgs = false) {
  if (!ctx.editable) return;
  if (!included) ctx.setOpen(path, null);
  ctx.setArgsOpen(path, !included && showArgs);
  ctx.actions.toggle(ctx.kind, path, !included);
}

/**
 * Shows its content with a short height-and-fade animation when `open` turns true, and plays it backwards
 * before taking the content away when it turns false. Content that was open from the start just shows.
 */
function Reveal({ open, children }: { open: boolean; children: ReactNode }) {
  const [phase, setPhase] = useState<"shut" | "opening" | "shown" | "closing">(open ? "shown" : "shut");
  const [was, setWas] = useState(open);
  if (open !== was) {
    // adjusted while rendering, so the first frame with the content already wears the animation
    setWas(open);
    const still = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ?? false;
    setPhase(open ? (still ? "shown" : "opening") : still ? "shut" : "closing");
  }
  useEffect(() => {
    if (phase !== "opening" && phase !== "closing") return;
    const t = setTimeout(() => setPhase(phase === "opening" ? "shown" : "shut"), revealMs + 30);
    return () => clearTimeout(t);
  }, [phase]);
  if (phase === "shut") return null;
  return (
    <div className={"gx-reveal" + (phase === "shown" ? "" : " " + phase)}>
      <div>{children}</div>
    </div>
  );
}

function FieldRow({ ctx, path, field, depth }: { ctx: Ctx; path: BuildPath; field: FieldInfo; depth: number }) {
  const sel = selectionAt(ctx.op, path);
  const included = !!sel;
  const named = namedType(field.type);
  const composite = ctx.schema.isComposite(named);
  const open = composite && ctx.isOpen(path, included);
  const badge = kindBadge(field);
  const required = field.args.some(isRequired);
  const toggle = () => select(ctx, path, included, required);
  const argsOpen = included && field.args.length > 0 && ctx.isArgsOpen(path);
  // the arguments the query gives the field, named on the "(…)" while they are hidden
  const given = sel?.kind === "Field" ? sel.arguments.map((a) => a.name) : [];
  const showArgs = () => (included ? ctx.setArgsOpen(path, !argsOpen) : select(ctx, path, false, true));
  return (
    <>
      <div className={"gx-row" + (included ? " on" : "")} style={{ paddingLeft: 4 + depth * indent }} title={field.description ?? undefined}>
        {composite ? <Caret open={open} label={field.name} onToggle={() => ctx.setOpen(path, !open)} /> : <span className="gx-caret-space" />}
        <input type="checkbox" checked={included} disabled={!ctx.editable} onChange={toggle} aria-label={"Select " + field.name} />
        <button className="gx-name" onClick={toggle} disabled={!ctx.editable}>
          {field.name}
        </button>
        {field.args.length > 0 && (
          <button
            type="button"
            className={"gx-args-mark" + (argsOpen ? " open" : "") + (given.length > 0 ? " set" : "")}
            onClick={showArgs}
            disabled={!included && !ctx.editable}
            aria-expanded={argsOpen}
            aria-label={(argsOpen ? "Hide the arguments of " : "Show the arguments of ") + field.name}
            title={
              argsOpen
                ? "Hide the arguments"
                : given.length > 0
                  ? "Arguments given: " + given.join(", ")
                  : "Show the arguments: " + field.args.map((a) => a.name).join(", ")
            }
          >
            (
            {required
              ? field.args
                  .filter(isRequired)
                  .map((a) => a.name)
                  .join(", ")
              : "…"}
            )
          </button>
        )}
        {badge && <span className="gx-badge">{badge}</span>}
        <button className="gx-type" onClick={() => ctx.actions.openDocs(named)} title={"About " + named}>
          {field.type}
        </button>
      </div>
      {field.args.length > 0 && (
        <Reveal open={argsOpen}>
          <div className="gx-args" style={{ marginLeft: 4 + caretSize + 3 + depth * indent }}>
            {field.args.map((a) => (
              <ArgRow key={a.name} ctx={ctx} path={path} field={field} arg={a} value={argumentValue(sel, a.name)} />
            ))}
          </div>
        </Reveal>
      )}
      {composite && (
        <Reveal open={open}>
          <Children ctx={ctx} path={path} typeName={named} depth={depth + 1} />
        </Reveal>
      )}
    </>
  );
}

function Children({ ctx, path, typeName, depth }: { ctx: Ctx; path: BuildPath; typeName: string; depth: number }) {
  const fields = ctx.schema.fields(typeName);
  const typename: FieldInfo = {
    name: "__typename",
    type: "String!",
    description: "The name of the object's type.",
    kind: "meta",
    many: false,
    property: null,
    nodeType: null,
    args: [],
  };
  const subtypes = ctx.schema.possibleTypes(typeName).filter((p) => p !== typeName);
  return (
    <>
      {fields.map((f) => (
        <FieldRow key={f.name} ctx={ctx} path={[...path, f.name]} field={f} depth={depth} />
      ))}
      <FieldRow ctx={ctx} path={[...path, "__typename"]} field={typename} depth={depth} />
      {subtypes.map((sub) => (
        <FragmentRow key={sub} ctx={ctx} path={[...path, "... on " + sub]} typeName={sub} parentType={typeName} depth={depth} />
      ))}
    </>
  );
}

function FragmentRow({ ctx, path, typeName, parentType, depth }: { ctx: Ctx; path: BuildPath; typeName: string; parentType: string; depth: number }) {
  const included = !!selectionAt(ctx.op, path);
  const open = ctx.isOpen(path, included);
  const own = ctx.schema.fields(typeName).filter((f) => !ctx.schema.field(parentType, f.name));
  const toggle = () => select(ctx, path, included);
  return (
    <>
      <div className={"gx-row gx-fragment" + (included ? " on" : "")} style={{ paddingLeft: 4 + depth * indent }} title={`Fields only a ${typeName} has`}>
        <Caret open={open} label={"the fields of " + typeName} onToggle={() => ctx.setOpen(path, !open)} />
        <input type="checkbox" checked={included} disabled={!ctx.editable} onChange={toggle} aria-label={"Select fields of " + typeName} />
        <button className="gx-name" onClick={toggle} disabled={!ctx.editable}>
          … on {typeName}
        </button>
        <span className="gx-badge">subtype</span>
        <button className="gx-type" onClick={() => ctx.actions.openDocs(typeName)} title={"About " + typeName}>
          <IconBook2 size={12} stroke={1.8} />
        </button>
      </div>
      <Reveal open={open}>
        {own.map((f) => (
          <FieldRow key={f.name} ctx={ctx} path={[...path, f.name]} field={f} depth={depth + 1} />
        ))}
        {own.length === 0 && (
          <div className="gx-row muted" style={{ paddingLeft: 4 + caretSize + 3 + (depth + 1) * indent }}>
            {typeName} has no fields of its own.
          </div>
        )}
      </Reveal>
    </>
  );
}

// ---- arguments and values ----

function ArgRow({ ctx, path, field, arg, value }: { ctx: Ctx; path: BuildPath; field: FieldInfo; arg: ArgInfo; value: ValueNode | undefined }) {
  const included = value !== undefined;
  const set = (vp: ValuePath, node: ValueNode | null) => {
    if (vp.length === 0) ctx.actions.setArgument(ctx.kind, path, arg.name, node);
    else ctx.actions.setArgument(ctx.kind, path, arg.name, updateValue(value, vp, node));
  };
  const toggle = () => ctx.editable && (included ? set([], null) : set([], defaultFor(ctx, arg.type, arg.name, field)));
  return (
    <div className={"gx-arg" + (included ? " on" : "")}>
      <div className="gx-arg-head" title={arg.description ?? undefined}>
        <input type="checkbox" checked={included} disabled={!ctx.editable} onChange={toggle} aria-label={"Use argument " + arg.name} />
        <button className="gx-arg-name" onClick={toggle} disabled={!ctx.editable}>
          {arg.name}
        </button>
        <span className="gx-arg-type">
          {arg.type}
          {arg.defaultValue !== null ? ` = ${arg.defaultValue}` : ""}
          {isRequired(arg) ? <span className="gx-required"> required</span> : null}
        </span>
      </div>
      {included && (
        <ValueEditor
          ctx={ctx}
          typeRef={arg.type}
          value={value}
          valuePath={[]}
          set={set}
          variable={{
            make: (vp, typeRef) => ctx.actions.toVariable(ctx.kind, path, arg.name, vp, typeRef),
            inline: (vp) => ctx.actions.toLiteral(ctx.kind, path, arg.name, vp),
          }}
          hint={hintFor(ctx, arg, field)}
        />
      )}
    </div>
  );
}

interface VariableActions {
  make(vp: ValuePath, typeRef: string): void;
  inline(vp: ValuePath): void;
}

function ValueEditor({
  ctx,
  typeRef,
  value,
  valuePath,
  set,
  variable,
  hint,
}: {
  ctx: Ctx;
  typeRef: string;
  value: ValueNode | undefined;
  valuePath: ValuePath;
  set: (vp: ValuePath, node: ValueNode | null) => void;
  variable: VariableActions;
  hint?: { label: string; value: unknown } | null;
}) {
  const here = valueAt(value, valuePath);
  const inner = stripNonNull(typeRef);
  const named = namedType(typeRef);
  const type = ctx.schema.type(named);
  const disabled = !ctx.editable;

  if (here?.kind === "Variable") {
    const current = ctx.variables[here.name];
    return (
      <div className="gx-value gx-variable">
        <span className="gx-var-chip" title="The value comes from the variables">
          ${here.name}
        </span>
        <DraftInput
          kind="json"
          value={current === undefined ? "" : typeof current === "string" ? current : JSON.stringify(current)}
          disabled={disabled}
          placeholder="value in the variables"
          onCommit={(text) => ctx.actions.setVariable(here.name, parseLoose(text, named))}
        />
        <button className="icon-button small" title="Write the value into the query instead" disabled={disabled} onClick={() => variable.inline(valuePath)}>
          <IconVariableOff size={14} stroke={1.8} />
        </button>
      </div>
    );
  }

  const makeVariable = (
    <button className="icon-button small" title="Pass this as a variable" disabled={disabled} onClick={() => variable.make(valuePath, typeRef)}>
      <IconVariable size={14} stroke={1.8} />
    </button>
  );

  if (isList(inner)) {
    const item = listItem(inner);
    const itemType = ctx.schema.type(namedType(item));
    const items = here?.kind === "List" ? here.values : here ? [here] : [];
    if (itemType?.kind === "INPUT_OBJECT") {
      return (
        <div className="gx-value gx-list">
          {items.map((_, i) => (
            <div key={i} className="gx-list-item">
              <div className="gx-list-head">
                <span className="muted">
                  {named} {i + 1}
                </span>
                <button className="icon-button small" title="Remove" disabled={disabled} onClick={() => set([...valuePath, i], null)}>
                  <IconX size={13} stroke={1.8} />
                </button>
              </div>
              <InputObjectEditor ctx={ctx} typeName={namedType(item)} value={value} valuePath={[...valuePath, i]} set={set} variable={variable} />
            </div>
          ))}
          <button className="link-button" disabled={disabled} onClick={() => set([...valuePath, items.length], { kind: "Object", fields: [], start: -1, end: -1 })}>
            + add {named}
          </button>
        </div>
      );
    }
    const text = items.map((v) => (v.kind === "String" || v.kind === "Enum" ? v.value : printValue(v))).join(", ");
    return (
      <div className="gx-value">
        <DraftInput
          kind="text"
          value={text}
          disabled={disabled}
          placeholder={itemType?.kind === "ENUM" ? (itemType.enumValues ?? []).slice(0, 3).join(", ") : "values, separated by commas"}
          onCommit={(t) =>
            set(valuePath, { kind: "List", values: splitList(t).map((s) => scalarNode(itemType?.kind === "ENUM" ? "enum" : namedType(item), s)), start: -1, end: -1 })
          }
        />
        {hint && (
          <SampleButton
            hint={hint}
            onUse={(v) => set(valuePath, { kind: "List", values: (Array.isArray(v) ? v : [v]).map((s) => scalarNode(namedType(item), String(s))), start: -1, end: -1 })}
          />
        )}
        {makeVariable}
      </div>
    );
  }

  if (type?.kind === "INPUT_OBJECT") {
    return (
      <div className="gx-value gx-object">
        <InputObjectEditor ctx={ctx} typeName={named} value={value} valuePath={valuePath} set={set} variable={variable} />
        <div className="gx-object-tools">{makeVariable}</div>
      </div>
    );
  }

  if (type?.kind === "ENUM") {
    const current = here?.kind === "Enum" ? here.value : "";
    return (
      <div className="gx-value">
        <select className="select compact" value={current} disabled={disabled} onChange={(e) => set(valuePath, { kind: "Enum", value: e.target.value, start: -1, end: -1 })}>
          {!current && <option value="">—</option>}
          {(type.enumValues ?? []).map((v) => (
            <option key={v} value={v}>
              {v}
            </option>
          ))}
        </select>
        {makeVariable}
      </div>
    );
  }

  if (named === "Boolean") {
    const current = here?.kind === "Boolean" ? String(here.value) : "";
    return (
      <div className="gx-value">
        <select
          className="select compact"
          value={current}
          disabled={disabled}
          onChange={(e) => set(valuePath, { kind: "Boolean", value: e.target.value === "true", start: -1, end: -1 })}
        >
          {!current && <option value="">—</option>}
          <option value="true">true</option>
          <option value="false">false</option>
        </select>
        {makeVariable}
      </div>
    );
  }

  const numeric = named === "Int" || named === "Long" || named === "Float" || named === "Decimal";
  const current = here ? (here.kind === "String" ? here.value : here.kind === "Int" || here.kind === "Float" ? here.value : here.kind === "Null" ? "" : printValue(here)) : "";
  return (
    <div className="gx-value">
      <DraftInput
        kind={named === "Int" || named === "Long" ? "int" : numeric ? "float" : "text"}
        value={current}
        disabled={disabled}
        placeholder={named === "DateTime" ? "2026-01-31T12:00:00Z" : named === "ID" ? "a node id" : named}
        onCommit={(t) => set(valuePath, numeric ? scalarNode(named, t) : { kind: "String", value: t, start: -1, end: -1 })}
      />
      {hint && <SampleButton hint={hint} onUse={(v) => set(valuePath, scalarNode(named, String(v)))} />}
      {makeVariable}
    </div>
  );
}

function InputObjectEditor({
  ctx,
  typeName,
  value,
  valuePath,
  set,
  variable,
}: {
  ctx: Ctx;
  typeName: string;
  value: ValueNode | undefined;
  valuePath: ValuePath;
  set: (vp: ValuePath, node: ValueNode | null) => void;
  variable: VariableActions;
}) {
  const type = ctx.schema.type(typeName);
  const here = valueAt(value, valuePath);
  const given = new Map(here?.kind === "Object" ? here.fields.map((f) => [f.name, f.value] as const) : []);
  return (
    <div className="gx-input-fields">
      {(type?.inputFields ?? []).map((f) => {
        const on = given.has(f.name);
        const fp = [...valuePath, f.name];
        const toggle = () => ctx.editable && set(fp, on ? null : defaultFor(ctx, f.type, f.name, null));
        return (
          <div key={f.name} className={"gx-arg" + (on ? " on" : "")}>
            <div className="gx-arg-head" title={f.description ?? undefined}>
              <input type="checkbox" checked={on} disabled={!ctx.editable} onChange={toggle} aria-label={"Use " + f.name} />
              <button className="gx-arg-name" onClick={toggle} disabled={!ctx.editable}>
                {f.name}
              </button>
              <span className="gx-arg-type">{f.type}</span>
            </div>
            {on && <ValueEditor ctx={ctx} typeRef={f.type} value={value} valuePath={fp} set={set} variable={variable} />}
          </div>
        );
      })}
    </div>
  );
}

function SampleButton({ hint, onUse }: { hint: { label: string; value: unknown }; onUse: (value: unknown) => void }) {
  return (
    <button className="gx-sample" title={`Use ${hint.label}`} onClick={() => onUse(hint.value)}>
      sample
    </button>
  );
}

/** A text box that keeps what is being typed while it is not yet a valid value, and shows the query's value otherwise. */
function DraftInput({
  kind,
  value,
  onCommit,
  placeholder,
  disabled,
}: {
  kind: "int" | "float" | "text" | "json";
  value: string;
  onCommit: (text: string) => void;
  placeholder?: string;
  disabled?: boolean;
}) {
  const [draft, setDraft] = useState(value);
  const focused = useRef(false);
  useEffect(() => {
    if (!focused.current) setDraft(value);
  }, [value]);
  const valid = (t: string) => (kind === "int" ? /^-?\d+$/.test(t) : kind === "float" ? /^-?\d+(\.\d+)?([eE][+-]?\d+)?$/.test(t) : true);
  return (
    <input
      className={"text-input gx-input" + (valid(draft) ? "" : " bad")}
      value={draft}
      disabled={disabled}
      placeholder={placeholder}
      spellCheck={false}
      inputMode={kind === "int" ? "numeric" : kind === "float" ? "decimal" : undefined}
      onFocus={() => (focused.current = true)}
      onBlur={() => {
        focused.current = false;
        setDraft(value);
      }}
      onChange={(e) => {
        setDraft(e.target.value);
        if (valid(e.target.value)) onCommit(e.target.value);
      }}
    />
  );
}

// ---- defaults ----

function splitList(text: string): string[] {
  return text
    .split(",")
    .map((s) => s.trim())
    .filter((s) => s.length > 0);
}

function scalarNode(named: string, text: string): ValueNode {
  const loc = { start: -1, end: -1 };
  if (named === "enum") return { kind: "Enum", value: text, ...loc };
  if ((named === "Int" || named === "Long") && /^-?\d+$/.test(text)) return { kind: "Int", value: text, ...loc };
  if ((named === "Float" || named === "Decimal") && /^-?\d+(\.\d+)?([eE][+-]?\d+)?$/.test(text)) return { kind: /[.eE]/.test(text) ? "Float" : "Int", value: text, ...loc };
  if (named === "Boolean" && (text === "true" || text === "false")) return { kind: "Boolean", value: text === "true", ...loc };
  return { kind: "String", value: text, ...loc };
}

function parseLoose(text: string, named: string): unknown {
  if (named === "String" || named === "ID" || named === "DateTime") {
    try {
      const v = JSON.parse(text);
      return typeof v === "string" ? v : text;
    } catch {
      return text;
    }
  }
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

function sampleFor(ctx: Ctx, field: FieldInfo | null): { id: string; name: string | null } | undefined {
  if (!field) return undefined;
  return ctx.samples[namedType(field.type)] ?? (field.nodeType ? Object.values(ctx.samples)[0] : undefined);
}

/** A stored node to fill an id with: offered next to the input, never filled in for an update or a delete. */
function hintFor(ctx: Ctx, arg: ArgInfo, field: FieldInfo): { label: string; value: unknown } | null {
  if (namedType(arg.type) !== "ID") return null;
  const sample = ctx.samples[namedType(field.type)] ?? ctx.samples[Object.keys(ctx.samples).find((k) => (ctx.schema.type(k)?.nodeType ?? "") === field.nodeType) ?? ""];
  if (!sample) return null;
  const label = sample.name ? `${sample.name} (${sample.id})` : sample.id;
  return { label, value: isList(stripNonNull(arg.type)) ? [sample.id] : sample.id };
}

/** What an argument or input field starts as when it is ticked. */
function defaultFor(ctx: Ctx, typeRef: string, name: string, field: FieldInfo | null): ValueNode {
  const loc = { start: -1, end: -1 };
  const inner = stripNonNull(typeRef);
  const named = namedType(typeRef);
  const type = ctx.schema.type(named);
  const kind = field?.kind;
  if (isList(inner)) {
    if (name === "ids") {
      const sample = sampleFor(ctx, field);
      return { kind: "List", values: sample ? [{ kind: "String", value: sample.id, ...loc }] : [], ...loc };
    }
    return { kind: "List", values: [], ...loc };
  }
  if (type?.kind === "INPUT_OBJECT") {
    const required = (type.inputFields ?? []).filter((f) => isNonNull(f.type));
    return { kind: "Object", fields: required.map((f) => ({ name: f.name, nameLoc: loc, value: defaultFor(ctx, f.type, f.name, null), ...loc })), ...loc };
  }
  if (type?.kind === "ENUM") return { kind: "Enum", value: type.enumValues?.[0] ?? "", ...loc };
  switch (name) {
    case "page":
      return { kind: "Int", value: "0", ...loc };
    case "pageSize":
      return { kind: "Int", value: "10", ...loc };
    case "top":
      return { kind: "Int", value: "5", ...loc };
    case "descending":
      return { kind: "Boolean", value: true, ...loc };
  }
  if (named === "ID" && name === "id" && kind === "single") {
    const sample = sampleFor(ctx, field);
    return { kind: "String", value: sample?.id ?? "", ...loc };
  }
  switch (named) {
    case "Int":
    case "Long":
    case "Float":
    case "Decimal":
      return { kind: "Int", value: "0", ...loc };
    case "Boolean":
      return { kind: "Boolean", value: true, ...loc };
    default:
      return { kind: "String", value: "", ...loc };
  }
}
