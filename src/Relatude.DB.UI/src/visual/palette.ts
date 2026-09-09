/**
 * The colours the visual pivot paints its cards with: one per distinct value of a property, and a
 * property can have hundreds - so a palette is a band of OKLCH space (a span of hue, of tone and of
 * chroma) filled by a low-discrepancy sequence, which spreads any number of colours evenly through
 * the band without two of them landing on top of each other.
 *
 * Tone is distance from the page behind the cards rather than a lightness of its own: 0 is a card
 * that barely lifts off the background, 1 one as far from it as the palette goes. The theme decides
 * which way that is - down from white in the light theme, up from near-black in the dark one - so a
 * palette keeps its character in both and never fades into the page in either.
 *
 * A palette that names no hue takes the one the app is already built on - the accent the buttons and
 * links are drawn in - so the default picture is in the blue of the page around it, in both themes.
 */

export type RGB = [number, number, number];

export interface PaletteColor {
  /** 0..255 per channel */
  rgb: RGB;
  css: string;
}

export interface PaletteSpec {
  id: string;
  name: string;
  /** degrees; absent is a narrow band on the hue of the UI's accent colour */
  hue?: [number, number];
  /** 0..1, distance from the background (see the note above) */
  tone: [number, number];
  chroma: [number, number];
}

export const palettes: PaletteSpec[] = [
  { id: "accent", name: "Accent", tone: [0, 0.95], chroma: [0.02, 0.15] },
  { id: "spectrum", name: "Spectrum", hue: [0, 360], tone: [0.28, 0.5], chroma: [0.1, 0.16] },
  { id: "pastel", name: "Pastel", hue: [0, 360], tone: [0.03, 0.2], chroma: [0.03, 0.08] },
  { id: "vivid", name: "Vivid", hue: [0, 360], tone: [0.3, 0.72], chroma: [0.17, 0.3] },
  { id: "warm", name: "Warm", hue: [5, 95], tone: [0, 0.82], chroma: [0.04, 0.19] },
  { id: "cool", name: "Cool", hue: [175, 290], tone: [0, 0.85], chroma: [0.04, 0.17] },
  { id: "forest", name: "Forest", hue: [100, 165], tone: [0, 0.92], chroma: [0.03, 0.16] },
  { id: "berry", name: "Berry", hue: [295, 380], tone: [0, 0.85], chroma: [0.04, 0.18] },
  { id: "ocean", name: "Ocean", hue: [220, 245], tone: [0, 1], chroma: [0.02, 0.16] },
  { id: "amber", name: "Amber", hue: [60, 80], tone: [0, 0.94], chroma: [0.02, 0.17] },
  { id: "rose", name: "Rose", hue: [10, 25], tone: [0, 0.96], chroma: [0.02, 0.17] },
  { id: "slate", name: "Slate", hue: [235, 265], tone: [0, 1], chroma: [0.004, 0.05] },
  // the one palette with no colour in it at all: chroma 0 is grey whatever the hue says, and it
  // stays grey through both of the lifts below, since nothing multiplies its way out of zero
  { id: "mono", name: "Mono", hue: [0, 0], tone: [0, 1], chroma: [0, 0] },
];

/**
 * What every palette's chroma is multiplied by. One number rather than thirteen edited ranges: the
 * bands above say how each palette is shaped and this says how colourful the lot of them are, so
 * they can all be lifted or calmed at once without any of them losing its character. A colour that
 * lands outside sRGB has its chroma pulled back in by oklchToRgb, so this cannot clip a channel.
 */
const chromaLift = 1.28;
/**
 * And what the dark theme's palettes are multiplied by on top of that. A card on a dark page has to
 * carry its colour against a ground that gives it none, where one on a white page is read against a
 * ground that lends it plenty; the same chroma reads as noticeably greyer there. Together with the
 * lower ceiling on tone (see buildPalette) this is what keeps a dark picture coloured rather than
 * chalky.
 */
const darkChroma = 1.22;
/**
 * How far the light theme's cards are pulled down when the picture is asked to be dimmer. Only the
 * light theme has this: there the page is white and stays white - it cannot give way any further -
 * so what gives way is the cards' own tone, and a wall of them stops glaring. The dark theme dims by
 * taking the ground to black instead, and its palette is left exactly as it is.
 */
const dimTone = 0.11;

export function paletteSpec(id: string | null | undefined): PaletteSpec {
  return palettes.find((p) => p.id === id) ?? palettes[0];
}

/**
 * Colours for `count` groups, in group order, for cards drawn on `background`. `dim` asks for the
 * quieter of the two brightnesses the light theme offers (see dimTone); it does nothing in the dark
 * theme, which dims its picture by another route.
 */
export function buildPalette(count: number, background: RGB, accent: RGB, id?: string | null, dim = false): PaletteColor[] {
  const p = paletteSpec(id);
  const bg = lightnessOf(background);
  const dark = bg <= 0.5;
  // How far a card stands off the page. The dark theme used to run all the way up to 0.92, which is
  // very nearly white - and a colour that light has almost no room left for chroma inside sRGB, so
  // the top half of every palette came out washed toward grey (and the lighting in the picture of
  // solids, which multiplies the colour up, pushed it further). Held lower, the same hues keep their
  // colour and the chroma above can go up rather than being pulled back in.
  const lower = !dark && dim ? dimTone : 0;
  const near = (dark ? bg + 0.13 : bg - 0.15) - lower;
  const far = (dark ? 0.76 : 0.36) - lower;
  const ah = hueOf(accent);
  const hue = p.hue ?? [ah - 10, ah + 10];
  const colors: PaletteColor[] = [];
  for (let i = 0; i < count; i++) {
    const h = hue[0] + frac(i * 0.6180339887) * (hue[1] - hue[0]);
    const tone = p.tone[0] + frac(0.5 + i * 0.7548776662) * (p.tone[1] - p.tone[0]);
    const C = (p.chroma[0] + frac(0.5 + i * 0.569840291) * (p.chroma[1] - p.chroma[0])) * chromaLift * (dark ? darkChroma : 1);
    const rgb = oklchToRgb(near + tone * (far - near), C, h);
    colors.push({ rgb, css: `rgb(${rgb[0]} ${rgb[1]} ${rgb[2]})` });
  }
  return colors;
}

/** An sRGB colour in OKLab: its lightness, and the two axes its hue is read from. */
function oklabOf([r, g, b]: RGB): [number, number, number] {
  const lin = (c: number) => {
    const s = c / 255;
    return s <= 0.04045 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  };
  const R = lin(r);
  const G = lin(g);
  const B = lin(b);
  const l = Math.cbrt(0.4122214708 * R + 0.5363325363 * G + 0.0514459929 * B);
  const m = Math.cbrt(0.2119034982 * R + 0.6806995451 * G + 0.1073969566 * B);
  const s = Math.cbrt(0.0883024619 * R + 0.2817188376 * G + 0.6299787005 * B);
  return [
    0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s,
    1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s,
    0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s,
  ];
}

/** What the eye reads as how light a colour is. */
function lightnessOf(rgb: RGB): number {
  return oklabOf(rgb)[0];
}

/** Where a colour sits on the hue wheel, in degrees. */
function hueOf(rgb: RGB): number {
  const [, a, b] = oklabOf(rgb);
  const h = (Math.atan2(b, a) * 180) / Math.PI;
  return h < 0 ? h + 360 : h;
}

function frac(x: number): number {
  return x - Math.floor(x);
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
