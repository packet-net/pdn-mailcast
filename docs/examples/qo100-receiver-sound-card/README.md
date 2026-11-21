# QO-100 receiver on a sound card

A receiver for a station on QO-100, fed by a sound card: audio from the [QO-100 web SDR](https://eshail.batc.org.uk/nb/) in a browser, or from any receiver you tune by hand. The receiver does not tune anything here; you do.

| File | Goes in |
|---|---|
| [`receiver.json`](receiver.json) | `/etc/pdn-mailcast/receiver.json` |
| [`stations.json`](stations.json) | `/etc/pdn-mailcast/stations.json` |

Change the sound card, the BBS login, and the station under `listenFor`: its callsign, the minute of its slots, and `frequencyHz`, the centre of its signal on the downlink. Set `dialKHz` to that centre less 1.8 kHz, and tune the web SDR or radio to the same dial on USB.

| Setting | Here |
|---|---|
| `frequencyHz` | 10489631800 |
| `dialKHz`, and the web SDR's dial | 10489630.0, that is 10489.630000 MHz USB |

**You need `stations.json`.** Without it the receiver checks `dialKHz` against the station it ships with, M0LTE on 40 m, and will not start:

```
"dialKHz" 10489630 puts the signal's centre at -10482576200 Hz in your audio, outside the 1000 to 2000 Hz this receiver can use
```

The empty list here replaces the shipped one for this receiver, so it follows only what `listenFor` names. To keep receiving M0LTE on 40 m as well, run that as its own receiver: see [two receivers on one machine](../two-receivers-one-machine/).

## Check it

The journal at start-up:

```
pdn-mailcast receiver 0.43.0: listening on plughw:CARD=Device,DEV=0 (USB dial 10489.630 MHz)
stations: /etc/pdn-mailcast/stations.json lists no stations
slots: G8BPQ's slots are every hour at 15 minutes past (added by hand)
```

And at the first slot it hears:

```
tone: 1800.0 Hz, 0.0 Hz from 1800 Hz
store: pinned G8BPQ's key KEYID, learnt from its first signed directory; its bulletins are delivered from now on
listen-for: G8BPQ was heard (its own signed directory); no longer needed, its own slots are used from now on
```

Between slots a sound card receiver rests, and decodes only while its status page is open on the spectrogram. It starts decoding 2 minutes before each slot.

## What was seen

Hearing the [QO-100 head end](../qo100-head-end/) example: tone at 1800.0 Hz, 17 of 17 frames, 3 of 3 bulletins complete, and the receiver stopped decoding by itself about two and a half minutes after the slot began.

## Not tested here

A real web SDR or radio and its audio path into the sound card; the transponder's own drift; delivery to a BBS (there was none on the bench, so the receiver completed the bulletins and kept trying to deliver them).

Run it yourself: `tools/bench/bench.sh qo100-head-end`.
