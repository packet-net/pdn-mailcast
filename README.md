# pdn-mailcast

An hourly one-way HF transmission of packet BBS bulletins, and a receiver that drops each bulletin it hears into your own BBS.

GB7RDG sends its bulletins on 40 m every hour on the hour, each one several times over a day, in a slot of a few minutes. The receiver listens through a sound card on your radio, or through a public web SDR, and collects pieces of the transmission until each bulletin is complete. It then hands the bulletin to your LinBPQ or FBB mail as an ordinary forwarding partner. Your BBS already rejects bulletins it has seen, so this is just one more route for mail to reach you.

This is an experiment at an early stage: the libraries, the head end and the receiver exist, and GB7RDG has been on the air with them since 2026-10-05. The plan is in [docs/design.md](docs/design.md). To run a receiver, see [src/Mailcast.Receiver/README.md](src/Mailcast.Receiver/README.md).

## Licence

AGPL-3.0. See [LICENSE](LICENSE).

## Building

You need the .NET 10 SDK.

```
dotnet build
dotnet test
```

The code so far:

- `src/Mailcast.RaptorQ`: RaptorQ (RFC 6330), with no dependency on the rest of the repo.
- `src/Mailcast.Core`: the on-air frame format, the bulletin model, zstd compression with the trained dictionary, the directory, the head end's store and slot schedule, and the receiver's symbol store.
- `src/Mailcast.Fbb`: FBB compressed (B1F) forwarding, both the calling and the answering side, copied from pdn-bbs with its tests. The receiver uses it to hand bulletins to your BBS, and the head end to take them from GB7RDG's.
- `src/Mailcast.HeadEnd`: the head end, `pdn-mailcast-headend`: takes bulletins from GB7RDG's BBS as a forwarding partner and sends them every hour through the station's pdn-soundmodem, or renders a slot to a WAV file offline. See [docs/headend.md](docs/headend.md); `scripts/build-headend-deb.sh linux-x64 VERSION` builds its .deb.
- `src/Mailcast.Receiver`: the receiver, with pdn-soundmodem's MS110D modem embedded from NuGet.
- `tools/Mailcast.DictionaryTool`: imports bulletins from a copy of a LinBPQ mail store, trains a dictionary, and compares compressed sizes.
- `tools/raptorq-vectors` and `tools/RaptorQ.InteropExport`: check our RaptorQ against the Rust `raptorq` crate, both ways. CI runs them; to run them by hand you also need cargo:

```
cargo run --release --manifest-path tools/raptorq-vectors/Cargo.toml -- generate /tmp/crate.json
dotnet run --project tools/RaptorQ.InteropExport -- tests/Mailcast.RaptorQ.Tests/Vectors/raptorq-crate-2.0.1.json /tmp/ours.json
cargo run --release --manifest-path tools/raptorq-vectors/Cargo.toml -- check /tmp/ours.json
```

To retrain the dictionary from a copy of a LinBPQ store (`DIRMES.SYS` and `Mail/`):

```
dotnet run --project tools/Mailcast.DictionaryTool -- import-bpq <copy-of-bpq-dir> <bulletin-dir>
dotnet run --project tools/Mailcast.DictionaryTool -- train <bulletin-dir> src/Mailcast.Core/Dictionaries/gb7rdg-1.zdict --size 65536 --no-7plus
dotnet run --project tools/Mailcast.DictionaryTool -- evaluate <bulletin-dir> --dictionary src/Mailcast.Core/Dictionaries/gb7rdg-1.zdict
```

A retrained dictionary needs a new dictionary ID, since receivers decompress by ID.

## Releasing

Push a version tag to cut a release:

```
git tag v0.2.0 <full-commit-sha>
git push origin v0.2.0
```

Or run the `release` workflow by hand with a `version` (without the `v`) and `publish` ticked; with `publish` left off it is a dry run that builds everything and keeps it as workflow artifacts. A version with a hyphen (`0.2.0-rc1`) becomes a prerelease, which the apt repo does not pick up.

Either way the tests run first, including the LinBPQ ones in docker, and nothing is built if they fail. A release then has `pdn-mailcast-receiver` and `pdn-mailcast-headend` as `.deb` packages for amd64, arm64 and armhf, the `M0LTE.RaptorQ` NuGet package, a `SHA256SUMS` file and notes made from the merged PRs since the last tag. Finally it asks the packet-net apt repo to rebuild, and starts `nuget.yml`, which pushes the release's `.nupkg` to nuget.org through NuGet trusted publishing (no API key). To push an existing release's package by hand: `gh workflow run nuget.yml -f tag=v0.1.0`. Pull requests that change the packaging run the same build with publishing off.
