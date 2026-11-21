# QO-100 receiver with a dish, an LNB and an RTL-SDR dongle

A receiver for a station on QO-100 with your own dish: the LNB brings the 10 GHz downlink down to about 739 MHz, an RTL-SDR dongle behind `rtl_tcp` receives that, pdn-soundmodem turns it into audio, and the receiver decodes it.

| File | Goes in |
|---|---|
| [`soundmodem.json`](soundmodem.json) | `/etc/pdn-soundmodem/soundmodem.json` |
| [`receiver.json`](receiver.json) | `/etc/pdn-mailcast/receiver.json` |

`rtl_tcp` has to be running where the dongle is; `rtltcp:127.0.0.1` in `soundmodem.json` is for the same machine. Change the BBS login, and the station under `listenFor`: its callsign, the minute of its slots, and `frequencyHz`, the centre of its signal on the downlink. Then the frequencies.

## The frequencies

An LNB takes its oscillator, nominally 9750 MHz, off everything it hears. A real one is some kHz away from that, and the dongle's own crystal adds its error, so the figure has to be measured for your LNB and dongle together: [receiver.md](../../receiver.md#qo-100-through-an-lnb) says how, against the transponder's beacon.

| | Here | Where it goes |
|---|---|---|
| The LNB's oscillator, as measured | 9750.012345 MHz | `lnbHz` in `receiver.json` |
| Centre of the station's signal on the downlink | 10489.631800 MHz | `frequencyHz` under `listenFor` |
| Centre at the dongle | 739.619455 MHz | nowhere: the downlink less `lnbHz` |
| pdn-soundmodem's dial | 739.617655 MHz USB | `dialFrequency` in `soundmodem.json`: 1800 Hz below that |

**You need `listenFor`.** Without it the receiver hears the signal and ignores it:

```
tone: 1800.0 Hz, 0.0 Hz from 1800 Hz, SNR 59.7 dB in 3 kHz, 10 s, but it began at 20:31:03 UTC, 29 min from any slot, so it is not M0LTE's: ignored
intake: a frame to MCAST from G8BPQ was ignored: not from an accepted source (M0LTE); follow the station or add it to "sources" in the config file
```

## Check it

pdn-soundmodem's journal at start-up:

```
audio: rtl_tcp 127.0.0.1:1234 (dongle at 739.592655 MHz, 1024000 S/s, tuner AGC) -> USB 150-3450 Hz at 739.617655 MHz, audio at 12000 Hz (RECEIVE ONLY)
```

The receiver's:

```
audio: pdn-soundmodem's radio is on USB dial 739.617655 MHz, in the LNB's range; listening continuously for 10489.6318 MHz until it hears a station
```

And once it has heard the station's first signed directory:

```
store: pinned G8BPQ's key KEYID, learnt from its first signed directory; its bulletins are delivered from now on
listen-for: G8BPQ was heard (its own signed directory); no longer needed, its own slots are used from now on
slots: now following every hour at 15 minutes past
```

Until then the log is misleading ([#287](https://github.com/packet-net/pdn-mailcast/issues/287)): at start-up it says the station is `not listened at`, and it reports the first tone as `not M0LTE's: ignored`. It decodes all the same.

## What was seen

Hearing the [QO-100 head end](../qo100-head-end/) example through an emulated LNB and dongle: the tone at 1800.0 Hz, all 17 frames (the first 2 booked to an earlier slot, from before it had learnt the station's own) and 3 of 3 bulletins complete.

## If `lnbHz` is out

The tone line tells you by how much, once you are within 100 Hz. With the LNB's oscillator really 40 Hz below the `lnbHz` in the file, the receiver said `tone: 1840.0 Hz, +40.0 Hz from 1800 Hz` and still completed all 3 bulletins. To put that right, take the figure off `lnbHz` and add it to `dialFrequency`.

With the nominal 9750000000 in both files, and the oscillator really 12.345 kHz above that, the receiver heard nothing at all: no tone line, and `nothing heard` for the slot.

## Not tested here

A real dish, LNB, dongle and `rtl_tcp`: their drift, their gain, and power to the LNB. Measuring `lnbHz` against the beacon. Delivery to a BBS. The bench's dongle is a program that speaks rtl_tcp's protocol and sends what a dongle behind that LNB would.

Run it yourself: `tools/bench/bench.sh qo100-head-end`, and `qo100-receiver-lnb-40-hz-out` and `qo100-receiver-lnb-uncalibrated` for the last section.
