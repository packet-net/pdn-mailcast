# Receiver sharing a radio with LinBPQ

A LinBPQ packet station whose radio the receiver borrows for each slot, with no pdn-soundmodem involved: the receiver is on a sound card on the radio's receive audio, moves the radio through rigctld, and tells LinBPQ itself to stop transmitting (`XMITOFF`) while the radio is away. It suits a hardware TNC such as a NinoTNC, or QtSoundModem or Direwolf on a KISS port.

If pdn-soundmodem has the sound card, use [receiver sharing a packet radio](../receiver-sharing-a-packet-radio/) and its section on another TNC instead.

| File | Goes in |
|---|---|
| [`receiver.json`](receiver.json) | `/etc/pdn-mailcast/receiver.json` |

Change the sound card and both passwords. `hfPort` is LinBPQ's port number for the radio, as its `PORTS` command lists it. In `bpq32.cfg`, the Telnet port needs an `FBBPORT` and a user whose line ends in `SYSOP`:

```
FBBPORT=8011
USER=sysop,change-me,G0ABC,,SYSOP
```

## Check it

The receiver's journal at start-up (the first line goes on):

```
retune: the radio is retuned through rigctld at 127.0.0.1:4532 to dial 7.052 MHz in PKTUSB with a 3000 Hz filter
retune: LinBPQ port 2 is "HF on the KISS TNC", asked on LinBPQ's FBBPORT at 127.0.0.1:8011
```

The second line names the port as LinBPQ has it, so you can see it is the right one. Around each slot:

```
retune: LinBPQ port 2's transmit is off for the 10:32 UTC slot (XMITOFF 2 1 confirmed)
retune: tuned to 7.052 MHz for the 10:32 UTC slot until 10:44 UTC; LinBPQ port 2 transmit off
retune: the rig is back; LinBPQ port 2's transmit is on again (XMITOFF 2 0 confirmed)
```

## What was seen

A real LinBPQ, sending its ID every minute through a stand-in for the TNC that keys the radio and writes down where its dial was each time. The slot began at 10:32:00:

| When | What happened |
|---|---|
| 10:30:15 | LinBPQ's ID went out on 7.050100 MHz |
| 10:31:19 | With LinBPQ confirmed off, the radio moved to 7.052000 MHz, PKTUSB, 3000 Hz |
| During | Tone at 1800.0 Hz, 17 of 17 frames, 3 of 3 bulletins. The TNC was handed nothing |
| 10:34:16 | Everything complete: radio back on 7.050100 MHz USB |
| 10:34:19 | LinBPQ's next ID went out, on 7.050100 MHz |

## If rigctld cannot tell whether the radio is transmitting

The receiver will not move the radio. Hamlib's dummy rig, as it starts by default, answers the "is it transmitting" question with an error, and with it the receiver said:

```
retune: WARNING - cannot check that the radio is not transmitting (rigctld answered "t" with RPRT -11, so this rig's PTT cannot be read); not tuning without that
retune: not retuning for the 10:32 UTC slot
```

Nothing was decoded, and LinBPQ was still kept off for the whole 12 minute window. Try `echo t | nc -q1 127.0.0.1 4532` against your own rigctld: on the bench `0` came back from a rig that can tell, and `RPRT -11` from this one.

## Not tested here

A real radio, rigctld back end and TNC; a TNC that is still holding a frame when LinBPQ is turned off; delivery to the BBS. The stand-in TNC puts nothing on the bench's air, so what a transmission of your own does to your reception is not shown.

Run it yourself: `tools/bench/bench.sh receiver-sharing-a-radio-with-linbpq`, and `receiver-sharing-a-radio-with-linbpq-ptt-unreadable`.
