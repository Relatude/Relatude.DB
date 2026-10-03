// Completion for the query editor: what can be typed where the caret is. The text before the caret is read
// with the tolerant lexer and a small state machine that knows which type a selection is on, which field's
// arguments are open, which input object a value is in, and so on - a query being typed rarely parses.

import { lex, type OperationType } from "./language";
import {
  builtInScalars,
  directives,
  isList,
  isNonNull,
  isRequired,
  listItem,
  namedType,
  stripNonNull,
  type ArgInfo,
  type FieldInfo,
  type InputFieldInfo,
  type Schema,
} from "./schema";

export type CompletionKind = "field" | "argument" | "type" | "enum" | "keyword" | "variable" | "fragment" | "directive" | "value";

export interface Completion {
  label: string;
  kind: CompletionKind;
  /** the type, shown to the right */
  detail?: string;
  doc?: string | null;
  /** the text put in; "|" marks where the caret goes (the end when there is none) */
  insert?: string;
  /** open the list again after inserting: an argument's name is followed by its values */
  reopen?: boolean;
}

export interface CompletionResult {
  from: number;
  to: number;
  items: Completion[];
}

interface DocFrame {
  k: "doc";
  state: "start" | "opHead" | "fragName" | "fragOn" | "fragType" | "fragHead";
  nextType: string | null;
}
interface VarsFrame {
  k: "vars";
  expectType: boolean;
  expectName: boolean;
}
interface SelFrame {
  k: "sel";
  type: string | null;
  field: FieldInfo | null;
  pendingType: string | null;
  spread: "" | "dots" | "on";
  alias: boolean;
  directive: "" | "at" | "named";
}
interface ArgsFrame {
  k: "args";
  args: ArgInfo[];
  current: ArgInfo | null;
  given: Set<string>;
  expectValue: boolean;
  variable: boolean;
}
interface ObjFrame {
  k: "obj";
  typeName: string | null;
  fields: InputFieldInfo[];
  current: InputFieldInfo | null;
  given: Set<string>;
  expectValue: boolean;
  variable: boolean;
}
interface ListFrame {
  k: "list";
  item: string | null;
  variable: boolean;
}
type Frame = DocFrame | VarsFrame | SelFrame | ArgsFrame | ObjFrame | ListFrame;

const wordChar = /[A-Za-z0-9_]/;

export function complete(text: string, offset: number, schema: Schema): CompletionResult | null {
  // the word being typed, with a leading $ or @ when there is one
  let from = offset;
  while (from > 0 && wordChar.test(text[from - 1])) from--;
  if (from > 0 && (text[from - 1] === "$" || text[from - 1] === "@")) from--;
  let to = offset;
  while (to < text.length && wordChar.test(text[to])) to++;
  const word = text.slice(from, offset);
  if (insideCommentOrString(text, from)) return null;
  if (/^[0-9-]/.test(word)) return null;

  const tokens = lex(text.slice(0, from), true).filter((t) => t.kind !== "eof");
  const stack: Frame[] = [{ k: "doc", state: "start", nextType: null }];
  let opVars: { name: string; type: string }[] = [];
  let varName: string | null = null;

  const rootOf = (op: OperationType): string | null => (op === "query" ? schema.queryType : op === "mutation" ? schema.mutationType : null);
  const inputFields = (typeRef: string | null): { name: string | null; fields: InputFieldInfo[] } => {
    if (!typeRef) return { name: null, fields: [] };
    const name = namedType(typeRef);
    return { name, fields: schema.type(name)?.inputFields ?? [] };
  };
  const pushValue = (typeRef: string | null, token: string) => {
    if (token === "{") {
      const inner = typeRef && isList(stripNonNull(typeRef)) ? listItem(stripNonNull(typeRef)) : typeRef;
      const f = inputFields(inner);
      stack.push({ k: "obj", typeName: f.name, fields: f.fields, current: null, given: new Set(), expectValue: false, variable: false });
    } else stack.push({ k: "list", item: typeRef && isList(stripNonNull(typeRef)) ? listItem(stripNonNull(typeRef)) : typeRef, variable: false });
  };

  for (const t of tokens) {
    const top = stack[stack.length - 1];
    const v = t.value;
    switch (top.k) {
      case "doc":
        if (t.kind === "name") {
          if (top.state === "start" && (v === "query" || v === "mutation" || v === "subscription")) {
            top.state = "opHead";
            top.nextType = rootOf(v);
            opVars = [];
          } else if (top.state === "start" && v === "fragment") top.state = "fragName";
          else if (top.state === "fragName") top.state = "fragOn";
          else if (top.state === "fragOn" && v === "on") top.state = "fragType";
          else if (top.state === "fragType") {
            top.nextType = v;
            top.state = "fragHead";
          }
        } else if (v === "(" && top.state === "opHead") stack.push({ k: "vars", expectType: false, expectName: false });
        else if (v === "(") stack.push({ k: "args", args: directives[0].args, current: null, given: new Set(), expectValue: false, variable: false });
        else if (v === "{") {
          if (top.state === "start") opVars = [];
          stack.push({ k: "sel", type: top.state === "start" ? schema.queryType : top.nextType, field: null, pendingType: null, spread: "", alias: false, directive: "" });
          top.state = "start";
          top.nextType = null;
        }
        break;
      case "vars":
        if (v === ")") stack.pop();
        else if (v === "$") top.expectName = true;
        else if (t.kind === "name" && top.expectName) {
          varName = v;
          top.expectName = false;
        } else if (v === ":") {
          top.expectType = true;
          if (varName) opVars.push({ name: varName, type: "" });
        } else if (top.expectType && (t.kind === "name" || v === "[" || v === "]" || v === "!")) {
          const last = opVars[opVars.length - 1];
          if (last) last.type += v;
          if (t.kind === "name") top.expectType = false;
        } else if (v === "]" || v === "!") {
          const last = opVars[opVars.length - 1];
          if (last) last.type += v;
        } else if (v === "=") top.expectType = false;
        break;
      case "sel":
        if (t.kind === "name") {
          if (top.directive === "at") top.directive = "named";
          else if (top.spread === "dots") {
            if (v === "on") top.spread = "on";
            else {
              top.spread = "";
              top.field = null;
              top.pendingType = null;
            }
          } else if (top.spread === "on") {
            top.spread = "";
            top.field = null;
            top.pendingType = v;
          } else {
            top.directive = "";
            top.alias = false;
            top.field = top.type ? (schema.field(top.type, v) ?? null) : null;
            top.pendingType = top.field ? namedType(top.field.type) : null;
          }
        } else if (v === ":") top.alias = true;
        else if (v === "...") {
          top.spread = "dots";
          top.directive = "";
        } else if (v === "@") top.directive = "at";
        else if (v === "(") {
          if (top.directive === "named") stack.push({ k: "args", args: directives[0].args, current: null, given: new Set(), expectValue: false, variable: false });
          else stack.push({ k: "args", args: top.field?.args ?? [], current: null, given: new Set(), expectValue: false, variable: false });
        } else if (v === "{") {
          const type = top.spread === "dots" ? top.type : top.pendingType;
          stack.push({ k: "sel", type: type ?? null, field: null, pendingType: null, spread: "", alias: false, directive: "" });
          top.field = null;
          top.pendingType = null;
          top.spread = "";
          top.directive = "";
        } else if (v === "}") stack.pop();
        break;
      case "args":
      case "obj": {
        const close = top.k === "args" ? ")" : "}";
        if (v === close && !top.expectValue) {
          stack.pop();
        } else if (!top.expectValue) {
          if (t.kind === "name") {
            top.current = top.k === "args" ? (top.args.find((a) => a.name === v) ?? null) : (top.fields.find((f) => f.name === v) ?? null);
            top.given.add(v);
          } else if (v === ":") top.expectValue = true;
        } else if (top.variable) {
          top.variable = false;
          top.expectValue = false;
        } else if (v === "$") top.variable = true;
        else if (v === "{" || v === "[") {
          top.expectValue = false;
          pushValue(top.current?.type ?? null, v);
        } else if (v === close) {
          stack.pop();
        } else top.expectValue = false;
        break;
      }
      case "list":
        if (top.variable) top.variable = false;
        else if (v === "$") top.variable = true;
        else if (v === "]") stack.pop();
        else if (v === "{" || v === "[") pushValue(top.item, v);
        break;
    }
  }

  const top = stack[stack.length - 1];
  let items: Completion[] = [];
  const prefix = word.replace(/^[$@]/, "");
  const variables = (typeRef: string | null): Completion[] =>
    opVars
      .filter((x) => !typeRef || namedType(x.type) === namedType(typeRef) || !x.type)
      .map((x) => ({ label: "$" + x.name, kind: "variable" as const, detail: x.type, insert: "$" + x.name }));

  if (word.startsWith("$")) {
    items = opVars.map((x) => ({ label: "$" + x.name, kind: "variable", detail: x.type, insert: "$" + x.name }));
  } else if (word.startsWith("@") || (top.k === "sel" && top.directive === "at")) {
    items = directives.map((d) => ({ label: "@" + d.name, kind: "directive", detail: "if: Boolean!", doc: d.description, insert: "@" + d.name + "(if: |)", reopen: true }));
  } else if (top.k === "doc") {
    if (top.state === "start") {
      items = [
        { label: "query", kind: "keyword", doc: "Reads data. A name and variables may follow.", insert: "query " },
        ...(schema.mutationType ? [{ label: "mutation", kind: "keyword" as const, doc: "Creates, updates or deletes nodes.", insert: "mutation " }] : []),
        { label: "fragment", kind: "keyword", doc: "A named set of fields to reuse: fragment Name on Type { … }.", insert: "fragment " },
        { label: "{", kind: "keyword", doc: "A query without a name.", insert: "{\n  |\n}", reopen: true },
      ];
    } else if (top.state === "fragOn") items = [{ label: "on", kind: "keyword", insert: "on ", reopen: true }];
    else if (top.state === "fragType") items = compositeTypes(schema).map((t) => typeItem(schema, t, true));
    else if (top.state === "opHead") items = [{ label: "{", kind: "keyword", insert: "{\n  |\n}", reopen: true }];
  } else if (top.k === "vars") {
    if (top.expectType) {
      items = [...schema.types.values()].filter((t) => schema.isInputType(t.name)).map((t) => typeItem(schema, t.name, false));
    }
  } else if (top.k === "sel") {
    if (top.spread === "dots") {
      items = [
        { label: "on", kind: "keyword", doc: "An inline fragment: fields that only apply to one type.", insert: "on ", reopen: true },
        ...fragmentNames(text)
          .filter((f) => !top.type || !schema.type(f.type) || schema.overlaps(f.type, top.type))
          .map((f) => ({ label: f.name, kind: "fragment" as const, detail: "on " + f.type, insert: f.name })),
      ];
    } else if (top.spread === "on") {
      items = (top.type ? schema.fragmentTargets(top.type) : []).map((t) => typeItem(schema, t, true));
    } else if (top.type && !top.type.startsWith("__")) {
      items = schema.fields(top.type).map((f) => fieldItem(schema, f));
      items.push({ label: "__typename", kind: "field", detail: "String!", doc: "The name of the object's type." });
      const possible = schema.possibleTypes(top.type).filter((p) => p !== top.type);
      for (const p of possible)
        items.push({ label: "... on " + p, kind: "fragment", detail: "inline fragment", doc: `Fields that only a ${p} has.`, insert: `... on ${p} {\n  |\n}`, reopen: true });
    }
  } else if (top.k === "args" || top.k === "obj") {
    if (!top.expectValue) {
      if (top.k === "args") items = top.args.filter((a) => !top.given.has(a.name)).map((a) => argItem(a.name, a.type, a.description, a.defaultValue, schema));
      else items = top.fields.filter((f) => !top.given.has(f.name)).map((f) => argItem(f.name, f.type, f.description, null, schema));
    } else {
      items = valueItems(schema, top.current?.type ?? null, variables(top.current?.type ?? null));
    }
  } else if (top.k === "list") {
    items = valueItems(schema, top.item, variables(top.item));
  }

  const lower = prefix.toLowerCase();
  const filtered = lower
    ? items
        .filter((i) => strip(i.label).toLowerCase().startsWith(lower))
        .concat(items.filter((i) => !strip(i.label).toLowerCase().startsWith(lower) && strip(i.label).toLowerCase().includes(lower)))
    : items;
  if (filtered.length === 0) return null;
  // the exact word alone is nothing to offer
  if (filtered.length === 1 && strip(filtered[0].label) === prefix && !filtered[0].reopen) return null;
  return { from, to, items: filtered.slice(0, 200) };
}

function strip(label: string): string {
  return label.replace(/^(\.\.\. on |[$@])/, "");
}

function compositeTypes(schema: Schema): string[] {
  return [...schema.types.values()].filter((t) => t.kind === "OBJECT" || t.kind === "INTERFACE").map((t) => t.name);
}

function typeItem(schema: Schema, name: string, withSelection: boolean): Completion {
  const t = schema.type(name);
  const detail = t ? (builtInScalars.has(name) ? "scalar" : t.kind.toLowerCase().replace("_", " ")) : undefined;
  return { label: name, kind: "type", detail, doc: t?.description, insert: withSelection ? `${name} {\n  |\n}` : name, reopen: withSelection };
}

function fieldItem(schema: Schema, f: FieldInfo): Completion {
  const composite = schema.isComposite(namedType(f.type));
  const required = f.args.filter((a) => isRequired(a));
  let insert = f.name;
  if (required.length > 0) insert += `(${required[0].name}: |)`;
  else if (composite) insert += " {\n  |\n}";
  return {
    label: f.name,
    kind: "field",
    detail: f.args.length > 0 ? `(${f.args.map((a) => a.name).join(", ")}) ${f.type}` : f.type,
    doc: f.description,
    insert,
    reopen: required.length > 0 || composite,
  };
}

function argItem(name: string, type: string, doc: string | null, defaultValue: string | null, schema: Schema): Completion {
  const inner = stripNonNull(type);
  const t = schema.type(namedType(type));
  let insert = name + ": |";
  if (isList(inner)) insert = name + ": [|]";
  else if (t?.kind === "INPUT_OBJECT") insert = name + ": { | }";
  return { label: name, kind: "argument", detail: type + (defaultValue ? " = " + defaultValue : ""), doc, insert, reopen: true };
}

function valueItems(schema: Schema, typeRef: string | null, variables: Completion[]): Completion[] {
  if (!typeRef) return variables;
  const items: Completion[] = [];
  const inner = stripNonNull(typeRef);
  if (isList(inner)) {
    items.push({ label: "[ ]", kind: "value", detail: inner, doc: "A list of values.", insert: "[|]", reopen: true });
    items.push(...valueItems(schema, listItem(inner), []));
  } else {
    const name = namedType(inner);
    const t = schema.type(name);
    if (t?.kind === "ENUM") for (const e of t.enumValues ?? []) items.push({ label: e, kind: "enum", detail: name });
    else if (t?.kind === "INPUT_OBJECT") items.push({ label: "{ }", kind: "value", detail: name, doc: t.description, insert: "{ | }", reopen: true });
    else if (name === "Boolean") items.push({ label: "true", kind: "value", detail: "Boolean" }, { label: "false", kind: "value", detail: "Boolean" });
    else if (name === "String" || name === "ID" || name === "DateTime") items.push({ label: '""', kind: "value", detail: name, insert: '"|"' });
  }
  if (!isNonNull(typeRef)) items.push({ label: "null", kind: "value", detail: "no value" });
  return [...items, ...variables];
}

function fragmentNames(text: string): { name: string; type: string }[] {
  const out: { name: string; type: string }[] = [];
  const re = /\bfragment\s+([A-Za-z_]\w*)\s+on\s+([A-Za-z_]\w*)/g;
  for (let m = re.exec(text); m; m = re.exec(text)) out.push({ name: m[1], type: m[2] });
  return out;
}

/** Whether the offset is inside a comment or a string, where nothing is completed. */
function insideCommentOrString(text: string, offset: number): boolean {
  const lineStart = text.lastIndexOf("\n", offset - 1) + 1;
  let inString = false;
  for (let i = lineStart; i < offset; i++) {
    const c = text[i];
    if (inString) {
      if (c === "\\") i++;
      else if (c === '"') inString = false;
    } else if (c === '"') inString = true;
    else if (c === "#") return true;
  }
  if (inString) return true;
  // inside a block string
  const before = text.slice(0, offset);
  const blocks = before.split('"""').length - 1;
  return blocks % 2 === 1;
}
