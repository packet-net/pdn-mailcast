#!/usr/bin/env python3
"""A stand-in for a hardware KISS TNC (a NinoTNC, say) on the listener's radio.

LinBPQ connects to it over TCP as it would to the TNC's serial port. Whatever LinBPQ hands it
to send, it "sends": it keys the listener's rig through rigctld for as long as the frame would
take at 300 baud, and writes one line saying when, how long, and where the rig's dial was.
Nothing goes on the bench's air; the point is the record of what would have been transmitted,
and on which frequency.

    kiss-tnc.py PORT RIGCTLD-HOST:PORT LOG
"""
import json
import socket
import sys
import time

port, rig, log_path = int(sys.argv[1]), sys.argv[2].rsplit(":", 1), sys.argv[3]


def rigctld(command):
    with socket.create_connection((rig[0], int(rig[1])), timeout=2) as sock:
        sock.sendall(command.encode() + b"\n")
        return sock.recv(4096).decode().split("\n")[0]


def record(**what):
    what["utc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    line = json.dumps(what)
    with open(log_path, "a") as f:
        f.write(line + "\n")
    print(line, flush=True)


server = socket.socket()
server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
server.bind(("127.0.0.1", port))
server.listen(1)
while True:
    client, _ = server.accept()
    record(event="LinBPQ connected")
    pending = b""
    while True:
        data = client.recv(4096)
        if not data:
            break
        pending += data
        while pending.count(b"\xc0") >= 2:
            start = pending.index(b"\xc0")
            end = pending.index(b"\xc0", start + 1)
            frame, pending = pending[start + 1:end], pending[end:]
            if len(frame) < 16 or frame[0] & 0x0f:   # a KISS setting, or the gap between frames
                continue
            seconds = round(len(frame) * 8 / 300 + 0.5, 1)
            dial = float(rigctld("f"))
            rigctld("T 1")
            record(event="frame keyed", bytes=len(frame) - 1, seconds=seconds, dialHz=dial,
                   text=frame[17:].decode(errors="replace").strip())
            time.sleep(seconds)
            rigctld("T 0")
    record(event="LinBPQ disconnected")
