using System.Globalization;
using System.Text;
using Packet.Mailcast;
using Packet.SoundModem.Waterfall;

namespace Mailcast.Receiver.Tests;

/// <summary>Bulletins and broadcast frames for the tests.</summary>
internal static class Samples
{
    public static readonly DateOnly Day = new(2026, 10, 4);

    /// <summary>The callsign the head end sends as today.</summary>
    public const string Source = "GB7RDG";

    /// <summary>A bulletin shaped like one GB7RDG forwards, with its R: lines.</summary>
    public static Bulletin Bulletin(int number, string from = "G4ABC", string to = "ALL", string at = "GBR", int bodyLines = 20, string? title = null, DateTimeOffset? date = null)
    {
        var when = date ?? new DateTimeOffset(2026, 10, 3, 8, 0, 0, TimeSpan.Zero).AddMinutes(number * 17);
        var body = new StringBuilder("\r\n");
        var rng = new Random(number);
        string[] words = ["the", "net", "will", "meet", "on", "Tuesday", "at", "2000", "local", "on", "the", "usual", "frequency",
            "propagation", "report", "solar", "flux", "is", "up", "and", "conditions", "on", "40", "m", "were", "good", "all", "week"];
        for (int line = 0; line < bodyLines; line++)
        {
            var text = new StringBuilder();
            while (text.Length < 60)
            {
                text.Append(words[rng.Next(words.Length)]).Append(' ');
            }
            body.Append(text.ToString().TrimEnd()).Append("\r\n");
        }
        body.Append(CultureInfo.InvariantCulture, $"73 de {from}\r\n");
        return new Bulletin(
            'B',
            from,
            to,
            at,
            string.Create(CultureInfo.InvariantCulture, $"{number}_GB7RDG"),
            title ?? string.Create(CultureInfo.InvariantCulture, $"Club news number {number}"),
            when,
            [
                string.Create(CultureInfo.InvariantCulture, $"R:{when:yyMMdd/HHmm}Z {number}@GB7RDG.#42.GBR.EURO LinBPQ6.0.25"),
                string.Create(CultureInfo.InvariantCulture, $"R:{when.AddMinutes(-9):yyMMdd/HHmm}Z 7{number}@GB7XYZ.#47.GBR.EURO LinBPQ6.0.25"),
            ],
            body.ToString());
    }

    /// <summary>One day's broadcast of <paramref name="bulletins"/>, as the AX.25 frames the modem would decode.</summary>
    public static IReadOnlyList<byte[]> Frames(IEnumerable<Bulletin> bulletins, int seed = 1, int dayOffset = 0)
    {
        var plan = BroadcastScheduler.Plan(
            bulletins.Select(b => new BroadcastBulletin(b, Day)),
            Day.AddDays(dayOffset),
            seed,
            Compression.Default);
        return [.. plan.Frames.Select(f => Ax25UiFrame.Build(Samples.Source, OnAir.Destination, f.ToBytes()))];
    }

    /// <summary>A slot's frames as a head end with these options sends them, its directory carrying its timetable if it has one.</summary>
    public static IReadOnlyList<byte[]> Frames(IEnumerable<Bulletin> bulletins, ScheduleOptions options, DateTimeOffset slot, int seed = 1)
    {
        var plan = BroadcastScheduler.Plan(bulletins.Select(b => new BroadcastBulletin(b, DateOnly.FromDateTime(slot.UtcDateTime), slot)), slot, seed, Compression.Default, options);
        return [.. plan.Frames.Select(f => Ax25UiFrame.Build(Samples.Source, OnAir.Destination, f.ToBytes()))];
    }

    /// <summary>
    /// As the other overload, with the directory naming <paramref name="mode"/> as the waveform
    /// the slot went out on: GB7RDG takes turns on WN4 and WN3, so the same rotation, resent on
    /// the other one, is a different object (the mode is part of what is compressed and hashed).
    /// </summary>
    public static IReadOnlyList<byte[]> Frames(IEnumerable<Bulletin> bulletins, ScheduleOptions options, DateTimeOffset slot, string mode, int seed = 1)
    {
        var carried = bulletins.Select(b =>
        {
            var transfer = TransferObject.ForBulletin(b, options.DictionaryId, Compression.Default, options.SymbolSize, options.Alignment);
            return new CarriedBulletin(b.Bid, b.Title, b.Serialize().Length, DateOnly.FromDateTime(slot.UtcDateTime), transfer, 0, slot);
        }).ToList();
        var plan = BroadcastScheduler.Plan(carried, slot, seed, Compression.Default, options, mode: mode);
        return [.. plan.Frames.Select(f => Ax25UiFrame.Build(Samples.Source, OnAir.Destination, f.ToBytes()))];
    }
}

/// <summary>A scratch directory removed when the test ends.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mailcast-receiver-tests-" + Guid.NewGuid().ToString("N"));
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
        catch (UnauthorizedAccessException)
        {
        }
    }
}
