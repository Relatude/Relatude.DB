// The schema of an endpoint as the explorer reads it (SchemaDescription.cs on the server), with the lookups
// the builder, the docs, validation, completion and the code writers share. Type references are SDL strings
// such as "[Article!]!".

export type TypeKind = "SCALAR" | "OBJECT" | "INTERFACE" | "ENUM" | "INPUT_OBJECT";

/** What a type is for: root, node, result (a page of nodes), object, filter, operator, input, orderBy, enum, scalar. */
export type TypeRole = "root" | "node" | "result" | "object" | "filter" | "operator" | "input" | "orderBy" | "enum" | "scalar";

/** What a field is: system, property, enum, file, geo, relation, reference, single, list, view, create, update, delete, items, count, value. */
export type FieldKind =
  "system" | "property" | "enum" | "file" | "geo" | "relation" | "reference" | "single" | "list" | "view" | "create" | "update" | "delete" | "items" | "count" | "value" | "meta";

export interface ArgInfo {
  name: string;
  type: string;
  description: string | null;
  /** as a GraphQL literal, such as "0" or "false" */
  defaultValue: string | null;
}

export interface FieldInfo {
  name: string;
  type: string;
  description: string | null;
  kind: FieldKind;
  many: boolean;
  property: string | null;
  nodeType: string | null;
  args: ArgInfo[];
}

export interface InputFieldInfo {
  name: string;
  type: string;
  description: string | null;
  property: string | null;
}

export interface TypeInfo {
  name: string;
  kind: TypeKind;
  role: TypeRole;
  description: string | null;
  nodeType: string | null;
  interfaces: string[] | null;
  possibleTypes: string[] | null;
  fields: FieldInfo[] | null;
  inputFields: InputFieldInfo[] | null;
  enumValues: string[] | null;
}

export interface SchemaInfo {
  queryType: string;
  mutationType: string | null;
  description: string | null;
  types: TypeInfo[];
}

const typenameField: FieldInfo = {
  name: "__typename",
  type: "String!",
  description: "The name of the object's type.",
  kind: "meta",
  many: false,
  property: null,
  nodeType: null,
  args: [],
};

const schemaField: FieldInfo = {
  name: "__schema",
  type: "__Schema!",
  description: "Introspection: the whole schema.",
  kind: "meta",
  many: false,
  property: null,
  nodeType: null,
  args: [],
};

const typeField: FieldInfo = {
  name: "__type",
  type: "__Type",
  description: "Introspection: one type by name.",
  kind: "meta",
  many: false,
  property: null,
  nodeType: null,
  args: [{ name: "name", type: "String!", description: null, defaultValue: null }],
};

export const directives = [
  { name: "include", description: "Only include this when if is true.", args: [{ name: "if", type: "Boolean!", description: null, defaultValue: null }] },
  { name: "skip", description: "Leave this out when if is true.", args: [{ name: "if", type: "Boolean!", description: null, defaultValue: null }] },
];

export const builtInScalars = new Set(["Int", "Float", "String", "Boolean", "ID"]);

export class Schema {
  readonly types = new Map<string, TypeInfo>();
  readonly queryType: string;
  readonly mutationType: string | null;
  readonly description: string | null;
  private usedByCache: Map<string, { type: string; field: string; arg?: string }[]> | null = null;

  constructor(
    public readonly info: SchemaInfo,
    /** whether __schema and __type are answered */
    public readonly introspection = true,
  ) {
    for (const t of info.types) this.types.set(t.name, t);
    this.queryType = info.queryType;
    this.mutationType = info.mutationType;
    this.description = info.description;
  }

  type(name: string | null | undefined): TypeInfo | undefined {
    return name ? this.types.get(name) : undefined;
  }

  /** The fields of a composite type; the root query type also offers __schema and __type when introspection is on. */
  fields(typeName: string): FieldInfo[] {
    const t = this.types.get(typeName);
    if (!t || !t.fields) return [];
    if (typeName === this.queryType && this.introspection) return [...t.fields, schemaField, typeField];
    return t.fields;
  }

  field(typeName: string, fieldName: string): FieldInfo | undefined {
    if (fieldName === "__typename") return typenameField;
    return this.fields(typeName).find((f) => f.name === fieldName);
  }

  isComposite(name: string): boolean {
    const k = this.types.get(name)?.kind;
    return k === "OBJECT" || k === "INTERFACE" || name.startsWith("__");
  }

  isInputType(name: string): boolean {
    const k = this.types.get(name)?.kind;
    return k === "SCALAR" || k === "ENUM" || k === "INPUT_OBJECT";
  }

  /** The object types a value of this type can be. */
  possibleTypes(name: string): string[] {
    const t = this.types.get(name);
    if (!t) return [];
    if (t.kind === "INTERFACE") return t.possibleTypes ?? [];
    if (t.kind === "OBJECT") return [name];
    return [];
  }

  /** Whether a fragment on `condition` can ever apply inside a selection on `parent`. */
  overlaps(condition: string, parent: string): boolean {
    const a = new Set(this.possibleTypes(condition));
    return this.possibleTypes(parent).some((p) => a.has(p));
  }

  /** The type conditions that make sense inside a selection on `parent`: the type itself, its interfaces, its possible types. */
  fragmentTargets(parent: string): string[] {
    const t = this.types.get(parent);
    if (!t) return [];
    const out = new Set<string>([parent]);
    for (const i of t.interfaces ?? []) out.add(i);
    for (const p of this.possibleTypes(parent)) out.add(p);
    return [...out];
  }

  /** Where a type is used: fields that return it, arguments and input fields that take it. */
  usedBy(name: string): { type: string; field: string; arg?: string }[] {
    if (!this.usedByCache) {
      const map = new Map<string, { type: string; field: string; arg?: string }[]>();
      const add = (target: string, entry: { type: string; field: string; arg?: string }) => {
        const list = map.get(target) ?? [];
        list.push(entry);
        map.set(target, list);
      };
      for (const t of this.types.values()) {
        for (const f of t.fields ?? []) {
          add(namedType(f.type), { type: t.name, field: f.name });
          for (const a of f.args) add(namedType(a.type), { type: t.name, field: f.name, arg: a.name });
        }
        for (const f of t.inputFields ?? []) add(namedType(f.type), { type: t.name, field: f.name });
      }
      this.usedByCache = map;
    }
    return this.usedByCache.get(name) ?? [];
  }

  /** The root field that fetches one node of the given type by id, when there is one. */
  singleFieldFor(typeName: string): FieldInfo | undefined {
    const roots = this.fields(this.queryType).filter((f) => f.kind === "single");
    const direct = roots.find((f) => namedType(f.type) === typeName);
    if (direct) return direct;
    // an object type is fetched through the interface it is referred to by
    const t = this.types.get(typeName);
    return roots.find((f) => (t?.interfaces ?? []).includes(namedType(f.type)) && this.possibleTypes(namedType(f.type)).includes(typeName));
  }
}

// ---- type references ----

/** "[Article!]!" -> "Article" */
export function namedType(ref: string): string {
  return ref.replace(/[[\]!]/g, "");
}

export function isNonNull(ref: string): boolean {
  return ref.endsWith("!");
}

export function stripNonNull(ref: string): string {
  return ref.endsWith("!") ? ref.slice(0, -1) : ref;
}

/** "[Article!]!" -> true */
export function isList(ref: string): boolean {
  return stripNonNull(ref).startsWith("[");
}

/** "[Article!]!" -> "Article!" */
export function listItem(ref: string): string {
  const s = stripNonNull(ref);
  return s.startsWith("[") ? s.slice(1, -1) : s;
}

/** An argument the server insists on: non-null and without a default. */
export function isRequired(arg: { type: string; defaultValue?: string | null }): boolean {
  return isNonNull(arg.type) && (arg.defaultValue === null || arg.defaultValue === undefined);
}

// ---- spelling ----

/** The closest of the candidates to a misspelled name, when it is close enough to be what was meant. */
export function suggest(name: string, candidates: string[]): string | null {
  const lower = name.toLowerCase();
  let best: string | null = null;
  let bestDistance = Infinity;
  for (const c of candidates) {
    const cl = c.toLowerCase();
    if (cl === lower) return c;
    const d = distance(lower, cl);
    if (d < bestDistance) {
      bestDistance = d;
      best = c;
    }
  }
  if (best === null) return null;
  const limit = Math.max(1, Math.floor(Math.max(name.length, best.length) / 3));
  return bestDistance <= limit || best.toLowerCase().startsWith(lower) ? best : null;
}

function distance(a: string, b: string): number {
  const row = Array.from({ length: b.length + 1 }, (_, j) => j);
  for (let i = 1; i <= a.length; i++) {
    let prev = row[0];
    row[0] = i;
    for (let j = 1; j <= b.length; j++) {
      const tmp = row[j];
      row[j] = Math.min(row[j] + 1, row[j - 1] + 1, prev + (a[i - 1] === b[j - 1] ? 0 : 1));
      prev = tmp;
    }
  }
  return row[b.length];
}
