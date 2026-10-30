#!/bin/bash
# run_all.sh VARIANT(b1f|ascii) - fresh lab; post every bulletin on laa and on laj; JNOS polls LAA (reverse forward),
# the area files are snapshotted BEFORE JNOS forwards (it deletes forwarded bulletins), then JNOS forwards to LAB; capture.
set -eu
cd "$(dirname "$0")"
V=${1:-b1f}
VARIANT=$V ./up.sh
rm -rf captured/transcripts
for k in $(ls ../bulletins/*.title | xargs -n1 basename | sed 's/\.title//'); do
  python3 -I post.py laa $k; python3 -I post.py laj $k
done
mkdir -p captured/$V; rm -rf captured/$V/transcripts; mv captured/transcripts captured/$V/transcripts
for i in 1 2; do docker exec lab138j-laj-1 tmux send-keys -t j "mbox ki Gb7laa" Enter; sleep 90; done
mkdir -p captured/$V/laj-before; docker run --rm -v "$PWD/state/laj:/s:ro" -v "$PWD/captured/$V/laj-before:/o" debian:stable-slim sh -c 'cp -a /s/spool /s/logs /o/; chmod -R a+rX /o'
docker exec lab138j-laj-1 tmux send-keys -t j "mbox ki Gb7lab" Enter
prev=""; same=0
for i in $(seq 1 30); do
  sleep 20
  cur=$(ls state/lab/Mail | wc -l; wc -c < state/lab/DIRMES.SYS)
  [ "$cur" = "$prev" ] && same=$((same+1)) || same=0; prev=$cur
  [ $same -ge 4 ] && break
done
./capture.sh captured/$V
echo done
