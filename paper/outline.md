# Mailcast paper: outline

Working title: **Mailcast: one-way HF delivery of packet BBS bulletins with fountain codes**

Possible homes: a full paper for the TAPR/ARRL Digital Communications Conference proceedings, or QEX. A shorter, friendlier version for RadCom, and a blog post that points at both. Everything here is written so the magazine and blog versions can be cut from it.

## 1. Abstract (150 words)

UK packet BBSes move bulletins over slow, fragile, point-to-point HF links. Mailcast sends them one way instead: one transmitter on 40 m NVIS, a MIL-STD-188-110D waveform, RaptorQ fountain coding so that any listener rebuilds a bulletin from any large enough set of pieces, and a receiver that hands each bulletin to the listener's own BBS. We describe the design and report a month of on-air results from 8 reporting listeners and a 2-week survey of about 10 web SDRs. We also describe Model B, in which many stations transmit and their pieces combine at the listener.

## 2. Introduction and motivation

- How bulletins move on UK packet today: forwarding partners, connected-mode HF, retries, and the airtime that costs. Include one example of a bulletin's route from its R: lines.
- Why one-way: one transmission serves every listener, and nothing has to be sent back.
- What we wanted: listeners need no changes to their BBS, a receiver that runs on a Pi or a public web SDR, and nothing that breaks the existing 40 m packet channels.

## 3. Related work (short)

- Broadcast file delivery with fountain codes: DVB and 3GPP MBMS use RaptorQ (RFC 6330). Also satellite and data-carousel systems.
- Amateur precedents: WSPR and FT8 (one-way and weak-signal, but no files), Winlink and VARA (connected, point to point), APRS, and any HF one-way bulletin systems we can find. This needs a literature search.
- MIL-STD-188-110D and STANAG 5066 in military and maritime use.

## 4. System design

- **Signal:** 110D Appendix D WN4 (1200 bps, BPSK 2400 baud) and WN3 (600 bps), 3 kHz, centre 7.0538 MHz, clear of the UK packet channels. Why WN4 is the main waveform. Figure: spectrum or waterfall. Source: factsheet.md.
- **Timetable:** hourly daylight slots worked out from sunrise and sunset, with a 10 minute cap and a clear-channel wait. Why daylight only: the 07:12 and 18:00 slots on 5 October that no web SDR heard.
- **Framing:** AX.25 UI frames in IL2P, 271-byte payload, an object ID made from the content's hash.
- **Coding:** RaptorQ with 240-byte symbols, one source block per object, and zstd with a trained dictionary (to about 30%). Repeat policy and how long a bulletin is kept: 6 K or 36 h, then spare airtime up to 10 K or 48 h. Worked example of the K-of-N arithmetic.
- **The slot:** tone, then probe, then bursts. The calibration tone gives SNR, and the ZC255 probe measures the channel. The directory is signed with Ed25519 and carries the timetable.
- **Propagation readings:** the ionosonde and PSK Reporter verdicts sent in the slot. They only observe, and never change what is sent.
- **Receiver:** sound card, SDRconnect or UberSDR; FBB B1F delivery as forwarding partner Q0CAST; sharing a radio with a packet node (XMITOFF, rigctld); the daily report.
- **Station:** a Flex 6500 at about 40 W, and coexistence with LinBPQ through the transmit lease.
- Figure: block diagram from GB7RDG's BBS to the listener's BBS.

## 5. Results

Each part names its source and the table or figure it needs.

1. **First day (5 October):** 41 of 41 frames and 6 of 6 bulletins at WESSEX and STUEY3D. Source: factsheet.md.
2. **Web SDR survey (6 to 20 October):** about 10 UK and Irish web SDRs on CT 150. For each: distance, tone SNR, share of frames decoded, bulletins completed. Figure: completion against distance and SNR (Tom's 22 October survey chart). *Source data still to gather from CT 150.*
3. **Listener reports (8 October to 7 November):** reports/data. Per listener: slots heard, frames, bulletins rebuilt and delivered. Figures:
   - share of slots heard by hour of day and by listener
   - tone SNR spread by listener
   - WN4 against WN3 frames per slot
   - virtual height and delay spread by hour (a measured NVIS channel)
   - how often the ionosonde and PSK Reporter verdicts matched what was actually heard
4. **Fountain coding in practice:** how many slots a listener needed to finish a bulletin; 92 of 110 bulletins retired at 6 K, median K 5; partial bulletins stuck at weak receivers and the fix (given-up count, leftover tier). Sources: memory and issue history, head end recent-slots, MCR header fields 8 to 10.
5. **Channel measurements:** probe-derived 2F delay and power, Doppler spread, virtual height. Compare probe with burst estimates where both exist.
6. **Interference and failures:** the 30 October radar sweep, a busy channel cancelling the 22 October afternoon slots, AM broadcast, FT8 and VARA over the channel, and report routing failures.
7. **Beyond the UK:** MS110D path tests to SA6DAZ in Sweden (6400 bps QAM16 frames decoded on 30 October). Source: SA6DAZ test logs.

## 6. Operational lessons

- Sharing a radio with a packet node is the hardest part for listeners: rig modes (UD against USB on the G90, PKTUSB), filters and dial choice ("Measure my filter").
- Getting reports back: mail routing to GB7RDG failed for several listeners. Nobody now has a full picture of who routes to whom.
- Licensing and wording: the callsign moved from GB7RDG to M0LTE on 11 October. Use "transmission", not "broadcast".
- Web SDRs as receivers: the 3-hours-a-day limit, and how the receiver budgets its listening around it.

## 7. Model B: many stations

- The idea: each station encodes its own BBS's copy, identical bytes give the same object, and pieces combine at the listener with no link between stations.
- The evidence that it can work: 111 of 112 bulletins shared between BBSes combine once in canonical form (research/138). What canonical means, and the exceptions (`>From`, forwarded From/To lines).
- What is built: bit-exact encoding with test vectors reproduced independently in Python and Rust; each station's own piece order; signed directories from every station; receivers that follow every station; relaying over the LAN; the receiver API.
- What is next: the on-air trial with GM8BPQ (40 m at :30, and QO-100); measuring how much the combining gains; personal mail (consultation, #217).

## 8. Discussion and future work

- Faster waveforms when the channel allows, and choosing the waveform per slot from the probe.
- Optional, sparse feedback from listeners to steer the repeat policy, using daily reports instead of a return channel.
- Other bands and paths: 80 m evenings, QO-100.
- Limits: daylight NVIS only, one frequency, a single point of failure until Model B.

## 9. Conclusion

## Appendices

- A. On-air format summary, pointing to docs/design.md and the test vectors.
- B. Daily report format (MCR1).
- C. Data availability: the reports archive, research/138 and the code (AGPL-3.0).

## Data to gather before writing

- [ ] CT 150 survey results (per receiver and slot), before CT 150 is changed or retired.
- [ ] Head end slot history: what was sent each slot, frames and airtime. Check how long the journal and recent-slots.json keep it, and archive them like the reports.
- [ ] The survey charts Tom posted on Discord on 22 October (the images).
- [ ] Listener reports missing from 8 to 18 October: ask listeners for copies from their own BBS.
- [ ] SA6DAZ MS110D path-test results.
- [ ] Consent from each listener named in the paper.
