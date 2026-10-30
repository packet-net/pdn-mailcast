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

# Extension 2026-10-30: XRouter, JNOS and openBCM

Same 20 bulletins (`bulletins/`), same posting scripts and pacing, same rule of one origin on each side: every extra BBS sits between two LinBPQ nodes (GB7LAA -> X -> GB7LAB, all over AXUDP in docker on hyperv-gha) and each BBS is also an origin (posted as G0LAB). Each lab has its own directory with setup, scripts and captures: `xrouter/`, `jnos/`, `openbcm/` (each `captured/b1f` and `captured/ascii`, plus `analyse.py` and `compare_posted.py`). Each run's matrix is produced with `python3 -I <dir>/analyse.py <dir>/captured/<variant> --matrix`; `compare_posted.py` compares the posted body with what the origin BBS stored and what GB7LAB received.

Common caveats:

- B1F means "whatever compressed protocol the pair negotiated". XRouter's SID is `[XR-505p-BFHM$]` (compressed FBB blocks, no `1` or `2` so neither B1 nor B2); JNOS offers `[JNOS-2.0q.1-B1FHIM$]` (it downgrades LinBPQ's B2F offer to B1F); openBCM sends `[OpenBCM-1.13-38-g65b7-B1FHMW$]`. ASCII means LinBPQ's partner records were told not to compress, as in the first lab.
- The 53 kB bulletin (t18) was left out of the three ASCII runs: in plain ASCII forwarding at these link settings XRouter never completes it (the link is dropped and restarted; GB7LAA -> XRouter did complete) and the other two would take over an hour.
- t12 (no final newline) and t15 (LF-only body) are not valid tests through the LinBPQ telnet node, as in the first lab; they are only informative where the BBS was posted to directly.
- Times in all captures are lab clock (container UTC).

## XRouter 505p PMS (`PMSTYPE=4`, image `ghcr.io/packethacking/xrouter:505p-amd64`)

Lab: LinBPQ GB7LAA -> XRouter GB7LAX (PMSCALL GB7LAX-2) -> LinBPQ GB7LAB. XRouter-origin bulletins were posted over its telnet port as G0LAB with the `BBS` command and `NP 0`. XRouter dials GB7LAB from `PMS/FWD.SYS` (`C 3 GB7LAB-1`), GB7LAA dials XRouter. Only XRouter's documented config was needed (`xrouter/x/`: `XROUTER.CFG`, `PMS/DISTRIB.SYS`, `PMS/FWD.SYS`, `PMS/PMS.CFG`); PMSTYPE 4 relays (mail from one BBS to another), which PMSTYPE 1 does not.

Where files live: one text file per message, `PMS/NNNNN`. Line 1 is the header (`1 BF G0LAB 1793321285 LABTST@WW 2_GB7LAA-1 &:GB7LAA`: number, type+status, sender, epoch, to@at, BID, source BBS), line 2 the title, then the body, with LF line ends. The forward queue for a partner is `PMS/GB7LAB.Q`.

| Question | Answer | Evidence |
| --- | --- | --- |
| Q1: where do R: lines live, what does it add? | In the body file, first lines. XRouter adds its own line ON TOP when it stores the message, relayed or its own: `R:yymmdd/hhmmZ @:CALL.HR [] #:N XrLin505p`. There is no `$:BID` and the `[]` is an empty city. Existing R: lines are kept below it and the one blank line after the block is kept. Its own copy therefore already has the line (unlike LinBPQ and LinFBB, which add theirs only as they send). A user-typed leading R: block is kept verbatim below (t17). | `xrouter/captured/b1f/lax/PMS/00001` (LAA-origin) starts `R:261030/0048Z @:GB7LAX.#99.GBR.EURO [] #:1 XrLin505p` then LinBPQ's line, then a blank line |
| Q2: `>From` escaping? | None, stored or sent, in any direction or protocol. t02 (`From ` lines) and t19 (`>From` line) arrive byte-identical after the routing prefix. | `xrouter/compare_posted.py` output for b1f and ascii; `analyse.py --matrix` |
| Q3: extra body header lines on forward | Own-origin bulletins only (not relayed ones). Right after the R: line, with no blank line between, XRouter writes an RFC 822 style block: `Date: Thu, 29 Oct 2026 23:58:25 +0000`, `From: g0lab@gb7lax.#99.gbr.euro <g0lab@gb7lax-2.#99.gbr.euro>`, `To: labtst@ww`, `Subject: <title>`, `Message-ID: <BID>`, then a blank line, then the body. The addresses are lower-cased. It is written when the message is entered, so XRouter's own copy has it, and it goes on the wire. The Message-ID is the BID (`AE3DDA002LAX`, assigned by XRouter; users cannot choose). | `xrouter/captured/b1f/lab/Mail/m_000002.mes`; `docs/xrouter-protocol.md` in M0LTE/packet-mail describes the same block |
| Q4: other byte-level changes | See the list below. | `compare_posted.py`, `analyse.py` |

Q4 detail for XRouter:

- Line endings: accepts CR, CRLF or LF from the user (t14, t15: a CRLF is one line break, not two), stores LF, and sends bare CR between lines in the compressed protocol. GB7LAB therefore stores CR-only lines for XRouter-origin and relayed bulletins in the B1F run, and CRLF in the ASCII run (the ASCII receive path in LinBPQ ends lines with CRLF). Any canonical form must treat CR, LF and CRLF alike.
- Trailing spaces and tabs, a leading blank line, trailing blank lines, 600-character lines: all kept (t04, t05, t06, t13). No wrapping.
- Titles: stored up to 60 characters (t09: 100 cut to 60, t08: 60 kept). GB7LAB (LinBPQ) then keeps 60 over B1F and 59 over ASCII. A 59-character title from LinBPQ passes unchanged.
- Charset: Latin-1, UTF-8, BEL, ESC and FF bytes preserved (t10, t11, t20). Lines `.` and `..` preserved (t16).
- End of message: a line that is exactly `/EX` ends entry, so t19's `/EX-ish line: /EXIT` is kept. Ctrl-Z ends entry only if followed by CR (t12 with a bare Ctrl-Z never ended; with Ctrl-Z CR the body gets a final newline).
- ASCII, LinBPQ -> XRouter: XRouter's stored body gains one extra empty line at the end of every bulletin (same effect as LinBPQ -> LinFBB ASCII) and that propagates to GB7LAB. XRouter -> GB7LAB in ASCII sends `SB LABTST @ WW < G0LAB $BID`, then `OK`, the title, the body, and `/EX`.
- Files: a 53 kB bulletin (t18) is fine over the compressed protocol in both directions, but XRouter -> GB7LAB over ASCII never completes (see caveats).
- Relayed bulletins are otherwise byte-identical after removing the added prefix, in both protocols (t01 to t20 except t17, whose "difference" is only that stripping all leading R: lines also strips the ones the user typed).

## JNOS 2.0q (VE4KLM, `Rhizomatica/jnos2`, built in `jnos/Dockerfile`)

Lab: LinBPQ GB7LAA <- JNOS GB7LAJ -> LinBPQ GB7LAB. JNOS only does AXUDP through its own IP stack, so the container gives it a tun interface and a small UDP relay (`jnos/j/relay.py`) moves the datagrams to the real peers. JNOS dials both partners: `mbox ki Gb7laa` polls LinBPQ (reverse forward, so JNOS receives what GB7LAA holds) and `mbox ki Gb7lab` sends. LinBPQ dialling JNOS did not work: JNOS's AX.25 listener only starts the mailbox after the caller sends a first line, and LinBPQ with a `C 2 GB7LAJ|BBS` script gets JNOS's SID and prompt but never sends its own SID. Bulletins are routed with a one-line `spool/rewrite` (`*@ww ww`) and area `ww` in `spool/forward.bbs`. JNOS deletes forwarded bulletins from the area file, so the run snapshots `spool/mail/ww.txt` just before JNOS forwards (`captured/<v>/laj-before`).

Where files live: one mbox-style file per area, `spool/mail/ww.txt` (`From user@host date` line, headers, blank line, body, LF line ends), with an index `ww.ind`.

| Question | Answer | Evidence |
| --- | --- | --- |
| Q1: where do R: lines live, what does it add? | In the body, as received. JNOS never adds an R: line of its own, relayed or own-origin: GB7LAB's copy of a LAA-origin bulletin has only LinBPQ's line. A relayed bulletin keeps the blank line after the R: block. | `jnos/captured/b1f/lab/Mail/m_000001.mes`: `R:261030/0048Z 2@GB7LAA-1.#99.GBR.EURO LinBPQ6.0.25`, blank, body |
| Q2: `>From` escaping? | YES. JNOS stores body lines that start with `From ` with a `>` in front (mbox "From " quoting) and forwards them that way, so GB7LAB receives `>From the desk of the lab`. It does not escape lines that are indented or lower case (`  From indented`, `from lower`), and it does not double an existing `>From` (t19). The change is therefore not reversible: after JNOS a bulletin cannot be told from one that always had `>From`. | `jnos/captured/b1f/laj-before/spool/mail/ww.txt`; `analyse.py t02_from_line`; `compare_posted.py` (escaped-From lines 2 at LAJ and 2 at LAB) |
| Q3: extra body header lines on forward | Not header lines, but a blank line: every body JNOS forwards ends with one extra empty line, and own-origin bulletins begin with one empty line (where the R: block would be). If the body already starts with R: lines (t17) or with a blank line (t05) the leading blank is not added. Not in JNOS's own stored copy of an own-origin bulletin (the area file has headers `Received:`, `Date:`, `Message-Id: <4@laj.ampr.org>`, `From:`, `To:`, `Subject:`, `X-BBS-Msg-Type: B`, `X-JNOS-User-Port`, but those never go on the wire). | `compare_posted.py` t01: "identical + one extra empty line at start and one at end" at LAB |
| Q4: other byte-level changes | See below. | |

Q4 detail for JNOS:

- Line endings: stores LF, sends CRLF. GB7LAB stores CRLF.
- LONG LINES ARE FOLDED AT 127 CHARACTERS: t06's 255, 256, 300 and 600 character lines become several lines of at most 127 (the 79, 80 and 100 character lines are untouched). This happens for LinBPQ-origin bulletins relayed through JNOS too, and is the only wrapping seen in any BBS so far. Not reversible.
- Trailing spaces, tabs, 8-bit Latin-1, UTF-8, BEL, ESC and FF: preserved. Lines `.` and `..` preserved (t16).
- Titles: not cut by JNOS (a 100 character title is held and sent in full); GB7LAB (LinBPQ) then keeps 60 over B1F and 59 over ASCII.
- t12 (Ctrl-Z not at the start of a line): JNOS does not end entry and stores the Ctrl-Z byte in the last line (`last line without newline\x1a`). Over ASCII the receiving LinBPQ treats that byte as end of message (GB7LAB's copy loses the byte).
- BIDs are `<base36 number>_<CALL>` (`3_GB7LAJ`) with the call taken from `mbox haddress`; users cannot choose them. No R: line carries a BID.
- Mailbox quirks that matter to a harness: the AX.25 listener needs a first line before it greets; users are asked to `REGISTER` and `Send(N=no)?` follows the end of message.
- ASCII: LinBPQ -> JNOS and JNOS -> LinBPQ both work and give the same bytes as B1F, with the same additions (no new ASCII-only change seen).

## openBCM 1.x (`oe5hpm/openBCM` at 65b7f71, 32-bit Linux build in `openbcm/Dockerfile`)

Lab: LinBPQ GB7LAA -> openBCM GB7LAM (`mycall GB7LAM-8`) -> LinBPQ GB7LAB. openBCM has its own layer 2 and speaks AXUDP directly (`init.l2`, `assign axip`); the forward path to GB7LAB is `GB7LAB - GB7LAB-1 1` (the trailing `1` is the AXUDP channel number). openBCM-origin bulletins were posted over AX.25 from the GB7LAA node (telnet needs a password unless the client is in `rhosts.bcm`). `boxaddress` had to end in `.EU` (`GB7LAM.#99.GBR.EU`), so its hierarchical address differs from the `.EURO` used elsewhere in the lab.

Where files live: one file per message, `info/<board>/<name>`: line 1 header (`LABTST < G0LAB ~GB7LAA @WW ... $2_GB7LAA-1 ...`), one or more forward-status lines (`GB7LAB*...`), the title, then the body with LF line ends. Bulletins entered for an unknown board land in `info/tmp/labtst/`.

| Question | Answer | Evidence |
| --- | --- | --- |
| Q1: where do R: lines live, what does it add? | In the body, first lines. openBCM puts its own line on top of everything it stores or originates: `R:yymmdd/hhmmz @:GB7LAM.#99.GBR.EU $:2_GB7LAA-1` for a relayed bulletin (lower-case `z`, with `$:BID`), and `R:yymmdd/hhmmz @:GB7LAM.#99.GBR.EU [OpenBCM] obcm1.13-38-g65b7` for its own (no BID). Existing R: lines and their blank line are kept below it. Its own copy has the line already. | `openbcm/captured/b1f/lam/info/tmp/labtst/*`; `analyse.py t01_plain` |
| Q2: `>From` escaping? | None (t02, t19 identical through B1F and ASCII). | `compare_posted.py`, `analyse.py --matrix` |
| Q3: extra body header lines on forward | Own-origin only: after the R: line openBCM writes `From: G0LAB @ GB7LAM.#99.GBR.EU`, `To:   LABTST @ WW` (three spaces after `To:`) and `X-Info: No login password` (present because the AX.25 user had no password; other logins would differ), then a blank line. Written when the message is entered, so openBCM's own copy has it, and it goes on the wire. Not added to relayed bulletins. | `openbcm/captured/b1f/lab/Mail/m_000002.mes`: `R:...\r\nFrom: G0LAB @ GB7LAM...\r\nTo:   LABTST @ WW\r\nX-Info: No login password\r\n\r\nPlain...` |
| Q4: other byte-level changes | See below. | |

Q4 detail for openBCM:

- Line endings: accepts CR/LF/CRLF from the user, stores LF, sends CRLF.
- Titles: stored up to 80 characters; for a longer one the rest is moved into the first line of the body (t09: 100 characters became an 80 character title plus a body line of the last 20 characters). GB7LAB then cuts the 80 character title to 60 (B1F) or 59 (ASCII). A 59 or 60 character title is untouched.
- Trailing spaces, tabs, leading and trailing blank lines, 600 character lines, Latin-1, UTF-8, BEL, ESC, FF, `.` lines: all preserved, nothing wrapped.
- End of message: `/EX`, `NNNN` or Ctrl-Z ends entry (from the manual and `send.cpp`; only `/EX` was exercised). With LinBPQ's ASCII forwarding, openBCM only recognises an exact upper-case `/EX` (source: `send.cpp`), and LinBPQ sends `/ex`, so GB7LAA -> openBCM over ASCII hangs at the end of the first message (log in `openbcm/captured/ascii-laa-hang/`). Compressed forwarding in both directions is fine. openBCM -> GB7LAB over ASCII works (`SB LABTST @ WW < G0LAB $UQ0GB7LAM001`, `OK`, title, body, `/EX`).
- BIDs: openBCM-origin `UQ0GB7LAM001` style (sequence in base 36); users cannot choose.
- t12 as posted by LinBPQ (the harness-broken `...newline\rB`) lost its last `B` line through openBCM; ignore.
- Forwarding triggered immediately on entry in the compressed run. In the ASCII run it stopped after the first 11 bulletins (a forward link attempt failed and the next try waited for the half-hourly forward cron), so the later ones were forwarded on the next cron run and captured after that.

# Combined comparison of all BBSes tested

LinBPQ 6.0.25.28 and LinFBB 7.0.11 are from the first run (sections above); XRouter, JNOS and openBCM from this one.

| Aspect | LinBPQ | LinFBB 7.0.11 | XRouter 505p (PMSTYPE 4) | JNOS 2.0q | openBCM 1.13 |
| --- | --- | --- | --- | --- | --- |
| Routing lines live in | body, first lines | body, first lines | body, first lines | body, first lines | body, first lines |
| Adds its own R: line | when sending, not in its own copy: `R:yymmdd/hhmmZ N@CALL.HR LinBPQ6.0.25` | when sending, not in its own copy: `R:yymmdd/hhmmZ @:CALL.HR #:N [City] $:BID` | when storing (so in its own copy too): `R:yymmdd/hhmmZ @:CALL.HR [] #:N XrLin505p`, no BID | never | when storing: `R:yymmdd/hhmmz @:CALL.HR $:BID` relayed, `... [OpenBCM] obcm1.13-...` own; lower-case `z` |
| Extra block on own-origin bulletins | none | blank, `From: call@hroute`, `To  : x@y`, blank, on the wire only | `Date:`/`From:`/`To:`/`Subject:`/`Message-ID:` then blank, stored at entry | one empty line before the body (wire only), one after | `From:`/`To:   `/`X-Info:` then blank, stored at entry |
| `>From` | not escaped | not escaped | not escaped | `From ` lines at column 1 become `>From `, stored and sent, not undone, `>From` not doubled | not escaped |
| Stored line ends / sent line ends | CRLF / CRLF | CRLF / CRLF | LF / bare CR (compressed), CRLF at LinBPQ after ASCII | LF / CRLF | LF / CRLF |
| Title limit when typed | 59 | 60 | 60 | none seen (100 kept) | 80, rest moves to the first body line |
| Title as received by LinBPQ | 59 | 60 kept over B1F, 59 over ASCII | 60 over B1F, 59 over ASCII | 60 over B1F, 59 over ASCII | 60 over B1F, 59 over ASCII |
| Trailing blank line added | no (ASCII to FBB: yes) | ASCII from LinBPQ: +1 empty line | ASCII from LinBPQ: +1 empty line | always +1 empty line at the end of forwarded bodies | no |
| Wraps long lines | no | no (79 col rule not triggered) | no | YES, at 127 | no |
| Trailing space, tabs, blank first/last lines | kept | kept | kept | kept (but extra blank lines above) | kept |
| 8-bit, UTF-8, BEL/ESC/FF | kept | kept | kept | kept | kept |
| Body line `/EX`-like | kept | cut at a body line starting `/EX` in ASCII | exact `/EX` ends entry; `/EX-ish` kept | kept | kept; ASCII receive wants exact upper-case `/EX` |
| Protocols seen | B1F, B2F offered, ASCII | B1F, ASCII | compressed FBB (`B`), ASCII | B1F (B2F offer downgraded), ASCII | B1F, ASCII |

Practical consequences for a Model B canonical form, beyond the earlier list: compare with CR, LF and CRLF treated as one line break; strip a leading block of R: lines and the blank after it; strip, only when directly after those, XRouter's `Date:`..`Message-ID:` block, openBCM's `From:`/`To:`/`X-Info:` block and FBB's `From:`/`To  :` block; tolerate one extra empty line at either end (JNOS, ASCII hops); treat `>From ` and `From ` at line start as equal; compare titles cut to 59; and treat a copy with lines folded at exactly 127 characters (JNOS) as a damaged copy that cannot be repaired.

## Other BBS software worth adding later

Source and binary locations marked (checked) responded here; the rest are from memory and need confirming before use.

| Software | Notes | Where |
| --- | --- | --- |
| JNOS 2.0 (VE4KLM, many forks) (DONE 2026-10-30, see above; built from `Rhizomatica/jnos2` 2.0q) | TCP/IP NOS with a BBS (MBL-style and SMTP); still used for packet-over-IP and by some UK/EU sites; its `R:` line and mail handling differ from the above | source mirrors on GitHub, e.g. `Rhizomatica/jnos2`, `claytondus/jnos2` (checked: both exist); Maiko's own site `langelaar.net/jnos2` was unreachable from here |
| XRouter PMS (G8PZT) (DONE 2026-10-30, see above) | XRouter's mailbox; with `PMSTYPE=4` it acts as a full BBS. Adds `R:261029/0750Z @:Q0XR.#42.GBR.EURO [] #:2 XrLin505p` style lines plus an RFC 822 block (Date/From/To/Subject/Message-ID) on local mail; subjects cut at 60 chars; bulletins are hidden from listings but `R n` reads them; paging is 25 lines (`NP 0` turns it off). Not run in this lab. | image `ghcr.io/packethacking/xrouter:505p-amd64`, repo `packethacking/xrouter-container`; ready-made config in `M0LTE/packet-mail` `test-bbs/xrouter/` (`run.sh bbs`); notes in that repo's `docs/xrouter-protocol.md` |
| BPQ32 on Windows | same mail code as LinBPQ (so same storage), but worth confirming the Windows build's line handling | `cantab.net/users/john.wiseman/Documents/` (checked), source `g8bpq/linbpq` (checked) |
| WinFBB / DosFBB / LinFBB | the F6FBB family; many UK sites run it (GB7CIP). The lab already runs LinFBB 7.0.11; older FBB builds (5.x, 7.0.x variants on other OSes) may differ | `f6fbb.org` (checked), `sourceforge.net/projects/linfbb` (checked) |
| OpenBCM (DONE 2026-10-30, see above) | Linux BBS popular in German-speaking Europe, MBL/FBB compatible | `oe5hpm/openBCM` (old-school 1.x port) builds with gcc-multilib; `andreaspeters/openBCM` is the 2.x port, not tried |
| MSYS | DOS/Windows/Linux BBS with its own storage, used mainly in Germany and parts of Europe | `msys.de` returned 403 here; not verified |
| DieBox, TheNet X1J PMS, PMS in TNCs | small or embedded mailboxes; low value for bulletin flooding | not verified here |
