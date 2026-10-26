#!/usr/bin/env python3
"""Read-only BPQ telnet client: log in, run a fixed list of commands, print the transcript.

Usage: bpqread.py HOST PORT CMD [CMD...]
Credentials come from $BPQ_LOGIN, default ~/.config/g8bpq/login (user line, password line).
Only list/read style commands are allowed; anything else is refused before sending.
"""
import os
import re
import socket
import sys
import time

ALLOWED = re.compile(r"^(BBS|L|LB|LL \d+|L> ?\S+|L< ?\S+|L@ ?\S+|LR|N|R \d+|RH \d+|V \d+|B|BYE|\?|HELP|I|NODES|MH|USERS|Tom)$", re.I)
IAC, DONT, DO, WONT, WILL, SB, SE = 255, 254, 253, 252, 251, 250, 240


def strip_telnet(buf: bytes, sock: socket.socket) -> bytes:
    out = bytearray()
    i = 0
    while i < len(buf):
        b = buf[i]
        if b == IAC and i + 1 < len(buf):
            cmd = buf[i + 1]
            if cmd in (DO, DONT, WILL, WONT) and i + 2 < len(buf):
                opt = buf[i + 2]
                reply = WONT if cmd in (DO, DONT) else DONT
                sock.sendall(bytes([IAC, reply, opt]))
                i += 3
                continue
            if cmd == SB:
                end = buf.find(bytes([IAC, SE]), i)
                i = len(buf) if end < 0 else end + 2
                continue
            i += 2
            continue
        out.append(b)
        i += 1
    return bytes(out)


# A BBS prompt ("de GB7NOT>") or a node prompt ("NOTTM:GB7NOT-2}") at the end of what has arrived.
PROMPT = re.compile(rb"(de [A-Z0-9-]+>|[A-Z0-9]+:[A-Z0-9-]+\})\s*$")


def read_for(sock: socket.socket, seconds: float, until_prompt: bool = False) -> str:
    """Read until `seconds` pass; with until_prompt, stop as soon as a prompt ends the output,
    otherwise after 2 s of quiet. A slow link pauses mid-message, so reads wait for the prompt."""
    sock.settimeout(0.5)
    data = bytearray()
    end = time.time() + seconds
    quiet = 0.0
    while time.time() < end:
        try:
            chunk = sock.recv(65536)
            if not chunk:
                break
            data += strip_telnet(chunk, sock)
            quiet = 0.0
            if until_prompt and PROMPT.search(bytes(data[-200:])):
                break
        except socket.timeout:
            quiet += 0.5
            if data and not until_prompt and quiet >= 2.0:
                break
    return data.decode("latin-1").replace("\r\n", "\n").replace("\r", "\n")


def main() -> int:
    host, port, cmds = sys.argv[1], int(sys.argv[2]), sys.argv[3:]
    for c in cmds:
        if not ALLOWED.match(c):
            print(f"refused: {c!r} is not a read-only command", file=sys.stderr)
            return 2
    user, password = open(os.path.expanduser(os.environ.get("BPQ_LOGIN", "~/.config/g8bpq/login"))).read().split("\n")[:2]
    with socket.create_connection((host, port), timeout=15) as s:
        out = read_for(s, 6)
        s.sendall((user + "\r").encode())
        out += read_for(s, 6)
        s.sendall((password + "\r").encode())
        out += read_for(s, 8)
        for c in cmds:
            s.sendall((c + "\r").encode())
            waits = c.upper() not in ("B", "BYE")
            out += f"\n>>> {c}\n" + read_for(s, 300 if waits else 10, until_prompt=waits)
    sys.stdout.write(out.replace(password, "********"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
