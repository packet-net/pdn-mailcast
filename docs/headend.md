# The head end

The head end runs beside GB7RDG's BBS and its pdn-soundmodem. It takes bulletins from the BBS as a forwarding partner, and sends them in slots through the station's own modem: every hour on the hour in daylight at GB7RDG, or once a day for a station configured that way. It never touches the radio directly: the frames go over KISS to a dedicated modem in pdn-soundmodem, and the lease and the calibration tone go through pdn-soundmodem's HTTP API.

## When the slots are

A slot starts at `slot.timeUtc` and then every `slot.everyMinutes`, round the clock. `everyMinutes` must divide a day (1440) and be at least 15. Left out it is 1440, one slot a day, which is how a head end configured before hourly slots carries on unchanged. GB7RDG uses `"timeUtc": "00:00"` and `"everyMinutes": 60`, a slot every hour on the hour, which is what receivers expect.

### Daylight only

40 m NVIS only carries over UK paths in daylight. On 2026-10-05 GB7RDG's slots at 07:12 and 18:00 UTC, an hour after sunrise and twenty minutes after sunset, were heard by nobody, while those from 09:12 to 16:00 decoded well. So GB7RDG runs only the slots that start in daylight:

```json
"slot": {
  "timeUtc": "00:00",
  "everyMinutes": 60,
  "daylight": { "locator": "IO91lk", "afterSunriseMinutes": 120, "beforeSunsetMinutes": 30 }
}
```

A slot runs if it starts between `afterSunriseMinutes` after sunrise and `beforeSunsetMinutes` before sunset at `locator` (a 4 or 6 character Maidenhead locator, taken at its centre), both edges included. The head end works out sunrise and sunset for each day from the date alone, with NOAA's solar equations, to within about a minute of the almanac; there is no network lookup. Everything is UTC, so summer time makes no difference. At IO91lk that is:

| Day | Sunrise and sunset (UTC) | Slots |
|---|---|---|
| 5 October | 06:11 and 17:33 | 09:00 to 17:00, 9 slots |
| 21 December | 08:07 and 15:57 | 11:00 to 15:00, 5 slots |
| 21 June | 03:47 and 20:25 | 06:00 to 19:00, 14 slots |

The other slots are passed over without a word, except for one line in the journal each day listing that day's slots (see Status below). A day with no slot (polar night, or offsets longer than the day) has none; polar day runs every slot. A configuration under which no slot in a whole year would run is refused. Left out, every slot runs, as before.

The head end's directory carries the slot times and the daylight rule, so receivers follow the head end rather than their own settings once they have heard it.

A few settings have a different default for a daily station and for anything more often:

| Setting | Daily (1440) | Hourly and other intervals |
|---|---|---|
| `slot.toneSeconds` | 30 | 10, which receivers expect from an hourly station |
| `slot.maxMinutes` | 40 | 10, so a slot ends while a web SDR receiver, which listens to 12 minutes past, is still there |
| `slot.catchUpMinutes` | 30 | 5 |
| how bulletins are carried | three days running (the shares rule) | each slot filled to its airtime budget (the budget rule, see below) |

## What happens in each slot

At each slot the head end:

1. Collects any bulletins waiting at the BBS, for 30 s at most (`intake.preSlotSeconds`) so a BBS that does not answer cannot hold the slot up, and plans the slot's frames. If no bulletin has anything to send, it keys nothing, not even the directory, and waits for the next slot. Under the budget rule that only happens when nothing is in rotation.
2. Checks that the system clock is synchronised (the kernel's own flag, as timedatectl shows it), and keys nothing until it is (`slot.requireClockSync`); a slot it skips for this is retried. It never runs a slot earlier than the last one it ran, whatever the clock says.
3. Reads the Flex's frequency reference and PA temperature, if configured, and logs whether it is GPS locked. It does not start with the PA already over the limit.
4. Opens the bulletin modem's KISS port, then takes the transmit lease for that modem's sub-channel. From here on pdn-soundmodem refuses everyone else's transmissions. The lease is renewed every 30 s, and tells the station to send each burst anyway once it has waited 10 s for a clear channel (`station.maxCarrierWaitSeconds`).
5. Waits for the station's channel-busy flag to clear, for up to 2 minutes (`slot.channelWaitSeconds`), then sends the calibration tone at 1800 Hz (`slot.toneSeconds`, 10 s at GB7RDG) as the lease holder's transmitter test, with the channel probe after it in the same keyup (see below). If the channel never clears it goes ahead without the tone (`"whenStillBusy": "go"`) or gives up on the slot (`"skip"`).
6. Pauses 8 s so the modem's CW ident, which falls due with the first transmission, goes out before the first burst.
7. Sends the frames a burst at a time (see below), checking the PA temperature every 5 s.
8. Releases the lease, dropping anything of ours not yet on the air. The station sends its closing CW ident, keeping the lease `closing` for up to 60 s while it does, and normal packet service resumes.

### The channel probe

Every tone is followed by a channel probe: 1.5 s of silence, then 6.5 s of pdn-soundmodem's `zc255` probe on the tone's own audio frequency, so it lands where the tone does on the air (1800 Hz at the receivers). Receivers measure the path from it: how many modes there are, their delays and their Doppler, even when the bursts are too weak to decode. It adds about 8 s to each slot, and the estimate counts it.

There is no setting for it. It needs pdn-soundmodem 0.87.2 or later; an older station sends the tone alone and the journal says so. The tone and the probe together must fit the station's `txTest.maxSeconds` (30 by default): a 10 s tone fits, but the daily default of 30 s does not, so a daily station either raises `maxSeconds` to 40 or shortens the tone. If the station refuses the pair because of the probe (it does not understand it, or the two pass `maxSeconds`), the head end asks for the tone alone at once. Any other refusal, such as Stop on the station page or a channel that would not clear, is answered as before, so Stop still wins. The probe never costs a slot its tone or its bulletins.

It stops early, cleanly, if a lease renewal fails, the PA passes `flex.paTemperatureLimitC`, the PA watch is lost and `flex.whenUnreachable` is `"skip"`, the KISS connection fails, the modem stops acknowledging frames, or the next burst could run past `slot.maxMinutes` from the slot's time (10 at GB7RDG). That check allows for the burst's gather, its longest carrier wait, the station's delay and the closing ident after it, so nothing keys past the hard stop however busy the channel. It then asks the station at once to drop whatever of ours is not yet keyed; a burst already on the air finishes inside the lease.

Whatever was not sent is owed: the next slot sends it on top of its own share, with fresh ESIs. Each burst is recorded as queued before any of it is written to the modem, and nothing is written if that fails, so a crash or restart never repeats a piece, the directory's included. The price is that a burst cut off part way (a KISS failure, an abort) is under-sent: its unwritten pieces are spent, and the next slot makes the shortfall up with fresh ones. A slot cut short by the head end stopping carries on when it starts again within `slot.catchUpMinutes` (5 at GB7RDG); later than that, the next slot sends the rest. One skipped for a reason that may clear (an unsynchronised clock, no KISS port, no lease, no Flex when it is required, a hot PA) is tried again 5 minutes later (`slot.retryMinutes`), within the same window.

## How each bulletin is carried

### Fill to budget (the default for hourly slots)

Each slot is filled to an airtime budget with fresh pieces of every bulletin in rotation, the least covered first. A bulletin's coverage is the pieces sent of it so far divided by its K (the number of pieces it is cut into), so new bulletins come first. Each slot first gives every bulletin enough that any 3 slots rebuild it (a third of K plus a spare piece), then shares the rest out, still the least covered first, but never more than 0.6 K of one bulletin in one slot. So a bulletin is spread over the day's slots rather than spent in one, a quiet day sends short slots, and a busy day sends full ones. A bulletin retires after 6 K in all, or 36 hours after its first slot, whichever comes first; it then leaves the slots and the directory.

The budget is 10 minutes on the air (`schedule.budgetMinutes`, `slot.maxMinutes` if left out), tone, pause, gaps and closing ident included. A slot is never filled closer to the 10 minute hard stop than the 2 minutes it may wait for a clear channel before its tone (`schedule.marginSeconds`, `slot.channelWaitSeconds` if left out, none without a tone), plus 11 s for the last burst's gather and carrier wait. So GB7RDG's slots are filled to 7 min 49 s. A slot that starts late (a catch-up or a retry) is filled that much less, because the hard stop counts from the slot's time: it ends when an on-time one would, while web SDR receivers are still listening at 12 minutes past.

The estimate counts the frames as the station packs them into 18 s bursts (`station.maxBurstSeconds`), each burst's airtime from the modem's own modulator for the slot's waveform, about 3 s to take the lease, the tone plus 1 s, the channel probe (8 s), the pause, 2.5 s more per burst than its airtime (gathering, keying and the acknowledgement, measured), the 1 s gaps and 8 s for the closing ident. Against GB7RDG's slots, which went out before the probe (so the estimate here leaves it out):

| Slot | Frames | Bursts | Measured, start to release | Estimate |
|---|---|---|---|---|
| 2026-10-05 16:00 (30 s tone) | 41 | 7 | 161.5 s | 162.5 s |
| 2026-10-06 09:00 (10 s tone) | 33 | 6 | 119.7 s | 120.5 s |

In a simulated October day with about 20 bulletins, that is 9 slots of 7.4 to 7.8 minutes on WN4, 140 to 149 frames each, carrying 22 to 26 bulletins. Each bulletin got about 6 K over about 10 slots, and in that simulation any 3 of them were enough to rebuild it. `tests/Mailcast.HeadEnd.Tests/BudgetTests.cs` checks this, a busy day and a quiet one, at both rates.

A bulletin that has had pieces sent but has no `FirstSlot` in its `state.txt` (one an older head end sent) is in rotation again from its `NextEsi`, and its next slot counts as its first.

The settings, in `schedule`:

- `rule`: `"budget"`, or `"shares"` for the fixed shares below. Left out, `budget` for anything but a daily station, unless the shares keys are set.
- `budgetMinutes`: 10 (`slot.maxMinutes`).
- `marginSeconds`: 120 (`slot.channelWaitSeconds`, or 0 without a tone).
- `spreadSlots`: 3, so any 3 slots rebuild a bulletin.
- `slotCap`: 0.6, as a multiple of K.
- `retireCoverage`: 6, as a multiple of K.
- `retireHours`: 36.

### A waveform per slot

`schedule.modes` lists waveforms the slots take in turn, for example `["ms110d-wn4", "ms110d-wn3"]` to compare 1200 and 600 bps on real mail. Consecutive slots take turns, and the next day starts one further on, so each hour gets each waveform on alternate days. Left out, every slot goes on `station.mode` and the modem is left alone.

With `modes` set, the head end switches the broadcast modem before each slot with a KISS SETHW frame on its KISS port (the waveform number, then 0 for the short interleaver), and waits for pdn-soundmodem's echo, which confirms it. It takes effect from the next burst, and nothing of ours is queued then. If no echo comes in 5 s it keys nothing and tries again like any slot skipped at the station. After the slot, however it ends (a missing echo, a stop, a broken connection, on a new connection if need be), it switches the modem back to `station.mode`. It does the same whenever it starts with an MS110D `station.mode`, whether or not `modes` is set, so a slot cut off by a crash never leaves WN3 set for long. The setting lives in pdn-soundmodem's memory only, so a pdn-soundmodem restart also puts it back.

Receivers need do nothing: pdn-soundmodem's MS110D receiver reads each burst's waveform from its preamble (autobaud), and every mailcast receiver release uses it. A WN3 frame takes about twice as long, so a WN3 slot carries about half as much: in the simulated October day alternating WN4 and WN3, a bulletin got about 4.7 K over about 14 slots, and a listener needed about 5 of them rather than 3. The journal's slot lines, the slot report (`mode`) and the directory (`mode=ms110d-wn3`) all name each slot's waveform.

### The shares rule: in daylight (GB7RDG until fill to budget)

A bulletin's first slot is the first slot planned after it is taken in, which for one that arrives at night is the morning's first. That slot carries 1.2 times its K (the number of pieces it is cut into) plus 2 spare pieces, enough to rebuild it from that slot alone with about a fifth of the frames lost. It goes out once more 5 hours later with 0.4 K of fresh pieces, for a receiver that lost more. A repeat that falls in the dark moves to the next daylight slot, so none is lost; if a bulletin has several repeats, each moves to a slot of its own, never one an earlier carrying already has, and the carry-over slots count only daylight slots. Pieces never repeat, whatever moves where.

Why less than the hourly plan below: all of a day's carrying has to fit in the daylight slots, and in December there are only 5. On GB7RDG's volume (about 25 bulletins a day, about 60 KB once compressed, in 240-byte pieces on WN4 in 18 s bursts), the hourly plan's five carryings would fill every December slot to its 10 minute hard stop and still not finish. The daylight plan averages, tone and idents included:

| Season | Daylight slots | Average daylight slot | Worst |
|---|---|---|---|
| December | 5 a day | about 4 minutes | 10, the morning's first, cut at the hard stop and finished in the next |
| October | 9 a day | about 2.6 minutes | 10, likewise, about once in three days |
| June | 14 a day | about 1.7 minutes | about 8 |

`tests/Mailcast.HeadEnd.Tests/DaylightAirtimeTests.cs` checks this on a sample of that size, with each slot stopping at 10 minutes as a real one does. The morning's first slot is the long one because it carries everything that arrived overnight. Even the first carrying alone would average about 3.6 minutes in December, so the repeat is kept small; in summer there is room for more, and `slotShares` can say so.

A web SDR receiver hears 8 of each day's daylight slots (see the receiver's documentation): in December that is all of them, and in June 8 of 14. It rebuilds a bulletin from its first slot alone, so on these settings it gets those whose first slot it hears: all of them in December, about 19 in 20 in October, and about 5 in 6 in June. A receiver on its own radio hears everything.

### The shares rule: every hour

A bulletin's first slot is the first slot planned after it is taken in. That slot carries enough of it for a clean rebuild from that slot alone: 1.5 times its K (the number of pieces it is cut into) plus 2 spare pieces, which still rebuilds it with a fifth of the frames lost. It then goes out again with fresh pieces, never repeats, 5, 10, 17 and 25 hours later, 0.7 K each time, so a listener who missed that hour still gets it, and it is heard in five different hours of the day over a little more than a day. Pieces from different slots add together at the receiver.

A web SDR receiver listens to every third slot (8 a day), to stay inside the web SDR's allowance. Two of the four repeats fall in each of the two sets of every third slot that miss a bulletin's first slot, so whichever set a receiver hears, it gets at least 1.4 K of every bulletin. A receiver on its own radio hears everything.

If a repeat's slot is skipped or cut short, the next slot makes up what it owed. After the last repeat a bulletin stays in rotation for 3 more slots (`schedule.carryOverSlots`) to make up a shortfall, then leaves it. The directory goes out in every slot that keys, listing every bulletin in rotation.

On GB7RDG's volume (about 25 bulletins a day, about 200 KB, about 60 KB once compressed, in 240-byte pieces on WN4 in 18 s bursts), that is about 3 minutes on the air in an average hour, tone and idents included (about 74 minutes a day), under 6 in 19 hours out of 20, and up to about 8 in the busiest. `tests/Mailcast.HeadEnd.Tests/AirtimeBudgetTests.cs` checks it on a sample of that size.

### The shares rule's settings

With `"rule": "shares"`, these settings, in `schedule`, tune it; left out, they are the defaults above:

- `slotShares`: the pieces for each carrying, as a multiple of K, the first for the first slot: `[1.2, 0.4]` with `daylight`, `[1.5, 0.7, 0.7, 0.7, 0.7]` without. Each is rounded up on its own.
- `slotOffsets`: which slot each carrying is in, counted in slots (hours) from the first, dark ones included: `[0, 5]` with `daylight`, `[0, 5, 10, 17, 25]` without. One for each share, starting at 0. Give shares without offsets and they are spread over the same span.
- `extraSymbols`: spare pieces on top of the first carrying's share: 2.
- `carryOverSlots`: 3.

For another interval the defaults keep the same hours, in that interval's slots. A daily station's defaults are three days running, `[1.4, 0.3, 0.3]` with 1 spare piece. The daily station's old keys `daysCarried`, `totalOverhead` and `dayShares` still work: each day's share of the total becomes a slot share, the carryings a day apart. A config can use those or the new ones, not both.

Under the shares rule, a bulletin first carried before slots had times is counted as first carried at midnight UTC on the day it was taken in.

## A slot on demand

`pdn-mailcast-headend --run-now` asks the running head end for a slot now, between the scheduled ones, for a test transmission say. It goes through the status listener: `POST /run` on `status.bind` and `status.port` (127.0.0.1:8216), and only from the same machine, so nothing on the LAN can key the transmitter. The head end answers 202 with the slot it started, named by the minute it starts in, or 409 if a slot is already running, a one-off is already starting, or the clock is behind the last slot run. The journal says who asked (the `X-Requested-By` header, which `--run-now` fills with the user, and the address).

It is a whole slot with the usual checks: the clock, the lease, the tone, the bursts and the idents. It runs whatever the time, daylight or not. It counts like any other slot, so no piece is ever sent twice: it sends what a scheduled slot would (a full slot under the budget rule; what is owed and the first share of anything new under the shares rule), and the directory even if nothing else is due. A bulletin first carried in a one-off slot at night has its repeat in daylight like any other. The schedule carries on as before; the next scheduled slot still runs, and sends nothing again that the one-off sent.

## How the bursts are paced

pdn-soundmodem packs frames queued together into one burst, up to the modem entry's `maxBurstSeconds`, after waiting `burstGatherSeconds` for the rest of a run to arrive. The head end works out how many frames fit (about 7 full frames in 60 s on WN4, from the modem's own modulator) and writes them over KISS together, as ACKMODE frames. pdn-soundmodem acknowledges every frame of a packed burst at once, when the burst has been handed to the sound card. Only when all of them are acknowledged does the head end queue the next burst, after a 1 s pause. So pdn-soundmodem never holds more than one burst of ours.

Before queueing a burst the head end checks that the lease it holds will outlast the burst by 15 s, and renews first if not. The 10 s carrier limit fits inside those 15 s, and the station drops our unkeyed frames itself if the lease runs out, so a burst either keys inside the lease or not at all. That is why the lease is 120 s rather than 60: a burst queued just after a renewal must finish inside the lease even if the next renewal, 30 s later, fails. With a 60 s lease a 60 s burst could not. Each lease call gives up after 30 s, so a station that stops answering is noticed in time. Waiting for the slot, the head end looks at the clock at least once a minute, so a box that booted with a wrong clock and is then corrected does not sleep through it, and the unit starts after `time-sync.target`.

## GB7RDG's pdn-soundmodem

It needs pdn-soundmodem with burst packing (#544) and the transmit lease (#545) with its dropQueued, channel-busy flag, maxCarrierWaitSeconds and closing ident, an API key, and one more modem entry for the bulletins, on its own sub-channel and KISS port:

```json
{
  "mode": "ms110d-wn4",
  "subChannel": 4,
  "port": 8112,
  "rfFrequency": 7053800,
  "maxBurstSeconds": 18,
  "burstGatherSeconds": 0.5,
  "identify": { "callsign": "GB7RDG", "intervalMinutes": 10 }
}
```

`rfFrequency` places the signal at 7.0538 MHz with the station's band plan (receivers tune 7.052 MHz USB). Set `txAmplitude` to 1.0 on this entry (pdn-soundmodem 0.85.0 or later): the modem's default of 0.5 peaks at only about 0.38 of full scale, and on GB7RDG's Flex that is about 12 W of data against about 41 W at 1.0. Because the station's dial sits below the signal, the head end's `slot.toneHz` is the tone's audio frequency on that dial: 4050 Hz on GB7RDG, so the tone lands on 7.0538 MHz.

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

`intake.dropDirectory` takes bulletin files too, checked every minute: one bulletin per file in Packet.Mailcast's serialised form (a `Type:`, `From:`, `To:`, `At:`, `Bid:`, `Date:` and `Title:` header, a blank line, then the message text with its R: lines). A file taken in is deleted; one refused moves to `rejected/` with the reason in the journal. Name a file `.tmp` or start it with a dot while writing it, then rename it. Either way, only bulletins (type B) up to `intake.maxBulletinBytes` (32 KB) are taken, each BID once, and the next slot is the first it is carried in.

## The Flex

With `flex.enabled`, the head end opens its own API session to the Flex for the length of each slot. It is a second, non-GUI client that only reads: it subscribes to the radio's status (for the frequency reference) and its meters (for PA temperature), with the radio's keepalive on so a dead session is noticed, and never asks for a slice, a DAX stream or the transmitter, so it cannot disturb pdn-soundmodem's slice. A PA reading older than 15 s (`flex.paStaleSeconds`) counts as none. If the radio cannot be reached, or the readings stop during the slot, the head end logs it and carries on without the PA watch, or with `"whenUnreachable": "skip"` skips the slot or stops it.

## The ionosonde reading

The head end keeps an eye on the ionosphere over Reading and tells receivers what it sees: whether 40 m is open at 100, 500 and 1000 km, and where the skip zone ends. It is observe only. It never changes what or when the head end sends; its two small frames go in slots that key anyway, inside the airtime budget, and a slot with nothing else to send still keys nothing.

It asks GIRO first, then PROPquest, for Chilton, then Fairford, then Dourbes, at most every 15 minutes and only from an hour before a slot to a quarter of an hour after one. A source that answers 429 or 5xx, or not at all, is left alone for longer each time (15 minutes, then 30, up to 4 hours, or its Retry-After). The asking never holds a slot up: a slot takes whatever reading is at hand, and a failure only ever makes the reading UNKNOWN. Soundings often reach both sources hours late, so UNKNOWN (no sounding in the last 45 minutes) is common; the reading then gives the last values and their age.

A distance counts as open when its MUF is at least 7.1 MHz and reliable when 0.85 times it is. GOOD means reliable at 100 and 500 km, POOR closed at all three, MARGINAL anything between. Where the source has no MUF for a distance, it is worked out from foF2 and M(3000)F2 by ITU-R P.533's basic MUF ([design.md](design.md#the-ionosonde-reading) has the details). It does not model D-layer absorption. The thresholds and stations are in `ionosphere` in the configuration (`openMhz`, `reliableFactor`, `staleMinutes`, `stations`); `"stations": []` turns it off.

Each slot logs one line about it, for example:

```
ionosonde: MARGINAL, Fairford FF051 at 2026-10-06 11:30Z (12 min old, PROPquest): foF2 6.05 MHz, MUF 6.65/8.21/11.64 MHz at 100/500/1000 km (MUFs estimated from foF2 and M(3000)F2); 100 km closed, 500 km open, just, 1000 km good; skip zone about 280 km; sent in 2 frames
```

Requests carry the User-Agent `pdn-mailcast-headend/VERSION (+https://github.com/packet-net/pdn-mailcast; GB7RDG)`, with the station's callsign.

## The PSK Reporter reading

The ionosonde says what the ionosphere could carry; live spots say what it is carrying. So the head end also subscribes to PSK Reporter's public MQTT feed for FT8, FT4 and WSPR spots on 40 and 80 m between stations in the UK and Ireland, and from the last 30 minutes of them says whether 40 m is open at 100, 500 and 1000 km, with the counts behind each verdict and a skip zone. It is observe only, exactly like the ionosonde's reading: its two small frames ride in slots that key anyway, and nothing about what or when the head end sends depends on it.

It only calls a distance open with at least 5 spots from 4 stations, and never calls one closed just because it is quiet: closed needs the band busy at the other distances (40 spots, 12 stations), or 80 m busy at that distance while 40 m is not. Otherwise it says it doesn't know. Open means open for FT8, which decodes far weaker signals than MS110D needs, so the median SNRs matter as much as the verdicts. [design.md](design.md#the-psk-reporter-reading) has the details and the record. 80 m is judged too, for this log and the status, but isn't sent.

It stays subscribed from an hour before a slot to a quarter of an hour after one, so the window is full by the slot. A lost connection is tried again after 5 s, then 10, 20 and so on up to 5 minutes; a broker that goes quiet for 90 s counts as lost. It holds at most 20,000 spots (about 4 MB) and throws away anything out of bounds, so a misbehaving broker can't eat the node's memory. If the feed was up for less than 20 of the last 30 minutes, the reading is UNKNOWN. A slot never waits for it.

Each slot logs one line, for example:

```
pskreporter: MARGINAL, last 30 min to 14:00Z (0 min old), UK and Ireland FT8/FT4/WSPR: 40 m 100 km closed (0 spots), 500 km open (90 spots, 18 stations, median -10 dB), 1000 km open (30 spots, 10 stations, median -12 dB); skip zone about 320 km; 80 m (not sent) 100 km open (20 spots, 9 stations, median -3 dB), 500 km too few spots (0 spots), 1000 km too few spots (0 spots); no skip zone, open close in; sent in 2 frames
```

`pskReporter` in the configuration has `enabled` (true), `host` (`mqtt.pskreporter.info`) and `port` (1883). The window and thresholds are fixed.

## Status

`http://127.0.0.1:8216/status` (`status.bind`, `status.port`) is a small JSON document: whether the head end is waiting or in a slot, the next slot, the slots run today (`slotsToday`: how many, and how many completed, were cut short or were skipped, each slot counted once by its latest run), bulletins held, the last intake, and the last slot: its start time (`slot`), start, end, outcome and reason, frames planned, queued and sent, bursts, bulletins in rotation, whether the tone went, whether the channel probe went whole (`probeSent`) and its id (`probeId`, `zc255-2400-rrc015-v1`), the PA temperature maximum, the reference state, who asked for it if it was a one-off (`requestedBy`), its waveform (`mode`), the planner's estimate of its time on the air (`estimatedSeconds`), the ionosonde reading it planned with (`ionosphere`) and how many frames carried it (`ionosphereFrames`), and the same for PSK Reporter (`pskReporter`, `pskReporterFrames`). `iono` is the reading as of now: `state`, `foF2`, `mufd100`, `mufd500`, `mufd1000`, `skipZoneKm`, `station`, `soundingTimeUtc`, `ageMinutes`, `source`, `method`, and the verdict at each distance (`at100`, `at500`, `at1000`). `pskReporter` is the PSK Reporter reading as of now: `state`, `observedUtc`, `windowMinutes`, `feedDown`, `at100`, `at500`, `at1000`, `skipZoneKm`, and `forty` and `eighty`, each with its `bins` (`km`, `fromKm`, `toKm`, `verdict`, `spots`, `stations`, `snrMedianDb`, `closedBy`) and `skipZoneKm`. `pskReporterFeed` says how the feed is doing: `connected`, `since`, `lastMessage`, `spotsHeld`, `messages`, `reconnects` and `problem`. `recent-slots.json` in the state directory keeps the same for the last slots. The journal carries the same in plain lines, each slot named by its start, and with `daylight`, once a day, that day's sunrise, sunset and slots, for example:

```
daylight 2026-10-05 at IO91lk: sunrise 06:11Z, sunset 17:33Z; 9 slots, 09:00, 10:00, 11:00, 12:00, 13:00, 14:00, 15:00, 16:00 and 17:00 UTC
next slot 2026-10-05 13:00Z; 38 bulletins held
slot 2026-10-05 13:00Z: starting at 13:00:03Z, 64 frames for 38 bulletins in 10 bursts on ms110d-wn4, about 3.6 min on the air with the tone and idents
slot 2026-10-05 13:00Z: Flex reference GPS locked (GPSDO locked)
slot 2026-10-05 13:00Z: transmit lease taken for sub-channel 4, 120 s, renewed every 30 s
slot 2026-10-05 13:00Z: calibration tone sent, 10 s at 1800 Hz, then the channel probe (zc255-2400-rrc015-v1, 8 s more)
slot 2026-10-05 13:00Z: done, 13:00:03 to 13:03:29Z, 64 of 64 frames sent in 10 bursts on ms110d-wn4, 38 bulletins in rotation, tone sent, probe sent, PA max 41.0 C, reference GPS locked (GPSDO locked)
slots today (2026-10-05): 14, 13 completed, 0 cut short, 1 skipped
```

## Offline

`pdn-mailcast-headend --plan` prints what a slot would send, and about how long it would be on the air. `pdn-mailcast-headend --wav slot.wav` renders the whole slot to a WAV file with pdn-soundmodem's own MS110D modem: the tone and the channel probe, the CW ident where the station would send it, and the frames. `--bulletins DIR` takes a directory of bulletin files instead of the head end's store, all of them new in that slot. `--date` picks the day and `--time` the slot, the one running at that time (`slot.timeUtc` if left out), daylight or not, and `--rate` the sample rate (48000 by default). Neither opens a connection to anything or changes the head end's state.

The published pdn-soundmodem package cannot pack frames yet, so offline every frame is its own burst, about 0.8 s longer each than on the air. A receiver decodes it the same way.

## Configuration

`/etc/pdn-mailcast-headend/headend.json`; the package seeds it from `headend.example.json`, which lists every key, with GB7RDG's hourly daylight slot settings and the defaults for the rest. Only `station.apiKey` (the station's `api.key`) has no default, and the service does not start without it. The package does not start the service on a first install: set up the station and the BBS, fill in the API key and the BBS password, then `systemctl start pdn-mailcast-headend`. `pdn-mailcast-headend --check-config` checks a file.

`schedule.symbolSize` sets the frame size: the bytes of bulletin each frame carries, 64 to 940 in steps of 4 (940 if left out). Smaller frames survive fades and other stations' transmissions better, and since a bulletin rounds up to whole pieces they waste less on short bulletins too. On 2026-10-05 a busy 40 m band left almost nothing of 940-byte frames, and 240 is the size to try first. Receivers read the size from each frame, so nothing changes at their end. A bulletin keeps the size it was first encoded with.

`scripts/build-headend-deb.sh linux-x64 VERSION` builds the package (also `linux-arm64` and `linux-arm`), laid out like the receiver's: the binary in `/usr/lib/pdn-mailcast-headend`, state in `/var/lib/pdn-mailcast-headend`.

## What the head end expects of pdn-soundmodem's lease

The head end uses #545's lease as it stands at f6722b3: `{"subChannel": N, "seconds": S, "maxCarrierWaitSeconds": W}` to take or renew; `{"release": true, "subChannel": N, "dropQueued": true}` to release and drop our unkeyed frames, answered at once, after which the lease stays `closing` for up to 60 s while the closing ident goes; `{"dropQueued": true, "subChannel": N}` to drop them and keep the lease; and the `channelBusy` flag in `GET /api/txlease`. The station drops the holder's unkeyed frames itself when a lease runs out. `src/Mailcast.HeadEnd/Station/StationApi.cs` is the one place that knows these shapes.
