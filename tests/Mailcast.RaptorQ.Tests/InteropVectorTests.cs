using System.Text.Json;

namespace Mailcast.RaptorQ.Tests;

/// <summary>
/// Checks against vectors made by the independent Rust raptorq crate (tools/raptorq-vectors):
/// the same symbols for the same ESIs, decoding the crate's symbols, and rejecting a set of
/// symbols the crate could not decode either.
/// </summary>
public class InteropVectorTests
{
    public sealed record Packet(int Sbn, uint Esi, byte[] Data);

    public sealed record VectorCase(
        string Name,
        long TransferLength,
        int SymbolSize,
        int SourceBlocks,
        int SubBlocks,
        int Alignment,
        uint Seed,
        string Oti,
        Packet[] Symbols,
        Packet[] Decodable,
        uint[]? Undecodable);

    private static readonly Lazy<Dictionary<string, VectorCase>> Cases = new(Load);

    private static Dictionary<string, VectorCase> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Vectors", "raptorq-crate-2.0.1.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, VectorCase>();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var undecodable = c.GetProperty("undecodable");
            var item = new VectorCase(
                c.GetProperty("name").GetString()!,
                c.GetProperty("transferLength").GetInt64(),
                c.GetProperty("symbolSize").GetInt32(),
                c.GetProperty("sourceBlocks").GetInt32(),
                c.GetProperty("subBlocks").GetInt32(),
                c.GetProperty("alignment").GetInt32(),
                c.GetProperty("seed").GetUInt32(),
                c.GetProperty("oti").GetString()!,
                ParsePackets(c.GetProperty("symbols")),
                ParsePackets(c.GetProperty("decodable")),
                undecodable.ValueKind == JsonValueKind.Null
                    ? null
                    : undecodable.GetString()!.Split(',').Select(uint.Parse).ToArray());
            result[item.Name] = item;
        }
        return result;
    }

    private static Packet[] ParsePackets(JsonElement array) =>
        array.EnumerateArray().Select(e =>
        {
            var parts = e.GetString()!.Split(':');
            return new Packet(int.Parse(parts[0]), uint.Parse(parts[1]), Convert.FromHexString(parts[2]));
        }).ToArray();

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Cases.Value.Keys)
        {
            data.Add(name);
        }
        return data;
    }

    private static ObjectTransmissionInformation OtiOf(VectorCase c) =>
        new(c.TransferLength, c.SymbolSize, c.SourceBlocks, c.SubBlocks, c.Alignment);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Oti_EncodesAsTheCrateDoes(string name)
    {
        var c = Cases.Value[name];
        var oti = OtiOf(c);
        Assert.Equal(c.Oti, Convert.ToHexStringLower(oti.ToBytes()));
        Assert.Equal(oti, ObjectTransmissionInformation.Read(Convert.FromHexString(c.Oti)));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Encode_MatchesTheCrateByteForByte(string name)
    {
        var c = Cases.Value[name];
        var encoder = new ObjectEncoder(TestData.Xorshift(c.Seed, c.TransferLength), OtiOf(c));
        foreach (var p in c.Symbols)
        {
            var ours = encoder.Encode(new PayloadId(p.Sbn, p.Esi));
            Assert.True(p.Data.AsSpan().SequenceEqual(ours), $"{name}: symbol {p.Sbn}:{p.Esi} differs");
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Decode_RebuildsTheObjectFromTheCratesSymbols(string name)
    {
        var c = Cases.Value[name];
        var decoder = new ObjectDecoder(ObjectTransmissionInformation.Read(Convert.FromHexString(c.Oti)));
        foreach (var p in c.Decodable)
        {
            decoder.Add(new PayloadId(p.Sbn, p.Esi), p.Data);
        }
        var result = decoder.TryDecode();
        Assert.NotNull(result);
        Assert.Equal(TestData.Xorshift(c.Seed, c.TransferLength), result);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Decode_FailsWhereTheCrateFails(string name)
    {
        var c = Cases.Value[name];
        if (c.Undecodable is null)
        {
            return; // the generator found no such set for this case
        }
        var oti = OtiOf(c);
        var encoder = new ObjectEncoder(TestData.Xorshift(c.Seed, c.TransferLength), oti);
        var decoder = new SourceBlockDecoder(oti.SourceBlockSymbols(0), oti.SymbolSize);
        foreach (uint esi in c.Undecodable)
        {
            decoder.Add(esi, encoder.Block(0).Encode(esi));
        }
        Assert.Equal(oti.SourceBlockSymbols(0), decoder.ReceivedCount);
        Assert.Null(decoder.TryDecode());
    }
}
