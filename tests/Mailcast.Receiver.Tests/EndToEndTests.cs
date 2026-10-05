using System.Collections.Concurrent;
using Packet.Mailcast;
using Mailcast.Receiver.Delivery;
using Packet.SoundModem.Waterfall;
using Xunit.Abstractions;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// The whole receiver without a radio: a day's broadcast from the head end's scheduler, modulated
/// by pdn-soundmodem's MS110D modem into a recording with noise and a fade, decoded by the
/// receiver and delivered into a real LinBPQ in docker.
/// </summary>
/// <remarks>
/// Needs docker. Tagged Category=Docker so a test run can choose it or leave it out explicitly:
/// <c>dotnet test --filter Category=Docker</c> runs it and <c>Category!=Docker</c> skips it. It is
/// not skipped quietly when docker is missing: asked for and unable to run, it fails.
/// </remarks>
[Trait("Category", "Docker")]
public class EndToEndTests(ITestOutputHelper output)
{
    // Dated a few hours ago, whenever the test runs: LinBPQ holds bulletins older than its BID
    // lifetime, and these must look like the day's traffic. Whole seconds, as bulletins are.
    private static readonly DateTimeOffset Written = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddHours(-3);

    private static readonly Bulletin[] Bulletins =
    [
        Samples.Bulletin(11, bodyLines: 12, date: Written),
        Samples.Bulletin(12, bodyLines: 160, title: "RSGB news for the week, with a title long enough to need trimming in places", date: Written.AddMinutes(5)),
        Samples.Bulletin(13, from: "M0XYZ", to: "NEWS", at: "WW", bodyLines: 40, date: Written.AddMinutes(10)),
        Samples.Bulletin(14, from: "LU9DCE", to: "DX", at: "WW", bodyLines: 4, date: Written.AddMinutes(15)),
    ];

    [Fact]
    public async Task Slot_ThroughNoiseAndAFade_EveryBulletinArrivesOnceInLinBpq()
    {
        // A deadline so a wedged container cannot hang the run; nothing is timed against it.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var cancellation = deadline.Token;
        using var dir = new TempDirectory();

        // The head end's plan for the day, and which bursts the fade takes: one directory frame
        // and one of the largest bulletin's, leaving it more than K. The plan lists the directory first.
        var plan = BroadcastScheduler.Plan(Bulletins.Select(b => new BroadcastBulletin(b, Samples.Day)), Samples.Day, 4, Compression.Default);
        var largest = plan.Objects.Skip(1).OrderByDescending(o => o.Count).First();
        Assert.True(largest.Count - 1 > largest.Transfer.SourceSymbols, "the fade would leave the largest bulletin short");
        var faded = new HashSet<int>
        {
            plan.Frames.Select((f, i) => (f, i)).First(x => x.f.ObjectId == plan.Objects[0].Transfer.ObjectId).i,
            plan.Frames.Select((f, i) => (f, i)).Last(x => x.f.ObjectId == largest.Transfer.ObjectId).i,
        };
        var frames = plan.Frames.Select(f => Ax25UiFrame.Build(OnAir.Source, OnAir.Destination, f.ToBytes())).ToList();
        string wav = Path.Combine(dir.Path, "slot.wav");
        SlotRecording.Write(wav, frames, faded, snrDb: 12, toneOffsetHz: 2.5, seed: 2026);
        output.WriteLine($"{frames.Count} frames, {faded.Count} lost to the fade, recording {new FileInfo(wav).Length / 1_000_000.0:F1} MB");

        await using var bpq = await LinBpqContainer.StartAsync(cancellation);

        // First receiver: hears the slot and delivers everything.
        var first = await DecodeAsync(Path.Combine(dir.Path, "first"), wav, bpq, cancellation);
        Assert.Null(first.Failure);
        Assert.Equal(frames.Count - faded.Count, first.FramesHeard);
        Assert.NotNull(first.Tone);
        Assert.InRange(first.Tone.OffsetHz, 2.2, 2.8);
        Assert.InRange(first.Tone.SnrDb, 10.5, 13.5);
        foreach (var bulletin in Bulletins)
        {
            Assert.Equal(DeliveryVerdict.Accepted, first.Verdicts[bulletin.Bid]);
        }

        // In the BBS: each bulletin once, with its BID, headers and text.
        var messages = await bpq.ReadAllMessagesAsync(cancellation);
        foreach (var (listing, text) in messages)
        {
            output.WriteLine(listing);
            output.WriteLine(text);
        }
        Assert.Equal(Bulletins.Length, messages.Count);
        foreach (var bulletin in Bulletins)
        {
            var (listing, message) = Assert.Single(messages, m => m.Text.Contains("Bid: " + bulletin.Bid + "\n", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("Type/Status: BN", message, StringComparison.Ordinal);
            Assert.Contains("From: " + bulletin.From + "\n", message, StringComparison.Ordinal);
            Assert.Contains("To: " + bulletin.To + "\n", message, StringComparison.Ordinal);
            Assert.Contains(" @" + bulletin.At + " ", listing, StringComparison.Ordinal);
            // LinBPQ keeps 60 characters of a title.
            Assert.Contains("Title: " + bulletin.Title[..Math.Min(60, bulletin.Title.Length)] + "\n", message, StringComparison.Ordinal);
            foreach (string routing in bulletin.RoutingLines)
            {
                Assert.Contains(routing, message, StringComparison.Ordinal);
            }
            Assert.Contains(bulletin.Body.Replace("\r\n", "\n", StringComparison.Ordinal).Trim(), message, StringComparison.Ordinal);
        }

        // A second receiver hearing the same slot: the BBS already has every BID, so nothing
        // arrives twice.
        var second = await DecodeAsync(Path.Combine(dir.Path, "second"), wav, bpq, cancellation);
        Assert.Null(second.Failure);
        foreach (var bulletin in Bulletins)
        {
            Assert.Equal(DeliveryVerdict.AlreadyHad, second.Verdicts[bulletin.Bid]);
        }
        Assert.Equal(Bulletins.Length, (await bpq.ReadAllMessagesAsync(cancellation)).Count);
    }

    private sealed record Decoded(string? Failure, long FramesHeard, ToneReport? Tone, IReadOnlyDictionary<string, DeliveryVerdict> Verdicts);

    private async Task<Decoded> DecodeAsync(string state, string wav, LinBpqContainer bpq, CancellationToken cancellation)
    {
        var config = new ReceiverConfig
        {
            Audio = "wav:" + wav,
            StateDirectory = state,
            Bbs = new BbsSettings { Host = "127.0.0.1", Port = bpq.FbbPort, Login = LinBpqContainer.Login, Password = LinBpqContainer.Password },
        };
        var log = new ConcurrentQueue<string>();
        await using var host = new ReceiverHost(config, TimeProvider.System, log.Enqueue);
        string? failure = await host.DecodeOnceAsync(wav, cancellation);
        foreach (string line in log)
        {
            output.WriteLine(line);
        }
        var verdicts = Bulletins.ToDictionary(b => b.Bid, b => host.Ledger.Latest(b.Bid)?.Verdict ?? DeliveryVerdict.NotOffered);
        return new Decoded(failure, host.Intake.FramesHeard, host.Slots.Last?.Tone, verdicts);
    }
}
