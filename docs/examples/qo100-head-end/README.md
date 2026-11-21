# QO-100 head end, on a radio with no CAT

A head end sending through the QO-100 narrowband transponder, from a radio pdn-soundmodem cannot tune (an IC-706 into a transverter, say). You set the radio's dial by hand, once.

| File | Goes in |
|---|---|
| [`soundmodem.json`](soundmodem.json) | `/etc/pdn-soundmodem/soundmodem.json` |
| [`headend.json`](headend.json) | `/etc/pdn-mailcast-headend/headend.json` |

Change the sound card, the keying, both callsigns, the API key (the same in both files), the locator, the slot minute and the BBS login. Then the three frequencies:

| Setting | Here | What it is |
|---|---|---|
| `rfFrequency` in `soundmodem.json` | 2400131800 | The centre of your signal on the uplink |
| The radio's dial | 2400.130300 MHz USB | What pdn-soundmodem prints at start-up. Set the radio to exactly this |
| `frequencyHz` in `headend.json` | 10489631800 | The centre of your signal on the downlink: the uplink plus 8089.5 MHz |

Listeners then tune 10489.630000 MHz USB, 1.8 kHz below the centre.

## Check it

pdn-soundmodem's journal at start-up:

```
band plan: dial 2400.130300 MHz USB, set your radio to this; ms110d-wn4 at 1500 Hz audio
```

The head end's journal in each slot:

```
calibration tone sent, 10 s at 1500 Hz, then the channel probe
```

The 1500 Hz is right. pdn-soundmodem puts the signal in the middle of a 300 to 2700 Hz transmit filter, and the tone goes at the centre of the data. A receiver still hears it at 1800 Hz, because its dial is 300 Hz lower than yours. A receiver's log for the same slot:

```
tone: 1800.0 Hz, 0.0 Hz from 1800 Hz
```

## What was seen

With the radio on 2400.130300 MHz, received by the [sound card receiver](../qo100-receiver-sound-card/):

| | |
|---|---|
| Tone and channel probe on the air | 10489.631800 MHz |
| Tone at the receiver | 1800.0 Hz |
| Frames | 17 sent, 17 heard |
| Bulletins | 3 of 3 complete |

## If the tone is off at the receiver

The radio is not on the dial pdn-soundmodem printed. With these files and the radio on the round 2400.130000 MHz, the whole signal went out 300 Hz low, the tone on 10489.631500 MHz, and the receiver heard no frames at all. Its log said:

```
tone: 1749.1 Hz, -50.9 Hz from 1800 Hz
```

Do not read the size of the error from that line. The receiver looks for the tone within 100 Hz of 1800 Hz; this one was 300 Hz away, outside that, so what it printed was not the tone. Any tone line far from 0.0 Hz means the same thing: check the dial.

## If you want a round dial

Add `"dialFrequency": 2400130000` at the top level of `soundmodem.json` and set the radio to 2400.130000 MHz. The signal then sits at 1800 Hz in your transmit audio and at the same place on the air. pdn-soundmodem warns that this is outside its nominal 300 to 2700 Hz and starts. Seen: tone at 1800.0 Hz at the receiver, 17 of 17 frames.

Do not add `"frequency": 1800` beside `rfFrequency`: pdn-soundmodem refuses to start, with `modem 4 sets both "frequency" (1800) and "rfFrequency" (2400131800): keep one.` Replacing `rfFrequency` with `"frequency": 1800` does work (seen: 17 of 17 frames, radio on 2400.130000 MHz), but pdn-soundmodem then has no band plan and prints no dial for you.

Through a 2.4 kHz transmit filter the default is the better of the two. The same slot through a 300 to 2700 Hz filter, with noise, decoded offline:

| Waveform and noise | Signal at 1500 Hz (the default) | Signal at 1800 Hz | No filter at all |
|---|---|---|---|
| 1200 bps (WN4), signal 1.5 dB over the noise | 30 and 23 of 45 frames | 8 and 4 | 39 and 38 |
| 1200 bps (WN4), 3 dB | 45 and 45 | 44 and 45 | 45 and 45 |
| 3200 bps (WN6), 5 dB | 36 and 37 | 4 and 6 | 42 and 42 |
| 3200 bps (WN6), 6 dB | 45 and 45 | 41 and 40 | 45 and 45 |

Each cell is two runs with different noise; the noise is measured in 3 kHz. The difference is about 1 dB, and it only shows for a listener at the very edge of copy. The filter was an ideal 300 to 2700 Hz one; a real radio's is neither that sharp nor exactly there, so treat this as the direction, not the size. `tools/bench/filter-study.py` is the script.

## A faster waveform

Change `"mode"` under `station` in `headend.json`, and nothing else. The head end sets pdn-soundmodem's modem to it, and receivers follow by themselves.

- With `"mode": "ms110d-wn6"` in `headend.json` only: the slot went out at 3200 bps and all 17 frames were heard.
- With `ms110d-wn6` in `soundmodem.json` only: the head end set the modem back to `ms110d-wn4` at start-up, and the slot went out at 1200 bps.

To take turns between waveforms, use `schedule.modes` instead ([headend.md](../../headend.md#a-waveform-per-slot)); that was not run here.

## Not tested here

A real radio, transverter and transponder; keying; levels; the BBS login the head end takes bulletins from (the bench drops bulletin files in instead).

pdn-soundmodem has no transmit-only setting: it decodes its receive audio all the time. On the bench it used about a fifth of one x86 core doing so, with nothing to hear.

Run it yourself: `tools/bench/bench.sh qo100-head-end`. The variants above are `qo100-head-end-radio-300-low`, `-pinned-dial`, `-frequency-1800`, `-faster` and `-faster-soundmodem-only`.
