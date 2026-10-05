using System.Globalization;
using System.Text;
using Mailcast.Core;

namespace Mailcast.HeadEnd.Tests;

public sealed class TempDirectory : IDisposable
{
    public TempDirectory() => Path = Directory.CreateTempSubdirectory("mailcast-headend-test-").FullName;

    public string Path { get; }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

/// <summary>Made-up bulletins with text that compresses roughly like real ones.</summary>
public static class Bulletins
{
    public static Bulletin Make(int seed, int bodySize, char type = 'B')
    {
        var rng = new Random(seed);
        var body = new StringBuilder("\r\n");
        var line = new StringBuilder();
        while (body.Length < bodySize)
        {
            line.Append(rng.Next(4) == 0 ? "propagation" : new string((char)('a' + rng.Next(26)), 1 + rng.Next(3)) + rng.Next(1000).ToString(CultureInfo.InvariantCulture)).Append(' ');
            if (line.Length > 60)
            {
                body.Append(line.ToString().TrimEnd()).Append("\r\n");
                line.Clear();
            }
        }
        string bid = string.Create(CultureInfo.InvariantCulture, $"{1000 + seed}_GB7XYZ");
        string routing = string.Create(CultureInfo.InvariantCulture, $"R:261004/1000Z {1000 + seed}@GB7XYZ.#42.GBR.EURO BPQ6.0.25\r\n");
        return Bulletin.FromMessageText(type, "G4ABC", "ALL", "WW", bid, $"Bulletin {seed}", new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.Zero), routing + body);
    }
}
