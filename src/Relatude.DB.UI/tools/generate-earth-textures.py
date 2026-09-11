#!/usr/bin/env python3
"""Builds src/map/earth.ts - the photographs of the Earth the globe can wear - from NASA imagery.

Two pictures, both public domain (NASA's media usage guidelines; no permission needed, credit given
in the generated file):

  day     Blue Marble Next Generation, December, cloudless - so the globe is not wearing one
          afternoon's weather for ever - with the sea floor under the water and the hills shaded.
  night   Black Marble, the Suomi NPP night lights, which is where PEOPLE are rather than where land
          is, and is what makes the dark side of a turning globe worth looking at. NASA publishes it
          as a COMPOSITE - the lights over a dim blue rendering of land, ocean and ice - and the
          globe adds this picture to itself as light, so that base would flood the whole night side
          blue. It is taken off here (see lightsOnly).

Both are taken from the largest originals NASA publishes (a quarter of a billion pixels for the day
one) and reduced here, rather than from the small copies beside them: the reduction is done once, by
a good filter, and what it saves is a globe that still holds together when somebody zooms into it.

They are written as base64 jpegs inside a module of their own, and that module is IMPORTED ONLY
WHEN SOMEONE TURNS THE PHOTOGRAPH ON. A megabyte of scenery has no business in the bundle everybody
downloads, and a dynamic import is all it takes for the bundler to split it out.

    python tools/generate-earth-textures.py          (run from src/Relatude.DB.UI)

Re-run it only to take new imagery; the output is checked in, so a build never needs the network.
"""

import base64
import io
import os
import sys
import urllib.request

from PIL import Image, ImageChops

# the originals are far past the size Pillow refuses to open unasked, and they are NASA's rather than
# something arriving from a stranger
Image.MAX_IMAGE_PIXELS = None

# Blue Marble Next Generation, Reto Stockli / NASA Earth Observatory; and Black Marble, NASA Earth
# Observatory from Suomi NPP VIIRS. 21600x10800 and 13500x6750 respectively.
SOURCES = {
    "day": "https://eoimages.gsfc.nasa.gov/images/imagerecords/73000/73909/world.topo.bathy.200412.3x21600x10800.jpg",
    "night": "https://eoimages.gsfc.nasa.gov/images/imagerecords/79000/79765/dnb_land_ocean_ice.2012.13500x6750.jpg",
}
# Equirectangular, and twice as wide as it is tall - which is what the globe's shader expects, and
# what both sources already are.
#
# 4096 is the width to stop at, for two reasons that happen to agree. It is the largest texture
# EVERY WebGL2 implementation is required to take, so a globe wearing one of these works everywhere
# rather than nearly everywhere; and doubling again would put four megabytes into the chunk, which is
# more than a picture of the Earth is worth even to somebody who asked for it. At 4096 a globe filling
# the screen has about two texels to the pixel, so it stays sharp through a good deal of zooming in.
WIDTH = 4096
QUALITY = 72
OUTPUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "map", "earth.ts")


def encode(name: str, cached: str | None) -> tuple[str, int]:
    if cached:
        print("reading " + cached)
        raw = open(cached, "rb").read()
    else:
        url = SOURCES[name]
        print("downloading " + url + " (tens of megabytes)")
        with urllib.request.urlopen(url, timeout=900) as response:
            raw = response.read()
    image = Image.open(io.BytesIO(raw))
    print(f"  {name}: {image.size[0]}x{image.size[1]} in")
    # Let the jpeg decoder do the first few halvings itself. It can only scale by powers of two, so
    # this lands somewhere above the width wanted and the filter below does the rest - but it saves
    # decoding a quarter of a billion pixels into memory to throw almost all of them away.
    image.draft("RGB", (WIDTH, WIDTH // 2))
    image = image.convert("RGB").resize((WIDTH, WIDTH // 2), Image.LANCZOS)
    if name == "night":
        image = lightsOnly(image)
    out = io.BytesIO()
    image.save(out, "JPEG", quality=QUALITY, optimize=True)
    data = out.getvalue()
    print(f"  {name}: {WIDTH}x{WIDTH // 2}, {len(data) // 1024} KB")
    return base64.b64encode(data).decode("ascii"), len(data)


# What the composite's base rises to, and what a light starts at, as luminance out of 255. Measured
# from the picture itself: open ocean sits at 5, the brightest of the base (Antarctic ice, the Sahara)
# at 31, and a city is 200 and up. Anything under the first is turned off, anything over the second
# is left exactly as it was, and the gap is faded so the faintest rural glow does not come back as a
# hard edge round every town.
BASE_TOP = 30
LIGHT_FLOOR = 64


def lightsOnly(image: Image.Image) -> Image.Image:
    """The lights with the land, ocean and ice under them taken away.

    Each pixel is scaled by how far ITS OWN brightness is above the base, which leaves the colour of
    a light alone - subtracting the base as a colour instead would take blue out of every city and
    turn the lot of them orange. What is left is lights on black, which is what a picture meant to be
    ADDED to a globe has to be, and which also happens to compress to a third of the size.
    """
    def curve(v: int) -> int:
        t = min(1.0, max(0.0, (v - BASE_TOP) / (LIGHT_FLOOR - BASE_TOP)))
        return round(255 * t * t * (3 - 2 * t))

    mask = image.convert("L").point(curve)
    return ImageChops.multiply(image, Image.merge("RGB", (mask, mask, mask)))


def wrap(name: str, encoded: str) -> str:
    """One image, as an ARRAY of short lines joined back together at load.

    Not as lines ADDED together with +, which is the obvious way to write it and the way this used
    to: a megabyte of base64 is nineteen thousand lines, nineteen thousand additions is a syntax tree
    nineteen thousand deep, and every javascript parser that walks such a tree runs out of stack on
    it - the bundler died on exactly that. A list is flat however long it is.
    """
    lines = [encoded[i:i + 120] for i in range(0, len(encoded), 120)]
    body = "\n".join('    "' + line + '",' for line in lines)
    return f'export const {name} =\n  "data:image/jpeg;base64," +\n  [\n{body}\n  ].join("");\n'


def main() -> int:
    # a local copy of either original can be given on the command line, matched by its file name, so
    # that a re-run does not fetch forty megabytes again
    cache: dict[str, str | None] = {"day": None, "night": None}
    for arg in sys.argv[1:]:
        for name, url in SOURCES.items():
            if os.path.basename(arg).lower() == os.path.basename(url).lower():
                cache[name] = arg
    day, dayBytes = encode("day", cache["day"])
    night, nightBytes = encode("night", cache["night"])
    with open(os.path.abspath(OUTPUT), "w", encoding="utf-8", newline="\n") as f:
        f.write(HEADER.format(width=WIDTH, height=WIDTH // 2, quality=QUALITY, day=SOURCES["day"], night=SOURCES["night"], kb=(dayBytes + nightBytes) // 1024))
        f.write("\n")
        f.write(wrap("dayImage", day))
        f.write("\n")
        f.write(wrap("nightImage", night))
    print("wrote " + os.path.normpath(os.path.abspath(OUTPUT)))
    return 0


HEADER = '''/**
 * The Earth as it looks from space: two {width}x{height} photographs the globe can wear, as base64
 * jpegs ({kb} KB of them).
 *
 * GENERATED - do not edit. tools/generate-earth-textures.py builds this from NASA imagery, which is
 * public domain, reducing the largest originals published rather than taking the small copies:
 *   day    Blue Marble Next Generation, Reto Stockli / NASA Earth Observatory
 *          {day}
 *   night  Black Marble, NASA Earth Observatory / Suomi NPP VIIRS
 *          {night}
 *
 * NOTHING may import this module statically. It is loaded with `await import(...)` the moment
 * somebody asks for the photograph and not one second sooner, so the bundle everybody downloads
 * does not carry a megabyte of scenery nobody has asked to see.
 */
'''


if __name__ == "__main__":
    raise SystemExit(main())
