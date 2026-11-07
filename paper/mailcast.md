# Mailcast: one-way HF delivery of packet BBS bulletins with fountain codes

Tom Fanning, M0LTE

*Draft, 7 November 2026. Figures come from the data in this repository and are reproduced by `paper/figures/results.py` and `paper/figures/heard_share.py`. Listener callsigns are as they appear in public bulletins; consent to name them is still to be asked.*

## Abstract

Packet bulletin boards in the UK pass bulletins between each other over slow, connected-mode radio links, and each link carries each bulletin again. Mailcast sends them one way instead. One station transmits on 40 m in daylight, using a MIL-STD-188-110D waveform at 600 or 1200 bps. Each bulletin is compressed, then cut into pieces with a RaptorQ fountain code, so a listener rebuilds it from any set of pieces slightly larger than the bulletin, heard in any slots. A small receiver program hands each rebuilt bulletin to the listener's own BBS as if it came from a forwarding partner. No reply is ever sent on air.

Between 5 October and 7 November 2026 the station ran 259 hourly slots and carried 465 bulletins. A two-week survey of eight public web SDRs, from 83 to 544 km away, and 124 daily reports from eight listeners show what reached people. Receivers with a good path heard 60 to 80% of the frames sent and rebuilt nearly every bulletin; weak ones heard 15 to 25% and still rebuilt many, because pieces from different hours add up. The measured channel is a typical 40 m NVIS path: a reflection about 270 km up, usually a second mode 1.6 ms later, and Doppler spread around a third of a hertz. We describe the design, the results, and Model B, in which several stations transmit and their pieces combine at the listener.

## 1. Introduction

A packet BBS in the UK gets its bulletins from forwarding partners: other BBSes that connect, offer what they have, and send what is wanted. On HF that means connected-mode AX.25 or similar over a channel that fades, with retries, and each pair of BBSes moves each bulletin again. A bulletin's routing lines tell the story. One of the reports in this study went from GB7NOT through GB7BWR and GB7BEX to GB7RDG in three minutes; others took days to get through, and some never arrived because a forwarding partner along the way wasn't set up.

A transmission one way, to anyone listening, avoids all of that. One transmission serves every listener at once, the sender never needs to know who is there, and nothing is ever sent back. The cost is that the sender cannot ask what was missed, so the coding has to make missing things not matter.

We wanted:

- No change to a listener's BBS. A bulletin should arrive the way bulletins always arrive, by forwarding, and the BBS's own duplicate check by BID should throw away anything it already has.
- A receiver that runs on a Raspberry Pi with a radio and a sound card, or on no radio at all, listening through a public web SDR.
- Nothing that disturbs the existing 40 m packet channels.

The system is called mailcast. The transmitting side is a *head end*, here running at GB7RDG near Reading and transmitting as M0LTE; the listening side is the *receiver*. Both are open source (AGPL-3.0) and packaged for Debian and Raspberry Pi OS.

## 2. Related work

*To be completed with a literature search.* Points to cover:

- Fountain codes for broadcast file delivery: LT and Raptor codes, RaptorQ (RFC 6330) and its use in DVB and 3GPP MBMS file delivery and in data carousels.
- One-way amateur systems: WSPR and FT8 (one way, weak signal, but tiny fixed messages), APRS (unacknowledged packets), and any HF bulletin broadcast systems.
- Connected HF mail in amateur radio: Winlink with PACTOR, ARDOP and VARA; BBS forwarding over HF packet.
- MIL-STD-188-110D and STANAG 5066 in military and maritime HF data.

## 3. Design

### 3.1 The signal

The head end transmits MIL-STD-188-110D Appendix D serial-tone waveforms in a 3 kHz channel centred on 7.0538 MHz. Listeners tune 7.052 MHz USB, which puts the signal at 1800 Hz in their audio. The signal occupies about 7.0522 to 7.0554 MHz, clear of the UK HF packet channels at 7.0503, 7.05095 and 7.0516 MHz.

Two waveforms are used, slot by slot in turn: WN4 (BPSK at 2400 baud, rate 2/3 coding, 1200 bps) and WN3 (rate 1/3, 600 bps). Since 2 November WN4 has used the long interleaver. The receiver reads each burst's waveform from its preamble, so a listener needs no setting for it. The modem is pdn-soundmodem, an open-source software modem, which carries IL2P frames with a CRC inside the 110D bit stream. A frame either arrives intact or is thrown away, which is exactly the kind of loss a fountain code handles best.

The station is a Flex 6500 at about 40 W. Each frame is an AX.25 UI frame from M0LTE, and each slot opens and closes with a CW ident.

### 3.2 The timetable

40 m NVIS only carries UK paths in daylight. On 5 October slots at 07:12 and 18:00 UTC were heard by none of nine UK and Irish web SDRs, while those from 09:12 to 16:00 decoded well. So the head end works out sunrise and sunset at Reading itself, with no internet needed, and transmits every hour on the hour from 2 hours after sunrise to 30 minutes before sunset: 09:00 to 16:00 UTC in October, 11:00 to 15:00 in December.

A slot lasts at most 10 minutes. The head end first listens for a clear channel, for up to 2 minutes, then sends a 10 s calibration tone, a sounding probe for the channel measurement, and its frames in bursts of up to 18 s. Each burst is its own transmission with its own preamble. The timetable is sent in every slot, so receivers follow it without being told.

### 3.3 Framing and coding

Each bulletin is compressed with zstd using a 64 KB dictionary trained on earlier UK bulletins, which brings bulletins to about 30% of their size. The result is cut into 240-byte symbols and coded with RaptorQ (RFC 6330), one source block per bulletin. A RaptorQ encoder can make as many different symbols as it likes, and any K of them, plus a couple more, rebuild a bulletin that is K symbols long. A median bulletin in this study was 5.3 kB of text, about six symbols once compressed.

Each frame carries one symbol, 271 bytes in all: a version byte, flags, an 8-byte object ID, the dictionary ID, the RaptorQ transmission information, the symbol's number, the symbol and a CRC-32. The object ID is the first 8 bytes of a SHA-256 hash of the bulletin, so every object checks itself.

Every slot also carries a *directory*: the bulletins in rotation, the timetable and the slot's waveform. Since 15 October directories are signed with Ed25519, and a receiver holds back anything that only an unsigned or unverified directory lists.

### 3.4 How much is sent, and for how long

Each slot is filled to about 7.8 minutes on the air while there is anything to send, least-covered bulletins first. Every bulletin gets fresh symbols in every slot until 6 K symbols have gone out or 36 hours have passed: enough that any three slots rebuild it. After that a bulletin is carried only in airtime nothing else wants, up to 10 K or 48 hours. No symbol number is ever sent twice, so everything a listener hears is new to them.

Over the whole period the head end sent 25,397 frames, about 6.1 MB of symbols, for 465 bulletins totalling 3.5 MB of text. Compressed, those bulletins come to roughly 1 MB, so with the directories and readings each bulletin was sent about six times over, which is the design's target.

### 3.5 Propagation readings

Each slot also carries two small readings: whether 40 m is open from Reading at 100, 500 and 1000 km by the latest ionosonde sounding (Fairford, Chilton or Dourbes), and the same judged from the last 30 minutes of FT8, FT4 and WSPR spots between UK and Irish stations on PSK Reporter. They are observations only, and never change what or when the head end sends. Section 5.6 asks how well they predicted reception.

### 3.6 The receiver

The receiver is one program. It takes audio from a sound card, from SDRconnect, or from a public UberSDR web receiver, and runs the MS110D modem itself. When it has enough symbols of a bulletin it rebuilds it, checks it against its ID and the signed directory, and logs in to the listener's LinBPQ, FBB or XRouter BBS as forwarding partner Q0CAST to offer it. The BBS keeps or refuses it by BID like any partner. Symbols are kept on disk for 14 days, so pieces heard in different slots and on different days add up.

A listener with one radio can share it with their packet node. The receiver turns the node's transmitter off on that port, retunes the rig through Hamlib for each slot, and puts everything back afterwards. A receiver on a web SDR listens to each slot for about 14 minutes and stays inside the 3 hours a day that those SDRs allow.

If the listener turns it on, the receiver sends a short public daily report: one line per slot with the frames decoded, the tone's signal to noise, the propagation readings it heard, and the channel measurement. The reports go as bulletins to `MCAST@GB7RDG`, and most of section 5 is built from them.

## 4. Data

Three sources, all in this repository:

- **The head end's journal**, from the first slot on 5 October to 7 November: what each slot planned and sent, on which waveform, for how long, and what was taken in from the BBS (`data/headend-gb7rdg`).
- **A web SDR survey**, 6 to 20 October. Eight receivers on one machine, each listening through a different public UberSDR, from 83 to 544 km away, logged every slot (`data/web-sdr-survey-2026-10`). Two of the SDRs were unreachable for some of the time; those slots are left out.
- **Listeners' daily reports**, 124 reports from eight listeners, 8 October to 7 November (`reports/`). Most were copied from GB7RDG; some never reached it and were pasted from other BBSes. Reports from 8 to 18 October are incomplete, because GB7RDG then kept bulletins for only a week and the archive was started later.

Throughout, a receiver's *share* is the frames it decoded over the frames sent in the slots it was listening to. A slot cut short counts the frames it did send.

## 5. Results

### 5.1 The transmitter

From 5 October to 7 November the head end ran 259 slots over 33 days: 252 completed, 6 cut short and 1 skipped. A completed slot was on the air for a median 7.6 minutes, 29.6 hours in all. It sent 25,397 frames in 5,182 bursts: 13,500 on WN4, 2,544 on WN4 with the long interleaver, and 9,320 on WN3.

The channel was often in use. In 150 of 258 slots the head end heard the channel busy when the slot began and waited for it to clear. Four afternoon slots on 22 October went out empty because the modem stopped acknowledging frames; two more were cut short, one by a restart of the head end and one by a dropped connection to the modem.

### 5.2 Who heard it: the web SDR survey

| SDR | Distance (km) | Slots listened | Slots with frames | Frames heard | Median tone SNR (dB) | Bulletins rebuilt |
|---|---|---|---|---|---|---|
| G4WNC | 83 | 58 | 9% | 1% | -8 | 1 |
| G4EYR | 89 | 119 | 50% | 13% | 0 | 80 |
| STUEY3D | 97 | 119 | 87% | 71% | 14 | 198 |
| WESSEX | 135 | 119 | 88% | 80% | 24 | 200 |
| M9TWM-1 | 221 | 111 | 67% | 24% | 3 | 131 |
| EI4HQ | 502 | 119 | 100% | 79% | 13 | 223 |
| M9PSY | 534 | 119 | 96% | 69% | 11 | 220 |
| GI3JMC | 544 | 119 | 1% | 0% | -10 | 0 |

*Table 1. Web SDR survey, 6 to 20 October. "Bulletins rebuilt" counts each bulletin once, when the receiver first completed it. The head end took in 231 bulletins up to 20 October, the last of them too late to be fully sent before the survey ended.*

Distance alone does not decide reception. Two SDRs over 500 km away, in Ireland and Fife, heard nearly as well as the best one 135 km away, while two within 90 km of Reading heard almost nothing. We cannot separate the path from each SDR's own antenna and local noise with this data, but the tone's signal to noise, measured the same way at every receiver, tracks the share of frames heard far better than distance does.

The four strongest SDRs rebuilt 198 to 223 of the 231 bulletins. Weak receivers rebuilt a lot too. M9TWM-1 decoded only a quarter of the frames sent, but rebuilt 131 bulletins; G4EYR heard 13% and rebuilt 80. This is the fountain code doing its job: a bulletin was spread over several slots and several hours, and a receiver that heard a bit of each often ended up with enough.

The two weeks also differed: the nearer strong SDRs heard much more in the second week (WESSEX 65% then 90%, STUEY3D 51% then 85%), while the distant ones changed little (`paper/figures/heard-share-tables.md`). We have not yet looked for the cause.

### 5.3 Who heard it: listeners' reports

| Listener | Locator | Audio | Days | Slots listened | Slots with frames | Frames heard | Median tone SNR (dB) | Bulletins rebuilt |
|---|---|---|---|---|---|---|---|---|
| M7TAW | IO91ma | radio | 10 | 79 | 82% | 62% | 13 | 159 |
| M0NVK | IO90hr | SDR (SDRconnect) | 12 | 88 | 84% | 58% | 20 | 143 |
| GM4CXF | IO75qn | radio | 12 | 91 | 98% | 48% | 11 | 152 |
| G8BPQ | IO92kx | radio | 23 | 172 | 82% | 23% | 4 | 163 |
| M9TWM | IO93fj | web SDR | 8 | 64 | 88% | 21% | 5 | 41 |
| G7TAJ | IO81le | radio | 20 | 153 | 65% | 18% | 8 | 125 |
| EI7KX | IO83jj | web SDR | 6 | 41 | 88% | 18% | 4 | 43 |
| GM8BPQ | IO68vl | radio | 25 | 184 | 76% | 14% | 4 | 99 |

*Table 2. Listeners' daily reports, 8 October to 7 November. Eight report-days on which a receiver had no audio at all are left out. M7TAW listened through a radio for most days and through the WESSEX web SDR for one.*

The picture matches the survey. Three listeners heard about half the frames or more; the other five heard 14 to 23%. Every one of them heard frames in most slots, from 65% of slots for G7TAJ to 98% for GM4CXF in Ayrshire, so even the weakest receivers heard something of most transmissions. GM8BPQ, on Lewis in the Outer Hebrides about 800 km from Reading, heard frames in three slots out of four.

Listeners' own setups mattered a great deal. Several shared a radio with a packet node, and the reports and the channel discussion show time lost to receive filters narrower than the signal, the wrong rig mode, and audio levels.

### 5.4 Waveforms: 1200 bps against 600 bps

| Receiver | WN4 | WN3 |
|---|---|---|
| WESSEX | 80% | 81% |
| EI4HQ | 76% | 83% |
| STUEY3D | 69% | 75% |
| M9PSY | 66% | 73% |
| M7TAW | 62% | 61% |
| M0NVK | 58% | 57% |
| GM4CXF | 46% | 51% |
| M9TWM | 18% | 26% |
| M9TWM-1 | 21% | 28% |
| G8BPQ | 15% | 36% |
| GM8BPQ | 10% | 21% |
| G7TAJ | 17% | 20% |
| EI7KX | 19% | 16% |

*Table 3. Share of frames heard on WN4 slots and on WN3 slots, for receivers that heard both.*

For strong receivers the two waveforms do about as well as each other, so WN4 delivers twice the data in the same airtime. For weak receivers WN3 roughly doubles the share heard (G8BPQ 15% to 36%, GM8BPQ 10% to 21%), but carries half as much per frame, so the bulletin data delivered per minute is about the same. Alternating the two keeps both kinds of listener served; choosing per slot from the channel measurement is future work.

### 5.5 The channel

Receivers measure the path in each slot from the sounding probe, or failing that from the data bursts. Across 719 measured report slots:

- **Propagation modes.** One mode in 237 slots, two in 333, three in 130, four in 19.
- **Virtual height.** Median 266 km, quartiles 243 to 290 km: an F-layer reflection.
- **Second mode.** Median 1.6 ms after the first (quartiles 1.4 to 1.8 ms), and 12 dB weaker (quartiles 15 to 6 dB weaker). That delay is what a two-hop (2F) path adds over one hop at these heights and ranges.
- **Delay spread.** Median 0.41 ms, quartiles 0.29 to 0.57 ms.
- **Doppler spread.** Median 0.34 Hz, quartiles 0.24 to 0.54 Hz.

Against the mid-latitude test channels of ITU-R F.1487, the two-path delay of 1.6 ms lies between the moderate (1 ms) and disturbed (2 ms) channels, and the Doppler spread between the quiet (0.1 Hz) and moderate (0.5 Hz) ones: a channel the 110D equaliser is designed for, fading slowly. In this data reception looks limited more by signal to noise and interference than by the channel's shape, though we have not tested that directly.

By hour of day, report slots with frames ranged from 73% at 09:00 UTC to 85% at 14:00, and the share of frames heard from 27% to 34%, with no strong pattern across the daylight window.

### 5.6 Did the propagation readings predict reception?

| Source | Verdict | Receiver-slots | With frames | Frames heard |
|---|---|---|---|---|
| Ionosonde | good | 801 | 83% | 44% |
| Ionosonde | marginal | 666 | 63% | 35% |
| Ionosonde | unknown | 232 | 78% | 26% |
| PSK Reporter | good | 1665 | 75% | 38% |
| PSK Reporter | unknown | 34 | 68% | 29% |

*Table 4. The verdict the head end logged as each slot began, against what the survey and report receivers heard in that slot. 56 receiver-slots had no verdict logged.*

The ionosonde verdict separates slots modestly: receivers heard 44% of frames in slots it called good and 35% in those it called marginal. PSK Reporter called 40 m open in essentially every slot. FT8 decodes far below what MS110D needs, so "open for FT8" says little about a 1200 bps link; the median SNR of the spots behind each verdict may predict better, and is left for later analysis.

### 5.7 Interference and failures

The channel at 7.050 to 7.053 MHz is shared. Listeners reported FT8, VARA and broadcast signals over the mailcast frequency, and on 30 October an over-the-horizon radar swept across it, wiping out reception at G7TAJ and in Sweden for that slot. The head end's clear-channel check found the channel busy at the start of 150 slots, and on 22 October the afternoon slots were lost when the modem stopped acknowledging frames. Reports themselves were sometimes lost on their way back: one listener's BBS offered five reports to a neighbouring BBS that had not set it up as a forwarding partner, and every offer was refused.

### 5.8 Beyond the UK

*To be added: MS110D path tests from Reading to SA6DAZ in Sweden on an IC-7300, including 6400 bps QAM16 frames decoded on 30 October.*

## 6. Lessons from operation

**Sharing a radio is the hard part.** Most listeners already run a packet node, and the receiver has to borrow the radio for each slot: stop the node transmitting, retune, and put everything back. Rig modes caught people out (a Xiegu G90 needs its UD mode rather than USB to pass the full 3 kHz), as did receive filters narrower than the signal. The receiver now defaults the rig to PKTUSB, and can measure the listener's filter and choose a dial to suit it.

**Getting reports back is a routing problem.** The daily report travels by ordinary packet mail. Several listeners' reports never reached GB7RDG, because some BBS along the way had not set up the next one as a forwarding partner. Nobody has a full map of who forwards to whom any more.

**Words and callsigns matter.** The UK licence allows messages to be "transmitted by a Beacon or via a Data Station for reception by Amateurs", but "broadcast" is a loaded word in amateur radio, and we say *transmission*. Transmitting under the BBS's callsign was outside its licence, so from 11 October the head end transmits as M0LTE.

**Web SDRs make listening easy.** Anyone can be a listener with no radio at all, through a public UberSDR. The receiver keeps inside each SDR's allowance of 3 hours a day.

## 7. Model B: many stations

With one transmitter, mailcast has one point of failure and one footprint. Model B lets any number of stations transmit, with no link between them, and lets a listener combine what it hears from all of them.

The idea rests on content addressing. If two stations encode the same bulletin to the same bytes, they produce symbols of the same object, and a listener combines them. That needs three things:

1. **The same bulletin becomes the same bytes**, whichever BBS it came through. Forwarding adds routing lines and the odd header, so each station reduces a bulletin to a canonical form first. We measured this across four UK BBSes, several of which receive bulletins by independent routes: of 112 bulletins held by two or more of them, 111 combine once in canonical form (`research/138`). In the odd one out the copies genuinely differed.
2. **Every station encodes identically.** The canonical bulletin, the compression (zstd 1.5.7, dictionary 1) and the RaptorQ coding are all fixed, and test vectors are reproduced independently in Python and Rust. This was confirmed on air on 30 October.
3. **Stations do not send the same symbols.** Each station sends its own scrambled order of symbol numbers, so two stations rarely duplicate each other.

Everything else is built: each station signs its own directory, receivers follow every station they can hear, a receiver rebuilds a bulletin with its own station's routing line, and a second copy of a bulletin already delivered is dropped and counted. A head end can also relay what its local receivers have heard from other stations. What remains is the on-air trial with a second station, John G8BPQ's GM8BPQ on 40 m at half past each hour (with a QO-100 trial to follow), to measure how much combining gains in practice.

## 8. Future work

- Choosing each slot's waveform from the channel measurement, and faster waveforms when the channel allows.
- Using the daily reports, rather than a return channel, to steer how much of each bulletin is sent.
- 80 m in the evenings, and QO-100.
- Personal mail as well as bulletins (under consultation).
- The Model B trial.

## 9. Conclusion

A one-way, fountain-coded transmission delivers packet bulletins to listeners across the UK and Ireland with no change to their BBS and nothing sent back. Receivers with a fair path hear most of what is sent and rebuild everything; weak ones hear a fraction and still rebuild most of it, because pieces from different hours add up. The channel is gentle, and the limits are signal to noise, interference and the practicalities of sharing a radio. Model B, now built, takes the same idea to many transmitters.

## Acknowledgements

Perry M0PYL suggested the fountain code. John G8BPQ, Steve G7TAJ, Eoin M0NVK, Anthony M7TAW, Jim GM4CXF and the other listeners on the OARC packet channels ran receivers and sent reports. The web SDR operators made the survey possible.

## Appendices (to write)

- A. On-air format, summarised from `docs/design.md`, with the test vectors.
- B. The daily report format (MCR1).
- C. Data and code availability.
