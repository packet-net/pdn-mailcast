#!/usr/bin/env python3
"""Regenerates the three embedded data files the receiver's Path picture uses to label ground
bounces (issue #89): towns, sea names and shipping forecast areas. Writes gzip-compressed text
straight into src/Mailcast.Receiver/Data/, which is embedded into the receiver as a resource.

Only one dependency beyond the standard library: shapely, for clipping and simplifying the sea
area polygons (pip install shapely).

Usage: python3 tools/ground-bounce-data/build.py
Needs network access for the towns and sea areas (not the shipping forecast areas, which are
a small literal table transcribed from a PDF, below). Takes well under a minute.

Data sources and licences (credited again in docs/receiver.md and in the files' own header
comments, as each licence requires):

- Towns: GeoNames (https://www.geonames.org/), cities5000.zip, CC BY 4.0
  (https://creativecommons.org/licenses/by/4.0/). Filtered to the region this receiver cares
  about (roughly Britain, Ireland and nearby Europe, 45 to 62 N, 15 W to 10 E) to keep the
  embedded file small.

- Sea names: Flanders Marine Institute (2018). IHO Sea Areas, version 3.
  https://doi.org/10.14284/323, CC BY 4.0. Fetched from Marine Regions' WFS
  (https://www.marineregions.org/), clipped to the same region and drastically simplified: this
  is only ever used to name roughly where a ground bounce estimate (good to a few tens of km)
  falls, not to trace a coastline, so small islands and fine detail are dropped on purpose.

- Shipping forecast areas: Met Office, National Meteorological Library and Archive, Factsheet 8
  "The Shipping Forecast" (2025), Table 1 "Co-ordinates of the sea areas used in the shipping
  forecast":
  https://www.metoffice.gov.uk/binaries/content/assets/metofficegovuk/pdf/research/library-and-archive/library/publications/factsheets/factsheet_8_shipping_forecast_2025.pdf
  Crown copyright, under the Open Government Licence v3.0
  (https://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/): the Library and
  Archive's own page (https://www.metoffice.gov.uk/research/library-and-archive/library/charges-copyright)
  confirms its own material, which this factsheet is, is OGL licensed. There is no API or
  machine-readable download for this table, so the 31 areas' corner coordinates below were
  transcribed once, by reconstructing the PDF's three-column table from each word's position on
  the page (pdfplumber), and checked by confirming every area's points form a simple (non
  self-intersecting) polygon. They are a straight transcription of Table 1, not a boundary
  drawn independently, so they stay exactly as published (degrees and minutes converted to
  decimal degrees; nothing simplified, moved or re-ordered).
"""
import gzip
import io
import json
import re
import urllib.request
import zipfile
from pathlib import Path

from shapely.geometry import Polygon, box, shape
from shapely.ops import unary_union
from shapely import simplify

REPO_ROOT = Path(__file__).resolve().parents[2]
DATA_DIR = REPO_ROOT / "src" / "Mailcast.Receiver" / "Data"

# Roughly Britain, Ireland and nearby Europe: wide enough to cover every receiver and GB7RDG
# path this feature is meant for, without embedding the whole world.
MIN_LAT, MAX_LAT = 45.0, 62.0
MIN_LON, MAX_LON = -15.0, 10.0


def fetch(url: str) -> bytes:
    with urllib.request.urlopen(url, timeout=60) as resp:  # noqa: S310 (fixed https URLs below)
        return resp.read()


def write_gz(name: str, text: str) -> None:
    path = DATA_DIR / name
    path.write_bytes(gzip.compress(text.encode("utf-8"), 9))
    print(f"{name}: {len(text)} bytes raw, {path.stat().st_size} bytes gzipped")


# --- Towns (GeoNames) -------------------------------------------------------------------------

def build_towns() -> None:
    raw = fetch("https://download.geonames.org/export/dump/cities5000.zip")
    with zipfile.ZipFile(io.BytesIO(raw)) as zf:
        lines = zf.read("cities5000.txt").decode("utf-8").splitlines()
    rows = []
    for line in lines:
        f = line.split("\t")
        # geonameid, name, asciiname, alternatenames, latitude, longitude, ..., population, ...
        asciiname, lat, lon, population = f[2], float(f[4]), float(f[5]), int(f[14])
        if MIN_LAT <= lat <= MAX_LAT and MIN_LON <= lon <= MAX_LON:
            rows.append((asciiname, lat, lon, population))
    rows.sort(key=lambda r: (-r[3], r[0]))
    header = "# GeoNames cities5000, CC BY 4.0 (https://www.geonames.org/), filtered to " \
        f"{MIN_LAT}-{MAX_LAT} N, {MIN_LON}-{MAX_LON} E. name\\tlat\\tlon\\tpopulation\n"
    body = "\n".join(f"{n}\t{la}\t{lo}\t{p}" for n, la, lo, p in rows)
    write_gz("towns.txt.gz", header + body + "\n")


# --- Sea names (Marine Regions IHO Sea Areas) --------------------------------------------------

# The IHO seas that overlap our region; the WFS query itself returns some seas elsewhere in the
# world too (their bounding boxes happen to overlap ours), which are dropped here.
KEPT_SEAS = [
    "Bristol Channel", "Irish Sea and St. George's Channel",
    "Inner Seas off the West Coast of Scotland", "Celtic Sea", "English Channel", "North Sea",
    "Bay of Biscay", "North Atlantic Ocean", "Norwegian Sea", "Kattegat", "Skagerrak",
    "Baltic Sea",
]

# A shorter, more natural name for the page; everything else keeps its official IHO name.
SEA_DISPLAY_NAMES = {"Irish Sea and St. George's Channel": "Irish Sea"}


def polys_of(geom):
    if geom.geom_type == "Polygon":
        return [geom]
    if geom.geom_type in ("MultiPolygon", "GeometryCollection"):
        out = []
        for g in geom.geoms:
            out.extend(polys_of(g))
        return out
    return []


def build_seas() -> None:
    url = (
        "https://geo.vliz.be/geoserver/wfs?service=WFS&version=1.1.0&request=GetFeature"
        "&typeName=MarineRegions:iho&outputFormat=application/json&srsName=EPSG:4326"
        f"&bbox={MIN_LON},{MIN_LAT},{MAX_LON},{MAX_LAT},EPSG:4326"
    )
    data = json.loads(fetch(url))
    bbox = box(MIN_LON, MIN_LAT, MAX_LON, MAX_LAT)
    lines = [
        "# Flanders Marine Institute (2018). IHO Sea Areas, version 3. "
        "https://doi.org/10.14284/323, CC BY 4.0. Clipped and heavily simplified.",
        "# name|part;part;...  each part is lon,lat;lon,lat;...",
    ]
    by_name = {f["properties"]["name"]: f for f in data["features"]}
    for name in KEPT_SEAS:
        feature = by_name.get(name)
        if feature is None:
            raise SystemExit(f"expected sea area {name!r} not found in the WFS response")
        geom = shape(feature["geometry"])
        clipped = geom.intersection(bbox)
        # Drop holes (small islands) and any fragment under 2% of the largest part's area: this
        # is only used to name roughly where a bounce point sits, not to trace a coastline, and
        # the detailed coastline is most of the point count (and almost all of the file size).
        parts = [Polygon(p.exterior) for p in polys_of(clipped)]
        if not parts:
            raise SystemExit(f"{name!r} does not overlap the region after clipping")
        max_area = max(p.area for p in parts)
        kept = [p for p in parts if p.area >= max_area * 0.02]
        # 0.1 degrees (roughly 10 km) was tried first and was small enough, but straightened
        # across bays enough to put real coastal towns (Aberystwyth among them) on the sea side
        # of the simplified coastline; 0.05 keeps them on land and is still a tenth the size of
        # the unsimplified clip.
        simplified = simplify(unary_union(kept), tolerance=0.05, preserve_topology=True)
        display = SEA_DISPLAY_NAMES.get(name, name)
        out_parts = []
        for p in polys_of(simplified) if simplified.geom_type != "Polygon" else [simplified]:
            pts = ";".join(f"{round(x, 3)},{round(y, 3)}" for x, y in p.exterior.coords)
            out_parts.append(pts)
        lines.append(f"{display}|{'#'.join(out_parts)}")
    write_gz("sea-areas.txt.gz", "\n".join(lines) + "\n")


# --- Shipping forecast areas (Met Office Factsheet 8, Table 1) --------------------------------

# Transcribed once from the PDF (see the module docstring); (degrees, minutes, N/S, degrees,
# minutes, E/W) per corner, in the order Table 1 gives them, one area at a time.
SHIPPING_FORECAST_AREAS_DMS: dict[str, list[tuple[int, int, str, int, int, str]]] = {
    "Viking": [(61, 0, "N", 0, 0, "W"), (61, 0, "N", 4, 0, "E"), (58, 30, "N", 4, 0, "E"), (58, 30, "N", 0, 0, "W")],
    "North Utsire": [(61, 0, "N", 4, 0, "E"), (61, 0, "N", 5, 0, "E"), (59, 0, "N", 5, 35, "E"), (59, 0, "N", 4, 0, "E")],
    "South Utsire": [(59, 0, "N", 4, 0, "E"), (59, 0, "N", 5, 35, "E"), (58, 0, "N", 7, 5, "E"), (57, 45, "N", 7, 30, "E"), (57, 45, "N", 4, 0, "E")],
    "Forties": [(58, 30, "N", 1, 0, "W"), (58, 30, "N", 4, 0, "E"), (56, 0, "N", 4, 0, "E"), (56, 0, "N", 1, 0, "W")],
    "Cromarty": [(57, 0, "N", 2, 10, "W"), (57, 0, "N", 1, 0, "W"), (58, 30, "N", 1, 0, "W"), (58, 30, "N", 3, 0, "W")],
    "Forth": [(55, 40, "N", 1, 50, "W"), (56, 0, "N", 1, 0, "W"), (57, 0, "N", 1, 0, "W"), (57, 0, "N", 2, 10, "W")],
    "Tyne": [(54, 15, "N", 0, 20, "W"), (54, 15, "N", 0, 45, "E"), (56, 0, "N", 1, 0, "W"), (55, 40, "N", 1, 50, "W")],
    "Dogger": [(56, 0, "N", 1, 0, "W"), (54, 15, "N", 0, 45, "E"), (54, 15, "N", 4, 0, "E"), (56, 0, "N", 4, 0, "E")],
    "Fisher": [(57, 45, "N", 4, 0, "E"), (56, 0, "N", 4, 0, "E"), (56, 0, "N", 8, 10, "E"), (57, 5, "N", 8, 35, "E"), (57, 45, "N", 7, 30, "E")],
    "German Bight": [(56, 0, "N", 8, 10, "E"), (56, 0, "N", 4, 0, "E"), (54, 15, "N", 4, 0, "E"), (53, 35, "N", 4, 40, "E"), (52, 45, "N", 4, 40, "E")],
    "Humber": [(52, 45, "N", 1, 40, "E"), (52, 45, "N", 4, 40, "E"), (53, 35, "N", 4, 40, "E"), (54, 15, "N", 4, 0, "E"), (54, 15, "N", 0, 20, "W")],
    "Thames": [(51, 15, "N", 1, 25, "E"), (51, 15, "N", 2, 55, "E"), (52, 45, "N", 4, 40, "E"), (52, 45, "N", 1, 40, "E")],
    "Dover": [(50, 45, "N", 0, 15, "E"), (50, 15, "N", 1, 30, "E"), (51, 15, "N", 2, 55, "E"), (51, 15, "N", 1, 25, "E")],
    "Wight": [(50, 35, "N", 1, 55, "W"), (49, 45, "N", 1, 55, "W"), (50, 15, "N", 1, 30, "E"), (50, 45, "N", 0, 15, "E")],
    "Portland": [(50, 25, "N", 3, 30, "W"), (48, 50, "N", 3, 30, "W"), (49, 45, "N", 1, 55, "W"), (50, 35, "N", 1, 55, "W")],
    "Plymouth": [(50, 5, "N", 5, 45, "W"), (50, 0, "N", 6, 15, "W"), (48, 27, "N", 6, 15, "W"), (48, 27, "N", 4, 45, "W"), (48, 50, "N", 3, 30, "W"), (50, 25, "N", 3, 30, "W")],
    "Biscay": [(48, 27, "N", 6, 15, "W"), (43, 35, "N", 6, 15, "W"), (48, 27, "N", 4, 45, "W")],
    "FitzRoy": [(48, 27, "N", 15, 0, "W"), (41, 0, "N", 15, 0, "W"), (41, 0, "N", 8, 40, "W"), (43, 35, "N", 6, 15, "W"), (48, 27, "N", 6, 15, "W")],
    "Trafalgar": [(35, 0, "N", 15, 0, "W"), (35, 0, "N", 6, 15, "W"), (41, 0, "N", 8, 40, "W"), (41, 0, "N", 15, 0, "W")],
    "Sole": [(50, 0, "N", 6, 15, "W"), (50, 0, "N", 15, 0, "W"), (48, 27, "N", 15, 0, "W"), (48, 27, "N", 6, 15, "W")],
    "Lundy": [(52, 30, "N", 6, 15, "W"), (50, 0, "N", 6, 15, "W"), (50, 5, "N", 5, 45, "W"), (52, 0, "N", 5, 5, "W")],
    "Fastnet": [(51, 35, "N", 10, 0, "W"), (50, 0, "N", 10, 0, "W"), (50, 0, "N", 6, 15, "W"), (52, 30, "N", 6, 15, "W")],
    "Irish Sea": [(54, 50, "N", 5, 5, "W"), (54, 45, "N", 5, 45, "W"), (52, 30, "N", 6, 15, "W"), (52, 0, "N", 5, 5, "W")],
    "Shannon": [(53, 30, "N", 15, 0, "W"), (50, 0, "N", 15, 0, "W"), (50, 0, "N", 10, 0, "W"), (51, 35, "N", 10, 0, "W"), (53, 30, "N", 10, 5, "W")],
    "Rockall": [(58, 0, "N", 10, 0, "W"), (58, 0, "N", 15, 0, "W"), (53, 30, "N", 15, 0, "W"), (53, 30, "N", 10, 5, "W"), (54, 20, "N", 10, 0, "W")],
    "Malin": [(57, 0, "N", 5, 50, "W"), (57, 0, "N", 10, 0, "W"), (54, 20, "N", 10, 0, "W"), (54, 45, "N", 5, 45, "W"), (54, 50, "N", 5, 5, "W")],
    "Hebrides": [(60, 35, "N", 10, 0, "W"), (57, 0, "N", 10, 0, "W"), (57, 0, "N", 5, 50, "W"), (58, 40, "N", 5, 0, "W")],
    "Bailey": [(62, 25, "N", 15, 0, "W"), (58, 0, "N", 15, 0, "W"), (58, 0, "N", 10, 0, "W"), (60, 35, "N", 10, 0, "W")],
    "Fair Isle": [(61, 50, "N", 2, 30, "W"), (59, 30, "N", 7, 15, "W"), (58, 40, "N", 5, 0, "W"), (58, 30, "N", 3, 0, "W"), (58, 30, "N", 0, 0, "W"), (61, 0, "N", 0, 0, "W")],
    "Faeroes": [(63, 20, "N", 7, 30, "W"), (61, 10, "N", 11, 30, "W"), (59, 30, "N", 7, 15, "W"), (61, 50, "N", 2, 30, "W")],
    "Southeast Iceland": [(63, 35, "N", 18, 0, "W"), (61, 10, "N", 11, 30, "W"), (63, 20, "N", 7, 30, "W"), (65, 0, "N", 13, 35, "W")],
}


def dms_to_decimal(deg: int, minute: int, hemisphere: str) -> float:
    value = deg + (minute / 60)
    return -value if hemisphere in ("S", "W") else value


def build_shipping_forecast_areas() -> None:
    lines = [
        "# Met Office, National Meteorological Library and Archive, Factsheet 8 'The Shipping "
        "Forecast' (2025), Table 1. Crown copyright, Open Government Licence v3.0.",
        "# name|lon,lat;lon,lat;...",
    ]
    for name, corners in SHIPPING_FORECAST_AREAS_DMS.items():
        points = []
        for lat_d, lat_m, ns, lon_d, lon_m, ew in corners:
            lat = dms_to_decimal(lat_d, lat_m, ns)
            lon = dms_to_decimal(lon_d, lon_m, ew)
            points.append((lon, lat))
        poly = Polygon(points)
        if not poly.is_valid:
            raise SystemExit(f"{name!r} is not a simple polygon: check the transcription")
        pts = ";".join(f"{round(x, 4)},{round(y, 4)}" for x, y in points)
        lines.append(f"{name}|{pts}")
    write_gz("shipping-forecast-areas.txt.gz", "\n".join(lines) + "\n")


if __name__ == "__main__":
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    build_towns()
    build_seas()
    build_shipping_forecast_areas()
