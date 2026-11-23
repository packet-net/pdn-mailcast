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
| `Dockerfile` | Debian 13 with pdn-soundmodem, pdn-mailcast-headend and pdn-mailcast-receiver from the packet-net apt repository, Hamlib, Python, and LinBPQ from the image this repo's tests use |
| `run.sh` | Starts everything for one scenario inside the container, and collects the logs |
| `air.py` | The simulated air: see below |
| `kiss-tnc.py` | A stand-in for a hardware KISS TNC on the listener's radio: it takes a real LinBPQ's frames, keys the dummy rig for each, and writes down where the dial was in `kiss-tnc.jsonl`. It puts nothing on the air |
| `hook-failures.sh` | The `bpq-xmitoff` hook against the bench's LinBPQ, right and wrong |
| `filter-study.py`, `refusals.sh` | Two things that need no air: an offline comparison of where the signal sits in a transmit filter, and two settings the programs refuse |
| `scenarios/NAME.env` | Which example files a scenario runs, where each radio's dial is, and any one-line change that makes it a variant |
| `stand-in/` | A head end on 7.0538 MHz that stands in for M0LTE in the 40 m scenarios, and the `bpq32.cfg` of the bench's LinBPQ |

`run.sh` replaces only what a bench cannot have; [the examples' README](../../docs/examples/README.md#how-these-were-tested) lists every item. `configs-as-run` in the output holds each file as it ran, to compare.

A run has no network but loopback, so runs side by side cannot find each other's receivers.

## The air

`air.py` treats the transmitting modem's audio as upper sideband on the transmit radio's dial, and gives each receiver what an upper sideband radio on its own dial would hear: the same audio moved by the difference of the two dials. Each end can have a filter. A dial is a fixed number, or a Hamlib rigctld to follow, so a station that retunes its rig is heard where it tuned to.

A receiver can also be an RTL-SDR dongle behind an LNB: the air serves rtl_tcp's protocol on a port, lets the receiving program tune it, and sends the IQ a dongle would, with the LNB's oscillator taken off.

It also measures every transmission as it leaves the transmitter. `air.jsonl` has one line for each, with its length, whether it was a tone or data, and its centre as audio and as RF, and one line whenever a rig's dial or mode changes. For data the centre is the middle of the band holding 98% of the power, which sits a few tens of Hz high when the bottom of the signal is cut off at 0 Hz; the tone and the probe are exact. A `gap` line means silence went out between two blocks of audio less than 50 ms apart: the modem fell behind, or one transmission followed another that closely.

Both ends pass 50 to 3600 Hz unless a scenario says otherwise, which is wider than a real radio.

There is no noise unless asked for, and no fading. The bench answers "do these settings put the signal where the other end listens". It is not a measure of how well anything decodes on a real path.

## Output

| File | What it holds |
|---|---|
| `bench.log` | What the bench started, and from which files |
| `air.jsonl`, `air.out` | Transmissions and dial changes; anything the air itself complained of |
| `tx-soundmodem.log`, `rx-soundmodem.log` | Each pdn-soundmodem's output |
| `headend-check.log`, `headend.log`, `receiver.log`, `receiver-2.log` | The head end's configuration check and output, and each receiver's |
| `processes.txt` | CPU time and memory of each program at the end |
| `configs-as-run/` | The config files as they ran |
