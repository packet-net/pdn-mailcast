#!/bin/bash
# Runs one bench scenario: tools/bench/bench.sh SCENARIO [OUTPUT-DIR [LOOPBACK-SUBDEVICE]]
# Needs Docker, and for the sound card scenarios the snd-aloop module (sudo modprobe snd-aloop).
set -eu
here=$(cd "$(dirname "$0")" && pwd); repo=$(cd "$here/../.." && pwd)
scenario=$1; out=${2:-$PWD/bench-out/$scenario}; sub=${3:-0}
docker build -q -t mailcast-bench "$here" >/dev/null
mkdir -p "$out"
sound=(); [ -e /dev/snd ] && sound=(--device /dev/snd)
# No network but loopback: nothing in a run needs one, and runs side by side must not find
# each other's receivers.
exec docker run --rm --name "bench-$scenario" --network none "${sound[@]}" -e SUB=$sub \
  -v "$repo":/repo:ro -v "$out":/out mailcast-bench "$scenario"
