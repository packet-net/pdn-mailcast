# Bulletin corpus for issue #138

How often do two BBSes hold byte-identical copies of the same bulletin, and when they don't, why? The answer decides whether Model B (#140) is worth building.

- `bpqread.py`: a read-only LinBPQ telnet client. It only sends BBS, LB, R n and B (plus a one-off name answer on first login), and reads credentials from `~/.config/g8bpq/login`, never from the repo.
- `fetch.py STATION HOST PORT`: lists bulletins (LB) and saves each one's read output to `corpus/<STATION>/msg-<n>.txt`, with the listing in `corpus/<STATION>/listing.txt`.
- `corpus/`: copies as each BBS's `R n` shows them over telnet, line ends normalised to LF. Bulletins only; no personal mail is read.

Stations so far: GB7NOT (nottm.g8bpq.net) and GB7BPQ (skig.g8bpq.net), John G8BPQ's nodes. GB7RDG and GB7CIP to follow.

Telnet output isn't a byte-exact copy of what the BBS stores. It shows what a reader sees, which is also what a head end reading the BBS would get.

## First result (2026-10-26)

GB7NOT (145 bulletins, from 6 Oct) and GB7RDG (90, from 19 Oct, it keeps about a week) share 15 BIDs; GB7NOT takes only some of GB7RDG's bulletins. GB7BPQ lists no bulletins to this account.

All 15 are identical once the R: routing lines are removed: 7 exactly, 8 after also removing the one blank line LinBPQ puts after the R: block when it forwards (the 8 are G8BPQ's MCAST reports, which start on GB7NOT and so carry no R: lines there). So the first canonical rule is: drop the R: lines and the blank line that follows them.

Headers as shown differ only by per-user display settings (GB7RDG's read account sees a `Body:` size line and the full route on `From:`), which are not part of the bulletin.

Caveat: every shared bulletin here took the same single hop between GB7RDG and GB7NOT. The real test is bulletins that reached two BBSes by independent routes (GB7CIP next).
