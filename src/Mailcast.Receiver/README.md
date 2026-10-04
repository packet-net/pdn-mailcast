# pdn-mailcast receiver

Once a day, around midday UK time, GB7RDG broadcasts its recent bulletins on 40 m. This program listens, rebuilds each bulletin from the pieces it hears, and hands it to your own BBS as if a forwarding partner had sent it. Your BBS throws away any bulletin it already has, so it is just one more way for mail to reach you.

You need a Linux box, LinBPQ (or Linux FBB) with its mail running, and one of:

- a web receiver: the receiver can listen through the Wessex web SDR (`wessex.zapto.org`) on its own, no radio needed;
- a radio on USB with its audio into a sound card, tuned to **7.0497 MHz** dial. The signal is centred on 7.0515 MHz, which puts it at 1800 Hz in your audio.

Nothing is ever transmitted.

## How it works

Each bulletin is cut into pieces and sent with some spare ones, so the receiver does not need to hear all of them: any set slightly larger than the bulletin rebuilds it, and pieces heard on different days add up. Pieces are kept on disk, so a restart loses nothing. A bulletin is offered to your BBS as soon as it is complete, using the same compressed forwarding (FBB B1F) that BBSes use with each other.

The slot opens with 30 seconds of steady tone. The receiver measures it and logs how far off frequency it is and the signal-to-noise ratio, which is a handy check on your dial and your antenna.

## Configuration

The config file is `/etc/pdn-mailcast/receiver.json`. There are only a few settings:

```json
{
  "audio": "ubersdr:wessex.zapto.org",
  "bbs": {
    "type": "linBpq",
    "host": "127.0.0.1",
    "port": 8011,
    "login": "Q0CAST",
    "password": "pick-one",
    "command": "BBS"
  },
  "web": { "port": 8073, "lan": false },
  "stateDirectory": "/var/lib/pdn-mailcast"
}
```

- `audio`: `ubersdr:wessex.zapto.org` for the web SDR, an ALSA device such as `plughw:CARD=Device,DEV=0` for your radio's sound card, or `wav:/path/to/file.wav` to decode a recording.
- `bbs`: where your BBS is and how the receiver logs in. See below.
- `web`: the status page. It has no password, so it only answers on this machine unless you set `lan` to true.
- `stateDirectory`: where the pieces, the rebuilt bulletins and the record of deliveries are kept.

To decode a recording once and deliver what it completes, run `pdn-mailcast-receiver --decode file.wav`.

## The receiver's login on your BBS

The receiver logs in as **Q0CAST**. It needs a login of its own:

- Not GB7RDG. If you already forward with GB7RDG, its real sessions and the receiver's would share one partner, and anything your BBS queued for GB7RDG could be offered to the receiver instead.
- Not your own callsign, which is your sysop login.
- A Q callsign is never issued to anyone, so Q0CAST cannot clash with a real station. It is never sent on the air. FBB only accepts logins shaped like a callsign, which rules out names like MCAST.

The receiver only ever sends. If your BBS does try to send it something, the receiver answers "later" so the BBS keeps it, and logs a warning.

### LinBPQ

Tested against LinBPQ 6.0.25.41.

1. In `bpq32.cfg`, in your Telnet port's `CONFIG` section, make sure there is an `FBBPORT` and add a user for the receiver:

   ```
    FBBPORT=8011
    USER=Q0CAST,pick-one,Q0CAST,,
   ```

   The receiver must use the FBBPORT, not the ordinary telnet port, because forwarding is binary. Leave the fourth field (the command run at login) empty: the receiver sends `BBS` itself. Restart LinBPQ.

2. In the mail configuration (the web page's Mail Mgmt, or BPQMail's configuration), add a user **Q0CAST** and tick **BBS**.

3. On Q0CAST's forwarding page, tick **Allow Blocked**, **Allow Compressed** and **Use B1 Protocol**. Leave the TO, AT and HR boxes empty, so nothing is ever queued for it. It does not need to be enabled for forwarding, because LinBPQ never has to call it.

4. Put the same password in the receiver's config, and `"port": 8011`.

LinBPQ holds bulletins older than its BID lifetime and maximum age. If you have set either very low, broadcast bulletins (carried for three days) may arrive held.

### Linux FBB

Not tested yet: this is from FBB 7.0.11's documentation and source.

1. In `port.sys`, add a telnet port (interface 9, address in hex, so `189C` is port 6300) and a TNC line for it with mode `T`, for example:

   ```
   2    9         189C               0
   ...
   2    8    2   0      250   2     1     10     00/60   TUWR  Telnet
   ```

2. Add the user Q0CAST (`EU Q0CAST`), give it the **B** (BBS) flag and the **M** flag (modem and telnet access), and set its password.

3. Don't add Q0CAST to `forward.sys`: FBB never needs to forward to it.

4. In the receiver's config use `"type": "fbb"`, FBB's host and port, and the password. The receiver logs in as `.Q0CAST`: the dot asks FBB for a binary session, without which FBB would mangle the compressed transfers.

## What it logs

Everything goes to the journal (`journalctl -u pdn-mailcast-receiver`), one plain line each: the audio source, the tone (`tone: 1801.3 Hz, +1.3 Hz from 1800 Hz, SNR 14.2 dB in 3 kHz, 30 s`), each bulletin as it completes, and what the BBS said about it. The record of deliveries is also kept in `deliveries.jsonl` in the state directory.

## Building from source

You need the .NET 10 SDK.

```
dotnet publish src/Mailcast.Receiver -c Release -o out
out/pdn-mailcast-receiver --config src/Mailcast.Receiver/receiver.example.json
```

The test suite includes one test that decodes a simulated slot into a real LinBPQ in docker. It is tagged `Category=Docker`: `dotnet test --filter Category=Docker` runs it, and `--filter "Category!=Docker"` leaves it out.
