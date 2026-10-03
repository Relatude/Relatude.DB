// Code for the query in the explorer, in the languages clients are written in: the model (types for what the
// query answers and for its variables, or for the whole schema) and the code that connects to the endpoint and
// runs it, with an example call using the variables as they are. Each file is complete: copy, paste, run.

import type { Language } from "../code/language";
import { print, usedFragments, type DocumentNode, type OperationNode, type SelectionSetNode, type TypeNode } from "./language";
import type { Schema } from "./schema";

export type CodeLanguage = "curl" | "powershell" | "javascript" | "typescript" | "python" | "csharp" | "java" | "go";
export type CodeScope = "query" | "schema";
export type CodePart = "all" | "model" | "connect";

export const codeLanguages: { id: CodeLanguage; label: string; editor: Language; extension: string; models: boolean }[] = [
  { id: "typescript", label: "TypeScript", editor: "typescript", extension: ".ts", models: true },
  { id: "javascript", label: "JavaScript", editor: "javascript", extension: ".js", models: true },
  { id: "csharp", label: "C#", editor: "csharp", extension: ".cs", models: true },
  { id: "python", label: "Python", editor: "python", extension: ".py", models: true },
  { id: "java", label: "Java", editor: "java", extension: ".java", models: true },
  { id: "go", label: "Go", editor: "go", extension: ".go", models: true },
  { id: "curl", label: "curl", editor: "shell", extension: ".sh", models: false },
  { id: "powershell", label: "PowerShell", editor: "shell", extension: ".ps1", models: false },
];

// ---- the model: what the languages write types for ----

type Ref =
  | { k: "scalar"; name: string; nullable: boolean }
  | { k: "enum"; name: string; nullable: boolean }
  | { k: "object"; name: string; nullable: boolean }
  | { k: "input"; name: string; nullable: boolean }
  | { k: "list"; of: Ref; nullable: boolean };

interface MField {
  key: string;
  ref: Ref;
  /** left out of the answer when a fragment on another type does not apply, or optional in an input */
  optional: boolean;
  description: string | null;
}

interface MType {
  name: string;
  input: boolean;
  fields: MField[];
  description: string | null;
}

interface Model {
  types: MType[];
  enums: { name: string; values: string[] }[];
}

interface Request {
  endpoint: string;
  apiKey: boolean;
  /** the document sent: the operation and the fragments it uses */
  document: string;
  operationName: string | null;
  /** PascalCase base for names: Paging, Query */
  base: string;
  /** the answer's type; null for the whole-schema model, where the answer is left as plain json */
  dataType: string | null;
  variablesType: string | null;
  variables: Record<string, unknown> | null;
  /** the operation's variables as fields, for writing the example values typed */
  variableFields: MField[];
}

export interface CodeInput {
  schema: Schema;
  doc: DocumentNode;
  op: OperationNode;
  variablesText: string;
  endpoint: string;
  apiKey: boolean;
}

export function generateCode(language: CodeLanguage, scope: CodeScope, part: CodePart, input: CodeInput): string {
  const { model, request } = scope === "query" ? queryModel(input) : schemaModel(input);
  const writer = writers[language];
  const model_ = writer.model(model, request);
  const connect = writer.connect(request, model);
  if (part === "model")
    return (
      model_ ||
      writer.comment("Nothing to declare: " + (language === "curl" || language === "powershell" ? "a shell sends json as it is." : "the query takes and returns no types."))
    );
  if (part === "connect") return connect;
  return writer.join(model_, connect);
}

function pascal(name: string): string {
  const s = name.replace(/^_+/, "").replace(/[^A-Za-z0-9_]/g, "");
  return s ? s[0].toUpperCase() + s.slice(1) : "Value";
}

function camel(name: string): string {
  const p = pascal(name);
  return p[0].toLowerCase() + p.slice(1);
}

function singular(name: string): string {
  if (/ies$/.test(name)) return name.slice(0, -3) + "y";
  if (/(ss|us)$/.test(name)) return name;
  if (/s$/.test(name)) return name.slice(0, -1);
  return name;
}

function refOfTypeNode(t: TypeNode, schema: Schema, inputs: (name: string) => void, nullable = true): Ref {
  if (t.kind === "NonNull") return refOfTypeNode(t.type, schema, inputs, false);
  if (t.kind === "List") return { k: "list", of: refOfTypeNode(t.type, schema, inputs), nullable };
  return namedRef(t.name, schema, nullable, inputs);
}

function refOfString(ref: string, schema: Schema, named: (name: string, nullable: boolean) => Ref, nullable = true): Ref {
  if (ref.endsWith("!")) return refOfString(ref.slice(0, -1), schema, named, false);
  if (ref.startsWith("[")) return { k: "list", of: refOfString(ref.slice(1, -1), schema, named), nullable };
  return named(ref, nullable);
}

function namedRef(name: string, schema: Schema, nullable: boolean, inputs: (name: string) => void): Ref {
  const t = schema.type(name);
  if (t?.kind === "ENUM") return { k: "enum", name, nullable };
  if (t?.kind === "INPUT_OBJECT") {
    inputs(name);
    return { k: "input", name, nullable };
  }
  return { k: "scalar", name, nullable };
}

function parseVariables(text: string): Record<string, unknown> {
  try {
    const v = JSON.parse(text || "{}");
    return v && typeof v === "object" && !Array.isArray(v) ? (v as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}

function documentOf(doc: DocumentNode, op: OperationNode): string {
  return print({ definitions: [op, ...usedFragments(doc, op)] }).trimEnd();
}

/** Types for exactly what the query selects, named after the operation and the path to each object. */
function queryModel(input: CodeInput): { model: Model; request: Request } {
  const { schema, doc, op } = input;
  const types: MType[] = [];
  const enums = new Map<string, string[]>();
  const taken = new Set<string>();
  const unique = (name: string) => {
    let n = name;
    for (let i = 2; taken.has(n); i++) n = name + i;
    taken.add(n);
    return n;
  };
  const frags = new Map(doc.definitions.filter((d) => d.kind === "Fragment").map((d) => [d.name, d] as const));
  const inputsWanted: string[] = [];
  const wantInput = (name: string) => {
    if (!inputsWanted.includes(name)) inputsWanted.push(name);
  };
  const enumRef = (name: string) => {
    if (!enums.has(name)) enums.set(name, schema.type(name)?.enumValues ?? []);
  };

  const base = pascal(op.name ?? (op.operation === "mutation" ? "Mutation" : "Query"));
  const objectType = (typeName: string, set: SelectionSetNode, genName: string): string => {
    const name = unique(genName);
    const t: MType = { name, input: false, fields: [], description: null };
    types.push(t);
    const collect = (on: string, s: SelectionSetNode, optional: boolean) => {
      for (const sel of s.selections) {
        if (sel.kind === "Field") {
          const key = sel.alias ?? sel.name;
          if (t.fields.some((f) => f.key === key)) continue;
          const def = schema.field(on, sel.name);
          if (!def) continue;
          const conditional = optional || sel.directives.some((d) => d.name === "include" || d.name === "skip");
          const ref = refOfString(def.type, schema, (n, nullable) => {
            const kind = schema.type(n)?.kind;
            if (kind === "ENUM") {
              enumRef(n);
              return { k: "enum", name: n, nullable };
            }
            if ((kind === "OBJECT" || kind === "INTERFACE") && sel.selectionSet) {
              const child = objectType(n, sel.selectionSet, name.replace(/Data$/, "") + pascal(def.many || isListRef(def.type) ? singular(key) : key));
              return { k: "object", name: child, nullable };
            }
            return { k: "scalar", name: n.startsWith("__") ? "JSON" : n, nullable };
          });
          t.fields.push({ key, ref, optional: conditional, description: def.description });
        } else {
          const condition = sel.kind === "InlineFragment" ? (sel.typeCondition ?? on) : (frags.get(sel.name)?.typeCondition ?? on);
          const setOf = sel.kind === "InlineFragment" ? sel.selectionSet : frags.get(sel.name)?.selectionSet;
          if (!setOf) continue;
          // a fragment on the type itself or an interface it has always applies; on a subtype only sometimes
          const always = condition === on || (schema.type(on)?.interfaces ?? []).includes(condition);
          collect(always ? on : condition, setOf, optional || !always);
        }
      }
    };
    collect(typeName, set, false);
    return name;
  };
  const root = op.operation === "mutation" ? schema.mutationType : schema.queryType;
  const dataType = root ? objectType(root, op.selectionSet, base + "Data") : null;

  const variableFields: MField[] = op.variables.map((v) => ({
    key: v.name,
    ref: refOfTypeNode(v.type, schema, wantInput),
    optional: v.type.kind !== "NonNull" || !!v.defaultValue,
    description: null,
  }));
  let variablesType: string | null = null;
  if (variableFields.length > 0) {
    variablesType = unique(base + "Variables");
    types.push({ name: variablesType, input: true, fields: variableFields, description: null });
    for (const f of variableFields) enumsIn(f.ref, enumRef);
  }
  // the input types the variables refer to, and the ones those refer to
  for (let i = 0; i < inputsWanted.length; i++) {
    const t = schema.type(inputsWanted[i]);
    if (!t) continue;
    taken.add(t.name);
    const fields: MField[] = (t.inputFields ?? []).map((f) => {
      const ref = refOfString(f.type, schema, (n, nullable) => namedRef(n, schema, nullable, wantInput));
      enumsIn(ref, enumRef);
      return { key: f.name, ref, optional: !f.type.endsWith("!"), description: f.description };
    });
    types.push({ name: t.name, input: true, fields, description: t.description });
  }
  const values = parseVariables(input.variablesText);
  const variables = op.variables.length > 0 ? Object.fromEntries(op.variables.filter((v) => v.name in values).map((v) => [v.name, values[v.name]])) : null;
  return {
    model: { types, enums: [...enums].map(([name, values]) => ({ name, values })) },
    request: {
      endpoint: input.endpoint,
      apiKey: input.apiKey,
      document: documentOf(doc, op),
      operationName: op.name ?? null,
      base,
      dataType,
      variablesType,
      variables,
      variableFields,
    },
  };
}

function isListRef(ref: string): boolean {
  return ref.replace(/!$/, "").startsWith("[");
}

function enumsIn(ref: Ref, add: (name: string) => void) {
  if (ref.k === "enum") add(ref.name);
  else if (ref.k === "list") enumsIn(ref.of, add);
}

/** Types for every node type, page, enum and input of the schema; the answer itself is left as json. */
function schemaModel(input: CodeInput): { model: Model; request: Request } {
  const { schema, doc, op } = input;
  const types: MType[] = [];
  const enums: { name: string; values: string[] }[] = [];
  const named = (n: string, nullable: boolean): Ref => {
    const kind = schema.type(n)?.kind;
    if (kind === "ENUM") return { k: "enum", name: n, nullable };
    if (kind === "INPUT_OBJECT") return { k: "input", name: n, nullable };
    if (kind === "OBJECT" || kind === "INTERFACE") return { k: "object", name: n, nullable };
    return { k: "scalar", name: n, nullable };
  };
  for (const t of schema.types.values()) {
    if (t.role === "root") continue;
    if (t.kind === "ENUM") enums.push({ name: t.name, values: t.enumValues ?? [] });
    else if (t.kind === "OBJECT" || t.kind === "INTERFACE") {
      types.push({
        name: t.name,
        input: false,
        description: t.description,
        fields: (t.fields ?? []).map((f) => ({ key: f.name, ref: refOfString(f.type, schema, named), optional: false, description: f.description })),
      });
    } else if (t.kind === "INPUT_OBJECT") {
      types.push({
        name: t.name,
        input: true,
        description: t.description,
        fields: (t.inputFields ?? []).map((f) => ({ key: f.name, ref: refOfString(f.type, schema, named), optional: !f.type.endsWith("!"), description: f.description })),
      });
    }
  }
  const values = parseVariables(input.variablesText);
  return {
    model: { types, enums },
    request: {
      endpoint: input.endpoint,
      apiKey: input.apiKey,
      document: documentOf(doc, op),
      operationName: op.name ?? null,
      base: pascal(op.name ?? "Query"),
      dataType: null,
      variablesType: null,
      variables: op.variables.length > 0 ? Object.fromEntries(op.variables.filter((v) => v.name in values).map((v) => [v.name, values[v.name]])) : null,
      variableFields: [],
    },
  };
}

// ---- the writers ----

interface Writer {
  model(model: Model, request: Request): string;
  connect(request: Request, model: Model): string;
  join(model: string, connect: string): string;
  comment(text: string): string;
}

const apiKeyNote = "The api key is read from the GRAPHQL_API_KEY environment variable; keep it out of code that runs in a browser.";

const writers: Record<CodeLanguage, Writer> = {
  typescript: {
    comment: (t) => "// " + t + "\n",
    model(model) {
      const out: string[] = [];
      for (const e of model.enums) out.push(`export type ${e.name} = ${e.values.map((v) => JSON.stringify(v)).join(" | ") || "string"};`);
      for (const t of model.types) {
        const lines = t.fields.map((f) => `  ${tsKey(f.key)}${f.optional ? "?" : ""}: ${tsType(f.ref)};${f.description ? " // " + oneLine(f.description) : ""}`);
        out.push(`${t.description ? "/** " + oneLine(t.description) + " */\n" : ""}export interface ${t.name} {\n${lines.join("\n")}\n}`);
      }
      return out.join("\n\n");
    },
    connect(r) {
      const fn = "run" + r.base;
      const typed = r.dataType !== null;
      const data = r.dataType ?? "T";
      const lines = [
        `const endpoint = ${JSON.stringify(r.endpoint)};`,
        ...(r.apiKey ? [`// ${apiKeyNote}`, `const apiKey = process.env.GRAPHQL_API_KEY ?? "";`] : []),
        "",
        `const ${camel(r.base)}Query = ${templateLiteral(r.document)};`,
        "",
        typed
          ? `export async function ${fn}(${r.variablesType ? `variables: ${r.variablesType}` : ""}): Promise<${data}> {`
          : `export async function graphql<T = unknown>(query: string, variables?: Record<string, unknown>, operationName?: string): Promise<T> {`,
        `  const response = await fetch(endpoint, {`,
        `    method: "POST",`,
        `    headers: { "Content-Type": "application/json"${r.apiKey ? `, "X-Api-Key": apiKey` : ""} },`,
        typed
          ? `    body: JSON.stringify({ query: ${camel(r.base)}Query${r.variablesType ? ", variables" : ""}${r.operationName ? `, operationName: ${JSON.stringify(r.operationName)}` : ""} }),`
          : `    body: JSON.stringify({ query, variables, operationName }),`,
        `  });`,
        "  if (!response.ok) throw new Error(`${response.status} ${response.statusText}: ${await response.text()}`);",
        `  const result = (await response.json()) as { data?: ${data}; errors?: { message: string }[] };`,
        `  if (result.errors?.length) throw new Error(result.errors.map((e) => e.message).join("\\n"));`,
        `  return result.data as ${data};`,
        `}`,
        "",
        `// Usage`,
        typed
          ? `const data = await ${fn}(${r.variablesType ? jsonLiteral(r.variables ?? {}, "") : ""});`
          : `const data = await graphql(${camel(r.base)}Query${r.variables ? ", " + jsonLiteral(r.variables, "") : r.operationName ? ", undefined" : ""}${r.operationName ? ", " + JSON.stringify(r.operationName) : ""});`,
        `console.log(JSON.stringify(data, null, 2));`,
      ];
      return lines.join("\n");
    },
    join: (model, connect) => [header0("//"), model, connect].filter(Boolean).join("\n\n") + "\n",
  },

  javascript: {
    comment: (t) => "// " + t + "\n",
    model(model) {
      const out: string[] = [];
      for (const e of model.enums) out.push(`/** @typedef {${e.values.map((v) => JSON.stringify(v)).join(" | ") || "string"}} ${e.name} */`);
      for (const t of model.types) {
        const lines = [`/**`, ...(t.description ? [` * ${oneLine(t.description)}`] : []), ` * @typedef {object} ${t.name}`];
        for (const f of t.fields) lines.push(` * @property {${jsType(f.ref)}} ${f.optional ? "[" + f.key + "]" : f.key}${f.description ? " " + oneLine(f.description) : ""}`);
        lines.push(` */`);
        out.push(lines.join("\n"));
      }
      return out.join("\n\n");
    },
    connect(r) {
      const fn = "run" + r.base;
      const typed = r.dataType !== null;
      const lines = [
        `const endpoint = ${JSON.stringify(r.endpoint)};`,
        ...(r.apiKey ? [`// ${apiKeyNote}`, `const apiKey = process.env.GRAPHQL_API_KEY ?? "";`] : []),
        "",
        `const ${camel(r.base)}Query = ${templateLiteral(r.document)};`,
        "",
        typed
          ? [`/**`, ...(r.variablesType ? [` * @param {${r.variablesType}} variables`] : []), ` * @returns {Promise<${r.dataType}>}`, ` */`].join("\n")
          : `/** @returns {Promise<any>} */`,
        typed ? `export async function ${fn}(${r.variablesType ? "variables" : ""}) {` : `export async function graphql(query, variables, operationName) {`,
        `  const response = await fetch(endpoint, {`,
        `    method: "POST",`,
        `    headers: { "Content-Type": "application/json"${r.apiKey ? `, "X-Api-Key": apiKey` : ""} },`,
        typed
          ? `    body: JSON.stringify({ query: ${camel(r.base)}Query${r.variablesType ? ", variables" : ""}${r.operationName ? `, operationName: ${JSON.stringify(r.operationName)}` : ""} }),`
          : `    body: JSON.stringify({ query, variables, operationName }),`,
        `  });`,
        "  if (!response.ok) throw new Error(`${response.status} ${response.statusText}: ${await response.text()}`);",
        `  const result = await response.json();`,
        `  if (result.errors?.length) throw new Error(result.errors.map((e) => e.message).join("\\n"));`,
        `  return result.data;`,
        `}`,
        "",
        `// Usage`,
        typed
          ? `const data = await ${fn}(${r.variablesType ? jsonLiteral(r.variables ?? {}, "") : ""});`
          : `const data = await graphql(${camel(r.base)}Query${r.variables ? ", " + jsonLiteral(r.variables, "") : r.operationName ? ", undefined" : ""}${r.operationName ? ", " + JSON.stringify(r.operationName) : ""});`,
        `console.log(JSON.stringify(data, null, 2));`,
      ];
      return lines.join("\n");
    },
    join: (model, connect) => [header0("//"), model, connect].filter(Boolean).join("\n\n") + "\n",
  },

  csharp: {
    comment: (t) => "// " + t + "\n",
    model(model) {
      const out: string[] = [];
      for (const e of model.enums) {
        out.push(`[JsonConverter(typeof(JsonStringEnumConverter))]\npublic enum ${e.name} { ${e.values.map(csIdentifier).join(", ")} }`);
      }
      for (const t of model.types) {
        const lines = t.fields.map((f) => {
          const prop = csProperty(f.key, t.name);
          const type = csType(f.ref, f.optional);
          const init = csInitializer(f.ref, f.optional);
          const ignore = t.input && (f.optional || f.ref.nullable) ? "[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] " : "";
          return `${f.description ? `    /// <summary>${xmlEscape(oneLine(f.description))}</summary>\n` : ""}    [JsonPropertyName(${JSON.stringify(f.key)})] ${ignore}public ${type} ${prop} { get; set; }${init}`;
        });
        out.push(`${t.description ? `/// <summary>${xmlEscape(oneLine(t.description))}</summary>\n` : ""}public sealed class ${t.name} {\n${lines.join("\n")}\n}`);
      }
      return out.join("\n\n");
    },
    connect(r, model) {
      const cls = r.base + "Request";
      const typed = r.dataType !== null;
      const vars = r.variablesType ? csLiteral({ k: "input", name: r.variablesType, nullable: false }, r.variables ?? {}, model, "") : null;
      const usage = typed
        ? [
            `using var http = new HttpClient();`,
            `var data = await ${cls}.RunAsync(http${vars ? ", " + vars : ""});`,
            `Console.WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));`,
          ]
        : [
            `using var http = new HttpClient();`,
            `var data = await GraphQLClient.QueryAsync<JsonElement>(http, ${cls}.Query, ${r.variables ? `JsonSerializer.Deserialize<JsonElement>(${csString(JSON.stringify(r.variables))})` : "null"}${r.operationName ? ", " + JSON.stringify(r.operationName) : ""});`,
            `Console.WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));`,
          ];
      const lines = [
        `// Usage`,
        ...usage,
        "",
        `public static class ${cls} {`,
        `    public const string Query = """`,
        ...r.document.split("\n").map((l) => (l ? "        " + l : "")),
        `        """;`,
        ...(typed
          ? [
              "",
              `    public static Task<${r.dataType}> RunAsync(HttpClient http${r.variablesType ? `, ${r.variablesType} variables` : ""}, CancellationToken cancellationToken = default)`,
              `        => GraphQLClient.QueryAsync<${r.dataType}>(http, Query, ${r.variablesType ? "variables" : "null"}, ${r.operationName ? JSON.stringify(r.operationName) : "null"}, cancellationToken);`,
            ]
          : []),
        `}`,
        "",
        `public static class GraphQLClient {`,
        `    public const string Endpoint = ${JSON.stringify(r.endpoint)};`,
        "",
        `    public static async Task<T> QueryAsync<T>(HttpClient http, string query, object? variables = null, string? operationName = null, CancellationToken cancellationToken = default) {`,
        `        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) {`,
        `            Content = JsonContent.Create(new GraphQLRequest(query, variables, operationName)),`,
        `        };`,
        ...(r.apiKey ? [`        // ${apiKeyNote}`, `        request.Headers.Add("X-Api-Key", Environment.GetEnvironmentVariable("GRAPHQL_API_KEY"));`] : []),
        `        using var response = await http.SendAsync(request, cancellationToken);`,
        `        response.EnsureSuccessStatusCode();`,
        `        var result = await response.Content.ReadFromJsonAsync<GraphQLResponse<T>>(cancellationToken);`,
        `        if (result?.Errors is { Count: > 0 } errors) throw new InvalidOperationException(string.Join("\\n", errors.Select(e => e.Message)));`,
        `        return result!.Data!;`,
        `    }`,
        `}`,
        "",
        `public sealed record GraphQLRequest(`,
        `    [property: JsonPropertyName("query")] string Query,`,
        `    [property: JsonPropertyName("variables"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Variables,`,
        `    [property: JsonPropertyName("operationName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OperationName);`,
        "",
        `public sealed class GraphQLResponse<T> {`,
        `    [JsonPropertyName("data")] public T? Data { get; set; }`,
        `    [JsonPropertyName("errors")] public List<GraphQLError>? Errors { get; set; }`,
        `}`,
        "",
        `public sealed class GraphQLError {`,
        `    [JsonPropertyName("message")] public string Message { get; set; } = "";`,
        `}`,
      ];
      return lines.join("\n");
    },
    join: (model, connect) => {
      // top-level statements come first in a C# file, the types after them
      const [usage, rest] = splitAt(connect, "\n\npublic static class ");
      return [header0("//"), "using System.Net.Http.Json;\nusing System.Text.Json;\nusing System.Text.Json.Serialization;", usage, model, rest].filter(Boolean).join("\n\n") + "\n";
    },
  },

  python: {
    comment: (t) => "# " + t + "\n",
    model(model) {
      const out: string[] = [];
      for (const e of model.enums) out.push(`${e.name} = Literal[${e.values.map((v) => JSON.stringify(v)).join(", ") || '""'}]`);
      for (const t of model.types) {
        const total = t.fields.every((f) => !f.optional);
        const classSyntax = t.fields.every((f) => /^[A-Za-z_]\w*$/.test(f.key) && !pythonKeywords.has(f.key));
        const doc = t.description ? `    """${oneLine(t.description).replace(/"""/g, "'''")}"""\n` : "";
        if (classSyntax) {
          const body = t.fields.map((f) => `    ${f.key}: ${pyType(f.ref)}`).join("\n");
          out.push(`class ${t.name}(TypedDict${total ? "" : ", total=False"}):\n${doc}${body || "    pass"}`);
        } else {
          const entries = t.fields.map((f) => `    ${JSON.stringify(f.key)}: ${JSON.stringify(pyType(f.ref))},`).join("\n");
          out.push(`${t.name} = TypedDict(${JSON.stringify(t.name)}, {\n${entries}\n}${total ? "" : ", total=False"})`);
        }
      }
      return out.join("\n\n\n");
    },
    connect(r) {
      const fn = "run_" + snake(r.base);
      const constant = snake(r.base).toUpperCase() + "_QUERY";
      const typed = r.dataType !== null;
      const lines = [
        `ENDPOINT = ${JSON.stringify(r.endpoint)}`,
        ...(r.apiKey ? [`# ${apiKeyNote}`, `API_KEY = os.environ.get("GRAPHQL_API_KEY", "")`] : []),
        "",
        `${constant} = """`,
        r.document.replace(/\\/g, "\\\\").replace(/"""/g, '\\"\\"\\"'),
        `"""`,
        "",
        "",
        `def graphql(query: str, variables: Optional[Dict[str, Any]] = None, operation_name: Optional[str] = None) -> Any:`,
        `    body = json.dumps({"query": query, "variables": variables, "operationName": operation_name}).encode("utf-8")`,
        `    headers = {"Content-Type": "application/json"${r.apiKey ? `, "X-Api-Key": API_KEY` : ""}}`,
        `    request = urllib.request.Request(ENDPOINT, data=body, headers=headers, method="POST")`,
        `    with urllib.request.urlopen(request) as response:`,
        `        result = json.load(response)`,
        `    if result.get("errors"):`,
        `        raise RuntimeError("\\n".join(error["message"] for error in result["errors"]))`,
        `    return result["data"]`,
        ...(typed
          ? [
              "",
              "",
              `def ${fn}(${r.variablesType ? `variables: ${r.variablesType}` : ""}) -> ${r.dataType}:`,
              `    return graphql(${constant}${r.variablesType ? ", variables" : ""}${r.operationName ? (r.variablesType ? "" : ", None") + ", " + JSON.stringify(r.operationName) : ""})`,
            ]
          : []),
        "",
        "",
        `if __name__ == "__main__":`,
        typed
          ? `    data = ${fn}(${r.variablesType ? pyLiteral(r.variables ?? {}, "    ") : ""})`
          : `    data = graphql(${constant}${r.variables ? ", " + pyLiteral(r.variables, "    ") : r.operationName ? ", None" : ""}${r.operationName ? ", " + JSON.stringify(r.operationName) : ""})`,
        `    print(json.dumps(data, indent=2))`,
      ];
      return lines.join("\n");
    },
    join: (model, connect) =>
      [
        '"""' + header0("").trim().replace(/^ /, "") + '"""',
        "from __future__ import annotations\n\nimport json\nimport os\nimport urllib.request\nfrom typing import Any, Dict, List, Literal, Optional, TypedDict",
        model,
        connect,
      ]
        .filter(Boolean)
        .join("\n\n\n") + "\n",
  },

  java: {
    comment: (t) => "// " + t + "\n",
    model(model) {
      const out: string[] = [];
      for (const e of model.enums) out.push(`    public enum ${e.name} { ${e.values.map(javaIdentifier).join(", ")} }`);
      for (const t of model.types) {
        const components = t.fields.map((f) => {
          const name = javaIdentifier(f.key);
          const annotation = name !== f.key ? `@JsonProperty(${JSON.stringify(f.key)}) ` : "";
          return `${annotation}${javaType(f.ref, f.optional)} ${name}`;
        });
        const inline = components.join(", ");
        const body = inline.length > 90 ? "\n        " + components.join(",\n        ") + "\n    " : inline;
        out.push(
          `${t.description ? `    /** ${oneLine(t.description)} */\n` : ""}${t.input ? "    @JsonInclude(JsonInclude.Include.NON_NULL)\n" : ""}    public record ${t.name}(${body}) {}`,
        );
      }
      return out.join("\n\n");
    },
    connect(r, model) {
      const typed = r.dataType !== null;
      const lines = [
        `    static final String ENDPOINT = ${JSON.stringify(r.endpoint)};`,
        `    static final String QUERY = """`,
        ...r.document
          .replace(/\\/g, "\\\\")
          .replace(/"""/g, '\\"""')
          .split("\n")
          .map((l) => (l ? "        " + l : "")),
        `        """;`,
        `    static final ObjectMapper JSON = new ObjectMapper().configure(DeserializationFeature.FAIL_ON_UNKNOWN_PROPERTIES, false);`,
        "",
        `    public static JsonNode graphql(String query, Object variables, String operationName) throws Exception {`,
        `        var body = new HashMap<String, Object>();`,
        `        body.put("query", query);`,
        `        if (variables != null) body.put("variables", variables);`,
        `        if (operationName != null) body.put("operationName", operationName);`,
        `        var request = HttpRequest.newBuilder(URI.create(ENDPOINT))`,
        `            .header("Content-Type", "application/json")`,
        ...(r.apiKey ? [`            // ${apiKeyNote}`, `            .header("X-Api-Key", System.getenv().getOrDefault("GRAPHQL_API_KEY", ""))`] : []),
        `            .POST(HttpRequest.BodyPublishers.ofString(JSON.writeValueAsString(body)))`,
        `            .build();`,
        `        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());`,
        `        JsonNode result = JSON.readTree(response.body());`,
        `        if (result.hasNonNull("errors")) throw new IllegalStateException(result.get("errors").toString());`,
        `        return result.get("data");`,
        `    }`,
        ...(typed
          ? [
              "",
              `    public static ${r.dataType} run(${r.variablesType ? `${r.variablesType} variables` : ""}) throws Exception {`,
              `        return JSON.treeToValue(graphql(QUERY, ${r.variablesType ? "variables" : "null"}, ${r.operationName ? JSON.stringify(r.operationName) : "null"}), ${r.dataType}.class);`,
              `    }`,
            ]
          : []),
        "",
        `    public static void main(String[] args) throws Exception {`,
        typed
          ? `        var data = run(${r.variablesType ? javaLiteral({ k: "input", name: r.variablesType, nullable: false }, r.variables ?? {}, model) : ""});`
          : `        var data = graphql(QUERY, ${r.variables ? `JSON.readTree(${JSON.stringify(JSON.stringify(r.variables))})` : "null"}, ${r.operationName ? JSON.stringify(r.operationName) : "null"});`,
        `        System.out.println(JSON.writerWithDefaultPrettyPrinter().writeValueAsString(data));`,
        `    }`,
      ];
      return lines.join("\n");
    },
    join: (model, connect) =>
      [
        header0("//") + "\n// Needs Java 17 or later and Jackson (com.fasterxml.jackson.core:jackson-databind 2.12 or later).",
        [
          "import com.fasterxml.jackson.annotation.JsonInclude;",
          "import com.fasterxml.jackson.annotation.JsonProperty;",
          "import com.fasterxml.jackson.databind.DeserializationFeature;",
          "import com.fasterxml.jackson.databind.JsonNode;",
          "import com.fasterxml.jackson.databind.ObjectMapper;",
          "import java.math.BigDecimal;",
          "import java.net.URI;",
          "import java.net.http.HttpClient;",
          "import java.net.http.HttpRequest;",
          "import java.net.http.HttpResponse;",
          "import java.util.HashMap;",
          "import java.util.List;",
        ].join("\n"),
        `public class GraphQLExample {\n${[model, connect].filter(Boolean).join("\n\n")}\n}`,
      ].join("\n\n") + "\n",
  },

  go: {
    comment: (t) => "// " + t + "\n",
    model(model) {
      const out: string[] = [];
      for (const e of model.enums) {
        const consts = e.values.map((v) => `\t${e.name}${pascal(v)} ${e.name} = ${JSON.stringify(v)}`);
        out.push(`type ${e.name} string${consts.length ? `\n\nconst (\n${alignGo(consts)}\n)` : ""}`);
      }
      for (const t of model.types) {
        const used = new Set<string>();
        const rows = t.fields.map((f) => {
          let name = pascal(f.key);
          while (used.has(name)) name += "_";
          used.add(name);
          const tag = `\`json:"${f.key}${t.input && (f.optional || f.ref.nullable) ? ",omitempty" : ""}"\``;
          return `\t${name} ${goType(f.ref, f.optional)} ${tag}`;
        });
        out.push(`${t.description ? `// ${t.name}: ${oneLine(t.description)}\n` : ""}type ${t.name} struct {\n${alignGo(rows)}\n}`);
      }
      return out.join("\n\n");
    },
    connect(r, model) {
      const fn = "Run" + r.base;
      const typed = r.dataType !== null;
      const raw = r.document.includes("`") ? JSON.stringify(r.document) : "`\n" + r.document + "\n`";
      const lines = [
        `const endpoint = ${JSON.stringify(r.endpoint)}`,
        "",
        `const ${camel(r.base)}Query = ${raw}`,
        "",
        `func graphql(query string, variables any, operationName string, data any) error {`,
        `\tpayload := map[string]any{"query": query, "variables": variables}`,
        `\tif operationName != "" {`,
        `\t\tpayload["operationName"] = operationName`,
        `\t}`,
        `\tbody, err := json.Marshal(payload)`,
        `\tif err != nil {`,
        `\t\treturn err`,
        `\t}`,
        `\trequest, err := http.NewRequest(http.MethodPost, endpoint, bytes.NewReader(body))`,
        `\tif err != nil {`,
        `\t\treturn err`,
        `\t}`,
        `\trequest.Header.Set("Content-Type", "application/json")`,
        ...(r.apiKey ? [`\t// ${apiKeyNote}`, `\trequest.Header.Set("X-Api-Key", os.Getenv("GRAPHQL_API_KEY"))`] : []),
        `\tresponse, err := http.DefaultClient.Do(request)`,
        `\tif err != nil {`,
        `\t\treturn err`,
        `\t}`,
        `\tdefer response.Body.Close()`,
        `\tvar result struct {`,
        `\t\tData   json.RawMessage \`json:"data"\``,
        `\t\tErrors []struct {`,
        `\t\t\tMessage string \`json:"message"\``,
        `\t\t} \`json:"errors"\``,
        `\t}`,
        `\tif err := json.NewDecoder(response.Body).Decode(&result); err != nil {`,
        `\t\treturn fmt.Errorf("%s: %w", response.Status, err)`,
        `\t}`,
        `\tif len(result.Errors) > 0 {`,
        `\t\treturn errors.New(result.Errors[0].Message)`,
        `\t}`,
        `\treturn json.Unmarshal(result.Data, data)`,
        `}`,
        ...(typed
          ? [
              "",
              `func ${fn}(${r.variablesType ? `variables ${r.variablesType}` : ""}) (*${r.dataType}, error) {`,
              `\tvar data ${r.dataType}`,
              `\tif err := graphql(${camel(r.base)}Query, ${r.variablesType ? "variables" : "nil"}, ${JSON.stringify(r.operationName ?? "")}, &data); err != nil {`,
              `\t\treturn nil, err`,
              `\t}`,
              `\treturn &data, nil`,
              `}`,
            ]
          : []),
        "",
        `func ptr[T any](v T) *T { return &v }`,
        "",
        `func main() {`,
        ...(typed
          ? [`\tdata, err := ${fn}(${r.variablesType ? goLiteral({ k: "input", name: r.variablesType, nullable: false }, r.variables ?? {}, model, "\t") : ""})`]
          : [
              `\tvar data map[string]any`,
              `\terr := graphql(${camel(r.base)}Query, ${r.variables ? `json.RawMessage(${JSON.stringify(JSON.stringify(r.variables))})` : "nil"}, ${JSON.stringify(r.operationName ?? "")}, &data)`,
            ]),
        `\tif err != nil {`,
        `\t\tfmt.Fprintln(os.Stderr, err)`,
        `\t\tos.Exit(1)`,
        `\t}`,
        `\tout, _ := json.MarshalIndent(data, "", "  ")`,
        `\tfmt.Println(string(out))`,
        `}`,
      ];
      return lines.join("\n");
    },
    join: (model, connect) =>
      [header0("//"), "package main", 'import (\n\t"bytes"\n\t"encoding/json"\n\t"errors"\n\t"fmt"\n\t"net/http"\n\t"os"\n)', model, connect].filter(Boolean).join("\n\n") + "\n",
  },

  curl: {
    comment: (t) => "# " + t + "\n",
    model: () => "",
    connect(r) {
      const body = JSON.stringify(
        { query: r.document, ...(r.variables ? { variables: r.variables } : {}), ...(r.operationName ? { operationName: r.operationName } : {}) },
        null,
        2,
      );
      return [
        ...(r.apiKey ? [`# ${apiKeyNote}`] : []),
        `curl ${shellQuote(r.endpoint)} \\`,
        `  -H "Content-Type: application/json" \\`,
        ...(r.apiKey ? [`  -H "X-Api-Key: $GRAPHQL_API_KEY" \\`] : []),
        `  --data-binary @- <<'JSON'`,
        body,
        `JSON`,
      ].join("\n");
    },
    join: (_model, connect) => header0("#") + "\n" + connect + "\n",
  },

  powershell: {
    comment: (t) => "# " + t + "\n",
    model: () => "",
    connect(r) {
      return [
        // JavaScriptSerializer, which Windows PowerShell reads json with, takes a "__type" key for a type name
        ...(/\b__type\s*\(/.test(r.document) ? ["# Needs PowerShell 7: Windows PowerShell 5.1 cannot read an answer holding the key __type."] : []),
        `$query = @'`,
        r.document,
        `'@`,
        `$body = @{`,
        `  query = $query`,
        ...(r.variables ? [`  variables = '${JSON.stringify(r.variables).replace(/'/g, "''")}' | ConvertFrom-Json`] : []),
        ...(r.operationName ? [`  operationName = '${r.operationName}'`] : []),
        `} | ConvertTo-Json -Depth 32`,
        ...(r.apiKey ? [`# ${apiKeyNote}`] : []),
        `$result = Invoke-RestMethod -Method Post -Uri '${r.endpoint.replace(/'/g, "''")}' -ContentType 'application/json'${r.apiKey ? ` -Headers @{ 'X-Api-Key' = $env:GRAPHQL_API_KEY }` : ""} -Body $body`,
        `if ($result.errors) { throw ($result.errors | ForEach-Object { $_.message }) -join "\`n" }`,
        `$result.data | ConvertTo-Json -Depth 32`,
      ].join("\n");
    },
    join: (_model, connect) => header0("#") + "\n" + connect + "\n",
  },
};

let currentHeader = "";
function header0(lineComment: string): string {
  return currentHeader
    ? currentHeader
        .split("\n")
        .map((l) => (lineComment ? lineComment + " " + l : l))
        .join("\n")
    : "";
}

/** generateCode with a first comment saying what the file is. */
export function generateFile(language: CodeLanguage, scope: CodeScope, part: CodePart, input: CodeInput): string {
  const op = input.op;
  const operation = op.name ? "the " + op.name + " operation" : "this query";
  const what =
    language === "curl" || language === "powershell"
      ? `Runs ${operation}`
      : scope === "schema"
        ? "Model types for the whole schema, and a client"
        : `Model types for ${operation} and the code to run it`;
  currentHeader = `${what}, against ${input.endpoint}.\nWritten by the Relatude.DB GraphQL explorer.`;
  try {
    return generateCode(language, scope, part, input);
  } finally {
    currentHeader = "";
  }
}

// ---- per language ----

function oneLine(s: string): string {
  return s.replace(/\s+/g, " ").trim();
}

function xmlEscape(s: string): string {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

function splitAt(text: string, marker: string): [string, string] {
  const i = text.indexOf(marker);
  return i < 0 ? [text, ""] : [text.slice(0, i), text.slice(i + 2)];
}

function snake(name: string): string {
  return name.replace(/([a-z0-9])([A-Z])/g, "$1_$2").toLowerCase();
}

function templateLiteral(text: string): string {
  return "`\n" + text.replace(/\\/g, "\\\\").replace(/`/g, "\\`").replace(/\$\{/g, "\\${") + "\n`";
}

function shellQuote(s: string): string {
  return "'" + s.replace(/'/g, "'\\''") + "'";
}

/** A JavaScript object literal for a json value: keys unquoted where they can be, on one line when it is short. */
function jsonLiteral(value: unknown, indent: string): string {
  if (value === null || typeof value !== "object") return JSON.stringify(value);
  const inner = indent + "  ";
  const parts = Array.isArray(value)
    ? value.map((v) => jsonLiteral(v, inner))
    : Object.entries(value as Record<string, unknown>).map(([k, v]) => `${/^[A-Za-z_$][\w$]*$/.test(k) ? k : JSON.stringify(k)}: ${jsonLiteral(v, inner)}`);
  if (parts.length === 0) return Array.isArray(value) ? "[]" : "{}";
  const [open, close] = Array.isArray(value) ? ["[", "]"] : ["{ ", " }"];
  const line = open + parts.join(", ") + close;
  if (line.length <= 80 && !line.includes("\n")) return line;
  return open.trim() + "\n" + parts.map((p) => inner + p).join(",\n") + ",\n" + indent + close.trim();
}

const tsScalars: Record<string, string> = {
  Int: "number",
  Float: "number",
  Long: "number",
  Decimal: "number",
  String: "string",
  ID: "string",
  DateTime: "string",
  Boolean: "boolean",
  JSON: "unknown",
};

function tsKey(key: string): string {
  return /^[A-Za-z_$][\w$]*$/.test(key) ? key : JSON.stringify(key);
}

function tsType(ref: Ref): string {
  const inner = ref.k === "list" ? `${wrapUnion(tsType(ref.of))}[]` : ref.k === "scalar" ? (tsScalars[ref.name] ?? "unknown") : ref.name;
  return ref.nullable ? inner + " | null" : inner;
}

function wrapUnion(s: string): string {
  return s.includes("|") ? `(${s})` : s;
}

function jsType(ref: Ref): string {
  const inner = ref.k === "list" ? `${wrapUnion(jsType(ref.of))}[]` : ref.k === "scalar" ? (tsScalars[ref.name] ?? "*") : ref.name;
  return ref.nullable ? `(${inner}|null)` : inner;
}

const csScalars: Record<string, [string, boolean]> = {
  Int: ["int", true],
  Float: ["double", true],
  Long: ["long", true],
  Decimal: ["decimal", true],
  String: ["string", false],
  ID: ["string", false],
  DateTime: ["DateTime", true],
  Boolean: ["bool", true],
  JSON: ["JsonElement", true],
};

function csType(ref: Ref, optional: boolean): string {
  const nullable = ref.nullable || optional;
  if (ref.k === "list") return `List<${csType(ref.of, false)}>${nullable ? "?" : ""}`;
  if (ref.k === "scalar") {
    const [name] = csScalars[ref.name] ?? ["JsonElement", true];
    return name + (nullable ? "?" : "");
  }
  return ref.name + (nullable ? "?" : "");
}

function csInitializer(ref: Ref, optional: boolean): string {
  if (ref.nullable || optional) return "";
  if (ref.k === "list") return " = [];";
  if (ref.k === "scalar") {
    const [name, valueType] = csScalars[ref.name] ?? ["JsonElement", true];
    if (valueType) return "";
    return name === "string" ? ' = "";' : "";
  }
  if (ref.k === "enum") return "";
  return " = default!;";
}

const csKeywords = new Set([
  "abstract",
  "as",
  "base",
  "bool",
  "break",
  "byte",
  "case",
  "catch",
  "char",
  "checked",
  "class",
  "const",
  "continue",
  "decimal",
  "default",
  "delegate",
  "do",
  "double",
  "else",
  "enum",
  "event",
  "explicit",
  "extern",
  "false",
  "finally",
  "fixed",
  "float",
  "for",
  "foreach",
  "goto",
  "if",
  "implicit",
  "in",
  "int",
  "interface",
  "internal",
  "is",
  "lock",
  "long",
  "namespace",
  "new",
  "null",
  "object",
  "operator",
  "out",
  "override",
  "params",
  "private",
  "protected",
  "public",
  "readonly",
  "ref",
  "return",
  "sbyte",
  "sealed",
  "short",
  "sizeof",
  "stackalloc",
  "static",
  "string",
  "struct",
  "switch",
  "this",
  "throw",
  "true",
  "try",
  "typeof",
  "uint",
  "ulong",
  "unchecked",
  "unsafe",
  "ushort",
  "using",
  "virtual",
  "void",
  "volatile",
  "while",
]);

function csIdentifier(name: string): string {
  return csKeywords.has(name) ? "@" + name : name;
}

function csProperty(key: string, typeName: string): string {
  const p = pascal(key);
  return p === typeName ? p + "Value" : p;
}

function csString(s: string): string {
  return JSON.stringify(s);
}

function findType(model: Model, name: string): MType | undefined {
  return model.types.find((t) => t.name === name);
}

/** A C# expression for an example value, typed to match the model. */
function csLiteral(ref: Ref, value: unknown, model: Model, indent: string): string {
  if (value === null || value === undefined) return "null";
  if (ref.k === "list") return "[" + (Array.isArray(value) ? value.map((v) => csLiteral(ref.of, v, model, indent)).join(", ") : csLiteral(ref.of, value, model, indent)) + "]";
  if (ref.k === "enum") return `${ref.name}.${csIdentifier(String(value))}`;
  if (ref.k === "input" || ref.k === "object") {
    const t = findType(model, ref.name);
    if (!t || typeof value !== "object") return "null";
    const entries = t.fields
      .filter((f) => f.key in (value as object))
      .map((f) => `${csProperty(f.key, t.name)} = ${csLiteral(f.ref, (value as Record<string, unknown>)[f.key], model, indent + "    ")}`);
    if (entries.length === 0) return `new ${t.name}()`;
    const inline = `new ${t.name} { ${entries.join(", ")} }`;
    return inline.length <= 100 ? inline : `new ${t.name} {\n${indent}    ${entries.join(",\n" + indent + "    ")},\n${indent}}`;
  }
  switch (ref.name) {
    case "Long":
      return `${Number(value)}L`;
    case "Decimal":
      return `${Number(value)}m`;
    case "Float":
      return Number.isInteger(value) ? `${value}.0` : String(value);
    case "Int":
      return String(Number(value));
    case "Boolean":
      return value ? "true" : "false";
    case "DateTime":
      return `DateTime.Parse(${csString(String(value))}, null, System.Globalization.DateTimeStyles.RoundtripKind)`;
    default:
      return csString(String(value));
  }
}

const pyScalars: Record<string, string> = { Int: "int", Float: "float", Long: "int", Decimal: "float", String: "str", ID: "str", DateTime: "str", Boolean: "bool", JSON: "Any" };

function pyType(ref: Ref): string {
  const inner = ref.k === "list" ? `List[${pyType(ref.of)}]` : ref.k === "scalar" ? (pyScalars[ref.name] ?? "Any") : ref.name;
  return ref.nullable ? `Optional[${inner}]` : inner;
}

const pythonKeywords = new Set([
  "False",
  "None",
  "True",
  "and",
  "as",
  "assert",
  "async",
  "await",
  "break",
  "class",
  "continue",
  "def",
  "del",
  "elif",
  "else",
  "except",
  "finally",
  "for",
  "from",
  "global",
  "if",
  "import",
  "in",
  "is",
  "lambda",
  "nonlocal",
  "not",
  "or",
  "pass",
  "raise",
  "return",
  "try",
  "while",
  "with",
  "yield",
]);

function pyLiteral(value: unknown, indent: string): string {
  if (value === null || value === undefined) return "None";
  if (value === true) return "True";
  if (value === false) return "False";
  if (typeof value === "number") return String(value);
  if (typeof value === "string") return JSON.stringify(value);
  if (Array.isArray(value)) return "[" + value.map((v) => pyLiteral(v, indent)).join(", ") + "]";
  const entries = Object.entries(value as Record<string, unknown>).map(([k, v]) => `${JSON.stringify(k)}: ${pyLiteral(v, indent + "    ")}`);
  const inline = "{" + entries.join(", ") + "}";
  return inline.length <= 90 ? inline : "{\n" + indent + "    " + entries.join(",\n" + indent + "    ") + ",\n" + indent + "}";
}

const javaKeywords = new Set([
  "abstract",
  "assert",
  "boolean",
  "break",
  "byte",
  "case",
  "catch",
  "char",
  "class",
  "const",
  "continue",
  "default",
  "do",
  "double",
  "else",
  "enum",
  "extends",
  "final",
  "finally",
  "float",
  "for",
  "goto",
  "if",
  "implements",
  "import",
  "instanceof",
  "int",
  "interface",
  "long",
  "native",
  "new",
  "package",
  "private",
  "protected",
  "public",
  "return",
  "short",
  "static",
  "strictfp",
  "super",
  "switch",
  "synchronized",
  "this",
  "throw",
  "throws",
  "transient",
  "try",
  "void",
  "volatile",
  "while",
  "true",
  "false",
  "null",
  "record",
  "var",
  "yield",
]);

function javaIdentifier(name: string): string {
  return javaKeywords.has(name) ? name + "_" : name;
}

const javaScalars: Record<string, [string, string]> = {
  Int: ["int", "Integer"],
  Float: ["double", "Double"],
  Long: ["long", "Long"],
  Decimal: ["BigDecimal", "BigDecimal"],
  String: ["String", "String"],
  ID: ["String", "String"],
  DateTime: ["String", "String"],
  Boolean: ["boolean", "Boolean"],
  JSON: ["JsonNode", "JsonNode"],
};

function javaType(ref: Ref, optional: boolean, boxed = false): string {
  if (ref.k === "list") return `List<${javaType(ref.of, false, true)}>`;
  if (ref.k === "scalar") {
    const [primitive, box] = javaScalars[ref.name] ?? ["JsonNode", "JsonNode"];
    return ref.nullable || optional || boxed ? box : primitive;
  }
  return ref.name;
}

function javaLiteral(ref: Ref, value: unknown, model: Model): string {
  if (value === null || value === undefined) return "null";
  if (ref.k === "list") return "List.of(" + (Array.isArray(value) ? value : [value]).map((v) => javaLiteral(ref.of, v, model)).join(", ") + ")";
  if (ref.k === "enum") return `${ref.name}.${javaIdentifier(String(value))}`;
  if (ref.k === "input" || ref.k === "object") {
    const t = findType(model, ref.name);
    if (!t || typeof value !== "object") return "null";
    return `new ${t.name}(${t.fields.map((f) => javaLiteral(f.ref, (value as Record<string, unknown>)[f.key], model)).join(", ")})`;
  }
  switch (ref.name) {
    case "Long":
      return `${Number(value)}L`;
    case "Decimal":
      return `new BigDecimal(${JSON.stringify(String(value))})`;
    case "Float":
      return Number.isInteger(value) ? `${value}.0` : String(value);
    case "Int":
      return String(Number(value));
    case "Boolean":
      return value ? "true" : "false";
    default:
      return JSON.stringify(String(value));
  }
}

const goScalars: Record<string, string> = {
  Int: "int",
  Float: "float64",
  Long: "int64",
  Decimal: "float64",
  String: "string",
  ID: "string",
  DateTime: "string",
  Boolean: "bool",
  JSON: "json.RawMessage",
};

function goType(ref: Ref, optional: boolean): string {
  if (ref.k === "list") return `[]${goType(ref.of, false)}`;
  const name = ref.k === "scalar" ? (goScalars[ref.name] ?? "json.RawMessage") : ref.name;
  return ref.nullable || optional ? "*" + name : name;
}

function goLiteral(ref: Ref, value: unknown, model: Model, indent: string, pointer = false): string {
  if (value === null || value === undefined) return "nil";
  const nullable = pointer || ref.nullable;
  if (ref.k === "list") {
    const items = (Array.isArray(value) ? value : [value]).map((v) => goLiteral(ref.of, v, model, indent, false));
    return `${goType({ ...ref, nullable: false }, false)}{${items.join(", ")}}`;
  }
  if (ref.k === "input" || ref.k === "object") {
    const t = findType(model, ref.name);
    if (!t || typeof value !== "object") return "nil";
    const entries = t.fields
      .filter((f) => f.key in (value as object))
      .map((f) => `${pascal(f.key)}: ${goLiteral(f.ref, (value as Record<string, unknown>)[f.key], model, indent + "\t", f.optional)}`);
    return `${nullable ? "&" : ""}${t.name}{${entries.join(", ")}}`;
  }
  let lit: string;
  if (ref.k === "enum") lit = `${ref.name}${pascal(String(value))}`;
  else {
    switch (ref.name) {
      case "Int":
        lit = String(Number(value));
        break;
      case "Long":
        lit = `int64(${Number(value)})`;
        break;
      case "Float":
      case "Decimal":
        lit = `float64(${Number(value)})`;
        break;
      case "Boolean":
        lit = value ? "true" : "false";
        break;
      default:
        lit = JSON.stringify(String(value));
    }
  }
  return nullable ? `ptr(${lit})` : lit;
}

/** gofmt lines up the names, types and tags of a block in columns. */
function alignGo(rows: string[]): string {
  const cells = rows.map((r) => r.replace(/^\t/, "").split(" "));
  const split = cells.map((c) => (c.length >= 3 && c[c.length - 1].startsWith("`") ? [c[0], c.slice(1, -1).join(" "), c[c.length - 1]] : [c[0], c.slice(1).join(" ")]));
  const w0 = Math.max(0, ...split.map((c) => c[0].length));
  const w1 = Math.max(0, ...split.filter((c) => c.length === 3).map((c) => c[1].length));
  return split.map((c) => "\t" + c[0].padEnd(w0) + " " + (c.length === 3 ? c[1].padEnd(w1) + " " + c[2] : c[1])).join("\n");
}
