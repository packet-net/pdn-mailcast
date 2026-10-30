#!/usr/bin/env python3 -I
"""post.py WHERE KEY...  - WHERE is laa (LinBPQ via telnet 18010, BBS app) or lax (XRouter telnet 18024, G0LAB, BBS command).
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
    elif where == "lax" and ending == b"\x1a": ending = b"\x1a\r"   # XRouter ignores a bare Ctrl-Z; it needs the line ended
    port = 18010 if where == "laa" else 18024
    s = P.S(port); s.rd(1.5); P.GLOBAL[0] = s
    if where == "laa":
        s.tx(b"user\r"); s.tx(b"user\r", 1.5); s.tx(b"BBS\r", 2)
    else:
        s.tx(b"G0LAB\r", 1.5); s.tx(b"user\r", 1.5); s.tx(b"BBS\r", 2); s.tx(b"NP 0\r", 1)
    s.tx(b"SB LABTST @ WW\r", 2)
    s.tx(title + b"\r", 2)
    for chunk in re.findall(rb"[^\r\n]*(?:\r\n|\r|\n)|[^\r\n]+$", body):
        s.s.sendall(chunk); s.log += b"[[" + chunk + b"]]"; time.sleep(P.PACE if len(body) < 5000 else 0.25)
    s.rd(0.5)
    s.log += b"[[" + ending + b"]]"; s.s.sendall(ending)
    r = b""; t0 = time.time()
    while time.time() - t0 < 90 and not re.search(rb"Bid:|saved|sent @|accepted|Message #|rejected|Error", r):
        r += s.rd(1.0, 5)
    s.tx(b"B\r", 2); s.s.close()
    os.makedirs(P.OUT, exist_ok=True)
    open(f"{P.OUT}/{where}-{key}.raw", "wb").write(s.log)
    return P.clean(r)

if __name__ == "__main__":
    for k in sys.argv[2:]:
        try: r = run(sys.argv[1], k)
        except Exception as e: r = repr(e).encode()
        print(sys.argv[1], k, r.decode("latin1").strip().replace("\r\n", " | ")[-100:])
