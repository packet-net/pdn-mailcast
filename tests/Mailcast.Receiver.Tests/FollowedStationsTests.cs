using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;

namespace Mailcast.Receiver.Tests;

/// <summary>Following every known station's slots (issues #178 and #144): the windows, the slot tracker, and the host.</summary>
public class FollowedStationsTests
{
    private static readonly DateOnly Day = new(2026, 10, 25);
    private static readonly SlotSchedule OnTheHour = new(TimeOnly.MinValue, 60);
    private static readonly SlotSchedule HalfPast = new(new TimeOnly(0, 30), 60);
    private static readonly FollowedSlots Two = new([("M0AAA", OnTheHour), ("M0BBB", HalfPast)]);

    private static DateTimeOffset At(int hour, int minute) => new(Day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.Zero);

    [Fact]
    public void OneStation_AnswersExactlyAsItsOwnSchedule()
    {
        var gb7rdg = new SlotSchedule(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg);
        FollowedSlots one = gb7rdg;
        Assert.True(one.OneStation);
        for (var t = At(0, 7); t < At(23, 59); t += TimeSpan.FromMinutes(37))
        {
            Assert.Equal(gb7rdg.NextActiveStart(t), one.NextActiveStart(t));
            Assert.Equal(gb7rdg.LatestActiveStart(t), one.LatestActiveStart(t));
            Assert.Equal(gb7rdg.LatestStart(t), one.LatestStart(t));
            Assert.Equal(gb7rdg.NearestStart(t), one.NearestStart(t));
            Assert.Equal(ListeningWindow.Next(t, gb7rdg), ListeningWindow.Next(t, one));
        }
        Assert.Equal(gb7rdg.ActiveOn(Day), one.ActiveOn(Day));
        Assert.Equal(ListeningWindow.WebSdrSlotsOn(gb7rdg, Day), ListeningWindow.WebSdrSlotsOn(one, Day));
        Assert.Equal(gb7rdg.Describe(), one.Describe());
    }

    [Fact]
    public void TwoStations_EachSlotIsItsOwn_AndTheWindowsCoverBoth()
    {
        Assert.Equal(48, Two.ActiveOn(Day).Count);
        Assert.Equal(At(10, 30), Two.LatestStart(At(10, 35)));
        Assert.Equal(At(10, 0), Two.LatestStart(At(10, 29)));
        Assert.Equal("M0BBB", Two.StationAt(At(10, 30)));
        Assert.Equal("M0AAA", Two.StationAt(At(10, 0)));
        Assert.Null(Two.StationAt(At(10, 15)));
        Assert.Equal(At(10, 30), Two.NearestStart(At(10, 27)));

        // A sound card decodes around the :30 slot too, not only the :00 one.
        var window = ListeningWindow.SoundCard(At(10, 20), Two)!.Value;
        Assert.Equal(At(10, 30), window.Slot);
        Assert.Equal(At(10, 28), window.Opens);
        Assert.Equal("M0AAA every hour on the hour; M0BBB every hour at 30 minutes past", Two.Describe());
    }

    [Fact]
    public void AWebSdr_SharesItsAllowance_AcrossTheStations()
    {
        var listened = ListeningWindow.WebSdrSlotsOn(Two, Day);
        Assert.Equal(ReceiverConfig.MostWebSdrSlotsPerDay, listened.Count);
        Assert.Contains(listened, s => s.Minute == 30);
        Assert.Contains(listened, s => s.Minute == 0);
        Assert.Equal(listened.Order(), listened);
        Assert.All(listened, s => Assert.NotNull(Two.StationAt(s)));
        Assert.Contains("of the 2 stations it follows", ListeningWindow.Describe(Two, Day), StringComparison.Ordinal);
        Assert.Equal(6, listened.Count(s => s.Minute == 30)); // an equal share each
        var next = ListeningWindow.Next(At(10, 13), Two);
        Assert.Equal(listened.First(s => s > At(10, 13)), next.Slot);
        Assert.Equal(next.Slot - ReceiverConfig.WebSdrBefore, next.Opens);
    }

    [Fact]
    public void SlotTracker_CountsAHalfPastStationsFrames_InItsOwnSlot()
    {
        var time = new FakeTimeProvider(At(10, 2));
        var log = new List<string>();
        var tracker = new SlotTracker(time, log.Add, Two);
        tracker.OnFrame("ms110d-wn4", 100);
        Assert.Equal(At(10, 0), tracker.Last!.Scheduled);
        Assert.Equal("M0AAA", tracker.Last.Station);

        time.SetUtcNow(At(10, 33));
        tracker.OnFrame("ms110d-wn4", 100);
        tracker.OnFrame("ms110d-wn4", 100);
        Assert.Equal(At(10, 30), tracker.Last!.Scheduled);
        Assert.Equal("M0BBB", tracker.Last.Station);
        Assert.Equal(2, tracker.Last.FramesHeard);
        Assert.Contains(log, l => l.StartsWith("slot: M0AAA's 10:00 UTC slot", StringComparison.Ordinal));

        // A directory of the other station heard in this slot is not this slot's.
        var other = new BroadcastDirectory(Day, [new DirectoryEntry(1, 1, 10, "1_A", "a")]);
        tracker.OnDirectory(other, "M0AAA");
        Assert.Null(tracker.Last.HeardDirectory);
        tracker.OnDirectory(other, null); // no station's
        Assert.Null(tracker.Last.HeardDirectory);
        tracker.OnDirectory(other, "M0BBB");
        Assert.Same(other, tracker.Last.HeardDirectory);
    }

    [Fact]
    public void SlotTracker_WithOneStation_NamesNone()
    {
        var time = new FakeTimeProvider(At(10, 33));
        var tracker = new SlotTracker(time, _ => { }, OnTheHour);
        tracker.OnFrame();
        Assert.Equal(At(10, 0), tracker.Last!.Scheduled);
        Assert.Null(tracker.Last.Station);
    }

    private static async Task HearAsync(ReceiverHost host, ScheduleOptions options, DateTimeOffset slot, int bulletin)
    {
        foreach (var frame in Samples.Frames([Samples.Bulletin(bulletin)], options, slot))
        {
            host.Intake.Offer(frame);
        }
        await host.Intake.DrainAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Host_FollowsAStationOnFirstHearing_AndTakesUpItsMove()
    {
        var bpq = DirectorySigner.FromPem(DirectorySigner.NewKeyPem()); // a throwaway key
        using var dir = new TempDirectory();
        var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = dir.Path, Daylight = null };
        var slot = At(10, 30);
        var log = new List<string>();
        await using var host = new ReceiverHost(config, new FakeTimeProvider(slot.AddMinutes(1)), line => { lock (log) { log.Add(line); } });
        Assert.True(host.Followed.OneStation);

        await HearAsync(host, ScheduleOptions.Hourly with { Timetable = new SlotTimetable(new TimeOnly(0, 30), 60), Signer = bpq, Station = "G8BPQ" }, slot, 1);
        Assert.Equal(["GB7RDG", "G8BPQ"], host.Followed.Stations.Select(s => s.Station));
        Assert.Equal(slot, host.Slots.Schedule!.LatestStart(slot.AddMinutes(1)));
        Assert.Equal("G8BPQ", host.Slots.Schedule.StationAt(slot));

        // Its own signed directory moves it to :45, and the tracker takes that up at once.
        var moved = At(10, 45);
        await HearAsync(host, ScheduleOptions.Hourly with { Timetable = new SlotTimetable(new TimeOnly(0, 45), 60), Signer = bpq, Station = "G8BPQ" }, moved, 2);
        Assert.Equal(moved, host.Slots.Schedule!.LatestStart(moved.AddMinutes(1)));
        lock (log)
        {
            Assert.Contains(log, l => l == "slots: now following GB7RDG every hour on the hour; G8BPQ every hour at 45 minutes past");
        }
    }

    [Fact]
    public async Task Host_FollowsAdvertisedStations_ShowsTheUnreachable_AndSkipsWhatItIsTold()
    {
        var xyz = DirectorySigner.FromPem(DirectorySigner.NewKeyPem()); // a throwaway key, pinned by hand
        using var dir = new TempDirectory();
        var config = new ReceiverConfig
        {
            Audio = "ubersdr:wessex.zapto.org",
            StateDirectory = dir.Path,
            Daylight = null,
            TrustedKeys = [new TrustedKeySettings { Label = "M0XYZ", PublicKey = xyz.PublicKeyBase64 }],
        };
        var slot = At(10, 15);
        await using (var host = new ReceiverHost(config, new FakeTimeProvider(slot.AddMinutes(1)), _ => { }))
        {
            await HearAsync(host, ScheduleOptions.Hourly with
            {
                Timetable = new SlotTimetable(new TimeOnly(0, 15), 60),
                Signer = xyz,
                Station = "M0XYZ",
                Adverts = [new("G8BPQ", new SlotTimetable(new TimeOnly(0, 30), 60), 7_053_800, Sideband.Usb), new("M0FAR", new SlotTimetable(new TimeOnly(0, 45), 60), 7_047_000, Sideband.Usb)],
            }, slot, 1);
            var listening = host.Listening;
            Assert.Equal(["GB7RDG", "G8BPQ", "M0FAR", "M0XYZ"], listening.Select(l => l.Station));
            Assert.Equal("an advert", listening[1].Source);
            Assert.False(listening[2].Followed);
            Assert.Contains("7.047 MHz USB, not where this receiver listens", listening[2].NotFollowedBecause, StringComparison.Ordinal);
            Assert.Equal("its own directory", listening[3].Source);
            Assert.Equal(["GB7RDG", "G8BPQ", "M0XYZ"], host.Followed.Stations.Select(s => s.Station));
        }

        // Told not to listen at G8BPQ's slots: its bulletins would still be delivered, but its slots are skipped.
        await using var skipping = new ReceiverHost(config with { SkipStations = ["G8BPQ"] }, new FakeTimeProvider(slot.AddMinutes(1)), _ => { });
        Assert.Equal(["GB7RDG", "M0XYZ"], skipping.Followed.Stations.Select(s => s.Station));
        Assert.Equal("you chose not to listen at its slots", skipping.Listening.Single(l => l.Station == "G8BPQ").NotFollowedBecause);
    }
}
