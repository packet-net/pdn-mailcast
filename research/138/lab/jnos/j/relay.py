#!/usr/bin/env python3
"""JNOS 2.0q only does AXUDP through its own IP stack, which here sits behind a tun. This relay moves the UDP
datagrams between JNOS (over tun0, 10.77.0.2) and the real peers on eth0. Per link: JNOS talks to a local fake
peer address FAKE:FPORT (JNOS thinks that is the remote BBS); the relay forwards each datagram to REAL:RPORT from
the container port LPORT, which is what the LinBPQ MAP entry expects, and the reverse."""
import socket, select
JNOS = "10.77.0.2"
LINKS = [  # fake peer ip, fake peer port (JNOS matches incoming src==its source port and dst==its dest port, so both are P), JNOS source port, real peer ip, real peer port, container port
    ("10.77.0.11", 10094, 10094, "172.33.0.11", 10093, 10094),
    ("10.77.0.12", 10095, 10095, "172.33.0.12", 10093, 10095),
]
socks = []
for fip, fport, jport, rip, rport, lport in LINKS:
    a = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); a.bind((fip, fport))      # JNOS side
    b = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); b.bind(("172.33.0.13", lport)) # real side
    socks.append((a, b, jport, rip, rport))
fds = {}
for l in socks: fds[l[0]] = ("j", l); fds[l[1]] = ("r", l)
while True:
    r, _, _ = select.select(list(fds), [], [])
    for s in r:
        side, (a, b, jport, rip, rport) = fds[s]
        d, src = s.recvfrom(4096)
        if side == "j": b.sendto(d, (rip, rport))
        else:
            if src[0] == rip: a.sendto(d, (JNOS, jport))
