pdn-mailcast reception survey on CT 150 (mailcast-rx)
=====================================================

What it is
----------
A week-long look at who hears GB7RDG's hourly 40 m mailcast slots (7.0538 MHz,
USB dial 7.052), when, and from how far. Set up 2026-10-06. It ends by itself
at 18:30 UTC on 2026-10-13, after the last slot that day.

Eight pdn-mailcast receivers run here, one per UberSDR. None has a BBS, so they
only listen. Each listens from 2 minutes before a daylight slot to 12 after
(webSdrSlotsPerDay 12), which keeps each SDR well inside its operator's limits.

  SDR       km from IO91lk  bearing  unit                                   page
  G4WNC          83          218     pdn-mailcast-receiver@g4wnc            127.0.0.1:8133
  G4EYR          89          137     pdn-mailcast-receiver@g4eyr            127.0.0.1:8137
  STUEY3D        97            8     pdn-mailcast-receiver@stuey3d          127.0.0.1:8132
  WESSEX        135          236     pdn-mailcast-receiver@wessex           127.0.0.1:8131
  M9TWM-1       221          351     pdn-mailcast-receiver@m9twm-1          127.0.0.1:8134
  EI4HQ         502          278     pdn-mailcast-receiver@ei4hq            127.0.0.1:8135
  M9PSY         534          344     pdn-mailcast-receiver (the original)   LAN :8130
  GI3JMC        544          320     pdn-mailcast-receiver@gi3jmc           127.0.0.1:8136

The original receiver (/etc/pdn-mailcast/receiver.json) is not part of the
survey's setup; the survey only reads its status. It was on M9PSY-1 (Dalgety
Bay, 40 m loop, 533 km) until 11:08 UTC on 2026-10-06, then moved to M9PSY
(same site, end-fed long wire). The collector names each row after the SDR the
receiver is on at the time, so rows before then say M9PSY-1.

The survey receivers' configs are in /etc/pdn-mailcast/survey/NAME.json and
their state in /var/lib/pdn-mailcast-NAME. The unit template is
/etc/systemd/system/pdn-mailcast-receiver@.service; they are grouped under
mailcast-survey.target.

What it writes
--------------
/usr/local/bin/mailcast-survey runs from mailcast-survey.timer at 5 and 15
minutes past every hour (UTC). At :05 it notes whether each receiver is
listening. At :15 it adds one row per receiver for the slot just gone to

  slots.csv          slot_utc, sdr, host, km, bearing_deg, listening,
                     tone_snr_db_3khz, tone_offset_hz, frames_heard (in that
                     slot only), frames_delta_total (change in the receiver's
                     running total, a cross-check), bulletins_completed (newly
                     complete in that slot), bulletins_complete_total,
                     receiver_version, waveform (WN4 or WN3, from the head
                     end's turn rule, see below), note

and rewrites daily-YYYY-MM-DD.txt, a table of hour against SDR. Night hours
(no slot) get no rows. state.json is the collector's own memory between runs.

The waveform column is worked out, not observed: receiver 0.4.2 does not say
which MS110D waveform it decoded. Head end v0.5.0 takes turns, slot n of day d
on mode (n + d) mod 2, which matches the head end's own record of 11:00 (WN3)
and 12:00 (WN4) on 2026-10-06. If GB7RDG's list of waveforms changes, the column goes wrong;
MODES in /usr/local/bin/mailcast-survey is the place to fix it.

CT 150 was raised from 2 to 4 cores and 1 to 2 GB for the survey. With all
eight listening it used about 0.6 of one core on average, peaking at 1.2 cores
for a few seconds.

How to stop it early
--------------------
  systemctl disable --now mailcast-survey.timer mailcast-survey-end.timer mailcast-survey.target
  systemctl stop 'pdn-mailcast-receiver@*'
  systemctl disable pdn-mailcast-receiver@{wessex,stuey3d,g4wnc,m9twm-1,ei4hq,gi3jmc,g4eyr}

That leaves the original receiver (now on M9PSY) running. The data stays here.

How to keep it going past 2026-10-13
------------------------------------
  systemctl disable --now mailcast-survey-end.timer
