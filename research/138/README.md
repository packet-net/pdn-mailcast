# Bulletin corpus for issue #138

How often do two BBSes hold byte-identical copies of the same bulletin, and when they don't, why? The answer decides whether Model B (#140) is worth building.

- `bpqread.py`: a read-only LinBPQ telnet client. It only sends BBS, LB, R n and B (plus a one-off name answer on first login), and reads credentials from `~/.config/g8bpq/login`, never from the repo.
- `fetch.py STATION HOST PORT`: lists bulletins (LB) and saves each one's read output to `corpus/<STATION>/msg-<n>.txt`, with the listing in `corpus/<STATION>/listing.txt`.
- `corpus/`: copies as each BBS's `R n` shows them over telnet, line ends normalised to LF. Bulletins only; no personal mail is read.

Stations so far: GB7NOT (nottm.g8bpq.net) and GB7BPQ (skig.g8bpq.net), John G8BPQ's nodes. GB7RDG and GB7CIP to follow.

Telnet output isn't a byte-exact copy of what the BBS stores. It shows what a reader sees, which is also what a head end reading the BBS would get.
