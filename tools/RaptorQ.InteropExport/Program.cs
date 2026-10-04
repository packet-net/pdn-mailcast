// Writes symbols made by Mailcast.RaptorQ for the cases in the crate's vector file, so that
// tools/raptorq-vectors can check them with the raptorq crate:
//
//   dotnet run --project tools/RaptorQ.InteropExport -- <crate-vectors.json> <ours.json>
//   cargo run --release --manifest-path tools/raptorq-vectors/Cargo.toml -- check <ours.json>
//
// For each case it writes our symbols for the same ESIs the crate listed, and a decode set of
// our own: a random part of the source symbols topped up with random repair symbols, K + 2 per
// source block in all.

using System.Text.Json;
using System.Text.Json.Nodes;
using Mailcast.RaptorQ;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: RaptorQ.InteropExport <crate-vectors.json> <ours.json>");
    return 2;
}

var input = JsonNode.Parse(File.ReadAllText(args[0]))!;
var cases = new JsonArray();
foreach (var c in input["cases"]!.AsArray())
{
    var oti = ObjectTransmissionInformation.Read(Convert.FromHexString((string)c!["oti"]!));
    uint seed = (uint)c["seed"]!;
    var encoder = new ObjectEncoder(Xorshift(seed, oti.TransferLength), oti);

    var symbols = new JsonArray();
    foreach (var s in c["symbols"]!.AsArray())
    {
        var parts = ((string)s!).Split(':');
        var id = new PayloadId(int.Parse(parts[0]), uint.Parse(parts[1]));
        symbols.Add(Format(id, encoder.Encode(id)));
    }

    var rng = new Random((int)seed);
    var decodable = new JsonArray();
    for (int sbn = 0; sbn < oti.SourceBlocks; sbn++)
    {
        int k = oti.SourceBlockSymbols(sbn);
        var esis = new SortedSet<uint>();
        int keep = rng.Next(k + 1);
        foreach (int esi in Enumerable.Range(0, k).OrderBy(_ => rng.Next()).Take(keep))
        {
            esis.Add((uint)esi);
        }
        while (esis.Count < k + 2)
        {
            esis.Add((uint)(k + rng.Next(20000)));
        }
        foreach (uint esi in esis)
        {
            var id = new PayloadId(sbn, esi);
            decodable.Add(Format(id, encoder.Encode(id)));
        }
    }

    cases.Add(new JsonObject
    {
        ["name"] = (string)c["name"]!,
        ["seed"] = seed,
        ["oti"] = Convert.ToHexStringLower(oti.ToBytes()),
        ["symbols"] = symbols,
        ["decodable"] = decodable,
    });
}

var doc = new JsonObject { ["generator"] = "tools/RaptorQ.InteropExport (Mailcast.RaptorQ)", ["cases"] = cases };
File.WriteAllText(args[1], doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"wrote {cases.Count} cases to {args[1]}");
return 0;

static string Format(PayloadId id, byte[] data) => $"{id.SourceBlockNumber}:{id.EncodingSymbolId}:{Convert.ToHexStringLower(data)}";

static byte[] Xorshift(uint seed, long length)
{
    var data = new byte[length];
    uint x = seed;
    for (long i = 0; i < length; i++)
    {
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        data[i] = (byte)x;
    }
    return data;
}
