#!/bin/bash
# Fresh lab: wipe state, seed BPQ configs, start the three BBSes. VARIANT=ascii switches every BPQ
# partner record to plain (uncompressed, unblocked) MBL forwarding. Run on a Linux host with the
# ax25 module available and docker (see README.md).
set -eu
cd "$(dirname "$0")/.."
docker compose down -v 2>/dev/null || true
sudo ip link del ax0 2>/dev/null || true
docker run --rm -v "$PWD:/w" debian:stable-slim rm -rf /w/state
for n in laa lab; do
  mkdir -p state/$n; cp bpq/$n/bpq32.cfg bpq/$n/linmail.cfg state/$n/
  if [ "${VARIANT:-b1f}" = ascii ]; then
    sed -i 's/AllowBlocked = 1/AllowBlocked = 0/;s/AllowCompressed = 1/AllowCompressed = 0/;s/UseB1Protocol = 1/UseB1Protocol = 0/;s/UseB2Protocol = 1/UseB2Protocol = 0/;s/SendCTRLZ = 1/SendCTRLZ = 0/' state/$n/linmail.cfg
  fi
done
sudo modprobe ax25 mkiss || true
docker compose up -d --build
# FBB first start creates its data files (about 60 s); wait for it to listen on ax0
until grep -q "GB7LAF-1 \* " /proc/net/ax25 2>/dev/null; do sleep 3; done
sleep 5
echo lab up
