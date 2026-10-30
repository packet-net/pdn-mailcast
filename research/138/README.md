# Bulletin corpus for issue #138

How often do two BBSes hold byte-identical copies of the same bulletin, and when they don't, why? The answer decides whether Model B (#140) is worth building.

- `bpqread.py`: a read-only LinBPQ telnet client. It only sends BBS, LB, R n and B (plus a one-off name answer on first login), and reads credentials from `~/.config/g8bpq/login`, never from the repo.
- `fetch.py STATION HOST PORT`: lists bulletins (LB) and saves each one's read output to `corpus/<STATION>/msg-<n>.txt`, with the listing in `corpus/<STATION>/listing.txt`.
- `corpus/`: copies as each BBS's `R n` shows them over telnet, line ends normalised to LF. Bulletins only; no personal mail is read.

Stations so far: GB7NOT (nottm.g8bpq.net) and GB7BPQ (skig.g8bpq.net), John G8BPQ's nodes. GB7RDG and GB7CIP to follow.

Telnet output isn't a byte-exact copy of what the BBS stores. It shows what a reader sees, which is also what a head end reading the BBS would get.

## Result so far (2026-10-29)

Stations: GB7RDG (LinBPQ, keeps about a week), GB7NOT and GB7BPQ (LinBPQ, John G8BPQ's), GB7CIP (FBB, everything since 19 Oct). GB7BPQ lists no bulletins to this account. GB7CIP's copies mostly arrive by different routes from GB7RDG's (for example via VE3 rather than GB7BEX and GB7BED), so this is a test of independent copies, not just one hop.

112 BIDs are held by two or more stations:

| After the canonical rules below | BIDs |
| --- | --- |
| identical | 102 |
| differ only by `From:` and `To  :` lines that forwarding added to the body | 9 |
| genuinely different content | 1 |

Canonical rules that make the 102 identical:

1. Drop the routing lines: LinBPQ's `R:` lines (either `Z` or `z`) and the blank line after them; FBB's `Path:` line and its indented continuation lines.
2. Drop trailing spaces on every line, and blank lines at the very start and end of the body.
3. Ignore what each BBS adds when showing a message (header layout, `Body: size`, the full route on `From:`, FBB's "This message has been read N times"); these depend on the reader's settings, not the bulletin.

The nine (all G8MNY's technical bulletins, which start at GB7CIP): copies downstream of GB7CIP begin with `From: G8MNY@GB7CIP.#32.GBR.EURO` and `To  : IBM@WW`, which GB7CIP's own copy lacks. Forwarding added them, like R: lines. A canonical form could strip them, but only safely if we know exactly which software adds them and when.

The one (ARLK079, ARRL keplerian data): GB7CIP shows a body line beginning `From ` as `>From` (old mbox escaping, stored or only displayed is unknown), and GB7CIP's copy also has lines GB7RDG's lacks. One title also differs in trailing space or length (9242_GB7CIP).

So far this says Model B works for the large majority of bulletins with a few simple rules, and the exceptions are recognisable. Not yet known: how GB7CIP's FBB stores `>From` and the forwarded From/To lines, and how often copies arrive truncated or edited.

## First result (2026-10-26, GB7NOT and GB7RDG only)

GB7NOT (145 bulletins, from 6 Oct) and GB7RDG (90, from 19 Oct, it keeps about a week) share 15 BIDs; GB7NOT takes only some of GB7RDG's bulletins. GB7BPQ lists no bulletins to this account.

All 15 are identical once the R: routing lines are removed: 7 exactly, 8 after also removing the one blank line LinBPQ puts after the R: block when it forwards (the 8 are G8BPQ's MCAST reports, which start on GB7NOT and so carry no R: lines there). So the first canonical rule is: drop the R: lines and the blank line that follows them.

Headers as shown differ only by per-user display settings (GB7RDG's read account sees a `Body:` size line and the full route on `From:`), which are not part of the bulletin.

Caveat: every shared bulletin here took the same single hop between GB7RDG and GB7NOT. The real test is bulletins that reached two BBSes by independent routes (GB7CIP next).
