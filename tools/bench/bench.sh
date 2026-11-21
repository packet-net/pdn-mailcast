#!/bin/bash
# Runs one bench scenario: tools/bench/bench.sh SCENARIO [OUTPUT-DIR [LOOPBACK-SUBDEVICE]]
# Needs Docker, and for the sound card scenarios the snd-aloop module (sudo modprobe snd-aloop).
set -eu
here=$(cd "$(dirname "$0")" && pwd); repo=$(cd "$here/../.." && pwd)
scenario=$1; out=${2:-$PWD/bench-out/$scenario}; sub=${3:-0}
docker build -q -t mailcast-bench "$here" >/dev/null
mkdir -p "$out"
sound=(); [ -e /dev/snd ] && sound=(--device /dev/snd)
exec docker run --rm --name "bench-$scenario" "${sound[@]}" -e SUB=$sub \
  -v "$repo":/repo:ro -v "$out":/out mailcast-bench "$scenario"
