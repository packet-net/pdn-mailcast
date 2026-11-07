#!/usr/bin/python3
"""pdn-mailcast reception survey collector (CT 150, from 2026-10-06).

Run by mailcast-survey.timer at 5 and 15 minutes past each hour.
  snap  (at :05) records whether each receiver is listening, mid-slot.
  row   (at :15) appends one CSV row per receiver for the slot just gone,
        then rewrites that day's summary table.
  summary [YYYY-MM-DD] only rewrites a day's summary table.
With no argument it picks by the minute: before :10 is snap, else row.
"""
import csv, datetime as dt, json, os, re, subprocess, sys, urllib.request, base64

OUT = "/var/lib/mailcast-survey"
CSV = os.path.join(OUT, "slots.csv")
STATE = os.path.join(OUT, "state.json")

# The receivers: systemd unit and status page port. Which SDR each listens to is read
# from its status page, so a receiver moved to another SDR is recorded under the new one.
RECEIVERS = [
    ("pdn-mailcast-receiver@g4wnc",   8133),
    ("pdn-mailcast-receiver@g4eyr",   8137),
    ("pdn-mailcast-receiver@stuey3d", 8132),
    ("pdn-mailcast-receiver@wessex",  8131),
    ("pdn-mailcast-receiver@m9twm-1", 8134),
    ("pdn-mailcast-receiver@ei4hq",   8135),
    ("pdn-mailcast-receiver",         8130),   # the original instance, LAN page with a password
    ("pdn-mailcast-receiver@gi3jmc",  8136),
]

# Known SDRs: host -> name, km and bearing from the IO91lk centre (51.4375 N, 1.0417 W),
# from the coordinates in https://instances.ubersdr.org/api/instances.
SDRS = {
    "g4wnc.tunnel.ubersdr.org":     ("G4WNC",    83, 218),
    "g4eyr.tunnel.ubersdr.org":     ("G4EYR",    89, 137),
    "stuey3d.tunnel.ubersdr.org":   ("STUEY3D",  97,   8),
    "wessex.zapto.org":             ("WESSEX",  135, 236),
    "m9twm-1.tunnel.ubersdr.org":   ("M9TWM-1", 221, 351),
    "ei4hq.tunnel.ubersdr.org":     ("EI4HQ",   502, 278),
    "m9psy-1.instance.ubersdr.org": ("M9PSY-1", 533, 344),
    "m9psy.tunnel.ubersdr.org":     ("M9PSY",   534, 344),
    "gi3jmc.tunnel.ubersdr.org":    ("GI3JMC",  544, 320),
}

# GB7RDG's waveform turns from head end v0.5.0 (src/Mailcast.HeadEnd/Slot/Waveforms.cs):
# slot n of the day on day d uses MODES[(n + d) % len(MODES)], d being .NET's DayNumber
# (date.toordinal() - 1) and n the hour for hourly slots from 00:00. Calibrated on
# 2026-10-06: 11:00 UTC was WN3, 12:00 UTC WN4. The receiver (0.4.2) does not report the
# waveform it decoded, so this is the head end's rule, not an observation.
MODES = ["WN4", "WN3"]
MODES_FROM = dt.datetime(2026, 10, 6, 11, 0, tzinfo=dt.timezone.utc)

FIELDS = ["slot_utc", "sdr", "host", "km", "bearing_deg", "listening",
          "tone_snr_db_3khz", "tone_offset_hz", "frames_heard", "frames_delta_total",
          "bulletins_completed", "bulletins_complete_total", "receiver_version",
          "waveform", "note"]

TONE = re.compile(r"^tone: ([0-9.]+) Hz, ([+-]?[0-9.]+) Hz from \d+ Hz, SNR (-?[0-9.]+) dB in 3 kHz")


def ascii(s):
    s = str(s).replace("\u2014", "-").replace("\u2013", "-").replace("\u00b7", ",").replace("\u2192", "->")
    return s.encode("ascii", "replace").decode("ascii")


def one_line(s):
    return ascii(s).replace("\n", " ").replace("\r", " ")


def waveform(slot):
    if slot < MODES_FROM:
        return "-"
    n = slot.hour if slot.minute == 0 else -1
    if n < 0:
        return "-"
    return MODES[(n + slot.date().toordinal() - 1) % len(MODES)]


def main_page_password():
    """The original instance's LAN password, read from its config (never printed)."""
    try:
        text = open("/etc/pdn-mailcast/receiver.json").read()
        text = "\n".join(l for l in text.splitlines() if not l.strip().startswith("//"))
        text = re.sub(r",(\s*[}\]])", r"\1", text)
        return json.loads(text).get("web", {}).get("password", "")
    except Exception:
        return ""


def status(port, password):
    req = urllib.request.Request(f"http://127.0.0.1:{port}/api/status")
    if password:
        req.add_header("Authorization", "Basic " + base64.b64encode(f"survey:{password}".encode()).decode())
    with urllib.request.urlopen(req, timeout=10) as r:
        return json.load(r)


def journal(unit, since, until):
    try:
        out = subprocess.run(["journalctl", "-u", unit, "-o", "cat", "--no-pager",
                              "--since", since.strftime("%Y-%m-%d %H:%M:%S UTC"),
                              "--until", until.strftime("%Y-%m-%d %H:%M:%S UTC")],
                             capture_output=True, text=True, timeout=30).stdout
        return out.splitlines()
    except Exception:
        return []


def parse_time(s):
    return dt.datetime.fromisoformat(s.replace("Z", "+00:00")) if s else None


def sdr_of(j, unit):
    host = ((j or {}).get("audio") or {}).get("source", "").removeprefix("ubersdr:")
    name, km, brg = SDRS.get(host, (host.split(".")[0].upper() or unit, "", ""))
    return name, host, km, brg


def load_state():
    try:
        return json.load(open(STATE))
    except Exception:
        return {}


def save_state(st):
    tmp = STATE + ".tmp"
    json.dump(st, open(tmp, "w"), indent=1)
    os.replace(tmp, STATE)


def snap(now):
    st = load_state()
    pw = main_page_password()
    slot = now.replace(minute=0, second=0, microsecond=0)
    for unit, port in RECEIVERS:
        try:
            j = status(port, pw if port == 8130 else "")
            name = sdr_of(j, unit)[0]
            a = j["audio"]
            st.setdefault(name, {})["snap"] = {"slot": slot.isoformat(), "phase": a.get("phase"), "problem": a.get("problem")}
        except Exception as e:
            st.setdefault(unit, {})["snap"] = {"slot": slot.isoformat(), "phase": "unreachable", "problem": str(e)}
    save_state(st)


def row(now, force=False):
    st = load_state()
    pw = main_page_password()
    slot = now.replace(minute=0, second=0, microsecond=0)
    if now - slot < dt.timedelta(minutes=12):
        slot -= dt.timedelta(hours=1)
    hhmm = slot.strftime("%H:%M")
    rows = []
    for unit, port in RECEIVERS:
        try:
            j = status(port, pw if port == 8130 else "")
            err = None
        except Exception as e:
            j, err = None, e
        name, host, km, brg = sdr_of(j, unit)
        s = st.setdefault(name if j is not None else unit, {})
        if s.get("last_row") == slot.isoformat() and not force:
            continue
        r = dict(slot_utc=slot.strftime("%Y-%m-%d %H:%M"), sdr=name, host=host, km=km, bearing_deg=brg,
                 listening="no", tone_snr_db_3khz="", tone_offset_hz="", frames_heard=0,
                 frames_delta_total="", bulletins_completed=0, bulletins_complete_total="",
                 receiver_version="", waveform=waveform(slot), note="")
        notes = []
        if j is None:
            notes.append(f"status page unreachable: {err}")
        else:
            today = (j.get("schedule") or {}).get("today") or []
            if hhmm not in today:
                continue  # not one of GB7RDG's slots (night)
            r["receiver_version"] = j.get("version", "")
            lines = journal(unit, slot - dt.timedelta(minutes=3), slot + dt.timedelta(minutes=13))
            sl = j.get("slot")
            if sl and parse_time(sl.get("scheduled")) == slot:
                r["frames_heard"] = sl.get("framesHeard", 0)
                t = sl.get("tone")
                if t:
                    r["tone_snr_db_3khz"] = t.get("snrDb")
                    r["tone_offset_hz"] = t.get("offsetHz")
            if r["tone_snr_db_3khz"] == "":
                # A restart forgets the slot; the tone is in the journal all the same.
                for l in lines:
                    m = TONE.match(l)
                    if m and "ignored" not in l:
                        r["tone_snr_db_3khz"], r["tone_offset_hz"] = float(m.group(3)), float(m.group(2))
                        notes.append("tone from the journal")
                        break
            if any(l.startswith("pdn-mailcast receiver ") for l in lines):
                notes.append("receiver restarted in the window")
            total = j.get("framesHeard", 0)
            prev = s.get("total_frames")
            r["frames_delta_total"] = total if (prev is None or total < prev) else total - prev
            s["total_frames"] = total
            done = sorted(b["bid"] for b in j.get("bulletins") or [] if b.get("complete"))
            old = set(s.get("complete_bids", []))
            r["bulletins_completed"] = len([b for b in done if b not in old])
            r["bulletins_complete_total"] = len(done)
            s["complete_bids"] = sorted(old | set(done))
            webtimes = ((j.get("schedule") or {}).get("webSdr") or {}).get("times") or []
            opened = any(l.startswith("audio: web receiver") for l in lines)
            sn = s.get("snap") or {}
            if sn.get("slot") == slot.isoformat():
                listening = sn.get("phase") == "listening" or opened
                if sn.get("phase") != "listening":
                    notes.append(f"at :05 the web SDR was {sn.get('phase')}" + (f": {sn['problem']}" if sn.get("problem") else ""))
            else:
                listening = opened
                if not opened:
                    notes.append("no :05 snapshot; journal shows no web SDR open")
            if hhmm not in webtimes and not listening:
                notes.append("not one of this receiver's web SDR slots today")
            if any("locked on one burst" in l for l in lines):
                notes.append("modem let go of a lock on an unreadable signal")
            r["listening"] = "yes" if listening else "no"
        r["note"] = one_line("; ".join(notes))[:200]
        s["last_row"] = slot.isoformat()
        rows.append(r)
    new = not os.path.exists(CSV)
    with open(CSV, "a", newline="") as f:
        w = csv.DictWriter(f, fieldnames=FIELDS)
        if new:
            w.writeheader()
        for r in rows:
            w.writerow(r)
    save_state(st)
    summary(slot.strftime("%Y-%m-%d"))


def summary(day):
    if not os.path.exists(CSV):
        return
    cells, hours, km, wave = {}, set(), {}, {}
    for r in csv.DictReader(open(CSV)):
        if not r["slot_utc"].startswith(day):
            continue
        h = r["slot_utc"][11:16]
        hours.add(h)
        km[r["sdr"]] = int(r["km"]) if r["km"].isdigit() else 99999
        wave[h] = r.get("waveform") or "-"
        if r["listening"] != "yes":
            c = "off"
        else:
            c = r["frames_heard"]
            c += " " + (f"{float(r['tone_snr_db_3khz']):+.0f}dB" if r["tone_snr_db_3khz"] else "nt")
            if r["bulletins_completed"] not in ("", "0"):
                c += f" b{r['bulletins_completed']}"
        cells[(h, r["sdr"])] = c
    names = sorted(km, key=lambda n: (km[n], n))
    width = 13
    out = [f"pdn-mailcast reception survey, {day} (UTC). GB7RDG, IO91lk, 7.0538 MHz.", "",
           "Each cell: frames heard in the slot, then tone SNR in 3 kHz ('nt' = no tone heard),",
           "then 'bN' if N bulletins completed in it. 'off' = that receiver was not listening.",
           "Blank = no receiver on that SDR at that hour. wave = the slot's waveform by the",
           "head end's turn rule.", ""]
    out.append("hour  wave" + "".join(n.rjust(width) for n in names))
    out.append("km        " + "".join(str(km[n]).rjust(width) for n in names))
    out.append("-" * (10 + width * len(names)))
    for h in sorted(hours):
        out.append(h.ljust(6) + wave.get(h, "-").ljust(4) + "".join(cells.get((h, n), "").rjust(width) for n in names))
    out.append("")
    tmp = os.path.join(OUT, f".daily-{day}.tmp")
    open(tmp, "w").write(ascii("\n".join(out)))
    os.replace(tmp, os.path.join(OUT, f"daily-{day}.txt"))


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    now = dt.datetime.now(dt.timezone.utc)
    mode = sys.argv[1] if len(sys.argv) > 1 else ("snap" if now.minute < 10 else "row")
    if mode == "snap":
        snap(now)
    elif mode == "row":
        row(now, force="--force" in sys.argv)
    elif mode == "summary":
        summary(sys.argv[2] if len(sys.argv) > 2 else now.strftime("%Y-%m-%d"))
