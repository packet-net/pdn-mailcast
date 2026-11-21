#!/usr/bin/env python3
"""Which audio centre suits a 2.4 kHz transmit filter?

Takes one slot rendered offline, puts it through a 300 to 2700 Hz filter with the signal
centred on 1500 Hz or on 1800 Hz of the transmit audio, adds noise, and decodes each with the
receiver, offline. Nothing here runs in real time, so the machine's load cannot change a result.

    pdn-mailcast-headend --wav slot.wav --date 2026-11-21 --time 12:00 --bulletins DIR --config headend.json
    filter-study.py slot.wav RECEIVER-CONFIG-DIR 1.5,3

RECEIVER-CONFIG-DIR holds a receiver.json and a stations.json naming the head end and its key.
The noise levels are the data's level over the noise in a 3 kHz bandwidth, in dB. Two runs are
made at each, with different noise.
"""
import json, os, subprocess, sys, wave
from concurrent.futures import ThreadPoolExecutor
import numpy as np
from scipy import signal
RATE = 48000
w = wave.open(sys.argv[1]); x = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2") / 32768.0
frames = x[: len(x) // 960 * 960].reshape(-1, 960); rms = np.sqrt((frames ** 2).mean(axis=1))
level = np.median(rms[rms > 0.01])          # the data bursts: they are most of what is loud
print("data level %.1f dBFS" % (20 * np.log10(level)), flush=True)
def band(lo, hi):
    return signal.firwin(1025, [lo, hi], pass_zero=False, fs=RATE)
# The rendered signal is centred on 1800 Hz, where the receiver hears it. A transmit filter
# passing 300-2700 Hz of transmit audio passes 300-2700 Hz of it with the modem at 1800 Hz, and
# 600-3000 Hz of it with the modem at 1500 Hz (the whole signal sits 300 Hz lower in the filter).
cases = {"no filter": None, "modem at 1500 Hz": (600, 3000), "modem at 1800 Hz": (300, 2700)}
def run(job):
    name, snr, seed = job
    y = x if cases[name] is None else signal.lfilter(band(*cases[name]), 1.0, x)
    noise = level / 10 ** (snr / 20) * np.sqrt(RATE / 2 / 3000)
    y = y + noise * np.random.default_rng(seed).standard_normal(len(y))
    tag = "%s-%s-%d" % (name.replace(" ", ""), snr, seed); path = "/tmp/%s.wav" % tag
    out = wave.open(path, "w"); out.setnchannels(1); out.setsampwidth(2); out.setframerate(RATE)
    out.writeframes((np.clip(y, -1, 1) * 32767).astype("<i2").tobytes()); out.close()
    os.makedirs("/tmp/c-" + tag, exist_ok=True)
    cfg = json.load(open(sys.argv[2] + "/receiver.json")); cfg["stateDirectory"] = "/tmp/s-" + tag; cfg.setdefault("web", {})["port"] = 20000 + abs(hash(tag)) % 20000
    json.dump(cfg, open("/tmp/c-%s/receiver.json" % tag, "w")); subprocess.run(["cp", sys.argv[2] + "/stations.json", "/tmp/c-" + tag])
    log = subprocess.run(["pdn-mailcast-receiver", "--decode", path, "--config", "/tmp/c-%s/receiver.json" % tag],
                         capture_output=True, text=True).stdout
    os.remove(path)
    heard = [l for l in log.splitlines() if l.startswith("decode:") and "frames heard" in l]
    return name, snr, seed, int(heard[0].split()[1]) if heard else -1
jobs = [(n, s, seed) for s in (float(a) for a in sys.argv[3].split(",")) for n in cases for seed in (1, 2)]
with ThreadPoolExecutor(3) as pool:
    for r in pool.map(run, jobs):
        print("%-18s %4.1f dB over the noise, run %d: %d frames" % r, flush=True)
