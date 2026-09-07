/**
 * The colours the visual pivot paints its cards with.
 *
 * One colour per distinct value of a property, and a property can have hundreds - so the palette is
 * computed, not drawn up by hand, and computed the same way every time: hues stepped around the
 * wheel by the golden angle, so any two neighbours in the sequence sit far apart, at a lightness and
 * a chroma that only wander a little (a seeded generator, the same seed every run). Holding those two
 * close is what makes five hundred different hues read as one family rather than as confetti; the
 * work is done in OKLCH, where "the same lightness" is what the eye agrees it is.
 */

export type RGB = [number, number, number];

export interface PaletteColor {
  /** 0..255 per channel */
  rgb: RGB;
  css: string;
}

/** Colours for `count` groups, in group order. The dark theme gets the same hues, lifted a little. */
export function buildPalette(count: number, dark: boolean): PaletteColor[] {
  const random = seeded(0x5eed);
  const colors: PaletteColor[] = [];
  const baseL = dark ? 0.74 : 0.66;
  const baseC = dark ? 0.125 : 0.135;
  for (let i = 0; i < count; i++) {
    const hue = (28 + i * 137.50776405) % 360;
    // a little wander keeps neighbours in the sequence from looking like tints of one another once
    // the wheel has been round a few times, without leaving the band the family lives in
    const L = baseL + (random() - 0.5) * 0.09;
    const C = baseC + (random() - 0.5) * 0.05;
    const rgb = oklchToRgb(L, C, hue);
    colors.push({ rgb, css: `rgb(${rgb[0]} ${rgb[1]} ${rgb[2]})` });
  }
  return colors;
}

/** mulberry32: small, fast, and the same sequence for the same seed on every machine. */
function seeded(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/**
 * OKLCH to sRGB bytes. A colour outside the sRGB gamut has its chroma pulled in until it fits, which
 * keeps its lightness and hue - the two things the palette is holding constant - rather than clipping
 * a channel and shifting both.
 */
export function oklchToRgb(L: number, C: number, hueDegrees: number): RGB {
  const h = (hueDegrees * Math.PI) / 180;
  let lo = 0;
  let hi = C;
  let rgb = oklabToLinear(L, C * Math.cos(h), C * Math.sin(h));
  if (!inGamut(rgb)) {
    for (let i = 0; i < 12; i++) {
      const mid = (lo + hi) / 2;
      const candidate = oklabToLinear(L, mid * Math.cos(h), mid * Math.sin(h));
      if (inGamut(candidate)) {
        lo = mid;
        rgb = candidate;
      } else {
        hi = mid;
      }
    }
  }
  return [toByte(rgb[0]), toByte(rgb[1]), toByte(rgb[2])];
}

function oklabToLinear(L: number, a: number, b: number): RGB {
  const l_ = L + 0.3963377774 * a + 0.2158037573 * b;
  const m_ = L - 0.1055613458 * a - 0.0638541728 * b;
  const s_ = L - 0.0894841775 * a - 1.291485548 * b;
  const l = l_ * l_ * l_;
  const m = m_ * m_ * m_;
  const s = s_ * s_ * s_;
  return [
    4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
    -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s,
  ];
}

function inGamut(rgb: RGB): boolean {
  const e = 0.0005;
  return rgb.every((c) => c >= -e && c <= 1 + e);
}

function toByte(linear: number): number {
  const c = Math.min(1, Math.max(0, linear));
  const srgb = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.pow(c, 1 / 2.4) - 0.055;
  return Math.round(srgb * 255);
}

/** A CSS colour as bytes: #rgb, #rrggbb or rgb()/rgba(); anything else comes out mid grey. */
export function parseCssColor(css: string): RGB {
  const s = css.trim();
  if (s.startsWith("#")) {
    const hex = s.slice(1);
    if (hex.length === 3 || hex.length === 4) return [parseInt(hex[0] + hex[0], 16), parseInt(hex[1] + hex[1], 16), parseInt(hex[2] + hex[2], 16)];
    if (hex.length >= 6) return [parseInt(hex.slice(0, 2), 16), parseInt(hex.slice(2, 4), 16), parseInt(hex.slice(4, 6), 16)];
  }
  const m = /rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)/.exec(s);
  if (m) return [Math.round(Number(m[1])), Math.round(Number(m[2])), Math.round(Number(m[3]))];
  return [128, 128, 128];
}
