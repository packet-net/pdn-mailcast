# Forwarding lab for issue #138

Three BBSes on one box, forwarding LAA -> LAF -> LAB over AXUDP (AX.25 over UDP), so the bytes each one stores for the same bulletin can be compared.

| Name | Software | Call | Role |
| --- | --- | --- | --- |
| GB7LAA | LinBPQ 6.0.25.28 (`m0lte/linbpq:latest`, the M0LTE fork image) | GB7LAA-1 | origin 1; forwards to LAF |
| GB7LAF | LinFBB 7.0.11 (F6FBB, built from the SourceForge tarball) | GB7LAF-1 | origin 2; relay; forwards to LAB |
| GB7LAB | LinBPQ 6.0.25.28 | GB7LAB-1 | end of the chain |

Forwarding is AXUDP, not telnet: LinBPQ uses its BPQAXIP driver and FBB uses kernel AX.25 (`ax25ipd` -> `kissattach` -> `ax0`), the same layout as M0LTE/f6fbb-interop (reused for its build recipe; nothing was pushed to that repo and its QEMU guest was not needed, because the host kernel still has the `ax25` module).

## Requirements

A Linux host with docker compose, `sudo`, and the kernel `ax25` and `mkiss` modules (Ubuntu 7.0.x kernels still ship them; mainline dropped AX.25 in 7.1). The FBB container runs privileged on the host network because AF_AX25 only exists in the init network namespace. Do not run it on a machine that uses kernel AX.25 for something else (it creates `ax0`).

## Run

```
cd research/138/lab
scripts/run_all.sh b1f      # compressed (B1F) forwarding where the peers allow it
scripts/run_all.sh ascii    # BPQ partner records switched to plain MBL ASCII forwarding
python3 -I scripts/analyse.py captured/b1f   # per-bulletin comparison tables
```

`run_all.sh` rebuilds the lab from scratch (about 3 minutes, FBB creates its data files on first start), posts each bulletin in `bulletins/` once on GB7LAA and once on GB7LAF as G0LAB (an ordinary user, via the LAA node's telnet port; FBB is reached with `C 2 GB7LAF-1` from that session because its own telnet port refuses unregistered users), waits for forwarding to settle and copies the stores into `captured/<variant>/`.

## Layout

- `fbb/` Dockerfile (LinFBB + ve7fet AX.25 tools), entrypoint, and FBB config (`etc/`).
- `bpq/laa`, `bpq/lab` bpq32.cfg and linmail.cfg for the two LinBPQ nodes.
- `compose.yml` the three services (bridge 172.32.0.0/24; LAF on the host network).
- `scripts/bulletins.py` the test bulletins (written out to `bulletins/` as raw `.title`, `.body`, `.ending` files).
- `scripts/post.py`, `capture.sh`, `up.sh`, `run_all.sh`, `analyse.py`.
- `captured/<variant>/{laa,laf,lab}` the stored files; `captured/<variant>/transcripts` the raw posting sessions.

## Harness caveats

- LinBPQ's telnet server drops characters when lines arrive in a burst (seen at 20 ms between lines), so `post.py` paces body lines (0.3 s; 0.1 s for the long bulletin). Anything unexpected in a stored body is checked against the BBS's own `logs/` input log.
- Users cannot choose a BID: LinBPQ refuses `<` and `$BID` from non-BBS users, so both BBSes assign their own (`n_GB7LAA-1`, `n_GB7LAF`). Each bulletin is identified by its title (`lab <key>`).
- FBB's stock `reject.sys` holds every user-entered bulletin (`L B * * * * 0`); the lab ships an empty one. FBB's `bbs.sys` must list the forwarding partners or it asks them for name and QTH like new users.
