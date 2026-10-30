#!/bin/bash
# run_all.sh VARIANT(b1f|ascii) - fresh lab; post every bulletin on laa and on lam; settle; capture.
set -eu
cd "$(dirname "$0")"
V=${1:-b1f}
VARIANT=$V ./up.sh
rm -rf captured/transcripts
for k in $(ls ../bulletins/*.title | xargs -n1 basename | sed 's/\.title//'); do
  # the 53 kB bulletin takes over an hour in plain ASCII forwarding at these link settings; skipped in the ascii variant
  [ "$V" = ascii ] && [ $k = t18_very_long ] && continue
  # in the ascii variant LinBPQ's end-of-message marker (/ex, lower case) is not recognised by openBCM's ascii receiver and the session hangs,
  # so only openBCM-origin bulletins are posted there
  [ "$V" = ascii ] || python3 -I post.py laa $k
  python3 -I post.py lam $k
done
prev=""; same=0
for i in $(seq 1 30); do
  sleep 20
  cur=$(ls state/lab/Mail | wc -l; wc -c < state/lab/DIRMES.SYS; find state/lam/info -type f | wc -l)
  [ "$cur" = "$prev" ] && same=$((same+1)) || same=0; prev=$cur
  [ $same -ge 6 ] && break
done
mkdir -p captured/$V; rm -rf captured/$V/transcripts; mv captured/transcripts captured/$V/transcripts
./capture.sh captured/$V
echo done
