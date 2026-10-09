# The receiver: reference

Setup is in [src/Mailcast.Receiver/README.md](../src/Mailcast.Receiver/README.md). This page has the detail.

## Configuration

The config file is `/etc/pdn-mailcast/receiver.json`. There are only a few settings:

```json
{
  "audio": "ubersdr:wessex.zapto.org",
  "dialKHz": 7052.0,
  "sources": ["GB7RDG", "M0LTE"],
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
  "archive": { "days": 30, "maxMegabytes": 50 },
  "stateDirectory": "/var/lib/pdn-mailcast"
}
```

- `audio`: `ubersdr:wessex.zapto.org` for the web SDR, an ALSA device such as `plughw:CARD=Device,DEV=0` for your radio's sound card, or `wav:/path/to/file.wav` to decode a recording.
- `dialKHz`: the USB dial in kHz, normally `7052.0` (7.052 MHz), which is also what you get if you leave it out. The web SDR is tuned there, and a radio on a sound card should be set there. The signal is centred 1800 Hz above the dial, on 7.0538 MHz. Only change it if the signal moves; anything from 1800 to 30000 kHz is accepted.
- `sources`: the callsigns the broadcast is accepted from. GB7RDG sends it today, and it may move to M0LTE, so the default is `["GB7RDG", "M0LTE"]`. Each is a callsign of 1 to 6 letters and digits; any SSID is accepted, so `M0LTE` also takes `M0LTE-1`, and one written with an SSID counts as the callsign without it. Frames from anyone else are ignored, and the log names the first few such callsigns once each. A config file from before this setting has no `sources`; the receiver uses the default and says so in the log, so it keeps hearing the broadcast. An empty list is refused. You can also change it on the status page.
- `bbs`: where your BBS is and how the receiver logs in. See below.
- `web`: the status page. It only answers on this machine unless you set `lan` to true, and then it needs a `password`, which you enter on the page's sign-in page. See [On your network](#on-your-network).
- `slotUtc` and `everyMinutes`: when GB7RDG's slots are, in UTC. One starts at `slotUtc` and then one every `everyMinutes`, round the clock, but only those in daylight run (see `daylight`). GB7RDG sends every hour on the hour, so `"00:00"` and `60`, which is also what you get if you leave them out. `everyMinutes` must divide a day (1440) and be at least 15. A config file from before hourly slots has only `slotUtc` (`"12:00"`); it is read as every 60 minutes from that time, which is the same hourly slots, so nothing needs changing. A sound card listens all the time, whatever these say. The receiver also uses the slots to make sense of what it hears: each frame counts for the slot whose start most recently passed, and a tone only counts as a slot's opening tone if it starts within 5 minutes of a slot's start, so someone tuning up near 7.0538 MHz isn't taken for GB7RDG.
- `daylight`: GB7RDG only sends in daylight, because 40 m does not reach UK stations at night. A slot runs only if it starts between `afterSunriseMinutes` after sunrise and `beforeSunsetMinutes` before sunset at `locator` (a 4 or 6 character Maidenhead locator). The receiver works out sunrise and sunset itself, from the date, in UTC, so it needs no internet and summer time makes no difference. Left out, it is GB7RDG's own: IO91lk, 120 and 30, which today (5 October) is 09:00 to 17:00 UTC, in midwinter 11:00 to 15:00 and in midsummer 06:00 to 19:00. `"daylight": null` means every slot. You should not need to change it: once the receiver has heard GB7RDG's directory, which gives GB7RDG's own slots and daylight hours, it uses those instead of these settings, and says so in the log and on the status page. A sound card listens all the time anyway; the daylight hours only matter for a web SDR and for the "next slot" on the page.
- A web SDR listens to every daylight slot that fits in its allowance. Public UberSDR receivers allow each address about three hours a day, and a web SDR is open 14 minutes for each slot (from 2 minutes before it to 12 after), so up to 12 slots a day fit. That is every slot for most of the year: 9 on 5 October (09:00 to 17:00 UTC), 5 in midwinter. Near midsummer there are up to 14, so it skips the earliest morning ones (06:00 and 07:00 UTC in late June) and keeps the late afternoon, which is often the best time for nearer stations. The log says each day how many slots it listens to and which it skips, and the status page shows them. Older config files may have a `webSdrSlotsPerDay` setting; it is no longer used, and the receiver ignores it and says so in the log.
- The receiver may end a slot's window early, once there is nothing left to wait for: the day's directory has been heard (a repeat of a directory already held counts too, so the two waveforms taking turns, or a restart, is not mistaken for "never heard"), every bulletin in it is complete or already delivered, this slot's ionosonde and PSK Reporter readings have both been received (unless this receiver has never heard that kind at all, since then there is no way to tell whether this head end sends it), the probe or channel measurement has been captured (or its own short window has passed), and it has been quiet for a minute since the last frame, in case of a burst still to come. It never ends early while anything is unknown or incomplete, or during a hook or a rig restore; a frame for an object outside the directory is treated as unfinished business, so it keeps the window open and resets the quiet minute. For a web SDR this gives back a few minutes of the daily allowance, and with `hooks` set "after" runs then too, not at the window's usual close; for a shared rig (see below) it gives LinBPQ its port back sooner. This is on by default; there is nothing to turn it off. The log says why, such as "audio: ending the 12:00 UTC slot's window early: the directory and everything in rotation are heard, the probe/channel measurement is captured, and it has been quiet for 60 s", and the status page's `slot.endedEarly` says the same.
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

Once the day's directory and everything in it are heard, the probe or channel measurement is captured and it has been quiet a minute, the receiver gives the rig back and releases LinBPQ early, the same way it would at the slot's usual close: "after" still runs once the rig is back and LinBPQ is released. It never does this during "before", "after" or while putting the rig back. See the note on this in [Configuration](#configuration).

## Running your own commands around each slot

If your radio is shared with something else, Ardopcf say, the receiver can run a command of yours before each slot it listens to and another afterwards, to stop that program and start it again. Add `hooks` to the config:

```json
"hooks": {
  "before": { "command": "/usr/local/bin/mailcast-hook", "args": ["stop"], "timeoutSeconds": 30 },
  "after": { "command": "/usr/local/bin/mailcast-hook", "args": ["start"] }
}
```

- `command` is the full path of a program or script, and the receiver's user must be able to run it. It is run directly, not through a shell, so each of `args` reaches it exactly as written. Either hook can be left out, and any other setting in a hook (a misspelt `timeout`, say) stops the receiver starting, with a message saying so.
- `timeoutSeconds` (30 unless set, 1 to 300): a command still running after this is stopped, along with what it started. A program that detaches itself into the background can escape this, so have your script wait for what it starts.
- "before" starts `timeoutSeconds` ahead of the listening window, so it is done by the time the window opens: 2 minutes before each slot a web SDR listens to, or before every slot for a sound card. "after" runs when the window closes, 12 minutes after the slot starts. With `rig`, "before" is done before LinBPQ is held off and the radio retuned, and "after" runs once the radio is back and LinBPQ is released.
- If "before" fails (it exits with anything but 0, runs out of time or can't be started), the log says so, and with `rig` the receiver doesn't retune the radio or hold LinBPQ off for that slot, since whatever it was meant to stop may still be transmitting. A web SDR, or a radio already on 7.052 MHz, still listens.
- "after" always runs once "before" has started, even if the slot went wrong or the receiver is stopping. If the receiver stops during a window, it runs "after" when it next starts, and leaves the rest of that slot alone. An "after" that doesn't finish with exit 0 (it failed, or was killed as the receiver stopped) is run again at the next start, unless a later slot's has worked since. Meanwhile the receiver keeps `hooks.json` in the state directory.
- If the receiver is stopping and couldn't put the radio back (with `rig`), "after" still runs, so what it starts may find the radio still on 7.052 MHz. The log says loudly when the radio couldn't be put back.
- Each command is told about the slot in environment variables: `MAILCAST_HOOK` (`before` or `after`), `MAILCAST_SLOT_UTC` (such as `2026-10-05T12:00:00Z`), `MAILCAST_DIAL_KHZ` (`7052.0`), `MAILCAST_CENTRE_KHZ` (`7053.8`), and for "after" `MAILCAST_BEFORE_OK` (`1` if "before" worked, `0` if not).
- What a command prints goes to the log, on lines starting `hooks:`.

The commands run as the receiver's own user, `pdn-mailcast`, with the same protections as the receiver: they can't use `sudo`, can't see `/home`, and can only write in `/var/lib/pdn-mailcast`, `/etc/pdn-mailcast` and a `/tmp` of their own that nothing else sees. So keep scripts somewhere like `/usr/local/bin`. This one stops or starts Ardopcf on another machine over ssh:

```sh
#!/bin/sh
# /usr/local/bin/mailcast-hook: stop or start Ardopcf on the shack PC.
exec ssh -o BatchMode=yes -o ConnectTimeout=10 ardop@shack-pc "sudo systemctl $1 ardopcf"
```

On the shack PC, let that user run those two commands without a password, with a sudoers line such as `ardop ALL=(root) NOPASSWD: /usr/bin/systemctl stop ardopcf, /usr/bin/systemctl start ardopcf`.

The receiver's user needs an ssh key of its own (yours is in `/home`, which it can't see). Make one and copy it across, which also records the shack PC's host key:

```
sudo install -d -m 750 -o pdn-mailcast -g pdn-mailcast /var/lib/pdn-mailcast
sudo install -d -m 700 -o pdn-mailcast -g pdn-mailcast /var/lib/pdn-mailcast/.ssh
sudo -u pdn-mailcast ssh-keygen -t ed25519 -N "" -f /var/lib/pdn-mailcast/.ssh/id_ed25519
sudo -u pdn-mailcast ssh-copy-id -i /var/lib/pdn-mailcast/.ssh/id_ed25519 ardop@shack-pc
```

Then try it as that user, `sudo -u pdn-mailcast /usr/local/bin/mailcast-hook stop` and `start` again, and restart the receiver.

## Sending a daily report

You can help the experiment by posting a short report each day of what your receiver heard. It is a public bulletin on GB7RDG, so anyone there can read it. It is off unless you turn it on:

```json
"feedback": { "enabled": true, "callsign": "G4ABC" }
```

Or tick "Send a daily report" near the top of the status page, give your callsign and save: that writes the same to the config and is in force at once, with no restart. "Turn off" there stops it again. The first report covers the slots from when you turned it on.

- `callsign` is yours, with no SSID, and the report comes from it. It has to look like a callsign, or the receiver won't start.
- The report is public. It goes as a bulletin (type B) to `MCAST@GB7RDG.#42.GBR.EURO`, through your own BBS, in the same session the bulletins use. The full address means mail routing carries it to GB7RDG and it stays there, rather than flooding the country. Anyone on GB7RDG can list the reports with `L> MCAST` and read them, and like any bulletin it can also be read on your own BBS and any it passes through on the way.
- Your BBS sends it on like any other bulletin addressed to GB7RDG, so it needs a route towards GB7RDG; most UK BBSes have one.
- GB7RDG's head end never broadcasts a report: it refuses anything addressed to MCAST, and anything that looks like a report.
- It goes about 30 minutes after the day's last daylight slot (17:30 UTC in early October), once a day at most. If the receiver was off at that time, it sends that day's report when it next starts, once.
- If your BBS asks for it later, or can't be reached, the receiver tries again after 1 hour, then 2, then every 4 hours, at most 6 times a day, until the next day's report is due. If your BBS refuses it, it isn't sent again; the status page shows what was said.
- It is small and plain text: about 500 bytes for a 9-slot autumn day, and about 760 for a 14-slot midsummer one. The status page shows the last one as sent, what the BBS said, and when the next goes. The receiver keeps its notes for it in `feedback.json` in the state directory.

### The daily report's format

The title is `MCR <callsign> <date>`, the date in UTC. Here is a whole day from a web SDR:

```
MCR G4ABC 2026-10-06

MCR1 0.6.0 IO80qr wessex.zapto.org 12/12 1:BBS1
09 W4 196 11 +1.3 IM 2 2.1/-14 0.42 0.31 301 b
10 W3 188 15 +1.2 IG 2 1.9/-17 0.35 0.21 287 b
11 W4 214 18 +1.2 IG 2 1.8/-16 0.31 0.18 279 b
12 W3 190 19 +1.1 IG 1 - 0.12 0.15 - b
13 W4 220 19 +1.1 IG 2 1.8/-19 0.29 0.17 276 b
14 W3 185 17 +1.0 IG 2 1.9/-15 0.36 0.22 284 b
15 W4 162 13 +1.0 IM 2 2.0/-12 0.47 0.38 296 b
16 W3 97 8 +0.9 IM 3 2.2/-10 0.61 0.55 310 b
17 - 0 3 +0.8 IP
```

Fields are separated by one space, and `-` means not known. The first line is the header:

1. `MCR1`: format 1.
2. The receiver's version.
3. The web SDR's 6 character locator, from the position it reports, or `-` for a sound card.
4. Where the audio came from: `sc` for a sound card, or the web SDR's name, such as `wessex.zapto.org`. A web SDR on your own network (an IP address, `localhost`, or a name like `sdr.local` or `sdr.lan`) is just `sdr`, so the report never carries your addresses.
5. Bulletins rebuilt / delivered to the BBS that day.
6. Errors: `0`, or how many, a colon, and how many of each kind: `BBS` (a session with the BBS failed), `AUD` (the audio failed or was lost), `RIG` (a problem retuning the radio), `HOOK` (a hook command failed).

Then one line for each slot listened to, in order:

1. The hour, UTC (`HHMM` if the slot isn't on the hour).
2. The waveform most frames came on: `W4` (1200 bps), `W3` (600 bps), or `WX` if they tied.
3. Frames heard.
4. The tone's signal to noise, dB in 3 kHz.
5. The tone's offset from 1800 Hz, Hz.
6. The propagation verdicts heard for the slot: `I` for the ionosonde or `P` for PSK Reporter, then `G` good, `M` marginal, `P` poor or `U` unknown, joined by `+` when there are both, such as `IG+PM`.

When the slot's channel was measured, six more follow:

7. How many paths (modes) there were.
8. The 2F path's delay after the first, ms, and its power against the first, dB, as `1.9/-17`. Without a locator to name the hops, it is the second path's.
9. The delay spread, ms.
10. The Doppler spread, Hz.
11. The virtual height, km. Without the web SDR's position it is worked out for a 150 km path.
12. What it was measured from: `b` the bursts, `p` the probe after the tone, whichever measured the slot better.

A real line, from a recording of the 16:00 slot on 5 October through the Wessex web SDR, reads `16 W4 41 - - - 2 1.9/-17 0.29 0.2 294 b`: 41 frames at 1200 bps, two paths with the second 1.9 ms later and 17 dB weaker, and a reflection about 290 km up. Its tone wasn't caught, so there is no SNR or offset. A slot listened to with nothing heard reads `11 - 0 - - -`. A reader should ignore anything after the sixth field of the header and the twelfth of a slot line, and take `-` in a slot line's seventh field as no measurement, whatever follows. So the format can grow at the ends of its lines without a new number; a later field on a line with no measurement comes after six `-`. `Packet.Mailcast.Feedback.DailyReport.Parse(title, body)` reads one, R: lines and all, as a BBS shows it.

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

2. In the mail configuration (the web page's Mail Mgmt, or BPQMail's configuration), add a user **Q0CAST** and tick **BBS**, so it's a forwarding partner rather than an ordinary terminal user.

3. On Q0CAST's forwarding page, tick **FBB Blocked** (forward in FBB's binary blocks, not line-by-line text), **Allow Binary** (LinBPQ's label for allowing compressed forwarding, which blocked forwarding also needs) and **Use B1 Protocol** (the simpler of FBB's two binary protocols). Leave the TO, AT, TIMES, Connect Script and HR Routes boxes empty, so nothing is ever queued for it, and leave Enable Forwarding, Request Reverse and the rest unticked. It does not need to be enabled for forwarding, because LinBPQ never has to call it. Click **Update** to save.

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

### Testing the login

The settings page's **Test BBS login** button tries the host, port, login and password shown (or typed in but not yet saved), the same way the receiver logs in to send mail, but exchanges no mail: it connects, logs in, enters the BBS command, and stops once it knows how far it got. It says plainly:

- logged in as a forwarding partner: the login works;
- the password is wrong;
- the login works but is not set up as a BBS forwarding partner, such as Q0CAST added to the Telnet port but not to the mail configuration's Users;
- the BBS could not be reached, because the host or port refused the connection or never answered;
- something else, with the BBS's own first line of reply.

Leave the password field empty to test with the one already saved. The button never saves anything, and the password is never shown back, logged, or sent anywhere other than the BBS you asked it to try.

## The status page

http://127.0.0.1:8130/ shows:

- when a newer version is out, a banner at the top: "Version 0.7.0 is available (you have 0.6.0)", a link to what's new, and the command to upgrade, ready to copy. If you installed from packet-net's apt repository that is `sudo apt update && sudo apt install --only-upgrade pdn-mailcast-receiver`; if you installed the .deb by hand, it links the new .deb for your machine and gives `sudo apt install ./pdn-mailcast-receiver_0.7.0_amd64.deb`. Upgrade between slots: in daylight, after :13 and before :58 past the hour; at night, any time. See [Checking for updates](#checking-for-updates);
- at the top, the speed: GB7RDG takes turns on 1200 bps (MS110D WN4) and 600 bps (WN3), and the modem reads either. The tile shows the burst being heard now, or else the last one frames came from, then the frames of the last slot at each speed, and the speed GB7RDG's directory gave for that slot if one was rebuilt in it. The speed is what the modem locked to at the start of each burst. Before anything is heard it says "Not heard yet.";
- when to listen: the frequency, the slots and today's slot times, whether they come from GB7RDG's directory or your config, the next slot, and for a web SDR which one it is (its address, and once it has been opened, the callsign, name and location it gives), which of today's slots it listens to and the next of them;
- for a web SDR, a **Listen now** button, so a new user can see the spectrogram and the level work without waiting for the next slot. It opens the web SDR for 3 minutes, then closes it again, and counts against the daily allowance the same as a slot (at most 3 times a day). It refuses if a slot's own window is due to open in the next 5 minutes, so the two never overlap; if GB7RDG's directory moves a window closer while a session is already running, that session is cut short at once instead. It never runs the hooks, retunes a shared rig or holds LinBPQ off, since it is a look at the web SDR only. The page shows a countdown while it is open;
- the last slot: how far off frequency the tone was, its signal-to-noise ratio, and how many frames were heard;
- the ionosphere: GB7RDG's latest ionosonde reading, in words, such as "Open from about 280 km" over "Fairford foF2 6.05 MHz at 11:30 UTC (12 min old). 40 m: closed at 100 km, open from about 280 km", with the verdict at 100, 500 and 1000 km. It is a guide to whether you can expect to hear GB7RDG, not a forecast, and it changes nothing about what is sent. Once the sounding is more than 45 minutes old (the head end's default) it says so instead. The receiver keeps the latest across restarts. Beside it is GB7RDG's PSK Reporter reading, what live FT8, FT4 and WSPR spots between UK and Irish stations said about 40 m over the half hour before the slot, such as "FT8 spots: open from about 520 km" over "40 m FT8/FT4/WSPR spots (PSK Reporter, last 30 min): open at 500 km (25 spots, 12 stations, median -11 dB) and 1000 km (16 spots, 9 stations, median -13 dB), nothing under 250 km despite 41 spots further out". FT8 decodes signals far weaker than the mailcast needs, so "open" there doesn't mean you will hear GB7RDG; the median SNRs are the better guide. If there weren't enough spots to say, it says that; once the reading is more than 45 minutes old it says it is too old;
- your BBS: where bulletins go, how many are waiting, and any problem reaching it;
- a live spectrogram from 0 to 4 kHz, with the signal's edges, its centre at 1800 Hz and the tone marked, so you can see whether the signal sits where it should in your passband; pdn-soundmodem's full waterfall is a link away. While there is no audio it says why instead: between a web SDR's slots it says when the spectrogram comes back, and for a sound card that can't be opened it gives the reason and when the receiver tries again;
- the input level. For a sound card it has the same target as pdn-soundmodem: peaks between -18 and -9 dBFS. For a web SDR (or a recording) the level isn't yours to set and the modem copes with any level short of clipping, so it only warns about clipping;
- the channel: what the radio path from GB7RDG was like in the last slot, and over the last day (see [The Channel tile](#the-channel-tile));
- what was heard, slot by slot: every burst, and every frame in it (see [What was heard](#what-was-heard));
- what to try if nothing is heard;
- the bulletins being sent, how many pieces of each have arrived, and what the BBS said about each;
- the mail this receiver holds (see [Mail](#mail));
- the daily report, if you have turned it on: the last one as sent, what your BBS said, and when the next goes (see [Sending a daily report](#sending-a-daily-report));
- the settings: audio, the callsigns frames are accepted from (`sources`), the BBS's address and login, and the page's own password. The USB dial is shown too, but it is only changed in the config file. Saving writes them to the config file (without its comments) and puts them in force at once. If you change the BBS's address, port or type, enter its password again: the saved one is never sent anywhere new without you. A **Test BBS login** button tries the login shown without exchanging any mail (see [Testing the login](#testing-the-login)).

On this machine only, the page answers to `localhost` and nothing else.

Scripts can read the same from `GET /api/status`. For the speed: `slot.waveform` is the waveform most of the last slot's frames came on (`ms110d-wn4`), or `mixed` if two tie; `slot.frameCounts` has the frames on each; `slot.listedWaveform` is the directory's, if one was rebuilt in that slot; `slot.endedEarly` is why its window ended early, in words, if it did (see the note on this above); and `burst` is the burst now (`live: true`) or the last one frames came from, with its `waveform` and `bps`, or null before any. `iono` is the ionosonde reading, or null before one is heard: `state` (`GOOD`, `MARGINAL`, `POOR` or `UNKNOWN`), `foF2`, `mufd100`, `mufd500`, `mufd1000` (MHz), `skipZoneKm`, `station`, `soundingTimeUtc`, `ageMinutes` (by this receiver's clock), `source`, `method`, `distances` (each with `km`, `mufMhz` and `verdict`), and `headline` and `words`, what the page shows. `pskReporter` is the PSK Reporter reading, or null before one is heard: `state`, `observedUtc`, `ageMinutes`, `windowMinutes`, `feedDown`, `skipZoneKm`, `distances` (each with `km`, `fromKm`, `toKm`, `verdict`, `spots`, `stations`, `snrMedianDb` and `closedBy`), and `headline`, `words` and `distanceWords`. `update` is the update check: `latest` (the newest in the apt repository, or null before a check has worked), `current`, `newer`, `checkedAt`, and when newer, `release` (its release notes), `command` and, for a .deb installed by hand, `download`. `bbs.reachable` is false when the BBS has never answered and the last session with it failed; the page only offers the daily report when it is true. `feedback` is the daily report: `enabled`, and when it is on, `lastSent`, `lastAnswer` (what the BBS said, in words), `next` (when the next goes, or the last is tried again) and `text` (the last report, title first). For a web SDR, `audio.webSdr` says which: its `host` (`wessex.zapto.org`), a `url` for its own page, and `about`, what it says about itself once opened (callsign, name and location), or null before then. `listenNow` is null unless the audio is a web SDR; otherwise `usesLeft`, `usesMax` (3), `minutes` (3), `until` (while a session it opened is still running), `canUse` and, when it cannot be used, `problem` (why, in words). `POST /api/listen-now` (no body needed) opens a session, if one is not refused; like saving the settings, it is refused from another site and needs the page's password, if it has one. It answers `{"ok": true, "until": "..."}`, or `{"ok": false, "error": "..."}` with it. `heardSlots` is the slim list "What was heard" shows, newest first: each slot's `slot`, `slotTicks` (a string of digits, its identity for the full drill-down, see [What was heard](#what-was-heard)), `bursts` and `frames` counted.

### On your network

To reach the page from other computers, set `"lan": true` and a `"password"` in `web`, and restart the receiver. Then the page asks you to sign in first:

- Enter the password. Tick **Keep me signed in on this device** to stay signed in for 30 days; otherwise you are signed out when you close the browser, or after a day at most.
- **Sign out**, at the top right of the page, signs that browser out and closes its live spectrogram.
- Sign-ins last a restart of the receiver. To change the page password on the page, enter the new one and the current one in the settings and save: every other browser is signed out, and the one you saved from stays signed in. Changing it in the config file and restarting the receiver signs every browser out.
- A wrong password is answered after a second. Five wrong in five minutes from one address and that address can't sign in for 10 minutes, even with the right password. If there are 50 wrong in five minutes from all addresses together, every sign-in, from anywhere, is slowed to one every 3 seconds until it calms down. The log says so once each time, never with the password.
- Scripts can skip the sign-in page and give the password with HTTP Basic (any user name), as before: `curl -u any:pick-one http://receiver:8130/api/mail`. A script that gives no password is asked for one (a 401 with a Basic challenge). Browsers always get the sign-in page instead, and a Basic login a browser remembers from before is ignored, so it can sign out. The receiver tells a browser by what browsers send (a user agent starting `Mozilla/`, or asking for a web page), so a script that sends those too is taken for a browser.

A password set without `lan` works the same way on this machine.

Use it on a network you trust: it is plain HTTP, so the password and the sign-in cookie cross the network unencrypted, and the cookie isn't marked Secure, since that would stop it working over HTTP. The cookie can't be read by scripts on the page. Browsers don't send it with requests started by pages on other machines, though they do for other pages on the same machine name, whatever their port. So, as before, any change (saving the settings, sending mail again, signing in or out) is refused unless it comes from this page itself, at this address and port. The signed-in browsers are kept in `web-sessions.json` in the state directory, readable only by the receiver, which holds a hash of each one, not the cookie itself and not the password. To sign everyone out, stop the receiver and delete it.

### Checking for updates

To know when there is a new version, the receiver reads packet-net's apt package list (https://packet-net.github.io/apt/Packages, about 75 kB) a minute or two after it starts, then every six hours. It sends its version in the User-Agent and nothing else, and it never downloads or installs anything. It tells an apt install from a hand-installed .deb by looking for packet-net.github.io in `/etc/apt/sources.list.d/`. If it can't reach the list, the page just shows no banner, and the log says why once.

### The Channel tile

Once a slot is over, the receiver works out what the path from GB7RDG was like, two ways.

- From the channel probe: 1.5 s after the tone, GB7RDG sends 6.5 s of a known test signal made for measuring paths. The receiver finds it from where the tone ended, and reads every path's delay to a few microseconds and its Doppler, down to about -15 dB signal to noise, well below anything it can decode. Its fades are part of the path and count, however deep; if the probe is cut short or the audio drops out, it measures what was heard.
- From the data bursts it decoded: each one can be made again exactly, so it is a known signal too, about 20 times a second. This works down to about 1 dB.

When it has both, it shows the one that sees further below the strongest path, with the other beside it. A slot with neither says "Not enough decoded to measure."

The tile says it in a line, such as "Last slot (16:00 UTC): 2 paths: 1 hop, and 2 hops 1.9 ms later, 17 dB weaker. Reflection about 290 km up. Doppler spread 0.2 Hz: steady (good for 1200 bps)." Below that:

- the delay profile: how much signal arrived how long after the strongest path, with each path marked. One hop off the F layer is 1F, two hops 2F, and so on; 1E (the E layer) is only considered more than 300 km from Reading. Naming the hops needs to know where your receiver is, which a web SDR says; with a sound card the paths are shown without names. The height comes from the time between the strongest path and its second hop, and hardly depends on the distance, so it is given either way. A second or third hop is only named when its delay fits a reflecting layer 200 to 450 km up; anything arriving before the strongest path is an artefact of the measurement and is ignored. When nothing fits, the paths are shown unnamed and no height is given;
- the delay spread (how much the echoes smear each symbol), the Doppler spread (how much the moving layer blurs the frequency), how deep the fades went (how far the signal fell below its middle level a tenth of the time), and how long the signal stays steady;
- what it was measured from, with the other measurement's summary when there were both;
- a strip of the last 24 hours, one column per hourly slot, with a dot for each path at its delay, darker for stronger.

Doppler spread under 0.5 Hz is steady and good for 1200 bps; up to 1 Hz, 1200 bps should still cope; beyond that, 600 bps copes better. It is a guide, and it changes nothing about what is sent.

The measuring runs on a thread of its own after the slot, never alongside the decoding, and rests between bursts so it uses at most half of one core. On a Raspberry Pi 4 a slot of 7 bursts takes about 5 seconds of one core, and the probe about 2 seconds more. It keeps the audio from 1 s before each tone's end to 12 s after for the probe (13 s, about 1.25 MB), and only bursts it read frames from, heard within 15 minutes of a slot's start: at most 12 of them (and 4 minutes of audio, about 25 MB) per slot until then. A burst longer than the 75 s of audio it keeps is left out, and the log and tile say so. The results for the last day are kept in `channel.json` in the state directory. `--decode` measures too, after delivering, and only logs the result.

In `GET /api/status`, `channel` has the last slot: `slot`, `basis` (`probe` or `bursts`), `enough`, `modes` (each with `label`, `delayMs` after the strongest, `powerDb` against the strongest, `dopplerShiftHz`, `dopplerSpreadHz` and `seenIn`), `delaySpreadMs`, `dopplerSpreadHz`, `fadeDb`, `coherenceS`, `virtualHeightKm`, `snrDb`, `offsetHz`, `distanceKm` (null when the receiver's position is not known), `floorDb` (how far below the strongest path the profile reaches), `words`, `other` (the same slot measured the other way, with `basis`, `enough`, `modes`, `delaySpreadMs`, `dopplerSpreadHz`, `virtualHeightKm`, `snrDb`, `offsetHz`, `floorDb`, `measurements`, `kept` and `words`; null without both), `tooLong` (bursts left out since start), the `profile` (`startMs`, `stepMs`, `db`), and `history`, the last day's slots, each with its `basis`. Before anything has been measured, `slot` is null.

### What was heard

For every burst in the last few slots, and every frame in each burst, the status page's **What was heard** section shows what it carried: the object it belongs to (a bulletin's BID and title, a directory, a propagation reading and its source, or a content type this receiver doesn't handle), its piece number, whether it was new, already held, or completed that object, and whether its CRC was good. It's for checking coverage and for debugging: a slot with a lot of duplicates, or one where most frames don't decode, shows up here. Click a slot to open it; each burst shown gives when it started, its waveform, its signal to noise once the slot's channel measurement has run, and its frame count.

It's in memory only, never written to disk, so it starts empty again after a restart: it isn't an archive, just a live look at what the receiver is hearing now. It holds at most 6 slots, or 2000 frames in total, whichever comes first; the oldest slot is dropped to make room, except the newest slot is never dropped even if it alone holds more than 2000 frames. Recording it happens off the audio thread entirely, from the same worker that writes pieces to the store, so it costs nothing on the decoder.

`GET /api/slots/{slotTicks}/frames`, behind the page's password if it has one, gives one slot's full drill-down as JSON: `slot`, and `bursts` (each with `started`, `waveform`, `words`, `snrDb` (null until the slot's channel measurement has run, or if it never does), `frames` (a count) and `pieces`, each with `heard`, `esi` (the piece number, null for a frame that didn't parse), `status` (`new`, `alreadyHeld`, `completedObject`, `rejected`, `notRecognised` or `unknownDictionary`), `crcGood` and `what` (the object it belongs to, in words)). `slotTicks` comes from `heardSlots` in `GET /api/status`, above: the slot's identity in .NET ticks, given as a string of digits so it survives JavaScript intact (as a number it is too big to keep exactly) and goes straight into the URL with nothing to encode. Anything else in its place answers 400, and a slot not (or no longer) held answers 404.

## Mail

The receiver keeps its own copy of every bulletin it rebuilds, so nothing is lost if the transfer to your BBS fails, or if the BBS later loses or refuses a bulletin.

A rebuilt bulletin waits in the outbox until your BBS has answered for it: accepted, already had (it has that BID) or refused. Then it moves to the archive with that answer and when it came. Copies are kept for `archive.days` (30) and up to `archive.maxMegabytes` (50 MB) in all, the oldest going first. Bulletins still waiting are never removed. Only bulletins are kept; nothing else the receiver hears goes to the BBS or the archive.

The status page's **Mail** section lists both, newest first, 25 to a page: BID, from, to, @, title, date, size, and status. Each item also shows when it was completed (rebuilt), and, once archived, when it was delivered to your BBS. A waiting bulletin shows the BBS's last answer, if any, and the next try. Click one to read the whole bulletin as it will reach the BBS: its header lines, its R: lines and its text. **Open as text** shows it on its own. The **Bulletins** progress list on the main page shows the same two times, next to each day's entries.

**Send to BBS again** puts an archived bulletin back in the outbox, and the next session offers it. If your BBS still has it, it says so by its BID, nothing is sent twice, and the bulletin stays down as accepted. Each one sent again is logged, the same one can only be sent again once every 2 minutes, and at most 50 sent again can wait for the BBS at once.

The copies are a convenience: if one cannot be written (a full disk, say), that is logged, and a bulletin your BBS has taken leaves the outbox all the same. A refused one is moved to `store/quarantine/` instead, so it is kept without holding up newer mail.

The same is there for scripts, behind the page's password if it has one:

- `GET /api/mail?offset=0&limit=50`: the list as JSON, newest first (at most 200 at a time).
- `GET /api/mail/<id>`: one bulletin as plain text, with the `id` from the list.
- `POST /api/mail/resend` with `{"id": "<id>"}` as `application/json`: sends one again. Like saving the settings, it is refused from another site.

On disk they are in the state directory: waiting ones in `store/outbox/`, archived ones in `store/archive/`, one file each. A file that cannot be read is moved to `store/quarantine/` and logged, and the receiver carries on.

## What it logs

Everything goes to the journal (`journalctl -u pdn-mailcast-receiver`), one plain line each: the audio source, GB7RDG's slots and where they come from (the config, or GB7RDG's directory once heard), which slots a web SDR listens to each day, the tone (`tone: 1801.3 Hz, +1.3 Hz from 1800 Hz, SNR 14.2 dB in 3 kHz, 10 s`; it reads 8 to 11 s for the 10 s tone, and the CW ident that follows on the same frequency is not counted), the speed of the first frame of each slot (`slot: 1 frame heard on 1200 bps (WN4)`) and of any frame that came at another speed, each bulletin as it completes, each ionosonde reading as it arrives (one line a slot), what the BBS said about it, each one sent again from the page, a newer version once when it is first seen (`update: version 0.7.0 is available (you have 0.6.0); the status page says how to upgrade`), and with the daily report on, what your BBS said about it (`feedback: the report for 2026-10-06 (MCR G4ABC 2026-10-06, 458 bytes) was accepted by the BBS`). The record of deliveries is also kept in `deliveries.jsonl` in the state directory.

## Building from source

You need the .NET 10 SDK.

```
dotnet publish src/Mailcast.Receiver -c Release -o out
out/pdn-mailcast-receiver --config src/Mailcast.Receiver/receiver.example.json
```

`scripts/build-receiver-deb.sh linux-x64 0.1.0` builds the .deb into `artifacts/`.

The test suite includes one test that decodes a simulated slot into a real LinBPQ in docker. It is tagged `Category=Docker`: `dotnet test --filter Category=Docker` runs it, and `--filter "Category!=Docker"` leaves it out.
