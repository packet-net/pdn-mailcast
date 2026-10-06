# pdn-mailcast design

Status: draft, 2026-10-04; built and on the air since 2026-10-05, every hour since v0.2.0, every daylight hour since v0.3.0.

## What it is

Every hour on the hour in daylight, GB7RDG sends its recent bulletins on 40 m. Nobody replies on air. Each listening station runs one program, the receiver, which hears some or all of the transmission, rebuilds each bulletin and forwards it into the local BBS as if it came from a forwarding partner. The BBS's own duplicate check (by BID) throws away anything it already has, so the sender never needs to know who is listening or what they hold.

Two ideas make a one-way link work:

- **Fountain coding.** Each bulletin is cut into pieces, and the sender can make as many different pieces as it likes. Any set of pieces slightly larger than the bulletin rebuilds it, whichever ones they are. A receiver that misses a third of the transmission in a fade still gets its bulletins, and pieces heard in different slots add together. Using a fountain code was Perry M0PYL's idea.
- **Frames that are either perfect or absent.** The transmission uses pdn-soundmodem's MS110D modes (MIL-STD-188-110D Appendix D), which carry IL2P frames with a CRC. A frame arrives intact or is discarded. That is exactly the kind of loss fountain codes handle best.

## Decisions so far

| Question | Decision |
|---|---|
| What is sent | Bulletins only. No personal mail. |
| Who sends | GB7RDG, a Flex 6500 on 40 m. |
| When | Every hour on the hour in daylight, from 2 hours after sunrise to 30 minutes before sunset near Reading: 09:00 to 17:00 UTC in early October, 11:00 to 15:00 in midwinter, 06:00 to 19:00 in midsummer. A short slot of a few minutes. Until 2026-10-05 it was once a day at 12:00 UTC, which a head end can still be set to. |
| Where | Centred on 7.0538 MHz (USB dial 7.052 MHz), occupying about 7.0522 to 7.0554 MHz, clear of the UK HF packet channels at 7.0503, 7.05095 and 7.0516 MHz. |
| Receivers | Linux, with LinBPQ or FBB mail. |
| Receiver packaging | One program with pdn-soundmodem's library embedded from NuGet. No separate pdn-soundmodem install and no KISS link. |
| Fountain code | RaptorQ (RFC 6330). |
| Compression | zstd with a trained dictionary. |
| Licence | AGPL-3.0. |

## How much there is to send

Measured on GB7RDG's mail store on 2026-10-04. Since bulletin forwarding started on 2026-09-26, GB7RDG has taken 20 to 35 bulletins a day, about 200 KB of text. For the week of 27 September to 3 October:

| | Bytes | Share of raw |
|---|---|---|
| Raw, 197 bulletins | 1,379,619 | 100% |
| gzip, each bulletin on its own | 626,359 | 45% |
| xz, the whole week as one file | 284,800 | 21% |

Compressing each bulletin with a trained zstd dictionary should land between those two, around 30%, so about 60 KB a day. 26 of the 197 were 7plus pictures, most of which the filters added on 2026-10-04 now keep out.

The first plan carried each bulletin on three days running, about twice its compressed size in pieces in total. That is about 120 KB a day:

| Mode | Rate | Airtime a day |
|---|---|---|
| `ms110d-wn4` (BPSK, rate 2/3) | 1200 bps | about 15 minutes (20 to 24 measured, see below) |
| `ms110d-wn3` (BPSK, rate 1/3) | 600 bps | about 30 minutes |

WN4 is our strongest on-air-proven MS110D point and is the starting choice. WN3 is the fallback if receivers struggle.

Measured once the core was built: the trained 64 KB dictionary brings a held-out week to 30% of raw (23% without 7plus), against 45% for plain zstd. Pieces are whole, though, and the average bulletin is only about 2.3 pieces, so "twice over" rounds up. On that week it comes to about 19 minutes a day of WN4 without 7plus and 24 with them.

The first on-air runs, on 2026-10-05, used 240-byte pieces rather than 940, which survive a busy band far better. Six bulletins went out as 41 frames in 7 bursts of up to 18 s, and were rebuilt at 5 of the 6 UK and Irish web SDRs tried.

### Hourly

From v0.2.0 GB7RDG sends every hour on the hour instead. A bulletin goes out first in the slot after it arrives, with enough pieces to rebuild it from that slot alone (1.5 times its pieces, plus 2), then again 5, 10, 17 and 25 hours later with 0.7 times its pieces each time, always fresh ones. So it is heard in five different hours of the day over a little more than a day, and a listener who missed one hour gets it later. A web SDR receiver listens to every third slot, to stay inside its allowance; whichever third it hears, it gets at least 1.4 times each bulletin's pieces. [headend.md](headend.md) has the details and the settings.

That is about twice as many pieces as the daily plan, plus a tone, idents and the directory every hour. On GB7RDG's volume, in 240-byte pieces, it comes to about 3 minutes on the air in an average hour (about 74 minutes a day), under 6 in 19 hours out of 20, and about 8 in the busiest hour.

### Daylight

40 m NVIS only carries over UK paths in daylight. On 2026-10-05, at GB7RDG (IO91lk, near Reading, sunrise about 06:10 UTC and sunset about 17:35), the slots at 07:12 and 18:00 UTC were heard by nobody, while those from 09:12 to 16:00 decoded well. From v0.3.0 GB7RDG keeps to the hourly slots that start between 2 hours after sunrise and 30 minutes before sunset. Sunrise and sunset come from a small solar calculator in the core library (NOAA's equations: the sun's declination, the equation of time and the hour angle), good to about a minute up to 60 degrees of latitude, so neither end needs the internet. Everything is UTC, so summer time plays no part.

With only 5 daylight slots in December, the hourly plan's five carryings would not fit, so in daylight a bulletin goes out twice: 1.2 times its pieces plus 2 in its first slot, then 0.4 times 5 hours later, or in the next daylight slot if that is dark. On GB7RDG's volume that is about 4 minutes in an average December daylight slot, about 2.6 in October and 1.7 in June. The morning's first slot, which carries everything that came in overnight, is the long one, and in winter it reaches the 10 minute hard stop; the next slot sends the rest. [headend.md](headend.md) has the details.

The head end's directory says when its slots are and what its daylight rule is, so a receiver that has heard it follows the head end even if its own settings differ.

### Filling each slot

The fixed shares left most slots short and some empty: on 2026-10-06 the 09:00 slot carried only the two bulletins that came in overnight, and at 10:00 nothing was due, so nothing went out, not even the directory. Twenty older bulletins sat unsent, because an older head end had not recorded their first slot.

So the head end now fills every slot to an airtime budget instead (the default for hourly slots). Each slot sends fresh pieces of every bulletin in rotation, the least covered first, spread so that a few slots rebuild each one, until it retires after 6 times its pieces or 36 hours. The stranded bulletins rejoin from where they stopped. A slot is filled to a little under 8 minutes, so even after the longest wait for a clear channel it ends inside the 10 minute hard stop, and a slot that starts late is filled less, so it ends when an on-time one would. In a simulated October day of about 20 bulletins that is 9 slots of 7.4 to 7.8 minutes. The old fixed shares are still there, as `"rule": "shares"`. [headend.md](headend.md) has the details.

### A waveform per slot

To compare 1200 and 600 bps on real mail, slots can take turns between waveforms, WN4 and WN3 say. The head end switches pdn-soundmodem's modem before each slot and back afterwards. Receivers need do nothing, since every mailcast receiver reads the waveform from each burst (autobaud). A WN3 slot carries about half as much. The slot report, the journal and the directory say which waveform each slot used.

## Each slot

1. The head end listens first. If the channel is busy, it waits, up to a limit.
2. It takes a transmit lease on GB7RDG's modem (see below), so nothing else can key the radio during the slot.
3. 10 seconds of steady tone at the centre frequency (30 for a daily station), then a CW ident.
4. Bursts of up to 18 seconds, each with its own MS110D preamble, so a receiver that tunes in late or loses sync in a fade picks up at the next burst.
5. A CW ident at least every 10 minutes and at the end.
6. The lease is released and normal packet service resumes. Nothing keys past 10 minutes from the slot's time, closing ident included: before each burst the head end allows for the longest it could wait for the channel, and stops if that would run over. Anything left goes in the next slot.

### Frequency

The signal is centred on **7.0538 MHz**. On USB, a dial of **7.052 MHz** puts that centre at 1800 Hz audio, which is the MS110D standard's own audio centre, and receivers tune there. The first plan was 7.0515 MHz, in the middle of 7.050 to 7.053, but on 2026-10-05 that turned out to sit on top of the UK HF packet channels, which wiped out most frames even with a strong signal. 7.0538 MHz keeps the whole signal above 7.052 MHz.

How much of the 3 kHz segment the signal fills is still to be settled (see the filter study below). With the standard's suggested pulse shaping, MS110D occupies about 2.9 kHz, 99% of its power falling inside 7.0501 to 7.0529 MHz.

### The tone

The tone gives each receiver three things:

- a mark on its spectrogram showing whether the signal sits where it should in the passband;
- its frequency offset from GB7RDG;
- the slot's signal-to-noise ratio, logged.

GB7RDG's Flex is normally GPS locked, so the tone is a true frequency reference. The head end checks the Flex's reference before the slot and says so in the log if it is running on its internal TCXO instead (it was, briefly, on 2026-09-28). Even unlocked it is close: in August the Wessex web SDR, which is GPS disciplined, measured GB7RDG's dial at +1.98 Hz. MS110D's receiver tracks offsets of tens of Hz without help, so the tone is a check rather than a necessity.

### Power and duty

Tom suggested about 40 W, into an SWR near 1. FlexRadio rates the 6000 series at "100% ICAS". Their support manager said RTTY or FT8 "all day" at full power is fine, because the PA is derated and has thermal protection. About 3 minutes an hour of short bursts at 40% of rated power is well inside that.

MS110D's PSK signal peaks at about twice its average power. If 40 W is the Flex's power setting, the average is about 20 W. If 40 W is the average, the peaks reach about 80 W, which is still within rating, but ALC needs watching. The tone is full carrier, so it is the hardest part of the slot on the PA; at 10 seconds an hour that is far less than the daily plan's 30. The head end can read the Flex's PA temperature and stop the slot if it climbs past a limit, and we watch it on the first few runs.

## Keeping other traffic off the air during the slot

GB7RDG's pdn-soundmodem serves LinBPQ (later packet.net) on its other KISS ports, on the same slice and the same transmitter. During the slot, none of that traffic may key the radio.

**What exists today:** `POST /api/config` applies a one-run configuration. That configuration could be one without LinBPQ's ports and with only the bulletin modem, and the next restart returns to the file. It works, but it costs two restarts a slot. Each restart drops LinBPQ's KISS links (they come back within about 25 seconds) and kills any HF sessions in progress. If the head end died mid-slot, the station would stay in its bulletin-only configuration until something restarted it, so it needs a systemd timer on the node as a backstop.

**Proposed instead:** a small addition to pdn-soundmodem, a transmit lease.

- `POST /api/txlease` with a sub-channel and a duration of about 60 seconds. The head end renews it every 30 seconds.
- While a lease is held, frames from every other sub-channel are refused and logged rather than queued. Refusing is better than queueing because a queue would release a burst of stale frames when the lease ends, while AX.25 simply retries.
- Receive carries on as normal, and KISS connections stay up.
- If the head end dies, the lease runs out within a minute and normal service resumes on its own.

## Head end (GB7RDG)

- **Source.** A forwarding partner of GB7RDG's BBS that accepts bulletins only and refuses everything else. pdn-bbs already has LinBPQ-compatible FBB B1F forwarding, which is reused here, so this keeps working when GB7RDG moves to packet.net.
- **Selection.** BPQ's own forwarding rules choose what reaches the partner, so GB7RDG's existing filters apply unchanged. Bulletins over a size cap (32 KB to start) are skipped.
- **Content.** Each bulletin is sent exactly as GB7RDG would forward it to a partner, routing (R:) lines included.
- **Radio.** The head end does not embed the modem. It uses the pdn-soundmodem already running the Flex: a new `ms110d-wn4` modem entry on its own KISS port, plus the transmit lease. Two programs cannot share the radio cleanly.
- **Schedule.** A slot every hour on the hour, in daylight. Each bulletin goes out in two daylight slots, five hours or a night apart. Its first slot gets enough to rebuild it; the later one sends fresh pieces, never repeats.

## On-air format

Every frame is an AX.25 UI frame from `GB7RDG`, which takes care of identification, to a fixed destination such as `MCAST`. The payload is:

| Field | Size | Meaning |
|---|---|---|
| Version | 1 byte | Format version, now 2 |
| Flags | 1 byte | None defined yet; senders write 0 and receivers ignore bits they don't know |
| Object | 8 bytes | Which object: the first 8 bytes of the SHA-256 of the dictionary ID (2 bytes) and the object |
| Dictionary | 2 bytes | Which zstd dictionary it was compressed with, 0 for none |
| Code parameters | 12 bytes | RaptorQ's transfer length and symbol size (RFC 6330's OTI) |
| Piece | 3 bytes | Which piece this is, counting on across slots |
| Data | the rest | One RaptorQ symbol, 940 bytes |
| CRC | 4 bytes | CRC-32 of everything before it, checked before a piece is stored |

A whole frame is then 987 bytes, 36 under IL2P's 1023-byte limit. Frames carry no source block number, so every object is a single RaptorQ block (up to 53 MB, far beyond any bulletin).

An object is one byte saying what it is, its content type (below), followed by one zstd frame (a propagation reading, type 4, is the one exception: a short record sent as it is), which carries zstd's own dictionary ID and a content checksum; a receiver refuses a zstd frame without the checksum. Objects are content-addressed: the object field is a hash of the dictionary ID and these bytes, so every rebuilt object checks itself against its own ID, with or without the directory, and two different objects can never share pieces. The head end compresses each bulletin once, when it first sees it, and sends those same bytes, with fresh pieces, in every later slot.

### Content types

The object's first byte flags what the object carries, so this traffic is marked as packet mail bulletins and the same frames can later carry other things. Its low 7 bits are the content type:

| Type | Meaning |
|---|---|
| 0 | Never used |
| 1 | Packet mail bulletin (FBB/BPQ message format), serialised as below |
| 2 | Directory |
| 3 | DAPPS message (reserved) |
| 4 | Propagation reading, one object per source (the ionosonde, and live PSK Reporter spots): a short record, not compressed, sent with dictionary 0 (see below) |
| 5 to 111 | Unassigned; given out in this table |
| 112 to 127 | Experiments; never assigned |

Its top bit (128) says a metadata block follows the type byte: a 2-byte big-endian length, then that many bytes of the wrapper's own header, then the zstd frame. A reader skips a metadata block it does not understand. Types 1 and 2 are sent without one, exactly as before v0.3.0, so receivers already in the field read them unchanged.

A receiver handles each object by its content type: bulletins go to the BBS as before, the directory is read, and from v0.6.0 the ionosonde's propagation reading is shown, and now PSK Reporter's beside it. A type it knows but does not handle (a DAPPS message, for now) is kept out of the BBS and logged once. A type it does not know is ignored without a word. Either way the object counts as done, so its later pieces are not collected. Receivers before v0.3.0 know only types 1 and 2 without metadata; anything else they drop as an object they cannot use, with a line in the log for each of its frames they hear, so it never reaches their BBS either.

### The ionosonde reading

From v0.6.0 the head end also says whether 40 m is open from Reading, by the nearest ionosonde, and sends that in each slot that keys anyway. It is observe only: it never changes what or when the head end sends, beyond the room its two frames take inside the airtime budget. A slot with nothing else to send still keys nothing.

The head end asks GIRO's DIDBase (`lgdc.uml.edu/fastchar/getbest`), then PROPquest, for Chilton (RL052), then Fairford (FF051), then Dourbes (DB049), at most every 15 minutes and only near a slot, backing off when a source answers 429 or 5xx or not at all. It drops GIRO soundings with an autoscaling confidence score under 50. It takes the first station with a sounding no older than 45 minutes, and works out the MUF at 100, 500 and 1000 km: the ionosonde's own MUF(D) when the source gives it, otherwise the F2 layer's basic MUF from foF2 and M(3000)F2 by ITU-R P.533 (section 3.5.1.1, equations 3 to 6, the same as ITU-R P.1240 section 3.1), as ITU-R Study Group 3's reference code works it (ITURHFProp, `P533/MUFBasic.c`). The gyrofrequency is taken as 1.2 MHz, its value at 300 km over southern England, and foF2/foE as 2, since the sources give no foE; from 2 to 6 that moves the MUF by about 1%. Values that are not finite, foF2 and MUFs outside 0.5 to 50 MHz, and M(3000)F2 outside 1 to 6 are treated as missing. Without M(3000)F2 only 100 km has a MUF, foF2 itself. D-layer absorption is not modelled, so an open path can still be too weak at midday. Nor is the geomagnetic field: there is no DEGRADED state for a high Kp, since it would mean a second source to trust for a word that changes nothing on the air.

A distance is open when its MUF is at least 7.1 MHz, and reliable when 0.85 times its MUF is. The skip zone is where the MUF first reaches 7.1 MHz: along the P.533 curve in 10 km steps when the MUFs are estimated, and on straight lines between the three distances when they are measured. The reading as a whole is GOOD when 100 and 500 km are reliable, POOR when all three distances are closed, MARGINAL in between, and UNKNOWN with no sounding or one more than 45 minutes old, when it gives the last values and their age and never extrapolates. Users are spread from 0 to 1000 km, so the three distances matter more than the one word: on 2026-10-06, with Fairford's foF2 peaking at 6.05 MHz (Chilton had no data that day), stations within 221 km heard nothing all day while those at 502 and 534 km rebuilt most of the bulletins. For that sounding the reading gives MUFs of 6.65, 8.21 and 11.64 MHz at 100, 500 and 1000 km, and a skip zone of about 280 km.

Type 4 is for propagation readings in general, one object per source, so a second source, live PSK Reporter spots (source 3, [below](#the-psk-reporter-reading)), fits without a new format. Each record starts with a part any source can fill, the verdict at the same three distances included, and the source's own numbers follow. After the type byte:

| Offset | Size | Meaning |
|---|---|---|
| 0 | 1 | Record version, 1 |
| 1 | 1 | Source: 1 ionosonde by GIRO, 2 ionosonde by PROPquest, 3 PSK Reporter spots |
| 2 | 4 | Observation time, minutes since 1970-01-01 00:00 UTC |
| 6 | 1 | Its age when sent, in minutes, up to 255 |
| 7 | 1 | Verdict at 100 km in bits 0 and 1, 500 km in bits 2 and 3, 1000 km in bits 4 and 5 (0 unknown, 1 closed, 2 open, 3 reliable); state in bits 6 and 7 (0 UNKNOWN, 1 POOR, 2 MARGINAL, 3 GOOD) |
| 8 | 1 | Skip zone in 10 km units; 0xFF when not open within 1000 km, or unknown |

Then, for an ionosonde:

| Offset | Size | Meaning |
|---|---|---|
| 9 | 5 | Station, its URSI code in ASCII, such as `RL052` |
| 14 | 1 | Method: 1 measured, 2 estimated, 3 mixed |
| 15 | 2 | foF2 in 10 kHz units; 0xFFFF for none |
| 17, 19, 21 | 2 each | MUF at 100, 500 and 1000 km, the same way |

All numbers are big-endian. A later version only adds bytes at the end, and a reader that does not know a source can still use the common part. The ionosonde's object is 24 bytes, one RaptorQ symbol, so each of its frames is 55 bytes and either one rebuilds it: the head end sends two fresh ESIs, normally 0 and 1, each checked to rebuild the object on its own. It is not listed in the directory. The sounding time and age are in it, so each slot's reading is nearly always a new object; if the same object comes round again, the head end keeps its next ESI by object ID, as for the directory, and never repeats one. A reading with no sounding, or one 255 minutes old or more, is not sent.

### The PSK Reporter reading

The ionosonde says what the ionosphere could carry; PSK Reporter says what it is carrying. So the head end also listens to PSK Reporter's public MQTT feed (`mqtt.pskreporter.info:1883`) for FT8, FT4 and WSPR spots on 40 and 80 m with both ends in the UK or Ireland, and from the last 30 minutes of them works out whether 40 m is open at 100, 500 and 1000 km. Like the ionosonde's reading, it is observe only and never changes what or when anything is sent.

It subscribes narrowly, to 128 topics in one go: `pskr/filter/v2/{40m,80m}/+/+/+/+/+/{sender's country}/{receiver's country}` for every pair of England (223), Wales (294), Scotland (279), Northern Ireland (265), Ireland (245), the Isle of Man (114), Guernsey (106) and Jersey (122). The feed gives countries as these DXCC entity numbers, the same in the topic and in the payload's `sa` and `ra`. The payload's locators are used, not the topic's, which cut them to 4 characters. QoS 0 and a clean session, so the broker keeps nothing for us; a lost connection is made again after 5 s, then 10, 20 and so on up to 5 minutes.

Each spot goes in a bin by the great-circle distance between the two locators: 30 to 250 km stands for 100 km, 250 to 700 for 500, and 700 to 1200 for 1000. Paths under 30 km (ground wave, or two stations in one square) are left out.

- A distance is open with at least 5 spots from at least 4 different callsigns.
- Silence is not proof of closure. A distance is closed only when it has at most 2 spots and there is evidence the band is in use: at least 40 spots from 12 stations at the other distances on the same band, or, for 40 m, 80 m busy at that distance (10 spots from 6 stations).
- Anything else is unknown, and so is every distance when the feed was up for less than 20 of the 30 minutes.
- The skip zone is 0 when 100 km is open. When 100 km is closed and a further distance is open, it is where the 40 m spots start: the 5th percentile of their distances, and at least the third shortest, to the nearest 10 km.
- The state uses the ionosonde's words: GOOD is open at 100 and 500 km, MARGINAL open somewhere else, POOR closed somewhere and open nowhere, UNKNOWN otherwise.
- Each bin's median SNR needs 5 SNRs. The feed sometimes gives none, and such a spot still counts.

PSK Reporter reports each pair of stations only every 5 or 6 minutes, and spots arrive a minute or so late (some much later), which is why the window is half an hour and the thresholds count stations as well as spots. 80 m is judged the same way for the head end's log and status, but only 40 m goes on the air.

After the common part, for source 3:

| Offset | Size | Meaning |
|---|---|---|
| 9 | 1 | Window, minutes (30) |
| 10 | 1 | Bits 0 to 2: closed at 100, 500 or 1000 km because 80 m is busy there; bits 3 to 5: closed there because 40 m is busy at the other distances; bit 7: the feed was down too long to judge by |
| 11, 16, 21 | 5 each | 100, 500 and 1000 km: spots (2 bytes), stations (2), median SNR in dB (1, signed, -128 for none) |

In the common part a verdict is 0 unknown, 1 closed or 2 open (never 3), and the observation time is the end of the window. The object is 27 bytes, one 28-byte symbol, so each frame is 59 bytes, and the head end sends two in each slot that keys, beside the ionosonde's two, either one rebuilding it. An UNKNOWN reading is still sent, with its counts; with no reading at all (the feed never came up) nothing is. Receivers from before this know type 4 but not source 3, and drop the object quietly.

### Bulletins

A bulletin is serialised as a header block of `Key: value` lines (`Type`, `From`, `To`, `At`, `Bid`, `Date`, `Title`), an empty line, then the message text exactly as a forwarding partner would receive it, R: lines included. Readers ignore keys they don't know, so later versions can add some.

### The directory

A small directory object also goes out repeatedly: the objects in rotation, with their object IDs, dictionaries, sizes, BIDs, titles and content types. It is text: a version line, the date, then one tab-separated line per object, and readers ignore fields after the title. From v0.3.0 there are such fields, each `key=value`: every line has `type=1` (its content type; a line without one is a bulletin), and the first line also has the head end's slots, `slots=00:00/60` (the first slot's time and the minutes between slots), and its daylight rule, `daylight=IO91lk/120/30` (locator, minutes after sunrise, minutes before sunset). Receivers before v0.3.0 read the same entries and ignore the rest, so the version line stays `MAILCAST DIRECTORY 1`. A directory with no entries has nowhere to carry the slots, and a receiver keeps the last ones it heard. The first line may also name the slot's waveform, `mode=ms110d-wn4`, for the record. It goes out in every slot that keys. A slot with no bulletin pieces to send keys nothing, not even the directory; under the budget rule that only happens when nothing is in rotation. Its ID comes from its content like any other object, so two slots can never mix two versions, and two slots with the same rotation on one day send the same object with fresh pieces. A receiver can then show "heard of 34, complete 30", and skip pieces of bulletins it has already rebuilt.

A receiver forgets which objects it has rebuilt after 14 days, and drops a partial object 14 days after its last new piece. The BBS's BID check remains the real duplicate filter.

The format is published here in full, because amateur transmissions must not obscure their meaning. Compression and coding are allowed; encryption is not.

The dictionary ships with the receiver. Later it can also be sent as an object of its own, so receivers can pick up a retrained dictionary without an upgrade.

No RaptorQ implementation exists for .NET, so we write one from RFC 6330 and check it against an existing implementation such as the Rust `raptorq` crate. ZstdSharp.Port, which is pure managed code, covers zstd with dictionaries.

## Receiver

One program and a small config.

- **Audio.** A sound card on the receiver's radio, or an UberSDR web receiver such as `wessex.zapto.org`. pdn-soundmodem's library supports both. The receiver detects the MS110D speed automatically, so there are no mode settings.
- **Decoding.** The library hands over each good frame. The receiver keeps every piece on disk, so partial bulletins survive restarts and add up across slots. If a rebuild does not match its object ID, the receiver keeps its pieces and, a few decodes per arriving piece, tries sets of them without the suspect ones; any set that rebuilds to the ID is accepted.
- **Delivery.** Each rebuilt bulletin is checked against its object ID, decompressed, and offered to the local BBS over its telnet port as FBB B1F forwarding, again reusing pdn-bbs's code. The BBS accepts or rejects by BID as with any partner.
- **Config.** The audio source, and the BBS host, port, login and password. Everything else is fixed, or comes from the head end: the slot times and daylight hours default to GB7RDG's, and once the receiver has heard the directory it uses what that says. A web SDR receiver listens to 8 of each day's daylight slots.
- **Packaging.** A .deb in the packet-net apt repo with a systemd service.

### Web page

A small local web page for setup and status:

- **Settings.** The audio device or web SDR, and the BBS login, written back to the config file.
- **Levels.** The input level meter, with the same target band as pdn-soundmodem.
- **Spectrogram.** A live view with markers for the tone frequency and the signal's edges.
- **Status.** The last slot (tone offset and signal-to-noise ratio, frames heard), bulletins complete and partial, and what the BBS accepted or rejected.

pdn-soundmodem's station page already has the waterfall and level meter in its library, so the page reuses those and adds the mailcast panels. It listens on localhost and the LAN only.

## Where the code lives

- **pdn-mailcast** (this repo) holds the head end, the receiver, and a core library with the on-air format, compression and directory, published on nuget.org as `Packet.Mailcast` (namespace `Packet.Mailcast`), after packet.net's `Packet.Core` and `Packet.Ax25`.
- **RaptorQ** is its own project in this repo, with no dependency on anything else here, so it can be published as a separate NuGet package (`M0LTE.RaptorQ`, matching `M0LTE.Il2p` and `M0LTE.Dsp`) if anything else wants it.
- **FBB forwarding comes from pdn-fbb** as the `Packet.Fbb` package (namespace `Packet.Fbb`), so there is one copy of it, shared with pdn-bbs.
- **MS110D stays inside pdn-soundmodem.** The receiver needs pdn-soundmodem's audio sources (sound card and UberSDR) and its waterfall anyway, so splitting the modem out would not shrink what the receiver depends on. It would only add a package release to every modem change.

## Changes needed in pdn-soundmodem

1. **Several frames in one burst.** Today each queued frame becomes its own MS110D transmission, with its own preamble and interleaver flush, which wastes a lot of airtime on short frames. The modem should pack frames that are queued together into one burst, up to a maximum length. Its receiver already deframes a continuous stream, so only the transmit side changes.
2. **The transmit lease** described above.
3. **A narrower transmit filter**, if the filter study says so.
4. **Whatever the receiver needs to embed the library cleanly**, found while building the receiver. This may be nothing more than making a few types public.

## Filter study (first piece of work)

The transmitter can fill all of 7.050 to 7.053 MHz, but many receivers have SSB filters narrower than that. The question is how much each choice of signal width costs a receiver with a given filter. The study measures it in simulation:

- **Transmit pulse shaping (roll-off).** 0.35 (the standard's suggestion, about 2.9 kHz occupied, 3.24 kHz edge to edge), and narrower settings down to about 2.5 kHz.
- **Receive filters.** 2.4, 2.7 and 3.0 kHz, centred and off-centre the way real rigs are, for example 300 to 2700 Hz.
- **Channels.** The standard's Poor HF channel and a moderate one, at a range of signal-to-noise ratios.
- **Modes.** WN3 and WN4.

The output is a plot of frame loss against receive filter width, one line per transmit width. That decides the transmit width and confirms the dial frequency.

## Build order

1. The filter study.
2. The pdn-soundmodem changes: burst packing, the transmit lease, and the transmit filter if needed.
3. The core library: RaptorQ, compression, the frame format and the directory, tested offline with pieces dropped at random.
4. The head end, run first into a WAV file from real GB7RDG traffic and decoded through pdn-soundmodem's simulated Poor channel.
5. The receiver with its web page, delivering into a test LinBPQ and a test FBB.
6. A real test transmission from GB7RDG, received by Claude running the receiver against the Wessex web SDR.
7. A trial with the three volunteers.

## Open questions

- Which callsign the receiver logs into the local BBS as. It has to be a forwarding partner there, and a station that already forwards with GB7RDG directly may not want two sessions under one call. This gets settled by testing against LinBPQ and FBB.
- Settled 2026-10-05: the Flex's transmit filter opens to 5700 Hz from GB7RDG's dial (7.04975 MHz), so the slice does not need to retune.
