// A node's picture was replaced under the page.
//
// The node form can put a new file on a file property (FileField in NodeEditor.tsx), and the visual
// pivot holds pictures of nodes in GPU textures and encoded bytes in memory, keyed by the node. Those
// two do not know about each other and should not: a page that draws pictures should not have to be
// told about every page that can change one. So the change is announced here, and whoever is drawing
// it lets go of what it holds.
//
// The id is the node's INT id, which is what the card views work in (query-cards, card-images); the
// form has it on the node it is showing. Nothing here carries the new picture - only the news that
// what was held is out of date.

interface PictureChange {
  storeId: string;
  /** the node's int id */
  nodeId: number;
}

const handlers = new Set<(change: PictureChange) => void>();

/** Says that this node's picture is not what it was. Safe to call when nothing is listening. */
export function notifyNodePicture(storeId: string, nodeId: number): void {
  for (const handler of handlers) handler({ storeId, nodeId });
}

/** Listens for it. Returns the way to stop listening, for an effect's cleanup. */
export function subscribeNodePicture(handler: (change: PictureChange) => void): () => void {
  handlers.add(handler);
  return () => {
    handlers.delete(handler);
  };
}
