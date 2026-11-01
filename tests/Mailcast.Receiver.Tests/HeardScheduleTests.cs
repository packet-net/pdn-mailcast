using Packet.Mailcast;
using Microsoft.Extensions.Time.Testing;

namespace Mailcast.Receiver.Tests;

/// <summary>A station's timetable, heard in its directory, in place of the stations file's.</summary>
public class HeardScheduleTests
{
    private static readonly DateTimeOffset Slot = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The slots the receiver counts for its one station now.</summary>
    private static SlotSchedule Used(ReceiverHost host) => host.Followed.Stations.Single().Schedule;

    [Fact]
    public async Task ADirectoryWithATimetable_IsUsedInsteadOfTheStationsFile_SaysSo_AndOutlivesARestart()
    {
        using var dir = new TempDirectory();
        var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = dir.Path };
        var heard = new SlotTimetable(new TimeOnly(0, 30), 60, new DaylightRule("IO91lk", 60, 60));
        var options = ScheduleOptions.HourlyDaylight with { Timetable = heard };
        var log = new List<string>();

        await using (var host = new ReceiverHost(config, new FakeTimeProvider(Slot), line => { lock (log) { log.Add(line); } }))
        {
            Assert.Empty(host.Intake.HeardSchedules);
            Assert.Equal(Fixture.Slots(config), Used(host));
            foreach (var frame in Samples.Frames([Samples.Bulletin(1)], options, Slot))
            {
                host.Intake.Offer(frame);
            }
            await host.Intake.DrainAsync(CancellationToken.None);

            Assert.Single(host.Intake.HeardSchedules);
            Assert.Equal(SlotSchedule.From(heard), Used(host));
            Assert.Equal(heard.Daylight, host.Slots.Schedule!.First.Daylight);
            Assert.Single(log, l => l == "slots: GB7RDG's directory gives its slots as every hour at 30 minutes past, in daylight: from 60 minutes after sunrise to 60 minutes before sunset at IO91lk; using that instead of the stations file's");

            // Hearing the same again says nothing more.
            foreach (var frame in Samples.Frames([Samples.Bulletin(2)], options, Slot.AddHours(1)))
            {
                host.Intake.Offer(frame);
            }
            await host.Intake.DrainAsync(CancellationToken.None);
            Assert.Single(log, l => l.StartsWith("slots: GB7RDG's directory", StringComparison.Ordinal));
        }

        await using (var again = new ReceiverHost(config, new FakeTimeProvider(Slot), _ => { }))
        {
            Assert.Single(again.Intake.HeardSchedules);
            Assert.Equal(SlotSchedule.From(heard), Used(again));
        }
    }

    [Fact]
    public async Task ADirectoryWithoutOne_AsFromAV020HeadEnd_LeavesTheStationsFileInForce()
    {
        using var dir = new TempDirectory();
        var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = dir.Path };
        await using var host = new ReceiverHost(config, new FakeTimeProvider(Slot), _ => { });
        foreach (var frame in Samples.Frames([Samples.Bulletin(1)], ScheduleOptions.Hourly, Slot))
        {
            host.Intake.Offer(frame);
        }
        await host.Intake.DrainAsync(CancellationToken.None);
        Assert.NotNull(host.Intake.Progress().Directory);
        Assert.Empty(host.Intake.HeardSchedules);
        Assert.Equal(Fixture.Slots(config), Used(host));
    }

    [Fact]
    public async Task ADappsMessage_IsKeptOutOfTheBbs_AndLoggedOnce()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        await using var intake = new Intake(dir.Path, line => { lock (log) { log.Add(line); } });
        var dapps = TransferObject.ForContent((byte)ObjectKind.DappsMessage, "a DAPPS message"u8.ToArray(), [], false, ZstdDictionary.Dictionary1Id, Compression.Default);
        for (uint esi = 0; esi < 10; esi++)
        {
            intake.Offer(Packet.SoundModem.Waterfall.Ax25UiFrame.Build(Samples.Source, OnAir.Destination, dapps.Frame(esi).ToBytes()));
        }
        await intake.DrainAsync(CancellationToken.None);
        Assert.Empty(intake.Pending());
        Assert.Single(log, l => l.Contains("is a DAPPS message, which this receiver does not handle; kept out of the BBS", StringComparison.Ordinal));
    }
}
