// Checks a query against the endpoint's schema before it is run: unknown fields and arguments (with the
// name that was probably meant), missing selections and required arguments, values of the wrong kind,
// fragments and variables that do not exist. What the server would refuse, said where it is in the text.

import type { LintIssue } from "../code/lint";
import {
  fragments,
  lineAndColumn,
  operations,
  printValue,
  tryParse,
  type DirectiveNode,
  type DocumentNode,
  type OperationNode,
  type SelectionSetNode,
  type TypeNode,
  type ValueNode,
} from "./language";
import { directives as knownDirectives, isList, isNonNull, isRequired, listItem, namedType, stripNonNull, suggest, type Schema } from "./schema";

export interface GqlIssue {
  offset: number;
  message: string;
}

export interface Analysis {
  doc: DocumentNode | null;
  issues: GqlIssue[];
}

export function analyze(text: string, schema: Schema | null): Analysis {
  if (text.trim().length === 0) return { doc: null, issues: [] };
  const parsed = tryParse(text);
  if (!parsed.doc) return { doc: null, issues: [{ offset: parsed.error.offset, message: parsed.error.message }] };
  return { doc: parsed.doc, issues: schema ? validate(parsed.doc, schema) : [] };
}

export function toLintIssues(text: string, issues: GqlIssue[]): LintIssue[] {
  return issues.map((i) => ({ line: lineAndColumn(text, Math.max(0, i.offset)).line, message: i.message }));
}

function didYouMean(name: string, candidates: string[]): string {
  const s = suggest(name, candidates);
  return s ? ` Did you mean ${s}?` : "";
}

export function validate(doc: DocumentNode, schema: Schema): GqlIssue[] {
  const issues: GqlIssue[] = [];
  const add = (offset: number, message: string) => issues.push({ offset, message });
  const ops = operations(doc);
  const frags = fragments(doc);

  const names = new Set<string>();
  for (const op of ops) {
    if (!op.name && ops.length > 1) add(op.start, "A query without a name must be the only operation in the document; give it a name.");
    if (op.name) {
      if (names.has(op.name)) add(op.start, `There are two operations named ${op.name}.`);
      names.add(op.name);
    }
  }
  const fragmentNames = new Set<string>();
  for (const d of doc.definitions) {
    if (d.kind !== "Fragment") continue;
    if (fragmentNames.has(d.name)) add(d.nameLoc.start, `There are two fragments named ${d.name}.`);
    fragmentNames.add(d.name);
  }

  const checkDirectives = (list: DirectiveNode[], variables: Map<string, string> | null) => {
    for (const d of list) {
      const known = knownDirectives.find((k) => k.name === d.name);
      if (!known) {
        add(
          d.start,
          `Unknown directive @${d.name}.${didYouMean(
            d.name,
            knownDirectives.map((k) => k.name),
          )}`,
        );
        continue;
      }
      for (const a of d.arguments) {
        const def = known.args.find((x) => x.name === a.name);
        if (!def) add(a.start, `@${d.name} has no argument ${a.name}.`);
        else checkValue(a.value, def.type, variables);
      }
      for (const def of known.args) if (isRequired(def) && !d.arguments.some((a) => a.name === def.name)) add(d.start, `@${d.name} needs the argument ${def.name}: ${def.type}.`);
    }
  };

  const checkValue = (v: ValueNode, ref: string, variables: Map<string, string> | null): void => {
    const at = v.start;
    if (v.kind === "Variable") {
      if (variables && !variables.has(v.name)) add(at, `The variable $${v.name} is not defined; add it to the operation's variables.`);
      return;
    }
    if (v.kind === "Null") {
      if (isNonNull(ref)) add(at, `A value of type ${ref} cannot be null.`);
      return;
    }
    const inner = stripNonNull(ref);
    if (isList(inner)) {
      if (v.kind === "List") for (const item of v.values) checkValue(item, listItem(inner), variables);
      else checkValue(v, listItem(inner), variables);
      return;
    }
    const name = namedType(inner);
    const type = schema.type(name);
    if (!type) return;
    if (type.kind === "ENUM") {
      const values = type.enumValues ?? [];
      if (v.kind === "Enum") {
        if (!values.includes(v.value)) add(at, `${name} has no value ${v.value}.${didYouMean(v.value, values)}`);
      } else if (v.kind === "String" && values.includes(v.value)) {
        add(at, `Enum values are written without quotes: ${v.value}.`);
      } else add(at, `${name} expected (one of ${values.slice(0, 6).join(", ")}${values.length > 6 ? "…" : ""}), found ${printValue(v)}.`);
      return;
    }
    if (type.kind === "INPUT_OBJECT") {
      if (v.kind !== "Object") {
        add(at, `${name} expected, written as { field: value }; found ${printValue(v)}.`);
        return;
      }
      const fields = type.inputFields ?? [];
      for (const f of v.fields) {
        const def = fields.find((x) => x.name === f.name);
        if (!def)
          add(
            f.start,
            `${name} has no field ${f.name}.${didYouMean(
              f.name,
              fields.map((x) => x.name),
            )}`,
          );
        else checkValue(f.value, def.type, variables);
      }
      for (const def of fields) if (isNonNull(def.type) && !v.fields.some((f) => f.name === def.name)) add(at, `${name} needs the field ${def.name}: ${def.type}.`);
      return;
    }
    const ok = scalarAccepts(name, v);
    if (!ok) add(at, `${name} expected, found ${printValue(v)}.`);
  };

  const checkSet = (parent: string, set: SelectionSetNode, variables: Map<string, string> | null) => {
    for (const s of set.selections) {
      if (s.kind === "Field") {
        checkDirectives(s.directives, variables);
        if (s.name === "__typename") {
          if (s.selectionSet) add(s.selectionSet.start, "__typename is a String; it has no fields to select.");
          continue;
        }
        if ((s.name === "__schema" || s.name === "__type") && parent === schema.queryType) {
          if (!schema.introspection) add(s.nameLoc.start, "Introspection is switched off for this endpoint (Settings), so the server refuses this.");
          else if (!s.selectionSet) add(s.nameLoc.start, `${s.name} needs a selection, such as { types { name } }.`);
          continue;
        }
        if (parent.startsWith("__")) continue; // inside introspection: left to the server
        const field = schema.field(parent, s.name);
        if (!field) {
          add(
            s.nameLoc.start,
            `${parent} has no field ${s.name}.${didYouMean(
              s.name,
              schema.fields(parent).map((f) => f.name),
            )}`,
          );
          continue;
        }
        for (const a of s.arguments) {
          const def = field.args.find((x) => x.name === a.name);
          if (!def) {
            add(
              a.start,
              field.args.length === 0
                ? `${s.name} takes no arguments.`
                : `${s.name} has no argument ${a.name}.${didYouMean(
                    a.name,
                    field.args.map((x) => x.name),
                  )}`,
            );
          } else checkValue(a.value, def.type, variables);
        }
        for (const def of field.args)
          if (isRequired(def) && !s.arguments.some((a) => a.name === def.name)) add(s.nameLoc.start, `${s.name} needs the argument ${def.name}: ${def.type}.`);
        const named = namedType(field.type);
        if (schema.isComposite(named)) {
          if (!s.selectionSet) add(s.nameLoc.end, `${s.name} returns ${named}, so it needs a selection of fields, such as { ${firstFields(schema, named)} }.`);
          else checkSet(named, s.selectionSet, variables);
        } else if (s.selectionSet) {
          add(s.selectionSet.start, `${s.name} is a ${field.type}; it has no fields to select.`);
        }
      } else if (s.kind === "InlineFragment") {
        checkDirectives(s.directives, variables);
        let target = parent;
        if (s.typeCondition) {
          const t = schema.type(s.typeCondition);
          if (!t) {
            add(s.typeLoc?.start ?? s.start, `There is no type ${s.typeCondition}.${didYouMean(s.typeCondition, schema.fragmentTargets(parent))}`);
            continue;
          }
          if (!schema.isComposite(s.typeCondition)) {
            add(s.typeLoc?.start ?? s.start, `${s.typeCondition} has no fields, so a fragment cannot be on it.`);
            continue;
          }
          if (!parent.startsWith("__") && !schema.overlaps(s.typeCondition, parent))
            add(s.typeLoc?.start ?? s.start, `A ${parent} is never a ${s.typeCondition}, so this fragment never applies.`);
          target = s.typeCondition;
        }
        checkSet(target, s.selectionSet, variables);
      } else {
        checkDirectives(s.directives, variables);
        const f = frags.get(s.name);
        if (!f) add(s.nameLoc.start, `There is no fragment ${s.name}.${didYouMean(s.name, [...frags.keys()])}`);
        else if (schema.type(f.typeCondition) && !parent.startsWith("__") && !schema.overlaps(f.typeCondition, parent))
          add(s.nameLoc.start, `${s.name} is on ${f.typeCondition}, and a ${parent} is never one.`);
      }
    }
  };

  for (const op of ops) {
    const root = op.operation === "query" ? schema.queryType : op.operation === "mutation" ? schema.mutationType : null;
    if (!root) {
      add(op.start, op.operation === "mutation" ? "This endpoint takes no mutations (Allow mutations in Settings)." : "Subscriptions are not supported.");
      continue;
    }
    const variables = new Map<string, string>();
    for (const v of op.variables) {
      const name = namedTypeOf(v.type);
      if (!schema.type(name))
        add(
          v.type.start,
          `There is no type ${name}.${didYouMean(
            name,
            [...schema.types.values()].filter((t) => schema.isInputType(t.name)).map((t) => t.name),
          )}`,
        );
      else if (!schema.isInputType(name)) add(v.type.start, `${name} is an output type; a variable needs a scalar, enum or input type.`);
      if (variables.has(v.name)) add(v.start, `$${v.name} is defined twice.`);
      variables.set(v.name, printTypeRef(v.type));
      if (v.defaultValue) checkValue(v.defaultValue, printTypeRef(v.type), null);
    }
    checkDirectives(op.directives, variables);
    checkSet(root, op.selectionSet, variables);
    // variables used through fragments must be defined by the operation too
    for (const name of variablesUsedInFragments(doc, op)) if (!variables.has(name)) add(op.start, `A fragment uses $${name}, which ${op.name ?? "the operation"} does not define.`);
  }
  for (const d of doc.definitions) {
    if (d.kind !== "Fragment") continue;
    const t = schema.type(d.typeCondition);
    if (!t)
      add(
        d.typeLoc.start,
        `There is no type ${d.typeCondition}.${didYouMean(
          d.typeCondition,
          [...schema.types.values()].filter((x) => schema.isComposite(x.name)).map((x) => x.name),
        )}`,
      );
    else if (!schema.isComposite(d.typeCondition)) add(d.typeLoc.start, `${d.typeCondition} has no fields, so a fragment cannot be on it.`);
    else checkSet(d.typeCondition, d.selectionSet, null);
  }
  issues.sort((a, b) => a.offset - b.offset);
  return issues;
}

function scalarAccepts(name: string, v: ValueNode): boolean {
  switch (name) {
    case "Int":
    case "Long":
      return v.kind === "Int";
    case "Float":
    case "Decimal":
      return v.kind === "Int" || v.kind === "Float";
    case "Boolean":
      return v.kind === "Boolean";
    case "ID":
      return v.kind === "String" || v.kind === "Int";
    case "String":
    case "DateTime":
      return v.kind === "String";
    default:
      return true;
  }
}

function firstFields(schema: Schema, typeName: string): string {
  const fields = schema.fields(typeName);
  const result = fields.find((f) => f.kind === "count");
  if (result) return "totalCount items { id }";
  const picks = fields
    .filter((f) => !schema.isComposite(namedType(f.type)))
    .slice(0, 2)
    .map((f) => f.name);
  return picks.length > 0 ? picks.join(" ") : "__typename";
}

export function namedTypeOf(t: TypeNode): string {
  return t.kind === "Named" ? t.name : namedTypeOf(t.type);
}

export function printTypeRef(t: TypeNode): string {
  if (t.kind === "Named") return t.name;
  if (t.kind === "List") return "[" + printTypeRef(t.type) + "]";
  return printTypeRef(t.type) + "!";
}

/** Every variable an operation refers to, in its own selection and in the fragments it uses. */
export function variablesUsed(doc: DocumentNode, op: OperationNode): string[] {
  const names = new Set<string>();
  const fromValue = (v: ValueNode) => {
    if (v.kind === "Variable") names.add(v.name);
    else if (v.kind === "List") v.values.forEach(fromValue);
    else if (v.kind === "Object") v.fields.forEach((f) => fromValue(f.value));
  };
  const fromDirectives = (list: DirectiveNode[]) => list.forEach((d) => d.arguments.forEach((a) => fromValue(a.value)));
  const frags = fragments(doc);
  const seen = new Set<string>();
  const fromSet = (set: SelectionSetNode) => {
    for (const s of set.selections) {
      fromDirectives(s.directives);
      if (s.kind === "Field") {
        s.arguments.forEach((a) => fromValue(a.value));
        if (s.selectionSet) fromSet(s.selectionSet);
      } else if (s.kind === "InlineFragment") fromSet(s.selectionSet);
      else if (!seen.has(s.name)) {
        seen.add(s.name);
        const f = frags.get(s.name);
        if (f) fromSet(f.selectionSet);
      }
    }
  };
  fromDirectives(op.directives);
  fromSet(op.selectionSet);
  return [...names];
}

function variablesUsedInFragments(doc: DocumentNode, op: OperationNode): string[] {
  const own = new Set<string>();
  const fromValue = (v: ValueNode) => {
    if (v.kind === "Variable") own.add(v.name);
    else if (v.kind === "List") v.values.forEach(fromValue);
    else if (v.kind === "Object") v.fields.forEach((f) => fromValue(f.value));
  };
  const fromSet = (set: SelectionSetNode) => {
    for (const s of set.selections) {
      s.directives.forEach((d) => d.arguments.forEach((a) => fromValue(a.value)));
      if (s.kind === "Field") {
        s.arguments.forEach((a) => fromValue(a.value));
        if (s.selectionSet) fromSet(s.selectionSet);
      } else if (s.kind === "InlineFragment") fromSet(s.selectionSet);
    }
  };
  fromSet(op.selectionSet);
  return variablesUsed(doc, op).filter((n) => !own.has(n));
}

/** Variables the operation needs that the json does not give, and names the json gives that the operation does not take. */
export function variableProblems(
  doc: DocumentNode,
  op: OperationNode,
  variablesText: string,
): { missing: string[]; unknown: string[]; undefinedUsed: string[]; invalidJson: boolean } {
  let values: Record<string, unknown> = {};
  let invalidJson = false;
  if (variablesText.trim()) {
    try {
      const parsed = JSON.parse(variablesText);
      if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) values = parsed as Record<string, unknown>;
      else invalidJson = true;
    } catch {
      invalidJson = true;
    }
  }
  const defined = new Set(op.variables.map((v) => v.name));
  const missing = op.variables.filter((v) => v.type.kind === "NonNull" && !v.defaultValue && (values[v.name] === undefined || values[v.name] === null)).map((v) => v.name);
  const unknown = invalidJson ? [] : Object.keys(values).filter((k) => !defined.has(k));
  const undefinedUsed = variablesUsed(doc, op).filter((n) => !defined.has(n));
  return { missing, unknown, undefinedUsed, invalidJson };
}
