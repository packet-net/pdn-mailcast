using Mailcast.Core;
using Microsoft.Extensions.Time.Testing;

namespace Mailcast.Receiver.Tests;

/// <summary>The head end's timetable, heard in its directory, in place of the config file's.</summary>
public class HeardScheduleTests
{
    private static readonly DateTimeOffset Slot = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ADirectoryWithATimetable_IsUsedInsteadOfTheConfig_SaysSo_AndOutlivesARestart()
    {
        using var dir = new TempDirectory();
        var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = dir.Path };
        var heard = new SlotTimetable(new TimeOnly(0, 30), 60, new DaylightRule("IO91lk", 60, 60));
        var options = ScheduleOptions.HourlyDaylight with { Timetable = heard };
        var log = new List<string>();

        await using (var host = new ReceiverHost(config, new FakeTimeProvider(Slot), line => { lock (log) { log.Add(line); } }))
        {
            Assert.False(host.ScheduleFromDirectory);
            Assert.Equal(config.Schedule, host.Schedule);
            foreach (var frame in Samples.Frames([Samples.Bulletin(1)], options, Slot))
            {
                host.Intake.Offer(frame);
            }
            await host.Intake.DrainAsync(CancellationToken.None);

            Assert.True(host.ScheduleFromDirectory);
            Assert.Equal(SlotSchedule.From(heard), host.Schedule);
            Assert.Equal(heard.Daylight, host.Slots.Schedule!.Daylight);
            Assert.Single(log, l => l == "slots: GB7RDG's directory gives its slots as every hour at 30 minutes past, in daylight: from 60 minutes after sunrise to 60 minutes before sunset at IO91lk; using that instead of the config file's");

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
            Assert.True(again.ScheduleFromDirectory);
            Assert.Equal(SlotSchedule.From(heard), again.Schedule);
        }
    }

    [Fact]
    public async Task ADirectoryWithoutOne_AsFromAV020HeadEnd_LeavesTheConfigInForce()
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
        Assert.False(host.ScheduleFromDirectory);
        Assert.Equal(config.Schedule, host.Schedule);
    }

    [Fact]
    public async Task ADappsMessage_IsKeptOutOfTheBbs_AndLoggedOnce()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        await using var intake = new Intake(dir.Path, line => { lock (log) { log.Add(line); } });
        var dapps = TransferObject.ForContent((byte)ObjectKind.DappsMessage, "a DAPPS message"u8.ToArray(), [], false, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        for (uint esi = 0; esi < 10; esi++)
        {
            intake.Offer(Packet.SoundModem.Waterfall.Ax25UiFrame.Build(OnAir.Source, OnAir.Destination, dapps.Frame(esi).ToBytes()));
        }
        await intake.DrainAsync(CancellationToken.None);
        Assert.Empty(intake.Pending());
        Assert.Single(log, l => l.Contains("is a DAPPS message, which this receiver does not handle; kept out of the BBS", StringComparison.Ordinal));
    }
}
