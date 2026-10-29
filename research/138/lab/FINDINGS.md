# Findings: what LinBPQ and LinFBB store, and what each forwarding hop changes

Lab: GB7LAA (LinBPQ 6.0.25.28) -> GB7LAF (LinFBB 7.0.11, F6FBB) -> GB7LAB (LinBPQ), all over AXUDP, run 2026-10-29. Setup and rerun steps are in `README.md`. 20 test bulletins (`bulletins/`) were posted once on LAA and once on LAF as an ordinary user, and the stored files were copied out after forwarding settled:

- `captured/b1f/` compressed B1F forwarding (what FBB and BPQ negotiate by default; SIDs `[FBB-7.0.11-AB1FHM$]` and `[BPQ-6.0.25.28-B12FWIHJM$]`)
- `captured/ascii/` plain MBL ASCII forwarding (BPQ partner records told not to use compression; FBB then offers `[FBB-7.0.11-AHM$]`)
- each has `laa/`, `laf/`, `lab/` (stores), `transcripts/` (the raw posting sessions), `analysis.txt` (per-bulletin dump of every copy) and `matrix.md` (copy-by-copy comparison)

Routes: LAF -> LAB is LAF dialling LAB. LAA -> LAF is LAF dialling LAA with reverse forwarding (`R` in FBB's forward.sys): LinBPQ, when it dials FBB, waits for a `>` prompt that FBB never sends to a BBS it hasn't flagged, so that direction stalls. The bytes on disk do not depend on who dialled. Forwarding is AXUDP throughout (telnet forwarding was not needed).

Where files live: LinBPQ `Mail/m_NNNNNN.mes` is the message text only; the header (BID, from, to, title, route) is a fixed 308-byte record in `DIRMES.SYS`. LinFBB `mail/mailN/m_NNNNNN.mes` (N = number mod 10) is the message text only; its header is a 222-byte record in `dirmes.sys`. `scripts/analyse.py` decodes both.

## Q1. Where do R: lines and path lines live, and what does forwarding add or remove?

| Item | LinBPQ | LinFBB 7.0.11 | Evidence |
| --- | --- | --- | --- |
| R: line storage | Inside the body file, as the first lines, before a blank line. Never in `DIRMES.SYS`. | Same: first lines of the body file. No separate path field in `dirmes.sys`. | `captured/b1f/laf/mail/mail1/m_000101.mes` starts `R:261029/2111Z 2@GB7LAA-1.#99.GBR.EURO LinBPQ6.0.25` then CRLF CRLF; `captured/b1f/lab/Mail/m_000001.mes` has FBB's line on top of BPQ's |
| Origin copy | No R: line at all (`laa/Mail/m_000002.mes` is just the text). | No R: line at all (`laf/mail/mail2/m_000102.mes`). | hexdump below |
| Who adds the line | The sender, as it sends: `R:yymmdd/hhmmZ N@CALL.HROUTE LinBPQ6.0.25`, then a blank line. The receiver stores what it got and adds nothing of its own. | The sender, as it sends: `R:yymmdd/hhmmZ @:CALL.HROUTE #:N [City] $:BID` (`$:` is the BID). If the message already has R: lines, FBB adds only its line on top (no extra blank line). | `lzhuf.c make_header()` in the LinFBB source; matrix.md shows +55 B (BPQ line) / +67 B (FBB line) per hop |
| Time in the line | The time it was forwarded, so it differs per hop and per copy. | Same. | `2111Z` vs `2112Z` in the hexdumps |
| Path:/Mid: style headers | None seen. | None seen. Not exercised: FBB's SID has no `2`, so no B2F was negotiated; B2F headers (`Mid:`, `Path:` style) belong to LinBPQ-to-Winlink-type sessions and are not covered here. | SIDs in `captured/*/laa/logs/` |
| R: lines typed into the body | A body that starts with `R:` lines is indistinguishable from routing lines afterwards (matrix.md t17: stripping "leading R: lines" eats them). A mid-body `R:` line is left alone. | FBB parses a leading `R:` block typed by the user as routing: it warned "...is incorrect." at post time and rewrote the BID in its own R: line to `$:F`, and the header BID became `F`. Mid-body R: untouched. | `captured/b1f/transcripts/laf-t17_r_lines.raw`; `captured/b1f/analysis.txt` section t17_r_lines |

Hexdump excerpts (b1f, bulletin t01, posted on LAA):

```
laa/Mail/m_000002.mes             50 6c 61 69 6e ... "Plain bulletin.\r\nSecond line.\r\nThird line.\r\n"   (44 bytes, no R: line)
laf/mail/mail1/m_000101.mes       "R:261029/2111Z 2@GB7LAA-1.#99.GBR.EURO LinBPQ6.0.25\r\n\r\nPlain bulletin.\r\n..." (99 bytes)
lab/Mail/m_000001.mes             "R:261029/2112Z @:GB7LAF.#99.GBR.EURO #:101 [Labtown] $:2_GB7LAA-1\r\nR:261029/2111Z 2@GB7LAA-1.#99.GBR.EURO LinBPQ6.0.25\r\n\r\nPlain bulletin.\r\n..." (166 bytes)
```

Canonical rule that follows: drop leading `R:` lines and the one blank line after them; but a user-typed leading `R:` block cannot be told apart, so a canonical form should drop only what it can prove was added (match the exact BPQ/FBB patterns above) or accept that such bodies are rare.

## Q2. Does FBB store `>From`, or only display it?

| Test | Result | Evidence |
| --- | --- | --- |
| Body line starting `From ` (t02: `From the desk...`, `From nobody Thu Oct 29...`, indented, lowercase) | FBB neither stores nor displays an escape: the file holds `From the desk of the lab` unchanged, and reading it back (`R 102` over AX.25) shows it unchanged. LinBPQ the same. Same through every hop, B1F and ASCII. | `captured/b1f/laf/mail/mail2/m_000102.mes`; `analysis.txt` t02_from_line; the FBB source has no `>From` string at all (`grep '>From' src/*.c`: nothing) |
| Body line already `>From` (t19) | Kept verbatim by both, not doubled. | `analysis.txt` t19_gt_from_and_misc |

So the `>From` seen in GB7CIP's ARLK079 copy was not made by LinFBB 7.0.11 on display or on storage; it was already in the text it received (or added by another program such as a mail gateway or an older FBB build). That makes it ordinary content, not a per-BBS artifact.

## Q3. Does FBB add the `From: CALL@BBS` / `To  : X@WW` body lines on forwarding?

Yes, for messages that originated on that FBB, on every forward, in both protocols.

| Case | B1F | ASCII | Evidence |
| --- | --- | --- | --- |
| Message posted on FBB (LAF), forwarded to LAB | Blank line, then `From: G0LAB@GB7LAF.#99.GBR.EURO`, `To  : LABTST@WW`, blank line, then the body, all after FBB's R: line. The copy on LAF itself does not have them. | Identical | `captured/b1f/lab/Mail/m_000002.mes` (hexdump: `...$:102_GB7LAF\r\n\r\nFrom: G0LAB@GB7LAF.#99.GBR.EURO\r\nTo  : LABTST@WW\r\n\r\nPlain...`); `captured/ascii/lab/Mail/m_000001.mes` |
| Message that arrived from another BBS (LAA via LAF) | Not added (FBB only adds its R: line). | Not added. | `captured/b1f/lab/Mail/m_000001.mes` |
| LinBPQ forwarding its own message | Never adds From/To lines. | Never. | `captured/*/laf/mail/*/m_0001*.mes` for LAA-origin |

Why: `entete_mess_fwd()` in `lzhuf.c` (compressed) is also called by `mbl_expo.c` (ASCII). If the message has no source BBS recorded (`ptmes->bbsf` is empty, ie it was entered locally) it appends `\rFrom: %s@%s\rTo  : %s@%s\r\r` using the sender, FBB's own H-route, the destination and the `@` field. So: only FBB-originated messages, only on the wire (never in FBB's own copy), and the downstream BBS stores them as body text. This is the origin of the 9 G8MNY bulletins in the corpus (they start at GB7CIP). A canonical form can safely strip exactly this pair when it directly follows the first R: line and blank line, because the text is deterministic: `From: <call>@<hroute of originating FBB>` then `To  : <to>@<at>`.

A body that itself starts with `From:` / `To  :` lines (t03) is stored verbatim by both and after FBB forwarding appears below FBB's own pair (two pairs), so the strip must remove only the first.

## Q4. Every other byte-level change

All figures from `captured/b1f/matrix.md` and `captured/ascii/matrix.md` ("same" = identical after removing the added R:/From/To prefix).

| Aspect | LinBPQ | LinFBB | Notes and evidence |
| --- | --- | --- | --- |
| Line endings on disk | CRLF | CRLF | User input arrived as CR (telnet Enter); CRLF in t14 and the CR in t01 both end up CRLF. A bare-LF post (t15) never completes through the telnet node (no CR, line never ends), so LF-only input could not be tested; B1F/ASCII forwarding preserved every stored CRLF. |
| Trailing spaces / tabs / space-only line (t04) | kept | kept | same at all three stores, both protocols |
| Blank first line, trailing blank lines (t05, t13) | kept | kept | same |
| Long lines 79, 80, 100, 255, 256, 300, 600 (t06) | not wrapped | not wrapped | same; the lab's FBB R: line is under 79 so its 79-column wrapping rule (`lzhuf.c`) was not triggered |
| Titles | typed titles stored up to 59 chars (60 and 100 char titles became 59); a 60-char title received from FBB over B1F was kept at 60; over ASCII it became 59 | stored up to 60 chars (100 became 60) | `captured/b1f/matrix.md` t08/t09 "title" column (LAA 59, LAF 60); `captured/ascii/matrix.md` shows `TITLE 59` on LAB. 40-char title untouched everywhere |
| 8-bit Latin-1 body and title (t10) | bytes preserved | bytes preserved | `caf\xe9 title \xa3` identical in all three `DIRMES`/`dirmes` records; body identical |
| UTF-8 body and title (t11) | preserved | preserved | same |
| Control chars BEL, ESC, FF (t20) | preserved | preserved | same |
| Line that is just `.` and `..` (t16) | preserved | preserved | same |
| `~`, `#`, `>From` lines (t19) | preserved | preserved | B1F same; see ASCII row below for the `/EX` line |
| Very long bulletin, 800 lines, 53 kB (t18) | preserved | preserved (LAF-origin copy in the b1f run lost 33 lines at input time: harness character loss on the AX.25 path, not forwarding; the ascii run's LAF copy is complete and identical downstream) | `captured/*/matrix.md` |
| Body ending without final newline (t12) | cannot be created: Ctrl-Z was not honoured by BPQ's telnet input in this lab (the next command line became part of the body), so the LAA copy is not a valid test | FBB accepted Ctrl-Z and stored a final CRLF | both always end bodies with CRLF; an unterminated last line cannot arise from user entry |
| Body line starting `/EX` | `/EX-ish line` inside a body is kept | In ASCII forwarding FBB ends the message at that line: LAF's copy of LAA's t19 is 37 bytes, the rest dropped. In B1F it is kept. Typed directly to FBB, a body line starting `/EX` also ends the message (t19 posted on LAF broke into commands). | `captured/ascii/matrix.md` t19 (72 vs 37 bytes); `captured/b1f/transcripts/laf-t19_gt_from_and_misc.raw` |
| ASCII forwarding BPQ -> FBB | | FBB's copy gets one extra empty line (CRLF) at the end of every body, which then propagates to LAB | `captured/ascii/matrix.md`: every LAA-origin row differs by exactly +2 bytes; `captured/ascii/laf/mail/mail0/m_000120.mes` ends `...Third line.\r\n\r\n`. B1F has no such change |
| ASCII forwarding FBB -> BPQ | | no change to the body beyond what B1F does | LAF-origin rows "same" |
| Header store | BID, from, to, title, `via` (`WW`), received-from BBS | same, `bbsfrom` set on received messages | `scripts/analyse.py` |
| BID | users cannot choose it; BPQ makes `N_CALL-1`, FBB `N_CALL` | same | |

## Compressed (B1F) versus ASCII, summary

- Both BBSes forward the same text bytes in B1F with no extra change. ASCII adds: BPQ -> FBB trailing blank line; FBB cuts at any body line starting `/EX`; BPQ's `SendCTRLZ` must be 0 or FBB hangs waiting for `/EX` (the session stalled for 13 minutes before this was found); titles over 59 chars lose one char at the BPQ end.
- LinBPQ's partner record needs `SendCTRLZ = 0` for FBB in ASCII mode; compressed mode is unaffected.
- Practical canonical-form consequence: copies compared across hops are equal after (a) dropping leading `R:` lines plus the blank after them, (b) dropping FBB's `From:`/`To  :` pair plus blank when present, (c) comparing titles truncated to 59 chars, (d) tolerating one trailing empty line if an ASCII BPQ -> FBB hop exists, (e) treating a body cut at a `/EX` line as a different (damaged) copy.

## Lab quirks worth knowing (not about the BBSes' storage)

- LinBPQ's telnet input drops the first character of lines that arrive in bursts (seen at 20 ms between lines; fine at 300 ms). `post.py` paces input.
- LinBPQ refuses `<` and `$BID` from non-BBS users, so BIDs are assigned by each BBS.
- FBB's stock `reject.sys` holds every bulletin entered by a user ("message accepted and held for sysop revision"); the lab ships an empty `reject.sys`. `bbs.sys` must list the forwarding partners or FBB asks them for their first name like a new user.
- FBB's telnet port (6300) refuses callsigns it does not know; the lab reaches FBB from the LAA node with `C 2 GB7LAF-1`.

## Other BBS software worth adding later

Source and binary locations marked (checked) responded here; the rest are from memory and need confirming before use.

| Software | Notes | Where |
| --- | --- | --- |
| JNOS 2.0 (VE4KLM, many forks) | TCP/IP NOS with a BBS (MBL-style and SMTP); still used for packet-over-IP and by some UK/EU sites; its `R:` line and mail handling differ from the above | source mirrors on GitHub, e.g. `Rhizomatica/jnos2`, `claytondus/jnos2` (checked: both exist); Maiko's own site `langelaar.net/jnos2` was unreachable from here |
| XRouter PMS (G8PZT) | XRouter's mailbox; with `PMSTYPE=4` it acts as a full BBS. Adds `R:261029/0750Z @:Q0XR.#42.GBR.EURO [] #:2 XrLin505p` style lines plus an RFC 822 block (Date/From/To/Subject/Message-ID) on local mail; subjects cut at 60 chars; bulletins are hidden from listings but `R n` reads them; paging is 25 lines (`NP 0` turns it off). Not run in this lab. | image `ghcr.io/packethacking/xrouter:505p-amd64`, repo `packethacking/xrouter-container`; ready-made config in `M0LTE/packet-mail` `test-bbs/xrouter/` (`run.sh bbs`); notes in that repo's `docs/xrouter-protocol.md` |
| BPQ32 on Windows | same mail code as LinBPQ (so same storage), but worth confirming the Windows build's line handling | `cantab.net/users/john.wiseman/Documents/` (checked), source `g8bpq/linbpq` (checked) |
| WinFBB / DosFBB / LinFBB | the F6FBB family; many UK sites run it (GB7CIP). The lab already runs LinFBB 7.0.11; older FBB builds (5.x, 7.0.x variants on other OSes) may differ | `f6fbb.org` (checked), `sourceforge.net/projects/linfbb` (checked) |
| OpenBCM | Linux BBS popular in German-speaking Europe, MBL/FBB compatible | not verified here |
| MSYS | DOS/Windows/Linux BBS with its own storage, used mainly in Germany and parts of Europe | `msys.de` returned 403 here; not verified |
| DieBox, TheNet X1J PMS, PMS in TNCs | small or embedded mailboxes; low value for bulletin flooding | not verified here |
