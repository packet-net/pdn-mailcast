using System.Text.Json.Nodes;
using Packet.Mailcast;

namespace Mailcast.ModelBVectors;

/// <summary>
/// One input of the Model B vector file (issue #142): a station's copy of a bulletin exactly as its BBS
/// held it. Everything else in the file is worked out from these.
/// </summary>
public sealed record VectorInput(string Name, string Description, char Type, string From, string To, string At, string Bid, byte[] Title, byte[] MessageText)
{
    /// <summary>The title as the characters the library takes (one char per octet).</summary>
    public string TitleText => Bulletin.TextEncoding.GetString(Title);

    /// <summary>Reads an input from its JSON form.</summary>
    public static VectorInput FromJson(JsonNode node) => new(
        (string)node["name"]!,
        (string)node["description"]!,
        ((string)node["input"]!["type"]!)[0],
        (string)node["input"]!["from"]!,
        (string)node["input"]!["to"]!,
        (string)node["input"]!["at"]!,
        (string)node["input"]!["bid"]!,
        Convert.FromHexString((string)node["input"]!["titleHex"]!),
        Convert.FromHexString((string)node["input"]!["messageTextHex"]!));

    /// <summary>Writes the input's JSON form (the "input" half of a case).</summary>
    public JsonObject ToJson() => new()
    {
        ["type"] = Type.ToString(),
        ["from"] = From,
        ["to"] = To,
        ["at"] = At,
        ["bid"] = Bid,
        ["titleHex"] = Hex(Title),
        ["messageTextHex"] = Hex(MessageText),
    };

    private static string Hex(byte[] data) => Convert.ToHexStringLower(data);
}

/// <summary>
/// Builds, from the inputs alone, every byte of the Model B format that two head ends have to agree on.
/// The vector file (tests/Packet.Mailcast.Tests/Vectors/model-b-v1.json) is the output of this class, and the
/// test rebuilds it and compares. Both the generator and the test use this one file, so the file can only
/// ever say what the library does; the test fails when what the library does changes.
/// </summary>
public static class ModelBVectors
{
    /// <summary>The piece size every object is cut at.</summary>
    public const int PieceSize = MailcastFrame.FixedSymbolSize;

    /// <summary>The encoding symbol IDs listed besides K-1, K and K+1: 0, 1000 and the largest, 2^24 - 1 (1000 is left out above 64 source symbols).</summary>
    public static readonly uint[] FixedEsis = [0, 1000, 16777215];

    /// <summary>The date written into the directory line (any date; it is not on the entry line).</summary>
    public static readonly DateOnly DirectoryDate = new(2026, 10, 30);

    /// <summary>Everything expected for one input.</summary>
    public static JsonObject Expected(VectorInput input)
    {
        var compression = Compression.Default;
        var (bulletin, envelope) = CanonicalForm.ToCanonicalBulletin(input.Type, input.From, input.To, input.At, input.Bid, input.TitleText, input.MessageText);
        var canonical = CanonicalForm.Canonicalise(input.MessageText, input.TitleText);
        var bulletinOctets = bulletin.Serialize();
        var envelopeOctets = envelope.Serialise();

        var withDictionary = TransferObject.ForBulletin(bulletin, ZstdDictionary.Gb7rdg1Id, compression, PieceSize);
        var withoutDictionary = TransferObject.ForBulletin(bulletin, Compression.NoDictionary, compression, PieceSize);
        var envelopeObject = TransferObject.ForEnvelope(envelope, ZstdDictionary.Gb7rdg1Id, compression);

        var link = new EnvelopeLink(envelopeObject.ObjectId, ObjectId.LongHash(envelopeObject.DictionaryId, envelopeObject.Bytes.ToArray()));
        var entry = new DirectoryEntry(withDictionary.ObjectId, withDictionary.DictionaryId, bulletinOctets.Length, bulletin.Bid, bulletin.Title,
            (byte)withDictionary.Kind, ObjectId.LongHash(withDictionary.DictionaryId, withDictionary.Bytes.ToArray()), link);
        var directory = Bulletin.TextEncoding.GetString(new BroadcastDirectory(DirectoryDate, [entry]).Serialize()).Split('\n');

        return new JsonObject
        {
            ["canonicalBodyHex"] = Hex(canonical.Body),
            ["envelopeHex"] = Hex(envelopeOctets),
            ["canonicalBulletinHex"] = Hex(bulletinOctets),
            ["bulletinDictionary1"] = Describe(withDictionary, withSymbols: true),
            ["bulletinDictionary0"] = Describe(withoutDictionary, withSymbols: false),
            ["envelopeObjectDictionary1"] = Describe(envelopeObject, withSymbols: true),
            ["directoryEntryLine"] = directory[2],
        };
    }

    /// <summary>The ESIs listed for an object of K source symbols, in order, without repeats.</summary>
    public static uint[] EsisFor(int k)
    {
        // Large objects (more than 64 source symbols) list ESI 0, K-1, K, K+1 and the largest only, to keep the file small.
        var list = k > 64 ? new SortedSet<uint>([0u, FixedEsis[^1]]) : new SortedSet<uint>(FixedEsis);
        list.Add((uint)(k - 1));
        list.Add((uint)k);
        list.Add((uint)(k + 1));
        return [.. list];
    }

    private static JsonObject Describe(TransferObject o, bool withSymbols)
    {
        var result = new JsonObject
        {
            ["objectHex"] = Hex(o.Bytes),
            ["id"] = ObjectId.Format(o.ObjectId),
            ["longHash"] = ObjectId.LongHash(o.DictionaryId, o.Bytes),
            ["transferLength"] = o.Length,
            ["sourceSymbols"] = o.SourceSymbols,
            ["otiHex"] = Hex(o.Oti.ToBytes()),
        };
        if (withSymbols)
        {
            var symbols = new JsonArray();
            foreach (var esi in EsisFor(o.SourceSymbols))
            {
                symbols.Add($"{esi}:{Hex(o.Frame(esi).Symbol.Span)}");
            }
            result["symbols"] = symbols;

            // Two whole frames: the first source symbol and the first repair symbol.
            var frames = new JsonArray
            {
                $"0:{Hex(o.Frame(0).ToBytes())}",
                $"{o.SourceSymbols}:{Hex(o.Frame((uint)o.SourceSymbols).ToBytes())}",
            };
            result["frames"] = frames;
        }
        return result;
    }

    private static string Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(data);

    /// <summary>The full document for these inputs.</summary>
    public static JsonObject Document(IEnumerable<VectorInput> inputs)
    {
        var cases = new JsonArray();
        foreach (var input in inputs)
        {
            cases.Add(new JsonObject
            {
                ["name"] = input.Name,
                ["description"] = input.Description,
                ["input"] = input.ToJson(),
                ["expected"] = Expected(input),
            });
        }

        return new JsonObject
        {
            ["format"] = "Model B v1, issue #142 of packet-net/pdn-mailcast. This file is the definition of the format: see docs/design.md.",
            ["generator"] = "tools/Mailcast.ModelBVectors (the library under test); reproduced independently by tools/raptorq-vectors/reproduce_model_b.py",
            ["zstd"] = "1.5.7 (ZstdSharp.Port 0.8.8), level 19, one thread, checksum, content size and dictionary ID on; dictionary 1 is src/Packet.Mailcast/Dictionaries/gb7rdg-1.zdict",
            ["pieceSize"] = PieceSize,
            ["notes"] = "All octets are lower-case hex. Symbols are 'esi:hex' of the encoding symbol; frames are 'esi:hex' of the whole payload including its CRC. The directory line is the bulletin's entry in an unsigned directory, with env= for the envelope object. Objects are type octet then the zstd frame.",
            ["cases"] = cases,
        };
    }

    /// <summary>Finds the first place two documents differ, as a path, or null when they are the same.</summary>
    public static string? FirstDifference(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected)
        {
            case JsonObject eo when actual is JsonObject ao:
                foreach (var (key, value) in eo)
                {
                    if (!ao.ContainsKey(key))
                    {
                        return $"{path}/{key} is missing";
                    }

                    if (FirstDifference(value, ao[key], $"{path}/{key}") is { } d)
                    {
                        return d;
                    }
                }

                return ao.Select(p => p.Key).FirstOrDefault(k => !eo.ContainsKey(k)) is { } extra ? $"{path}/{extra} is extra" : null;
            case JsonArray ea when actual is JsonArray aa:
                if (ea.Count != aa.Count)
                {
                    return $"{path} has {aa.Count} items, the file has {ea.Count}";
                }

                for (int i = 0; i < ea.Count; i++)
                {
                    if (FirstDifference(ea[i], aa[i], $"{path}[{i}]") is { } d)
                    {
                        return d;
                    }
                }

                return null;
            default:
                var e = expected?.ToJsonString();
                var a = actual?.ToJsonString();
                return e == a ? null : $"{path} differs";
        }
    }
}
