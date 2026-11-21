#!/usr/bin/env python3
"""The bench's simulated air: one SSB transmitter, any number of SSB receivers.

A transmitting pdn-soundmodem writes its audio to a FIFO (its pipe: device). This program is
the two radios and the air between them. It treats that audio as upper sideband on the
transmitter's dial, and hands each receiver the audio an upper sideband receiver on its own
dial would produce:

    receive audio frequency = transmit audio frequency + transmit dial - receive dial

A dial is a fixed number of Hz, or rig:HOST:PORT to follow a Hamlib rigctld (the dummy rig
stands in for a real one, so a station that retunes its rig is heard where it tuned to).
Each end has a filter, LOW@HIGH in Hz of its own audio: what the radio passes. A receiver can
have white noise added, given as its level in a 3 kHz bandwidth in dB below full scale.

It also measures every transmission as it leaves the transmitter and writes one JSON line for
each to the log: when, how long, the strongest frequency, and the band holding 98% of the
power, as audio and as RF. A steady tone and a data burst are told apart by that width.

No fading, and no noise unless asked for: it answers "do these settings put the signal where the other end
listens", not "how well does it decode on a real path".
"""
import argparse
import json
import os
import queue
import socket
import subprocess
import sys
import threading
import time

import numpy as np
from scipy import signal

RATE = 48000
TAPS = 1025


def log(path, record):
    record["utc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    line = json.dumps(record)
    with open(path, "a") as f:
        f.write(line + "\n")
    print(line, flush=True)


class Dial:
    """A dial in Hz: fixed, or read from rigctld every 250 ms."""

    def __init__(self, spec, name, log_path):
        self.name = name
        self.log_path = log_path
        self.mode = None
        if spec.startswith("rig:"):
            host, port = spec[4:].rsplit(":", 1)
            self.hz = None
            self.address = (host, int(port))
            threading.Thread(target=self._poll, daemon=True).start()
            while self.hz is None:
                time.sleep(0.1)
        else:
            self.hz = float(spec)
            log(log_path, {"event": "dial", "radio": name, "hz": self.hz, "fixed": True})

    def _ask(self, sock, command):
        sock.sendall(command)
        return sock.recv(4096).decode().split("\n")

    def _poll(self):
        while True:
            try:
                with socket.create_connection(self.address, timeout=2) as sock:
                    while True:
                        hz = float(self._ask(sock, b"f\n")[0])
                        mode = " ".join(self._ask(sock, b"m\n")[:2])
                        if hz != self.hz or mode != self.mode:
                            log(self.log_path, {"event": "dial", "radio": self.name, "hz": hz, "mode": mode})
                        self.hz, self.mode = hz, mode
                        time.sleep(0.25)
            except (OSError, ValueError):
                time.sleep(0.5)


def bandpass(low, high):
    """Complex taps passing low..high Hz of positive frequencies only, gain 1."""
    lowpass = signal.firwin(TAPS, (high - low) / 2, fs=RATE)
    n = np.arange(TAPS)
    return lowpass * np.exp(2j * np.pi * ((low + high) / 2) * n / RATE)


class Filter:
    def __init__(self, low, high):
        self.taps = bandpass(low, high)
        self.state = np.zeros(TAPS - 1, dtype=complex)

    def run(self, x):
        y, self.state = signal.lfilter(self.taps, 1.0, x, zi=self.state)
        return y


class Receiver:
    """One receiving radio: shifts by the difference of the dials, filters, writes its audio out."""

    def __init__(self, spec, tx_dial, log_path):
        parts = spec.split("@")
        self.target, dial = parts[0], parts[1]
        low, high = (float(parts[2]), float(parts[3])) if len(parts) > 3 else (50.0, 3600.0)
        # Optional fifth field: white noise, as its level in a 3 kHz bandwidth in dB below full scale.
        self.noise = 10 ** (-float(parts[4]) / 20) * np.sqrt(RATE / 2 / 3000) if len(parts) > 4 else 0.0
        self.random = np.random.default_rng()
        self.name = os.path.basename(self.target)
        self.dial = Dial(dial, "rx " + self.name, log_path)
        self.tx_dial = tx_dial
        self.filter = Filter(low, high)
        self.phase = 0.0
        self.queue = queue.Queue()
        if self.target.startswith("alsa:"):
            # A sound card runs all the time, so this one is fed silence between transmissions.
            self.player = subprocess.Popen(
                ["aplay", "-q", "-t", "raw", "-f", "FLOAT_LE", "-r", str(RATE), "-c", "1",
                 "-D", self.target[5:], "--buffer-time=400000", "-"], stdin=subprocess.PIPE)
            threading.Thread(target=self._play, daemon=True).start()
        else:
            if not os.path.exists(self.target):
                os.mkfifo(self.target)
            self.fd = os.open(self.target, os.O_RDWR)
            threading.Thread(target=self._write, daemon=True).start()

    def feed(self, analytic):
        shift = self.tx_dial.hz - self.dial.hz
        n = len(analytic)
        phases = self.phase + 2 * np.pi * shift * np.arange(n) / RATE
        self.phase = (self.phase + 2 * np.pi * shift * n / RATE) % (2 * np.pi)
        audio = 2 * np.real(self.filter.run(analytic * np.exp(1j * phases)))
        if self.noise:
            audio = audio + self.noise * self.random.standard_normal(n)
        self.queue.put(audio.astype("<f4").tobytes())

    def _write(self):
        while True:
            data = self.queue.get()
            while data:
                data = data[os.write(self.fd, data):]

    def _play(self):
        # The stream is continuous and in real time already; 300 ms of silence first gives
        # aplay's buffer the slack to ride out uneven arrival.
        self.player.stdin.write(np.zeros(3 * RATE // 10, dtype="<f4").tobytes())
        while True:
            self.player.stdin.write(self.queue.get())
            self.player.stdin.flush()


class Meter:
    """Measures each transmission: a run of audio with no gap longer than half a second."""

    def __init__(self, dial, log_path, name="tx"):
        self.dial, self.log_path, self.name = dial, log_path, name
        self.blocks, self.quiet, self.started = [], 0, None

    def feed(self, audio):
        loud = np.sqrt(np.mean(audio ** 2)) > 1e-4 if len(audio) else False
        if loud:
            if not self.blocks:
                self.started = time.time()
            self.blocks.append(audio)
            self.quiet = 0
        elif self.blocks:
            self.quiet += len(audio)
            if self.quiet > RATE // 2:
                self.finish()

    def finish(self):
        audio = np.concatenate(self.blocks)
        self.blocks, self.quiet = [], 0
        if len(audio) < RATE // 10:
            return
        f, p = signal.welch(audio, RATE, nperseg=min(len(audio), 1 << 16))
        total = np.cumsum(p) / np.sum(p)
        low, high = f[np.searchsorted(total, 0.01)], f[np.searchsorted(total, 0.99)]
        peak = f[np.argmax(p)]
        centre = (low + high) / 2
        dial = self.dial.hz if self.dial else None
        record = {
            "event": "transmission", "radio": self.name,
            "started": time.strftime("%H:%M:%S", time.gmtime(self.started)),
            "seconds": round(len(audio) / RATE, 1),
            "kind": "tone" if high - low < 100 else "data",
            "levelDbfs": round(float(10 * np.log10(np.mean(audio ** 2))), 1),
            "audioPeakHz": round(float(peak), 1), "audioLowHz": round(float(low)),
            "audioHighHz": round(float(high)), "audioCentreHz": round(float(centre)),
        }
        if dial is not None:
            record["dialHz"] = dial
            record["rfCentreHz"] = round(dial + (peak if record["kind"] == "tone" else centre))
        log(self.log_path, record)


def read_fifo(path):
    """Yields the FIFO as a sound card would play it: 50 ms blocks in real time, silence when idle.

    The modem writes a transmission as fast as the FIFO takes it, so the pacing a sound card
    gives it has to come from here: 50 ms is read every 50 ms, and the modem waits. Whatever
    is missing from a block is silence: the modem is ahead of the clock for the whole of a
    transmission, so a short read is its end. A transmission is started one block late, so
    that its first block is whole too.
    """
    if not os.path.exists(path):
        os.mkfifo(path)
    fd = os.open(path, os.O_RDWR | os.O_NONBLOCK)
    size = RATE // 20
    pending, sending = b"", False
    due = time.monotonic()
    while True:
        try:
            pending += os.read(fd, 4 * size - len(pending))
        except BlockingIOError:
            pass
        if not sending and pending:
            sending, block = True, np.zeros(size)
        else:
            whole = len(pending) // 4 * 4
            block = np.zeros(size)
            block[:whole // 4] = np.frombuffer(pending[:whole], dtype="<f4")
            pending = pending[whole:]
            sending = whole > 0
        yield block
        due = max(due, time.monotonic() - 0.2) + size / RATE
        wait = due - time.monotonic()
        if wait > 0:
            time.sleep(wait)


def watch(spec, log_path):
    """Drains a station's transmit FIFO that nothing else listens to, and logs what it sends."""
    path, _, dial = spec.partition("@")
    meter = Meter(Dial(dial, "watched " + os.path.basename(path), log_path) if dial else None,
                  log_path, os.path.basename(path))
    for block in read_fifo(path):
        meter.feed(block)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--tx", required=True, help="FIFO@DIAL[@LOW@HIGH]: the transmitting station's audio out")
    parser.add_argument("--rx", action="append", default=[],
                        help="FIFO@DIAL[@LOW@HIGH[@NOISE]] or alsa:DEVICE@DIAL[@LOW@HIGH[@NOISE]]: a receiver's audio in (repeatable)")
    parser.add_argument("--watch", action="append", default=[],
                        help="FIFO[@DIAL]: another station's transmit FIFO, drained and logged only (repeatable)")
    parser.add_argument("--log", required=True)
    args = parser.parse_args()

    parts = args.tx.split("@")
    tx_dial = Dial(parts[1], "tx", args.log)
    low, high = (float(parts[2]), float(parts[3])) if len(parts) > 3 else (50.0, 3600.0)
    tx_filter = Filter(low, high)
    log(args.log, {"event": "filter", "radio": "tx", "lowHz": low, "highHz": high})
    receivers = [Receiver(spec, tx_dial, args.log) for spec in args.rx]
    for spec in args.watch:
        threading.Thread(target=watch, args=(spec, args.log), daemon=True).start()
    meter = Meter(tx_dial, args.log)
    for block in read_fifo(parts[0]):
        analytic = tx_filter.run(block)
        meter.feed(2 * np.real(analytic))
        for receiver in receivers:
            receiver.feed(analytic)


if __name__ == "__main__":
    sys.exit(main())
