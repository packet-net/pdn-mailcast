#!/bin/bash
# capture.sh DEST  - copy every BBS's stored files into DEST/{laa,lab,laf}
set -eu
cd "$(dirname "$0")/.."
D=$1; rm -rf $D/laf $D/laa $D/lab; mkdir -p $D/laa $D/lab $D/laf
for n in laa lab; do
  docker run --rm -v "$PWD/state/$n:/s:ro" -v "$PWD/$D/$n:/o" debian:stable-slim sh -c 'cp -a /s/Mail /s/DIRMES.SYS /s/WFBID.SYS /s/linmail.cfg /o/ 2>/dev/null; cp -a /s/logs /o/; ls /s | grep -v HTML > /o/ls.txt; chmod -R a+rX /o'
done
docker cp lab138-laf-1:/usr/local/var/ax25/fbb/mail $D/laf/mail
for f in dirmes.sys wfbid.sys inf.sys; do docker cp lab138-laf-1:/usr/local/var/ax25/fbb/$f $D/laf/$f; done
docker cp lab138-laf-1:/usr/local/var/ax25/fbb/log $D/laf/log
