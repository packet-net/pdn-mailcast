#!/bin/bash
# Two settings the programs refuse, run for the record:
#   docker run --rm --network none --entrypoint bash -v "$PWD":/repo:ro mailcast-bench /repo/tools/bench/refusals.sh
E=/repo/docs/examples/qo100-head-end
echo '== pdn-soundmodem: "frequency": 1800 added beside "rfFrequency"'
jq '.device="null" | del(.ptt) | .modems[0].frequency=1800' $E/soundmodem.json >/tmp/both.json
timeout 20 pdn-soundmodem --config /tmp/both.json; echo "exit status $?"
echo '== pdn-mailcast-headend: "frequencyHz" given as the uplink, 2400131800'
jq '.frequencyHz=2400131800' $E/headend.json >/tmp/uplink.json
pdn-mailcast-headend --check-config --config /tmp/uplink.json; echo "exit status $?"
