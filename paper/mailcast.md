# Mailcast: one-way HF delivery of packet BBS bulletins with fountain codes

Tom Fanning, M0LTE

*Draft, 7 November 2026. Figures come from the data in this repository and are reproduced by `paper/figures/results.py` and `paper/figures/heard_share.py`. Listener callsigns are as they appear in public bulletins; consent to name them is still to be asked.*

## Abstract

Packet bulletin boards in the UK pass bulletins between each other over slow, connected-mode radio links, and each link carries each bulletin again. Mailcast sends them one way instead. One station transmits on 40 m in daylight, using a MIL-STD-188-110D waveform at 600 or 1200 bps. Each bulletin is compressed, then cut into pieces with a RaptorQ fountain code, so a listener rebuilds it from any set of pieces slightly larger than the bulletin, heard in any slots. A small receiver program hands each rebuilt bulletin to the listener's own BBS as if it came from a forwarding partner. No reply is ever sent on air.

After a day of tests on 5 October, the station ran 259 hourly daylight slots from 6 October to 7 November 2026 and carried 465 bulletins. A two-week survey of eight public web SDRs, from 83 to 544 km away, and 129 daily reports from nine listeners show what reached people. Receivers with a good path heard 60 to 80% of the frames sent and rebuilt 88 to 99% of the bulletins; weak ones heard 13 to 25% and still rebuilt a third to over half of them, because pieces from different hours add up. Reception fell sharply in the first week of November, on both waveforms. A Zadoff-Chu sounding probe at the start of each slot measures the path at every receiver, down to about -15 dB signal to noise, often in slots where no data gets through. Read with a simple mirror model, it shows a typical 40 m NVIS channel: a reflection about 270 km up, usually a second path 1.6 ms later, and Doppler spread around a third of a hertz. A receiver in Sweden, 1,100 km away, heard frames in most daylight slots and once saw an E-layer reflection. We describe the design, the results, and Model B, in which several stations transmit and their pieces combine at the listener.

## 1. Introduction

A packet BBS in the UK gets its bulletins from forwarding partners: other BBSes that connect, offer what they have, and send what is wanted. On HF that means connected-mode AX.25 or similar over a channel that fades, with retries, and each pair of BBSes moves each bulletin again. A bulletin's routing lines tell the story. One of the reports in this study passed GB7NOT, GB7BWR and GB7BEX within minutes; others took days to get through, and some never arrived because a forwarding partner along the way wasn't set up.

A transmission one way, to anyone listening, avoids all of that. One transmission serves every listener at once, the sender never needs to know who is there, and nothing is ever sent back. The cost is that the sender cannot ask what was missed, so the coding has to make missing things not matter.

We wanted:

- No change to a listener's BBS beyond adding one forwarding partner. A bulletin should arrive the way bulletins always arrive, by forwarding, and the BBS's own duplicate check by BID should throw away anything it already has.
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

The station is a Flex 6500 at about 40 W. Each frame is an AX.25 UI frame, from GB7RDG until 11 October and from M0LTE since, and each slot opens and closes with a CW ident.

### 3.2 Why MIL-STD-188-110D

UK HF packet today runs 300 baud AFSK or BPSK, carried as IL2P, in channels about 500 Hz wide, with ARQ: every frame is acknowledged and repeated until it gets through. Winlink stations use PACTOR or VARA, which are proprietary, or ARDOP, which is open; all three are built around a two-way session whose speed is negotiated by feedback. A one-way transmission has none of that to lean on. What it needs from the modem is different:

- **No negotiation.** Every MS110D burst starts with a preamble that says which waveform and interleaver follow, so a receiver that has never heard the station decodes whatever comes, at whatever speed (autobaud). The sender can change speed slot by slot without telling anyone, which is how mailcast alternated 600 and 1200 bps and later changed interleaver.
- **A ladder of speeds in one channel.** Appendix D defines waveforms from 75 bps to 16 kbit/s in the same 3 kHz (pdn-soundmodem implements them up to 6400 bps), with one receiver. WN3 and WN4 are near the robust end of that ladder; faster ones are there for when a path allows (table below, and section 6.10).
- **Built for the HF channel.** A serial-tone waveform with known probe symbols every few dozen data symbols, an adaptive equaliser, strong convolutional coding and an interleaver handles several milliseconds of multipath and fades of a hertz or so. The 300 baud modes avoid multipath by slowing down instead, at a quarter of WN4's rate.
- **Frames that are perfect or absent.** IL2P frames with a CRC inside the 110D stream either arrive intact or are thrown away: an erasure channel, which is what a fountain code needs.
- **Open and specified.** MIL-STD-188-110D is a public standard (Distribution A), so anyone can build a compatible receiver from the document. pdn-soundmodem's implementation is held, in simulation, to the standard's own performance figures on its Poor HF channel, and WN0 to WN4 had all been decoded over the air before mailcast began.

| Waveform | Rate | Modulation | Code rate | Long interleaver | Standard's target SNR, steady / Poor channel |
|---|---|---|---|---|---|
| WN1 | 150 bps | BPSK | 1/8 | 10.24 s | -3 / 3 dB |
| WN2 | 300 bps | BPSK | 1/4 | 10.24 s | 0 / 5 dB |
| **WN3** | **600 bps** | BPSK | 1/3 | 6.83 s | 3 / 7 dB |
| **WN4** | **1200 bps** | BPSK | 2/3 | 6.83 s | 5 / 10 dB |
| WN5 | 1600 bps | BPSK | 3/4 | 7.68 s | 6 / 11 dB |
| WN13 | 2400 bps | QPSK | 9/16 | 7.68 s | 6 / 11 dB |
| WN6 | 3200 bps | QPSK | 3/4 | 7.68 s | 9 / 14 dB |
| WN7 | 4800 bps | 8PSK | 3/4 | 7.68 s | 13 / 19 dB |
| WN8 | 6400 bps | 16QAM | 3/4 | 7.68 s | 16 / 23 dB |

*Table 1. The MS110D Appendix D waveforms used in this work, all at 2400 symbols a second in 3 kHz (from the M0LTE.Ms110d waveform reference, after the standard's Tables D-II, D-XXXVII and D-LXIV). The target SNR is the standard's requirement in 3 kHz, not what the implementation achieves. Mailcast used WN3 and WN4; the Sweden path tests used the rest.*

The costs are bandwidth and signal level. MS110D fills about 3 kHz, so it cannot sit on the existing narrow packet channels, and many rigs' filters cut into it (section 6.5). And it needs more signal than the slowest modes: in simulation WN4 needs about 1.5 dB signal to noise in 3 kHz on a steady signal and 12 to 16 dB on a fading path for 10% frame loss, WN3 a few dB less. The fountain code turns part of that loss into "takes a few more slots" rather than "never arrives".

### 3.3 The timetable

40 m NVIS only carries UK paths in daylight. On 5 October the earliest and latest test slots were heard by none of the UK and Irish web SDRs tried, while those through the middle of the day decoded well. So the head end works out sunrise and sunset at Reading itself, with no internet needed, and transmits every hour on the hour from 2 hours after sunrise to 30 minutes before sunset: 09:00 to 16:00 UTC in October, 11:00 to 15:00 in December.

A slot lasts at most 10 minutes. The head end first listens for a clear channel, for up to 2 minutes, then sends a 10 s calibration tone, 1.5 s of silence, a 6.5 s sounding probe (section 3.4), a CW ident, and its frames in bursts of up to 18 s (31 s since 2 November). Each burst is its own transmission with its own preamble. The timetable is sent in every slot, so receivers follow it without being told.

### 3.4 The sounding probe

Since 7 October every slot has opened with a channel sounding: the 10 s tone, then a 6.5 s probe on the tone's own audio frequency. The probe is a Zadoff-Chu sequence of length 255 at 2400 chips per second, shaped with a root-raised-cosine pulse (roll-off 0.15) to fill 420 to 3180 Hz, and repeated 61 times back to back. A Zadoff-Chu sequence correlates to exactly zero against any cyclic shift of itself, so when it arrives by several paths each one shows up as a separate peak. One period lasts 106.25 ms, so any echo up to that late reads out unambiguously; one chip is 0.417 ms, so paths about that far apart are resolved; and each period gives a fresh estimate, so over 6 s the receiver also sees each path's Doppler shift and spread. Averaging 58 periods on top of the 255-chip correlation gain makes the probe measurable at about -15 dB signal to noise in 3 kHz, far below anything the data waveforms can decode.

The receiver finds the probe from the tone's end, correlates each period against the reference (which it builds with the very same code the transmitter uses), and accepts it only if the averaged profile stands at least 6 dB over its floor in at least half the periods. Paths closer than 0.4 ms count as one mode. Each mode is then remeasured on its own Doppler frequency, which removes the small delay error a frequency offset causes in a Zadoff-Chu probe and discards "modes" that were only another mode's shoulder.

The receiver has no absolute timing, only the delays between modes, but that is enough. With the mirror model over a round Earth, the gap between the one-hop (1F) and two-hop (2F) modes depends mainly on the layer's virtual height and hardly on distance for nearby stations: within about 200 km of Reading, a 2F mode 1.9 ms after 1F means a layer about 290 km up. A later mode is only named 2F if its delay fits an F layer between 200 and 450 km, and a height is only reported from such a confirmed pair, never from a single mode. The heights in this paper are therefore inferences under that model, bounded by its 200 to 450 km window, not independent measurements of the layer. The same model then places each ground bounce along the great circle, and the receiver names it from a small built-in gazetteer ("near Aberystwyth", or a sea and shipping forecast area). The full method is described in `docs/channel-probe.md`.

When the probe cannot be measured, the receiver falls back to the decoded data bursts as the known signal, which works only down to about +1 dB.

### 3.5 Framing and coding

Each bulletin is compressed with zstd using a 64 KB dictionary trained on earlier UK bulletins, which brings bulletins to about 30% of their size. The result is cut into 240-byte symbols and coded with RaptorQ (RFC 6330), one source block per bulletin. A RaptorQ encoder can make as many different symbols as it likes, and any K of them, plus a couple more, rebuild a bulletin that is K symbols long. A median bulletin in this study was 5.3 kB of text, about seven symbols once compressed.

Each frame carries one symbol, 271 bytes in all: a version byte, flags, an 8-byte object ID, the dictionary ID, the RaptorQ transmission information, the symbol's number, the symbol and a CRC-32. The object ID is the first 8 bytes of a SHA-256 hash over the dictionary ID and the object, so every object checks itself.

Every slot also carries a *directory*: the bulletins in rotation, the timetable and the slot's waveform. Since 15 October directories are signed with Ed25519, and a receiver holds back anything that only an unsigned or unverified directory lists.

### 3.6 How much is sent, and for how long

Each slot is filled to about 7.8 minutes on the air while there is anything to send, least-covered bulletins first. Every bulletin gets fresh symbols in every slot until 6 K symbols have gone out or 36 hours have passed: enough that any three slots rebuild it. After that a bulletin is carried only in airtime nothing else wants, up to 10 K or 48 hours. No symbol number is ever sent twice, so everything a listener hears is new to them.

From 6 October the head end sent 25,397 frames, about 6.1 MB of symbols, for 465 bulletins totalling 3.5 MB of text (about 1 MB compressed, at the dictionary's typical 30%). Not all of those frames carry bulletins: the directory takes 20 to 35 frames in every slot, about 40% of a 75-frame WN3 slot, and the readings four more. 2.7 MB of the 3.5 MB were one sender's daily news digests.

### 3.7 Propagation readings

Each slot also carries two small readings: whether 40 m is open from Reading at 100, 500 and 1000 km by the latest ionosonde sounding (Fairford, Chilton or Dourbes), and the same judged from the last 30 minutes of FT8, FT4 and WSPR spots between UK and Irish stations on PSK Reporter. They are observations only, and never change what or when the head end sends. Section 6.8 asks how well they predicted reception.

### 3.8 The receiver

The receiver is one program. It takes audio from a sound card, from SDRconnect, or from a public UberSDR web receiver, and runs the MS110D modem itself. When it has enough symbols of a bulletin it rebuilds it, checks it against its ID and the signed directory, and logs in to the listener's LinBPQ, FBB or XRouter BBS as forwarding partner Q0CAST to offer it. The BBS keeps or refuses it by BID like any partner. Symbols are kept on disk for 14 days, so pieces heard in different slots and on different days add up.

A listener with one radio can share it with their packet node. The receiver turns the node's transmitter off on that port, retunes the rig through Hamlib for each slot, and puts everything back afterwards. A receiver on a web SDR listens to each slot for about 14 minutes and stays inside the 3 hours a day that those SDRs allow.

If the listener turns it on, the receiver sends a short public daily report: one line per slot with the frames decoded, the tone's signal to noise, the propagation readings it heard, and the channel measurement. The reports go as bulletins to `MCAST@GB7RDG`, and most of section 6 is built from them.

## 4. How it changed over the month, and why

Mailcast was not designed once and then measured. It went from a first test transmission on 5 October to its present form in 70 releases, and most changes came from what the previous days' slots, the survey or a listener showed. This section follows the changes that matter for reading the results; the full dated record, with sources, is `paper/notes/evolution-timeline.md`.

### 4.1 Phases

1. **5 October: from a one-off test to hourly daylight slots.** The first slots, at 06:59 and 10:15, were barely heard, and a full test at 11:52 was wiped out because the original channel sat on top of the UK 40 m packet channels. The signal moved up to its present 7.0538 MHz centre, the pieces shrank from 940 to 240 bytes in bursts of at most 18 s, and the modem's drive was doubled. The 15:17 and 16:00 tests then rebuilt all six bulletins at most web SDRs. That evening the plan went from one slot a day to hourly, then to daylight only, because the 07:12 and 18:00 slots were heard by nobody.
2. **6 to 9 October: fill every slot, and add instruments.** The first daylight morning showed the fixed-share plan wasting airtime (the 10:00 slot keyed nothing at all) while weak listeners got little. From 11:00 on 6 October the head end filled every slot to its airtime budget and alternated WN4 and WN3. The same days added the ionosonde and PSK Reporter readings, the sounding probe, the daily reports and the dial setting for narrow rig filters (section 6.5).
3. **9 to 20 October: identity, trust and retirement.** The transmitting callsign changed from GB7RDG to M0LTE on 11 October. Directories were signed from 15 October. A look at retired bulletins on 14 October led to the leftover tier and to receivers giving up on bulletins no longer sent. The web SDR survey ran through this phase.
4. **22 to 27 October: any station can transmit.** With John G8BPQ preparing a second station, the head end lost its built-in GB7RDG settings, slots could start at any minute, and receivers followed every station they knew. The modem's probe pattern was corrected to follow MIL-STD-188-110D exactly.
5. **28 October to 1 November: Model B.** Bulletins went out in canonical form at a fixed piece size, each station in its own piece order, and a head end could relay what its local receivers heard (section 8).
6. **2 to 7 November: retuning from the daily reports.** A look at 42 daily reports showed that only the strongest listener kept up with everything, and that one sender's daily news digests were 65% of the bulletins taken in. WN4 moved to the long interleaver, bursts lengthened, and those bulk bulletins were sent last.

### 4.2 The sending strategy

| From | What the head end did | Why |
|---|---|---|
| 5 Oct 06:59 | One slot a day. Each bulletin carried on three days running, about 2 K pieces in all; 940-byte pieces. | The original design: about 120 KB a day, 15 to 24 minutes of WN4. |
| 5 Oct 15:17 | 240-byte pieces, bursts of at most 18 s. | On a busy band almost nothing of the 940-byte frames survived; short pieces also waste less on short bulletins. |
| 5 Oct evening | Hourly, then daylight only: 1.2 K + 2 pieces in a bulletin's first slot, 0.4 K five slots later. | A five-carrying hourly plan overflowed December's five daylight slots in simulation. |
| 6 Oct 11:00 | Fill each slot to 7.8 minutes, least-covered bulletin first; each bulletin enough per slot that any three slots rebuild it, at most 0.6 K a slot; retire at 6 K or 36 hours. | The 10:00 slot had sent nothing while 20 bulletins from the unheard 18:00 slot sat unsent. |
| 10 Oct 09:00 | More directory frames per slot: enough for a receiver hearing a quarter of a slot to get it with 80% probability. | G7TAJ, hearing about a quarter of the frames, saw a directory four days old. |
| 15 Oct 10:00 | Leftover tier: after 6 K or 36 hours, a bulletin is carried only in otherwise unused airtime, up to 10 K or 48 hours. | On 14 October most retired bulletins had stopped at 6 K while weak receivers held them part-built, with no way to finish. In simulation the tier raised the pieces sent per bulletin from 5.9 K to 8.1 K. |
| 30 Oct | Fixed 240-octet pieces for every object; canonical bulletins; each station's own scrambled piece order. | So that pieces from different stations combine (Model B). |
| 31 Oct 12:45 | Relayed objects join the same rotation. | A bulletin heard from another station helps listeners in a different skip zone. |
| 2 Nov 13:00 | Bulk senders strictly last; a bulletin not started 48 hours after intake retires unsent. | Daily reports showed only the strongest listener keeping up and one sender's digests making up 65% of what was taken in. A rule to concentrate on a few bulletins at a time was simulated the same day and dropped. |

### 4.3 The waveform

| From | Waveform plan | Why |
|---|---|---|
| 5 Oct | WN4 (1200 bps, short interleaver) only. | The strongest MS110D point already proven on air; WN3 held in reserve. |
| 5 Oct 16:00 | Modem drive doubled, data bursts from about 12 W to 41 W. | The Flex's meters showed the data bursts far below the tone's power. |
| 6 Oct 11:00 | Slots alternate WN4 and WN3, the order shifting each day so every hour gets both. | To compare the two on real mail; receivers follow by autobaud. |
| 26 Oct | The standard's mini-probe pattern only. | The earlier pattern sent false block boundaries that receivers written to the standard could not decode. |
| 2 Nov 11:00 | WN4 on the long interleaver (bursts padded to whole 6.83 s blocks); WN3 unchanged. | It rides out longer fades. *[Tom: what prompted the change? Issue #234 records "not now" for this trial the same morning.]* |
| 2 Nov 10:15 | Bursts of up to 31 s instead of 18 s. | *[Tom: reason not recorded. 31 s holds four long-interleaver blocks with the preamble, and fewer bursts save each burst's preamble.]* WN3 slots went from 73 to about 86 frames, and long WN4 slots carried 161 frames. |

Two things follow for reading the results. Most of the reports come from after 15 October, so they describe the filled slots with the leftover tier, alternating WN4 and WN3. And the long interleaver ran only in the last six days, too short to compare with the short interleaver on the air; the Sweden path tests (section 6.10) compared them directly.

## 5. Data

Three sources, all in this repository:

- **The head end's journal**, from the first test slot on 5 October to 7 November (the figures below start on 6 October, the first day of hourly slots): what each slot planned and sent, on which waveform, for how long, and what was taken in from the BBS (`data/headend-gb7rdg`).
- **A web SDR survey**, 6 to 20 October. Eight receivers on one machine, each listening through a different public UberSDR, from 83 to 544 km away, logged every slot (`data/web-sdr-survey-2026-10`). Two of the SDRs were unreachable for some of the time; those slots are left out.
- **Listeners' daily reports**, 129 reports from nine listeners, 8 October to 7 November (`reports/`). Most were copied from GB7RDG; some never reached it and were pasted from other BBSes. Reports from 8 to 18 October are incomplete, because GB7RDG then kept bulletins for only a week and the archive was started later; GB7RDG's message index names 12 more whose text is gone.

Throughout, a receiver's *share* is the frames it decoded over the frames sent in the slots it was listening to. A slot cut short counts the frames it did send; a slot that sent nothing is left out. Days on which a listener's receiver had no audio at all are left out, but slots lost to a shorter audio fault count as heard nothing.

Three cautions apply to every comparison over time. The software changed throughout (the head end went through 37 releases, receivers from 0.4 to 0.33), listeners chose themselves and cover different dates, and several improved their setups during the study. Tone signal to noise on a web SDR also passes through that SDR's own receiver and AGC.

## 6. Results

### 6.1 The transmitter

From 6 October to 7 November the head end ran 259 slots over 33 days: 252 completed, 6 cut short and 1 skipped. A completed slot lasted a median 7.6 minutes from its start to the closing ident, 29.6 hours in all, including any wait for a clear channel. It sent 25,397 frames in 5,182 bursts: 13,500 on WN4, 2,544 on WN4 with the long interleaver, and 9,320 on WN3.

The clear-channel check found the channel busy at the start of 150 of 258 slots and waited for it to clear, usually for only a few seconds. On the afternoon of 22 October four slots sent nothing: the channel read busy throughout, probably local interference, and the modem never keyed. Two more slots were cut short, one by a restart of the head end and one by a dropped connection to the modem.

### 6.2 Who heard it: the web SDR survey

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

*Table 2. Web SDR survey, 6 to 20 October. "Bulletins rebuilt" counts each bulletin once, when the receiver first completed it; 224 bulletins had been taken in before the survey's last slot. G4WNC was unreachable for 61 of its slots.*

Distance alone does not decide reception. Two SDRs over 500 km away, in Ireland and Fife, heard nearly as well as the best one 135 km away, while the two nearest, within 90 km of Reading, heard 1% and 13%. G4WNC, the nearest, had decoded 38 of 41 frames on 5 October, so its later silence says more about that SDR than about the path. We cannot separate the path from each SDR's own antenna and local noise with this data, but the tone's signal to noise tracks the share of frames heard far better than distance does.

The four strongest SDRs rebuilt 198 to 223 of the 224 bulletins. Weak receivers rebuilt a good part too: M9TWM-1 decoded only a quarter of the frames sent but rebuilt 131 (58%), and G4EYR heard 13% and rebuilt 80 (36%). This is the fountain code doing its job: a bulletin was spread over several slots and several hours, and a receiver that heard a bit of each often ended up with enough.

The two weeks also differed: the nearer strong SDRs heard much more in the second week (WESSEX 65% then 90%, STUEY3D 51% then 85%), while the distant ones changed little (`paper/figures/heard-share-tables.md`). Most of the difference is two bad days, 6 October (both heard nothing while EI4HQ heard 78%) and 11 October; from 12 October both heard 85 to 98% most days. The receivers' software also changed several times during the survey.

### 6.3 Who heard it: listeners' reports

| Listener | Locator | Dates | Days | Slots listened | Slots with frames | Frames heard | To 1 Nov | From 2 Nov | Median tone SNR (dB) | Bulletins rebuilt |
|---|---|---|---|---|---|---|---|---|---|---|
| M7TAW | IO91ma | 24 to 31 Oct | 7 | 56 | 86% | 57% | 57% | - | 10 | 111 |
| M0NVK | IO90hr | 15 Oct to 6 Nov | 10 | 71 | 87% | 55% | 67% | 37% | 20 | 124 |
| GM4CXF | IO75qn | 25 Oct to 6 Nov | 13 | 99 | 98% | 50% | 66% | 21% | 11 | 165 |
| M9TWM | IO93fj | 8 to 30 Oct | 9 | 72 | 89% | 24% | 24% | - | 5 | 63 |
| G8BPQ | IO92kx | 8 to 31 Oct | 23 | 168 | 84% | 23% | 23% | - | 4 | 163 |
| GM8BPQ | IO68vl | 8 Oct to 7 Nov | 25 | 180 | 77% | 14% | 17% | 2% | 4 | 99 |
| G7TAJ | IO81le | 8 Oct to 3 Nov | 18 | 135 | 64% | 13% | 14% | 5% | 8 | 79 |
| MM0MQN | IO86bd | 28 to 29 Oct | 2 | 12 | 33% | 3% | 3% | - | -4 | 0 |
| EI7KX (web SDR) | - | 2 to 7 Nov | 6 | 41 | 88% | 18% | - | 18% | 4 | 43 |
| M7TAW (WESSEX SDR) | - | 21 to 26 Oct | 3 | 19 | 89% | 75% | 75% | - | 22 | 48 |
| M0NVK (WESSEX SDR) | - | 9 to 10 Oct | 2 | 13 | 92% | 77% | 77% | - | 24 | 19 |
| G7TAJ (WESSEX SDR) | - | 26 to 27 Oct | 2 | 14 | 93% | 66% | 66% | - | 27 | 46 |

*Table 3. Listeners' daily reports, 8 October to 7 November, on their own equipment (M9TWM's own UberSDR counts as his) and, below, the days some listened through a public web SDR. Nine report-days with no audio at all are left out. The listeners cover different dates, so the table compares dates as much as stations.*

The picture matches the survey. On their own equipment three listeners heard about half the frames or more and five heard 13 to 24%; MM0MQN, in central Scotland, sent only two reports. All but MM0MQN heard frames in most slots, from 64% of slots for G7TAJ to 98% for GM4CXF in Ayrshire. GM8BPQ, on Lewis in the Outer Hebrides about 850 km from Reading, heard frames in three slots out of four.

**Reception fell sharply from 2 November.** Every listener who reported on both sides of that date heard much less afterwards: GM4CXF from 66% to 21%, M0NVK from 67% to 37%, G7TAJ from 14% to 5%, GM8BPQ from 17% to 2%. Several things changed that day: WN4 moved to the long interleaver, bursts lengthened from 18 s to 31 s, one sender's digests were sent last, and the first slot moved from 09:00 to 10:00 as the days shortened. The drop is as large on WN3, whose coding did not change (GM4CXF 73% to 22%, GM8BPQ 20% to 3%), so the long interleaver alone does not explain it. The longer bursts, which apply to both waveforms, the season, and the station itself all remain possible causes; section 6.11 looks at the space weather. *[To resolve before publication.]*

Listeners' own setups mattered a great deal. Several shared a radio with a packet node, and the reports and the channel discussion show time lost to receive filters narrower than the signal, the wrong rig mode, and audio levels.

### 6.4 Waveforms: 1200 bps against 600 bps

| Receiver | Share heard, WN4 | Share heard, WN3 | Frames a minute, WN4 | Frames a minute, WN3 |
|---|---|---|---|---|
| WESSEX | 80% | 82% | 14.9 | 7.9 |
| EI4HQ | 76% | 83% | 14.2 | 8.0 |
| STUEY3D | 69% | 76% | 12.9 | 7.4 |
| M9PSY | 66% | 75% | 12.3 | 7.2 |
| M0NVK | 55% | 54% | 10.7 | 5.4 |
| M7TAW | 55% | 60% | 10.7 | 5.9 |
| GM4CXF | 48% | 54% | 9.4 | 5.5 |
| M9TWM | 21% | 29% | 3.9 | 2.8 |
| EI7KX (web SDR) | 19% | 16% | 3.8 | 1.7 |
| G7TAJ | 12% | 15% | 2.3 | 1.5 |
| G4EYR | 12% | 16% | 2.2 | 1.5 |
| G8BPQ | 15% | 36% | 2.9 | 3.5 |
| GM8BPQ | 10% | 21% | 1.8 | 2.1 |
| MM0MQN | 2% | 6% | 0.3 | 0.6 |

*Table 4. Frames heard on WN4 and WN3 slots, as a share of what was sent and per minute of slot time, for the survey SDRs and listeners (own equipment) that heard both. WN4 includes its long-interleaver slots from 2 November. Each frame carries the same 240-byte piece on either waveform, so frames a minute is the rate at which bulletin data arrived. M9TWM-1 matches M9TWM and is not repeated.*

WN3 is heard a little more often than WN4 by most receivers, but sends half as many frames a minute, so WN4 delivered more data a minute to every receiver except the three weakest: G8BPQ and GM8BPQ, where WN3 more than doubled the share heard, and MM0MQN. For those the order reverses, though only modestly (3.5 against 2.9 frames a minute for G8BPQ). Alternating the two cost most receivers some throughput to help a few; choosing the waveform per slot, or per listener group, from the channel measurement and the daily reports is future work.

### 6.5 Through an ordinary rig's filters

A Flex or an SDR passes the whole 3.24 kHz signal. Most listeners' rigs do not: SSB and data filters of 2.1 to 2.7 kHz are typical, and they usually sit around 300 to 2700 Hz in the audio, below the signal's 1800 Hz centre. One listener's FT-450D, measured in its data mode, passed 367 to 2190 Hz. The MS110D signal is 2400 Hz wide at its core, so such a filter always cuts into it.

Before the first transmission we simulated this (pdn-soundmodem, `docs/dev/ms110d/evidence/2026-10-04-filter-width`): 111,000 frames of WN4 and WN3 through models of 2.4, 2.7 and 3.0 kHz receive filters, both an 8-pole crystal filter and a linear-phase DSP filter, on a steady signal and on the moderate and poor ITU-R F.1487 mid-latitude fading channels, with the transmit pulse roll-off varied from 0.35 down to 0.05. The findings:

- **A 2.4 kHz filter costs about half a dB on a steady signal and 1 to 2 dB on a fading path** for WN4, about 1 dB for WN3, plus a few percent of frames that more signal does not bring back. 2.7 and 3.0 kHz filters cost between nothing and 1 dB, mostly about half a dB.
- **Narrowing the transmitted signal does not help.** The core is 2400 Hz whatever the roll-off; a narrower roll-off only trims the skirts and makes the pulse ring longer, which the receiver handles worse on a fading channel. Through a 2.4 kHz filter on the poor channel, WN4 lost 21% of frames at roll-off 0.35 and 30% at 0.05. So the transmitter keeps the standard's 0.35.
- **The receiver need not match a narrower transmitter**: its equaliser absorbs the mismatch.
- **Frame length mattered more than any filter.** On the poor channel a 960-byte WN4 frame needed about 16 dB for 10% loss, a 255-byte one about 12.4 dB, which is one reason the transmitter moved to 240-byte pieces on the first day.

Real recordings then showed how much the filter's position matters. On 8 October recorded slots were played through a 2.1 kHz DSP filter and a 2.4 kHz crystal filter. With the signal at 1800 Hz in the audio, the 2.1 kHz filter kept 16 of 36 frames; moving the dial 300 Hz up, so the signal sat at 1500 Hz, nearer the middle of the filter, kept 30 of 36. Through the 2.4 kHz crystal filter it was 23 against 34 (pdn-mailcast PR #65; the recordings and the analysis were not published, and should be before the paper is). The signal tolerates losing a little at both edges far better than losing a lot at one.

So the receiver now puts the signal in the middle of the listener's own filter rather than asking them to change the rig. "Measure my filter" listens to about 10 s of band noise between slots, finds the passband's edges, and sets the dial so that the 7.0538 MHz centre lands in the middle of it: for the FT-450D above, a dial of 7.05252 MHz. Listeners can also type the edges from their rig's manual. SDRs, web SDRs and Flex radios stay on 7.052 MHz.

Two gaps remain. Neither study tested a narrow *transmit* filter, which matters for Model B: a second station transmitting from an ordinary rig puts its own filter in the path, ahead of the noise rather than after it, and the effect on reception has not been measured. And we have not yet compared listeners' reception before and after they changed dial, since the reports do not record the dial.

### 6.6 The sounding probe in use

Of the 880 report slots in which the head end sent frames, the probe gave a channel measurement in 610 (69%). In 64 of those no data frame was decoded at all: the probe saw the path when the data could not get through, at tone signal to noise ratios down to -16 dB. The data bursts gave the measurement in another 133 slots where the probe was not found, and 137 slots had no measurement at all, 96 of them because nothing was heard.

The probe's value to a listener is that it answers "is there a path, and what is it like?" even in a slot that delivers nothing, and a receiver's status page says so in a line such as "2 paths: 1 hop, and 2 hops 1.9 ms later, 17 dB weaker. Reflection about 290 km up. Doppler spread 0.2 Hz: steady." Its value to this study is the measurements in the next section, three quarters of which come from the probe.

### 6.7 The channel

Receivers measure the path in each slot from the sounding probe, or failing that from the data bursts. Across 743 measured report slots:

- **Propagation modes.** One mode in 243 slots, two in 345, three in 135, four in 20.
- **Virtual height.** Median 266 km, quartiles 244 to 290 km, inferred from the 1F to 2F delay gap (section 3.4).
- **Second mode.** Median 1.6 ms after the first (quartiles 1.4 to 1.8 ms), and 11 dB weaker (quartiles 15 to 6 dB weaker). Because the model names a mode 2F only if its delay fits an F layer, the height and this delay are two readings of the same measurement, not independent confirmations.
- **Delay spread.** Median 0.42 ms, quartiles 0.29 to 0.58 ms.
- **Doppler spread.** Median 0.34 Hz, quartiles 0.24 to 0.52 Hz.

Against the mid-latitude test channels of ITU-R F.1487, the two-path delay of 1.6 ms lies between the moderate (1 ms) and disturbed (2 ms) channels, and the Doppler spread between the quiet (0.1 Hz) and moderate (0.5 Hz) ones: a channel the 110D equaliser is designed for, fading slowly. In this data reception looks limited more by signal to noise and interference than by the channel's shape, though we have not tested that directly.

By hour of day, report slots with frames ranged from 73% at 09:00 UTC to 88% at 14:00, and the share of frames heard from 27% to 34%, with no strong pattern across the daylight window. The 09:00 slot ran only in October, before the November drop, so it is if anything flattered. The median virtual height stayed between 256 and 277 km from hour to hour, again with no clear trend over the day.

### 6.8 Did the propagation readings predict reception?

| Source | Verdict | Receiver-slots | With frames | Frames heard |
|---|---|---|---|---|
| Ionosonde | good | 822 | 83% | 43% |
| Ionosonde | marginal | 653 | 65% | 35% |
| Ionosonde | unknown | 232 | 78% | 26% |
| PSK Reporter | good | 1673 | 76% | 38% |
| PSK Reporter | unknown | 34 | 68% | 29% |

*Table 5. The verdict the head end logged as each slot began, against what the survey and report receivers heard in that slot. 56 receiver-slots had no verdict logged.*

The ionosonde verdict separates slots modestly: receivers heard 43% of frames in slots it called good and 35% in those it called marginal. Marginal verdicts cluster at the first and last slots of the day, and almost all the unknown verdicts fall in November, when the ionosonde feed was often stale, so the unknown row largely reflects the November drop. PSK Reporter called 40 m open in essentially every slot. FT8 decodes far below what MS110D needs, so "open for FT8" says little about a 1200 bps link; the median SNR of the spots behind each verdict may predict better, and is left for later analysis.

### 6.9 Interference and failures

The channel around 7.052 to 7.055 MHz is shared. Listeners reported FT8, VARA and broadcast signals over the mailcast signal. On 30 October an over-the-horizon radar swept across it; in Sweden it wiped out reception of a path test, while G7TAJ reported still decoding 1200 bps through it. On 22 October local interference kept the channel reading busy all afternoon and four slots sent nothing. Reports themselves were sometimes lost on their way back: one listener's BBS offered five reports to a neighbouring BBS that had not set it up as a forwarding partner, and every offer was refused.

### 6.10 Beyond the UK

Daz SA6DAZ, near Gothenburg (JO67fm, about 1,100 km from Reading), lent his station from late October: an IC-7300 on a resonant dipole about 8 m up, broadside to the UK, with a Raspberry Pi running pdn-soundmodem and the mailcast receiver. 1,100 km is well beyond the NVIS footprint the design assumed, and we expected little.

**Mailcast in Sweden.** From 30 October to 7 November the receiver listened to 65 daylight slots. It heard frames in 46 of them, 875 frames in all, and completed 30 bulletins. Tone signal to noise ranged from about -3 to +14 dB. Its median virtual height was 310 km (51 measurements), higher than the UK receivers' 266 km, as a longer one-hop path off the same layer would suggest under the model. On 6 November at 14:00 UTC its probe resolved three paths: one hop off the E layer, one hop off the F layer 0.5 ms later at about the same strength, and two F hops 1.6 ms after that, 8 dB down.

**Path tests.** Between mailcast slots, from 30 October to 1 November, the head end sent 136 rounds of test bursts to the same station, each round a tone and probe and then one burst of each of eleven MS110D waveform and interleaver combinations, recorded and decoded in Sweden. At 6 to 9 dB signal to noise in 3 kHz the faster waveforms were still useful: WN5 (1600 bps) decoded 95% of its frames, WN13 (2400 bps) 89% and WN6 (3200 bps) 82%, while WN7 (4800 bps) managed 12% and WN8 (6400 bps, 16QAM) none. At 12 to 15 dB WN8 decoded 58%. The robust end, WN1 to WN4, decoded most frames from around 0 dB upwards. These are close to the standard's targets in Table 1, given that the path was fading.

So a single transmitter on 40 m reaches well into Scandinavia in daylight, at least in autumn, and the channel there often supports waveforms two to three times faster than mailcast used.

## 7. Lessons from operation

**Sharing a radio is the hard part.** Most listeners already run a packet node, and the receiver has to borrow the radio for each slot: stop the node transmitting, retune, and put everything back. Rig modes caught people out (a Xiegu G90 needs its UD mode rather than USB for its wider filter), as did receive filters narrower than the signal (section 6.5). The receiver now defaults the rig to PKTUSB, and places the signal in the middle of whatever filter the rig has rather than asking anyone to widen it.

**Getting reports back is a routing problem.** The daily report travels by ordinary packet mail. Several listeners' reports never reached GB7RDG, because some BBS along the way had not set up the next one as a forwarding partner. Nobody has a full map of who forwards to whom any more.

**Words and callsigns matter.** The UK licence allows messages to be "transmitted by a Beacon or via a Data Station for reception by Amateurs", but "broadcast" is a loaded word in amateur radio, and we say *transmission*. Transmitting under the BBS's callsign was technically outside the terms of GB7RDG's licence, so from 11 October the head end transmits as M0LTE.

**Web SDRs make listening easy.** Anyone can be a listener with no radio at all, through a public UberSDR. The receiver keeps inside each SDR's allowance of 3 hours a day.

## 8. Model B: many stations

With one transmitter, mailcast has one point of failure and one footprint. Model B lets any number of stations transmit, with no link between them, and lets a listener combine what it hears from all of them.

The idea rests on content addressing. If two stations encode the same bulletin to the same bytes, they produce symbols of the same object, and a listener combines them. That needs three things:

1. **The same bulletin becomes the same bytes**, whichever BBS it came through. Forwarding adds routing lines and the odd header, so each station reduces a bulletin to a canonical form first. We measured this across three UK BBSes that list their bulletins, which receive them by largely independent routes: of 112 bulletins held by two or more of them, 111 combine once in canonical form (`research/138`). In the odd one out the copies genuinely differed.
2. **Every station encodes identically.** The canonical bulletin, the compression (zstd 1.5.7, dictionary 1) and the RaptorQ coding are all fixed, and test vectors are reproduced independently in Python and Rust. This was confirmed on air on 30 October.
3. **Stations do not send the same symbols.** Each station sends its own scrambled order of symbol numbers, so two stations rarely duplicate each other.

Everything else is built: each station signs its own directory, receivers follow every station they can hear, a receiver rebuilds a bulletin with its own station's routing line, and a second copy of a bulletin already delivered is dropped and counted. A head end can also relay what its local receivers have heard from other stations. What remains is the on-air trial with a second station, John G8BPQ's GM8BPQ on 40 m at half past each hour (with a QO-100 trial to follow), to measure how much combining gains in practice.

## 9. Future work

- Choosing each slot's waveform from the channel measurement, and faster waveforms when the channel allows.
- Using the daily reports, rather than a return channel, to steer how much of each bulletin is sent.
- Comparing the probe's virtual heights with the ionosonde's h'F for the same hour, which is also a virtual height and so directly comparable.
- 80 m in the evenings, and QO-100.
- Personal mail as well as bulletins (under consultation).
- Measuring the cost of a narrow transmit filter, before stations with ordinary rigs join Model B.
- The Model B trial.

## 10. Conclusion

A one-way, fountain-coded transmission delivers packet bulletins to listeners across the UK and Ireland, and on occasion Sweden, with nothing sent back and no change to their BBS beyond one forwarding partner. Receivers with a good path hear most of what is sent and rebuild nearly all of it; weak ones hear a fraction and still rebuild a third to over half, because pieces from different hours add up. The channel is gentle; the limits are signal to noise, interference and the practicalities of sharing a radio, and a sharp fall in reception in early November still needs explaining. Model B, now built, takes the same idea to many transmitters.

## Acknowledgements

Perry M0PYL suggested the fountain code. John G8BPQ, Steve G7TAJ, Eoin M0NVK, Anthony M7TAW, Jim GM4CXF and the other listeners on the OARC packet channels ran receivers and sent reports. Daz SA6DAZ lent his station in Sweden. The web SDR operators made the survey possible.

## Appendices (to write)

- A. On-air format, summarised from `docs/design.md`, with the test vectors.
- B. The daily report format (MCR1).
- C. Data and code availability.
