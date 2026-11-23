# Worked examples

Whole config files for set-ups people have asked about. Each file was run exactly as it is here, through the released packages, and each page says what was seen.

| Example | For |
|---|---|
| [QO-100 head end](qo100-head-end/) | Sending mailcast through QO-100 from an SSB radio and a transverter |
| [QO-100 receiver with an LNB and RTL-SDR](qo100-receiver-lnb-rtl-sdr/) | Receiving a QO-100 station with your own dish, LNB and dongle |
| [QO-100 receiver on a sound card](qo100-receiver-sound-card/) | Receiving a QO-100 station from a web SDR or any receiver tuned by hand |
| [Receiver sharing a packet radio](receiver-sharing-a-packet-radio/) | An HF packet station on pdn-soundmodem whose radio moves to 7.052 MHz for each slot and back, with or without a TNC of LinBPQ's own on the same radio |
| [Receiver sharing a radio with LinBPQ](receiver-sharing-a-radio-with-linbpq/) | The same without pdn-soundmodem: the receiver moves the radio and holds LinBPQ off itself |
| [Two receivers on one machine](two-receivers-one-machine/) | A second receiver, such as one for QO-100 beside one for 40 m |

## The one rule about frequencies

A mailcast signal has one frequency on the air: the centre of the signal, `frequencyHz`. Everything else follows from it.

- Each radio hears or sends that centre at an audio frequency of its own: the signal's frequency minus that radio's USB dial.
- The two ends do not have to agree. A head end with the signal at 1500 Hz in its transmit audio and a receiver with it at 1800 Hz in its receive audio are both right, as long as each radio's dial is where its own program says.
- pdn-soundmodem prints the dial it expects when it starts: `band plan: dial 432.130300 MHz USB, set your radio to this`. Set the radio there, not to a round number near it.
- A transverter, a transponder or an LNB each move the whole signal by a fixed amount and change nothing else. Work in the frequency each program's own radio is on: the radio's for pdn-soundmodem, the one listeners use for `frequencyHz`.
- The receiver tunes 1.8 kHz below the signal's centre unless you give it `dialKHz`.

If the receiver's log says the tone is far from 1800 Hz, one radio is not on the dial its program expects. Move the radio; do not change the audio frequency to compensate.

## How these were tested

The bench in [`tools/bench`](../../tools/bench/) runs the packaged programs in a container and puts a simulated air between them:

- The transmitting pdn-soundmodem's audio is treated as upper sideband on the transmit radio's dial, and each receiver gets the audio a radio on its own dial would hear. A radio with CAT is a Hamlib dummy rig, and the air follows wherever it is tuned. Where a station has LinBPQ, it is a real one, and a TNC of its own is a program that takes LinBPQ's frames over KISS, keys the dummy rig for each and writes down where the dial was. An RTL-SDR dongle behind an LNB is a program that speaks rtl_tcp's protocol and sends the IQ such a dongle would.
- The radios on the bench pass 50 to 3600 Hz of audio, wider than a real one, so nothing here says what your radio's filter does to the signal.
- The bench measures every transmission as it leaves the transmitter, so each page can say where the signal really was.
- Only these parts of a file are replaced for a run, and everything else runs as written:
  - the sound card, by the simulated air, and the keying, as there is no radio to key;
  - the pdn-soundmodem API key;
  - the slot: one a run, at a minute a few minutes ahead, every hour, with no daylight rule;
  - at the head end, the BBS it takes bulletins from (files are dropped in instead), and the ionosonde and PSK Reporter readings, which are turned off;
  - each receiver's state directory;
  - for a receiver that relies on the shipped stations file to know M0LTE, a stations file naming the bench's stand-in for M0LTE, with the bench's key;
  - where two stations that would be on two machines share the bench's one, the second's ports. Each scenario file in `tools/bench/scenarios` says what it moves.
- Log lines quoted on these pages are as the runs printed them, except for what a run cannot share with your machine: the sound card's name, paths under `/etc`, the slot's minute and the bench's key.

What that does not cover: your radio's filters and levels, keying, a real BBS taking the bulletins, and a real path. Each page lists what it did and did not test.

The logs of every run are in [`tools/bench/evidence`](../../tools/bench/evidence/).

Tested on 2026-11-21, and the LinBPQ and transmit-path scenarios on 2026-11-23 (LinBPQ 6.0.25.41), with pdn-soundmodem 0.109.0, pdn-mailcast-headend 0.43.0 and pdn-mailcast-receiver 0.43.0 from the packet-net apt repository, on Debian 13.
