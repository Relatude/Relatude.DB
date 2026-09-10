#!/usr/bin/env python3
"""Builds src/map/worldMap.ts - the country outlines the Map view draws - from Natural Earth.

The source is the world-atlas build of Natural Earth's "admin 0 countries", which is public domain
(Natural Earth's terms: "no permission needed"). It arrives as TopoJSON: a set of ARCS - runs of
coordinates - and countries built from them, so a border between two countries is one arc used
twice rather than two lines drawn on top of each other. That topology is exactly what a map of lines
wants, so it is kept: the outlines are drawn once, arc by arc.

What this writes is that same topology as a base64 payload of the numbers, which needs no parser in
the browser beyond the thirty lines in worldMap.ts.

The 50m source is the one worth having: 1:110m is a world map and nothing more - Denmark is four
strokes and Norway has no fjords - while 1:50m still reads as the country you know at a city's zoom.
It is ten times the points, so they are re-quantized onto a coarser grid than the source's own
(GRID, below) and points that land on the same cell twice are dropped, which is most of the
difference between a file worth shipping and one that is not. The grid is far finer than a line one
pixel wide can show at any zoom the map offers.

    python tools/generate-world-map.py                    (run from src/Relatude.DB.UI)
    python tools/generate-world-map.py cached.json        (from a file already downloaded)

Re-run it only to take a new source or a different resolution; the output is checked in, so a build
never needs the network.
"""

import base64
import json
import os
import sys
import urllib.request

SOURCE = "https://cdn.jsdelivr.net/npm/world-atlas@2/countries-50m.json"
OUTPUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "map", "worldMap.ts")

# The grid the coordinates are snapped to, in degrees: about 550 m of longitude at the equator and
# less than that anywhere else. A country line is one pixel wide, and one pixel is 550 m only when
# the map is zoomed to a single town - so nothing this throws away is ever on screen.
GRID = 0.005


def varint(value: int, out: bytearray) -> None:
    """LEB128, unsigned."""
    while True:
        byte = value & 0x7F
        value >>= 7
        if value:
            out.append(byte | 0x80)
        else:
            out.append(byte)
            return


def zigzag(value: int, out: bytearray) -> None:
    """A signed number as an unsigned one, small either side of zero."""
    varint((value << 1) ^ (value >> 63) if value < 0 else value << 1, out)


def main() -> int:
    cache = sys.argv[1] if len(sys.argv) > 1 else None
    if cache:
        print("reading " + cache)
        topo = json.load(open(cache, encoding="utf-8"))
    else:
        print("downloading " + SOURCE)
        with urllib.request.urlopen(SOURCE, timeout=60) as response:
            topo = json.load(response)

    transform = topo["transform"]
    geometries = topo["objects"]["countries"]["geometries"]
    source_points = sum(len(a) for a in topo["arcs"])

    # The source's own grid, and the one written out. Snapping is done on absolute coordinates so
    # that a point two arcs share lands on the same cell in both - which is what keeps a country's
    # rings closed - and the result is delta-encoded again on the way out.
    scale_x = transform["scale"][0]
    scale_y = transform["scale"][1]
    steps_x = max(1, round(GRID / scale_x))
    steps_y = max(1, round(GRID / scale_y))
    arcs = []
    for arc in topo["arcs"]:
        x = y = 0
        snapped = []
        for dx, dy in arc:
            x += dx
            y += dy
            point = (round(x / steps_x), round(y / steps_y))
            # a point that lands where the last one did adds nothing but bytes
            if not snapped or snapped[-1] != point:
                snapped.append(point)
        # an arc that collapses to a single cell still has to have two ends: rings are walked
        # arc by arc, and one with nothing in it would break the chain
        while len(snapped) < 2:
            snapped.append(snapped[-1])
        arcs.append(snapped)

    payload = bytearray()
    payload += b"RWM1"
    varint(len(arcs), payload)
    for arc in arcs:
        varint(len(arc), payload)
        x = y = 0
        for px, py in arc:  # delta-encoded within the arc; the first point is absolute
            zigzag(px - x, payload)
            zigzag(py - y, payload)
            x, y = px, py

    countries = sorted(geometries, key=lambda g: g["properties"]["name"])
    # the country raster keeps one byte a cell, sea included, so the world has to fit in 255
    assert len(countries) < 255, f"{len(countries)} countries will not fit in a byte"
    varint(len(countries), payload)
    for country in countries:
        name = country["properties"]["name"].encode("utf-8")
        varint(len(name), payload)
        payload += name
        # a Polygon is one list of rings, a MultiPolygon a list of those: both are written as a flat
        # list of rings, since nothing here needs to know which ring belongs to which island
        polygons = country["arcs"] if country["type"] == "MultiPolygon" else [country["arcs"]]
        rings = [ring for polygon in polygons for ring in polygon]
        varint(len(rings), payload)
        for ring in rings:
            varint(len(ring), payload)
            for index in ring:
                zigzag(index, payload)

    encoded = base64.b64encode(bytes(payload)).decode("ascii")
    lines = [encoded[i:i + 120] for i in range(0, len(encoded), 120)]
    points = sum(len(a) for a in arcs)

    with open(os.path.abspath(OUTPUT), "w", encoding="utf-8", newline="\n") as f:
        f.write(HEADER.format(
            source=SOURCE,
            arcs=len(arcs),
            points=points,
            countries=len(countries),
            bytes=len(payload),
            scaleX=repr(scale_x * steps_x),
            scaleY=repr(scale_y * steps_y),
            translateX=repr(transform["translate"][0]),
            translateY=repr(transform["translate"][1]),
            payload="\n".join('  "' + line + '" +' for line in lines).rstrip(" +") + ";",
        ))
        f.write(FOOTER)
    print("wrote " + os.path.normpath(os.path.abspath(OUTPUT)))
    print(f"{len(arcs)} arcs, {points} points of {source_points} in the source, {len(countries)} countries, {len(payload)} bytes ({len(encoded)} base64)")
    return 0


HEADER = '''/**
 * The world as lines: {arcs} arcs of {points} points making up the coastlines and borders of
 * {countries} countries, and which arcs each country is made of.
 *
 * GENERATED - do not edit. tools/generate-world-map.py builds this from Natural Earth's admin 0
 * countries (public domain), by way of the world-atlas TopoJSON build:
 *   {source}
 *
 * The topology of the source is kept rather than flattened into a shape per country: a border
 * between two countries is ONE arc, so a map of lines draws it once instead of laying two identical
 * lines over each other, and the whole world is {bytes} bytes of numbers rather than three quarters
 * of a megabyte of json. Coordinates sit on a grid of about half a kilometre - finer than a line
 * one pixel wide can show at any zoom the map offers - which is what makes them delta-encode into
 * a byte or two a point.
 *
 * Decoding is done once, lazily, on first use: the payload becomes a Float32Array of lon/lat per arc
 * (degrees), and a list of countries holding ring after ring of arc indices, where a NEGATIVE index
 * `i` means arc `~i` walked backwards - TopoJSON's own convention, kept because it is what makes the
 * rings of a country close.
 */

/** The grid the payload's integers are on: degrees = translate + value * scale. */
const scaleX = {scaleX};
const scaleY = {scaleY};
const translateX = {translateX};
const translateY = {translateY};

const payload =
{payload}
'''

FOOTER = '''
/** One country: its name, and the rings it is drawn from, each a list of arc indices (see above). */
export interface WorldCountry {
  name: string;
  rings: number[][];
  /** west, south, east, north in degrees; what a label or a zoom-to-country needs without walking the rings again. */
  bounds: [number, number, number, number];
}

export interface World {
  /** Every arc as interleaved lon/lat degrees: `points[i * 2]`, `points[i * 2 + 1]`. */
  arcs: Float32Array[];
  countries: WorldCountry[];
}

let decoded: World | null = null;

/** The world, decoded on first use and kept. */
export function world(): World {
  if (decoded === null) decoded = decode();
  return decoded;
}

function decode(): World {
  const binary = atob(payload);
  let at = 4; // past the "RWM1" tag
  const uint = () => {
    let value = 0;
    let shift = 1;
    for (;;) {
      const b = binary.charCodeAt(at++);
      value += (b & 0x7f) * shift;
      if ((b & 0x80) === 0) return value;
      shift *= 128;
    }
  };
  // zigzag: the low bit is the sign, so a small negative number is as short as a small positive one
  const sint = () => {
    const value = uint();
    return value % 2 === 1 ? -(value + 1) / 2 : value / 2;
  };

  const arcs: Float32Array[] = [];
  const arcCount = uint();
  for (let a = 0; a < arcCount; a++) {
    const count = uint();
    const points = new Float32Array(count * 2);
    let x = 0;
    let y = 0;
    for (let i = 0; i < count; i++) {
      x += sint(); // the first point of an arc is absolute, the rest are steps from the one before
      y += sint();
      points[i * 2] = translateX + x * scaleX;
      points[i * 2 + 1] = translateY + y * scaleY;
    }
    arcs.push(points);
  }

  const countries: WorldCountry[] = [];
  const countryCount = uint();
  for (let c = 0; c < countryCount; c++) {
    const nameLength = uint();
    // utf-8 out of the byte string: the names carry accents (Côte d'Ivoire, Curaçao)
    const bytes = new Uint8Array(nameLength);
    for (let i = 0; i < nameLength; i++) bytes[i] = binary.charCodeAt(at++);
    const name = utf8.decode(bytes);
    const rings: number[][] = [];
    const ringCount = uint();
    let west = 180;
    let south = 90;
    let east = -180;
    let north = -90;
    for (let r = 0; r < ringCount; r++) {
      const length = uint();
      const ring = new Array<number>(length);
      for (let i = 0; i < length; i++) {
        const index = sint();
        ring[i] = index;
        const points = arcs[index < 0 ? ~index : index];
        for (let p = 0; p < points.length; p += 2) {
          if (points[p] < west) west = points[p];
          if (points[p] > east) east = points[p];
          if (points[p + 1] < south) south = points[p + 1];
          if (points[p + 1] > north) north = points[p + 1];
        }
      }
      rings.push(ring);
    }
    countries.push({ name, rings, bounds: [west, south, east, north] });
  }
  return { arcs, countries };
}

const utf8 = new TextDecoder();

/**
 * One ring as a run of lon/lat degrees, its arcs joined end to end and a negative index walked
 * backwards (see the note at the top). The point an arc shares with the next one is written once.
 */
export function ringPoints(world: World, ring: number[]): Float32Array {
  let total = 0;
  for (const index of ring) total += world.arcs[index < 0 ? ~index : index].length / 2 - 1;
  const out = new Float32Array((total + 1) * 2);
  let at = 0;
  for (const index of ring) {
    const points = world.arcs[index < 0 ? ~index : index];
    const count = points.length / 2;
    // the last point written is the first of the next arc, so every arc but the first skips its own
    const skip = at > 0 ? 1 : 0;
    if (index < 0) {
      for (let i = count - 1 - skip; i >= 0; i--) {
        out[at++] = points[i * 2];
        out[at++] = points[i * 2 + 1];
      }
    } else {
      for (let i = skip; i < count; i++) {
        out[at++] = points[i * 2];
        out[at++] = points[i * 2 + 1];
      }
    }
  }
  return at === out.length ? out : out.subarray(0, at);
}
'''


if __name__ == "__main__":
    raise SystemExit(main())
