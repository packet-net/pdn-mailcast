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
  "timeUtc": "00:30",
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

Pick a time no other mailcast station on your frequency uses. GB7RDG sends on the hour, so on 40 m start yours at half past: `"timeUtc": "00:30"` with `"everyMinutes": 60` gives a slot at 00:30, 01:30 and so on, and only those in daylight run. A slot lasts at most 10 minutes. The journal's `schedule:` line at start-up says when your slots are.

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

Receivers listen to GB7RDG out of the box. A signed list of transmitters, sent over the air, will let them find and follow yours too; that is [#178](https://github.com/packet-net/pdn-mailcast/issues/178), and it is not built yet. Until then a listener who wants yours sets their receiver to your frequency (`dialKHz`), your callsign (`sources`), your slots (`slotUtc`), your locator (`daylight.locator`, since receivers work out GB7RDG's daylight at IO91lk unless told otherwise) and your public key (`trustedKeys`); see [receiver.md](receiver.md).
