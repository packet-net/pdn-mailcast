#!/usr/bin/env python3 -I
"""analyse.py CAPTURE_DIR [KEY ...] - join the stored files of the three BBSes by test key and print what each stored.
BPQ DIRMES.SYS: packed 308-byte records (type, status, number@2, length@6, bbsfrom@14, via@21, from@62, to@69, bid@76, title@89).
FBB dirmes.sys: 222-byte records (number@2, size@10, bbs@26, route@33, from@74, to@81, bid@88, title@101)."""
import sys, os, re, hashlib, struct
D = sys.argv[1]
def cs(b, o, n): return b[o:o+n].split(b"\0")[0].decode("latin1")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bulletins import B
def keyof(t):
    tb = t.encode("latin1")
    for k, (title, _, _) in B.items():
        if tb[:30] == title[:30]: return k
def recs(path, size, tpos, bpq):
    d = open(path, "rb").read(); out = []
    for s in range(0, len(d) - size + 1, size):
        r = d[s:s+size]
        if bpq: o = dict(num=struct.unpack_from("<i", r, 2)[0], size=struct.unpack_from("<i", r, 6)[0], bbs=cs(r,14,7), via=cs(r,21,41), frm=cs(r,62,7), to=cs(r,69,7), bid=cs(r,76,13), title=cs(r,89,61))
        else:   o = dict(num=struct.unpack_from("<q", r, 2)[0], size=struct.unpack_from("<q", r, 10)[0], bbs=cs(r,26,7), via=cs(r,33,41), frm=cs(r,74,7), to=cs(r,81,7), bid=cs(r,88,13), title=cs(r,101,61))
        o["key"] = keyof(o["title"])
        if o["key"] and o["num"] > 0: out.append(o)
    return out
def body(store, n):
    p = f"{D}/{store}/Mail/m_{n:06d}.mes" if store != "laf" else f"{D}/laf/mail/mail{n%10}/m_{n:06d}.mes"
    return open(p, "rb").read()
S = {"laa": recs(f"{D}/laa/DIRMES.SYS", 308, 89, True), "lab": recs(f"{D}/lab/DIRMES.SYS", 308, 89, True), "laf": recs(f"{D}/laf/dirmes.sys", 222, 101, False)}
def origin(r): return "LAA" if "LAA" in r["bid"] else "LAF"
def show(keys):
    for k in keys:
        for org in ("LAA", "LAF"):
            print(f"\n## {k} (posted on GB{'7'}{org})")
            sent = open(f"{os.path.dirname(os.path.abspath(__file__))}/../bulletins/{k}.body", "rb").read()
            print("posted :", len(sent), "bytes", hashlib.sha1(sent).hexdigest()[:8])
            for st in ("laa", "laf", "lab"):
                for r in S[st]:
                    if r["key"] == k and origin(r) == org:
                        b = body(st, r["num"])
                        print(f"{st}: #{r['num']} bid={r['bid']} bbsfrom={r['bbs']!r} via={r['via']!r} from={r['frm']} to={r['to']} hdrsize={r['size']} tlen={len(r['title'])} title={r['title'][:12]!r}")
                        print(f"   file {len(b)} bytes sha1={hashlib.sha1(b).hexdigest()[:8]} body={b[:400]!r}{'...' if len(b)>400 else ''}")
if __name__ == "__main__":
    keys = sys.argv[2:] or sorted({r["key"] for r in S["laa"]})
    show(keys)
