# Run your own head end

GB7RDG was the first mailcast transmitter, but any station can run one. This page gets you from nothing to your first slot. [headend.md](headend.md) has the detail on every setting.

## What you need

- **A BBS** that forwards bulletins: LinBPQ or pdn-bbs. The head end logs in as a forwarding partner and takes the bulletins queued for it. [GB7RDG's LinBPQ](headend.md#gb7rdgs-linbpq) shows the LinBPQ setup; it is the same for yours.
- **A radio with pdn-soundmodem**, with an API key and one more modem entry for the bulletins, on its own sub-channel and KISS port. [GB7RDG's pdn-soundmodem](headend.md#gb7rdgs-pdn-soundmodem) shows the entry; put your own frequency in `rfFrequency` and your own callsign in `identify`.
- **A Linux machine** for the head end, usually the one pdn-soundmodem runs on. Install it from packet-net's apt repository (https://packet-net.github.io/apt): `sudo apt install pdn-mailcast-headend`.

## The config

The package puts an example in `/etc/pdn-mailcast-headend/headend.json`. Fill in what names your station; the head end will not start until you do, and tells you what is missing:

```json
"callsign": "G0ABC",
"slot": {
  "minute": 30,
  "everyMinutes": 60,
  "daylight": { "locator": "IO92ab", "afterSunriseMinutes": 120, "beforeSunsetMinutes": 30 }
},
"station": { "apiKey": "your pdn-soundmodem api.key", "kissPort": 8112, "subChannel": 4 },
"intake": {
  "fbb": { "host": "127.0.0.1", "port": 8011, "partnerCallsign": "Q0HEAD", "user": "Q0HEAD", "password": "pick-one" }
}
```

- `callsign`: every frame is sent from it.
- `locator`: where your station is, so the head end can work out your sunrise and sunset. Leave `daylight` out to send in every slot, day and night.
- `intake.fbb`: the login your BBS knows the head end by. Q0HEAD is a Q callsign, so it cannot clash with a real station.

Then `sudo pdn-mailcast-headend --check-config` and `sudo systemctl start pdn-mailcast-headend`. `journalctl -u pdn-mailcast-headend` shows what it is doing.

## The slot start

Pick a time no other mailcast station on your frequency uses. GB7RDG sends on the hour, so on 40 m start yours at half past: `"minute": 30` with `"everyMinutes": 60` gives a slot at 00:30, 01:30 and so on, and only those in daylight run. A slot lasts at most 10 minutes. The journal's `schedule:` line at start-up says when your slots are.

## The signing key

Sign your directories, so listeners can tell your bulletins from a stray or forged source. Make the key once:

```
sudo sh -c 'umask 077; openssl genpkey -algorithm ed25519 -out /etc/pdn-mailcast-headend/signing-key.pem'
sudo chown root:pdn-mailcast-headend /etc/pdn-mailcast-headend/signing-key.pem
sudo chmod 0640 /etc/pdn-mailcast-headend/signing-key.pem
sudo systemctl restart pdn-mailcast-headend
```

The journal's `signing:` line gives your public key. Keep the private key safe and backed up: a new key means telling every listener again.

## Getting heard

Receivers listen to GB7RDG out of the box. To be heard by them, ask an existing station such as GB7RDG to add you to its `advertise` list: its directories then tell every receiver when and where your slots are, and receivers listen there unless the listener switches you off. The first time a receiver hears your signed directory it learns your key and delivers your bulletins from then on; it refuses anyone else sending under your callsign with another key. Receivers hear a station on another HF frequency if they can retune for it, or if it falls in their passband; a QO-100 station is followed by receivers that have set `lnbHz` for a dish and LNB (see [receiver.md](receiver.md)), and shown as not followed by the rest. Say where you are on the air and where you are with `frequencyHz`, `sideband` and `locator` (see [headend.md](headend.md)), so receivers know without being told. `frequencyHz` is the frequency listeners receive on: for QO-100 the narrowband downlink, 10489.5 to 10490.0 MHz (such as `10489750000`), never the 2.4 GHz uplink or the IF of the radio driving a transverter. A listener can also add you by hand: your key in `trustedKeys` (and `daylight.locator` set to yours, since receivers work out daylight at IO91lk unless told otherwise); see [receiver.md](receiver.md).

Every bulletin is sent in the shared canonical form with its envelope, in 240-octet pieces, so another head end's copy of the same bulletin is made of the same pieces. Nothing in the config sets this; an old `schedule.symbolSize` is ignored.
