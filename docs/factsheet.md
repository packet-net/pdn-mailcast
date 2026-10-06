# pdn-mailcast technical fact sheet

A one-way HF transmission of packet BBS bulletins from GB7RDG, received by a standalone program or a web SDR and forwarded into the listener's own BBS. Figures are as configured and measured on 2026-10-05. The design and its reasoning are in [design.md](design.md).

## Signal

| | |
|---|---|
| Frequency | Centre 7.0538 MHz; receivers tune a USB dial of **7.052 MHz**, which puts the centre at 1800 Hz audio |
| Occupied bandwidth | 3.24 kHz edge to edge (7.0522 to 7.0554 MHz); 99% of the power within 2.89 kHz (7.0524 to 7.0553 MHz) |
| Clear of | The UK HF packet channels at 7.0503, 7.05095 and 7.0516 MHz |
| Waveform | MIL-STD-188-110D Appendix D, 3 kHz serial tone, waveform number 4 (`ms110d-wn4` in pdn-soundmodem). The head end can alternate slots with WN3 (600 bps, rate 1/3) to compare them; receivers follow by autobaud, and the slot report and directory name each slot's waveform |
| Modulation | BPSK at 2400 baud on an 1800 Hz subcarrier, root-raised-cosine pulse shaping, roll-off 0.35 |
| User data rate | 1200 bps (rate 2/3 convolutional code, mini-probes of 32 symbols every 96 data symbols) |
| Interleaver | Short; 3-superframe preamble per burst; receiver is autobaud |
| Bursts | Frames queued together share one burst of up to 18 s, one preamble each; each burst is its own keyup |
| Power | 50 W setting on a Flex 6500; measured forward power about 41 W median during data, 44 W during the tone; SWR 1.02 to 1.09 |
| Transmit level | pdn-soundmodem `txAmplitude` 1.0 (data peaks at about 0.75 of full scale; the tone at 0.8) |
| Identification | AX.25 source callsign GB7RDG on every frame, plus CW ident GB7RDG at 20 wpm on the signal centre at the start of each slot and when the transmitter is released |

## Timetable

| | |
|---|---|
| Slots | Hourly on the hour, UTC, in daylight only |
| Daylight window | From 2 hours after sunrise to 30 minutes before sunset at IO91lk, by a built-in solar calculation (no internet); about 09:00 to 17:00 UTC in October, 11:00 to 15:00 in December, 06:00 to 19:00 in June |
| Why daylight | 40 m NVIS only carries the UK paths in daylight: on 2026-10-05 slots at 07:12 and 18:00 UTC were heard by none of nine UK and Irish web SDRs |
| Slot sequence | Listen for a clear channel (up to 2 min), take the transmit lease, 10 s tone at 7.0538 MHz, 8 s pause, bursts with 1 s gaps, release the lease, closing CW ident |
| Slot limit | 10 minutes from the slot's time, closing ident included; nothing keys past it. The budget rule fills a slot to about 7.8 minutes, keeping back the 2 minute clear-channel wait and a carrier wait, and fills a late slot less |
| Typical airtime | Head end 0.3.0: about 2.5 minutes per slot in October, about 4 in December. Budget rule: up to about 7.8 minutes in every slot while bulletins are in rotation, about 70 minutes a day in October at WN4 |
| Timetable announcement | Sent in every slot inside the directory, so receivers follow GB7RDG without configuration |

## Framing and coding

| | |
|---|---|
| Link layer | AX.25 UI frame, GB7RDG to MCAST, PID F0, carried as IL2P with CRC inside the MS110D bit stream |
| Frame payload | 271 bytes: version (1), flags (1), object ID (8), dictionary ID (2), RaptorQ OTI (12), ESI (3), symbol (240), CRC-32 (4) |
| Object ID | First 8 bytes of SHA-256 over the dictionary ID and the object, so every object checks itself |
| Forward error correction | RaptorQ (RFC 6330), one source block per object, 240-byte symbols; any K plus a couple of symbols rebuilds an object, and pieces from different slots add up |
| Repeats | Budget rule: every slot sends fresh symbols of every bulletin in rotation, least covered first, at least enough that any 3 slots rebuild it and at most 0.6 x K a slot, until 6 x K in all or 36 hours. Head end 0.3.0: 1.2 x K + 2 in the first slot, 0.4 x K five slots later. A symbol number is never reused |
| Compression | zstd with a trained 64 KB dictionary (`gb7rdg-1`, ID 1), checksum required; bulletins come down to about 30% of their size |
| Content types | First byte of each object: 1 packet mail bulletin (FBB/BPQ message format), 2 directory, 3 reserved for DAPPS, 112 to 127 experimental; top bit set means a length-prefixed metadata block follows |
| Directory | Sent in every slot that keys: the bulletins in rotation (BID, title, size, content type), the timetable, the daylight rule and the slot's waveform; text, versioned, unknown fields ignored. A slot with nothing to send keys nothing, directory included; 0.3.0 skipped slots that way when no share was due, the budget rule only when nothing is in rotation |
| Bulletin format | Key: value header (type, from, to, at, BID, date, title), blank line, then the message with its R: lines, as a BBS partner would forward it |

## Station (GB7RDG)

| | |
|---|---|
| Software | pdn-mailcast head end 0.3.0 beside pdn-soundmodem 0.85.0 on the node that drives the Flex |
| Intake | Forwarding partner Q0HEAD on GB7RDG's LinBPQ (FBB B1F over the FBBPORT, bulletins only, flood routes WW, GBR, EURO); bulletins over 32 KB are refused, and personal mail and NTS are left queued in LinBPQ |
| Coexistence | A transmit lease in pdn-soundmodem holds LinBPQ's packet traffic off for the slot only; frames waiting when the lease ends are dropped, never sent late |
| Safety | Refuses to key unless the system clock is synchronised; stops the slot if the PA passes 70 C (read-only Flex meter connection); one-off slots by `pdn-mailcast-headend --run-now` |

## Receivers

| | |
|---|---|
| Software | pdn-mailcast receiver (Linux, amd64, arm64 and armhf; from the packet-net apt repo) |
| Audio | A radio on 7.052 MHz USB into a sound card (listens all the time), or a public UberSDR web receiver (listens to every daylight slot, 14 minutes each, up to 12 a day to stay inside the 3 hours a day such receivers allow) |
| Delivery | Logs in to the listener's LinBPQ or Linux FBB as forwarding partner Q0CAST and offers each rebuilt bulletin by FBB B1F; the BBS keeps or refuses it by BID like any partner |
| Durability | Rebuilt bulletins stay in an on-disk outbox until the BBS has answered; partial objects and completed markers expire after 14 days |
| Mail | The receiver keeps its own copy of every bulletin (30 days or 50 MB unless set), readable on the status page, and any of them can be sent to the BBS again |
| Sharing a radio | If the radio is also a LinBPQ packet radio, the receiver turns LinBPQ's transmitter off on that port (`XMITOFF`), tunes the rig to 7.052 MHz by Hamlib rigctld (flrig works through `rigctld -m 4`) from a minute before each slot to 12 minutes after, then puts the rig back and turns the port on again; it gives the rig back at once if it ever sees PTT on |
| Status | Local web page with level meter, spectrogram, the tone's frequency offset and signal-to-noise, today's slot times and bulletin progress; open on the local network only with a password, behind a sign-in page |

## Measured on air, 2026-10-05

16:00 UTC slot, 6 bulletins (22 KB of text) as 41 frames in 7 bursts, 2.7 minutes including the tone (30 s; the head end was still daily then). The head end's airtime estimate for it is 2.71 minutes to the release:

| Web SDR | Distance | Tone SNR in 3 kHz | Frames decoded | Bulletins rebuilt |
|---|---|---|---|---|
| WESSEX | 143 km | 24.9 dB | 41 of 41 | 6 of 6 |
| STUEY3D | 92 km | 19.5 dB | 41 of 41 | 6 of 6 |
| M9PSY | 531 km | 11.5 dB | 41 of 41 | 6 of 6 |
| G4WNC | 90 km | 9.9 dB | 38 of 41 | 6 of 6 |
| EI4HQ | 507 km | 18.0 dB | 36 of 41 | 6 of 6 |
| M9TWM-1 | 218 km | 7.6 dB | 20 of 41 | 4 of 6 |

## Software and licences

| Component | Where | Licence |
|---|---|---|
| Head end, receiver, core library | github.com/packet-net/pdn-mailcast; the core library is Packet.Mailcast on nuget.org | AGPL-3.0-only |
| RaptorQ | M0LTE.RaptorQ on nuget.org, checked byte for byte against the Rust `raptorq` crate | AGPL-3.0-only |
| FBB forwarding | Packet.Fbb on nuget.org, from github.com/packet-net/pdn-fbb, shared with pdn-bbs | AGPL-3.0-or-later |
| Modem, transmit lease, rig control | github.com/packet-net/pdn-soundmodem | AGPL-3.0-or-later, with some files GPL-3.0 (see its LICENSING.md) |

Using a fountain code to make a one-way link work was Perry M0PYL's idea.
