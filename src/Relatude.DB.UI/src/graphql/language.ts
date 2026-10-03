// GraphQL executable documents in the browser: a lexer, a parser that keeps the offsets of what it reads
// (for marking mistakes and for completion), and a printer. No dependencies, like the server's own parser
// (src/Relatude.DB.GraphQL/Language). Type-system definitions are not read: an endpoint only runs queries.

export interface Loc {
  start: number;
  end: number;
}

export type TokenKind = "punct" | "name" | "int" | "float" | "string" | "block" | "eof";

export interface Token extends Loc {
  kind: TokenKind;
  /** the punctuator or name as written; the decoded value for strings */
  value: string;
}

export class GqlSyntaxError extends Error {
  constructor(
    message: string,
    public offset: number,
  ) {
    super(message);
  }
}

const bom = String.fromCharCode(0xfeff);
const punctuators = new Set(["!", "$", "&", "(", ")", ":", "=", "@", "[", "]", "{", "|", "}"]);

function isNameStart(c: string): boolean {
  return (c >= "A" && c <= "Z") || (c >= "a" && c <= "z") || c === "_";
}

function isNameChar(c: string): boolean {
  return isNameStart(c) || (c >= "0" && c <= "9");
}

/**
 * The tokens of the text. Commas, white space and comments are skipped. A tolerant lexer reads to the end
 * whatever it meets - an unterminated string becomes a string to the end, an unknown character is skipped -
 * which is what completion needs while a query is being typed; a strict one throws.
 */
export function lex(text: string, tolerant = false): Token[] {
  const tokens: Token[] = [];
  const n = text.length;
  let i = 0;
  while (i < n) {
    const c = text[i];
    if (c === " " || c === "\t" || c === "\n" || c === "\r" || c === "," || c === bom) {
      i++;
      continue;
    }
    if (c === "#") {
      while (i < n && text[i] !== "\n" && text[i] !== "\r") i++;
      continue;
    }
    if (c === "." && text.startsWith("...", i)) {
      tokens.push({ kind: "punct", value: "...", start: i, end: i + 3 });
      i += 3;
      continue;
    }
    if (punctuators.has(c)) {
      tokens.push({ kind: "punct", value: c, start: i, end: i + 1 });
      i++;
      continue;
    }
    if (isNameStart(c)) {
      let j = i + 1;
      while (j < n && isNameChar(text[j])) j++;
      tokens.push({ kind: "name", value: text.slice(i, j), start: i, end: j });
      i = j;
      continue;
    }
    if (c === "-" || (c >= "0" && c <= "9")) {
      const m = /-?(?:0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?/y;
      m.lastIndex = i;
      const r = m.exec(text);
      if (!r || r[0] === "-") {
        if (!tolerant) throw new GqlSyntaxError(`Unexpected character "${c}".`, i);
        i++;
        continue;
      }
      tokens.push({ kind: r[1] || r[2] ? "float" : "int", value: r[0], start: i, end: i + r[0].length });
      i += r[0].length;
      continue;
    }
    if (c === '"') {
      if (text.startsWith('"""', i)) {
        const close = text.indexOf('"""', i + 3);
        if (close < 0 && !tolerant) throw new GqlSyntaxError("Unterminated block string.", i);
        const end = close < 0 ? n : close + 3;
        tokens.push({ kind: "block", value: blockValue(text.slice(i + 3, close < 0 ? n : close)), start: i, end });
        i = end;
        continue;
      }
      let j = i + 1;
      let value = "";
      let closed = false;
      while (j < n) {
        const d = text[j];
        if (d === '"') {
          closed = true;
          j++;
          break;
        }
        if (d === "\n" || d === "\r") break;
        if (d === "\\") {
          const e = text[j + 1];
          const simple: Record<string, string> = { '"': '"', "\\": "\\", "/": "/", b: "\b", f: "\f", n: "\n", r: "\r", t: "\t" };
          if (e in simple) {
            value += simple[e];
            j += 2;
            continue;
          }
          if (e === "u" && /^[0-9a-fA-F]{4}$/.test(text.slice(j + 2, j + 6))) {
            value += String.fromCharCode(parseInt(text.slice(j + 2, j + 6), 16));
            j += 6;
            continue;
          }
          if (!tolerant) throw new GqlSyntaxError("Invalid escape in a string.", j);
          j++;
          continue;
        }
        value += d;
        j++;
      }
      if (!closed && !tolerant) throw new GqlSyntaxError("Unterminated string.", i);
      tokens.push({ kind: "string", value, start: i, end: j });
      i = j;
      continue;
    }
    if (!tolerant) throw new GqlSyntaxError(`Unexpected character "${c}".`, i);
    i++;
  }
  tokens.push({ kind: "eof", value: "", start: n, end: n });
  return tokens;
}

function blockValue(raw: string): string {
  const lines = raw.replace(/\\"""/g, '"""').split(/\r\n|\r|\n/);
  let common = Infinity;
  for (let k = 1; k < lines.length; k++) {
    const indent = /^[ \t]*/.exec(lines[k])![0].length;
    if (indent < lines[k].length) common = Math.min(common, indent);
  }
  const out = lines.map((l, k) => (k > 0 && common !== Infinity ? l.slice(common) : l));
  while (out.length > 0 && out[0].trim() === "") out.shift();
  while (out.length > 0 && out[out.length - 1].trim() === "") out.pop();
  return out.join("\n");
}

// ---- the tree ----

export type ValueNode =
  | ({ kind: "Variable"; name: string } & Loc)
  | ({ kind: "Int"; value: string } & Loc)
  | ({ kind: "Float"; value: string } & Loc)
  | ({ kind: "String"; value: string; block?: boolean } & Loc)
  | ({ kind: "Boolean"; value: boolean } & Loc)
  | ({ kind: "Null" } & Loc)
  | ({ kind: "Enum"; value: string } & Loc)
  | ({ kind: "List"; values: ValueNode[] } & Loc)
  | ({ kind: "Object"; fields: ObjectFieldNode[] } & Loc);

export interface ObjectFieldNode extends Loc {
  name: string;
  nameLoc: Loc;
  value: ValueNode;
}

export interface ArgumentNode extends Loc {
  name: string;
  nameLoc: Loc;
  value: ValueNode;
}

export interface DirectiveNode extends Loc {
  name: string;
  arguments: ArgumentNode[];
}

export interface FieldNode extends Loc {
  kind: "Field";
  alias?: string;
  name: string;
  nameLoc: Loc;
  arguments: ArgumentNode[];
  directives: DirectiveNode[];
  selectionSet?: SelectionSetNode;
}

export interface InlineFragmentNode extends Loc {
  kind: "InlineFragment";
  typeCondition?: string;
  typeLoc?: Loc;
  directives: DirectiveNode[];
  selectionSet: SelectionSetNode;
}

export interface FragmentSpreadNode extends Loc {
  kind: "FragmentSpread";
  name: string;
  nameLoc: Loc;
  directives: DirectiveNode[];
}

export type SelectionNode = FieldNode | InlineFragmentNode | FragmentSpreadNode;

export interface SelectionSetNode extends Loc {
  selections: SelectionNode[];
}

export type TypeNode = ({ kind: "Named"; name: string } & Loc) | ({ kind: "List"; type: TypeNode } & Loc) | ({ kind: "NonNull"; type: TypeNode } & Loc);

export interface VariableDefinitionNode extends Loc {
  name: string;
  nameLoc: Loc;
  type: TypeNode;
  defaultValue?: ValueNode;
}

export type OperationType = "query" | "mutation" | "subscription";

export interface OperationNode extends Loc {
  kind: "Operation";
  operation: OperationType;
  name?: string;
  variables: VariableDefinitionNode[];
  directives: DirectiveNode[];
  selectionSet: SelectionSetNode;
}

export interface FragmentNode extends Loc {
  kind: "Fragment";
  name: string;
  nameLoc: Loc;
  typeCondition: string;
  typeLoc: Loc;
  directives: DirectiveNode[];
  selectionSet: SelectionSetNode;
}

export type DefinitionNode = OperationNode | FragmentNode;

export interface DocumentNode {
  definitions: DefinitionNode[];
}

/** A node made by code rather than read from text has no place in it. */
export const noLoc: Loc = { start: -1, end: -1 };

// ---- parsing ----

export function parse(text: string): DocumentNode {
  return new Parser(lex(text)).document();
}

/** The parse, or the syntax error with its offset. */
export function tryParse(text: string): { doc: DocumentNode; error: null } | { doc: null; error: GqlSyntaxError } {
  try {
    return { doc: parse(text), error: null };
  } catch (e) {
    if (e instanceof GqlSyntaxError) return { doc: null, error: e };
    return { doc: null, error: new GqlSyntaxError(e instanceof Error ? e.message : String(e), 0) };
  }
}

class Parser {
  private i = 0;
  constructor(private tokens: Token[]) {}

  private get tok(): Token {
    return this.tokens[this.i];
  }

  private peek(value: string): boolean {
    const t = this.tok;
    return (t.kind === "punct" || t.kind === "name") && t.value === value;
  }

  private skip(value: string): boolean {
    if (!this.peek(value)) return false;
    this.i++;
    return true;
  }

  private expect(value: string): Token {
    const t = this.tok;
    if (!this.peek(value)) throw new GqlSyntaxError(`Expected "${value}", found ${describe(t)}.`, t.start);
    this.i++;
    return t;
  }

  private name(): Token {
    const t = this.tok;
    if (t.kind !== "name") throw new GqlSyntaxError(`Expected a name, found ${describe(t)}.`, t.start);
    this.i++;
    return t;
  }

  document(): DocumentNode {
    const definitions: DefinitionNode[] = [];
    while (this.tok.kind !== "eof") definitions.push(this.definition());
    return { definitions };
  }

  private definition(): DefinitionNode {
    const t = this.tok;
    if (this.peek("{")) {
      const selectionSet = this.selectionSet();
      return { kind: "Operation", operation: "query", variables: [], directives: [], selectionSet, start: t.start, end: selectionSet.end };
    }
    if (t.kind === "name" && (t.value === "query" || t.value === "mutation" || t.value === "subscription")) {
      this.i++;
      const op: OperationNode = { kind: "Operation", operation: t.value, variables: [], directives: [], selectionSet: null!, start: t.start, end: t.end };
      if (this.tok.kind === "name") op.name = this.name().value;
      if (this.peek("(")) op.variables = this.variableDefinitions();
      op.directives = this.directives(false);
      op.selectionSet = this.selectionSet();
      op.end = op.selectionSet.end;
      return op;
    }
    if (t.kind === "name" && t.value === "fragment") {
      this.i++;
      const name = this.name();
      if (name.value === "on") throw new GqlSyntaxError('A fragment cannot be named "on".', name.start);
      this.expect("on");
      const type = this.name();
      const directives = this.directives(false);
      const selectionSet = this.selectionSet();
      return {
        kind: "Fragment",
        name: name.value,
        nameLoc: { start: name.start, end: name.end },
        typeCondition: type.value,
        typeLoc: { start: type.start, end: type.end },
        directives,
        selectionSet,
        start: t.start,
        end: selectionSet.end,
      };
    }
    throw new GqlSyntaxError(`Expected query, mutation, fragment or "{", found ${describe(t)}.`, t.start);
  }

  private variableDefinitions(): VariableDefinitionNode[] {
    this.expect("(");
    const list: VariableDefinitionNode[] = [];
    while (!this.skip(")")) {
      const start = this.expect("$").start;
      const name = this.name();
      this.expect(":");
      const type = this.type();
      const def: VariableDefinitionNode = { name: name.value, nameLoc: { start, end: name.end }, type, start, end: type.end };
      if (this.skip("=")) {
        def.defaultValue = this.value(true);
        def.end = def.defaultValue.end;
      }
      this.directives(true);
      list.push(def);
      if (this.tok.kind === "eof") throw new GqlSyntaxError('Expected ")".', this.tok.start);
    }
    return list;
  }

  private type(): TypeNode {
    const t = this.tok;
    let type: TypeNode;
    if (this.skip("[")) {
      const inner = this.type();
      const close = this.expect("]");
      type = { kind: "List", type: inner, start: t.start, end: close.end };
    } else {
      const name = this.name();
      type = { kind: "Named", name: name.value, start: name.start, end: name.end };
    }
    if (this.peek("!")) {
      const bang = this.expect("!");
      type = { kind: "NonNull", type, start: t.start, end: bang.end };
    }
    return type;
  }

  private directives(isConst: boolean): DirectiveNode[] {
    const list: DirectiveNode[] = [];
    while (this.peek("@")) {
      const at = this.expect("@");
      const name = this.name();
      const d: DirectiveNode = { name: name.value, arguments: [], start: at.start, end: name.end };
      if (this.peek("(")) {
        d.arguments = this.arguments(isConst);
        d.end = this.tokens[this.i - 1].end;
      }
      list.push(d);
    }
    return list;
  }

  private arguments(isConst: boolean): ArgumentNode[] {
    this.expect("(");
    const list: ArgumentNode[] = [];
    while (!this.skip(")")) {
      const name = this.name();
      this.expect(":");
      const value = this.value(isConst);
      list.push({ name: name.value, nameLoc: { start: name.start, end: name.end }, value, start: name.start, end: value.end });
      if (this.tok.kind === "eof") throw new GqlSyntaxError('Expected ")".', this.tok.start);
    }
    return list;
  }

  private selectionSet(): SelectionSetNode {
    const open = this.expect("{");
    const selections: SelectionNode[] = [];
    while (!this.peek("}")) {
      if (this.tok.kind === "eof") throw new GqlSyntaxError('Expected "}".', this.tok.start);
      selections.push(this.selection());
    }
    const close = this.expect("}");
    if (selections.length === 0) throw new GqlSyntaxError("A selection set cannot be empty.", open.start);
    return { selections, start: open.start, end: close.end };
  }

  private selection(): SelectionNode {
    const t = this.tok;
    if (this.skip("...")) {
      if (this.peek("on") || this.peek("{") || this.peek("@")) {
        let typeCondition: string | undefined;
        let typeLoc: Loc | undefined;
        if (this.skip("on")) {
          const type = this.name();
          typeCondition = type.value;
          typeLoc = { start: type.start, end: type.end };
        }
        const directives = this.directives(false);
        const selectionSet = this.selectionSet();
        return { kind: "InlineFragment", typeCondition, typeLoc, directives, selectionSet, start: t.start, end: selectionSet.end };
      }
      const name = this.name();
      const directives = this.directives(false);
      return { kind: "FragmentSpread", name: name.value, nameLoc: { start: name.start, end: name.end }, directives, start: t.start, end: this.tokens[this.i - 1].end };
    }
    let name = this.name();
    let alias: string | undefined;
    if (this.skip(":")) {
      alias = name.value;
      name = this.name();
    }
    const field: FieldNode = {
      kind: "Field",
      alias,
      name: name.value,
      nameLoc: { start: name.start, end: name.end },
      arguments: [],
      directives: [],
      start: t.start,
      end: name.end,
    };
    if (this.peek("(")) field.arguments = this.arguments(false);
    field.directives = this.directives(false);
    if (this.peek("{")) field.selectionSet = this.selectionSet();
    field.end = this.tokens[this.i - 1].end;
    return field;
  }

  private value(isConst: boolean): ValueNode {
    const t = this.tok;
    const loc = { start: t.start, end: t.end };
    if (t.kind === "punct") {
      if (t.value === "$") {
        if (isConst) throw new GqlSyntaxError("A variable cannot be used here.", t.start);
        this.i++;
        const name = this.name();
        return { kind: "Variable", name: name.value, start: t.start, end: name.end };
      }
      if (t.value === "[") {
        this.i++;
        const values: ValueNode[] = [];
        while (!this.peek("]")) {
          if (this.tok.kind === "eof") throw new GqlSyntaxError('Expected "]".', this.tok.start);
          values.push(this.value(isConst));
        }
        const close = this.expect("]");
        return { kind: "List", values, start: t.start, end: close.end };
      }
      if (t.value === "{") {
        this.i++;
        const fields: ObjectFieldNode[] = [];
        while (!this.peek("}")) {
          if (this.tok.kind === "eof") throw new GqlSyntaxError('Expected "}".', this.tok.start);
          const name = this.name();
          this.expect(":");
          const value = this.value(isConst);
          fields.push({ name: name.value, nameLoc: { start: name.start, end: name.end }, value, start: name.start, end: value.end });
        }
        const close = this.expect("}");
        return { kind: "Object", fields, start: t.start, end: close.end };
      }
    }
    this.i++;
    switch (t.kind) {
      case "int":
        return { kind: "Int", value: t.value, ...loc };
      case "float":
        return { kind: "Float", value: t.value, ...loc };
      case "string":
        return { kind: "String", value: t.value, ...loc };
      case "block":
        return { kind: "String", value: t.value, block: true, ...loc };
      case "name":
        if (t.value === "true" || t.value === "false") return { kind: "Boolean", value: t.value === "true", ...loc };
        if (t.value === "null") return { kind: "Null", ...loc };
        return { kind: "Enum", value: t.value, ...loc };
    }
    throw new GqlSyntaxError(`Expected a value, found ${describe(t)}.`, t.start);
  }
}

function describe(t: Token): string {
  if (t.kind === "eof") return "the end of the query";
  if (t.kind === "string" || t.kind === "block") return "a string";
  if (t.kind === "int" || t.kind === "float") return `the number ${t.value}`;
  return `"${t.value}"`;
}

// ---- printing ----

const indentUnit = "  ";

/** The document laid out the usual way: one field per line, arguments and values on the field's line. */
export function print(doc: DocumentNode): string {
  return doc.definitions.map(printDefinition).join("\n\n") + (doc.definitions.length > 0 ? "\n" : "");
}

export function printDefinition(def: DefinitionNode): string {
  if (def.kind === "Fragment") {
    return `fragment ${def.name} on ${def.typeCondition}${printDirectives(def.directives)} ${printSelectionSet(def.selectionSet, "")}`;
  }
  const anonymousQuery = def.operation === "query" && !def.name && def.variables.length === 0 && def.directives.length === 0;
  if (anonymousQuery) return printSelectionSet(def.selectionSet, "");
  const variables = def.variables.length > 0 ? "(" + def.variables.map(printVariableDefinition).join(", ") + ")" : "";
  return `${def.operation}${def.name ? " " + def.name : variables ? " " : ""}${variables}${printDirectives(def.directives)} ${printSelectionSet(def.selectionSet, "")}`;
}

function printVariableDefinition(v: VariableDefinitionNode): string {
  return `$${v.name}: ${printType(v.type)}${v.defaultValue ? " = " + printValue(v.defaultValue) : ""}`;
}

export function printType(t: TypeNode): string {
  if (t.kind === "Named") return t.name;
  if (t.kind === "List") return "[" + printType(t.type) + "]";
  return printType(t.type) + "!";
}

function printSelectionSet(set: SelectionSetNode, indent: string): string {
  const inner = indent + indentUnit;
  return "{\n" + set.selections.map((s) => inner + printSelection(s, inner)).join("\n") + "\n" + indent + "}";
}

function printSelection(s: SelectionNode, indent: string): string {
  switch (s.kind) {
    case "Field": {
      const head = (s.alias ? s.alias + ": " : "") + s.name + printArguments(s.arguments) + printDirectives(s.directives);
      return s.selectionSet ? head + " " + printSelectionSet(s.selectionSet, indent) : head;
    }
    case "InlineFragment":
      return "..." + (s.typeCondition ? " on " + s.typeCondition : "") + printDirectives(s.directives) + " " + printSelectionSet(s.selectionSet, indent);
    case "FragmentSpread":
      return "..." + s.name + printDirectives(s.directives);
  }
}

function printArguments(args: ArgumentNode[]): string {
  return args.length === 0 ? "" : "(" + args.map((a) => a.name + ": " + printValue(a.value)).join(", ") + ")";
}

function printDirectives(directives: DirectiveNode[]): string {
  return directives.map((d) => " @" + d.name + printArguments(d.arguments)).join("");
}

export function printValue(v: ValueNode): string {
  switch (v.kind) {
    case "Variable":
      return "$" + v.name;
    case "Int":
    case "Float":
      return v.value;
    case "String":
      return JSON.stringify(v.value);
    case "Boolean":
      return v.value ? "true" : "false";
    case "Null":
      return "null";
    case "Enum":
      return v.value;
    case "List":
      return "[" + v.values.map(printValue).join(", ") + "]";
    case "Object":
      return v.fields.length === 0 ? "{}" : "{ " + v.fields.map((f) => f.name + ": " + printValue(f.value)).join(", ") + " }";
  }
}

// ---- helpers ----

export function operations(doc: DocumentNode): OperationNode[] {
  return doc.definitions.filter((d): d is OperationNode => d.kind === "Operation");
}

export function fragments(doc: DocumentNode): Map<string, FragmentNode> {
  const map = new Map<string, FragmentNode>();
  for (const d of doc.definitions) if (d.kind === "Fragment") map.set(d.name, d);
  return map;
}

/** The operation the offset is in, or the first one. */
export function operationAt(doc: DocumentNode, offset: number): OperationNode | null {
  const ops = operations(doc);
  return ops.find((o) => offset >= o.start && offset <= o.end) ?? ops[0] ?? null;
}

/** The fragments an operation uses, directly or through other fragments. */
export function usedFragments(doc: DocumentNode, op: OperationNode): FragmentNode[] {
  const all = fragments(doc);
  const seen = new Set<string>();
  const order: FragmentNode[] = [];
  const visit = (set: SelectionSetNode) => {
    for (const s of set.selections) {
      if (s.kind === "Field" && s.selectionSet) visit(s.selectionSet);
      else if (s.kind === "InlineFragment") visit(s.selectionSet);
      else if (s.kind === "FragmentSpread" && !seen.has(s.name)) {
        seen.add(s.name);
        const f = all.get(s.name);
        if (f) {
          order.push(f);
          visit(f.selectionSet);
        }
      }
    }
  };
  visit(op.selectionSet);
  return order;
}

/** A value node for a plain json value: what a variable holds, written into the query. */
export function valueFromJson(value: unknown): ValueNode {
  if (value === null || value === undefined) return { kind: "Null", ...noLoc };
  if (typeof value === "boolean") return { kind: "Boolean", value, ...noLoc };
  if (typeof value === "number") return Number.isInteger(value) ? { kind: "Int", value: String(value), ...noLoc } : { kind: "Float", value: String(value), ...noLoc };
  if (typeof value === "string") return { kind: "String", value, ...noLoc };
  if (Array.isArray(value)) return { kind: "List", values: value.map(valueFromJson), ...noLoc };
  return {
    kind: "Object",
    fields: Object.entries(value as Record<string, unknown>).map(([name, v]) => ({ name, nameLoc: noLoc, value: valueFromJson(v), ...noLoc })),
    ...noLoc,
  };
}

/** The json a literal stands for; variables inside it become null. */
export function jsonFromValue(v: ValueNode, variables?: Record<string, unknown>): unknown {
  switch (v.kind) {
    case "Variable":
      return variables?.[v.name] ?? null;
    case "Int":
    case "Float":
      return Number(v.value);
    case "String":
    case "Enum":
      return v.value;
    case "Boolean":
      return v.value;
    case "Null":
      return null;
    case "List":
      return v.values.map((x) => jsonFromValue(x, variables));
    case "Object":
      return Object.fromEntries(v.fields.map((f) => [f.name, jsonFromValue(f.value, variables)]));
  }
}

export function lineAndColumn(text: string, offset: number): { line: number; column: number } {
  let line = 1;
  let lineStart = 0;
  const end = Math.min(offset, text.length);
  for (let i = 0; i < end; i++) {
    if (text.charCodeAt(i) === 10) {
      line++;
      lineStart = i + 1;
    }
  }
  return { line, column: end - lineStart + 1 };
}
