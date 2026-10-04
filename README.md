# pdn-mailcast

A daily one-way HF broadcast of packet BBS bulletins, and a receiver that drops each bulletin it hears into your own BBS.

GB7RDG sends the day's bulletins once a day on 40 m, around midday UK time. The receiver listens through a sound card on your radio, or through a public web SDR, and collects pieces of the broadcast until each bulletin is complete. It then hands the bulletin to your LinBPQ or FBB mail as an ordinary forwarding partner. Your BBS already rejects bulletins it has seen, so this is just one more route for mail to reach you.

This is an experiment at an early stage: the libraries below exist, the head end and the receiver do not yet. The plan is in [docs/design.md](docs/design.md).

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
- `src/Mailcast.Core`: the on-air frame format, the bulletin model, zstd compression with the trained dictionary, the directory, the head end's daily schedule, and the receiver's symbol store.
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
