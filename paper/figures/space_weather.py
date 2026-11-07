#!/usr/bin/env python3
"""Space weather, the ionosonde and Mailcast reception, 5 October to 7 November 2026.

Usage: paper/figures/space_weather.py [repo root] > /dev/null
       (prints the tables quoted in paper/notes/space-weather.md and writes the charts
       paper/figures/plots/space-weather-*.png and .svg)

Needs numpy and matplotlib (python3 -m venv v && v/bin/pip install numpy matplotlib).

Reception definitions are exactly those of paper/figures/results.py: a share is frames heard over
frames sent in the slots a receiver listened to; slots in which the head end sent nothing are left
out; a listener's report-days with an AUD error and no slot heard are left out; a listener's days on
a public web SDR (wessex.zapto.org, nope.tunnel.ubersdr.org) are kept apart as "CALL via SDR".
Frames sent per slot come from the journal's "done" or "ABORTED" line.

Space weather and ionosonde data are the raw files in data/space-weather (see its README).
"""
import collections
import csv
import datetime as dt
import glob
import gzip
import json
import math
import os
import random
import re
import statistics
import sys

import numpy as np

root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
J = os.path.join(root, "data/headend-gb7rdg/headend-journal-to-2026-11-07.txt")
SURVEY = os.path.join(root, "data/web-sdr-survey-2026-10/slots.csv")
R_SLOTS = os.path.join(root, "reports/data/slots.csv")
R_DAYS = os.path.join(root, "reports/data/reports.csv")
SW = os.path.join(root, "data/space-weather")
PLOTS = os.path.join(root, "paper/figures/plots")
CHANGE = "2026-11-02"  # long interleaver, 31 s bursts and the bulk tier began on this day
F_TX = 7.0538
random.seed(20261107)


def med(xs):
    return statistics.median(xs) if xs else None


def pct(a, b):
    return f"{100 * a / b:.0f}%" if b else "-"


def fmt(x, nd=0):
    return "-" if x is None or (isinstance(x, float) and math.isnan(x)) else f"{x:.{nd}f}"


# ---------------------------------------------------------------------------------------------
# Statistics helpers (no scipy: ranks and Spearman by hand)
# ---------------------------------------------------------------------------------------------
def ranks(xs):
    order = sorted(range(len(xs)), key=lambda i: xs[i])
    r = [0.0] * len(xs)
    i = 0
    while i < len(order):
        j = i
        while j + 1 < len(order) and xs[order[j + 1]] == xs[order[i]]:
            j += 1
        for k in range(i, j + 1):
            r[order[k]] = (i + j) / 2 + 1
        i = j + 1
    return r


def spearman(x, y):
    """Spearman's rho, n, and an approximate 95% interval (Fisher z with the Bonett-Wright
    standard error). Returns (rho, n, lo, hi)."""
    pairs = [(a, b) for a, b in zip(x, y) if a is not None and b is not None]
    n = len(pairs)
    if n < 5:
        return (None, n, None, None)
    rx, ry = ranks([p[0] for p in pairs]), ranks([p[1] for p in pairs])
    rho = float(np.corrcoef(rx, ry)[0, 1])
    if math.isnan(rho):
        return (None, n, None, None)
    rho_c = max(min(rho, 0.9999), -0.9999)
    z = math.atanh(rho_c)
    se = math.sqrt((1 + rho_c ** 2 / 2) / (n - 3))
    return (rho, n, math.tanh(z - 1.96 * se), math.tanh(z + 1.96 * se))


def fmt_rho(t):
    rho, n, lo, hi = t
    if rho is None:
        return f"- (n={n})"
    return f"{rho:+.2f} [{lo:+.2f}, {hi:+.2f}] (n={n})"


def boot_by_day(recs, stat, n=2000):
    """Percentile 95% interval of stat(recs) when whole days are resampled (frames within a
    slot, and slots within a day, are not independent)."""
    days = sorted({r["day"] for r in recs})
    by = collections.defaultdict(list)
    for r in recs:
        by[r["day"]].append(r)
    out = []
    for _ in range(n):
        pick = [r for d in random.choices(days, k=len(days)) for r in by[d]]
        v = stat(pick)
        if v is not None:
            out.append(v)
    if len(out) < 50:
        return (None, None)
    out.sort()
    return (out[int(0.025 * len(out))], out[int(0.975 * len(out)) - 1])


def share(recs):
    s = sum(r["sent"] for r in recs)
    return sum(r["heard"] for r in recs) / s if s else None


# ---------------------------------------------------------------------------------------------
# Geometry and the sun
# ---------------------------------------------------------------------------------------------
def locator(loc):
    loc = loc.strip()
    lon = (ord(loc[0].upper()) - 65) * 20 - 180 + int(loc[2]) * 2
    lat = (ord(loc[1].upper()) - 65) * 10 - 90 + int(loc[3])
    if len(loc) >= 6:
        lon += (ord(loc[4].lower()) - 97) * (2 / 24) + 1 / 24
        lat += (ord(loc[5].lower()) - 97) * (1 / 24) + 1 / 48
    else:
        lon += 1
        lat += 0.5
    return lat, lon


def km(a, b):
    la1, lo1, la2, lo2 = map(math.radians, (*a, *b))
    h = math.sin((la2 - la1) / 2) ** 2 + math.cos(la1) * math.cos(la2) * math.sin((lo2 - lo1) / 2) ** 2
    return 6371 * 2 * math.asin(math.sqrt(h))


TX = locator("IO91lk")
DOURBES = (50.1, 4.6)
FAIRFORD = (51.7, -1.8)


def zenith(t, lat, lon):
    """Solar zenith angle in degrees (NOAA low-precision formulae, good to about 0.5 degree)."""
    doy = t.timetuple().tm_yday
    g = 2 * math.pi / 365 * (doy - 1 + (t.hour - 12 + t.minute / 60) / 24)
    decl = (0.006918 - 0.399912 * math.cos(g) + 0.070257 * math.sin(g) - 0.006758 * math.cos(2 * g)
            + 0.000907 * math.sin(2 * g) - 0.002697 * math.cos(3 * g) + 0.00148 * math.sin(3 * g))
    eot = 229.18 * (0.000075 + 0.001868 * math.cos(g) - 0.032077 * math.sin(g) - 0.014615 * math.cos(2 * g)
                    - 0.040849 * math.sin(2 * g))
    tst = t.hour * 60 + t.minute + eot + 4 * lon
    ha = math.radians(tst / 4 - 180)
    la = math.radians(lat)
    c = math.sin(la) * math.sin(decl) + math.cos(la) * math.cos(decl) * math.cos(ha)
    return math.degrees(math.acos(max(-1, min(1, c))))


def daylight(day, lat, lon):
    """Minutes of daylight (zenith < 90.833 degrees) on a day."""
    d = dt.datetime.fromisoformat(day)
    return sum(1 for m in range(0, 1440, 5) if zenith(d + dt.timedelta(minutes=m), lat, lon) < 90.833) * 5


# ---------------------------------------------------------------------------------------------
# The head end's journal: frames sent, waveform, and the ionosonde reading before each slot
# ---------------------------------------------------------------------------------------------
end = re.compile(r"slot (\d{4}-\d\d-\d\d) (\d\d:\d\d)Z: (done|ABORTED)\b.*?\b(\d+) of \d+ frames sent in (\d+) bursts(?: on (ms110d-[a-z0-9-]+))?")
iono = re.compile(r"^(\d{4}-\d\d-\d\d)T(\d\d):(\d\d).*?ionosonde: (GOOD|MARGINAL|POOR|UNKNOWN), (\S+) (\w+) at (\S+ \S+)Z \((\d+) min old, (\w+)\): foF2 ([\d.]+) MHz, MUF ([\d.]+)/([\d.]+)/([\d.]+) MHz")
sent, jiono = {}, {}
with open(J, encoding="latin-1") as f:
    for line in f:
        if m := end.search(line):
            sent[(m[1], m[2])] = (int(m[4]), int(m[5]), m[6] or "", m[3])
        elif (m := iono.search(line)) and int(m[3]) < 15:
            jiono[(m[1], f"{m[2]}:00")] = dict(verdict=m[4], station=f"{m[5]} {m[6]}", at=m[7], age=int(m[8]),
                                              source=m[9], fof2=float(m[10]), muf100=float(m[11]), muf500=float(m[12]),
                                              muf1000=float(m[13]))


def family(mode):
    return {"ms110d-wn4": "WN4", "ms110d-wn4-long": "WN4-long", "ms110d-wn3": "WN3"}.get(mode, "?")


# ---------------------------------------------------------------------------------------------
# Reception records (one per receiver per slot), with results.py's rules
# ---------------------------------------------------------------------------------------------
recs = []
with open(SURVEY, newline="") as f:
    for r in csv.DictReader(f):
        if r["listening"] != "yes":
            continue
        day, hhmm = r["slot_utc"].split()
        if (day, hhmm) not in sent or sent[(day, hhmm)][0] == 0:
            continue
        name = "M9PSY" if r["sdr"].startswith("M9PSY") else r["sdr"]
        recs.append(dict(src="survey", rx=name, day=day, hhmm=hhmm, heard=int(r["frames_heard"] or 0),
                         sent=sent[(day, hhmm)][0], snr=float(r["tone_snr_db_3khz"]) if r["tone_snr_db_3khz"] else None,
                         vh=None, km=int(r["km"]), own=True))

day_rows = list(csv.DictReader(open(R_DAYS, newline="")))
deaf = {(r["callsign"], r["day"]) for r in day_rows if "AUD" in r["errors"] and r["slots_heard"] == "0"}
SDR_NAMES = {"wessex.zapto.org": "WESSEX", "nope.tunnel.ubersdr.org": "a web SDR"}
OWN_SDRS = {"m9twm-1.tunnel.ubersdr.org"}
audio_of = {(r["callsign"], r["day"]): r["audio"] for r in day_rows}
loc_of = {r["callsign"]: r["locator"] for r in day_rows if r["locator"]}


def who(call, day):
    a = audio_of.get((call, day), "sc")
    if a in ("sc", "sdrconnect", "soundmodem", "") or a in OWN_SDRS:
        return call
    return f"{call} via {SDR_NAMES.get(a, a.split('.')[0])}"


for r in csv.DictReader(open(R_SLOTS, newline="")):
    if r["station"] not in ("", "M0LTE", "GB7RDG") or (r["callsign"], r["day"]) in deaf:
        continue
    if sent.get((r["day"], r["slot_utc"]), (0,))[0] <= 0:
        continue
    w = who(r["callsign"], r["day"])
    d_km = round(km(TX, locator(loc_of[r["callsign"]]))) if r["callsign"] in loc_of else None
    recs.append(dict(src="reports", rx=w, day=r["day"], hhmm=r["slot_utc"], heard=int(r["frames"] or 0),
                     sent=sent[(r["day"], r["slot_utc"])][0], snr=float(r["snr_db"]) if r["snr_db"] else None,
                     vh=float(r["virtual_height_km"]) if r["virtual_height_km"] else None, km=d_km, own=" via " not in w,
                     basis=r["channel_basis"]))

for r in recs:
    r["mode"] = family(sent[(r["day"], r["hhmm"])][2])
    r["hour"] = int(r["hhmm"][:2])
    r["nov"] = r["day"] >= CHANGE

# ---------------------------------------------------------------------------------------------
# Space weather: GFZ Kp/ap/Ap/SN/F10.7, NOAA flares
# ---------------------------------------------------------------------------------------------
gfz = {}


def read_gfz(lines, overwrite):
    for line in lines:
        if line.startswith("#") or not line.strip():
            continue
        p = line.split()
        day = f"{p[0]}-{p[1]}-{p[2]}"
        if not ("2026-10-01" <= day <= "2026-11-08"):
            continue
        if day in gfz and not overwrite:
            continue
        kp = [float(x) for x in p[7:15]]
        if kp[0] < 0:
            continue
        gfz[day] = dict(kp=kp, ap=[int(x) for x in p[15:23]], Ap=int(p[23]), sn=int(p[24]), f107=float(p[25]),
                        definitive=int(p[27]))


with gzip.open(os.path.join(SW, "gfz-Kp_ap_Ap_SN_F107_since_1932.txt.gz"), "rt") as f:
    read_gfz(f, True)
with open(os.path.join(SW, "gfz-Kp_ap_Ap_SN_F107_nowcast.txt")) as f:
    read_gfz(f, False)  # only fills days the archive file lacks (7 November)
gdays = sorted(gfz)


def kp_at(day, hour):
    return gfz[day]["kp"][hour // 3] if day in gfz else None


def kp_max_24h(day, hour):
    """Largest 3-hourly Kp in the 24 h before the slot (F-region storms lag the Kp rise)."""
    d = dt.date.fromisoformat(day)
    vals = []
    for back in range(0, 9):
        t = dt.datetime(d.year, d.month, d.day, hour) - dt.timedelta(hours=3 * back)
        k = gfz.get(t.date().isoformat())
        if k:
            vals.append(k["kp"][t.hour // 3])
    return max(vals) if vals else None


events = json.load(open(os.path.join(SW, "noaa-swpc-edited_events.json")))
flares = {}
for e in events:
    if e["type"] == "XRA" and e["particulars1"] and e["particulars1"][0] in "MX":
        key = e["max_datetime"]
        flares.setdefault(key, (e["begin_datetime"], e["max_datetime"], e["end_datetime"], e["particulars1"]))
flares = sorted(flares.values())

# ---------------------------------------------------------------------------------------------
# PROPquest ionosonde series (Dourbes DB049, Fairford FF051), 5-minute steps
# ---------------------------------------------------------------------------------------------
pq = {"DB049": {}, "FF051": {}}
for path in sorted(glob.glob(os.path.join(SW, "propquest", "propquest-*.json"))):
    st = os.path.basename(path).split("-")[1]
    o = json.load(open(path))["Observations"][0]
    for part in ("TODAY", "YDAY"):
        ds, ts = o[f"DATE_{part}"].split(","), o[f"TIME_{part}"].split(",")
        fo, mu, obs = o[f"foF2_{part}"].split(","), o[f"MUFD3000_{part}"].split(","), o[f"OBSERVATORY_{part}"].split(",")
        for d, t, a, b, ob in zip(ds, ts, fo, mu, obs):
            if a != "null" and ob == st:
                pq[st][dt.datetime.fromisoformat(f"{d}T{t}")] = (float(a), float(b) if b != "null" else None)


def pq_at(st, day, hhmm, lo=0, hi=10):
    """Median foF2 and M(3000)F2 from the station's soundings between lo and hi minutes past the
    slot's start (the slot's first 10 minutes by default)."""
    t0 = dt.datetime.fromisoformat(f"{day}T{hhmm}")
    vals = [pq[st][t0 + dt.timedelta(minutes=m)] for m in range(lo, hi + 1, 5) if t0 + dt.timedelta(minutes=m) in pq[st]]
    if not vals:
        return None, None
    fo = med([v[0] for v in vals])
    ms = [v[1] / v[0] for v in vals if v[1]]
    return fo, (med(ms) if ms else None)


def hmf2(m3000):
    """Peak height from M(3000)F2 by Shimazaki's formula, no foE correction: a rough guide only."""
    return 1490 / m3000 - 176 if m3000 else None


# Attach ionosphere and geomagnetic values to each slot.
slot_info = {}
for (day, hhmm), v in sent.items():
    if v[0] <= 0:
        continue
    j = jiono.get((day, hhmm))
    fo_pq, m_pq = pq_at("DB049", day, hhmm)
    fo_ff, _ = pq_at("FF051", day, hhmm, -15, 25)
    t = dt.datetime.fromisoformat(f"{day}T{hhmm}") + dt.timedelta(minutes=4)
    slot_info[(day, hhmm)] = dict(
        fof2_j=j["fof2"] if j else None, j_station=j["station"] if j else None, j_age=j["age"] if j else None,
        j_source=j["source"] if j else None,
        fof2_pq=fo_pq, m3000=m_pq, hmf2=hmf2(m_pq), fof2_ff=fo_ff,
        kp=kp_at(day, int(hhmm[:2])), kp24=kp_max_24h(day, int(hhmm[:2])), zen=zenith(t, *TX))
    si = slot_info[(day, hhmm)]
    # The foF2 used below: the journal's reading when it is under an hour old (as the brief asks),
    # otherwise Dourbes from PROPquest at the slot time.
    si["fof2"] = si["fof2_j"] if (j and j["age"] <= 60) else fo_pq
    si["fof2_basis"] = "journal" if (j and j["age"] <= 60) else ("propquest" if fo_pq else None)

for r in recs:
    r.update({k: slot_info[(r["day"], r["hhmm"])][k] for k in ("fof2", "fof2_pq", "fof2_j", "hmf2", "kp", "kp24", "zen")})

OUT = []


def out(s=""):
    OUT.append(s)
    print(s)


# =============================================================================================
# 0. The context: what space weather did
# =============================================================================================
out("# Space weather and Mailcast reception: the numbers\n")
out("Generated by paper/figures/space_weather.py; the write-up is paper/notes/space-weather.md.\n")
out("## 0. Context\n")
out("| Day | Kp (8 x 3 h) | max Kp | Ap | SN | F10.7 obs | M/X flares (UTC peak) |")
out("|---|---|---|---|---|---|---|")
for d in gdays:
    g = gfz[d]
    fl = ", ".join(f"{x[3]} {x[1][11:16]}" for x in flares if x[1][:10] == d)
    out(f"| {d} | {' '.join(f'{k:.1f}' for k in g['kp'])} | {max(g['kp']):.1f} | {g['Ap']} | {g['sn']} | {g['f107']:.0f} | {fl or '-'} |")
storm_days = [d for d in gdays if max(gfz[d]["kp"]) >= 5]
out(f"\nDays with Kp >= 5 (a G1 or bigger storm) at some point: {', '.join(storm_days) or 'none'}.")

# Agreement between the journal's foF2 and PROPquest Dourbes at the same slot.
pairs = [(v["fof2_j"], v["fof2_pq"]) for v in slot_info.values() if v["fof2_j"] and v["fof2_pq"] and v["j_station"] == "Dourbes DB049"]
diffs = [a - b for a, b in pairs]
stations = collections.Counter((v["j_station"], v["j_source"]) for v in slot_info.values() if v["j_station"])
ages = [v["j_age"] for v in slot_info.values() if v["j_age"] is not None]
out(f"\nSlots that sent frames: {len(slot_info)}. Journal ionosonde readings at those slots: {sum(1 for v in slot_info.values() if v['fof2_j'])} "
    f"({', '.join(f'{k[0]} by {k[1]}: {n}' for k, n in stations.most_common())}); age median {med(ages)} min, "
    f"{sum(1 for a in ages if a > 60)} over 60 min.")
out(f"foF2 used per slot: from the journal {sum(1 for v in slot_info.values() if v['fof2_basis'] == 'journal')}, "
    f"from PROPquest Dourbes {sum(1 for v in slot_info.values() if v['fof2_basis'] == 'propquest')}, none {sum(1 for v in slot_info.values() if not v['fof2_basis'])}.")
if diffs:
    out(f"Journal Dourbes foF2 minus PROPquest Dourbes foF2 at the slot's first 10 min: median {med(diffs):+.2f} MHz, "
        f"middle half {np.percentile(diffs, 25):+.2f} to {np.percentile(diffs, 75):+.2f} MHz, n={len(diffs)} "
        f"(the journal's reading is 20 to 60 min older, and foF2 changes through the day).")
ffp = [(v["fof2_ff"], v["fof2_pq"]) for v in slot_info.values() if v["fof2_ff"] and v["fof2_pq"]]
if ffp:
    out(f"Fairford minus Dourbes (PROPquest, same slot): median {med([a - b for a, b in ffp]):+.2f} MHz, n={len(ffp)}.")

# Noon foF2 and daylight through the period (seasonal trend).
noon = {}
for d in gdays:
    vals = [v[0] for t, v in pq["DB049"].items() if t.date().isoformat() == d and 11 <= t.hour < 13]
    if vals:
        noon[d] = med(vals)
slot_fo = {}
for d in gdays:
    vals = [v[0] for t, v in pq["DB049"].items() if t.date().isoformat() == d and 9 <= t.hour < 17]
    if vals:
        slot_fo[d] = (med(vals), sum(1 for x in vals if x < F_TX) / len(vals))
out("\n### Season: Dourbes foF2 and daylight at Reading, by week\n")
out("| Week from | median foF2 11-13 UTC (MHz) | median foF2 09-17 UTC (MHz) | share of 09-17 UTC soundings with foF2 below 7.05 MHz | daylight at Reading (h) | noon zenith angle (deg) | mean Ap | max Kp |")
out("|---|---|---|---|---|---|---|---|")
wk_start = dt.date(2026, 10, 1)
while wk_start <= dt.date(2026, 11, 7):
    ds = [(wk_start + dt.timedelta(days=i)).isoformat() for i in range(7)]
    ds = [d for d in ds if d in gfz]
    n_ = [noon[d] for d in ds if d in noon]
    s_ = [slot_fo[d][0] for d in ds if d in slot_fo]
    b_ = [slot_fo[d][1] for d in ds if d in slot_fo]
    mid = ds[len(ds) // 2]
    z12 = min(zenith(dt.datetime.fromisoformat(mid) + dt.timedelta(minutes=m), *TX) for m in range(600, 840, 5))
    out(f"| {wk_start} | {fmt(med(n_), 1)} | {fmt(med(s_), 1)} | {pct(sum(b_), len(b_)) if b_ else '-'} | {daylight(mid, *TX) / 60:.1f} | {z12:.0f} | "
        f"{statistics.mean(gfz[d]['Ap'] for d in ds):.0f} | {max(max(gfz[d]['kp']) for d in ds):.1f} |")
    wk_start += dt.timedelta(days=7)

# =============================================================================================
# 1. foF2 at the slot against share heard and tone SNR
# =============================================================================================
BINS = [(0, 6.0), (6.0, 7.0), (7.0, 8.0), (8.0, 9.0), (9.0, 10.0), (10.0, 99)]


def bin_label(b):
    lo, hi = b
    return f"< {hi:.0f}" if lo == 0 else (f">= {lo:.0f}" if hi == 99 else f"{lo:.0f} to {hi:.0f}")


def in_bin(x, b):
    return x is not None and b[0] <= x < b[1]


own = [r for r in recs if r["own"]]
GROUPS = [("Survey web SDRs (6 to 20 Oct)", [r for r in recs if r["src"] == "survey"]),
          ("Listeners, own equipment, to 1 Nov", [r for r in own if r["src"] == "reports" and not r["nov"]]),
          ("Listeners, own equipment, from 2 Nov", [r for r in own if r["src"] == "reports" and r["nov"]])]
out("\n## 1. foF2 at the slot against reception (receiver-slots)\n")
out("Share = frames heard / frames sent, pooled over the receiver-slots in the bin; 95% interval from resampling whole days. "
    "'Any' = receiver-slots with at least one frame. SNR = median tone SNR where the tone was measured.\n")
fig1 = {}
for gname, rs in GROUPS:
    out(f"### {gname}\n")
    out("| foF2 (MHz) | receiver-slots | days | any | share | 95% interval | tone SNR median (dB) | SNR n |")
    out("|---|---|---|---|---|---|---|---|")
    fig1[gname] = []
    for b in BINS:
        sub = [r for r in rs if in_bin(r["fof2"], b)]
        if not sub:
            continue
        sh = share(sub)
        lo, hi = boot_by_day(sub, share, 1000)
        snr = [r["snr"] for r in sub if r["snr"] is not None]
        out(f"| {bin_label(b)} | {len(sub)} | {len({r['day'] for r in sub})} | {pct(sum(1 for r in sub if r['heard']), len(sub))} | {pct(sum(r['heard'] for r in sub), sum(r['sent'] for r in sub))} | "
            f"{fmt(lo * 100 if lo is not None else None)} to {fmt(hi * 100 if hi is not None else None)}% | {fmt(med(snr))} | {len(snr)} |")
        fig1[gname].append((b, sh, lo, hi, len(sub), med(snr), len(snr)))
    nofo = [r for r in rs if r["fof2"] is None]
    if nofo:
        out(f"\n({len(nofo)} receiver-slots had no foF2 and are not in the table.)")
    out("")

out("### Rank correlation with foF2, per receiver (receiver-slot level; per-slot share)\n")
out("Spearman rho [approximate 95% interval] (n receiver-slots). Slots are not independent (same day, same hour), so the intervals are too narrow if anything.\n")
out("| Receiver | km | share vs foF2 | tone SNR vs foF2 | share vs hour-matched foF2 anomaly |")
out("|---|---|---|---|---|")
# foF2 anomaly: foF2 minus the median foF2 of slots at the same hour in the same 15-day half of the
# period, which takes out most of the daily cycle and the season.
hour_med = collections.defaultdict(list)
for (day, hhmm), v in slot_info.items():
    if v["fof2"] is not None:
        hour_med[(hhmm[:2], day >= "2026-10-22")].append(v["fof2"])
hour_med = {k: med(v) for k, v in hour_med.items()}
for r in recs:
    r["fof2_anom"] = r["fof2"] - hour_med[(r["hhmm"][:2], r["day"] >= "2026-10-22")] if r["fof2"] is not None else None
by_rx = collections.defaultdict(list)
for r in recs:
    by_rx[r["rx"]].append(r)
rx_rows = []
for rx, rs in sorted(by_rx.items(), key=lambda kv: (kv[1][0]["km"] or 0)):
    if len(rs) < 20:
        continue
    a = spearman([r["fof2"] for r in rs], [r["heard"] / r["sent"] for r in rs])
    b = spearman([r["fof2"] for r in rs if r["snr"] is not None], [r["snr"] for r in rs if r["snr"] is not None])
    c = spearman([r["fof2_anom"] for r in rs], [r["heard"] / r["sent"] for r in rs])
    out(f"| {rx} | {fmt(rs[0]['km'])} | {fmt_rho(a)} | {fmt_rho(b)} | {fmt_rho(c)} |")
    rx_rows.append((rx, rs[0]["km"], a, b, c))

# Pooled over every receiver, within hour.
out("\n### Within the same hour of day (all own-equipment receivers and survey SDRs, receiver-slots)\n")
out("| Hour (UTC) | receiver-slots | share, foF2 < 7 | n | share, foF2 7 to 8 | n | share, foF2 >= 8 | n |")
out("|---|---|---|---|---|---|---|---|")
for h in range(9, 17):
    rs = [r for r in own if r["hour"] == h and r["fof2"] is not None]
    cells = []
    for b in [(0, 7.0), (7.0, 8.0), (8.0, 99)]:
        sub = [r for r in rs if in_bin(r["fof2"], b)]
        cells.append(f"{pct(sum(r['heard'] for r in sub), sum(r['sent'] for r in sub))} | {len(sub)}")
    out(f"| {h:02d} | {len(rs)} | " + " | ".join(cells) + " |")

# =============================================================================================
# 2. Daily share against Kp, Ap, F10.7, SN and storms
# =============================================================================================
out("\n## 2. Daily share against geomagnetic and solar indices\n")
CONSIST = {"survey": ["WESSEX", "STUEY3D", "EI4HQ", "M9PSY"], "reports": ["GM4CXF", "M0NVK", "G8BPQ", "GM8BPQ", "G7TAJ"]}


def daily(src, rxs, lo="0", hi="9"):
    rs = [r for r in recs if r["src"] == src and r["rx"] in rxs and lo <= r["day"] <= hi]
    overall = {rx: share([r for r in rs if r["rx"] == rx]) for rx in rxs}
    days = collections.defaultdict(list)
    for r in rs:
        days[r["day"]].append(r)
    rows = []
    for d, sub in sorted(days.items()):
        rel = []
        for rx in rxs:
            x = [r for r in sub if r["rx"] == rx]
            if x and overall[rx]:
                rel.append(share(x) / overall[rx])
        fo = [slot_info[(r["day"], r["hhmm"])]["fof2"] for r in sub if slot_info[(r["day"], r["hhmm"])]["fof2"] is not None]
        rows.append(dict(day=d, share=share(sub), rel=med(rel), nrx=len({r["rx"] for r in sub}), nslots=len(sub),
                         Ap=gfz[d]["Ap"], kpmax=max(gfz[d]["kp"]), f107=gfz[d]["f107"], sn=gfz[d]["sn"],
                         kpprev=max(gfz[d]["kp"] + gfz[(dt.date.fromisoformat(d) - dt.timedelta(days=1)).isoformat()]["kp"][4:]),
                         fof2=med(fo)))
    return rows


DAILY = [("Survey: WESSEX, STUEY3D, EI4HQ, M9PSY (6 to 20 Oct)", daily("survey", CONSIST["survey"])),
         ("Listeners on own equipment, 8 Oct to 1 Nov", daily("reports", CONSIST["reports"], hi="2026-11-01")),
         ("Listeners on own equipment, 8 Oct to 7 Nov", daily("reports", CONSIST["reports"]))]
out("Each day: pooled share over the named receivers present that day, and the median over those receivers of (their share that day / their share over the whole period), which is less sensitive to which receivers reported. "
    "Kp max (prev) = largest 3-hourly Kp from 12 UTC the day before to the end of the day.\n")
out("| Set | against | rho with pooled share | rho with relative share |")
out("|---|---|---|---|")
for name, rows in DAILY:
    for k, label in [("Ap", "daily Ap"), ("kpmax", "max Kp that day"), ("kpprev", "max Kp, previous 36 h"), ("f107", "F10.7"), ("sn", "sunspot number"), ("fof2", "median foF2 at the day's slots")]:
        out(f"| {name} | {label} | {fmt_rho(spearman([r[k] for r in rows], [r['share'] for r in rows]))} | {fmt_rho(spearman([r[k] for r in rows], [r['rel'] for r in rows]))} |")
out("\n### Day by day (listeners on own equipment)\n")
out("| Day | receivers | receiver-slots | pooled share | relative share | Ap | max Kp | F10.7 | median foF2 at slots |")
out("|---|---|---|---|---|---|---|---|---|")
for r in DAILY[2][1]:
    out(f"| {r['day']} | {r['nrx']} | {r['nslots']} | {fmt(r['share'] * 100)}% | {fmt(r['rel'], 2)} | {r['Ap']} | {r['kpmax']:.1f} | {r['f107']:.0f} | {fmt(r['fof2'], 1)} |")
out("\n### Day by day (survey SDRs)\n")
out("| Day | SDRs | receiver-slots | pooled share | relative share | Ap | max Kp | F10.7 | median foF2 at slots |")
out("|---|---|---|---|---|---|---|---|---|")
for r in DAILY[0][1]:
    out(f"| {r['day']} | {r['nrx']} | {r['nslots']} | {fmt(r['share'] * 100)}% | {fmt(r['rel'], 2)} | {r['Ap']} | {r['kpmax']:.1f} | {r['f107']:.0f} | {fmt(r['fof2'], 1)} |")

# Storm: 10 October (Kp 7.3 at 15-21 UTC) and the day after.
out("\n### The 10 October storm and the flares during slots\n")
out("Share by day for the consistent survey SDRs and listeners, the days around 10 October.\n")
out("| Day | max Kp | survey share | survey slots | listeners' share | listeners' slots |")
out("|---|---|---|---|---|---|")
for d in ["2026-10-08", "2026-10-09", "2026-10-10", "2026-10-11", "2026-10-12", "2026-10-13"]:
    a = [r for r in recs if r["src"] == "survey" and r["rx"] in CONSIST["survey"] and r["day"] == d]
    b = [r for r in own if r["src"] == "reports" and r["rx"] in CONSIST["reports"] and r["day"] == d]
    out(f"| {d} | {max(gfz[d]['kp']):.1f} | {pct(sum(r['heard'] for r in a), sum(r['sent'] for r in a))} | {len(a)} | {pct(sum(r['heard'] for r in b), sum(r['sent'] for r in b))} | {len(b)} |")
out("\nM and X flares whose X-ray event overlapped a slot's first 10 minutes (D-region absorption, a short-wave fade, is possible on the day side):\n")
out("| Flare | begin-peak-end (UTC) | slot | receiver-slots | share in that slot | same receivers, same day, other slots |")
out("|---|---|---|---|---|---|")
for b_, m_, e_, cls in flares:
    tb, te = dt.datetime.fromisoformat(b_), dt.datetime.fromisoformat(e_)
    for (day, hhmm) in sorted(slot_info):
        t0 = dt.datetime.fromisoformat(f"{day}T{hhmm}")
        if tb <= t0 + dt.timedelta(minutes=10) and te >= t0:
            sub = [r for r in recs if (r["day"], r["hhmm"]) == (day, hhmm) and r["own"]]
            rxs = {r["rx"] for r in sub}
            other = [r for r in recs if r["day"] == day and r["hhmm"] != hhmm and r["rx"] in rxs]
            out(f"| {cls} | {b_[11:16]}-{m_[11:16]}-{e_[11:16]} {day} | {hhmm} | {len(sub)} | {pct(sum(r['heard'] for r in sub), sum(r['sent'] for r in sub))} | {pct(sum(r['heard'] for r in other), sum(r['sent'] for r in other))} |")

# =============================================================================================
# 3. The November drop
# =============================================================================================
out("\n## 3. The November drop\n")
NOVRX = ["GM4CXF", "GM8BPQ", "M0NVK", "G7TAJ"]
out("Receivers on their own equipment with slots both before and after 2 November. 'Late Oct' = 20 Oct to 1 Nov (closest in season). Share by waveform; tone SNR median.\n")
out("| Receiver | km | period | days | receiver-slots | share all | WN4 (short) | WN4-long | WN3 | tone SNR median (dB) | tone measured | median foF2 at its slots | median max Kp 24 h |")
out("|---|---|---|---|---|---|---|---|---|---|---|---|---|")
nov_tab = []
for rx in NOVRX:
    rs = [r for r in recs if r["rx"] == rx]
    for label, sub in [("Oct (to 1 Nov)", [r for r in rs if not r["nov"]]), ("late Oct", [r for r in rs if "2026-10-20" <= r["day"] < CHANGE]), ("Nov (from 2 Nov)", [r for r in rs if r["nov"]])]:
        if not sub:
            continue
        cells = []
        for mo in ("WN4", "WN4-long", "WN3"):
            x = [r for r in sub if r["mode"] == mo]
            cells.append(f"{pct(sum(r['heard'] for r in x), sum(r['sent'] for r in x))} ({len(x)})" if x else "-")
        snr = [r["snr"] for r in sub if r["snr"] is not None]
        out(f"| {rx} | {sub[0]['km']} | {label} | {len({r['day'] for r in sub})} | {len(sub)} | {pct(sum(r['heard'] for r in sub), sum(r['sent'] for r in sub))} | "
            + " | ".join(cells) + f" | {fmt(med(snr))} | {len(snr)}/{len(sub)} | {fmt(med([r['fof2'] for r in sub if r['fof2'] is not None]), 1)} | {fmt(med([r['kp24'] for r in sub]), 1)} |")
        nov_tab.append((rx, label, sub))

# Indirect standardisation: expected November frames if each receiver had heard each November
# slot at its own October rate for the same stratum.
def oe(base, test, key):
    rate = collections.defaultdict(lambda: [0, 0])
    for r in base:
        k = key(r)
        rate[k][0] += r["heard"]
        rate[k][1] += r["sent"]
    o = e = 0
    used = []
    for r in test:
        k = key(r)
        if rate[k][1] >= 100:  # need some October frames in the stratum
            o += r["heard"]
            e += r["sent"] * rate[k][0] / rate[k][1]
            used.append(r)
    return o, e, used


def fo_bin(x):
    return None if x is None else (0 if x < 7 else 1 if x < 8 else 2 if x < 9 else 3)


out("\n### Observed against expected at matched conditions\n")
out("Expected = November frames sent x the same receiver's October share in the same stratum (strata with at least 100 October frames). "
    "O/E 1.0 means November reception was as October's at the same conditions. 95% interval from resampling November days (October rates held fixed).\n")
out("| Waveform in November | October baseline | stratum | Nov receiver-slots matched | observed | expected | O/E | 95% interval |")
out("|---|---|---|---|---|---|---|---|")
base_all = [r for r in recs if r["rx"] in NOVRX and not r["nov"]]
nov_all = [r for r in recs if r["rx"] in NOVRX and r["nov"]]
oe_rows = []
for wf_label, test_modes, base_modes in [("WN3 (same waveform, 31 s bursts)", {"WN3"}, {"WN3"}),
                                         ("WN4-long vs October WN4", {"WN4-long"}, {"WN4"}),
                                         ("all", {"WN3", "WN4-long"}, {"WN3", "WN4"})]:
    for base_label, base in [("all Oct", base_all), ("late Oct", [r for r in base_all if r["day"] >= "2026-10-20"])]:
        for s_label, key in [("receiver x foF2 bin", lambda r: (r["rx"], fo_bin(r["fof2"]), r["mode"].replace("-long", ""))),
                             ("receiver x hour", lambda r: (r["rx"], r["hour"], r["mode"].replace("-long", ""))),
                             ("receiver x hour x foF2 >= 8", lambda r: (r["rx"], r["hour"], (r["fof2"] or 0) >= 8, r["mode"].replace("-long", "")))]:
            b = [r for r in base if r["mode"] in base_modes]
            t = [r for r in nov_all if r["mode"] in test_modes]
            o, e, used = oe(b, t, key)
            if not e:
                continue

            def stat(pick, b=b, key=key):
                o2, e2, _ = oe(b, pick, key)
                return o2 / e2 if e2 else None
            lo, hi = boot_by_day(used, stat, 1000)
            out(f"| {wf_label} | {base_label} | {s_label} | {len(used)} | {o} | {e:.0f} | {o / e:.2f} | {fmt(lo, 2)} to {fmt(hi, 2)} |")
            oe_rows.append((wf_label, base_label, s_label, len(used), o, e, lo, hi))

out("\n### Tone SNR (independent of waveform and coding) at matched hours\n")
out("Per receiver and hour: median tone SNR in October and in November; the table gives the median of the per-hour differences, and the pooled median SNR. "
    "A receiver-slot without a measured tone is left out.\n")
out("| Receiver | hours with both | Oct slots with tone | Nov slots with tone | median SNR Oct (dB) | median SNR Nov (dB) | median per-hour difference Nov-Oct (dB) | tone detected Oct | tone detected Nov |")
out("|---|---|---|---|---|---|---|---|---|")
snr_rows = []
for rx in NOVRX:
    rs = [r for r in recs if r["rx"] == rx]
    a = [r for r in rs if not r["nov"]]
    b = [r for r in rs if r["nov"]]
    hrs = sorted({r["hour"] for r in a if r["snr"] is not None} & {r["hour"] for r in b if r["snr"] is not None})
    dif = []
    for h in hrs:
        dif.append(med([r["snr"] for r in b if r["hour"] == h and r["snr"] is not None]) - med([r["snr"] for r in a if r["hour"] == h and r["snr"] is not None]))
    sa = [r["snr"] for r in a if r["snr"] is not None and r["hour"] in hrs]
    sb = [r["snr"] for r in b if r["snr"] is not None and r["hour"] in hrs]
    out(f"| {rx} | {len(hrs)} | {len(sa)} | {len(sb)} | {fmt(med(sa))} | {fmt(med(sb))} | {fmt(med(dif), 1)} | "
        f"{pct(sum(1 for r in a if r['snr'] is not None), len(a))} | {pct(sum(1 for r in b if r['snr'] is not None), len(b))} |")
    snr_rows.append((rx, sa, sb, med(dif) if dif else None))

out("\n### Share heard against tone SNR, before and after 2 November (all own-equipment listeners)\n")
out("If the drop were the path, November slots would sit at lower SNR but on the October curve; if it were the station's coding or burst change, the same SNR would give a lower share.\n")
out("| Tone SNR (dB) | Oct receiver-slots | Oct share | Nov receiver-slots | Nov share | Nov WN3 share | Oct WN3 share |")
out("|---|---|---|---|---|---|---|")
SNRB = [(-99, 0.5), (0.5, 3), (3, 6), (6, 9), (9, 99)]
snr_curve = []
for lo_, hi_ in SNRB:
    a = [r for r in own if r["src"] == "reports" and not r["nov"] and r["snr"] is not None and lo_ <= r["snr"] < hi_]
    b = [r for r in own if r["src"] == "reports" and r["nov"] and r["snr"] is not None and lo_ <= r["snr"] < hi_]
    a3 = [r for r in a if r["mode"] == "WN3"]
    b3 = [r for r in b if r["mode"] == "WN3"]
    lab = "0" if lo_ < 0 else (f">= {lo_:g}" if hi_ == 99 else f"{lo_:g} to {hi_:g}")
    out(f"| {lab} | {len(a)} | {pct(sum(r['heard'] for r in a), sum(r['sent'] for r in a))} | {len(b)} | {pct(sum(r['heard'] for r in b), sum(r['sent'] for r in b))} | "
        f"{pct(sum(r['heard'] for r in b3), sum(r['sent'] for r in b3))} | {pct(sum(r['heard'] for r in a3), sum(r['sent'] for r in a3))} |")
    snr_curve.append((lab, share(a), len(a), share(b), len(b), share(a3), len(a3), share(b3), len(b3)))

# Day by day around the change for the two receivers that span it best.
out("\n### GM4CXF and GM8BPQ day by day around the change\n")
out("| Day | GM4CXF share | GM4CXF WN3 | GM4CXF tone SNR median | GM8BPQ share | GM8BPQ WN3 | GM8BPQ tone SNR median | median foF2 at slots | max Kp |")
out("|---|---|---|---|---|---|---|---|---|")
for d in sorted({r["day"] for r in recs if r["rx"] in ("GM4CXF", "GM8BPQ") and r["day"] >= "2026-10-25"}):
    cells = []
    for rx in ("GM4CXF", "GM8BPQ"):
        x = [r for r in recs if r["rx"] == rx and r["day"] == d]
        x3 = [r for r in x if r["mode"] == "WN3"]
        cells += [pct(sum(r["heard"] for r in x), sum(r["sent"] for r in x)), pct(sum(r["heard"] for r in x3), sum(r["sent"] for r in x3)),
                  fmt(med([r["snr"] for r in x if r["snr"] is not None]))]
    fo = med([v["fof2"] for (dd, _), v in slot_info.items() if dd == d and v["fof2"] is not None])
    out(f"| {d} | " + " | ".join(cells) + f" | {fmt(fo, 1)} | {max(gfz[d]['kp']):.1f} |")

# =============================================================================================
# 4. Virtual height against the ionosonde
# =============================================================================================
out("\n## 4. Measured virtual height against the ionosonde\n")
vh = [r for r in recs if r["vh"] is not None]
out(f"Receiver-slots with a virtual height: {len(vh)} ({sum(1 for r in vh if r.get('basis') == 'probe')} from the probe). "
    f"hmF2 here is estimated from Dourbes M(3000)F2 by Shimazaki's formula with no E-layer correction (GIRO, which has scaled hmF2 and h'F, did not answer on 7 November); treat it as a rough guide.\n")
out("| Against | rho with virtual height |")
out("|---|---|")
out(f"| foF2 at the slot | {fmt_rho(spearman([r['fof2'] for r in vh], [r['vh'] for r in vh]))} |")
out(f"| foF2 / 7.05 MHz, only where foF2 > 7.05 | {fmt_rho(spearman([r['fof2'] / F_TX if r['fof2'] and r['fof2'] > F_TX else None for r in vh], [r['vh'] for r in vh]))} |")
out(f"| estimated hmF2 (Dourbes) | {fmt_rho(spearman([r['hmf2'] for r in vh], [r['vh'] for r in vh]))} |")
out(f"| receiver distance (km) | {fmt_rho(spearman([r['km'] for r in vh], [r['vh'] for r in vh]))} |")
out("\n| foF2 (MHz) | receiver-slots | median virtual height (km) | quartiles (km) | median est. hmF2 (km) |")
out("|---|---|---|---|---|")
vh_bins = []
for b in BINS:
    sub = [r for r in vh if in_bin(r["fof2"], b)]
    if len(sub) < 3:
        continue
    v = sorted(r["vh"] for r in sub)
    q = np.percentile(v, [25, 75])
    out(f"| {bin_label(b)} | {len(sub)} | {med(v):.0f} | {q[0]:.0f} to {q[1]:.0f} | {fmt(med([r['hmf2'] for r in sub if r['hmf2']]))} |")
    vh_bins.append((b, len(sub), med(v), q[0], q[1]))
out("\n| Receiver | km | slots | median virtual height (km) | rho with foF2 |")
out("|---|---|---|---|---|")
for rx in sorted({r["rx"] for r in vh}):
    sub = [r for r in vh if r["rx"] == rx]
    if len(sub) >= 8:
        out(f"| {rx} | {fmt(sub[0]['km'])} | {len(sub)} | {med([r['vh'] for r in sub]):.0f} | {fmt_rho(spearman([r['fof2'] for r in sub], [r['vh'] for r in sub]))} |")

# =============================================================================================
# Charts
# =============================================================================================
import matplotlib  # noqa: E402

matplotlib.use("Agg")
import matplotlib.dates as mdates  # noqa: E402
import matplotlib.pyplot as plt  # noqa: E402

INK, INK2, MUTED, GRID, AXIS, SURF = "#0b0b0b", "#52514e", "#898781", "#e1e0d9", "#c3c2b7", "#fcfcfb"
S1, S2, S3 = "#2a78d6", "#eb6834", "#1baf7a"
plt.rcParams.update({"font.family": "sans-serif", "font.size": 9, "axes.edgecolor": AXIS, "axes.labelcolor": INK2,
                     "xtick.color": MUTED, "ytick.color": MUTED, "axes.grid": True, "grid.color": GRID, "grid.linewidth": 0.6,
                     "axes.spines.top": False, "axes.spines.right": False, "figure.facecolor": SURF, "axes.facecolor": SURF,
                     "axes.titlesize": 10, "axes.titlecolor": INK, "axes.titleweight": "bold", "axes.titlelocation": "left",
                     "legend.frameon": False, "svg.fonttype": "none"})
os.makedirs(PLOTS, exist_ok=True)


def save(fig, name):
    for ext in ("png", "svg"):
        fig.savefig(os.path.join(PLOTS, f"space-weather-{name}.{ext}"), dpi=200 if ext == "png" else None, bbox_inches="tight")
    plt.close(fig)


change_t = dt.datetime(2026, 11, 2)

# Chart 1: context time series.
fig, ax = plt.subplots(4, 1, figsize=(8, 7.6), sharex=True, gridspec_kw={"height_ratios": [1.1, 0.8, 1.3, 1.1]})
t3, k3 = [], []
for d in gdays:
    for i, k in enumerate(gfz[d]["kp"]):
        t3.append(dt.datetime.fromisoformat(d) + dt.timedelta(hours=3 * i + 1.5))
        k3.append(k)
ax[0].bar(t3, k3, width=0.11, color=[S2 if k >= 5 else S1 for k in k3], linewidth=0)
ax[0].axhline(5, color=MUTED, lw=0.8, ls=":")
ax[0].text(t3[0], 5.2, "Kp 5 = minor storm (G1)", color=INK2, fontsize=7.5)
ax[0].set_ylabel("Kp (3-hourly)")
ax[0].set_title("Geomagnetic activity, solar flux, and the ionosphere over Mailcast's first five weeks")
ax[0].set_ylim(0, 9)
td = [dt.datetime.fromisoformat(d) + dt.timedelta(hours=12) for d in gdays]
ax[1].plot(td, [gfz[d]["f107"] for d in gdays], color=S1, lw=2)
ax[1].set_ylabel("F10.7 (sfu)")
pts = sorted(pq["DB049"].items())
ax[2].plot([t for t, v in pts], [v[0] for t, v in pts], color=S1, lw=0.7, alpha=0.55, label="Dourbes, every 5 min (PROPquest)")
sx = [dt.datetime.fromisoformat(f"{d}T{h}") for (d, h), v in sorted(slot_info.items()) if v["fof2"] is not None]
sy = [v["fof2"] for k, v in sorted(slot_info.items()) if v["fof2"] is not None]
ax[2].scatter(sx, sy, s=10, color=S2, zorder=3, edgecolors=SURF, linewidths=0.6, label="at each Mailcast slot")
ax[2].axhline(F_TX, color=INK2, lw=0.9, ls="--")
ax[2].text(td[0], F_TX + 0.25, "7.05 MHz: below this, near-vertical 40 m escapes", color=INK2, fontsize=7.5)
ax[2].set_ylabel("foF2 (MHz)")
ax[2].set_ylim(2, 13)
ax[2].legend(loc="upper right", fontsize=7.5, ncol=2)
# Listeners' daily share (own equipment, consistent set) and survey.
for (name, rows), col, lab in [(DAILY[0], S3, "survey web SDRs (4)"), (DAILY[2], S2, "listeners, own equipment (5)")]:
    ax[3].plot([dt.datetime.fromisoformat(r["day"]) + dt.timedelta(hours=12) for r in rows], [r["share"] * 100 for r in rows], color=col, lw=2, marker="o", ms=4, label=lab)
ax[3].set_ylabel("frames heard (%)")
ax[3].set_ylim(0, 100)
ax[3].legend(loc="upper left", fontsize=7.5, ncol=2)
for a in ax:
    a.axvline(change_t, color=INK2, lw=0.9)
ax[3].text(change_t + dt.timedelta(hours=6), 88, "2 Nov: long interleaver,\n31 s bursts, bulk tier", fontsize=7.5, color=INK2, va="top")
ax[3].xaxis.set_major_formatter(mdates.DateFormatter("%d %b"))
ax[3].xaxis.set_major_locator(mdates.DayLocator(bymonthday=[1, 8, 15, 22, 29]))
ax[3].set_xlim(dt.datetime(2026, 10, 1), dt.datetime(2026, 11, 8))
fig.align_ylabels(ax)
save(fig, "context")

# Chart 2: share and SNR by foF2 bin.
fig, ax = plt.subplots(1, 2, figsize=(8.4, 3.4))
cols = [S3, S1, S2]
w = 0.26
for gi, (gname, rows) in enumerate(fig1.items()):
    xs = [BINS.index(b) + (gi - 1) * w for b, *_ in rows]
    ys = [sh * 100 for b, sh, *_ in rows]
    err = [[(sh - lo) * 100 if lo is not None else 0 for b, sh, lo, hi, *_ in rows], [(hi - sh) * 100 if hi is not None else 0 for b, sh, lo, hi, *_ in rows]]
    ax[0].bar(xs, ys, width=w - 0.03, color=cols[gi], label=gname, linewidth=0)
    ax[0].errorbar(xs, ys, yerr=err, fmt="none", ecolor=INK2, elinewidth=0.8, capsize=0)
    for x, (b, sh, lo, hi, n, *_r) in zip(xs, rows):
        ax[0].text(x, 1, f"{n}", ha="center", va="bottom", fontsize=6, color=SURF if sh * 100 > 8 else INK2, rotation=90)
    xs2 = [BINS.index(b) + (gi - 1) * 0.1 for b, sh, lo, hi, n, ms, ns in rows if ms is not None and ns >= 5]
    ys2 = [ms for b, sh, lo, hi, n, ms, ns in rows if ms is not None and ns >= 5]
    ax[1].plot(xs2, ys2, color=cols[gi], lw=2, marker="o", ms=5, label=gname)
for a in ax:
    a.set_xticks(range(len(BINS)))
    a.set_xticklabels([bin_label(b) for b in BINS], fontsize=8)
    a.set_xlabel("foF2 at the slot (MHz)")
ax[0].set_ylabel("frames heard (%)")
ax[0].set_title("Share heard by foF2")
ax[1].set_ylabel("median tone SNR (dB)")
ax[1].set_title("Tone SNR by foF2")
ax[0].legend(fontsize=7, loc="upper left")
ax[0].text(0.99, 0.98, "bars: 95% interval, days resampled\nnumbers: receiver-slots", transform=ax[0].transAxes, ha="right", va="top", fontsize=6.5, color=INK2)
ax[1].text(0.99, 0.02, "survey SNR is the collector's 3 kHz figure;\nreports' is the receiver's own; not comparable", transform=ax[1].transAxes, ha="right", va="bottom", fontsize=6.5, color=INK2)
fig.tight_layout()
save(fig, "fof2")

# Chart 3: daily share against Ap.
fig, ax = plt.subplots(1, 2, figsize=(8.4, 3.3), sharey=True)
for a, (name, rows), col in [(ax[0], DAILY[0], S3), (ax[1], DAILY[2], S2)]:
    pre = [r for r in rows if r["day"] < CHANGE]
    post = [r for r in rows if r["day"] >= CHANGE]
    a.scatter([r["Ap"] for r in pre], [r["share"] * 100 for r in pre], s=30, color=col, edgecolors=SURF, linewidths=1, label="to 1 Nov", zorder=3)
    if post:
        a.scatter([r["Ap"] for r in post], [r["share"] * 100 for r in post], s=34, facecolors=SURF, edgecolors=col, linewidths=1.6, label="from 2 Nov", zorder=3)
        a.legend(fontsize=7.5, loc="upper right")
    rho = spearman([r["Ap"] for r in pre], [r["share"] for r in pre])
    a.set_title(name.split(" (")[0].replace("Survey: ", "Survey SDRs: ").replace(", 8 Oct to 7 Nov", ""), fontsize=9)
    a.text(0.02, 0.97, f"to 1 Nov: Spearman {rho[0]:+.2f}, 95% {rho[2]:+.2f} to {rho[3]:+.2f}, n={rho[1]} days", transform=a.transAxes, fontsize=7, color=INK2, va="top")
    a.set_xlabel("daily Ap")
    a.set_xscale("log")
    a.set_xticks([2, 5, 10, 20, 50])
    a.set_xticklabels(["2", "5", "10", "20", "50"])
ax[0].set_ylabel("frames heard that day (%)")
ax[0].set_ylim(0, 100)
fig.tight_layout()
save(fig, "daily-ap")

# Chart 4: the November drop, share against tone SNR before and after.
fig, ax = plt.subplots(1, 2, figsize=(8.4, 3.4))
labs = [c[0] for c in snr_curve]
xs = range(len(labs))
for idx, (col, lab, off) in enumerate([(S1, "to 1 Nov", 1), (S2, "from 2 Nov", 3)]):
    ys = [c[off] * 100 if c[off] is not None and c[off + 1] >= 5 else np.nan for c in snr_curve]
    ax[0].plot(xs, ys, color=col, lw=2, marker="o", ms=5, label=lab)
    for x, c in zip(xs, snr_curve):
        if c[off] is not None and c[off + 1] >= 5:
            ax[0].annotate(f"{c[off + 1]}", (x, c[off] * 100), textcoords="offset points", xytext=(0, 6 if idx == 0 else -11), ha="center", fontsize=6.5, color=INK2)
ax[0].set_xticks(list(xs))
ax[0].set_xticklabels(labs, fontsize=8)
ax[0].set_xlabel("tone SNR at the receiver (dB)")
ax[0].set_ylabel("frames heard (%)")
ax[0].set_ylim(0, 100)
ax[0].set_title("Share heard at the same tone SNR")
ax[0].legend(fontsize=7.5, loc="upper left")
ax[0].text(0.99, 0.02, "listeners on own equipment, all waveforms;\nnumbers: receiver-slots", transform=ax[0].transAxes, ha="right", va="bottom", fontsize=6.5, color=INK2)
# Right: tone SNR per receiver, Oct vs Nov (box-free: median dots with interquartile lines).
for i, (rx, sa, sb, dif) in enumerate(snr_rows):
    for j, (vals, col) in enumerate([(sa, S1), (sb, S2)]):
        if len(vals) >= 3:
            q = np.percentile(vals, [25, 50, 75])
            y = i + (j - 0.5) * 0.3
            ax[1].plot([q[0], q[2]], [y, y], color=col, lw=2, solid_capstyle="round")
            ax[1].scatter([q[1]], [y], s=40, color=col, edgecolors=SURF, linewidths=1, zorder=3, label=["to 1 Nov", "from 2 Nov"][j] if i == 0 else None)
            ax[1].text(q[2] + 0.3, y, f"n={len(vals)}", va="center", fontsize=6.5, color=INK2)
ax[1].set_yticks(range(len(snr_rows)))
ax[1].set_yticklabels([r[0] for r in snr_rows])
ax[1].invert_yaxis()
ax[1].set_xlabel("tone SNR (dB), median and middle half")
ax[1].set_title("Tone SNR at the same hours, before and after")
ax[1].legend(fontsize=7.5, loc="lower right")
fig.tight_layout()
save(fig, "november")

# Chart 5: virtual height against foF2.
fig, ax = plt.subplots(figsize=(5.6, 3.6))
pre = [r for r in vh if r["fof2"] is not None and not r["nov"]]
post = [r for r in vh if r["fof2"] is not None and r["nov"]]
ax.scatter([r["fof2"] for r in pre], [r["vh"] for r in pre], s=12, color=S1, alpha=0.6, edgecolors="none", label="to 1 Nov")
ax.scatter([r["fof2"] for r in post], [r["vh"] for r in post], s=14, facecolors="none", edgecolors=S2, linewidths=0.9, label="from 2 Nov")
bx = [(b[0] + min(b[1], 12)) / 2 if b[0] else 5.5 for b, *_ in vh_bins]
ax.plot(bx, [m for b, n, m, q1, q3 in vh_bins], color=INK, lw=2, label="median per foF2 bin")
ax.axvline(F_TX, color=INK2, lw=0.9, ls="--")
ax.set_xlabel("foF2 at the slot (MHz)")
ax.set_ylabel("virtual height from the probe (km)")
ax.set_title("Measured virtual height against foF2")
ax.legend(fontsize=7.5, loc="upper right")
fig.tight_layout()
save(fig, "vheight")
