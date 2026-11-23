#!/bin/bash
# The bpq-xmitoff hook against a real LinBPQ, right and wrong, for the record:
#   docker run --rm --network none --entrypoint bash -v "$PWD":/repo:ro mailcast-bench /repo/tools/bench/hook-failures.sh
hook=/repo/docs/examples/receiver-sharing-a-packet-radio/bpq-xmitoff
mkdir -p /var/lib/bench-linbpq && cd /var/lib/bench-linbpq && cp /repo/tools/bench/stand-in/bpq32.cfg .
(nc -lk 127.0.0.1 8100 >/dev/null &)   # something for LinBPQ's KISS port to connect to
LD_LIBRARY_PATH=/opt/linbpq /opt/linbpq/linbpq >/dev/null 2>&1 &
bpq=$!; sleep 15
try() { echo "== $1"; shift; "$@"; echo "exit status $?"; }
try "as written: off, then on" sh -c "$hook 1 && $hook 0"
sed 's/^PASSWORD=.*/PASSWORD=wrong/' $hook >/tmp/wrong-password && chmod +x /tmp/wrong-password
try "the wrong password" /tmp/wrong-password 1
sed 's/^HFPORT=.*/HFPORT=9/' $hook >/tmp/wrong-port && chmod +x /tmp/wrong-port
try "a port LinBPQ does not have" /tmp/wrong-port 1
kill $bpq; sleep 5; pkill -9 linbpq; sleep 2
try "LinBPQ not running" $hook 1
