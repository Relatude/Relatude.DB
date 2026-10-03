import { useMemo, useState } from "react";
import { IconArrowLeft, IconPlus, IconSearch } from "@tabler/icons-react";
import { namedType, type FieldInfo, type Schema, type TypeInfo, type TypeRole } from "../graphql/schema";

// The Docs panel: the schema as pages to browse. The first page lists the types by what they are for;
// a type's page gives its fields with their arguments, what it stands for in the datamodel, what it
// implements and where it is used. Every type name is a link, and the search finds types and fields.

export interface DocsPlace {
  type: string | null;
  field?: string;
}

const roleTitles: [TypeRole[], string, string][] = [
  [["root"], "Entry points", "where every query and mutation starts"],
  [["node"], "Node types", "the datamodel's types, as the endpoint shows them"],
  [["result"], "Pages", "what a list returns: items and counts"],
  [["object"], "Other objects", "files, positions and the like"],
  [["filter"], "Filters", "the filter argument of each list"],
  [["operator"], "Operators", "conditions on one property: eq, gt, in…"],
  [["orderBy"], "Orderings", "what each list can be ordered by"],
  [["input"], "Inputs", "what mutations take"],
  [["enum"], "Enums", "properties with a fixed set of values"],
  [["scalar"], "Scalars", "single values"],
];

export function ExplorerDocs({
  schema,
  place,
  onPlace,
  onAddRoot,
}: {
  schema: Schema;
  place: DocsPlace[];
  onPlace: (stack: DocsPlace[]) => void;
  /** ticks a root field in the builder */
  onAddRoot: (kind: "query" | "mutation", field: string) => void;
}) {
  const [search, setSearch] = useState("");
  const current = place[place.length - 1] ?? { type: null };
  const go = (type: string | null, field?: string) => {
    setSearch("");
    onPlace([...place, { type, field }]);
  };
  const back = () => onPlace(place.slice(0, -1));
  const hits = useMemo(() => (search.trim() ? searchSchema(schema, search.trim()) : []), [schema, search]);

  return (
    <div className="gx-docs">
      <div className="gx-docs-search">
        <IconSearch size={14} stroke={1.8} />
        <input className="text-input" placeholder="Search types and fields" value={search} onChange={(e) => setSearch(e.target.value)} />
      </div>
      {search.trim() ? (
        <div className="gx-docs-hits">
          {hits.length === 0 && <div className="muted">Nothing is called that.</div>}
          {hits.map((h) => (
            <button key={h.type + "." + (h.field ?? "")} className="gx-docs-hit" onClick={() => go(h.type, h.field)}>
              <span className="gx-docs-hit-name">
                {h.type}
                {h.field && <span className="muted">.</span>}
                {h.field}
              </span>
              <span className="muted">{h.detail}</span>
            </button>
          ))}
        </div>
      ) : (
        <>
          {place.length > 0 && (
            <div className="gx-docs-nav">
              <button className="icon-button small" onClick={back} title="Back">
                <IconArrowLeft size={14} stroke={1.8} />
              </button>
              <button className="link-button" onClick={() => onPlace([])}>
                Schema
              </button>
              {place.map((p, i) => (
                <span key={i}>
                  {" / "}
                  <button className="link-button" onClick={() => onPlace(place.slice(0, i + 1))}>
                    {p.type ?? "Schema"}
                  </button>
                </span>
              ))}
            </div>
          )}
          {current.type && schema.type(current.type) ? (
            <TypePage schema={schema} type={schema.type(current.type)!} focus={current.field} go={go} onAddRoot={onAddRoot} />
          ) : (
            <SchemaPage schema={schema} go={go} />
          )}
        </>
      )}
    </div>
  );
}

function SchemaPage({ schema, go }: { schema: Schema; go: (type: string) => void }) {
  const all = [...schema.types.values()];
  return (
    <div className="gx-docs-page">
      <p className="gx-docs-text">{schema.description ?? "The schema of this endpoint."}</p>
      {roleTitles.map(([roles, title, hint]) => {
        const types = all.filter((t) => roles.includes(t.role)).sort((a, b) => (a.role === "root" ? (a.name === schema.queryType ? -1 : 1) : a.name.localeCompare(b.name)));
        if (types.length === 0) return null;
        return (
          <div key={title} className="gx-docs-group">
            <div className="gx-docs-group-head">
              {title} <span className="muted">{hint}</span>
            </div>
            <div className="gx-docs-chips">
              {types.map((t) => (
                <button key={t.name} className="gx-type-link" onClick={() => go(t.name)} title={t.description ?? undefined}>
                  {t.name}
                </button>
              ))}
            </div>
          </div>
        );
      })}
    </div>
  );
}

function TypeLink({ typeRef, go }: { typeRef: string; go: (type: string) => void }) {
  const name = namedType(typeRef);
  const [before, after] = typeRef.split(name);
  return (
    <span className="gx-type-ref">
      {before}
      <button className="gx-type-link" onClick={() => go(name)}>
        {name}
      </button>
      {after}
    </span>
  );
}

const kindNames: Record<string, string> = { OBJECT: "object type", INTERFACE: "interface", ENUM: "enum", INPUT_OBJECT: "input", SCALAR: "scalar" };

function TypePage({
  schema,
  type,
  focus,
  go,
  onAddRoot,
}: {
  schema: Schema;
  type: TypeInfo;
  focus?: string;
  go: (type: string, field?: string) => void;
  onAddRoot: (kind: "query" | "mutation", field: string) => void;
}) {
  const usedBy = schema.usedBy(type.name).filter((u) => u.type !== type.name);
  const rootKind = type.name === schema.queryType ? "query" : type.name === schema.mutationType ? "mutation" : null;
  return (
    <div className="gx-docs-page">
      <div className="gx-docs-title">
        {type.name} <span className="gx-badge">{kindNames[type.kind] ?? type.kind}</span>
      </div>
      {type.description && <p className="gx-docs-text">{type.description}</p>}
      {type.nodeType && type.description !== type.nodeType && (
        <p className="gx-docs-text muted">
          The datamodel type <code>{type.nodeType}</code>.
        </p>
      )}
      {(type.interfaces ?? []).length > 0 && (
        <p className="gx-docs-text">
          Implements{" "}
          {type.interfaces!.map((i, n) => (
            <span key={i}>
              {n > 0 && ", "}
              <TypeLink typeRef={i} go={go} />
            </span>
          ))}
          .
        </p>
      )}
      {(type.possibleTypes ?? []).length > 0 && (
        <p className="gx-docs-text">
          Is one of{" "}
          {type.possibleTypes!.map((i, n) => (
            <span key={i}>
              {n > 0 && ", "}
              <TypeLink typeRef={i} go={go} />
            </span>
          ))}
          ; use <code>… on Type</code> for what only one of them has.
        </p>
      )}
      {type.fields && (
        <div className="gx-docs-fields">
          <div className="gx-docs-group-head">Fields</div>
          {schema.fields(type.name).map((f) => (
            <FieldDoc key={f.name} field={f} focus={focus === f.name} go={go} onAdd={rootKind && !f.name.startsWith("__") ? () => onAddRoot(rootKind, f.name) : undefined} />
          ))}
        </div>
      )}
      {type.inputFields && (
        <div className="gx-docs-fields">
          <div className="gx-docs-group-head">Input fields</div>
          {type.inputFields.map((f) => (
            <div key={f.name} className={"gx-docs-field" + (focus === f.name ? " focus" : "")}>
              <div>
                <span className="gx-docs-field-name">{f.name}</span>: <TypeLink typeRef={f.type} go={go} />
              </div>
              {f.description ? (
                <div className="gx-docs-field-text">{f.description}</div>
              ) : (
                f.property && f.property.toLowerCase() !== f.name.toLowerCase() && <div className="gx-docs-field-text muted">The property {f.property}, under another name.</div>
              )}
            </div>
          ))}
        </div>
      )}
      {type.enumValues && (
        <div className="gx-docs-fields">
          <div className="gx-docs-group-head">Values</div>
          <div className="gx-docs-chips">
            {type.enumValues.map((v) => (
              <code key={v} className="gx-enum-value">
                {v}
              </code>
            ))}
          </div>
        </div>
      )}
      {usedBy.length > 0 && (
        <div className="gx-docs-fields">
          <div className="gx-docs-group-head">Used by</div>
          {usedBy.slice(0, 40).map((u, i) => (
            <div key={i} className="gx-docs-used">
              <button className="gx-type-link" onClick={() => go(u.type, u.field)}>
                {u.type}.{u.field}
              </button>
              {u.arg && <span className="muted"> argument {u.arg}</span>}
            </div>
          ))}
          {usedBy.length > 40 && <div className="muted">… and {usedBy.length - 40} more</div>}
        </div>
      )}
    </div>
  );
}

function FieldDoc({ field, focus, go, onAdd }: { field: FieldInfo; focus: boolean; go: (type: string, field?: string) => void; onAdd?: () => void }) {
  return (
    <div className={"gx-docs-field" + (focus ? " focus" : "")} ref={focus ? (el) => el?.scrollIntoView({ block: "nearest" }) : undefined}>
      <div className="gx-docs-field-line">
        <span>
          <span className="gx-docs-field-name">{field.name}</span>
          {field.args.length > 0 && <span className="muted">(…)</span>}: <TypeLink typeRef={field.type} go={go} />
        </span>
        {onAdd && (
          <button className="icon-button small" title="Add to the query" onClick={onAdd}>
            <IconPlus size={13} stroke={1.8} />
          </button>
        )}
      </div>
      {field.description && <div className="gx-docs-field-text">{field.description}</div>}
      {field.property && field.property.toLowerCase() !== field.name.toLowerCase() && (
        <div className="gx-docs-field-text muted">The property {field.property}, under another name.</div>
      )}
      {field.args.length > 0 && (
        <div className="gx-docs-args">
          {field.args.map((a) => (
            <div key={a.name}>
              <span className="gx-docs-arg">{a.name}</span>: <TypeLink typeRef={a.type} go={go} />
              {a.defaultValue !== null && <span className="muted"> = {a.defaultValue}</span>}
              {a.description && <span className="muted"> — {a.description}</span>}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function searchSchema(schema: Schema, text: string): { type: string; field?: string; detail: string }[] {
  const q = text.toLowerCase();
  const types: { type: string; field?: string; detail: string; score: number }[] = [];
  for (const t of schema.types.values()) {
    const name = t.name.toLowerCase();
    if (name.includes(q)) types.push({ type: t.name, detail: kindNames[t.kind] ?? "", score: name === q ? 0 : name.startsWith(q) ? 1 : 2 });
    for (const f of t.fields ?? []) {
      const fn = f.name.toLowerCase();
      if (fn.includes(q)) types.push({ type: t.name, field: f.name, detail: f.type, score: fn === q ? 3 : fn.startsWith(q) ? 4 : 5 });
    }
    for (const f of t.inputFields ?? []) {
      const fn = f.name.toLowerCase();
      if (fn.includes(q) && t.role !== "operator") types.push({ type: t.name, field: f.name, detail: f.type, score: fn === q ? 6 : 7 });
    }
  }
  types.sort((a, b) => a.score - b.score || a.type.localeCompare(b.type));
  return types.slice(0, 80);
}
