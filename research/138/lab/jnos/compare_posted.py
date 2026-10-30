#!/usr/bin/env python3 -I
"""compare_posted.py CAPTURE_DIR - for JNOS-origin bulletins: body as posted vs what JNOS stored in its area file (mbox style) vs what LAB received.
Lines compared after splitting on CRLF, CR or LF; JNOS's mbox ">From " escaping is shown separately."""
import sys, os, re
sys.argv = [sys.argv[0], sys.argv[1]]
exec(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "analyse.py")).read().split('if __name__')[0])
def lines(b): return re.split(rb"\r\n|\r|\n", b)
def diff(posted, got):
    if got == posted: return "identical lines"
    if got == posted + [b""]: return "identical + one extra trailing empty line"
    if got == [b""] + posted: return "identical + one extra leading empty line"
    if got == [b""] + posted + [b""]: return "identical + one extra empty line at start and one at end"
    res = f"DIFF posted {len(posted)} lines, got {len(got)}"
    for a, b2 in zip(posted, got):
        if a != b2: res += f"; first diff {a[:50]!r} -> {b2[:50]!r}"; break
    return res
for k in sorted(B):
    title, body, ending = B[k]
    posted = lines(body); out = []
    for st in ("laj", "lab"):
        for r in S[st]:
            if r["key"] == k and origin(r, st) == "LAJ":
                b = r["body"]
                out.append(f"{st}: {diff(posted, lines(b))}; escaped-From lines {len(re.findall(rb'(?m)^>+From ', b))}; title {len(r['title'])} chars; max line {max(len(l) for l in lines(b))}")
    print(k, "|", " | ".join(out) or "not created")
