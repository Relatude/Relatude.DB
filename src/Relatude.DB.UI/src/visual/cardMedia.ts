import { CardKind, cardFill, detailCssPx, imageLevels, imageShare, type CardField } from "./cardField";
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
/** how many pictures one request asks for (the server allows 128), and how many names */
const imagesPerBatch = 64;
const namesPerBatch = 500;
/** texels uploaded per frame at most: a 2048 picture is three million, a 256 one fifty thousand */
const uploadTexelBudget = 3_500_000;
/** decodes running at once */
const maxDecoding = 6;
/** encoded bytes kept, across results */
const byteCacheBudget = 96 * 1024 * 1024;
/**
 * The most layers each level is given. Each layer is a picture: 48 KB at 128, 192 KB at 256, 768 KB
 * at 512, 3 MB at 1024, 12 MB at 2048 - so these bound the GPU memory at about 150 MB with every
 * level in use, and a level is only made when the zoom first reaches it. Fewer layers than cards on
 * screen at a level means the rest draw the level below, one step softer.
 */
const layerCaps = [384, 160, 48, 12, 3];
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
  inFlight: boolean;
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
  const images = new Map<number, { p: string; v: string }>();
  // per card of the current result
  let state: Uint8Array = new Uint8Array(0);
  const shown = new Map<number, Shown>();
  const levels: Level[] = imageLevels.map(() => ({ layers: 0, slotCard: new Int32Array(0), slotSeen: new Float64Array(0), free: [], cardSlot: new Map(), inFlight: false }));
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
        if (best < 0 && desired > 1) want(i, 1, visibleDist[k] + 1e6, wanted, now);
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
      if (levels[l].inFlight) continue;
      // a level whose every layer is in view has no room for more: asking would only waste the decode
      const L = levels[l];
      if (L.layers > 0 && L.free.length === 0 && L.cardSlot.size >= L.layers && everyLayerInView(L)) continue;
      // and no level is asked for more pictures than it has layers, nearest the centre first
      const room = L.layers > 0 ? L.layers - L.cardSlot.size + countOutOfView(L) : capacityFor(l);
      if (room <= 0) continue;
      list.sort((a, b) => a.dist - b.dist);
      requestImages(l, list.slice(0, Math.min(imagesPerBatch, room)).map((w) => w.index));
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
          if (card.image) images.set(card.id, { p: card.image, v: card.version ?? "" });
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
    L.inFlight = true;
    const items: { id: number; p: string }[] = [];
    for (const i of indexes) {
      const id = ids[i];
      const info = images.get(id);
      if (info === undefined) continue;
      inFlight.add(key(id, level));
      items.push({ id, p: info.p });
    }
    if (items.length === 0) {
      L.inFlight = false;
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
        L.inFlight = false;
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
      byteTotal -= value.byteLength;
    }
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
    while (ready.length > 0 && budget > 0) {
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
    for (const L of levels) L.inFlight = false;
    toDecode.length = 0;
    for (const r of ready) r.bitmap.close();
    ready.length = 0;
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
      byteCache.clear();
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
        return;
      }
      desired = levelFor(cardCss * dpr);
      scan(now);
      uploadReady(now);
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
