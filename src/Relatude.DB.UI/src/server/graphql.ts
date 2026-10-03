// The GraphQL endpoints of one database (UIGraphQL.cs on the server). An endpoint is a json file in the
// database's graphql folder: the url it answers on, which node types and properties it exposes under which
// names (by their datamodel ids, so renames in the model do not break it), views defined by a query, and
// whether it takes mutations. The server builds the schema from the file and the datamodel whenever either
// changes; the page previews that schema, as SDL and as TypeScript, while the definition is edited.

import type { SchemaInfo } from "../graphql/schema";
import { send } from "./channel";

export type EndpointMode = "Selected" | "WholeDatamodel";

export interface EndpointPropertyDef {
  propertyId: string;
  name?: string | null;
}

export interface EndpointTypeDef {
  nodeTypeId: string;
  name?: string | null;
  singleName?: string | null;
  listName?: string | null;
  readOnly?: boolean;
  /** null (or missing) exposes every property of the type */
  properties?: EndpointPropertyDef[] | null;
}

export interface EndpointViewDef {
  name: string;
  query: string;
  description?: string | null;
}

export interface EndpointDefinition {
  id?: string;
  name: string;
  description?: string | null;
  url: string;
  enabled: boolean;
  mode: EndpointMode;
  exactNames: boolean;
  allowMutations: boolean;
  enableIntrospection: boolean;
  enableGetRequests: boolean;
  /** a browser asking for the url gets the explorer page */
  enableExplorer?: boolean;
  /** a facet search over the exposed types is served beside the schema, and the page opens on it as a visual pivot */
  enableFacetSearch?: boolean;
  /** how many nodes the facet search's picture may hold */
  maxFacetCards?: number;
  includeSystemTypes: boolean;
  apiKey?: string | null;
  maxQueryDepth: number;
  maxIncludeDepth: number;
  defaultPageSize: number;
  maxPageSize: number;
  types: EndpointTypeDef[];
  views: EndpointViewDef[];
}

export interface EndpointSummary {
  id: string | null;
  name: string;
  file: string;
  url: string | null;
  enabled: boolean;
  mode: EndpointMode;
  exactNames: boolean;
  allowMutations: boolean;
  /** the explorer page is served on the url */
  explorer: boolean;
  /** the facet search is served, and the page on the url opens on it */
  facets: boolean;
  /** introspection is on: the schema is told, as __schema and as ?sdl */
  introspection: boolean;
  typeCount: number;
  viewCount: number;
  /** set when the file could not be read; the other fields are then placeholders */
  error: string | null;
}

export interface CatalogProperty {
  id: string;
  name: string;
  kind: string;
  inherited: boolean;
  target: string | null;
  /** false for property types GraphQL cannot carry (binary, embedded...) */
  supported: boolean;
  writable: boolean;
}

export interface CatalogType {
  id: string;
  name: string;
  fullName: string;
  isInterface: boolean;
  isSystem: boolean;
  parents: string[];
  properties: CatalogProperty[];
}

export interface EndpointsInfo {
  open: boolean;
  state: string;
  adminRoot: string;
  endpoints: EndpointSummary[];
  catalog: { types: CatalogType[] } | null;
}

export interface EndpointIssue {
  severity: "error" | "warning";
  message: string;
  isError: boolean;
}

export interface EndpointPreview {
  issues: EndpointIssue[];
  warnings: string[];
  sdl: string | null;
  types: string | null;
  sample: string | null;
  csharpTypes: string | null;
  csharpSample: string | null;
  sampleQuery: string | null;
  typeCount: number;
  mutationCount: number;
}

export interface EndpointExample {
  id: string;
  title: string;
  query: string;
  /** variables as json text, null when the query takes none */
  variables: string | null;
}

export interface EndpointExampleGroup {
  /** the GraphQL type the examples are about; empty for views and introspection */
  type: string;
  label: string;
  examples: EndpointExample[];
}

export interface EndpointLoad {
  definition: EndpointDefinition;
  file: string;
  preview: EndpointPreview;
}

export function fetchEndpoints(storeId: string): Promise<EndpointsInfo> {
  return send<EndpointsInfo>("graphql-endpoints", { storeId });
}

export function reloadEndpoints(storeId: string): Promise<EndpointsInfo> {
  return send<EndpointsInfo>("graphql-reload", { storeId });
}

export function fetchEndpoint(storeId: string, id: string): Promise<EndpointLoad> {
  return send<EndpointLoad>("graphql-endpoint", { storeId, id });
}

export function previewEndpoint(storeId: string, definition: EndpointDefinition, signal?: AbortSignal): Promise<EndpointPreview> {
  return send<EndpointPreview>("graphql-preview", { storeId, definition }, signal);
}

export function saveEndpoint(storeId: string, definition: EndpointDefinition): Promise<{ id: string; file: string }> {
  return send<{ id: string; file: string }>("graphql-save", { storeId, definition });
}

export function deleteEndpoint(storeId: string, id: string): Promise<{ deleted: boolean }> {
  return send<{ deleted: boolean }>("graphql-delete", { storeId, id });
}

export function fetchExamples(storeId: string, definition: EndpointDefinition, signal?: AbortSignal): Promise<{ groups: EndpointExampleGroup[] }> {
  return send<{ groups: EndpointExampleGroup[] }>("graphql-examples", { storeId, definition }, signal);
}

export interface GuideExample {
  /** the guide topic, such as "filter" or "fragments" */
  id: string;
  query: string;
  variables: string | null;
  /** what the example uses on this endpoint, and where its values came from */
  note: string | null;
  isMutation: boolean;
}

export interface ExplorerData {
  schema: SchemaInfo;
  /** one stored node per type (by GraphQL type name), so ids in the builder are real */
  samples: Record<string, { id: string; name: string | null }>;
  guide: { examples: GuideExample[]; unavailable: { id: string; reason: string }[] };
  warnings: string[];
}

/** What the explorer needs for a definition: the schema, sample ids, and the guide's examples. */
export function fetchExplorer(storeId: string, definition: EndpointDefinition, signal?: AbortSignal): Promise<ExplorerData> {
  return send<ExplorerData>("graphql-explorer", { storeId, definition }, signal);
}

export function executeEndpoint(
  storeId: string,
  request: { id?: string; definition?: EndpointDefinition; query: string; variables?: unknown; operationName?: string },
): Promise<{ result: unknown }> {
  return send<{ result: unknown }>("graphql-execute", { storeId, ...request });
}

export function newEndpointDefinition(): EndpointDefinition {
  return {
    name: "",
    url: "/graphql",
    enabled: true,
    mode: "Selected",
    exactNames: false,
    allowMutations: false,
    enableIntrospection: true,
    enableGetRequests: true,
    enableExplorer: false,
    enableFacetSearch: false,
    maxFacetCards: 200_000,
    includeSystemTypes: false,
    apiKey: null,
    maxQueryDepth: 16,
    maxIncludeDepth: 8,
    defaultPageSize: 25,
    maxPageSize: 200,
    types: [],
    views: [],
  };
}

/** Fills in what a hand-written file may leave out, so the form has every field. */
export function normalizeDefinition(d: Partial<EndpointDefinition>): EndpointDefinition {
  const base = newEndpointDefinition();
  return {
    ...base,
    ...d,
    types: (d.types ?? []).map((t) => ({ ...t, properties: t.properties === undefined ? null : t.properties })),
    views: d.views ?? [],
  };
}
