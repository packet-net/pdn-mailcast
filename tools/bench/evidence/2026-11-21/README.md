# Bench runs of 2026-11-21

The output of each scenario as it ran, unedited: pdn-soundmodem 0.109.0, pdn-mailcast-headend 0.43.0 and pdn-mailcast-receiver 0.43.0 from the packet-net apt repository, Debian 13, in Docker on a 12 core x86 VM, three scenarios at a time. One directory per scenario, laid out as [the bench's README](../../README.md#output) says.

- `systemd-two-receivers.txt`: the two receiver services under systemd, set up with the commands on the two-receivers page; first without the second's `stations.json`, to show the refusal, then with it.
- `refusals.txt`: the output of `tools/bench/refusals.sh`.
- `filter-study/`: `tools/bench/filter-study.py` on one rendered slot of 45 frames: `wn4.log` is 1200 bps at 1.5 and 3 dB, `wn6.log` is 3200 bps at 5 and 6 dB. In these logs "modem at 1500 Hz" passes 600 to 3000 Hz of the signal as the receiver hears it, and "modem at 1800 Hz" passes 300 to 2700 Hz: the same 300 to 2700 Hz transmit filter, seen from where the receiver has the signal.

The bench's signing key is made when the image is built, so the public key in these logs is nobody's station key.
