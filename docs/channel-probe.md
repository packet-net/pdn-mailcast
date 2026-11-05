# How a mailcast receiver works out where the signal bounced

Every hourly mailcast slot from M0LTE (GB7RDG's bulletin service on 40 m) starts with a short test transmission: a plain tone, then a few seconds of a special "channel probe" signal. A receiver that hears it can tell how many ways the signal reached it, how long each way took, and from that how high the ionosphere was and roughly where on the ground the signal came down between hops.

This note covers what that probe is, how the receiver measures it, and how it gets from a delay measurement to "Reflection about 290 km up, bounced near Aberystwyth".

The code is in [packet-net/pdn-soundmodem](https://github.com/packet-net/pdn-soundmodem) (`ProbeSignal.cs`, the transmitter) and [packet-net/pdn-mailcast](https://github.com/packet-net/pdn-mailcast) (`ProbeChannel.cs`, `PathGeometry.cs`, `GroundBounceGazetteer.cs`, the receiver).

## The start of every slot

```
 key up
   |
   |<------- 10 s ------->|<- 1.5 s ->|<------------ 6.5 s ------------>|
   |  calibration tone    |  silence  |  zc255 channel probe            |  CW ident, then data bursts
   |  1800 Hz             |           |  centred on the tone, 420-3180 Hz
```

- **The tone** (10 s at 1800 Hz) lets each receiver find the signal, measure its exact audio frequency (so a receiver slightly off frequency still lines up) and see when it ends.
- **The gap** (1.5 s) gives the receiver a clean marker: the probe is expected 1.5 s after the tone stops.
- **The probe** (6.5 s) is the measuring signal. It goes out on the tone's own audio frequency, in the same keyup, so it lands exactly where the tone did at every receiver.

Together they add about 8 s to each slot. There is no setting to turn the probe off; a station too old to send it just sends the tone.

## The probe signal

The probe is a Zadoff-Chu sequence, a kind of signal made for measuring radio channels. Its useful property: correlate it against a cyclically shifted copy of itself and you get exactly zero, at every shift except zero. So when it arrives by several paths at once, the paths separate cleanly instead of smearing into each other.

| Parameter | Value |
|---|---|
| Id | `zc255-2400-rrc015-v1` (short name `zc255`) |
| Sequence | Zadoff-Chu, length 255, root 1: `c[n] = exp(-j pi n (n+1) / 255)` |
| Chip rate | 2400 chips/s (one chip = 0.417 ms) |
| Pulse shaping | Root-raised-cosine, roll-off 0.15, 32 chips either side |
| Bandwidth | 2760 Hz: 420 to 3180 Hz at an 1800 Hz centre |
| One period | 255 chips = 106.25 ms |
| Periods sent | 61, repeated back to back; the first acts as a cyclic prefix |
| Ramps | 10 ms raised-cosine rise and fall |
| Total length | 6.50125 s |
| Level | Peaks at the same level as the tone, nearly constant envelope (about 3 dB peak to average, like a sine wave) |

Why those numbers:

- **106.25 ms period:** any echo up to 106 ms late reads out unambiguously. Real HF paths spread over a few ms, so there is plenty of room.
- **0.417 ms chip:** paths about a chip apart are resolved; with interpolation the delay of each path comes out to a few microseconds.
- **61 periods:** averaging 58 of them adds about 18 dB on top of the 24 dB each 255-chip period already gives, which is why the probe is still measurable at about -15 dB signal to noise in 3 kHz, far below anything the data modes can decode.
- **Repeating it:** one estimate every 106.25 ms means the receiver also sees how each path's phase drifts over 6 s, which gives its Doppler shift and spread (up to 4.7 Hz either way, resolving spreads of about 0.1 Hz).

The id is a contract. Change any parameter and it is a different probe with a new id. The receiver builds its reference by calling the very same renderer the transmitter uses, so the two cannot drift apart.

## What the receiver does with it

1. **Find it.** After the slot, the receiver takes the audio from just before the tone's end to well after the probe, mixes it down to complex baseband at 9600 Hz on the tone's measured frequency, and cuts it into windows one period long (1020 samples). It searches from 1.5 s earlier to 3 s later than where the tone's end puts it, since a tone detector's idea of "the end" is only good to about a second.
2. **Correlate each window.** Narrowband interference is notched out, then each window is circularly correlated with one period of the probe. Inside the probe, each window gives the channel's impulse response directly: one peak per path, at that path's delay.
3. **Decide it was really there.** The run of 58 windows where the result stands highest is the probe. It counts as found only if the averaged profile peaks at least 6 dB over its floor, and stands clear in at least half the windows. (Noise alone reaches under 3 dB; a probe at -15 dB reaches about 12 dB.) A fade, however deep, is the channel and stays in; a stretch where the probe was simply not there (stopped early, lost audio) is left out.
4. **Separate the modes.** Paths less than 0.4 ms apart count as one mode. Anything weaker than -25 dB against the strongest is ignored (-6 dB within 0.85 ms of it, where measurement shoulders appear).
5. **Clean up Doppler.** A Zadoff-Chu probe ties delay and frequency together: a path heard 1 Hz off frequency appears 24 us early, with shoulders. So each mode found is measured again, mixed on its own Doppler frequency, and the small leftover is corrected. A "mode" not found again on its own frequency was another mode's shoulder and is dropped.

What comes out is a short list of modes, each with a delay after the strongest one, a relative power, a Doppler shift and a Doppler spread.

## From delays to a height

The receiver has no absolute timing: it does not know exactly when the probe left Reading. It only knows the delays between modes. That turns out to be enough.

It uses the standard **mirror model** over a round Earth: each hop goes in a straight line up to a mirror at the layer's virtual height and straight back down. An n-hop path splits the ground distance into n equal hops, and its delay is its total length over the speed of light:

```
half-angle  a = (ground distance / Earth radius) / (2 n)
one leg     L = sqrt(R^2 + (R+h)^2 - 2 R (R+h) cos a)     R = 6371 km
delay(n, h) = 2 n L / c
```

The gap between the one-hop (1F) and two-hop (2F) modes grows with the height and hardly depends on the distance for nearby stations:

| Ground distance | 1F-to-2F gap at 200 km | 250 km | 290 km | 350 km | 450 km |
|---|---|---|---|---|---|
| 100 km | 1.31 ms | 1.65 ms | 1.92 ms | 2.32 ms | 2.99 ms |
| 300 km | 1.18 ms | 1.53 ms | 1.81 ms | 2.23 ms | 2.92 ms |
| 500 km | 1.01 ms | 1.36 ms | 1.65 ms | 2.08 ms | 2.79 ms |
| 800 km | 0.78 ms | 1.11 ms | 1.39 ms | 1.82 ms | 2.53 ms |

So a receiver that sees "2 hops, 1.9 ms after 1 hop" within about 200 km of Reading is looking at a layer about 290 km up. The height is found by bisection on that formula for the measured gap. It moves about 7 us per km of height, so the measurement itself is far finer than the model is.

Without the receiver's position, the receiver assumes 150 km, which changes the height little at those distances.

### Naming the hops honestly

The first mode is assumed to be one hop (1F). A later mode is only called 2F if its delay fits a real F layer, somewhere from 200 to 450 km up, at this distance. The first one that fits pins the height; every later mode (3F, 4F) must then match that one height to within 0.3 ms, not be refitted at some other height. Anything that fits nothing is left unnamed: it is a measurement artefact, not a path. Anything arriving before the strongest path is ignored.

The E layer (1E, taken as 110 km) is only considered beyond 300 km, since 40 m can only come off the E layer at a shallow enough angle. Both readings (first mode 1F, or first mode 1E) are tried and the one that explains more of the later modes wins, a tie going to no E.

A height is only reported from a confirmed 1F and 2F pair. It is never guessed from a single mode.

## From a height to a place on the map

Once the modes have names, the ground bounces follow from the same mirror model: an n-hop path touches the ground at 1/n, 2/n, ... of the way along the great circle from the transmitter to the receiver. A 2F path bounces at the midpoint, a 3F path at the thirds.

Each point is looked up in a small gazetteer built into the receiver, so no internet is needed:

- On land, the nearest town (GeoNames, population over 15000, or over 5000 in Britain and Ireland), but only if it is within 30 km: "near Aberystwyth".
- At sea, the IHO sea area, plus the shipping forecast area if the point falls in one: "Celtic Sea (Lundy)". Where the two names say the same thing, it is said once: "Irish Sea".

These are estimates, good to a few tens of km, which is why the labels only ever say "near". A one-hop path has no ground bounce, so it shows nothing new.

## How well it works in practice

- **Sensitivity:** the probe is measured down to about -15 dB signal to noise in 3 kHz. The data bursts can also be used as a known signal once decoded, but only down to about +1 dB, so on weak paths the probe is often the only measurement there is.
- **When both exist,** the receiver shows whichever sees further below the strongest path, with the other beside it.
- **Cost:** about 2 s of one core on a Raspberry Pi 4 per slot, run after the slot on its own thread, never alongside decoding.
- **Output:** the status page's Channel tile says it in one line, for example *"2 paths: 1 hop, and 2 hops 1.9 ms later, 17 dB weaker. Reflection about 290 km up. Doppler spread 0.2 Hz: steady (suits any speed)."*, with the delay profile, a side-view picture of the hops, a 24-hour strip, and a globe view of the day's paths.
- **Limits of the model:** a real wave bends gradually through a layer tens of km thick and turns round lower than the virtual height. But its delay is what a mirror at the virtual height would give, and that is what is measured; an ionosonde's h'F is a virtual height too, so the two can be compared directly.
- **Equal hops** assume the layer is the same height all along the path. On a long path where the ionosphere tilts, the bounce point can be off by more than the label's "near" suggests.

## Using the probe yourself

Anyone can build the reference from the parameters above without reading our code: generate the 255 Zadoff-Chu chips, shape them with the RRC pulse at 2400 chips/s, normalise to a peak of 1, ramp the ends, and put it on the audio carrier. Then drop the first period, correlate the rest one period at a time, and each window is the channel's impulse response.
