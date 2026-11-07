## 2. Related work

**Fountain codes and one-way file delivery.** The idea of a "digital fountain", from which any number of receivers collect a file at times of their own choosing with no feedback channel, comes from Byers, Luby, Mitzenmacher and Rege [1]. LT codes [2] were the first practical rateless codes and Raptor codes [3] made them linear time; RaptorQ [4], their standardised successor, fails at most once in a million given two symbols beyond the source block. These codes are carried by the IETF's one-way delivery protocols, ALC and FLUTE [5, 6], and are in deployed broadcast systems: 3GPP MBMS uses the earlier Raptor code [7], and ATSC 3.0 television uses RaptorQ [8]. Mailcast borrows this unchanged; its directory plays the part of FLUTE's file delivery table, and its content-hash object ID that of a BitTorrent info hash [9].

**Amateur broadcast of bulletins and files.** One-way bulletin delivery is old in amateur radio: W1AW sends ARRL bulletins on HF in Baudot, PSK31 and MFSK16 as well as CW [10]. The PACSAT broadcast protocol of 1990 [11] cut files into AX.25 UI frames, each carrying a file ID and offset so that frames could be sent "in any order and repeated at any time"; receive-only stations collected bulletins just by listening, and others could uplink a list of holes. In the same year Clark's BULLPRO [12] proposed broadcasting packet BBS bulletins as UI frames tagged with their BID and line number, sent several times, and noted that a listener could take several BULLPRO servers at once and sort out duplicates if all used a common BID. On HF, FLAMP sends files as checksummed blocks and fills gaps by sending again [14], and RDFT and HamDRM send files with Reed-Solomon coding, repeated instances and requests for missing segments [15, 16]. Since 2022 AMSAT-DL's QO-100 beacon has broadcast AMSAT bulletins and other data one way through a geostationary transponder [17].

Mailcast's structure (UI frames that each stand alone, receive-only listeners, a rotation of bulletins) is PACSAT's and BULLPRO's. What differs is how losses are made up and where bulletins go. Those systems rely on repetition and optional requests; mailcast has no return channel and uses a rateless code, so every frame a listener hears is new to it and pieces from different hours and days add up. And none of them hands what it receives to the listener's own BBS: mailcast's receiver offers each bulletin to the listener's LinBPQ, FBB or XRouter as an ordinary forwarding partner, using the FBB proposal protocol [13], so the BBS's own BID check does the duplicate removal.

**Fountain codes on HF.** Mailcast is not the first fountain-coded HF broadcast. Rhizomatica's HERMES project has had a RaptorQ data carousel for HF broadcast since August 2024 [18], sent over its open Mercury modem with FreeDV-style OFDM data modes, and in September 2026 brought RaptorQ file broadcast into Mercury itself [19]. Its documentation describes files from one sender, a mode set by hand at the receiver, and no link to mail or BBS systems; we found no published measurements from it. Military HF also delivers data to silent receivers: STANAG 5066 offers a non-ARQ service for broadcast and for receivers under emission control, and carries the ACP 142 reliable multicast protocol [20], but by repetition and delayed acknowledgement rather than rateless codes.

**HF waveforms and channels.** Amateur HF mail is mostly Winlink over PACTOR, ARDOP [21] or VARA [22], all two-way with negotiated speed; Codec 2 offers open OFDM data modes [23]; UK packet runs AX.25 [24], increasingly as IL2P [25]. Mailcast uses the public MIL-STD-188-110D Appendix D waveforms [26], tested on air in their 110C form [27], whose preamble tells any receiver what follows. Performance is judged on the Watterson channel [28] as set out in ITU-R F.1487 [29], whose mid-latitude conditions (1 ms and 0.5 Hz moderate, 2 ms and 1 Hz disturbed) bracket what mailcast's probe measured. WSPR and FT8 [30, 31] are one way and weak signal too, but carry a few dozen bits per transmission.

**Sounding.** Oblique HF sounders measure delay and Doppler spread with chirps, m-sequences and Barker codes [32]. Mailcast's probe does the same with a Zadoff-Chu sequence [33, 34] and reads delays with the mirror-reflection geometry used in ITU-R P.533 [35] over the NVIS paths described by Witvliet and Alsina-Pages [36]. The new part is only that every receiver of a data broadcast, web SDRs included, gets a sounding of its own path in every slot.

**Several senders.** Combining fountain-coded packets from several senders is also known: Byers, Luby and Mitzenmacher let a client download one encoding from several mirrors at once [37]. NAVTEX stations repeating a warning keep its identifier so ships print it once [38], and BULLPRO relied on a common BID across servers [12]. Model B's difference is that its senders do not share one encoding or one copy of the file. Each station gets a bulletin by its own forwarding route, reduces it to a canonical form and encodes it deterministically, so independent copies become one object whose pieces combine at the listener. We found no earlier system that does this; the on-air trial is still to come.

In short, mailcast's parts are borrowed. What we believe is new is the combination: rateless one-way delivery of packet BBS bulletins on amateur HF, received straight into unmodified BBSes, on an autobaud waveform, with a per-slot channel sounding at every receiver, and combining across independent transmitters by content addressing.

### References

[1] J. W. Byers, M. Luby, M. Mitzenmacher, A. Rege, "A digital fountain approach to reliable distribution of bulk data", ACM SIGCOMM Computer Communication Review 28(4), 56-67, 1998. doi:10.1145/285243.285258

[2] M. Luby, "LT codes", Proc. 43rd IEEE Symposium on Foundations of Computer Science, 271-280, 2002. doi:10.1109/SFCS.2002.1181950

[3] A. Shokrollahi, "Raptor codes", IEEE Transactions on Information Theory 52(6), 2551-2567, 2006. doi:10.1109/TIT.2006.874390

[4] M. Luby, A. Shokrollahi, M. Watson, T. Stockhammer, L. Minder, "RaptorQ Forward Error Correction Scheme for Object Delivery", RFC 6330, IETF, 2011.

[5] M. Luby, M. Watson, L. Vicisano, "Asynchronous Layered Coding (ALC) Protocol Instantiation", RFC 5775, IETF, 2010.

[6] T. Paila, R. Walsh, M. Luby, V. Roca, R. Lehtonen, "FLUTE - File Delivery over Unidirectional Transport", RFC 6726, IETF, 2012.

[7] M. Luby, A. Shokrollahi, M. Watson, T. Stockhammer, "Raptor Forward Error Correction Scheme for Object Delivery", RFC 5053, IETF, 2007 (identical to the code in 3GPP TS 26.346, MBMS).

[8] ATSC, "Signaling, Delivery, Synchronization, and Error Protection", A/331:2026-04, 2026.

[9] B. Cohen, "The BitTorrent Protocol Specification", BEP 3, 2008. https://www.bittorrent.org/beps/bep_0003.html

[10] ARRL, "W1AW Operating Schedule". https://www.arrl.org/w1aw-operating-schedule

[11] H. E. Price, J. Ward, "PACSAT Broadcast Protocol", ARRL Computer Networking Conference, 1990.

[12] T. Clark, "BULLPRO: A Simple Bulletin Distribution Protocol", ARRL Computer Networking Conference, 275-277, 1990.

[13] J.-P. Roubelat (F6FBB), FBB software documentation, appendices 9 (forward protocol) and 18 (unproto message lists), 1999.

[14] FLAMP, Amateur Multicast Protocol program (W1HKJ et al.), package description. https://packages.debian.org/bookworm/hamradio/flamp

[15] R. E. Taggart, "Digital Slow-Scan Television", QST, February 2004.

[16] PY4ZBZ, "HamDRM". https://www.qsl.net/p/py4zbz/hdsstv/HamDRM.htm

[17] P. Guelzow, "QO-100 High Speed Multi Media Beacon", AMSAT-DL, 2022.

[18] Rhizomatica, hermes-broadcast (HERMES data broadcast over HF, RaptorQ carousel), 2024-2026. https://github.com/Rhizomatica/hermes-broadcast

[19] Rhizomatica, Mercury pull request 238, "Broadcast file transfer (RaptorQ)", 2026. https://github.com/Rhizomatica/mercury/pull/238

[20] NATO, STANAG 5066 Edition 4 (draft v1.6), "Technical Standards for HF Radio Link Layer and Application Support Protocols", 2021.

[21] R. Muething, M. Pitts, J. Wiseman, "ARDOP (Amateur Radio Digital Open Protocol)", ARRL/TAPR Digital Communications Conference, 2015.

[22] EA5HVK, VARA HF modem. https://rosmodem.wordpress.com/

[23] D. Rowe et al., Codec 2 data modes, README_data.md. https://github.com/drowe67/codec2

[24] W. A. Beech, D. E. Nielsen, J. Taylor, "AX.25 Link Access Protocol for Amateur Packet Radio", version 2.2, TAPR, rev. 2017.

[25] N. Carrillo, "IL2P Specification", draft v0.6, 2024. https://tarpn.net/t/il2p/il2p.html

[26] US DoD, MIL-STD-188-110D, "Interoperability and Performance Standards for Data Modems", 2017.

[27] W. N. Furman, J. W. Nieto, "Latest on-air testing of U.S. MIL-STD-188-110C Appendix D wideband HF data waveforms", IET IRST 2012. doi:10.1049/cp.2012.0386

[28] C. C. Watterson, J. R. Juroshek, W. D. Bensema, "Experimental confirmation of an HF channel model", IEEE Transactions on Communication Technology 18(6), 792-803, 1970. doi:10.1109/TCOM.1970.1090438

[29] ITU-R F.1487, "Testing of HF modems with bandwidths of up to about 12 kHz using ionospheric channel simulators", 2000.

[30] J. Taylor, B. Walker, "WSPRing Around the World", QST, November 2010, 30-32.

[31] S. Franke, B. Somerville, J. Taylor, "The FT4 and FT8 Communication Protocols", QEX, July/August 2020, 7-17.

[32] M. Hervas, P. Bergada, R. M. Alsina-Pages, "Ionospheric Narrowband and Wideband HF Soundings for Communications Purposes: A Review", Sensors 20(9), 2486, 2020. doi:10.3390/s20092486

[33] D. C. Chu, "Polyphase codes with good periodic correlation properties", IEEE Transactions on Information Theory 18(4), 531-532, 1972. doi:10.1109/TIT.1972.1054840

[34] R. L. Frank, S. A. Zadoff, R. C. Heimiller, "Phase shift pulse codes with good periodic correlation properties", IRE Transactions on Information Theory 8(6), 381-382, 1962. doi:10.1109/TIT.1962.1057786

[35] ITU-R P.533-14, "Method for the prediction of the performance of HF circuits", 2019.

[36] B. A. Witvliet, R. M. Alsina-Pages, "Radio communication via Near Vertical Incidence Skywave propagation: an overview", Telecommunication Systems 66(2), 295-309, 2017. doi:10.1007/s11235-017-0287-2

[37] J. W. Byers, M. Luby, M. Mitzenmacher, "Accessing multiple mirror sites in parallel: using Tornado codes to speed up downloads", Proc. IEEE INFOCOM '99, 275-283, 1999. doi:10.1109/INFCOM.1999.749293

[38] ITU-R M.540-2, "Operational and technical characteristics for an automated direct-printing telegraph system for promulgation of navigational and meteorological warnings and urgent information to ships" (NAVTEX), 1990.
