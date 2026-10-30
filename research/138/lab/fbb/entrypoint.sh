#!/bin/bash
# Bring up ax25ipd -> kissattach (ax0) -> xfbbd inside the host network namespace.
set -u
modprobe ax25 2>/dev/null; modprobe mkiss 2>/dev/null
install -m644 /lab/etc/axports /etc/ax25/axports
install -m644 /lab/etc/ax25ipd.conf /etc/ax25/ax25ipd.conf
C=/usr/local/etc/ax25/fbb; V=/usr/local/var/ax25/fbb
install -m644 /lab/etc/port.sys $C/port.sys
install -m644 /lab/etc/forward.sys $C/forward.sys
install -m644 /lab/etc/bbs.sys $C/bbs.sys
install -m644 /lab/etc/reject.sys $C/reject.sys
if [ ! -f $C/fbb.conf.done ]; then
  { cat /lab/etc/fbb.conf.head; sed 's/^fbbfwd *=.*/fbbfwd = OK/' $C/fbbopt.conf; } > $C/fbb.conf
  touch $C/fbb.conf.done
fi
stdbuf -oL /usr/sbin/ax25ipd -f -c /etc/ax25/ax25ipd.conf >/run/axout 2>&1 &
for i in $(seq 1 50); do PTS=$(grep -oE '/dev/pts/[0-9]+' /run/axout | head -1); [ -n "$PTS" ] && break; sleep 0.2; done
ip link set ax0 down 2>/dev/null; ip link del ax0 2>/dev/null; true
/usr/sbin/kissattach "$PTS" axudp &
sleep 1
ip link set ax0 up 2>/dev/null; ip -brief link show ax0
cd $V
# xfbbd reads the sysop console from stdin; on first start it asks "create? (Y/N)" per data file,
# one read() each, so feed paced Ys, then block forever.
if [ -f $V/statis.dat ] && [ -f $V/dirmes.sys ]; then FEED='exec sleep infinity'; else FEED='for _ in $(seq 1 150); do echo Y; sleep 0.4; done; exec sleep infinity'; fi
( eval "$FEED" ) | /usr/local/sbin/xfbbd > /tmp/xfbbd.out 2>&1
echo "xfbbd exited rc=$?"; tail -20 /tmp/xfbbd.out; sleep 3600
