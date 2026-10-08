# pdn-mailcast receiver

Hears GB7RDG's experimental mailcasts on 40 m, sent every hour on the hour in daylight, and passes each bulletin to your LinBPQ, like a forwarding partner would. It never transmits, and you don't need a radio.

You need a Linux machine (a Pi is fine) running LinBPQ with its mail. It doesn't have to be on the same machine as LinBPQ.

## 1. Install

```
curl -fsSL https://packet-net.github.io/apt/pubkey.asc | sudo gpg --dearmor -o /usr/share/keyrings/packet-net.gpg
echo "deb [signed-by=/usr/share/keyrings/packet-net.gpg] https://packet-net.github.io/apt ./" | sudo tee /etc/apt/sources.list.d/packet-net.list
sudo apt update
sudo apt install pdn-mailcast-receiver
```

## 2. Add it to LinBPQ

1. Stop LinBPQ. In `bpq32.cfg`, in your Telnet port's `CONFIG` section, add these two lines (if you already have an `FBBPORT` line, keep yours and use its number in step 3):

   ```
    FBBPORT=8011
    USER=Q0CAST,choose-a-password,Q0CAST,,
   ```

   Start LinBPQ again.

2. In LinBPQ's web page, open **Mail Mgmt**:
   - under **Users**, add **Q0CAST** and tick **BBS** (makes it a BBS forwarding partner rather than an ordinary terminal user);
   - on Q0CAST's **Forwarding** page, tick **FBB Blocked** (forward in FBB's binary blocks, not line-by-line text), **Allow Binary** (LinBPQ's label for allowing compressed forwarding; blocked forwarding needs this too) and **Use B1 Protocol** (the simpler of FBB's two binary protocols). Leave the TO/AT/TIMES/Connect Script boxes, both HR Routes boxes, BBS HA, Enable Forwarding, Request Reverse and everything else unticked and empty: Q0CAST never needs LinBPQ to call it, because the receiver always calls in.

   Click **Update** on each page to save it.

3. Put the password in the receiver's config:

   ```
   sudo nano /etc/pdn-mailcast/receiver.json
   ```

   Set `"password"` to the one you chose. Change `"port"` too if your `FBBPORT` isn't 8011.

## 3. Start it

```
sudo systemctl start pdn-mailcast-receiver
```

Open http://127.0.0.1:8130/ on that machine to see what it hears. Bulletins appear in your BBS as they complete, and the page keeps a copy of each for 30 days, to read or send to the BBS again. A web SDR is only open around each daylight slot, to stay inside its allowance, so give it a few hours.

## Using your own radio instead

Set your radio to USB on **7.052 MHz** (the signal is centred on 7.0538 MHz), with its receive audio into a sound card. It then hears every slot, not just some. Find the card's name with `arecord -L`, then in `/etc/pdn-mailcast/receiver.json` change `"audio"` to it, for example `"plughw:CARD=Device,DEV=0"`. Restart the receiver and set the level so peaks sit between -18 and -9 dBFS on the status page.

## Linux FBB

Instead of step 2: add a telnet port in `port.sys`, add user **Q0CAST** (`EU Q0CAST`) with the **B** and **M** flags and a password, and don't add it to `forward.sys`. In the receiver's config set `"type": "fbb"` and FBB's telnet port. (Not tested yet.)

## More

The settings, the status page, the logs and building from source are in [docs/receiver.md](../../docs/receiver.md).
