# Mailcast listener feedback from the OARC Discord (#packet-hf and elsewhere)

Collected 2026-11-07 (UTC) from the OARC Discord archive MCP tools (read only). Window: 2026-10-01 to 2026-11-07, all 1,408 #packet-hf messages in the window were read for the period 10-04 onward in full from 10-20, and keyword-filtered with one line of context either side for 10-04 to 10-19 (see Limitations). Searches run across all channels: mailcast, MCAST, MCR, MCR1, GB7RDG (partial), MS110D, fountain, "Model B", "Last slot", bulletins.

## Limitations (read first)

- The archive tool does NOT give message ids or links. Messages are identified below by date and time (UTC, to the minute) plus author. A reader can find any of them with the Discord search or by timestamp in #packet-hf.
- The archive keeps only the FIRST LINE of a multi-line message. Every fenced code block, pasted report, config or table is therefore cut to its opening line (for example Steve G7TAJ's message at 2026-10-07 12:46 is stored as just "```Last slot"). Section 4 (pasted reports) is consequently mostly EMPTY of verbatim report bodies; only the few reports that fit on one line survived. The full bodies must be fetched from Discord directly (the channel is #packet-hf, id 1154055633428037662 per link text in the archive; individual message links need the Discord client).
- Images and attachments (waterfalls, screenshots, Eoin's .docx summaries, G3AGM's signal-chain PDF) were not opened. They are listed as "[image]" where they carry reception evidence.
- Quotes are verbatim except that curly quotes, emoji and non-ASCII punctuation were reduced to plain ASCII. Author names have emoji stripped. Some long messages are cut by the tool at ~300 characters; those are marked "(cut)".
- There were no hits at all for "MCR", "MCR1" or "Model B" in the archive from 2026-10-01. No "MCR ..." daily report with an "MCR1 ..." header line is visible in the archive text (they would have been inside code blocks, which the tool drops, or sent by packet mail rather than Discord).
- The Discord threads under #packet-hf (e.g. "mail wash-up", "Moving 40m packet around", "Friday activity", "Sunday activity") did not return mailcast hits; the only mailcast thread hits were in "-anthony---m7taw-windows-build-of-pdn-" (6 messages, listed below). Not every thread was read in full.

## 1. Summary of what listeners said

- Participation: at least a dozen distinct people tried receiving (G7TAJ, G8BPQ, GM4CXF, M7TAW, MM0MQN, M0NVK/IOW, EI5IYB, G3AGM, M0JQQ, G6PHF, G6FCI, SA6DAZ, and others). Setups ranged from a direct rig + soundcard, to NinoTNC/Pi, to UberSDR/WebSDR taps (Wessex, M9PSY, EI4HQ), to an SDRconnect server, to a Pi 2W (EI5IYB).
- What worked: when the band was open the results were strong. Tom's own survey post (2026-10-22) shows "a huge range of results"; G7TAJ got 22 bulletins from only ~30% decoded on 10-18 (Perry's reading of Tom's chart); EI4HQ received 19 complete bulletins in one day (10-06); G8BPQ received 10.5K of bulletins (4.5K compressed) in 2 minutes (10-06) and 7 complete at GB7BPQ later that day; M7TAW, GM4CXF, G3AGM, EI5IYB all reported frames and bulletins; G7TAJ delivered ~40 bulletins in the first ~10 days and reported "1200bps from RDG most slots and quite a lot of frames".
- What did not: propagation variability dominated. Several people reported nothing for whole slots or days (10-06, 10-07, 10-09, 10-22, 11-01, 11-02). G7TAJ on 10-28: "heard 8 of 8 slots, 193 frames, 1 bulletin completed" and concluded "7 mins on the hour for 8 slots we have is not enough air time. For me at-least." Tom disagreed from his survey (most of ~10 UK WebSDRs got all bulletins) and suggested chunk-mix tweaks.
- Interference: the Russian OTH/"mil radar" swept across the 7050-7053 slot on 10-30 and wiped out reception for G7TAJ (1.1 dB SNR on that run) and SA6DAZ ("totally wiped out by the radar"); Tom still decoded 6400 bps QAM16 frames in Sweden that day. Also AM broadcast and FT8 over the mailcast frequency (10-27), VARA over the top, and a busy-channel refusal that stopped RDG sending all of the afternoon slots on 10-22.
- Practical complaints: sharing the radio with a packet node (stopping TX, retuning, restarting around each slot) was the main pain point; hooks, rigctld and PKTUSB/UD handling were worked out in-channel. Rig mode issue: the Xiegu G90 needs UD not USB (M7TAW), and UD gives 3 kHz vs 2.7 kHz bandwidth, which Tom called valuable SNR; Tom then defaulted the mode to PKTUSB.
- Software feedback: oversized "Today" section when only one slot is listed (G7TAJ, 10-28 and 11-02); no "bytes received this session" counter (G7TAJ 10-14); housekeeping of bulletins that are no longer transmitted (G7TAJ 10-14); wish for slot statistics in the journal (10-16, delivered); locator box missing (10-13, added); 600/1200 bps ID in the UI; sudo not allowed in hooks (G8BPQ); dsnoop/snoop device permissions (G8BPQ, fixed by moving .asoundrc to the system file); LAN-visible web UI by default added 10-28; XRouter support asked for by G6FCI (added 11-03, v0.33.1, untested).
- Reports back to Tom: the daily MCAST report goes by packet mail to RDG. Several people had trouble getting it routed (GM4CXF, M7TAW, MM0MQN, G8BPQ), because they were not set up as BBS partners at the receiving end and the local network is partitioned; Tom noted "nobody knows who routes to who any more". Some sent reports by email or by hand.
- Feature requests / ideas: multiple transmit sites sharing fountain chunks (Perry, G7TAJ, Tom); receivers returning to normal early once enough chunks are held; swapping chunks between nearby receivers over a local link; frequency-hopping and radar blanking (Perry); a QO-100 variant (G8BPQ); channel-equalisation sounding (Perry); KiwiSDR support (Eoin); SDRconnect support (done, 0.91.1); XRouter support; a way to stop the BBS calling out during a mail run (Eoin; INTERLOCK).
- Compliance/wording: Szymon G3KEY (2026-10-05): "Careful not to use the word 'broadcast', some muppet will inevitably complain at you about 'license conditions'". Perry quoted the licence text on messages "transmitted by a Beacon or via a Data Station for reception by Amateurs" the same morning. Tom moved the transmit callsign from GB7RDG to M0LTE on 10-11 because it was "technically operating outside the FAC for GB7RDG".
- Spectrum/neighbours: the signal is "2.9khz 99% power" (Tom, 10-05); G7TAJ measured ~3.2 kHz on an UberSDR picture. Tom: "It's straight over the top of all for an experiment. Centred in the middle of 7050-7053". Nobody complained of QRM caused by mailcast in the channel in the visible text; the only related complaint (10-27 intruder-watch, G7TAJ) is of other signals on the mailcast frequency.

## 2. Chronological list of notable messages

Format: date time (UTC) | author | channel | quote (verbatim, ASCII-normalised) or [P] = paraphrase. No message ids available (see Limitations).

### Early October: launch and first receivers

- 2026-10-04 19:18 | Tom M0LTE | #packet-hf | "I have an implementation of a NATO STANAG mode which should be pretty resilient, and the idea is to combine it with fountain codes in such a way that a 'virtual peer' hears some or all of the regular one way transmission and for each mail decoded from that stream, drops it into your existing bbs." (cut)
- 2026-10-05 08:28 | Tom M0LTE | #packet-hf | "If anyone wants to play along in the next day or two with the mail fountain, here's some instructions (written for LinBPQ)"
- 2026-10-05 08:53 | Szymon G3KEY | #packet-hf | "Careful not to use the word 'broadcast', some muppet will inevitably complain at you about 'license conditions'"
- 2026-10-05 09:59 | Perry M0PYL | #packet-hf | Quotes licence text: "c) Messages transmitted by a Beacon or via a Data Station for reception by Amateurs."
- 2026-10-05 11:03 | John G8BPQ | #packet-hf | "Will that fit in with our current 3 slots on 40?" ; Tom 11:03: "No. It's straight over the top of all for an experiment. Centred in the middle of 7050-7053"
- 2026-10-05 15:20 | Tom M0LTE | #packet-hf | "Wessex UberSDR copied 100%"; 15:22: "41 frames copied 100% accurately at 1200bps, 10-15W average power output here..."
- 2026-10-05 15:21 | Steve G7TAJ | #packet-hf | "not a thing here. not sure if thats audio related . tone and cw were ok and strong. audio was only heard for about 20sec here afterwards ?"
- 2026-10-05 17:37 | John G8BPQ | #packet-hf | "I received for just over 2 mins at 1700"
- 2026-10-05 18:15 | Tom M0LTE | #packet-hf | "20 bulletins sent over 8 minutes at the top of the hour. 130 frames in 22 bursts. Pending websdr results." 18:16: "WebSDRs heard nothing, band collapsing."
- 2026-10-05 19:45 | Tom M0LTE | #packet-hf | "2.9khz 99% power. You don't really lose much SNR with a narrower receiver filter though."
- 2026-10-05 19:54 | Andy G3AGM | #packet-hf | "Is the pdn-mailcast a replacement for AXIP links by using WebSDRs over the internet as an alternative but still internet?" Steve G7TAJ: "nope, thats just one method of RX. the main idea would be to use your own RX". Tom: "Hopefully will reduce the need to depend on AXIP at all..."
- 2026-10-05 19:57 | Andy G3AGM | #packet-hf | "My node obliterates my 40m receive when it's running"
- 2026-10-05 19:58 | Steve G7TAJ | #packet-hf | "it would be helpful to run a script to stop TX, move freq , get the RX and revert"
- 2026-10-05 20:30 | Tom M0LTE | #packet-hf | "Doing XMITOFF. It's the most universal solution I think, without inventing new stuff. pdn-mailcast will need to hold a sysop password in its config."
- 2026-10-06 09:15 | John G8BPQ | #packet-hf | "I received 10.5K of bulls in 2 mins (4.5k compressed)" ; Steve 09:24 queried the maths ("my bad maths makes that 300bd?"); Tom 09:37: "It's not straightforward to describe the speed. It's not just sending bulls the way you might exchange them with an FBB neighbour today."
- 2026-10-06 09:41 | Tom M0LTE | #packet-hf | "Mailcasts later today will contain some half-speed (600bps) bursts mixed in - the modem is autobaud so you'll hear that too. There will be more traffic too. Trying different strategies to maximise the number of chunks a station will hear on average over a 2-3 day period."
- 2026-10-06 11:00 | Tom M0LTE | #packet-hf | "600bps, 74 frames, 22 bulletins, 8 minutes."; Steve 11:00: "nothing here unfortunatly"
- 2026-10-06 13:08 | Tom M0LTE | #packet-hf | Posted an image "at M9PSY, following that mailcast" (waterfall/decodes; not viewed). 13:02: "I'm decoding frames right now through m9psy.tunnel.ubersdr.org"
- 2026-10-06 13:29 | Tom M0LTE | #packet-hf | "EI4HQ has received 19 complete bulletins via this method today... band sees to be staying long"
- 2026-10-06 14:13 | John G8BPQ | #packet-hf | "7 complete, lots of bits at gb7bpq. Nothing heard in Nottingham which suggests somethnig wrong with set up"; 15:11: "Another 4 received in Skigersta"
- 2026-10-06 14:13 | Steve G7TAJ | #packet-hf | "I dont even get a blip on the waterfall today. so it's condx"
- 2026-10-06 14:21 | Perry M0PYL | #packet-hf | [P] Suggested two nodes with a fast link between them could swap received chunks to reassemble messages. Tom: "oh now that's too clever". Perry 14:38: "My ideas for using fountain codes appears to have shipped".
- 2026-10-06 15:11 | Mike Quin MM0MQN | #packet-hf | "Sounds like bittorrent"
- 2026-10-06 15:52 | Szymon G3KEY | #packet-hf | [P] Compared it to broadcasting programs as 15 second recordings on local radio to a computer club (cut).
- 2026-10-07 07:21 | Tom M0LTE | #packet-hf | "First TX 10am and hourly."
- 2026-10-07 09:05 | Steve G7TAJ | #packet-hf | "not hearing RDG this AM again but on M9 ubsersdr, it does look outside 3khz?" 09:06: "~3.2 from this" [image]
- 2026-10-07 10:31 | Robin M0JQQ | #packet-hf | "Last slot (10:00 UTC): Not enough decoded to measure." (copy of receiver status line); 11:10: "Getting bite this time, hopefully the next time slot will be a bit better"
- 2026-10-07 11:10 | Eoin M0NVK (GB7IOW) | #packet-hf | "this is super strong now, GB7RDG-2>EI0RSI-1"
- 2026-10-07 11:12 | Tom M0LTE | #packet-hf | "I'm trying to convince Vic to get a listening station for the mailcast over in EI... EI4HQ got SO much of the test traffic yesterday, far more than England stations."
- 2026-10-07 11:24 | Tom M0LTE | #packet-hf | "When each bar is full it will attempt to deliver it to your BPQ, and it will appear in the normal mail list." "It's completely normal for your LinBPQ BBS to reject the bulletins it already has."
- 2026-10-07 12:15 | John G8BPQ | #packet-hf | "One frame in Nottingham and two in Skigerta n 1300 slot. S/N 1.1 today arond 7 yesterday" ; Tom: "huge variability."
- 2026-10-07 12:21 | Eoin M0NVK | #packet-hf | "ChatGpt loves the channel report and graph. 4 pages on interpretation of it"; posted two .docx summaries ("HF 600 Baud Channel Summary", 12:25 and 13:01; not opened). 12:26-12:28: "mainly want to keep an eye on SNR... Your biggest issue is probably SNR"
- 2026-10-07 12:45 | Steve G7TAJ | #packet-hf | "600 bps (WN3)" then a code block "Last slot" (receiver status; body lost, see Section 4)
- 2026-10-07 13:03 | Steve G7TAJ | #packet-hf | "I think I got that freq change slightly wrong `-87.8 Hz, -0.2 dB`"
- 2026-10-07 13:19 | Steve G7TAJ | #packet-hf | "I guess I have no chance of receving these as they have 'been sent' and there is no way to request a re-send"; Tom: "They're re-sent in a rolling window over the next few days"; 13:30: "additional chunks of them are sent over the coming 3 days. Once you have received n chunks, you will have the messages. It doesn't matter which chunks. That's fountain codes."
- 2026-10-07 13:29 | Steve G7TAJ | #packet-hf | "looks like AGC was affecting the bandwidth. I thought that was off but it was cutting off" [image]
- 2026-10-07 14:03 | Steve G7TAJ | #packet-hf | "4 frames. 'a little hot' but the audio is at the lowest it can go (QTSM now says its too low)"
- 2026-10-07 14:41 | Mike Quin MM0MQN | #packet-hf | "Success. Just need to get it talking to BPQ now." [image]
- 2026-10-07 16:10 | Eoin M0NVK | #packet-hf | "Note. tried a few different SDR last two slots...none work" (Tom has only UberSDR support, offered KiwiSDR)
- 2026-10-08 08:42 | Tom M9TWM (Tom's second call) | #packet-hf | "you've just generated the most legitimate mail the packet network has seen in years" (about the daily reports sent by packet mail)
- 2026-10-08 09:09 | Tom M0LTE | #packet-hf | Announced switch of transmit callsign GB7RDG to M0LTE: "Reason for this is it's technically operating outside the FAC for GB7RDG. Mail delivery etc is unaffected." (cut)
- 2026-10-08 09:13 | Tom M0LTE | #packet-hf | On when the daily report goes: "It will get sent out at the start of the next session. It won't queue them up, just sends the latest."
- 2026-10-08 09:44 | John G8BPQ | #packet-hf | "I have qo100 tx live and hope to get the Rx back online early late next week."
- 2026-10-08 14:08 | Steve G7TAJ | #packet-hf | "Mine says for the last slot `-3.3 Hz, 7.5 dB`"; Tom 14:11 explains the offset is CW-tone vs expected, and that rig filter shape matters; 14:15 "wow, that's a lot more multipath than say M9PSY is receiving"; Steve: "probably the Mendips and Quantocks"; rig FT-450AT.
- 2026-10-08 14:36 | Steve G7TAJ | #packet-hf | "I get way better packet decodes with AGC at fast than OFF. which it on, audio is 300-2700. Off is 0-3000"
- 2026-10-09 10:26 | Andy G3AGM | #packet-hf | "I saw the CW tone but nothing on any of my radios for the wide band signal. Is it essentially a weak-signal mode like WSPR..." Tom 10:32: "It's the same power budget spread out, unsurprising."
- 2026-10-09 11:33 | Steve G7TAJ | #packet-hf | Pasted receiver text: "Last slot (11:00 UTC): 3 paths: 0.5 ms after the first, 6 dB weaker; and 1.8 ms after the first, 13 dB weaker. Reflection about 270 km up. Doppler spread 0.3 Hz: steady (good for 1200 bps)."
- 2026-10-09 13:04 | Steve G7TAJ | #packet-hf | "I'm onlt at +1.8dB today. 600 works for a few, that was ok but not enough chunks as you say. and 1200 ='hearing' but nothing more"
- 2026-10-09 15:08 | Steve G7TAJ | #packet-hf | "`09/10/2026, 16:00:54 1200 bps (WN4) SNR not measured yet 6 frames`" ; 15:09 "Used the 'filter button' to set the dial to 7052.460 and working well today it seems. well, way better than it was."
- 2026-10-09 21:07 | Mike G6PHF | #packet-hf | New listener: could hear 40m activity on FT-991A + QtSoundModem but saw only waterfall; Tom pointed to the ukpacketradio.network 40m info page. 10-10 10:03: "It appears I am hearing some stuff!" [image]
- 2026-10-10 07:40 | Andy G3AGM | #packet-hf | Setup problem: Q0CAST user "logged in, but it is not set up as a BBS forwarding partner". 09:12: "Yay! My hybrid setup picked up 23 frames of 600 bps (WN3). Not enough to form any bulls but getting closer" [image]; 09:28 posted "G3AGM Mailcast Signal Chain" PDF (not opened).
- 2026-10-10 18:24 | Eoin M0NVK | #packet-hf | "I have only just realised you are sending your daily report back on a Packet mail...O what fun!" 18:25: "Very clever Tom."
- 2026-10-11 11:24 | Tom M0LTE | #packet-hf | "Mailcast users: transmissions will be switching to a source callsign M0LTE instead of GB7RDG following the next slot."
- 2026-10-11 15:12 | Eoin M0NVK | #packet-hf | "As for mailcast last received show 3rd hop stronger than 2nd interesting data."
- 2026-10-12 15:14 | Jim GM4CXF | #packet-hf | "First complete set of slots and frames received. What would you like to see from the UI that would help you folks? All received in IO75qn (Troon)"
- 2026-10-12 18:55 | Eoin M0NVK | #packet-hf | "147 frames was best for me today."
- 2026-10-12 19:35 | Tom M0LTE | #packet-hf | "Hey Jim, thanks for the report! Did you check the box to send reports back to me?"
- 2026-10-13 12:11 | Jim GM4CXF | #packet-hf | "I now have a scripted setup with a Pi5 handling the mailcast and the PC with the BPQ32 suite for normal packet where the script automatically changes frequency after shutting off the HF packet tx function. I think I'm in love with Claude"
- 2026-10-13 13:16 | Steve G7TAJ | #packet-hf | "got a lot pending / ~50-80% though. perhaps a longer slot?"
- 2026-10-13 15:19 | Steve G7TAJ | #packet-hf | "When condx are ok, it works very well and far greater speeds than ever before on HF."
- 2026-10-13 15:16 | Tom M0LTE | #packet-hf | Explains MS110D probes between data blocks: the receiver "is able to collect the energy in the echoes and combine them for more snr." (cut)
- 2026-10-14 11:08 | Steve G7TAJ | #packet-hf | Asks for a "bytes RXed this session" figure; Perry notes air bytes and reassembled bytes differ.
- 2026-10-14 13:44 | Steve G7TAJ | #packet-hf | Housekeeping request: bulletins no longer transmitted clutter the list.
- 2026-10-14 14:03 | Steve G7TAJ | #packet-hf | Asks why the start of the TX is much stronger [image]; Tom 15:35: it is the channel probe, "sent before the data so receivers can measure the path. It looks hotter because it holds a steady level near its peak." (cut)
- 2026-10-14 09:34 to 10-15 08:14 | Anthony M7TAW | #packet-hf thread "-anthony---m7taw-windows-build-of-pdn-" | Windows build tried: "mailcast receiver isn't connecting to the BBS though which is the main issue" (10-14 09:34); "IP, Port and user/pass are all correct as far as I'm aware" (10-14 18:56); "I guess I should just move it all over to linux" (10-15 08:14). Mike Quin 10-14 10:50 found the Windows config path "C:\ProgramData\pdn-mailcast\receiver.json".
- 2026-10-15 10:52 | Tom M0LTE | #packet-hf | "A few of of us have been discussing where to take the mailcast idea next. Ideas include multiple sites transmitting chunks, either the same chunks that are being provided now by RDG, or potentially chunking bulletins independently."
- 2026-10-15 11:43 | Tom M0LTE | #packet-hf | "at the end- up to 7.8 minutes. The bulletin list is spread through the whole slot. Today's 11am slot added 10 additional bulletins at the end." ; 11:49: "next TX is going out with a ED25519 signed bulletin list"
- 2026-10-15 13:40 | Steve G7TAJ | #packet-hf | "After a rocky start, I'm getting 1200bps from RDG most slots and quite a lot of frames. ~40 bulls delivered over the period its been under test."
- 2026-10-15 14:35 | Tom M0LTE | #packet-hf | "This is covered in the paper." "(You're describing option A from the paper, option B is the north star)" "There's no state where a bulletin is delivered broken."
- 2026-10-15 18:02 | Jim GM4CXF | #packet-hf | "Are my daily reports getting down to you fine folks? I'm not seeing anything on the packet logs."
- 2026-10-16 10:31 | Steve G7TAJ | #packet-hf | "idea - can the slot statistics be written to the journal after the slot ends, i.e. after it hears the finishing CW?" Tom 11:01: "Yes, check the system journal."
- 2026-10-16 11:02 | Tom M0LTE | #packet-hf | "The sounding waveform which appears after the CW ident sounds mad"
- 2026-10-16 16:19 | Perry M0PYL | #packet-hf | Linked his reply on Tom's gist (design discussion) (link: gist.github.com/M0LTE/a0ef207d1ca7295251edfec8e850db39, comment 6430558).
- 2026-10-17 09:02 | Tom M0LTE | #packet-hf | On G8BPQ's problem: "mailcast is upsetting the bpq login flow by sending the password before bpq is ready for it"; 10:58: "0.16.2 fixes the PASSWORD error and lets you use rig with your hooks and no bpq section." G8BPQ 09:14 recommended FBBPORT over TCPPORT.
- 2026-10-18 11:01 | Eoin M0NVK | #packet-hf | "Mailcast in action." [image]

### Late October: spread, survey, radar

- 2026-10-22 08:12 | Eoin M0NVK | #packet-hf | "GB7IOW is mailcasting today so HF port is not running." Repeated 10-28 07:04 and 11-04 07:01 ("Mailcast is on.") and 11-02 20:12.
- 2026-10-22 09:19 | John G8BPQ | #packet-hf | "I got at lot of frames in Skigerta but none in Nottingham. I think the propagation was very long"; same minute Anthony M7TAW: "Is RDG running this morning. Think condx terrible - couldn't hear it."
- 2026-10-22 09:19 | John G8BPQ | #packet-hf | "I use the before and after hooks to stop and restart ardopcf and qtsm around each slot"
- 2026-10-22 09:27 | John G8BPQ | #packet-hf | Overruns on a Pi4 sharing audio with arecord/aplay ("half second dropouts"); 11:00: "I moved my asoundrc from the per user file to the system file and it now works." Tom 10:47: the post hook "runs at a fixed time, 12 minutes after the slot starts, not at the detected end."
- 2026-10-22 19:55 | Perry M0PYL | #packet-hf | "how has mailcast been going for the last week or so? Are a reasonable number of people decoding the bulls?" Mike Quin 20:54: "Conditions seem to have been a bit meh, I've not gotten much in the last few days." Steve 21:16: "75 coming in, 103 already had".
- 2026-10-22 21:23 | Tom M0LTE | #packet-hf | "here's the results of my two week UberSDR survey - huge range of results - huge range of antennas, locations, radios, and QRM. Many variables." (two images; not viewed). Perry 21:33: "g7taj only decoded 30% on the 18th, but managed to extract 22 bulletins!"
- 2026-10-22 21:28 | Tom M0LTE | #packet-hf | "RDG didn't transmit any of this afternoon's bulletins due to the channel apparently being busy throughout - which sounds like a QRM source. I'm having it record the channel as evidence for future refusals."
- 2026-10-22 21:40 | Tom M0LTE | #packet-hf | "appendix d waveform 4 (1200 bps) alternating slots with waveform 3 (600 bps)"
- 2026-10-23 12:42 | Eoin M0NVK | #packet-hf | "I have a few results to send you... Yesterday results... Was a hard day". Tom 13:23: "I'll incorporate all your manually sent reports at the end when I write this up"
- 2026-10-23 14:00 | Jim GM4CXF | #packet-hf | "I'm still struggling to get my MCAST reports to send." 14:58: "I cheated and e-mailed you". Tom 15:06: "nobody knows who routes to who any more, it's just devolved largely to fan out bulletin distribution"
- 2026-10-23 15:13 | Jim GM4CXF | #packet-hf | "that last mailcast was a goody" [image]; Steve: "+thats a lot of skips+"
- 2026-10-23 16:02 | Eoin M0NVK | #packet-hf | [P] Saw a signal on the waterfall that looked like Tom's tone, with stops and starts; Tom had no more TX that day. 16:19: "OTH radar running lower...circa 7.04"
- 2026-10-23 19:48 | John G8BPQ | #packet-hf | "My MCAST reports usually get sent soon after the slot ends when normally the band is still open, particularly from BPQ."
- 2026-10-24 09:08 | Anthony M7TAW | #packet-hf | "can I run bpq have the dial set to 7050.1 and still receive mailcast if I get everything setup on the Pi?"; John 11:13: rig control lets mailcast change to 7052 for the slot and back.
- 2026-10-24 16:19 | Jim GM4CXF | #packet-hf | "This is what I see on the packet freqency just after the mailcast finishes - not much in the way of valid packets when I check the mailcast rx" [image]
- 2026-10-26 06:43 | Tom M0LTE | #packet-hf | "There is an update to install please- a slight on-air format change to make. This version will tolerate both on-air formats, and in a week I'll switch RDG over to transmitting the new format and remove the old one from the code."
- 2026-10-26 10:27 | Steve G7TAJ | #packet-hf | "wessex works a bit better than mine ever did"; Tom 10:28: "WESSEX is a WebSDR, so no hardware filters... Consider trying a session with your UberSDR hooked up to your main antenna"
- 2026-10-26 12:17 | Perry M0PYL | #packet-hf | "Is anyone going to FOSDEM that can talk about Mailcast? ... they're asking for submissions"
- 2026-10-27 15:02 | Steve G7TAJ | #packet-hf (and #intruder-watch 15:04) | "looks like someone is spamming the 7052 pdn mailcast with an AM broadcast"; "and then FT8 over the top". Tom: "and yet, it's still decoding". Steve: "not here unfortunatly". In #intruder-watch: "packet pdn mailcast being spammed on 40 with an LSB broadcast (RU). Probably becasue they noticed the large 3khz transmisions" [image].
- 2026-10-27 15:19 | Anthony M7TAW | #packet-hf | "Managed to finally get some MCASTs through IOW. Its a mix of Wessex and my audio at some point. The mailcast is now on the Pi, with the rig dialled to 7050.1."
- 2026-10-27 16:34 | Tom M0LTE | #packet-hf | "no need to be using cron for this, I recommend you use the hooks built in; no guarantee that the schedule will always be on-the-hour, and mailcast sends a schedule."
- 2026-10-28 04:36 | Tom M0LTE | #packet-hf | "I've just shipped an update to mailcast which changes it so the web interface is available on the lan by default. If you don't want this, put "lan": false in your config."
- 2026-10-28 09:17 | Steve G7TAJ | #packet-hf | "pdn-mailcast v0.22.0 has a rather large 'Today' section. Div seems to be a bit OTT"; 10:56-11:00: fixes itself once there are 2 slots. Repeated 11-02 10:02 ("thats all my screen!").
- 2026-10-28 09:43 | Vic EI5IYB | #packet-hf | "I am on mailcast receive as well using ubersdr:ei4hq.tunnel.ubersdr.org"; 15:09: "Mailcast running on Pi2W" [images]; Perry: "spicy amounts of cpu".
- 2026-10-28 10:01 | Anthony M7TAW | #packet-hf | "niche issue but the G90 needs the mode to be UD not USB. Mailcast rigctl set it to USB which doesn't lineout audio annoyingly." 10-29 13:44-13:49: "3k on UD versus 2.7k on USB"; Tom: "that's valuable SNR for mailcast." 13:55: "I'm going to put a change in which defaults to PKTUSB"
- 2026-10-28 12:21 | Steve G7TAJ | #packet-hf | "leaving it running between slots seems to help the SNR part?" Tom: "It shouldn't... it just sits idle between slots."
- 2026-10-28 17:51 | Steve G7TAJ | #packet-hf | "GB7RDG: heard 8 of 8 slots, 193 frames, 1 bulletin completed, tone SNR 8 dB (median), virtual height 263 km (median)" (this is a receiver report line; see Section 4). 17:54: "the total bytes RXed today is 47k !"; 17:55: "My taking from this is that 7 mins on the hour for 8 slots we have is not enough air time. For me at-least. and that was me running on a 100% copy SDR for 4 days !"
- 2026-10-28 19:39 | Tom M0LTE | #packet-hf | "I did a 2 week survey monitoring some 10 or so UK websdrs, and most of them got all bulletins. I wonder if there's something else about your setup which is hurting it?" "(7 mins on the hour was more than enough for most of the sites I surveyed, by quite some margin)" "I've got quite a lot of MCAST reports now, I'll crunch some data." 20:00: "it sounds like you've been unlucky or possibly there's benefit in tweaking the chunk mix."
- 2026-10-28 19:59 | Steve G7TAJ | #packet-hf | "I was just supprosed by the quantity of data received but only 1 bull decoded" (using Wessex)
- 2026-10-29 09:28 | Steve G7TAJ | #packet-hf | "`45, 12.2 KB decoded, last at 09:07:32`"
- 2026-10-29 09:16 | Mike Quin MM0MQN | #gm-packet-network | "Part of the mailcast thing that Tom's been playing around with... is mail reporting via packet mail. It looks like my systems first attempt didn't get past NDHBBS."
- 2026-10-29 23:24 | Tom M0LTE | #packet-hf | Announced the 2026-10-30 MS110D test day with SA6DAZ's rig in Sweden (RDG TX every 10 min 06:00-08:00, :20 and :40 through the day, every 10 min sunset to 23:00) (cut). 23:37: "AX.25 IL2P+CRC UI frames, about 100 bytes of random text and a 16 byte self-describing header. 1 frame at WN1, 2 at WN2, 4 at WN3, 8 at WN4, 11 at WN5, and 16 at WN6, WN7, WN8 and WN13."
- 2026-10-30 09:12 | Steve G7TAJ | #packet-hf | "Working well at low levels this AM" "`9 bursts 21 frames`"
- 2026-10-30 15:02 | Steve G7TAJ | #packet-hf | "working at 1200bd even through this!" [image]; Tom: "Quite hilarious having a NATO mode competing with a mil radar"; 15:07 "nino on the far left, MS110D on the mid left, radar EVERYWHERE else"; Steve 15:11: "yes, only 1.1dB on that run." 15:12: "The tone was +9.6dB but possibly not caught in the radar"
- 2026-10-30 15:14 | Daz SA6DAZ | #packet-hf | "I got totally wiped out by the radar on that one" [image]; 15:44 "The radar has finally stopped"
- 2026-10-30 15:17 | Tom M0LTE | #packet-hf | "looking at early results from the Sweden experiment, Daz' IC7300 in Sweden has successfully decoded multiple 6400bps frames sent by Reading today. 11 out of 16 frames at 07:50, 12 out of 16 frames at 09:40. Same 3kHz width signal as the WN3/4 modes we've been using. QAM16 over a real HF channel."
- 2026-10-30 15:19 | Perry M0PYL / Tom M0LTE | #packet-hf | Radar mitigation discussion: Perry proposes blanking (zero out-of-range samples, then low-pass) and frequency hopping; Tom: "Or just throw all the FEC at it." "The NinoTNC packet modems are ~500Hz, MS110D appendix D is always 3kHz."

### November

- 2026-11-02 08:16 | Steve G7TAJ | #packet-hf | "didnt hear much yesterday on pdn-mailcast. was that condx or other testing?" 10:44 John G8BPQ: "I got very little on multicast yesterday as well"
- 2026-11-02 12:21 | Chris G6FCI | #packet-hf | New interest: "Where can I find out more about multicast? It sounds and looks interesting." (he runs XRouter). Tom 20:00: "I can very easily add explicit xrouter support if needed." Tom 11-03 23:04: "Please try v0.33.1, this should contain a working xrouter integration - though I have no real means to test it myself."
- 2026-11-02 17:04 | John G8BPQ | #packet-hf | "GB7NOT SSD has failed so multicast testing has been suspended." 21:38: "I did pick up the pilot tone a couple of times on NOT but propagation has been terrible."
- 2026-11-02 20:01 | Tom M0LTE | #packet-hf | "oh cool, that's the first time I've seen it find two layers." (about the receiver's path/multipath display)
- 2026-11-02 10:54 | Jim GM4CXF | #packet-hf | "This my path(s) from the 10 o'clock cast. Getting complicated but still working." [image]
- 2026-11-02 12:13 | Perry M0PYL | #data-modes | "There's a bunch of experiments going on in #packet-hf at the moment using fountain codes to distribute messages to multiple receivers."
- 2026-11-03 12:16 | Steve G7TAJ | #packet-hf | "+7db or more today but only 6 bursts and 11 frames"; Tom 20:25: "That 7dB is the CW tone I guess? So not very much SNR for a 3kHz wide signal"; Steve 21:49: "the actual decodes are -0.9 -> 3.6dB"; Tom: "Yeah that's tight."
- 2026-11-05 14:08 | Anthony M7TAW | #packet-hf | "where do you pull the ionosonde data from and how does it calc the bounce?" Tom 14:09: "Whichever is working, they are unreliable." 21:46: "I got opus to write up a short description with pointers to source." (docs/channel-probe.md in pdn-mailcast)
- 2026-11-04 07:00 / 2026-11-06 16:46 | Eoin M0NVK | #packet-hf | His own transmit signal quality problems at GB7IOW ("Spikes and rubbish"), not mailcast related; he kept HF off and ran mailcast receive instead.

### Other channels

- 2026-10-23 10:31 | Tom M0LTE | #modem-dsp | "I've just extracted my AGPL3 implementation of MIL-STD-118-110D appendix D (STANAG 5069) modem to a standalone library. It covers waveforms 0-8 and 13, with waveforms 3 and 4 in use on air for successful packet mailcast transmissions in the UK." (verbatim, including the "118" as typed.)
- 2026-10-06 06:43 | John G8BPQ | #ninotnc-help | "Had a power cut in Skigersta around midnight last night so pdn-mailcast-receiver has been down."
- 2026-10-19 16:03 | Anthony M7TAW | #packet-network | "Bulls I guess I'm getting through the mailcast, guess makes sense just to go personals tbh"
- 2026-10-31 23:16 | Anthony M7TAW | #packet-general | "Ahh were you thinking about mailcast? All good!" (context not read)

## 3. Reception results stated in messages

Locators are given only where the author or a well known station location is evident; blanks mean not stated in the visible text.

| Date (UTC) | Station / author | Location / locator | Receiver | What was decoded or reported |
|---|---|---|---|---|
| 10-05 15:20 | Wessex UberSDR (reported by Tom M0LTE) | Wessex, UK | UberSDR (web) | "copied 100%"; Tom: 41 frames copied 100% accurately at 1200 bps |
| 10-05 | G7TAJ Steve | not stated (his remark about Mendips and Quantocks suggests south-west England; inferred) | Own receiver | Tone and CW strong, no data decode on first test; later 10-15: ~40 bulletins delivered; 1200 bps most slots |
| 10-06 | G8BPQ John | Skigersta, Isle of Lewis (GB7BPQ) | Own | 10.5K of bulls in 2 min (4.5K compressed); 7 complete, later +4 |
| 10-06 | G8BPQ John | Nottingham (GB7NOT) | Own | Nothing heard on 10-06; "One frame in Nottingham and two in Skigerta" on 10-07 1300 slot |
| 10-06 | EI4HQ (reported by Tom) | Ireland | UberSDR (ei4hq.tunnel.ubersdr.org) | 19 complete bulletins received that day |
| 10-06 | Tom M0LTE | M9PSY (Nathan's UberSDR) | UberSDR | Decoding frames via m9psy.tunnel.ubersdr.org |
| 10-07 | G8BPQ John | Skigersta/Nottingham | Own | S/N "1.1 today around 7 yesterday" |
| 10-07 | M0JQQ Robin | IO95ec | Own | "Not enough decoded to measure" (10:00 slot); "Getting bite this time" later |
| 10-07 | MM0MQN Mike Quin | Scotland (MM0, via Nathan's SDR first) | UberSDR then local | "Success", then BPQ delivery work |
| 10-08 | G7TAJ | as above | FT-450AT | tone offset -3.3 Hz, 7.5 dB |
| 10-09 11:00 | G7TAJ | as above | Own (UberSDR/rig) | Receiver text: 3 paths, 0.5 ms after the first (6 dB weaker) and 1.8 ms after (13 dB weaker); reflection about 270 km up; Doppler spread 0.3 Hz |
| 10-09 16:00 | G7TAJ | as above | Own | 1200 bps (WN4), 6 frames; freq +7.1 Hz, 11 dB; dial 7052.460 working well |
| 10-10 | G3AGM Andy | (not stated) | Hybrid (NinoTNC/Pi/XRouter plus separate receiver) | 23 frames of 600 bps (WN3), no complete bulletins |
| 10-10 | G6PHF Mike | (not stated) | FT-991A + QtSoundModem | "hearing some stuff" (screenshot) |
| 10-12 | GM4CXF Jim | IO75qn (Troon) | Pi5 | First complete set of slots and frames |
| 10-12 | M0NVK Eoin (GB7IOW) | IO90hr | Icom rig (model not stated); SDRconnect tried later | 147 frames best of the day |
| 10-13 | G7TAJ | as above | Own | ~50-80% pending on the bulletin bars |
| 10-18 | G7TAJ (reported via Perry/Tom survey) | as above | Own | ~30% decoded, 22 bulletins extracted |
| 10-22 | G8BPQ | Skigersta (frames) / Nottingham (none) | Own | "a lot of frames in Skigerta but none in Nottingham... propagation was very long" |
| 10-22 | G7TAJ | as above | Own | 75 chunks coming in, 103 already had |
| 10-28 | G7TAJ | as above | Wessex SDR for 4 days | "GB7RDG: heard 8 of 8 slots, 193 frames, 1 bulletin completed, tone SNR 8 dB (median), virtual height 263 km (median)"; 47k bytes received that day |
| 10-29 09:07 | G7TAJ | as above | Own | "45, 12.2 KB decoded" |
| 10-30 | G7TAJ | as above | Own | 9 bursts, 21 frames (AM); 1.1 dB SNR during the radar sweep; tone +9.6 dB |
| 10-30 | SA6DAZ Daz (reported by Tom) | Sweden (IC-7300, 40 m) | Own | 11/16 frames at 07:50 and 12/16 at 09:40 of 6400 bps (QAM16, same 3 kHz width); later wiped out by radar |
| 11-03 12:16 | G7TAJ | as above | Own | +7 dB tone, 6 bursts, 11 frames; real decode SNR -0.9 to 3.6 dB |

## 4. Pasted reports and data

No "MCR ..." / "MCR1 ..." report text is visible in the archive (zero search hits). Pasted receiver status text that survived on a single line is reproduced below verbatim. Multi-line pastes lost everything after their first line in the archive; those are listed with the surviving fragment so they can be fetched from Discord by timestamp.

Survived in full (single line):

Steve G7TAJ, 2026-10-09 11:33 UTC, #packet-hf (as a quoted receiver line; id not available):

```
Last slot (11:00 UTC): 3 paths: 0.5 ms after the first, 6 dB weaker; and 1.8 ms after the first, 13 dB weaker. Reflection about 270 km up. Doppler spread 0.3 Hz: steady (good for 1200 bps).
```

Steve G7TAJ, 2026-10-28 17:51 UTC, #packet-hf (id not available):

```
GB7RDG: heard 8 of 8 slots, 193 frames, 1 bulletin completed, tone SNR 8 dB (median), virtual height 263 km (median)
```

Steve G7TAJ, 2026-10-29 09:28 UTC, #packet-hf (id not available):

```
45, 12.2 KB decoded, last at 09:07:32
```

Steve G7TAJ, 2026-10-09 15:08 UTC, #packet-hf (id not available):

```
09/10/2026, 16:00:54 1200 bps (WN4) SNR not measured yet 6 frames
```

Steve G7TAJ, 2026-10-30 09:12 UTC, #packet-hf (id not available):

```
9 bursts 21 frames
```

Robin M0JQQ, 2026-10-07 10:31 UTC, #packet-hf (opening line only, rest cut):

```
"Last slot (10:00 UTC): Not enough decoded to measure.
```

Truncated in the archive (body lost; fetch from Discord by timestamp, all #packet-hf):

- 2026-10-07 12:46 Steve G7TAJ: opens a code block "Last slot ..." (receiver status for the 600 bps WN3 slot).
- 2026-10-12 09:13 Steve G7TAJ: code block starting "Speed".
- 2026-10-13 11:08 Steve G7TAJ: code block starting "1200 bps (WN4)".
- 2026-10-26 11:17 Anthony M7TAW: a trace of failed MCAST report delivery to RDG ("I think something must be misconfigured. Trying to get the MCAST reports through to RDG:").
- 2026-10-28 12:29 Anthony M7TAW: a config block (rigctld/hook setup) for the G90.
- 2026-10-29 13:35 Tom M0LTE: asks Anthony to put the radio in UD and type a command (command lost).
- 2026-10-12 and 2026-10-23: Eoin M0NVK (GB7IOW) "Yesterday results" manual MCAST reports (10-23 12:42), contents not in the text; image/attachment not opened.
- 2026-10-10 18:21 Eoin M0NVK: an image of the mail delivered to his BBS by the receiver (and the technical report returned to Tom).

## 5. Pointers for the paper and article

- Attribution: use the names and callsigns above as shown in the archive. Eoin's Discord name is "Eoin M0NVK" (GB7IOW); Steve is G7TAJ (GB7BWR node); John is G8BPQ (GB7BPQ and GB7NOT); Jim is GM4CXF; Anthony is M7TAW (now GB7PTR); Perry is M0PYL; Mike Quin is MM0MQN; Vic is EI5IYB; Daz is SA6DAZ; Andy is G3AGM; Robin is M0JQQ; Mike is G6PHF; Chris is G6FCI. Ask each person before naming them publicly if the publication needs consent.
- For the survey chart and the daily report data, the two raw sources are Tom's own packet mail archive on GB7RDG (the MCAST reports) and the survey images Tom posted 2026-10-22 21:23 and 21:25 (not opened).
- Items worth getting from the Discord client directly (the archive cannot supply them): the full text of the code blocks listed in Section 4, the two survey images, and Eoin's two "HF 600 Baud Channel Summary" .docx files.

## 6. From the full channel export (added 2026-11-07)

Tom exported #packet-hf (6 October to 6 November, 1149 messages with all attachments) on 2026-11-07. The raw export and the 99 attachments are kept outside the repo, since they hold everyone's chat. What it settled:

- **The 22 October tables** (Tom, 21:23 and 21:25 UTC) were tables, not charts. They are rebuilt from the archived data by `paper/figures/heard_share.py` (output in `paper/figures/heard-share-tables.md`). A share is frames heard over frames sent (including what a slot cut short did send) in the slots the listener was listening to. With that rule the weekly table matches Tom's in every cell but one (WESSEX in the first week, 65% against 64%), and the survey rows of the daily table match exactly. The gaps in the listener rows are reports missing from the archive: G7TAJ and GM8BPQ for 16 to 18 October and M0NVK for 18 October had expired from GB7RDG before they were copied, and M0NVK on 21 October is not in the archive at all. Days on which a receiver had no audio at all show as "-".
- **Eoin M0NVK's "Yesterday results"** (23 October 12:42 UTC) is his MCR report for 22 October, identical to the copy already in `reports/daily/2026-10-22/M0NVK.txt`. No other report was pasted in the channel.
- **M7TAW's reports refused on the way to GB7RDG** (Anthony M7TAW, 26 October 11:17 UTC, message 1564236639947989043): his BBS connected to GB7IOW (M0NVK) and offered five MCAST reports as FBB proposals (`FA B M7TAW GB7RDG.#42.GBR.EURO MCAST 6292P0M7TAW 493` and four more); GB7IOW answered each with "Invalid Command", because M7TAW was not set up there as a forwarding partner, and the session was dropped. Eoin did not want to add him as a BBS partner without ETCC registration; Anthony pointed out that a Foundation station under 5 W on its own callsign does not need it, and applied anyway. Two of his reports (21 and 22 October) did reach GB7RDG via GB7IOW on 27 October, by their R: lines; most of the rest are in the archive only because Tom pasted them from GB7PTR. A good example for the paper of report routing failing for reasons outside mailcast.
- **G7TAJ's receiver pastes**, in full:
  - 7 October 13:46 UTC: "Last slot / -1.5 Hz, 7.2 dB / Tone at 1798.5 Hz (it should be at 1800 Hz), signal to noise in 3 kHz, 10 s long."
  - 13 October 12:08 UTC: "1200 bps (WN4) / Last burst at 12:06:35, 1 frame. / Last slot / 1200 bps (WN4), 14 frames / GB7RDG says / 1200 bps (WN4), which agrees"
- **Eoin's .docx summaries** were written by his LLM (Tom, 2026-11-07): background only, not a source.
