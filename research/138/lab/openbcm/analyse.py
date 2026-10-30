#!/usr/bin/env python3 -I
"""analyse.py CAPTURE_DIR [--matrix|KEY...]  - openBCM lab: what LAA (LinBPQ), LAM (openBCM) and LAB (LinBPQ) stored per bulletin.
openBCM message file (info/<board>/<name>): line 1 header (to < from ~via @at $BID ...), line 2 forward flags, line 3 title,
then the body with LF line ends (including the R: line and From:/To:/X-Info: block openBCM adds itself).
LinBPQ DIRMES.SYS: 308-byte records (see ../scripts/analyse.py)."""
import sys, os, re, struct, hashlib
D = sys.argv[1]
H = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(H, "..", "scripts"))
from bulletins import B
def cs(b, o, n): return b[o:o+n].split(b"\0")[0]
def keyof(t):
    for k, (title, _, _) in B.items():
        if t[:30] == title[:30]: return k
def bpq(store):
    d = open(f"{D}/{store}/DIRMES.SYS", "rb").read(); out = []
    for s in range(0, len(d) - 307, 308):
        r = d[s:s+308]; n = struct.unpack_from("<i", r, 2)[0]; t = cs(r, 89, 61)
        k = keyof(t)
        if k and n > 0:
            out.append(dict(key=k, num=n, bid=cs(r, 76, 13).decode("latin1"), title=t, body=open(f"{D}/{store}/Mail/m_{n:06d}.mes", "rb").read()))
    return out
def bcm():
    out = []; base = f"{D}/lam/info"
    for root, _, files in os.walk(base):
        for f in sorted(files):
            if f == "list.bcm": continue
            d = open(os.path.join(root, f), "rb").read()
            parts = d.split(b"\n", 3)
            if len(parts) < 4: continue
            h, flags, title, body = parts
            m = re.search(rb"\$(\S+)", h)
            out.append(dict(key=keyof(title), num=f, hdr=h, bid=m.group(1).decode("latin1") if m else "", title=title, body=body))
    return out
S = {"laa": bpq("laa"), "lab": bpq("lab"), "lam": bcm()}
def origin(r): return "LAA" if "LAA" in r["bid"] else "LAM"
def eols(b): return dict(crlf=b.count(b"\r\n"), cr=b.count(b"\r") - b.count(b"\r\n"), lf=b.count(b"\n") - b.count(b"\r\n"))
def split_prefix(b):
    """return (added_prefix, rest): leading R: lines, then openBCM's From:/To:/X-Info: block, with the blank line after."""
    ls = re.split(rb"\r\n|\n", b); i = 0
    while i < len(ls) and ls[i].startswith(b"R:"): i += 1
    j = i
    if ls[j:j+1] and ls[j].startswith(b"From: ") and ls[j+1:j+2] and ls[j+1].startswith(b"To:"):
        j += 2
        while j < len(ls) and ls[j].startswith(b"X-"): j += 1
    if j < len(ls) and ls[j] == b"": j += 1
    elif j == i and i and i < len(ls) and ls[i] == b"": j = i + 1
    return b"\n".join(ls[:j]), b"\n".join(ls[j:])
def show(keys):
    for k in keys:
        for org in ("LAA", "LAM"):
            print(f"\n## {k} (posted on GB7{org})")
            for st in ("laa", "lam", "lab"):
                for r in S[st]:
                    if r["key"] == k and origin(r) == org:
                        b = r["body"]
                        print(f"{st}: #{r['num']} bid={r['bid']} title({len(r['title'])})={r['title'][:70]!r} body {len(b)}B eol={eols(b)} sha={hashlib.sha1(b).hexdigest()[:8]}")
                        print(f"   head={b[:520]!r}{'...' if len(b) > 520 else ''}")
def norm(b): return b"\n".join(re.split(rb"\r\n|\r|\n", b))
def matrix():
    print("| key | origin | origin copy | at LAM (relay) | at LAB | title at LAB |")
    print("| --- | --- | --- | --- | --- | --- |")
    for k in sorted(B):
        for org in ("LAA", "LAM"):
            row = {}
            for st in ("laa", "lam", "lab"):
                for r in S[st]:
                    if r["key"] == k and origin(r) == org: row[st] = r
            o = row.get("laa" if org == "LAA" else "lam")
            if not o: print(f"| {k} | {org} | not created | | | |"); continue
            ob = norm(o["body"]) if org == "LAA" else norm(split_prefix(o["body"])[1])
            def cmp(r):
                if r is None: return "missing"
                pre, rest = split_prefix(r["body"]); same = norm(rest).rstrip(b"\n") == ob.rstrip(b"\n")
                return (f"same after strip (+{len(r['body']) - len(rest)}B prefix)" if same else f"DIFF {len(rest)} vs {len(ob)}B") + f", eol {eols(r['body'])}"
            tl = ""
            if "lab" in row and row["lab"]["title"] != o["title"]: tl = f"{len(row['lab']['title'])} (origin {len(o['title'])})"
            print(f"| {k} | {org} | {len(o['body'])}B title {len(o['title'])} eol {eols(o['body'])} | {cmp(row.get('lam')) if org == 'LAA' else 'origin'} | {cmp(row.get('lab'))} | {tl} |")
if __name__ == "__main__":
    if len(sys.argv) > 2 and sys.argv[2] == "--matrix": matrix()
    else: show(sys.argv[3:] if False else (sys.argv[2:] or sorted(B)))
