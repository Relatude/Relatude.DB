import { CardKind, cardFill, detailCssPx, imageLevels, imageShare, tileSlots, tileWidths, type CardField, type TileOnScreen } from "./cardField";
import type { Layout } from "./layouts";
import { IntMap } from "./intMap";
import { fetchCards, streamCardImages } from "../server/query";

/**
 * Keeps the cards on the screen supplied with their names and pictures, once they are wide enough
 * to carry them, and decides which layer of which texture level every card draws (cardField.ts
 * only draws what it is told). Everything here is driven by what is in view: a result of a million
 * cards costs nothing until the zoom brings a few hundred of them up to readable size, and then only
 * those few hundred are ever asked about.
 *
 * How it works, per frame drawn:
 *
 *  1. The cards in view are found from the layout, not by searching the cards: both layouts put a
 *     card on an integer cell, so the cells under the viewport are simply looked up in a cell-to-card
 *     map (built once per layout, when first needed). The viewport is the camera's destination, so
 *     a wheel step fetches for where it is going, and a glide fetches for where it lands.
 *  2. A card whose name and picture are not known is put on a list; the lists go to the server in
 *     batches (query-cards). A card with a picture is wanted at the level whose width is at least the
 *     card's width on the screen; if it does not hold that level yet, it is put on the list of that
 *     level, nearest the centre first. One request per level is in flight at a time, each a stream
 *     of encoded pictures (card-images), decoded off the main thread as they arrive.
 *  3. Decoded pictures are uploaded into a free layer of their level - or the layer of the card
 *     that has been out of view longest - within a budget of texels per frame, so a burst of arrivals
 *     never stalls a pan. Each upload rewrites the three picture words of one card.
 *
 * Levels are held loosely: a card keeps every level it was given until the layer is needed by
 * someone else, so zooming back out costs nothing, and zooming in again finds the sharper level
 * still there. What the card draws is the smallest held level that is at least as wide as the card
 * on screen (or the largest held, when none is), and a change of level is a cross-fade in the shader.
 *
 * Encoded bytes are cached here too, keyed by node, file version and level, so a card that lost
 * its layer and comes back into view is a decode away rather than a request.
 *
 * Past the largest level the zoom goes on with tiles: the part of a card's picture that is in view,
 * cut out by the server at the width of the canvas (through the same conversion cache, as a zoomed
 * and focused adjustment), on a grid that halves with every doubling of the card - so a tile is
 * never more than a screen of pixels, and the picture stays sharp down to the pixels of the
 * original, past which the tiles are simply magnified. A card wider than the canvas is at most four
 * tiles; coarser ones stay underneath while sharper ones arrive.
 */

export interface CardMedia {
  /** The database the cards belong to; a change drops everything known. */
  setStore(storeId: string): void;
  /** A new set of cards, by node id in card order. What is known about the nodes is kept; every card starts flat. */
  setCards(ids: Int32Array): void;
  /** Where the cards are (or are going): the layout the viewport is read against. */
  setLayout(layout: Layout | null): void;
  /** Called after every frame the field draws: keeps the pictures of the cards in view coming, within a time budget. */
  frame(now: number): void;
  /** The cards in view as of the last frame, by card index; valid until the next frame. */
  visible(): { indexes: Int32Array; count: number };
  /** The name of a card, once known. */
  nameOf(index: number): string | null;
  destroy(): void;
}

// ---- tuning ----

/** how far outside the viewport, in cells, cards are fetched for */
const marginCells = 1;
/** how often the lists are looked at and requests sent */
const scheduleEveryMs = 100;
/** a card's width on screen, as a share of detailCssPx, at which fetching starts: a little before the pictures show */
const engageShare = 0.85;
/** how many pictures one request asks for (the server allows 128; the small levels take the most, a screen holds thousands of them), and how many names */
const imagesPerBatchFor = (level: number) => (level <= 1 ? 128 : 64);
/**
 * How many requests one level may have on its way at once. A screen of the smallest cards is
 * thousands of pictures - a dozen batches - and one at a time would fill the screen in stages you
 * could count; the small pictures are a few kilobytes each, so several batches at once cost little.
 * The large levels stay at one: those requests are megabytes and seconds of conversion.
 */
const maxRequestsFor = (level: number) => (level <= 1 ? 4 : level <= 3 ? 2 : 1);
const namesPerBatch = 500;
/** texels uploaded per frame at most: a 2048 picture is three million, a 256 one fifty thousand */
const uploadTexelBudget = 3_500_000;
/** and pictures per frame at most, whatever their size: an upload is a driver call, and the small levels arrive in hundreds */
const uploadsPerFrame = 64;
/** decodes running at once */
const maxDecoding = 6;
/** encoded bytes kept, across results */
const byteCacheBudget = 96 * 1024 * 1024;
/**
 * The most layers each level is given. Each layer is a picture: 12 KB at 64, 48 KB at 128, 192 KB
 * at 256, 768 KB at 512, 3 MB at 1024, 12 MB at 2048 - so these bound the GPU memory at about 200 MB
 * with every level in use, and a level is only made when the zoom first reaches it. Fewer layers
 * than cards on screen at a level means the rest draw the level below, one step softer.
 */
const layerCaps = [2048, 1024, 160, 48, 12, 2];
/** how long tiles are kept after the zoom has left them before their textures are let go */
const tileIdleMs = 8000;
/** how far beyond the part in view a tile is asked for, as a share of the part in view */
const tileMargin = 0.1;
/** how far a tile may magnify the original's pixels before a sharper one is pointless: there is nothing sharper to show */
const tileMaxMagnification = 2;
/** tile requests in flight at once */
const maxTileRequests = 2;
/** how long to wait before asking again for a picture still being converted, doubling each time, and how often */
const retryBaseMs = 1500;
const maxRetries = 6;
/** the most cells a viewport scan will visit: past this the cards are too small to carry pictures anyway */
const maxVisible = 8192;

const enum State {
  Unknown = 0,
  Asked = 1,
  Image = 2,
  None = 3,
}

interface Level {
  layers: number;
  /** the card in each layer, -1 for free */
  slotCard: Int32Array;
  /** when the card in each layer was last in view */
  slotSeen: Float64Array;
  free: number[];
  /** card index → layer */
  cardSlot: Map<number, number>;
  /** requests on their way for this level */
  requests: number;
}

interface Shown {
  level: number;
  layer: number;
  fromLevel: number;
  fromLayer: number;
}

interface Ready {
  index: number;
  id: number;
  level: number;
  bitmap: ImageBitmap;
}

/** a tile: the card, the node and file version, the grid it is on (p halvings, cell i, j) and the width it is made at */
interface TileWanted {
  index: number;
  id: number;
  v: string;
  p: number;
  i: number;
  j: number;
  width: number;
  dist: number;
}

interface TileSlot extends TileWanted {
  /** the part of the picture the tile shows, as the server made it */
  rect: Float32Array;
  arrival: number;
  lastWanted: number;
}

export function createCardMedia(field: CardField, initialStoreId: string): CardMedia {
  let storeId = initialStoreId;
  let ids: Int32Array = new Int32Array(0);
  let count = 0;
  let indexOf: IntMap | null = null; // node id → card index, built when the first answer needs it
  let layout: Layout | null = null;
  // the cell map: card index by cell, built when the zoom first calls for it
  let cells: IntMap | null = null;
  let cellX0 = 0;
  let cellY0 = 0;
  let spanX = 1;
  // what is known about the nodes, by node id: kept across results
  const names = new Map<number, string>();
  const images = new Map<number, { p: string; v: string; w: number; h: number }>();
  // per card of the current result
  let state: Uint8Array = new Uint8Array(0);
  const shown = new Map<number, Shown>();
  const levels: Level[] = imageLevels.map(() => ({ layers: 0, slotCard: new Int32Array(0), slotSeen: new Float64Array(0), free: [], cardSlot: new Map(), requests: 0 }));
  // the viewport, as of the last frame
  const visibleIndexes = new Int32Array(maxVisible);
  const visibleDist = new Float32Array(maxVisible);
  let visibleCount = 0;
  let visibleSet = new Set<number>();
  let scanStamp = 0; // performance.now() of the last scan: a layer seen at this stamp is in view
  let desired = 0; // the level the cards in view are wanted at
  let engaged = false;
  // requests
  let abort = new AbortController();
  let namesInFlight = false;
  const inFlight = new Set<string>(); // "id:level"
  const retries = new Map<string, { tries: number; notBefore: number }>();
  // bytes and bitmaps
  const byteCache = new Map<string, Uint8Array>(); // "id:v:level", oldest first
  let byteTotal = 0;
  const toDecode: { index: number; id: number; level: number; bytes: Uint8Array }[] = [];
  let decoding = 0;
  const ready: Ready[] = [];
  // tiles
  const tiles: (TileSlot | null)[] = Array.from({ length: tileSlots }, () => null);
  let tilesInFlight = 0; // tile requests on their way; a couple at a time, they are large and the server converts them beside each other
  let tilesWantedAt = 0; // when a tile was last wanted, for letting the textures go
  const noTiles = new Set<number>(); // node ids whose picture cannot be tiled (its size unknown, or the server said so)
  const tileRegions = new Map<string, Float32Array>(); // by byte cache key: the part of the picture a cached tile shows
  const tileReady: { want: TileWanted; bitmap: ImageBitmap; region: Float32Array }[] = [];
  let lastSchedule = 0;
  let timer = 0;
  let destroyed = false;

  // ---- helpers ----

  function key(id: number, level: number): string {
    return id + ":" + level;
  }
  function cacheKey(id: number, v: string, level: number): string {
    return id + ":" + v + ":" + level;
  }
  /** the smallest level at least as wide as a card of `devPx` device pixels, or the largest there is */
  function levelFor(devPx: number): number {
    for (let i = 0; i < imageLevels.length; i++) if (imageLevels[i] >= devPx) return i;
    return imageLevels.length - 1;
  }
  function heldLevels(index: number): number[] {
    const held: number[] = [];
    for (let l = 0; l < levels.length; l++) if (levels[l].cardSlot.has(index)) held.push(l);
    return held;
  }
  /** the level a card should draw out of those it holds: the smallest at least `want`, else the largest */
  function choose(held: number[], want: number): number {
    let best = -1;
    for (const l of held) {
      if (l >= want) return l; // held is ascending
      best = l;
    }
    return best;
  }
  function ensureIndexOf(): IntMap {
    if (indexOf === null) {
      indexOf = new IntMap(count);
      for (let i = 0; i < count; i++) indexOf.set(ids[i], i);
    }
    return indexOf;
  }
  function kick() {
    if (timer === 0 && !destroyed) timer = window.setTimeout(tick, scheduleEveryMs);
  }
  function tick() {
    timer = 0;
    if (destroyed) return;
    schedule(performance.now());
  }

  // ---- what a card draws ----

  /** tells the field what a card draws, from the levels it holds; `now` stamps a change of level */
  function present(index: number, now: number) {
    const held = heldLevels(index);
    if (held.length === 0) {
      if (shown.delete(index)) field.setCardImage(index, CardKind.Image, -1, -1, -1, -1, 0);
      return;
    }
    const best = choose(held, desired);
    const cur = shown.get(index);
    if (cur !== undefined && cur.level === best) {
      // the same picture; the one it was fading from may have lost its layer meanwhile
      if (cur.fromLevel >= 0 && !levels[cur.fromLevel].cardSlot.has(index)) {
        cur.fromLevel = -1;
        cur.fromLayer = -1;
        field.setCardImage(index, CardKind.Image, cur.level, cur.layer, -1, -1, now);
      }
      return;
    }
    const layer = levels[best].cardSlot.get(index)!;
    let fromLevel = -1;
    let fromLayer = -1;
    if (cur !== undefined && levels[cur.level].cardSlot.has(index)) {
      fromLevel = cur.level;
      fromLayer = cur.layer;
    }
    shown.set(index, { level: best, layer, fromLevel, fromLayer });
    field.setCardImage(index, CardKind.Image, best, layer, fromLevel, fromLayer, now);
  }

  function setKind(index: number, kind: CardKind) {
    shown.delete(index);
    field.setCardImage(index, kind, -1, -1, -1, -1, 0);
  }

  // ---- layers ----

  /** how many layers a level gets: enough for a screen of the smallest cards it serves, within its cap */
  function capacityFor(level: number): number {
    const { dpr } = field.size();
    const sw = (window.screen?.width ?? 1920) * dpr;
    const sh = (window.screen?.height ?? 1080) * dpr;
    const minPx = level === 0 ? detailCssPx * dpr : imageLevels[level - 1];
    const need = (Math.ceil(sw / minPx) + 2) * (Math.ceil(sh / minPx) + 2);
    return Math.max(2, Math.min(layerCaps[level], need));
  }

  function ensureLevel(level: number): Level {
    const L = levels[level];
    if (L.layers > 0) return L;
    field.ensureImageLevel(level, capacityFor(level));
    const layers = field.imageLayers(level);
    L.layers = layers;
    L.slotCard = new Int32Array(layers).fill(-1);
    L.slotSeen = new Float64Array(layers);
    L.free = [];
    for (let s = layers - 1; s >= 0; s--) L.free.push(s);
    L.cardSlot.clear();
    return L;
  }

  /** a layer for a card at a level: a free one, else the one whose card has been out of view longest; -1 when every layer is in view */
  function allocate(level: number, index: number, now: number): number {
    const L = ensureLevel(level);
    let layer = -1;
    if (L.free.length > 0) {
      layer = L.free.pop()!;
    } else {
      let oldest = Infinity;
      for (let s = 0; s < L.layers; s++) {
        const seen = L.slotSeen[s];
        if (seen < scanStamp && seen < oldest) {
          oldest = seen;
          layer = s;
        }
      }
      if (layer < 0) return -1;
      const evicted = L.slotCard[layer];
      L.cardSlot.delete(evicted);
      present(evicted, now); // it draws what it still holds, or goes back to its colour
    }
    L.slotCard[layer] = index;
    L.slotSeen[layer] = now;
    L.cardSlot.set(index, layer);
    return layer;
  }

  function freeAllLayers() {
    for (const L of levels) {
      L.cardSlot.clear();
      L.slotCard.fill(-1);
      L.slotSeen.fill(0);
      L.free = [];
      for (let s = L.layers - 1; s >= 0; s--) L.free.push(s);
    }
  }

  // ---- the viewport ----

  function ensureCells(): IntMap | null {
    if (cells !== null || layout === null || count === 0) return cells;
    const b = layout.bounds;
    cellX0 = Math.floor(b.x0) - 1;
    cellY0 = Math.floor(b.y0) - 1;
    spanX = Math.ceil(b.x1) - cellX0 + 2;
    const map = new IntMap(count);
    const p = layout.positions;
    for (let i = 0; i < count; i++) {
      const k = (Math.round(p[i * 2 + 1]) - cellY0) * spanX + (Math.round(p[i * 2]) - cellX0);
      if (k >= 0 && k < 0x7fffffff) map.set(k, i);
    }
    cells = map;
    return cells;
  }

  function scan(now: number) {
    visibleCount = 0;
    const map = ensureCells();
    if (map === null || layout === null) {
      visibleSet = new Set();
      return;
    }
    const cam = field.cameraTarget();
    const { width, height } = field.size();
    const halfW = width / 2 / cam.zoom;
    const halfH = height / 2 / cam.zoom;
    const b = layout.bounds;
    const x0 = Math.max(Math.floor(b.x0), Math.floor(cam.x - halfW - marginCells));
    const x1 = Math.min(Math.ceil(b.x1), Math.ceil(cam.x + halfW + marginCells));
    const y0 = Math.max(Math.floor(b.y0), Math.floor(cam.y - halfH - marginCells));
    const y1 = Math.min(Math.ceil(b.y1), Math.ceil(cam.y + halfH + marginCells));
    const set = new Set<number>();
    for (let cy = y0; cy <= y1 && visibleCount < maxVisible; cy++) {
      for (let cx = x0; cx <= x1 && visibleCount < maxVisible; cx++) {
        const i = map.get((cy - cellY0) * spanX + (cx - cellX0));
        if (i < 0) continue;
        visibleIndexes[visibleCount] = i;
        const dx = cx + 0.5 - cam.x;
        const dy = cy + 0.5 - cam.y;
        visibleDist[visibleCount] = dx * dx + dy * dy;
        visibleCount++;
        set.add(i);
      }
    }
    visibleSet = set;
    scanStamp = now;
    // the layers of the cards in view are marked seen, so they are the last to be taken
    for (let k = 0; k < visibleCount; k++) {
      const i = visibleIndexes[k];
      for (const L of levels) {
        if (L.layers === 0) continue;
        const s = L.cardSlot.get(i);
        if (s !== undefined) L.slotSeen[s] = now;
      }
    }
  }

  // ---- requests ----

  function schedule(now: number) {
    lastSchedule = now;
    if (destroyed || !engaged || visibleCount === 0) return;
    let outstanding = false;
    // names first: a card cannot ask for its picture before it knows which property holds it
    const needNames: number[] = [];
    for (let k = 0; k < visibleCount; k++) {
      const i = visibleIndexes[k];
      if (state[i] === State.Unknown) {
        // known from an earlier result, or another card of the same node?
        const id = ids[i];
        if (names.has(id)) {
          settle(i, id, now);
          continue;
        }
        needNames.push(id);
        if (needNames.length >= namesPerBatch) break;
      }
    }
    if (needNames.length > 0) {
      outstanding = true;
      if (!namesInFlight) requestNames(needNames);
    }
    // then pictures, per level, nearest the centre first
    const wanted: { index: number; dist: number }[][] = imageLevels.map(() => []);
    for (let k = 0; k < visibleCount; k++) {
      const i = visibleIndexes[k];
      if (state[i] !== State.Image) continue;
      const held = heldLevels(i);
      const best = choose(held, desired);
      if (best !== (shown.get(i)?.level ?? -1)) present(i, now);
      if (best < desired) {
        // nothing sharp enough: the level wanted, and a quick small one first when it has nothing at all
        if (best < 0 && desired > 2) want(i, 2, visibleDist[k] + 1e6, wanted, now);
        want(i, desired, visibleDist[k], wanted, now);
      } else if (best > desired + 1) {
        // far sharper than the card is wide, which shimmers: the right level, after everything else
        want(i, desired, visibleDist[k] + 1e7, wanted, now);
      }
    }
    for (let l = 0; l < levels.length; l++) {
      const list = wanted[l];
      if (list.length === 0) continue;
      outstanding = true;
      const L = levels[l];
      let spare = maxRequestsFor(l) - L.requests;
      if (spare <= 0) continue;
      // a level whose every layer is in view has no room for more: asking would only waste the decode
      if (L.layers > 0 && L.free.length === 0 && L.cardSlot.size >= L.layers && everyLayerInView(L)) continue;
      // and no level is asked for more pictures than it has layers, nearest the centre first
      let room = L.layers > 0 ? L.layers - L.cardSlot.size + countOutOfView(L) : capacityFor(l);
      if (room <= 0) continue;
      list.sort((a, b) => a.dist - b.dist);
      // as many batches as the level will carry, the nearest cards in the first of them
      const batch = imagesPerBatchFor(l);
      for (let at = 0; at < list.length && spare > 0 && room > 0; at += batch) {
        const take = Math.min(batch, room, list.length - at);
        requestImages(l, list.slice(at, at + take).map((w) => w.index));
        spare--;
        room -= take;
      }
    }
    if (outstanding || inFlight.size > 0 || namesInFlight || retries.size > 0) kick();
  }

  function everyLayerInView(L: Level): boolean {
    for (let s = 0; s < L.layers; s++) if (L.slotSeen[s] < scanStamp) return false;
    return true;
  }
  /** the taken layers whose card is out of view: what a new picture could take over */
  function countOutOfView(L: Level): number {
    let n = 0;
    for (let s = 0; s < L.layers; s++) if (L.slotCard[s] >= 0 && L.slotSeen[s] < scanStamp) n++;
    return n;
  }

  /** puts a card on a level's list, unless it is on its way, waiting to be asked again, or already in the byte cache */
  function want(index: number, level: number, dist: number, wanted: { index: number; dist: number }[][], now: number) {
    const id = ids[index];
    const k = key(id, level);
    if (inFlight.has(k)) return;
    const retry = retries.get(k);
    if (retry !== undefined && retry.notBefore > now) return;
    const info = images.get(id);
    if (info === undefined) return;
    const cached = byteCache.get(cacheKey(id, info.v, level));
    if (cached !== undefined) {
      // in hand: straight to the decoder, once (it is in flight while it decodes)
      inFlight.add(k);
      toDecode.push({ index, id, level, bytes: cached });
      pumpDecodes();
      return;
    }
    wanted[level].push({ index, dist });
  }

  /** what is known about a node, applied to a card of it */
  function settle(index: number, id: number, _now: number) {
    if (images.has(id)) {
      state[index] = State.Image;
      if (!shown.has(index)) setKind(index, CardKind.Image);
    } else {
      state[index] = State.None;
      setKind(index, CardKind.Placeholder);
    }
  }

  function requestNames(list: number[]) {
    namesInFlight = true;
    const asked = list.slice();
    const map = ensureIndexOf();
    for (const id of asked) {
      const i = map.get(id);
      if (i >= 0) state[i] = State.Asked;
    }
    const signal = abort.signal;
    fetchCards(storeId, asked)
      .then((answer) => {
        if (signal.aborted) return;
        const now = performance.now();
        const seen = new Set<number>();
        for (const card of answer.cards) {
          seen.add(card.id);
          names.set(card.id, card.name);
          if (card.image) images.set(card.id, { p: card.image, v: card.version ?? "", w: card.width ?? 0, h: card.height ?? 0 });
          else images.delete(card.id);
          const i = map.get(card.id);
          if (i >= 0) settle(i, card.id, now);
        }
        // a node the server did not answer for is gone: no name, no picture
        for (const id of asked) {
          if (seen.has(id)) continue;
          names.set(id, "");
          images.delete(id);
          const i = map.get(id);
          if (i >= 0) settle(i, id, now);
        }
        field.invalidate();
      })
      .catch(() => {
        if (signal.aborted) return;
        // asked again on a later pass
        for (const id of asked) {
          const i = map.get(id);
          if (i >= 0 && state[i] === State.Asked) state[i] = State.Unknown;
        }
      })
      .finally(() => {
        if (signal.aborted) return;
        namesInFlight = false;
        kick();
      });
  }

  function requestImages(level: number, indexes: number[]) {
    const L = levels[level];
    L.requests++;
    const items: { id: number; p: string }[] = [];
    for (const i of indexes) {
      const id = ids[i];
      const info = images.get(id);
      if (info === undefined) continue;
      inFlight.add(key(id, level));
      items.push({ id, p: info.p });
    }
    if (items.length === 0) {
      L.requests--;
      return;
    }
    const signal = abort.signal;
    const map = ensureIndexOf();
    streamCardImages(
      storeId,
      imageLevels[level],
      items,
      (id, status, bytes) => {
        if (signal.aborted) return;
        const k = key(id, level);
        inFlight.delete(k);
        const i = map.get(id);
        const info = images.get(id);
        if (status === 0) {
          retries.delete(k);
          if (info !== undefined) cacheBytes(cacheKey(id, info.v, level), bytes);
          if (i >= 0 && visibleSet.has(i)) {
            inFlight.add(k); // while it decodes
            toDecode.push({ index: i, id, level, bytes });
            pumpDecodes();
          }
        } else if (status === 1) {
          // still converting: asked again later, a little later each time, and given up on in the end
          const r = retries.get(k) ?? { tries: 0, notBefore: 0 };
          r.tries++;
          r.notBefore = performance.now() + retryBaseMs * Math.pow(2, r.tries - 1);
          retries.set(k, r);
          if (r.tries > maxRetries) {
            retries.delete(k);
            giveUp(id, i);
          }
        } else {
          giveUp(id, i);
        }
      },
      signal,
    )
      .catch(() => {
        // the request failed as a whole: what it carried is asked for again on a later pass
      })
      .finally(() => {
        if (signal.aborted) return;
        for (const item of items) inFlight.delete(key(item.id, level));
        L.requests--;
        kick();
      });
  }

  /** a picture that will not come: the card shows the placeholder from here on */
  function giveUp(id: number, index: number) {
    images.delete(id);
    if (index >= 0) {
      state[index] = State.None;
      setKind(index, CardKind.Placeholder);
    }
  }

  function cacheBytes(k: string, bytes: Uint8Array) {
    const old = byteCache.get(k);
    if (old !== undefined) {
      byteTotal -= old.byteLength;
      byteCache.delete(k);
    }
    byteCache.set(k, bytes);
    byteTotal += bytes.byteLength;
    for (const [oldest, value] of byteCache) {
      if (byteTotal <= byteCacheBudget) break;
      byteCache.delete(oldest);
      tileRegions.delete(oldest);
      byteTotal -= value.byteLength;
    }
  }

  // ---- tiles: past the largest level, the part of the picture in view ----

  function tileKey(w: TileWanted): string {
    return w.id + ":" + w.v + ":t" + w.p + ":" + w.i + ":" + w.j + ":" + w.width;
  }
  function sameTile(a: TileWanted, b: TileWanted): boolean {
    return a.id === b.id && a.v === b.v && a.p === b.p && a.i === b.i && a.j === b.j && a.width === b.width;
  }
  /** the tile width for a canvas: the first at least as wide as it, so a tile's pixels are never stretched over the screen's */
  function tileWidthFor(canvasDevPx: number): number {
    for (const w of tileWidths) if (w >= canvasDevPx) return w;
    return tileWidths[tileWidths.length - 1];
  }

  /**
   * The tiles the cards in view want, nearest the centre first. A card wants tiles once it is wider
   * on screen than the largest level, on the grid whose tiles are at least as wide as the canvas
   * (p halvings of the picture), and no finer than the original's own pixels allow.
   */
  function wantedTiles(now: number): TileWanted[] {
    const wanted: TileWanted[] = [];
    if (layout === null || visibleCount === 0) return wanted;
    const cam = field.cameraTarget();
    const { width, height, dpr } = field.size();
    const cardDev = cardFill * cam.zoom * dpr;
    const top = imageLevels[imageLevels.length - 1];
    if (cardDev <= top) return wanted;
    const tw = tileWidthFor(width * dpr);
    const positions = layout.positions;
    const halfW = width / 2 / cam.zoom;
    const halfH = height / 2 / cam.zoom;
    const pictureH = cardFill * imageShare;
    for (let k = 0; k < visibleCount; k++) {
      const index = visibleIndexes[k];
      if (state[index] !== State.Image) continue;
      const id = ids[index];
      const info = images.get(id);
      if (info === undefined || noTiles.has(id) || info.w <= 0 || info.h <= 0) continue;
      // the picture is the 4:3 middle of the original; tiles of it are sharp down to its pixels
      const pictureW = info.w * 3 >= info.h * 4 ? (info.h * 4) / 3 : info.w;
      const pMax = Math.floor(Math.log2((tileMaxMagnification * pictureW) / tw));
      const p = Math.min(pMax, Math.floor(Math.log2(cardDev / tw)));
      if (p < 1) continue;
      const n = 1 << p;
      // the part of the picture in view, as fractions of the picture, with a margin around it
      const x0 = positions[index * 2] + 0.5 - cardFill / 2;
      const y0 = positions[index * 2 + 1] + 0.5 - cardFill / 2;
      let u0 = (cam.x - halfW - x0) / cardFill;
      let u1 = (cam.x + halfW - x0) / cardFill;
      let v0 = (cam.y - halfH - y0) / pictureH;
      let v1 = (cam.y + halfH - y0) / pictureH;
      const mu = tileMargin * (u1 - u0);
      const mv = tileMargin * (v1 - v0);
      u0 = Math.max(0, u0 - mu);
      u1 = Math.min(1, u1 + mu);
      v0 = Math.max(0, v0 - mv);
      v1 = Math.min(1, v1 + mv);
      if (u1 <= u0 || v1 <= v0) continue;
      const uc = (u0 + u1) / 2;
      const vc = (v0 + v1) / 2;
      const i0 = Math.min(n - 1, Math.floor(u0 * n));
      const i1 = Math.min(n - 1, Math.floor(u1 * n));
      const j0 = Math.min(n - 1, Math.floor(v0 * n));
      const j1 = Math.min(n - 1, Math.floor(v1 * n));
      for (let j = j0; j <= j1; j++) {
        for (let i = i0; i <= i1; i++) {
          const du = (i + 0.5) / n - uc;
          const dv = (j + 0.5) / n - vc;
          wanted.push({ index, id, v: info.v, p, i, j, width: tw, dist: du * du + dv * dv });
        }
      }
    }
    wanted.sort((a, b) => a.dist - b.dist);
    if (wanted.length > tileSlots) wanted.length = tileSlots;
    if (wanted.length > 0) tilesWantedAt = now;
    return wanted;
  }

  function tileFrame(now: number) {
    const wanted = wantedTiles(now);
    for (const w of wanted) {
      const held = tiles.find((t) => t !== null && sameTile(t, w));
      if (held) {
        held.lastWanted = now;
        continue;
      }
      const k = tileKey(w);
      if (inFlight.has(k)) continue;
      const retry = retries.get(k);
      if (retry !== undefined && retry.notBefore > now) continue;
      const cached = byteCache.get(k);
      const region = tileRegions.get(k);
      if (cached !== undefined && region !== undefined) {
        inFlight.add(k);
        decodeTile(w, cached, region);
      } else if (tilesInFlight < maxTileRequests) {
        requestTile(w);
      }
    }
    uploadTilesReady(now);
    // tiles nobody has wanted for a while go, textures and all
    if (wanted.length === 0 && tilesWantedAt > 0 && now - tilesWantedAt > tileIdleMs && tiles.some((t) => t !== null)) {
      tiles.fill(null);
      field.freeTiles();
      tilesWantedAt = 0;
    }
  }

  function requestTile(w: TileWanted) {
    const info = images.get(w.id);
    if (info === undefined) return;
    const k = tileKey(w);
    tilesInFlight++;
    inFlight.add(k);
    const signal = abort.signal;
    const n = 1 << w.p;
    streamCardImages(
      storeId,
      w.width,
      [{ id: w.id, p: info.p, tile: { x: w.i / n, y: w.j / n, size: 1 / n, width: w.width } }],
      (id, status, bytes, region) => {
        if (signal.aborted || id !== w.id) return;
        inFlight.delete(k);
        if (status === 0 && region !== null) {
          retries.delete(k);
          cacheBytes(k, bytes);
          tileRegions.set(k, region);
          inFlight.add(k); // while it decodes
          decodeTile(w, bytes, region);
        } else if (status === 1) {
          const r = retries.get(k) ?? { tries: 0, notBefore: 0 };
          r.tries++;
          r.notBefore = performance.now() + retryBaseMs * Math.pow(2, r.tries - 1);
          retries.set(k, r);
          if (r.tries > maxRetries) {
            retries.delete(k);
            noTiles.add(id);
          }
        } else {
          noTiles.add(id); // no tile to be had for this picture; it keeps its largest level
        }
      },
      signal,
    )
      .catch(() => {
        // asked again on a later pass
      })
      .finally(() => {
        if (signal.aborted) return;
        inFlight.delete(k);
        tilesInFlight--;
        kick();
        field.invalidate();
      });
  }

  function decodeTile(w: TileWanted, bytes: Uint8Array, region: Float32Array) {
    const signal = abort.signal;
    createImageBitmap(new Blob([bytes as BlobPart]), { premultiplyAlpha: "none", colorSpaceConversion: "default" })
      .then((bitmap) => {
        if (signal.aborted || destroyed) {
          bitmap.close();
          return;
        }
        tileReady.push({ want: w, bitmap, region });
        field.invalidate();
      })
      .catch(() => {
        if (!signal.aborted) inFlight.delete(tileKey(w));
      });
  }

  /** one tile a frame into a free slot, or the slot whose tile has gone unwanted longest */
  function uploadTilesReady(now: number) {
    while (tileReady.length > 0) {
      const r = tileReady.shift()!;
      inFlight.delete(tileKey(r.want));
      if (ids[r.want.index] !== r.want.id || state[r.want.index] !== State.Image) {
        r.bitmap.close();
        continue;
      }
      let slot = tiles.findIndex((t) => t === null);
      if (slot < 0) {
        let oldest = Infinity;
        for (let s = 0; s < tileSlots; s++) {
          const t = tiles[s]!;
          if (t.lastWanted < now && t.lastWanted < oldest) {
            oldest = t.lastWanted;
            slot = s;
          }
        }
        if (slot < 0) {
          r.bitmap.close(); // every slot is wanted as it is
          continue;
        }
      }
      field.uploadTile(slot, r.bitmap);
      r.bitmap.close();
      tiles[slot] = { ...r.want, rect: r.region, arrival: now, lastWanted: now };
      showTiles();
      break;
    }
    if (tileReady.length > 0) field.invalidate();
  }

  /** the field is told the tiles held, coarsest first, so a sharper one is laid over the one it replaces */
  function showTiles() {
    const shown: TileOnScreen[] = [];
    for (let s = 0; s < tileSlots; s++) {
      const t = tiles[s];
      if (t !== null) shown.push({ card: t.index, slot: s, rect: t.rect, arrivalMs: t.arrival });
    }
    shown.sort((a, b) => tiles[a.slot]!.p - tiles[b.slot]!.p);
    field.setTiles(shown);
  }

  // ---- decoding and uploading ----

  function pumpDecodes() {
    while (decoding < maxDecoding && toDecode.length > 0) {
      const job = toDecode.shift()!;
      decoding++;
      const signal = abort.signal;
      createImageBitmap(new Blob([job.bytes as BlobPart]), { premultiplyAlpha: "none", colorSpaceConversion: "default" })
        .then((bitmap) => {
          if (signal.aborted || destroyed) {
            bitmap.close();
            return;
          }
          ready.push({ index: job.index, id: job.id, level: job.level, bitmap });
          field.invalidate(); // the frame uploads it
        })
        .catch(() => {
          // bytes that do not decode: dropped, and the card is left as it is
          if (!signal.aborted) inFlight.delete(key(job.id, job.level));
        })
        .finally(() => {
          decoding--;
          if (!signal.aborted) pumpDecodes();
        });
    }
  }

  function uploadReady(now: number) {
    let budget = uploadTexelBudget;
    let uploads = uploadsPerFrame;
    while (ready.length > 0 && budget > 0 && uploads > 0) {
      const r = ready.shift()!;
      inFlight.delete(key(r.id, r.level));
      // meanwhile the card may have left the screen, changed, or been given the level another way
      if (!visibleSet.has(r.index) || ids[r.index] !== r.id || levels[r.level].cardSlot.has(r.index) || state[r.index] !== State.Image) {
        r.bitmap.close();
        continue;
      }
      const layer = allocate(r.level, r.index, now);
      if (layer < 0) {
        r.bitmap.close(); // no room at this level while every layer is in view; the card keeps what it has
        continue;
      }
      field.uploadImage(r.level, layer, r.bitmap);
      r.bitmap.close();
      budget -= imageLevels[r.level] * imageLevels[r.level] * imageShare;
      uploads--;
      present(r.index, now);
    }
    if (ready.length > 0) field.invalidate(); // the rest next frame
  }

  // ---- the api ----

  function reset() {
    abort.abort();
    abort = new AbortController();
    inFlight.clear();
    retries.clear();
    namesInFlight = false;
    for (const L of levels) L.requests = 0;
    toDecode.length = 0;
    for (const r of ready) r.bitmap.close();
    ready.length = 0;
    for (const r of tileReady) r.bitmap.close();
    tileReady.length = 0;
    tilesInFlight = 0;
    tiles.fill(null);
    field.setTiles([]);
    shown.clear();
    freeAllLayers();
    visibleCount = 0;
    visibleSet = new Set();
    cells = null;
    indexOf = null;
  }

  return {
    setStore(id) {
      if (id === storeId) return;
      storeId = id;
      reset();
      names.clear();
      images.clear();
      noTiles.clear();
      byteCache.clear();
      tileRegions.clear();
      byteTotal = 0;
      state.fill(0);
    },
    setCards(nextIds) {
      reset();
      ids = nextIds;
      count = nextIds.length;
      state = new Uint8Array(count);
    },
    setLayout(next) {
      layout = next;
      cells = null;
    },
    frame(now) {
      if (destroyed) return;
      if (count === 0 || layout === null) {
        engaged = false;
        visibleCount = 0;
        return;
      }
      const cam = field.cameraTarget();
      const { dpr } = field.size();
      const cardCss = cardFill * cam.zoom;
      engaged = cardCss >= detailCssPx * engageShare;
      if (!engaged) {
        visibleCount = 0;
        visibleSet = new Set();
        // pictures that arrived for a view since left: kept as bytes, not as layers
        for (const r of ready) {
          inFlight.delete(key(r.id, r.level));
          r.bitmap.close();
        }
        ready.length = 0;
        tileFrame(now);
        return;
      }
      desired = levelFor(cardCss * dpr);
      scan(now);
      uploadReady(now);
      tileFrame(now);
      if (now - lastSchedule >= scheduleEveryMs) schedule(now);
      else kick();
    },
    visible: () => ({ indexes: visibleIndexes, count: visibleCount }),
    nameOf(index) {
      if (index < 0 || index >= count) return null;
      return names.get(ids[index]) ?? null;
    },
    destroy() {
      destroyed = true;
      if (timer !== 0) window.clearTimeout(timer);
      timer = 0;
      reset();
    },
  };
}
