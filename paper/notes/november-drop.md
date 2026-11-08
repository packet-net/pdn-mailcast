# The November reception drop: where each figure comes from

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

The space weather analysis (`space-weather.md`) uses a 9 dB threshold and gets 65% against 26%; the paper uses results.py's 8 dB figures. Both say the same thing.
