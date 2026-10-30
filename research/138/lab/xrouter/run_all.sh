#!/bin/bash
# run_all.sh VARIANT(b1f|ascii) - fresh lab; post every bulletin on laa and on lax; settle; capture.
set -eu
cd "$(dirname "$0")"
V=${1:-b1f}
VARIANT=$V ./up.sh
rm -rf captured/transcripts
for k in $(ls ../bulletins/*.title | xargs -n1 basename | sed 's/\.title//'); do
  python3 -I post.py laa $k; python3 -I post.py lax $k
done
mkdir -p captured/$V; rm -rf captured/$V/transcripts; mv captured/transcripts captured/$V/transcripts
prev=""; same=0
for i in $(seq 1 60); do
  sleep 20
  cur=$(ls state/laa/Mail state/lab/Mail | wc -l; ls state/lax/PMS | wc -l; wc -c < state/lax/PMS/GB7LAB.Q)
  [ "$cur" = "$prev" ] && same=$((same+1)) || same=0; prev=$cur
  [ $same -ge 6 ] && break
done
./capture.sh captured/$V
echo done
