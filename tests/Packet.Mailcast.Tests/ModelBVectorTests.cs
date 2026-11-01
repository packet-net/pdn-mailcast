using System.Text.Json.Nodes;
using Mailcast.ModelBVectors;
using Packet.Mailcast;

namespace Packet.Mailcast.Tests;

/// <summary>
/// Rebuilds every byte of the Model B format (issue #142) from the inputs in Vectors/model-b-v1.json and compares.
/// Two head ends send interchangeable pieces only while these bytes are the same, so a dependency bump
/// (ZstdSharp, say) or a code change that alters any of them fails here. The file is the definition of the format.
/// </summary>
public class ModelBVectorTests
{
    private static readonly Lazy<JsonObject> File = new(() =>
        (JsonObject)JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "model-b-v1.json")))!);

    private static JsonArray Cases => File.Value["cases"]!.AsArray();

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases)
        {
            data.Add((string)c!["name"]!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Case_RebuildsEveryByteTheFileHolds(string name)
    {
        var expected = Cases.Single(c => (string)c!["name"]! == name)!;
        var input = VectorInput.FromJson(expected);
        var actual = ModelBVectors.Expected(input);

        var difference = ModelBVectors.FirstDifference(expected["expected"], actual, "expected");
        Assert.True(difference is null,
            $"The Model B format changed: case '{name}', {difference}. If the change is intended it is a format change (docs/design.md), so regenerate with tools/Mailcast.ModelBVectors and roll it out expand then contract.");
    }

    [Fact]
    public void File_CoversTheListedInputs()
    {
        var names = Cases.Select(c => (string)c!["name"]!).ToArray();
        Assert.Equal(12, names.Length);
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.Equal(ModelBVectors.PieceSize, (int)File.Value["pieceSize"]!);
        Assert.Equal(240, ModelBVectors.PieceSize);
    }

    [Fact]
    public void File_IsAsciiOnly()
    {
        var bytes = System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Vectors", "model-b-v1.json"));
        Assert.All(bytes, b => Assert.True(b < 0x80, "the vector file has a non-ASCII octet"));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Case_ObjectsRebuildTheBulletinFromTheFile(string name)
    {
        // Independent of the builder: take the stored object octets, decompress, parse, and rebuild the station's copy.
        var c = Cases.Single(x => (string)x!["name"]! == name)!;
        var input = VectorInput.FromJson(c);
        var e = c["expected"]!;
        var compression = Compression.Default;

        var bulletinObject = Convert.FromHexString((string)e["bulletinDictionary1"]!["objectHex"]!);
        var (kind, content) = TransferObject.Unpack(bulletinObject, ZstdDictionary.Dictionary1Id, compression);
        Assert.Equal(ObjectKind.Bulletin, kind);
        var bulletin = Bulletin.Parse(content);
        Assert.True(bulletin.Canonical);

        var envelopeObject = Convert.FromHexString((string)e["envelopeObjectDictionary1"]!["objectHex"]!);
        var (envKind, envContent) = TransferObject.Unpack(envelopeObject, ZstdDictionary.Dictionary1Id, compression);
        Assert.Equal(ObjectKind.Envelope, envKind);
        var envelope = BulletinEnvelope.Parse(envContent);

        var (text, title) = CanonicalForm.FromCanonicalBulletin(bulletin, envelope);
        Assert.Equal(input.MessageText, text);
        Assert.Equal(input.TitleText, title);

        var plain = Convert.FromHexString((string)e["bulletinDictionary0"]!["objectHex"]!);
        Assert.Equal(content, TransferObject.Unpack(plain, Compression.NoDictionary, compression).Content);
    }
}
