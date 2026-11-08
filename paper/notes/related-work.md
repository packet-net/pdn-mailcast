# Related work: verified sources

Literature search for section 2 of the mailcast paper, done 7 November 2026. Every entry below was opened (web page, PDF text, or publisher/Crossref metadata) and checked for title, authors, year and the point it is cited for. "How verified" says exactly what was read. Where only metadata was checked, that is said, and the note sticks to what the title or abstract supports.

## A. Fountain codes and broadcast file delivery

1. J. W. Byers, M. Luby, M. Mitzenmacher, A. Rege, "A digital fountain approach to reliable distribution of bulk data", ACM SIGCOMM Computer Communication Review 28(4), pp. 56-67, October 1998 (Proc. SIGCOMM '98). DOI 10.1145/285243.285258.
   - Relevant: defines the "digital fountain": any number of heterogeneous clients collect bulk data at times of their choosing, with no feedback channel needed even under high loss. This is the idea mailcast applies to HF.
   - How verified: Crossref metadata for the DOI; abstract read on the SIGCOMM '98 programme page (conferences2.sigcomm.org/sigcomm/1998/tp/abs_05.html).

2. M. Luby, "LT codes", Proc. 43rd Annual IEEE Symposium on Foundations of Computer Science (FOCS 2002), pp. 271-280, 2002. DOI 10.1109/SFCS.2002.1181950.
   - Relevant: first practical rateless (fountain) code.
   - How verified: Crossref metadata (title, author, venue, pages); DOI resolves to IEEE Xplore 1181950. Shokrollahi's abstract [3] describes LT codes as "a new class of codes introduced by Luby for the purpose of scalable and fault-tolerant distribution of data". Full text not read.

3. A. Shokrollahi, "Raptor codes", IEEE Transactions on Information Theory 52(6), pp. 2551-2567, June 2006. DOI 10.1109/TIT.2006.874390.
   - Relevant: Raptor codes extend LT codes to linear-time encoding and decoding; any (1+epsilon)k output symbols recover k source symbols with high probability; systematic versions.
   - How verified: first page and abstract read from a PDF copy (people.ece.ubc.ca/janm/Papers_RG/Shokrollahi_IT_June06.pdf); journal header confirms volume, issue, page 2551, June 2006.

4. M. Luby, A. Shokrollahi, M. Watson, T. Stockhammer, L. Minder, "RaptorQ Forward Error Correction Scheme for Object Delivery", IETF RFC 6330, August 2011. https://www.rfc-editor.org/rfc/rfc6330
   - Relevant: the code mailcast uses. Section 5.8: a compliant decoder fails at most 1 in 100 times with K' symbols, 1 in 10,000 with K'+1, and 1 in 1,000,000 with K'+2. This backs the paper's "any K of them, plus a couple more".
   - How verified: RFC text read (abstract and section 5.8).

5. M. Luby, A. Shokrollahi, M. Watson, T. Stockhammer, "Raptor Forward Error Correction Scheme for Object Delivery", IETF RFC 5053, October 2007. https://www.rfc-editor.org/rfc/rfc5053
   - Relevant: the earlier Raptor code. Its introduction says the code "is identical to that described in [MBMS]", and [MBMS] is 3GPP TS 26.346 v6.1.0 (June 2005). So 3GPP MBMS download uses Raptor (RFC 5053), not RaptorQ. Note for the author: the paper's outline bullet says "RaptorQ ... in DVB and 3GPP MBMS"; MBMS should be cited as Raptor.
   - How verified: RFC text read, including the informative reference list.

6. 3GPP TS 26.346, "Multimedia Broadcast/Multicast Service (MBMS); Protocols and codecs". https://www.3gpp.org/dynareport/26346.htm
   - Relevant: MBMS download delivery runs over FLUTE with an application-layer FEC (Raptor) and a post-delivery file repair procedure.
   - How verified: spec page and section listing (itecspec.com mirror of Rel-19 section 7) read; that Raptor is the MBMS code is verified through RFC 5053 [5]. The spec body itself was not read; cite through [5] or read before quoting detail.

7. T. Paila, R. Walsh, M. Luby, V. Roca, R. Lehtonen, "FLUTE - File Delivery over Unidirectional Transport", IETF RFC 6726, November 2012. https://www.rfc-editor.org/rfc/rfc6726
   - Relevant: standard protocol for unidirectional file delivery, built on ALC, "designed for massively scalable multicast distribution"; its File Delivery Table is the analogue of mailcast's directory.
   - How verified: RFC abstract and section 3.2 read.

8. M. Luby, M. Watson, L. Vicisano, "Asynchronous Layered Coding (ALC) Protocol Instantiation", IETF RFC 5775, April 2010. https://www.rfc-editor.org/rfc/rfc5775
   - Relevant: "massively scalable reliable content delivery" to an unlimited number of receivers from one sender; section 1.2: "No feedback packets are required from receivers to the sender."
   - How verified: RFC text read.

9. ATSC, "ATSC Standard: Signaling, Delivery, Synchronization, and Error Protection", A/331:2026-04, 14 April 2026. https://www.atsc.org/wp-content/uploads/2026/04/A331-2026-04-Signaling-Delivery-Sync-FEC.pdf
   - Relevant: ATSC 3.0 broadcast TV specifies RaptorQ (RFC 6330) as its application-layer FEC for ROUTE delivery, so RaptorQ is in deployed one-way broadcast.
   - How verified: PDF downloaded; title page read; text searched: RFC 6330 is normative reference [28] and is used for the RaptorQ FEC Payload ID and repair packets.

10. J. W. Byers, M. Luby, M. Mitzenmacher, "Accessing multiple mirror sites in parallel: using Tornado codes to speed up downloads", Proc. IEEE INFOCOM '99, vol. 1, pp. 275-283, 1999. DOI 10.1109/INFCOM.1999.749293.
    - Relevant: multi-source fountain delivery: several mirrors send packets from the same encoding and a client rebuilds the file from enough packets from any mix of them. Model B is this idea, with the difference that mailcast's senders encode independently held copies and must agree on the bytes first.
    - How verified: Crossref metadata; abstract read on the author's page (eecs.harvard.edu/~michaelm/abstracts/infocom1999.html), which also notes the approach "applies naturally to wireless networks and satellite networks".

11. B. Cohen, "The BitTorrent Protocol Specification", BEP 3, created 10 January 2008. https://www.bittorrent.org/beps/bep_0003.html
    - Relevant: content addressing in practice: a torrent is identified by the SHA-1 of its info dictionary, each piece is checked against a SHA-1 hash, and pieces come from many peers. Mailcast's object ID (truncated SHA-256 of dictionary ID plus object) plays the same role.
    - How verified: BEP text read.

12. R. Ahlswede, N. Cai, S.-Y. R. Li, R. W. Yeung, "Network information flow", IEEE Transactions on Information Theory 46(4), pp. 1204-1216, July 2000. DOI 10.1109/18.850663.
    - Relevant: founding network-coding paper; background only if the section mentions coding at relays (a Model B head end relays pieces it heard).
    - How verified: Crossref and Semantic Scholar metadata only; abstract elided by publisher. Not used in the draft section.

13. Y. Collet, M. Kucherawy (Ed.), "Zstandard Compression and the 'application/zstd' Media Type", IETF RFC 8878, February 2021. https://www.rfc-editor.org/rfc/rfc8878
    - Relevant: zstd format; frames carry an optional Dictionary_ID and "Dictionaries are not included in the compressed content but rather are provided out of band", which is how mailcast's trained dictionary works.
    - How verified: RFC text read (sections 3.1.1.1.3 and 5). Date from the copyright line and RFC number; the page did not show a month.

## B. Amateur broadcast of files and bulletins (closest prior art)

14. H. E. Price (NK6K), J. Ward (G0/K8KA), "PACSAT Broadcast Protocol", ARRL Computer Networking Conference (9th, 1990). TAPR archive: https://files.tapr.org/meetings/CNC_1990/CNC1990-PacSatBroadcast-NK6K-K8KA.pdf ; HTML copy: https://www.ir3ip.net/iw3fqg/doc/pacsat.htm
    - Relevant: files of general interest are cut into AX.25 UI frames and sent repeatedly, round robin by priority; each frame carries file ID and offset so it can be placed independently; "Frames can be transmitted in any order and repeated at any time"; duplicates discarded; receive-only stations accumulate bulletins "just by listening"; stations that miss data may uplink a hole list. No erasure coding: reliability comes from repetition and optional hole-list requests. Used on UO-14 from 1990. This is the closest structural ancestor of mailcast.
    - How verified: text of the TAPR PDF extracted (title, authors, abstract, hole-list format, the "any order" sentence) and the HTML copy read. Venue from the TAPR archive path CNC_1990 (the PDF has no venue line).

15. T. Clark (W3IWI), "BULLPRO: A Simple Bulletin Distribution Protocol", ARRL Computer Networking Conference (9th, 1990), pp. 275-277. https://web.tapr.org/meetings/CNC_1990/CNC1990-Bullpro-W3IWI.pdf
    - Relevant: proposes broadcasting packet BBS bulletins as unacknowledged UI frames to a whole local network, one line per frame, each tagged with the bulletin's BID and a line number, retransmitted several times, with "fills" of missing information as a possible extension. It also says that with several BULLPRO servers a user can accept all of them and sort out duplicates in software, which "requires that all the servers adhere to a common BID standard". This anticipates one-way BBS bulletin broadcast and, in a simple form, multi-server combining keyed on bulletin identity. Local VHF network, no FEC, no BBS injection.
    - How verified: PDF text extracted and read (page numbers 275 and 277 printed on the pages; ARRL bulletin example dated July 1990).

16. J.-P. Roubelat (F6FBB), FBB software documentation, "Unproto message lists" (Appendix 18) and "Forward protocol" (Appendix 9), 1999 (Debian fbbdoc package). https://sources.debian.org/data/main/f/fbbdoc/1999/fmtunpro.htm and https://sources.debian.org/data/main/f/fbbdoc/1999/docfwpro.htm
    - Relevant: (a) FBB forwarding is proposal-based by BID/MID ("FB B ... BID size"), the partner accepts, rejects or defers each message; this is the interface mailcast's receiver uses as partner Q0CAST. (b) FBB could broadcast its message list as UI frames so listening stations build the list without connecting; missing lines are requested by UI frame and message bodies fetched by connecting. One-way list broadcast, not body broadcast.
    - How verified: both pages read in full. Authorship: FBB is F6FBB's software; the pages carry no author line.

17. FLAMP (Amateur Multicast Protocol), program by W1HKJ et al., Debian package description. https://packages.debian.org/bookworm/hamradio/flamp
    - Relevant: amateur connectionless file transfer to many stations at once, used in emergency communications over fldigi modes: each file is cut into checksummed blocks; successive transmissions fill missing blocks; fills are the whole file again or only the missing blocks. Repetition, no fountain code.
    - How verified: Debian package description read. The AMP-2 protocol specification (SourceForge, 2024) was not opened; W1HKJ attribution taken from the pyAMP README, which credits Dave Freese W1HKJ's fldigi. See leads.

18. R. E. Taggart (WB8DQT), "Digital Slow-Scan Television", QST, February 2004 (ARRL). Copy: https://bruxy.regnet.cz/hamradio/SSTV_Library/Magazine_Articles/QST_2004_February_-_Digital_Slow-scan_Television.pdf
    - Relevant: RDFT (redundant digital file transfer, KB9VAK) sends any digital file over HF with "two levels of Reed-Solomon coding", about 92 bytes/s; the ancestor of HamDRM/EasyPal one-to-many file transfer on amateur HF.
    - How verified: PDF text extracted; author, title, "From February 2004 QST (c) ARRL" footer and the Reed-Solomon sentence read. Page numbers not captured.

19. R. (PY4ZBZ), "HamDRM" page, qsl.net. https://www.qsl.net/p/py4zbz/hdsstv/HamDRM.htm
    - Relevant: HamDRM (by HB9TLK, based on the Dream DRM software) splits a file into segments with Reed-Solomon FEC, can send several instances in one transmission, and receivers can send a BSR (bad segment request) for missing segments. Widely used one-to-many file/image transfer on amateur HF, with repetition and requests rather than a rateless code.
    - How verified: page read. Hobbyist web page, not peer reviewed.

20. P. Guelzow (AMSAT-DL), "QO-100 High Speed Multi Media Beacon", AMSAT-DL, 30 May 2022. https://amsat-dl.org/en/qo-100-high-speed-multi-media-beacon/
    - Relevant: one-way amateur data broadcast via the QO-100 geostationary transponder of AMSAT bulletins, DX cluster and skimmer data, using DJ0ABR's modem at 7200 bit/s in 2.7 kHz. Relevant to mailcast's untested QO-100 receiver option. The page does not say how files are protected.
    - How verified: page read.

21. ARRL, "W1AW Operating Schedule". https://www.arrl.org/w1aw-operating-schedule
    - Relevant: long-running one-way HF bulletin broadcast; digital bulletins in 45.45-baud Baudot, PSK31 and MFSK16 on a revolving schedule, including 7.095 MHz. Plain repetition, no coding across transmissions.
    - How verified: page read.

22. ITU-R Recommendation M.540-2, "Operational and technical characteristics for an automated direct-printing telegraph system for promulgation of navigational and meteorological warnings and urgent information to ships" (NAVTEX), 1990. https://www.itu.int/rec/R-REC-M.540-2-199006-I/en
    - Relevant: a scheduled one-way broadcast service with several transmitters: receivers must avoid printing a message already received, deduced from the B1B2B3B4 identifier; a message repeated by another station "should" keep its original preamble. This is multi-transmitter duplicate suppression by identifier, the whole-message ancestor of Model B's combining.
    - How verified: ITU PDF downloaded, text extracted; the relevant numbered paragraphs read.

## C. Fountain-coded HF broadcast (directly undercuts any "first" claim)

23. Rhizomatica, "hermes-broadcast: Hermes software for broadcasting data in HF" (HERMES, High-frequency Emergency and Rural Multimedia Exchange System), lead developer Rafael Diniz (PU2UIT), GitHub repository created 19 June 2024. https://github.com/Rhizomatica/hermes-broadcast
    - Relevant: one-way data broadcast over HF with a "Data carousel mechanism implemented using RaptorQ fountain code" (NanoRQ); "there is no return path, so a receiver tunes in at an arbitrary moment", so configuration goes in every frame. RaptorQ was added on 14 August 2024 ("added raptorq and new framing structure", "raptorQ working" 15 August 2024); a joint RaptorQ TX/RX daemon followed in February 2026. Runs over the Mercury modem (FreeDV DATAC-style OFDM modes). Not tied to BBSes; no multi-sender combining described.
    - How verified: README read; repository cloned and git history inspected (dates and commit messages above).

24. Rhizomatica, Mercury modem, pull request #238 "Broadcast file transfer (RaptorQ) with a Fyne UI panel", by rafael2k, merged 2 September 2026. https://github.com/Rhizomatica/mercury/pull/238
    - Relevant: brings RaptorQ file broadcast, wire compatible with hermes-broadcast, into Mercury's HF modem; notes that broadcast has no negotiation, so sender and receiver must share the mode by hand (contrast with MS110D autobaud).
    - How verified: PR page read.

25. M. Anderson, "This Digital Radio Gets Messages to the World's Remotest Locations", IEEE Spectrum, 21 September 2026. https://spectrum.ieee.org/hermes-shortwave-radio-digital-data
    - Relevant: independent description of HERMES/Mercury deployments (Bangladesh, Brazil, Ecuador), 400 to 600 km links at about 20 W. Describes point-to-point use; does not mention broadcast.
    - How verified: article read.

## D. HF data standards and channel models

26. US Department of Defense, MIL-STD-188-110D, "Interoperability and Performance Standards for Data Modems", 29 December 2017. https://everyspec.com/MIL-STD/MIL-STD-0100-0299/MIL-STD-188-110D_55856/
    - Relevant: the standard mailcast's waveforms come from (Appendix D serial-tone, 3 to 48 kHz).
    - How verified: EverySpec listing read (title, date). The waveform details are cited by the paper from the standard itself (Table 1); not re-read here.

27. W. N. Furman, J. W. Nieto, "Latest on-air testing of U.S. MIL-STD-188-110C Appendix D wideband HF data waveforms", 12th IET International Conference on Ionospheric Radio Systems and Techniques (IRST 2012), p. 58, 2012. DOI 10.1049/cp.2012.0386.
    - Relevant: on-air evaluation of the Appendix D family (in its 110C form).
    - How verified: Crossref metadata only (IET page returned 403). Cite only for the existence of on-air testing, as the title states.

28. NATO, STANAG 5066 Edition 4, "Technical Standards for HF Radio Link Layer and Application Support Protocols", draft for review v1.6, 27 August 2021 (NATO UNCLASSIFIED, releasable to the public; copy hosted by Isode). https://www.isode.com/wp-content/uploads/2024/04/Main.pdf
    - Relevant: the Data Transfer Sublayer provides "a reliable (ARQ) data link service, as well as best-effort (non-ARQ) service for broadcast, multicast and communication to peers in EMCON", supports STANAG 4539 and STANAG 5069 autobaud waveforms (with MIL-STD-188-110D references), and carries ACP 142 (P_MUL, "A Protocol for Reliable Multicast Messaging in Bandwidth Constrained and Delayed Acknowledgement (EMCON) Environments"). Military HF already does one-way/receive-silent delivery, by repetition and delayed acknowledgement rather than fountain codes.
    - How verified: PDF downloaded and text searched; quoted lines read. It is a draft; cite as such or cite Edition 3.

29. ITU-R Recommendation F.1487, "Testing of HF modems with bandwidths of up to about 12 kHz using ionospheric channel simulators", May 2000. https://www.itu.int/rec/R-REC-F.1487/en
    - Relevant: the Watterson-model simulator conditions: mid-latitude quiet 0.5 ms / 0.1 Hz, moderate 1 ms / 0.5 Hz, disturbed 2 ms / 1 Hz, disturbed near vertical incidence 7 ms / 1 Hz; the "poor channel" of the former F.520 is 2 ms / 1 Hz. Useful to place the measured 1.6 ms and about 0.33 Hz.
    - How verified: ITU PDF downloaded; tables read.

30. C. C. Watterson, J. R. Juroshek, W. D. Bensema, "Experimental confirmation of an HF channel model", IEEE Transactions on Communication Technology 18(6), pp. 792-803, December 1970. DOI 10.1109/TCOM.1970.1090438.
    - Relevant: the stationary tapped-delay-line HF model with independent Rayleigh-fading taps, one per mode, valid over roughly a quarter of the reciprocal of the time spread; basis of F.1487 and of the standard's performance tables.
    - How verified: Crossref metadata; abstract read on the COIT historical library page (forohistorico.coit.es).

## E. Amateur HF digital systems

31. W. A. Beech (NJ7P), D. E. Nielsen (N7LEM), J. Taylor (N7OO), "AX.25 Link Access Protocol for Amateur Packet Radio", Version 2.2, TAPR, revision 10 September 2017 (copyright 1997 TAPR). https://wiki.oarc.uk/_media/packet:ax25.2.2.10.pdf
    - Relevant: the link layer; mailcast frames are AX.25 UI frames.
    - How verified: PDF front pages read.

32. N. Carrillo (KK4HEJ), "IL2P Specification", Draft v0.6, 16 March 2024. https://tarpn.net/t/il2p/il2p.html
    - Relevant: IL2P adds FEC and drops bit stuffing relative to AX.25, with the first hardware implementation translating AX.25 KISS to IL2P on air; described at the 2020 TAPR DCC.
    - How verified: TARPN page and spec PDF front pages read.

33. R. Muething (KN6KB), M. Pitts (N8OHU), J. Wiseman (GM8BPQ), "ARDOP (Amateur Radio Digital Open Protocol): A next generation digital protocol for HF and VHF/UHF", ARRL/TAPR Digital Communications Conference, 2015. https://tapr.org/wp-content/uploads/DCC2015-ARDOP-KN6KB-N8OHU-GM8BPQ.pdf
    - Relevant: open ARQ HF protocol with bandwidth negotiation; describes WINMOR's acceptance as "a low-cost Pactor alternative" in Winlink 2000. Supports the paper's description of Winlink transports.
    - How verified: PDF text read (title, authors, abstract, keywords, Winlink sentence).

34. EA5HVK, "VARA HF" (rosmodem). https://rosmodem.wordpress.com/
    - Relevant: "a High Performance HF modem based on OFDM modulation", 2400 Hz, licensed, used with Winlink.
    - How verified: page read. No technical specification is published.

35. D. Rowe et al., Codec 2 "README_data.md" (FreeDV raw data modes DATAC0/1/3...). https://github.com/drowe67/codec2/blob/main/README_data.md
    - Relevant: open OFDM/LDPC data modes for HF; the modem family under Mercury/hermes-broadcast.
    - How verified: README read.

36. J. Taylor (K1JT), B. Walker (W1BW), "WSPRing Around the World", QST, November 2010, pp. 30-32. https://wsjt.sourceforge.io/WSPR_QST_Nov_2010.pdf
    - Relevant: one-way beacon-like transmissions with strong FEC, many receive-only reporters; a precedent for crowd reception reports (mailcast's daily reports).
    - How verified: PDF text read; page numbers from ARRL/search listings.

37. S. Franke (K9AN), B. Somerville (G4WJS), J. Taylor (K1JT), "The FT4 and FT8 Communication Protocols", QEX, July/August 2020, pp. 7-17.
    - Relevant: 77-bit fixed messages with strong FEC; weak-signal, timed, but tiny messages.
    - How verified: QEX July/August 2020 table of contents PDF from arrl.org read (title and authors listed). Page range from search listings, not checked.

## F. Sounding, NVIS, prediction

38. M. Hervas, P. Bergada, R. M. Alsina-Pages, "Ionospheric Narrowband and Wideband HF Soundings for Communications Purposes: A Review", Sensors 20(9), 2486, 2020. DOI 10.3390/s20092486.
    - Relevant: surveys oblique and wideband HF sounders (DAMSON, WHISPER, Antarctica link) measuring delay and Doppler spread with chirps, m-sequences (PN-255/511/1023), Barker and other codes; Zadoff-Chu/CAZAC sequences are not mentioned. Supports saying mailcast's in-band ZC probe borrows the method of PN-sequence oblique sounding with a different sequence family, run on every receiver of a broadcast.
    - How verified: open-access article read on PMC (PMC7273218); Crossref metadata.

39. D. C. Chu, "Polyphase codes with good periodic correlation properties", IEEE Transactions on Information Theory 18(4), pp. 531-532, July 1972. DOI 10.1109/TIT.1972.1054840.
    - How verified: Crossref metadata (title states the property cited).

40. R. L. Frank, S. A. Zadoff, R. C. Heimiller, "Phase shift pulse codes with good periodic correlation properties", IRE/IEEE Transactions on Information Theory 8(6), pp. 381-382, October 1962. DOI 10.1109/TIT.1962.1057786.
    - How verified: Crossref metadata (Crossref lists Frank, Zadoff and Heimiller).

41. B. A. Witvliet, R. M. Alsina-Pages, "Radio communication via Near Vertical Incidence Skywave propagation: an overview", Telecommunication Systems 66(2), pp. 295-309, 2017. DOI 10.1007/s11235-017-0287-2.
    - Relevant: NVIS covers about a 200 km radius without infrastructure, using about 3 to 10 MHz; overview of propagation, antennas, diversity, modulation and coding.
    - How verified: abstract read via Semantic Scholar API; Crossref metadata (open access CC BY).

42. ITU-R Recommendation P.533-14, "Method for the prediction of the performance of HF circuits", August 2019. https://www.itu.int/rec/R-REC-P.533/en
    - Relevant: uses ray-path mirror-reflection heights for hop geometry, the same model the paper uses to read the 1F-2F delay.
    - How verified: ITU PDF downloaded; title and the "mirror-reflection heights" sections found in the text.

## Unverified leads (not cited)

- Othernet (formerly Outernet): one-way satellite datacasting to receive-only devices (L-band, then Ku-band). Only secondary sources found (Wikipedia mirrors, rtl-sdr.com); nothing primary on its FEC or carousel.
- Blockstream Satellite: one-way satellite broadcast of the Bitcoin blockchain. Not searched to a primary source.
- DVB-H IPDC (ETSI TS 102 472) and DSM-CC data carousels (ISO/IEC 13818-6): likely Raptor-coded and carousel-based respectively; not opened.
- STANAG 5069 as bit-identical to MIL-STD-188-110 Appendix D: stated on sigidwiki.com (secondary); STANAG 5066 Ed. 4 draft [28] confirms only that STANAG 5069 is an autobaud waveform it supports.
- FLAMP AMP-2 v3.0 specification (SourceForge fldigi/flamp, Amp-2.V3.0 Specification, 2024): would give the authoritative block and fill format; not opened.
- APRS Protocol Reference 1.0.1 (TAPR/APRS working group, 2000): aprs.org copy did not download; cite only after opening.
- Winlink B2F specification (winlink.org/b2f): behind a Cloudflare check; not read. Winlink's use of PACTOR is supported here only through [33].
- PACSAT companion papers at CNC 1990 (Price and Ward, "PACSAT Protocol Suite: An Overview" and "PACSAT Data Specification Standards") and the later journal paper by Ward and Price, "Protocols for store-and-forward message switching via microsatellites": seen in search results, not opened.
- Harris/L3Harris paper by W. N. Furman, "130-Furman.pdf" (sites.bc.edu) on 110C Appendix D: not opened.
- NinoTNC and LinBPQ documentation: not searched to a primary page; cite the project sites if needed.
- Wideband HF patent "Enhanced Wideband High Frequency (HF) data transmission with adaptive interference avoidance" (USPTO 12615656) proposes a fountain encoder for HF broadcast traffic: patent, claims not checked.
- Steinwurf ApS "OTAcast" over-the-air file delivery (company blog, 2020): seen in results, not opened.
