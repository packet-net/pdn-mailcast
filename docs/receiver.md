# The receiver: reference

Setup is in [src/Mailcast.Receiver/README.md](../src/Mailcast.Receiver/README.md). This page has the detail.

## Configuration

The config file is `/etc/pdn-mailcast/receiver.json`. There are only a few settings:

```json
{
  "audio": "ubersdr:wessex.zapto.org",
  "dialKHz": 7052.0,
  "bbs": {
    "type": "linBpq",
    "host": "127.0.0.1",
    "port": 8011,
    "login": "Q0CAST",
    "password": "pick-one",
    "command": "BBS"
  },
  "web": { "port": 8130, "lan": false, "password": "" },
  "slotUtc": "00:00",
  "everyMinutes": 60,
  "webSdrSlotsPerDay": 8,
  "stateDirectory": "/var/lib/pdn-mailcast"
}
```

- `audio`: `ubersdr:wessex.zapto.org` for the web SDR, an ALSA device such as `plughw:CARD=Device,DEV=0` for your radio's sound card, or `wav:/path/to/file.wav` to decode a recording.
- `dialKHz`: the USB dial in kHz, normally `7052.0` (7.052 MHz), which is also what you get if you leave it out. The web SDR is tuned there, and a radio on a sound card should be set there. The signal is centred 1800 Hz above the dial, on 7.0538 MHz. Only change it if the signal moves; anything from 1800 to 30000 kHz is accepted.
- `bbs`: where your BBS is and how the receiver logs in. See below.
- `web`: the status page. It only answers on this machine unless you set `lan` to true, and then it needs a `password`, which your browser asks for (any user name).
- `slotUtc` and `everyMinutes`: when GB7RDG's slots are, in UTC. One starts at `slotUtc` and then one every `everyMinutes`, round the clock. GB7RDG sends every hour on the hour, so `"00:00"` and `60`, which is also what you get if you leave them out. `everyMinutes` must divide a day (1440) and be at least 15. A config file from before hourly slots has only `slotUtc`; it is read as one slot a day at that time, and the log and the status page say so. A sound card listens all the time, whatever these say.
- `webSdrSlotsPerDay`: how many slots a day a web SDR listens to, normally 8. Public UberSDR receivers allow each address about three hours a day, so a web SDR can't listen every hour. It listens to this many slots, spread evenly through the day starting at `slotUtc` (8 of the 24 hourly slots is 00:00, 03:00 and so on to 21:00 UTC), from 2 minutes before each slot to 12 minutes after. That is 14 minutes a slot, so 12 is the most. The log and the status page say which slots it listens to.
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

LinBPQ holds bulletins older than its BID lifetime and maximum age. If you have set either very low, bulletins sent this way (carried for three days) may arrive held.

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

## The status page

http://127.0.0.1:8130/ shows:

- when to listen: the frequency, the slots, the next slot, and for a web SDR the next slot it will listen to;
- the last slot: how far off frequency the tone was, its signal-to-noise ratio, and how many frames were heard;
- your BBS: where bulletins go, how many are waiting, and any problem reaching it;
- a live spectrogram from 0 to 4 kHz, with the signal's edges, its centre at 1800 Hz and the tone marked, so you can see whether the signal sits where it should in your passband; pdn-soundmodem's full waterfall is a link away;
- the input level, with the same target as pdn-soundmodem: peaks between -18 and -9 dBFS;
- what to try if nothing is heard;
- the bulletins being sent, how many pieces of each have arrived, and what the BBS said about each;
- the settings: audio, and the BBS's address and login. The USB dial is shown too, but it is only changed in the config file. Saving writes them to the config file (without its comments) and puts them in force at once. If you change the BBS's address, port or type, enter its password again: the saved one is never sent anywhere new without you.

On this machine only, the page answers to `localhost` and nothing else. To reach it from your network, set `"lan": true` and a `"password"` in `web`; the browser asks for it (any user name). Use it on a network you trust: it is plain HTTP.

## What it logs

Everything goes to the journal (`journalctl -u pdn-mailcast-receiver`), one plain line each: the audio source, which slots a web SDR listens to, the tone (`tone: 1801.3 Hz, +1.3 Hz from 1800 Hz, SNR 14.2 dB in 3 kHz, 10 s`), each bulletin as it completes, and what the BBS said about it. The record of deliveries is also kept in `deliveries.jsonl` in the state directory.

## Building from source

You need the .NET 10 SDK.

```
dotnet publish src/Mailcast.Receiver -c Release -o out
out/pdn-mailcast-receiver --config src/Mailcast.Receiver/receiver.example.json
```

`scripts/build-receiver-deb.sh linux-x64 0.1.0` builds the .deb into `artifacts/`.

The test suite includes one test that decodes a simulated slot into a real LinBPQ in docker. It is tagged `Category=Docker`: `dotnet test --filter Category=Docker` runs it, and `--filter "Category!=Docker"` leaves it out.
