# Space weather and ionosonde data, 1 October to 8 November 2026

Downloaded for the space weather analysis of the Mailcast paper (paper/notes/space-weather.md, paper/figures/space_weather.py). Every file is exactly as the server sent it, apart from the file name; the one big file is gzipped (`gunzip -c` gives the original bytes). All fetched on 7 November 2026 between 22:56 and 23:01 UTC, with curl from the LAN at M0LTE.

## Files

| File | Source and URL | What it holds |
|---|---|---|
| `gfz-Kp_ap_Ap_SN_F107_since_1932.txt.gz` | GFZ Potsdam, https://kp.gfz.de/app/files/Kp_ap_Ap_SN_F107_since_1932.txt (the old address https://kp.gfz-potsdam.de/app/files/... redirects there) | Daily rows from 1932: eight 3-hourly Kp and ap, daily Ap, sunspot number SN, F10.7 observed and adjusted, and a definitive flag. Ends on 6 November 2026; the October and November rows are flagged preliminary (D=0). |
| `gfz-Kp_ap_Ap_SN_F107_nowcast.txt` | GFZ Potsdam, https://kp.gfz.de/app/files/Kp_ap_Ap_SN_F107_nowcast.txt | The same columns for the last 30 days (9 October to 7 November). Used only for 7 November. Its preliminary SN differs from the big file's by a few units on some days (6 Nov: 112 against 106). |
| `gfz-api-Kp-2026-10-01-to-11-08.json` | GFZ Kp web service, https://kp.gfz.de/app/json/?start=2026-10-01T00:00:00Z&end=2026-11-08T23:59:59Z&index=Kp&status=all | 3-hourly Kp with times and status, as a cross-check of the text files. |
| `noaa-swpc-daily-solar-indices.txt` | NOAA SWPC, https://services.swpc.noaa.gov/text/daily-solar-indices.txt | Last 30 days (8 Oct to 6 Nov): F10.7, SESC sunspot number, sunspot area, daily counts of C, M and X flares. |
| `noaa-swpc-daily-geomagnetic-indices.txt` | NOAA SWPC, https://services.swpc.noaa.gov/text/daily-geomagnetic-indices.txt | Last 30 days: Fredericksburg, College and planetary A and 3-hourly K. |
| `noaa-swpc-edited_events.json` | NOAA SWPC, https://services.swpc.noaa.gov/json/edited_events.json | The edited event list for the last 30 days (7 Oct 22:57 to 7 Nov 18:21 UTC): GOES X-ray events (XRA) with class, begin, peak and end, optical flares, radio bursts. The M and X flares are taken from here. |
| `noaa-swpc-solar-geophysical-event-reports.txt` | NOAA SWPC, https://services.swpc.noaa.gov/text/solar-geophysical-event-reports.txt | The latest day's event report only; kept for completeness, not used. |
| `propquest/propquest-STATION-DAY1-DAY2.json` | PROPquest, https://propquest.org/JSON-grab-ionosphere?DATE1=DAY1&DATE2=DAY2&OBSERVATORY=STATION, for STATION = DB049 (Dourbes) and FF051 (Fairford), DAY1 from 1 October every second day to 6 November, DAY2 = DAY1 + 1 | Two days per file (the answer's `YDAY` arrays are DAY1, `TODAY` arrays DAY2), on a 5-minute grid: foF2, MUF(3000)D, foEs, hF2 and the observatory each value came from. hF2 and the MUF(D) at 100, 500 and 1000 km are always null. Dourbes sends a sounding every 5 minutes up to midday on 1 November, then almost nothing (6 to 13 values a day); Fairford's values are hourly. Chilton (RL052) is not in PROPquest at all (a test request returned nulls only). |

The head end's own ionosonde readings before each slot are in its journal (data/headend-gb7rdg), not here.

## What could not be fetched

- **GIRO DIDBase** (lgdc.uml.edu), which has scaled hmF2 and h'F for Chilton, Dourbes and Fairford: every request answered `504 Gateway Time-out` after about 15 s, at 22:58, 23:03 and 23:15 UTC, whatever the period (one day or one hour) or the endpoint (`/common/DIDBGetValues` and `/fastchar/getbest`, the head end's own). So there is no measured hmF2 here; the analysis estimates it from Dourbes' M(3000)F2 instead. Worth fetching later with the same query, for example https://lgdc.uml.edu/common/DIDBGetValues?ursiCode=RL052&charName=foF2,hmF2,hF2,MUFD,foE&DMUF=3000&fromDate=2026/10/01+00:00:00&toDate=2026/11/08+00:00:00 (and DB049, FF051).
- **NOAA SWPC FTP archive over HTTPS** (https://ftp.swpc.noaa.gov/pub/indices/old_indices/2026Q4_DSD.txt and `..._DGD.txt`, and the daily `pub/indices/events/` lists): connection timed out after 90 s from here. The 30-day text products above cover 8 October onwards instead; flares before 7 October 22:57 UTC are therefore not covered (no Mailcast slot to compare before 6 October anyway, and 6 to 7 October are missing from the flare list).
- **NCEI/NGDC GOES XRS flare summaries** (data.ngdc.noaa.gov): connection timed out.
