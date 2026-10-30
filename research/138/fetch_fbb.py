#!/usr/bin/env python3
"""Fetch bulletins from an FBB BBS (GB7CIP), read-only, into corpus/<STATION>/msg-<n>.txt (#138).

Usage: fetch_fbb.py STATION HOST PORT SINCE_MMDD
Logs in with $BPQ_LOGIN, sends LB, then R n for each bulletin dated on or after SINCE_MMDD
(e.g. 1019) not already saved, a batch per connection. Only LB, R n and B are sent.

Each file is written in the same shape as the LinBPQ corpus so compare.py can read it:
From/To/Bid/Title/Date/Time header lines, a "Path:" header for FBB's routing line, then the body.
FBB's own display lines ("This message has been read N times") are dropped; they are not part
of the bulletin.
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fetch  # noqa: E402  (session(), shared with the LinBPQ fetcher)

fetch.DIRECT = True  # an FBB login lands straight in the BBS

LIST = re.compile(r"^(\d+)\s+(B\S*)\s+(\d+)\s+(\S+?)\s*@(\S+)\s+(\S+)\s+(\d{4})/(\d{4})\s+(.*)$")
FIELD = re.compile(r"^(From|To|Type/Status|Date/Time|BID \(MID\)|Message #|Subject)\s*: ?(.*?)\s*$")
BATCH = 10


def convert(n: int, text: str) -> str | None:
    m = re.search(rf">>> R {n}\n(From .*?)\n\s*--- End of message#: {n} ", text, re.S)
    if not m:
        return None
    lines = m.group(1).split("\n")
    head, i = {}, 0
    while i < len(lines) and (f := FIELD.match(lines[i])):
        head[f.group(1)] = f.group(2)
        i += 1
    path = None
    rest = []
    for line in lines[i:]:
        if path is None and line.startswith("Path: "):
            path = line[6:]
            continue
        if re.match(r"^\(This message has been read .* so far in this BBS\)\.$", line):
            continue
        rest.append(line)
    out = [f"From: {head.get('From', '')}", f"To: {head.get('To', '').split('@')[0]}",
           f"Type/Status: {head.get('Type/Status', '')}", f"Date/Time: {head.get('Date/Time', '')}",
           f"Bid: {head.get('BID (MID)', '')}", f"Title: {head.get('Subject', '')}", f"Path: {path or ''}"]
    # FBB shows blank lines before and after its Path line; the bulletin's own first line follows.
    while rest and rest[0] == "":
        rest.pop(0)
    return "\n".join(out + [""] + rest) + "\n"


def main() -> int:
    station, host, port, since = sys.argv[1], sys.argv[2], int(sys.argv[3]), sys.argv[4]
    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "corpus", station)
    os.makedirs(out_dir, exist_ok=True)
    listing = fetch.session(host, port, ["LB"])
    rows = [l.strip() for l in listing.split("\n") if LIST.match(l.strip())]
    open(os.path.join(out_dir, "listing.txt"), "w").write("\n".join(rows) + "\n")
    have = {int(x.split("-")[1].split(".")[0]) for x in os.listdir(out_dir) if x.startswith("msg-")}
    todo = [int(LIST.match(r).group(1)) for r in rows if LIST.match(r).group(7) >= since]
    todo = [n for n in todo if n not in have]
    for i in range(0, len(todo), BATCH):
        batch = todo[i:i + BATCH]
        text = fetch.session(host, port, [f"R {n}" for n in batch])
        for n in batch:
            body = convert(n, text)
            if body is None:
                print(f"{station} {n}: not read", file=sys.stderr)
                continue
            open(os.path.join(out_dir, f"msg-{n}.txt"), "w").write(body)
            print(f"{station} {n}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
