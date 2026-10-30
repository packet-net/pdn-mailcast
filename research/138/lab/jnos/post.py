#!/usr/bin/env python3 -I
"""post.py WHERE KEY...  - WHERE is laa (LinBPQ BBS, telnet 38010) or laj (JNOS, reached by C 2 GB7LAJ from the laa node as G0LAB).
Reuses the session helpers and bulletin set from ../scripts. Raw sessions go to captured/transcripts/<where>-<key>.raw"""
import sys, os, re, time
H = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(H, "..", "scripts"))
import post as P
from bulletins import B
P.OUT = os.path.join(H, "captured", "transcripts")

def run(where, key):
    title, body, ending = B[key]
    if os.environ.get("ENDING_HEX"): ending = bytes.fromhex(os.environ["ENDING_HEX"])
    elif where == "laj" and ending == b"\x1a": ending = b"\x1a\r"
    s = P.S(38010); s.rd(1.5); P.GLOBAL[0] = s
    s.tx(b"user\r"); s.tx(b"user\r", 1.5)
    if where == "laa":
        s.tx(b"BBS\r", 2)
    else:
        r = s.tx(b"C 2 GB7LAJ\r", 12); assert b"Connected to GB7LAJ" in r, r
        s.s.sendall(b"\r"); g = b""; t0 = time.time()
        while b"Area:" not in g and time.time() - t0 < 30: g += s.rd(1.0, 5)
        assert b"Area:" in g, g
    s.tx(b"SB LABTST @ WW\r", 2)
    s.tx(title + b"\r", 2)
    for chunk in re.findall(rb"[^\r\n]*(?:\r\n|\r|\n)|[^\r\n]+$", body):
        s.s.sendall(chunk); s.log += b"[[" + chunk + b"]]"; time.sleep(P.PACE if len(body) < 5000 else 0.25)
    s.rd(0.5)
    s.log += b"[[" + ending + b"]]"; s.s.sendall(ending)
    r = b""; t0 = time.time()
    while time.time() - t0 < 90 and not re.search(rb"Bid:|queued|Send\(N=no\)|sent @|accepted|Message #|rejected|Error", r):
        r += s.rd(1.0, 5)
    if where == "laj" and b"Send(N=no)" in r: r += s.tx(b"\r", 2)
    s.tx(b"B\r", 3); s.tx(b"B\r", 1); s.s.close()
    os.makedirs(P.OUT, exist_ok=True)
    open(f"{P.OUT}/{where}-{key}.raw", "wb").write(s.log)
    return P.clean(r)

if __name__ == "__main__":
    for k in sys.argv[2:]:
        try: r = run(sys.argv[1], k)
        except Exception as e: r = repr(e).encode()
        print(sys.argv[1], k, r.decode("latin1").strip().replace("\r\n", " | ")[-100:])
