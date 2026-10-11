# Moving from pdn-soundmodem's built-in receiver

pdn-soundmodem used to have its own mailcast receiver, set up with a `mailcast` section in its config. That receiver is going. Its replacement is this receiver, pdn-mailcast-receiver, which listens through your pdn-soundmodem on the same machine. Your radio, your BBS and your settings all carry over. This page takes you across in about ten minutes.

If you have never used the built-in receiver, you don't need this page: see [Using it with pdn-soundmodem](receiver.md#using-it-with-pdn-soundmodem).

## What you need

- pdn-soundmodem 0.89.0 or later, and pdn-mailcast-receiver 0.9.0 or later. `--version` on either says which you have.
- Your pdn-soundmodem's config at hand: `/etc/pdn-soundmodem/soundmodem.json`.

## 1. Install the receiver

```
sudo apt update
sudo apt install pdn-soundmodem pdn-mailcast-receiver
```

If pdn-soundmodem came from the packet-net apt repository, that is all. Otherwise add the repository first, as [the receiver's README](../src/Mailcast.Receiver/README.md#1-install) shows.

## 2. Copy your settings across

In `/etc/pdn-mailcast/receiver.json`, set `"audio": "soundmodem"`. If pdn-soundmodem's page is not on port 8107 (a second instance, `pdn-soundmodem@NAME`, say), use `"soundmodem:PORT"` with its `waterfall` port.

Then copy these from pdn-soundmodem's `mailcast` section:

| In pdn-soundmodem's `mailcast` | In `receiver.json` | Notes |
|---|---|---|
| `bbs` | `bbs` | Copy it as it is. Same keys, same defaults, nothing changes on your BBS. |
| `sources` | `sources` | The same. |
| `dialKHz` | `dialKHz` | The same, but only 7051.8 to 7052.8 now. It only matters with `retune`. |
| `retune` | `retune` | The same. pdn-soundmodem still needs its `rig` section, because it tunes the radio. |
| `hooks` | `hooks` | The same keys and environment variables, but they now run as the `pdn-mailcast` user. A script that used pdn-soundmodem's ssh key needs a key of its own. |
| `stateDirectory` | leave it out | The receiver keeps its own. |

For example, this in pdn-soundmodem:

```json
"mailcast": { "bbs": { "password": "pick-one" }, "retune": true, "sources": ["GB7RDG", "M0LTE"] }
```

becomes this in `receiver.json`, alongside the rest of the file:

```json
"audio": "soundmodem",
"retune": true,
"sources": ["GB7RDG", "M0LTE"],
"bbs": { "type": "linBpq", "host": "127.0.0.1", "port": 8011, "login": "Q0CAST", "password": "pick-one", "command": "BBS" },
```

If your pdn-soundmodem warns at start-up that the built-in receiver is going, that warning lists the keys your own section sets.

## 3. Switch over

Do it between slots: in daylight, after :13 and before :58 past the hour.

**Without `retune`:**

1. Start the receiver: `sudo systemctl restart pdn-mailcast-receiver`. Leave the built-in running for now.
2. Once the receiver has heard a whole slot (its page shows it), remove the `mailcast` section from pdn-soundmodem's config.
3. Restart pdn-soundmodem: `sudo systemctl restart pdn-soundmodem`. The receiver reconnects by itself.

**With `retune`**, switch in one go, because only one of the two can borrow the radio for a slot:

1. Remove the `mailcast` section from pdn-soundmodem's config.
2. `sudo systemctl restart pdn-soundmodem`
3. `sudo systemctl restart pdn-mailcast-receiver`

## 4. Check it works

- The receiver's page, http://127.0.0.1:8130/, shows a live spectrogram, and from the next slot what was heard.
- pdn-soundmodem's page shows "listening: pdn-mailcast-receiver" at the top.
- pdn-soundmodem no longer warns at start-up about the built-in receiver, if your version did.

## Good to know

- **Nothing arrives twice.** The receiver may offer your BBS bulletins the built-in already delivered. LinBPQ answers "already have", and the receiver's page counts them as already had. With FBB this has not been tested yet.
- **Part-heard bulletins start again.** Nothing is carried over from the built-in, and M0LTE repeats every bulletin, so they complete in a later slot.
- **On a Flex** there is nothing to retune. While the receiver is connected, pdn-soundmodem opens a headless slice's receive filter to cover the signal. In attach mode, open SmartSDR's filter to cover 350 to 3250 Hz on the usual 7.052 MHz dial.
- **Your dial.** Without the `mailcast` section, pdn-soundmodem may run at 12 kHz instead of 48 kHz. Without `retune`, its dial then needs to be 7.04925 MHz or above. If it isn't, the receiver's log and page say so, and what to change. More in [Which dial](receiver.md#using-it-with-pdn-soundmodem).
- **Measure my filter** is on the receiver's settings page now.
