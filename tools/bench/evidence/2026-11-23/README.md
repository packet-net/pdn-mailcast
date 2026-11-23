# Bench runs of 2026-11-23

Five more scenarios as they ran, unedited, on the same packages as [2026-11-21](../2026-11-21/README.md) (pdn-soundmodem 0.109.0, pdn-mailcast-receiver and head end 0.43.0), with LinBPQ 6.0.25.41 in the four that have one. `kiss-tnc.jsonl` in those is the stand-in TNC's record of every frame LinBPQ handed it: when, and where the radio's dial was.

- `receiver-sharing-a-packet-radio-transmit-paths`: run alone. `hooks.log` is what its two hook commands wrote when they ran, and `during.log` is what was handed to the listener's modems and when. Its dummy rig was started without `-P RIG`, before `run.sh` added that.
- `receiver-sharing-a-radio-with-linbpq`, `-ptt-unreadable` and `receiver-sharing-a-packet-radio-another-tnc`: run together.
- `receiver-sharing-a-packet-radio-another-tnc-no-hooks`: run alone, after those.
- `rigctld-ptt.txt`: what Hamlib's dummy rig answers to "is it transmitting", with and without `-P RIG`.
