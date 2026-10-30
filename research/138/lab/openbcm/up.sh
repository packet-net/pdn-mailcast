#!/bin/bash
# Fresh openBCM lab: wipe state, seed configs, start LAA (LinBPQ), LAM (openBCM in tmux), LAB (LinBPQ).
set -eu
cd "$(dirname "$0")"
docker compose down -v 2>/dev/null || true
docker run --rm -v "$PWD:/w" debian:stable-slim rm -rf /w/state
for n in laa lab; do
  mkdir -p state/$n; cp bpq/$n/bpq32.cfg bpq/$n/linmail.cfg state/$n/
  if [ "${VARIANT:-b1f}" = ascii ]; then
    sed -i 's/AllowBlocked = 1/AllowBlocked = 0/;s/AllowCompressed = 1/AllowCompressed = 0/;s/UseB1Protocol = 1/UseB1Protocol = 0/;s/UseB2Protocol = 1/UseB2Protocol = 0/;s/SendCTRLZ = 1/SendCTRLZ = 0/' state/$n/linmail.cfg
  fi
done
mkdir -p state/lam; cp b/init.bcm b/init.l2 b/fwd.bcm b/rhosts.bcm state/lam/
chmod -R a+rwX state
docker compose up -d
sleep 8
