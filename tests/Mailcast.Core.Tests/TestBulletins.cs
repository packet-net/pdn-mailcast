using System.Globalization;
using System.Text;

namespace Mailcast.Core.Tests;

/// <summary>Made-up bulletins that look enough like real ones to compress like them.</summary>
internal static class TestBulletins
{
    // Common words, and a few thousand made-up ones, so the text compresses about as well as
    // real bulletins do (to roughly a third with plain zstd).
    private static readonly string[] Common =
    [
        "the", "and", "net", "packet", "station", "frequency", "band", "QSO", "DX", "contest", "news",
        "repeater", "antenna", "propagation", "solar", "flux", "index", "weekend", "club", "meeting",
        "operators", "licence", "RSGB", "IARU", "73", "de", "on", "at", "from", "will", "be", "this",
        "earthquake", "magnitude", "km", "of", "depth", "UTC", "report", "bulletin", "update", "list",
    ];

    private static readonly string[] Rare = MakeRare();

    private static string[] MakeRare()
    {
        var rng = new Random(12345);
        var words = new string[3000];
        for (int i = 0; i < words.Length; i++)
        {
            var chars = new char[3 + rng.Next(7)];
            for (int c = 0; c < chars.Length; c++)
            {
                chars[c] = (char)('a' + rng.Next(26));
            }
            words[i] = new string(chars);
        }
        return words;
    }

    private static string Word(Random rng) => rng.Next(2) == 0 ? Common[rng.Next(Common.Length)] : Rare[rng.Next(Rare.Length)];

    private static readonly string[] Calls = ["G4ABC", "M0XYZ", "LU9DCE", "HP2DFA", "KC2NJV", "GB7RDG", "M9YYY", "F6FBB"];

    public static Bulletin Make(int seed, int bodySize, DateTimeOffset? date = null)
    {
        var rng = new Random(seed);
        string from = Calls[rng.Next(Calls.Length)];
        string bbs = Calls[rng.Next(Calls.Length)];
        var when = date ?? new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(rng.Next(86400));
        var routing = new List<string>();
        for (int i = 0; i < 1 + rng.Next(4); i++)
        {
            routing.Add(string.Create(CultureInfo.InvariantCulture, $"R:{when.AddMinutes(-i * 7):yyMMdd/HHmm}Z {rng.Next(10000)}@{Calls[rng.Next(Calls.Length)]}.#42.GBR.EURO BPQ6.0.25"));
        }
        var body = new StringBuilder("\r\n");
        var line = new StringBuilder();
        while (body.Length < bodySize)
        {
            line.Append(Word(rng)).Append(' ');
            if (line.Length > 60)
            {
                body.Append(line.ToString().TrimEnd()).Append("\r\n");
                line.Clear();
            }
        }
        body.Append(CultureInfo.InvariantCulture, $"73 de {from}\r\n");
        return new Bulletin(
            'B',
            from,
            rng.Next(2) == 0 ? "ALL" : "NEWS",
            rng.Next(3) == 0 ? "" : "WW",
            string.Create(CultureInfo.InvariantCulture, $"{seed}_{bbs}"),
            string.Create(CultureInfo.InvariantCulture, $"Test bulletin {seed}: {Word(rng)} {Word(rng)}"),
            when,
            routing,
            body.ToString());
    }

    /// <summary>A day's worth: sizes from a few hundred octets to about 20 KB, most small.</summary>
    public static List<Bulletin> Day(int seed, int count)
    {
        var rng = new Random(seed);
        var result = new List<Bulletin>();
        for (int i = 0; i < count; i++)
        {
            int size = rng.Next(4) == 0 ? 3000 + rng.Next(17000) : 300 + rng.Next(3000);
            result.Add(Make((seed * 1000) + i, size));
        }
        return result;
    }
}

/// <summary>A scratch directory removed when the test ends.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mailcast-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Helpers for content-addressed IDs.</summary>
internal static class Ids
{
    /// <summary>The object ID a bulletin gets with the standard settings.</summary>
    public static ulong Of(Bulletin bulletin) =>
        TransferObject.ForBulletin(bulletin, ZstdDictionary.Gb7rdg1Id, Compression.Default).ObjectId;

    /// <summary>The directory's object ID in a plan.</summary>
    public static ulong DirectoryOf(SlotBroadcast plan) => plan.Objects[0].Transfer.ObjectId;
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class TestStores
{
    /// <summary>Store options without fsync, for tests that write thousands of symbols.</summary>
    public static ReceiverStoreOptions Fast => new() { FlushToDisk = false };
}
