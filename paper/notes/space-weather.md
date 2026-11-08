# Space weather, the ionosphere and Mailcast reception

Working note for the paper, 7 November 2026. Data in data/space-weather (with its README), analysis in paper/figures/space_weather.py, charts in paper/figures/plots/space-weather-*.png and .svg. Every number below comes from that script; it prints all the tables, including the longer ones only summarised here.

## Summary

- **foF2 at the slot is the one ionospheric measure that clearly goes with reception.** When the F2 critical frequency was below about 7 MHz, so near-vertical 7.05 MHz could pass straight through, receivers heard roughly a third as much as when it was above 8 MHz. Survey web SDRs, 6 to 20 October: 13% of frames heard with foF2 under 6 MHz (40 receiver-slots), 34% at 6 to 7 MHz (227), 48 to 53% above 7 MHz (560). Listeners on their own equipment showed the same shape (17%, 22%, 32 to 37%). Above about 8 MHz it levels off. Tone SNR rises with foF2 in the same way.
- **It holds within the same hour of day**, so it is not just the daily cycle: at every hour from 09 to 16 UTC, slots with foF2 under 7 MHz did worse (5 to 37% heard) than slots with foF2 of 8 MHz or more (42 to 50%).
- **Distance matters to it.** For receivers within about 250 km the per-slot rank correlation with foF2 is moderate (Spearman +0.3 to +0.5 for six of ten, each n of 100 or so). For the five receivers 500 to 850 km away it is about zero (-0.25 to +0.08). That fits the physics: a longer, more oblique path is supported by a lower foF2.
- **Daily geomagnetic and solar indices show nothing measurable.** Daily share against Ap: Spearman +0.07 (95% interval -0.46 to +0.57, 15 survey days) and -0.06 (-0.46 to +0.36, 23 listener days). Kp, F10.7 and sunspot number are no better or are inconsistent between the two sets. With 15 to 23 days and only one sizeable storm, a modest effect could easily hide in that noise. Daily median foF2 does correlate (+0.79, n=14, and +0.47, n=22).
- **The one big storm (Kp 7.3 on 10 October, from 15 UTC) left a one-day dip**: the survey's four strong SDRs heard 75% on the 10th, 48% on the 11th and 93% on the 12th. Four M-class flares fell inside slots (up to M6.4 on 10 October at 15:10); none of those slots did worse than the same receivers' other slots that day. One storm and four flares are anecdotes, not a result.
- **The November drop does not follow the space weather indices or foF2.** (Its cause is not settled: see the paper, section 6.3, and november-drop.md.) From the 11:00 slot on 2 November, the first with 31 s bursts, receivers heard about a third of the frames their late-October rates predict. That holds matched receiver by receiver, hour by hour and foF2 bin by foF2 bin. On WN3, whose coding did not change, it was 0.25 to 0.30 of expected; on WN4, now on the long interleaver, 0.36 to 0.42. Over 3 to 7 November the ionosphere was if anything better than in late October (daily median foF2 at slot times 7.8 to 9.7 MHz, Kp mostly 3 or less). The measured tone was as strong as before (per-hour SNR change -3 to +3 dB, four of five receivers up). At a tone SNR of 9 dB or more, listeners heard 65% of frames before and 26% after.
- **A storm came first, and it did hurt.** From 15:00 on 31 October to the end of 1 November, reception was poor everywhere and the tone was often missing (found in a third to three quarters of slots, against 65 to 94% before). That is what a path problem looks like. A G1 storm began that evening and pushed foF2 below 7 MHz at every slot on 1 November. Reception came back with the ionosphere: on the morning of 2 November, still on 18 s bursts, GM4CXF heard 62% and 53% (its October level is 69% on WN4 and 83% on WN3). From 11:00 it fell to 5 to 21% for the rest of the day, with its tone at 10 to 15 dB. The lasting drop begins with the change in burst length on both waveforms, not with an ionospheric change that these indices show; the receivers' own Doppler readings did rise (paper, section 6.3). The head end and modem update at 14:15 on 31 October is unlikely to be the cause: GM4CXF was back to normal on the morning of 2 November, by which time the station ran the same or later software. Nor are the receiver upgrades most listeners installed on 1 November: GM4CXF dropped on 31 October on an unchanged receiver, and M0NVK was as low on 3 November on its old 0.17.1 receiver as on 0.33.1 afterwards.
- **Measured virtual height barely follows the ionosonde.** It is a little higher when foF2 is close to 7 MHz: median 284 km at 6 to 7 MHz against 263 km at 9 to 10 MHz, rank correlation -0.13 (n=430). That is the direction expected from group delay near the critical frequency, but weak. It shows no relation to an estimated peak height (+0.08, n=340). Measured hmF2 could not be fetched.

## Method

### Reception

Definitions are those of paper/figures/results.py. A share is frames heard over frames sent, over the slots a receiver was listening to. Frames sent per slot come from the head end journal's "done" or "ABORTED" line. Slots that sent nothing are left out. A listener's report-days with an AUD error and no slot heard are left out. A listener's days on a public web SDR (wessex.zapto.org, nope.tunnel.ubersdr.org) are kept apart as "CALL via SDR". The survey uses `listening = yes` rows only.

One source is added that results.py does not use: **M9PSY all along**. The original CT 150 receiver (end-fed wire at Dalgety Bay, 533 km) kept running after the survey ended. From 21 October it is the only receiver in data/web-sdr-survey-2026-10/receiver-journal.txt, so its "slot ended" lines give frames heard and tone SNR per slot without matching process IDs. Its survey rows cover 6 to 20 October. It is one receiver on one fixed antenna through the whole period, run by us, which makes it the cleanest before-and-after comparison there is.

Reception sets used:
- The survey's eight web SDRs, 6 to 20 October (888 receiver-slots), and its four strong ones (WESSEX, STUEY3D, EI4HQ, M9PSY) for daily figures.
- Listeners on their own equipment: GM4CXF, M0NVK, G8BPQ, GM8BPQ and G7TAJ for daily figures, all listeners for slot-level figures.
- M9PSY on CT 150, 21 October to 7 November.

### Periods

- **A**: to the 14:00 slot on 31 October. That is before the head end and modem update at 14:15 and before the storm.
- **B**: 31 October 15:00 to 2 November 10:00. It covers the storm, its low-foF2 day, and the last two slots with 18 s bursts.
- **C**: from the 11:00 slot on 2 November. Bursts were up to 31 s on both waveforms from 10:15, WN4 used the long interleaver from 11:00, and bulk senders went last from 13:00. The brief's "November" is C, apart from the 09:00 and 10:00 slots on 2 November, which belong with B.

The foF2 and daily-index correlations use period A only, so that the later drop cannot pose as an ionospheric effect.

### Space weather

- Kp, ap, Ap, sunspot number and F10.7 come from GFZ Potsdam, preliminary values. The archive file covers to 6 November and the nowcast file covers 7 November.
- M and X flares come from NOAA SWPC's edited event list (GOES XRS), 7 October 23 UTC to 7 November.
- Storm days are days with any 3-hourly Kp of 5 or more: 4 and 5 October, 10 October (Kp 7.3, G3), 31 October (5.7), 1 November (5.0) and 7 November (5.0).

### foF2 at each slot

1. The head end journal's reading logged at the slot start, when it was under 60 minutes old: 195 of 254 slots. These are mostly Dourbes DB049 via GIRO, 20 minutes old.
2. Otherwise Dourbes from PROPquest, the median over the slot's first 10 minutes: 8 slots.
3. Otherwise Fairford FF051 from PROPquest, within 30 minutes before to 40 minutes after the slot start: 41 slots.

Ten slots have none. Where both exist, the journal's Dourbes value and PROPquest's agree (median difference +0.03 MHz, middle half -0.31 to +0.33 MHz, n=191). Fairford reads 0.15 MHz below Dourbes at the same slots (n=199).

Dourbes stopped reporting to PROPquest and GIRO after midday on 1 November. From 2 November the head end's own readings were 3 to 5 hours old, so period C rests almost entirely on Fairford's hourly values (38 of 41 slots).

Dourbes is about 420 km ESE of Reading and Fairford about 65 km WNW. Neither sits under the reflection point of the Scottish or Irish paths.

### Statistics

- Spearman rank correlation, with an approximate 95% interval (Fisher z with the Bonett-Wright standard error).
- Pooled shares get a 95% interval by resampling whole days, because frames within a slot, and slots within a day, are not independent.
- **O/E (observed against expected).** For each later receiver-slot, the frames expected are the frames sent times the same receiver's period-A share in the same stratum. The strata are receiver by foF2 bin, receiver by hour, or receiver by hour by foF2 of 8 MHz or more, always within one waveform family, and only where the stratum had at least 100 period-A frames. O/E of 1 means as good as before at the same conditions. Its interval comes from resampling the later days.
- **Estimated hmF2.** It comes from Dourbes' M(3000)F2 by Shimazaki's formula, with no E-layer correction. It is rough, and it exists only to 1 November.

## Results

### 0. What the Sun and the ionosphere did

Chart: plots/space-weather-context.png. It shows Kp, F10.7, foF2 with each slot's value marked, and daily share for M9PSY, the survey SDRs and the listeners.

- **Geomagnetic activity.** Quiet to unsettled most days. Minor storms on 4 and 5 October (before the hourly slots began). A strong storm on 10 October: Kp 7.3 and 7.0 from 15 to 21 UTC, Ap 52. Minor storms on 31 October to 1 November (Kp 5.7 from 18 UTC, then 5.0) and on the evening of 7 November.
- **Solar flux.** F10.7 rose from 92 to 137 by 18 October, fell to about 89 to 93 from 25 October to 1 November, and rose again to 127 by 7 November.
- **foF2 by week.** Weekly medians of midday foF2 stayed between 7.7 and 8.4 MHz through October, with 9 to 35% of 09 to 17 UTC soundings below 7.05 MHz. The week from 5 November was higher (9.6 MHz, on Fairford). Daylight at Reading fell from 11.5 to 9.5 hours and the noon solar zenith angle rose from 55 to 67 degrees. There was no seasonal fall in midday foF2 over the period.

### 1. foF2 against share heard and tone SNR (receiver-slots)

Chart: plots/space-weather-fof2.png.

| foF2 (MHz) | Survey SDRs, 6 to 20 Oct: receiver-slots | share | Listeners, period A: receiver-slots | share | Listeners, period C: receiver-slots | share |
|---|---|---|---|---|---|---|
| < 6 | 40 | 13% | 13 | 17% | 3 | 5% |
| 6 to 7 | 227 | 34% | 98 | 22% | 6 | 13% |
| 7 to 8 | 247 | 48% | 271 | 32% | 41 | 13% |
| 8 to 9 | 257 | 51% | 207 | 35% | 36 | 18% |
| 9 to 10 | 56 | 53% | 37 | 37% | 21 | 19% |
| >= 10 | - | - | - | - | 8 | 21% |

95% intervals, resampling days: survey 9 to 15%, 27 to 43%, 41 to 54%, 48 to 54%, 48 to 57%. Listeners in period A: 12 to 22%, 14 to 33%, 26 to 38%, 28 to 41%, 24 to 47%. The survey's median tone SNR goes 2, 7, 9, 10 and 12 dB up the same bins.

M9PSY on its own: in period A, 52 to 75% at every foF2 above 6 MHz. In period C, 22 to 37% at the same foF2.

**Within each hour, period A, all receivers on their own equipment and survey SDRs:**

| Hour (UTC) | foF2 < 7: share (n) | 7 to 8: share (n) | >= 8: share (n) |
|---|---|---|---|
| 09 | 37% (132) | 44% (45) | 50% (9) |
| 10 | 21% (44) | 45% (99) | 49% (45) |
| 11 | 29% (53) | 39% (26) | 46% (118) |
| 12 | 5% (8) | 39% (60) | 44% (129) |
| 13 | 24% (20) | 40% (94) | 44% (78) |
| 14 | 23% (28) | 44% (97) | 49% (67) |
| 15 | 25% (20) | 35% (83) | 45% (83) |
| 16 | 24% (74) | 38% (56) | 42% (58) |

**Per receiver, period A, Spearman of per-slot share with foF2:**

| Receiver | km | rho [95%] (n) |
|---|---|---|
| G4EYR | 89 | +0.49 [+0.32, +0.63] (111) |
| STUEY3D | 97 | +0.54 [+0.39, +0.67] (111) |
| WESSEX | 135 | +0.32 [+0.14, +0.48] (111) |
| G7TAJ | 142 | +0.47 [+0.30, +0.61] (110) |
| G8BPQ | 172 | +0.39 [+0.24, +0.52] (159) |
| M9TWM-1 | 221 | +0.42 [+0.24, +0.57] (103) |
| M7TAW | 47 | -0.09 [-0.35, +0.18] (54) |
| G4WNC | 83 | -0.02 [-0.28, +0.24] (58) |
| M0NVK | 82 | +0.30 [-0.04, +0.57] (36) |
| M9TWM | 220 | +0.23 [-0.01, +0.44] (72) |
| EI4HQ | 502 | -0.11 [-0.29, +0.08] (111) |
| GM4CXF | 516 | +0.08 [-0.19, +0.34] (54) |
| M9PSY | 533 | +0.01 [-0.13, +0.15] (192) |
| GI3JMC | 544 | -0.16 [-0.34, +0.03] (111) |
| GM8BPQ | 849 | -0.25 [-0.41, -0.08] (129) |

Against foF2 minus the median for that hour (in the same half of the period), the near receivers keep +0.28 to +0.49. So foF2's day-to-day variation matters at a given hour, not only its daily rise and fall. G4WNC (83 km) and M7TAW (47 km) are close in but show no relation; the data do not say why.

### 2. Daily share against Kp, Ap, F10.7, sunspots and storms

Chart: plots/space-weather-daily-ap.png.

| Set (days) | Ap | max Kp | max Kp, previous 36 h | F10.7 | SN | median foF2 at the slots |
|---|---|---|---|---|---|---|
| Survey's 4 strong SDRs, 6 to 20 Oct (15) | +0.07 [-0.46, +0.57] | +0.10 | -0.13 | +0.63 [+0.12, +0.88] | -0.14 | +0.79 [+0.38, +0.94] (14) |
| Listeners, own equipment, 8 to 30 Oct (23) | -0.06 [-0.46, +0.36] | +0.00 | -0.18 | -0.14 [-0.52, +0.29] | -0.31 | +0.47 [+0.04, +0.76] (22) |

These are Spearman rho values for pooled daily share. The relative share, which is each receiver's share that day over its own overall share and is less sensitive to who reported, gives the same picture.

F10.7's +0.63 on the survey set does not carry over to the listeners (-0.14). It is most likely the survey's own trend: its first days were its worst while it was being set up, and F10.7 happened to rise over the same fortnight. Including the days to 7 November leaves every index between -0.29 and +0.14.

**The 10 October storm.** Kp reached 7.3 between 15 and 21 UTC.

| Day | max Kp | survey's 4 SDRs | listeners |
|---|---|---|---|
| 9 Oct | 3.7 | 80% | 27% |
| 10 Oct | 7.3 | 75% | 12% (7 receiver-slots) |
| 11 Oct | 4.0 | 48% | 14% |
| 12 Oct | 3.0 | 93% | 26% |

The day after the storm is the survey's second-worst day, with median foF2 at its slots 7.1 MHz against 7.7 to 8.6 MHz on the days around it. That is the expected negative phase of an F-region storm, but it is one storm.

**Flares during slots.**

| Flare | Peak (UTC) | Share in that slot | Same receivers, same day, other slots |
|---|---|---|---|
| M1.4 | 9 Oct 14:09 | 53% (11 receiver-slots) | 40% |
| M1.2 | 10 Oct 14:12 | 42% (8) | 39% |
| M6.4 | 10 Oct 15:10 | 46% (9) | 39% |
| M1.6 | 19 Oct 09:03 | 40% (10) | 40% |

There is no sign of a short-wave fade. At 7 MHz, an M1 flare's extra D-region absorption may simply be too small to matter on these links.

### 3. The November drop

Charts: plots/space-weather-context.png (bottom panel) and plots/space-weather-november.png.

**Per receiver.** Share of frames heard; receiver-slots in brackets.

| Receiver | km | A: all | A: WN4 / WN3 | B: all | C: all | C: WN4-long / WN3 | tone SNR median, A to C (dB) |
|---|---|---|---|---|---|---|---|
| GM4CXF | 516 | 74% (54) | 69% / 83% | 30% (12) | 19% (33) | 18% / 21% | 10 to 13 |
| M9PSY | 533 | 67% (200) | 65% / 72% | 25% (9) | 27% (28) | 31% / 22% | 11 to 11 |
| M0NVK | 82 | 67% (44) | 65% / 70% | - | 37% (27) | 41% / 29% | 19 to 22 |
| G7TAJ | 142 | 16% (110) | 15% / 17% | 3% (12) | 4% (13) | 2% / 8% | 8 to 8 |
| GM8BPQ | 849 | 19% (129) | 13% / 29% | 2% (12) | 2% (39) | 1% / 2% | 4 to 2 |

results.py reports GM4CXF on WN3 as 73% to 22%. That is the same thing with October counted to 1 November and November from 2 November.

**Observed against expected at matched conditions** (from the script's O/E table):

| Later period and waveform | Stratum | Matched receiver-slots | O/E | 95% interval |
|---|---|---|---|---|
| C, WN3 (coding unchanged, bursts 31 s) | receiver x foF2 bin | 66 | 0.27 | 0.20 to 0.35 |
| C, WN3 | receiver x hour | 68 | 0.29 | 0.22 to 0.38 |
| C, WN3 | receiver x hour x foF2 >= 8 | 55 | 0.30 | 0.25 to 0.36 |
| C, WN4-long against A's WN4 | receiver x foF2 bin | 64 | 0.36 | 0.31 to 0.41 |
| C, WN4-long against A's WN4 | receiver x hour | 72 | 0.42 | 0.35 to 0.50 |
| C, all | receiver x foF2 bin | 130 | 0.32 | 0.27 to 0.38 |
| B, same waveforms and bursts as A | receiver x foF2 bin | 45 | 0.36 | (3 days only) |

Using only 20 to 31 October as the baseline, closest in season, gives the same: 0.25 to 0.42.

**Tone SNR at matched hours.** Median of the per-hour differences from period A:

| Receiver | B (dB) | C (dB) | Tone detected: A, B, C |
|---|---|---|---|
| GM4CXF | -0.5 | +3.0 | 94%, 75%, 76% |
| M9PSY | -1.7 | +0.8 | 94%, 56%, 79% |
| M0NVK | - | +3.0 | 84%, -, 85% |
| G7TAJ | -3.0 | +1.8 | 65%, 33%, 69% |
| GM8BPQ | -3.0 | -3.0 | 83%, 33%, 56% |

In B the tone was often missing and a little weaker: the path was poor. In C it was found less often at two receivers, but when found it was as strong as in October or stronger, except at GM8BPQ, the longest path at 849 km.

**Share at the same tone SNR**, listeners on their own equipment:

| Tone SNR (dB) | A: share (receiver-slots) | C: share (receiver-slots) |
|---|---|---|
| < 0.5 | 14% (86) | 0% (6) |
| 0.5 to 3 | 22% (64) | 1% (6) |
| 3 to 6 | 21% (108) | 3% (7) |
| 6 to 9 | 35% (130) | 6% (10) |
| >= 9 | 65% (149) | 26% (50) |

On WN3 alone, at 9 dB or more: 71% in A, 24% in C.

**Slot by slot.** Pooled over the receivers listening out of GM4CXF, GM8BPQ, G7TAJ, G8BPQ, M0NVK and M9PSY:

| Slot | Waveform | Share | Tone SNR median (dB) | foF2 (MHz) | Kp | What happened before it |
|---|---|---|---|---|---|---|
| 31 Oct 12:00 | WN3 | 60% | 11 | 8.1 | 2.7 | |
| 31 Oct 13:00 | WN4 | 42% | 6 | 7.9 | 2.7 | head end 0.27.0 at 12:45 |
| 31 Oct 14:00 | WN3 | 55% | 9 | 8.3 | 2.7 | |
| 31 Oct 15:00 | WN4 | 8% | 3 | 7.5 | 3.7 | head end 0.28.1 at 14:15 |
| 31 Oct 16:00 | WN3 | 16% | 3 | 6.3 | 3.7 | |
| 1 Nov 09:00 | WN3 | 2% | - | 5.2 | 3.0 | head end 0.29.0; storm, Kp 5.7 from 18 UTC |
| 1 Nov 10:00 | WN4 | 0% | - | 5.6 | 3.0 | |
| 1 Nov 11:00 to 16:00 | both | 4 to 25% | 6 to 17 | 5.6 to 6.7 | 2.0 to 3.0 | |
| 2 Nov 09:00 | WN4 | 21% | 6 | 6.2 | 2.0 | head end 0.30.0 at 23:10 on 1 Nov |
| 2 Nov 10:00 | WN3 | 31% | 6 | 7.0 | 2.0 | |
| 2 Nov 11:00 to 16:00 | WN4-long, WN3 | 5 to 16% | 5 to 10 | 5.2 to 7.6 | 1.3 to 2.0 | 31 s bursts from 10:15; long interleaver; bulk tier from 13:00 |
| 3 Nov 10:00 to 16:00 | WN4-long, WN3 | 3 to 21% | 10 to 13 | 7.0 to 8.3 | 0.7 to 1.3 | |

**The two 18 s slots after the storm**, each receiver against its own period-A share on that waveform:

| Receiver | 2 Nov 09:00, WN4 | A on WN4 | 2 Nov 10:00, WN3 | A on WN3 |
|---|---|---|---|---|
| GM4CXF | 62% | 69% | 53% | 83% |
| G7TAJ | 0% | 15% | 25% | 17% |
| GM8BPQ | 1% | 13% | 14% | 29% |

M9PSY's CT 150 journal has no lines for those two slots.

Day by day, GM4CXF went 80% and 69% on 30 and 31 October, then 24%, 23%, 15%, 20%, 21% and 32%. M9PSY went 79% and 67%, then 27%, 19%, 24%, 24%, 35%, 40% and 33%. Both climbed a little from 4 November but stayed well below their late-October level.

**Reading it.** There are four candidate explanations.

- **The ionosphere and the storm.** These explain period B well. The tone was weaker or missing at the first two slots after 15:00 on 31 October, as Kp rose ahead of the storm. On 1 November foF2 was under 7.05 MHz at every slot (5.2 to 6.7 MHz), and the tone was often not found. That is the storm's negative phase. They do not explain period C. From 3 November, daily median foF2 was 7.8 to 9.7 MHz, Kp was mostly 3 or less, and the tone was normal, yet receivers heard only about a third of the expected frames. The 10:00 WN3 slot on 2 November, on foF2 7.0 MHz and still with 18 s bursts, was close to normal for GM4CXF and G7TAJ.
- **Season** (later sunrise, earlier sunset, a larger solar zenith angle). The hours are matched in the O/E and tone comparisons, and midday foF2 did not fall between late October and early November. Season cannot make a step within one day.
- **The software update at 14:15 on 31 October.** The head end restarted on 0.28.1; the operator's notes also put pdn-soundmodem 0.98.1 to 0.99.0 in the same restart. It does coincide with the first bad slot. But the tone fell in that slot too, so the path was already worse. And GM4CXF was back to its normal level on the morning of 2 November, by which time the station was running the same or later software. On this evidence it is a coincidence.
- **The 2 November changes.** The lasting drop starts with the first slot after bursts went from 18 s to 31 s, at 10:15 on 2 November. It is as deep on WN3, which kept the short interleaver, as on WN4, which moved to the long one. Burst length is the change the two waveforms share. The tone is unaffected, so the signal reached the receivers as well as before: whatever went wrong happened in turning that signal into frames. This note cannot say what that was. The paper (section 6.3) leaves the cause open between the 31 s bursts and a channel the receivers measured as more Doppler-spread, found by re-decoding the recorded slots, and that is consistent with everything here. The bulk tier changes what is sent, not how, and cannot lower a share heard.

**What would settle it.** The head end went back to 18 s bursts and the short interleaver at 23:14 UTC on 7 November. If shares return to the period-A level at matched foF2 and tone SNR, burst length is confirmed. Re-running this script with the reports from 8 November onwards will show it.

### 4. Measured virtual height against the ionosonde

Chart: plots/space-weather-vheight.png.

There are 436 receiver-slots with a virtual height, 364 of them from the probe. The median is about 265 km.

| foF2 (MHz) | Receiver-slots | Median virtual height (km) | Quartiles (km) | Median estimated hmF2 (km) |
|---|---|---|---|---|
| 6 to 7 | 45 | 284 | 240 to 321 | 251 |
| 7 to 8 | 180 | 271 | 252 to 294 | 259 |
| 8 to 9 | 153 | 264 | 244 to 279 | 252 |
| 9 to 10 | 40 | 263 | 251 to 283 | 257 |

Spearman of virtual height:

- With foF2: -0.13 [-0.22, -0.03] (n=430).
- With foF2/7.05, only where foF2 is above 7.05 MHz: -0.12 (n=374).
- With estimated hmF2: +0.08 [-0.03, +0.19] (n=340).
- With receiver distance: -0.12 (n=436).

Per receiver, it ranges from -0.45 (M0NVK, 82 km, n=52) to +0.33 (G7TAJ, n=29).

The direction is the physical one. As 7.05 MHz approaches foF2, the wave slows near the reflection level and the measured (group) height rises. The effect is weak, though, and the scatter (quartiles roughly 245 to 295 km) is larger than the trend. The estimated hmF2 is of the same size as the measured heights (250 to 265 km), but the two do not track each other slot by slot. With no E-layer correction and an ionosonde 420 km away, that estimate is too rough to test against. Measured hmF2 and h'F from GIRO would be the proper comparison.

## Limitations

- **Small samples.** There are 15 survey days, 23 listener days before 31 October, one sizeable storm, four flares in slots, and parts of 3 days in period B. Period C before the 7 November revert is 6 days. The day-level intervals are wide. Absence of a Kp or Ap effect means "not detectable here", not "none".
- **Dependence.** Receiver-slots on the same day share an ionosphere, and frames in a slot share a burst schedule. The intervals use whole-day resampling, but the per-receiver Spearman intervals treat slots as independent and are too narrow.
- **The ionosonde is not at the reflection point.** Dourbes is 420 km from Reading and Fairford 65 km. Paths to Scotland and Ireland reflect 250 to 400 km north or west of either.
- **The ionosonde record is patchy.** The journal's readings are 20 to 60 minutes old and sometimes hours old. Dourbes went silent on 1 November, so period C's foF2 is Fairford's hourly value. GIRO was down when this was fetched, so no measured hmF2 or h'F is available. PROPquest's Dourbes series has a few single-sample spikes, which were not filtered.
- **foF2 is entangled with time.** It rises and falls through each day and drifted over the period, and the survey's worst-foF2 days were also its first days. The within-hour and hour-anomaly checks reduce this but do not remove it.
- **Different receivers, different SNR.** The survey's tone SNR and the listeners' come from different receivers and antennas, so compare shapes, not levels.
- **Tone SNR stands in for the path.** The conclusion about November rests on it. The tone is a separate 10 s transmission at the start of the slot. It shows that the signal arrived as strongly as before. It cannot tell a decoding problem from a change in the data waveform as sent, or in its drive level relative to the tone.
- **Several things changed within 48 hours.** Station software (31 October and 1 to 2 November), listeners' receiver versions (mostly on 1 November), a storm, and on 2 November the burst length, WN4's interleaver and the bulk tier. Separating the storm from the burst change rests on a few slots: the two 18 s slots on the morning of 2 November, mainly GM4CXF's. Ruling out the receiver upgrades rests on two cases: GM4CXF on an unchanged receiver on 31 October, and M0NVK on its old receiver on 3 November. The long interleaver and the burst length changed together for WN4, so this data cannot judge the interleaver on its own.
- **One fact comes from outside this repository.** The pdn-soundmodem versions come from the operator's notes; the journal shows only the head end's own versions.
- **Shares are pooled over frames.** Large slots weigh more. The two waveforms carry different numbers of frames, and that changed on 2 November (WN3 from 73 to 75 frames up to 85 to 87).
