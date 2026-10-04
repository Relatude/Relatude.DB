// The facet search of a GraphQL endpoint, as its own page reads it: "?facets=action" requests to the endpoint's url
// (server side: NodeServer/GraphQL/GraphQLFacetSearch.cs). The answers have the shapes of the admin UI's query
// commands, so the same visual pivot draws them - only the types and properties the endpoint exposes take part.

import { readCardImages, type CardImageItem, type CardInfo, type Facet, type FacetSelection, type PivotModel, type VisualRequest, type VisualResult } from "../server/query";
import type { VisualSource } from "../visual/visualSource";

export interface FacetTypeInfo {
  id: string;
  name: string;
  count: number;
}

export interface FacetModel {
  name: string;
  description: string | null;
  /** the types that can be searched, in the order of the endpoint's schema */
  types: FacetTypeInfo[];
  /** the type the page opens on: the one the endpoint's owner picked, else the first */
  defaultTypeId?: string | null;
  /** how many cards the picture may hold */
  maxCards: number;
}

export interface FacetSearchRequest {
  typeId: string | null;
  text: string;
  selections: FacetSelection[];
  /** the facets whose every value is asked for, rather than the first few */
  expanded: string[];
}

export interface FacetSearchResult {
  typeId: string;
  typeName: string;
  total: number;
  /** how many there are before the facet selection narrows them */
  sourceCount: number;
  durationMs: number;
  facets: Facet[];
}

/** One property of a node, as the endpoint shows it: a value as text, or the nodes it leads to by name. */
export interface FacetNodeProperty {
  id: string;
  /** the datamodel's name */
  name: string;
  /** what kind of property it is: String, Integer, Relation (many), Reference... */
  kind: string;
  /** the value as text, "" when it has none; null for a relation or reference, and for a value that could not be read */
  value: string | null;
  /** the first related or referenced nodes the endpoint shows, by name */
  nodes: string[] | null;
  /** how many nodes it leads to in all */
  count: number | null;
}

/** A card's node, for the dialog a click on it opens. */
export interface FacetNode {
  id: string;
  /** the type it is seen as: its own, or the nearest the endpoint exposes */
  type: string;
  name: string;
  createdUtc: string;
  changedUtc: string;
  properties: FacetNodeProperty[];
}

export interface FacetSource extends VisualSource {
  model(signal?: AbortSignal): Promise<FacetModel>;
  search(request: FacetSearchRequest): Promise<FacetSearchResult>;
  /** the node a card was drawn for, by the internal id the picture knows it by */
  node(id: number, signal?: AbortSignal): Promise<FacetNode>;
}

/** A request the endpoint turned down; `status` 401 is a missing or wrong API key. */
export class FacetRequestError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
  }
}

/** The facet search at `url`, sending the API key when there is one; `onUnauthorized` hears of every 401. */
export function createFacetSource(url: string, apiKey: string, onUnauthorized: () => void): FacetSource {
  const headers: Record<string, string> = { "Content-Type": "application/json" };
  if (apiKey) headers["X-Api-Key"] = apiKey;

  async function ask(action: string, body: unknown | undefined, signal?: AbortSignal): Promise<Response> {
    const response = await fetch(url + "?facets=" + action, body === undefined ? { headers, signal } : { method: "POST", headers, body: JSON.stringify(body), signal });
    if (response.ok) return response;
    if (response.status === 401) onUnauthorized();
    const answer = (await response.json().catch(() => null)) as { errors?: { message: string }[] } | null;
    throw new FacetRequestError(answer?.errors?.[0]?.message ?? `${response.status} ${response.statusText}`, response.status);
  }

  const json = async <T>(action: string, body?: unknown, signal?: AbortSignal) => (await (await ask(action, body, signal)).json()) as T;

  return {
    key: "facets:" + url + ":" + apiKey,
    // the page draws its cards as solids, and the endpoint makes no tiles
    tiles: false,
    model: (signal) => json<FacetModel>("model", undefined, signal),
    search: (request) => json<FacetSearchResult>("search", request),
    node: (id, signal) => json<FacetNode>("node", { id }, signal),
    pivotModel: (typeId) => json<PivotModel>("pivot-model", { typeId }),
    visual: (request: VisualRequest) =>
      json<VisualResult>("visual", {
        typeId: request.typeId,
        text: request.text,
        selections: request.selections,
        properties: request.properties,
        sortBy: request.sortBy ?? null,
        sortDescending: request.sortDescending ?? false,
        maxCards: request.maxCards ?? 0,
      }),
    cards: (ids: number[]) => json<{ cards: CardInfo[] }>("cards", { ids }),
    async cardImages(level: number, items: CardImageItem[], onRecord, signal) {
      const response = await ask("card-images", { level, items }, signal);
      await readCardImages(response, onRecord);
    },
  };
}
