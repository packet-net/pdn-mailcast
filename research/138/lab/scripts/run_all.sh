#!/bin/bash
# run_all.sh VARIANT(b1f|ascii) - fresh lab, post every bulletin on laa and laf, let forwarding settle, capture.
set -eu
cd "$(dirname "$0")/.."
V=${1:-b1f}
VARIANT=$V ./scripts/up.sh
rm -rf captured/transcripts
for k in $(ls bulletins/*.title | xargs -n1 basename | sed 's/\.title//'); do
  python3 -I scripts/post.py laa $k; python3 -I scripts/post.py laf $k
done
mkdir -p captured/$V; mv captured/transcripts captured/$V/transcripts
echo "posted; waiting for forwarding to settle"
prev=""; same=0
for i in $(seq 1 60); do
  sleep 20
  cur=$(ls state/laa/Mail state/lab/Mail | wc -l; docker exec lab138-laf-1 sh -c 'find /usr/local/var/ax25/fbb/mail -type f | wc -l')
  [ "$cur" = "$prev" ] && same=$((same+1)) || same=0; prev=$cur
  [ $same -ge 6 ] && break
done
./scripts/capture.sh captured/$V
echo done
