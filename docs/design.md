# pdn-mailcast design

Status: draft, 2026-10-04; built and on the air since 2026-10-05, every hour since v0.2.0, every daylight hour since v0.3.0.

## What it is

Every hour on the hour in daylight, GB7RDG sends its recent bulletins on 40 m. Nobody replies on air. Each listening station runs one program, the receiver, which hears some or all of the transmission, rebuilds each bulletin and forwards it into the local BBS as if it came from a forwarding partner. The BBS's own duplicate check (by BID) throws away anything it already has, so the sender never needs to know who is listening or what they hold.

Two ideas make a one-way link work:

- **Fountain coding.** Each bulletin is cut into pieces, and the sender can make as many different pieces as it likes. Any set of pieces slightly larger than the bulletin rebuilds it, whichever ones they are. A receiver that misses a third of the transmission in a fade still gets its bulletins, and pieces heard in different slots add together.
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

## Each slot

1. The head end listens first. If the channel is busy, it waits, up to a limit.
2. It takes a transmit lease on GB7RDG's modem (see below), so nothing else can key the radio during the slot.
3. 10 seconds of steady tone at the centre frequency (30 for a daily station), then a CW ident.
4. Bursts of up to 18 seconds, each with its own MS110D preamble, so a receiver that tunes in late or loses sync in a fade picks up at the next burst.
5. A CW ident at least every 10 minutes and at the end.
6. The lease is released and normal packet service resumes. An hourly slot never runs past 10 minutes from its start; anything left goes in the next one.

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

An object is one byte saying what it is, its content type (below), followed by one zstd frame, which carries zstd's own dictionary ID and a content checksum; a receiver refuses a zstd frame without the checksum. Objects are content-addressed: the object field is a hash of the dictionary ID and these bytes, so every rebuilt object checks itself against its own ID, with or without the directory, and two different objects can never share pieces. The head end compresses each bulletin once, when it first sees it, and sends those same bytes, with fresh pieces, in every later slot.

### Content types

The object's first byte flags what the object carries, so this traffic is marked as packet mail bulletins and the same frames can later carry other things. Its low 7 bits are the content type:

| Type | Meaning |
|---|---|
| 0 | Never used |
| 1 | Packet mail bulletin (FBB/BPQ message format), serialised as below |
| 2 | Directory |
| 3 | DAPPS message (reserved) |
| 4 to 111 | Unassigned; given out in this table |
| 112 to 127 | Experiments; never assigned |

Its top bit (128) says a metadata block follows the type byte: a 2-byte big-endian length, then that many bytes of the wrapper's own header, then the zstd frame. A reader skips a metadata block it does not understand. Types 1 and 2 are sent without one, exactly as before v0.3.0, so receivers already in the field read them unchanged.

A receiver handles each object by its content type: bulletins go to the BBS as before, and the directory is read. A type it knows but does not handle (a DAPPS message, for now) is kept out of the BBS and logged once. A type it does not know is ignored without a word. Either way the object counts as done, so its later pieces are not collected. Receivers before v0.3.0 know only types 1 and 2 without metadata; anything else they log once as an object they cannot use and drop, so it never reaches their BBS either.

### Bulletins

A bulletin is serialised as a header block of `Key: value` lines (`Type`, `From`, `To`, `At`, `Bid`, `Date`, `Title`), an empty line, then the message text exactly as a forwarding partner would receive it, R: lines included. Readers ignore keys they don't know, so later versions can add some.

### The directory

A small directory object also goes out repeatedly: the objects in rotation, with their object IDs, dictionaries, sizes, BIDs, titles and content types. It is text: a version line, the date, then one tab-separated line per object, and readers ignore fields after the title. From v0.3.0 there are such fields, each `key=value`: every line has `type=1` (its content type; a line without one is a bulletin), and the first line also has the head end's slots, `slots=00:00/60` (the first slot's time and the minutes between slots), and its daylight rule, `daylight=IO91lk/120/30` (locator, minutes after sunrise, minutes before sunset). Receivers before v0.3.0 read the same entries and ignore the rest, so the version line stays `MAILCAST DIRECTORY 1`. A directory with no entries has nowhere to carry the slots, and a receiver keeps the last ones it heard. It goes out in every slot. Its ID comes from its content like any other object, so two slots can never mix two versions, and two slots with the same rotation on one day send the same object with fresh pieces. A receiver can then show "heard of 34, complete 30", and skip pieces of bulletins it has already rebuilt.

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
