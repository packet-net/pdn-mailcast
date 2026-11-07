#!/usr/bin/env python3
"""Copies new daily reports from GB7RDG's LinBPQ mail store into reports/daily.

Usage: reports/import-gb7rdg.py [SSH_TARGET]   (default tf@gb7rdg-node; needs sudo there)

Read-only on the node: it reads DIRMES.SYS (LinBPQ's message index) and the m_NNNNNN.mes files of
messages titled "MCR CALL YYYY-MM-DD". A report already in reports/daily is left alone. GB7RDG
expires bulletins after about a week, so run this at least every few days, then regenerate the
tables with `dotnet run --project tools/Mailcast.ReportArchive -- reports`.
"""
import datetime
import os
import re
import struct
import subprocess
import sys

TARGET = sys.argv[1] if len(sys.argv) > 1 else "tf@gb7rdg-node"
BPQ = "/opt/oarc/bpq"
DAILY = os.path.join(os.path.dirname(os.path.abspath(__file__)), "daily")
TITLE = re.compile(rb"MCR ([A-Z0-9/]+) (2026-\d\d-\d\d|20\d\d-\d\d-\d\d)")


def fetch(path: str) -> bytes:
    return subprocess.run(["ssh", TARGET, "sudo", "cat", path], check=True, capture_output=True).stdout


def text(b: bytes) -> str:
    return b.split(b"\0")[0].decode("latin-1")


def main() -> int:
    index = fetch(f"{BPQ}/DIRMES.SYS")
    today = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d")
    added = 0
    for m in TITLE.finditer(index):
        # A DIRMES.SYS record, as LinBPQ 6.0.25 writes it: type, status, number (int32),
        # length (int32), ..., via at 22, from at 62, to at 69, BID at 76, title at 89.
        i = m.start() - 89
        if i < 0:
            continue
        kind, number, length = chr(index[i]), *struct.unpack("<ii", index[i + 2:i + 10])
        frm, to, bid, title = text(index[i + 62:i + 69]), text(index[i + 69:i + 76]), text(index[i + 76:i + 89]), text(index[i + 89:i + 150])
        call, day = m.group(1).decode(), m.group(2).decode()
        if kind not in "BP" or title != f"MCR {call} {day}" or frm != call:
            continue
        out = os.path.join(DAILY, day, f"{call}.txt")
        if os.path.exists(out):
            continue
        name = f"m_{number:06d}.mes"
        try:
            raw = fetch(f"{BPQ}/Mail/{name}")
        except subprocess.CalledProcessError:
            print(f"{title}: {name} is gone from the mail store", file=sys.stderr)
            continue
        body = raw.decode("latin-1").replace("\r\n", "\n")
        if body.startswith("MID:"):
            body = body.partition("\n\n")[2]
        if "MCR1" not in body:
            continue
        check = "stored size matches the BBS's index" if len(raw) == length else "STORED SIZE DOES NOT MATCH THE INDEX"
        head = [f"Title: {title}", f"From: {frm}", f"To: {to}@GB7RDG.#42.GBR.EURO" if to == "MCAST" else f"To: {to}",
                f"Type: {'B (bulletin)' if kind == 'B' else 'P (personal mail)'}", f"Bid: {bid}",
                f"Source: GB7RDG's LinBPQ mail store, message {number} ({name}), copied {today}", f"Check: {check}"]
        os.makedirs(os.path.dirname(out), exist_ok=True)
        with open(out, "w", newline="\n") as f:
            f.write("\n".join(head) + "\n\n" + body.strip("\n") + "\n")
        print(f"added {day}/{call}.txt")
        added += 1
    print(f"{added} new report(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
