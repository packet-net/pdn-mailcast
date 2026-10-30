#!/bin/bash
# capture.sh DEST - copy the stores of laa, lab (LinBPQ) and lax (XRouter PMS dir) into DEST
set -eu
cd "$(dirname "$0")"
D=$1; rm -rf $D/laa $D/lab $D/lax; mkdir -p $D/laa $D/lab $D/lax
for n in laa lab; do
  docker run --rm -v "$PWD/state/$n:/s:ro" -v "$PWD/$D/$n:/o" debian:stable-slim sh -c 'cp -a /s/Mail /s/DIRMES.SYS /s/WFBID.SYS /s/linmail.cfg /o/ 2>/dev/null; cp -a /s/logs /o/; chmod -R a+rX /o'
done
docker run --rm -v "$PWD/state/lax:/s:ro" -v "$PWD/$D/lax:/o" debian:stable-slim sh -c 'cp -a /s/PMS /s/LOG /o/; chmod -R a+rX /o'
