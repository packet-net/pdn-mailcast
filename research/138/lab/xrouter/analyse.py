#!/usr/bin/env python3 -I
"""analyse.py CAPTURE_DIR  - XRouter lab: per bulletin, show what LAA (LinBPQ), LAX (XRouter PMS) and LAB (LinBPQ) stored.
XRouter PMS file: line 1 header (num, type+status, from, epoch, to@at, bid, &:source), line 2 title, then the body with LF line ends.
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
            out.append(dict(key=k, num=n, bid=cs(r, 76, 13).decode("latin1"), frm=cs(r, 62, 7), to=cs(r, 69, 7), title=t,
                            body=open(f"{D}/{store}/Mail/m_{n:06d}.mes", "rb").read()))
    return out
def xr():
    out = []
    for f in sorted(os.listdir(f"{D}/lax/PMS")):
        if not re.fullmatch(r"\d{5}", f): continue
        d = open(f"{D}/lax/PMS/{f}", "rb").read()
        h, rest = d.split(b"\n", 1); title, body = rest.split(b"\n", 1)
        hf = h.split(b" ")
        out.append(dict(key=keyof(title), num=int(f), hdr=h, bid=hf[5].decode("latin1") if len(hf) > 5 else "", title=title, body=body))
    return out
S = {"laa": bpq("laa"), "lab": bpq("lab"), "lax": xr()}
def origin(r): return "LAA" if "LAA" in r["bid"] else "LAX"
def eols(b): return dict(crlf=b.count(b"\r\n"), cr=b.count(b"\r") - b.count(b"\r\n"), lf=b.count(b"\n") - b.count(b"\r\n"))
def strip_added(b):
    """drop leading R: lines + the blank after them; then an RFC822-ish block XRouter adds to its own mail (Date:..Message-ID:) + blank."""
    ls = re.split(rb"\r\n|\r|\n", b); i = 0
    while i < len(ls) and ls[i].startswith(b"R:"): i += 1
    if i and i < len(ls) and ls[i] == b"": i += 1
    return b"\n".join(ls[i:])
def norm(b): return b"\n".join(re.split(rb"\r\n|\r|\n", b))
def show(keys):
    for k in keys:
        for org in ("LAA", "LAX"):
            print(f"\n## {k} (posted on GB7{org})")
            for st in ("laa", "lax", "lab"):
                for r in S[st]:
                    if r["key"] == k and origin(r) == org:
                        b = r["body"]
                        print(f"{st}: #{r['num']} bid={r['bid']} title({len(r['title'])})={r['title'][:70]!r} body {len(b)}B eol={eols(b)} sha={hashlib.sha1(b).hexdigest()[:8]}")
                        print(f"   head={b[:420]!r}{'...' if len(b) > 420 else ''}")
def matrix():
    print("| key | origin | origin copy | at X (relay or origin) | at LAB | title at LAB |")
    print("| --- | --- | --- | --- | --- | --- |")
    for k in sorted(B):
        for org in ("LAA", "LAX"):
            row = {}
            for st in ("laa", "lax", "lab"):
                for r in S[st]:
                    if r["key"] == k and origin(r) == org: row[st] = r
            o = row.get("laa" if org == "LAA" else "lax")
            if not o: print(f"| {k} | {org} | not created | | | |"); continue
            ob = norm(o["body"]) if org == "LAA" else norm(strip_added(o["body"]))  # X-origin body: strip X's own R:+RFC822? see below
            def cmp(r):
                if r is None: return "missing"
                sb = norm(strip_added(r["body"]))
                sb2 = sb
                exact = sb == ob
                if exact: return f"same after strip (+{len(r['body']) - len(strip_added(r['body']))}B prefix), eol {eols(r['body'])}"
                return f"DIFF {len(sb)} vs {len(ob)}B, eol {eols(r['body'])}"
            tl = ""
            if "lab" in row and row["lab"]["title"] != o["title"]: tl = f"{len(row['lab']['title'])} (origin {len(o['title'])})"
            print(f"| {k} | {org} | {len(o['body'])}B title {len(o['title'])} eol {eols(o['body'])} | {cmp(row.get('lax')) if org == 'LAA' else 'origin'} | {cmp(row.get('lab'))} | {tl} |")
if __name__ == "__main__":
    if len(sys.argv) > 2 and sys.argv[2] == "--matrix": matrix()
    else: show(sys.argv[2:] or sorted(B))
