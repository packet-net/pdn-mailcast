# Listener reports

Every mailcast receiver can send a short daily report of what it heard: one line per slot with the frames decoded, the tone's signal to noise, the propagation verdicts and the measured radio path. Reports go as public bulletins to `MCAST@GB7RDG.#42.GBR.EURO` (see [receiver.md](../docs/receiver.md#sending-a-daily-report) for the format). GB7RDG kept bulletins for only about a week until 7 November 2026 (180 days since), so this folder is the permanent record.

## What's here

- `daily/YYYY-MM-DD/CALL.txt`: one report per listener per day.
- `data/reports.csv`, `data/slots.csv`, `data/stations.csv`: the same reports as tables, one row per report, per slot and per station line. These are generated; don't edit them by hand.
- `import-gb7rdg.py`: copies new reports from GB7RDG's mail store.
- `sync-gb7rdg.sh` and `systemd/`: run the import on gb7rdg-node every 3 hours and push to GitHub.

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

New reports on GB7RDG arrive by themselves: see the next section. The workflow `reports.yml` regenerates the tables whenever reports land on main.

From anywhere else (a listener's own BBS, an email): make `daily/YYYY-MM-DD/CALL.txt` with at least the `Title`, `From` and `Source` lines, a blank line, then the message text as the BBS showed it, and push it to main. A report already in `daily/` is never overwritten by the import. To check a file before pushing, run `dotnet run --project tools/Mailcast.ReportArchive -- reports`: it names any report it can't read and leaves it out of the tables.

## Automatic copying from GB7RDG

`mailcast-report-archive.timer` on gb7rdg-node runs `sync-gb7rdg.sh` every 3 hours as `tf`. It pulls main, runs `import-gb7rdg.py local` (read-only on LinBPQ, through `sudo -n cat`), commits any new reports and pushes. It pushes with a deploy key that can write to this repository and nothing else.

Setup, once, on the node:

```
ssh-keygen -t ed25519 -N "" -C gb7rdg-node-reports -f ~/.ssh/pdn-mailcast-reports
# add ~/.ssh/pdn-mailcast-reports.pub as a deploy key with write access on packet-net/pdn-mailcast
printf 'Host github-reports\n  HostName github.com\n  User git\n  IdentityFile ~/.ssh/pdn-mailcast-reports\n  IdentitiesOnly yes\n' >> ~/.ssh/config
git clone --depth 1 --filter=blob:none --sparse git@github-reports:packet-net/pdn-mailcast.git ~/pdn-mailcast-reports
git -C ~/pdn-mailcast-reports sparse-checkout set reports
sudo cp ~/pdn-mailcast-reports/reports/systemd/mailcast-report-archive.* /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now mailcast-report-archive.timer
```

`journalctl -u mailcast-report-archive` shows each run.

## The tables

Columns follow the report format in [receiver.md](../docs/receiver.md#the-daily-reports-format). A blank cell means the report didn't say. Some reminders:

- `snr_db` and `offset_hz` are the 10 s calibration tone's, in 3 kHz. No tone heard means blank, not zero.
- `ionosonde` and `psk_reporter` are the propagation verdicts the receiver heard for that slot.
- The channel columns (`modes` to `channel_basis`) are only there when the receiver measured the path, from the sounding probe (`probe`) or the data bursts (`bursts`).
- `station` is only filled when a report covers more than one transmitting station.
- `source_kind` and `check_kind` let you leave out unverified copies.
