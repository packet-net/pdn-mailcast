# Receiver sharing a packet radio

An HF packet station on pdn-soundmodem, with a CAT radio that normally sits on its packet dial (7.050100 MHz here). For each mailcast slot the radio moves to 7.052 MHz, the bulletins are received, and it goes back.

| File | Goes in |
|---|---|
| [`soundmodem.json`](soundmodem.json) | `/etc/pdn-soundmodem/soundmodem.json` |
| [`receiver.json`](receiver.json) | `/etc/pdn-mailcast/receiver.json` |

Change the sound card, the API key, the BBS login, and the packet modems and dial to your own.

What makes it work:

- `"rig"` in `soundmodem.json`, pointing at your rigctld. pdn-soundmodem does the tuning.
- `"audio": "soundmodem"` and `"retune": true` in `receiver.json`.
- No mailcast modem in `soundmodem.json`. The receiver reads pdn-soundmodem's audio and decodes it itself, so there is nothing to add or shift there.

## Check it

pdn-soundmodem's journal at start-up:

```
rig: a receive window for another program on this machine, at http://127.0.0.1:8107/rig-window (loopback only, no key)
```

The receiver's:

```
retune: the radio is retuned through pdn-soundmodem's receive window on 127.0.0.1:8107 to dial 7.052 MHz for each slot; pdn-soundmodem holds its own transmitting meanwhile
```

At start-up the receiver also notes that on your packet dial the signal would be at 3700 Hz in the audio. That is expected: it is where the radio sits between slots, not during them.

## What was seen

One slot, with every bulletin completing:

| When, from the slot's start | What happened |
|---|---|
| 1 minute before | Radio moved from 7.050100 MHz USB, 2400 Hz passband, to 7.052000 MHz USB, 3250 Hz passband |
| During | Tone at 1800.0 Hz, 17 of 17 frames, 3 of 3 bulletins |
| 2 minutes in | A packet frame was handed to the packet modem. It was held |
| 2 minutes 15 seconds in | Everything was complete and the air had been quiet for a minute: radio back on 7.050100 MHz, and the held frame went out at once, on 7.051600 MHz |

## Packet frames while the radio is away

pdn-soundmodem holds a frame for 30 seconds, then drops it. In the run above the frame waited 14.6 seconds and was sent. In the long run below, with the radio away for 13 minutes, the same frame was dropped:

```
tx[0] DROPPED G0ABC>BENCH 49 bytes: another service is holding the channel (waited 30s); transmission dropped
```

So a connected packet session has to survive on its own retries while the radio is away, and a beacon or other one-off frame sent in that time is lost.

## What else is kept off the air

pdn-soundmodem kept its own transmitter off the air while the radio was away, in each way that was tried: a packet frame over KISS was held up to 30 seconds and then dropped (above), and an ARDOP call was dropped at once. On that showing, a LinBPQ whose only way to the radio is this pdn-soundmodem's KISS or ARDOP port needs no `XMITOFF`. A second run tried two more things around one slot:

| What was tried | What happened |
|---|---|
| Six long packet frames, 40 seconds of sending, started 75 seconds before the slot | They went out on the packet frequency. pdn-soundmodem refused to move the radio while it was keyed (it normally moves 60 seconds before the slot, as in the first run), and the radio moved 3 seconds after the last frame ended, 30 seconds before the slot. All 17 mailcast frames were still heard |
| An ARDOP modem beside the packet one, told to call a station 90 seconds into the slot | Nothing went out on the air. pdn-soundmodem's log says `ardop: transmission dropped` for each of its five tries, and the call failed. The program driving ARDOP was still told `PTT TRUE` and `PTT FALSE` each time, as if it had sent |

Not tried: LinBPQ itself (a script played its part on the KISS and ARDOP ports), ARDOP's data port, a CW ident, and the transmitter test. And pdn-soundmodem cannot stop anything that keys the radio without going through it, such as another TNC or sound modem on the same radio. For that, use the receiver's hooks.

## Your own commands still run

`hooks` in `receiver.json` work in this set-up too ([receiver.md](../../receiver.md#running-your-own-commands-around-each-slot)). With a "before" and an "after" command added, "before" ran 90 seconds before the slot, a minute ahead of the radio moving, and "after" ran in the second the radio came back. What the two commands wrote for the 09:27:00 slot (each line goes on), with the radio moving at 09:26:30 and back at 09:29:15:

```
09:25:30 hook before ran: MAILCAST_HOOK=before MAILCAST_SLOT_UTC=2026-11-23T09:27:00Z
09:29:15 hook after ran: MAILCAST_HOOK=after MAILCAST_SLOT_UTC=2026-11-23T09:27:00Z
```

So a hook that sends LinBPQ `XMITOFF` and back would run at the right moments here. It is the way to stop LinBPQ trying at all, or to stop something this pdn-soundmodem does not control. A real `XMITOFF` hook against LinBPQ was not run; the bench's hooks only wrote down when they ran.

## How long the radio is away

The receiver hands the radio back early only when everything in the station's directory is complete and nothing has been heard for a minute. With bulletins too big to finish in one slot, the head end finished sending 1 minute 39 seconds into the slot, and the radio stayed on 7.052 MHz until 12 minutes after the slot's start: 13 minutes away in all. Nothing sent in a slot tells a receiver the slot is over ([#285](https://github.com/packet-net/pdn-mailcast/issues/285)).

## Not tested here

A real radio and rigctld back end (the bench uses Hamlib's dummy rig); your radio accepting the 3250 Hz passband pdn-soundmodem asks for; delivery to a BBS.

Run it yourself: `tools/bench/bench.sh receiver-sharing-a-packet-radio`, `receiver-sharing-a-packet-radio-unfinished` for the long case, and `receiver-sharing-a-packet-radio-transmit-paths` for these two tries and the hooks.
