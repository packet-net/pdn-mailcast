using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;

namespace Mailcast.Receiver.Tests;

/// <summary>Receivers and a head end's evening slots: followed by default, the "eveningSlots" opt-out, and a web SDR's allowance.</summary>
public class EveningSlotReceiverTests
{
    private static readonly SlotTimetable Gb7rdg = new(TimeOnly.MinValue, 60, StationFixture.Daylight, new EveningRule(new TimeOnly(22, 0)));

    private static readonly DateTimeOffset Noon = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static DateOnly D(string day) => DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string[] Hhmm(IEnumerable<DateTimeOffset> slots) => [.. slots.Select(s => s.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture))];

    private static ReceiverConfig Config(string dir, bool? eveningSlots, string? locator) => new()
    {
        Audio = "wav:/nonexistent.wav",
        StateDirectory = dir,
        Bbs = new BbsSettings { Port = 8011, Login = "Q0CAST", Password = "secret" },
        EveningSlots = eveningSlots,
        Locator = locator,
    };

    [Fact]
    public async Task ByDefault_EveryAnnouncedSlotIsFollowed_EveningOnesToo()
    {
        using var dir = new TempDirectory();
        await using var host = new ReceiverHost(Config(dir.Path, null, "JO67fm"), new FakeTimeProvider(Noon), _ => { });
        Assert.Same(Gb7rdg, host.AsFollowed(Gb7rdg));
        await using var on = new ReceiverHost(Config(dir.Path, true, null), new FakeTimeProvider(Noon), _ => { });
        Assert.Same(Gb7rdg, on.AsFollowed(Gb7rdg));
    }

    [Fact]
    public async Task OptedOut_TheEveningEndsAtSunsetHere()
    {
        using var dir = new TempDirectory();
        await using var host = new ReceiverHost(Config(dir.Path, false, "IO41"), new FakeTimeProvider(Noon), _ => { });
        var followed = host.AsFollowed(Gb7rdg);
        Assert.Equal("IO41", followed.Evening!.ListenerLocator);
        // The same rule each time, so its sunsets are worked out once.
        Assert.Same(followed.Evening, host.AsFollowed(Gb7rdg).Evening);
        // In the far west of Ireland it is still light at 16:00 UTC at midwinter, and dark by 17:00.
        Assert.Equal(["16:00"], Hhmm(followed.EveningSlotsOn(D("2026-12-21"))));
        // The daylight slots are all still followed.
        Assert.Equal(Gb7rdg.WithoutEvening().ActiveSlotsOn(D("2026-12-21")), SlotSchedule.From(followed).DaylightOn(D("2026-12-21")));

        // In Sweden it is dark before any evening slot.
        await using var sweden = new ReceiverHost(Config(dir.Path, false, "JO67fm"), new FakeTimeProvider(Noon), _ => { });
        Assert.Empty(sweden.AsFollowed(Gb7rdg).EveningSlotsOn(D("2026-12-21")));

        // A timetable without evening slots is left as it is.
        var plain = Gb7rdg.WithoutEvening();
        Assert.Same(plain, host.AsFollowed(plain));
        Assert.Contains("evening slots skipped after sunset here (\"eveningSlots\": false)", ConfigFile.PartsLine(host.Config), StringComparison.Ordinal);
        Assert.DoesNotContain("evening", ConfigFile.PartsLine(Config(dir.Path, null, null)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OptedOut_WithNoPlaceKnown_SkipsEveryEveningSlot_AndSaysSoOnce()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        await using var host = new ReceiverHost(Config(dir.Path, false, null), new FakeTimeProvider(Noon), log.Add);
        log.Clear();
        Assert.Equal(Gb7rdg.WithoutEvening(), host.AsFollowed(Gb7rdg));
        Assert.Equal(Gb7rdg.WithoutEvening(), host.AsFollowed(Gb7rdg));
        var said = Assert.Single(log, l => l.Contains("eveningSlots", StringComparison.Ordinal));
        Assert.Equal("slots: \"eveningSlots\" is false and this receiver's place is not known (no \"locator\"), so it skips every evening slot", said);
        Assert.True(said.All(char.IsAscii));
    }

    [Fact]
    public async Task OptedOut_OnAWebSdrThatHasNotSaidWhereItIs_SaysSo()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        await using var host = new ReceiverHost(Config(dir.Path, false, null) with { Audio = "ubersdr:wessex.zapto.org" }, new FakeTimeProvider(Noon), log.Add);
        log.Clear();
        Assert.Equal(Gb7rdg.WithoutEvening(), host.AsFollowed(Gb7rdg));
        Assert.Equal("slots: \"eveningSlots\" is false and the web SDR has not said where it is yet, so every evening slot is skipped until it does",
            Assert.Single(log, l => l.Contains("eveningSlots", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheSetting_IsRead_AndLeftOutOfASavedFileUntilSet()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, """{ "audio": "ubersdr:wessex.zapto.org", "eveningSlots": false }""");
        var off = ReceiverConfig.Load(path);
        Assert.False(off.EveningSlots);
        Assert.False(off.FollowsEveningSlots);

        File.WriteAllText(path, """{ "audio": "ubersdr:wessex.zapto.org" }""");
        var config = ReceiverConfig.Load(path);
        Assert.Null(config.EveningSlots);
        Assert.True(config.FollowsEveningSlots);
        config.Save(path);
        Assert.DoesNotContain("eveningSlots", File.ReadAllText(path), StringComparison.Ordinal);
        off.Save(path);
        Assert.Contains("\"eveningSlots\": false", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Theory]
    // Midwinter: 5 daylight and 6 evening slots, 11 in all, inside the allowance of 12.
    [InlineData("2026-12-21", "11:00,12:00,13:00,14:00,15:00,16:00,17:00,18:00,19:00,20:00,21:00")]
    // October: 9 daylight slots, and the 3 earliest of the 4 evening ones in what is left.
    [InlineData("2026-10-05", "09:00,10:00,11:00,12:00,13:00,14:00,15:00,16:00,17:00,18:00,19:00,20:00")]
    // Midsummer: 14 daylight slots already fill it; the evening slot gets nothing.
    [InlineData("2026-06-21", "08:00,09:00,10:00,11:00,12:00,13:00,14:00,15:00,16:00,17:00,18:00,19:00")]
    public void AWebSdr_GivesItsAllowanceToTheDaylightSlotsFirst(string day, string expected)
    {
        var schedule = SlotSchedule.From(Gb7rdg);
        Assert.Equal(expected.Split(','), Hhmm(ListeningWindow.WebSdrSlotsOn(schedule, D(day))));
        Assert.Equal(expected.Split(','), Hhmm(ListeningWindow.WebSdrSlotsOn(FollowedSlots.One(schedule, "GB7RDG"), D(day))));
    }

    [Fact]
    public void AWebSdr_FollowingTwoStations_SharesWhatTheDaylightSlotsLeave()
    {
        // GB7RDG with evening slots, and another station at :30 in daylight only: midwinter, 5 + 5 daylight slots, then 2 evening.
        var other = new SlotSchedule(new TimeOnly(0, 30), 60, StationFixture.Daylight);
        var followed = new FollowedSlots([("GB7RDG", SlotSchedule.From(Gb7rdg)), ("G8BPQ", other)]);
        var slots = ListeningWindow.WebSdrSlotsOn(followed, D("2026-12-21"));
        Assert.Equal(ReceiverConfig.MostWebSdrSlotsPerDay, slots.Count);
        Assert.Equal(["16:00", "17:00"], Hhmm(slots.Where(s => s.Minute == 0 && s.Hour >= 16)));
        Assert.Equal(10, slots.Count(s => !Gb7rdg.IsEvening(s)));
        Assert.Contains("12 of the 16 slots of the 2 stations", ListeningWindow.Describe(followed, D("2026-12-21")), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSchedule_InWords_NamesTheEveningSlots()
    {
        Assert.Equal("every hour on the hour, in daylight: from 120 minutes after sunrise to 30 minutes before sunset at IO91lk; and evening slots from sunset until 22:00 UTC",
            SlotSchedule.From(Gb7rdg).Describe());
        Assert.Contains("all 11 daylight and evening slots", ListeningWindow.Describe(SlotSchedule.From(Gb7rdg), D("2026-12-21")), StringComparison.Ordinal);
    }
}
