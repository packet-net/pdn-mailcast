#!/usr/bin/env python3 -I
"""analyse.py CAPTURE_DIR [--matrix|KEY...]  - JNOS lab: what LAA (LinBPQ), LAJ (JNOS) and LAB (LinBPQ) stored per bulletin.
JNOS keeps an area as one mbox-style file, spool/mail/ww.txt ("From user date" line, RFC822-ish headers, blank, body).
The area is emptied when JNOS has forwarded it, so the copy analysed is the snapshot in laj-before/ taken just before the forward to LAB.
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
def jnos():
    p = f"{D}/laj-before/spool/mail/ww.txt"
    d = open(p, "rb").read(); out = []
    chunks = re.split(rb"(?m)^(?=From \S+@\S+ [A-Z][a-z]{2} [A-Z][a-z]{2} )", d)
    for c in chunks:
        if not c.strip(): continue
        head, _, body = c.partition(b"\n\n")
        m = re.search(rb"(?m)^Subject: (.*)$", head)
        title = m.group(1) if m else b""
        frm = re.search(rb"(?m)^From: (.*)$", head); mid = re.search(rb"(?m)^Message-Id: (.*)$", head)
        out.append(dict(key=keyof(title), num=len(out)+1, bid=(frm.group(1).decode("latin1") if frm else ""), head=head, title=title, body=body))
    return out
S = {"laa": bpq("laa"), "lab": bpq("lab"), "laj": jnos()}
def origin(r, st):
    if st == "laj": return "LAA" if "gb7laa" in r["bid"].lower() else "LAJ"
    return "LAA" if "LAA" in r["bid"] else "LAJ"
def eols(b): return dict(crlf=b.count(b"\r\n"), cr=b.count(b"\r") - b.count(b"\r\n"), lf=b.count(b"\n") - b.count(b"\r\n"))
def norm(b): return b"\n".join(re.split(rb"\r\n|\r|\n", b))
def unesc(b): return re.sub(rb"(?m)^>(>*From )", rb"\1", b)
def show(keys):
    for k in keys:
        for org in ("LAA", "LAJ"):
            print(f"\n## {k} (posted on GB7{org})")
            for st in ("laa", "laj", "lab"):
                for r in S[st]:
                    if r["key"] == k and origin(r, st) == org:
                        b = r["body"]
                        print(f"{st}: #{r['num']} id={r['bid']} title({len(r['title'])})={r['title'][:70]!r} body {len(b)}B eol={eols(b)} sha={hashlib.sha1(b).hexdigest()[:8]}")
                        if st == "laj": print(f"   headers={r['head'][:600]!r}")
                        print(f"   body={b[:420]!r}{'...' if len(b) > 420 else ''}")
def strip_r(b):
    ls = re.split(rb"\r\n|\r|\n", b); i = 0
    while i < len(ls) and ls[i].startswith(b"R:"): i += 1
    if i and i < len(ls) and ls[i] == b"": i += 1
    return b"\n".join(ls[i:])
def matrix():
    print("| key | origin | origin copy | at LAJ (stored) | at LAB | title at LAB |")
    print("| --- | --- | --- | --- | --- | --- |")
    for k in sorted(B):
        for org in ("LAA", "LAJ"):
            row = {}
            for st in ("laa", "laj", "lab"):
                for r in S[st]:
                    if r["key"] == k and origin(r, st) == org: row[st] = r
            o = row.get("laa" if org == "LAA" else "laj")
            if not o: print(f"| {k} | {org} | not created | | | |"); continue
            ob = norm(unesc(o["body"])).strip(b"\n")
            def cmp(r, st):
                if r is None: return "missing"
                rest = norm(unesc(strip_r(r["body"]))).strip(b"\n")
                return ("same" if rest == ob else f"DIFF {len(rest)} vs {len(ob)}B") + f" (stored {len(r['body'])}B), eol {eols(r['body'])}"
            tl = ""
            if "lab" in row and row["lab"]["title"] != o["title"]: tl = f"{len(row['lab']['title'])} (origin {len(o['title'])})"
            print(f"| {k} | {org} | {len(o['body'])}B title {len(o['title'])} eol {eols(o['body'])} | {cmp(row.get('laj'), 'laj') if org == 'LAA' else 'origin'} | {cmp(row.get('lab'), 'lab')} | {tl} |")
if __name__ == "__main__":
    if len(sys.argv) > 2 and sys.argv[2] == "--matrix": matrix()
    else: show(sys.argv[2:] or sorted(B))
