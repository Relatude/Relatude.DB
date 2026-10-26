#!/usr/bin/env python3
"""Builds the icon library the server carries - NodeServer/Icons in Relatude.DB.NodeServer - from Iconify's
packages of open icon sets.

A node type can be given an icon (NodeTypeModel.Icon, [Node(Icon = ...)]): an SVG of its own, or a named icon
of this library, written "set:name" the way Iconify names them ("lucide:house"). The admin UI's datamodel
editor has a picker over the library, and the server turns a name into its SVG for whoever shows the type
(IconLibrary.ToSvg - the CMS's API does, for its interface).

The sets, all under open licences, three monochrome (drawn in the colour of the text around them) and four in
colour:

  lucide              Lucide                         ISC           monochrome
  tabler              Tabler Icons                   MIT           monochrome
  mdi                 Material Design Icons          Apache-2.0    monochrome
  fluent-color        Fluent UI System Color Icons   MIT           colour
  flat-color-icons    Flat Color Icons               MIT           colour
  icon-park           IconPark                       Apache-2.0    colour
  fluent-emoji-flat   Fluent Emoji Flat              MIT           colour

Each set is written as {prefix}.json.gz in Iconify's JSON format, cut down to what the picker and the server
use: the icons, the aliases (other names for an icon, which a search finds too) and the categories some sets
have. An icon a set keeps only so an old name still works is kept, marked hidden, so a type that chose it goes
on showing it. Fluent's colour icons come in up to six sizes each, drawn apart; one is kept per icon, the 24
pixel one where there is one. index.json lists the sets, with a version per file the server hands the browser
to cache it by, and LICENSES.md holds each set's licence, which goes wherever the icons go.

    python tools/generate-icon-library.py               (run from src/Relatude.DB.UI)
    python tools/generate-icon-library.py downloads     (from a folder holding the .tgz files already)

Re-run it only to take newer versions of the sets (the versions are pinned in SETS); the output is checked in,
so a build never needs the network.
"""

import gzip
import hashlib
import io
import json
import os
import re
import sys
import tarfile
import urllib.request

# prefix, pinned version of @iconify-json/{prefix}, whether the icons carry colours of their own
SETS = [
    ("lucide", "1.2.147", False),
    ("tabler", "1.2.42", False),
    ("mdi", "1.2.3", False),
    ("fluent-color", "1.2.21", True),
    ("flat-color-icons", "1.2.3", True),
    ("icon-park", "1.2.4", True),
    ("fluent-emoji-flat", "1.2.6", True),
]

# where a set's licence text is, for one whose package names no licence page on GitHub
LICENCE_TEXT = {
    "flat-color-icons": "https://raw.githubusercontent.com/icons8/flat-color-icons/master/LICENSE.md",
}

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "..", "Relatude.DB.NodeServer", "NodeServer", "Icons"))

# Fluent's sizes, best first: the icon is shown at 16 to 32 pixels
SIZE_PREFERENCE = ["24", "20", "28", "32", "48", "16"]
SIZED = re.compile(r"^(.*)-(\d+)$")
ICON_KEYS = ("body", "width", "height", "left", "top", "hidden")
ALIAS_KEYS = ("parent", "width", "height", "left", "top", "rotate", "hFlip", "vFlip", "hidden")


def tarball(prefix, version, folder):
    name = "iconify-json-%s-%s.tgz" % (prefix, version)
    if folder:
        with open(os.path.join(folder, name), "rb") as f:
            return f.read()
    url = "https://registry.npmjs.org/@iconify-json/%s/-/%s-%s.tgz" % (prefix, prefix, version)
    print("  downloading " + url)
    with urllib.request.urlopen(url) as response:
        return response.read()


def licence_text(prefix, url):
    """The licence's own words, from the set's repository: the packages hold only a link to them."""
    raw = LICENCE_TEXT.get(prefix)
    m = re.match(r"^https://github\.com/([^/]+)/([^/]+)/blob/(.+)$", url or "")
    if raw is None and m:
        raw = "https://raw.githubusercontent.com/%s/%s/%s" % m.groups()
    if raw is None:
        return ""
    try:
        with urllib.request.urlopen(raw) as response:
            return response.read().decode("utf-8", "replace").strip()
    except Exception as error:  # the link is written all the same
        print("  could not read the licence at %s: %s" % (raw, error))
        return ""


def read_package(data):
    files = {}
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as tar:
        for member in tar.getmembers():
            if not member.isfile():
                continue
            base = member.name.split("/", 1)[-1]  # package/icons.json -> icons.json
            if base in ("icons.json", "info.json", "metadata.json"):
                files[base] = tar.extractfile(member).read()
    return files


def one_size_each(icons):
    """Fluent's colour set: of calendar-16 ... calendar-48 keep the one that suits an icon of 16-32 pixels."""
    groups = {}
    for name in icons:
        m = SIZED.match(name)
        base, size = (m.group(1), m.group(2)) if m else (name, "")
        groups.setdefault(base, {})[size] = name
    kept = set()
    for sizes in groups.values():
        best = next((sizes[s] for s in SIZE_PREFERENCE if s in sizes), None) or next(iter(sizes.values()))
        kept.add(best)
    return kept


def slim(prefix, files, colour):
    source = json.loads(files["icons.json"])
    info = json.loads(files["info.json"]) if "info.json" in files else source.get("info", {})
    metadata = json.loads(files["metadata.json"]) if "metadata.json" in files else {}
    icons = source["icons"]
    names = one_size_each(icons) if prefix == "fluent-color" else set(icons)
    out = {"prefix": prefix}
    for key in ("width", "height", "left", "top"):
        if key in source:
            out[key] = source[key]
    out["icons"] = {}
    for name in sorted(names):
        icon = icons[name]
        out["icons"][name] = {k: icon[k] for k in ICON_KEYS if k in icon}

    # an alias is kept when the icon it ends at is, so every name it gives can be shown
    aliases = source.get("aliases", {})

    def ends_at(name, depth=0):
        if name in out["icons"]:
            return True
        alias = aliases.get(name)
        return alias is not None and depth < 8 and ends_at(alias["parent"], depth + 1)

    kept_aliases = {n: {k: a[k] for k in ALIAS_KEYS if k in a} for n, a in sorted(aliases.items()) if ends_at(a["parent"])}
    if kept_aliases:
        out["aliases"] = kept_aliases

    categories = {}
    for category, members in (metadata.get("categories") or {}).items():
        present = [m for m in members if m in out["icons"] and not out["icons"][m].get("hidden")]
        if present:
            categories[category] = present
    if categories:
        out["categories"] = categories

    visible = sum(1 for i in out["icons"].values() if not i.get("hidden"))
    author = info.get("author") or {}
    licence = info.get("license") or {}
    entry = {
        "prefix": prefix,
        "name": info.get("name", prefix),
        "colour": colour,
        "total": visible,
        "author": author.get("name"),
        "authorUrl": author.get("url"),
        "license": licence.get("spdx") or licence.get("title"),
        "licenseUrl": licence.get("url"),
        "samples": [s for s in (info.get("samples") or []) if s in out["icons"]][:6],
    }
    return out, entry


def write_gz(path, data):
    # mtime 0 and no file name: the same icons give the same bytes, so an unchanged set is no diff
    buffer = io.BytesIO()
    with gzip.GzipFile(filename="", mode="wb", fileobj=buffer, compresslevel=9, mtime=0) as gz:
        gz.write(data)
    with open(path, "wb") as f:
        f.write(buffer.getvalue())
    return buffer.getvalue()


def main():
    folder = sys.argv[1] if len(sys.argv) > 1 else None
    os.makedirs(OUT, exist_ok=True)
    index = []
    licences = [
        "# Icon library licences\n",
        "\nThe icons in this folder come from the open icon sets below, through Iconify's packages of them"
        " (https://iconify.design). Each set's licence goes with its icons. Generated by"
        " src/Relatude.DB.UI/tools/generate-icon-library.py.\n",
    ]
    for prefix, version, colour in SETS:
        print(prefix + " " + version)
        files = read_package(tarball(prefix, version, folder))
        data, entry = slim(prefix, files, colour)
        raw = json.dumps(data, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        written = write_gz(os.path.join(OUT, prefix + ".json.gz"), raw)
        entry["version"] = hashlib.sha256(written).hexdigest()[:16]
        entry["package"] = "@iconify-json/%s@%s" % (prefix, version)
        index.append(entry)
        print("  %d icons, %d KB" % (entry["total"], len(written) // 1024))
        licences.append("\n## %s (%s)\n\nBy %s, under the %s licence%s. From %s.\n" % (
            entry["name"], prefix, entry["author"] or "its authors", entry["license"] or "set's",
            ": " + entry["licenseUrl"] if entry["licenseUrl"] else "", entry["package"]))
        text = licence_text(prefix, entry["licenseUrl"])
        if text:
            licences.append("\n```text\n%s\n```\n" % text)
    with open(os.path.join(OUT, "index.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({"sets": index}, f, ensure_ascii=False, indent=2)
        f.write("\n")
    with open(os.path.join(OUT, "LICENSES.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write("".join(licences))
    print("written to " + OUT)


if __name__ == "__main__":
    main()
