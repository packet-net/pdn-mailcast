using Packet.Mailcast;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Planning;
using Mailcast.HeadEnd.Service;
using Mailcast.HeadEnd.Slot;
using Mailcast.HeadEnd.Status;

namespace Mailcast.HeadEnd.Tests;

/// <summary>
/// GB7RDG, 9 to 11 Nov 2026: daylight slots alternating WN4 and WN3 and evening slots on WN3. Once enough bulletins had started,
/// every WN3 slot was "skipped: nothing to send" while every WN4 slot went out: the directory's weak-station floor (about 5 frames
/// per directory symbol) had grown past what a WN3 slot can carry, so no bulletin piece fitted beside it.
/// </summary>
public class SlowWaveformSlotTests
{
    private const string Gb7rdgLike = """
        {"callsign": "GB7RDG", "station": {"apiKey": "k"},
         "slot": {"minute": 0, "everyMinutes": 60, "daylight": {"locator": "IO91lk"}, "evening": {"untilUtc": "22:00", "modes": ["ms110d-wn3"]}},
         "schedule": {"modes": ["ms110d-wn4", "ms110d-wn3"], "bulk": ["LU9DCE"]}}
        """;

    [Fact]
    public void WaveformSlots_AfterEveningSlotsStartManyBulletins_StillSendBulletins_OnWn3AsOnWn4()
    {
        using var dir = new TempDirectory();
        var start = new DateTimeOffset(2026, 11, 9, 17, 0, 0, TimeSpan.Zero);
        var time = new VirtualTime(start);
        var journal = new MemoryJournal();
        var config = HeadEndConfig.Parse(Gb7rdgLike);
        // Signed, as GB7RDG's are: each directory line carries the long hashes too.
        var options = config.ToScheduleOptions() with { Signer = DirectorySigner.FromPem(DirectorySigner.NewKeyPem()), Station = "M0LTE" };
        var store = new RotationStore(dir.Path, Compression.Default, options, journal, time);
        for (int i = 0; i < 150; i++)
        {
            store.Offer(Bulletins.Make(i, 2000 + (i * 397 % 9000), from: i % 3 == 0 ? "LU9DCE" : "G4ABC"), new DateOnly(2026, 11, 9));
        }
        var station = new FakeStation(time, 4);
        var planner = new StoreSlotPlanner(store, Compression.Default, options, config.ToWaveforms(Waveforms.Measure), config.ToSlotSettings(), config.FillLimitAfter, time);
        var runner = new SlotRunner(new SlotSettings { Callsign = "GB7RDG", SubChannel = 4 }, station, station, null, FakeAirtime.For(station), journal, time, new FakeClockSync());
        var service = new HeadEndService(config.ToSlotSchedule(), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), planner, store, [], runner, new StatusStore(null, time), journal, time);

        // Two evenings and the day between, every slot in one process, as the live head end ran them: 6 daylight slots
        // from 10:00 to 15:00 UTC and 5 evening ones from 17:00 to 21:00.
        var slots = Enumerable.Range(0, 29).Select(h => start.AddHours(h)).Where(t => t.Hour is >= 10 and <= 21 and not 16).ToList();
        var reports = new List<SlotReport>();
        foreach (var slot in slots)
        {
            time.StepWallClock(slot - time.GetUtcNow());
            reports.Add(time.Run(() => service.RunSlotAsync(slot, CancellationToken.None)));
        }

        Assert.Equal(16, reports.Count);
        Assert.Contains(reports, r => r.Mode == "ms110d-wn3" && !r.Evening);
        Assert.Contains(reports, r => r.Mode == "ms110d-wn3" && r.Evening);
        Assert.All(reports, r => Assert.True(r.Outcome == SlotOutcome.Completed && r.FramesSent > 0, $"{SlotRunner.Name(r.Slot)} on {r.Mode}: {r.Outcome} {r.Reason}"));
        Assert.DoesNotContain(journal.Lines, l => l.Contains("nothing to send", StringComparison.Ordinal));
    }
}
