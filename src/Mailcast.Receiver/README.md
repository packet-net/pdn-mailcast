# pdn-mailcast receiver

Hears M0LTE's experimental mailcasts on 40 m, transmitted from GB7RDG's station, sent every hour on the hour in daylight, and passes each bulletin to your LinBPQ, like a forwarding partner would. It never transmits, and you don't need a radio.

You need a Linux machine (a Pi is fine) running LinBPQ with its mail, or a Windows one: see [Windows](#windows). It doesn't have to be on the same machine as LinBPQ.

## 1. Install

```
sudo apt install curl gpg
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

SDR users: nothing else to do. If your radio's own receive filter is narrower, especially 2.4 kHz or less, the right dial is 7.0538 MHz minus the middle of that filter, and the settings page works it out for you: either **Measure my filter** (it listens to about 10 s of band noise with nothing tuned in and reports the filter and the dial), or type the filter's low and high edge in Hz yourself. A worked example: a filter from 367 to 2190 Hz (an FT-450D's narrow setting) gives a dial of 7.05252 MHz. Either way a button writes the dial for you; `"dialKHz"` in `receiver.json` takes it too.

## Using pdn-soundmodem

If pdn-soundmodem (0.89.0 or later) already runs your radio on this machine, set `"audio": "soundmodem"` (or `"soundmodem:PORT"` if its page is not on port 8107) and restart the receiver. It listens through pdn-soundmodem's own audio, with no `ms110d` modem needed there, and **Measure my filter** works the same. Its dial follows pdn-soundmodem's when pdn-soundmodem gives one. If pdn-soundmodem's radio is usually on another frequency, add `"retune": true` and the receiver takes pdn-soundmodem's receive window for each slot: pdn-soundmodem tunes its radio to `dialKHz` and holds its own transmitting until the slot is over (pdn-soundmodem needs a `rig` section for this). See [Retuning through pdn-soundmodem](../../docs/receiver.md#retuning-through-pdn-soundmodem).

Moving from pdn-soundmodem's built-in receiver (its `mailcast` section)? [Moving from pdn-soundmodem's built-in receiver](../../docs/moving-from-built-in.md) says what to copy across and how to switch over.

## Using an SDRplay radio through SDRconnect

If your SDRplay radio (an RSP1A, say) runs in SDRconnect on a Windows PC, the receiver can listen to it over your network and tune it itself. No audio cable, real or virtual, is needed.

On the Windows PC:

1. Update SDRconnect to 1.0.6 or later, and run only one copy of it.
2. In SDRconnect, go to **Other Actions**, **Preferences**, **WebSocket Server** and switch it on (it starts off). Leave its port at 5454.
3. Make sure Windows treats your network as **Private** (Settings, Network & internet): SDRconnect's firewall rule only lets the receiver in on a Private network. If it has to stay Public, allow TCP port 5454 in.
4. Note the PC's IP address (`ipconfig` shows it). Use a wired connection if you can: the receiver takes the radio's raw signal, about 8 Mbit/s, which Wi-Fi does not always keep up with.

On the receiver's machine, open its status page (see [3. Start it](#3-start-it)) and, under **Settings**:

1. For **Audio**, choose **SDRconnect (SDRplay) on another computer**.
2. In the box below it, put the PC's address after `sdrconnect:`, such as `sdrconnect:192.168.1.20` (add `:PORT` if you changed SDRconnect's port).
3. Fill in **Your locator**.
4. Click **Save**. It takes effect at once, with nothing to restart.

Or set `"audio": "sdrconnect:192.168.1.20"` in `/etc/pdn-mailcast/receiver.json` and restart the receiver.

The page should then say it is listening to SDRconnect, with the level meter moving. If it can't connect, the page and the log say why, and it keeps trying.

While the receiver runs, leave SDRconnect's tuning alone: the receiver tunes it for 7.052 MHz USB (the radio itself sits a little below that) and puts it back if it is moved.

## Windows

The receiver runs on Windows 10 or 11 (x64) too. Download `pdn-mailcast-receiver_VERSION_win-x64.zip` from the [latest release](https://github.com/packet-net/pdn-mailcast/releases/latest) and unzip it somewhere to keep, such as `C:\Program Files\pdn-mailcast-receiver`. There is no .NET to install. Then, from an administrator PowerShell in that folder:

```
powershell -ExecutionPolicy Bypass -File .\install-service.ps1
```

That makes the config, `C:\ProgramData\pdn-mailcast\receiver.json`, from the example, and runs the receiver as a Windows service that starts at boot and starts again a minute after any failure. Its log is `receiver.log` in the same folder, and its pieces and bulletins are in `state` there. Step 2 is the same with BPQ32 on Windows. The page is http://127.0.0.1:8130/ as before. Upgrading means stopping the service (`sc.exe stop pdn-mailcast-receiver`), unzipping the new version over the old folder, and starting it again; the page says when there is one.

For a sound card, give the recording device by the name Windows shows for it, such as `"audio": "soundcard:Microphone (USB Audio Device)"`, or just `"soundcard:USB Audio Device"` if you have only one of it. The settings page lists the ones it can see. Turn off audio enhancements for that device in Windows' sound settings. To try it without the service, run `pdn-mailcast-receiver.exe` from a command prompt; Ctrl+C stops it. The zip's `README.txt` has the rest.

## Linux FBB

Instead of step 2: add a telnet port in `port.sys`, add user **Q0CAST** (`EU Q0CAST`) with the **B** and **M** flags and a password, and don't add it to `forward.sys`. In the receiver's config set `"type": "fbb"` and FBB's telnet port. (Not tested yet.)

## More

The settings, the status page, the logs and building from source are in [docs/receiver.md](../../docs/receiver.md).
