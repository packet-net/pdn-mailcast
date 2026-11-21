# The examples bench

Runs the config files in [`docs/examples`](../../docs/examples/) through the released packages, with a simulated air between a transmitting station and its receivers. It is how the "what was seen" on each example page was seen.

```
sudo modprobe snd-aloop                    # once, for the sound card scenarios
tools/bench/bench.sh qo100-head-end        # one scenario; about 13 minutes
ls bench-out/qo100-head-end
```

It needs Docker. Each run is one slot: the bench sets the slot 4 minutes ahead, waits for it, and stops 9 minutes after (a receiver follows a station for at most one slot an hour, so there is no quicker way). Run at most three at once on a 12 core machine: the modems work in real time, and a starved one loses frames that a real one would not.

## What is in a run

| Piece | What it is |
|---|---|
| `Dockerfile` | Debian 13 with pdn-soundmodem, pdn-mailcast-headend and pdn-mailcast-receiver from the packet-net apt repository, Hamlib and Python |
| `run.sh` | Starts everything for one scenario inside the container, and collects the logs |
| `air.py` | The simulated air: see below |
| `scenarios/NAME.env` | Which example files a scenario runs, where each radio's dial is, and any one-line change that makes it a variant |
| `stand-in/` | A head end on 7.0538 MHz that stands in for M0LTE in the 40 m scenarios |

`run.sh` replaces only what a bench cannot have: the sound card (`pipe:` FIFOs, or an ALSA loopback card for a receiver on a sound card), the keying, the API key, the slot minute, the head end's BBS intake (bulletin files are dropped in instead) and the state directory. `configs-as-run` in the output holds each file as it ran, to compare.

## The air

`air.py` treats the transmitting modem's audio as upper sideband on the transmit radio's dial, and gives each receiver what an upper sideband radio on its own dial would hear: the same audio moved by the difference of the two dials. Each end can have a filter. A dial is a fixed number, or a Hamlib rigctld to follow, so a station that retunes its rig is heard where it tuned to.

It also measures every transmission as it leaves the transmitter. `air.jsonl` has one line for each, with its length, whether it was a tone or data, and its centre as audio and as RF, and one line whenever a rig's dial or mode changes.

There is no noise unless asked for, and no fading. The bench answers "do these settings put the signal where the other end listens". It is not a measure of how well anything decodes on a real path.

## Output

| File | What it holds |
|---|---|
| `bench.log` | What the bench started, and from which files |
| `air.jsonl` | Transmissions and dial changes |
| `tx-soundmodem.log`, `rx-soundmodem.log` | Each pdn-soundmodem's output |
| `headend.log`, `receiver.log`, `receiver-2.log` | The head end's and each receiver's |
| `processes.txt` | CPU time and memory of each program at the end |
| `configs-as-run/` | The config files as they ran |
