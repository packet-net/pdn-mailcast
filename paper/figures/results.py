#!/usr/bin/env python3
"""The figures the paper's results section quotes, worked out from the archived data.

Usage: paper/figures/results.py [repo root] > paper/figures/results.md

Sources: the head end's journal (data/headend-gb7rdg), the web SDR survey
(data/web-sdr-survey-2026-10) and listeners' daily reports (reports/data). A share is frames heard
over frames sent in the slots a receiver was listening to; a slot cut short counts what it sent.
"""
import collections
import csv
import os
import re
import statistics
import sys

root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
J = os.path.join(root, "data/headend-gb7rdg/headend-journal-to-2026-11-07.txt")
SURVEY = os.path.join(root, "data/web-sdr-survey-2026-10/slots.csv")
R_SLOTS = os.path.join(root, "reports/data/slots.csv")
R_DAYS = os.path.join(root, "reports/data/reports.csv")


def med(xs):
    return statistics.median(xs) if xs else None


def pct(a, b):
    return f"{100 * a / b:.0f}%" if b else "-"


def fmt(x, nd=0):
    return "-" if x is None else f"{x:.{nd}f}"


# --- The head end -------------------------------------------------------------------------------
end = re.compile(r"slot (\d{4}-\d\d-\d\d) (\d\d:\d\d)Z: (done|ABORTED)\b.*?\b(\d+) of \d+ frames sent in (\d+) bursts(?: on (ms110d-[a-z0-9-]+))?")
span = re.compile(r"slot \S+ \S+Z: done, (\d\d):(\d\d):(\d\d) to (\d\d):(\d\d):(\d\d)Z")
today = re.compile(r"slots today \((\d{4}-\d\d-\d\d)\): (\d+), (\d+) completed, (\d+) cut short, (\d+) skipped")
intake = re.compile(r"intake: (\S+) from \S+ to \S+, (\d+) octets")
busy = re.compile(r"slot (\S+ \S+)Z: channel busy")
sent, minutes, days, bids, busy_slots = {}, {}, {}, {}, set()
with open(J, encoding="latin-1") as f:
    for line in f:
        if m := end.search(line):
            sent[(m[1], m[2])] = (int(m[4]), int(m[5]), m[6] or "", m[3])
            if s := span.search(line):
                a = int(s[1]) * 3600 + int(s[2]) * 60 + int(s[3])
                b = int(s[4]) * 3600 + int(s[5]) * 60 + int(s[6])
                minutes[(m[1], m[2])] = (b - a) / 60
        elif m := today.search(line):
            days[m[1]] = tuple(int(x) for x in m.groups()[1:])
        elif m := intake.search(line):
            bids.setdefault(m[1], int(m[2]))
        elif m := busy.search(line):
            busy_slots.add(m[1])

print("## Head end, 5 October to 7 November 2026\n")
keyed = [k for k, v in sent.items() if v[0] > 0]
by_mode = collections.Counter()
for k, (n, b, mode, how) in sent.items():
    by_mode[mode.replace("-long", " (long interleaver)") or "not logged"] += n
print(f"- Days with slots: {len(days)}; slots run {sum(v[0] for v in days.values())}, completed {sum(v[1] for v in days.values())}, cut short {sum(v[2] for v in days.values())}, skipped {sum(v[3] for v in days.values())} (by each day's last tally).")
print(f"- Slots that sent frames: {len(keyed)}; frames sent: {sum(v[0] for v in sent.values())}; bursts: {sum(v[1] for v in sent.values())}.")
print(f"- Frames by waveform: " + ", ".join(f"{k} {v}" for k, v in by_mode.most_common()) + ".")
mins = list(minutes.values())
print(f"- Time on air per completed slot (start to closing ident): median {med(mins):.1f} min, total {sum(mins) / 60:.1f} h over {len(mins)} slots.")
print(f"- Slots that found the channel busy and waited: {len(busy_slots)} of {len(sent)}.")
print(f"- Bulletins taken in from GB7RDG: {len(bids)} distinct BIDs, {sum(bids.values()) / 1000:.0f} kB of text, median {med(list(bids.values())):.0f} bytes.")

# --- The web SDR survey -------------------------------------------------------------------------
sv = collections.defaultdict(lambda: dict(slots=0, heard=0, frames=0, sent=0, snr=[], bulletins=0, km=None))
with open(SURVEY, newline="") as f:
    for r in csv.DictReader(f):
        if r["listening"] != "yes":
            continue
        day, hhmm = r["slot_utc"].split()
        if (day, hhmm) not in sent:
            continue
        name = "M9PSY" if r["sdr"].startswith("M9PSY") else r["sdr"]
        s = sv[name]
        s["km"] = int(r["km"])
        s["slots"] += 1
        fr = int(r["frames_heard"] or 0)
        s["frames"] += fr
        s["sent"] += sent[(day, hhmm)][0]
        if fr:
            s["heard"] += 1
        if r["tone_snr_db_3khz"]:
            s["snr"].append(float(r["tone_snr_db_3khz"]))
        s["bulletins"] += int(r["bulletins_completed"] or 0)

print("\n## Web SDR survey, 6 to 20 October 2026\n")
print("| SDR | km | slots listened | slots with frames | frames heard | median tone SNR (dB) | bulletins rebuilt |")
print("|---|---|---|---|---|---|---|")
for n, s in sorted(sv.items(), key=lambda kv: kv[1]["km"]):
    print(f"| {n} | {s['km']} | {s['slots']} | {pct(s['heard'], s['slots'])} | {pct(s['frames'], s['sent'])} | {fmt(med(s['snr']))} | {s['bulletins']} |")
tot_f = sum(s["frames"] for s in sv.values()); tot_s = sum(s["sent"] for s in sv.values())
print(f"\nAll SDRs together: {pct(tot_f, tot_s)} of frames heard over {sum(s['slots'] for s in sv.values())} receiver-slots.")

# --- Listeners' reports -------------------------------------------------------------------------
deaf = set()
day_rows = list(csv.DictReader(open(R_DAYS, newline="")))
for r in day_rows:
    if "AUD" in r["errors"] and r["slots_heard"] == "0":
        deaf.add((r["callsign"], r["day"]))
rows = [r for r in csv.DictReader(open(R_SLOTS, newline=""))
        if r["station"] in ("", "M0LTE", "GB7RDG") and (r["callsign"], r["day"]) not in deaf and (r["day"], r["slot_utc"]) in sent]

ls = collections.defaultdict(lambda: dict(days=set(), slots=0, heard=0, frames=0, sent=0, snr=[]))
for r in rows:
    s = ls[r["callsign"]]
    s["days"].add(r["day"]); s["slots"] += 1
    fr = int(r["frames"] or 0)
    s["frames"] += fr; s["sent"] += sent[(r["day"], r["slot_utc"])][0]
    if fr:
        s["heard"] += 1
    if r["snr_db"]:
        s["snr"].append(float(r["snr_db"]))
loc = {}
rebuilt = collections.Counter()
for r in day_rows:
    if r["locator"]:
        loc[r["callsign"]] = (r["locator"], r["audio"])
    if (r["callsign"], r["day"]) not in deaf:
        rebuilt[r["callsign"]] += int(r["rebuilt"] or 0)

print("\n## Listeners' daily reports, 8 October to 7 November 2026\n")
print(f"{len(day_rows)} reports from {len(ls)} listeners; {len(deaf)} report-days with no audio at all are left out below.\n")
print("| Listener | locator | audio | days | slots listened | slots with frames | frames heard | median tone SNR (dB) | bulletins rebuilt |")
print("|---|---|---|---|---|---|---|---|---|")
for n, s in sorted(ls.items(), key=lambda kv: -kv[1]["frames"] / max(kv[1]["sent"], 1)):
    lc, au = loc.get(n, ("-", "-"))
    au = "sound card" if au == "sc" else au
    print(f"| {n} | {lc} | {au} | {len(s['days'])} | {s['slots']} | {pct(s['heard'], s['slots'])} | {pct(s['frames'], s['sent'])} | {fmt(med(s['snr']))} | {rebuilt[n]} |")

# Waveform: share heard on WN4 slots against WN3 slots, for receivers that heard something of both.
print("\n### WN4 (1200 bps) against WN3 (600 bps)\n")
print("Share of frames heard, per receiver, over slots of each waveform it listened to (survey and reports).\n")
wf = collections.defaultdict(lambda: collections.Counter())
def add_wf(name, day, hhmm, fr):
    n, _, mode, _ = sent[(day, hhmm)]
    mode = "wn4" if mode.startswith("ms110d-wn4") else "wn3" if mode == "ms110d-wn3" else None
    if mode:
        wf[name][mode + "_f"] += fr; wf[name][mode + "_s"] += n
for r in csv.DictReader(open(SURVEY, newline="")):
    d, h = r["slot_utc"].split()
    if r["listening"] == "yes" and (d, h) in sent:
        add_wf("M9PSY" if r["sdr"].startswith("M9PSY") else r["sdr"], d, h, int(r["frames_heard"] or 0))
for r in rows:
    add_wf(r["callsign"], r["day"], r["slot_utc"], int(r["frames"] or 0))
print("| Receiver | WN4 | WN3 |")
print("|---|---|---|")
for n, c in sorted(wf.items()):
    if c["wn4_f"] + c["wn3_f"] == 0 or not c["wn4_s"] or not c["wn3_s"]:
        continue
    print(f"| {n} | {pct(c['wn4_f'], c['wn4_s'])} | {pct(c['wn3_f'], c['wn3_s'])} |")

# Hour of day.
print("\n### By hour of day (UTC), all report slots\n")
hr = collections.defaultdict(lambda: dict(slots=0, heard=0, frames=0, sent=0))
for r in rows:
    h = r["slot_utc"][:2]
    s = hr[h]; s["slots"] += 1
    fr = int(r["frames"] or 0); s["frames"] += fr; s["sent"] += sent[(r["day"], r["slot_utc"])][0]
    if fr:
        s["heard"] += 1
print("| Hour | slots | slots with frames | frames heard |")
print("|---|---|---|---|")
for h in sorted(hr):
    s = hr[h]
    print(f"| {h} | {s['slots']} | {pct(s['heard'], s['slots'])} | {pct(s['frames'], s['sent'])} |")

# Channel measurements.
print("\n### The measured channel (report slots with a channel measurement)\n")
ch = [r for r in rows if r["modes"]]
probe = [r for r in ch if r["channel_basis"] == "probe"]
def col(rs, k):
    return [float(r[k]) for r in rs if r[k]]
print(f"- Slots measured: {len(ch)} ({len(probe)} from the probe, {len(ch) - len(probe)} from the bursts).")
modes = collections.Counter(int(r["modes"]) for r in ch)
print("- Propagation modes seen: " + ", ".join(f"{k}: {modes[k]}" for k in sorted(modes)) + ".")
for k, label, nd in [("virtual_height_km", "virtual height (km)", 0), ("two_f_delay_ms", "second mode's delay (ms)", 1),
                     ("two_f_power_db", "second mode's power against the first (dB)", 0), ("delay_spread_ms", "delay spread (ms)", 2),
                     ("doppler_spread_hz", "Doppler spread (Hz)", 2)]:
    xs = sorted(col(ch, k))
    if xs:
        q = statistics.quantiles(xs, n=4)
        print(f"- {label}: median {xs[len(xs) // 2]:.{nd}f}, quartiles {q[0]:.{nd}f} to {q[2]:.{nd}f}, n={len(xs)}.")

# Propagation verdicts against what was heard. The verdict is the one the head end logged as the
# slot began (a receiver only learns it by decoding it, so its own copy is biased towards strong
# receivers); every receiver-slot of the survey and the reports counts.
print("\n### The head end's propagation verdicts against what was heard (survey and report slots)\n")
verdict = collections.defaultdict(dict)
vline = re.compile(r"^(\d{4}-\d\d-\d\d)T(\d\d):(\d\d).*?(ionosonde|pskreporter): (GOOD|MARGINAL|POOR|UNKNOWN)")
with open(J, encoding="latin-1") as f:
    for line in f:
        if (m := vline.search(line)) and int(m[3]) < 15:
            verdict[m[4]][(m[1], f"{m[2]}:00")] = m[5].lower()
recv = []
for r in csv.DictReader(open(SURVEY, newline="")):
    d, h = r["slot_utc"].split()
    if r["listening"] == "yes" and (d, h) in sent:
        recv.append((d, h, int(r["frames_heard"] or 0)))
for r in rows:
    recv.append((r["day"], r["slot_utc"], int(r["frames"] or 0)))
print("| Source | verdict | receiver-slots | with frames | frames heard |")
print("|---|---|---|---|---|")
for src, label in (("ionosonde", "ionosonde"), ("pskreporter", "PSK Reporter")):
    g = collections.defaultdict(lambda: dict(slots=0, heard=0, frames=0, sent=0))
    for d, h, fr in recv:
        v = verdict[src].get((d, h), "not logged")
        x = g[v]; x["slots"] += 1; x["frames"] += fr; x["sent"] += sent[(d, h)][0]
        if fr:
            x["heard"] += 1
    for v in ("good", "marginal", "poor", "unknown", "not logged"):
        if v in g:
            x = g[v]
            print(f"| {label} | {v} | {x['slots']} | {pct(x['heard'], x['slots'])} | {pct(x['frames'], x['sent'])} |")
