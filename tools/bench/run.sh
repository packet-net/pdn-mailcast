#!/bin/bash
# Runs one scenario inside the bench container: /repo is this repository, /out takes the logs,
# and $1 names a file in tools/bench/scenarios. README.md beside this file says what it does.
set -u
O=/out; R=/run/bench; X=/repo/docs/examples; B=/repo/tools/bench
mkdir -p $R $O /etc/bench; cd $R
SUB=${SUB:-0}   # which ALSA loopback subdevice this run has to itself
TX_PATCH=.; HEADEND_PATCH=.; RX_PATCH=.; RECEIVER_PATCH=.; RECEIVER2_PATCH=.
. $B/scenarios/$1.env
KEY=bench-api-key
say() { echo "$(date -u +%H:%M:%S) bench: $*" | tee -a $O/bench.log; }

# The slot: SLOT_LEAD minutes from now, at that minute past each hour (a receiver follows a
# station for at most one slot an hour, so the bench runs one slot and stops).
now=$(date -u +%s)
slot_epoch=$(( (now / 60 + ${SLOT_LEAD:-4}) * 60 )); MINUTE=$(( (slot_epoch / 60) % 60 ))
say "scenario $1; slot at $(date -u -d @$slot_epoch +%H:%M) UTC"

for n in ${RIGS:-}; do   # NAME:PORT:HZ:MODE:PASSBAND, a Hamlib dummy rig
  IFS=: read name port hz mode pb <<<"$n"
  rigctld -m 1 -t $port >$O/rigctld-$name.log 2>&1 &
  sleep 1; printf 'F %s\nM %s %s\n' $hz $mode $pb | nc -q1 127.0.0.1 $port >/dev/null
  say "dummy rig $name on port $port at $hz Hz $mode"
done

python3 $B/air.py --log $O/air.jsonl $AIR >$O/air.out 2>&1 &
sleep 2

soundmodem() {   # NAME FILE PATCH: the audio device, the keying and the API key are replaced
  jq --arg dev "pipe:$R/$1.in,$R/$1.out" --arg key $KEY "$3"' | .device=$dev | del(.captureDevice,.playbackDevice,.alsa)
     | (if (.ptt.type // "") == "rigctld" then . else del(.ptt) end) | .api.key=$key' $2 >/etc/bench/$1-soundmodem.json
  pdn-soundmodem --config /etc/bench/$1-soundmodem.json >$O/$1-soundmodem.log 2>&1 &
  say "pdn-soundmodem $1 started from ${2#/repo/}$([ "$3" = . ] || echo " with $3")"
}
soundmodem tx ${TX_SOUNDMODEM:-$B/stand-in/soundmodem.json} "$TX_PATCH"
[ -n "${RX_SOUNDMODEM:-}" ] && soundmodem rx $RX_SOUNDMODEM "$RX_PATCH"
sleep 5

# The head end: the slot time, no BBS (bulletins come from the file drop), no outside feeds.
HEADEND=${HEADEND:-$B/stand-in/headend.json}
jq --arg key $KEY --argjson minute $MINUTE "$HEADEND_PATCH"' | .station.apiKey=$key | .slot.minute=$minute
   | .slot.everyMinutes=60 | del(.slot.daylight,.intake.fbb,.intake.reports)
   | .ionosphere.stations=[] | .pskReporter.enabled=false' $HEADEND >/etc/bench/headend.json
pdn-mailcast-headend --check-config --config /etc/bench/headend.json >$O/headend-check.log 2>&1
PUBKEY=$(pdn-mailcast-headend --make-key 2>&1 | grep -oE '[A-Za-z0-9+/]{40,}=*' | tail -1)
CALL=$(jq -r .callsign /etc/bench/headend.json); HZ=$(jq -r .frequencyHz /etc/bench/headend.json)
mkdir -p /var/lib/pdn-mailcast-headend/drop
for i in $(seq 1 ${BULLETINS:-3}); do
  { printf 'Type: B\nFrom: G0ABC\nTo: BENCH\nAt: WW\nBid: %s%d_BENCH\nDate: %s\nTitle: Bench bulletin %d\n\n' \
      $(date -u +%H%M) $i $(date -u +%Y-%m-%dT%H:%M:%SZ) $i
    for l in $(seq 1 ${BULLETIN_LINES:-12}); do echo "Line $l of bench bulletin $i: the quick brown fox jumps over the lazy dog."; done
  } >/var/lib/pdn-mailcast-headend/drop/b$i.txt
done
pdn-mailcast-headend --config /etc/bench/headend.json >$O/headend.log 2>&1 &
say "head end $CALL started from ${HEADEND#/repo/}$([ "$HEADEND_PATCH" = . ] || echo " with $HEADEND_PATCH")"

receiver() {   # NAME FILE PATCH CARD [STATIONS]: the sound card, the state directory and the slot minute are replaced
  local dir=/etc/bench/$1; mkdir -p $dir
  jq --arg state /var/lib/bench-$1 --argjson minute $MINUTE --arg card "$4" "$3"' | .stateDirectory=$state
     | (if (.audio | startswith("plughw:")) then .audio=$card else . end)
     | (if .listenFor then .listenFor |= map(.minute=$minute) else . end)' $2 >$dir/receiver.json
  if [ -n "${5:-}" ]; then
    cp $5 $dir/stations.json   # the example's own stations file, as it is
  else
    # No stations file in the example: a real install then uses the shipped one, which names
    # M0LTE. Here the bench head end stands in for M0LTE, with its own key and slot minute.
    jq -n --arg key "$PUBKEY" --argjson minute $MINUTE --arg call $CALL --argjson hz $HZ '
      {report:"MCAST@GB7RDG.#42.GBR.EURO", stations:[{callsign:$call, publicKey:$key,
        slots:{minute:$minute, everyMinutes:60}, frequencyHz:$hz, sideband:"usb", locator:"IO91lk"}]}' >$dir/stations.json
  fi
  pdn-mailcast-receiver --config $dir/receiver.json >$O/$1.log 2>&1 &
  say "receiver $1 started from ${2#/repo/}${5:+ and ${5#/repo/}}"
}
[ -n "${RECEIVER:-}" ] && receiver receiver $RECEIVER "$RECEIVER_PATCH" "plughw:CARD=Loopback,DEV=1,SUBDEV=$SUB" "${RECEIVER_STATIONS:-}"
[ -n "${RECEIVER2:-}" ] && receiver receiver-2 $RECEIVER2 "$RECEIVER2_PATCH" "plughw:CARD=Loopback,DEV=1,SUBDEV=$((SUB + 1))" "${RECEIVER2_STATIONS:-}"

[ -n "${DURING:-}" ] && ( bash $B/scenarios/$DURING $slot_epoch >$O/during.log 2>&1 & )

end=$(( slot_epoch + ${RUN_MINUTES:-9} * 60 ))
while [ $(date -u +%s) -lt $end ]; do sleep 5; done
ps -eo etimes,cputime,rss,args | grep -E "pdn-|rigctld|air.py" | grep -v grep | cut -c1-140 >$O/processes.txt
cp -r /etc/bench $O/configs-as-run
say "done"
