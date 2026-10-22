# The receiver: reference

Setup is in [src/Mailcast.Receiver/README.md](../src/Mailcast.Receiver/README.md). This page has the detail.

## Configuration

The config file is `/etc/pdn-mailcast/receiver.json` (on Windows, `C:\ProgramData\pdn-mailcast\receiver.json`, and the state directory is `state` beside it). There are only a few settings:

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

- `audio`: `ubersdr:wessex.zapto.org` for the web SDR, an ALSA device such as `plughw:CARD=Device,DEV=0` for your radio's sound card (on Windows, its recording device by the name Windows shows, such as `soundcard:Microphone (USB Audio Device)`, or the device's own name, `soundcard:USB Audio Device`, if only one has it), `soundmodem` for the pdn-soundmodem on this machine (0.89.0 or later; `soundmodem:PORT` if its page is not on its usual port 8107), `sdrconnect:HOST` for an SDRplay radio in SDRconnect (1.0.6 or later) on another computer (`sdrconnect:HOST:PORT` if its WebSocket port is not the usual 5454), or `wav:/path/to/file.wav` to decode a recording. With `soundmodem` the receiver reads pdn-soundmodem's channel audio stream and never its config: it hears every slot, needs no `ms110d` modem in pdn-soundmodem, and pdn-soundmodem's page links to the receiver's. If pdn-soundmodem gives a dial (its `waterfall.dialFrequencyHz` if that is set, else its band plan's dial, else its `dialFrequency`), that dial is used instead of `dialKHz`. As with pdn-soundmodem's built-in receiver, any dial whose channel carries the whole signal is used: a headless Flex opens its filter for it, and a signal outside 1000 to 2000 Hz in the audio is moved to 1800 Hz before it is decoded. A radio on a sound card still wants its centre within 1000 to 2000 Hz unless its filter is wide enough, and the log says so. A dial that puts the centre below 1000 Hz, or the top of the signal above what the channel carries (6000 Hz on a 12 kHz channel), is refused with the reason, and asked about again after 5 minutes, then less often, up to every 30; nothing is retuned, unless `retune` is on (see [Retuning through pdn-soundmodem](#retuning-through-pdn-soundmodem)). So is a pdn-soundmodem too old for the stream, or one whose channel runs at a rate this receiver cannot use. A 12 kHz channel is brought up to 48 kHz; a keyup or a hole in the audio makes the modem listen afresh. If pdn-soundmodem is not running, or restarts, the log and the status page say so and the receiver connects again after 1, 2, 5, 10 and then every 30 seconds. With `sdrconnect` the receiver connects to SDRconnect's WebSocket API, tunes the radio itself (its centre a little below `dialKHz`, clear of the spike an SDR has at its centre) and turns its IQ into USB audio here, so SDRconnect's own demodulator, AGC and filters play no part; it hears every slot, like a radio of your own. If someone retunes SDRconnect, the receiver puts it back. A dropped connection is taken up again by itself, and meanwhile the status page says SDRconnect was lost and why (`lost SDRconnect at 192.168.40.61:5454 (SDRconnect closed the connection); reconnecting`), then listening again once its audio is back (a web SDR's dropped stream is said the same way); if SDRconnect cannot be reached at the start, or delivers nothing for 5 minutes, the log and the status page say why and the receiver tries again every 30 seconds; the same failure is said in the log again only every 10 minutes, with the attempts between counted. The usual reasons are said as such: the connection refused (SDRconnect not running, or its WebSocket Server, off to begin with, not switched on under Other Actions, Preferences, WebSocket Server), no answer (SDRconnect not running, since Windows gives no answer at all for a program that is not there, or the Windows firewall, which lets SDRconnect in only on a network Windows treats as Private; an address that answered earlier is said to have gone, most likely SDRconnect closed), or SDRconnect saying `can_control` is false. See [Using an SDRplay radio through SDRconnect](../src/Mailcast.Receiver/README.md#using-an-sdrplay-radio-through-sdrconnect).
- `locator`: where your radio is, as a 4 or 6 character Maidenhead locator such as `IO91lk`, for a sound card, `soundmodem`, `sdrconnect` or a recording. It lets the receiver name the radio path's hops and where they come down (see [The Channel tile](#the-channel-tile)), work out the reflection height for your real distance from GB7RDG rather than an assumed one, and put your locator in the daily report. Left out, none of that knows where you are. A web SDR says where it is itself, so with one this is not used. You can also set it on the status page.
- `dialKHz`: the USB dial in kHz, normally `7052.0` (7.052 MHz), which is also what you get if you leave it out. The web SDR is tuned there, and a radio on a sound card should be set there. The signal itself never moves: it stays on 7.0538 MHz, so the best dial is 7.0538 MHz minus the middle of your radio's own receive filter. SDR users, data-mode audio paths and a filter 2.7 kHz or wider need no change. A narrower filter, especially 2.4 kHz or less, does better on a different dial, which the status page works out for you: **Measure my filter** listens to about 10 s of band noise (nothing tuned in) and reports the filter and the dial; typing the filter's low and high edge in Hz gives the same answer without listening. Either writes `dialKHz` for you. On real band noise the measured edges can be out by about 100 Hz, so measure two or three times and take the answer that repeats, or type the edges from your radio's manual if you know them. A worked example: a filter from 367 to 2190 Hz (an FT-450D's narrow setting) gives 7.05252. Only a dial whose centre lands between 1000 and 2000 Hz in your audio is accepted; outside that the page says so. A web SDR gains nothing from any of this, so it always stays on 7.052.
- `sources`: the callsigns the broadcast is accepted from. M0LTE sends it, transmitted from GB7RDG's station; the default, `["GB7RDG", "M0LTE"]`, accepts both, so nothing needed to change here when it moved from GB7RDG. Each is a callsign of 1 to 6 letters and digits; any SSID is accepted, so `M0LTE` also takes `M0LTE-1`, and one written with an SSID counts as the callsign without it. Frames from anyone else are ignored, and the log names the first few such callsigns once each. A config file from before this setting has no `sources`; the receiver uses the default and says so in the log, so it keeps hearing the broadcast. An empty list is refused. You can also change it on the status page.
- `bbs`: where your BBS is and how the receiver logs in. See below.
- `web`: the status page. It only answers on this machine unless you set `lan` to true, and then it needs a `password`, which you enter on the page's sign-in page. See [On your network](#on-your-network).
- `slotUtc` and `everyMinutes`: when GB7RDG's slots are, in UTC. One starts at `slotUtc` and then one every `everyMinutes`, round the clock, but only those in daylight run (see `daylight`). M0LTE sends every hour on the hour, so `"00:00"` and `60`, which is also what you get if you leave them out. `everyMinutes` must divide a day (1440) and be at least 15. A config file from before hourly slots has only `slotUtc` (`"12:00"`); it is read as every 60 minutes from that time, which is the same hourly slots, so nothing needs changing. A sound card stays open all the time, but only decodes from 2 minutes before each slot that runs until the slot is over (see below). The receiver also uses the slots to make sense of what it hears: each frame counts for the slot whose start most recently passed, and a tone only counts as a slot's opening tone if it starts within 5 minutes of a slot's start, so someone tuning up near 7.0538 MHz isn't taken for M0LTE.
- `daylight`: M0LTE only sends in daylight, because 40 m does not reach UK stations at night. A slot runs only if it starts between `afterSunriseMinutes` after sunrise and `beforeSunsetMinutes` before sunset at `locator` (a 4 or 6 character Maidenhead locator). The receiver works out sunrise and sunset itself, from the date, in UTC, so it needs no internet and summer time makes no difference. Left out, it is GB7RDG's own: IO91lk, 120 and 30, which today (5 October) is 09:00 to 17:00 UTC, in midwinter 11:00 to 15:00 and in midsummer 06:00 to 19:00. `"daylight": null` means every slot. You should not need to change it: once the receiver has heard GB7RDG's directory, which gives GB7RDG's own slots and daylight hours, it uses those instead of these settings, and says so in the log and on the status page. The daylight hours decide which slots a web SDR or a sound card listens to, and the "next slot" on the page.
- A sound card stays open all the time, but between slots it doesn't decode, which saves a small computer's CPU. It decodes from 2 minutes before each slot until the slot is over, 12 minutes after it starts or sooner (see below). While a status page shows the spectrogram, it decodes all the time, so the page still shows the band and the level; turn the spectrogram off on the page to let it rest. **Measure my filter** works whenever no slot is in progress. The page and the log say when it is between slots. The computer's clock must be right to within a minute or so (by NTP, GPS or a real-time clock), or the receiver will miss slots.
- A web SDR listens to every daylight slot that fits in its allowance. Public UberSDR receivers allow each address about three hours a day, and a web SDR is open 14 minutes for each slot (from 2 minutes before it to 12 after), so up to 12 slots a day fit. That is every slot for most of the year: 9 on 5 October (09:00 to 17:00 UTC), 5 in midwinter. Near midsummer there are up to 14, so it skips the earliest morning ones (06:00 and 07:00 UTC in late June) and keeps the late afternoon, which is often the best time for nearer stations. The log says each day how many slots it listens to and which it skips, and the status page shows them. Older config files may have a `webSdrSlotsPerDay` setting; it is no longer used, and the receiver ignores it and says so in the log.
- The receiver may end a slot's window early, once there is nothing left to wait for: the day's directory has been heard (a repeat of a directory already held counts too, so the two waveforms taking turns, or a restart, is not mistaken for "never heard"), every bulletin in it is complete or already delivered, this slot's ionosonde and PSK Reporter readings have both been received (unless this receiver has never heard that kind at all, since then there is no way to tell whether this head end sends it), the probe or channel measurement has been captured (or its own short window has passed), and it has been quiet for a minute since the last frame, in case of a burst still to come. It never ends early while anything is unknown or incomplete, or during a hook or a rig restore; a frame for an object outside the directory is treated as unfinished business, so it keeps the window open and resets the quiet minute. For a web SDR this gives back a few minutes of the daily allowance; for a sound card it stops decoding until the next slot; for a shared rig (see below) it gives LinBPQ its port back sooner. With `hooks` set, "after" runs then too, whatever the audio source, not at the window's usual close. This is on by default; there is nothing to turn it off. The log says why, such as "audio: ending the 12:00 UTC slot's window early: the directory and everything in rotation are heard, the probe/channel measurement is captured, and it has been quiet for 60 s", and the status page's `slot.endedEarly` says the same.
- `archive`: how long the receiver keeps its own copy of each bulletin after your BBS has answered for it (see [Bulletins](#bulletins)). `days` is 30 and `maxMegabytes` 50 if you leave them out; the oldest copies go first once either is passed. `0` for either keeps no copies. Waiting bulletins are never removed, however old.
- `stateDirectory`: where the pieces, the rebuilt bulletins, the copies kept and the record of deliveries are kept.
- `trustedKeys`: other transmitters to trust, as well as GB7RDG, whose key is built in. Each is a `label`, which the page shows, and the `publicKey` from that transmitter's start-up line (see [Signed directories](#signed-directories)), for example `"trustedKeys": [ { "label": "GB7XYZ", "publicKey": "base64 from its head end" } ]`. Leave it out to trust GB7RDG only. A key that cannot be read stops the receiver at start-up with the reason.

To decode a recording once and deliver what it completes, run `pdn-mailcast-receiver --decode file.wav`.

### Editing the config file by hand

The receiver reads its config file once, when it starts. After editing it by hand, restart the receiver (`sudo systemctl restart pdn-mailcast-receiver`) for the change to take effect.

Until you do, the receiver won't overwrite your edit. Within a minute it logs `config: the config file was changed by hand; restart the receiver to use it`, and the status page shows the same as a notice at the top. Saving anything from the page in the meantime (the settings, the daily report, a measured dial) is refused with "The config file was edited by hand since the receiver started; restart the receiver to use it, then make this change again", so nothing you added by hand is lost. If the file can't be read at that moment (an editor on Windows can hold it open), the save is refused with "Couldn't read the config file to check it hasn't been edited by hand; try again in a moment".

At start-up the log says which file it read, any key it didn't recognise, and which optional parts are in force:

```
config: read /etc/pdn-mailcast/receiver.json
config: "rgi" is not a setting and is ignored
config: rig retuning off (no "rig"), hooks off (no "hooks"), daily report on as G8BPQ
```

A key that isn't a setting (a typo, a wrong case such as `"Rig"`, or one from another version) is ignored rather than stopping the receiver. If you set up `rig` or `hooks`, that line says so, and they get lines of their own too (`retune: the radio is retuned through rigctld at ...`, `hooks: "before" each slot ...`). If it says off, the receiver didn't read them: check the key's spelling, that it is at the top level and not inside another setting, and that you restarted.

Each save from the page logs what changed in one line, such as `config: saved from the page: dial 7052.00 -> 7052.07 kHz, daily report on as G4ABC`. A changed password is logged only as `BBS password changed` or `page password changed`, never the password itself.

## Using it with pdn-soundmodem

If pdn-soundmodem already runs your radio, the receiver can listen through it, on the same machine.

**Moving from pdn-soundmodem's built-in receiver** (a `mailcast` section in its config)? Follow [Moving from pdn-soundmodem's built-in receiver](moving-from-built-in.md) instead: it covers what to copy and how to switch over.

You need pdn-soundmodem 0.89.0 or later and this receiver 0.9.0 or later (`--version` on either says which you have). 0.88.0 works too, but on a web receiver (and now and then on a Flex) it reports audio as lost when it was only late, and the receiver then starts its decoder afresh and can miss frames. pdn-soundmodem needs no `ms110d` modem for this. It does need its station page, which its `waterfall` section gives it; the packaged config has one.

### Install

If pdn-soundmodem came from the packet-net apt repository, the repository is already set up:

```
sudo apt update
sudo apt install pdn-soundmodem pdn-mailcast-receiver
```

That also brings pdn-soundmodem up to date. If you installed pdn-soundmodem some other way, add the repository first with the two lines in [the README](../src/Mailcast.Receiver/README.md#1-install).

### Set it up

In `/etc/pdn-mailcast/receiver.json`, set `"audio": "soundmodem"`. If pdn-soundmodem's page is not on port 8107 (a second instance, `pdn-soundmodem@NAME`, say), use `"soundmodem:PORT"` with its `waterfall` port.

Set your `bbs` too (see [Configuration](#configuration)), then restart the receiver: `sudo systemctl restart pdn-mailcast-receiver`.

**On a Flex** there is no `rig` and nothing to retune. While the receiver is connected, pdn-soundmodem opens a headless slice's receive filter (a `flex:` device with no `@station`) to cover the signal. In attach mode the filter is SmartSDR's, so open it there: the signal is at 350 to 3250 Hz on the usual 7.052 MHz dial.

**Measure my filter**, on the receiver's settings page, works on this source too.

**Which dial.** Without `retune`, the receiver listens on whatever dial pdn-soundmodem's radio is on, and any dial whose channel carries the whole signal will do. On a 48 kHz channel that is a wide range. On a 12 kHz channel the dial must be 7.04925 MHz or above. pdn-soundmodem runs at 12 kHz unless one of its modems needs 48 kHz. With `retune`, the radio is moved to `dialKHz` for each slot, and that must be 7051.8 to 7052.8. If a dial will not do, the receiver's log and page say so, and what to change.

### Check it works

- The receiver's page, http://127.0.0.1:8130/, shows a live spectrogram of pdn-soundmodem's audio, and from the next slot what was heard.
- pdn-soundmodem's own page shows "listening: pdn-mailcast-receiver" at the top, linked to the receiver's page. From another machine the link only works if the receiver's page is on the network: `"web": { "lan": true, "password": "..." }`.
- The receiver's journal (`journalctl -u pdn-mailcast-receiver -f`) says `audio: pdn-soundmodem on 127.0.0.1:8107, ...`, with the channel's rate and the dial it uses, and pdn-soundmodem's says `channel-audio: ... connected`. If pdn-soundmodem stops or restarts, the receiver says so and connects again after 1, 2, 5, 10 and then every 30 seconds.

## Retuning through pdn-soundmodem

With `"audio": "soundmodem"`, if pdn-soundmodem's radio is usually somewhere else (on your packet frequency, say), add `"retune": true` and the receiver borrows it for each slot through pdn-soundmodem's receive window (pdn-soundmodem 0.88.0 or later, with a `rig` section, since it is pdn-soundmodem that tunes the radio). About a minute before each daylight slot pdn-soundmodem tunes its radio to `dialKHz`, with a passband up to the signal's top edge (3250 Hz at the usual dial), and holds all its own transmitting, so KISS frames sent meanwhile wait; 12 minutes after the slot starts (sooner if the slot ends early, later if the next follows straight on) the receiver gives the window back and pdn-soundmodem puts its radio back. The window needs no key and the receiver never reads pdn-soundmodem's config. `rig` and `bpq` are not used with this source, and `retune` does nothing with any other.

- The window is renewed every minute. If pdn-soundmodem refuses it (its transmitter is keyed, its rigctld is not connected, it holds a transmit lease for a head end, or it has no `rig` section), the log and the status page say why, the slot is heard on whatever the radio is on, and the window is asked for again: every 10 seconds, or every minute while a transmit lease is held. A station with no `rig` is asked again at the next slot, or when pdn-soundmodem restarts. A pdn-soundmodem that is slow to answer is not taken to have lost the window.
- If pdn-soundmodem restarts during a slot, its radio goes back, and the receiver takes the window again as soon as it has reconnected, for what is left of the slot.
- Stopping the receiver during a slot gives the window back. If the receiver is killed instead, pdn-soundmodem puts its radio back by itself within 5 minutes, when the window runs out.
- The hooks run around each slot as they do with `rig`: "before" first, and only if it worked is the window taken; "after" once it is given back.

The log lines start `retune:`, and the status page shows what it is doing and the last problem.

## Sharing a radio with LinBPQ

If the radio on the receiver's sound card is also your LinBPQ packet radio (through QtSoundModem, say, with flrig controlling the rig), the receiver can borrow it for each slot. About a minute before each daylight slot it tells LinBPQ to stop transmitting on that radio's port, tunes the radio to USB on `dialKHz`, listens until 12 minutes after the slot starts, puts the radio back where it was, and only then lets LinBPQ transmit again.

Add this to the config:

```json
"rig": { "rigctld": "127.0.0.1:4532" },
"bpq": { "host": "127.0.0.1", "port": 8011, "user": "sysop", "password": "your-sysop-password", "hfPort": 2 }
```

- `rig`: Hamlib's rigctld for the radio. With flrig, run `rigctld -m 4` beside it: that is Hamlib's flrig backend, so flrig stays in charge of the rig.
- `bpq`: LinBPQ's node, on the Telnet port's `FBBPORT` (the same one the receiver delivers mail to is fine), and a user whose `USER=` line ends in `SYSOP`, such as `USER=sysop,your-sysop-password,G4ABC,,SYSOP`. Use the `FBBPORT` rather than the `TCPPORT`: as John G8BPQ puts it, the `TCPPORT` starts a telnet options negotiation and is meant for real telnet users, while the `FBBPORT` is for automated connections like this one. A config that already gives the `TCPPORT` (`"port"` is 8010 unless set) still works; the receiver tells the two apart by whether LinBPQ asks for a user name. `hfPort` is the number of LinBPQ's port on the shared radio, as its `PORTS` command lists it. The log says which port that is at start-up, and whether it asked on the `FBBPORT` or the `TCPPORT`; add `"expectedPortId": "..."` with the name `PORTS` gives it, and the receiver won't retune if the number ever points at another port.
- `drainSeconds` in `bpq` (15 unless set, at most 40): how long to wait after LinBPQ stops before tuning, so that anything LinBPQ had already handed to the TNC goes out first.
- If nothing else ever transmits on the radio, leave out `bpq` and say so instead: `"rig": { "rigctld": "127.0.0.1:4532", "dedicatedRadio": true }`.
- If your own commands stop whatever else transmits on the radio (your modems, say), you can leave out `bpq` and use a `hooks` "before" command instead (see [Running your own commands around each slot](#running-your-own-commands-around-each-slot)). The receiver trusts it to keep the radio quiet while it is retuned, and doesn't retune for a slot whose "before" fails. The log says so at start-up.
- With `rig` and none of these three, the receiver won't start.

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

## Sharing a sound card with another program

If Ardopcf, QtSoundModem or another program already listens to your radio's sound card, the receiver can listen to the same card at the same time. Nothing has to stop for the slots, and there is no audio to pipe between programs.

You do this with a shared device, which ALSA (Linux's sound system) calls `dsnoop`.

1. Find your card's name. Run `arecord -L` and look for the line starting `hw:CARD=` for your radio's sound card, such as `hw:CARD=Device,DEV=0`.
2. Open the ALSA settings file with `sudo nano /etc/asound.conf`. If it already has something in it, add this at the end. Put your card's name from step 1 in place of `hw:CARD=Device,DEV=0`:

   ```
   pcm.shared {
       type dsnoop
       ipc_key 5678
       ipc_key_add_uid false
       ipc_perm 0666
       slave {
           pcm "hw:CARD=Device,DEV=0"
           channels 1
           rate 48000
       }
   }
   ```

3. In `/etc/pdn-mailcast/receiver.json`, set `"audio": "shared"`.
4. In the other program, set its input (capture) device to `plug:shared`. The `plug:` part lets it keep the sample rate it wants. Leave its output (playback) device as it was: the shared device can only listen, so the program still transmits through the card as before.
5. Restart the other program and the receiver.

Each part of the settings matters:

- **`/etc/asound.conf`, not `~/.asoundrc`.** The receiver runs as its own user, `pdn-mailcast`, and can't see your home folder. If the device is in `~/.asoundrc`, the receiver says `No such file or directory`.
- **`ipc_key`.** Programs use this number to find the shared device, and ALSA won't make one without it. Any number will do, as long as no other shared device in the file uses the same one.
- **`ipc_perm 0666` and `ipc_key_add_uid false`.** These let programs running as different users share the device. Without them, the receiver says `Permission denied` if the other program opened the device first.
- **`channels 1` and `rate 48000`.** The receiver only listens in mono at 48000 Hz. With anything else here, it won't open the device.

To check, start the other program, then record 5 seconds as the receiver's user:

```
sudo -u pdn-mailcast -g audio arecord -D shared -r 48000 -c 1 -f S16_LE -d 5 /tmp/test.wav
```

If that works, the receiver will too.

This only shares the listening. The radio still has to be on the bulletin frequency for each slot, and the other program must not transmit during one. If the radio needs retuning for the slots, or the other program needs to stay quiet during them, see [Sharing a radio with LinBPQ](#sharing-a-radio-with-linbpq) or the next section.

## Running your own commands around each slot

If your radio is shared with something else, Ardopcf say, the receiver can run a command of yours before each slot it listens to and another afterwards, to stop that program and start it again. (If the other program only needs to listen, [sharing the sound card](#sharing-a-sound-card-with-another-program) is simpler.) Add `hooks` to the config:

```json
"hooks": {
  "before": { "command": "/usr/local/bin/mailcast-hook", "args": ["stop"], "timeoutSeconds": 30 },
  "after": { "command": "/usr/local/bin/mailcast-hook", "args": ["start"] }
}
```

- `command` is the full path of a program or script, and the receiver's user must be able to run it. It is run directly, not through a shell, so each of `args` reaches it exactly as written. Either hook can be left out, and any other setting in a hook (a misspelt `timeout`, say) stops the receiver starting, with a message saying so.
- `timeoutSeconds` (30 unless set, 1 to 300): a command still running after this is stopped, along with what it started. A program that detaches itself into the background can escape this, so have your script wait for what it starts.
- "before" starts `timeoutSeconds` ahead of the listening window, so it is done by the time the window opens: 2 minutes before each slot a web SDR listens to, or before every slot for a sound card. "after" runs when the window closes: as soon as the slot has been heard in full (see [Configuration](#configuration)), or 12 minutes after the slot starts if not. With `rig`, "before" is done before LinBPQ is held off and the radio retuned, and "after" runs once the radio is back and LinBPQ is released.
- With `rig` and no `bpq`, a "before" command is what keeps the radio quiet while it is retuned, so it must stop everything that could transmit on it, and only exit with 0 once it has.
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
- GB7RDG's head end never broadcasts a report. From 0.16.0 it does note whose reports reach it, and for which day, and lists them in the directory it sends every slot: each listener's newest report, if it is for that slot's day or one of the two days before, up to 100 listeners. Your receiver looks for your callsign there, so the status page can tell you "GB7RDG got your report for 2026-10-15", or that it has not had your latest yet (see [My report never arrives](#my-report-never-arrives)).
- It goes about 30 minutes after the day's last daylight slot (17:30 UTC in early October), once a day at most. If the receiver was off at that time, it sends that day's report when it next starts, once.
- If your BBS asks for it later, or can't be reached, the receiver tries again after 1 hour, then 2, then every 4 hours, at most 6 times a day, until the next day's report is due. If your BBS refuses it, it isn't sent again; the status page shows what was said.
- It is small and plain text: about 500 bytes for a 9-slot autumn day, and about 760 for a 14-slot midsummer one. The status page shows the last one as sent, what the BBS said, when the next goes, and whether GB7RDG got it. The receiver keeps its notes for it in `feedback.json` in the state directory.

### My report never arrives

Your BBS taking the report is only the first hop. After that it is ordinary bulletin forwarding from BBS to BBS until it reaches GB7RDG, which can take a few hours. The status page's daily report box has a **GB7RDG** line once the receiver has heard a directory that lists reports (from GB7RDG's head end 0.16.0):

- "GB7RDG got your report for 2026-10-15": all is well.
- "GB7RDG has not had your report for 2026-10-16 yet": normal for a few hours after it goes. If it still says so the next afternoon, or it says it has had none from you lately, the report is stuck somewhere on the way.

If it is stuck, work along the path:

1. On the status page, check "The BBS said" for the last report. "Accepted by the BBS" means your BBS has it; anything else says what went wrong there.
2. On your BBS, list it (`L> MCAST`, or look for the title `MCR <your callsign> <date>`) and check its forwarding. It is addressed `@GB7RDG.#42.GBR.EURO`, so your BBS needs a route for `#42.GBR.EURO` (or `GBR`, or `EURO`), or for GB7RDG itself, to a partner that forwards towards GB7RDG. Most UK BBSes have one; a BBS with no partners, or partners only for local mail, holds it for ever.
3. If it has left your BBS, the partner it went to is the next place to look.
4. Still stuck? Ask on #packet-hf, with your callsign and the date of the report: someone on GB7RDG can see what has arrived.

GB7RDG lists only your newest report, and only while it is for the directory's day or one of the two before, so after a long gap it may say it has had none lately even though older ones arrived.

### The daily report's format

The title is `MCR <callsign> <date>`, the date in UTC. Here is a whole day from a web SDR:

```
MCR G4ABC 2026-10-06

MCR1 0.6.0 IO80qr wessex.zapto.org 12/12 1:BBS1 0 1
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
3. The receiver's locator: a web SDR's, from the position it reports, or for any other audio the `locator` setting; `-` if neither is known. Receivers up to 0.9.1 had no `locator` setting, so a sound card always sent `-`.
4. Where the audio came from: `sc` for a sound card, `sdrconnect` for SDRconnect, or the web SDR's name, such as `wessex.zapto.org`. A web SDR on your own network (an IP address, `localhost`, or a name like `sdr.local` or `sdr.lan`) is just `sdr`, so the report never carries your addresses.
5. Bulletins rebuilt / delivered (accepted outright) to the BBS that day.
6. Errors: `0`, or how many, a colon, and how many of each kind: `BBS` (a session with the BBS failed), `AUD` (the audio failed or was lost), `RIG` (a problem retuning the radio), `HOOK` (a hook command failed).
7. How many of the day's bulletins the BBS already had (issue #86), such as `0` in the example above, or `3` for a day when the BBS answered FS `-` to three. A header with no seventh field at all, from a receiver before this was tracked, means not known, not zero: the old header folded this into field 5's delivered instead.
8. How many part-received bulletins were given up that day: cleared because GB7RDG stopped sending them before this receiver had enough pieces (see [Bulletins](#bulletins)), such as `1` in the example above. Each counts once, on the day it was cleared; one GB7RDG starts sending again before then isn't counted. A header with no eighth field, from an older receiver, means not known. When the eighth field is there, so is the seventh, as `-` if that one isn't known.

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

A real line, from a recording of the 16:00 slot on 5 October through the Wessex web SDR, reads `16 W4 41 - - - 2 1.9/-17 0.29 0.2 294 b`: 41 frames at 1200 bps, two paths with the second 1.9 ms later and 17 dB weaker, and a reflection about 290 km up. Its tone wasn't caught, so there is no SNR or offset. A slot listened to with nothing heard reads `11 - 0 - - -`. A reader should ignore anything after the eighth field of the header (`-` or missing in the seventh or eighth means that count is not known) and the twelfth of a slot line, and take `-` in a slot line's seventh field as no measurement, whatever follows. So the format can grow at the ends of its lines without a new number; a later field on a line with no measurement comes after six `-`. `Packet.Mailcast.Feedback.DailyReport.Parse(title, body)` reads one, R: lines and all, as a BBS shows it.

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

   The receiver must use the FBBPORT, not the ordinary telnet port, because forwarding is binary. LinBPQ matches the login against the first field case-sensitively, so give the receiver the login exactly as it is written there (`m7taw` is not `M7TAW`). Leave the fourth field (the command run at login) empty: the receiver sends `BBS` itself. Restart LinBPQ.

2. In the mail configuration (the web page's Mail Mgmt, or BPQMail's configuration), add a user **Q0CAST** and tick **BBS**, so it's a forwarding partner rather than an ordinary terminal user. Also give it a **Name** (Web Mgmt, BBS Users): a BBS user with no Name gets LinBPQ's new-user prompt instead of the forwarding prompt, and the receiver reports that as needing a Name rather than logging in.

3. On Q0CAST's forwarding page, tick **FBB Blocked** (forward in FBB's binary blocks, not line-by-line text), **Allow Binary** (LinBPQ's label for allowing compressed forwarding, which blocked forwarding also needs) and **Use B1 Protocol** (the simpler of FBB's two binary protocols). Leave the TO, AT, TIMES, Connect Script and HR Routes boxes empty, so nothing is ever queued for it, and leave Enable Forwarding, Request Reverse and the rest unticked. It does not need to be enabled for forwarding, because LinBPQ never has to call it. Click **Update** to save.

4. Put the same password in the receiver's config, and `"port": 8011`.

LinBPQ holds bulletins older than its BID lifetime and maximum age. If you have set either very low, bulletins sent this way (carried for a day or so) may arrive held.

### Linux FBB

Tested against FBB 7.0.11 (Ubuntu's `fbb` package) in docker; see `tests/Mailcast.Receiver.Tests/Fbb`.

1. In `port.sys`, add a telnet port (interface 9, address in hex, so `189C` is port 6300) and a TNC line for it with mode `T`, for example:

   ```
   2    9         189C               0
   ...
   2    8    2   0      250   2     1     10     00/60   TUW   Telnet
   ```

   Leave `R` (read-only) out of the mode: on a read-only port FBB answers the receiver's forwarding request with "is not a valid command" and takes nothing.

2. Add the user Q0CAST: on FBB's console, `EU Q0CAST` and `Y` to create it, then `B` for the BBS flag, `M` for the modem flag (telnet access), `W`, a space and the password, e.g. `W mailcast12`, and an empty line to finish. Without the B flag FBB treats Q0CAST as a new user and asks for its name instead of taking its mail. FBB keeps only 12 characters of a password, so use 12 or fewer; it ignores their case.

3. Don't add Q0CAST to `forward.sys`: FBB never needs to forward to it.

4. In the receiver's config use `"type": "fbb"`, FBB's host and port, and the password. The receiver logs in as `.Q0CAST`: the dot asks FBB for a binary session, without which FBB would mangle the compressed transfers.

FBB answers "already have" for a bulletin it already holds, so a second receiver, or one that lost its state, never puts a bulletin in twice.

### Testing the login

The settings page's **Test BBS login** button tries the host, port, login and password shown (or typed in but not yet saved), the same way the receiver logs in to send mail, but exchanges no mail: it connects, logs in, enters the BBS command, and stops once it knows how far it got. It says plainly:

- logged in as a forwarding partner: the login works;
- the password is wrong: LinBPQ asked for the password again after it was sent, or FBB said so;
- the login was not recognised: LinBPQ asked for the login again, because no `USER=` line on that port has it. LinBPQ's logins are case-sensitive, so check the case too;
- the port is LinBPQ's ordinary telnet port (`TCPPORT`), not its `FBBPORT`. The ordinary port asks for the login and password one at a time, so the receiver can't use it;
- the login works but is not set up as a BBS forwarding partner, such as Q0CAST added to the Telnet port but not to the mail configuration's Users;
- the login works but LinBPQ is asking for a name, because the BBS user has none: give it a Name in LinBPQ's BBS user settings (Web Mgmt, BBS Users), then test again. A real delivery session that meets the same prompt fails the same way, rather than hanging or giving a generic error;
- the BBS could not be reached, because the host or port refused the connection or never answered;
- something else, with the BBS's own first line of reply.

When the login is refused, it also shows what the BBS actually said, and what the receiver sent: the login, the command, and that a password was sent.

Leave the password field empty to test with the one already saved. The button never saves anything, and the password is never shown back, logged, or sent anywhere other than the BBS you asked it to try. Nothing about it is logged, not even its length, and if the BBS echoes it back it is replaced with `***`.

Each test also goes to the journal (or `receiver.log` on Windows), so you can see the exchange there:

```
bbs: login test: sent LinBPQ at 192.168.1.127:8011 the login "M7TAW", a password and the command "BBS", each line ending in CR, all at once
bbs: login test: the BBS answered "user:"
bbs: login test: LinBPQ asked for the login again, so no USER= line on this port has the login "M7TAW"; LinBPQ logins are case-sensitive, so it must match the USER= line exactly.
```

In what the BBS answered, a line end shows as `<CR>` or `<LF>`, and any other byte that isn't printable shows in hex, such as `<FF>` (the start of telnet's own negotiation, which only the ordinary telnet port sends).

## The status page

http://127.0.0.1:8130/ shows:

- when the config file has been changed by hand since the receiver started, a notice at the top saying to restart the receiver to use it; until then, saving from the page is refused (see [Editing the config file by hand](#editing-the-config-file-by-hand));
- when a newer version is out, a banner at the top: "Version 0.7.0 is available (you have 0.6.0)", a link to what's new, and the command to upgrade, ready to copy. If you installed from packet-net's apt repository that is `sudo apt update && sudo apt install --only-upgrade pdn-mailcast-receiver`; if you installed the .deb by hand, it links the new .deb for your machine and gives `sudo apt install ./pdn-mailcast-receiver_0.7.0_amd64.deb`. Upgrade between slots: in daylight, after :13 and before :58 past the hour; at night, any time. See [Checking for updates](#checking-for-updates);
- at the top, the speed: M0LTE takes turns on 1200 bps (MS110D WN4) and 600 bps (WN3), and the modem reads either. The tile shows the burst being heard now, or else the last one frames came from, then the frames of the last slot at each speed, and the speed GB7RDG's directory gave for that slot if one was rebuilt in it. The speed is what the modem locked to at the start of each burst, shown only once a frame has decoded on it: the modem can lock for a moment on the tone or the channel probe to a speed GB7RDG never sends, so until then it says "Hearing a burst" with no speed, and a burst that never decodes a frame leaves the last real one's speed in place. Before anything is heard it says "Not heard yet.";
- when to listen: the frequency, the slots and today's slot times, whether they come from GB7RDG's directory or your config, the next slot, and for a web SDR which one it is (its address, and once it has been opened, the callsign, name and location it gives), which of today's slots it listens to and the next of them;
- for a web SDR, a **Listen now** button, so a new user can see the spectrogram and the level work without waiting for the next slot. It opens the web SDR for 3 minutes, then closes it again, and counts against the daily allowance the same as a slot (at most 3 times a day). It refuses if a slot's own window is due to open in the next 5 minutes, so the two never overlap; if GB7RDG's directory moves a window closer while a session is already running, that session is cut short at once instead. It never runs the hooks, retunes a shared rig or holds LinBPQ off, since it is a look at the web SDR only. The page shows a countdown while it is open;
- the last slot: how far off frequency the tone was, its signal-to-noise ratio, and how many frames were heard;
- the ionosphere: GB7RDG's latest ionosonde reading, in words, such as "Open from about 280 km" over "Fairford foF2 6.05 MHz at 11:30 UTC (12 min old). 40 m: closed at 100 km, open from about 280 km", with the verdict at 100, 500 and 1000 km. It is a guide to whether you can expect to hear M0LTE, not a forecast, and it changes nothing about what is sent. Once the sounding is more than 45 minutes old (the head end's default) it says so instead. The receiver keeps the latest across restarts. Beside it is GB7RDG's PSK Reporter reading, what live FT8, FT4 and WSPR spots between UK and Irish stations said about 40 m over the half hour before the slot, such as "FT8 spots: open from about 520 km" over "40 m FT8/FT4/WSPR spots (PSK Reporter, last 30 min): open at 500 km (25 spots, 12 stations, median -11 dB) and 1000 km (16 spots, 9 stations, median -13 dB), nothing under 250 km despite 41 spots further out". FT8 decodes signals far weaker than the mailcast needs, so "open" there doesn't mean you will hear M0LTE; the median SNRs are the better guide. If there weren't enough spots to say, it says that; once the reading is more than 45 minutes old it says it is too old;
- your BBS: where bulletins go, how many are waiting, and any problem reaching it;
- a live spectrogram from 0 to 4 kHz, with the signal's edges, its centre at 1800 Hz and the tone marked, so you can see whether the signal sits where it should in your passband; pdn-soundmodem's full waterfall is a link away. While there is no audio it says why instead: between a web SDR's slots it says when the spectrogram comes back, for a sound card that can't be opened it gives the reason and when the receiver tries again, while SDRconnect or a web SDR is being reconnected to it says it was lost and why, and while a source is being opened again it says why the last try failed. A button below it turns the spectrogram off, and on again. This browser remembers the choice. While it is off, nothing is streamed to the page, so the input level doesn't show either. The same goes while the page's tab is hidden: it picks up again when you come back to it. With a sound card, turning the spectrogram off, or closing or hiding the tab, also lets the receiver rest between slots;
- the input level. For a sound card it has the same target as pdn-soundmodem: peaks between -18 and -9 dBFS. For a web SDR (or a recording) the level isn't yours to set and the modem copes with any level short of clipping, so it only warns about clipping;
- the channel: what the radio path from GB7RDG was like in the last slot, and over the last day (see [The Channel tile](#the-channel-tile));
- what was heard, slot by slot: every burst, and every frame in it (see [What was heard](#what-was-heard));
- what to try if nothing is heard;
- the bulletins this receiver holds (see [Bulletins](#bulletins)): one row each, newest first, a part-received one with a progress bar (named from any directory that has named it, or "not yet named" otherwise) and a complete one with what the BBS said about it and when. A summary line at the top counts how many are coming in, waiting for your BBS, delivered, already had and refused. If the newest directory heard is not today's, it says so plainly instead of naming that stale date as if it were current;
- the daily report, if you have turned it on: the last one as sent, what your BBS said, when the next goes, and whether GB7RDG got it (see [Sending a daily report](#sending-a-daily-report));
- the settings: audio, the callsigns frames are accepted from (`sources`), the BBS's address and login, and the page's own password. The USB dial is shown too; for a sound card it also offers **Measure my filter** and typing the edges, above, which writes `dialKHz`. For a web SDR or a recording it is only changed in the config file. Saving writes them to the config file (without its comments) and puts them in force at once. If you change the BBS's address, port or type, enter its password again: the saved one is never sent anywhere new without you. A **Test BBS login** button tries the login shown without exchanging any mail (see [Testing the login](#testing-the-login)).

If the receiver stops, or the page can't reach it, the page says so in one line at the top and asks again every half minute until the receiver answers. The bulletin list is only fetched again when something in it has changed.

On this machine only, the page answers to `localhost` and nothing else.

Scripts can read the same from `GET /api/status`. For the speed: `slot.waveform` is the waveform most of the last slot's frames came on (`ms110d-wn4`), or `mixed` if two tie; `slot.frameCounts` has the frames on each; `slot.bytesDecoded` is the bytes decoded from the slot's frames (each frame's information field, repeats included; not the size of the bulletins rebuilt from them); `slot.listedWaveform` is the directory's, if one was rebuilt in that slot; `slot.endedEarly` is why its window ended early, in words, if it did (see the note on this above); and `burst` is the burst now (`live: true`) or the last one frames came from, with its `waveform`, `bps` and `words`, or null before any; while the burst now has decoded no frame yet (`frames` 0), `waveform`, `bps` and `words` are null, since a lock alone can be the tone or probe. `iono` is the ionosonde reading, or null before one is heard: `state` (`GOOD`, `MARGINAL`, `POOR` or `UNKNOWN`), `foF2`, `mufd100`, `mufd500`, `mufd1000` (MHz), `skipZoneKm`, `station`, `soundingTimeUtc`, `ageMinutes` (by this receiver's clock), `source`, `method`, `distances` (each with `km`, `mufMhz` and `verdict`), and `headline` and `words`, what the page shows. `pskReporter` is the PSK Reporter reading, or null before one is heard: `state`, `observedUtc`, `ageMinutes`, `windowMinutes`, `feedDown`, `skipZoneKm`, `distances` (each with `km`, `fromKm`, `toKm`, `verdict`, `spots`, `stations`, `snrMedianDb` and `closedBy`), and `headline`, `words` and `distanceWords`. `configChanged` is true once the config file has been changed by hand since the receiver started (see [Editing the config file by hand](#editing-the-config-file-by-hand)). `update` is the update check: `latest` (the newest in the apt repository, or null before a check has worked), `current`, `newer`, `checkedAt`, and when newer, `release` (its release notes), `command` and, for a .deb installed by hand, `download`. `bbs.reachable` is null until a delivery session or a Test BBS login has actually been tried, true once one has reached the BBS and gotten an answer, false once one has been tried and never got that far; the page only offers the daily report when it is true. `feedback` is the daily report: `enabled`, and when it is on, `lastSent`, `lastAnswer` (what the BBS said, in words), `next` (when the next goes, or the last is tried again), `text` (the last report, title first) and `receipt`, what GB7RDG's directories say about your reports, or null until one that lists reports is heard: `got` (the date of your newest report GB7RDG has listed, or null), `gotSeen` (when that was first heard), `listedOn` (the date of the newest directory heard that lists reports), `gotLatest` (true when GB7RDG has your latest report) and `text`, what the page shows. `audio.state` is what the audio is doing, in words, and `audio.phase` the same as one of `starting`, `closed` (a web SDR between its slots), `opening`, `listening`, `reconnecting` (SDRconnect or a web SDR dropped and its client is getting it back), `failed` or `ended` (a recording played through); `audio.live` is true only while `listening`. `audio.problem` is why, while `reconnecting` (null if the client did not say) or `failed`, and while `opening` again after a failure, why the last try failed. For a web SDR, `audio.webSdr` says which: its `host` (`wessex.zapto.org`), a `url` for its own page, and `about`, what it says about itself once opened (callsign, name and location), or null before then. `listenNow` is null unless the audio is a web SDR; otherwise `usesLeft`, `usesMax` (3), `minutes` (3), `until` (while a session it opened is still running), `canUse` and, when it cannot be used, `problem` (why, in words). `POST /api/listen-now` (no body needed) opens a session, if one is not refused; like saving the settings, it is refused from another site and needs the page's password, if it has one. It answers `{"ok": true, "until": "..."}`, or `{"ok": false, "error": "..."}` with it. `mailVersion` is a number that changes whenever anything `GET /api/mail` shows does, so a script can fetch the list only when it differs from last time (it means nothing across a restart). `heardSlots` is the slim list "What was heard" shows, newest first: each slot's `slot`, `slotTicks` (a string of digits, its identity for the full drill-down, see [What was heard](#what-was-heard)), `bursts` and `frames` counted.

`directory` is the newest directory in use, with `date`, `entries`, `stale` and `signature` ("signed by GB7RDG" or "not signed"). `lastDirectory` is the directory heard most recently, used or not: `date`, `state` (`signed`, `unsigned` or `rejected`), `signedBy`, `reason` (why it was rejected) and `words`. `GET /api/status` also still carries `bulletins` and `deliveries`, kept in their old shape for any script already reading them there; the page itself now reads the same facts from `GET /api/mail` instead (see [Bulletins](#bulletins)).

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

- From the channel probe: 1.5 s after the tone, M0LTE sends 6.5 s of a known test signal made for measuring paths. The receiver finds it from where the tone ended, and reads every path's delay to a few microseconds and its Doppler, down to about -15 dB signal to noise, well below anything it can decode. Its fades are part of the path and count, however deep; if the probe is cut short or the audio drops out, it measures what was heard.
- From the data bursts it decoded: each one can be made again exactly, so it is a known signal too, about 20 times a second. This works down to about 1 dB.

When it has both, it shows the one that sees further below the strongest path, with the other beside it. A slot with neither says "Not enough decoded to measure."

The tile says it in a line, such as "Last slot (16:00 UTC): 2 paths: 1 hop, and 2 hops 1.9 ms later, 17 dB weaker. Reflection about 290 km up. Doppler spread 0.2 Hz: steady (good for 1200 bps)." Below that:

- the delay profile: how much signal arrived how long after the strongest path, with each path marked. One hop off the F layer is 1F, two hops 2F, and so on; 1E (the E layer) is only considered more than 300 km from Reading. Naming the hops needs to know where your receiver is, which a web SDR says; with a sound card the paths are shown without names. The height comes from the time between the strongest path and its second hop, and hardly depends on the distance, so it is given either way. A second or third hop is only named when its delay fits a reflecting layer 200 to 450 km up; anything arriving before the strongest path is an artefact of the measurement and is ignored. When nothing fits, the paths are shown unnamed and no height is given;
- the delay spread (how much the echoes smear each symbol), the Doppler spread (how much the moving layer blurs the frequency), how deep the fades went (how far the signal fell below its middle level a tenth of the time), and how long the signal stays steady;
- what it was measured from, with the other measurement's summary when there were both;
- a strip of the last 24 hours, one column per hourly slot, with a dot for each path at its delay, darker for stronger.

When your receiver's position is known (a web SDR's position, or a sound card with its `locator` set) a 2 or more hop path's ground bounces are named on the picture: the nearest town on land ("near Aberystwyth"), or at sea the sea's name and, where the point also falls inside one, the shipping forecast area ("Celtic Sea (Lundy)"; said once, not twice, where the two names are effectively the same, such as "Irish Sea"). The bounce points are estimates (equal hops along the great circle), good to a few tens of km, so the labels say "near" and claim nothing more precise; a single hop path shows nothing new. This needs no internet at any point: it is built from three small files baked into the receiver, regenerated by `tools/ground-bounce-data/build.py` and credited here as each source's licence requires.

- Towns: [GeoNames](https://www.geonames.org/), under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/): the `cities15000` extract (population over 15000, or a capital) across every bearing a bounce can fall in from GB7RDG out to 2000 km, plus the finer `cities5000` extract (over 5000) within the core of Britain and Ireland, where most receivers in practice are.
- Sea names: Flanders Marine Institute (2018). *IHO Sea Areas*, version 3 (<https://doi.org/10.14284/323>), under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/), clipped to the same region and heavily simplified (this only ever names roughly where a bounce estimate falls, not a coastline).
- Shipping forecast areas: Met Office, National Meteorological Library and Archive, [Factsheet 8 "The Shipping Forecast"](https://www.metoffice.gov.uk/binaries/content/assets/metofficegovuk/pdf/research/library-and-archive/library/publications/factsheets/factsheet_8_shipping_forecast_2025.pdf), Table 1. &copy; Crown copyright, under the [Open Government Licence v3.0](https://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/).

Doppler spread under 0.5 Hz is steady and good for 1200 bps; up to 1 Hz, 1200 bps should still cope; beyond that, 600 bps copes better. It is a guide, and it changes nothing about what is sent.

The measuring runs on a thread of its own after the slot, never alongside the decoding, and rests between bursts so it uses at most half of one core. On a Raspberry Pi 4 a slot of 7 bursts takes about 5 seconds of one core, and the probe about 2 seconds more. It keeps the audio from 1 s before each tone's end to 12 s after for the probe (13 s, about 1.25 MB), and only bursts it read frames from, heard within 15 minutes of a slot's start: at most 12 of them (and 4 minutes of audio, about 25 MB) per slot until then. A burst longer than the 75 s of audio it keeps is left out, and the log and tile say so. The results for the last day are kept in `channel.json` in the state directory. `--decode` measures too, after delivering, and only logs the result.

In `GET /api/status`, `channel` has the last slot: `slot`, `basis` (`probe` or `bursts`), `enough`, `modes` (each with `label`, `delayMs` after the strongest, `powerDb` against the strongest, `dopplerShiftHz`, `dopplerSpreadHz` and `seenIn`), `delaySpreadMs`, `dopplerSpreadHz`, `fadeDb`, `coherenceS`, `virtualHeightKm`, `snrDb`, `offsetHz`, `distanceKm` (null when the receiver's position is not known), `floorDb` (how far below the strongest path the profile reaches), `words`, `other` (the same slot measured the other way, with `basis`, `enough`, `modes`, `delaySpreadMs`, `dopplerSpreadHz`, `virtualHeightKm`, `snrDb`, `offsetHz`, `floorDb`, `measurements`, `kept` and `words`; null without both), `tooLong` (bursts left out since start), the `profile` (`startMs`, `stepMs`, `db`), and `history`, the last day's slots, each with its `basis`. Before anything has been measured, `slot` is null.

### Where it bounced

Below Today, a section closed by default draws the day's paths from GB7RDG to your receiver on a globe: a sphere 6371 km in radius, to scale, with nothing exaggerated. It appears once the receiver knows where it is (a web SDR's position, or `locator` for anything else), and only for slots whose hops could be labelled. Each path is drawn as the receiver models it: straight rays from the ground up to a mirror at the virtual height measured and back down, the bounces spaced equally along the great circle, as on the Path picture. The real wave bends gradually through a layer tens of km thick and turns round lower than that, but its delay is that of the mirror at the virtual height, which is what the receiver measures; an ionosonde's h'F is a virtual height too. "Play the day" steps through the slots, and "Side view" flies the camera out to look along the Earth's edge, with the layer drawn round the whole globe at its height.

The coastlines are in the receiver (about 300 kB), and the page only fetches them, from `/globe.json`, when the section is opened, so it needs no internet. They come from [Natural Earth](https://www.naturalearthdata.com/), which is in the public domain, and `tools/globe-data/build.py` regenerates them.

### What was heard

For every burst in the last few slots, and every frame in each burst, the status page's **What was heard** section shows what it carried: the object it belongs to (a bulletin's BID and title, a directory, a propagation reading and its source, or a content type this receiver doesn't handle), its piece number, whether it was new, already held, or completed that object, and whether its CRC was good. It's for checking coverage and for debugging: a slot with a lot of duplicates, or one where most frames don't decode, shows up here. Click a slot to open it; each burst shown gives when it started, its waveform, its signal to noise once the slot's channel measurement has run, and its frame count.

It's in memory only, never written to disk, so it starts empty again after a restart: it isn't an archive, just a live look at what the receiver is hearing now. It holds at most 6 slots, or 2000 frames in total, whichever comes first; the oldest slot is dropped to make room, except the newest slot is never dropped even if it alone holds more than 2000 frames. Recording it happens off the audio thread entirely, from the same worker that writes pieces to the store, so it costs nothing on the decoder.

`GET /api/slots/{slotTicks}/frames`, behind the page's password if it has one, gives one slot's full drill-down as JSON: `slot`, and `bursts` (each with `started`, `waveform`, `words`, `snrDb` (null until the slot's channel measurement has run, or if it never does), `frames` (a count) and `pieces`, each with `heard`, `esi` (the piece number, null for a frame that didn't parse), `status` (`new`, `alreadyHeld`, `completedObject`, `rejected`, `notRecognised` or `unknownDictionary`), `crcGood` and `what` (the object it belongs to, in words)). `slotTicks` comes from `heardSlots` in `GET /api/status`, above: the slot's identity in .NET ticks, given as a string of digits so it survives JavaScript intact (as a number it is too big to keep exactly) and goes straight into the URL with nothing to encode. Anything else in its place answers 400, and a slot not (or no longer) held answers 404.

## Bulletins

The receiver keeps its own copy of every bulletin it rebuilds, so nothing is lost if the transfer to your BBS fails, or if the BBS later loses or refuses a bulletin.

A rebuilt bulletin waits in the outbox until your BBS has answered for it: accepted, already had (it has that BID) or refused. Then it moves to the archive with that answer and when it came. Copies are kept for `archive.days` (30) and up to `archive.maxMegabytes` (50 MB) in all, the oldest going first. Bulletins still waiting are never removed. Only bulletins are kept; nothing else the receiver hears goes to the BBS or the archive.

The status page's **Bulletins** section lists everything this receiver holds, newest first, 25 to a page: a part-received object on top (a progress bar, named from any directory that has named it, or "not yet named" otherwise), then every complete bulletin, with its BID, from, to, @, title, date, size, and status. Each complete one also shows when it was completed (rebuilt), and, once archived, when it was delivered to your BBS. A waiting bulletin shows the BBS's last answer, if any, and the next try. A summary line at the top counts how many are coming in, waiting for your BBS, delivered, already had and refused (issue #86: already had is its own count, not folded into delivered), plus, when the newest directory heard is not today's, a note saying so.

GB7RDG sends each bulletin for a day or so, then moves on. If this receiver hadn't gathered enough pieces by then, the bulletin can't be finished. The receiver notices when a list of what's being sent (a directory) leaves it out, and the row then says so plainly: "GB7RDG has stopped sending this one, so it can't be finished. It will be cleared at 14:05 UTC." It is cleared 24 hours after the receiver notices, and the summary line counts it as no longer sent rather than coming in. If GB7RDG starts sending it again, or a later list names it again, the row goes back to normal and keeps its pieces. This only applies to pieces the receiver knows are a bulletin (a list has named it, or it holds the first piece); anything else waits the usual 14 days. A part-received bulletin GB7RDG is still sending, but that hasn't had a new piece for 14 days, is cleared too.

Click a complete one to read the whole bulletin as it will reach the BBS: its header lines, its R: lines and its text, what the BBS has said about it each time it was offered, and **Send to BBS again**. **Open as text** shows it on its own. A problem reaching your BBS at all (no answer, a login refused) is not about any one bulletin, so it shows in the **Your BBS** tile instead, not here. A delivery record for a bulletin since pruned from the archive (by `archive.days` or `archive.maxMegabytes`) has nothing left to open and so drops out of this section, though it is still in `GET /api/status`'s `deliveries` for as long as that keeps it (normally the shorter of the two).

**Send to BBS again** puts an archived bulletin back in the outbox, and the next session offers it. If your BBS still has it, it says so by its BID, nothing is sent twice, and the bulletin stays down as accepted. Each one sent again is logged, the same one can only be sent again once every 2 minutes, and at most 50 sent again can wait for the BBS at once.

### Signed directories

A transmitter can sign its directory, the list of what it is sending, so you can tell its bulletins from a stray or forged source. GB7RDG's key is built in; add others with `trustedKeys`. Each bulletin's row says which:

- **signed by GB7RDG**: this receiver checked the bulletin it rebuilt against the fingerprint a directory signed by a key you trust gives for it, and it matched. Only then.
- **does not match GB7RDG's signed list**: it was delivered before any directory listed it (as always), and the signed directory heard since gives a different fingerprint. The log says so. One that a signed directory already lists, and that does not match, is not delivered at all.
- **in GB7RDG's signed list**: a signed directory lists it, but it is not complete yet, or was rebuilt by an older receiver that kept no fingerprint, so nothing has been checked.
- **not signed**: only directories with no signature list it, which is how every directory was before signing, and how a transmitter without a key still sends. It is delivered as usual.

A directory whose signature does not check, or is signed by a key you have not added, is not used at all: not for names, not for the list, and not to decide what has stopped being sent. The log says why, once for each such directory, and the summary line says "The last list heard was not used" with the reason. A bulletin that only such a directory lists is held when it completes, not delivered; the row says so, and it goes to your BBS as soon as a good directory lists it too.

A bulletin shows this while a directory naming it is remembered, which is 48 hours. A held bulletin stays held across a restart, until a good directory lists it or 14 days pass.

What signing does not stop yet: a directory with its signature cut off is just an unsigned one, and is used as today; a bulletin no directory lists is still delivered, as today; and a signed directory can be sent again the same day, which is no worse than before. A later setting to accept signed directories only, not built yet, would close the first two.

The copies are a convenience: if one cannot be written (a full disk, say), that is logged, and a bulletin your BBS has taken leaves the outbox all the same. A refused one is moved to `store/quarantine/` instead, so it is kept without holding up newer mail.

The same is there for scripts, behind the page's password if it has one:

- `GET /api/mail?offset=0&limit=50`: the list as JSON, newest first (at most 200 at a time): `total`, `waiting`, `archived`, `delivered`, `alreadyHad` and `refused` (counts; issue #86: `delivered` means the BBS accepted the bulletin outright, and `alreadyHad` is its own count, no longer folded into `delivered`), `partial` (every part-received object, with `bid`, `title`, `received`, `needed`, `noLongerSent` (true once GB7RDG has stopped sending it), `clearsAt` (when such a one will be cleared, otherwise null), `signature` (as below) and `withheld` (true for a complete bulletin held because only a directory that failed its check lists it); never paged, since there are normally few of them), `archive` (`days`, `maxMegabytes`), `signature` (the newest directory in use: "signed by GB7RDG" or "not signed"), `rejected` (why the last directory heard was not used, or null) and `items`, the page itself, each with `attempts`, what the BBS has said about that BID each time it was offered (oldest first), alongside its final `verdict`, `completed` and `delivered` times, and `signature` (as on the page, see [Signed directories](#signed-directories); null once no directory remembered names it).
- `GET /api/mail/<id>`: one bulletin as plain text, with the `id` from the list.
- `POST /api/mail/resend` with `{"id": "<id>"}` as `application/json`: sends one again. Like saving the settings, it is refused from another site.

On disk they are in the state directory: waiting ones in `store/outbox/`, archived ones in `store/archive/`, one file each. A file that cannot be read is moved to `store/quarantine/` and logged, and the receiver carries on.

## What it logs

Everything goes to the journal (`journalctl -u pdn-mailcast-receiver`; on Windows the service writes `receiver.log` beside its config file instead), one plain line each: the audio source, GB7RDG's slots and where they come from (the config, or GB7RDG's directory once heard), which slots a web SDR listens to each day, the tone (`tone: 1801.3 Hz, +1.3 Hz from 1800 Hz, SNR 14.2 dB in 3 kHz, 10 s`; it reads 8 to 11 s for the 10 s tone, and the CW ident that follows on the same frequency is not counted), the speed of the first frame of each slot (`slot: 1 frame heard on 1200 bps (WN4)`) and of any frame that came at another speed, one line summing each slot up once its window is over (after the closing CW ident, at the window's usual close or as soon as it ends early: `slot: the 11:00 UTC slot ended: 125 frames, 120 on 1200 bps (WN4) and 5 on 600 bps (WN3); 29.4 KB decoded from them; tone SNR 14 dB, +1.2 Hz off; bulletins 3 completed, 1 delivered; directory heard, signed`, with the bulletins already had and given up when there were any, and `window ended early, nothing left to wait for` when it did; a slot listened to that nothing at all was heard in says `slot: the 11:00 UTC slot ended: nothing heard`, and one not listened to (a web SDR skipping it, or the receiver stopped) gets no line; "decoded" is the bytes in the frames heard, repeats and all, not the size of the bulletins rebuilt from them, and 1 KB is 1000 bytes), each bulletin as it completes, each directory with whether it is signed (`directory for 2026-10-15: 21 bulletins in rotation, sent on 1200 bps (WN4), signed by GB7RDG`) or why it is not used, a bulletin held or not delivered because of a directory's signature, a part-received one when GB7RDG has stopped sending it and again when it is cleared, each ionosonde reading as it arrives (one line a slot), what the BBS said about it, each one sent again from the page, the login exchange when a delivery session fails at login (`bbs: delivery login: sent ...` and `bbs: delivery login: the BBS answered ...`, as for [Testing the login](#testing-the-login), with nothing about the password), a newer version once when it is first seen (`update: version 0.7.0 is available (you have 0.6.0); the status page says how to upgrade`), and with the daily report on, what your BBS said about it (`feedback: the report for 2026-10-06 (MCR G4ABC 2026-10-06, 458 bytes) was accepted by the BBS`) and, once for each, when GB7RDG's directory first lists it (`feedback: GB7RDG got the daily report from G4ABC for 2026-10-06, as its directory for 2026-10-07 says`). The record of deliveries is also kept in `deliveries.jsonl` in the state directory.

## Building from source

You need the .NET 10 SDK.

```
dotnet publish src/Mailcast.Receiver -c Release -o out
out/pdn-mailcast-receiver --config src/Mailcast.Receiver/receiver.example.json
```

`scripts/build-receiver-deb.sh linux-x64 0.1.0` builds the .deb into `artifacts/`.

The test suite includes two tests that decode a simulated slot into a real BBS in docker, one LinBPQ and one Linux FBB (built from `tests/Mailcast.Receiver.Tests/Fbb` on first use). They are tagged `Category=Docker`: `dotnet test --filter Category=Docker` runs them, and `--filter "Category!=Docker"` leaves them out.
