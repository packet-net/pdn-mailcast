#!/usr/bin/env python3
"""Every chart and diagram for the mailcast paper, drawn from the archived data.

Usage: paper/figures/plots.py [repo root]
Needs matplotlib and numpy. Writes PNG (300 dpi) and SVG files to paper/figures/plots/.

The data definitions are copied from paper/figures/results.py so the charts and the tables agree:
a share is frames heard over frames sent in the slots a receiver was listening to; slots in which
the head end sent nothing do not count; report days with an AUD error and no slot heard are left
out; a listener's days on a public web SDR are kept apart from days on their own equipment.
"""
import collections
import csv
import datetime as dt
import math
import os
import re
import statistics
import sys

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402
from matplotlib.patches import FancyArrowPatch, FancyBboxPatch, Rectangle  # noqa: E402

root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
J = os.path.join(root, "data/headend-gb7rdg/headend-journal-to-2026-11-07.txt")
SURVEY = os.path.join(root, "data/web-sdr-survey-2026-10/slots.csv")
R_SLOTS = os.path.join(root, "reports/data/slots.csv")
R_DAYS = os.path.join(root, "reports/data/reports.csv")

# The paper's data runs to 7 November 2026; reports added to the archive later are left out so its
# figures stay fixed.
CUTOFF = "2026-11-07"
def upto(rows):
    return [r for r in rows if r["day"] <= CUTOFF]
OUT = os.path.join(root, "paper/figures/plots")
os.makedirs(OUT, exist_ok=True)

# --- Look ----------------------------------------------------------------------------------------
# One palette for every figure. Waveforms always wear the same colours.
INK, INK2, MUTED = "#0b0b0b", "#52514e", "#898781"
GRID, AXIS, SURFACE = "#e1e0d9", "#c3c2b7", "#ffffff"
BLUE, ORANGE, AQUA = "#2a78d6", "#eb6834", "#1baf7a"
NEUTRAL = "#c3c2b7"  # "not logged" and other non-data fills
WASH = "#f0efec"
SEQ = ["#ffffff", "#cde2fb", "#9ec5f4", "#6da7ec", "#3987e5", "#256abf", "#184f95", "#0d366b"]
WAVE_COLOR = {"WN4": BLUE, "WN4 long interleaver": AQUA, "WN3": ORANGE, "not logged": NEUTRAL}

plt.rcParams.update({
    "font.family": "DejaVu Sans",
    "font.size": 8.5,
    "axes.titlesize": 10,
    "axes.titleweight": "bold",
    "axes.titlelocation": "left",
    "axes.labelsize": 8.5,
    "axes.labelcolor": INK2,
    "axes.edgecolor": AXIS,
    "axes.linewidth": 0.8,
    "axes.unicode_minus": False,
    "axes.spines.top": False,
    "axes.spines.right": False,
    "axes.grid": True,
    "axes.axisbelow": True,
    "grid.color": GRID,
    "grid.linewidth": 0.6,
    "xtick.color": MUTED,
    "ytick.color": MUTED,
    "xtick.labelcolor": INK2,
    "ytick.labelcolor": INK2,
    "xtick.major.size": 0,
    "ytick.major.size": 0,
    "legend.frameon": False,
    "legend.fontsize": 8,
    "figure.facecolor": SURFACE,
    "axes.facecolor": SURFACE,
    "savefig.facecolor": SURFACE,
    "svg.fonttype": "path",
    "text.color": INK,
})

W = 6.5  # inches: a full text width; every figure stays readable shrunk to one column


def save(fig, name):
    for ext, kw in (("png", dict(dpi=300)), ("svg", {})):
        fig.savefig(os.path.join(OUT, f"{name}.{ext}"), bbox_inches="tight", pad_inches=0.08, **kw)
    plt.close(fig)
    print("wrote", name)


def title(fig, text, sub=None):
    fig.suptitle(text, x=0.01, ha="left", fontsize=10.5, fontweight="bold", color=INK, y=1.0)
    if sub:
        fig.text(0.01, 0.955, sub, ha="left", va="top", fontsize=8.5, color=INK2)


def pct_axis(ax, axis="y"):
    f = matplotlib.ticker.PercentFormatter(100, decimals=0)
    (ax.yaxis if axis == "y" else ax.xaxis).set_major_formatter(f)


def day(s):
    return dt.date.fromisoformat(s)


# --- The head end (as results.py) ---------------------------------------------------------------
end = re.compile(r"slot (\d{4}-\d\d-\d\d) (\d\d:\d\d)Z: (done|ABORTED)\b.*?\b(\d+) of \d+ frames sent in (\d+) bursts(?: on (ms110d-[a-z0-9-]+))?")
span = re.compile(r"slot \S+ \S+Z: done, (\d\d):(\d\d):(\d\d) to (\d\d):(\d\d):(\d\d)Z")
aborted_at = re.compile(r"slot \S+ (\d\d):(\d\d)Z: ABORTED at (\d\d):(\d\d):(\d\d)Z")
# The 5 October test slots were logged without a slot time; results.py leaves them out, the
# head-end-over-time chart shows them on their own.
first_day = re.compile(r"slot (2026-10-05): done, (\d\d):(\d\d):(\d\d) to (\d\d):(\d\d):(\d\d)Z, (\d+) of \d+ frames sent in (\d+) bursts")
intake = re.compile(r"intake: (\S+) from \S+ to \S+, (\d+) octets")
sent, minutes, early, bid_day = {}, {}, [], {}


def secs(h, m, s):
    return int(h) * 3600 + int(m) * 60 + int(s)


with open(J, encoding="latin-1") as f:
    for line in f:
        if m := end.search(line):
            sent[(m[1], m[2])] = (int(m[4]), int(m[5]), m[6] or "", m[3])
            if s := span.search(line):
                minutes[(m[1], m[2])] = (secs(*s.groups()[3:]) - secs(*s.groups()[:3])) / 60
            elif a := aborted_at.search(line):
                # only for the per-day airtime chart: a cut-short slot's time to the abort
                minutes[("cut", m[1], m[2])] = (secs(*a.groups()[2:]) - secs(a[1], a[2], 0)) / 60
        elif m := first_day.search(line):
            early.append((int(m[8]), (secs(*m.groups()[4:7]) - secs(*m.groups()[1:4])) / 60))
        elif m := intake.search(line):
            bid_day.setdefault(m[1], line[:16])  # date and time, to compare with the last survey slot


def wave(mode):
    return {"ms110d-wn4": "WN4", "ms110d-wn4-long": "WN4 long interleaver", "ms110d-wn3": "WN3"}.get(mode, "not logged")


# --- The web SDR survey (as results.py) ---------------------------------------------------------
survey_rows = []
with open(SURVEY, newline="") as f:
    for r in csv.DictReader(f):
        if r["listening"] != "yes":
            continue
        d, h = r["slot_utc"].split()
        if (d, h) not in sent:
            continue
        r["name"] = "M9PSY" if r["sdr"].startswith("M9PSY") else r["sdr"]
        r["d"], r["h"] = d, h
        survey_rows.append(r)

sv = collections.defaultdict(lambda: dict(slots=0, heard=0, frames=0, sent=0, snr=[], bulletins=0, km=None))
sv_day = collections.defaultdict(lambda: [0, 0])
for r in survey_rows:
    s = sv[r["name"]]
    n = sent[(r["d"], r["h"])][0]
    fr = int(r["frames_heard"] or 0)
    s["km"] = int(r["km"]); s["slots"] += 1; s["frames"] += fr; s["sent"] += n
    s["heard"] += 1 if fr else 0
    if r["tone_snr_db_3khz"]:
        s["snr"].append(float(r["tone_snr_db_3khz"]))
    s["bulletins"] += int(r["bulletins_completed"] or 0)
    sv_day[(r["name"], r["d"])][0] += fr
    sv_day[(r["name"], r["d"])][1] += n
SDRS = sorted(sv, key=lambda k: sv[k]["km"])

# --- Listeners' reports (as results.py) ---------------------------------------------------------
day_rows = list(upto(csv.DictReader(open(R_DAYS, newline=""))))
deaf = {(r["callsign"], r["day"]) for r in day_rows if "AUD" in r["errors"] and r["slots_heard"] == "0"}
SDR_NAMES = {"wessex.zapto.org": "WESSEX", "nope.tunnel.ubersdr.org": "a web SDR"}
OWN_SDRS = {"m9twm-1.tunnel.ubersdr.org"}  # M9TWM's own UberSDR, the survey's M9TWM-1
audio_of = {(r["callsign"], r["day"]): r["audio"] for r in day_rows}


def who(call, d):
    a = audio_of.get((call, d), "sc")
    if a in ("sc", "sdrconnect", "soundmodem", "") or a in OWN_SDRS:
        return call
    return f"{call} via {SDR_NAMES.get(a, a.split('.')[0])}"


rows = [dict(r, who=who(r["callsign"], r["day"])) for r in upto(csv.DictReader(open(R_SLOTS, newline="")))
        if r["station"] in ("", "M0LTE", "GB7RDG") and (r["callsign"], r["day"]) not in deaf
        and sent.get((r["day"], r["slot_utc"]), (0,))[0] > 0]


# =================================================================================================
# 1. Survey: share heard against tone SNR and against distance
# =================================================================================================
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


def spearman(a, b):
    ra, rb = ranks(a), ranks(b)
    return float(np.corrcoef(ra, rb)[0, 1])


def fig_survey_snr_distance():
    share = [100 * sv[n]["frames"] / sv[n]["sent"] for n in SDRS]
    snr = [statistics.median(sv[n]["snr"]) for n in SDRS]
    km = [sv[n]["km"] for n in SDRS]
    fig, (a, b) = plt.subplots(1, 2, figsize=(W, 2.9), sharey=True)
    fig.subplots_adjust(wspace=0.08, top=0.80)
    title(fig, "Signal to noise predicts reception; distance does not",
          "Web SDR survey, 6 to 20 October 2026. One point per SDR; share of frames heard over the slots it listened to.")
    offs = {"G4WNC": (7, 2), "G4EYR": (7, -2), "STUEY3D": (7, -3), "WESSEX": (0, 9), "M9TWM-1": (7, -3),
            "EI4HQ": (-7, 5), "M9PSY": (-7, -4), "GI3JMC": (0, 9)}
    offs_km = {"G4WNC": (6, 4), "G4EYR": (6, 2), "STUEY3D": (6, -8), "WESSEX": (6, 2), "M9TWM-1": (6, -2),
               "EI4HQ": (-6, 6), "M9PSY": (6, -10), "GI3JMC": (-6, 6)}
    for ax, xs, xl, o in ((a, snr, "Median tone SNR in 3 kHz (dB)", offs), (b, km, "Distance from Reading (km)", offs_km)):
        ax.scatter(xs, share, s=42, color=BLUE, edgecolor=SURFACE, linewidth=1.5, zorder=3)
        for n, x, y in zip(SDRS, xs, share):
            dx, dy = o[n]
            ax.annotate(n, (x, y), xytext=(dx, dy), textcoords="offset points", fontsize=7.5, color=INK2,
                        ha="right" if dx < 0 else "center" if dx == 0 else "left", va="center")
        ax.set_xlabel(xl)
        ax.set_ylim(-5, 100)
    pct_axis(a)
    a.set_ylabel("Frames heard")
    a.set_xlim(-14, 30)
    b.set_xlim(0, 620)
    a.set_title(f"Against tone SNR (rank corr. {spearman(snr, share):.2f})", fontsize=8.5, color=INK)
    b.set_title(f"Against distance (rank corr. {spearman(km, share):.2f})", fontsize=8.5, color=INK)
    save(fig, "fig01-survey-snr-vs-distance")


# =================================================================================================
# 2. Survey: daily share per SDR as a heat map
# =================================================================================================
def fig_survey_daily():
    days = [dt.date(2026, 10, 6) + dt.timedelta(d) for d in range(15)]
    grid = np.full((len(SDRS), len(days)), np.nan)
    for i, n in enumerate(SDRS):
        for j, d in enumerate(days):
            fr, s = sv_day.get((n, d.isoformat()), (0, 0))
            if s:
                grid[i, j] = 100 * fr / s
    cmap = matplotlib.colors.LinearSegmentedColormap.from_list("seq", SEQ)
    cmap.set_bad(WASH)
    fig, ax = plt.subplots(figsize=(W, 3.0))
    fig.subplots_adjust(top=0.80)
    title(fig, "Daily share of frames heard by each survey SDR",
          "6 to 20 October 2026, SDRs nearest first. Pale grey: not listening. Low days on 6 and 11 October; better nearby in week 2.")
    im = ax.imshow(grid, cmap=cmap, vmin=0, vmax=100, aspect="auto")
    ax.grid(False)
    for s in ax.spines.values():
        s.set_visible(False)
    for i in range(len(SDRS)):
        for j in range(len(days)):
            v = grid[i, j]
            if not np.isnan(v):
                ax.text(j, i, f"{v:.0f}", ha="center", va="center", fontsize=6.5,
                        color=SURFACE if v >= 45 else INK2)
    ax.set_xticks(range(len(days)), [f"{d.day}" for d in days])
    ax.set_yticks(range(len(SDRS)), [f"{n}  {sv[n]['km']} km" for n in SDRS])
    ax.set_xticks(np.arange(-0.5, len(days)), minor=True)
    ax.set_yticks(np.arange(-0.5, len(SDRS)), minor=True)
    ax.grid(which="minor", color=SURFACE, linewidth=2)
    ax.tick_params(which="minor", length=0)
    ax.set_xlabel("Day of October 2026 (cell value: % of frames sent that day that the SDR heard)")
    ax.axvline(6.5, color=INK, linewidth=1.2)
    ax.text(6.6, -0.6, "week 2 ->", fontsize=7.5, color=INK2, ha="left", va="bottom")
    ax.text(6.4, -0.6, "<- week 1", fontsize=7.5, color=INK2, ha="right", va="bottom")
    cb = fig.colorbar(im, ax=ax, fraction=0.025, pad=0.01)
    cb.outline.set_visible(False)
    cb.ax.yaxis.set_major_formatter(matplotlib.ticker.PercentFormatter(100, decimals=0))
    cb.ax.tick_params(labelsize=7, length=0)
    save(fig, "fig02-survey-daily-heatmap")


# =================================================================================================
# 3. Listeners: daily share on their own equipment, 8 October to 7 November
# =================================================================================================
def fig_listeners_daily():
    per = collections.defaultdict(lambda: collections.defaultdict(lambda: [0, 0]))
    tot = collections.defaultdict(lambda: dict(all=[0, 0], oct=[0, 0], nov=[0, 0]))
    for r in rows:
        if " via " in r["who"]:
            continue
        fr, n = int(r["frames"] or 0), sent[(r["day"], r["slot_utc"])][0]
        per[r["who"]][r["day"]][0] += fr; per[r["who"]][r["day"]][1] += n
        t = tot[r["who"]]
        for k in ("all", "nov" if r["day"] >= "2026-11-02" else "oct"):
            t[k][0] += fr; t[k][1] += n
    names = sorted(per, key=lambda k: -tot[k]["all"][0] / tot[k]["all"][1])
    ncol = 2
    nrow = math.ceil(len(names) / ncol)
    fig, axes = plt.subplots(nrow, ncol, figsize=(W, 1.25 * nrow + 0.9), sharex=True, sharey=True)
    fig.subplots_adjust(hspace=0.55, wspace=0.08, top=1 - 0.95 / (1.25 * nrow + 0.7))
    title(fig, "Listeners' daily share of frames heard, own equipment",
          "8 October to 7 November 2026. Dots: one day. Black lines: share over all of October (to 1 Nov) and of November.\n"
          "Grey lines: 15 Oct leftover tier; 2 Nov long interleaver, 31 s bursts, bulk senders last (shaded from here).")
    x0, x1 = dt.date(2026, 10, 7), dt.date(2026, 11, 8)
    m15, m2 = dt.date(2026, 10, 15), dt.date(2026, 11, 2)
    for k, ax in enumerate(axes.flat):
        if k >= len(names):
            ax.set_visible(False)
            continue
        n = names[k]
        ds = sorted(per[n])
        xs = [day(d) + dt.timedelta(0.5) for d in ds]
        ys = [100 * per[n][d][0] / per[n][d][1] for d in ds]
        ax.axvspan(m2, x1, color=WASH, zorder=0, linewidth=0)
        for mx in (m15, m2):
            ax.axvline(mx, color=MUTED, linewidth=0.8, zorder=1)
        ax.scatter(xs, ys, s=16, color=BLUE, edgecolor=SURFACE, linewidth=1, zorder=3)
        t = tot[n]
        for key, a0, a1 in (("oct", x0, m2 - dt.timedelta(1)), ("nov", m2, x1)):
            fr, s = t[key]
            if s:
                v = 100 * fr / s
                dd = [day(d) + dt.timedelta(0.5) for d in ds if (d >= "2026-11-02") == (key == "nov")]
                ax.plot([min(dd) - dt.timedelta(0.4), max(dd) + dt.timedelta(0.4)], [v, v], color=INK, linewidth=1.6, zorder=2,
                        solid_capstyle="round")
                ax.text(max(dd) + dt.timedelta(0.8), v, f"{v:.0f}%", fontsize=7, color=INK, va="center")
        days_n = len(ds)
        ax.set_title(f"{n}  ({days_n} days, {100 * t['all'][0] / t['all'][1]:.0f}% overall)", fontsize=8, color=INK)
        ax.set_ylim(-5, 105)
        ax.set_yticks([0, 50, 100])
        pct_axis(ax)
        ax.set_xlim(x0, x1)
    for ax in axes[-1]:
        ax.set_xticks([dt.date(2026, 10, 8), m15, dt.date(2026, 10, 22), m2], ["8 Oct", "15 Oct", "22 Oct", "2 Nov"])
    save(fig, "fig03-listeners-daily-share")
    return {n: tot[n] for n in names}


# =================================================================================================
# 4. WN4 against WN3: frames heard per minute of slot time
# =================================================================================================
def fig_wn4_wn3():
    wf = collections.defaultdict(collections.Counter)

    def add(name, d, h, fr):
        n, _, mode, _ = sent[(d, h)]
        mode = "wn4" if mode.startswith("ms110d-wn4") else "wn3" if mode == "ms110d-wn3" else None
        if mode and n > 0 and (d, h) in minutes:
            wf[name][mode + "_f"] += fr; wf[name][mode + "_s"] += n; wf[name][mode + "_m"] += minutes[(d, h)]
    for r in survey_rows:
        add(r["name"], r["d"], r["h"], int(r["frames_heard"] or 0))
    for r in rows:
        add(r["who"], r["day"], r["slot_utc"], int(r["frames"] or 0))
    data = []
    for n, c in wf.items():
        if c["wn4_f"] + c["wn3_f"] == 0 or not c["wn4_s"] or not c["wn3_s"]:
            continue
        data.append((n, c["wn4_f"] / c["wn4_m"], c["wn3_f"] / c["wn3_m"], 100 * c["wn4_f"] / c["wn4_s"], 100 * c["wn3_f"] / c["wn3_s"]))
    data.sort(key=lambda t: t[1])
    fig, ax = plt.subplots(figsize=(W, 4.6))
    fig.subplots_adjust(top=0.88)
    title(fig, "Frames heard per minute of slot time: 1200 bps (WN4) against 600 bps (WN3)",
          "Survey and report receivers that heard both, 6 October to 7 November 2026. WN4 includes its long-interleaver slots.")
    y = np.arange(len(data))
    for i, (n, a4, a3, s4, s3) in enumerate(data):
        ax.plot([a3, a4], [i, i], color=AXIS, linewidth=2, zorder=1, solid_capstyle="round")
    ax.scatter([d[1] for d in data], y, s=40, color=BLUE, edgecolor=SURFACE, linewidth=1.5, zorder=3, label="WN4, 1200 bps")
    ax.scatter([d[2] for d in data], y, s=40, color=ORANGE, edgecolor=SURFACE, linewidth=1.5, zorder=3, label="WN3, 600 bps")
    ax.set_yticks(y, [d[0] for d in data])
    ax.set_xlabel("Frames heard per minute of slot time (start to closing ident)")
    ax.set_xlim(0, 17)
    ax.grid(axis="y", visible=False)
    ax.legend(loc="lower right", ncol=1, handletextpad=0.3)
    save(fig, "fig04-wn4-vs-wn3-frames-per-minute")
    return data


# =================================================================================================
# 5. The measured channel
# =================================================================================================
def fig_channel():
    ch = [r for r in rows if r["modes"]]

    def col(k):
        return np.array([float(r[k]) for r in ch if r[k]])
    panels = [("virtual_height_km", "Virtual height (km)", np.arange(100, 460, 10), None),
              ("two_f_delay_ms", "Second mode's delay after the first (ms)", np.arange(0.4, 3.2, 0.1), None),
              ("delay_spread_ms", "Delay spread (ms)", np.arange(0, 1.6, 0.05), 1.55),
              ("doppler_spread_hz", "Doppler spread (Hz)", np.arange(0, 2.05, 0.05), 2.0)]
    fig, axes = plt.subplots(2, 2, figsize=(W, 4.4))
    fig.subplots_adjust(hspace=0.62, wspace=0.18, top=0.84)
    title(fig, "The 40 m channel as receivers measured it",
          f"Listeners' report slots with a measurement, 8 October to 7 November 2026 ({len(ch)} slots; probe or data bursts).")
    out = {}
    for ax, (k, lab, bins, clip) in zip(axes.flat, panels):
        x = col(k)
        xc = np.clip(x, bins[0], clip - 1e-9) if clip else x
        ax.hist(xc, bins=bins, color=BLUE, edgecolor=SURFACE, linewidth=0.6)
        q1, md, q3 = np.percentile(x, [25, 50, 75])
        qs = statistics.quantiles(sorted(x), n=4)
        mdv = sorted(x)[len(x) // 2]
        out[k] = (mdv, qs[0], qs[2], len(x))
        ax.axvspan(qs[0], qs[2], color=WASH, zorder=0, linewidth=0)
        ax.axvline(mdv, color=INK, linewidth=1.2)
        nd = 0 if k == "virtual_height_km" else 2 if k in ("delay_spread_ms", "doppler_spread_hz") else 1
        ax.set_title(f"{lab}", fontsize=8.5, color=INK)
        ax.text(0.98, 0.95, f"median {mdv:.{nd}f}\nquartiles {qs[0]:.{nd}f} to {qs[2]:.{nd}f}\nn = {len(x)}" + (f"\nlast bar: {clip - (bins[1] - bins[0]):g} and over" if clip else ""),
                transform=ax.transAxes, ha="right", va="top", fontsize=7, color=INK2)
        ax.set_ylabel("Slots")
        ax.grid(axis="x", visible=False)
    save(fig, "fig05-channel-distributions")
    return out


# =================================================================================================
# 6. The head end over time
# =================================================================================================
def fig_headend():
    days = [dt.date(2026, 10, 5) + dt.timedelta(d) for d in range(34)]
    order = ["WN4", "WN4 long interleaver", "WN3", "not logged"]
    fr = {w: np.zeros(len(days)) for w in order}
    mn = {w: np.zeros(len(days)) for w in order}
    for (d, h), (n, b, mode, how) in sent.items():
        i = (day(d) - days[0]).days
        fr[wave(mode)][i] += n
        mn[wave(mode)][i] += minutes.get((d, h), minutes.get(("cut", d, h), 0))
    for n, m in early:  # 5 October, slot times not logged
        fr["not logged"][0] += n
        mn["not logged"][0] += m
    fig, (a, b) = plt.subplots(2, 1, figsize=(W, 4.4), sharex=True)
    fig.subplots_adjust(hspace=0.35, top=0.84)
    title(fig, "What the head end sent each day, by waveform",
          "GB7RDG head end journal, 5 October to 7 November 2026. Lines mark the changes to the sending strategy.")
    x = np.array([np.datetime64(d) for d in days])
    for ax, src, yl in ((a, fr, "Frames sent per day"), (b, mn, "Slot time per day (min)")):
        bottom = np.zeros(len(days))
        for w in order:
            if src[w].sum() == 0:
                continue
            ax.bar(x, src[w], bottom=bottom, width=0.72, color=WAVE_COLOR[w], edgecolor=SURFACE, linewidth=0.5,
                   label={"WN4": "WN4, 1200 bps", "WN3": "WN3, 600 bps", "WN4 long interleaver": "WN4, long interleaver",
                          "not logged": "waveform not logged (WN4)"}[w])
            bottom += src[w]
        ax.set_ylabel(yl)
        ax.grid(axis="x", visible=False)
    marks = [(dt.date(2026, 10, 5), "5 Oct: test slots"), (dt.date(2026, 10, 6), "6 Oct\nfill to budget,\nWN4/WN3 turns"),
             (dt.date(2026, 10, 15), "15 Oct\nleftover tier"), (dt.date(2026, 11, 2), "2 Nov\nlong interleaver,\n31 s bursts")]
    for ax in (a, b):
        for d, _ in marks[1:]:
            ax.axvline(np.datetime64(d) - np.timedelta64(12, "h"), color=INK, linewidth=0.8, zorder=0)
    ymax = a.get_ylim()[1]
    a.set_ylim(0, ymax * 1.42)
    for d, lab in marks[1:]:
        xx = np.datetime64(d) - np.timedelta64(12, "h")
        a.text(xx + np.timedelta64(4, "h"), ymax * 1.40, lab, fontsize=6.5, color=INK2, va="top", ha="left")
    a.text(np.datetime64(days[0]), fr["not logged"][0] + 30, marks[0][1], rotation=90, fontsize=6.5, color=INK2,
           ha="center", va="bottom")
    b.set_ylim(0, b.get_ylim()[1] * 1.1)
    b.legend(loc="upper center", bbox_to_anchor=(0.5, -0.2), ncol=4, fontsize=7, handlelength=1, columnspacing=1.2)
    b.xaxis.set_major_locator(matplotlib.dates.DayLocator(bymonthday=[5, 12, 19, 26, 2]))
    b.xaxis.set_major_formatter(matplotlib.dates.DateFormatter("%-d %b"))
    save(fig, "fig06-headend-per-day")
    return fr, mn


# =================================================================================================
# 7. Bulletins rebuilt against share heard
# =================================================================================================
def fig_rebuilt():
    taken = sum(1 for d in bid_day.values() if d < "2026-10-20T16:00")  # before the survey's last slot
    fig, ax = plt.subplots(figsize=(W * 0.62, 3.0))
    fig.subplots_adjust(top=0.78)
    title(fig, "Weak receivers still rebuild many bulletins",
          "Web SDR survey, 6 to 20 October 2026. One point per SDR.")
    xs = [100 * sv[n]["frames"] / sv[n]["sent"] for n in SDRS]
    ys = [sv[n]["bulletins"] for n in SDRS]
    ax.axhline(taken, color=MUTED, linewidth=0.8)
    ax.text(1, taken + 4, f"{taken} bulletins taken in before the survey's last slot", fontsize=7, color=INK2, va="bottom")
    ax.scatter(xs, ys, s=42, color=BLUE, edgecolor=SURFACE, linewidth=1.5, zorder=3)
    offs = {"G4WNC": (6, 4), "G4EYR": (6, -2), "STUEY3D": (-6, -8), "WESSEX": (6, -6), "M9TWM-1": (6, 0),
            "EI4HQ": (6, -6), "M9PSY": (-7, 0), "GI3JMC": (6, -6)}
    for n, x, y in zip(SDRS, xs, ys):
        dx, dy = offs[n]
        ax.annotate(n, (x, y), xytext=(dx, dy), textcoords="offset points", fontsize=7.5, color=INK2,
                    ha="right" if dx < 0 else "left", va="center")
    ax.set_xlim(-3, 90)
    ax.set_ylim(-10, 260)
    pct_axis(ax, "x")
    ax.set_xlabel("Frames heard")
    ax.set_ylabel("Bulletins rebuilt")
    save(fig, "fig07-bulletins-rebuilt-vs-share")
    return taken


# =================================================================================================
# Diagrams
# =================================================================================================
def box(ax, x, y, w, h, head, body=None, fc=SURFACE, ec=INK2, lw=1.0, ls="-"):
    ax.add_patch(FancyBboxPatch((x, y), w, h, boxstyle="round,pad=0,rounding_size=0.08", fc=fc, ec=ec, lw=lw, ls=ls))
    if body:
        ax.text(x + w / 2, y + h - 0.16, head, ha="center", va="top", fontsize=7.8, fontweight="bold", color=INK)
        ax.text(x + w / 2, y + h - 0.42, body, ha="center", va="top", fontsize=6.8, color=INK2, linespacing=1.3)
    else:
        ax.text(x + w / 2, y + h / 2, head, ha="center", va="center", fontsize=8, fontweight="bold", color=INK)


def arrow(ax, p, q, color=INK2, ls="-", lw=1.2, rad=0.0, text=None, tpos=None, tkw=None):
    ax.add_patch(FancyArrowPatch(p, q, arrowstyle="-|>,head_length=5,head_width=3", color=color, lw=lw, ls=ls,
                                 connectionstyle=f"arc3,rad={rad}", shrinkA=2, shrinkB=2))
    if text:
        ax.text(*tpos, text, fontsize=6.8, color=INK2, **(tkw or {}))


def canvas(w, h):
    fig = plt.figure(figsize=(w, h))
    ax = fig.add_axes([0, 0, 1, 1])
    ax.set_xlim(0, w * 2)
    ax.set_ylim(0, h * 2)
    ax.axis("off")
    return fig, ax


def fig_system():
    fig, ax = canvas(W, 3.5)  # 13 x 7 units
    ax.set_ylim(0.2, 7)
    ax.text(0.1, 6.8, "How a bulletin travels", fontsize=10.5, fontweight="bold", va="top")
    ax.text(0.1, 6.42, "One way on air; the only return path is the daily report, by ordinary packet mail.",
            fontsize=8, color=INK2, va="top")
    bw, bh, y1 = 2.42, 1.3, 4.3
    xs = [0.1, 2.8, 5.5, 8.2]
    box(ax, xs[0], y1, bw, bh, "GB7RDG BBS", "LinBPQ, Reading\nforwards bulletins to\npartner Q0HEAD (FBB)")
    box(ax, xs[1], y1, bw, bh, "Head end", "compress (zstd),\nRaptorQ pieces,\nsigned directory", ec=BLUE, lw=1.6)
    box(ax, xs[2], y1, bw, bh, "pdn-soundmodem", "MIL-STD-188-110D\nWN4 1200, WN3 600 bps\nIL2P frames, bursts")
    box(ax, xs[3], y1, bw, bh, "Flex 6500", "about 40 W\n7.0538 MHz centre\ntone, probe, CW ident")
    for i in range(3):
        arrow(ax, (xs[i] + bw, y1 + bh / 2), (xs[i + 1], y1 + bh / 2))
    # the sky
    ax.add_patch(FancyBboxPatch((10.85, 2.4), 2.05, 3.2, boxstyle="round,pad=0,rounding_size=0.3", fc=WASH, ec="none"))
    ax.text(11.87, 5.4, "40 m NVIS", ha="center", va="top", fontsize=8, fontweight="bold")
    ax.text(11.87, 5.05, "daylight only\nF layer about\n270 km up", ha="center", va="top", fontsize=6.8, color=INK2,
            linespacing=1.3)
    t = np.linspace(0, np.pi, 50)
    ax.plot(11.32 + 0.55 * (1 - np.cos(t)), 3.0 + 0.85 * np.sin(t), color=BLUE, lw=1.4)
    arrow(ax, (xs[3] + bw, y1 + bh / 2), (11.32, 3.05), rad=-0.25)
    # receive chain, right to left
    y2 = 1.6
    xr = [8.2, 5.5, 2.8]
    box(ax, xr[0], y2, bw, bh, "Listener's radio", "or a public web SDR\n7.052 MHz USB dial\naudio at 1800 Hz")
    box(ax, xr[1], y2, bw, bh, "Receiver", "modem, collect pieces,\nrebuild, check hash\nand signed directory", ec=BLUE, lw=1.6)
    box(ax, xr[2], y2, bw, bh, "Listener's BBS", "LinBPQ, FBB, XRouter;\nthe receiver forwards\nas partner Q0CAST")
    arrow(ax, (12.42, 3.05), (xr[0] + bw, y2 + bh / 2), rad=-0.25)
    arrow(ax, (xr[0], y2 + bh / 2), (xr[1] + bw, y2 + bh / 2))
    arrow(ax, (xr[1], y2 + bh / 2), (xr[2] + bw, y2 + bh / 2))
    # report path
    arrow(ax, (xr[2], y2 + 0.35), (xs[0] + bw / 2, y1), color=ORANGE, ls=(0, (3, 2)), rad=-0.35)
    ax.text(0.15, 2.6, "daily report:\npacket mail to\nMCAST@GB7RDG", fontsize=6.8, color=INK2, va="center",
            linespacing=1.3)
    ax.plot([2.8, 3.9], [0.55, 0.55], color=INK2, lw=1.2)
    ax.text(4.0, 0.55, "on the air, one way", fontsize=6.8, color=INK2, va="center")
    ax.plot([6.3, 7.4], [0.55, 0.55], color=ORANGE, lw=1.2, ls=(0, (3, 2)))
    ax.text(7.5, 0.55, "the only return path: ordinary BBS forwarding", fontsize=6.8, color=INK2, va="center")
    save(fig, "fig08-system-diagram")


def fig_slot():
    fig, (a, b) = plt.subplots(2, 1, figsize=(W, 2.9), gridspec_kw=dict(height_ratios=[1, 1], hspace=1.05))
    fig.subplots_adjust(top=0.80)
    title(fig, "Anatomy of a slot",
          "One hourly slot, drawn with the longest clear-channel wait. Times as configured from 2 November.")
    for ax in (a, b):
        ax.set_yticks([])
        ax.spines["left"].set_visible(False)
        ax.grid(False)
        ax.set_ylim(0, 1)
    # whole slot, minutes
    wait = 2.0
    pre = (10 + 1.5 + 6.5 + 4) / 60  # tone, gap, probe, CW ident
    fill = 7.8
    a.add_patch(Rectangle((0, 0.2), wait, 0.6, fc=SURFACE, ec=MUTED, hatch="////", lw=0.8))
    a.text(wait / 2, 0.5, "clear-channel\nwait, 0 to 2 min", ha="center", va="center", fontsize=6.8, color=INK,
           bbox=dict(fc=SURFACE, ec="none", pad=1))
    a.add_patch(Rectangle((wait, 0.2), pre, 0.6, fc=INK2, ec="none"))
    t = wait + pre
    burst, gap = 31 / 60, 2.5 / 60
    while t + burst < wait + fill:
        a.add_patch(Rectangle((t, 0.2), burst, 0.6, fc=BLUE, ec="none"))
        t += burst + gap
    a.add_patch(Rectangle((t, 0.2), 4 / 60, 0.6, fc=INK2, ec="none"))
    a.axvline(10, color=INK, lw=1.4)
    a.text(10.1, 0.5, "10 min\nlimit", ha="left", va="center", fontsize=7, color=INK)
    a.annotate("", xy=(wait, 1.06), xytext=(wait + fill, 1.06), arrowprops=dict(arrowstyle="<->", color=INK2, lw=0.8),
               annotation_clip=False)
    a.text(wait + fill / 2, 1.1, "filled to about 7.8 min on air", ha="center", va="bottom", fontsize=7, color=INK2)
    a.set_xlim(0, 10.9)
    a.spines["bottom"].set_bounds(0, 10)
    a.set_xticks(range(0, 11))
    a.set_xlabel("Minutes after the hour", labelpad=1)
    # zoom on the opening, seconds
    segs = [(0, 10, "tone\n1800 Hz, 10 s", WASH, INK2), (10, 1.5, "", SURFACE, MUTED),
            (11.5, 6.5, "probe\n6.5 s", "#9ec5f4", INK2), (18, 4, "CW\nident", INK2, None)]
    for x0, w, lab, fc, ec in segs:
        b.add_patch(Rectangle((x0, 0.2), w, 0.6, fc=fc, ec=ec or "none", lw=0.8))
        if lab:
            b.text(x0 + w / 2, 0.5, lab, ha="center", va="center", fontsize=6.8,
                   color=SURFACE if fc == INK2 else INK)
    b.annotate("gap 1.5 s", xy=(10.75, 0.8), xytext=(10.75, 1.12), ha="center", fontsize=6.8, color=INK2,
               arrowprops=dict(arrowstyle="-", color=MUTED, lw=0.6), annotation_clip=False)
    x = 22.5
    for k in range(2):
        b.add_patch(Rectangle((x, 0.2), 31, 0.6, fc=BLUE, ec="none"))
        b.add_patch(Rectangle((x, 0.2), 18, 0.6, fc="none", ec=SURFACE, lw=1.2, ls=(0, (2, 2))))
        if k == 0:
            b.text(x + 24.5, 0.5, "data burst,\nup to 31 s", ha="center", va="center", fontsize=6.8, color=SURFACE)
            b.annotate("18 s limit before 2 Nov", xy=(x + 18, 0.8), xytext=(x + 18, 1.12), ha="center", fontsize=6.8,
                       color=INK2, arrowprops=dict(arrowstyle="-", color=MUTED, lw=0.6), annotation_clip=False)
        x += 31 + 2.5
    b.set_xlim(0, 60)
    b.text(55, 1.12, "next burst after a gap", ha="center", fontsize=6.8, color=INK2)
    b.set_xlabel("Seconds after the transmitter keys (the opening, enlarged)", labelpad=1)
    save(fig, "fig09-slot-anatomy")


def fig_fountain():
    fig, ax = canvas(W, 3.2)  # 13 x 6.4
    ax.text(0.1, 6.25, "Fountain coding: any K+2 pieces, from any slots", fontsize=10.5, fontweight="bold", va="top")
    ax.text(0.1, 5.87, "A bulletin of K = 6 pieces; each slot sends new pieces never sent before; the listener misses many.",
            fontsize=8, color=INK2, va="top")
    # bulletin -> compressed -> K pieces
    ax.add_patch(Rectangle((0.2, 3.3), 1.0, 1.6, fc=SURFACE, ec=INK2, lw=1))
    for i in range(6):
        ax.plot([0.35, 1.05 - (0.2 if i == 5 else 0)], [4.6 - 0.22 * i] * 2, color=MUTED, lw=0.8)
    ax.text(0.7, 3.1, "bulletin\n5.3 kB", ha="center", va="top", fontsize=6.8, color=INK2)
    arrow(ax, (1.3, 4.1), (1.9, 4.1))
    ax.add_patch(Rectangle((2.0, 3.6), 0.75, 1.0, fc=SURFACE, ec=INK2, lw=1))
    ax.text(2.37, 4.1, "zstd", ha="center", va="center", fontsize=6.8)
    ax.text(2.37, 3.1, "compressed\nabout 30%", ha="center", va="top", fontsize=6.8, color=INK2)
    arrow(ax, (2.85, 4.1), (3.35, 4.1))
    s = 0.34
    for i in range(6):
        ax.add_patch(Rectangle((3.45 + i * (s + 0.06), 3.93), s, s, fc=INK2, ec="none"))
    ax.text(3.45 + 3 * (s + 0.06) - 0.03, 3.1, "K = 6 source pieces\n240 bytes each", ha="center", va="top", fontsize=6.8,
            color=INK2)
    arrow(ax, (5.9, 4.1), (6.45, 4.1))
    ax.text(6.55, 4.1, "RaptorQ makes an endless\nstream of distinct pieces\n(numbers never reused)",
            fontsize=6.8, color=INK2, va="center")
    # three slots
    heard = [[1, 0, 0, 1, 0], [0, 1, 1, 0, 1], [1, 0, 1, 1, 0]]
    total = 0
    y0 = 1.6
    for k, row in enumerate(heard):
        x0 = 0.4 + k * 4.25
        ax.text(x0, y0 + 0.62, f"slot {k + 1}" + ("  (10:00)", "  (11:00)", "  (14:00)")[k], fontsize=7.5,
                fontweight="bold", va="bottom")
        for i, h in enumerate(row):
            esi = 1 + k * 5 + i
            x = x0 + i * 0.66
            ax.add_patch(Rectangle((x, y0), 0.52, 0.52, fc=BLUE if h else WASH, ec="none"))
            ax.text(x + 0.26, y0 + 0.26, f"{esi}", ha="center", va="center", fontsize=6.5, zorder=3,
                    color=SURFACE if h else INK2, bbox=None if h else dict(fc=WASH, ec="none", pad=0.5))
            if not h:
                ax.plot([x + 0.06, x + 0.46], [y0 + 0.06, y0 + 0.46], color=AXIS, lw=0.9, zorder=1)
                ax.plot([x + 0.06, x + 0.46], [y0 + 0.46, y0 + 0.06], color=AXIS, lw=0.9, zorder=1)
        total += sum(row)
        ax.text(x0, y0 - 0.25, f"heard {sum(row)} of 5; held {total} of 8", fontsize=6.8, color=INK2, va="top")
    ax.text(0.4, 0.8, "8 = K+2 pieces held after slot 3: the bulletin is rebuilt, checked against its hash, and passed to the BBS.",
            fontsize=7.5, color=INK, va="center")
    ax.text(0.4, 0.35, "Which pieces were lost does not matter, only how many arrived.", fontsize=7.5, color=INK2, va="center")
    ax.add_patch(Rectangle((9.7, 0.19), 0.32, 0.32, fc=BLUE, ec="none"))
    ax.text(10.1, 0.35, "heard", fontsize=6.8, color=INK2, va="center")
    ax.add_patch(Rectangle((10.85, 0.19), 0.32, 0.32, fc=WASH, ec="none"))
    ax.plot([10.89, 11.13], [0.23, 0.47], color=AXIS, lw=0.9)
    ax.plot([10.89, 11.13], [0.47, 0.23], color=AXIS, lw=0.9)
    ax.text(11.25, 0.35, "lost", fontsize=6.8, color=INK2, va="center")
    save(fig, "fig10-fountain-coding")


R_E, C_KM_MS = 6371.0, 299.792458


def delay(n, h, d):
    a = (d / R_E) / (2 * n)
    L = math.sqrt(R_E ** 2 + (R_E + h) ** 2 - 2 * R_E * (R_E + h) * math.cos(a))
    return 2 * n * L / C_KM_MS


def gap(h, d):
    return delay(2, h, d) - delay(1, h, d)


def height_for(g, d):
    lo, hi = 50.0, 800.0
    for _ in range(60):
        mid = (lo + hi) / 2
        lo, hi = (mid, hi) if gap(mid, d) < g else (lo, mid)
    return (lo + hi) / 2


def fig_mirror(ch):
    fig, (a, b) = plt.subplots(1, 2, figsize=(W, 2.9), gridspec_kw=dict(width_ratios=[1.25, 1], wspace=0.28))
    fig.subplots_adjust(top=0.78)
    title(fig, "Mirror model: why the gap between one hop and two gives the height",
          "Left: 1F and 2F paths to a receiver 300 km away, mirror at 266 km (drawn flat; the formula uses a round Earth).")
    d, h = 300, 266
    a.set_aspect("equal")
    a.axhline(0, color=INK2, lw=1.2)
    a.axhline(h, color=MUTED, lw=0.9, ls=(0, (4, 3)))
    a.text(d + 12, h, f"mirror at\nvirtual\nheight h\n= {h} km", fontsize=6.8, color=INK2, va="center")
    a.plot([0, d / 2, d], [0, h, 0], color=BLUE, lw=1.8, label="1F: one hop")
    a.plot([0, d / 4, d / 2, 3 * d / 4, d], [0, h, 0, h, 0], color=ORANGE, lw=1.8, label="2F: two hops")
    for x, lab in ((0, "Reading"), (d, "receiver")):
        a.plot([x], [0], marker="o", ms=6, color=INK, mec=SURFACE, mew=1.5, zorder=4)
        a.text(x, -18, lab, ha="center", va="top", fontsize=7, color=INK2)
    a.text(d / 2, -18, "ground\nbounce", ha="center", va="top", fontsize=6.5, color=INK2)
    a.set_xlim(-20, d + 95)
    a.set_ylim(-75, h + 50)
    a.set_xticks([0, 100, 200, 300])
    a.set_yticks([0, 100, 200, h])
    a.set_xlabel("Ground distance (km)")
    a.set_ylabel("Height (km)")
    a.grid(False)
    a.legend(loc="upper left", fontsize=7, bbox_to_anchor=(0, 1.13), ncol=2, handlelength=1.2)
    t1, t2 = delay(1, h, d), delay(2, h, d)
    a.text(d + 12, h * 0.35, f"1F {t1:.2f} ms\n2F {t2:.2f} ms\ngap {t2 - t1:.2f} ms", ha="left", va="center",
           fontsize=6.8, color=INK)
    # right: gap against height
    hs = np.linspace(150, 450, 200)
    cols = ["#9ec5f4", "#5598e7", "#256abf", "#0d366b"]
    for dd, c in zip((100, 300, 500, 800), cols):
        b.plot(hs, [gap(x, dd) for x in hs], color=c, lw=1.8)
        b.text(455, gap(450, dd) + {100: 0.06, 300: -0.04}.get(dd, 0), f"{dd} km", fontsize=6.5, color=INK2, va="center")
    md, q1, q3 = ch["two_f_delay_ms"][:3]
    b.axhspan(q1, q3, color=WASH, zorder=0, lw=0)
    b.axhline(md, color=INK, lw=1)
    hmd = height_for(md, 300)
    b.plot([hmd, hmd], [0.6, md], color=INK, lw=0.8, ls=(0, (2, 2)))
    b.text(hmd + 6, 0.68, f"{hmd:.0f} km at 300 km", fontsize=6.8, color=INK)
    b.text(345, 1.0, f"band: measured\nmedian {md:.1f} ms,\nquartiles {q1:.1f} to {q3:.1f}", fontsize=6.5, color=INK2,
           va="center")
    b.set_xlim(150, 500)
    b.set_xticks([150, 250, 350, 450])
    b.set_ylim(0.6, 3.1)
    b.set_xlabel("Virtual height (km)")
    b.set_ylabel("Delay of 2F after 1F (ms)")
    b.set_title("Gap against height, by ground distance", fontsize=8.5, color=INK)
    save(fig, "fig11-mirror-model")


# =================================================================================================
if __name__ == "__main__":
    fig_survey_snr_distance()
    fig_survey_daily()
    lst = fig_listeners_daily()
    wf = fig_wn4_wn3()
    ch = fig_channel()
    fr, mn = fig_headend()
    taken = fig_rebuilt()
    fig_system()
    fig_slot()
    fig_fountain()
    fig_mirror(ch)
    # Numbers to check against results.md.
    print("\nchecks:")
    print("  survey:", {n: (f"{100 * sv[n]['frames'] / sv[n]['sent']:.0f}%", f"{statistics.median(sv[n]['snr']):.0f} dB", sv[n]["bulletins"]) for n in SDRS})
    print("  listeners (own equipment):", {n: tuple(f"{100 * t[k][0] / t[k][1]:.0f}%" if t[k][1] else "-" for k in ("all", "oct", "nov")) for n, t in lst.items()})
    print("  WN4/WN3 frames per min:", {d[0]: (round(d[1], 1), round(d[2], 1)) for d in wf})
    print("  channel:", {k: tuple(round(x, 2) for x in v) for k, v in ch.items()})
    print("  frames by waveform (incl. 5 Oct):", {w: int(v.sum()) for w, v in fr.items()}, "slot hours:", round(sum(v.sum() for v in mn.values()) / 60, 1))
    print("  bulletins taken in by 20 Oct:", taken)
    print("  mirror table check (gap ms at 100/300/500/800 km, 290 km):", [round(gap(290, d), 2) for d in (100, 300, 500, 800)])
