#!/usr/bin/env python3
"""Fetch every bulletin a LinBPQ BBS lists, read-only, into corpus/<STATION>/<BID>.txt (issue #138).

Usage: fetch.py STATION HOST PORT [--max N]
Logs in with ~/.config/g8bpq/login, sends LB, then R n for each bulletin not already in the
corpus, a batch per connection. Only LB, R n and B are ever sent. Each file is the BBS's read
output from "From:" to the line before "[End of Message", as telnet delivered it (line ends
normalised to LF), so copies from different BBSes can be compared line by line.
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpqread  # noqa: E402

LIST_LINE = re.compile(r"^(\d+)\s+\S+\s+(B\S)\s+(\d+)\s+(\S+)\s+@(\S*)\s+(\S+)\s+(.*)$")
BATCH = 15


def session(host: str, port: int, cmds: list[str]) -> str:
    import io
    import contextlib
    argv = sys.argv
    sys.argv = ["bpqread", host, str(port), "BBS", *cmds, "B"]
    buf = io.StringIO()
    try:
        with contextlib.redirect_stdout(buf):
            bpqread.main()
    finally:
        sys.argv = argv
    return buf.getvalue()


def main() -> int:
    station, host, port = sys.argv[1], sys.argv[2], int(sys.argv[3])
    limit = int(sys.argv[sys.argv.index("--max") + 1]) if "--max" in sys.argv else 10_000
    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "corpus", station)
    os.makedirs(out_dir, exist_ok=True)
    listing = session(host, port, ["LB"])
    wanted = []
    index_rows = []
    for line in listing.split("\n"):
        m = LIST_LINE.match(line.strip())
        if m:
            index_rows.append(line.strip())
            wanted.append(int(m.group(1)))
    with open(os.path.join(out_dir, "listing.txt"), "w") as f:
        f.write("\n".join(index_rows) + "\n")
    have = set()
    for name in os.listdir(out_dir):
        if name.startswith("msg-"):
            have.add(int(name.split("-")[1].split(".")[0]))
    todo = [n for n in wanted if n not in have][:limit]
    for i in range(0, len(todo), BATCH):
        batch = todo[i:i + BATCH]
        text = session(host, port, [f"R {n}" for n in batch])
        for n in batch:
            m = re.search(rf">>> R {n}\n(From:.*?)\n\[End of Message #{n} ", text, re.S)
            if not m:
                print(f"{station} {n}: not read", file=sys.stderr)
                continue
            body = m.group(1)
            bid = re.search(r"^Bid: (\S+)", body, re.M)
            name = f"msg-{n}.txt"
            with open(os.path.join(out_dir, name), "w") as f:
                f.write(body + "\n")
            print(f"{station} {n} {bid.group(1) if bid else '?'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
