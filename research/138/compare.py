#!/usr/bin/env python3
"""Compare the same bulletin as different BBSes hold it (issue #138).

Usage: compare.py [corpus dir]  (default: corpus/ beside this script)
Groups corpus/<STATION>/msg-*.txt by Bid, splits each copy into its BBS header (From, To,
Type/Status, Date/Time, Bid, Title), its R: routing lines and its body, and reports for every
Bid held by two or more stations whether the bodies are identical, identical after trivial
normalisation, or different, and how. Writes report.md beside the corpus.
"""
import collections
import difflib
import os
import re
import sys

HEADER = re.compile(r"^(From|To|Type/Status|Date/Time|Bid|Title|Body|Path): ?(.*)$")
# Shown only to some users (e.g. a "Body: size" line, the full route on From:): not part of the bulletin.
PER_USER = {"Type/Status", "Body", "Path", "Date/Time"}
ROUTE = re.compile(r"^R:\d{6}/\d{4}[Zz]? ")


def parse(path):
    lines = open(path, encoding="latin-1").read().split("\n")
    head, routes, i = {}, [], 0
    # The BBS header is a block of field lines; LinBPQ may follow it with a blank line and a
    # "Body: size" line. Anything after that is the bulletin, even if it looks like a header.
    while i < len(lines) and (m := HEADER.match(lines[i])):
        head[m.group(1)] = m.group(2)
        i += 1
    j = i
    while j < len(lines) and lines[j] == "":
        j += 1
    if j < len(lines) and lines[j].startswith("Body: "):
        head["Body"] = lines[j][6:]
        i = j + 1
    while i < len(lines) and lines[i] == "":
        i += 1
    # FBB wraps a long Path onto indented continuation lines ("      !NC8Q!").
    while "Path" in head and i < len(lines) and re.match(r"^\s+![A-Z0-9!.#-]+!$", lines[i]):
        i += 1
    while i < len(lines) and ROUTE.match(lines[i]):
        routes.append(lines[i])
        i += 1
    body = "\n".join(lines[i:])
    return head, routes, body


def trivial(body):
    lines = [l.rstrip() for l in body.split("\n")]
    while lines and lines[0] == "":
        lines.pop(0)
    while lines and lines[-1] == "":
        lines.pop()
    return "\n".join(lines)


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "corpus")
    copies = collections.defaultdict(dict)
    for station in sorted(os.listdir(root)):
        d = os.path.join(root, station)
        if not os.path.isdir(d):
            continue
        for name in os.listdir(d):
            if name.startswith("msg-"):
                head, routes, body = parse(os.path.join(d, name))
                if bid := head.get("Bid"):
                    copies[bid][station] = (head, routes, body, name)
    shared = {b: c for b, c in copies.items() if len(c) > 1}
    counts = collections.Counter()
    notes = []
    for bid in sorted(shared):
        c = shared[bid]
        stations = sorted(c)
        bodies = {s: c[s][2] for s in stations}
        heads = {s: {k: (v.split("@")[0] if k == "From" else v) for k, v in c[s][0].items() if k not in PER_USER} for s in stations}
        same_head = len({tuple(sorted(h.items())) for h in heads.values()}) == 1
        if len(set(bodies.values())) == 1:
            kind = "identical"
        elif len({trivial(b) for b in bodies.values()}) == 1:
            kind = "identical after trimming trailing spaces and blank edges"
        else:
            kind = "different"
        counts[kind] += 1
        if not same_head:
            counts["header fields differ"] += 1
        if kind != "identical" or not same_head:
            a, b = stations[0], stations[1]
            diff = list(difflib.unified_diff(bodies[a].split("\n"), bodies[b].split("\n"), a, b, n=0, lineterm=""))[:12]
            hd = [f"{k}: {heads[a].get(k)!r} vs {heads[b].get(k)!r}" for k in sorted(set(heads[a]) | set(heads[b])) if heads[a].get(k) != heads[b].get(k)]
            notes.append((bid, stations, kind, hd, diff))
    out = [f"# Bulletin copies compared ({', '.join(sorted(os.listdir(root)))})", "",
           f"Bids held by two or more stations: {len(shared)}", ""]
    out += ["| Body comparison | Bids |", "| --- | --- |"]
    out += [f"| {k} | {v} |" for k, v in counts.most_common()]
    out += ["", "R: routing lines are left out of the body before comparing; Type/Status and Body: are left out of the header comparison, and From: is compared without its @route (those are display settings per user, not the bulletin).", ""]
    for bid, stations, kind, hd, diff in notes:
        out += [f"## {bid} ({', '.join(stations)}): {kind}", ""]
        out += [f"- header {h}" for h in hd]
        if diff:
            out += ["", "```diff", *diff, "```"]
        out += [""]
    report = os.path.join(os.path.dirname(root), "report.md")
    open(report, "w").write("\n".join(out) + "\n")
    print("\n".join(out[:12]))


if __name__ == "__main__":
    main()
