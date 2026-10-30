#!/bin/bash
# run_all.sh VARIANT(b1f|ascii) - fresh lab; post every bulletin on laa and on lax; settle; capture.
set -eu
cd "$(dirname "$0")"
V=${1:-b1f}
VARIANT=$V ./up.sh
rm -rf captured/transcripts
for k in $(ls ../bulletins/*.title | xargs -n1 basename | sed 's/\.title//'); do
  # the 53 kB bulletin never completes in plain ASCII forwarding at these link settings (XRouter drops the link and restarts it); skipped in the ascii variant
  [ "$V" = ascii ] && [ $k = t18_very_long ] && continue
  python3 -I post.py laa $k; python3 -I post.py lax $k
done
# LinBPQ sometimes never forwards a message that was entered while its forward session was open; repost any LAA bulletin XRouter lacks
sleep 120
for k in $(ls ../bulletins/*.title | xargs -n1 basename | sed 's/\.title//'); do
  t=$(cat ../bulletins/$k.title | head -c 30)
  if [ -f ../bulletins/$k.body ] && ! grep -l "_GB7LAA-1 &:GB7LAA" state/lax/PMS/0* 2>/dev/null | xargs -r grep -l -F "$t" >/dev/null 2>&1; then
    if [ $k != t15_lf_body ]; then echo "reposting $k on laa"; python3 -I post.py laa $k; fi
  fi
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
