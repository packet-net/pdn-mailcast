# Two receivers on one machine

A second receiver beside the first: here one for 40 m through pdn-soundmodem, and one for a QO-100 station on a second sound card. One receiver cannot do both, because a sound card receiver hears only the dial its radio is on.

| File | Goes in |
|---|---|
| [`soundmodem.json`](soundmodem.json) | `/etc/pdn-soundmodem/soundmodem.json` |
| [`receiver.json`](receiver.json) | `/etc/pdn-mailcast/receiver.json`, the first receiver |
| [`receiver-qo100.json`](receiver-qo100.json) | `/etc/pdn-mailcast-qo100/receiver.json`, the second |
| [`stations-qo100.json`](stations-qo100.json) | `/etc/pdn-mailcast-qo100/stations.json` |
| [`pdn-mailcast-receiver-qo100.service`](pdn-mailcast-receiver-qo100.service) | `/etc/systemd/system/` |

The first receiver is the packaged one, unchanged, with whatever audio it already has. The second needs three things of its own, all in these files:

- its own config directory, with `stations.json` in it (see the [QO-100 receiver](../qo100-receiver-sound-card/) for why);
- its own `stateDirectory`, `/var/lib/pdn-mailcast-qo100`;
- its own `web.port`, 8131.

The BBS login can be the same in both; delivery was not run here.

## Set it up

```
sudo install -d -o root -g pdn-mailcast -m 0770 /etc/pdn-mailcast-qo100
sudo install -o root -g pdn-mailcast -m 0660 receiver-qo100.json /etc/pdn-mailcast-qo100/receiver.json
sudo install -o root -g pdn-mailcast -m 0640 stations-qo100.json /etc/pdn-mailcast-qo100/stations.json
sudo install -m 0644 pdn-mailcast-receiver-qo100.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now pdn-mailcast-receiver-qo100
```

The package has no template unit for a second receiver, so the unit file here is the packaged one with its three paths and its description changed. Its journal is `journalctl -u pdn-mailcast-receiver-qo100`.

## What was seen

Under systemd, from the packages, with both services started (each on a loopback sound card there, so the first was not on pdn-soundmodem's audio in that test):

- both active, with status pages answering on ports 8130 and 8131;
- separate state in `/var/lib/pdn-mailcast` and `/var/lib/pdn-mailcast-qo100`;
- different names on the network by themselves: the second adds `-p8131` to the first's.

On the bench, both running through one slot from a 40 m station: the first heard the tone at 1800.0 Hz and 17 of 17 frames, and completed every bulletin; the second, with nothing sent for it, opened its sound card, kept to its own station's slots, heard nothing and closed its window by itself.

## Not tested here

Both receivers with a signal to decode at once (here both were decoding in the same minutes, but only the first had anything to hear); delivery to a BBS from either.

Run it yourself: `tools/bench/bench.sh two-receivers-one-machine`.
