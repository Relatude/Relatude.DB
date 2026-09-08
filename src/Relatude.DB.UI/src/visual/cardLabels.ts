import { cardFill, imageShare, type CardField } from "./cardField";
import type { CardMedia } from "./cardMedia";

/**
 * The names on the cards: drawn on a 2D canvas laid over the WebGL one, from the same frame callback,
 * so text follows the cards exactly - through a pan, a zoom and a move - without an element per card
 * or React in the loop. A few hundred fillText calls a frame is a millisecond or two; that is what a
 * screen of readable cards holds.
 *
 * A name sits in the strip below the picture, in a light or dark ink according to what is behind it -
 * the card's own colour, or the page where the card is cut out to a shape that does not reach there -
 * at a size that follows the strip, trimmed to the card with an ellipsis. It comes in with the same
 * curve the shader brings the pictures in on, so pictures and names appear as one thing.
 */

export interface LabelColors {
  /** the group of every card, or null for one colour */
  assignment: Uint16Array | null;
  /** rgba bytes per group: the palette the field draws with */
  palette: Uint8Array;
  /**
   * Whether the cards are cut out to shapes. A name is drawn where a plain card keeps its colour,
   * but a shaped card mostly does not reach down there - a circle or a star leaves the strip to the
   * page - so the name is read against the page and takes its ink from that instead.
   */
  shaped: boolean;
  /** the silhouette of every card, or null when they are plain: what a pulse on the shape legend addresses */
  shapes: Uint16Array | null;
  /** the page behind the cards, rgb */
  panel: [number, number, number];
}

export interface CardLabels {
  /** Draws the names of the cards in view; called after every frame the field draws. */
  draw(field: CardField, media: CardMedia, colors: LabelColors | null): void;
  clear(): void;
}

/** how wide a card is, in css px, when its name comes in: the strip below the picture is a quarter of that, room for a small font */
const nameCssPx = 64;
const lightInk = "rgba(255, 255, 255, 0.94)";
const darkInk = "rgba(26, 24, 22, 0.92)";
const fontFamily = 'system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", sans-serif';
const maxFitCache = 4000;

export function createCardLabels(canvas: HTMLCanvasElement): CardLabels | null {
  const ctx = canvas.getContext("2d", { alpha: true });
  if (!ctx) return null;
  let positions = new Float32Array(0);
  // name and width → the text that fits, so a still picture measures nothing twice
  const fitCache = new Map<string, string>();
  let backingWidth = 0;
  let backingHeight = 0;

  function fit(name: string, maxWidth: number, fontPx: number): string {
    const k = fontPx + "|" + Math.round(maxWidth) + "|" + name;
    const hit = fitCache.get(k);
    if (hit !== undefined) return hit;
    let text = name;
    if (ctx!.measureText(name).width > maxWidth) {
      // the longest prefix that fits with an ellipsis, by bisection
      let lo = 0;
      let hi = name.length;
      while (lo < hi) {
        const mid = (lo + hi + 1) >> 1;
        if (ctx!.measureText(name.slice(0, mid) + "…").width <= maxWidth) lo = mid;
        else hi = mid - 1;
      }
      text = lo === 0 ? "" : name.slice(0, lo).trimEnd() + "…";
    }
    if (fitCache.size >= maxFitCache) fitCache.clear();
    fitCache.set(k, text);
    return text;
  }

  return {
    draw(field, media, colors) {
      const { width, height, dpr } = field.size();
      const w = Math.max(1, Math.round(width * dpr));
      const h = Math.max(1, Math.round(height * dpr));
      if (w !== backingWidth || h !== backingHeight) {
        canvas.width = w;
        canvas.height = h;
        backingWidth = w;
        backingHeight = h;
      }
      ctx!.setTransform(1, 0, 0, 1, 0, 0);
      ctx!.clearRect(0, 0, w, h);
      const cam = field.camera();
      const side = cardFill * cam.zoom;
      const detail = smoothstep(nameCssPx * 0.9, nameCssPx * 1.1, side);
      if (detail <= 0.001) return;
      const { indexes, count } = media.visible();
      if (count === 0) return;
      if (positions.length < count * 2) positions = new Float32Array(count * 2);
      field.positionsOf(indexes, count, positions);
      const strip = side * (1 - imageShare);
      const fontPx = Math.max(7, Math.min(400, Math.round(strip * 0.42)));
      const padX = side * 0.06;
      const maxWidth = side - 2 * padX;
      ctx!.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx!.font = `500 ${fontPx}px ${fontFamily}`;
      ctx!.textBaseline = "middle";
      ctx!.textAlign = "left";
      ctx!.globalAlpha = detail;
      const palette = colors?.palette ?? null;
      const assignment = colors?.assignment ?? null;
      // a pulse takes the cards it addresses into the page; their names go with them, or they would
      // be left hanging over the gap. One group fades at a time, so the alpha is set when it changes
      const pulse = field.pulseFade();
      const shapes = colors?.shapes ?? null;
      let alpha = detail;
      const onPage = colors?.shaped === true ? inkFor(colors.panel[0], colors.panel[1], colors.panel[2]) : null;
      let lastGroup = -2;
      let ink = onPage ?? lightInk;
      for (let k = 0; k < count; k++) {
        const i = indexes[k];
        const name = media.nameOf(i);
        if (!name) continue;
        const [cx, cy] = field.worldToCss(positions[k * 2] + 0.5, positions[k * 2 + 1] + 0.5);
        const x0 = cx - side / 2;
        const y0 = cy - side / 2 + side * imageShare;
        if (x0 > width || x0 + side < 0 || y0 > height || y0 + strip < 0) continue;
        const group = assignment !== null ? assignment[i] : 0;
        if (pulse !== null) {
          const faded = group === pulse.group || (shapes !== null && shapes[i] === pulse.shape);
          const a = faded ? detail * (1 - pulse.amount) : detail;
          if (a !== alpha) {
            alpha = a;
            ctx!.globalAlpha = a;
          }
        }
        if (onPage === null && group !== lastGroup) {
          lastGroup = group;
          ink = palette !== null && group * 4 + 2 < palette.length ? inkFor(palette[group * 4], palette[group * 4 + 1], palette[group * 4 + 2]) : lightInk;
        }
        ctx!.fillStyle = ink;
        ctx!.fillText(fit(name, maxWidth, fontPx), x0 + padX, y0 + strip / 2 + fontPx * 0.04);
      }
      ctx!.globalAlpha = 1;
    },
    clear() {
      ctx!.setTransform(1, 0, 0, 1, 0, 0);
      ctx!.clearRect(0, 0, canvas.width, canvas.height);
    },
  };
}

function smoothstep(a: number, b: number, x: number): number {
  const t = Math.min(1, Math.max(0, (x - a) / (b - a)));
  return t * t * (3 - 2 * t);
}

/** light text on a dark card, dark text on a light one, by the card's relative luminance */
function inkFor(r: number, g: number, b: number): string {
  const lin = (c: number) => {
    const s = c / 255;
    return s <= 0.04045 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  };
  const luminance = 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
  return luminance > 0.45 ? darkInk : lightInk;
}
