#!/usr/bin/env python3
"""Share of GB7RDG's frames each listener heard, from the archived data.

Usage: paper/figures/heard_share.py [repo root]

Frames sent per slot come from the head end's journal ("done, ... N of M frames sent ... on
<mode>"). Frames heard come from the web SDR survey (data/web-sdr-survey-2026-10/slots.csv) and
from listeners' daily reports (reports/data/slots.csv). Prints two tables: each survey SDR's share
by week, and each listener's share and bulletins rebuilt by day. A share is frames heard over
frames sent in the slots that listener was listening to.
"""
import csv
import collections
import os
import re
import sys

root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
J = os.path.join(root, "data/headend-gb7rdg/headend-journal-to-2026-11-07.txt")
SURVEY = os.path.join(root, "data/web-sdr-survey-2026-10/slots.csv")
REPORTS = os.path.join(root, "reports/data/slots.csv")
REPORT_DAYS = os.path.join(root, "reports/data/reports.csv")

# Frames sent per slot, the last "done" line for each slot winning (a slot run again replaces it).
sent = {}
done = re.compile(r"slot (\d{4}-\d\d-\d\d) (\d\d:\d\d)Z: done, .*?(\d+) of \d+ frames sent .*? on (ms110d-\S+?),")
with open(J, encoding="latin-1") as f:
    for line in f:
        m = done.search(line)
        if m:
            sent[(m.group(1), m.group(2))] = (int(m.group(3)), m.group(4))

heard = collections.defaultdict(lambda: collections.Counter())  # (listener, day) -> frames, bulletins
listened = collections.defaultdict(set)
with open(SURVEY, newline="") as f:
    for r in csv.DictReader(f):
        if r["listening"] != "yes":
            continue
        name = "M9PSY" if r["sdr"].startswith("M9PSY") else r["sdr"]
        day, hhmm = r["slot_utc"].split()
        heard[(name, day)]["frames"] += int(r["frames_heard"] or 0)
        heard[(name, day)]["bulletins"] += int(r["bulletins_completed"] or 0)
        listened[(name, day)].add(hhmm)
survey_names = sorted({k[0] for k in heard})

with open(REPORTS, newline="") as f:
    for r in csv.DictReader(f):
        if r["station"] not in ("", "M0LTE", "GB7RDG"):
            continue
        heard[(r["callsign"], r["day"])]["frames"] += int(r["frames"] or 0)
        listened[(r["callsign"], r["day"])].add(r["slot_utc"])
with open(REPORT_DAYS, newline="") as f:
    for r in csv.DictReader(f):
        heard[(r["callsign"], r["day"])]["bulletins"] = int(r["rebuilt"] or 0)

# Frames heard over frames sent in the slots that listener was listening to: a slot it was not
# listening to (an SDR that was down, a receiver stopped) counts for neither.
def share(name, days):
    s = sum(sent.get((d, hhmm), (0, ""))[0] for d in days for hhmm in listened.get((name, d), ()))
    h = sum(heard[(name, d)]["frames"] for d in days if (name, d) in heard)
    return f"{100 * h / s:.0f}%" if s else "-"

weeks = [("6 to 12 Oct", [f"2026-10-{d:02d}" for d in range(6, 13)]), ("13 to 20 Oct", [f"2026-10-{d:02d}" for d in range(13, 21)])]
print("| SDR | " + " | ".join(w for w, _ in weeks) + " |")
print("|---|" + "---|" * len(weeks))
for n in survey_names:
    print(f"| {n} | " + " | ".join(share(n, days) for _, days in weeks) + " |")

days = [f"2026-10-{d:02d}" for d in range(16, 23)]
names = sorted({k[0] for k in heard if k[1] in days})
print()
print("| Listener | " + " | ".join(d[5:] for d in days) + " |")
print("|---|" + "---|" * len(days))
for n in names:
    cells = []
    for d in days:
        c = heard.get((n, d))
        cells.append(f"{share(n, [d])}, {c['bulletins']}" if c is not None and (n, d) in listened else "-")
    print(f"| {n} | " + " | ".join(cells) + " |")
