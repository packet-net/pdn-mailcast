# Data

Raw records kept for the Mailcast paper (see [paper/outline.md](../paper/outline.md)). Listeners' daily reports are in [reports/](../reports/README.md). Everything here is a copy of what the machines wrote, not edited, apart from file names and the layout. No passwords or keys: the copies were checked against every secret in the configs they came from.

## web-sdr-survey-2026-10

Who heard GB7RDG's hourly slots, 6 to 20 October 2026. Eight pdn-mailcast receivers on CT 150, each on a different public UberSDR (83 to 544 km from Reading), listening to every daylight slot with no BBS. A collector script added one row per receiver per slot. Copied from CT 150 on 7 November 2026.

- `slots.csv`: the main table, one row per slot per SDR: distance, bearing, tone SNR, frames heard, bulletins completed, receiver version, waveform. The columns are described in `collector-README.txt`.
- `daily-YYYY-MM-DD.txt`: the collector's hour-by-SDR table for each day.
- `collector-README.txt`: the survey's own notes, written when it started. It says the survey ends on 13 October; Tom extended it to 20 October on 11 October.
- `collector.py`: the collector script itself. `state.json` is its memory between runs.
- `channel/NAME.json`: each receiver's channel measurements (paths, delay and Doppler spread, virtual height). `m9psy.json` is the main receiver, which also kept running after the survey.
- `receiver-journal.txt`: the journal of every receiver on CT 150 from 5 October to 7 November, survey instances and the main one: tone, frames, slot summaries, bulletins completed.

The `waveform` column in `slots.csv` was worked out from the head end's turn rule at the time, not observed. The head end journal below says what each slot actually sent.

## headend-gb7rdg

What GB7RDG's head end (running as M0LTE) sent. Copied from the station on 7 November 2026.

- `headend-journal-to-2026-11-07.txt`: the head end's journal from its first slot on 5 October. Each slot's plan (frames, bulletins, bursts, waveform, estimated airtime), what went out, skips and why, the ionosonde and PSK Reporter readings, intake from the BBS, and the reports it saw listed.
- `recent-slots.json` and `last-slot.json`: the status records for the last few slots (since 6 November), in the form `GET /status` shows them.
- `reports.json`: whose daily reports the head end had seen listed on GB7RDG.
- `bulletins-held-2026-11-07/`: one file per bulletin in rotation or kept on 7 November: BID, title, size, when it was taken in, its first slot, and how many pieces have been sent (`NextEsi`). Bulletins that had already been retired are not here; the journal has them.
