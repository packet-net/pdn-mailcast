#!/usr/bin/env python3 -I
"""compare_posted.py CAPTURE_DIR - for XRouter-origin bulletins: the body as posted vs what XRouter stored (after removing its R: line and its
Date/From/To/Subject/Message-ID block) vs what LAB stored (same removal). Lines compared after splitting on CRLF, CR or LF."""
import sys, os, re
sys.argv = [sys.argv[0], sys.argv[1]]
exec(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "analyse.py")).read().split('if __name__')[0])
def lines(b): return re.split(rb"\r\n|\r|\n", b)
def strip_x(b):
    ls = lines(b); i = 0
    while i < len(ls) and ls[i].startswith(b"R:"): i += 1
    if ls[i:i+1] and ls[i].startswith(b"Date: ") and any(l.startswith(b"Message-ID: ") for l in ls[i:i+7]):
        while not ls[i].startswith(b"Message-ID: "): i += 1
        i += 1
        if ls[i:i+1] == [b""]: i += 1
    return ls[i:]
for k in sorted(B):
    title, body, ending = B[k]
    posted = lines(body)
    out = []
    for st in ("lax", "lab"):
        for r in S[st]:
            if r["key"] == k and origin(r) == "LAX":
                got = strip_x(r["body"])
                if got == posted: res = "identical lines"
                elif got[:-1] == posted and got[-1] == b"": res = "identical + one extra trailing empty line"
                elif got == posted + [b""]: res = "identical + trailing empty"
                else:
                    res = f"DIFF posted {len(posted)} lines, got {len(got)}"
                    for a, b2 in zip(posted, got):
                        if a != b2: res += f"; first diff {a[:60]!r} -> {b2[:60]!r}"; break
                out.append(f"{st}: {res}; title {len(r['title'])} chars")
    print(k, "|", " | ".join(out) or "not created")
