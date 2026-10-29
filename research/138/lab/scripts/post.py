#!/usr/bin/env python3 -I
"""post.py WHERE KEY...  - post bulletins. WHERE is laa (LinBPQ via telnet, BBS app) or laf (FBB, reached by
connecting from the laa node over AXUDP as G0USR, since FBB telnet refuses unregistered users).
Logs the whole conversation (raw bytes) to captured/transcripts/<where>-<key>.raw"""
import socket, sys, time, os, re
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bulletins import B
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "captured", "transcripts")
GLOBAL = [None]
PACE = float(os.environ.get("PACE", "0.3"))
class S:
    def __init__(s, port):
        s.s = socket.create_connection(("127.0.0.1", port), 20); s.log = b""
    def rd(s, quiet=0.7, maxt=30):
        end = time.time() + maxt; last = time.time(); got = b""
        s.s.settimeout(0.1)
        while time.time() < end and time.time() - last < quiet:
            try:
                d = s.s.recv(65536)
                if not d: break
                got += d; last = time.time()
            except socket.timeout: pass
        s.log += b"<<<" + got + b">>>"; return got
    def tx(s, d, quiet=0.7):
        s.log += b"[[" + d + b"]]"; s.s.sendall(d); return s.rd(quiet)
def clean(b):
    return re.sub(rb"\xff[\xfb-\xfe].", b"", b)
def post(where, key):
    title, body, ending = B[key]
    try: return _post(where, key, title, body, ending)
    except Exception:
        raise

def _post(where, key, title, body, ending):
    s = S(18010); s.rd(1.5)
    GLOBAL[0] = s
    s.tx(b"user\r"); s.tx(b"user\r", 1.5)
    if where == "laf":
        r = s.tx(b"C 2 GB7LAF-1\r", 4); assert b"Connected to GB7LAF-1" in r, r
        r += s.rd(2)
        for _ in range(8):          # first contact: FBB registers the caller, answer whatever it asks
            if b"Please enter" not in r[-200:] and b"City" not in r[-200:]: break
            ans = b"GB7LAF" if b"HomeBBS" in r[-200:] else b"00000" if b"ZIP" in r[-200:] else b"Labtown" if b"City" in r[-200:] else b"Lab"
            r = s.tx(ans + b"\r", 1.5)
    else:
        r = s.tx(b"BBS\r", 2)
    r = s.tx(b"SB LABTST @ WW\r", 2)
    r += s.tx(title + b"\r", 2)
    # body: send line by line (split after CR or LF) so the BBS sees realistic pacing
    for chunk in re.findall(rb"[^\r\n]*(?:\r\n|\r|\n)|[^\r\n]+$", body):
        s.s.sendall(chunk); s.log += b"[[" + chunk + b"]]"; time.sleep(PACE if len(body) < 5000 else 0.1)
    r = s.rd(0.5)
    s.log += b"[[" + ending + b"]]"; s.s.sendall(ending)
    r = b""; t0 = time.time()
    while time.time() - t0 < 90 and not re.search(rb"Bid:|sent @|accepted|Message #|rejected|Error", r):
        r += s.rd(1.0, 5)
    s.log += b"\n"
    s.tx(b"B\r", 2)
    s.s.close()
    os.makedirs(OUT, exist_ok=True)
    open(f"{OUT}/{where}-{key}.raw", "wb").write(s.log)
    return clean(r)
if __name__ == "__main__":
    for k in sys.argv[2:]:
        try: r = post(sys.argv[1], k)
        except Exception as e:
            r = repr(e).encode(); os.makedirs(OUT, exist_ok=True)
            open(f"{OUT}/{sys.argv[1]}-{k}.raw", "wb").write(GLOBAL[0].log)
        print(sys.argv[1], k, r.decode("latin1").strip().replace("\r\n", " | ")[-120:])
