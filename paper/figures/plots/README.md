# Figures for the mailcast paper

Every file here is made by `paper/figures/plots.py` (needs matplotlib and numpy): run `python3 paper/figures/plots.py` from the repository root. Each figure comes as a PNG at 300 dpi and as an SVG. The data definitions are copied from `paper/figures/results.py`, so the numbers agree with `paper/figures/results.md`. The script prints a short list of checks at the end.

| # | File | Caption | Data |
|---|---|---|---|
| 1 | fig01-survey-snr-vs-distance | Share of frames heard by each survey SDR against its median tone SNR and against its distance from Reading: SNR ranks the SDRs almost perfectly (rank correlation 0.98), distance not at all (0.02). | data/web-sdr-survey-2026-10/slots.csv, frames sent per slot from the head end journal |
| 2 | fig02-survey-daily-heatmap | Daily share of frames heard by each survey SDR, 6 to 20 October: weak days on 6 and 11 October, and the nearer strong SDRs better in week 2. | survey slots.csv, head end journal |
| 3 | fig03-listeners-daily-share | Listeners' daily share of frames heard on their own equipment, 8 October to 7 November, with October and November shares: the November drop for GM4CXF, M0NVK, GM8BPQ and G7TAJ after the 2 November changes. | reports/data/slots.csv and reports.csv (web SDR days and no-audio days left out), head end journal |
| 4 | fig04-wn4-vs-wn3-frames-per-minute | Frames heard per minute of slot time on WN4 (1200 bps) and WN3 (600 bps) for every receiver that heard both: strong receivers get about twice as much through on WN4; for weak ones the two are close. | survey and report slots, slot start-to-ident times from the head end journal |
| 5 | fig05-channel-distributions | Distributions of virtual height, second mode's delay, delay spread and Doppler spread over 759 measured report slots, with median and quartiles. | reports/data/slots.csv |
| 6 | fig06-headend-per-day | Frames sent and slot time per day, stacked by waveform, with the strategy changes of 6 October, 15 October and 2 November; 5 October's test slots shown on their own. | head end journal ("done" and "ABORTED" lines) |
| 7 | fig07-bulletins-rebuilt-vs-share | Bulletins rebuilt by each survey SDR against its share of frames heard: SDRs hearing 13 to 24% of frames still rebuilt 80 to 131 of the 224 bulletins taken in before the survey's last slot. | survey slots.csv, head end journal (intake lines before 20 Oct 16:00) |
| 8 | fig08-system-diagram | How a bulletin travels: GB7RDG BBS, head end, pdn-soundmodem, Flex 6500, 40 m NVIS, listener's radio or web SDR, receiver, listener's BBS as Q0CAST; the daily report returns by packet mail to MCAST@GB7RDG. | diagram (docs/factsheet.md) |
| 9 | fig09-slot-anatomy | Anatomy of a slot: clear-channel wait of up to 2 min, 10 s tone, 1.5 s gap, 6.5 s probe, CW ident, data bursts (18 s; 31 s from 2 to 7 November), closing ident, about 7.8 min on air inside the 10 min limit. | diagram (docs/channel-probe.md, docs/factsheet.md, timeline) |
| 10 | fig10-fountain-coding | Fountain coding: a bulletin is compressed and cut into K source pieces; each slot sends new pieces, and a listener who hears any K+2 of them across several slots rebuilds it. | diagram (illustrative, K = 6) |
| 11 | fig11-mirror-model | Mirror model: 1F and 2F paths to a receiver 300 km away, and the 1F to 2F delay against virtual height for 100 to 800 km, with the measured median gap of 1.6 ms reading as about 260 km. | formula and table in docs/channel-probe.md, measured gap from reports/data/slots.csv |

Notes:

- "Slot time" (figures 4 and 6) runs from the slot's start to its closing ident, as in results.py, so it includes any wait for a clear channel. Figure 6 also counts cut-short slots up to the moment they stopped, and the 5 October test slots, so its total (30.4 h) is a little above results.md's 29.6 h for completed slots.
- Figure 6's "waveform not logged" is the 5 October test slots and the 6 October 09:00 slot (296 frames); all were WN4 at the time.
- Figure 3 shows own equipment only; M9TWM's own UberSDR counts as his equipment, as in results.py. EI7KX (web SDR only) does not appear.
