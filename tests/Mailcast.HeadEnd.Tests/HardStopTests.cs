using Packet.Mailcast;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Planning;
using Mailcast.HeadEnd.Slot;
using Microsoft.Extensions.Time.Testing;

namespace Mailcast.HeadEnd.Tests;

/// <summary>Nothing keys past the hard stop, however busy the channel or late the slot, and a late slot is planned shorter.</summary>
public class HardStopTests
{
    private static readonly DateTimeOffset Slot = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static (SlotReport Report, FakeStation Station) Run(DateTimeOffset startAt, int frames, bool busy)
    {
        var time = new VirtualTime(startAt);
        var station = new FakeStation(time, 4);
        if (busy)
        {
            // Somebody else on the channel for the whole slot: the tone waits its 2 minutes and goes
            // without, and every burst waits its 10 s carrier limit.
            station.ChannelBusyUntil = Slot.AddHours(1);
        }
        var settings = new SlotSettings { SubChannel = 4, ToneLength = TimeSpan.FromSeconds(10), MaxSlotLength = TimeSpan.FromMinutes(10), MaxBurst = TimeSpan.FromSeconds(18) };
        var runner = new SlotRunner(settings, station, station, null, FakeAirtime.For(station), new MemoryJournal(), time, new FakeClockSync());
        var report = time.Run(() => runner.RunAsync(Slot, [.. Enumerable.Range(0, frames).Select(i => FakeAirtime.Frame(i))], 5, CancellationToken.None));
        return (report, station);
    }

    private static void AssertNothingKeysPastTheHardStop(FakeStation station)
    {
        Assert.NotEmpty(station.Keyups);
        var stop = Slot + TimeSpan.FromMinutes(10);
        // The station's closing ident follows the release.
        Assert.All(station.Keyups, k => Assert.True(k.End + SlotAirtime.ClosingIdent <= stop, $"{k.What} ended at {k.End:HH:mm:ss.f}"));
        Assert.True(station.Releases.Single() + SlotAirtime.ClosingIdent <= stop, $"released at {station.Releases.Single():HH:mm:ss.f}");
    }

    [Fact]
    public void BusyChannel_EveryBurstWaitingItsLongest_StillStopsInTime()
    {
        var (report, station) = Run(Slot, 120, busy: true);
        Assert.Equal(SlotOutcome.Aborted, report.Outcome);
        Assert.Contains("maximum slot length", report.Reason, StringComparison.Ordinal);
        Assert.True(report.FramesSent > 0);
        AssertNothingKeysPastTheHardStop(station);
    }

    [Fact]
    public void LateSlot_StopsWhenAnOnTimeOneWould()
    {
        // A retry five minutes late: the hard stop still counts from 12:00.
        var (report, station) = Run(Slot.AddMinutes(5), 120, busy: false);
        Assert.Equal(SlotOutcome.Aborted, report.Outcome);
        AssertNothingKeysPastTheHardStop(station);
    }

    [Fact]
    public void LatePlan_FillsLess_SoTheSlotStillEndsByElevenMinutesPast()
    {
        // GB7RDG's settings, a busy store, and a slot planned 5 minutes late.
        var config = HeadEndConfig.Parse("""
            {
              "station": { "apiKey": "k", "mode": "ms110d-wn4", "maxBurstSeconds": 18 },
              "slot": { "timeUtc": "00:00", "everyMinutes": 60, "maxMinutes": 10, "toneSeconds": 10, "channelWaitSeconds": 120 },
              "schedule": { "symbolSize": 240 }
            }
            """);
        using var state = new TempDirectory();
        var options = config.ToScheduleOptions();
        var store = new RotationStore(state.Path, Compression.Default, options, new MemoryJournal());
        for (int i = 0; i < 60; i++)
        {
            store.Offer(Bulletins.Make(500 + i, 6000), new DateOnly(2026, 10, 6));
        }
        var settings = config.ToSlotSettings();
        var waveforms = config.ToWaveforms(LinearAirtime.Measure);
        TimeSpan OnAir(SlotPlan plan) => new SlotAirtime(settings, plan.Waveform!.Airtime).ForPayloads([.. plan.Frames.Select(f => f.Payload.Length)]);

        var onTime = new StoreSlotPlanner(store, Compression.Default, options, waveforms, settings, config.FillLimitAfter, new FakeTimeProvider(Slot.AddSeconds(30))).Plan(Slot);
        var late = new StoreSlotPlanner(store, Compression.Default, options, waveforms, settings, config.FillLimitAfter, new FakeTimeProvider(Slot.AddMinutes(5))).Plan(Slot);

        Assert.True(OnAir(onTime) <= config.FillLimitAfter(TimeSpan.FromSeconds(30)));
        Assert.True(OnAir(onTime) > config.FillLimitAfter(TimeSpan.FromSeconds(30)) - TimeSpan.FromSeconds(30));
        Assert.True(OnAir(late) <= config.FillLimitAfter(TimeSpan.FromMinutes(5)));
        Assert.True(late.Frames.Count < onTime.Frames.Count / 2 + 10, $"{late.Frames.Count} frames late against {onTime.Frames.Count} on time");
        // Started at +5, even after the longest clear-channel wait it ends inside the hard stop at +10.
        Assert.True(TimeSpan.FromMinutes(5) + OnAir(late) + config.Margin <= TimeSpan.FromMinutes(10));

        // So late that nothing fits: nothing keys.
        var tooLate = new StoreSlotPlanner(store, Compression.Default, options, waveforms, settings, config.FillLimitAfter, new FakeTimeProvider(Slot.AddMinutes(8))).Plan(Slot);
        Assert.Empty(tooLate.Frames);
    }
}
