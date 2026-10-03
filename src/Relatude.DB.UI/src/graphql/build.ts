// The query builder's edits. The query text stays the one source of truth: the builder reads the parsed
// document to show what is selected, and every tick, argument or value it changes is made on a copy of that
// document, which is printed back into the editor. A path names a field by the field names leading to it from
// the operation's root; "... on Type" stands for an inline fragment on the way.

import {
  jsonFromValue,
  noLoc,
  operations,
  valueFromJson,
  type ArgumentNode,
  type DocumentNode,
  type FieldNode,
  type InlineFragmentNode,
  type OperationNode,
  type OperationType,
  type SelectionNode,
  type SelectionSetNode,
  type TypeNode,
  type ValueNode,
} from "./language";
import { variablesUsed } from "./validate";

export type BuildPath = string[];

/** One step into a value: an object field by name or a list item by index. */
export type ValuePath = (string | number)[];

export function isFragmentSegment(seg: string): boolean {
  return seg.startsWith("... on ");
}

export function fragmentType(seg: string): string {
  return seg.slice(7);
}

function find(set: SelectionSetNode | undefined, seg: string): FieldNode | InlineFragmentNode | undefined {
  if (!set) return undefined;
  if (isFragmentSegment(seg)) {
    const type = fragmentType(seg);
    return set.selections.find((s): s is InlineFragmentNode => s.kind === "InlineFragment" && s.typeCondition === type);
  }
  return (
    set.selections.find((s): s is FieldNode => s.kind === "Field" && s.name === seg && !s.alias) ?? set.selections.find((s): s is FieldNode => s.kind === "Field" && s.name === seg)
  );
}

/** The field or inline fragment at the path, or undefined when it is not selected. */
export function selectionAt(op: OperationNode | null | undefined, path: BuildPath): FieldNode | InlineFragmentNode | undefined {
  if (!op) return undefined;
  let set: SelectionSetNode | undefined = op.selectionSet;
  let node: FieldNode | InlineFragmentNode | undefined;
  for (const seg of path) {
    node = find(set, seg);
    if (!node) return undefined;
    set = node.selectionSet;
  }
  return node;
}

/** The operation of the given kind the builder works on: the active one when it is of that kind, else the first. */
export function builderOperation(doc: DocumentNode | null, kind: OperationType, activeIndex: number): OperationNode | undefined {
  if (!doc) return undefined;
  const ops = operations(doc);
  const active = ops[activeIndex];
  if (active && active.operation === kind) return active;
  return ops.find((o) => o.operation === kind);
}

function emptySet(): SelectionSetNode {
  return { selections: [], ...noLoc };
}

export function newField(name: string, args: ArgumentNode[] = [], selections?: SelectionNode[]): FieldNode {
  return {
    kind: "Field",
    name,
    nameLoc: noLoc,
    arguments: args,
    directives: [],
    selectionSet: selections ? { selections, ...noLoc } : undefined,
    ...noLoc,
  };
}

export function newArgument(name: string, value: ValueNode): ArgumentNode {
  return { name, nameLoc: noLoc, value, ...noLoc };
}

function uniqueName(base: string, taken: Set<string>): string {
  if (!taken.has(base)) return base;
  for (let i = 2; ; i++) if (!taken.has(base + i)) return base + i;
}

/** Finds the operation to edit in a copy of the document, creating it when there is none. */
function ensureOperation(doc: DocumentNode, kind: OperationType, activeIndex: number): OperationNode {
  const existing = builderOperation(doc, kind, activeIndex);
  if (existing) return existing;
  const ops = operations(doc);
  const taken = new Set(ops.map((o) => o.name).filter((n): n is string => !!n));
  // an operation without a name must be alone: the ones there get names first
  for (const o of ops) {
    if (!o.name) {
      o.name = uniqueName(o.operation === "query" ? "MyQuery" : "MyMutation", taken);
      taken.add(o.name);
    }
  }
  const op: OperationNode = {
    kind: "Operation",
    operation: kind,
    name: ops.length > 0 || kind !== "query" ? uniqueName(kind === "query" ? "MyQuery" : "MyMutation", taken) : undefined,
    variables: [],
    directives: [],
    selectionSet: emptySet(),
    ...noLoc,
  };
  doc.definitions.push(op);
  return op;
}

export interface FieldDefaults {
  /** the arguments a newly ticked field starts with */
  args(path: BuildPath): ArgumentNode[];
  /** the selection a newly ticked field starts with; undefined for a leaf */
  selection(path: BuildPath): SelectionNode[] | undefined;
}

/** Ticks or unticks the field at the path. Ticking creates what leads to it; unticking removes what is left empty. */
export function toggleField(doc: DocumentNode | null, kind: OperationType, activeIndex: number, path: BuildPath, on: boolean, defaults: FieldDefaults): DocumentNode {
  const next: DocumentNode = doc ? structuredClone(doc) : { definitions: [] };
  if (on) {
    const op = ensureOperation(next, kind, activeIndex);
    let set = op.selectionSet;
    for (let i = 0; i < path.length; i++) {
      const seg = path[i];
      const sub = path.slice(0, i + 1);
      let node = find(set, seg);
      const last = i === path.length - 1;
      if (!node) {
        if (isFragmentSegment(seg)) {
          node = { kind: "InlineFragment", typeCondition: fragmentType(seg), directives: [], selectionSet: emptySet(), ...noLoc };
          if (last) node.selectionSet.selections = defaults.selection(sub) ?? [newField("__typename")];
        } else {
          const selection = last ? defaults.selection(sub) : [];
          node = newField(seg, defaults.args(sub), selection);
        }
        set.selections.push(node);
      }
      if (!last) {
        if (!node.selectionSet) node.selectionSet = emptySet();
        set = node.selectionSet;
      }
    }
    return next;
  }
  const op = builderOperation(next, kind, activeIndex);
  if (!op) return next;
  removeAt(next, op, path);
  return next;
}

function removeAt(doc: DocumentNode, op: OperationNode, path: BuildPath) {
  // the chain of selection sets down to the field's parent
  const sets: SelectionSetNode[] = [op.selectionSet];
  for (let i = 0; i < path.length - 1; i++) {
    const node = find(sets[i], path[i]);
    if (!node?.selectionSet) return;
    sets.push(node.selectionSet);
  }
  for (let i = path.length - 1; i >= 0; i--) {
    const set = sets[i];
    const node = find(set, path[i]);
    if (node && (i === path.length - 1 || (node.selectionSet && node.selectionSet.selections.length === 0))) {
      set.selections = set.selections.filter((s) => s !== node);
    }
    if (set.selections.length > 0) return;
  }
  // nothing is left of the operation
  doc.definitions = doc.definitions.filter((d) => d !== op);
}

function fieldIn(doc: DocumentNode, kind: OperationType, activeIndex: number, path: BuildPath): { op: OperationNode; field: FieldNode } | null {
  const op = builderOperation(doc, kind, activeIndex);
  const node = selectionAt(op, path);
  if (!op || !node || node.kind !== "Field") return null;
  return { op, field: node };
}

/** Sets an argument of the field at the path; null removes it. */
export function setArgument(doc: DocumentNode, kind: OperationType, activeIndex: number, path: BuildPath, name: string, value: ValueNode | null): DocumentNode {
  const next = structuredClone(doc);
  const found = fieldIn(next, kind, activeIndex, path);
  if (!found) return next;
  const { field } = found;
  const index = field.arguments.findIndex((a) => a.name === name);
  if (value === null) {
    if (index >= 0) field.arguments.splice(index, 1);
  } else if (index >= 0) field.arguments[index] = { ...field.arguments[index], value };
  else field.arguments.push(newArgument(name, value));
  return next;
}

export function argumentValue(field: FieldNode | InlineFragmentNode | undefined, name: string): ValueNode | undefined {
  if (!field || field.kind !== "Field") return undefined;
  return field.arguments.find((a) => a.name === name)?.value;
}

/** The value at a path inside another value. */
export function valueAt(value: ValueNode | undefined, path: ValuePath): ValueNode | undefined {
  let v = value;
  for (const step of path) {
    if (!v) return undefined;
    if (typeof step === "number") v = v.kind === "List" ? v.values[step] : undefined;
    else v = v.kind === "Object" ? v.fields.find((f) => f.name === step)?.value : undefined;
  }
  return v;
}

/** The value with the part at the path replaced (null removes it), creating objects on the way. */
export function updateValue(value: ValueNode | undefined, path: ValuePath, replacement: ValueNode | null): ValueNode | null {
  if (path.length === 0) return replacement;
  const [step, ...rest] = path;
  if (typeof step === "number") {
    const list = value?.kind === "List" ? [...value.values] : [];
    const inner = updateValue(list[step], rest, replacement);
    if (inner === null) list.splice(step, 1);
    else list[step] = inner;
    return { kind: "List", values: list, ...noLoc };
  }
  const fields = value?.kind === "Object" ? [...value.fields] : [];
  const index = fields.findIndex((f) => f.name === step);
  const inner = updateValue(index >= 0 ? fields[index].value : undefined, rest, replacement);
  if (inner === null) {
    if (index >= 0) fields.splice(index, 1);
  } else if (index >= 0) fields[index] = { ...fields[index], value: inner };
  else fields.push({ name: step, nameLoc: noLoc, value: inner, ...noLoc });
  return { kind: "Object", fields, ...noLoc };
}

export function typeNodeOf(ref: string): TypeNode {
  if (ref.endsWith("!")) return { kind: "NonNull", type: typeNodeOf(ref.slice(0, -1)), ...noLoc };
  if (ref.startsWith("[")) return { kind: "List", type: typeNodeOf(ref.slice(1, -1)), ...noLoc };
  return { kind: "Named", name: ref, ...noLoc };
}

function parseVariables(text: string): Record<string, unknown> {
  try {
    const v = JSON.parse(text || "{}");
    return v && typeof v === "object" && !Array.isArray(v) ? (v as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}

/**
 * Turns a value written into the query into a variable: the operation gets "$name: Type", the value moves into
 * the variables json, and the query refers to it. The way clients usually pass values.
 */
export function toVariable(
  doc: DocumentNode,
  kind: OperationType,
  activeIndex: number,
  path: BuildPath,
  argName: string,
  valuePath: ValuePath,
  typeRef: string,
  variablesText: string,
): { doc: DocumentNode; variables: string } {
  const next = structuredClone(doc);
  const found = fieldIn(next, kind, activeIndex, path);
  if (!found) return { doc: next, variables: variablesText };
  const { op, field } = found;
  const arg = field.arguments.find((a) => a.name === argName);
  const literal = valueAt(arg?.value, valuePath);
  const words = [argName, ...valuePath.filter((s): s is string => typeof s === "string")];
  const base =
    words.length <= 1
      ? argName
      : words
          .slice(-2)
          .map((w, i) => (i === 0 ? w : w[0].toUpperCase() + w.slice(1)))
          .join("");
  const name = uniqueName(base, new Set(op.variables.map((v) => v.name)));
  op.variables.push({ name, nameLoc: noLoc, type: typeNodeOf(typeRef), ...noLoc });
  const replaced = updateValue(arg?.value, valuePath, { kind: "Variable", name, ...noLoc });
  if (arg && replaced) arg.value = replaced;
  else if (replaced) field.arguments.push(newArgument(argName, replaced));
  const values = parseVariables(variablesText);
  values[name] = literal ? jsonFromValue(literal, values) : null;
  return { doc: next, variables: JSON.stringify(values, null, 2) };
}

/** The opposite: the variable's value is written into the query, and the variable goes when nothing else uses it. */
export function toLiteral(
  doc: DocumentNode,
  kind: OperationType,
  activeIndex: number,
  path: BuildPath,
  argName: string,
  valuePath: ValuePath,
  variablesText: string,
): { doc: DocumentNode; variables: string } {
  const next = structuredClone(doc);
  const found = fieldIn(next, kind, activeIndex, path);
  if (!found) return { doc: next, variables: variablesText };
  const { op, field } = found;
  const arg = field.arguments.find((a) => a.name === argName);
  const variable = valueAt(arg?.value, valuePath);
  if (!arg || variable?.kind !== "Variable") return { doc: next, variables: variablesText };
  const values = parseVariables(variablesText);
  const replaced = updateValue(arg.value, valuePath, valueFromJson(values[variable.name] ?? null));
  if (replaced) arg.value = replaced;
  if (!variablesUsed(next, op).includes(variable.name)) {
    op.variables = op.variables.filter((v) => v.name !== variable.name);
    delete values[variable.name];
  }
  return { doc: next, variables: JSON.stringify(values, null, 2) };
}

/** Sets a variable's value in the variables json. */
export function setVariable(variablesText: string, name: string, value: unknown): string {
  const values = parseVariables(variablesText);
  values[name] = value;
  return JSON.stringify(values, null, 2);
}
