# Listener reports

Every mailcast receiver can send a short daily report of what it heard: one line per slot with the frames decoded, the tone's signal to noise, the propagation verdicts and the measured radio path. Reports go as public bulletins to `MCAST@GB7RDG.#42.GBR.EURO` (see [receiver.md](../docs/receiver.md#sending-a-daily-report) for the format). GB7RDG only keeps bulletins for about a week, so this folder is the permanent record.

## What's here

- `daily/YYYY-MM-DD/CALL.txt`: one report per listener per day.
- `data/reports.csv`, `data/slots.csv`, `data/stations.csv`: the same reports as tables, one row per report, per slot and per station line. These are generated; don't edit them by hand.
- `import-gb7rdg.py`: copies new reports from GB7RDG's mail store.

## A report file

A few header lines we add, a blank line, then the report exactly as the BBS held it, routing (`R:`) lines and all. Line ends are normalised to LF; nothing else is changed.

```
Title: MCR GM8BPQ 2026-11-05
From: GM8BPQ@GB7BPQ.#76.GBR.EU
To: MCAST@GB7RDG.#42.GBR.EURO
Type: B (bulletin)
Bid: 63097FGM8BPQ
Source: GB7RDG's LinBPQ mail store, message 4697 (m_004697.mes), copied 2026-11-07
Check: stored size matches the BBS's index

R:261105/1630Z 1857@GB7BPQ.#76.GBR.EU LinBPQ6.0.26

MCR1 0.32.0 IO68vl sc 0/0 14:RIG14 0 0
10 W4 3 3 +19.9 IU+PU 4 1.5/-1 0.59 0.24 271 p
...
```

`Source` says where this copy came from. `Check` says whether its length was checked against the size the BBS recorded for it, which catches a copy that was cut short. `Agrees with` counts other copies we found with the same report lines.

## Where the first 124 came from

The archive was started on 2026-11-07, after many reports had already expired from GB7RDG. They were gathered from, best first:

| Source | Reports |
| --- | --- |
| GB7RDG's mail store, still on disk | 34 |
| GB7RDG and GB7NOT read over telnet on 29 and 30 October for issue #138 | 62 |
| GB7RDG's mail store as dumped or read in earlier work sessions | 13 |
| Pasted by M0LTE from his own BBS (M7TAW's via GB7PTR, and M0NVK's) | 15 |

84 of the 124 passed the length check; the rest came from a source that recorded no size. Where two sources held the same report, the report lines agreed every time.

Known gaps:

- 8 to 18 October is patchy: only what earlier sessions happened to read survived.
- Some reports never reached GB7RDG at all, because mail routing to it failed for some listeners. M7TAW's from 24 October and several of M0NVK's are here only because M0LTE pasted them from another BBS.
- The first two reports (8 October, G8BPQ and GM8BPQ) went as personal mail to M0LTE, before reports became public bulletins later that day.

Listeners can fill gaps from their own BBS, which keeps a copy of each report they sent.

## Adding reports

From GB7RDG (needs ssh and sudo on the node; read-only there):

```
reports/import-gb7rdg.py
dotnet run --project tools/Mailcast.ReportArchive -- reports
```

Run it at least every few days, or reports expire before they are copied. A report already in `daily/` is never overwritten.

From anywhere else (a listener's own BBS, an email): make `daily/YYYY-MM-DD/CALL.txt` with at least the `Title`, `From` and `Source` lines, a blank line, then the message text as the BBS showed it. Then regenerate the tables. The tool stops and names the file if it can't read a report.

## The tables

Columns follow the report format in [receiver.md](../docs/receiver.md#the-daily-reports-format). A blank cell means the report didn't say. Some reminders:

- `snr_db` and `offset_hz` are the 10 s calibration tone's, in 3 kHz. No tone heard means blank, not zero.
- `ionosonde` and `psk_reporter` are the propagation verdicts the receiver heard for that slot.
- The channel columns (`modes` to `channel_basis`) are only there when the receiver measured the path, from the sounding probe (`probe`) or the data bursts (`bursts`).
- `station` is only filled when a report covers more than one transmitting station.
- `source_kind` and `check_kind` let you leave out unverified copies.
