using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;
using Packet.SoundModem.Waterfall;

namespace Mailcast.Receiver.Tests;

/// <summary>Issue #228: frames are accepted from every station the receiver follows or knows, not only "sources".</summary>
public class FollowedSourcesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 10, 5, 0, TimeSpan.Zero);

    private static byte[] Payload() => Samples.Frames([Samples.Bulletin(1)])[0].AsSpan(16).ToArray();

    private static bool Hears(ReceiverHost host, string source)
    {
        long before = host.Intake.FramesHeard;
        host.Intake.Offer(Ax25UiFrame.Build(source, OnAir.Destination, Payload()));
        return host.Intake.FramesHeard > before;
    }

    private static ReceiverConfig Config(string dir, params ListenForEntry[] entries) => new()
    {
        Audio = "ubersdr:wessex.zapto.org",
        StateDirectory = dir,
        Stations = StationFixture.EverySlot,
        ListenFor = entries.Length == 0 ? null : entries,
    };

    private static ListenForEntry Entry(string call = "GM8BPQ") =>
        new() { Callsign = call, Minute = 30, EveryMinutes = 60, FrequencyHz = 7_047_000, Sideband = "usb" };

    [Fact]
    public async Task AListenForStation_IsAccepted_AnUnknownOneIsNot_AndNamedOnce()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        await using var host = new ReceiverHost(Config(dir.Path, Entry()), new FakeTimeProvider(Now), l => { lock (log) { log.Add(l); } });

        Assert.True(Hears(host, "GM8BPQ"));
        Assert.True(Hears(host, "GM8BPQ-3")); // any SSID: the bootstrap case, before its own directory is heard
        Assert.False(Hears(host, "G4ABC"));
        Assert.False(Hears(host, "G4ABC"));
        Assert.Equal(["GB7RDG", "GM8BPQ"], host.SourcesFromFollowed);
        Assert.Equal(["GB7RDG", "GM8BPQ"], host.Intake.Sources);
        lock (log)
        {
            Assert.Single(log, l => l.StartsWith("intake: a frame", StringComparison.Ordinal) && l.Contains("G4ABC", StringComparison.Ordinal));
            Assert.All(log, l => Assert.All(l, c => Assert.True(c < 128)));
        }
    }

    [Fact]
    public async Task AStationFollowedFromAnAdvert_IsAccepted_AndASkipKeepsIt()
    {
        var xyz = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        using var dir = new TempDirectory();
        var config = Config(dir.Path) with { TrustedKeys = [new TrustedKeySettings { Label = "M0XYZ", PublicKey = xyz.PublicKeyBase64 }] };
        await using var host = new ReceiverHost(config, new FakeTimeProvider(Now), _ => { });
        Assert.False(Hears(host, "G8BPQ"));
        Assert.True(Hears(host, "M0XYZ")); // pinned by hand

        foreach (var frame in Samples.Frames([Samples.Bulletin(1)], ScheduleOptions.Hourly with
        {
            Timetable = new SlotTimetable(new TimeOnly(0, 5), 60),
            Signer = xyz,
            Station = "M0XYZ",
            Adverts = [new("G8BPQ", new SlotTimetable(new TimeOnly(0, 30), 60), 7_053_800, Sideband.Usb)],
        }, Now))
        {
            host.Intake.Offer(frame);
        }
        await host.Intake.DrainAsync(CancellationToken.None);

        Assert.Contains("G8BPQ", host.SourcesFromFollowed);
        Assert.True(Hears(host, "G8BPQ-1"));

        // Told to skip it: its slots are not listened at, but its frames are still accepted.
        host.Reconfigure(host.Config with { SkipStations = ["G8BPQ"] });
        Assert.Contains("G8BPQ", host.SourcesFromFollowed);
        Assert.True(Hears(host, "G8BPQ"));
    }

    [Fact]
    public async Task RemovingAListenForEntry_UpdatesTheList()
    {
        using var dir = new TempDirectory();
        await using var host = new ReceiverHost(Config(dir.Path, Entry()), new FakeTimeProvider(Now), _ => { });
        Assert.True(Hears(host, "GM8BPQ"));

        host.Reconfigure(Config(dir.Path));
        Assert.Equal(["GB7RDG"], host.SourcesFromFollowed);
        Assert.False(Hears(host, "GM8BPQ"));
    }

    [Fact]
    public async Task APinnedStationLearntEarlier_IsAcceptedAfterARestart()
    {
        var bpq = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        using var dir = new TempDirectory();
        var config = Config(dir.Path) with { TrustedKeys = [new TrustedKeySettings { Label = "GM8BPQ", PublicKey = bpq.PublicKeyBase64 }] };
        await using (var first = new ReceiverHost(config, new FakeTimeProvider(Now), _ => { }))
        {
            foreach (var frame in Samples.Frames([Samples.Bulletin(1)], ScheduleOptions.Hourly with { Timetable = new SlotTimetable(new TimeOnly(0, 40), 60), Signer = bpq, Station = "GM8BPQ" }, Now.AddMinutes(30)))
            {
                first.Intake.Offer(frame);
            }
            await first.Intake.DrainAsync(CancellationToken.None);
        }

        await using var restarted = new ReceiverHost(config, new FakeTimeProvider(Now), _ => { });
        Assert.Contains("GM8BPQ", restarted.SourcesFromFollowed);
        Assert.True(Hears(restarted, "GM8BPQ"));
    }

    [Fact]
    public async Task Bootstrap_ListenFor_ThenItsDirectoryIsHeard_ThenPinned_ThenRetired_AcceptedThroughout()
    {
        var bpq = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        using var dir = new TempDirectory();
        var config = Config(dir.Path, Entry()) with { TrustedKeys = [new TrustedKeySettings { Label = "GM8BPQ", PublicKey = bpq.PublicKeyBase64 }] };
        await using var host = new ReceiverHost(config, new FakeTimeProvider(Now), _ => { });
        Assert.True(Hears(host, "GM8BPQ")); // asked for by hand, before anything is heard

        var frames = Samples.Frames([Samples.Bulletin(1)], ScheduleOptions.Hourly with { Timetable = new SlotTimetable(new TimeOnly(0, 40), 60), Signer = bpq, Station = "GM8BPQ" }, Now.AddMinutes(30));
        foreach (var frame in frames)
        {
            // Sent as GM8BPQ itself, which only a receiver that knows of it accepts.
            long before = host.Intake.FramesHeard;
            host.Intake.Offer(Ax25UiFrame.Build("GM8BPQ", OnAir.Destination, frame.AsSpan(16).ToArray()));
            Assert.Equal(before + 1, host.Intake.FramesHeard);
        }
        await host.Intake.DrainAsync(CancellationToken.None);

        // Pinned now, and the entry retired: still accepted.
        Assert.Contains(host.Intake.Pins(), p => p.Station == "GM8BPQ");
        Assert.NotNull(host.ListenForState.Get(host.Config.ListenFor![0])!.Retired);
        Assert.Contains("GM8BPQ", host.SourcesFromFollowed);
        Assert.True(Hears(host, "GM8BPQ"));
    }

    [Fact]
    public void SourcesLine_SaysFollowedStationsAreAlsoAccepted()
    {
        Assert.Contains("the stations it knows of", ReceiverHost.SourcesLine(new ReceiverConfig()), StringComparison.Ordinal);
    }
}
