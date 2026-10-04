# pdn-mailcast

A daily one-way HF broadcast of packet BBS bulletins, and a receiver that drops each bulletin it hears into your own BBS.

GB7RDG sends the day's bulletins once a day on 40 m, around midday UK time. The receiver listens through a sound card on your radio, or through a public web SDR, and collects pieces of the broadcast until each bulletin is complete. It then hands the bulletin to your LinBPQ or FBB mail as an ordinary forwarding partner. Your BBS already rejects bulletins it has seen, so this is just one more route for mail to reach you.

This is an experiment and nothing is built yet. The plan is in [docs/design.md](docs/design.md).

## Licence

AGPL-3.0. See [LICENSE](LICENSE).
