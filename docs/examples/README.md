# Worked examples

Whole config files for set-ups people have asked about. Each file was run exactly as it is here, through the released packages, and each page says what was seen.

| Example | For |
|---|---|
| [QO-100 head end](qo100-head-end/) | Sending mailcast through QO-100 from a radio with no CAT |
| [QO-100 receiver on a sound card](qo100-receiver-sound-card/) | Receiving a QO-100 station from a web SDR or any radio tuned by hand |
| [Receiver sharing a packet radio](receiver-sharing-a-packet-radio/) | An HF packet station whose radio moves to 7.052 MHz for each slot and back |
| [Two receivers on one machine](two-receivers-one-machine/) | A second receiver, such as one for QO-100 beside one for 40 m |

## The one rule about frequencies

A mailcast signal has one frequency on the air: the centre of the signal, `frequencyHz`. Everything else follows from it.

- Each radio hears or sends that centre at an audio frequency of its own: the signal's frequency minus that radio's USB dial.
- The two ends do not have to agree. A head end with the signal at 1500 Hz in its transmit audio and a receiver with it at 1800 Hz in its receive audio are both right, as long as each radio's dial is where its own program says.
- pdn-soundmodem prints the dial it expects when it starts: `band plan: dial 2400.130300 MHz USB, set your radio to this`. Set the radio there, not to a round number near it.
- The receiver tunes 1.8 kHz below the signal's centre unless you give it `dialKHz`.

If the receiver's log says the tone is far from 1800 Hz, one radio is not on the dial its program expects. Move the radio; do not change the audio frequency to compensate.

## How these were tested

The bench in [`tools/bench`](../../tools/bench/) runs the packaged programs in a container and puts a simulated air between them:

- The transmitting pdn-soundmodem's audio is treated as upper sideband on the transmit radio's dial, and each receiver gets the audio a radio on its own dial would hear. A radio with CAT is a Hamlib dummy rig, and the air follows wherever it is tuned.
- The bench measures every transmission as it leaves the transmitter, so each page can say where the signal really was.
- Only these parts of a file are replaced for a run: the sound card (by the simulated air), the keying (there is no radio to key), the pdn-soundmodem API key, the slot minute (so a run does not wait up to an hour), the BBS intake at the head end (bulletins are dropped in as files) and the state directory. Everything else runs as written.

What that does not cover: your radio's filters and levels, keying, a real BBS taking the bulletins, and a real path. Each page lists what it did and did not test.

The logs of every run are in [`tools/bench/evidence`](../../tools/bench/evidence/).

Tested on 2026-11-21 with pdn-soundmodem 0.109.0, pdn-mailcast-headend 0.43.0 and pdn-mailcast-receiver 0.43.0 from the packet-net apt repository, on Debian 13.
