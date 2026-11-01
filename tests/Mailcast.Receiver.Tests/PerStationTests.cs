using Mailcast.Receiver.Retune;
using Packet.SoundModem.Rig;
using Mailcast.Receiver.Web;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;

namespace Mailcast.Receiver.Tests;

/// <summary>A frequency, place and name for each station, not GB7RDG's built in (issue #179).</summary>
public class PerStationTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly GroundPlace Wessex = GroundPlace.FromLocator("IO80")!;

    private static StationOnAir Hf(long hz, Sideband sideband = Sideband.Usb) => new("G8BPQ", hz, sideband, "IO83", "its own directory");

    [Fact]
    public void ARetuningReceiver_MovesTheDial_ToKeepTheSignalWhereItsConfigPutsGb7rdgs()
    {
        // 1800 Hz in the audio at the usual dial: a signal on 7.047 MHz needs the dial on 7.0452.
        var reach = StationReaching.Judge(Hf(7_047_000), 1800, 7_052_000, canRetune: true);
        Assert.True(reach.Heard);
        Assert.Equal(7_045_200, reach.DialHz, 0.5);
        Assert.Equal(HowHeard.Retune, reach.How);

        // The narrow-filter dial (1500 Hz) keeps its choice for every station.
        Assert.Equal(7_045_500, StationReaching.Judge(Hf(7_047_000), 1500, 7_052_300, canRetune: true).DialHz, 0.5);
    }

    [Fact]
    public void AReceiverThatCannotRetune_HearsWhatItsPassbandHolds_AndSaysWhyNotOtherwise()
    {
        // Dial 7.052 MHz hears 7.053 to 7.054 MHz: GB7RDG's 7.0538, not G8BPQ's 7.047.
        Assert.True(StationReaching.Judge(Hf(7_053_800), 1800, 7_052_000, canRetune: false) is { Heard: true, How: HowHeard.Passband });
        var no = StationReaching.Judge(Hf(7_047_000), 1800, 7_052_000, canRetune: false);
        Assert.False(no.Heard);
        Assert.Contains("7.047 MHz USB, not where this receiver listens", no.Why, StringComparison.Ordinal);
        Assert.Contains("does not retune", no.Why, StringComparison.Ordinal);

        // Move its dial and the same station is in the passband.
        Assert.True(StationReaching.Judge(Hf(7_047_000), 1800, 7_045_200, canRetune: false).Heard);
    }

    [Fact]
    public void BehindAnLnb_AStationAboveHfIsHeardAtItsIf_WithTheRealFrequencyKept()
    {
        // A calibrated, non-round oscillator; QO-100's downlink 10489.75 MHz comes out at 739.737655 MHz.
        const long lo = 9_750_012_345;
        var reach = StationReaching.Judge(Hf(10_489_750_000), 1800, 7_052_000, canRetune: true, lo);
        Assert.True(reach.Heard);
        Assert.Equal(HowHeard.Retune, reach.How);
        Assert.Equal(739_737_655, reach.IfHz);
        Assert.Equal(739_737_655 - 1800, reach.DialHz, 0.5);

        // A fixed receiver whose dial is at the IF hears it in its passband.
        var fixedDial = StationReaching.Judge(Hf(10_489_750_000), 1800, 739_735_855, canRetune: false, lo);
        Assert.True(fixedDial is { Heard: true, How: HowHeard.Passband, IfHz: 739_737_655 });
        Assert.False(StationReaching.Judge(Hf(10_489_750_000), 1800, 7_052_000, canRetune: false, lo).Heard);

        // HF is unchanged by the setting.
        var hf = StationReaching.Judge(Hf(7_047_000), 1800, 7_052_000, canRetune: true, lo);
        Assert.Equal(7_045_200, hf.DialHz, 0.5);
        Assert.Null(hf.IfHz);

        // Outside the LNB's input range, or below its oscillator.
        var wrong = StationReaching.Judge(Hf(14_000_000_000), 1800, 7_052_000, canRetune: true, lo);
        Assert.False(wrong.Heard);
        Assert.Contains("lnbHz", wrong.Why, StringComparison.Ordinal);
        Assert.False(StationReaching.Judge(Hf(9_000_000_000), 1800, 7_052_000, canRetune: true, lo).Heard);
    }

    [Fact]
    public void WithoutAnLnb_AStationAboveHfIsNotFollowed_NamingTheSetting()
    {
        var no = StationReaching.Judge(Hf(10_489_750_000), 1800, 7_052_000, canRetune: false);
        Assert.False(no.Heard);
        Assert.Contains("lnbHz", no.Why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BehindAnLnb_TheHostTunesTheIf_AndShowsTheDownlink()
    {
        var bpq = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        using var dir = new TempDirectory();
        var slot = new DateTimeOffset(2026, 10, 26, 10, 30, 0, TimeSpan.Zero);
        var config = new ReceiverConfig
        {
            Audio = Samples.SoundCard,
            StateDirectory = dir.Path,
            Stations = StationFixture.EverySlot,
            Rig = new RigSettings { Rigctld = "127.0.0.1:4532", DedicatedRadio = true },
            LnbHz = 9_750_000_000,
        };
        await using var host = new ReceiverHost(config, new FakeTimeProvider(slot.AddMinutes(1)), _ => { });
        await Hear(host, ScheduleOptions.Hourly with { Timetable = new SlotTimetable(new TimeOnly(0, 30), 60), Signer = bpq, Station = "G8BPQ", Details = new StationDetails(10_489_750_000, Sideband.Usb, "IO83") }, slot, 1);

        var listened = host.Listening.Single(l => l.Station == "G8BPQ");
        Assert.True(listened.Followed);
        Assert.Equal(10_489_750_000, listened.FrequencyHz);
        Assert.Equal(739_750_000, listened.IfHz);
        var pipeline = AudioPipeline.Create(new AudioSource(AudioSourceKind.Alsa, "test"), config.DialHz, StationFixture.SignalHz, _ => { }, new FakeTimeProvider(slot));
        Assert.Equal(739_748_200, host.PointAtSlot(slot, pipeline)!.Value, 0.5);
        Assert.Equal(739_750_000, pipeline.SignalHz);
        Assert.Equal(9_750_000_000, pipeline.LnbOffsetHz);
        Assert.Equal(1800, pipeline.AudioCentreHz, 0.5);
    }

    [Theory]
    [InlineData(9_750_012_345L, true)]
    [InlineData(10_600_000_000L, true)]
    [InlineData(5L, false)]
    [InlineData(60_000_000_000L, false)]
    public void LnbHz_IsReadFromTheConfig_AndHeldToARealOscillator(long lnb, bool ok)
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, $$"""{ "lnbHz": {{lnb}} }""");
        if (ok)
        {
            Assert.Equal(lnb, ReceiverConfig.Load(path).LnbHz);
        }
        else
        {
            Assert.Contains("lnbHz", Assert.Throws<ConfigException>(() => ReceiverConfig.Load(path)).Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AboveHf_OrLowerSideband_IsNotFollowed_WithTheReason()
    {
        var qo100 = StationReaching.Judge(Hf(10_489_750_000), 1800, 7_052_000, canRetune: true);
        Assert.False(qo100.Heard);
        Assert.Contains("above HF", qo100.Why, StringComparison.Ordinal);
        Assert.Contains("lnbHz", qo100.Why, StringComparison.Ordinal);

        var lsb = StationReaching.Judge(Hf(7_047_000, Sideband.Lsb), 1800, 7_052_000, canRetune: true);
        Assert.False(lsb.Heard);
        Assert.Contains("only listens for upper sideband", lsb.Why, StringComparison.Ordinal);

        // A fixed receiver whose passband does hold a station above HF (a converter's dial) hears it.
        Assert.True(StationReaching.Judge(Hf(10_489_750_000), 1800, 10_489_748_200, canRetune: false).Heard);
    }

    [Fact]
    public void OnlyHfIsSkywave()
    {
        Assert.True(Hf(7_047_000).Skywave);
        Assert.True(Hf(30_000_000).Skywave);
        Assert.False(Hf(10_489_750_000).Skywave);
        Assert.Equal("IO83", Hf(7_047_000).Place!.Locator);
    }

    [Fact]
    public void ThePipeline_PlacesTheSignalByTheStationsFrequency_AndMovesWhenAnotherSlotComes()
    {
        var log = new List<string>();
        var time = new FakeTimeProvider(Noon);
        // Dial 7.052 MHz; GB7RDG's signal is at 1800 Hz in the audio.
        var pipeline = AudioPipeline.Create(new AudioSource(AudioSourceKind.Alsa, "test"), 7_052_000, StationFixture.SignalHz, log.Add, time);
        Assert.Equal(1800, pipeline.AudioCentreHz, 0.5);
        Assert.Equal(StationFixture.SignalHz, pipeline.SignalHz);

        // A station 700 Hz lower: 1100 Hz in the audio, still inside 1000 to 2000 Hz.
        Assert.True(pipeline.SetSignal(7_053_100));
        Assert.False(pipeline.SetSignal(7_053_100));
        Assert.Equal(7_053_100, pipeline.SignalHz);
        Assert.Contains(log, l => l.Contains("listening for a signal centred on 7.0531 MHz now, at 1100 Hz", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADirectorySaysWhereTheStationIs_AndTheHostFollowsItThere()
    {
        var bpq = DirectorySigner.FromPem(DirectorySigner.NewKeyPem()); // a throwaway key
        using var dir = new TempDirectory();
        var slot = new DateTimeOffset(2026, 10, 26, 10, 30, 0, TimeSpan.Zero);
        var details = new StationDetails(7_047_000, Sideband.Usb, "IO83fb");

        async Task<ReceiverHost> Open(ReceiverConfig config)
        {
            var host = new ReceiverHost(config, new FakeTimeProvider(slot.AddMinutes(1)), _ => { });
            foreach (var frame in Samples.Frames([Samples.Bulletin(1)], ScheduleOptions.Hourly with { Timetable = new SlotTimetable(new TimeOnly(0, 30), 60), Signer = bpq, Station = "G8BPQ", Details = details }, slot))
            {
                host.Intake.Offer(frame);
            }
            await host.Intake.DrainAsync(CancellationToken.None);
            return host;
        }

        // A web SDR is tuned for each slot: the dial for 7.047 MHz, the place from the directory.
        {
            await using var host = await Open(new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = dir.Path, Stations = StationFixture.EverySlot });
            var bpqListening = host.Listening.Single(l => l.Station == "G8BPQ");
            Assert.True(bpqListening.Followed);
            Assert.Equal(7_047_000, bpqListening.FrequencyHz);
            Assert.Equal("IO83fb", bpqListening.Locator);
            Assert.Equal("its own directory", bpqListening.Source);
            Assert.Equal(HowHeard.Retune, bpqListening.How);
            Assert.Equal(7_045_200, bpqListening.DialHz!.Value, 0.5);
            // GB7RDG is where it always was.
            var rdgListening = host.Listening.Single(l => l.Station == StationFixture.Callsign);
            Assert.Equal(7_052_000, rdgListening.DialHz!.Value, 0.5);
            Assert.Equal("IO91lk", rdgListening.Locator);

            // Opened for G8BPQ's slot, the pipeline is tuned and listening for that station.
            var pipeline = host.CreatePipeline(AudioSource.Parse("ubersdr:wessex.zapto.org"), host.Config, slot);
            Assert.Equal(7_045_200, pipeline.DialHz, 0.5);
            Assert.Equal(7_047_000, pipeline.SignalHz);
            Assert.Equal(1800, pipeline.AudioCentreHz, 0.5);
            // And for GB7RDG's, as before.
            var rdg = host.CreatePipeline(AudioSource.Parse("ubersdr:wessex.zapto.org"), host.Config, new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero));
            Assert.Equal(7_052_000, rdg.DialHz, 0.5);
            Assert.Equal(StationFixture.SignalHz, rdg.SignalHz);
        }

        // A sound card with its dial left at 7.052 MHz cannot hear 7.047 MHz, and says so; set
        // to 7.0452 MHz it hears it from the passband alone, with nothing to retune.
        {
            await using var fixedDial = await Open(new ReceiverConfig { Audio = Samples.SoundCard, StateDirectory = dir.Path, Stations = StationFixture.EverySlot });
            var no = fixedDial.Listening.Single(l => l.Station == "G8BPQ");
            Assert.False(no.Followed);
            Assert.Contains("7.047 MHz USB, not where this receiver listens", no.NotFollowedBecause, StringComparison.Ordinal);

            await using var moved = await Open(new ReceiverConfig { Audio = Samples.SoundCard, StateDirectory = dir.Path, Stations = StationFixture.EverySlot, DialKHz = 7045.2 });
            var yes = moved.Listening.Single(l => l.Station == "G8BPQ");
            Assert.True(yes.Followed);
            Assert.Equal(HowHeard.Passband, yes.How);
        }
    }

    private static PathPicture Picture() => new()
    {
        Basis = "bursts",
        Modes = [new PictureMode(0, 0, 0, 1), new PictureMode(1.9, -17, 0, 1)],
        PathsMs = [0, 1.9],
        PathsDb = [0, -17],
        DelaySpreadMs = 0.5,
        DopplerSpreadHz = 0.2,
        OffsetHz = 3.2,
        FadeDb = 4,
        CoherenceS = 10,
        SnrDb = 8.4,
        FloorDb = -30,
        GoodSnapshots = 200,
        Profile = [0, -10, -20, -30],
        ProfileStartMs = 0,
        ProfileStepMs = 0.5,
    };

    [Fact]
    public void ASkywavePath_IsDrawnFromTheStationsOwnPlace()
    {
        var g8bpq = new StationOnAir("G8BPQ", 7_047_000, Sideband.Usb, "IO83fb", "its own directory");
        var report = ChannelReport.Summarise(Noon, Noon, [Picture(), Picture()], 2, Wessex, transmitter: g8bpq);
        Assert.Equal("G8BPQ", report.Station);
        Assert.Equal("IO83fb", report.StationLocator);
        Assert.True(report.Skywave);
        Assert.Equal(Math.Round(PathGeometry.DistanceKm(Wessex, GroundPlace.FromLocator("IO83fb")!)), report.DistanceKm);
        Assert.NotEqual(Math.Round(PathGeometry.DistanceKm(Wessex, GroundPlace.FromLocator("IO91lk")!)), report.DistanceKm);
        Assert.NotEmpty(report.Modes);

        string svg = ChannelPicture.Svg(report, report.Locator, 340, withLabels: true, report.Place)!;
        Assert.Contains("G8BPQ IO83fb", svg, StringComparison.Ordinal);
        Assert.Contains("from G8BPQ to", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("GB7RDG", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void AStationWithNoLocator_HasNoDistanceAndNoPicture_AndIsNotTakenForGb7rdg()
    {
        var report = ChannelReport.Summarise(Noon, Noon, [Picture(), Picture()], 2, Wessex, transmitter: new StationOnAir("G8BPQ", 7_047_000, Sideband.Usb, null, "an advert"));
        Assert.Null(report.DistanceKm);
        Assert.Null(ChannelPicture.Svg(report, report.Locator, 340, withLabels: true, report.Place));
    }

    [Fact]
    public void AReportWithNoStationNamed_IsOfNoParticularStation_AndHasNoPlaceForIt()
    {
        var report = ChannelReport.Summarise(Noon, Noon, [Picture(), Picture()], 2, Wessex);
        Assert.Null(report.DistanceKm);
        Assert.Null(report.Station);
        Assert.Equal("the station", report.StationName);
        Assert.Equal("station", report.StationLabel);
        Assert.Null(report.StationPlaceOrDefault);
        Assert.True(report.Skywave);
    }

    [Fact]
    public void AReportWithTheFixtureStation_HasItsDistanceNameAndLabel()
    {
        var report = ChannelReport.Summarise(Noon, Noon, [Picture(), Picture()], 2, Wessex, transmitter: Fixture.OnAir);
        Assert.Equal(PathGeometry.DistanceKm(Wessex, GroundPlace.FromLocator("IO91lk")!), report.DistanceKm!.Value, 1.0);
        Assert.Equal("GB7RDG", report.StationName);
        Assert.Equal("GB7RDG IO91lk", report.StationLabel);
    }

    [Fact]
    public void ASatellitePath_ShowsSnrOffsetAndFrames_NotAnIonosphere()
    {
        var qo100 = new StationOnAir("G8BPQ", 10_489_750_000, Sideband.Usb, "IO83fb", "an advert");
        var report = ChannelReport.Summarise(Noon, Noon, [Picture(), Picture()], 2, Wessex, transmitter: qo100);
        Assert.False(report.Skywave);
        Assert.True(report.Enough);
        Assert.Empty(report.Modes);
        Assert.Null(report.VirtualHeightKm);
        Assert.Null(report.DelaySpreadMs);
        Assert.Null(report.DopplerSpreadHz);
        Assert.Empty(report.ProfileDb);
        Assert.Equal(8.4, report.SnrDb);
        Assert.Equal(3.2, report.OffsetHz);
        Assert.Equal(2, report.Kept);
        Assert.Equal("Signal to noise 8.4 dB, heard 3.2 Hz high. This is not a skywave path, so there is no height or multipath to measure.", report.Words);
        Assert.Null(ChannelPicture.Svg(report, report.Locator, 340, withLabels: true, report.Place));

        // Too little still says so.
        var thin = ChannelReport.Summarise(Noon, Noon, [], 0, Wessex, transmitter: qo100);
        Assert.False(thin.Enough);
    }

    [Fact]
    public async Task TheTile_SaysWhichStation_AndWhetherItsPathIsSkywave()
    {
        using var dir = new TempDirectory();
        await using var watch = new ChannelWatch(new FakeTimeProvider(Noon), _ => { }, null, () => Wessex);
        var json = System.Text.Json.JsonSerializer.Serialize(ChannelTile.View(watch, null, Noon), ReceiverConfig.JsonLine);
        Assert.Contains("\"skywave\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"station\":null", json, StringComparison.Ordinal);
    }

    private static async Task Hear(ReceiverHost host, ScheduleOptions options, DateTimeOffset slot, int bulletin)
    {
        foreach (var frame in Samples.Frames([Samples.Bulletin(bulletin)], options, slot))
        {
            host.Intake.Offer(frame);
        }
        await host.Intake.DrainAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ARigRetunedOnASoundCard_TellsThePipelineTheDialItTuned()
    {
        var bpq = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        using var dir = new TempDirectory();
        var slot = new DateTimeOffset(2026, 10, 26, 10, 30, 0, TimeSpan.Zero);
        var config = new ReceiverConfig
        {
            Audio = Samples.SoundCard,
            StateDirectory = dir.Path,
            Stations = StationFixture.EverySlot,
            Rig = new RigSettings { Rigctld = "127.0.0.1:4532", DedicatedRadio = true },
        };
        await using var host = new ReceiverHost(config, new FakeTimeProvider(slot.AddMinutes(1)), _ => { });
        await Hear(host, ScheduleOptions.Hourly with { Timetable = new SlotTimetable(new TimeOnly(0, 30), 60), Signer = bpq, Station = "G8BPQ", Details = new StationDetails(7_047_000, Sideband.Usb, "IO83") }, slot, 1);

        // Config dial 7.052 MHz, station on 7.047 MHz, the rig retuned for its slot.
        var pipeline = AudioPipeline.Create(new AudioSource(AudioSourceKind.Alsa, "test"), config.DialHz, StationFixture.SignalHz, _ => { }, new FakeTimeProvider(slot));
        Assert.Equal(7_052_000, pipeline.DialHz);
        Assert.Equal(7_045_200, host.PointAtSlot(slot, pipeline)!.Value, 0.5);
        Assert.Equal(7_045_200, pipeline.DialHz, 0.5);
        Assert.Equal(7_047_000, pipeline.SignalHz);
        Assert.Equal(1800, pipeline.AudioCentreHz, 0.5);
    }

    [Fact]
    public async Task WhenAStationsWindowCloses_ThePipelineGoesBackToTheConfigDialAndSignal()
    {
        var bpq = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        using var dir = new TempDirectory();
        var slot = new DateTimeOffset(2026, 10, 26, 10, 30, 0, TimeSpan.Zero);
        var config = new ReceiverConfig
        {
            Audio = Samples.SoundCard,
            StateDirectory = dir.Path,
            Stations = StationFixture.EverySlot,
            Rig = new RigSettings { Rigctld = "127.0.0.1:4532", DedicatedRadio = true },
        };
        await using var host = new ReceiverHost(config, new FakeTimeProvider(slot.AddMinutes(1)), _ => { });
        await Hear(host, ScheduleOptions.Hourly with { Timetable = new SlotTimetable(new TimeOnly(0, 30), 60), Signer = bpq, Station = "G8BPQ", Details = new StationDetails(7_047_000, Sideband.Usb, "IO83") }, slot, 1);
        var pipeline = AudioPipeline.Create(new AudioSource(AudioSourceKind.Alsa, "test"), config.DialHz, StationFixture.SignalHz, _ => { }, new FakeTimeProvider(slot));
        host.PointAtSlot(slot, pipeline);
        Assert.Equal(7_047_000, pipeline.SignalHz);
        Assert.Equal(7_045_200, pipeline.DialHz, 0.5);

        host.ReturnToConfig(pipeline);
        Assert.Equal(StationFixture.SignalHz, pipeline.SignalHz);
        Assert.Equal(7_052_000, pipeline.DialHz, 0.5);
    }

    [Fact]
    public async Task AnAdvert_DoesNotMoveAStationWhoseOwnDirectoryIsHeard()
    {
        var bpq = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        var other = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        using var dir = new TempDirectory();
        var slot = new DateTimeOffset(2026, 10, 26, 10, 30, 0, TimeSpan.Zero);
        var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = dir.Path, Stations = StationFixture.EverySlot };
        await using var host = new ReceiverHost(config, new FakeTimeProvider(slot.AddMinutes(1)), _ => { });
        // G8BPQ's own directory, saying nothing of its frequency, is heard here.
        await Hear(host, ScheduleOptions.Hourly with { Timetable = new SlotTimetable(new TimeOnly(0, 30), 60), Signer = bpq, Station = "G8BPQ" }, slot, 1);
        // Another head end advertises it elsewhere.
        await Hear(host, ScheduleOptions.Hourly with
        {
            Timetable = new SlotTimetable(new TimeOnly(0, 50), 60),
            Signer = other,
            Station = "M0ABC",
            Adverts = [new("G8BPQ", new SlotTimetable(new TimeOnly(0, 30), 60), 7_047_000, Sideband.Usb, "IO83")],
        }, slot.AddMinutes(20), 2);

        var g8bpq = host.Listening.Single(l => l.Station == "G8BPQ");
        Assert.Equal("its own directory", g8bpq.Source);
        Assert.Equal(7_053_800, g8bpq.FrequencyHz); // where it was heard, not the advert's 7.047 MHz
        Assert.Null(g8bpq.Locator);
        Assert.Equal(7_052_000, g8bpq.DialHz!.Value, 0.5);
    }

    [Fact]
    public void SdrConnect_FilterHoldsASignalAnywhereInTheAcceptedRange()
    {
        var (low, high) = AudioPipeline.SdrConnectFilterForAnyStation();
        Assert.Equal(100, low);
        Assert.True(high >= ReceiverConfig.HighestAudioCentreHz + OnAir.HalfWidthHz);
    }
}
