# QO-100 head end, through a transverter

A head end sending through QO-100's narrowband transponder: pdn-soundmodem on an SSB radio (an IC-706 on 70 cm, say) that drives a transverter up to 2.4 GHz.

| File | Goes in |
|---|---|
| [`soundmodem.json`](soundmodem.json) | `/etc/pdn-soundmodem/soundmodem.json` |
| [`headend.json`](headend.json) | `/etc/pdn-mailcast-headend/headend.json` |

Change the sound card, the keying, both callsigns, the API key (the same in both files), the locator, the slot minute and the BBS login. Then the frequencies.

## The frequencies

The transponder takes 2400.000 to 2400.500 MHz up and sends it back 8089.5 MHz higher, on 10489.500 to 10490.000 MHz, the same way up: upper sideband stays upper sideband ([AMSAT-DL's band plan](https://amsat-dl.org/en/p4-a-nb-transponder-bandplan-and-operating-guidelines)). The transverter adds its own oscillator before that: 1968 MHz for one driven on 432 MHz. Use your own transverter's figure.

| | Here | Where it goes |
|---|---|---|
| Centre of the signal on the downlink | 10489.631800 MHz | `frequencyHz` in `headend.json`: what listeners are told |
| Centre on the uplink | 2400.131800 MHz | nowhere: the downlink less 8089.5 MHz |
| Centre at the radio | 432.131800 MHz | `rfFrequency` in `soundmodem.json`: the uplink less the transverter's 1968 MHz |
| The radio's dial | 432.130300 MHz USB | nowhere: pdn-soundmodem prints it at start-up. Set the radio to exactly this |

The head end refuses an uplink frequency in `frequencyHz` and names the downlink to use.

## Check it

pdn-soundmodem's journal at start-up:

```
band plan: dial 432.130300 MHz USB, set your radio to this; ms110d-wn4 at 1500 Hz audio
```

The head end's journal in each slot (the line goes on):

```
calibration tone sent, 10 s at 1500 Hz, then the channel probe
```

The 1500 Hz is right. pdn-soundmodem puts the signal in the middle of a 300 to 2700 Hz transmit filter, and the tone goes at the centre of the data. A receiver still hears it at 1800 Hz, because its dial is 300 Hz lower than yours. A receiver's log for the same slot (the line goes on):

```
tone: 1800.0 Hz, 0.0 Hz from 1800 Hz
```

## What was seen

With the radio on 432.130300 MHz:

| | |
|---|---|
| Tone and channel probe on the air | 10489.631800 MHz |
| [Sound card receiver](../qo100-receiver-sound-card/) on a 10489.630000 MHz dial | tone at 1800.0 Hz, 17 of 17 frames, 3 of 3 bulletins |
| [LNB and RTL-SDR receiver](../qo100-receiver-lnb-rtl-sdr/) | tone at 1800.0 Hz, 17 of 17 frames, 3 of 3 bulletins |

## If the tone is off at the receiver

The radio is not on the dial pdn-soundmodem printed. With these files and the radio on the round 432.130000 MHz, the whole signal went out 300 Hz low, the tone on 10489.631500 MHz, and neither receiver heard a frame. The sound card receiver's log said (the line goes on):

```
tone: 1749.2 Hz, -50.8 Hz from 1800 Hz
```

Do not read the size of the error from that line. The receiver looks for the tone within 100 Hz of 1800 Hz (`SearchHz` in its source); this one was 300 Hz away, outside that, so what it printed was not the tone. Any tone line far from 0.0 Hz means the same thing: check the dial.

## With CAT

If rigctld controls the radio, add this to `soundmodem.json` and remove the `ptt` line that is there:

```json
"rig": { "rigctld": "127.0.0.1:4532" },
"ptt": { "type": "rigctld" },
```

pdn-soundmodem then sets the radio's dial itself. Seen, with the radio left on 432.000000 MHz beforehand:

```
rig: rigctld at 127.0.0.1:4531: 432.000000 MHz USB (2400 Hz passband)
rig: setting the rig to 432.130300 MHz USB (2801 Hz passband) from the band plan
```

The signal was then on 10489.631800 MHz and both receivers heard all 17 frames. (The bench's rigctld was on port 4531.)

## 1500 Hz or 1800 Hz in your audio

The default puts the signal at 1500 Hz in the transmit audio. To have it at 1800 Hz, on a round dial, add `"dialFrequency": 432130000` at the top level of `soundmodem.json` and set the radio to 432.130000 MHz. pdn-soundmodem warns that this is outside its nominal 300 to 2700 Hz, and starts. Seen: the same place on the air, tone at 1800.0 Hz at the receiver, 17 of 17 frames.

There is a trade:

| | At 1500 Hz (default) | At 1800 Hz (dial pinned) |
|---|---|---|
| pdn-soundmodem's CPU over a whole run, one x86 core | 2 min 28 s in 12 min 55 s, about 19% | 38 s in 12 min 56 s, about 5% |
| Through a 2.4 kHz transmit filter | the whole core of the signal passes | the top 300 Hz of it is cut |

Each run sent one slot of about 70 seconds and was otherwise idle, so nearly all of that is receiving. 1800 Hz is the waveform's own centre, and pdn-soundmodem's reference says it reaches any other centre by shifting the audio around the unchanged modem; the difference is the cost of that, and it is paid all the time. pdn-soundmodem has no transmit-only setting ([pdn-soundmodem#824](https://github.com/packet-net/pdn-soundmodem/issues/824)).

What the filter costs, from a separately rendered slot of 45 frames put through an ideal 300 to 2700 Hz filter with noise, decoded offline:

| Waveform, and signal over noise in 3 kHz | At 1500 Hz | At 1800 Hz | No filter |
|---|---|---|---|
| 1200 bps (WN4), 1.5 dB | 30 and 23 of 45 frames | 8 and 4 | 39 and 38 |
| 1200 bps (WN4), 3 dB | 45 and 45 | 44 and 45 | 45 and 45 |
| 3200 bps (WN6), 5 dB | 36 and 37 | 4 and 6 | 42 and 42 |
| 3200 bps (WN6), 6 dB | 45 and 45 | 41 and 40 | 45 and 45 |

Each cell is two runs with different noise. The gap only shows for a listener at the very edge of copy, and a real radio's filter is neither that sharp nor exactly there, so take the direction from this, not the size. `tools/bench/filter-study.py` made it.

Do not add `"frequency": 1800` beside `rfFrequency`: pdn-soundmodem refuses to start, with `modem 4 sets both "frequency" (1800) and "rfFrequency" (432131800): keep one.` Replacing `rfFrequency` with `"frequency": 1800` works (seen: 17 of 17 frames, radio on 432.130000 MHz), but pdn-soundmodem then has no band plan and prints no dial for you.

## A faster waveform

Change `"mode"` under `station` in `headend.json`, and nothing else. The head end sets pdn-soundmodem's modem to it, and receivers follow by themselves.

- With `"mode": "ms110d-wn6"` in `headend.json` only: the slot went out at 3200 bps and all 17 frames were heard.
- With `ms110d-wn6` in `soundmodem.json` only: the head end set the modem back to `ms110d-wn4` at start-up, and the slot went out at 1200 bps.

To take turns between waveforms there is `schedule.modes` ([headend.md](../../headend.md#a-waveform-per-slot)); that was not run here.

## The transponder's own rules

Read [AMSAT-DL's guidelines](https://amsat-dl.org/en/p4-a-nb-transponder-bandplan-and-operating-guidelines) before putting a scheduled head end on QO-100. As they stood on 2026-11-21:

- 10489.580 to 10489.650 MHz is for digital modes up to 2700 Hz wide. This waveform is 2400 baud with skirts out to about 3.2 kHz, so it relies on the radio's SSB filter to stay inside that. A wideband SDR transmitter would need a filter of its own.
- Unattended, automatic stations are allowed for emergency communication only, you must be able to hear your own downlink while sending, and no signal may be stronger than the beacon.

## Not tested here

A real radio, transverter, dish and transponder; keying; levels; the BBS login the head end takes bulletins from (the bench drops bulletin files in instead). The bench's radios pass 50 to 3600 Hz, wider than a real one.

Run it yourself: `tools/bench/bench.sh qo100-head-end`. The variants above are `qo100-head-end-radio-300-low`, `-cat`, `-pinned-dial`, `-frequency-1800`, `-faster` and `-faster-soundmodem-only`; `tools/bench/refusals.sh` shows the two refusals.
