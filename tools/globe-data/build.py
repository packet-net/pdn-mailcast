#!/usr/bin/env python3
"""Regenerates src/Mailcast.Receiver/Web/globe.json, the coastlines the receiver's status page
draws its globe of where the signal bounced with. Standard library only.

Usage: python3 tools/globe-data/build.py
Needs network access. Takes a few seconds.

Source: Natural Earth (https://www.naturalearthdata.com/), public domain. Two scales: the 1:10m
countries for Britain, Ireland and the near continent, where the paths are and the camera
starts, and the 1:50m land for the rest of the world, seen only from far out. Both are thinned
(a point is kept only once it is far enough from the last one kept), since the page needs a
recognisable coast, not a survey: about 0.025 degrees (under 3 km) near Britain, 0.35 degrees
elsewhere.

Output: {"near": [ring, ...], "world": [ring, ...]}, each ring a list of [longitude, latitude]
pairs, an outer boundary only (lakes and holes are dropped).
"""

import json
import math
import os
import urllib.request

BASE = "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/v5.1.2/geojson/"
NEAR_URL = BASE + "ne_10m_admin_0_countries.geojson"
WORLD_URL = BASE + "ne_50m_land.geojson"

# Britain, Ireland and the near continent: west, south, east, north, degrees.
W, S, E, N = -12.0, 48.0, 6.0, 61.0

OUT = os.path.join(os.path.dirname(__file__), "..", "..", "src", "Mailcast.Receiver", "Web", "globe.json")


def fetch(url):
    with urllib.request.urlopen(url, timeout=120) as response:
        return json.load(response)


def rings(feature):
    geometry = feature["geometry"]
    if geometry["type"] == "Polygon":
        return [geometry["coordinates"][0]]
    return [polygon[0] for polygon in geometry["coordinates"]]


def thin(ring, step_for, digits):
    kept = [ring[0]]
    for x, y in ring[1:]:
        if math.hypot(x - kept[-1][0], y - kept[-1][1]) >= step_for(x, y):
            kept.append([x, y])
    return [[round(x, digits), round(y, digits)] for x, y in kept] if len(kept) > 3 else None


def inside_near(xs, ys):
    return W <= min(xs) and max(xs) <= E and S <= min(ys) and max(ys) <= N


def main():
    near = []
    for feature in fetch(NEAR_URL)["features"]:
        for ring in rings(feature):
            xs, ys = [p[0] for p in ring], [p[1] for p in ring]
            if max(xs) < W or min(xs) > E or max(ys) < S or min(ys) > N:
                continue
            if (max(xs) - min(xs)) * (max(ys) - min(ys)) < 0.004:
                continue
            # Fine inside the box; a country reaching outside it (France, say) is coarse there.
            out = thin(ring, lambda x, y: 0.025 if W <= x <= E and S <= y <= N else 0.5, 3)
            if out:
                near.append(out)

    world = []
    for feature in fetch(WORLD_URL)["features"]:
        for ring in rings(feature):
            xs, ys = [p[0] for p in ring], [p[1] for p in ring]
            # Wholly near Britain: already drawn, in more detail, from the near set.
            if inside_near(xs, ys) or (max(xs) - min(xs)) * (max(ys) - min(ys)) < 0.5:
                continue
            out = thin(ring, lambda x, y: 0.35, 2)
            if out:
                world.append(out)

    with open(OUT, "w", encoding="ascii") as f:
        json.dump({"near": near, "world": world}, f, separators=(",", ":"))
        f.write("\n")
    print(f"{len(near)} near rings, {len(world)} world rings, {os.path.getsize(OUT)} bytes -> {os.path.relpath(OUT)}")


if __name__ == "__main__":
    main()
