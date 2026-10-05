# The head end

The head end runs beside GB7RDG's BBS and its pdn-soundmodem. It takes bulletins from the BBS as a forwarding partner, and once a day broadcasts them through the station's own modem. It never touches the radio directly: the frames go over KISS to a broadcast modem in pdn-soundmodem, and the lease and the calibration tone go through pdn-soundmodem's HTTP API.

## What happens each day

At 12:00 UTC (`slot.timeUtc`) the head end:

1. Collects any bulletins waiting at the BBS, for 30 s at most (`intake.preSlotSeconds`) so a BBS that does not answer cannot hold the slot up, and plans the day's frames.
2. Reads the Flex's frequency reference and PA temperature, if configured, and logs whether it is GPS locked. It does not start with the PA already over the limit.
3. Opens the broadcast modem's KISS port, then takes the transmit lease for that modem's sub-channel. From here on pdn-soundmodem refuses everyone else's transmissions. The lease is renewed every 30 s, and tells the station to send each burst anyway once it has waited 10 s for a clear channel (`station.maxCarrierWaitSeconds`).
4. Waits for the station's channel-busy flag to clear, for up to 2 minutes (`slot.channelWaitSeconds`), then sends the 30 s calibration tone at 1800 Hz as the lease holder's transmitter test. If the channel never clears it goes ahead without the tone (`"whenStillBusy": "go"`) or gives up for the day (`"skip"`).
5. Pauses 8 s so the modem's CW ident, which falls due with the first transmission, goes out before the first burst.
6. Sends the frames a burst at a time (see below), checking the PA temperature every 5 s.
7. Releases the lease, dropping anything of ours not yet on the air. The station sends its closing CW ident first (holding the lease up to 60 s for it), and normal packet service resumes.

It stops early, cleanly, if a lease renewal fails, the PA passes `flex.paTemperatureLimitC`, the PA watch is lost and `flex.whenUnreachable` is `"skip"`, the KISS connection fails, the modem stops acknowledging frames, or the next burst would run past `slot.maxMinutes` (40). It then asks the station at once to drop whatever of ours is not yet keyed; a burst already on the air finishes inside the lease.

Whatever was not sent is owed: the next plan sends it on top of that day's share, with fresh ESIs. Each burst is recorded as queued before any of it is written to the modem, and nothing is written if that fails, so a crash or restart never repeats a piece, the directory's included. A slot cut short by the head end stopping carries on when it starts again within `slot.catchUpMinutes` (30). One skipped for a reason at the station (no KISS port, no lease, no Flex when it is required, a hot PA) is tried again 5 minutes later (`slot.retryMinutes`), within the same window.

## How the bursts are paced

pdn-soundmodem packs frames queued together into one burst, up to the modem entry's `maxBurstSeconds`, after waiting `burstGatherSeconds` for the rest of a run to arrive. The head end works out how many frames fit (about 7 full frames in 60 s on WN4, from the modem's own modulator) and writes them over KISS together, as ACKMODE frames. pdn-soundmodem acknowledges every frame of a packed burst at once, when the burst has been handed to the sound card. Only when all of them are acknowledged does the head end queue the next burst, after a 1 s pause. So pdn-soundmodem never holds more than one burst of ours.

Before queueing a burst the head end checks that the lease it holds will outlast the burst by 15 s, and renews first if not. The 10 s carrier limit fits inside those 15 s, and the station drops our unkeyed frames itself if the lease runs out, so a burst either keys inside the lease or not at all. That is why the lease is 120 s rather than 60: a burst queued just after a renewal must finish inside the lease even if the next renewal, 30 s later, fails. With a 60 s lease a 60 s burst could not. Each lease call gives up after 30 s (a release after 90 s, as it waits for the closing ident), so a station that stops answering is noticed in time. Waiting for the slot, the head end looks at the clock at least once a minute, so a box that booted with a wrong clock and is then corrected does not sleep through it, and the unit starts after `time-sync.target`.

## GB7RDG's pdn-soundmodem

It needs pdn-soundmodem with burst packing (#544) and the transmit lease (#545) with its dropQueued, channel-busy flag, maxCarrierWaitSeconds and closing ident, an API key, and one more modem entry for the broadcast, on its own sub-channel and KISS port:

```json
{
  "mode": "ms110d-wn4",
  "subChannel": 4,
  "port": 8112,
  "rfFrequency": 7051500,
  "maxBurstSeconds": 60,
  "burstGatherSeconds": 0.5,
  "identify": { "callsign": "GB7RDG", "intervalMinutes": 10 }
}
```

`rfFrequency` places the signal at 7.0515 MHz with the station's band plan; without one, `frequency` is the audio centre (1800 Hz on a 7.0497 MHz dial). The sub-channel and port are examples: use any free ones, and put the same numbers in the head end's `station.subChannel` and `station.kissPort`, with `station.kissPortNibble` 0 for a per-modem port. LinBPQ should not be given this port.

## GB7RDG's LinBPQ

The head end is a forwarding partner that calls LinBPQ, logs in on its FBBPORT and takes the bulletins LinBPQ has queued for it. LinBPQ never calls the head end, so the partner needs no connect script. This setup is tested against a real LinBPQ 6.0.25.41 in docker (`tests/Mailcast.HeadEnd.Tests/LinBpq`):

1. In `bpq32.cfg`, in the Telnet port's `CONFIG`, make sure there is an `FBBPORT` and add a user for the head end. Leave the fourth field empty: the head end sends `BBS` itself.

   ```
    FBBPORT=8011
    USER=Q0HEAD,pick-a-password,Q0HEAD,,
   ```

2. In the mail configuration, add a user **Q0HEAD** and tick **BBS**.

3. On Q0HEAD's forwarding page, tick **Allow Blocked**, **Allow Compressed** and **Use B1 Protocol**. Set **HR** (flood bulletin routes) to `WW` and **BBS HA** to `Q0HEAD.#42.GBR.EURO`, so it gets the same flood bulletins as any partner in the UK: @WW, @EURO and @GBR all reach it. Leave **TO**, **AT** and the personal HR routes empty, so personal mail is never queued for it. It does not need to be enabled for forwarding, and needs no connect script.

4. Put the password in the head end's `intake.fbb.password`, with `"port": 8011`.

GB7RDG's existing bulletin filters apply as for any partner, because LinBPQ chooses what to queue. Local bulletins (@GB7RDG) are not queued, which is right: they are for GB7RDG's own users.

Q0HEAD is a Q callsign, which is never issued, so it cannot clash with a station, and it is never sent on the air (frames come from GB7RDG). FBB-style BBSs only accept logins shaped like a callsign. It must not be the receiver's login (Q0CAST) or GB7RDG itself.

If LinBPQ ever offers the head end a personal message or NTS traffic, the head end answers `=` (later), so LinBPQ keeps it queued rather than counting it delivered, and logs a WARNING naming the BID every time it is offered: the partner's routes need fixing, and the message sending on by hand. Bulletins over the size cap, or with a BID already held, are answered `-`.

### Later, packet.net's BBS

pdn-bbs speaks the same FBB B1F forwarding, and its `fbbTcp` listener (BPQ's FBBPORT equivalent) asks only for the callsign. Add Q0HEAD as a partner there with the same bulletin-only routing, and change the head end's login to `[{ "expect": "Callsign :", "send": "{call}" }]` with pdn-bbs's port. pdn-bbs hands a partner that dials in whatever it holds for it, as LinBPQ does.

## Bulletins by file

`intake.dropDirectory` takes bulletin files too, checked every minute: one bulletin per file in Mailcast.Core's serialised form (a `Type:`, `From:`, `To:`, `At:`, `Bid:`, `Date:` and `Title:` header, a blank line, then the message text with its R: lines). A file taken in is deleted; one refused moves to `rejected/` with the reason in the journal. Name a file `.tmp` or start it with a dot while writing it, then rename it. Either way, only bulletins (type B) up to `intake.maxBulletinBytes` (32 KB) are taken, each BID once, and the day it is first seen is the first of its three carrying days.

## The Flex

With `flex.enabled`, the head end opens its own API session to the Flex for the length of each slot. It is a second, non-GUI client that only reads: it subscribes to the radio's status (for the frequency reference) and its meters (for PA temperature), with the radio's keepalive on so a dead session is noticed, and never asks for a slice, a DAX stream or the transmitter, so it cannot disturb pdn-soundmodem's slice. A PA reading older than 15 s (`flex.paStaleSeconds`) counts as none. If the radio cannot be reached, or the readings stop during the slot, the head end logs it and carries on without the PA watch, or with `"whenUnreachable": "skip"` skips the day or stops the slot.

## Status

`http://127.0.0.1:8216/status` (`status.bind`, `status.port`) is a small JSON document: whether the head end is waiting or in a slot, the next slot, bulletins held, the last intake, and the last slot's start, end, outcome and reason, frames planned, queued and sent, bursts, bulletins in rotation, whether the tone went, the PA temperature maximum and the reference state. The journal carries the same in plain lines, for example:

```
slot 2026-10-05: starting at 12:00:00Z, 171 frames for 34 bulletins in 25 bursts, about 21.6 min on the air
slot 2026-10-05: Flex reference GPS locked (GPSDO locked)
slot 2026-10-05: transmit lease taken for sub-channel 4, 120 s, renewed every 30 s
slot 2026-10-05: calibration tone sent, 30 s at 1800 Hz
slot 2026-10-05: done, 12:00:00 to 12:23:31Z, 171 of 171 frames sent in 25 bursts, 34 bulletins in rotation, tone sent, PA max 41.0 C, reference GPS locked (GPSDO locked)
```

## Offline

`pdn-mailcast-headend --plan` prints what the day's slot would send. `pdn-mailcast-headend --wav slot.wav` renders the whole slot to a WAV file with pdn-soundmodem's own MS110D modem: the tone, the CW ident where the station would send it, and the frames. `--bulletins DIR` takes a directory of bulletin files instead of the head end's store, `--date` picks the day, and `--rate` the sample rate (48000 by default). Neither opens a connection to anything or changes the head end's state.

The published pdn-soundmodem package cannot pack frames yet, so offline every frame is its own burst, about 0.8 s longer each than on the air. A receiver decodes it the same way.

## Configuration

`/etc/pdn-mailcast-headend/headend.json`; the package seeds it from `headend.example.json`, which lists every key with its default. Only `station.apiKey` (the station's `api.key`) has no default, and the service does not start without it. The package does not start the service on a first install: set up the station and the BBS, fill in the API key and the BBS password, then `systemctl start pdn-mailcast-headend`. `pdn-mailcast-headend --check-config` checks a file.

`scripts/build-headend-deb.sh linux-x64 VERSION` builds the package (also `linux-arm64` and `linux-arm`), laid out like the receiver's: the binary in `/usr/lib/pdn-mailcast-headend`, state in `/var/lib/pdn-mailcast-headend`.

## What the head end expects of pdn-soundmodem's lease

The head end uses #545's lease as it stands at f6722b3: `{"subChannel": N, "seconds": S, "maxCarrierWaitSeconds": W}` to take or renew; `{"release": true, "subChannel": N, "dropQueued": true}` to release and drop our unkeyed frames, answered once the closing ident has gone (up to 60 s, so the head end allows 90); `{"dropQueued": true, "subChannel": N}` to drop them and keep the lease; and the `channelBusy` flag in `GET /api/txlease`. The station drops the holder's unkeyed frames itself when a lease runs out. `src/Mailcast.HeadEnd/Station/StationApi.cs` is the one place that knows these shapes.
