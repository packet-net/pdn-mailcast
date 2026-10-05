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
  "daylight": { "locator": "IO91lk", "afterSunriseMinutes": 120, "beforeSunsetMinutes": 30 },
  "webSdrSlotsPerDay": 8,
  "archive": { "days": 30, "maxMegabytes": 50 },
  "stateDirectory": "/var/lib/pdn-mailcast"
}
```

- `audio`: `ubersdr:wessex.zapto.org` for the web SDR, an ALSA device such as `plughw:CARD=Device,DEV=0` for your radio's sound card, or `wav:/path/to/file.wav` to decode a recording.
- `dialKHz`: the USB dial in kHz, normally `7052.0` (7.052 MHz), which is also what you get if you leave it out. The web SDR is tuned there, and a radio on a sound card should be set there. The signal is centred 1800 Hz above the dial, on 7.0538 MHz. Only change it if the signal moves; anything from 1800 to 30000 kHz is accepted.
- `bbs`: where your BBS is and how the receiver logs in. See below.
- `web`: the status page. It only answers on this machine unless you set `lan` to true, and then it needs a `password`, which your browser asks for (any user name).
- `slotUtc` and `everyMinutes`: when GB7RDG's slots are, in UTC. One starts at `slotUtc` and then one every `everyMinutes`, round the clock, but only those in daylight run (see `daylight`). GB7RDG sends every hour on the hour, so `"00:00"` and `60`, which is also what you get if you leave them out. `everyMinutes` must divide a day (1440) and be at least 15. A config file from before hourly slots has only `slotUtc` (`"12:00"`); it is read as every 60 minutes from that time, which is the same hourly slots, so nothing needs changing. A sound card listens all the time, whatever these say. The receiver also uses the slots to make sense of what it hears: each frame counts for the slot whose start most recently passed, and a tone only counts as a slot's opening tone if it starts within 5 minutes of a slot's start, so someone tuning up near 7.0538 MHz isn't taken for GB7RDG.
- `daylight`: GB7RDG only sends in daylight, because 40 m does not reach UK stations at night. A slot runs only if it starts between `afterSunriseMinutes` after sunrise and `beforeSunsetMinutes` before sunset at `locator` (a 4 or 6 character Maidenhead locator). The receiver works out sunrise and sunset itself, from the date, in UTC, so it needs no internet and summer time makes no difference. Left out, it is GB7RDG's own: IO91lk, 120 and 30, which today (5 October) is 09:00 to 17:00 UTC, in midwinter 11:00 to 15:00 and in midsummer 06:00 to 19:00. `"daylight": null` means every slot. You should not need to change it: once the receiver has heard GB7RDG's directory, which gives GB7RDG's own slots and daylight hours, it uses those instead of these settings, and says so in the log and on the status page. A sound card listens all the time anyway; the daylight hours only matter for a web SDR and for the "next slot" on the page.
- `webSdrSlotsPerDay`: how many slots a day a web SDR listens to, normally 8. Public UberSDR receivers allow each address about three hours a day, so a web SDR can't listen every hour. It listens to this many of the day's daylight slots, spread evenly starting with the first (8 of the 9 on 5 October is 09:00 to 16:00 UTC; in midwinter there are only 5, so it hears them all), from 2 minutes before each slot to 12 minutes after. That is 14 minutes a slot, so 12 is the most. The log says which slots it listens to each day, and the status page shows them.
- `archive`: how long the receiver keeps its own copy of each bulletin after your BBS has answered for it (see [Mail](#mail)). `days` is 30 and `maxMegabytes` 50 if you leave them out; the oldest copies go first once either is passed. `0` for either keeps no copies. Waiting bulletins are never removed, however old.
- `stateDirectory`: where the pieces, the rebuilt bulletins, the copies kept and the record of deliveries are kept.

To decode a recording once and deliver what it completes, run `pdn-mailcast-receiver --decode file.wav`.

## Sharing a radio with LinBPQ

If the radio on the receiver's sound card is also your LinBPQ packet radio (through QtSoundModem, say, with flrig controlling the rig), the receiver can borrow it for each slot. About a minute before each daylight slot it tells LinBPQ to stop transmitting on that radio's port, tunes the radio to USB on `dialKHz`, listens until 12 minutes after the slot starts, puts the radio back where it was, and only then lets LinBPQ transmit again.

Add this to the config:

```json
"rig": { "rigctld": "127.0.0.1:4532" },
"bpq": { "host": "127.0.0.1", "port": 8010, "user": "sysop", "password": "your-sysop-password", "hfPort": 2 }
```

- `rig`: Hamlib's rigctld for the radio. With flrig, run `rigctld -m 4` beside it: that is Hamlib's flrig backend, so flrig stays in charge of the rig.
- `bpq`: LinBPQ's node telnet port (the Telnet port's `TCPPORT`, not the `FBBPORT`) and a user whose `USER=` line ends in `SYSOP`, such as `USER=sysop,your-sysop-password,G4ABC,,SYSOP`. `hfPort` is the number of LinBPQ's port on the shared radio, as its `PORTS` command lists it. The log says which port that is at start-up; add `"expectedPortId": "..."` with the name `PORTS` gives it, and the receiver won't retune if the number ever points at another port.
- `drainSeconds` in `bpq` (15 unless set, at most 40): how long to wait after LinBPQ stops before tuning, so that anything LinBPQ had already handed to the TNC goes out first.
- If nothing else ever transmits on the radio, leave out `bpq` and say so instead: `"rig": { "rigctld": "127.0.0.1:4532", "dedicatedRadio": true }`. With `rig` and neither of these, the receiver won't start.

It only retunes when `audio` is the radio's sound card, never for a web SDR, and only when rigctld is answering.

To keep LinBPQ off the air it logs in as that user and sends `XMITOFF 2 1`, which makes LinBPQ drop anything it would send on port 2, and `XMITOFF 2 0` afterwards. It only tunes the radio once LinBPQ has confirmed, the drain time has passed and rigctld says the radio is not transmitting. During the slot it checks LinBPQ and the radio's PTT every 5 seconds. Everything that can go wrong goes the safe way:

- if LinBPQ doesn't confirm, or the radio is still transmitting, the radio isn't retuned for that slot;
- if LinBPQ's answer is lost or garbled, it counts as taken, and `XMITOFF 2 0` is sent afterwards;
- if LinBPQ can't be reached during the slot, the radio goes straight back;
- if the radio transmits during the slot anyway (a frame the TNC was still holding for a busy channel), the radio goes straight back and LinBPQ is let go;
- if LinBPQ restarts during the slot (which turns `XMITOFF` off), the receiver sees the connection drop at once and turns it off again, or puts the radio straight back if LinBPQ isn't answering yet;
- if the radio can't be put back where it was, LinBPQ stays off, the log says so loudly, and the receiver keeps trying;
- if the receiver stops in the middle of a slot, LinBPQ stays off rather than transmitting on the bulletin frequency. The next start puts the radio back first and then turns LinBPQ on again, and the log says what happened. If the receiver never comes back, LinBPQ stays off until you send `XMITOFF 2 0` as sysop or restart LinBPQ;
- if you had already turned the port off yourself, it is left off afterwards.

For this to be safe:

- the shared radio must be on a KISS or AGW-style port (QtSoundModem, Direwolf and the like). Pactor-type drivers such as VARA and ARDOP don't go through the queue `XMITOFF` stops, so they can't be held off this way;
- one LinBPQ port per radio: the receiver holds off only `hfPort`;
- the TNC must not transmit on its own during a slot. In QtSoundModem, send the CW ID only after transmissions, not on a timer;
- no other program may be connected straight to the TNC's AGW or KISS ports, because those don't go through LinBPQ;
- tune the radio through CAT (flrig or rigctld), not by hand. The receiver puts it back where it was before the slot, and won't let LinBPQ transmit until the radio reads that frequency again.

One gap remains: a LinBPQ that restarts during a slot comes back with transmit on, and anything it sends in the seconds before the receiver has logged in again (an ID at start-up, say) goes out on the bulletin frequency. Avoid restarting LinBPQ during a slot.

The status page shows what it is doing (idle, holding LinBPQ's transmit off, tuned to 7.052 MHz, putting the rig back) and the last problem, and the log lines start `retune:` and `rig:`. The sysop password is never logged or shown. While it works it keeps `interlock.json` and `rig-restore-HOST-PORT.json` in the state directory; leave them alone.

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

LinBPQ holds bulletins older than its BID lifetime and maximum age. If you have set either very low, bulletins sent this way (carried for a day or so) may arrive held.

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

- when to listen: the frequency, the slots and today's slot times, whether they come from GB7RDG's directory or your config, the next slot, and for a web SDR which of today's slots it listens to and the next of them;
- the last slot: how far off frequency the tone was, its signal-to-noise ratio, and how many frames were heard;
- your BBS: where bulletins go, how many are waiting, and any problem reaching it;
- a live spectrogram from 0 to 4 kHz, with the signal's edges, its centre at 1800 Hz and the tone marked, so you can see whether the signal sits where it should in your passband; pdn-soundmodem's full waterfall is a link away;
- the input level, with the same target as pdn-soundmodem: peaks between -18 and -9 dBFS;
- what to try if nothing is heard;
- the bulletins being sent, how many pieces of each have arrived, and what the BBS said about each;
- the mail this receiver holds (see [Mail](#mail));
- the settings: audio, and the BBS's address and login. The USB dial is shown too, but it is only changed in the config file. Saving writes them to the config file (without its comments) and puts them in force at once. If you change the BBS's address, port or type, enter its password again: the saved one is never sent anywhere new without you.

On this machine only, the page answers to `localhost` and nothing else. To reach it from your network, set `"lan": true` and a `"password"` in `web`; the browser asks for it (any user name). Use it on a network you trust: it is plain HTTP.

## Mail

The receiver keeps its own copy of every bulletin it rebuilds, so nothing is lost if the transfer to your BBS fails, or if the BBS later loses or refuses a bulletin.

A rebuilt bulletin waits in the outbox until your BBS has answered for it: accepted, already had (it has that BID) or refused. Then it moves to the archive with that answer and when it came. Copies are kept for `archive.days` (30) and up to `archive.maxMegabytes` (50 MB) in all, the oldest going first. Bulletins still waiting are never removed. Only bulletins are kept; nothing else the receiver hears goes to the BBS or the archive.

The status page's **Mail** section lists both, newest first, 25 to a page: BID, from, to, @, title, date, size, and status. A waiting bulletin shows the BBS's last answer, if any, and the next try. Click one to read the whole bulletin as it will reach the BBS: its header lines, its R: lines and its text. **Open as text** shows it on its own.

**Send to BBS again** puts an archived bulletin back in the outbox, and the next session offers it. If your BBS still has it, it says so by its BID, nothing is sent twice, and the bulletin stays down as accepted. Each one sent again is logged, the same one can only be sent again once every 2 minutes, and at most 50 sent again can wait for the BBS at once.

The copies are a convenience: if one cannot be written (a full disk, say), that is logged, and a bulletin your BBS has taken leaves the outbox all the same. A refused one is moved to `store/quarantine/` instead, so it is kept without holding up newer mail.

The same is there for scripts, behind the page's password if it has one:

- `GET /api/mail?offset=0&limit=50`: the list as JSON, newest first (at most 200 at a time).
- `GET /api/mail/<id>`: one bulletin as plain text, with the `id` from the list.
- `POST /api/mail/resend` with `{"id": "<id>"}` as `application/json`: sends one again. Like saving the settings, it is refused from another site.

On disk they are in the state directory: waiting ones in `store/outbox/`, archived ones in `store/archive/`, one file each. A file that cannot be read is moved to `store/quarantine/` and logged, and the receiver carries on.

## What it logs

Everything goes to the journal (`journalctl -u pdn-mailcast-receiver`), one plain line each: the audio source, GB7RDG's slots and where they come from (the config, or GB7RDG's directory once heard), which slots a web SDR listens to each day, the tone (`tone: 1801.3 Hz, +1.3 Hz from 1800 Hz, SNR 14.2 dB in 3 kHz, 10 s`; it reads 8 to 11 s for the 10 s tone, and the CW ident that follows on the same frequency is not counted), each bulletin as it completes, what the BBS said about it, and each one sent again from the page. The record of deliveries is also kept in `deliveries.jsonl` in the state directory.

## Building from source

You need the .NET 10 SDK.

```
dotnet publish src/Mailcast.Receiver -c Release -o out
out/pdn-mailcast-receiver --config src/Mailcast.Receiver/receiver.example.json
```

`scripts/build-receiver-deb.sh linux-x64 0.1.0` builds the .deb into `artifacts/`.

The test suite includes one test that decodes a simulated slot into a real LinBPQ in docker. It is tagged `Category=Docker`: `dotnet test --filter Category=Docker` runs it, and `--filter "Category!=Docker"` leaves it out.
