# One mailcast receiver: plan

A living document. Steps are tracked as GitHub issues (see the tracker issue in packet-net/pdn-mailcast); decisions and changes to the plan go in the amendment log at the end.

## Goal

There is one mailcast receiver: pdn-mailcast-receiver. Someone who already runs pdn-soundmodem installs the receiver alongside it and points it at pdn-soundmodem, instead of configuring a second receiver built into pdn-soundmodem. pdn-soundmodem stays a general sound modem and gains only general features that any program could use.

## The agreed design (Tom, M0LTE, 2026-10-08)

This supersedes the earlier shared-engine plan.

- pdn-soundmodem's core stays pure: nothing mailcast-specific is added to it.
- The standalone receiver (packet-net/pdn-mailcast, `src/Mailcast.Receiver`) gains a third audio source, a running pdn-soundmodem, alongside its sound card (ALSA) and web SDR (UberSDR) sources. It already runs pdn-soundmodem's MS110D modem in-process through the `pdn-soundmodem` package, so nothing about decoding moves.
- pdn-soundmodem gets three general features:
  1. a local-only channel audio stream: a program on the same machine reads the channel's received audio;
  2. a receive window: a program asks it to retune the rig and hold all transmitting for a while, then put everything back;
  3. a link on its page to the page of a program reading its audio (the receiver).
- Then the built-in receiver (`src/Packet.SoundModem.Daemon/Mailcast/`, the `mailcast` config section; #554, #557, #563, #580, #581) is removed, after a deprecation release that warns anyone who has the `mailcast` section.
- One receiver guide. A listener's pdn-soundmodem then needs no `ms110d` modem; GB7RDG's own node keeps its ms110d modem 4 (KISS 8112), because the head end transmits through it.
- Staged, with no change for existing users at each step until the deprecation release.
- Tom's rules: no config settings he didn't ask for (fixed behaviour where possible; anything genuinely needed is listed below as a decision for him); nothing changes for web SDR users; Flex users keep working (GB7RDG's node is `flex:discover DAX`); sound card rigs on CM108 and the like keep working.

## What the code does today

Read from pdn-soundmodem main at v0.87.5 (73d5852) and pdn-mailcast main at v0.8.7 (89d8651).

### pdn-soundmodem's built-in receiver

- **Audio.** `MailcastReceiver.Attach` adds a receive tap on the station's one channel (`SoundModemChannel.AddReceiveTap`): mono float at the channel's DSP rate, 100 ms blocks on the receive thread. Taps get nothing while the channel is keyed (`ProcessReceive` returns early). There is no `RemoveReceiveTap`. One station is one channel and one input per process; more channels means more instances (`pdn-soundmodem@NAME`).
- **Rate.** Configuring `mailcast` adds a phantom `ms110d-wn4` modem to the rate choice (Program.cs:527-538), so the channel runs at 48 kHz. Without it a station with only 12 kHz modems runs at 12 kHz, and a Flex then takes 24 kHz s16 DAX decimated by 2 instead of 48 kHz float DAX (`DaxStreamFormat.ForDspRate`).
- **No modem needed.** The receiver builds its own MS110D modem from `ModemCatalog`; no `ms110d` modem has to be configured, and it never transmits.
- **Placement.** `MailcastPlacement.Decide` works out from the device and config whether the station's passband hears the signal (7.0538 MHz centre), whether to retune (needs `mailcast.retune` and a `rig` section), or refuses to start. `MailcastReceiveShift` moves the signal to MS110D's own centre when the dial is not 7.052 MHz.
- **Flex.** A Flex is never retuned (a `rig` section is refused on a `flex:` device). A headless Flex opens its slice receive filter at start-up to cover the mailcast signal as well as the modems (Program.cs:1035-1060). Attach mode leaves the filter to SmartSDR.
- **Retune and hold.** `MailcastRetuner` takes a `RigControl.Tune` window owned by "mailcast" from 1 minute before a slot to 12 after (renewed every minute, at most 5 minutes per window), at `dialKHz` in the rig's USB-family mode (else USB) with a 3000 Hz passband. While any rig window is open `RigControl.HoldsTransmitter` is true, which sets `channel.TransmitInhibit`, makes `RigPtt` refuse to key and holds idents. Queued KISS frames wait, but each is rejected after `TransmitInhibitTimeout` (30 s). The rig's previous dial is written to a restore file first, so a crash puts it back at the next start.
- **Generic pieces already there.** `POST /api/rig/tune` (RigApi.cs) is a keyed, owner-tagged rig window over HTTP on the same `RigControl.Tune`, so it already retunes and holds transmitting; it needs an explicit `mode` and works only with a `rig` (rigctld) section. `/api/txlease` (`TransmitLease`) is what pdn-mailcast's head end uses to own GB7RDG's transmitter; a lease outranks the inhibit, which is why a head end must never also retune.
- **Servers and auth.** One HTTP listener, the station page (`WaterfallWebServer`, `waterfall.port` 8107, top-level `bind`, default 127.0.0.1). The page's WebSocket already carries listener audio (`[0x02][3 pad][s16 LE]`, channel rate, 40 ms blocks) for the browser. `/api/*` is `ConfigApi`, installed only when `api.key` is set (or audio controls are on); the key is sent as `X-API-Key` or `Authorization: Bearer`, and is never generated: it is the operator's own string. The packaged config is `/etc/pdn-soundmodem/soundmodem.json`, mode 0644, seeded with a `waterfall` section and no `api`. `/api/mailcast` (keyless, read only) and `POST /api/mailcast/measure` (keyed) serve the panel; on a station with no `api.key` the measure button gets a 404.
- **The `mailcast` section.** `bbs` (`type` linBpq or fbb, `host`, `port` 8011, `login` Q0CAST, `password` required, `command` BBS), `sources` (default GB7RDG and M0LTE), `dialKHz` (7052.0), `stateDirectory` (`<state>/mailcast`: `store/`, `deliveries.jsonl`, `hooks.json`), `retune` (false), `hooks` (`before`, `after`: `command`, `args`, `timeoutSeconds`). Everything else (timetable from the directory, receive shift, measure, passband) is fixed in code. Unknown keys warn; there is a precedent for naming retired keys (`AlsaMixerConfig.RenamedKeys`, `ForcedOffKeys`).
- **Package pin.** `Packet.Mailcast` 0.4.3 (and `Packet.Fbb` 0.1.0), used only by the built-in receiver. pdn-mailcast is at 0.8.7: since 0.4.3 the core gained the directory `mode=` field, the propagation objects (kind 4), the daily report, held bulletins and a 48 h directory cache, and `ReceiverStore.AcceptResult` changed shape. The built-in is out of date with the broadcast and would need work to catch up; this plan does not bump it.
- **GB7RDG's node** (root@pdn-soundmodem, pdn-soundmodem 0.87.5, head end 0.8.7) has no `mailcast` section, so the removal does not touch it.

### pdn-mailcast's receiver

- **Audio sources.** No interface: `AudioSource` is a kind (`Alsa`, `UberSdr`, `Wav`) and a target parsed from the one `audio` string. `AudioPipeline.StartAsync` opens an `IAudioInput` (`SampleRate`, `Read(Span<float>)`) per kind; everything runs as mono float at 48 kHz in 100 ms blocks; a source silent for 30 s is reopened (a web SDR between sessions is exempt). WAV input is resampled by whole factors to 48 kHz.
- **Dial.** No source reports a dial; it is `dialKHz` (7052.0), checked so the signal's audio centre is 1000 to 2000 Hz, with measure-or-type on the page (`POST /api/filter/measure`, sound card only, and `/api/filter/dial`).
- **Retune.** Only with `rig` (rigctld) and only for a sound card. It holds LinBPQ off with `XMITOFF` over the node's telnet port (`bpq`), or `dedicatedRadio: true`, checks PTT through rigctld, tunes through pdn-soundmodem's own `RigControl` (USB, no passband), keeps an `interlock.json` note for crash recovery, and puts everything back in order. There is no Flex code.
- **What only the standalone receiver has.** Bulletin archive and mail view with resend, settings and LAN sign-in on its page, update check, daily feedback report, ionosonde and PSK Reporter, Channel tile and probe, early window end (#49), Listen now, `--decode`, held bulletins, the LinBPQ interlock.
- **What only the built-in has.** Retune in the rig's data mode (PKTUSB and the like) with a 3000 Hz passband; holding its own transmitter instead of LinBPQ's; the headless-Flex filter opening; running per pdn-soundmodem instance.
- **Both have.** BBS delivery (LinBPQ and FBB, same keys and defaults), hooks (same `HookCommand` shape), `sources`, `dialKHz` with measure-or-type, the directory timetable.

So a user moving from the built-in needs, in the receiver: their `bbs`, `sources`, `dialKHz` and `hooks` copied across as they are (same names), the audio from pdn-soundmodem, the Flex filter opening, and, if they had `retune`, a retune that goes through pdn-soundmodem and holds its transmitter. Bulletin state is not carried over (decision 6).

## Steps

pdn-soundmodem steps 1 to 3 ship together as one minor release (0.88.0) and change nothing for anyone not using them. pdn-mailcast steps 4 to 6 ship as receiver 0.9.0 (0.8.8 is #73). Steps 7 and 8 are pdn-soundmodem releases of their own.

### 1. pdn-soundmodem: a local channel audio stream

- **What.** A WebSocket on the station page server that sends the channel's received audio to a program on the same machine (shape in decision 1). A connection says who it is (a name and, optionally, its own page's port) and the audio band it needs to hear. One receive tap fans out to all connections, each with a small bounded queue; a slow reader loses blocks with a gap marker rather than slowing the modem. While the channel is keyed, blocks keep coming, marked as transmitted and silent, so the reader's clock never stops. A headless Flex opens its slice receive filter to cover any band a connection asks for, while that connection is open, as it does today for the built-in receiver; attach mode leaves the filter alone, as now.
- **What users see.** Nothing. No config; nothing listens unless a program connects.
- **Proof.** Tests: refused from a non-loopback address and from a browser (an `Origin` header), the rate and block shape, the gap marker, the keyed marking, several readers at once, the Flex filter widened and put back. Real: a station on a `pipe:` device fed a recorded slot (see "Proof with recordings"), with a test reader showing the stream is the input sample for sample on a 48 kHz channel, and the decimated input on a 12 kHz one. A short read-only check on a Flex (GB7RDG's node between slots, or radio1 if a Flex is on the bench) that DAX audio streams.

### 2. pdn-soundmodem: a receive window

- **What.** A keyed request (shape in decision 2) for "tune the rig to this dial, hold all transmitting, for this long", renewable, releasable, always put back. It is built on the same `RigControl.Tune` window and `HoldsTransmitter` hold the built-in uses, with the built-in's fixed choices made general: the rig's own USB-family data mode (else USB) and a passband wide enough for the asked band (3000 Hz for mailcast). It is refused while a transmit lease is held (and a lease is refused while a window is open), so it can never be combined with a head end by accident. If the client vanishes the window ends by itself within 5 minutes and the rig goes back, as the restore file already guarantees across a crash. The built-in receiver is not moved onto it (it is removed in step 8).
- **What users see.** Nothing unless a program asks.
- **Proof.** Tests with the existing fake rigctld: tuned, renewed, released, expired; transmitting held (KISS frames wait, idents wait, PTT refused); lease and window refuse each other; restore after a simulated crash. Real: on radio1 with a rigctld rig (or Hamlib's dummy rig, `rigctld -m 1`, if the bench radio is not free), a client holds a window: the dial moves and comes back, and a KISS frame sent during it is held and not keyed.

### 3. pdn-soundmodem: a link to the receiver's page

- **What.** When a connection to the audio stream gave its page port, pdn-soundmodem's page shows its name with a link to that page, on the same host the browser used. If that would not reach it (the reader's page is local-only and the browser is elsewhere), the name is shown without a link and with a one-line hint.
- **What users see.** Only someone running a receiver against pdn-soundmodem sees the link.
- **Proof.** Page tests for the link and the no-link case; a screenshot with the receiver from step 4 connected.

### 4. pdn-mailcast: pdn-soundmodem as the receiver's audio source

- **What.** A new `audio` spelling for "the pdn-soundmodem on this machine" (decision 3). The receiver reads pdn-soundmodem's own config file for the page port, bind address and `api.key`, so the user types nothing else. It connects to the stream, asks for the mailcast band, resamples by a whole factor to 48 kHz when the channel runs slower, treats transmitted blocks as silence and resets the modem after each, reconnects when pdn-soundmodem restarts, and gives its page port for step 3's link. Measure-or-type works on this source as it does on a sound card. A Flex station needs no `rig` and nothing is retuned: the stream's filter opening is enough, as today. No `ms110d` modem is needed in pdn-soundmodem.
- **What users see.** Nothing unless they choose the new source.
- **Proof.** Tests against a fake stream (shape, resample, gaps, reconnect, keyed blocks). Real: the recordings below decode through pdn-soundmodem's stream with the same bulletins and frame counts as `bin/decode_slot.sh` gets from the files directly, at 48 kHz and at 12 kHz. Live: one slot on CT 150 through a temporary pdn-soundmodem on a web SDR, removed afterwards (see "Proof with recordings").

### 5. pdn-mailcast: retune through pdn-soundmodem

- **What.** With the pdn-soundmodem source and retune chosen (decision 4), the receiver takes pdn-soundmodem's receive window for each slot instead of rigctld and LinBPQ: the same timing (1 minute before to 12 after, early end, back-to-back slots chained), renewed every minute, released at the end, and the hooks around it as now. If pdn-soundmodem has no `api.key`, the receiver does not retune and its page says what to add. `rig` and `bpq` stay as they are for sound card users with LinBPQ.
- **What users see.** Nothing unless they choose it.
- **Proof.** Tests against a fake receive window (taken, renewed, refused, lost mid-slot, receiver stopped mid-slot). Real: radio1 or a dummy rig with pdn-soundmodem on a `pipe:` device: a recorded slot played in after the retune decodes, the dial moves and comes back, a KISS frame sent during the window waits; the receiver killed mid-window and the rig back within 5 minutes.

### 6. pdn-mailcast: one receiver guide

- **What.** `src/Mailcast.Receiver/README.md` and `docs/receiver.md` gain "Using it with pdn-soundmodem": install from the packet-net apt repo alongside, set `audio`, copy `bbs`, `sources`, `dialKHz` and `hooks` from the old `mailcast` section (the same names), and turn on retune if they had `retune: true`. A Flex note, and a note that the listener's pdn-soundmodem needs no ms110d modem. pdn-soundmodem's `docs/14-mailcast.md` shrinks to a pointer to this guide in step 7.
- **What users see.** The docs.
- **Proof.** Followed end to end on a clean box (a fresh LXC) with pdn-soundmodem on a `pipe:` device and the receiver from apt, decoding a recording.

### 7. pdn-soundmodem: the deprecation release

- **What.** A station with a `mailcast` section starts and runs exactly as before, but says at start-up (journal, plain ASCII) and on the mailcast panel that the built-in receiver is going, links the receiver guide, and lists what to copy. `docs/14-mailcast.md` becomes the pointer and migration table; `docs/reference/config.md` marks the section deprecated.
- **What users see.** The warning, if they have the section; nothing otherwise.
- **Proof.** Tests that the warning appears with the section and not without; the existing mailcast tests unchanged.

### 8. pdn-soundmodem: remove the built-in receiver

- **What.** Delete `Mailcast/`, the phantom 48 kHz modem, the mailcast filter opening (replaced by step 1's), `/api/mailcast`, `/api/mailcast/measure` and the panel, the `Packet.Mailcast` and `Packet.Fbb` pins and the mailcast tests and docs. A leftover `mailcast` section becomes a named retired key: the station still starts and says once where the receiver is, using the `RenamedKeys` precedent, and its old state directory is left on disk. The units' longer stop timeout, which was for the hooks, goes back to the normal one.
- **What users see.** A station with the section still starts, listens as a modem, and warns. Its mailcast reception stops unless the receiver is installed.
- **Proof.** Full solution tests; a station with an old config containing `mailcast` starts and warns; a Flex station without `mailcast` keeps its usual filter; the receiver from step 4 still decodes through the stream.

## Proof with recordings

Recordings in `/home/tf/src/mailcast-test/`: `fulltest4` (six web SDRs, 2026-10-05), `slot-20261006-0900`, `testslot-20261008-0805`. `bin/decode_slot.sh DIR` turns each IQ file into 48 kHz SSB audio (`*.ssb.wav`) and decodes it with the receiver; its numbers are the reference.

Through pdn-soundmodem: run a pdn-soundmodem with `pipe:/tmp/rx-in,/tmp/rx-out,48000` and one ordinary modem (once with a 48 kHz mode and once with only 12 kHz modes, so the channel runs at each rate), play `X.ssb.wav` into `/tmp/rx-in` as float32 at real time (`sox X.ssb.wav -t f32 - | pv -q -L 192k > /tmp/rx-in`), and run the receiver with the pdn-soundmodem source and its own state directory. Pass: the same bulletins complete and the frame count within a frame or two of the direct decode, for every recording. Heavy runs go on hyperv-gha or studybox, not proxmox1.

Live, once, for step 4: on CT 150 (root@10.45.0.235) a temporary pdn-soundmodem on `ubersdr:` an SDR none of the survey instances use, the receiver pointed at it, for one daylight slot only, then both removed. A web SDR allows about three hours a day per address and pdn-soundmodem holds the session for as long as it runs, so it is started just before the slot and stopped straight after.

## Release and deploy notes

- pdn-soundmodem releases are tagged on a full commit SHA. GB7RDG's node (root@pdn-soundmodem) is upgraded only between :14 and :57 past the hour, with `timeout 45 ~/update.sh`; it has no `mailcast` section, so each of these releases is routine there. Its ms110d modem 4 (KISS 8112) and the head end's `/api/txlease` use are untouched by every step.
- Receiver releases go through the packet-net apt repo. CT 150's receivers (root@10.45.0.235: the main instance and seven survey instances on web SDRs) are upgraded after each receiver release; none of them uses the new source, so nothing changes for them.
- Order: 0.88.0 (steps 1 to 3), then receiver 0.9.0 (steps 4 and 5, docs from step 6), then the deprecation release (step 7), then the removal (step 8) after the wait in decision 5.

## Open decisions for Tom

1. **Stream shape and auth.** Recommendation: a new WebSocket path on the station page server (no new port), loopback only (refused from any other address whatever `bind` is), refused when the request carries an `Origin` header (that is, from any web page, which also closes the DNS rebinding route in #423), and no key: it only lets a local program listen. Mono float32 at the channel's own rate in 100 ms blocks, each with a sample counter and a transmitted flag, and a first message giving the rate and the dial when pdn-soundmodem knows it (rig or Flex slice). The alternative, reusing the page's existing listener audio, is s16, browser-shaped and reachable from the LAN, so not recommended.
2. **Receive window auth.** Recommendation: require `api.key`, like `/api/rig/tune` and `/api/txlease`, because it moves the rig and takes the station off the air. The receiver reads the key from pdn-soundmodem's config file (0644 on the same machine), so nothing new is typed; a station without `api.key` gets audio but no retune, and the receiver's page says so. The alternative, keyless from loopback without an `Origin` header, saves one line in pdn-soundmodem's config but lets any local program hold the transmitter.
3. **How the receiver names pdn-soundmodem.** Recommendation: `"audio": "soundmodem"` for the packaged `/etc/pdn-soundmodem/soundmodem.json`, and `"audio": "soundmodem:NAME"` for an instance (`NAME.json`), with port, bind and key read from that file. No other new setting.
4. **Retune by choice or automatically.** Recommendation: by choice, with one receiver setting, `"retune": true`, which only applies to the pdn-soundmodem source and replaces the built-in's `mailcast.retune`. It is justified because a retune takes the station off the air for 13 minutes of each daylight hour, which should never happen because a user changed their audio source. Without it the receiver listens on pdn-soundmodem's passband and says so if the signal is out of reach, as the built-in does. Everything inside the window (mode, passband, timing) stays fixed.
5. **Deprecation timing.** Recommendation: the deprecation release goes out once receiver 0.9.0 is released and steps 4 and 5 are proven on recordings and the one live slot; the removal follows no sooner than two weeks later and at least one pdn-soundmodem release after it. A leftover section warns and never stops a station starting.
6. **Bulletin state on moving.** Recommendation: no import. Partly received bulletins in the built-in's store are lost (GB7RDG repeats them), and the receiver keeps its own delivery record from then on. Step 4 checks what LinBPQ and FBB do if a bulletin the built-in already delivered arrives again from the receiver; if either accepts a duplicate, the guide says to stop the built-in after a slot has completed everything.
7. **The measure endpoint.** Recommendation: pdn-soundmodem's `POST /api/mailcast/measure` goes with the removal and the receiver's own measure (which gains the pdn-soundmodem source in step 4) is the only one. pdn-soundmodem #582 (warn when the noise is too uneven) then belongs to the receiver's measure: close it there and reopen it in pdn-mailcast.
8. **The built-in until removal.** Recommendation: frozen. No bump of `Packet.Mailcast` from 0.4.3 and no new features; fixes only if it breaks for someone before step 8.

## Amendment log

- 2026-10-09: first version, from the design agreed with Tom on 2026-10-08 and a read of both repos at pdn-soundmodem v0.87.5 and pdn-mailcast v0.8.7.
