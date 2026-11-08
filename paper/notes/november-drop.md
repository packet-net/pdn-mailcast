# The November reception drop: where each figure comes from

**Cause, found on 8 November 2026:** GB7RDG's GPS-disciplined 10 MHz reference had lost GPS lock but kept its output running, so the Flex 6500 still reported "external reference locked" while the transmitted signal wandered by about 1 Hz. Restarting the GPSDO and the Flex at about 10:50 UTC on 8 November cured it at once. Established by the reception recovery session from the 8 November slots below.

Section 6.3 of the paper. Figures marked "results.py" are recomputed from the repository by `paper/figures/results.py` (section "The November drop"). The rest come from records outside this repository, named here, and were reported by the sessions that read them on 7 and 8 November 2026.

| Figure in the paper | Source |
|---|---|
| Shares at a tone of 8 dB or more: 61% with 18 s bursts (122 receiver-slots), 24% with 31 s bursts (56) | results.py |
| GM4CXF on 2 November: 62% and 53% at 09:00 and 10:00, then 4 to 21% | results.py |
| WN3 shares before and after: GM4CXF 73% to 22%, GM8BPQ 20% to 3% | results.py |
| Listeners' median Doppler spread: 0.28 Hz (25 to 30 Oct), 0.91 Hz (2 Nov), 0.67 Hz (3 to 7 Nov) | results.py |
| Bursts running to their normal end: 79% (29 Oct to 1 Nov) against 27% (3 to 6 Nov) | MS110D shadow recorder on CT 153, per-burst comparisons in /var/lib/ms110d-shadow/slots/<UTC>/compare.md, decoded by the C# receiver and the C++ port; reported by the C++ port session |
| The equaliser losing the signal 3 to 5 s into November bursts; switching off the signal-absent exit recovers no frames | Re-decoding of 11 shadow slots (30 Oct, 3, 6, 7 Nov) with per-frame equaliser error; reported by the reception recovery session |
| Simulation: 27 s bursts decode as well as 13 s on the Poor channel, and at 2 to 3 Hz Doppler spread | Simulation with the transmit and receive code (M0LTE.Ms110d), reported by the reception recovery session |
| Transmit audio to the Flex steady through every long burst | Packet capture on GB7RDG's node, reported by the reception recovery session |
| M9PSY on CT 150: probe and burst Doppler agreeing before November, bursts reading higher after (2 Nov 0.8 against 0.35 Hz) | CT 150 receiver journal "channel:" lines; reported by the reception recovery session |
| 8 November 10:00 (18 s short-interleaver bursts, before the restart) and 11:00 and 13:00 (after): frames heard, tone SNR and Doppler spread per survey SDR (paper Table 6); probe Doppler 1.15 to 1.34 Hz at 10:00; shadow bursts ending normally 3 of 15 at 10:00, 9 of 9 at 11:00 | Web SDR survey on CT 150 (/var/lib/mailcast-survey/2026-11/), the MS110D shadow on CT 153, and the head end journal; reported by the reception recovery session |
| The 12:00 slot on 8 November lost to a separate fault (the Flex dropped its clients at 11:59; pdn-soundmodem restarted at 12:20) | Reported by the reception recovery session; not part of the reference fault |

Precisions from the reception recovery session: the equaliser error rose above 1 between about 3 and 15 s into most November bursts, against 0.03 to 0.2 throughout every burst on 30 October; in simulation (Watterson Poor geometry, 2 equal paths 2 ms apart, 10 to 13 dB, M0LTE.Ms110d 0.4.1) Doppler spreads of 1, 2 and 3 Hz decode 94 to 100% with no fall-off along the burst, while a 200 ppm transmit/receive clock skew does produce a length-dependent cliff; the transmit audio to the Flex had an 80 ms pre-fill then exactly 187.5 packets/s, no gap over 12 ms, through every 28 s burst on 7 November.

The space weather analysis (`space-weather.md`) uses a 9 dB threshold and gets 65% against 26%; the paper uses results.py's 8 dB figures. Both say the same thing.
