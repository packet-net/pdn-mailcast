## Head end, 5 October to 7 November 2026

- Days with slots: 33; slots run 259, completed 252, cut short 6, skipped 1 (by each day's last tally).
- Slots that sent frames: 254; frames sent: 25397; bursts: 5182.
- Frames by waveform: ms110d-wn4 13500, ms110d-wn3 9320, ms110d-wn4 (long interleaver) 2544, not logged 33.
- Time on air per completed slot (start to closing ident): median 7.6 min, total 29.6 h over 252 slots.
- Slots that found the channel busy and waited: 150 of 258.
- Bulletins taken in from GB7RDG: 465 distinct BIDs, 3492 kB of text, median 5285 bytes.

## Web SDR survey, 6 to 20 October 2026

| SDR | km | slots listened | slots with frames | frames heard | median tone SNR (dB) | bulletins rebuilt |
|---|---|---|---|---|---|---|
| G4WNC | 83 | 58 | 9% | 1% | -8 | 1 |
| G4EYR | 89 | 119 | 50% | 13% | 0 | 80 |
| STUEY3D | 97 | 119 | 87% | 71% | 14 | 198 |
| WESSEX | 135 | 119 | 88% | 80% | 24 | 200 |
| M9TWM-1 | 221 | 111 | 67% | 24% | 3 | 131 |
| EI4HQ | 502 | 119 | 100% | 79% | 13 | 223 |
| M9PSY | 534 | 119 | 96% | 69% | 11 | 220 |
| GI3JMC | 544 | 119 | 1% | 0% | -10 | 0 |

All SDRs together: 45% of frames heard over 883 receiver-slots.

## Listeners' daily reports, 8 October to 7 November 2026

124 reports from 8 listeners; 8 report-days with no audio at all are left out below.

| Listener | locator | audio | days | slots listened | slots with frames | frames heard | median tone SNR (dB) | bulletins rebuilt |
|---|---|---|---|---|---|---|---|---|
| M7TAW | IO91ma | sound card | 10 | 79 | 82% | 62% | 13 | 159 |
| M0NVK | IO90hr | sdrconnect | 12 | 88 | 84% | 58% | 20 | 143 |
| GM4CXF | IO75qn | sound card | 12 | 91 | 98% | 48% | 11 | 152 |
| G8BPQ | IO92kx | sound card | 23 | 172 | 82% | 23% | 4 | 163 |
| M9TWM | IO93fj | m9twm-1.tunnel.ubersdr.org | 8 | 64 | 88% | 21% | 5 | 41 |
| G7TAJ | IO81le | sound card | 20 | 153 | 65% | 18% | 8 | 125 |
| EI7KX | IO83jj | nope.tunnel.ubersdr.org | 6 | 41 | 88% | 18% | 4 | 43 |
| GM8BPQ | IO68vl | sound card | 25 | 184 | 76% | 14% | 4 | 99 |

### WN4 (1200 bps) against WN3 (600 bps)

Share of frames heard, per receiver, over slots of each waveform it listened to (survey and reports).

| Receiver | WN4 | WN3 |
|---|---|---|
| EI4HQ | 76% | 83% |
| EI7KX | 19% | 16% |
| G4EYR | 12% | 15% |
| G4WNC | 0% | 2% |
| G7TAJ | 17% | 20% |
| G8BPQ | 15% | 36% |
| GI3JMC | 0% | 0% |
| GM4CXF | 46% | 51% |
| GM8BPQ | 10% | 21% |
| M0NVK | 58% | 57% |
| M7TAW | 62% | 61% |
| M9PSY | 66% | 73% |
| M9TWM | 18% | 26% |
| M9TWM-1 | 21% | 28% |
| STUEY3D | 69% | 75% |
| WESSEX | 80% | 81% |

### By hour of day (UTC), all report slots

| Hour | slots | slots with frames | frames heard |
|---|---|---|---|
| 09 | 88 | 73% | 30% |
| 10 | 111 | 75% | 31% |
| 11 | 112 | 82% | 30% |
| 12 | 114 | 83% | 32% |
| 13 | 113 | 82% | 27% |
| 14 | 113 | 85% | 34% |
| 15 | 113 | 79% | 27% |
| 16 | 108 | 81% | 27% |

### The measured channel (report slots with a channel measurement)

- Slots measured: 719 (591 from the probe, 128 from the bursts).
- Propagation modes seen: 1: 237, 2: 333, 3: 130, 4: 19.
- virtual height (km): median 266, quartiles 243 to 290, n=402.
- second mode's delay (ms): median 1.6, quartiles 1.4 to 1.8, n=415.
- second mode's power against the first (dB): median -12, quartiles -15 to -6, n=415.
- delay spread (ms): median 0.41, quartiles 0.29 to 0.57, n=719.
- Doppler spread (Hz): median 0.34, quartiles 0.24 to 0.54, n=718.

### The sounding probe (report slots)

- Report slots that sent frames: 852.
- Measured from the probe: 591, of which 60 decoded no data frame at all.
- Measured from the data bursts (no probe result): 128.
- No measurement: 133 (41 of them with frames).
- Lowest tone SNR at which the probe gave a measurement: -16 dB; median 6 dB.
- Median virtual height by hour (km): 09:00 271 (n=25), 10:00 260 (n=55), 11:00 263 (n=53), 12:00 270 (n=50), 13:00 256 (n=51), 14:00 278 (n=60), 15:00 261 (n=56), 16:00 265 (n=52).

### The head end's propagation verdicts against what was heard (survey and report slots)

| Source | verdict | receiver-slots | with frames | frames heard |
|---|---|---|---|---|
| ionosonde | good | 801 | 83% | 44% |
| ionosonde | marginal | 666 | 63% | 35% |
| ionosonde | unknown | 232 | 78% | 26% |
| ionosonde | not logged | 56 | 23% | 14% |
| PSK Reporter | good | 1665 | 75% | 38% |
| PSK Reporter | unknown | 34 | 68% | 29% |
| PSK Reporter | not logged | 56 | 23% | 14% |
