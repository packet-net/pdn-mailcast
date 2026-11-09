# After the paper's data: running notes

Observations after 7 November 2026, outside the paper's main data. The 9 November reception and burst figures are quoted in section 6.3; the rest is kept for reference. Each is as reported by the reception recovery session unless marked otherwise. Its 8 November column was computed on the day as per-slot medians and differs by a point or so from the totals in `november-drop.md` (95, 94, 56, 17%), which the paper uses.

## 9 November (day 2 after the reference fix)

Web SDR survey, share of frames heard (median tone): 8 Nov 11:00 to 15:00 / 9 Nov daylight / 9 Nov evening (first evening slots, 17:00 to 21:00 UTC, WN3):

| SDR | km | 8 Nov after fix | 9 Nov daylight | 9 Nov evening |
|---|---|---|---|---|
| G4EYR | 89 | 16% (+7 dB) | 36% (+6) | 0% |
| STUEY3D | 97 | 93% (+16) | 97% (+18) | 0% |
| WESSEX | 135 | 94% (+24) | 99% (+26) | 0% |
| M9TWM-1 | 221 | 79% (+12) | 71% (+9) | not listening |
| EI4HQ | 502 | 93% (+20) | 97% (+15) | 93% (+16) |
| M9PSY | 534 | 55% (+7) | 62% (+6) | 20% (+18) |
| GI3JMC | 544 | 0% | 0% | 0% |

- Evening slots reach Ireland well (EI4HQ 64 of 72 frames in the first); M9PSY hears a strong evening tone but decodes little, cause unknown; near UK SDRs hear nothing after dark.
- MS110D shadow (CT 153, M9PSY feed), 9 Nov daylight: bursts ending normally 73%, 100%, 62%, 100%, 75%, 96% by slot (fault period 3 to 7 Nov: 21 to 35% a day; late October 87 to 97%), mean tone +3.5 to +9.7 dB. 17:00 evening slot: 89%.
- Sweden (SA6DAZ), 9 Nov daylight: 17 to 49 frames a slot at tone -1 to +10 dB; 17:00 evening slot 12 of 72 at +8 dB, Doppler spread 0.2 to 0.3 Hz. Its pdn-soundmodem had about one audio capture overrun a minute (317 that day), each dropping the burst in progress; CPU governor set to performance and pdn-soundmodem 0.101.0 to 0.105.0 at 18:22 to 18:30 UTC.
- Daily reports: receivers 0.34 and later send theirs after the last evening slot, so 9 Nov reports arrive on 10 Nov.

Station:
- GB7RDG's Flex ran on its own TCXO from 11:20 to 12:30 UTC on 9 Nov after the GPSDO was unplugged (the Flex 6500 only adopts an external 10 MHz at power-up, and the cable had been left off); power-cycled back onto the GPSDO at 12:30.
- The TCXO's stored calibration (-1390 ppb) is right to about 65 ppb: M9PSY (GPSDO-locked) measured the tone at a median -0.45 Hz over 86 TCXO slots in October, against 0.00 Hz on the GPSDO.
- pdn-soundmodem 0.105.0 on GB7RDG: a lost radio now always restarts the modem, and its reference verdict follows which oscillator the Flex is using.
- Evening slots (v0.34.0) from 8 Nov.
