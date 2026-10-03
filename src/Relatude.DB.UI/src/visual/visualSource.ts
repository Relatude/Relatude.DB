import { fetchCards, fetchPivotModel, runVisual, streamCardImages, type CardImageItem, type CardInfo, type PivotModel, type PropertyScope, type VisualRequest, type VisualResult } from "../server/query";

/**
 * Where the cards' names and pictures come from (see cardMedia.ts): the admin UI's own commands, or the facet
 * search of a GraphQL endpoint, which answers the same questions over the url anyone can open (explorer/facetSource.ts).
 */
export interface CardSource {
  /** What the cards belong to. Another key is another set of nodes, and everything known about the old one is dropped. */
  key: string;
  /** The names and picture properties of the cards on screen, by their int ids (see fetchCards). */
  cards(ids: number[]): Promise<{ cards: CardInfo[] }>;
  /** Their pictures at one width, streamed back as they come ready (see streamCardImages). */
  cardImages(
    level: number,
    items: CardImageItem[],
    onRecord: (id: number, status: number, bytes: Uint8Array, region: Float32Array | null) => void,
    signal?: AbortSignal,
  ): Promise<void>;
  /** Whether a card zoomed past its largest picture may ask for tiles of it; absent is yes. */
  tiles?: boolean;
}

/** Where the visual pivot gets everything it draws: the properties it can group by, the cards, and their names and pictures. */
export interface VisualSource extends CardSource {
  pivotModel(typeId: string | null, propertyScope?: PropertyScope): Promise<PivotModel>;
  visual(request: VisualRequest): Promise<VisualResult>;
}

const adminSources = new Map<string, VisualSource>();

/** The admin UI's commands for one database: one object per database, so it is the same dependency from render to render. */
export function adminVisualSource(storeId: string): VisualSource {
  let source = adminSources.get(storeId);
  if (!source) {
    source = {
      key: "admin:" + storeId,
      pivotModel: (typeId, propertyScope) => fetchPivotModel(storeId, typeId, propertyScope),
      visual: (request) => runVisual(request),
      cards: (ids) => fetchCards(storeId, ids),
      cardImages: (level, items, onRecord, signal) => streamCardImages(storeId, level, items, onRecord, signal),
    };
    adminSources.set(storeId, source);
  }
  return source;
}
